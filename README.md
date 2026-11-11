# AIPlatform Studio - MVP 4

Plateforme multi-agents locale .NET 10 + React/TypeScript. Ollama selectionne un
agent autorise et son modele configure. Studio permet de suivre des missions
asynchrones, generer les fichiers d'un petit projet .NET 10, puis les compiler et
tester hors ligne dans Docker apres autorisation humaine. Les corrections sont
ciblees et limitees a trois au maximum.

**Le code genere est non fiable. Aucun code genere ne doit etre execute sur l'hote.**
Sans Docker Linux disponible, image hors ligne preparee, activation explicite de
la sandbox ET accord humain pour la mission, la compilation et les tests ne sont
pas executes. Le statut final est alors `GeneratedUnverified`, jamais un succes
de compilation.

## Application Windows

Studio dispose maintenant d'une enveloppe **Tauri 2 / WebView2**. Le backend reste
en .NET 10 ; Rust ne gere que le lancement, la disponibilite et l'arret du processus
compagnon. React est compile et embarque : aucun serveur Vite en production.

### Installer et lancer sans terminal

1. Utiliser l'installateur Windows x64 produit dans
   `AIPlatform.Web\src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis\AIPlatform Studio_0.4.0_x64-setup.exe`.
2. L'installation est reservee a l'utilisateur courant, sans elevation demandee.
   Elle cree le raccourci **AIPlatform Studio** du menu Demarrer et propose un
   raccourci Bureau. L'icone est incluse.
3. Lancer ce raccourci par double-clic. La fenetre native demarre une API privee
   sans console, attend sa vraie disponibilite et affiche les etats API/Ollama/Docker.
4. Fermer la fenetre arrete uniquement son backend, apres annulation des missions
   actives. Les missions en attente d'autorisation restent en attente.
   **Ollama et Docker ne sont jamais arretes par Studio.**

Le backend est autonome : l'utilisateur final n'a besoin ni du SDK .NET, ni de
Node.js, ni de Rust. WebView2 est requis ; l'installateur propose son bootstrap
Microsoft s'il manque (telechargement necessaire). **Ollama et Docker Desktop
restent externes** : les demarrer ou les installer separement depuis
[Ollama](https://ollama.com/download/windows) et
[Docker Desktop](https://www.docker.com/products/docker-desktop/).
Une dependance absente produit un etat explicite et des instructions ; Studio
ne l'installe pas et ne lance pas ses services automatiquement.
L'absence de Docker ou d'image preparee n'empeche pas la generation des fichiers.

L'installateur est **non signe** : ne pas le presenter comme une distribution
certifiee. Une distribution publique necessiterait signature, politique de mises
a jour et revue de securite. Si Windows affiche un avertissement, la decision
appartient a l'utilisateur ; aucune protection Windows n'est desactivee.

### Services et securite locale

- L'API ecoute uniquement sur `127.0.0.1`, avec un port libre attribue par Windows.
  Elle n'utilise pas le port de developpement 5167 et ne prend jamais possession
  d'un service deja present sur un port.
- Un jeton aleatoire de 256 bits est renouvelle a chaque lancement/redemarrage.
  Toutes les requetes Desktop exigent `Authorization: Bearer ...`, y compris
  les consultations. Le jeton reste en memoire et dans l'environnement du seul
  processus compagnon : ni argument de commande, ni fichier, ni localStorage.
- Le signal de disponibilite est suivi d'un appel authentifie a `/api/health`
  avec controle du PID du processus possede. Aucune requete React n'est envoyee
  avant la fin de cette verification.
- Les origines sont limitees aux assets Tauri et au serveur de developpement
  local. La navigation native vers un site externe est refusee.
- L'IPC autorise seulement la consultation d'etat et le redemarrage du backend
  de cette instance. Aucun plugin shell, fichiers ou HTTP general n'est expose.
- **Relancer le backend** demande confirmation dans Studio ; les missions actives
  sont annulees et les brouillons non envoyes sont reinitialises.
- L'arret utilise un canal stdin prive. Sa fermeture, meme si le lanceur disparait,
  demande l'arret .NET. Apres 22 secondes, seul le processus compagnon possede peut
  etre termine de force. Un echec est journalise, pas presente comme un arret propre.
- Une seconde ouverture du lanceur active la fenetre existante. Ne pas lancer
  manuellement plusieurs API sur les memes donnees.

Cette protection limite les appels d'autres pages/processus sans jeton ; ce n'est
pas une isolation contre un logiciel malveillant executant du code sous le meme
compte Windows. Les validations des fichiers, accords humains et conteneurs du
MVP 3 restent obligatoires.

### Donnees et configuration Desktop

Les donnees ne sont jamais stockees dans le dossier d'installation :

```text
%LOCALAPPDATA%\AIPlatformStudio\
  StudioData\missions\...
  StudioData\workspaces\...
  backend.log
  backend.previous.log
  settings.json                 # facultatif, cree manuellement
```

Le journal technique est renouvelle au demarrage s'il depasse 1 Mio ; la rotation
precedente est conservee. Les logs de generation/build/tests restent consultables
dans les missions. Une erreur de demarrage affiche aussi l'emplacement du journal.

La configuration embarquee combine `appsettings.json` et
[appsettings.Desktop.json](AIPlatform.API/appsettings.Desktop.json).
Le fichier facultatif `%LOCALAPPDATA%\AIPlatformStudio\settings.json` surcharge
les options metier, Ollama et sandbox au demarrage ; fermer puis relancer Studio
apres modification. Le port loopback et le dossier de donnees restent imposes.
Les configurations Development, donnees existantes et secrets ne sont pas distribues.

Le profil Desktop utilise le nom portable `aiplatform-sandbox:net10`. Il ne
telecharge jamais cette image : suivre la preparation controlee du chapitre 6.
Pour figer son identifiant sur une installation, definir `Sandbox.Image` dans
`settings.json` apres inspection locale et autorisation.

Le mode developpement conserve ses donnees dans le depot. **Aucune migration
automatique ou suppression n'est effectuee.** Pour reutiliser l'historique :
fermer toutes les instances, sauvegarder les deux stockages, puis copier le contenu
de `StudioData` du depot vers le stockage Desktop vide. Ne pas fusionner des
identifiants existants sans revue ; les workspaces et leurs metadonnees doivent
etre copies ensemble. Une reinstallation conserve les donnees AppData.

### Construire l'application Windows

Prerequis developpeur : .NET SDK 10, Node/npm, Rust MSVC indique par
`rust-toolchain.toml`, Visual Studio 2022 avec outils C++ et Windows SDK, WebView2.
Le CLI Tauri telecharge ses outils NSIS lors du premier packaging ; un acces
reseau aux registres npm/crates et outils officiels est necessaire.

```powershell
Set-Location .\AIPlatform.Web
npm ci
$env:PATH = "$env:USERPROFILE\.cargo\bin;$env:PATH"
npm run desktop:dev            # Vite local + backend autonome + fenetre native
# Fermer cette instance avant de reconstruire les fichiers du compagnon.
npm run desktop:build          # publication .NET + React + Rust + installateur NSIS
```

Ne pas distribuer le seul executable Rust du dossier target : utiliser
l'installateur, qui inclut aussi le compagnon et sa configuration.

## 1. Prerequis et demarrage

Environnement verifie : Windows, SDK .NET 10, Node.js 20.14, npm 10.8,
Ollama sur `http://localhost:11434`, modeles deja installes :

- `qwen3:4b` : routage et conversation generale ;
- `qwen2.5-coder:7b` : developpement et generation de projets.

Docker Desktop 4.99.0 / Engine 29.9.3, contexte `desktop-linux` (WSL 2), a
maintenant ete valide sur cette machine. L'image hors ligne a ete preparee apres
autorisation. Le runner ne telecharge aucune image, n'ouvre jamais le reseau et
n'utilise jamais Windows comme solution de repli. Docker reste facultatif pour
generer et consulter les fichiers. Aucun modele Ollama n'a ete modifie.

Mode navigateur de developpement conserve. Depuis la racine du depot, terminal API :

```powershell
dotnet build .\AIPlatform.slnx
dotnet test .\AIPlatform.slnx --no-build
dotnet run --project .\AIPlatform.API --launch-profile http
```

Dans un second terminal :

```powershell
Set-Location .\AIPlatform.Web
npm ci
npm run dev
```

- Studio : **http://127.0.0.1:5173**
- API : **http://localhost:5167**
- OpenAPI en Development : **http://localhost:5167/openapi/v1.json**

Vite proxifie `/api` vers l'API locale. Ne pas utiliser `--host 0.0.0.0`.
Les versions de Vite et Vitest ont ete choisies pour fonctionner avec Node 20.14.
Le serveur Vite refuse de changer automatiquement de port si 5173 est occupe.

## 2. Utiliser Studio

### Workspace

- Envoyer une mission en mode **Conversation** ou **Projet .NET**.
- Selection **Automatique** ou choix d'un agent executant issu de l'API.
- Lire la reponse Markdown et copier les blocs de code.
- Voir l'agent, le modele, la justification et le recours eventuel au routage de secours.
- Consulter les etapes et logs reels dans le panneau lateral.
- En mode projet, attendre `AwaitingApproval`, consulter les fichiers dans Projects,
  puis revenir au Workspace ou a Missions pour examiner le plan et **Autoriser**
  ou **Refuser**. Il n'y a plus de case d'accord avant generation dans React.
- Demander l'annulation d'une mission active ou en attente.

L'historique est persiste par le serveur. Chaque mission est independante :
le modele ne recoit pas automatiquement le contexte des conversations precedentes.
Il n'y a pas de pourcentage de progression fictif.

### Agents / Models

Les agents et modeles ne sont pas codes en dur dans le frontend.
`canGenerateProjects` indique l'agent autorise pour le mode projet.
L'agent de routage n'est pas selectionnable comme executant.

Les etats de modele sont distincts :

- **Installe** : observe via `/api/tags` d'Ollama ;
- **Charge** : observe via `/api/ps` ;
- **Utilise** : appel `/api/chat` actif dans cette instance de la plateforme.

Une telemetrie indisponible est affichee comme **inconnue**, pas comme un modele
absent. Les autres applications utilisant Ollama ne sont pas identifiees comme
appels actifs de Studio.

### Projects / Missions

Projects liste les workspaces persistants, leurs fichiers et les logs de verification.
Les fichiers sont affiches comme du texte, jamais comme une application executable.
Missions presente les etats, etapes, durees mesurees, corrections effectuees,
tentatives de conteneur, erreurs/avertissements MSBuild et resultats des tests.
Un compteur absent est affiche comme non compte, jamais comme zero.

Exemple de mission :

> Cree une API REST .NET 10 avec CRUD utilisateurs et tests unitaires.

Mission MVP 3 reellement generee, autorisee puis verifiee dans Docker :

> Cree une bibliotheque .NET 10 avec une classe Calculator et des tests pour l'addition et la soustraction.

Le LLM peut produire du code incomplet ou errone. Meme un projet compile et teste
dans un conteneur doit etre relu avant utilisation. **Aucune garantie de reussite
automatique pour toutes les applications generees.**

## 3. API REST

Les trois endpoints du MVP 1 sont conserves :

| Methode | Endpoint | Resultat |
|---|---|---|
| GET | `/api/agents` | Agents configures et capacites |
| GET | `/api/models` | Modeles autorises, parametres de conversation, affectations |
| POST | `/api/agents/run` | Ancien mode synchrone, texte uniquement |

Studio ajoute :

| Methode | Endpoint | Resultat |
|---|---|---|
| POST | `/api/missions` | `202 Accepted`, mission et en-tete Location |
| GET | `/api/missions` | Historique persiste |
| GET | `/api/missions/{id}` | Etat complet pour polling |
| POST | `/api/missions/{id}/cancel` | Annulation : 202, 404 ou 409 si terminale |
| GET | `/api/missions/{id}/events?after=0` | Evenements ordonnes par sequence |
| GET | `/api/missions/{id}/verification` | Logs, codes de sortie et nombre de tests si disponible |
| GET | `/api/missions/{id}/execution-plan` | Plan, image, limites et empreinte des fichiers/configuration ; 409 hors attente d'accord |
| POST | `/api/missions/{id}/approval` | `{ "approved": true/false, "fingerprint": "..." }` ; 202, 404 ou 409 |
| GET | `/api/workspaces` | Missions ayant un workspace |
| GET | `/api/workspaces/{id}/files` | Inventaire des fichiers |
| GET | `/api/workspaces/{id}/file?path=src%2FApp%2FProgram.cs` | Contenu texte valide |
| GET | `/api/models/local` | Disponibilite de la telemetrie et etats locaux |
| GET | `/api/sandbox` | Disponibilite de l'isolation et etat distinct `engineAvailable` du moteur Docker Linux |
| GET | `/api/health` | Disponibilite et PID du processus API ; protege dans Desktop |

Les exemples sont dans [AIPlatform.API.http](AIPlatform.API/AIPlatform.API.http).

```powershell
$body = @{
    task = "Cree une API REST .NET 10 CRUD utilisateurs avec tests xUnit."
    kind = "Project"                    # Chat ou Project
    agentId = $null                     # automatique ; ou identifiant autorise
    approveSandboxExecution = $false   # accord apres generation via /approval
} | ConvertTo-Json

$mission = Invoke-RestMethod http://localhost:5167/api/missions `
    -Method Post -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes($body))

Invoke-RestMethod "http://localhost:5167/api/missions/$($mission.id)"
Invoke-RestMethod "http://localhost:5167/api/missions/$($mission.id)/events?after=0"
Invoke-RestMethod "http://localhost:5167/api/workspaces/$($mission.id)/files"
```

Apres `AwaitingApproval`, **relire les fichiers et operations**, puis seulement :

```powershell
$plan = Invoke-RestMethod "http://localhost:5167/api/missions/$($mission.id)/execution-plan"
$plan | Format-List
# Decision humaine : true pour ce plan uniquement, false pour conserver les fichiers sans execution.
$decision = @{ approved = $true; fingerprint = $plan.fingerprint } | ConvertTo-Json
Invoke-WebRequest "http://localhost:5167/api/missions/$($mission.id)/approval" `
    -Method Post -ContentType 'application/json' -Body $decision
```

L'empreinte SHA-256 lie les fichiers et les parametres d'execution affiches.
Une empreinte obsolete ou une seconde decision renvoie 409. Avant la reprise,
les sources sont revalidees et l'empreinte est controlee a nouveau.
L'accord n'autorise ni commandes libres, ni reseau, ni acces general au PC.
L'ancien champ `approveSandboxExecution=true` reste compatible pour les clients
API donnant explicitement un accord borne par mission des sa soumission ;
React utilise exclusivement la decision apres generation.

Le retour initial ne bloque pas jusqu'a la fin du calcul. Le frontend interroge
les missions toutes les 2 secondes et les etats locaux toutes les 5 secondes.
Les requetes de polling ne se chevauchent pas avec leur propre execution precedente.
Leurs erreurs sont visibles et les donnees perimees sont signalees.

### Etats et erreurs

- `Queued`, `Running` : mission en attente ou en execution.
- `AwaitingApproval` : fichiers prets, aucune execution ; le worker et le verrou
  IA sont liberes pendant l'attente de l'utilisateur.
- `Completed` : conversation terminee (et anciens resultats MVP 2 conserves).
- `GeneratedUnverified` : fichiers generes, verification indisponible ou refusee ;
  les resultats precisent si une etape precedente avait deja ete tentee.
- `BuildFailed`, `TestsFailed` : verification echouee apres les corrections autorisees.
- `Verified` : build avec code 0 ET tests effectivement executes, code 0,
  rapport TRX valide, non vide et sans echec.
- `Failed` : erreur de generation, fournisseur, validation ou traitement.
- `Cancelled` : annulation utilisateur, arret du serveur ou interruption au redemarrage.

Les erreurs de requete sont au format Problem Details : `400` entree invalide,
`404` mission/fichier absent, `409` annulation trop tardive, `429` plafond de
missions atteint, `503` fournisseur indisponible, `504` timeout fournisseur
pour l'ancien endpoint synchrone. Les echecs asynchrones figurent dans la mission.
Une erreur de telemetrie ou de disponibilite Docker est un etat explicite de
consultation, pas un succes de compilation.

## 4. Architecture et persistance

- [Core](AIPlatform.Core) : registre, interfaces LLM, routeur JSON, reponse structuree,
  contrats de missions, verrou d'execution partage.
- [Infrastructure](AIPlatform.Infrastructure) : Ollama, stockage des missions,
  workspaces, generation de manifeste, orchestration des etapes et runner Docker.
- [API](AIPlatform.API) : endpoints minimal API, DTO, erreurs, DI et worker heberge.
- [Web](AIPlatform.Web) : React, TypeScript, Vite, Tailwind, Lucide, Markdown,
  tests de composants et navigateur.
- [Tests](AIPlatform.Tests) : xUnit/VSTest et doubles de fournisseurs/conteneurs.

Le routeur recoit un catalogue d'agents executants et un schema JSON enumere.
La reponse est validee strictement : identifiant inconnu, routeur comme executant,
champs supplementaires, doublons et justification vide sont rejetes.
Le LLM ne peut pas imposer un modele. L'affectation vient toujours du registre.
En secours, un score deterministe de mots-cles (sous-chaines, casse ignoree)
selectionne l'agent, puis `FallbackAgentId` si aucun mot-cle ne correspond.
Les egalites suivent l'ordre de configuration.

Le mode projet reste reserve a `Studio.DeveloperAgentId`, meme si le routage
general propose un autre agent ; cette restriction est ajoutee a la justification.
Le choix manuel est valide avant mise en file et ne sollicite pas le routeur.

### Donnees

En Development, `Studio.DataDirectory` vaut `..\StudioData` relativement au projet
API. En Desktop, les donnees sont dans `%LOCALAPPDATA%\AIPlatformStudio\StudioData`,
independamment du repertoire d'installation.

```text
StudioData\
  missions\<mission-id-sans-tirets>.json
  workspaces\<mission-id-sans-tirets>\
    Generated.slnx
    src\App\...
    tests\App.Tests\...
```

**Le stockage doit rester hors de tous les projets .NET sources.** Une validation
refuse un chemin dont un ancetre contient un `.csproj`. L'ancien emplacement
`AIPlatform.API\StudioData` est en plus exclu des items implicites du SDK.
Cela empeche les sources generees de participer a la compilation de la plateforme.
Les workspaces ne sont ni references dans la solution ni servis comme fichiers statiques.

Les metadonnees sont ecrites atomiquement et rechargees au demarrage.
Une mission interrompue devient `Cancelled` : elle n'est jamais relancee
automatiquement. Une mission `AwaitingApproval` reste en attente au redemarrage :
elle ne reprend qu'apres une decision explicite et un nouveau controle d'empreinte.
Les projets et historiques terminaux restent consultables.
Une seule instance de l'API doit utiliser ce stockage. Pas de base de donnees,
verrou inter-processus, chiffrement, suppression ou archivage automatique dans ce MVP.
Le plafond est de 200 missions conservees ; l'archivage se fait serveur arrete.
Les missions et logs peuvent contenir du contenu confidentiel : ne pas les committer.

## 5. Generation et outils autorises

Le modele retourne un manifeste JSON complet : `summary`, `files[{path,content}]`.
Il ne recoit pas d'outil shell. La plateforme :

1. route la mission et cree un workspace unique ;
2. demande le manifeste au modele autorise ;
3. valide TOUS les chemins et contenus avant ecriture ;
4. ecrit les fichiers et une solution `Generated.slnx` de confiance ;
5. verifie Docker et attend un accord humain sans immobiliser le worker ;
6. si autorise, execute build puis tests dans des conteneurs distincts ;
7. transmet les diagnostics reels au modele, qui retourne uniquement les fichiers
   a remplacer ; fusionne ces remplacements avec les fichiers inchanges,
   revalide le projet complet et utilise de NOUVEAUX conteneurs a chaque essai.

Le plafond `MaxCorrectionAttempts` accepte uniquement 0 a 3. Il couvre aussi les
corrections de manifestes invalides avant compilation. Les fichiers omis dans une
correction valide sont conserves ; aucune suppression n'est proposee par le LLM.
Le prompt exige de conserver les comportements et assertions ; cela ne remplace
pas une revue humaine des changements ni une specification fonctionnelle fiable.

Les chemins sont relatifs, avec `/` dans le manifeste. Ils sont limites aux
dossiers `src/App/`, `tests/App.Tests/` et au fichier `README.md`.
Sont refuses : `..`, chemins absolus, lecteurs/ADS Windows, noms reserves,
separateurs inverses, dossiers bin/obj, doublons insensibles a la casse,
extensions executables, fichiers binaires et depassements de taille.
Les liens symboliques/jonctions/reparse points sont refuses, y compris les ancetres.

Les projets doivent cibler `net10.0` et employer un SDK .NET standard.
Les cibles MSBuild personnalisees, imports, commandes, proprietes non autorisees
et references externes sont refuses. Le projet de tests doit referencer
l'application et les trois packages epingles presents dans l'image hors ligne.
Un manifeste invalide est corrige dans la limite configuree ou termine en echec.
Un JSON tronque, vide ou invalide n'est jamais transforme en succes.

Ces validations reduisent la surface d'attaque ; elles ne rendent PAS le code
genere fiable. Le C# des tests peut executer des operations arbitraires a
l'interieur du conteneur. L'isolation est donc obligatoire.
Le stockage doit rester prive a l'utilisateur du service : les controles de liens
ne constituent pas une protection contre un autre processus du meme utilisateur
qui modifierait volontairement les dossiers pendant une operation.

## 6. Sandbox Docker - preparation controlee et execution hors ligne

**Validee sur le moteur Docker reel**, et non seulement avec des doubles.
Les tests opt-in inspectent les conteneurs effectifs et executent des assertions
sur les permissions, le reseau et la racine en lecture seule.

Une image standard du SDK ne contient pas les packages xUnit. Le reseau d'execution
etant desactive, une image preparee hors ligne est fournie dans [sandbox](sandbox).
Le SDK officiel est fixe par le digest Linux/amd64 suivant dans le Dockerfile :
`mcr.microsoft.com/dotnet/sdk:10.0@sha256:ddbaedfc8b774389d91a511af08b77296f2c7a8d15d86f6214d2a87166933625`.

Apres autorisation humaine et revue des seuls fichiers de confiance de `sandbox` :

```powershell
# Operation manuelle avec reseau : telecharge le SDK et les packages publics epingles.
# Ne jamais utiliser un workspace genere comme contexte de cette construction.
docker build --tag aiplatform-sandbox:net10 .\sandbox
docker image inspect aiplatform-sandbox:net10 --format '{{.Id}}'
```

Ensuite :

1. Renseigner `Sandbox.Image` avec l'identifiant immuable renvoye par inspect, ou
   la reference `nom@sha256:...` de `RepoDigests`. Ne pas confondre le digest du
   manifeste avec le digest de configuration affiche dans les logs de build.
2. `Sandbox.Enabled` est actif dans la configuration de cette machine validee.
   Sur une autre machine, preparer l'image et remplacer son identifiant ; une
   image manquante produit un etat indisponible, sans pull ni execution de repli.
3. Redemarrer l'API, verifier `GET /api/sandbox`, creer une mission, puis
   relire ses fichiers et autoriser son plan dans Studio.
4. Les anciennes missions terminales ne sont jamais relancees automatiquement.

L'image locale validee est :
`aiplatform-sandbox@sha256:232a19ba3edf91e03de6c2804cc895dd07bf3585c226316b497c1ffdd3025515`.
Une reconstruction locale peut produire un identifiant different.
Si `docker` n'est pas dans PATH sous Windows, le runner recherche aussi le chemin
standard de Docker Desktop sous Program Files.

La restauration reseau est limitee a cette preparation de confiance, jamais a
un projet genere. Les trois packages de tests epingles et leurs dependances sont
mis en cache sous `/opt/nuget` ; l'execution force `RestoreSources=/opt/empty-feed`
et `NuGetAudit=false`. Une dependance non disponible doit rester en erreur :
une nouvelle preparation controlee et autorisee est necessaire, pas une ouverture
reseau du conteneur de mission.

Les commandes sont fixes : `dotnet build Generated.slnx`, puis
`dotnet test tests/App.Tests/App.Tests.csproj`, avec des options controlees.
Le LLM ne fournit ni executable, ni commande, ni argument de processus.
Chaque etape copie les sources dans un espace temporaire du conteneur :
les artefacts de compilation ne sont pas ecrits sur le PC hote.
L'etape Tests reconstruit donc le projet dans son propre conteneur.

Protections du runner :

- `--pull=never`, aucun telechargement automatique ;
- reseau `none`, aucun port publie ni socket Docker monte ;
- racine en lecture seule, toutes les capabilities retirees, `no-new-privileges` ;
- utilisateur non root `10001:10001` ;
- seul le workspace de la mission est monte, en lecture seule ;
- pas de dossier utilisateur, secret, configuration NuGet personnelle ou depot entier monte ;
- CPU, RAM, swap, nombre de processus, descripteurs, espace temporaire et temps limites ;
- annulation/timeout : arret du client puis `docker rm --force` du conteneur nomme ;
- si le nettoyage ne peut pas etre confirme, sandbox bloquee jusqu'a inspection
  des conteneurs `aiplatform-*` et redemarrage du serveur ;
- les tests ne sont reussis que si la commande retourne 0 ET un TRX non vide
  contient des tests tous passes. Une commande sans tests n'est pas un succes.

Les journaux sont decodes explicitement en UTF-8, y compris le BOM du TRX sous
Windows. Le collecteur fixe encadre le fichier TRX apres la sortie de dotnet test ;
un texte ressemblant a un TRX hors de ce cadre n'est pas utilise comme preuve.
Les journaux sont plafonnes a 64 K caracteres par commande (sortie de tests a
24 000 octets avant le rapport, troncature signalee). Un rapport trop long
pour etre valide n'est pas accepte comme succes. Les tests generes ne prouvent
pas, a eux seuls, la correction fonctionnelle de l'application.
Docker partage le noyau de sa VM ; pour du code deliberement hostile, prevoir une
isolation plus forte et une revue de l'environnement avant activation.
Ne pas activer plusieurs instances ou partager le moteur avec des operations sensibles.
Le SDK peut emettre un message de verification de workloads dans cette racine en
lecture seule ; il reste visible dans les logs. Aucune commande workload update
n'est lancee pour le masquer.

## 7. Configuration et ressources locales

Toutes les options sont dans [appsettings.json](AIPlatform.API/appsettings.json).
Redemarrer l'API apres modification.

| Parametre | Defaut | Effet |
|---|---|---|
| `Platform.MaxConcurrentMissions` | 1 | Impose pour Studio ; ancien endpoint et worker partagent le verrou |
| `Platform.MaxTaskLength` | 16000 | Caracteres maximum d'une mission |
| `Studio.MaxMissions` | 200 | Missions conservees, terminales comprises |
| `Studio.MaxCorrectionAttempts` | 3 | Plafond strict 0..3, generation initiale exclue |
| `Studio.MissionTimeoutSeconds` | 900 | Delai par prise en charge active, attente du verrou incluse ; attente humaine exclue, nouveau delai a la reprise |
| `Studio.GenerationContextSize` | 8192 | Contexte du modele pour les projets |
| `Studio.GenerationMaxOutputTokens` | 6000 | Plafond de sortie du manifeste |
| `Studio.MaxFiles` | 30 | Fichiers proposes, plus la solution creee par le serveur |
| `Studio.MaxFileBytes` | 65536 | Limite UTF-8 par fichier |
| `Studio.MaxProjectBytes` | 262144 | Limite UTF-8 totale du manifeste |
| `Models[].ContextSize` | 4096 | Contexte de conversation et routage |
| `Models[].MaxOutputTokens` | 1024 / 1536 | Sortie conversation et routage |
| `Ollama.RequestTimeoutSeconds` | 180 | Delai par appel fournisseur |
| `Ollama.KeepAliveSeconds` | 120 | Conservation du modele apres appel |
| `Ollama.UnloadPreviousModel` | true | Dechargement avant changement de modele |
| `Sandbox.Enabled` | true ici | Exige toujours l'image locale exacte et un accord par mission ; false desactive toute execution |
| `Sandbox.TimeoutSeconds` | 120 | Delai par etape conteneur |
| `Sandbox.MemoryMegabytes` | 2048 | RAM et plafond RAM+swap identiques |
| `Sandbox.CpuCount` | 2 | Quota CPU du conteneur |

Un modele identique est reutilise. Les appels Ollama sont toujours serialises.
Le premier appel ne decharge pas les modeles charges par d'autres applications.
Conserver une seule instance et surveiller `ollama ps`. Si necessaire, configurer
manuellement le service Ollama avec `OLLAMA_MAX_LOADED_MODELS=1` et
`OLLAMA_NUM_PARALLEL=1` puis le redemarrer.

Les limites ne sont pas globales a toutes les applications du PC. Les 8 Go de
VRAM ne garantissent pas la residence GPU pour n'importe quel contexte.
Les prompts, anciens fichiers, diagnostics et sortie partagent le contexte du modele.
Une grosse mission peut donc depasser les budgets et echouer, sans etre presentee comme reussie.

Pour ajouter un agent : ajouter le modele autorise si besoin, puis l'entree
`Platform.Agents` et redemarrer. Le routeur et le frontend utilisent le catalogue.
Pour un fournisseur cloud futur : implementer `ILLMProvider`, l'enregistrer dans
la DI et adapter la validation des fournisseurs. Aucun fournisseur cloud n'est
appele actuellement. Aucun nom de modele n'est code en dur dans la logique metier.

## 8. Tests

```powershell
dotnet build .\AIPlatform.slnx
dotnet test .\AIPlatform.slnx --no-build

Set-Location .\AIPlatform.Web
npm ci
npm run build
npm test
npm run test:e2e
npm audit
```

Verification du compagnon **reellement publie**, application Desktop fermee :

```powershell
npm run desktop:prepare
npm run desktop:test-backend
cargo test --manifest-path .\src-tauri\Cargo.toml --release --target x86_64-pc-windows-msvc
```

Le test du compagnon verifie le port dynamique avec 5167 occupe, l'ignorance des
endpoints reseau herites, les refus 401/403, le preflight CORS, la rotation du
jeton, la detection reelle des dependances et l'arret code 0 par commande puis
par fermeture du canal proprietaire. Il ne soumet aucune mission et n'execute
aucun projet genere. Les tests React couvrent l'attente de disponibilite, les
erreurs natives et l'authentification des lectures, annulations et autorisations.

Les tests .NET ordinaires et React n'utilisent pas de modele reel. Les parcours E2E standard
simulent l'API et verifient les cinq ecrans en desktop et tablette.
Playwright utilise Microsoft Edge installe, sans telecharger de navigateur.
Sur une machine sans Edge, adapter `channel` ou installer manuellement un navigateur de test.

Tests d'integration OPTIONNELS avec l'API, le serveur Vite et Ollama reels deja demarres :

```powershell
npm run test:live
```

Ils creent des missions et workspaces persistants, consomment le GPU local et
verifient conversation, routage, generation, consultation des fichiers et absence
de compilation sans accord. Ils ne compilent jamais le code genere sur l'hote.

### Tests Docker reels (opt-in, apres revue et autorisation)

Depuis la racine, executer d'abord les quatre essais deterministes, sans modele :

```powershell
$env:AIPLATFORM_TEST_DOCKER = '1'
$env:AIPLATFORM_TEST_IMAGE = docker image inspect aiplatform-sandbox:net10 --format '{{.Id}}'
dotnet test .\AIPlatform.Tests --filter 'Category=DockerIntegration&Category!=OllamaIntegration'
```

Ces tests construisent Calculator avec trois tests (addition, soustraction,
isolation), provoquent une erreur compilee puis une correction deterministe,
un echec d'assertion, une annulation et un timeout. Ils inspectent les limites
reelles et verifient la disparition de chaque conteneur nomme.

L'essai suivant utilise **vraiment Qwen2.5-Coder** pour corriger une erreur de
compilation initiale volontaire, puis compile et teste dans Docker en conservant
les tests existants. Il necessite une autorisation distincte et Ollama disponible :

```powershell
$env:AIPLATFORM_TEST_OLLAMA = '1'
dotnet test .\AIPlatform.Tests --filter 'Category=OllamaIntegration'
Remove-Item Env:\AIPLATFORM_TEST_DOCKER, Env:\AIPLATFORM_TEST_IMAGE, Env:\AIPLATFORM_TEST_OLLAMA
```

Sans ces indicateurs, les cinq tests reels sont **ignores**, pas declares reussis.
Les workspaces temporaires de tests sont supprimes ; ceux des missions Studio
restent persistants. Aucun de ces tests ne lance un projet genere sur Windows.

Pour verifier dans le navigateur une mission Calculator deja autorisee et verifiee
(lecture seule ; ce test ne donne jamais l'autorisation) :

```powershell
Set-Location .\AIPlatform.Web
$env:AIPLATFORM_VERIFIED_MISSION_ID = '6e571b92-1501-4176-80c3-33b7ece857d1'
npx playwright test --config playwright.live.config.ts --grep 'approved Calculator'
Remove-Item Env:\AIPLATFORM_VERIFIED_MISSION_ID
```

### Compte rendu de validation du MVP 4

Validation effectuee sur cette installation Windows, avec autorisation de
l'utilisateur pour l'installation et les deux missions de generation :

| Verification executee | Resultat |
|---|---|
| Compilation complete .NET 10 | 0 erreur, 0 avertissement |
| Suite backend ordinaire | 148 reussis ; 5 integrations Docker/Ollama explicitement ignorees, non reexecutees pour le MVP 4 |
| Build TypeScript/Vite | Reussi |
| Vitest / Testing Library | 20 reussis, dont transport Desktop, attente de disponibilite, erreurs natives et confirmation de redemarrage |
| Playwright avec API simulee | 6 reussis, desktop et tablette |
| Test Rust de disponibilite | Reussi : adresse locale et PID possede obligatoires, adresses et processus etrangers rejetes |
| Compagnon autonome reel publie, deux executions successives | Reussi : port 5167 occupe, endpoints herites ignores, jeton invalide/ancien refuse, origine externe refusee, preflight Tauri accepte |
| Arret du compagnon publie | Code 0 par commande privee puis par fermeture du stdin proprietaire |
| Detection reelle des dependances | Ollama disponible, moteur Docker Linux disponible, image hors ligne prete |
| Installateur NSIS x64 | Produit, environ 33,3 Mio ; installation puis reinstallation dans le profil courant |
| Fenetre native et ressources de production | Demarrage sans terminal ni Vite, API authentifiee, ecran Models et explorateur de fichiers fonctionnels |
| Socket du backend installe | Un seul listener observe, uniquement 127.0.0.1 sur port dynamique |
| Redemarrage depuis React | Ancien PID arrete, nouveau compagnon pret, historique et projet conserves |
| Fermeture et relancement/reinstallation | Arret des deux processus propres a Studio ; projet et attente d'autorisation conserves |
| Ollama/Docker apres fermeture | Services toujours disponibles ; aucun conteneur aiplatform restant |
| Audit npm de production | 0 vulnerabilite signalee lors du controle |

La premiere mission Calculator a ete refusee parce que le modele ne respectait
pas `net10.0`, apres les trois corrections autorisees. Ce resultat est affiche
comme un echec, sans execution.

Un second essai, explicitant `TargetFramework=net10.0`, les chemins et les versions
autorisees, a produit les cinq fichiers attendus sans correction. Mission
`09873107-b3f5-4c17-95ab-cab47e1e3a6f` : **AwaitingApproval**, sources et deux tests
consultables, aucune compilation ni execution effectuee dans cette validation
Desktop. Ce projet n'est donc PAS declare `Verified`.

La validation native a utilise WebView2 et les outils deja presents sur cette
machine ; une installation sur une machine Windows vierge n'a pas ete testee.
Le bootstrap WebView2 absent, l'absence reelle des services externes et la
signature de distribution ne sont pas annonces comme valides sur une seconde machine.

Principaux fichiers du MVP 4 :

- [Program.cs](AIPlatform.API/Program.cs),
  [DesktopSession.cs](AIPlatform.API/Services/DesktopSession.cs) et
  [DesktopLifetime.cs](AIPlatform.API/Services/DesktopLifetime.cs) : profil prive,
  authentification, disponibilite et cycle de vie.
- [appsettings.Desktop.json](AIPlatform.API/appsettings.Desktop.json) :
  configuration de production portable, distincte du developpement.
- [main.rs](AIPlatform.Web/src-tauri/src/main.rs) et
  [tauri.conf.json](AIPlatform.Web/src-tauri/tauri.conf.json) :
  lanceur natif, permissions minimales, ressources et installateur.
- [DesktopApp.tsx](AIPlatform.Web/src/desktop/DesktopApp.tsx),
  [transport.ts](AIPlatform.Web/src/desktop/transport.ts) et
  [App.tsx](AIPlatform.Web/src/App.tsx) : attente du backend, requetes protegees,
  services et redemarrage confirme.
- [prepare-backend.mjs](AIPlatform.Web/scripts/prepare-backend.mjs),
  [test-backend.mjs](AIPlatform.Web/scripts/test-backend.mjs) et
  [DesktopSessionTests.cs](AIPlatform.Tests/DesktopSessionTests.cs) :
  publication avec liste blanche, validation du processus reel et tests d'acces.

### Compte rendu de validation du MVP 3

| Verification executee | Resultat |
|---|---|
| Compilation complete .NET 10 finale | 0 erreur, 0 avertissement |
| Suite backend avec les deux indicateurs d'integration actives | 140 reussis, dont 135 ordinaires et 5 reels Docker/Ollama |
| Suite backend sans indicateurs | 135 reussis, 5 integrations explicitement ignorees |
| Build TypeScript/Vite et tests React | Reussis ; 13 tests de composants passes |
| Playwright avec API simulee | 6 reussis : navigation, autorisation et refus, desktop/tablette |
| Playwright sur la mission Calculator reelle deja autorisee | 1 reussi : statut Verified, vrais resultats et fichiers |
| Redemarrage API | Mission Verified, accord, fichiers et resultats conserves sans reexecution |
| Image officielle fixee par digest et cache NuGet prepare avec accord humain | Reussi, execution ensuite sans reseau |
| Quatre tests deterministes sur Docker reel | Reussis : build, tests, echec compile et correction, assertion echouee, annulation/timeout, nettoyage |
| Inspection des conteneurs effectifs | 2 CPU, 2 Gio RAM/swap, PID 128, UID 10001, reseau none, racine et montage sources read-only, cap-drop ALL, no-new-privileges |
| Assertions dans le conteneur | Interface lo uniquement, UID/capabilities/no-new-privileges verifies, ecriture racine et sources refusee, /tmp accessible |
| Correction par le vrai qwen2.5-coder:7b sur erreur compilee volontaire | Reussie ; vrais build et trois tests passes, tests inchanges |
| Mission Calculator exacte via React, API et Ollama reels | `Verified` apres accord humain ; deux tests arithmetiques passes |
| Generation de cette mission | Une correction de manifeste net10.0 avant autorisation ; aucun conteneur avant accord |
| Compilation/tests de cette mission | Codes 0 ; environ 6,7 s / 5,6 s, logs et rapport TRX conserves |
| Modeles Ollama | Les deux modeles existants uniquement ; aucun ajout ou changement |

`Verified` ne prouve ni l'absence de vulnerabilites, ni la qualite des exigences,
ni la correction de comportements non testes. Le code et les tests generes
restent a relire ; Docker n'est pas une frontiere de securite absolue.

### Compte rendu de validation du MVP 2

| Verification executee | Resultat |
|---|---|
| Compilation complete .NET 10 | 0 erreur, 0 avertissement |
| Tests backend, dont les 63 tests existants | 124 reussis |
| Installation propre `npm ci` et build TypeScript/Vite | Reussis |
| Tests React avec Vitest / Testing Library | 9 reussis |
| Parcours Playwright avec API simulee | 2 reussis : desktop et tablette |
| Parcours Playwright finaux avec API et Ollama reels | 2 reussis : conversation et generation de projet |
| Annulation reelle d'une mission avec agent manuel | Confirmee, etapes actives fermees |
| Historique et workspaces apres migration/redemarrage | Recharges et consultables |
| Sources de workspaces absentes des items Compile de l'API | Verifie apres generation reelle |
| Modeles Ollama installes | Noms et digests inchanges |
| `npm audit` | 0 vulnerabilite signalee lors de la validation |
| Docker, build et tests du code genere en conteneur reel | NON executes : Docker absent |

Lors de la validation historique du MVP 2, les projets restaient volontairement
`GeneratedUnverified` et seuls des doubles validaient le runner. Le MVP 3
ci-dessus a ensuite valide le moteur et les executions reels.
Les serveurs de validation temporaires sont arretes apres les essais.

## 9. Limites de securite et de perimetre

- Usage local uniquement : aucune publication Internet/Azure. Desktop impose une
  session authentifiee ; seul le profil Development conserve l'acces sans jeton.
- API accessible uniquement depuis la boucle locale ; origines Desktop limitees,
  origines de mutation non locales refusees en Development.
- URL Ollama validee loopback, proxies et redirections desactives.
- Pas d'outil filesystem arbitraire, de commande proposee par le LLM ou de modification d'un projet utilisateur existant.
- Pas de preview executable des applications generees, de deploiement ni d'installation automatique.
- Markdown sans HTML brut ; images externes bloquees pour eviter des chargements non souhaites.
- Annulation propagee jusqu'a HttpClient ; l'arret GPU effectif depend aussi d'Ollama.
- Pas de streaming des tokens : suivi d'etapes par polling et reponse finale.
- Pas de garantie de qualite semantique du routage, du code ou de pertinence des tests generes.
