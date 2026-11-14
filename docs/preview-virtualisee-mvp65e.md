# MVP 6.5-E - Architecture Preview virtualisee

Date : 2026-11-14. Statut : proposition technique, **pas une implementation
validee ni une autorisation d'execution**. HTTP 503 reste actif.

## 1. Decision et observations locales

Recommandation pour ce poste : **appliance Linux jetable sous Hyper-V**,
sans carte reseau virtuelle, contenant le navigateur et, ulterieurement,
l'application generee. L'affichage sort uniquement sous forme de pixels et les
entrees reviennent par un protocole borne sur Hyper-V sockets.
C'est une VM dediee de type A, reduite en appliance de type C, pas une nouvelle
frontiere creee par le mot « legere ». Pas de Docker, WSLg ou navigateur Windows
pour afficher le contenu genere.

Le prototype AppContainer/WFP [6.5-D](../tools/WindowsConfinementPrototype/README.md)
n'est pas integre. Son blocage d'un processus Node ne prouve pas celui d'Edge.
Le test Edge/Playwright avec fuite WebRTC n'est ni masque ni remplace par des
tests de codec ou une desactivation de WebRTC.

Inspection locale **en lecture seule**, sans enumerer, modifier ou lancer des VM :

| Observation | Resultat | Interpretation |
|---|---|---|
| Edition / version | Windows 11 Professional, 23H2, build 22631 | Candidat Hyper-V, pas certification des correctifs de securite |
| Hyperviseur detecte | Oui | Pas une preuve de configuration adaptee a Preview |
| Services `vmms`, `vmcompute` | En cours | Composants presents ; droits de gestion non demontres |
| Module PowerShell Hyper-V | Present | Aucun appel de creation/demarrage effectue |
| `WinHvPlatform.dll` | Presente | Ne certifie ni WHPX ni une VM QEMU securisee |
| RAM physique visible | Environ 15,2 Gio | Coherent avec une machine vendue avec 16 Go |
| RAM disponible a l'inspection | Environ 1,3 Gio | **Insuffisant pour autoriser le prototype** |
| Espace libre sur C: | Environ 100,5 Gio | Compatible avec le budget propose, a recontroler |
| Administrateur actif | Non | Aucun prompt UAC ni changement de groupe effectue |

WMI annonce ici `SLAT=false` alors que l'hyperviseur est deja actif. Ne pas
conclure que le processeur est incompatible sur cette seule valeur : Microsoft
indique que les exigences Hyper-V ne sont plus exposees normalement lorsqu'un
hyperviseur est detecte [S2]. La verification finale doit employer les controles
de compatibilite appropries, sans changer BIOS, edition ou composants.
Les ressources disponibles sont une photographie, pas une reservation.

**Arret avant prototype reel :** verifier navigateur, rendu et reseau exige
de demarrer une VM. Cette execution n'est pas autorisee. Aucun invite, aucune
image systeme, aucun Hyper-V socket, aucune capture reseau et aucun navigateur
de prototype n'ont ete lances pendant cette etape.

## 2. Comparatif des solutions

Les chiffres sont des **budgets de conception a mesurer**, pas des benchmarks.
Une seule session est admise. Une VM peut proteger Windows sans isoler entre
eux les processus invites : ce second niveau exige ses propres protections.

### A. VM Hyper-V dediee

| Critere | Evaluation |
|---|---|
| Windows Home / Pro | Role Hyper-V non pris en charge sur Home ; Pro/Enterprise compatibles sous conditions materielles [S1, S2] |
| Privileges / composants | Hyper-V, virtualisation firmware, noyau Linux compatible ; activation et enregistrement HvSocket privilegies ; delegation de gestion a verifier |
| 16 Go de RAM | Faisable pour une seule petite appliance ; budget 2 Gio statique ou 4 Gio full-stack, plus surcout hote et marge Windows |
| CPU / GPU | 2 vCPU au depart ; rendu logiciel, aucun GPU partage ou passthrough ; consommation a mesurer |
| Demarrage / arret | Cible demarrage a froid <= 90 s ; arret gracieux puis extinction forcee ; jamais de reprise d'une VM generee precedente |
| Reseau | Zero vNIC, aucun switch, NAT, bridge ou route hote ; loopback Linux distinct du loopback Windows |
| Fichiers Windows | Pas de SMB, DrvFs, 9p, virtiofs, disque physique, partage de repertoire ou copie generique |
| Chromium / enfants | Tous restent dans la VM, meme en cas de compromission du navigateur ; sandbox Chromium conserve, cgroups et UID non root |
| Affichage / entrees | Agent invite et pixels via HvSocket ; clavier/souris virtuels limites au compositor invite |
| Crash | Watchdog invite + controleur hote ; extinction, inventaire Hyper-V et destruction du disque differentiel |
| Tauri | Aucun HTML invite dans WebView ; fenetre de rendu de confiance et IPC local borne a developper |
| Maintenance | Linux, Chromium, hyperviseur et broker a maintenir ; image signee et requalification lors des mises a jour |

Avantage : separation explicite du noyau Windows, du reseau et du stockage.
Limites : gestion privilegiee, taille d'image, maintien des correctifs et surface
VMBus/broker. La VM n'est pas une garantie contre une faille d'hyperviseur.

### B. WSL2 renforce

| Critere | Evaluation |
|---|---|
| Windows Home / Pro | WSL2 disponible sur les editions Desktop dont Home [S3] ; ne pas confondre Virtual Machine Platform et role Hyper-V |
| Privileges / composants | WSL2/VMP et distribution ; activation initiale potentiellement privilegiee ; rien installer dans cette etape |
| 16 Go / CPU | Cible 2-4 Gio, 2 vCPU ; options usuelles de RAM/reseau dans `.wslconfig` communes aux distributions [S4] |
| Demarrage / arret | Rapide dans de nombreux usages, non mesure ici ; `wsl --shutdown` affecte les autres distributions, donc interdit comme nettoyage de session |
| Reseau | NAT par defaut ; mirrored permet d'atteindre Windows via 127.0.0.1 ; DNS tunneling peut utiliser un canal non IP [S5] |
| Renforcement possible | `networkingMode=none`, pas de tunneling DNS, autoProxy ou forwarding ; mais portee globale a verifier et risque d'affecter Docker/autres distributions |
| Fichiers / processus Windows | Automount/interop actifs par defaut ; `automount=false` n'interdit pas a lui seul tous les montages ; desactiver interop/PATH ne constitue pas une frontiere externe [S4] |
| Chromium / enfants | Noyau Linux dans une VM geree ; distributions sous forme de conteneurs dans cette VM [S6] ; pas une appliance noyau dediee par session |
| Graphique / entrees | WSLg facilite volontairement integration, clipboard et GPU [S7] ; a ne pas utiliser pour ce besoin |
| Crash / nettoyage | Terminer une distro et retirer son disque ne certifie pas la disparition de tous les canaux WSL ou des ressources communes |
| Tauri / maintenance | Lancement simple mais isolation complexe, sensible aux mises a jour et configurations globales ; distribution de Preview seule insuffisante |

**Non retenu.** On peut reduire des integrations sans prouver que le poste
Windows et les autres distributions sont hors d'atteinte. Ne pas modifier
`.wslconfig`, WSLg ou le reseau Docker existant pour ce prototype.

### C. VM legere dediee et IPC strict

Ce choix definit une appliance, pas un hyperviseur. Deux realisations :

- **C1 : Hyper-V minimal**, retenue sur le poste Pro. Meme frontiere que A,
  moins de logiciels et de peripheriques. HvSocket offre un canal sans TCP/IP,
  avec identification de VM [S8]. Aucun besoin de virtualisation imbriquee.
- **C2 : QEMU + WHPX**, alternative eventuelle pour une distribution plus
  portable. QEMU prend en charge WHPX sur Windows [S9]. La disponibilite et les
  privileges de WHP sur l'edition Home cible restent a verifier par un prototype
  separe ; la presence de WSL/VMP ne suffit pas a la garantir.
  Ne pas promettre une prise en charge Home deja validee.

| Critere C2 | Evaluation |
|---|---|
| Composants | Binaire QEMU, firmware, WHP et noyau/image verifies ; aucun pilote tiers a installer implicitement |
| CPU/RAM | Meme ordre de grandeur invite que C1 ; memoire du modele de peripheriques a borner separement, pas seulement la RAM invitee |
| Reseau/fichiers | Aucun peripherique reseau ni backend reseau, partage ou passthrough ; attention aux peripheriques/defaults implicites |
| IPC | Virtio-serial vers pipe local a ACL restrictive, si supporte par le build retenu ; aucune exposition TCP de VNC/SPICE/QMP |
| Graphique/entrees | Meme agent pixels/entrees ; QMP strictement reserve au controleur de confiance, jamais au guest ou a la page |
| Arret/crash | Job Object et extinction VM ; disques overlays par session, inventaire et verifications comme C1 |
| Packaging | QEMU/firmware/licences/SBOM/correctifs supplementaires ; aucune dependance ne serait telechargee silencieusement |
| Securite | Grand modele de peripheriques a reduire et backend/accelerateur a qualifier [S10] |

**Pas de repli silencieux vers QEMU TCG** : la politique de securite QEMU
exclut explicitement les garanties d'isolation pour son usage d'emulation TCG
[S10]. « Cela fonctionne sur Home » n'est pas un critere suffisant.
Firecracker/KVM ne constitue pas non plus une solution Windows native sans
couche Linux/virtualisation supplementaire a qualifier.

Conclusion : C1 reduit le nombre de nouveaux composants sur ce poste Pro.
Home restera « non disponible » tant qu'une variante C2 n'aura pas ete qualifiee.

## 3. Frontiere de confiance et acces autorises

```text
HOTE WINDOWS - DE CONFIANCE
Studio / StudioData / secrets
  | API Desktop authentifiee, sources approuvees capturees
PreviewSessionManager
  | plan / autorisation consommee
Controleur de session -> gestion Hyper-V strictement bornee
  | handle de VM exact / droits minimaux
Broker de donnees isole -> rendu pixels dans une fenetre de confiance
  | deux flux HvSocket, VMID exact, pas d'IP, pas de commande generique
---------------- FRONTIERE HYPERVISEUR ----------------
INVITE LINUX - A TRAITER COMME NON FIABLE COTE HOTE
Agent fixe / compositor virtuel
  | entrees et pixels ; services internes controles
Chromium sans privilege <-> frontend/API generee <-> SQLite temporaire
  | namespace applicatif : loopback uniquement
aucune carte reseau, aucun montage Windows, aucun socket Docker
```

Le controleur et le broker sont distincts : le processus qui parse des donnees
invites ne recoit ni droits Hyper-V, ni token Studio, ni handle vers StudioData.
L'IPC broker-controleur ne permet que des evenements enumeres (Ready, Fault,
Disconnected) et des demandes provenant de Studio. Il ne transporte jamais une
ligne de commande ou un chemin fourni par l'invite.
La compromission d'un agent invite est dans le modele de menace : son
authentification n'autorise aucune operation hote additionnelle.

| Acteur | Seuls acces autorises |
|---|---|
| Studio | Magasin existant ; demande preparation/approbation/arret selon les endpoints actuels |
| Controleur | Identifiant VM propre a la session, image immuable approuvee, overlay et journal d'appartenance ; pas de VM tierce |
| Broker hote | Deux handles de flux, buffers bornes, IPC local avec ACL et identite de processus ; aucun acces disque/reseau genere par le guest |
| Invite | Noyau/image de base verifies, overlay propre a la session, memoire, deux canaux IPC fixes ; aucun secret durable |
| Agent/compositor | Capture du seul ecran virtuel et entrees dans ce seul compositor ; pas de controle des fenetres/processus Windows |
| Navigateur | Profil vide, frontend/API loopback de l'invite ; aucun fichier Windows, commande Tauri ou extension personnelle |
| Application | Sources approuvees copies, SQLite/work temporaires ; UID/cgroups/namespace dedies sans acces aux canaux du broker |

### Configuration externe obligatoire

- **Aucune vNIC**, meme deconnectee : inventaire Hyper-V avant/apres demarrage,
  aucune carte ajoutee par une automatisation. Pas de switch « Default »,
  prive/interne/externe, NAT, TAP, bridge ou port publie.
- Aucun GPU passthrough/partitionne, audio, microphone, camera, USB, presse-papiers,
  redirection de lecteurs, glisser-deposer, RDP/Enhanced Session, WSLg ou SSH.
- Base de confiance sans donnees personnelles ; racine invitee read-only,
  profil/work/tmpfs jetables, disque differentiel borne si necessaire.
  Aucun disque physique Windows ou stockage Studio attache.
- Desactiver pour cette VM les integrations copie de fichiers, KVP et sauvegarde
  non necessaires ; garder uniquement les integrations necessaires et auditees,
  dont hv_sock et les pilotes disque/VMBus. Arret et heartbeat doivent etre
  inventories explicitement [S11], jamais supposes inoffensifs.
- L'hote ne monte **jamais** l'overlay ecrit par un invite non fiable pour lire
  ses journaux ou fichiers. Il ferme ses handles et detruit l'overlay.
- Construire les sources, plus tard, sans restauration reseau : dependances
  offline dans l'image versionnee ; aucune installation depuis un LLM.
  Copier les octets approuves par un support read-only construit par l'hote,
  jamais partager un repertoire Windows. Aucun ISO source pour le prototype.

### Limites a prouver, meme sans carte reseau

L'absence de vNIC supprime le chemin IP vers Windows, pas tous les chemins
VMBus. Les services HvSocket/integration presents sur l'hote sont une surface
distincte : inventorier les services accessibles depuis cette VM et refuser
tout autre endpoint. Le broker se connectera au **VMID exact**, jamais a un
wildcard acceptant toutes les partitions. Il faut verifier les ACL/possibilites
de restriction par VM et l'echec d'acces aux autres services.
Si cette restriction n'est pas demontrable, **pas d'activation** ; evaluer un
transport peripherique serie/virtio-serial strict ou C2 dans une etape distincte,
pas comme contournement automatique.

Dans Linux : agent hors du namespace applicatif, sockets Unix a ACL, UID
separes, AF_VSOCK refuse aux processus application/navigateur, pas de montage,
pas de capacites administratives et sandbox Chromium active. Les enfants
heritent des contraintes de cgroups/namespace ; le root invite reste considere
hostile pour l'analyse du broker et de l'hyperviseur.
Les regles Linux sont une defense supplementaire : leur suppression dans un
test d'invite compromis ne doit pas creer une interface Windows inexistante.

Le rendu hote n'est pas une preuve d'authenticite du contenu : une page peut
dessiner une fausse interface Studio. Bandeau de confiance hors pixels, pas de
gestionnaire de mots de passe, pas de clipboard ni saisie de secrets Studio.
Les failles d'hyperviseur, side-channels et compromission de l'hote ne sont pas
« prouves impossibles » par des tests de paquets a zero.

## 4. Broker minimal propose (non implemente)

### 4.1 Transport et ouverture

Deux connexions AF_HYPERV cote Windows / AF_VSOCK cote Linux : **controle**
et **images**, pour qu'une image lente ne bloque pas Stop.
Le noyau invite doit offrir `CONFIG_VSOCKET` et `CONFIG_HYPERV_VSOCKETS` [S8].
Le mapping du service Linux utilise un port et le GUID-template documente :
pas un GUID aleatoire interprete comme un port sans conversion.
Les deux ServiceId, le mapping et la version du protocole appartiennent au plan.

**Prerequis privilegie reel :** Microsoft documente un enregistrement sous
`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices`
pour ces services [S8]. Rien n'a ete enregistre ici. Deux entrees appartenant
exclusivement au prototype devront etre autorisees, sauvegardees si elles
preexistent (auquel cas refuser de les reutiliser), puis retirees apres l'essai.
Pas de reutilisation d'un service Windows existant pour eviter cette autorisation.
L'activation future permanente de ce composant serait un consentement distinct.

L'hote choisit VMID et SessionId apres l'autorisation. Chaque flux est lie a ce
couple et a son role. Un challenge aleatoire de 32 octets par session/flux,
transmis dans la configuration de boot read-only propre a l'essai, est retourne
par le Hello ; aucune cle/API Studio n'entre dans l'invite.
Le challenge empeche la confusion/relecture d'une ancienne session ; il **ne
rend pas digne de confiance un invite compromis**, qui peut le lire.
Pas de token dans URL, logs ou stockage JavaScript. Le controleur n'accepte
pas de parametres reseau arbitraires du guest.

### 4.2 Cadrage borne

Proposition de contrat binaire v1 a implementer et tester avant VM :

- En-tete fixe **48 octets** : magic 4, version 2, type 2, SessionId 16
  (octets UUID RFC 4122), sequence 8, longueur payload 4, flags reserves 4,
  correlation 8. Entiers non signes big-endian ; flags reserves = zero.
- Maximum payload global **4 MiB** ; verifier longueur, role, etat, sequence
  et additions/multiplications avec overflow verifie **avant allocation**.
- Sequence strictement croissante par direction/flux ; aucune reprise de
  connexion d'une session terminee. En-tete tronque, type inconnu, mauvais
  role/session ou doublon : fermer et signaler un echec.
- Lecture d'en-tete <= 2 s, payload <= 2 s apres l'en-tete ; backpressure,
  aucune recherche illimitee d'un magic dans des donnees malformees.
- Cibles : 200 messages/s total, burst 256, 40 MiB/s images, burst 4 MiB.
  Un message hors contrat de taille/direction ferme immediatement le canal ;
  saturation des files est bornee et signalee. Mouvement souris et images
  obsoletes peuvent etre coalesces, jamais les press/release.

Ces nombres sont un point de depart a mesurer, pas une limite configurable
librement par l'application generee.

### 4.3 Messages autorises

| Direction | Message | Contenu / bornes |
|---|---|---|
| Hote -> invite | Hello | Version exacte, SessionId/challenge, role, delais et profil de rendu approuves ; <= 256 octets |
| Invite -> hote | HelloAck | Echo du challenge et digests attendus ; <= 256 octets, une fois ; assertions toujours non fiables |
| Invite -> hote | Ready | Etats enumeres compositor/browser/frontend/backend, version d'agent ; <= 128 octets, une fois apres HelloAck |
| Hote -> invite | Input | Evenement clavier/souris exclusivement dans le compositor invite, <= 512 octets |
| Invite -> hote | Frame | Pixels BGRA8, dimensions du profil approuve ; metadata fixe 16 octets + stride * hauteur |
| Hote -> invite | FrameAck | Identifiant de la derniere image acceptee ; <= 16 octets |
| Deux sens | Heartbeat | Compteur monotone opaque ; <= 16 octets, 1/s par direction |
| Invite -> hote | Event | Code de journal enumere + metadonnees typees bornees ; <= 1024 octets, 10/s maximum |
| Hote -> invite | Stop | Code de raison enumere ; <= 16 octets, idempotent |
| Invite -> hote | Stopped / Fault | Code enumere ; <= 128 octets ; n'autorise aucune action Windows arbitraire |

Le flux images n'accepte que Frame/FrameAck apres son handshake.
Le flux controle n'accepte jamais Frame. Le guest n'envoie jamais Input, Stop
ou une demande de lancement/lecture de fichier sur Windows.

**Images :** profil prototype 960x540, maximum futur 1280x720 ; stride =
largeur * 4, dimensions fixes pour la session. Metadata : largeur/hauteur u16,
stride u32, frameId u64. Pas de PNG/JPEG/video, compression, HTML, SVG,
JavaScript, URL ou police envoyee a un decodeur hote dans ce premier protocole.
Copie brute vers un buffer de taille connue, alpha force opaque cote hote ;
canvas/ImageData ou texture locale, pas `innerHTML`/navigation.
10 images/s maximum, une seule image non acquittee et deux buffers fixes ;
une image retardee est abandonnee plutot qu'accumulee.
A 1280x720 : 3 686 400 octets par image, environ 35,2 Mio/s a 10 Hz.

**Entrees :** uniquement lorsque la fenetre de confiance a le focus et que
la session est Ready ; coordonnees entieres dans le cadre, boutons 1-3,
scroll borne, codes clavier physiques enumerees, up/down/modificateurs valides.
Texte UTF-8 <= 256 octets pour IME/clavier francais, sans acces presse-papiers.
Pas de hooks globaux, Win/Alt-Tab/Ctrl-Alt-Del ou injection Windows.
120 evenements/s maximum, mouvements coalesces a 30 Hz, file 64 ;
un depassement non coalescable suspend les entrees, relache les touches et
signale l'erreur. Blur, deconnexion ou Stop relachent toutes les touches.
Pas de macros, shell, JavaScript/CDP fourni par la page ou commandes arbitraires.

**Logs :** liste fermee de codes, compteurs et durées ; pas de stdout genere
transmis tel quel, ANSI, chemins Windows, URLs, token ou contenu saisi. Buffer
circulaire hote <= 64 KiB, 64 evenements de cycle de vie comme actuellement ;
debit excedentaire agrege dans un compteur explicite, pas de disque illimite.
Les logs et Ready invites sont des indications ; arret/nettoyage sont confirmes
par l'inventaire de l'hyperviseur, pas par `Stopped` du guest.

### 4.4 Affichage dans Studio

Premiere validation : fenetre de visualisation locale de confiance, hors du
WebView principal, uniquement pixels + entrees + bandeau Stop.
L'agent capture le compositor virtuel de la VM ; aucune capture de fenetre
personnelle Windows ou lancement d'une application hote par le guest.

Integration Tauri ulterieure : frontend local signe ne chargeant aucun HTML
invite, capability propre a cette fenetre, trois operations bornees
(recevoir frame, envoyer Input, demander Stop). Les capabilities `main`
existantes ne sont pas etendues au contenu invite. Canal local a ACL restrictive
et session liee au processus du viewer, sans serveur TCP et sans bearer Studio
dans une page generee. Le frontend de confiance transporte les pixels par IPC
binaire, pas base64 a debit illimite.
Le traitement des frames/inputs devra etre audite et teste ; ce document ne
pretend pas que ces commandes Tauri ou ce viewer existent deja.

## 5. Cycle de vie et nettoyage

1. **Preflight sans execution** : prerequis, droits et ressources libres,
   hash/signature de l'image, version broker/protocole, aucun residu.
   Une indisponibilite reste un refus, jamais un changement de transport automatique.
2. **Plan** : valeurs canoniques completes, limites, hash de page/image,
   peripheriques, integrations, protocoles et politique de nettoyage.
3. **Consentement distinct** : autorisation du seul essai statique ou,
   ulterieurement, autorisation Preview pour la revision et l'empreinte exactes.
4. **Allocation apres consentement** : journal d'appartenance durable avant
   chaque effet externe ; VMID, image de base read-only, overlay et nonce propres.
   AutomaticStartAction = Nothing, pas de save state, checkpoints ou reprise.
5. **Demarrage** : 2 vCPU/2 Gio fixes, zero vNIC, verification de configuration
   effective ; handshake + readiness + premiere image avec temoin deterministe.
   Pas de simple `VM Running` considere comme navigateur pret.
6. **Execution** : duree absolue depuis le debut du demarrage, heartbeat 1/s,
   perte >= 3 s -> fermeture des entrees/flux, extinction controlee.
   Watchdog invite independant du processus guest application.
7. **Arret** : revoquer l'acces viewer et les nonces immediatement ; Stop invite
   <= 3 s, puis extinction Hyper-V exacte si necessaire. Confirmer VM Off,
   desinscription et liberation des disques/handles ; effacer overlay,
   configuration secrete et staging de cette seule session. Ne pas effacer
   l'image de base fournie par l'utilisateur ni les ressources d'autres VM.
8. **Crash du controleur** : Hyper-V peut continuer a tourner apres la mort
   du client, contrairement a un simple Job Object. Supervisor hote independant
   ou gestionnaire de bail a qualifier, plus watchdog invite et TTL absolu.
   Le choix d'installer un service hote serait soumis a autorisation separee.
   Un crash de tous les composants laisse un residu possible : recuperation
   au redemarrage indispensable avant tout nouveau lancement.

Pour la recuperation : journal a ACL de l'hote, stocke hors du guest et hors du
magasin de mission ; UUID exact + emplacement canonique + marqueur d'appartenance.
Un nom/prefixe de VM seul ne suffit pas. Rejeter symlinks/reparse points et
chemins inattendus ; inventaire incomplet, acces refuse, fichier verrouille
ou VM encore en cours = **Failed/CleanupFailed**, nouveaux starts bloques.
Pas de `Stopped` si une ressource de session subsiste ; echec signale.
Suppression n'est pas effacement forensique du SSD ; ne pas fournir de secrets
Studio a l'invite et ne pas conserver ses checkpoints.

## 6. Prototype statique soumis a autorisation (NON EXECUTE)

### Perimetre exact propose

- Une VM Linux Gen2 neuve, 2 vCPU, **2048 MiB fixes**, zero vNIC, aucune
  virtualisation imbriquee, aucun Docker ou Ollama, aucune mission generee.
- Image appliance preparee separement depuis une distribution Linux maintenue
  avec Chromium, noyau hv_sock, agent et compositor logiciels ; image
  versionnee, SBOM, licences et SHA-256/signature a fournir **avant l'essai**.
  Aucune URL ou empreinte fictive n'est presentee comme approuvee.
- Page HTML/JS deterministe embarquee dans l'image, servie dans l'invite
  par un serveur fixe sur loopback. Aucun CDN, police externe, compte ou secret.
  Un carre colore, compteur d'entrees, bouton et canevas de resultat sont les
  seules fonctions nominales ; hash de page et dimensions fixes dans le plan.
- Payloads de test fixes vers les faux services, jamais une commande utilisateur,
  avec WebRTC actif. Versions exactes Chromium/agent/protocole consignees.
- Deux canaux HvSocket et viewer de pixels ; maximum **180 s depuis start**,
  delai de readiness 90 s, une seule tentative de demarrage, pas d'auto-retry.
- Arret normal, puis essais distincts de fermeture forcee/crash apres nouvelle
  autorisation de la campagne : chaque instance detruite, aucun etat reutilise.
- Aucun besoin de lancer un projet pour tester ce transport. Une image Linux
  statique n'est pas l'image Docker validee v9 ; approbations non interchangeables.

### Preuves a produire

Image recue avec hash du payload pixel et repere visuel attendu, marqueur
`JS_EXECUTED`, clic/clavier retournes au compteur invite, inventaire des processus
navigateur/enfants ; aucune capture de l'ecran Windows.
Un navigateur qui plante ou une page qui n'execute pas les sondes donne
**echec/non teste**, jamais « zero paquet donc securise ».

Le prototype n'est pas construit/lance dans cette etape : aucune VM et aucune
image preapprouvee n'ont ete fournies. Ecrire un faux runtime ou un test de
serialisation ne verifierait ni le noyau invite ni le transport reel.
On s'arrete donc a ce plan, conformement au verrou d'execution demande.

## 7. Campagne de securite preparee

**Tous les tests ci-dessous sont a executer apres autorisation. Aucun compteur
reseau nouveau n'est disponible pour 6.5-E.** Les mesures 6.5-D ne sont pas
des mesures de VM.

Recepteurs factices Windows exclusivement sur des adresses possedees et des
ports ephemeres, jamais sur une interface publique/wildcard. Compter connexions
TCP acceptees et datagrammes UDP/DNS effectivement recus, avec temoins positifs
avant/apres et observation hote quand une capture de paquets est autorisee.
Ne pas installer de pilote de capture. Si les droits d'observation manquent,
le declarer ; pas d'affirmation d'absence de trafic systeme sur les seuls logs.
Tous les serveurs et sockets de test sont arretes a la fin.

Le **127.0.0.1 de la VM n'est pas celui de Windows** : identifier explicitement
le recepteur vise, inclure les adresses Windows controles et les anciennes
passerelles potentielles, ne pas confondre une reponse Linux avec une fuite.
Un service STUN et un resolver DNS temoins dans l'invite permettront de verifier
que les sondes fonctionnent ; ils n'ajoutent aucune vNIC ou route vers Windows.

| Test | Stimulus et preuve attendue |
|---|---|
| Execution nominale | Page/hash et premiere frame attendus, 10 Hz max, saisie de test retournee ; sinon tous les tests navigateur sont non concluants |
| HTTP Windows/localports | Fetch/XHR, img/CSS, iframe, Worker, popup, formulaire et navigation directe vers faux Studio, autres ports, localhost, 127.0.0.1/127.0.0.2 ; **0 connexion recue par Windows** |
| IPv4/IPv6 | Viser ::1, adresses Windows controlees, adresses privees/link-local de test ; aucun paquet externe reel, 0 connexion/paquet Windows |
| Internet | Noms `.invalid` et destinations de laboratoire controlees, pas de trafic vers un tiers ; absence de vNIC/route documentee, pas d'« Internet prouve » par un timeout |
| UDP/STUN WebRTC | RTCPeerConnection + DataChannel + ICE, cible STUN Windows factice ; temoin STUN guest positif et marqueurs JS ; **0 paquet STUN Windows**, WebRTC non desactive |
| DNS | DNS natif UDP/TCP vers resolver factice Windows, resolution systeme navigateur et essais de canaux indirects ; 0 requete Windows, pas de DNS tunneling ou DoH via broker |
| WebSockets | ws/wss vers faux services Windows, y compris depuis Worker ; 0 connexion Windows ; les WebSockets internes a l'invite ne sont pas des sorties Windows |
| Redirections | Service guest retourne 30x vers Windows/IPv6/protocole custom ; 0 requete recue, aucun lancement d'application Windows |
| Fichiers Windows | Fichier sentinelle de test hors StudioData ; tentatives file://, UNC, /mnt/c, 9p/virtiofs ; aucune lecture/ecriture hote, aucun montage ; ne pas essayer de lire un vrai secret |
| Enfants | Sondes natives et navigateur depuis descendants/namespaces de l'invite ; arbre de PID et marques d'execution verifies, 0 paquet interdit |
| Guest compromis | Processus root de laboratoire tente reseau, montage et HvSocket hors services de session ; aucun acces hote indu, ni autre service de gestion |
| Broker hostile | Longueurs overflow/tronquees, type/direction/session/sequence invalides, frames trop grandes, flood/slow reader, log injection ; refus explicite et RAM/debit bornes |
| Sessions concurrentes | Deux identites de test, mauvais VMID/challenge et relecture : aucune frame/input entre sessions ; production toujours limitee a une session |
| Clavier/souris | Coordonnees hors bornes et surdebit refuses, blur/Stop relachent les touches ; aucun evenement injecte dans une application Windows |
| Clipboard/peripheriques | Pas de clipboard, fichier, audio, webcam, GPU, shell, CDP distant ou peripherique partage exploitable |
| Arret force | Fermer viewer/tuer agent/broker ; inputs invalides immediatement, fermeture complete mesuree et aucun port/flux reutilisable |
| Crash controleur | Tuer le controleur detenu, observer bail/supervisor, TTL et recuperation ; un processus Hyper-V encore present est un echec, pas un Job Object suppose suffisant |
| Crash/reboot hote | Campagne distincte explicitement autorisee, **aucun reboot de ce poste dans cette etape** ; prouver pas d'auto-start et recuperation avant nouvelle session |
| Suppression | Echecs partiels a chaque allocation, VM/disques/handles/journal verifiques, aucun residu ; verrouillages et acces refuses remontent CleanupFailed |
| Portee | Temoins Windows non confines fonctionnels avant/apres, aucune config/rule/firewall/VM tierce modifiee |

Acceptation : tous les recepteurs interdits comptent **zero**, sondes executees,
temoins positifs, image/politique identiques et nettoyage confirme. Un test
manquant, non observable ou en echec garde l'activation fermee.
Des captures IP a zero ne prouvent pas a elles seules l'absence de messages
sur VMBus ; tests et inventaire IPC sont obligatoires en plus.

## 8. Cout estime et garde-fous

| Poste | Prototype HTML statique | Future Preview full-stack |
|---|---|---|
| RAM invite fixe | 2 Gio | 4 Gio cible, a revoir si build hors budget |
| Surcout hote reserve (hyperviseur/broker/viewer) | 0,5 Gio minimum de budget, a mesurer | 1 Gio minimum de budget, a mesurer |
| Marge Windows non allouee | >= 2 Gio apres allocation | >= 2 Gio apres allocation |
| Precondition de RAM disponible | >= **4,5 Gio** avant essai | >= **7 Gio** avant essai |
| CPU | 2 vCPU ; quota Hyper-V a verifier | 2 vCPU, builds frontend/backend serialises ; pas de concurrence LLM |
| GPU / VRAM | Aucun partage GPU, 0 VRAM dediee requise | Meme principe, pas de promesse d'acceleration |
| Image de base sur disque | Environ 3-6 Gio | Environ 6-12 Gio avec SDK/dependances offline |
| Ecriture de session | Plafond 1 Gio de budget physique | Plafond 2 Gio de budget physique, refus si non applicable |
| Marge disque pour essais/mise a jour | >= 15 Gio libres | >= 25 Gio libres avec ancienne/nouvelle image |
| Affichage brut | 960x540, ~19,8 Mio/s a 10 Hz | 1280x720 maximum, ~35,2 Mio/s |
| Latence cible | <= 250 ms, a mesurer | Identique ; pas une garantie temps reel |

RAM VM fixe, nombre de vCPU et quotas CPU supervises ; pas de swap hote utilise
comme argument pour accepter une machine saturee. Precondition verifiee juste
avant allocation et erreur claire si les ressources ont change entre-temps.
Un disque differentiel ne limite pas magiquement les ecritures physiques :
quota du stockage de session a rendre effectif et testable, sinon refuser.
Le prototype du jour est **bloque par les 1,3 Gio disponibles**.
Ne pas fermer des applications, des distributions ou des processus partages
pour recuperer la RAM sans demande de l'utilisateur.

## 9. Integration progressive au code existant

**Aucun fichier de production, endpoint, DI, schema ou autorisation n'est
modifie par 6.5-E.** Le code Docker/proxy/navigateur reste present et verrouille.

1. Qualifier l'appliance statique et son broker hors Studio. Fournir preuves
   reseau, ressources, image, profils/canaux et nettoyage. Ne pas reutiliser
   l'autorisation Docker de validation ou la mission v9.
2. Implementer ensuite un `VirtualizedPreviewRuntime : IPreviewRuntime`,
   en conservant [PreviewSessionManager](../AIPlatform.Infrastructure/Studio/PreviewSessionManager.cs),
   ses gates, capture source, autorisation unique, readiness et arret.
   Ce nom est une proposition, pas une classe livree.
3. **Ne pas forcer le contrat `IControlledPreviewBrowser.OpenAsync(origin,
   token, ...)`** a representer une VM : il suppose un navigateur hote et une
   origine proxy. Le nouveau runtime possedera la VM, le broker et le viewer ;
   il remplacera le couple navigateur/proxy dans le parcours virtualise.
   Les appels HTTP applicatifs resteront entierement dans l'invite, avec
   frontend/API loopback et routage interne fixe. Aucun proxy HTTP vers Windows.
4. Faire evoluer [PreviewPlan](../AIPlatform.Core/Studio/PreviewPlan.cs) dans
   un **schema explicitement versionne**, sans changer le calcul des plans v1
   existants. `SchemaVersion == 1` est aujourd'hui impose et l'image attend
   un digest Docker `sha256:...` : ne pas y faire passer silencieusement un VHDX.
   Le nouveau schema identifiera le backend Hyper-V, le hash de base VHDX et
   de son manifeste signe, noyau/agent/compositor/Chromium/broker/viewer, les
   versions du protocole et de politique, zero vNIC et liste de peripheriques/
   integrations, transport/services, dimensions/debits/inputs, limites,
   stockage et regles de nettoyage. Les changements de representation
   doivent avoir des vecteurs de hash golden et une serialisation stabilisee.
5. Les valeurs d'execution aleatoires (VMID, nonce, chemins de session)
   appartiennent a l'allocation liee a SessionId, pas a une regeneration
   aleatoire du plan lors de sa comparaison. Le plan contient leur politique ;
   le controleur prouve que les ressources effectives y correspondent.
   Source/image/base/config sont revalidees juste avant boot. Les champs
   approuves ne changent pas sous pretexte d'un port dynamique.
6. Conserver [PreviewAuthorizationStore](../AIPlatform.Infrastructure/Studio/PreviewAuthorizationStore.cs)
   et sa consommation atomique apres revalidation. Changement de backend,
   version, image ou politique = nouvelle empreinte, ancienne autorisation
   invalidee, nouveau consentement. Les plans/historiques/validations v9
   restent immuables ; **une autre mission de test** sera necessaire plus tard.
   Le store actuel n'encode pas de date limite d'approbation : si un TTL
   d'autorisation est ajoute, il doit etre explicite, teste et distinct du
   TTL de VM ; ne pas annoncer qu'il est deja implemente.
7. `PreviewReadiness` expose deux booleens et une URL optionnelle :
   garder la signification backend/frontend et demander en plus la readiness
   browser/frame avant Ready. Etendre la reponse de maniere additive avec
   un handle opaque de viewer versionne ; ne pas detourner `Url` avec une
   URL speciale ou l'utiliser pour fournir une destination arbitraire.
8. Conserver les evenements existants, ajouter des codes de VM/broker filtres ;
   un transport change ne reecrit pas l'historique de mission. La maintenance
   actuelle depend de `runtime.IsAvailable` : distinguer, dans l'integration,
   **disponibilite de demarrage et capacite de recuperation/arret** pour qu'un
   broker casse ne fasse pas sauter le nettoyage. Pas de modification ici.
9. Packaging Tauri : ajout explicite et audite du broker/viewer signes et d'un
   manifeste d'appliance. Le script [prepare-backend.mjs](../AIPlatform.Web/scripts/prepare-backend.mjs)
   utilise une allowlist stricte, sans VM aujourd'hui : ne pas copier un VHDX
   ou des executables dans cette liste sans changement examine.
   Installation de prerequis, enregistrement HvSocket et droits Hyper-V
   exigent un consentement Windows separe. Ne jamais elever tout Studio.
10. Seulement apres tests full-stack avec **nouvelle autorisation** : selection
    DI explicite du runtime qualifie, gates de securite reevalues, anciens
    parcours et tests simules conserves. Pas de fallback vers Edge Windows,
    WSL ou Docker si la VM echoue. HTTP 503 demeure jusqu'a integration validee.

## 10. Validations realisees et autorisations suivantes

### Realise dans cette etape

- Inspection Windows/CIM/services/commandes et disque, sans demarrage VM/WSL.
- Lecture du code runtime/plan/autorisations/packaging existant.
- Verification des possibilites documentees Microsoft/QEMU [S1-S11].
- Architecture, contrat propose et campagne de tests prepares ici.
- Aucun test VM, aucun compteur de paquets VM, aucun rendu invite affirme.
- Trois tests backend cibles rejoues : enregistrement du runtime concret
  indisponible, refus de preparation par le coordinateur sans appel Docker,
  reponse HTTP 503 de l'API lorsque le runtime securise est indisponible.
  Resultat : **3 reussites, zero echec** ; pas de nouvelle execution de toute
  la suite des 352 tests dans cette etape documentaire.
- Mission de reference verifiee en lecture seule : statut Verified, version 9,
  32 fichiers du manifeste, zero divergence. SHA-256 du JSON de mission :
  `6563AF1EDEF8180CABD5CBE6CFC1CCFC09AF936B6BB9038D66279256CB5F29FF`,
  identique aux controles precedents.
- Liens locaux du document verifies, `git diff --check` sans erreur.

### Autorisations distinctes requises AVANT tout prototype reel

1. **Image/outillage** : approbation de la distribution, provenance, licences,
   versions, taille et empreintes de l'appliance et du broker/agent. Toute
   acquisition ou preparation en ligne est distincte de l'execution offline.
   Ne rien telecharger ou installer maintenant.
2. **Configuration Windows** : verifier les droits de gestion Hyper-V puis
   autoriser, si necessaire, une operation privilegiee precise realisee par
   l'utilisateur. Enregistrement de deux services HvSocket avec nettoyage
   explicite des seules nouvelles entrees. Aucun ajout de groupe global,
   service, role, redemarrage, ACL BFE ou pare-feu sans consentement additionnel.
3. **Ressources** : utilisateur libere volontairement assez de RAM ; recontrole
   >= 4,5 Gio et >= 15 Gio disque avant essai. Aucun arret de processus tiers.
4. **Execution VM statique** : accord pour creation, boot puis destruction
   de la seule VM nommee/identifiee dans un plan complete avec ses vrais
   digests ; 2 vCPU/2 Gio/180 s maximum, sans reseau/partages.
   C'est une autorisation de laboratoire, **pas une autorisation Preview**.
5. **Observabilite et crash** : services factices locaux et, seulement si
   necessaire et autorise, capture via un outil Windows deja present ;
   liste precise des processus de test pouvant etre tues. Un reboot ou un
   test de crash systeme fait toujours l'objet d'une autorisation supplementaire.

Tant que le plan n'a ni image verifiee, ni droits/transport qualifies, ni RAM
disponible, il n'est pas executable ni presentable comme une approbation
Preview prete. **Aucune demande d'autorisation de mission n'est creee ici.**

## Sources officielles consultees

- [S1 - Hyper-V : installation, editions et privileges](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/get-started/install-hyper-v)
- [S2 - Hyper-V : materiel, SLAT et hyperviseur deja detecte](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/host-hardware-requirements)
- [S3 - FAQ WSL : editions et plateforme de virtualisation](https://learn.microsoft.com/en-us/windows/wsl/faq)
- [S4 - Configuration WSL : portee globale, automount, interop et reseau](https://learn.microsoft.com/en-us/windows/wsl/wsl-config)
- [S5 - WSL : NAT, mirrored et DNS tunneling](https://learn.microsoft.com/en-us/windows/wsl/networking)
- [S6 - Architecture WSL2 : distributions dans une VM geree](https://learn.microsoft.com/en-us/windows/wsl/compare-versions)
- [S7 - WSLg : integration graphique, clipboard et GPU](https://github.com/microsoft/wslg)
- [S8 - Hyper-V sockets : absence d'IP, Linux et enregistrement privilegie](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/make-integration-service)
- [S9 - QEMU : accelerateurs, dont WHPX Windows](https://www.qemu.org/docs/master/system/introduction.html)
- [S10 - QEMU : frontiere de securite, TCG et limites du modele](https://www.qemu.org/docs/master/system/security.html)
- [S11 - Hyper-V : services d'integration et copie de fichiers](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/integration-services)
