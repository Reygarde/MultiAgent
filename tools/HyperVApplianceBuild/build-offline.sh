#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C.UTF-8 TZ=UTC
export PATH=/usr/sbin:/usr/bin
umask 077

if [[ $# != 5 || "$1" != "--build-reviewed-offline" ]]; then
  printf '%s\n' 'NOT AUTHORIZED IN 6.5-G. Future syntax: build-offline.sh --build-reviewed-offline BUNDLE LOCK REVIEWED_LOCK_SHA256 NEW_OUTPUT' >&2
  exit 2
fi
[[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 && "$EUID" == 0 ]] || {
  printf '%s\n' 'Requires a separately approved, disposable Linux amd64 root builder. No sudo or WSL fallback.' >&2
  exit 2
}
IFS= read -r kernel_name < /proc/sys/kernel/osrelease
case "${kernel_name,,}" in
  *microsoft*|*wsl*) printf '%s\n' 'WSL builders are not approved by this recipe.' >&2; exit 2 ;;
esac
bundle="$(realpath -e -- "$2")"
lock="$(realpath -e -- "$3")"
digest="$4"
output="$5"
script="$(realpath -e -- "${BASH_SOURCE[0]}")"
helper="${script%/*}/inputs.mjs"
[[ "$bundle" != / && "$output" == /* && "$output" != */../* && "$output" != */./* && ! -e "$output" ]] || {
  printf '%s\n' 'Bundle must be dedicated; output must be a new absolute directory.' >&2
  exit 2
}
[[ "$bundle" =~ ^/[a-zA-Z0-9._+/-]+$ && "$output" =~ ^/[a-zA-Z0-9._+/-]+$ &&
   "$output" != "$bundle/"* ]] || {
  printf '%s\n' 'Use dedicated ASCII paths without whitespace; output must be outside the bundle.' >&2
  exit 2
}
node "$helper" tools "$bundle" "$lock" "$digest"

exec unshare --net --mount --pid --fork --mount-proc bash -s -- \
  "$bundle" "$lock" "$digest" "$output" "$helper" "$script" <<'BUILD'
set -euo pipefail
bundle="$1"
lock="$2"
digest="$3"
output="$4"
helper="$5"
script="$6"
mount --make-rprivate /
mount --bind "$bundle" "$bundle"
mount -o remount,bind,ro "$bundle"
node "$helper" verify "$bundle" "$lock" "$digest"
field() { node "$helper" field "$bundle" "$lock" "$digest" "$1"; }
SOURCE_DATE_EPOCH="$(field sourceDateEpoch)"
export SOURCE_DATE_EPOCH
kernel="$(field kernelRelease)"
keyring="$bundle/$(field keyring)"
grub_cert="$bundle/$(field grubSecureBootCertificate)"
kernel_cert="$bundle/$(field kernelSecureBootCertificate)"
ms_cert="$bundle/$(field microsoftUefiCertificate)"
gpgv --keyring "$keyring" "$bundle/debian/dists/trixie/InRelease"
gpgv --keyring "$keyring" "$bundle/security/dists/trixie-security/InRelease"
mkdir -- "$output"
output="$(realpath -e -- "$output")"
root="$output/rootfs"
includes="$(node "$helper" includes "$bundle" "$lock" "$digest")"
sources="$output/offline.sources.list"
printf 'deb [signed-by=%s] file:%s/debian trixie main\n' "$keyring" "$bundle" > "$sources"
printf 'deb [signed-by=%s] file:%s/security trixie-security main\n' "$keyring" "$bundle" >> "$sources"
mmdebstrap --mode=root --variant=custom --architectures=amd64 --format=directory \
  --keyring="$keyring" --include="$includes" \
  --aptopt='APT::Install-Recommends "false"' \
  --aptopt='APT::Install-Suggests "false"' \
  --aptopt='Acquire::Retries "0"' \
  --aptopt='Acquire::AllowInsecureRepositories "false"' \
  --aptopt='APT::Get::AllowUnauthenticated "false"' \
  --aptopt='Acquire::Check-Valid-Until "true"' \
  trixie "$root" "$sources"
chroot "$root" dpkg-query -W '-f=${Package}\t${Version}\t${Architecture}\t${db:Status-Status}\n' \
  > "$output/packages.installed.tsv"
node "$helper" installed "$bundle" "$lock" "$digest" "$output/packages.installed.tsv"
node "$helper" overlay "$bundle" "$lock" "$digest" "$root"

shim="$root/usr/lib/shim/shimx64.efi.signed"
grub="$root/usr/lib/grub/x86_64-efi-signed/grubx64.efi.signed"
vmlinuz="$root/boot/vmlinuz-$kernel"
[[ -s "$root/boot/initrd.img-$kernel" && -s "$shim" && -s "$grub" && -s "$vmlinuz" ]] || {
  printf '%s\n' 'Missing signed boot chain or initramfs. Never substitute unsigned GRUB/kernel.' >&2
  exit 1
}
sbverify --cert "$ms_cert" "$shim"
sbverify --cert "$grub_cert" "$grub"
sbverify --cert "$kernel_cert" "$vmlinuz"
root_uuid=42506567-6510-4000-8000-000000000001
cat > "$root/etc/fstab" <<EOF
UUID=$root_uuid / ext4 ro,nodev 0 1
tmpfs /tmp tmpfs rw,nosuid,nodev,size=128M,mode=1777 0 0
tmpfs /var/log tmpfs rw,nosuid,nodev,noexec,size=16M 0 0
EOF
mkdir -p -- "$root/boot/grub"
cat > "$root/boot/grub/grub.cfg" <<EOF
set timeout=0
set default=0
menuentry 'AIPlatform static appliance' {
    search --no-floppy --fs-uuid --set=root $root_uuid
    linux /boot/vmlinuz-$kernel root=UUID=$root_uuid ro quiet
    initrd /boot/initrd.img-$kernel
}
EOF
# No grub-install or efibootmgr: never touch the builder's boot disk/NVRAM.
truncate -s 268435456 "$output/esp.fat"
mkfs.vfat --invariant -F 32 -i 65006500 "$output/esp.fat"
mmd -i "$output/esp.fat" ::/EFI ::/EFI/BOOT ::/EFI/debian
for target in ::/EFI/BOOT/BOOTX64.EFI ::/EFI/debian/shimx64.efi; do
  mcopy -i "$output/esp.fat" "$shim" "$target"
done
for target in ::/EFI/BOOT/grubx64.efi ::/EFI/debian/grubx64.efi; do
  mcopy -i "$output/esp.fat" "$grub" "$target"
done
cat > "$output/grub-esp.cfg" <<EOF
search --no-floppy --fs-uuid --set=root $root_uuid
configfile /boot/grub/grub.cfg
EOF
for target in ::/EFI/BOOT/grub.cfg ::/EFI/debian/grub.cfg; do
  mcopy -i "$output/esp.fat" "$output/grub-esp.cfg" "$target"
done
truncate -s 6171918336 "$output/root.ext4"
mkfs.ext4 -q -F -U "$root_uuid" -L preview-root -d "$root" \
  -E lazy_itable_init=0,lazy_journal_init=0 "$output/root.ext4"
truncate -s 6442450944 "$output/disk.raw"
sfdisk "$output/disk.raw" <<'EOF'
label: gpt
label-id: 42506567-6510-4000-8000-000000000002
unit: sectors

start=2048, size=524288, type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B, uuid=42506567-6510-4000-8000-000000000003
start=526336, size=12054528, type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, uuid=42506567-6510-4000-8000-000000000004
EOF
dd if="$output/esp.fat" of="$output/disk.raw" bs=512 seek=2048 conv=notrunc status=none
dd if="$output/root.ext4" of="$output/disk.raw" bs=512 seek=526336 conv=notrunc status=none
qemu-img convert -f raw -O vhdx -o subformat=fixed "$output/disk.raw" "$output/appliance.vhdx"
qemu-img info --output=json "$output/appliance.vhdx" > "$output/vhdx-info.json"
node "$helper" image-info "$output/vhdx-info.json" "$output/appliance.vhdx"
node "$helper" manifest "$output/build-manifest.json" "$digest" \
  "$output/appliance.vhdx" "$output/vhdx-info.json" "$output/packages.installed.tsv" "$script" "$helper"
printf '%s\n' 'Build artifacts only. Not signed, boot-tested, isolated, reproducible-bit-for-bit certified or authorized for Preview.'
BUILD
