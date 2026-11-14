# MVP 6.5-D - Prototype Windows de confinement Preview

## Decision

**Ne pas integrer ce prototype au navigateur de production. Conserver HTTP 503.**
AppContainer permet de lancer un processus sans capacites reseau et les essais
natifs ont effectivement observe zero connexion/paquet interdit. Toutefois,
Edge confine plante avant d'executer la page, et les droits WFP necessaires
ne sont pas disponibles sans modification des permissions ou elevation.
Ce resultat ne termine ni MVP 6.5 ni la validation du navigateur.

## Prototype effectivement realise

Projet console .NET 10 Windows independant de la solution et de Tauri :

- [Program.cs](./Program.cs) : orchestration, identite unique, profil temporaire,
  rapports et nettoyage.
- [NativeWindows.cs](./NativeWindows.cs) : appels Win32 AppContainer, lecture des
  tokens et diagnostic des droits WFP.
- [RestrictedProcess.cs](./RestrictedProcess.cs) : creation suspendue,
  `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`, liste explicite des trois
  handles stdio herites, affectation a un Job Object avant reprise.
- [NetworkLab.cs](./NetworkLab.cs) : recepteurs HTTP/TCP IPv4 et IPv6, UDP/STUN
  et DNS factice sur ports loopback ephemeres, compteurs reels.
- [network-probe.mjs](./network-probe.mjs) : sondes natives Node et tentative
  d'execution d'un enfant, sans Playwright, proxy ou interception HTTP.
- [CrashJobProbe.cs](./CrashJobProbe.cs) : controleur auxiliaire tue explicitement
  pour verifier `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.

Aucun pilote, modele, package Node, service Windows ou application n'est installe.
Le binaire Node deja installe est copie dans le repertoire prive de l'essai.
Les seules ACL et l'etiquette d'integrite modifiees concernent ce repertoire
temporaire, avec le SID propre a cet essai. Aucun droit sur `C:\`, les profils
personnels ou les fichiers Studio n'est ajoute.
Le profil AppContainer est enregistre temporairement puis supprime par
`DeleteAppContainerProfile`. Il ne faut pas confondre cette suppression normale
avec une recuperation automatique apres crash du controleur principal.

Le chargement Node emploie `--preserve-symlinks` et `--preserve-symlinks-main` :
sans eux, sa canonicalisation tente `lstat C:\`, refuse par AppContainer.
Les fichiers de sonde sont des copies controlees dans le repertoire prive ;
aucune ACL globale n'est elargie pour contourner ce refus.
Le processus enfant recoit uniquement une liste de variables Windows necessaires
au demarrage, pas les variables ou jetons Studio. Les fichiers de controle
restent distincts des profils des navigateurs personnels.

## Faisabilite AppContainer

Essai realise sans elevation sur Windows 11 build **22631**, avec Edge
**157.0.4341.46** :

- creation/suppression d'un profil dedie : reussies ;
- token du processus natif effectivement `TokenIsAppContainer=true` ;
- SID attendu, integrite basse `S-1-16-4096`, **zero capacite** effectivement lue
  dans le token, et pas seulement demandee dans les arguments ;
- essais reseau du parent effectivement executes et mesures ;
- creation de son enfant Node refusee avec `spawn EPERM`, signalee et non masquee ;
- Edge demarre sous AppContainer, mais termine avec **0xC0000005**
  (`STATUS_ACCESS_VIOLATION`) avant le marqueur d'execution JavaScript ;
- 19 processus ont ete observes dans son Job Object lors de l'essai final :
  tous AppContainer, capacites vides, avec **deux SID distincts**, dont un SID
  imbrique derive du parent.

Un filtre par egalite sur le seul SID parent ne peut donc pas etre suppose
suffisant pour Chromium. Le comportement des conditions ALE avec ces identites
imbriquees exige une verification reelle, pas une hypothese.

Les erreurs Edge mentionnent notamment `LoadEnclaveImageW` (577) et des echecs
de resolution de chemins/jetons. Elles sont conservees dans le rapport brut ;
elles ne prouvent pas a elles seules la cause exacte du crash.
Aucun flag ne desactive le sandbox Chromium, WebRTC ou les protections Edge
pour faire passer cet essai.

Les API utilisees existent depuis Windows 8 ou avant et sont disponibles sur
Windows 10/11. **Cela ne constitue pas une certification Windows 10** :
seule la machine Windows 11 ci-dessus a ete testee.

## Faisabilite WFP et privileges

Diagnostic reel sous token moyen, administrateurs non actifs :

| Appel | Resultat |
|---|---|
| `FwpmEngineOpen0`, session dynamique | 0, succes |
| `FwpmTransactionBegin0`, ecriture | **5, ERROR_ACCESS_DENIED** |
| Ajout de sous-couche/filtre | non execute |
| `FwpmEngineClose0` | 0, succes |

**Zero filtre WFP cree**, persistant ou dynamique. Aucune regle du pare-feu
global, ACL BFE ou exception loopback n'est modifiee. Aucun prompt d'elevation
n'est demande.
Les droits d'ouverture ne sont pas les droits d'ajout/ecriture. Un token
administrateur actif, un groupe autorise ou une delegation BFE appropriee serait
necessaire ; les contraintes de cette mission interdisent de les obtenir
automatiquement ou de modifier les permissions globales.
Un filtrage par chemin `msedge.exe` affecterait aussi les autres instances :
ce n'est pas une solution acceptable.

Une session WFP dynamique permettrait une duree de vie liee au controleur, sans
pilote pour des filtres ALE classiques. **Aucun filtrage WFP du trafic n'a ete
demontre ici**, faute de droits. La disparition de filtres apres crash n'a donc
pas ete testee.

## Compteurs effectivement observes

Compteurs de l'execution finale, chaque temoin Node incluant parent et enfant :

| Recepteur/evenement | Node avant | Node AppContainer | Node apres | Edge temoin | Edge AppContainer |
|---|---:|---:|---:|---:|---:|
| Connexions HTTP IPv4/localhost | 8 | 0 | 8 | 4 | 0* |
| Connexions autre port local | 4 | 0 | 4 | 2 | 0* |
| Connexions IPv6 `::1` | 2 | 0 | 2 | 1 | 0* |
| Paquets UDP/STUN | 2 | 0 | 2 | 4 | 0* |
| Paquets DNS vers le resolver factice | 2 | 0 | 2 | 0** | 0* |
| Requetes Upgrade WebSocket | 2 | 0 | 2 | 1 | 0* |
| Requetes de redirection | 2 | 0 | 2 | 1 | 0* |

`*` Edge confine n'a pas execute la page : ses zeros ne sont **pas** un test
WebRTC/navigateur reussi. Le temoin Edge a effectivement execute JavaScript et
envoye de vrais paquets STUN ; le test n'a pas desactive WebRTC.
Le Node confine teste du UDP natif, pas l'implementation WebRTC d'Edge.
Les volumes STUN du temoin peuvent varier selon le timing ICE ; le seuil requis
pour la session confinee reste strictement zero.

`**` Le DNS utilise une instance `dns.Resolver` dirigee vers le serveur UDP
factice. Les parametres DNS Windows ne sont pas modifies. Le resolver systeme,
ses brokers, DoH et toutes les sorties possibles d'un navigateur ne sont pas
certifies par ce test. Aucun service tiers n'est utilise comme recepteur.

Les retours `socket.send` ne servent pas de preuve : le parent confine a obtenu
un retour d'envoi UDP, mais le recepteur a compte **zero paquet**.
Les compteurs des temoins avant/apres restent identiques et positifs :
les restrictions ne bloquent pas ces processus non confines.
Les navigateurs personnels n'ont ete ni pilotes ni modifies.

## Nettoyage et crash

- tous les Job Objects du dernier essai ont confirme leur inventaire vide ;
- arret brutal du controleur auxiliaire : le processus AppContainer surveille
  est termine par fermeture du Job Object, sans tuer l'arbre explicitement ;
- profil AppContainer supprime, resultat API 0, repertoire de profil absent ;
- repertoire temporaire final supprime ;
- les points de reanalyse WinINet crees dans le profil prive sont uniquement
  **delies**, jamais traverses ; leurs cibles ne sont pas supprimees ;
- un repertoire residuel d'une iteration precedente a ete nettoye de la meme
  maniere, par son chemin exact, sans modifier ses cibles ou des profils tiers.

La fermeture du Job ne supprime pas automatiquement un profil AppContainer
enregistre. **Le crash brutal du controleur principal et la recuperation de ce
profil ne sont pas certifies** : une integration demanderait un journal de
propriete et une recuperation strictement bornee. Pas de promesse de nettoyage
complet sur la seule base du flag Kill-on-close.

## Reproduction et packaging

Depuis la racine du depot, avec .NET 10, Node et Edge deja installes :

```powershell
dotnet build .\tools\WindowsConfinementPrototype\WindowsConfinementPrototype.csproj
$report = Join-Path $env:TEMP ('windows-confinement-' + [guid]::NewGuid().ToString('N') + '.json')
dotnet .\tools\WindowsConfinementPrototype\bin\Debug\net10.0-windows\WindowsConfinementPrototype.dll `
  --node (Get-Command node.exe).Source --report $report
```

Le rapport ne peut pas ecraser un fichier existant. Le runner retourne **2** :
prototype non certifie pour integration, avec echecs conserves. Ce n'est pas
une commande de lancement de Preview.
La compilation actuelle reussit sans avertissement. Le projet n'est reference
ni par le backend, ni par le packaging NSIS, ni par les capabilities Tauri.
Une integration future aurait besoin d'un lanceur signe, des dependances du
navigateur, d'un broker strict et d'un cycle de vie/crash valides. Aucune
installation ou mise a jour Windows/Tauri n'a ete effectuee.

## Recommandation

**Passer a une architecture virtualisee dediee plutot qu'integrer ce prototype
Edge/AppContainer/WFP dans son etat actuel.**

Alternative a valider separement : navigateur dans un invite ephemere sans
interface reseau vers Windows/Internet, sans profils/disques partages,
clipboard, integrations ou secrets Studio. Un broker hote de confiance
transporterait seulement les requetes bornees de la session approuvee via un
canal hyperviseur/IPC strict, sans destination choisie par la page. L'affichage
utiliserait une console dediee, pas le WebView privilegie de Tauri.
Le WSL/Docker Desktop existant ne doit pas etre presente comme cette enceinte
dediee sans preuves supplementaires.

Cette alternative n'a ete ni installee ni lancee. Sa consommation memoire,
son packaging et ses tests de contournement doivent etre verifies avant toute
autorisation de Preview/Docker. HTTP 503 demeure actif.

## References Microsoft

- [AppContainer isolation](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation)
- [Attributs de creation de processus](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute)
- [Controle d'acces WFP](https://learn.microsoft.com/en-us/windows/win32/fwp/access-control)
- [Ouverture d'une session WFP](https://learn.microsoft.com/en-us/windows/win32/api/fwpmu/nf-fwpmu-fwpmengineopen0)
