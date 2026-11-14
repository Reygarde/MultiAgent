# MVP 6.5-G - Preparation de la levee des prerequis

Date : 2026-11-14. **Lecture seule du poste ; scripts prepares, non construits.**
HTTP 503 reste actif. Aucun changement de groupe, elevation, installation,
service HvSocket, VM, execution Docker/Ollama ou autorisation Preview.
Le [plan 6.5-F](preview-hyperv-mvp65f.plan.json) reste inchange et non executable ;
les nouvelles observations ne reecrivent pas son empreinte historique.

## 1. Etat Windows et materiel confirme

| Controle | Resultat | Conclusion |
|---|---|---|
| Edition / version | Windows 11 Professional 23H2, build 22631.6199, x64 | Version hors support depuis novembre 2025 |
| Firmware / disque | UEFI (BiosFirmwareType=2), GPT, disque C: ~476,94 Gio | Base adaptee ; pas une certification de mise a niveau |
| Processeur | AMD Ryzen 7 7840HS, 8 coeurs / 16 threads, 64 bits | Modele present dans la liste AMD Windows 11 officielle ; validation OEM/version cible encore requise |
| Virtualisation firmware | Active, hyperviseur detecte | Ne pas desactiver l'hyperviseur pour interpreter WMI |
| SLAT WMI | False sous hyperviseur actif | Valeur non concluante dans cet etat ; ne pas annoncer un echec materiel |
| RAM physique visible | 15927668 KiB, environ 15,19 Gio | Capacite totale suffisante pour le petit prototype, disponibilite insuffisante |
| Disque libre C: | Environ 100,42 Gio | > 15 Gio du prototype ; ne garantit pas la place temporaire demandee par Windows Setup |
| Secure Boot hote | Lecture refusee : privileges insuffisants | Etat inconnu, pas desactive presume |
| TPM | Win32_Tpm : acces refuse | Presence, activation et version 2.0 non confirmees |
| BitLocker | Lecture du statut refusee | Chiffrement et protection inconnus ; aucune cle lue ou exportee |

Les autres exigences Windows (TPM 2.0, Secure Boot capable, pilote graphique
DirectX/WDDM, compatibilite OEM/firmware, blocages de compatibilite) ne sont pas
certifiees par le seul fait que Windows 11 est deja installe.
La verification finale doit employer Windows Update/diagnostic Microsoft et
l'assistance OEM, sans bypass TPM/CPU/Secure Boot ni outil tiers non approuve.

### Mise a niveau proposee, NON effectuee

1. **Sauvegarder et verifier la restauration avant toute modification.**
   - Copie externe de l'ensemble du depot, y compris fichiers modifies/non
     suivis ; un clone Git seul ne contient pas tout le travail actuel.
   - Donnees Studio : magasin effectivement configure, missions, workspaces,
     revisions, historiques et manifestes, dont la v9. La configuration API
     de developpement utilise `..\StudioData` ; le magasin Desktop est distinct.
     Identifier le repertoire effectif dans chaque installation, ne pas
     supposer que le magasin de developpement couvre le Desktop.
   - Fichiers personnels utiles et configurations locales non suivies,
     protegees comme des secrets ; ne pas les ajouter au depot ou au rapport.
   - Sauvegarde systeme/restauration sur support externe et media de recuperation
     Microsoft approuve. Une restauration systeme ou Windows.old n'est pas une
     sauvegarde de tous les fichiers.
   - Verifier hors de ce chat la disponibilite de la cle de recuperation
     BitLocker et des moyens de connexion/recuperation. Ne pas afficher la cle,
     suspendre le chiffrement, effacer le TPM ou changer le firmware ici.
   - Verifier une restauration de copie avant d'accepter un redemarrage.
2. Planifier avec l'operateur une interruption distincte : enregistrer le
   travail et organiser manuellement la coherence des magasins actifs et
   applications partagees. Aucun arret ou copie a chaud supposee coherente ici.
3. Examiner l'offre Windows Update et les safeguard holds, le support OEM et
   les problemes connus de la version cible. Microsoft indique actuellement
   **26H2** pour les appareils eligibles ; **25H2** reste une alternative
   supportee si c'est celle proposee/validee pour ce materiel. Ne pas viser
   24H2 comme sortie durable du hors-support a cette date.
4. Depuis 23H2, laisser Windows determiner le chemin de mise a niveau complet :
   ne pas appliquer aveuglement un enablement package prevu pour une autre base.
   Aucun changement de registre pour forcer la version, aucun contournement
   d'un blocage de compatibilite. Ne pas telecharger ISO, assistant ou pilote
   dans cette etape.
5. Apres autorisation distincte et sauvegardes, l'operateur pourra effectuer
   la mise a niveau via le canal Microsoft approuve, sur secteur, avec assez
   d'espace pour Setup et une fenetre de redemarrage. Aucun clic ou UAC ici.
6. Apres upgrade : verifier version/support, correctifs Windows/Hyper-V,
   firmware/pilotes OEM, TPM, Secure Boot, BitLocker, services et applications.
   Verifier la restauration des magasins et les empreintes v9, puis refaire
   les controles Hyper-V/RAM. Ne pas supposer que les droits, templates Secure
   Boot ou identifiants materiels ont conserve exactement le meme etat.

Il n'y a pas de garantie de retour arriere basee sur une duree Windows.old :
elle depend du systeme et du nettoyage. Le plan de recuperation repose sur
la sauvegarde independante, pas sur un rollback suppose disponible.

## 2. Droits : appartenance configuree vs jeton effectif

Controles SID, sans collecter les autres membres des groupes dans le rapport :

| Groupe | Appartenance directe configuree | Droit effectif dans le jeton courant |
|---|---|---|
| Administrateurs locaux, S-1-5-32-544 | **Oui** | **Non** |
| Hyper-V Administrators, S-1-5-32-578 | Non | Non |

Le compte est deja administrateur local au sens de la configuration SAM, mais
le contexte actuel est restreint/filtre. Ce n'est pas la preuve que l'API
Hyper-V serait utilisable sans elevation. Le refus Get-VMHost de 6.5-F reste
coherent ; aucun reessai privilegie ni demande UAC n'a ete fait.
Le dossier F parlait d'absence de droits **actifs**, pas d'une absence certaine
d'appartenance configuree.

| Operation future | Privilege / procedure minimale proposee |
|---|---|
| Lire RAM/processus, verifier hashes et preparer les fichiers | Contexte standard actuel |
| Lire TPM, Secure Boot, BitLocker et inventaire Hyper-V refuse | Diagnostic manuel par un operateur autorise, apres accord specifique |
| Creer/configurer/start/stop/supprimer la seule VM de laboratoire | Operateur Hyper-V deja habilite, ou session administrateur ouverte manuellement pour la campagne precise |
| Deux cles GuestCommunicationServices HKLM et nettoyage | Consentement administrateur distinct, deux GUID exacts du plan F, refus de collision, journalisation et suppression des seules creations |
| Repertoire ProgramData et ACL de session | Provisionnement limite et examine par l'operateur ; pas d'ACL generale vers StudioData |
| Construction Linux | Root dans un constructeur Linux jetable dedie, sans identifiants/volumes Windows ; pas d'elevation automatique de ce Windows |

**Pas d'ajout de groupe recommande par defaut** : Hyper-V Administrators
accorde une gestion etendue des VM du poste, pas seulement la Preview.
Ne pas elever tout Studio, desactiver UAC, creer une tache/service permanent,
modifier les ACL globales, ni transmettre un jeton admin au parseur du broker.
La future campagne doit lister les commandes/ressources autorisees et les
operations de retrait. Une console privilegiee manuelle est une operation
ulterieure, pas une autorisation implicite dans ce dossier.

## 3. Ressources et optimisations manuelles

Mesure du **2026-11-14 a 05:31:36 +01:00** :
1181200 KiB disponibles, soit **1,126 Gio**. Seuil requis : 4718592 KiB
(4,5 Gio). Il manque environ **3,37 Gio**, sans aucune reservation effectuee.

Agregation voisine des processus, sans titres de fenetre ni lignes de commande :

| Famille | Processus | Working set somme, Mio | Memoire privee, Mio |
|---|---|---|---|
| VS Code (`Code`) | 21 | 3484,2 | 2795,1 |
| Chrome | 20 | 3454,1 | 2873,9 |
| svchost | 85 | 1171,5 | 403,3 |
| Creative Cloud UI Helper | 4 | 733,1 | 702,3 |
| copilot-runtime | 1 | 496,4 | 614,0 |
| NVIDIA Overlay | 5 | 378,9 | 298,1 |
| Code ServiceHost | 2 | 362,4 | 190,1 |
| Defender MsMpEng | 1 | 328,6 | 378,3 |

La somme des working sets double-compte des pages partagees ; memoire privee
et RAM liberable ne sont pas interchangeables. Ces chiffres ne promettent pas
qu'une fermeture rendra une quantite precise de RAM.

Propositions **a faire uniquement par l'utilisateur** : enregistrer puis fermer
ses onglets/fenetres Chrome inutiles ; fermer ses workspaces VS Code non utilises,
sans interrompre le travail/agent courant ; quitter Creative Cloud ou l'overlay
NVIDIA via leurs interfaces si inutiles et si aucune synchronisation n'est
en cours. Ne pas terminer des processus par nom, les applications d'un autre
utilisateur, svchost ou Defender ; ne pas desactiver l'antivirus ou le swap.
Aucune limite Ollama/Docker n'est lue via leur CLI ni modifiee.
Recontroler la RAM immediatement avant allocation ET boot ; ne rien creer
sous 4,5 Gio meme si une ancienne mesure satisfaisait le seuil.

## 4. Scripts de construction prepares

Dossier separe, sans lien DI, API ou packaging :

- [build-offline.sh](../tools/HyperVApplianceBuild/build-offline.sh) :
  recette Linux amd64 explicite, aucun sudo, aucun WSL/reseau/telechargement
  de secours. Sans flag `--build-reviewed-offline` et constructeur root
  approuve, refus. **Jamais lance pour construire dans cette etape.**
- [inputs.mjs](../tools/HyperVApplianceBuild/inputs.mjs) :
  SHA du lock approuve separement, verifications des fichiers, absence d'entree
  non inventoriee/lien symbolique, chemins bornes, outils/versions epingles,
  composants obligatoires, fermeture installee exacte et manifeste SHA-256.
- [inputs.example.json](../tools/HyperVApplianceBuild/inputs.example.json) :
  **modele volontairement incomplet**, hashes/versions inconnus null, pas un
  lock approuve ; refuse avant construction.
- [inputs.test.mjs](../tools/HyperVApplianceBuild/inputs.test.mjs) :
  tests locaux sans binaire Linux, paquet Debian, construction ou VM.

### Preparation des entrees avant toute construction

Le bundle doit etre dans un dossier dedie immutable, sans magasin Studio ni
secret, distinct du lock et de l'output. Chaque fichier porte chemin relatif,
SHA-256, provenance officielle et version. Tout fichier non liste, lien
symbolique ou chemin sortant est refuse. Le SHA-256 du lock complet doit
etre examine et transmis **separement**, pas calcule puis accepte aveuglement
depuis le meme bundle.

Structure a produire plus tard, sans acquisition ici :

```text
bundle/
  trust/     trousseau Debian et certificats PE independamment verifies
  debian/    miroir local signe trixie, index et tous les .deb requis
  security/  miroir local signe trixie-security, index et .deb requis
  payload/   agent, module Weston, broker/viewer/superviseur versionnes
  overlay/   fichiers runtime systemd/hardening examines et empreintes
```

Le lock exige tous les paquets transitifs et versions, notamment Chromium,
chromium-common/sandbox identiques, Weston/libweston-14, noyau signe, shim,
GRUB signe, polices locales et base systeme. Les versions candidates 6.5-F
restent des **candidates** tant que les index signes snapshot ne les confirment
pas. La page statique F doit etre incluse dans l'overlay avec son SHA approuve.
Les cinq composants propres n'existent toujours pas : aucun placeholder
executable ou faux Ready ne les remplace.

Outils constructeur requis et a epingler par version, chemin et SHA :
Node, Bash, outils coreutils utilises, util-linux (`unshare`, `mount`, `sfdisk`),
gpgv, mmdebstrap, dpkg-query/chroot, e2fsprogs, dosfstools, mtools, qemu-img,
sbverify/sbsigntool. Leurs propres dependances et la provenance du constructeur
doivent etre verrouillees dans une image de build approuvee. Le premier Node/
Bash de verification doit deja etre de confiance : l'auto-hash n'est pas une
chaine de confiance initiale.

Sur Windows, seul Node 20.14 et Git Bash natif ont ete utilises pour tests/
syntaxe. Les `bash.exe`/`wsl.exe` Windows ne prouvent pas la presence d'un
constructeur Linux ; ils n'ont pas ete lances. Pas de gpgv, mmdebstrap,
qemu-img ou openssl trouves sur ce PATH. Aucun paquet installe pour les obtenir.
Le constructeur Linux futur reste a fournir, avec budget distinct :
amd64, root isole, environ 4 Gio RAM et **30 Gio de disque** pour rootfs,
partitions intermediaires, raw et VHDX ; ce n'est pas le budget VM de 2 Gio/180 s.

### Recette fournie et bornes de sa validation

Le script verifie les octets et outils, entre dans des namespaces reseau/mount/
PID neufs via un bloc fixe, rend le bundle read-only, verifie les deux InRelease
avec gpgv, puis utilise **uniquement** les deux depots file: avec APT authentifie
et Valid-Until actif. Aucun apt-get d'outillage constructeur ni URL externe.
Le namespace reseau bloque aussi une URL externe accidentelle dans un index.
Le lock doit etre a jour : apres expiration, nouvelle collecte/approbation,
pas de desactivation de Valid-Until.

mmdebstrap produit une racine selon le lock ; dpkg-query compare la fermeture
installee exacte (version/architecture/etat), pas seulement les paquets demandes.
L'overlay examine apporte systemd, utilisateur non root, sandbox, tmpfs/profil,
AF_VSOCK refuse au navigateur/enfants, absence de SSH/RDP/KVP/fcopy/automount,
watchdog et hardening. **Ces fichiers runtime et binaires restent manquants** :
le modele actuel refuse de passer, et la recette n'est pas une appliance livree.

L'image est assemblee sans boot ni montage de disque physique : GPT fixe,
ESP FAT, ext4 depuis le rootfs, copie de shim/GRUB signes, kernel/initrd,
conversion raw -> VHDX Fixed 6 Gio. Aucune commande grub-install/efibootmgr
ne touche le NVRAM ou le disque de boot du constructeur.
Un echec conserve les fichiers uniquement dans le nouveau dossier de build
pour diagnostic ; aucun rm -rf automatique. Les namespaces disparaissent a
la sortie. Tout resultat partiel reste non livrable.

SourceDateEpoch, UUID, sources, versions et commandes sont fixes ; le script
capture lock, inventaire installe, info VHDX et hashes des artefacts/scripts.
Il n'emploie pas d'heure courante dans le manifeste.
**Reproductibilite bit a bit non demontree** : timestamps de maintainer scripts,
initramfs/ext4, identifiants internes VHDX et sortie qemu-img peuvent differer.
Deux builds propres avec comparaison de contenu et explication des differences
seront necessaires ; ne pas annoncer que SOURCE_DATE_EPOCH les elimine tous.

Le manifeste genere porte `executionEligible: false`, n'est pas signe
automatiquement et ne valide pas le boot. Une signature externe de release,
avec cle dediee hors Studio et provenance des inputs, devra ensuite etre fournie
et verifiee. Aucun fichier du plan F n'est rempli par ces scripts.

Verification sans construction, depuis la racine du depot :

```powershell
node --test .\tools\HyperVApplianceBuild\inputs.test.mjs
```

Le check readonly d'un futur bundle complet est `node inputs.mjs verify BUNDLE
LOCK SHA256_LOCK_EXAMINE`. Le modele actuel doit echouer, pas servir de valeur
par defaut. La seule entree future de construction est le flag explicite
`--build-reviewed-offline` suivi de BUNDLE, LOCK, SHA et NOUVEAU_DOSSIER_ABSOLU,
dans le constructeur Linux approuve ; aucune de ces commandes de build
n'est executee ici.

## 5. Secure Boot : compatible en principe, non certifie sur ce poste

Chaine prevue : firmware Hyper-V Gen2/template
`MicrosoftUEFICertificateAuthority` -> shim Microsoft signe -> GRUB Debian
signe -> noyau Debian signe. Les fichiers EFI sont copies tels quels ; aucun
GRUB genere non signe, DKMS, MOK additionnelle ou desactivation de Secure Boot.
Les certificats du shim, de GRUB et du kernel sont des entrees distinctes,
verifiees hors bande, puis epinglees dans le lock.
sbverify --cert verifie leur signature PE ; APT/gpgv verifient la provenance
des paquets. Ni un hash seul ni `sbverify --list` ne prouvent la confiance.

La transition **UEFI CA 2011 -> 2023**, les revocations dbx/SBAT et le template
Hyper-V installe doivent etre examines apres mise a niveau de Windows.
L'expiration des CA 2011 ne signifie pas automatiquement que tout ancien boot
est casse ; inversement le nom du template n'assure pas qu'il approuve chaque
shim recent. Ne pas modifier les cles UEFI pour forcer un resultat.
Un test de boot Secure Boot reel sera necessaire apres consentement.

Secure Boot ne scelle pas automatiquement l'initramfs, les fichiers de
configuration, le rootfs ou le module Weston utilisateur. Hash de l'image
avant copie, manifeste signe, racine read-only et frontiere hyperviseur restent
necessaires ; aucune affirmation de UKI/verity mesuree n'est faite.

## 6. Validation effectuee et gates de premiere autorisation

Validation locale : **7 tests helpers reussis**, analyse syntaxique Bash du
script externe et du corps de namespace via Git Bash natif `-n`, sans execution
de leur contenu. Aucun outil constructeur Linux ou logiciel invite execute.
Les tests ne prouvent ni le boot, ni l'isolation des canaux, ni le nettoyage VM.

Avant de demander un **premier essai statique**, fournir :

1. Windows supporte et corrige, compatibilite OEM, TPM/Secure Boot/recuperation
   verifies, sauvegardes restaurees en essai, fenetre operateur approuvee.
2. Accord distinct pour diagnostic/gestion privilegiee et les deux inscriptions
   HvSocket temporaires ; droits effectifs verifies. Pas d'ajout de groupe
   ou delegation permanente par defaut.
3. >= 4,5 Gio de RAM libre et >= 15 Gio disque immediatement avant allocation
   et boot, une seule VM, aucune application tierce terminee par automatisation.
4. Construction **ulterieurement autorisee** dans son environnement dedie,
   verrou complet et signatures valides ; VHDX et binaires finaux reellement
   produits, examines, hashes, signes et avec provenance/SBOM/licences.
5. Broker, agent, module, viewer et superviseur implements/testes hors parcours
   Preview. Borne de 180 s, echec sans Ready, crash et recuperation definis.
   Un Job Object ne suffit pas a arreter Hyper-V.
6. Inventaire hote HvSocket/VMBus complete, allowlist et risque du premier boot
   explicitement examines. La preuve invite de refus des autres canaux fait
   partie de l'essai, pas d'un resultat invente comme prerequis deja satisfait.
7. **Nouvelle revision canonique** contenant toutes les vraies empreintes,
   tailles, politique, commandes, VMID alloue/journalise selon le contrat,
   tests/compteurs, cleanup et risques. F n'est pas une autorisation reutilisable.
8. Consentement humain explicite pour creation/start/stop/destruction du seul
   laboratoire statique ; distinct d'une autorisation Preview ou de validation
   Docker. Pour la premiere campagne, aucun projet genere, Ollama ou Docker.

HTTP 503 reste ferme avant integration qualifiee ; aucun de ces gates ne
represente une demande d'autorisation creee ici. **MVP 6.5 non termine.**

## Sources

- [Microsoft : cycle Windows Home/Pro](https://learn.microsoft.com/en-us/lifecycle/products/windows-11-home-and-pro)
- [Microsoft : exigences Windows 11](https://www.microsoft.com/en-us/windows/windows-11-specifications)
- [Microsoft : processeurs AMD compatibles](https://learn.microsoft.com/en-us/windows-hardware/design/minimum/supported/windows-11-supported-amd-processors)
- [Microsoft : etat et problemes connus 26H2](https://learn.microsoft.com/en-us/windows/release-health/status-windows-11-26h2)
- [Microsoft : etat 25H2 et offre 26H2](https://learn.microsoft.com/en-us/windows/release-health/status-windows-11-25h2)
- [Microsoft : prerequis Hyper-V](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/host-hardware-requirements)
- [Microsoft : Secure Boot Gen2 Linux](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/generation-2-virtual-machine-security-features)
- [Debian : Secure Boot](https://wiki.debian.org/SecureBoot)
- [Debian : transition CA 2011/2023](https://wiki.debian.org/SecureBoot/CAChanges)
- [Debian : mmdebstrap, depots locaux et modes](https://manpages.debian.org/trixie/mmdebstrap/mmdebstrap.1.en.html)
