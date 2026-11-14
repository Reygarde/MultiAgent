# AIPlatform Studio - 0.6.5

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

## Avancement MVP 6 (incomplet)

### Live Preview (en cours, indisponible)

Le verificateur de sources Preview compare une revision full-stack Verified,
son plan canonique et les fichiers du workspace, sans les executer. Il ne
prepare pas de session et ne constitue pas une autorisation Preview.
Le statut Verified et l'autorisation Docker de validation ne permettent pas
de demarrer une application.

Un modele de plan canonique, un registre d'autorisations en memoire et un
coordinateur relient le verificateur de sources a un adaptateur de Preview.
Le coordinateur revalide le plan avant de consommer une autorisation, capture
les octets verifies dans un instantane immutable et attend deux controles de
disponibilite avant `Ready`. Son parcours et son nettoyage sont testes avec
un adaptateur simule uniquement. Les autorisations ne survivent pas a un
redemarrage ; le coordinateur refuse le demarrage tant que la recuperation
des ressources orphelines n'a pas ete confirmee par l'adaptateur.

L'API Desktop authentifiee expose la preparation, le plan, l'autorisation, le
demarrage, l'etat, les evenements de cycle de vie et l'arret. La maintenance
demande la recuperation des ressources au demarrage et verifie l'expiration
chaque seconde. Une seule session peut etre active a la fois. Les evenements
sont limites a 64 messages predefinis ; ils ne contiennent pas les journaux
de l'application generee.

`DockerPreviewAdapter` est maintenant enregistre comme `IPreviewRuntime`.
Son moteur interne resout l'image Linux amd64 par ID immuable, copie un
instantane verifie dans un staging controle puis en verifie encore les hash
dans le conteneur avant compilation. Il cree et demarre un conteneur non root,
racine read-only, sans capacites, no-new-privileges, avec limites CPU/RAM/PID,
sources montees read-only et tmpfs `/work` et `/tmp`. Les deux services ecoulent
leur trafic uniquement sur le loopback du conteneur (`--network=none`) ;
SQLite reste dans `/tmp/preview.db`. Aucun port hote, reseau Docker dedie ou
volume persistant n'est cree. Des controles HTTP via `docker exec` et un
controle d'etat du conteneur precedent la reussite du demarrage interne.
L'arret confirme l'absence du conteneur avant suppression du staging ; la
recuperation cible les labels propres au magasin Studio et refuse les noms
inattendus. Un echec de nettoyage verrouille les demarrages internes jusqu'a
recuperation confirmee.

`PreviewProxy` et `DockerPreviewHttpTransport` sont enregistres en DI.
Le proxy Kestrel ouvre une origine HTTP temporaire sur `127.0.0.1`, uniquement
apres les controles de disponibilite du conteneur. Chaque requete exige un
jeton aleatoire propre a la session dans `X-Preview-Token`. Aucun cookie,
jeton Studio, Host fourni ou en-tete d'authentification n'est transmis.
Les origines et Host entrants sont verifies ; les en-tetes de forwarding et
WebSocket sont refuses. Les routes sont limitees au frontend (`/` et assets
approuves) et au CRUD `/api/tasks`. Aucune URL de destination n'est acceptee.
Le relais utilise un programme Node fixe via `docker exec`, avec un payload
JSON encode en base64 comme argument, sans shell derive de la requete.
Les seules destinations sont les ports 5080/5081 du loopback du conteneur.
Les reponses 3xx sont toutes refusees, ainsi que les types MIME inattendus.
Limites : corps entrant 16 KiB, reponse 1 MiB, delai 10 secondes et un relais
actif par session. Les WebSockets, SSE et streaming ne sont pas pris en charge.
Ces parametres et l'exposition loopback ephemere sont lies au plan canonique.
L'arret ferme et annule le proxy avant de retirer le conteneur ; l'expiration
refuse les requetes et la maintenance de session demande l'arret.

### Navigateur dedie MVP 6.5-C : implementation concrete, activation bloquee

[ControlledPreviewBrowser](AIPlatform.Infrastructure/Studio/ControlledPreviewBrowser.cs)
implemente `IControlledPreviewBrowser` et est enregistre en DI. Il lance un
processus Node distinct avec un helper fixe, une entree JSON sur stdin,
un message de readiness borne, un environnement sans les variables Studio,
un profil temporaire par session et une fermeture gracieuse puis forcee si
necessaire. Les erreurs du helper sont signalees sans recopier les sorties brutes.
Les profils orphelins ne sont jamais declares nettoyes ni supprimes a l'aveugle ;
leur presence bloque la recuperation. Aucun processus tiers n'est tue.

Le helper [preview-browser.mjs](AIPlatform.Web/scripts/preview-browser.mjs)
utilise Edge installe et Playwright, sans WebView ni API Tauri. L'interception
HTTP ne laisse passer que l'origine Preview approuvee. Le sandbox Chromium
est explicitement active, sans le `--no-sandbox` par defaut de Playwright.
Le helper relaie ces
requetes lui-meme, avec le jeton en memoire : le renderer ne connait ni le
jeton ni les cookies Studio. Les cookies, Authorization et Set-Cookie sont
supprimes. Les requetes non interceptees rencontrent un proxy de refus ;
les WebSockets sont refuses, les service workers bloques, les popups fermes,
les redirections refusees et les telechargements denies par CDP.
Les corps sont limites a 16 KiB, les reponses a 1 MiB, le relais a 10 secondes ;
une file bornee de huit requetes serialise les appels vers le proxy.

L'adaptateur ouvre le navigateur apres le proxy et les health checks, seulement
dans le parcours de demarrage autorise. Un echec ferme le proxy et supprime le
conteneur, meme si le nettoyage du navigateur echoue. La maintenance detecte
une sortie du helper et ferme la session au prochain passage (environ une seconde).
L'expiration ferme aussi le navigateur ; un profil restant est un echec explicite,
jamais un arret propre. Les parametres du navigateur sont lies au plan canonique.

**Blocage de securite effectivement observe :** les tests Edge headless avec
de faux services locaux ont observe des paquets UDP WebRTC vers un faux serveur
STUN, malgre `--force-webrtc-ip-handling-policy=disable_non_proxied_udp`.
Le test attend toujours zero paquet et reste volontairement en echec.
Les autres assertions ont verifie le refus HTTP vers Studio factice, localhost,
127.0.0.2, ::1, prive/link-local/externe, les navigations file/custom et les
redirections, fetch/XHR, images/CSS, iframe/worker/popup, les limites et timeouts.
Les faux services HTTP n'ont recu aucune requete interdite. Les tests de deux
sessions et d'expiration/profil reussissent. Cela ne certifie pas tous les
protocol handlers, workers blob, sorties DNS, acces disque ou contournements
du moteur : une interception HTTP n'est pas une enceinte reseau Windows.
Les essais concernent des pages factices, pas un projet genere.

`IsolationVerified` reste explicitement false, preparation **HTTP 503** et
`StartAsync` verrouille. Node/Playwright/helper ne sont pas inclus dans le
packaging Windows actuel ; aucune nouvelle application n'a ete installee.
Les capabilities Tauri restent reservees a `main`, sans delegation au navigateur
dedie. Le fonctionnement apres crash dur et le packaging restent non certifies.

Avant toute activation, il faut une isolation imposee par Windows, independante
du JavaScript et des flags Edge : par exemple un processus dedie sous AppContainer
et confinement WFP propre a cette instance, ou un environnement virtualise dedie
sans reseau Windows partage. Cette alternative doit etre prototypee et verifiee
sans modifier les politiques des navigateurs personnels. Il faut ensuite
reexecuter les tests de contournement, verifier le packaging et presenter un
nouveau plan complet pour une autorisation Preview explicite a usage unique.
Une autorisation de validation v9 n'est jamais reutilisable pour la Preview.
Les tests doubles ne prouvent pas l'isolation ni le nettoyage Docker reels.
Aucune Preview reelle n'a ete executee ; MVP 6.5 et 6.5-C ne sont pas declares termines.

Tests reproductibles sans Docker ni Ollama :
`npm run preview:test-browser` dans `AIPlatform.Web` (l'echec WebRTC est bloquant)
et les tests backend Preview avec `dotnet test`.

### Prototype Windows MVP 6.5-D (non integre)

Le prototype Windows MVP 6.5-D est disponible dans
[tools/WindowsConfinementPrototype](tools/WindowsConfinementPrototype/README.md),
sans raccordement au runtime de production. Les sondes natives AppContainer
ont mesure zero connexion/paquet interdit, mais Edge confine plante avant
l'execution de la page et WFP refuse la transaction d'ecriture sans privileges
supplementaires. Les temoins non confines restent fonctionnels, le nettoyage
normal et Kill-on-job-close sont verifies ; la recuperation du profil apres
crash du controleur principal ne l'est pas. Recommandation : enceinte
virtualisee dediee a valider separement. HTTP 503 reste actif, sans Docker reel,
Ollama ni execution d'un projet genere.

### Architecture Preview virtualisee MVP 6.5-E (non executee)

Le [comparatif et plan MVP 6.5-E](docs/preview-virtualisee-mvp65e.md)
recommande une appliance Linux Hyper-V jetable sans carte reseau, avec
navigateur/application dans l'invite et broker pixels/entrees sur HvSocket.
WSL2 n'est pas retenu comme enceinte dediee. Le document definit les frontieres
de confiance, le protocole borne, les tests de paquets, le nettoyage et la
migration versionnee du runtime. Aucune VM ni image systeme n'a ete lancee :
autorisation d'execution distincte, droits Windows/transport qualifies et
ressources disponibles restent requis. HTTP 503 est conserve.

### Preparation Hyper-V MVP 6.5-F (sans execution)

Le [dossier 6.5-F](docs/preview-hyperv-mvp65f.md) fixe la provenance Debian
13.7.0, les sommes publiees, la configuration Gen2 et les deux services HvSocket
proposes, avec commandes non executees, tests et recovery.
Le [plan de preparation](docs/preview-hyperv-mvp65f.plan.json) est empreinte mais
**non executable** : appliance/broker/supervision et isolation des autres canaux
non verifies, droits Hyper-V refuses et RAM mesuree sous 4,5 Gio.
Aucune VM, inscription de service ou autorisation Preview creee ; HTTP 503 conserve.

### Prerequis Hyper-V MVP 6.5-G (sans modification systeme)

Le [dossier 6.5-G](docs/preview-hyperv-mvp65g.md) confirme Windows 23H2 hors
support, les lectures TPM/Secure Boot refusees et environ 1,13 Gio de RAM libre.
Il distingue l'appartenance Administrateurs deja configuree du jeton courant
non privilegie, prepare sauvegardes/mise a niveau et decrit les accords minimaux.
Les [scripts de construction offline](tools/HyperVApplianceBuild/build-offline.sh)
et leur [modele de lock incomplet](tools/HyperVApplianceBuild/inputs.example.json)
sont separes de la production : aucune appliance construite ni VM lancee.
Les signatures, composants propres et essais reels restent requis ; HTTP 503 conserve.

### Correctif de chronologie 0.6.5 (non installe)

La chronologie collaborative n'agrege plus les derniers evenements par nom
d'etape. Une reprise pouvait laisser `Queue/Running` sans cloture ; une attente
`Approval/Running` etait resolue par `Authorization/Succeeded`, une autre cle.
Ce regroupement melangeait aussi les anciennes versions avec la version actuelle.

L'affichage utilise maintenant les identifiants et resultats reels des etapes
du workflow, avec des groupes distincts pour la version actuelle et l'historique.
L'API expose une version derivee, sans modifier les snapshots persistants :
les suffixes `resume-review` et `report` sont des positions dans le graphe,
pas des numeros de version. Une association inconnue reste explicitement inconnue.
Seule une etape de la version actuelle, compatible avec le statut actif de la
mission, affiche une activite en cours. Une mission Verified n'en affiche aucune.
Les anciennes attentes sans resultat enregistre sont neutres, pas declarees
reussies ; les autorisations terminees, annulees ou ignorees gardent leur resultat.
Le journal conserve tous les evenements et distingue un debut historique d'une
operation actuellement active. Les missions Calculator sans workflow conservent
leur chronologie avec la meme protection contre les activites historiques.

Aucune migration, reecriture JSON, nouvelle autorisation ou execution
Ollama/Docker n'est necessaire. Windows reste en 0.6.4 jusqu'a une installation
explicitement autorisee du correctif 0.6.5 ; StudioData reste inchange.

Regressions : 238 tests backend reussis, zero echec, sept scenarios opt-in ignores ;
43 tests React reussis. TypeScript, Vite et packaging NSIS hors ligne reussis.
Le rendu des copies des quatre missions confirme zero etape active :
Twitch v6 (65 evenements), React v5 (80), Calculator Verified (18) et Failed (29).
Les reprises Twitch `resume-review-15`/`resume-review-20` appartiennent a v4/v6 ;
la reprise React `resume-review-29` appartient a v5.
Les 37 fichiers Windows et les 44 fichiers sauvegardes sont toujours identiques
par SHA-256 ; Twitch conserve ses 13 fichiers, trois revisions et trois propositions.
L'avertissement Vite de bundle superieur a 500 Ko reste non bloquant et preexistant.
Installateur prepare, non execute :
`AIPlatform.Web\src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis\AIPlatform Studio_0.6.5_x64-setup.exe`.
SHA-256 : `DA1393783A35525E2BE82AF4C88B7D393C1B150097D4444814FD09739017CC37`.

### Mise a jour Windows 0.6.4 (installee sur place)

La build 0.6.4 embarque les templates Discord, React et Twitch, le runner Vite
corrige et le workflow collaboratif actuel. L'identifiant `com.aiplatform.studio`,
le mode d'installation par utilisateur et le magasin Windows
`%LOCALAPPDATA%\AIPlatformStudio\StudioData` restent inchanges.

L'audit prealable confirmait une installation 0.6.3 fermee et trois missions Windows.
Sauvegarde complete verifiee par SHA-256 :
`%LOCALAPPDATA%\AIPlatformStudio-Backups\20261113-182254`,
avec 23 fichiers Windows, 14 fichiers Twitch (mission et 13 sources) et
sept fichiers de l'installation 0.6.3. Les onze JSON de sauvegarde sont lisibles.
Empreinte du manifeste :
`6BEC5DE324757696F64C0D644EBFF6FC5393898709CA2F696B6CC776D7A80CA5`.

La compatibilite a ete testee sur une copie isolee combinant les quatre missions :
Calculator Failed/Verified, React Verified v5 et Twitch Verified v6.
Statuts, autorisations historiques, fichiers, evenements, revisions et propositions
sont conserves, y compris apres rechargement du magasin. Les empreintes React
et Twitch sont recomputees avec la configuration Desktop et correspondent
exactement aux revisions approuvees. Aucune source ni reference de chemin
n'est reecrite ; aucune execution Docker n'a ete lancee.
Le workspace vide du Calculator Failed est une limitation preexistante conservee.

La mission Twitch `4fc727c4-c386-4f03-bf7d-ea3b085241b4` a ete importee seule,
avec son workspace, apres accord explicite : Verified v6, 13 fichiers,
65 evenements, trois revisions, trois propositions. L'import conserve le JSON
octet pour octet, sans nouvelle autorisation ni relance de validation.
Les 23 fichiers Windows initiaux restent identiques aux sauvegardes.
Le magasin final contient 37 fichiers et 17 repertoires, soit quatre missions.

Packaging 0.6.4 reussi hors ligne : frontend TypeScript/Vite et backend
self-contained win-x64. Les trois templates et la commande exacte du runner
React corrige ont ete controles dans les binaires publies.
Regressions : 236 tests backend reussis, zero echec, sept scenarios opt-in ignores ;
35 tests frontend reussis, zero echec. L'avertissement Vite de bundle superieur
a 500 Ko est preexistant et n'empeche pas la build.
Installateur :
`AIPlatform.Web\src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis\AIPlatform Studio_0.6.4_x64-setup.exe`.
SHA-256 : `E4F08B0234479CEDCDB7F711C73756D7A51F2F784E8CC93428ADBAD35FF61F23`.
L'installation NSIS `/S /UPDATE` a reussi apres accord explicite, sans
desinstallation ni modification du magasin de donnees existant.
Les executables Studio et API installes sont respectivement en 0.6.4 et
0.6.4.0 et correspondent aux binaires verifies. Le marqueur Tauri de type de
bundle est normalise en memoire pour la comparaison UNK/NSS, sans modifier
les executables.

Controles sur l'application installee : lancement depuis le raccourci du
menu Demarrer, API prete et protegee, Ollama disponible, moteur Docker pret ;
les choix Discord Bot, React App et Twitch Bot sont presents dans le selecteur.
La fermeture arrete proprement Studio et son backend. Apres redemarrage,
les quatre missions sont toujours visibles : Calculator Failed, Calculator
Verified, React Verified v5, Twitch Verified v6. Le journal Twitch affiche
65 evenements et ses resultats historiques affichent compilation reussie
et 31 tests reussis. Toutes les sauvegardes, tous les fichiers Windows initiaux
et les 14 fichiers importes restent identiques par SHA-256.
Aucun build/test Docker ni aucune nouvelle autorisation n'a ete lance apres import.

L'export ZIP et la coloration de l'apercu sont disponibles pour les workspaces
.NET et React existants. Les templates Discord, React et Twitch decrits ci-dessous
reutilisent le meme workflow collaboratif. La mission React est maintenant
`Verified` en version 5 : TypeScript, Vite et dix tests Vitest ont reellement
reussi dans Docker. Cela ne valide pas les futures missions Twitch.

### MVP 6.3 - Twitch (scenario Ollama/Docker reel verifie)

Le choix **Twitch Bot** selectionne `twitch-bot-dotnet` v1. Le squelette versionne
contient douze fichiers plus la solution creee par le stockage .NET existant.
Planner planifie ; DeveloperAgent implemente progressivement `CommandRouter.cs`
et `BotHost.cs` ; ReviewAgent examine ces sources et leurs contrats. Transport,
authentification, configuration, projets, README et tests restent immuables et
sont controles avant ecriture. Les corrections ne peuvent viser que les deux
sources applicatives ; chaque revision exige une nouvelle autorisation.
La revue Ollama n'embarque pas les longues sources immuables du transport :
elles sont testees par les contrats hors ligne, pas presumees validees en live.

Choix technique approuve : .NET 10 natif (`HttpClient`, `ClientWebSocket`,
`System.Text.Json`, `TimeProvider`), sans TwitchLib, IRC ni package runtime.
Twitch recommande EventSub WebSocket pour les bots installes et Helix pour
l'envoi de chat :
- https://dev.twitch.tv/docs/chat/
- https://dev.twitch.tv/docs/chat/authenticating/
- https://dev.twitch.tv/docs/eventsub/handling-websocket-events/
- https://dev.twitch.tv/docs/chat/send-receive-messages/
- https://dev.twitch.tv/docs/authentication/validate-tokens/

Le transport souscrit a `channel.chat.message` v1 apres le welcome, surveille
les keepalives, deduplique les 512 derniers identifiants et ignore ses propres
messages. Une reprise Twitch conserve l'ancien socket jusqu'au nouveau welcome
et transfere les abonnements sans les recreer. Une perte reseau provoque une
nouvelle session avec temporisation exponentielle bornee a 64 secondes.
Les destinations de reprise sont limitees au WebSocket TLS Twitch.
L'envoi utilise `POST /helix/chat/messages`, espace les tentatives de 1,6 seconde
et verifie `is_sent`. Aucun echec d'envoi n'est rejoue aveuglement.

`!ping` repond `Pong!`, `!help` liste les commandes et `!uptime` mesure la duree
de fonctionnement du **bot**, pas du live (decision utilisateur). Horloge
monotone injectable, casse et arguments controles, messages ordinaires ignores.
Les tests xUnit utilisent uniquement faux transport, HTTP, sockets et horloge :
aucun compte Twitch, secret, Internet ou attente de reconnexion reelle.

Configuration exclusivement par `TWITCH_CLIENT_ID`, `TWITCH_USER_ID`,
`TWITCH_BROADCASTER_ID`, `TWITCH_ACCESS_TOKEN`. Ne jamais fournir ces secrets
aux prompts ni les sauvegarder dans les sources, logs ou exports. Les scopes
requis sont `user:read:chat` et `user:write:chat`. Le jeton utilisateur est
valide au demarrage puis au plus toutes les 30 minutes, avant son expiration
si elle est plus proche. Identite/scopes invalides ou revocation arretent le bot.
**Limite explicite : obtention et renouvellement OAuth manuels, pas de connexion
interactive ni de rafraichissement automatique. Aucune session Twitch reelle
n'a ete ouverte ou validee.** Un lancement live necessite une decision manuelle
distincte et `TWITCH_CONNECT_LIVE=1` ; la verification ne lance jamais le bot.

Le runner reutilise `aiplatform-sandbox:net10` et son cache deja present :
`xunit [2.9.3]`, `xunit.runner.visualstudio [3.1.4]`,
`Microsoft.NET.Test.Sdk [17.14.1]`. Aucun telechargement/preparation reseau
supplementaire. Les commandes sont `dotnet build` puis `dotnet test` en
`--configuration Release`, avec restauration hors ligne depuis `/opt/nuget`.
Image immuable, commandes exactes, fichiers et limites entrent dans l'empreinte
d'autorisation. Reseau desactive, UID 10001, racine/sources en lecture seule,
capacites supprimees, limites et nettoyage restent inchanges.

Regressions Studio : **236 backend reussis, 0 echec, 7 ignores** (opt-in reels),
**35 frontend reussis, 0 echec, 0 ignore**. Le build frontend TypeScript/Vite
reussit ; avertissement preexistant de bundle >500 Ko. Ces tests ne compilent
et n'executent aucun projet genere sur Windows. Le scenario Ollama/Docker
Twitch a produit la mission `4fc727c4-c386-4f03-bf7d-ea3b085241b4`, initialement
version 1 `AwaitingApproval`, dans le magasin de validation isole `twitch-validation`
(aucun import Windows). Planner `qwen3:4b`, Developer et Review
`qwen2.5-coder:7b` ont reellement ete appeles ; deux fichiers applicatifs generes,
douze fichiers de projet plus la solution, onze evenements, une revision,
aucune correction ni autorisation Docker. Empreinte de cette revision :
`7D8B76D4F458D43184D8D32C4C8480F650CA779E2361FD3293E0BA91EFF03355`.

L'inspection directe a constate des defauts dans le `BotHost` initial : boucle
de reconnexion dupliquee, erreurs fatales absorbees et journalisation de
`Exception.Message`. Le ReviewAgent n'a produit que des avertissements et
a aussi emis des remarques erronees sur la configuration et les tests proteges
non transmis. Sa revue n'est donc pas une preuve de correction.

Apres autorisation explicite v1 : compilation Release reussie, aucun avertissement
ni erreur ; tests arretes a la limite de 120 secondes a cause de la boucle du
BotHost. Les deux conteneurs ont ete supprimes. Ollama a corrige progressivement
le BotHost ; une sortie invalide et une limite de contexte ont interrompu des
tentatives, reprises sans effacer l'historique ni reinitialiser le compteur.
Une condition incorrecte qui selectionnait la politique .NET generique lors
des corrections Twitch a ete corrigee. Le chemin JSON est maintenant contraint
au seul fichier demande ; les anciennes sources restent stockees mais ne sont
pas retransmises dans le contexte de correction du 7B.

La revue inclut un controle serveur explicite des constructions interdites
dans BotHost (boucle externe, catch global, texte d'exception). Ce controle
borne ne remplace pas une analyse C# semantique ni les tests. Un faux positif
sur le delai annulable apres erreur d'envoi a ete corrige : un cooldown n'est
pas une reconnexion. Les regressions couvrent ces politiques et le budget.
La proposition v5 deja generee par Ollama a ete relue puis appliquee en v6,
sans nouvelle generation, autorisation automatique ou tentative supplementaire.

Apres un **nouvel accord explicite v6**, pour l'empreinte
`2B01709443ACB92342D7D52F9BC6FEF0F2E9FF5BFDCB5C529D0CD8D8EA4DF976` :
- `dotnet build` Release : **Succeeded**, sortie 0, 0 erreur, 0 avertissement.
- `dotnet test` Release : **Succeeded**, sortie 0, **31 tests executes et
  reussis, 0 echec, 0 ignore**, rapport TRX reel controle.
- Image `sha256:232a19ba3edf91e03de6c2804cc895dd07bf3585c226316b497c1ffdd3025515`.
- Isolation Docker observee : reseau `none`, UID `10001:10001`, lecture seule,
  capacites `ALL` supprimees, no-new-privileges, 2 CPU et 2 Go.
  Les deux conteneurs v6 ont ete supprimes, comme ceux de v1.

Etat persiste final : **Verified, workflow version 6**, treize fichiers avec
la solution, **65 evenements**, trois revisions appliquees (v1/v3/v6), trois
propositions conservees (v2/v3/v5), trois corrections sur la limite de trois.
L'echec v1 reste dans l'historique. L'export ZIP du projet verifie contient
treize entrees et passe les controles de secrets habituels.
Les trois missions Windows d'origine gardent leurs empreintes JSON exactes ;
la mission Twitch reste dans le magasin isole, sans import ni mise a jour Windows.
Lors de cette livraison MVP Twitch, aucun installateur n'avait ete prepare.
La mise a jour Windows 0.6.4 est decrite ci-dessus. La connexion a Twitch reste
une action manuelle distincte, non realisee et non validee par ces tests.

Fichiers ajoutes/modifies pour ce perimetre (sans compter les changements
anterieurs deja presents dans le depot) :
- Template : [TwitchTemplate.cs](AIPlatform.Infrastructure/Studio/TwitchTemplate.cs).
- Workflow et protections : [WorkflowAgents.cs](AIPlatform.Infrastructure/Studio/WorkflowAgents.cs),
  [CollaborativeProcessor.cs](AIPlatform.Infrastructure/Studio/CollaborativeProcessor.cs),
  [MissionStore.cs](AIPlatform.Infrastructure/Studio/MissionStore.cs),
  [WorkspaceStore.cs](AIPlatform.Infrastructure/Studio/WorkspaceStore.cs),
  [ProjectExport.cs](AIPlatform.Infrastructure/Studio/ProjectExport.cs).
- Docker : [DockerSandboxRunner.cs](AIPlatform.Infrastructure/Studio/DockerSandboxRunner.cs),
  [ExecutionPlanService.cs](AIPlatform.Infrastructure/Studio/ExecutionPlanService.cs).
- Interface : [App.tsx](AIPlatform.Web/src/App.tsx),
  [MissionDetails.tsx](AIPlatform.Web/src/MissionDetails.tsx).
- Tests : [TwitchTemplateTests.cs](AIPlatform.Tests/TwitchTemplateTests.cs),
  [App.test.tsx](AIPlatform.Web/src/App.test.tsx),
  [Workflow.test.tsx](AIPlatform.Web/src/Workflow.test.tsx).
- Documentation : ce README.

### Correctif du runner Vite 0.6.3

Le profil precedent liait tout `/work/node_modules` aux packages immuables sous
`/opt/aiplatform/node_modules`. Vite 6.4.6 y cherchait son cache `.vite-temp` :
la creation echouait avec `ENOENT` dans le conteneur en lecture seule. Le meme
echec a ete reproduit sans Ollama sur une application de reference ecrite a la
main ; il ne provenait pas du projet utilisateur.

Le runner cree maintenant un vrai repertoire `node_modules` dans le tmpfs
`/work`, avec des liens individuels vers les packages approuves et `.bin`.
Les caches `.vite-temp` et `.vite` sont inscriptibles et ephemeres ; les
dependances sous `/opt` restent non modifiables. L'image, le Dockerfile, le
manifeste et le lockfile approuves ne changent pas. Aucun package n'est installe.
Les commandes React exactes entrent desormais dans l'empreinte d'autorisation :
une autorisation du profil precedent n'autorise pas le runner corrige.
Les empreintes des profils .NET et Discord ne sont pas modifiees par cet ajout.

Apres autorisation limitee a cette reference isolee, le test
`ReactReferenceBuildsAndTestsWithWritableCachesAndReadOnlyDependencies` passe :
TypeScript, build Vite et dix tests Vitest/Testing Library reussissent. Il
controle aussi UID 10001, reseau limite a loopback, absence de privileges,
racine et sources en lecture seule, packages immuables, caches inscriptibles,
limites Docker et suppression des quatre conteneurs. Les fichiers sources de
reference restent inchanges. Le test echouait auparavant sur le vrai `ENOENT`
Vite. Il reste opt-in avec `AIPLATFORM_TEST_DOCKER=1` et un filtre sur son nom,
apres autorisation humaine ; les autres scenarios Docker/Ollama ne sont pas
necessaires pour cette validation.

Regressions : backend 226 reussis, aucun echec, sept ignores, dont ce scenario
Docker execute separement avec succes ; frontend 33 reussis, aucun echec.
L'installateur NSIS Windows 0.6.3 a ete construit puis installe sur place apres
accord explicite et nouvelle sauvegarde verifiee. Le frontend NSIS et le backend
installes correspondent au build ; les nouvelles commandes du runner ont aussi
ete retrouvees dans le backend installe. Le demarrage par le raccourci du menu
Demarrer, API, Ollama, Docker et les trois missions ont ete verifies.
Les 23 fichiers de StudioData restent identiques a la sauvegarde, empreintes
SHA-256 comprises. Aucune validation de la mission React n'a ete lancee.

La mission v4 reste `BuildFailed`, avec 15 fichiers, 67 evenements, quatre
revisions et trois corrections consommees. Le bouton existant **Reprendre**
cree une nouvelle version et relance ReviewAgent : il ne convient pas a une
revalidation strictement en v4 apres correction d'infrastructure.
Une action distincte est proposee, pas encore implementee : verifier les sources
contre la revision v4, ajouter un cycle de validation sans nouvelle revision,
preserver les anciens resultats et le compteur de corrections, invalider
l'ancien accord et demander une nouvelle autorisation sur le plan recalcule.
Cette preparation ne doit appeler ni Ollama ni Docker pour executer du code.

### Mise a jour Windows 0.6.2 (effectuee)

La mise a jour sur place et l'importation ont ete autorisees et effectuees apres
sauvegardes SHA-256 et compatibilite sur copies. Le magasin
`%LOCALAPPDATA%\AIPlatformStudio\StudioData`, les deux Calculator et les
donnees originales de validation sont conserves. Trois missions sont visibles.
Le Calculator deja en echec garde son workspace vide, sans reparation.
React a ete importe intact en v2 avec 15 fichiers, 33 evenements et deux
revisions. L'application demarre par le raccourci du menu Demarrer et detecte
API, Ollama et Docker.

La mise a jour 0.6.3 conserve l'identifiant Tauri et le mode par utilisateur.
Le mode NSIS `/UPDATE` evite une desinstallation prealable ; Studio et son
backend doivent etre arretes avant toute installation explicitement autorisee.
La preparation du correctif ne modifie pas le magasin Windows.

### MVP 6.2 - React (validation Docker reelle reussie)

Le dossier `sandbox/react` contient un manifeste npm de confiance, son lockfile
et un Dockerfile de preparation. L'image locale
`aiplatform-react-sandbox:1.0.0` a ete preparee sans code genere, avec scripts
npm desactives. La resolution utilise `--legacy-peer-deps` pour contourner une
erreur interne de npm 10 ; `npm ls --all` verifie ensuite l'arbre installe.
Les commandes de version TypeScript, Vite, Vitest et esbuild ont fonctionne
dans un conteneur sans reseau. Cela ne valide pas un projet React.

Le choix **React App** initialise `react-typescript-vite` v1 dans le workflow
Planner -> Developer -> Review -> autorisation -> TypeScript -> Vite -> Vitest.
Le squelette fournit composants, styles responsive, configuration TypeScript
stricte, ESLint, README et dix tests contractuels Vitest/Testing Library.
DeveloperAgent implemente progressivement les trois fichiers applicatifs.
Les tests, configurations, manifeste npm et lockfile sont immuables : aucun
package, script ou fichier supplementaire ne peut etre ajoute par le modele.
Le lockfile reel est embarque depuis `sandbox/react/package-lock.json`, sans
regeneration par le LLM. La revue ne recoit que les sources et tests pertinents,
pas les 160 Ko du lockfile. Les autres fichiers sont controles par le serveur.

Le runner utilise l'image preparee, jamais `npm install` dans le projet genere.
Les commandes fixes sont `tsc --noEmit`, `vite build`, puis `vitest run` avec
rapport JSON. Un code de sortie nul sans rapport coherent, sans tests ou avec
tests ignores ne valide pas les tests. Les trois etapes doivent reussir pour
la meme version avant `Verified`. L'image reelle, les fichiers et les limites
entrent dans l'empreinte d'autorisation. Chaque correction preserve les tests
et exige une nouvelle revue et une nouvelle autorisation.

Le 13 novembre 2026, deux premiers essais Ollama ont echoue avant Docker :
limite de contexte de revue, puis rapport de revue invalide et tests generes
inadequats. Le contexte a ete reduit et les tests rendus deterministes et
immuables. La mission `eb0efd99-95a7-4d62-82ac-8c8053e35ec6` a ensuite
ete generee avec `qwen2.5-coder:7b`, planifiee avec `qwen3:4b` et revue.
Apres accord explicite pour la version 1 (empreinte
`5C756026DAC7A31BE9E6F106217A2717F7BC105AAB742DC85A43E79340F92F27`),
TypeScript a reellement echoue dans Docker (sortie 2, trois imports manquants).
Vite et Vitest n'ont pas ete executes. La correction a d'abord atteint la
limite de contexte ; les doublons de sources, de diagnostics et de tests
immuables ont ete retires du contexte de correction, sans supprimer l'historique.
Apres reprise explicite, Ollama a corrige uniquement les imports de `src/App.tsx`
et ReviewAgent a examine cette version 2.

La version 2 a ete importee avec l'empreinte
`252FC2B97E711A0F430AEEAF4848CDA84B7143B340D96C6645A438AF134E88F9`.
L'image est
`sha256:a7f7808c85c85c61afb8e29784bcb85273e99c6e1d27c2416cfb3bcaa85411ee`.
L'utilisateur a ensuite autorise lui-meme les versions 2, 3 et 4 dans Studio.
TypeScript passe, mais Vite echoue a chaque fois sur le cache `.vite-temp` ;
Vitest n'est pas execute. A l'ouverture du chantier correctif, la mission v4
est deja `BuildFailed`, avec 67 evenements, quatre revisions et une autorisation
historique encore enregistree. L'utilisateur a demande de conserver exactement
cet etat : aucun statut, historique, fichier ou accord n'est modifie pendant
la correction du runner. Le projet genere n'etait pas encore `Verified`.
La revue ne remplace jamais TypeScript ni les tests.

Apres installation du correctif 0.6.3 et decision utilisateur dans Studio,
la mission est maintenant `Verified` en version 5 : 15 fichiers, 80 evenements,
cinq revisions ; TypeScript, Vite et dix tests Vitest reussis, sorties 0.
Le chantier Twitch utilise un autre magasin isole et ne modifie pas cette mission.

Regressions du correctif Vite : 226 tests backend reussis, 0 echecs, 7 ignores (scenarios manuels
ou integrations reelles opt-in) ; 33 tests frontend reussis, 0 echec, 0 ignore.
Le build de Studio TypeScript/Vite reussit avec un avertissement de taille de
bundle. Ces tests de Studio ne remplacent pas la validation du projet genere.
L'installateur correctif ne constitue pas une validation du projet genere.

### MVP 6.1 - Discord (validation en cours)

Le mode projet propose le template versionne `discord-bot-dotnet` v1. Le
squelette contient une application .NET 10 Discord.Net.WebSocket 3.20.2,
les commandes `/ping`, `/help`, `/info` et des tests xUnit hors ligne. Le
DeveloperAgent adapte la logique de reponse, puis ReviewAgent examine les
sources. Les fichiers de tests et les references de packages restent proteges.
Le bot lit `DISCORD_BOT_TOKEN` uniquement au demarrage : ne jamais transmettre
de token a Studio ni le placer dans les sources. La detection de secrets est
heuristique et ne garantit pas leur absence. L'analyse du ReviewAgent peut
contenir des erreurs et ne prouve pas qu'un projet fonctionne.

Le profil Discord utilise `aiplatform-sandbox:discord-net10`, distinct du
profil generique. L'empreinte de l'autorisation inclut l'identifiant reel de
l'image, les fichiers et les limites ; une image absente bloque la validation.
Les dependances directes autorisees sont Discord.Net.WebSocket 3.20.2 dans le
projet du bot, ainsi que xunit 2.9.3, xunit.runner.visualstudio 3.1.4 et
Microsoft.NET.Test.Sdk 17.14.1 pour les tests. Le cache prepare contient aussi
Discord.Net.Core, Discord.Net.Rest et Discord.Net.Dave 3.20.2 et Newtonsoft.Json
13.0.4. Les autres references directes et versions sont refusees.

Apres autorisation humaine specifique, preparer l'image depuis le dossier de
confiance `sandbox`, sans y ajouter de code genere :

```powershell
docker buildx build --load --provenance=false --network=default --resource memory=2g --resource cpu-quota=200000 --resource cpu-period=100000 --build-arg BASE_IMAGE=aiplatform-sandbox:net10 --file sandbox\Discord.Dockerfile --tag aiplatform-sandbox:discord-net10 sandbox
```

Cette restauration est executee en utilisateur non privilegie avec une source
NuGet configuree. Le reseau du conteneur de restauration n'est pas filtre par
domaine. La compilation et les tests ulterieurs sont distincts, sans reseau,
et necessitent une nouvelle autorisation liee a la version des fichiers. Ils
ne demarrent pas le bot et ne testent pas une connexion Discord.

Le 13 novembre 2026, la mission `0eaec9d6-f679-406c-9e2d-e610bedf1cfe`
a ete generee et revue avec Ollama (`qwen2.5-coder:7b` ; plan par
`qwen3:4b`). Une premiere reponse de revue invalide a exige une reprise
explicite ; la seconde revue a produit cinq avertissements, susceptibles de
contenir des erreurs. Apres autorisation humaine liee a l'empreinte
`181F7813D1E7F44181C84AA03082EB3086D9BCB2F77E2704A4CCBC41326674E6`,
la compilation Docker a reussi et les quatre tests hors ligne sont passes.
La mission a atteint `Verified`. L'image locale utilisee etait identifiee par
`sha256:c39d32476517215fd902a4fa1e88ecf3f143ab8f86d8d9d9467ce09a5ac11e2e`.
Ces tests ne valident pas la connexion ou l'enregistrement des commandes sur
Discord. Aucun installateur MVP 6.1 n'a ete prepare.

### MVP 6.4 - Full-Stack React + ASP.NET Core

Le template versionne `fullstack-react-dotnet` v1 partage un contrat OpenAPI
fixe entre BackendAgent et FrontendAgent. Le workflow sequentiel genere le
backend ASP.NET Core .NET 10 avec SQLite, puis le frontend React/TypeScript,
et les fait examiner par ReviewAgent. Les lockfiles NuGet sont veritables et
les dependances restent fixes. La validation requiert une autorisation humaine
pour chaque version et enchaine build Release, xUnit, TypeScript, Vite, Vitest
et un test HTTP reel sur loopback dans un conteneur sans reseau externe.

L'image `aiplatform-fullstack-sandbox:1.0.0` reutilise les paquets .NET
prepares avec `sandbox\FullStackSandboxPackages.csproj` et son lockfile, ainsi
que les dependances Node de
`aiplatform-react-sandbox:1.0.0`. Sa recette est `sandbox\FullStack.Dockerfile`;
les deux images de base doivent deja etre presentes. La recette revision 2
separe explicitement la preparation reseau du build final hors ligne :

1. Apres autorisation reseau distincte, exporter le target `dependency-feed`.
   Il utilise uniquement le projet fixe de preparation, jamais les sources
   generees, reutilise `/opt/nuget` et restaure le graphe verrouille depuis
   `https://api.nuget.org/v3/index.json`. `FullStackNuGet.Config` efface les
   sources et fallback folders herites. Les six paquets manquants de SQLite
   sont Microsoft.Data.Sqlite et Microsoft.Data.Sqlite.Core 10.0.15, puis
   SQLitePCLRaw.bundle_e_sqlite3, core, lib.e_sqlite3 et provider.e_sqlite3
   2.1.13. Les treize autres paquets sont deja dans l'image .NET de reference.
2. Construire le target final avec `--network=none`. Il ne depend pas du
   target reseau : il consomme uniquement le feed local exporte, verifie
   `SHA512SUMS`, puis restaure a neuf avec `RestoreLockedMode=true`.
   NuGet verifie aussi les empreintes de contenu du lockfile (distinctes du
   SHA-512 brut des archives signees). Un feed absent ou incomplet bloque
   le build, sans recours a Internet.

Commandes a examiner avant autorisation (depuis la racine du depot) :

```powershell
# RESEAU : uniquement apres autorisation explicite, destination neuve.
docker buildx build --pull=false --network=default --resource memory=2g --resource cpu-quota=200000 --resource cpu-period=100000 --target dependency-feed --file sandbox\FullStack.Dockerfile --output type=local,dest=sandbox/fullstack-feed sandbox
# HORS LIGNE : apres preparation et verification du feed.
docker buildx build --load --pull=false --network=none --resource memory=2g --resource cpu-quota=200000 --resource cpu-period=100000 --tag aiplatform-fullstack-sandbox:1.0.0 --file sandbox\FullStack.Dockerfile sandbox
docker image inspect aiplatform-fullstack-sandbox:1.0.0 --format '{{.Id}}'
```

Le feed local est exclu de Git. Conserver son manifeste avec le lockfile
pour les reconstructions. Le tag reste compatible, mais le label
`org.aiplatform.fullstack.recipe=2` et l'identifiant SHA-256 de l'image
identifient la nouvelle recette. Le plan d'execution inclut l'identifiant
effectif de l'image et les commandes du runner dans son empreinte : une
image reconstruite requiert un nouveau plan et une nouvelle autorisation.

La restauration s'execute avec l'UID 10001 : les entrees sont sous
`/opt/fullstack-seed`, les copies temporaires et `obj` sous `/tmp`, jamais
dans `/seed/obj` herite de root. Les validations conservent la racine en
lecture seule, les sources et caches en lecture seule et les tmpfs limites
`/tmp` et `/work`. Ni npm install ni scripts du projet ne sont utilises
pour preparer l'image. Le reseau de preparation ne constitue pas une
autorisation d'execution. Aucune image n'est telechargee ou construite
automatiquement. La mission et son plan doivent etre examines avant toute
autorisation de validation Docker.

Les tests .NET et le serveur HTTP d'integration utilisent `LD_LIBRARY_PATH`
vers la bibliotheque SQLite approuvee du cache immuable `/opt/nuget`
(Linux x64, SQLitePCLRaw 2.1.13). Cela permet son chargement natif sans
rendre les tmpfs executables. Le runner lit TRX pour xUnit et JSON pour
Vitest ; une sortie vide ou incoherente ne valide pas les tests.

Une mission full-stack `GeneratedUnverified` deja relue peut utiliser
« Preparer une nouvelle validation · sans regeneration » dans Studio
(`POST /api/missions/{id}/revalidate`). Le systeme exige que tous les fichiers
correspondent a la derniere revision relue et que l'image soit disponible.
Il ajoute une version de validation avec les memes sources et une nouvelle
empreinte, conserve les revisions, evenements et resultats precedents,
ne consomme aucune correction et n'appelle ni Ollama ni Docker run.
La mission attend ensuite une nouvelle autorisation humaine ; une ancienne
autorisation n'est jamais reutilisee. Une modification des sources exige
la reprise avec revue habituelle, pas ce raccourci d'infrastructure.
Si cette validation echoue, elle s'arrete avec ses diagnostics sans declencher
de correction automatique : l'utilisateur examine d'abord l'origine de l'erreur.
Pour chaque version full-stack, le plan canonique enregistre les empreintes des
fichiers, la politique sans correction automatique, les six commandes et leur
isolation, l'image par identifiant effectif et les limites. Il est compare au
plan actuel avant approbation et avant chaque etape. Les sources copiees dans le
conteneur sont aussi comparees au manifeste approuve avant toute commande. Un
changement exige une nouvelle version et un nouvel accord. Une ancienne version
en attente sans plan canonique peut etre reprise par cette meme action de
revalidation, sans reutiliser son autorisation ni modifier les anciennes preuves.

Une correction full-stack examinee explicitement peut etre appliquee via
`POST /api/missions/{id}/manual-backend-revision` ou
`POST /api/missions/{id}/manual-frontend-revision`, avec l'empreinte courante et
les remplacements complets des fichiers modifiables du role concerne. L'operation refuse
les autres chemins et les sources ou plans perimes, conserve une proposition
et les versions precedentes, invalide l'autorisation, puis cree une revision et
un plan canoniques sans appeler de modele ni lancer Docker. La revision est
attribuee a l'examen humain, sans inventer une revue d'agent. En cas d'interruption,
la mission ne s'execute pas et la proposition reste consultable pour diagnostic.
Elle est aussi disponible apres un echec de compilation `BuildFailed`, avec
conservation des resultats et obligation d'une nouvelle autorisation.

### Export ZIP des projets existants

Projects propose l'export des sources d'un projet inactif. Les chemins sont
valides avant lecture ; les liens sont refuses. L'archive exclut notamment
`bin`, `obj`, `node_modules`, `.env`, les configurations `appsettings`, les
fichiers dont le nom indique un secret et les certificats/cles. Les chemins
inattendus bloquent l'export. Certains secrets visibles dans les sources
provoquent aussi son refus, sans publier leur valeur dans l'erreur.
Cette detection est **non exhaustive** : inspecter les fichiers et l'archive
avant tout partage. Les configurations exclues doivent etre recreees localement.
L'apercu colore les fichiers C#, projets XML, JSON, JavaScript et TypeScript ;
les types inconnus restent affiches en texte brut sans executer le contenu.

## Avancement MVP 7 (incomplet)

Les workspaces generes existants peuvent etre analyses statiquement depuis
Projects ou `GET /api/workspaces/{id}/analysis`. L'analyse examine les fichiers
autorisés, les declarations de projets et de dependances, les noms de scripts,
les points d'entree et les fichiers Docker. Les scripts ne sont pas executes,
leurs commandes ne sont pas exposees et Ollama n'est pas appele. Les erreurs de
lecture de manifestes sont signalees. Le rapport est une observation statique,
non un calcul MSBuild/npm, et n'est actuellement pas persiste ; Git n'est pas
inspecte. Les fichiers sensibles et les artefacts connus sont exclus.

L'analyseur accepte au plus 2 000 fichiers, 256 Kio par fichier et 16 Mio au
total par defaut ; ces limites sont configurables dans la section Studio.
L'endpoint actuel utilise aussi les limites plus strictes des workspaces
generes existants. XML interdit les DTD et la resolution d'entites externes ;
JSON est limite en profondeur. Les versions de dependances non resolues sont
signalees plutot qu'executees ou devinees. Un workspace actif ou modifie pendant
une mission est refuse ; une modification externe concurrente peut toutefois
changer les fichiers pendant l'analyse. Le rapport ne constitue donc ni un
snapshot transactionnel ni une autorisation de build.

L'importation de dossiers ou depots externes, la recherche et les changements
sur un projet original ne sont pas disponibles. Il n'existe pas encore de
selecteur natif ni d'autorisation de dossier, de PatchEngine, d'integration Git,
de validation Docker pour projets importes ou d'installateur MVP 7. Aucun code
importe ne doit etre execute sur Windows. La detection des secrets reste
incomplete : elle ne peut pas garantir qu'un texte est sans secret.

## Fiabilite des corrections collaboratives (MVP 5.1)

Les erreurs de compilation et de tests produisent un diagnostic structure
(commande, code de sortie, projet, fichier, ligne, colonne, code et message quand
disponibles). Un extrait borne est envoye au modele ; le journal technique est
conserve avec ses limites explicites : 24 000 octets de sortie de tests dans le
conteneur et 65 536 caracteres pour la sortie Docker. Une troncature est signalee ;
il ne s'agit pas d'un journal illimite.

Les corrections ne peuvent modifier que les fichiers C# applicatifs planifies.
Les tests, assertions et fichiers de projet restent inchanges. Le diagnostic
selectionne le fichier applicatif identifie, sinon les sources applicatives
existantes ; aucun nouveau chemin ni aucune dependance ne sont autorises.
Les propositions, y compris celles refusees par la revue, sont persistees et
validables avant ecriture. La revue contient
probleme, justification, risques, conformite et recommandation ; elle ne remplace
pas Docker. Les versions precedentes et leurs changements avant/apres sont
consultables dans Missions et par `GET /api/missions/{id}/revisions/{version}`.

Une interruption de correction exige une reprise manuelle : proposition et
diagnostic sont conserves, la revue est reprise si necessaire et aucun accord
precedent n'est reutilise. Si le workspace ne correspond plus aux fichiers connus,
la reprise s'arrete explicitement. L'approbation est refusee si l'empreinte ne
correspond pas a la version relue. Les changements de configuration invalident
aussi l'empreinte. Une nouvelle approbation humaine est necessaire avant chaque
validation de version ; aucune commande de projet genere n'est executee sur Windows.

### Etat des validations MVP 5.1

- Tests simules : erreur de compilation et limite de corrections, echec de tests,
  refus, reprise d'une correction interrompue, diagnostic et empreinte non revue.
- Regression : 176 tests backend reussis ; 5 integrations Docker/Ollama et la
  preparation manuelle optionnelle ignorees par defaut ; 25 tests React reussis
  et build frontend reussi. La preparation a aussi ete lancee separement et a
  cree une mission en attente, sans execution ni autorisation Docker.
- Scenario reel `c5dd4453-0594-41d5-ade8-a675aa59c412` : apres accord humain,
  compilation Docker echouee (CS1525 ligne 1, colonne 92), suivie de trois
  propositions Qwen2.5-Coder et revues reelles. Les trois premieres revues ont
  repris a tort le diagnostic de l'ancienne version et epuise les tentatives.
  Le contexte de revue a ete corrige puis la derniere proposition a ete relue
  explicitement sans nouvelle execution Docker. Cette revue signale un
  avertissement sur le test d'isolation ; ce test n'a pas ete modifie.
  Apres une **seconde autorisation humaine**, la version corrigee a ete compilee
  dans Docker (sortie 0, aucune erreur ni avertissement), puis ses **3 tests**
  (addition, soustraction et isolation) ont reussi, sans echec : `Verified`.
  Le test d'isolation verifie notamment UID, capacites, no-new-privileges,
  interfaces reseau et ecriture refusee sur les montages en lecture seule.
  Aucun conteneur AIPlatform restant n'a ete observe. Apres redemarrage du
  backend isole, statut et trois resultats sont conserves.
  Les deux premieres propositions, effectuees avant la persistance des
  propositions, ne sont pas recuperables ; la derniere et sa revue sont conservees.
- Le scenario B (test arithmetique defectueux) n'a pas ete execute avec les
  services reels. Le refus et la reprise pendant correction ont ete couverts
  par des tests simules ; ils ne constituent pas une validation Docker reelle.
- L'installateur Windows x64 0.5.1 a ete construit. Son backend publie a passe
  le smoke test isole (authentification locale, services, arret et redemarrage) ;
  l'installation et l'interface native 0.5.1 n'ont pas ete testees.
- Les tests Docker optionnels historiques ne constituent pas une preuve de ce
  scenario collaboratif et ne doivent pas etre lances sans accord explicite.

Pour preparer uniquement le scenario A reproductible, choisir un repertoire de
donnees **inexistant**, hors du depot, puis lancer depuis la racine :

```powershell
$env:AIPLATFORM_PREPARE_CORRECTION = '1'
$env:AIPLATFORM_SCENARIO_DIRECTORY = 'C:\Temp\AIPlatform-scenario-unique'
dotnet test .\AIPlatform.Tests\AIPlatform.Tests.csproj --filter "FullyQualifiedName~ManualCorrectionScenarioTests" --logger "console;verbosity=detailed"
```

Cette preparation utilise un plan, des fichiers et une revue initiale simules ;
elle ne lance ni Ollama ni Docker et ne donne aucune autorisation. La sortie
indique la mission et son empreinte. Pour la consulter, demarrer le backend de
developpement avec `Studio__DataDirectory` pointant sur ce repertoire, puis
ouvrir l'interface de developpement. L'autorisation initiale concerne la
compilation/tests de cette seule version. Apres l'echec et la correction/revue
reelles, examiner les differences et prendre une decision distincte pour la
version suivante. Les donnees de ce scenario sont isolees de celles de
l'application installee.

## Collaboration locale (MVP 5)

Les nouvelles missions `Project` utilisent par defaut un workflow collaboratif
sequentiel (`Studio:CollaborativeWorkflows=true`). Les conversations et les anciens
endpoints restent disponibles. Les anciennes missions sans champ `workflow` sont
lues sans migration destructive et conservent leur historique.

### Architecture et exemple

```text
OrchestratorAgent (C#, dependances et transitions)
  PlannerAgent (qwen3:4b, plan JSON valide)
    DeveloperAgent (qwen2.5-coder:7b, un fichier par appel)
      ReviewAgent (qwen2.5-coder:7b, constats JSON, lecture seule)
        Autorisation humaine de la version des fichiers
          TestAgent (C#, Docker build puis tests hors ligne)
            Rapport final

Echec de revue/build/tests -> analyse persistante -> correction ciblee
  -> nouvelle revue -> NOUVELLE autorisation -> nouveaux conteneurs
```

Le moteur ne delegue jamais ses transitions, permissions ou commandes a un LLM.
Le DAG des etapes et celui des fichiers refusent les cycles, identifiants dupliques
et dependances inconnues. Une etape exige des prerequis termines avec succes.
Une seule mission et un seul appel Ollama sont actifs a la fois. Planification
utilise le generaliste ; generation et revue restent sur le codeur pour limiter
les changements de modele. Les agents deterministes n'appellent aucun modele.

Les composants existants restent utilises : `MissionStore`, worker et verrou
d'execution uniques, fournisseur Ollama, `WorkspaceStore`, validation des projets,
`ExecutionPlanService`, `DockerSandboxRunner`, transport Desktop authentifie.
`CollaborativeProcessor` gere les transitions ; `WorkflowAgents` valide les sorties
et construit les contextes limites par role. Aucun shell ni outil fichiers natif
n'est accessible aux modeles.

### Memoire, reprises et versions

Chaque snapshot JSON de mission contient, en plus de la demande d'origine :

- WorkflowId/MissionId, configuration figee des agents/modeles et etapes ;
- plan valide, decisions d'architecture et dependances de fichiers ;
- brouillon incremental, constats de revue et versions sources avec empreintes ;
- diagnostics reels, historique des corrections, logs et resultats de tests ;
- entree observable, sortie, erreur, duree et compteur de chaque etape.

Les snapshots utilisent le remplacement atomique deja present. Les appels LLM
recoivent uniquement les donnees utiles : demande pour le plan, plan/fichier/
prerequis pour le developpeur, plan/sources pour la revue. Les sorties restent des
donnees non fiables dans les messages utilisateur, jamais des instructions systeme.
Les instructions sont configurables uniquement par l'administration locale.
L'API de workflow ne publie ni les prompts internes ni les brouillons sources.
Les fichiers restent consultables par les endpoints controles du workspace.

Au redemarrage, une mission interrompue devient `Cancelled` avec un motif explicite.
La reprise est manuelle ; aucune commande sensible n'est relancee automatiquement.
Une mission deja en attente d'autorisation reste en attente. La reprise abandonne
l'ancien accord et reprend la correction interrompue ou revalide les sources
existantes, puis impose une nouvelle revue/autorisation. Les missions verifiees, supprimees ou anciennes sans
workflow ne peuvent pas etre reprises ; une nouvelle mission reste possible.

Chaque modification collaborative invalide l'accord precedent. L'empreinte est
recontrolee avant build ET tests. L'option historique `approveSandboxExecution`
ne court-circuite pas l'accord post-generation d'un nouveau workflow. Les trois
corrections automatiques maximum partagent un compteur persistant, y compris apres
reprise ; les sources des tests et les csproj sont immuables pendant ces corrections.
Les fichiers non planifies, suppressions et modifications
hors workspace sont refuses. Une correction de test source exige une intervention
humaine ou une nouvelle mission, pas un affaiblissement automatique des assertions.

### Agents et modeles configurables

Dans **Agents**, utiliser **Configurer les agents** pour creer/modifier identifiant,
nom, role, description, capacites, modele, instructions, types de missions, outils,
delai et etat actif. Dans **Models**, les noms Ollama et budgets contexte/sortie/
temperature sont editables ; l'ajout d'un modele signifie seulement configurer un
modele deja installe, jamais le telecharger.

L'enregistrement requiert l'absence de missions actives ou en attente d'accord.
Les regles sont imposees par le serveur :

| Role | Outils maximum |
|---|---|
| Chat / Router | Aucun outil systeme |
| Planner | Plan |
| Developer | GenerateFiles |
| Review | ReadSources |
| Test | DockerBuild, DockerTests |
| Orchestrator | Workflow |

Un seul agent actif par role collaboratif est pris en charge. Des agents Chat
specialises supplementaires sont possibles ; les futurs roles Discord/Twitch,
frontend ou base de donnees ne fournissent pas encore de nouveaux outils.
Desactiver un role indispensable ou retirer ses permissions bloque explicitement
la creation du workflow. Routeur et agent de secours doivent rester utilisables.
La configuration est conservee dans `StudioData/agents.json` ; les snapshots
existants conservent la leur. Les parametres initiaux restent dans appsettings.
Le modele est selectionne depuis cette configuration, jamais depuis une sortie LLM.

### Interface et nouveaux endpoints

Workspace affiche le plan, les intervenants et les resultats observables, pas les
raisonnements bruts. Missions permet d'ouvrir chaque etape pour lire contexte
transmis, agent, modele, dependances, duree, tentatives et erreurs. Projects utilise
un nom court editable, description, date, mission associee et historique de versions.
La suppression exige confirmation, refuse les projets actifs et conserve l'audit
de mission tout en supprimant sources et contenus des anciennes versions.

| Methode | Endpoint | Resultat |
|---|---|---|
| GET | `/api/agents/configuration` | Configuration locale complete |
| PUT | `/api/agents/configuration` | Validation/enregistrement atomique, 400 invalide, 409 mission active |
| GET | `/api/missions/{id}/workflow` | Etapes et memoire observable, 404 si absent/ancien |
| POST | `/api/missions/{id}/resume` | 202 reprise explicite, 409 non reprenable |
| PUT | `/api/workspaces/{id}` | Nom (1-80 caracteres), description (0-1000) |
| DELETE | `/api/workspaces/{id}` | 204 suppression des sources inactives, 409 sinon |

Les endpoints existants, notamment fichiers, logs, annulation et autorisation,
restent inchanges. Exemples executables dans `AIPlatform.API/AIPlatform.API.http`.
En Desktop, toutes ces routes restent protegees par le jeton de session Tauri.

### Limites honnetes

Le plan est limite a 4-12 fichiers, dans les deux projets .NET 10 controles deja
supportes. Il ne rend pas compilables tous les types d'applications. Les seuls
packages disponibles restent les trois packages de tests de l'image hors ligne.
Un contexte trop grand est refuse explicitement plutot que tronquer silencieusement
une source ou promettre un grand projet. Les budgets du modele et delais d'agent
sont appliques aux appels ; le delai de traitement actif du worker et les limites Docker restent
actifs. Une revue LLM n'est pas une preuve de correction ni un audit de securite.
`Verified` exige un build sorti a zero et des tests effectivement executes,
non vides, sans echec, dans les conteneurs.

### Validation executee pour MVP 5

- Solution .NET : build reussi, **0 avertissement, 0 erreur**.
- Tests backend : **169 reussis** (dont 21 nouveaux cas collaboratifs/API) ;
  les 5 integrations historiques Docker/Ollama opt-in sont explicitement ignorees
  dans cette execution par defaut, pas comptees comme validees.
- Frontend : build TypeScript/Vite reussi, **24 tests React reussis** ;
  **6 tests navigateur** desktop/tablette avec API simulee reussis.
- Rust : **1 test** de validation de disponibilite locale/PID reussi.
- Sidecar publie autonome : smoke test reel reussi, incluant port occupe,
  authentification, origines CORS, detection Ollama/Docker, rotation du jeton,
  arret par commande et fermeture du canal proprietaire.
- **Workflow reel Ollama + Docker** : mission Calculator
  `3ae0ea59-59c2-4f13-8810-eb9e2efb83e6`, planifiee par qwen3:4b, quatre
  fichiers produits incrementalement et relus par qwen2.5-coder:7b.
  Apres accord humain sur l'empreinte exacte, build Docker sorti a 0
  (0 avertissement/erreur), **2 tests executes et reussis**, statut `Verified`,
  aucune correction necessaire. Sources en lecture seule, image locale figee,
  pas de reseau, aucun conteneur AIPlatform restant apres execution.
- Redemarrage du backend sur ces memes donnees : statut, etapes et resultats
  conserves. **1 test navigateur reel en lecture seule** confirme ensuite
  workflow, modeles par etape, statut verifie, fichiers et resultats Docker.
  Aucune autorisation n'est accordee par ce test navigateur.
- Windows : installateur **0.5.0 x64, environ 33,3 Mio**, installation et lancement
  natif controles. Interface MVP 5, sept agents, formulaire de configuration,
  connexion React/API protegee et anciens projets preserves observes.
  L'ancien Calculator Desktop a ete retrouve `Verified` (validation anterieure
  de l'utilisateur, distincte du nouveau test collaboratif).
  API installee observee sur une seule adresse `127.0.0.1` avec port dynamique.
  Fermeture : aucun processus Studio/backend restant ; Ollama/Docker restent actifs.

Les cycles de correction (limite de trois, diagnostics conserves, tests non
supprimes et nouvel accord apres modification), annulations, erreurs JSON,
permissions et reprises d'interruption sont verifies avec des doublures.
**Une nouvelle correction collaborative avec un vrai LLM apres echec Docker
n'a pas ete provoquee lors de cette validation** : le Calculator reel a reussi
au premier essai. Ne pas confondre ces tests avec les integrations reelles
historiques du MVP 3 decrites plus bas.

## Application Windows

Studio dispose maintenant d'une enveloppe **Tauri 2 / WebView2**. Le backend reste
en .NET 10 ; Rust ne gere que le lancement, la disponibilite et l'arret du processus
compagnon. React est compile et embarque : aucun serveur Vite en production.

### Installer et lancer sans terminal

1. Utiliser l'installateur Windows x64 produit dans
   `AIPlatform.Web\src-tauri\target\x86_64-pc-windows-msvc\release\bundle\nsis\AIPlatform Studio_0.6.4_x64-setup.exe`.
   Pour une mise a jour, sauvegarder StudioData et fermer Studio/backend avant
   de lancer l'installation explicitement autorisee, sans desinstallation prealable.
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
