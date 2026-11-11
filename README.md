# AIPlatform Studio - MVP 2

Plateforme multi-agents locale .NET 10 + React/TypeScript. Ollama selectionne un
agent autorise et son modele configure. Studio permet de suivre des missions
asynchrones et de generer les fichiers d'un petit projet .NET 10.

**Le code genere est non fiable. Aucun code genere ne doit etre execute sur l'hote.**
Sans Docker Linux disponible, image hors ligne preparee, activation explicite de
la sandbox ET accord humain pour la mission, la compilation et les tests ne sont
pas executes. Le statut final est alors `GeneratedUnverified`, jamais un succes
de compilation.

## 1. Prerequis et demarrage

Environnement verifie : Windows, SDK .NET 10, Node.js 20.14, npm 10.8,
Ollama sur `http://localhost:11434`, modeles deja installes :

- `qwen3:4b` : routage et conversation generale ;
- `qwen2.5-coder:7b` : developpement et generation de projets.

Docker n'est pas installe dans l'environnement de developpement actuel.
Il n'est pas necessaire pour generer et consulter les fichiers.
La plateforme n'installe ni Docker ni modele Ollama, et ne telecharge aucune image.

Depuis `C:\Users\utilisateur\Downloads\AIPlatform`, terminal API :

```powershell
dotnet build .\AIPlatform.slnx
dotnet test .\AIPlatform.slnx --no-build
dotnet run --project .\AIPlatform.API --launch-profile http
```

Dans un second terminal :

```powershell
Set-Location C:\Users\utilisateur\Downloads\AIPlatform\AIPlatform.Web
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
Missions presente les etats, etapes, tentatives de correction et erreurs.

Exemple de mission :

> Cree une API REST .NET 10 avec CRUD utilisateurs et tests unitaires.

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
| GET | `/api/workspaces` | Missions ayant un workspace |
| GET | `/api/workspaces/{id}/files` | Inventaire des fichiers |
| GET | `/api/workspaces/{id}/file?path=src%2FApp%2FProgram.cs` | Contenu texte valide |
| GET | `/api/models/local` | Disponibilite de la telemetrie et etats locaux |
| GET | `/api/sandbox` | Disponibilite reelle de l'isolation |

Les exemples sont dans [AIPlatform.API.http](AIPlatform.API/AIPlatform.API.http).

```powershell
$body = @{
    task = "Cree une API REST .NET 10 CRUD utilisateurs avec tests xUnit."
    kind = "Project"                    # Chat ou Project
    agentId = $null                     # automatique ; ou identifiant autorise
    approveSandboxExecution = $false   # accord explicite requis pour toute execution
} | ConvertTo-Json

$mission = Invoke-RestMethod http://localhost:5167/api/missions `
    -Method Post -ContentType 'application/json; charset=utf-8' `
    -Body ([Text.Encoding]::UTF8.GetBytes($body))

Invoke-RestMethod "http://localhost:5167/api/missions/$($mission.id)"
Invoke-RestMethod "http://localhost:5167/api/missions/$($mission.id)/events?after=0"
Invoke-RestMethod "http://localhost:5167/api/workspaces/$($mission.id)/files"
```

Le retour initial ne bloque pas jusqu'a la fin du calcul. Le frontend interroge
les missions toutes les 2 secondes et les etats locaux toutes les 5 secondes.
Les requetes de polling ne se chevauchent pas avec leur propre execution precedente.
Leurs erreurs sont visibles et les donnees perimees sont signalees.

### Etats et erreurs

- `Queued`, `Running` : mission en attente ou en execution.
- `Completed` : conversation terminee, ou projet ayant effectivement passe build et tests en conteneur.
- `GeneratedUnverified` : fichiers generes, compilation/tests non executes.
- `Failed` : erreur fournisseur, limites atteintes, echec de verification ou de traitement.
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

Par defaut, `Studio.DataDirectory` vaut `..\StudioData` relativement au projet API,
donc `C:\Users\utilisateur\Downloads\AIPlatform\StudioData`.

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
automatiquement. Les projets et historiques terminaux restent consultables.
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
5. verifie la disponibilite de Docker et l'accord de la mission ;
6. si autorise, execute build puis tests dans des conteneurs distincts ;
7. transmet les diagnostics au modele et regenere le manifeste en cas d'echec,
   jusqu'au plafond de corrections configure.

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

## 6. Sandbox Docker - activation manuelle uniquement

**Non validee sur un Docker reel ici, car Docker est absent.** La construction
de l'image et le chemin reel de build/tests restent a valider sur une machine
equipee de Docker Desktop avec moteur Linux. Les tests automatises verifient les
arguments, limites, erreurs, annulations, nettoyage et resultats a l'aide de doubles.

Une image standard du SDK ne contient pas les packages xUnit. Le reseau d'execution
etant desactive, une image preparee hors ligne est fournie dans [sandbox](sandbox).
Apres installation manuelle de Docker et revue des fichiers de confiance :

```powershell
# Operation manuelle avec reseau : telecharge le SDK et les packages publics epingles.
# Ne jamais utiliser un workspace genere comme contexte de cette construction.
docker build --tag aiplatform-sandbox:net10 .\sandbox
```

Ensuite :

1. Activer `Sandbox.Enabled` dans [appsettings.json](AIPlatform.API/appsettings.json).
2. Redemarrer l'API et verifier `GET /api/sandbox`.
3. Relire la mission et cocher l'accord de compilation/tests dans Studio.
4. Envoyer une NOUVELLE mission. Les anciennes ne sont pas relancees.

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

Les journaux sont plafonnes a 64 K caracteres par commande. Un rapport trop long
pour etre valide n'est pas accepte comme succes. Les tests generes ne prouvent
pas, a eux seuls, la correction fonctionnelle de l'application.
Docker partage le noyau de sa VM ; pour du code deliberement hostile, prevoir une
isolation plus forte et une revue de l'environnement avant activation.
Ne pas activer plusieurs instances ou partager le moteur avec des operations sensibles.
Pour une distribution controlee, epingler aussi l'image SDK par digest verifie.

## 7. Configuration et ressources locales

Toutes les options sont dans [appsettings.json](AIPlatform.API/appsettings.json).
Redemarrer l'API apres modification.

| Parametre | Defaut | Effet |
|---|---|---|
| `Platform.MaxConcurrentMissions` | 1 | Impose pour Studio ; ancien endpoint et worker partagent le verrou |
| `Platform.MaxTaskLength` | 16000 | Caracteres maximum d'une mission |
| `Studio.MaxMissions` | 200 | Missions conservees, terminales comprises |
| `Studio.MaxCorrectionAttempts` | 2 | Deux corrections maximum apres la generation initiale |
| `Studio.MissionTimeoutSeconds` | 900 | Delai total une fois la mission prise par le worker, attente du verrou incluse |
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
| `Sandbox.Enabled` | false | Aucune execution par defaut |
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

Les tests .NET et React n'utilisent pas de modele reel. Les parcours E2E standard
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

Les derniers projets generes restent volontairement `GeneratedUnverified`.
Les tests de correction, de nettoyage Docker et d'indisponibilite fournisseur
sont automatises avec doubles ; ils ne sont pas presentes comme des executions
reelles de conteneurs. Les serveurs de validation temporaires ont ete arretes.

## 9. Limites de securite et de perimetre

- Usage local uniquement, sans authentification : aucune publication Internet/Azure.
- API accessible uniquement depuis la boucle locale ; origines de mutation non locales refusees.
- URL Ollama validee loopback, proxies et redirections desactives.
- Pas d'outil filesystem arbitraire, de commande proposee par le LLM ou de modification d'un projet utilisateur existant.
- Pas de preview executable des applications generees, de deploiement ni d'installation automatique.
- Markdown sans HTML brut ; images externes bloquees pour eviter des chargements non souhaites.
- Annulation propagee jusqu'a HttpClient ; l'arret GPU effectif depend aussi d'Ollama.
- Pas de streaming des tokens : suivi d'etapes par polling et reponse finale.
- Pas de garantie de qualite semantique du routage, du code ou de pertinence des tests generes.
