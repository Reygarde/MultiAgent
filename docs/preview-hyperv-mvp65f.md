# MVP 6.5-F - Dossier de preparation Hyper-V statique

Date : 2026-11-14. Revision : 65f-r1. **Examinable, non executable.**
Suite du [choix 6.5-E](preview-virtualisee-mvp65e.md).
Ce dossier ne constitue ni une autorisation Preview ni une demande UAC.
HTTP 503 reste actif ; aucun runtime de production n'est modifie.

## 1. Decision, elements disponibles et blocages

Choix fixe : Debian **13.7.0 amd64 (trixie)**, appliance Linux Hyper-V Gen2,
Chromium/Weston logiciels, sans carte reseau, deux flux HvSocket.
Seuls des documents et metadonnees officiels ont ete consultes en ligne.
**Aucun ISO, paquet, VHDX, binaire ou cle n'a ete telecharge ou execute.**
La [page nominale](hyperv65f-static.html) est preparee mais non executee.

Le [plan JSON](preview-hyperv-mvp65f.plan.json) est un manifeste de preparation
separe du schema Preview v1. Son empreinte est dans le
[fichier SHA-256](preview-hyperv-mvp65f.sha256).
Il porte deliberement `executionEligible: false`.

| Bloquant | Etat reel / condition de levee |
|---|---|
| B1 - appliance | Aucun VHDX construit, ni SHA-256 d'appliance ; le SHA de l'ISO ci-dessous n'est pas celui d'une appliance |
| B2 - logiciels propres | Broker, agent, module Weston, viewer et superviseur Hyper-V non implementes ; version cible seulement, aucun binaire certifie |
| B3 - chaine d'approvisionnement | Signatures et fermeture transitive des paquets non verifiees ; aucun verificateur OpenPGP trouve sur ce PATH |
| B4 - canaux | Pas de preuve de refus par VM des autres endpoints HvSocket/VMBus ; inscription au registre != filtre de securite |
| B5 - gestion Windows | `Get-VMHost` refuse par les droits actuels ; configuration hote complete et inventaire par VM non obtenus |
| B6 - ressources | 1,721 Gio disponibles au second controle, < 4,5 Gio ; aucun processus ferme |
| B7 - cycle de vie | Supervision independante, borne de 180 s et recuperation apres crash non implementes ni testes |
| B8 - support hote | Windows 11 Pro 23H2 build 22631.6199 confirme ; fin de support de cette version en novembre 2025, aucune mise a jour effectuee ici |

Une empreinte de preparation ne remplace aucun de ces elements. L'execution
exigera une nouvelle revision complete, de vraies empreintes et un consentement
distinct. Il serait inexact de presenter ce dossier comme deja autorisable au
boot, ou de remplir les champs inconnus avec des hash fictifs.

## 2. Image Linux : provenance, versions et verification

### 2.1 Support officiel de depart

| Champ | Valeur figee proposee |
|---|---|
| Distribution / architecture | Debian GNU/Linux 13.7.0, trixie, amd64 |
| Support d'amorcage du constructeur | `debian-13.7.0-amd64-netinst.iso` |
| Origine | `https://cdimage.debian.org/debian-cd/13.7.0/amd64/iso-cd/` |
| SHA-256 publie | `a7ef94ac2fb9a7fec454552abd629b7cc9d5155c886165a45649f5ce6167e355` |
| Metadonnees | `SHA256SUMS`, signature detachee `SHA256SUMS.sign` au meme emplacement |
| Verifie ici | Lecture HTTPS des sommes officielles, correspondance nom/version |
| NON verifie ici | Signature OpenPGP, octets de l'ISO, boot et chaine Secure Boot |

Le chemin versionne a ete consulte avec succes. Le chemin d'archive
`cdimage/archive/13.7.0/...` repondait 404 : il n'est pas utilise comme provenance.
Ne pas remplacer par `current` lors d'une acquisition future.
Un netinst n'est **pas** une image contenant deja Chromium, Weston ou le broker ;
il ne sera jamais branche sur la VM de test comme substitut a l'appliance.

Chaine de verification prevue, dans un environnement de construction approuve :

1. Obtenir le trousseau Debian par une chaine de confiance independante et
   approuver explicitement son empreinte ; pas de confiance au simple UID.
2. Verifier `SHA256SUMS.sign` avec `gpgv --keyring ./debian-role-keys.pgp
   SHA256SUMS.sign SHA256SUMS`. La page Debian cite notamment la cle CD stable
   `DF9B9C49EAA9298432589D76DA87E80D6294BE9B` ; verifier aussi validite,
   revocation et signataire effectif, ne pas annoncer qu'elle a signe ce fichier
   sans verification. Rotation = examen, pas acceptation automatique.
3. Comparer les octets de l'ISO au SHA-256 approuve, puis seulement permettre son
   utilisation par le constructeur. Telechargement en quarantaine sans execution,
   jamais montage automatique. Acquisition de binaires distinctement autorisee.
4. Verifier les `InRelease` Debian avec `debian-archive-keyring`, puis les
   SHA-256 des index et de **chaque** paquet de la fermeture des dependances.
   Une page packages.debian.org n'est pas une signature de paquet.

### 2.2 Composants cibles et sommes publiees

Versions vues sur les pages officielles, pas paquets executes :

| Composant | Version cible / SHA-256 du paquet amd64 publie |
|---|---|
| Chromium | `156.0.8078.79-1~deb13u1` ; `9325777fb842eafa3f992209ee6f1b69bad76c3311a88efd4d7772cec22e3795` |
| Weston | `14.0.2-1` ; `cb2cddfc082a6f14d15e4682cdbb9778439f9b89123ed41356f108ccb3ebb48e` |
| Noyau signe | `linux-image-6.12.117+deb13-amd64`, paquet `6.12.117-1` ; `ca712d36790184f6ac125ce2b20ea4e2202ae6bb7817b7de268f1f0115ccda73` |
| Broker hote / agent invite | Cible `0.1.0-design`, protocole `65e-binary-v1` ; source/binaire absents, SHA-256 **non disponible** |
| Module de capture/entrees libweston | Cible `0.1.0-design`, ABI libweston-14 ; non implemente, SHA-256 **non disponible** |
| Viewer / superviseur independant | Cible `0.1.0-design` ; non implementes, SHA-256 **non disponible** |

Ajouter au lock complet : `chromium-common`, `chromium-sandbox` exactement de
meme version que Chromium ; `libweston-14-0`, Pixman, Wayland et bibliotheques ;
systemd, initramfs, shim/GRUB signes, libc, certificats, polices locales.
Construction : mmdebstrap, apt, debian-archive-keyring, e2fsprogs, dosfstools,
outils GPT et qemu-utils dans un constructeur Linux distinct. **Leurs versions
et hashes transitifs ne sont pas encore verrouilles** : B3, pas de `latest`.
Aucun de ces outils n'est installe sur ce Windows par cette etape.

Sources temporelles proposees :

```text
https://snapshot.debian.org/archive/debian/20261114T000000Z/ trixie main
https://snapshot.debian.org/archive/debian-security/20261114T000000Z/ trixie-security main
```

Leurs fichiers Release ont ete lus : Debian 13.7 date du 2026-09-12 ; l'index
security lu est date du 2026-11-13, valide jusqu'au 2026-11-20.
Les versions ci-dessus doivent encore etre retrouvees dans leurs index signes :
la concordance entre pages courantes et snapshot n'est **pas** attestee.
Si absentes ou deja vulnerables, refuser et soumettre une nouvelle revision.
Ne pas desactiver globalement les signatures ou `Valid-Until` pour y parvenir.

### 2.3 Recette de construction a figer, sans construction ici

1. Construire hors de Studio, sur un constructeur approuve et ephemere, sans
   StudioData, identifiants Studio, disque Windows partage ou mission v9.
2. Geler snapshot, `InRelease`, index, versions, URL, taille et SHA de tous les
   paquets/outils dans `packages.lock.json`. Fermer les dependances, sans
   recommends, source Debian main seulement ; signer le manifeste de collecte.
   Une dependance manquante arrete la construction, aucun depot de secours.
3. Separer collecte verifiee et assemblage offline. Fixer locale C.UTF-8,
   fuseau UTC, epoch source 2026-11-14T00:00:00Z, UID/GID et UUID GPT/FS de
   construction dans la recette versionnee. Pas d'horloge de build dans la page.
4. Creer un VHDX **Fixed de 6 Gio virtuels**, GPT, ESP FAT et racine Linux ;
   installer shim/GRUB/noyau signes. Racine en lecture seule au boot, profil
   Chromium/work/logs en tmpfs bornes, aucun swap ni partition hote.
5. Noyau : valider VMBus, stockage Hyper-V et hv_sock/AF_VSOCK ; pilotes
   CONFIG_VSOCKET/CONFIG_HYPERV_VSOCKETS disponibles dans l'initramfs ou noyau.
   Pas de fcopy, KVP, VSS, reseau, RDP, SSH, automount, console de login,
   cloud-init ou mise a jour en ligne au runtime. Comptes sans mot de passe
   utilisable, navigateur non root, sandbox et WebRTC conserves.
6. Installer Weston headless, renderer Pixman, sortie 960x540. Implementer le
   module de capture pixels/entrees **dans l'invite** ; lancer Weston headless
   ne cree pas par magie une API de capture ou d'injection. Chromium Wayland
   rend seulement `http://127.0.0.1:5080/`, page embarquee servie par l'agent fixe.
   Aucun CDP, VNC/RDP, serveur graphique ou shell accessible depuis Windows.
7. Agent seul autorise a AF_VSOCK ; navigateur/enfants sans ce droit, sans
   capacites et avec restrictions de montage/cgroups. Deux ports fixes seulement.
   Le root invite reste traite comme hostile pour les tests des canaux hote.
8. Ajouter page nominale, sondes de securite fixes, watchdog et services systemd
   enumeres. Pas de scripts/commandes recueillis depuis la page ou le broker.
   SBOM/licences et manifeste source/build, aucun historique/secret constructeur.
9. Deux builds propres : comparer lock, contenu FS et composants ; documenter
   toute difference de metadonnees. La reproductibilite **bit a bit du VHDX
   n'est pas demontree** : timestamps/GUID de conteneur ne doivent pas etre
   masques. Signer et hasher l'artefact final exact choisi, de taille bornee.
10. Livrer VHDX, manifeste signe, lock, versions/hash des logiciels propres,
    resultats de build et nouvelle empreinte de plan. Aucun boot de cette
    appliance dans le cadre de 6.5-F.

Politique de maintenance : image immuable, aucune auto-update en session.
Avant chaque campagne, revue des avis Debian/Chromium/Hyper-V et de l'expiration
des signatures/index. Correctif de securite applicable ou modification d'un
composant = reconstruction, nouvelles empreintes et nouvel examen ; jamais
de mise a jour silencieuse d'une image deja approuvee.

## 3. Configuration Hyper-V precise proposee

| Parametre | Valeur |
|---|---|
| Plan / session de laboratoire | `b7b65f01-9a39-4c72-9180-8efa162aa6f0`, revision 1 |
| Nom VM | `AIPlatform-Preview-65F-b7b65f01` |
| VMID Hyper-V | **Non alloue** ; genere par New-VM, capture et lie au journal avant boot |
| Generation / firmware | 2 / UEFI, Secure Boot On, template MicrosoftUEFICertificateAuthority |
| Console firmware | None ; aucun VMConnect, Enhanced Session ou RDP utilise |
| CPU / memoire | 2 vCPU, maximum CPU Hyper-V 50 %, extensions imbriquees false ; 2147483648 octets fixes, memoire dynamique false |
| Boot | Disque SCSI 0:0 exclusivement, pas PXE/DVD boot |
| Disque runtime | Copie privee Fixed VHDX 6 Gio ; jamais base commune writable, differencing ou disque physique |
| Configuration | Un ISO read-only <= 2 Mio fabrique par l'hote, aucun autorun, seulement SessionId/2 challenges de 32 octets et politique validee |
| Reseau | Zero vNIC avant tout boot ; aucune association a un switch, meme prive ou Default |
| Integrations | Toutes les integrations facultatives de cette VM desactivees |
| Peripheriques | Aucun GPU/USB/audio/camera/presse-papiers/lecteur Windows partage, vTPM non active |
| Reprise | AutomaticStartAction Nothing, AutomaticStopAction TurnOff, checkpoints Disabled et automatiques false |
| Rendu | 960x540 BGRA8, 10 Hz, une image en attente, aucun codec compresse |
| Delais | Readiness 90 s ; plafond 180 s depuis l'appel Start-VM, pas depuis Ready ; aucun retry |
| Stockage hote | C:\ProgramData\AIPlatformPreviewPrototype\65f\b7b65f01-9a39-4c72-9180-8efa162aa6f0 |

New-VM **n'a pas de parametre -Id** dans le module local. Inventer un VMID
avant creation serait faux. L'autorisation future doit explicitement accepter
cette allocation liee au SessionId/chemin exact ; si l'exigence impose un VMID
preconnu, le plan reste bloque, sans import frauduleux d'une VM existante.

Le disque Fixed copie privee remplace ici le budget d'overlay de 1 Gio de 6.5-E :
cela borne physiquement le fichier a la taille fixe verifiee, sans pretendu
quota de repertoire NTFS. Budget fichier <= 6 Gio + 64 Mio, config <= 2 Mio,
logs hote <= 64 Kio, au moins 15 Gio libres avant copie.
L'hote ne monte jamais le disque apres ecriture invitee.

Le mode console None ne supprime pas VMBus ni les droits d'un administrateur
Hyper-V. L'absence de xrdp/Enhanced Session cote invite et l'absence de client
VMConnect evitent les redirections ; aucun Set-VMHost global ne sera effectue.
Des peripheriques virtuels firmware/clavier/video peuvent toujours etre
presentes par Hyper-V : ce ne sont pas des peripheriques Windows rediriges.
Leur inventaire effectif est un gate, pas une absence deja prouvee.

## 4. Inventaire des canaux, sans modification

### Observe sur l'hote a 05:06 environ

- `vmms` et `vmcompute` actifs.
- Services `vmicguestinterface`, `vmicheartbeat`, `vmickvpexchange`, `vmicrdv`,
  `vmicshutdown`, `vmictimesync`, `vmicvmsession`, `vmicvss` presents et arretes.
  Ce sont les services Windows **du poste**, pas l'etat des integrations d'une
  future VM Linux ; ils n'ont pas ete modifies.
- Pilotes observes : VMBus Provider 10.0.22621.6133, Virtual Disk Server
  10.0.22621.6199, infrastructure/PCI Hyper-V 10.0.22621.5415 et Virtual Switch
  Extension Adapter 10.0.22621.1. Presence != exposition de ces pilotes au guest.
- Deux entrees sous GuestCommunicationServices, uniquement valeur ElementName :

| ServiceId enregistre | Nom |
|---|---|
| 999E53D4-3D5C-4C3E-8779-BED06EC056E1 | VM Session Service |
| A5201C21-2770-4C11-A68E-F182EDB29220 | VM Session Service-2 |

Leurs ACL de registre sont lisibles mais **ne sont pas les ACL des sockets**.
Leur enregistrement ne prouve ni ecoute ni accessibilite depuis Linux.
Il n'est pas permis de les supprimer/reconfigurer pour faciliter Preview.
Le registre ne garantit pas non plus un inventaire exhaustif des listeners
HCS/WSL ou autres : inventaire WMI et essais invites restent requis.
`Get-VMHost` a retourne acces refuse. Aucune VM n'etant creee, pas de
`Get-VMIntegrationService` ni de liste /sys/bus/vmbus/devices invitee possible.

### Liste d'autorisation cible

| Canal | Decision |
|---|---|
| Hyperviseur/VMBus noyau, interrupts/timers, UEFI | Necessaires au boot, pas commandes de gestion fournies au guest |
| Stockage synthetique hv_storvsc | Un disque copie privee + support config read-only seulement |
| hv_sock / AF_VSOCK | Deux services ci-dessous, lies a la VM exacte et a la session |
| hv_netvsc / carte reseau | Aucune vNIC ; aucune interface autre que loopback dans l'invite |
| KVP, fcopy/Copy-VMFile, VSS/backup, heartbeat/time-sync/shutdown d'integration | Desactives sur cette VM ; Stop/heartbeat passent par le protocole propre |
| PowerShell Direct / VM Session Services | Aucune utilisation ; tester le refus d'acces invite vers services hote existants |
| RDP/Enhanced Session, partage fichiers/clipboard, COM named pipe, GPU/PCI passthrough | Interdits ; aucun daemon/service de ce type dans l'image |
| Canal inconnu | Refus de boot/certification tant que nature, exposition et traitement non examines |

Deux services **proposes, non enregistres**, conformes au template Linux :

| Role | Port AF_VSOCK decimal | ServiceId Windows | ElementName |
|---|---|---|---|
| Controle | 50065 (0xc391) | 0000c391-facb-11e6-bd58-64006a7986d3 | AIPlatform Preview 65F Control |
| Images | 50066 (0xc392) | 0000c392-facb-11e6-bd58-64006a7986d3 | AIPlatform Preview 65F Frames |

L'hote ecouterait sur le **VMID retourne par New-VM**, jamais ZERO/WILDCARD/
CHILDREN. Le guest se connecte au CID hote 2 et aux deux ports fixes.
Cette direction precise le choix de transport 6.5-E. Un listener host lie a
la VM exacte n'interdit **pas** au guest de contacter d'autres listeners hote.
Aucune propriete de registre ACL non documentee n'est inventee pour le promettre.
Avant certification : verifier accessibilite des deux services existants,
autres endpoints inventories et tentatives natives root/enfants ; toute
connexion/operation interdite ou impossibilite d'inventaire est un echec.
Si un refus par session ne peut pas etre demontre sans changement systeme
global, arreter et revoir le transport ; pas de pare-feu global improvise.

## 5. Commandes prevues, NON EXECUTEES

Ces blocs sont des **primitives de revue**, pas un orchestrateur livre.
Ils commencent par un refus inconditionnel pour eviter une execution accidentelle.
Ne pas retirer ce verrou : les B1-B8 doivent etre traites dans une nouvelle revision.
Les arguments Hyper-V ont ete compares aux metadonnees du module local.
Presence d'un parametre != test reel de ses effets.

### 5.1 Construction de la VM, apres approbation future uniquement

```powershell
throw '65F preparation only: VM creation and start are NOT authorized.'
$ErrorActionPreference = 'Stop'
$SessionId = 'b7b65f01-9a39-4c72-9180-8efa162aa6f0'
$VmName = 'AIPlatform-Preview-65F-b7b65f01'
$Root = 'C:\ProgramData\AIPlatformPreviewPrototype\65f\b7b65f01-9a39-4c72-9180-8efa162aa6f0'
$Base = 'C:\ProgramData\AIPlatformPreviewPrototype\images\debian-13.7.0-preview65f-0.1.0.vhdx'
$Disk = Join-Path $Root 'runtime.vhdx'
$Config = Join-Path $Root 'session-config.iso'
$VmDirectory = Join-Path $Root 'vm'
$Journal = Join-Path $Root 'ownership.json'
$Plan = Get-Content -LiteralPath '.\docs\preview-hyperv-mvp65f.plan.json' -Raw | ConvertFrom-Json
if (-not $Plan.executionEligible -or -not $Plan.image.applianceSha256) {
    throw 'Missing execution-approved appliance and plan.'
}
$os = Get-CimInstance Win32_OperatingSystem
if ($os.FreePhysicalMemory -lt 4718592) { throw 'At least 4.5 GiB free required.' }
if ((Get-PSDrive C).Free -lt 15GB) { throw 'At least 15 GiB free required.' }
if (Test-Path -LiteralPath $Root) { throw 'Session path collision.' }
$baseInfo = Get-VHD -Path $Base
if ($baseInfo.VhdType -ne 'Fixed' -or $baseInfo.VhdFormat -ne 'VHDX' -or
    $baseInfo.Size -ne 6GB -or $baseInfo.ParentPath) { throw 'Unexpected base disk.' }
if ((Get-FileHash -LiteralPath $Base -Algorithm SHA256).Hash -ne $Plan.image.applianceSha256) {
    throw 'Appliance digest mismatch.'
}
# The reviewed controller must first create and ACL the ownership journal.
# It must reject reparse points, bind artifact signatures, and stage Config.
Copy-Item -LiteralPath $Base -Destination $Disk -ErrorAction Stop
(Get-Item -LiteralPath $Disk).IsReadOnly = $false
if ((Get-Item -LiteralPath $Disk).Length -gt (6GB + 64MB)) { throw 'Disk budget exceeded.' }
$vm = New-VM -Name $VmName -Generation 2 -MemoryStartupBytes 2GB -NoVHD -Path $VmDirectory
$VmId = $vm.Id
@{ SessionId = $SessionId; VmId = $VmId.Guid; Root = $Root; Phase = 'Created' } |
    ConvertTo-Json | Set-Content -LiteralPath $Journal -Encoding UTF8
Set-VM -VM $vm -Notes "AIPlatform-65F:$SessionId" -AutomaticStartAction Nothing `
    -AutomaticStopAction TurnOff -AutomaticCheckpointsEnabled $false -CheckpointType Disabled
Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes 2GB
Set-VMProcessor -VM $vm -Count 2 -Maximum 50 -ExposeVirtualizationExtensions $false
Get-VMNetworkAdapter -VM $vm | Remove-VMNetworkAdapter -Confirm:$false
Get-VMDvdDrive -VM $vm | Remove-VMDvdDrive -Confirm:$false
Get-VMIntegrationService -VM $vm | Disable-VMIntegrationService
Add-VMHardDiskDrive -VM $vm -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0 -Path $Disk
Add-VMDvdDrive -VM $vm -ControllerNumber 0 -ControllerLocation 1 -Path $Config
$boot = Get-VMHardDiskDrive -VM $vm
Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftUEFICertificateAuthority `
    -FirstBootDevice $boot -ConsoleMode None
if (@(Get-VMNetworkAdapter -VM $vm).Count -ne 0) { throw 'NIC detected: refuse boot.' }
if (@(Get-VMIntegrationService -VM $vm | Where-Object Enabled).Count -ne 0) {
    throw 'Unexpected enabled integration: refuse boot.'
}
```

Le commentaire de preparation represente un composant **manquant**, pas une
etape executee ou un helper implicite. Ne pas remplacer le journal durable,
ACL, signatures ou ISO par un fichier vide. L'exemple Set-Content illustre le
contenu du journal, pas une implementation transactionnelle/flush fiable.
Le controleur final reverifiera apres creation CPU, RAM, firmware, tous les
disques/ISO et leurs tailles/hash, vTPM, peripheriques, integrations, identite,
canaux, watchdog et superviseur **avant** Start-VM.
Les suppressions de cartes/DVD visent seulement l'objet `$vm` nouvellement cree.
La qualification reelle des canaux implique un premier boot de laboratoire :
elle ne peut pas etre presentee comme deja reussie avant ce boot. Ce premier
essai necessitera un consentement de risque explicite, inventaire hote complet
et allowlist de l'image examines, sans accorder une autorisation Preview.
Toute offre VMBus inconnue au boot interdit de poursuivre les sondes applicatives.

### 5.2 Enregistrement HvSocket : operation administrateur distincte

```powershell
throw '65F preparation only: registry writes are NOT authorized.'
$ErrorActionPreference = 'Stop'
$Key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices'
$Services = @(
    @{ Id = '0000c391-facb-11e6-bd58-64006a7986d3'; Name = 'AIPlatform Preview 65F Control' },
    @{ Id = '0000c392-facb-11e6-bd58-64006a7986d3'; Name = 'AIPlatform Preview 65F Frames' }
)
foreach ($s in $Services) {
    if (Test-Path -LiteralPath (Join-Path $Key $s.Id)) { throw 'ServiceId collision: do not modify.' }
}
foreach ($s in $Services) {
    $p = Join-Path $Key $s.Id
    New-Item -Path $p -ErrorAction Stop | Out-Null
    New-ItemProperty -LiteralPath $p -Name ElementName -PropertyType String -Value $s.Name | Out-Null
}
```

Le controleur doit journaliser l'intention et la reussite de **chaque** ecriture.
Collision ou erreur partielle : pas de boot ; nettoyer seulement les creations
prouvees. Aucun nom de service existant n'est reutilise. Les deux inscriptions
seraient temporaires et retirees apres campagne, pas a chaque requete de page.

### 5.3 Demarrage, arret force et retrait exacts

```powershell
throw '65F preparation only: start, stop and deletion are NOT authorized.'
$ErrorActionPreference = 'Stop'
# VmId must come from the validated ownership journal, never a page or a name prefix.
$vm = Get-VM -Id $VmId
Start-VM -VM $vm -ErrorAction Stop
# Normal stop uses the fixed broker Stop message. If not Off within its deadline:
Stop-VM -VM $vm -TurnOff -Confirm:$false -ErrorAction Stop
$vm = Get-VM -Id $VmId
if ($vm.State -ne 'Off') { throw 'CleanupFailed: VM not Off.' }
Remove-VM -VM $vm -Force -ErrorAction Stop
```

Start-VM n'est autorise qu'apres armer un superviseur independant, le journal et
le chronometre monotone. Stop normal vise t0+175 s au plus tard, extinction
forcee declenchee au plus tard t0+178 s, confirmation Off avant t0+180 s.
Ces marges sont des **criteres a tester**, pas une garantie temps reel Windows.
Le delai de readiness 90 s n'ajoute pas 90 s au budget global.
Au crash du broker : revoquer inputs/handles immediatement, demander Stop puis
forcer l'arret en <= 3 s, sans attendre la fin des 180 s.
Ne jamais tuer vmms, vmcompute, vmwp ou un processus identifie par son seul nom.

### 5.4 Suppression des fichiers et cles, apres verification d'appartenance

```powershell
throw '65F preparation only: cleanup writes are NOT authorized.'
$ErrorActionPreference = 'Stop'
# Ownership, exact VM absence, closed handles and non-reparse paths must be proven first.
Remove-Item -LiteralPath $Disk -ErrorAction Stop
Remove-Item -LiteralPath $Config -ErrorAction Stop
# Only remove the two journal-owned, unchanged, childless service keys.
foreach ($s in $Services) {
    $p = Join-Path $Key $s.Id
    if (@(Get-ChildItem -LiteralPath $p).Count -ne 0) { throw 'Unexpected registry children.' }
    if ((Get-ItemProperty -LiteralPath $p -Name ElementName).ElementName -ne $s.Name) {
        throw 'Registry ownership mismatch.'
    }
    Remove-Item -LiteralPath $p -ErrorAction Stop
}
```

Pas de suppression recursive de racine. Apres Remove-VM, enumerer les fichiers
de configuration restants sous `$VmDirectory`, comparer au journal des creations,
refuser tout objet inconnu/reparse et supprimer **chaque chemin exact approuve**.
Ces noms de fichiers contiennent le VMID non alloue ; impossible de donner ici
une fausse liste finale. Puis retirer les seuls dossiers vides, journal en
dernier ; garder un resultat filtre hors du dossier supprime.
Confirmer absence VM, disque, ISO, listeners, processus de test et cles creees.

### Droits

Les lectures CIM/registre/metadonnees effectuees ici ne demandent pas d'UAC.
La gestion Hyper-V est refusee au compte courant ; creation/configuration/
start/stop/retrait necessitent un operateur administrateur ou des droits de
gestion deja delegues et verifies. L'ecriture HKLM exige une operation
administrateur distincte. Ne pas ajouter automatiquement l'utilisateur au groupe
Hyper-V Administrators, elever Studio ou modifier les ACL de VM tierces.
La creation/ACL du stockage ProgramData demande aussi un provisionnement
autorise. Aucun service Windows, tache planifiee, regle pare-feu ou capture
privilegiee n'est installe pour masquer un prerequis.

## 6. Tests prepares et criteres de reussite

**Aucun de ces tests VM n'a ete execute.** Campagne future en instances fraiches,
sans mission generee, avec temoins positifs et compteurs reels Windows avant/
apres. Ne pas connecter une vNIC, meme pour les temoins : temoins Windows depuis
Windows et temoins de fonctionnement des sondes dans l'invite.
Les faux services lient seulement des adresses possedees, ports controles ;
pas de trafic vers Internet ni de tentative sur un vrai secret Studio.

| ID | Test | Critere |
|---|---|---|
| T01 | Boot + Chromium/Weston et enfants | VM Running ne suffit pas : processus, JS_EXECUTED et Ready observes <= 90 s |
| T02 | Page nominale | SHA source approuve, repere vert a (0,0), texte PREVIEW_65F_STATIC_V1, premiere frame coherente ; fonts/version fixes, pas de hash complet de pixels invente |
| T03 | Frames HvSocket | 960x540, stride 3840, metadata 16 + 2073600 octets, BGRA8, alpha force opaque, <= 10 Hz, 1 frame non acquittee, 2 buffers |
| T04 | Entrees | Clic connu -> compteur +1, texte test borne -> echo ; hors focus/coordonnees/debit refuses, blur/Stop relachent touches, aucun input Windows |
| T05 | HTTP/XHR/fetch, img/CSS, iframe/Worker/popup/form/navigation | Faux Studio et autres ports Windows, 127.0.0.1, 127.0.0.2, ::1 et adresses Windows controlees : 0 connexion recue |
| T06 | UDP/STUN WebRTC, DNS UDP/TCP, ws/wss | WebRTC actif et sondes reellement executees, temoins invites positifs ; 0 paquet/connexion Windows ; descendance/root inclus |
| T07 | Redirections + destinations privees/link-local | Serveur guest 30x vers recepteurs controles, file/custom/UNC ; 0 acces hote, aucun lancement d'application Windows |
| T08 | Fichiers Windows | Sentinelle de test hors StudioData : pas de lecture/ecriture, ni /mnt/c, SMB/9p/virtiofs, montage hote, Copy-VMFile ou clipboard |
| T09 | VMBus/HvSocket hors allowlist | Inventaire guest /sys/bus/vmbus/devices et drivers, etat integrations hote ; sondes natives root/enfants sur services existants/autres endpoints inventories ; 0 connexion ou operation interdite |
| T10 | Protocole hostile | Longueurs/overflow/troncatures/session/sequence/role invalides, flood et backpressure : refus explicite, RAM bornee, aucune commande hote |
| T11 | Stop manuel | Inputs revoques immediatement, VM Off confirme <= 3 s, nettoyage exact ; ancienne connexion/session non reutilisable |
| T12 | Expiration | Chronometre depuis Start-VM, Off observe avant 180 s meme sans Ready, guest ou images ; tout depassement = echec |
| T13 | Crash broker / controleur | Processus possedes seulement, superviseur independant eteint VM, handles fermes ; pas de Job Object suppose suffisant pour Hyper-V |
| T14 | Echecs partiels / recovery | Interruption apres chaque allocation, verrouillage disque, droits retires : aucune suppression tierce, residu signale CleanupFailed et futurs starts bloques |
| T15 | Suppression / portee | VM/disque/ISO/cles/listeners absents ; image de base et services/VM/applications tiers inchanges, temoins Windows encore fonctionnels |

Le protocole conserve les bornes 6.5-E : header 48 octets, payload <= 4 Mio,
delais header/payload 2 s, 200 messages/s burst 256, images 40 Mio/s burst 4 Mio,
Input <= 512 octets/120 Hz, mouvements coalesces 30 Hz, file 64, texte <= 256
octets UTF-8 ; logs enumerees <= 1024 octets/10 Hz, ring 64 Kio et 64 evenements.
Handshake distinct pour chaque flux, challenge 32 octets, role/SessionId/VMID,
sequence monotone ; aucun token Studio. Page/app n'ont pas acces a AF_VSOCK.

Un zero de paquets avec navigateur plante, sonde non executee ou temoin absent
est **non concluant**, pas reussi. Un seul canal non observe ou accessible garde
la certification fermee. Les compteurs de serveurs ne remplacent pas une capture
IP globale ni l'inventaire VMBus ; les limites d'observation doivent etre notees.

## 7. Ressources et recuperation apres crash

Mesures CIM en lecture seule du 2026-11-14 :

| Heure locale +01:00 | Libre KiB | Libre Gio | Gate 4,5 Gio |
|---|---|---|---|
| 05:05:19 | 1652092 | 1,576 | Echec |
| 05:07:10 | 1804128 | 1,721 | Echec |

RAM totale visible : 15927668 KiB (~15,19 Gio). C: libre ~100,43 Gio au second
controle. Rien n'est reserve ; recontrole immediat avant allocation et boot,
refus sous 4718592 KiB. Aucun arret d'application/WSL/service pour liberer RAM.

Journal hote a ACL restrictive, hors StudioData : PlanId, empreinte approuvee,
phase d'intention/reussite, VMID retourne, chemins canoniques, creations de
registre et de fichiers, delais monotones et identites de processus possedes.
Ecriture durable avant effets, sauvegarde atomique apres ; jamais un nom VM seul.

Hyper-V survit au crash du client. Superviseur independant de l'analyseur
invite, avec bail 1 Hz et expiration 3 s ; il doit exister avant boot.
Un watchdog invite est une defense supplementaire, pas une preuve d'arret d'un
invite compromis. Sans superviseur valide, aucun start.
Un crash de tous les composants hote n'est pas couvert par un Job Object :
pas d'auto-start/save-state, recuperation obligatoire au prochain lancement.
Si un plafond strict 180 s doit survivre a cette panne totale, il faut un
mecanisme hote independant additionnel a autoriser/qualifier ; non promis ici.

Recovery : lire et valider journal/hash/ACL/chemins, retrouver **VMID exact**,
invalider flux/entrees, eteindre seulement cette VM, confirmer Off, retirer VM,
disque, ISO, fichiers connus et cles prouvees. Si crash entre New-VM et
journalisation du VMID : garder l'intention et le dossier prive comme preuves,
ne pas supprimer automatiquement une VM sur son seul nom ; ambiguite =
quarantaine et examen operateur. Ni effacement aveugle ni succes fictif.
Image de base jamais supprimee ; registre preexistant jamais modifie.

## 8. Plan canonique et limites de l'examen

Le plan JSON contient politique, versions cibles, sommes publiees, canaux,
limites, tests et blocages. Les valeurs d'artefacts non produits sont `null`,
jamais des valeurs de remplacement executees. SHA-256 et signatures d'appliance,
lock, binaires, inventaire effectif et supervision devront etre ajoutes dans une
**nouvelle revision**, avec nouvelles empreintes et examen.

Canonicalisation `65f-ascii-json-v1` : JSON sans cles dupliquees, cles/chaines
ASCII uniquement, nombres entiers surs, bool/null/tableaux/objets ; tri recursif
ordinal des cles, ordre des tableaux conserve, JSON compact UTF-8 sans BOM ni
newline. SHA-256 de ces octets, hexadecimal minuscule. Le fichier .sha256 indique
explicitement cette transformation, ce n'est pas un hash du pretty-print.
Le plan lie aussi les octets exacts UTF-8 du present dossier et de la page par
SHA-256 : modification de recette/commandes/page = empreinte a recalculer.
Le digest n'est ni une signature ni un jeton d'autorisation.

Contrats restant a realiser : appliance verifiee, module Weston, agent/broker/
viewer/superviseur, signatures/lock, politique par VM des autres HvSockets,
preuve des delais et cleanup. Risques residuels : hyperviseur/VMBus, parsing du
broker, ecran trompeur, fuite par side-channel, saturation de ressources et
administrateur hote compromis. Pixels bruts et absence de vNIC ne les annulent pas.
La fin de support de Windows 11 Pro 23H2 est un prerequis de securite a traiter
par l'operateur avant une certification ; elle ne declenche ici aucune
installation, changement de version ou demande d'elevation.

Validations 6.5-F : seulement lecture systeme/sources officielles, controle de
syntaxe des commandes sans execution, hash/structure/liens des livrables.
Pas de nouveaux tests backend ou VM annonces. Le JSON de la mission Verified v9
reste au SHA-256
`6563AF1EDEF8180CABD5CBE6CFC1CCFC09AF936B6BB9038D66279256CB5F29FF`.
Pas de creation VM, installation, elevation, Docker, Ollama ou autorisation
Preview. **HTTP 503 conserve. MVP 6.5 non termine.**

## Sources officielles consultees

- [Debian 13.7.0 : sommes officielles](https://cdimage.debian.org/debian-cd/13.7.0/amd64/iso-cd/SHA256SUMS)
- [Debian : verification et cles CD](https://www.debian.org/CD/verify)
- [Chromium amd64 : version et SHA-256](https://packages.debian.org/trixie/amd64/chromium/download)
- [Weston amd64 : version et SHA-256](https://packages.debian.org/trixie/amd64/weston/download)
- [Noyau signe amd64 : version et SHA-256](https://packages.debian.org/trixie/amd64/linux-image-6.12.117%2Bdeb13-amd64/download)
- [Snapshot Debian fige : Release](https://snapshot.debian.org/archive/debian/20261114T000000Z/dists/trixie/Release)
- [Snapshot Security fige : Release](https://snapshot.debian.org/archive/debian-security/20261114T000000Z/dists/trixie-security/Release)
- [Weston : headless et renderer logiciel](https://wayland.pages.freedesktop.org/weston/toc/running-weston.html)
- [Microsoft : HvSocket, mapping Linux et inscription administrateur](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/make-integration-service)
- [Microsoft : services d'integration et copie de fichiers](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/integration-services)
- [Microsoft : New-VM et allocation d'identite](https://learn.microsoft.com/en-us/powershell/module/hyper-v/new-vm?view=windowsserver2025-ps)
- [Microsoft : cycle de support Windows 11 Home/Pro](https://learn.microsoft.com/en-us/lifecycle/products/windows-11-home-and-pro)
