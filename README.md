# AIPlatform Studio - 0.6.5

Plateforme multi-agents locale .NET 10 + React/TypeScript. Ollama selectionne un
agent autorise et son modele configure. Studio permet de suivre des missions
asynchrones, generer les fichiers d'un petit projet .NET 10, puis les compiler et
tester hors ligne dans Docker apres autorisation humaine. Les corrections sont
ciblees et limitees a trois par defaut.

### Dimensionnement des nouvelles missions React v2

Le selecteur **Dimensionnement du projet** est alimente par
`GET /api/missions/capacity`. Son choix initial, **Parametres existants
(inchanges)**, n'enregistre aucun profil et conserve les anciens comportements.
Un profil est selectionne explicitement avec `sizingProfileId` dans
`POST /api/missions`. Il est copie dans `mission.sizing`, persiste et immuable :
modifier la configuration ne modifie ni une mission existante ni son budget.
Les autres templates, notamment Twitch, ne peuvent pas selectionner ces profils.

| Limite | Avant / sans profil | Petit | Standard | Complexe |
|---|---:|---:|---:|---:|
| Fichiers proposes par le Planner | 12 | 12 | 32 | 64 |
| Fichiers du workspace, scaffold compris | 40 | 40 | 64 | 100 |
| Taille UTF-8 d'un fichier genere | 64 Kio | 64 Kio | 128 Kio | 256 Kio |
| Taille UTF-8 totale, lockfile compris | 256 Kio | 256 Kio | 1 Mio | 4 Mio |
| Criteres fonctionnels | 32 | 32 | 64 | 128 |
| Corrections partagees entre les etapes | 3 | 3 | 6 | 12 |
| Groupes Developer par reparation | brouillon 3, rapport Docker 8, autonome borne par les fichiers | 3 | 12 | 32 |
| Fichiers par groupe | brouillon/autonome 4, rapport Docker 2 | 4 | 4 | 4 |
| Delai du worker par passage | 900 s | 900 s | 3600 s | 7200 s |

Le scaffold comporte actuellement 11 fichiers. Le plafond de generation est donc
de 23, 43 ou 75 fichiers pour ces profils ; les 40/64/100 sont les plafonds de
stockage/validation, pas une promesse de generer autant de fichiers applicatifs.
Le lockfile technique garde son exception de taille existante ; il compte toujours
dans la taille totale. Les lectures, exports, copies isolees, corrections et
validations utilisent les limites de la mission, pas seulement le schema Planner.

Les profils sont configurables dans la section backend `Studio:SizingProfiles`
(liste complete remplacant les profils fournis, puis redemarrage). Exemple :

```json
{
  "Studio": {
    "SizingProfiles": [{
      "Id": "standard",
      "Name": "Projet standard",
      "MaxFiles": 64,
      "MaxPlannedFiles": 32,
      "MaxFileBytes": 131072,
      "MaxProjectBytes": 1048576,
      "MaxCriteria": 64,
      "MaxCorrections": 6,
      "MaxRepairGroups": 12,
      "MaxFilesPerRepairGroup": 4,
      "MissionTimeoutSeconds": 3600
    }]
  }
}
```

La liste contient 1 a 8 profils uniques. Bornes de securite : 100 fichiers,
89 propositions au plus et assez de place pour le scaffold, 1 Mio/fichier,
4 Mio/projet, 128 criteres, 12 corrections, 32 groupes, 8 fichiers/groupe et
7200 s par passage. Les nombres nuls, depassements et incoherences sont refuses
(zero correction est permis). Aucun profil n'augmente le budget des generations.

Pour les missions avec profil, le preflight et l'execution utilisent la meme
revue groupee : couverture integrale des criteres et de leurs dependances,
schema compris dans le contexte, decoupage selon contexte ET estimation de
sortie. La sortie est estimee par une enveloppe JSON synthetique par critere,
en UTF-8 avec coefficient 1,25 et 20 % de marge. Ce n'est pas une borne garantie :
des explications, preuves ou findings plus longs peuvent encore etre refuses
par les controles de troncature. Les rapports agreges restent bornes par le
nombre de groupes ; les fichiers hors couverture restent signales, sans supprimer
de critere fonctionnel pour faire tenir une revue.

**Limites restantes et ressources, non augmentees par les profils :**

| Domaine | Limites reelles / configuration existante |
|---|---|
| Demande utilisateur | `Platform:MaxTaskLength`, 16000 caracteres par defaut |
| Modele | `num_ctx` jusqu'a 262144 ; `num_predict` strictement inferieur au contexte, profils de modeles explicites |
| Prompts | budget conservateur `(contexte - sortie) * 2` caracteres ; refus plutot que troncature des sources |
| Ollama | delai configure 180 s par defaut, plage 1-3600 s ; borne effective aussi limitee par l'agent et le worker |
| Agents | delai configure 180 s par defaut, plage 5-900 s |
| Worker | delai par passage, pas un cumul persistant sur toute la vie de la mission ; attente/reprise redemarre un passage |
| Budget | plafond persistant de 1 a 1000 generations ; aucune reservation fictive de revue consommee avant appel |
| Planner | 3 generations au plus ; 12 dependances par fichier et 12 chemins par critere ; chemins relatifs 180 caracteres / 8 segments |
| Revue | au plus un groupe par critere (128), schema borne, pas de reduction de couverture ; refus d'une unite indivisible trop grande |
| Helper TypeScript | 15 s, heap Node 256 Mio, restitution d'au plus 60 diagnostics ; un candidat encore invalide n'est pas publie |
| Docker | 2048 Mio, 2 CPU, 120 s par defaut ; configuration globale bornee a 512-4096 Mio, 1-4 CPU et 5-600 s |
| Isolation Docker | reseau desactive, non-root, racine read-only, aucune capacite, 128 PID ; tmpfs `/tmp` 128 Mio et `/work` 512 Mio |
| Reponses Ollama | `num_predict`, refus `done_reason=length`, reponse incomplete/JSON invalide ; pas de plafond HTTP metier en octets distinct du buffering HttpClient |

Les budgets, modeles, contextes/sorties, delais des agents/provider et ressources
Docker se reglent separement. Augmenter un profil ne charge aucun modele et ne
garantit ni sa compatibilite memoire, ni le debit, ni la reussite fonctionnelle.
Sur 64 Go RAM / 24 Go VRAM, de plus grands modeles/contextes peuvent etre affectes
via les mecanismes existants ; les plafonds Docker et du helper restent ceux
ci-dessus. Aucun modele, conteneur ou projet reel n'a ete lance pour cette etape.

Fichiers concernes par le dimensionnement :

- Contrats : [ProjectSizingProfile](AIPlatform.Core/Studio/ProjectSizingProfile.cs),
  [MissionModels](AIPlatform.Core/Studio/MissionModels.cs).
- Orchestration : [CollaborativeProcessor](AIPlatform.Infrastructure/Studio/CollaborativeProcessor.cs),
  [corrections autonomes](AIPlatform.Infrastructure/Studio/CollaborativeProcessor.ReactCorrections.cs),
  [WorkflowAgents](AIPlatform.Infrastructure/Studio/WorkflowAgents.cs),
  [WorkflowModelRouting](AIPlatform.Infrastructure/Studio/WorkflowModelRouting.cs)
  (lecture du plafond de corrections uniquement, algorithme de routage inchange),
  [ExecutionPlanService](AIPlatform.Infrastructure/Studio/ExecutionPlanService.cs).
- Persistance/fichiers : [MissionStore](AIPlatform.Infrastructure/Studio/MissionStore.cs),
  [reservations economiques](AIPlatform.Infrastructure/Studio/MissionStore.EconomicRepair.cs),
  [WorkspaceStore](AIPlatform.Infrastructure/Studio/WorkspaceStore.cs),
  [ProjectExport](AIPlatform.Infrastructure/Studio/ProjectExport.cs).
- Validation/reparation : [MissionReactTemplate](AIPlatform.Infrastructure/Studio/MissionReactTemplate.cs),
  [ReactDraftRepair](AIPlatform.Infrastructure/Studio/ReactDraftRepair.cs),
  [ReactRevisionCorrection](AIPlatform.Infrastructure/Studio/ReactRevisionCorrection.cs),
  [ReactRevisionValidation](AIPlatform.Infrastructure/Studio/ReactRevisionValidation.cs),
  [ReactPlanAmendment](AIPlatform.Infrastructure/Studio/ReactPlanAmendment.cs),
  [ReviewerCorrectionResumeService](AIPlatform.Infrastructure/Studio/ReviewerCorrectionResumeService.cs).
- API : [Program](AIPlatform.API/Program.cs), [MissionWorker](AIPlatform.API/Services/MissionWorker.cs),
  [StudioContracts](AIPlatform.API/StudioContracts.cs), [StudioEndpoints](AIPlatform.API/StudioEndpoints.cs),
  [exemple HTTP](AIPlatform.API/AIPlatform.API.http).
- Interface : [App](AIPlatform.Web/src/App.tsx), [MissionDetails](AIPlatform.Web/src/MissionDetails.tsx),
  [types](AIPlatform.Web/src/types.ts).
- Tests : [ProjectSizingTests](AIPlatform.Tests/ProjectSizingTests.cs),
  [WorkflowApiTests](AIPlatform.Tests/WorkflowApiTests.cs),
  [CollaborativeWorkflowTests](AIPlatform.Tests/CollaborativeWorkflowTests.cs),
  [App.test](AIPlatform.Web/src/App.test.tsx),
  [MissionDetails.test](AIPlatform.Web/src/MissionDetails.test.tsx).

### Orchestration logicielle autonome React v2 (optionnelle)

Les nouvelles missions React v2 proposent **Corrections logicielles autonomes
React v2** (`automaticReactCorrections: true` a la creation). Desactive par
defaut : les missions historiques et les autres templates ne changent pas.
Cette option est independante du routage et de l'approbation du plan.

Le processeur collaboratif existant enchaine generation, controle statique,
reparations AST sures, Developer et nouvelle revue. Les findings fonctionnels
du Reviewer et les diagnostics des etapes Docker deja autorisees alimentent
la meme boucle. Une panne d'infrastructure ou des diagnostics sans fichier
approuve identifiable arretent la validation pour inspection, sans correction
Developer speculative. Les rapports Reviewer mal formes restent corriges par le
mecanisme existant, pas confondus avec des defauts applicatifs.

Les corrections sont preparees sur une copie : fichiers complets/groupes
lies (quatre fichiers par defaut, jusqu'a huit dans un profil borne), ou patchs exacts lorsque des lignes de diagnostic
fournissent une ancre unique et une sortie mesuree plus petite. Les contrats
JSON, applications de patchs et controles statiques existants sont reutilises.
Pas de patch approximatif. Les groupes dependant de routes figees differentes
ne sont pas fusionnes. Le contexte est remesure avant chaque appel ; sortie
conservatrice UTF-8 + 25 %, avec 20 % de marge. Ce n'est pas un tokenizer exact
ni une garantie que du code nouveau tiendra dans la sortie.

La capacite de tous les groupes Developer et de la revue complete (sources
stressees de 25 %) est controlee avant correction, puis atomiquement lors des
reservations Developer. Les criteres ne sont jamais retires pour economiser
une revue. Sans profil, la limite existante `Studio:MaxCorrectionAttempts` (0 a 3)
reste applicable. Un profil explicitement choisi fige sa limite (0 a 12) ;
elle reste partagee avec les autres corrections. Un candidat
inchange, un retour a des sources precedentes ou les memes findings repetes
arretent la boucle sans publication du candidat.

Les originaux, candidats, contrats, decisions, groupes termines et appels en
vol sont persistants. Une fermeture n'entraine jamais de relance spontanee.
La reprise explicite reutilise les groupes termines ; une reservation sans
resultat confirme bloque le rejeu automatique et exige une inspection.
Les checkpoints versionnes refusent les ecritures d'un processus obsolete.
Les prompts et sources des checkpoints ne sont pas exposes par les resumes API.

Le suivi Missions montre progression, budget, decisions, blocages et validations
reellement executees. **Aucun code genere n'est execute sur l'hote.** Une revue
favorable prepare la validation Docker ; l'autorisation humaine reste liee
aux sources et a leur empreinte. Toute correction de sources invalide l'accord
precedent et impose une nouvelle autorisation avant de poursuivre Docker.
Un constat Reviewer `Unverified` ne devient pas une preuve fonctionnelle.

Pour React v2, generation, reparations AST et revue statique ne sondent pas
Docker. La revision conserve son descripteur statique sans digest invente.
Apres la revue, les prerequis Docker sont controles ; leur absence donne
`GeneratedUnverified` avec une raison explicite, sans executer de code.
Le plan d'execution verifie est conserve separement dans
`ProjectRevision.PreparedExecutionPlan`, sans reecrire l'empreinte statique.
L'approbation a usage unique exige cette revision et l'identite immuable
actuellement verifiee de l'image.

Le bouton **Valider cette revision** peut aussi preparer une revision React v2
historique `GeneratedUnverified` dont le descripteur statique ne contient pas
encore de digest. Il verifie le plan approuve, les checkpoints, les sources,
la configuration actuelle et l'image locale contre le manifeste de confiance
verifie dans la session. Seules des inspections Docker sont effectuees.
La liaison immuable est conservee dans une preuve distincte sous
`react-revision-validation/preparations`, avec un `PreparedExecutionPlan` :
la mission, sa revision et ses budgets ne sont pas reecrits. Une preparation
identique reutilise sa preuve ; une nouvelle approbation explicite, a usage
unique, reste obligatoire. Sources, mission, configuration, preuve et image
sont recontroles avant execution ; aucun agent ni reservation LLM ne sont
necessaires. Le rapport independant reste technique et ne change pas le statut
de la mission.

Chaque validation independante conserve des snapshots immuables par execution
sous `react-revision-validation/reports` : demarrage, etapes terminees et
resultat final. Ils lient mission, revision, manifeste SHA-256 des fichiers,
image et plan d'execution. Un arret avant le resultat final est presente comme
interrompu apres redemarrage, sans relance automatique. Le fichier historique
de rapport unique reste lisible et n'est ni migre ni ecrase. L'interface montre
l'historique technique separement du statut du workflow et des appels ambigus.
Un rapport technique reussi ne valide ni le workflow ni ses criteres fonctionnels.
Les revisions historiques bloquees par un appel ambigu ne deviennent pas
executables par cette conservation ; les autorisations Docker restent distinctes.

Un candidat AST `Ready` ne peut etre reutilise que si son manifeste, les
preuves de checkpoint et son empreinte correspondent au draft courant.
Il est revalide avec les controles actuels, sans reappliquer l'AST ni reserver
une generation. Pour une generation initiale interrompue sans revision publiee,
une reprise explicite peut autoriser la revalidation d'un ancien candidat AST
devenu invalide. Le checkpoint original reste intact ; une trace distincte lie
mission, plan, configuration, sources et rejet statique reel, sans recu LLM
fabrique. Les diagnostics sont conserves avant preparation d'une nouvelle
correction Developer avec les limites normales. Cette preparation ne reserve
aucune generation et ne reapplique pas l'AST.
Apres interruption, la meme preparation est reutilisee. Une intention d'appel
sans aucune reservation correspondante peut reprendre explicitement ; un appel
reserve mais non valide reste bloque, sans rejeu ni remboursement.
`src/App.tsx` ne contient ni runtime de tests ni tests embarques ;
`src/App.test.tsx` importe et teste le vrai `App` depuis `./App`.
Les copies locales et contenus application/tests identiques sont des
diagnostics Developer, jamais des corrections AST silencieuses.

Validation de cette integration : modeles, controles et Docker simules seulement,
dans des repertoires temporaires. Une validation reelle doit etre autorisee
separement, disposer des modeles locaux et du helper TypeScript, puis obtenir
les accords du plan et de chaque revision Docker. Aucune mission historique
n'est migree ou reprise par l'activation de l'option.

### Bibliotheque de modeles et routage local

L'onglet **Bibliotheque de modeles** propose Catalogue, Modeles installes et
Telechargements : recherche/filtres, metadonnees `/api/tags` et `/api/show`,
modeles charges `/api/ps`, installation `/api/pull` en flux et suppression
`/api/delete`. Aucun telechargement ni chargement ne resulte de la consultation.
Installer/supprimer/annuler exige une session Desktop authentifiee ; une
installation ou suppression exige aussi une confirmation explicite.
Les operations de mutation et de generation Studio sont serialisees.
Une suppression est bloquee pour les modeles charges, les executions actives
ou les affectations configurees : reaffecter explicitement dans Agents/Teams
et dans les configurations concernees. Les references purement historiques
ne sont pas effacees et ne bloquent pas une suppression.

Le catalogue documentaire extensible est
`AIPlatform.Infrastructure/Studio/model-catalog.json` : tags consultes dans les
fiches officielles Ollama le 17 novembre 2026, sources par fiche, tailles
approximatives et aucune promesse de performance/disponibilite permanente.
Il couvre Qwen2.5-Coder, Qwen3-Coder/Next, Devstral, DeepSeek-Coder, Codestral,
Qwen3/3.5, DeepSeek-R1/Distill, GLM, Yi, Llama, Gemma et Mistral.
Les identifiants manuels `[namespace/]modele[:tag]` sont verifies contre le
manifeste du registre public Ollama avant installation ; les tags cloud et
URL arbitraires sont refuses. Cette verification transfere des metadonnees,
pas les poids. Une variante absente est signalee et n'est pas remplacee.
Les licences inconnues restent inconnues ; les poids ouverts ne sont pas
automatiquement qualifies d'open-source, meme avec une licence permissive.

Les telechargements affichent les octets reels de la couche signalee par
Ollama (pas un pourcentage global invente). Annuler ferme le flux ; Ollama peut
conserver ses couches partielles. Apres interruption de Studio, l'operation
est marquee interrompue a la prochaine consultation : une nouvelle
confirmation est necessaire pour reprendre les couches en cache.
Les 50 dernieres operations sont stockees dans `model-downloads.json`
du repertoire de donnees Studio, sans modifier les missions.

La sonde `IHardwareProbe` mesure la RAM Windows via GlobalMemoryStatusEx,
la VRAM NVIDIA via l'executable fixe System32/nvidia-smi.exe, sans shell
ni argument fourni par l'utilisateur, et le disque du repertoire Studio.
Autres GPU/OS ou mesures indisponibles : avertissement et donnees inconnues.
L'estimation distingue poids GGUF quantifies, KV F16 calcule d'apres
l'architecture/contexte, buffers et reserve systeme de 1,5 Gio. CPU, GPU
et offload hybride sont evalues separement ; RAM et VRAM ne sont pas une
memoire interchangeable. Architectures hybrides/MoE ou metadonnees manquantes :
informations insuffisantes, jamais une compatibilite inventee.
Un modele deja charge n'est pas compte deux fois. L'estimation ne predit pas
le debit reel. Les telechargements ne sont pas interdits a cause de la RAM ;
l'espace disque est verifie avant le pull. Avant generation, une estimation
insuffisante bloque l'appel ; les estimations manuelles limites/inconnues
sont explicitement journalisees, et Ollama peut encore refuser l'allocation.
Studio ne ferme pas d'applications et ne modifie pas le fichier d'echange.

Les **agents personnalises** ont un mode Manuel (par defaut) ou Automatique
et des parametres optionnels. En equipe, le mode automatique demande
**aussi** l'activation dans cette equipe ; un agent manuel reste manuel.
Les workflows systeme versionnes conservent leurs modeles configures par defaut
et proposent un opt-in distinct par nouvelle mission, decrit ci-dessous.
Sans parametres personnalises, les anciens budgets sont conserves en manuel ;
le mode automatique utilise 4096 tokens de contexte / 1024 de sortie / 0,2
de temperature, sous reserve des plafonds propres aux corrections.
Aucun LLM n'est appele par le routeur : classification heuristique explicable,
capacites completion, contexte, specialisation documentee, memoire estimee,
modele deja charge et observations reelles. Les modeles d'architecture
inconnue ne sont pas automatiquement declares admissibles.
La confiance est qualitative (faible/moderee), pas une probabilite.
La previsualisation ne genere rien.

#### Profils explicites et futur materiel

Chaque carte permet d'enregistrer les specialisations **Code, General, Planning,
Review, Debugging, Tests, Architecture, Documentation, Reasoning** reconnues par
le routeur existant. Elles sont conservees atomiquement dans
`StudioData/model-routing-profiles.json`, independamment des missions et
des parametres des agents. Sans override (ou apres restauration), les valeurs
du catalogue restent appliquees ; un nouveau modele installe n'a aucune
specialisation par defaut. Declarer Code n'est pas une certification de qualite.

Pour une architecture sans estimation analytique fiable, **Qualifier le
chargement** observe uniquement un modele deja charge par Ollama : aucune
generation ni aucun chargement n'est declenche. Une confirmation explicite,
la meme empreinte SHA-256 dans `/api/tags` et `/api/ps`, un contexte resident
connu, des allocations coherentes et une reserve RAM suffisante sont requis.
La qualification persiste l'empreinte, le contexte reel et les allocations
observees. Elle ne sert jamais a outrepasser une insuffisance de l'estimateur.
Les ressources sont reverifiees avant utilisation ; sans modele resident,
RAM et VRAM libres doivent couvrir le placement observe et la reserve systeme.
Pas d'extrapolation a un contexte superieur, ni d'invention d'un cache KV pour
une architecture inconnue. Un changement de poids, une preuve absente ou
inverifiable bloque l'admissibilite automatique ; retirer la qualification
est explicite. La selection manuelle reste possible avec avertissement pour
une estimation inconnue, mais pas pour une insuffisance detectee.
Les decisions deja figees ne sont pas reclassees ; leur admissibilite automatique
est recontrolee avant appel, y compris a la reprise.

Les profils systeme, personnalises, la bibliotheque, l'estimateur et le provider
acceptent des contextes jusqu'a **262144 tokens**. Aucune valeur existante n'est
agrandie ; les minima propres aux profils sont conserves et la sortie doit
rester strictement inferieure au contexte. Le modele doit annoncer un contexte
suffisant et les ressources doivent le permettre pour le routage automatique.
Les timeouts, sorties par defaut, budgets, plafonds de fichiers/corrections et
controles de troncature ne changent pas.

Un tag Ollama futur peut donc etre installe, specialise, qualifie puis affecte
depuis l'interface sans recompilation : bibliotheque pour le profil, editeurs
Modeles/Agents pour les agents systeme, affectation manuelle sur la carte pour
les agents personnalises. La premiere observation qualifiante demande un
chargement prealable via Ollama au contexte voulu. Une mesure n'est pas une
garantie de debit ou de rechargement ; les sondes Windows/NVIDIA et les
metadonnees reelles restent necessaires pour une admission automatique.
Les versions Ollama ne publiant pas digest/contexte/allocations suffisants
ne permettent pas cette qualification ; aucun besoin memoire manuel n'est accepte.

La chaine initiale reste stable pendant une tache/reprise ; l'escalade
existante ne passe au secours suivant qu'apres deux echecs de qualite.
Les autorisations, historiques, limites d'essais et rollback restent requis.
Les tentatives conservent categorie, choix/alternatives/raisons, duree,
compilation, commandes de tests, navigateur et rollback lorsqu'ils sont
observables. L'historique est deduplique entre runs et plans ; au moins trois
observations comparables sont requises pour influencer faiblement le score.
Une compilation seule n'est pas comptee comme reussite fonctionnelle ;
memoire, contexte, timeout et infrastructure ne penalisent pas la qualite.
Reinitialiser le classement enregistre seulement une date de coupure dans
`model-ranking-reset.json`, sans supprimer les historiques de missions.

### Routage des missions systeme

Dans le formulaire Projet, **Selection automatique des modeles pour les
missions** est decoche par defaut et revient a cet etat apres soumission.
L'API accepte `automaticModelSelection: true` uniquement pour un projet avec
workflow collaboratif. Aucune migration n'active une mission/equipe existante ;
le parametre absent reste faux. Les missions legacy non collaboratives et
les conversations ne sont pas basculees vers ce mecanisme.

Planner, Developer (dont Backend/Frontend), Reviewer et les corrections
passent par le meme routeur local. Role, template, fichier, contexte borne
reel, diagnostics et complexite heuristique sont transmis sans autre LLM.
Les budgets, outils autorises et parametres du workflow gele sont preserves ;
seul le nom du modele admissible change. Les roles systeme distinguent .NET
et React meme lorsqu'un contrat full-stack mentionne les deux technologies.
Si un modele code installe supporte le contexte demande, les categories de
generation React/.NET/projet exigent un modele specialise code admissible.
Un Planner generaliste deja resident ne remplace pas silencieusement ce
Developer quand la memoire du modele code est insuffisante ou inconnue.
Les allocations des autres modeles charges ne sont pas supposees liberees :
un changement necessite une nouvelle verification des ressources.

Le nom selectionne conserve le `num_ctx`, le `num_predict` et la temperature
du profil de role fige, pas ceux d'un autre profil portant le meme nom.
Une reponse Ollama `done_reason=length` est rejetee entierement, meme avec un
contenu vide et du raisonnement. Le diagnostic `OutputLimit` expose la limite,
`eval_count` si fourni et les tailles du contenu/raisonnement, sans publier
le fragment ni declencher une nouvelle generation. Il n'est pas classe comme
une panne d'infrastructure ou une erreur de qualite justifiant un retry.
Une reprise reste explicite, conserve les reservations et saute les fichiers
deja presents dans le brouillon. Une chaine de routage deja figee n'est pas
remplacee automatiquement par une nouvelle politique.

La chaine initiale est persistee par agent/fichier ou operation. Les reprises
et corrections la reutilisent sans reclasser ni revenir sur un modele rejete.
La politique existante exige deux echecs de qualite avant le secours suivant ;
les retries consomment la limite globale de corrections existante, jamais une
nouvelle enveloppe. Memoire/contexte sont controles avant l'appel ; un blocage
ne consomme ni tentative de generation ni correction. Une allocation refusee
par le fournisseur annule aussi ces compteurs et garde une observation
`Blocked`, non utilisee pour decider une escalade de qualite. Une alternative
devenue indisponible bloque la reprise, sans reordonner la chaine en silence.

Les details des etapes exposent mode, decisions/alternatives/contraintes,
modele effectivement appele, raison d'escalade et validations observees.
Ces invocations alimentent les statistiques existantes, dedupliquees par
identifiant ; builds et tests ne prouvent pas seuls les criteres fonctionnels.
Les choix d'une mission active sont des dependances protegees de suppression.
Les sources, versions, propositions, approbations et traces opt-in ne changent
pas de politique. En particulier, le workflow full-stack versionne conserve
son arret pour diagnostic humain apres une validation echouee : activer le
routage n'autorise pas de nouvelles corrections jusque-la interdites.

### Evolution d'un projet Studio existant

Dans Desktop, selectionner une equipe puis un projet Studio React ou .NET.
Choisir son plan enregistre, decrire la nouvelle demande et etendre le plan :
les taches terminees sont conservees et le Planner recoit les fichiers actuels,
un inventaire borne et la memoire historique. Les demandes et leurs taches sont
liees a une conversation du Planner et restent consultables apres redemarrage.
Demarrer ou reprendre le plan requiert de nouvelles autorisations distinctes
pour les modifications et pour l'execution locale.

Les scripts npm et les projets .NET peuvent executer du code sur Windows :
utiliser uniquement un projet explicitement declare fiable. Les tests executes
ne prouvent pas automatiquement chaque critere d'acceptation. Les criteres du
Planner sont des propositions a classer : exigence utilisateur, condition
technique ou suggestion facultative. Pour chaque critere obligatoire, une
personne doit associer un scenario navigateur enregistre et expliquer pourquoi
il verifie le critere. Studio verifie le resultat et la fraicheur du scenario,
mais ne peut pas deduire automatiquement sa pertinence ; sans scenario adapte,
le critere reste non verifie. Un build ou une affirmation d'agent ne suffit
pas. Le plan reste en attente
de validation tant qu'un critere obligatoire est echoue ou non verifie. Une
modification du workspace invalide les confirmations precedentes ; leur trace
reste visible. Les anciens plans sont egalement reexamines sans relancer leurs
taches. Les limites de taille du contexte et les fichiers
proteges du scaffold peuvent empecher certaines modifications.

**Le code genere est non fiable.** Le workflow de validation isole ci-dessous
n'execute pas de code genere sur l'hote ; l'execution locale Desktop decrite
ci-dessus est un mode distinct, non isole et explicitement autorise.
Pour le workflow isole, sans Docker Linux disponible, image hors ligne preparee, activation explicite de
la sandbox ET accord humain pour la mission, la compilation et les tests ne sont
pas executes. Le statut final est alors `GeneratedUnverified`, jamais un succes
de compilation.

Avec `Sandbox.Enabled=false`, ni l'empreinte pre-revue ni la verification de
disponibilite ne lancent Docker, meme pour une inspection. Le plan et la revue
peuvent etre conserves sans executer le projet ; l'empreinte lie aussi
l'activation du sandbox et une reconfiguration invalide l'accord precedent.
Cela n'active pas une validation locale des missions systeme. L'executeur
Desktop existant rattache ses processus au Job Object apres leur demarrage et
ne refuse pas un hote eleve : il ne suffit pas a garantir le controle demande
des enfants des leur creation et l'absence de privileges eleves. Aucun mode
« Validation locale supervisee » n'est propose sur cette base ; aucun resultat
local n'est invente ni transmis au Reviewer comme une verification effectuee.

### Contrat des nouvelles missions React

Le choix **React App** utilise `react-typescript-vite-v2` : seul le scaffold
technique est fourni (dependances figees, configuration stricte, point d'entree
et test de fonctionnement du runtime). Aucun ajout/suppression de taches,
filtre ou autre fonctionnalite metier n'est impose. L'identifiant historique
`react-typescript-vite` conserve son contrat de gestion de taches pour la
compatibilite ; les missions et plans existants ne sont pas migres.

La demande originale reste dans le plan persistant. Les elements de listes
explicites sont classes separement. Pour une demande en prose, les phrases
completes sont separees sans decouper les chemins ou les versions. Une
enumeration positive d'actions a objet commun peut etre developpee
(`modifier et supprimer des rapports` donne deux comportements) ; les
negations et les listes de champs ne sont pas ainsi developpees. Les fragments
originaux restent dans la trace, y compris les exigences transversales
(responsive, tests) et techniques. Les comportements fonctionnels deviennent
`C1`, `C2`, etc. (32 maximum), sans garantie d'interpretation semantique complete.
Les plans et criteres historiques ne sont jamais renormalises a la lecture :
modifier un plan approuve exige un amendement explicite et une nouvelle
approbation liee a son empreinte, pas un simple appel de reprise.
Pour un echec de generation initiale non publiee, l'action explicite
`POST /api/missions/{id}/react-plan-amendment` prepare cet amendement :
elle exige `expectedUpdatedAt`, l'ancienne empreinte approuvee, le plan
complet avec manifeste inchange et le modele code local choisi. Elle
renormalise les exigences originales, valide la couverture et la version
React verrouillee, verifie inventaire/ressources et budget minimal, puis
conserve l'ancien plan, son approbation, routage et brouillon dans l'historique.
Les sources existantes restent intactes, avec leurs diagnostics statiques ;
leur conservation n'est pas une preuve de compatibilite ou de compilation.
L'interface identifie l'amendement comme assiste, pas comme un nouveau
resultat Planner. L'etat devient `AwaitingPlanApproval`, sans mise en queue,
generation, reservation ni sandbox. L'approbation suivante lie aussi le
profil Developer fige et ses choix par fichier ; les ressources seront
reverifiees a l'execution. Les anciennes empreintes sans amendement gardent
leur format. Cette action est limitee aux missions React v2 a routage
automatique et approbation obligatoire, sans revision/workspace, avec un
budget fige, et a trois amendements au maximum. Exemple dans
`AIPlatform.API/studio-management.http`. L'approbation et Docker restent
deux autorisations distinctes ; le budget minimal ne garantit pas la
reussite des futures corrections ou du Reviewer.

Le Planner
recoit la version React lue dans le scaffold verrouille. Il
conserve ses chemins autorises sous `src/` et declare, pour chaque critere,
des fichiers d'implementation et de tests. Le prompt precise que chaque entree
`criterionCoverage`, y compris l'etat initial et l'interface, doit referencer
au moins une implementation editable et un test metier reconnu (`.test.ts`
ou `.test.tsx`, suffixes sensibles a la casse), tous declares dans `files`.
Le test technique protege ne compte pas ; un test declare ailleurs dans le
plan ne couvre pas automatiquement tous les criteres. Un exemple JSON neutre
illustre le format existant, sans ajouter ni reparer automatiquement de couverture.
Une couverture manquante bloque le plan ; une declaration de couverture ne
prouve pas sa pertinence.

Sur une mission React v2 inactive ayant une revision publiee (y compris en
`Failed`), **Valider cette revision** prepare une demande sans lancer Docker.
Studio verifie les fichiers publies et affiche l'image locale par ID, les
commandes et les protections du conteneur. Il faut une autorisation explicite,
a usage unique et liee a cette revision et a cette configuration. Une demande
expire au bout de 15 minutes et ne survit pas a un redemarrage de l'API.
La validation reutilise le sandbox hors reseau et teste une copie en lecture
seule, sans modifier la mission, ses fichiers ni son budget. Le dernier rapport
technique reste consultable apres redemarrage ; il ne valide aucun critere
fonctionnel a lui seul. Si la revision ne fournit pas d'ID d'image immuable
ou que cette image n'est plus disponible localement, Studio n'execute rien
et ne telecharge aucune image.

Depuis le rapport technique en echec de la revision courante, **Corriger
depuis ce rapport** permet de preparer des groupes de un ou deux chemins
editables par appel Developer. Laisser le champ vide propose les fichiers
identifies par les diagnostics ; si aucun fichier editable n'est identifiable,
il faut definir le perimetre. Studio transmet les erreurs TypeScript et Vite,
les echecs de chargement et les assertions Vitest, avec leurs positions si
disponibles, les sources utiles et le budget restant. La preparation verifie
les contextes et le budget global avant toute generation. Les groupes suivants
voient les dependances retenues telles qu'elles viennent d'etre corrigees ;
si le contexte ne tient plus, la correction s'arrete sans publication.

Avant publication, Studio compare les fichiers au manifeste du plan et analyse
la syntaxe TypeScript, les imports/exports locaux, les dependances du scaffold
et les usages incompatibles de Jest et de jest-dom. Seuls le helper Studio et
son compilateur TypeScript de confiance tournent sur l'hote : les sources sont
lues en memoire, sans charger leur configuration ni executer leurs scripts.
Node.js doit etre disponible pour ce controle. Au premier brouillon, une
passe Developer ciblee peut corriger ces erreurs si le budget restant et la
limite de tentatives le permettent. Si les erreurs persistent, le brouillon
est conserve mais aucune revision n'est publiee.

La correction depuis un rapport reutilise la limite de tentatives de la
mission et le budget global. Un candidat en echec n'est pas publie ; les appels
deja effectues restent comptes. Studio preserve les revisions precedentes, les
fichiers hors perimetre et le statut de la mission. La publication ne relance ni
Planner, ni Reviewer, ni Docker. Il faut preparer puis autoriser une nouvelle
validation Docker pour l'empreinte corrigee. L'analyse statique n'est ni une
compilation complete ni une preuve de couverture fonctionnelle.

#### Reparation economique d'un brouillon React v2

L'API `POST /api/missions/{id}/react-draft-repair/prepare` projette une
reparation sur une copie en memoire, sans modifier la mission ni appeler un
modele. Le helper TypeScript de confiance transforme uniquement des cas
prouves par diagnostic et AST : imports nommes utilises exclusivement comme
types (effets de module conserves), mauvais chemin vers un unique module
purement type exportant les symboles attendus, `jest.fn()` sans argument vers
`vi.fn()` sans collision, et ancien import a effet de bord jest-dom lorsque
le setup Vitest est exactement celui du scaffold. Les fichiers avec erreur
syntaxique et les cas ambigus restent intacts. Chaque edition conserve
positions, avant/apres et SHA256 ; une nouvelle analyse rejette tout nouveau
diagnostic. Ces transformations sont idempotentes.

La preparation accepte 1 a 3 groupes ordonnes de 1 a 4 fichiers editables
existants, avec instructions de coordination. Elle reutilise le constructeur
de contexte, le contrat JSON strict et le transport Developer des corrections
depuis rapport ; seules les sources cibles et leurs dependances transitives
sont fournies. La reponse doit contenir exactement les chemins demandes,
sans doublon, propriete inconnue, chemin supplementaire ou JSON incomplet.
Une sortie tronquee est rejetee par le provider. Les dependances deja
corrigees sont rafraichies avant le groupe suivant ; les cibles puis le
candidat complet repassent le controle statique.

Chaque groupe peut choisir `patchTargets: [{ "path": "src/App.tsx",
"original": "texte exact unique" }]` au lieu du contrat de fichiers complets.
Ce mode est aussi disponible pour les corrections depuis rapport de revision.
Les 1 a 8 ancrages doivent couvrir exactement les chemins du groupe, sans
doublon ni chevauchement, et chaque original doit exister une seule fois.
Le Developer doit renvoyer uniquement
`{ "patches": [{ "path": "...", "original": "...", "replacement": "..." }] }`
avec exactement les couples prepares. Tout element supplementaire, manquant,
inchange, ambigu ou tronque est refuse avant application. Le remplacement
vide supprime du texte ; aucun ajout ou retrait de fichier n'est permis.
La comparaison est ordinale, sans normalisation des fins de ligne ni
recherche approximative. Les offsets sont calcules sur la meme source
originale et les remplacements appliques en ordre inverse sur une copie.
Chaque groupe repasse l'analyse syntaxique et des imports : diagnostics
restants sur les cibles ou nouveaux diagnostics ailleurs interdisent sa
validation. Les checkpoints du brouillon conservent les patchs valides,
leurs positions, avant/apres et SHA256 ; les revisions publiees conservent
leurs sources completes dans l'historique pour comparaison.

Le preflight expose `replyContract`, `baselineJsonBytes`,
`completeFilesJsonBytes` et `completeFilesContextCharacters`. Les sources
pertinentes restent fournies en entier : les ancrages et leur schema peuvent
donc augmenter le contexte. La sonde de sortie des patchs mesure le JSON
avec un remplacement de meme taille que l'original, pas la future reponse.
Un ancrage couvrant tout un fichier peut couter plus que le contrat complet.
Le choix reste explicite ; les anciens appels et checkpoints restent en
mode fichiers complets. Ni `num_predict` ni les sources et criteres complets
du Reviewer ne sont reduits ou augmentes pour favoriser les patchs.

Le nombre de revues est mesure par le meme decoupage que le vrai Reviewer,
avec un stress de croissance des sources de 25 %. Sans tokenizer integre ni
compteurs de sortie persistants pour les generations reussies, la borne de
sortie utilise les octets UTF-8 du JSON initial, majores de 25 %, et exige
20 % de marge sous `num_predict`. Ce garde conservateur peut refuser un
regroupement realisable ; il ne pretend pas mesurer les tokens exacts ni
borner la longueur d'un nouveau programme. Les contextes restent soumis au
plafond existant en caracteres. Une preparation refusee n'autorise aucun appel.

`POST /api/missions/{id}/react-draft-repair/start` exige l'empreinte exacte
du preflight accepte et constitue l'action explicite de generation.
Sous le mutex persistant du budget officiel, un checkpoint separe dans
`missions/economic-repairs/` protege les capacites Developer et Reviewer ;
aucune n'est consommee avant son appel reel. Tous les chemins de reservation,
y compris d'autres processus, respectent cette protection. Les sauvegardes
perimees ne peuvent effacer ni le registre des appels ni le candidat applique.
`GET /api/missions/{id}/react-draft-repair/` expose la progression et les
extraits avant/apres des patchs valides, sans les fichiers sources complets.
Les groupes complets sont repris sans regeneration ; un envoi facture sans
resultat complet interdit tout rejeu automatique. Les echecs restent comptes.

Le brouillon original est archive dans le checkpoint et n'est remplace
qu'apres validation de tout le candidat et nouveau calcul Reviewer. Une
croissance hors capacite arrete la correction sans appliquer le candidat.
Cette action ne change pas le statut Failed, ne publie aucune revision et
ne met rien en file : la reprise officielle reste une action humaine distincte.
Elle n'execute ni code genere, ni Docker, ni Planner, ni Reviewer.
La validation Docker d'une future revision necessitera toujours sa propre
autorisation. Le controle statique ne garantit pas la correction fonctionnelle.

### Budget global des generations par mission

Chaque nouvelle mission Studio fige `MaxTotalGenerations` a la creation :
champ API `maxTotalGenerations` (1 a 1000), sinon
`Studio:DefaultMaxTotalGenerations` (10 par defaut). Le champ
« Generations maximales » est disponible dans la creation de mission.
Les nouveaux projets d'equipes personnalisees acceptent aussi ce champ.
Les plafonds Planner et les budgets de correction existants restent des limites
supplementaires, sans remplacement ni remise a zero lors d'une reprise.

`MissionStore.EnterGenerationScope` associe la mission aux appels asynchrones ;
`LlmGenerationScope.Reserve` est l'unique point de reservation. Le fournisseur
Ollama natif l'appelle avant toute operation reseau de generation ; le resolver
enveloppe les fournisseurs simules/autres sans doubler une reservation native.
Cela couvre le routage LLM des conversations Studio, Planner, chaque fichier
Developer, Reviewer, corrections, escalades et reprises. Les conversations et
equipes personnalisees liees a un projet Studio utilisent le meme budget, meme
si le Reviewer ne recoit volontairement pas de contexte de fichiers.
Les appels sans mission Studio (orchestrateur texte independant, espaces
externes/personnalises sans projet et configuration de commandes Twitch) n'ont
pas de budget de mission a debiter. Les requetes d'inventaire, telechargement
et dechargement sans prompt ne sont pas des generations.

Une reservation contient un identifiant unique, le role, le modele et la date.
Elle est ecrite avant l'appel, sous verrou memoire et mutex OS partage par le
repertoire, puis synchronisee sur disque et remplacee atomiquement. La source
persistante est relue sous ce verrou ; les ecritures ulterieures fusionnent le
journal sans pouvoir supprimer ses unites ni changer le plafond.
Un echec de persistance interdit l'appel. Une reservation incertaine apres
interruption reste comptee. **Aucun remboursement automatique** n'est implemente :
une annulation avant reservation ne consomme rien, mais un echec, une attente,
un timeout ou une annulation apres reservation consomme l'unite, meme si le
fournisseur n'a finalement pas envoye de requete. C'est volontairement prudent.

Au premier appel refuse, la mission termine `Failed`, avec le motif
`GenerationBudgetExhausted` ; aucun nouvel appel n'est lance et les sources,
plans, diagnostics et reservations sont conserves. Ce motif persiste meme si
un autre appel deja reserve se termine ensuite. Une reprise ne restaure aucune
unite. La consultation affiche utilise/restant et le journal par role, sans
assimiler une reservation a un test reussi. Un risque de budget insuffisant est
signale a la creation quand un minimum est connu, puis avant Developer d'apres
le plan valide ; aucune estimation ne garantit la completion.

Les missions historiques sans plafond restent explicitement non plafonnees :
aucune migration, aucun recalcul de leurs compteurs ou generations passees.

### Correction ciblee des rapports Reviewer

Les nouvelles missions collaboratives figent `reviewerCorrectionEnabled = true`.
Un rapport invalide reste distinct d'un defaut du code : les erreurs de champs
et de citations retournent au Reviewer, sans creer de correction Developer.
La nouvelle tentative recoit la demande, les criteres, les sources actuelles,
le rapport rejete disponible et ses diagnostics exacts. Le moteur ne remplace,
ne normalise et n'invente aucune citation ; les chemins et extraits restent
strictement compares (casse, espaces, retours a la ligne, 500 caracteres maximum,
sans numeros de ligne ajoutes). Les findings ne sont jamais retires par le moteur.
Un rapport valide signalant un defaut de code utilise la correction Developer existante.

Les reprises reutilisent le routage, l'escalade et `MaxCorrectionAttempts` partage,
sans boucle supplementaire ni remise a zero du budget. Un budget nul interdit
la correction du rapport. Les annulations et limites de contexte restent applicables.
Si une correction React v2 depasse le budget de caracteres, les instructions
redondantes, descriptions du plan et anciens journaux sont reduits ; seuls les
chemins de couverture sont conserves. Si necessaire, les explications de criteres
du rapport rejete non visees par un diagnostic sont omises et leurs champs sont
explicitement listes. Les constats, risques, verdicts, citations, diagnostics,
criteres originaux, preuves d'execution actuelles et sources editables completes
restent presents. Le rapport prive original demeure intact. Si ces donnees ne
tiennent pas, ou si une source de couverture manque, la correction s'arrete avec
un diagnostic avant toute reservation de generation. Aucune source ni citation
n'est tronquee pour satisfaire le budget ; le controle porte sur les messages
systeme et utilisateur complets, sans modifier la taille du contexte modele.
Le retour cible est lie au run, a l'etape, aux sources/plan/criteres/version et au
fichier de routage ; une revue d'une autre version ne reutilise pas un rejet obsolete.
Les missions historiques sans cette politique gardent leur fonctionnement anterieur.

Pour la generation fichier par fichier React v2, le Developer recoit la couverture
du fichier courant, ses prerequis declares et les exigences transversales et
techniques non dupliquees dans les criteres. Un fichier sans lien explicite de
couverture garde tous les criteres pour ne pas perdre de comportement. La demande
et le plan complets restent persistes. Les sources selectionnees sont transmises
sans troncature et la limite de contexte continue de s'appliquer.

Si une revue statique React v2 ne tient pas en un appel, Studio couvre les
criteres par groupes dans la meme etape Reviewer. Chaque groupe recoit les
fichiers complets mappes au plan et leurs dependances, ainsi que les exigences
liees. Les citations sont validees contre les sources du groupe avant
agregation ; les fichiers editables non consultes sont signales. Une source
requise manquante ou un critere qui ne tient pas seul bloque la revue sans
tronquer le code. Chaque appel reste soumis au budget global existant.

Les rapports structures et erreurs (critere, champ, regle, chemin, extrait disponible)
restent prives dans le magasin de diagnostics existant : ACL, taille, nombre,
retention et suppression restent applicables. Aucune trace brute Reviewer n'est activee.
Les endpoints `GET/DELETE /api/missions/{id}/reviewer-diagnostics` permettent
l'inspection explicite sans reprise et la suppression apres terminaison, sans
modifier l'historique ou les compteurs. Un rapport rejete n'est jamais une revue
acceptee ; l'absence de preuves d'execution interdit de declarer les tests reussis.
Les anciens rapports non conserves ne peuvent pas etre recuperes retroactivement.

### Correction autonome bornee du Planner

Les nouvelles missions projet collaboratives figent `plannerGenerationLimit = 3` :
une generation initiale et au plus deux corrections. Apres un rejet du validateur,
le prochain appel recoit la demande originale, les criteres figes, le plan rejete
disponible et les diagnostics exacts depuis le magasin prive existant. Les memes
regles de fichiers/couverture et le meme schema de reponse restent obligatoires.
Pour React v2, `dependencies` declare uniquement les packages npm du scaffold ;
`files[].dependsOn` declare les chemins exacts d'autres fichiers planifies, ou
`[]` sans dependance interne. Avant validation, le moteur retire de `dependsOn`
uniquement les noms exacts de packages du scaffold qui ne correspondent a aucun
chemin planifie (comparaison des chemins sans distinction de casse). Les retraits
sont conserves dans les diagnostics prives et affiches dans Studio, y compris si
le plan reste rejete. Aucun package n'est ajoute. References inconnues, ambigues,
doublons de chemins et cycles restent soumis aux validateurs existants. Les listes
de dependances depassant leur plafond ne sont pas normalisees.
Si toutes les validations passent, aucune correction LLM ni reservation
supplementaire n'est necessaire. Sinon, seul le modele peut proposer une
correction : le moteur ne fabrique aucune couverture et ne supprime aucun critere.
Le prompt et le schema enumerent les noms exacts des packages autorises. Un
diagnostic `UnknownDependency` rappelle que `dependsOn` attend des chemins de
fichiers et indique ces noms, sans convertir un alias tel que
`react-testing-library` en `@testing-library/react`.

Les tentatives restent dans l'historique et les diagnostics prives (memes limites,
retention et consentement separe pour les traces brutes). Le routage et l'escalade
existants sont reutilises. Les corrections consomment aussi le budget partage
`MaxCorrectionAttempts` ; un budget nul interdit toute correction. Le plafond
Planner borne egalement les reprises sur erreur de generation du routage
automatique. Un appel provider commence reste compte dans ce plafond meme si
l'allocation memoire echoue ; un blocage de ressources avant l'appel ne le consomme
pas. Annulation, timeout ou erreur de ressources ne declenchent aucune
boucle supplementaire. Apres redemarrage, une reprise explicite conserve les
compteurs et utilise le dernier rejet encore disponible ; un plafond epuise
interdit une nouvelle planification pour cette mission.

Un plan invalide ne peut jamais atteindre Developer. Avec l'approbation du plan,
les corrections ont lieu **avant** `AwaitingPlanApproval` ; seul le plan accepte
et son empreinte sont presentes a l'utilisateur pour decision. Sans cette option,
le workflow continue automatiquement apres validation. Les missions historiques
sans cette politique persistante conservent leur comportement, sans migration.

### Approbation optionnelle du plan avant Developer

Pour une nouvelle mission projet collaborative, **Demander mon approbation
apres la planification** (`requirePlanApproval`, desactive par defaut) suspend
le workflow seulement si le validateur existant accepte le plan. Le plan,
le scaffold, l'etape Planner terminee, son empreinte et l'etat
`AwaitingPlanApproval` sont sauvegardes atomiquement avant la pause. Aucun
Developer ni Reviewer ne demarre. Un plan rejete conserve les diagnostics
existants, sans attente d'approbation.

Le suivi presente le plan complet, les fichiers, les criteres et leur couverture
lorsque le contrat les declare, le modele Planner et ses generations consommees.
La consultation ne reprend rien. **Approuver et continuer** lie la decision a
l'empreinte exacte (mission, workflow, version du plan, demande, criteres et plan)
et remet une seule fois la mission dans la file du worker existant. Le Planner
n'est pas rappele et le plan n'est pas modifie. Les decisions concurrentes,
repetees ou obsoletes sont refusees (`409`). Le contenu approuve est recontrole
avant les etapes suivantes ; les versions ulterieures du code n'invalident pas
a elles seules la version initiale du plan approuve.

**Refuser le plan** termine la mission en `Cancelled`, conserve le plan et
l'historique, et interdit la reprise generique de ce plan refuse. Une mission
en attente reste en attente apres redemarrage, sans execution automatique.
Cette approbation ne donne **aucune autorisation de compilation, tests ou
execution** : la demande d'autorisation du workspace genere reste distincte.
Les missions anterieures et le comportement automatique par defaut ne changent pas.

Le Developer recoit la demande, les criteres, le plan et les sources utiles.
Il genere les tests metier de cette mission ; leurs titres portent les IDs
`[C1]`, etc. Les corrections ne peuvent pas modifier ces tests ni le scaffold.
Le Reviewer recoit aussi les fichiers reels et les resultats disponibles,
avec leur provenance. Chaque critere exige un verdict statique explique :
`Satisfied` avec citation de source verifiable, `NotSatisfied` ou `Unverified`.
Des `findings: []` ou une affirmation du modele ne constituent pas une preuve.

La validation fonctionnelle exige, en plus de la revue statique et des trois
etapes TypeScript/build/tests, des assertions Vitest effectivement rapportees
comme reussies pour chaque critere, dans ses fichiers de tests prevus. Ces
traces sont liees a la version et a l'empreinte autorisee des sources ; le test
technique seul ne compte pas. Sans correspondance, l'etat reste
`GeneratedUnverified`. L'interface distingue revue et execution.
La qualite semantique des criteres, des tests generes et de leur revue reste
une limite : un nom de test ne garantit pas la pertinence de ses assertions.
Ce contrat n'ajoute aucune permission, aucun mode local supervise ni aucune
execution automatique de code genere.

Les nouveaux essais Planner React v2 conservent leur resultat JSON structure
**avant validation**, y compris en cas de rejet. Ces copies de diagnostic ne
sont jamais des plans approuves ou executables. Elles sont associees a la
mission, au workflow/run, au modele reel et a l'identifiant/numero de tentative.
Les erreurs indiquent le critere, une regle stable, le champ, le chemin
eventuel et une explication : couverture absente, critere inconnu, explication
invalide, doublon, chemin interdit/non planifie, implementation ou test reconnu
manquant. Les conditions d'acceptation restent inchangees.

Dans le suivi de mission, **Consulter les diagnostics Planner** ouvre ces copies
explicitement, sans reprise ni execution. Le stockage reutilise `ollama-traces`
prive, ses ACL Windows, sa limite par fichier et sa retention/nombre de fichiers
configurables (par defaut 1 Mio, 7 jours, 20 fichiers, partages avec les autres
traces). Une limite de stockage bloque explicitement la capture, sans accepter
le plan. Les copies expirees ou supprimees ne sont plus consultables.
Une suppression explicite des diagnostics est disponible meme sans workspace ;
la suppression d'un projet retire aussi ses diagnostics Planner.

La case **Conserver les traces detaillees du Planner pour cette mission**
(`recordPlannerTrace`, desactive par defaut et non reconduit apres envoi)
autorise en plus les corps HTTP exacts, potentiellement sensibles. Sans cet
accord, aucune reponse brute n'est conservee. La consultation brute exige aussi
l'autorisation Desktop et une action distincte ; elle n'est pas incluse dans
les reponses ordinaires de l'historique ou les journaux generaux.
Les anciennes missions ne sont pas modifiees : un plan precedemment rejete
et non capture ne peut pas etre recupere retroactivement. Du JSON mal forme
ne peut pas etre conserve comme plan structure ; son diagnostic reste
disponible, et le brut seulement si la capture avait ete autorisee.

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

### Simulation locale des bots configures

Dans le module Twitch, la simulation execute uniquement le routeur des commandes
enregistrees, avec un utilisateur et un role simules. Elle ne lit pas les jetons
OAuth et ne contacte pas Twitch. Une sequence peut tester le cooldown avec une
horloge virtuelle ; son etat est independant de celui du bot connecte. Les
commandes peuvent etre reservees aux moderateurs ou au diffuseur ; en mode
connecte, les roles proviennent des badges EventSub. Les anciennes commandes
restent accessibles a tous par defaut. Seuls les messages de chat sont pris en
charge ; les follow, abonnements et raids ne sont pas simules. Les resultats ne
sont pas des preuves persistantes et cette fonction n'execute ni n'isole les
projets Twitch generes.

Le module Twitch permet aussi de demander a une equipe existante une proposition
de configuration structuree. Un bot doit etre arrete ; l'utilisateur examine
les operations et approuve explicitement leur application (avec une confirmation
supplementaire pour une suppression). La configuration precedente est conservee,
les simulations utilisent le routeur existant et l'horloge virtuelle, et un
echec restaure la configuration. Les propositions et resultats sont conserves
dans les donnees Twitch ; si la configuration change, leurs preuves sont marquees
perimees. Une application interrompue peut etre restauree si la configuration
correspond encore a son instantane. Ces simulations verifient les operations
proposees, pas la totalite des intentions exprimees en langage naturel. Les
reponses sont du texte fixe : aucune generation aleatoire ni execution de code.
Une nouvelle commande utilise par defaut un cooldown de cinq secondes ; une
autre valeur doit etre explicitement demandee. La detection des contenus
ressemblant a du code est conservatrice et n'est pas exhaustive : les operations
doivent etre examinees avant approbation.

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

Avant une reprise, Studio consulte `GET /api/missions/{id}/resume-eligibility` sans
reserver de generation. Une confirmation distincte indique que les agents peuvent
etre relances ; Docker exige toujours sa propre approbation. Le POST recontrole
les memes gardes et renvoie un motif exploitable en cas de 409. Un appel Ollama
potentiellement envoye, ou sans preuve suffisante de non-envoi, bloque la reprise :
conserver les reservations et checkpoints et examiner les preuves avant toute
recuperation manuelle ; aucun rejeu ni remboursement automatique n'est effectue.
Si le resultat de l'appel reste inconnu, il n'existe pas de reprise automatique
de cette mission : ses sources peuvent etre exportees depuis Projets pour examen,
sans execution ni modification de l'historique.
Pour une revue interrompue apres publication, un candidat de correction anterieur
reste verifiable avec sa version d'origine seulement si sa revision, son draft,
son plan et ses recus concordent ; les preuves et reservations restent intactes.

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

Le backend .NET est autonome : aucun SDK .NET ni Rust n'est necessaire pour
Studio lui-meme. Le controle statique React v2, les reparations AST et les
helpers navigateur exigent Node.js x64 >= 20.14 installe dans
`%ProgramFiles%\nodejs` (une jonction d'installation est resolue).
Node.js n'est pas embarque : en son absence la publication React est bloquee
explicitement, jamais validee par defaut. Les helpers de confiance et leurs
dependances TypeScript/Playwright sont, eux, inclus dans les ressources
`studio-browser` de l'installateur ; aucun `npm install` n'est lance sur les
sources generees. WebView2 est requis ; l'installateur propose son bootstrap
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

### Sauvegarde et restauration Studio (Desktop)

Dans **Missions**, Desktop propose **Sauvegarder Studio** et **Restaurer Studio**.
L'archive ZIP versionnee contient un manifeste, un inventaire et une empreinte
SHA-256 par fichier. La capture bloque les ecritures API, attend les traitements
Studio, puis compare les empreintes avant et apres la copie. Il faut arreter les
missions, collaborations, bots Twitch et applications locales en cours.

La restauration commence par une verification et un apercu : elle remplace toutes
les donnees internes, sans fusion, affiche les missions dont l'identifiant existe
deja et controle l'espace disque. Une confirmation distincte est requise pour le
remplacement et pour les conflits d'identifiants. Le remplacement est prepare a
part, avec retour a l'etat precedent en cas d'echec. Apres validation, le backend
reste bloque jusqu'a son redemarrage par le lanceur Desktop. Les approbations
Docker/locales et les autorisations externes persistees sont revoquees ; les
missions qui etaient en attente ou en cours sont arretees.

Les limites actuelles sont de 512 Mio pour l'archive, 2 Gio de contenu decompresse,
256 Mio par fichier et 100 000 fichiers. Les valeurs de parametres dont le nom
indique un secret sont omises ; une archive contenant une valeur detectee comme
identifiant ou jeton est refusee. L'archive n'est pas chiffree : la conserver dans
un emplacement protege.

Ne sont pas exportes : poids/cache Ollama, images Docker, identifiants du
Gestionnaire d'informations d'identification Windows, jeton Desktop, binaires,
outils installes, projets choisis dans des dossiers externes et sauvegardes de
workspaces externes. Les chemins externes peuvent necessiter une nouvelle
selection. Installer ou verifier separement Ollama et ses modeles, Docker et ses
images sandbox, Node.js et le helper React/TypeScript, ainsi que le SDK .NET et
les workloads necessaires. Aucune dependance n'est installee automatiquement et
aucune donnee n'est envoyee dans le cloud.

### Diagnostic et preparation d'un nouveau PC

L'ecran **Environnement** effectue une verification a la demande. Il distingue
la detection d'un composant de son fonctionnement valide : Windows x64, RAM,
GPU/VRAM, presence du runtime WebView2, version Node.js, reponse de l'API
Ollama, modeles configures (y compris les modeles des agents personnalises),
presence de Docker Desktop, moteur Linux et identifiants des images sandbox.
L'inspection ne lance aucun conteneur. Le diagnostic Node execute uniquement
`node.exe --version` depuis `%ProgramFiles%\nodejs`; les sondes Docker sont
`docker info` et `docker image inspect`.

Les templates utilisent ces images versionnees :

| Image locale | Version recette | Utilisee par | Dependances principales |
| --- | --- | --- | --- |
| `aiplatform-sandbox:net10` | `net10-v1` | .NET et Twitch | SDK .NET 10 epingle par digest, xUnit 2.9.3 et Microsoft.NET.Test.Sdk 17.14.1 |
| `aiplatform-sandbox:discord-net10` | `discord-net10-v1` | Discord | image .NET de base, Discord.Net 3.20.2 et cache NuGet |
| `aiplatform-react-sandbox:1.0.0` | `react-v1` | React | Node.js 22 epingle par digest, dependances du package-lock React |
| `aiplatform-fullstack-sandbox:1.0.0` | `fullstack-v2` | FullStack | images .NET/React, lockfile NuGet et feed local SQLite/xUnit |

Avant le packaging d'une version Desktop, le poste de livraison doit construire
les quatre images et produire le paquet embarque :

```powershell
.\tools\Build-SandboxProvisioning.ps1 -ConfirmDockerBuild
Set-Location .\AIPlatform.Web
npm run desktop:build
```

Le script exige une confirmation explicite, un moteur Docker Linux et les
recettes du depot. Il exporte un tar unique, un manifeste versionne avec les
identifiants Docker, les empreintes des recettes et le SHA-256 de l'archive.
Tauri embarque ce repertoire comme ressource ; le nouveau PC ne depend pas du
depot Git. L'ecran verifie le manifeste et l'archive, puis exige une confirmation
distincte avant `docker load`. Il ne lance ni `docker pull`, ni build, ni
conteneur. Les images deja presentes ne sont marquees **Pretes** que si leur
identifiant correspond au manifeste verifie ; l'approbation explicite par
mission reste requise. Toute modification d'une source sandbox exige de
regenerer et versionner le paquet.

Sur le nouveau PC, installer manuellement l'installateur Studio, Node.js x64
20.14 ou ulterieur dans `%ProgramFiles%\nodejs`, WebView2 Evergreen si requis,
Ollama avec les modeles necessaires, puis Docker Desktop avec le moteur Linux.
Ouvrir **Environnement**, lancer le diagnostic, verifier le paquet, et seulement
ensuite confirmer son import. Apres restauration Studio, cette page signale les
modeles et images absents ; les chemins de projets externes doivent etre
reselectionnes et toute autorisation d'execution doit etre redonnee. Aucune
ancienne autorisation n'est restauree.

Le SHA-256 detecte les archives corrompues par rapport au manifeste embarque,
mais ne constitue pas a lui seul une signature editeur. L'installateur Windows
actuel etant non signe, l'authenticite de l'editeur n'est pas certifiee. Les
versions Discord.Net sont epinglees directement dans le projet de cache, mais
les dependances NuGet transitives de cette recette n'ont pas de lockfile
commis ; le manifeste fige et identifie l'image livree, pas une reconstruction
bit-a-bit garantie.

### Construire l'application Windows

Prerequis developpeur : .NET SDK 10, Node/npm, Rust MSVC indique par
`rust-toolchain.toml`, Visual Studio 2022 ou 2026, ou Build Tools correspondants
avec MSVC x64/x86 et Windows SDK, ainsi que WebView2.
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

#### Stabilisation Windows 0.6.6

La version 0.6.6 reprend le moteur React v2, son helper TypeScript, les
reparations AST, groupees et par patchs, sans modifier les contrats
d'autorisation ou les budgets. La fabrique de workspaces utilise maintenant
les options injectees, y compris dans les tests isoles. La compaction
existante des rapports Reviewer rejetes s'applique egalement aux revues
groupees ; criteres, sources completes, citations et diagnostics restent
obligatoires, et un contexte encore trop grand est refuse avant generation.

Verification de livraison : 373 tests backend cibles, 102 tests de composants
React, six parcours Edge desktop/tablette et onze tests des helpers reussis.
Deux tests du backend et des helpers empaquetes et six tests Rust natifs
reussissent egalement.
Les onze anciens echecs Workflow API sont resolus : stockage capture au lieu
des options injectees, puis configuration du sandbox simule incoherente avec
le plan canonique de la fixture. Les fixtures de normalisation et les
selecteurs d'interface obsoletes ont ete alignes sur les contrats actuels.

Le backend autonome publie a reellement demarre en mode Desktop : authentification,
CORS, rotation du jeton et arret par commande ou fermeture du canal proprietaire
verifies. Un autre demarrage avec stockage Development vide et isole a ete
verifie. Les helpers empaquetes chargent leur TypeScript embarque et analysent
des sources synthetiques sans les executer. Aucun appel Ollama, execution de
code genere sous Docker ou changement des missions historiques n'a ete requis.

L'installateur NSIS x64 non signe est produit par `npm run desktop:build` dans
`AIPlatform.Web/src-tauri/target/x86_64-pc-windows-msvc/release/bundle/nsis/`.
L'assistant NSIS a reellement ete ouvert jusqu'a la page de detection d'une
version deja installee. Ces controles ne certifient pas une installation ou
mise a jour Windows, ni le lancement du frontend natif apres installation.
La verification du script NSIS montre que le mode `/UPDATE` n'efface pas
les donnees applicatives ; ne jamais cocher une suppression des donnees lors
d'une desinstallation. Conserver une sauvegarde avant toute mise a jour.

#### Livraison corrective Windows 0.6.8

La version 0.6.8 corrige la detection de WebView2, renforce la verification du
manifeste et des ressources sandbox, et limite le contrat du Planner React v2
aux dependances du scaffold et a Vitest. Les corrections TS1002 ne sont
appliquees que lorsque leurs ancres sont exactes et uniques ; le preflight et sa
marge de securite restent inchanges et les diagnostics indiquent les estimations,
le contrat choisi et le motif d'un refus, sans enregistrer prompts ni reponses.
Une reprise apres echec avant envoi exige une preuve persistante de non-envoi ;
les appels ambigus restent refuses et les reservations de generations sont
conservees.

Validation de la livraison : 116 tests backend cibles et 114 tests frontend
reussis ; le build `desktop:build` a produit l'installateur NSIS x64. Le build
frontend signale toujours un chunk JavaScript superieur a 500 kB, sans faire
echouer la compilation. Le paquet sandbox existant a ete reutilise, sans
reconstruction d'image Docker.

## 1. Prerequis et demarrage

Environnement verifie : Windows, SDK .NET 10, Node.js 20.14, npm 10.8,
Ollama sur `http://localhost:11434`, modeles deja installes :

- `qwen3:4b` : routage et conversation generale ;
- `qwen2.5-coder:7b` : developpement et generation de projets.

### Escalade locale des modeles des equipes

Les reprises ciblees distinguent le fichier actuel apres rollback, le fragment
du candidat precedent rejete (avec son run), ses diagnostics historiques non
verifies sur le fichier actuel, et l'objectif HTTP. La fonction complete et
ses variables restent fournies ; les diagnostics ne sont pas rebaptises en
erreurs actuelles.

Le champ `recordOllamaTrace` de la requete `retry` est **false par defaut**.
Le passer a `true` constitue un consentement explicite a enregistrer localement
le code et les prompts de cette invocation uniquement. Ils peuvent contenir des
secrets : aucun masquage parfait n'est garanti. Les en-tetes d'authentification
ne sont jamais enregistres et les corps ne vont pas dans les logs generaux.
Les traces privees Windows sont dans `StudioData/ollama-traces`, avec ACL
limitee au compte courant (les administrateurs peuvent reprendre les droits).
Elles conservent le modele, les corps JSON exacts requete/reponse et leurs
metadonnees de fin, les operations extraites, application/rollback et validation,
associes au workspace, run, task et identifiant de tentative.
Les limites `Studio.OllamaTraceMaxBytes` (1 Mio par trace),
`OllamaTraceMaxFiles` (20) et `OllamaTraceRetentionDays` (7) sont configurables.
La retention est appliquee aux ecritures et au demarrage de Studio ; aucun
nettoyage ne tourne lorsque Studio est arrete. Un depassement explicite arrete
la tentative plutot que tronquer une trace en pretendant qu'elle est complete.
`DELETE /api/studio/projects/{id}/ollama-traces` supprime ces traces locales,
sans toucher au projet ni aux historiques ; une capture active doit etre arretee.

Dans **Agents / Teams**, l'escalade est optionnelle et desactivee par defaut,
y compris pour les equipes deja enregistrees. Le modele configure de chaque
agent reste le premier choix. Les secours sont ordonnes et modifiables :
priorite a la liste du membre de l'equipe, puis a celle de l'agent ; une liste
vide interdit les secours, une liste absente herite. Pour le Developer, les
suggestions par defaut sont `qwen2.5-coder:7b`, `qwen3.5:9b`,
`qwen2.5-coder:14b`, uniquement lorsqu'ils sont installes.

Les collaborations et reprises d'une meme tache passent au secours suivant
apres deux echecs consecutifs de generation (code invalide, correction sans
effet, tests fonctionnels echoues). Ollama `/api/tags` et `/api/show` verifient
l'installation et la capacite `completion`, sans telechargement ni generation.
Un contexte excessif est reduit avant l'appel ; s'il reste trop grand, il est
refuse. Contexte, autorisation, infrastructure, environnement, memoire et
timeout ne declenchent pas d'escalade et interrompent la tentative concernee.

Chaque tentative conserve modele, motif, resultat de compilation/tests,
diagnostics et decision suivante. L'interface affiche ces informations pendant
l'execution. Les historiques cumules restent attaches a la tache lors d'une
reprise autorisee, sans relancer les taches terminees ni augmenter les limites.
Les requetes Studio restent serialisees et le modele precedent est decharge
lors d'un changement pour une equipe ayant active l'escalade. Cela ne controle
pas les autres clients Ollama ; un modele 14B peut depasser la memoire disponible.
Les autorisations d'execution, rollback et annulation restent obligatoires.

Pour les projets full-stack d'agents, le contexte de correction contient une
seule copie de chaque source pertinente (version proposee incluse) et le contrat
API reel. Le budget reserve la reponse, le schema JSON et 768 tokens de marge ;
les fichiers optionnels sont retires avant l'appel si necessaire. Un contexte
indispensable trop grand produit une erreur explicite sans appel Ollama.
La requete `POST /api/custom-agent-teams/{id}/development-plans/{planId}/retry`
accepte `taskOnly: true` pour reprendre uniquement `taskId`, puis mettre le plan
en pause apres sa reussite. Sans cette option, le comportement reste inchange.
`resume` accepte egalement `taskOnly` et `taskInstructions` (2000 caracteres)
pour preciser la correction de la prochaine tache sans reecrire son historique.
Une correction identique conserve les diagnostics fonctionnels et consomme une
tentative ; elle ne vaut pas validation. L'annulation conserve les derniers
resultats persistes. Le helper navigateur est resolu depuis les scripts du depot
pour l'hote source, ou les ressources voisines de l'hote installe ; les liens
symboliques et jonctions restent refuses, sans chemin fourni par le workspace.
`POST /api/custom-agent-teams/{id}/development-plans/{planId}/targeted-corrections`
ajoute une tache d'integration a un plan en pause avec consentements workspace et
execution. Le Developer recoit l'extrait, le fichier concerne complet (fonction
et variables englobantes), la signature API, les diagnostics HTTP/compilateur et
le format attendu. Le budget reserve la reponse, le schema et une marge de
securite ; un contexte trop grand est refuse sans tronquer la fonction ni appeler
la generation. Un remplacement doit correspondre a un
extrait unique du fichier courant ; aucun Planner ni regeneration complete
n'est utilise. Une seule generation est permise, sans correction automatique ;
la modification n'est acceptee qu'apres compilation, tests et verifications
existantes. Un echec ou une annulation restaure les fichiers anterieurs par le
mecanisme de modifications revues, sans ecraser une modification concurrente.
Les modifications tentees, le diagnostic HTTP initial et les erreurs de
compilation restent dans l'historique ; un echec de restauration est explicite.
La reprise autorisee de cette tache echouee ne relance aucune autre tache et
conserve les compteurs. Elle recharge le contexte et les erreurs precedentes,
y compris pour une ancienne correction ayant laisse du code invalide en place.
L'option `compilationOnly: true` de la reprise est reservee aux corrections
ciblees explicitement autorisees : seuls les builds sont executes, puis le plan
se met en pause sans tests, navigateur ni Reviewer. Ce mode ne constitue pas une
preuve HTTP ; celle-ci doit etre obtenue avant les validations d'integration.

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

Pour ouvrir le Studio React v2 connecte sans utiliser les donnees installees :

```powershell
cd AIPlatform.Web
npm run studio:dev
```

Cette commande lance l'API sur `http://localhost:5167` et Vite sur
`http://127.0.0.1:5173`. Elle reutilise `Studio__DataDirectory` avec
`%LOCALAPPDATA%\AIPlatformStudio.Dev\StudioData` et desactive les appels Ollama
et les sondes Docker pour cette session de verification. `Ctrl+C` arrete les
processus. Le demarrage de l'API Development refuse les chemins qui chevauchent
le dossier installe (y compris les alias Windows) ; le mode Desktop conserve
son chemin d'origine et ignore ce reglage.

Ne pas utiliser `desktop:dev` pour cette verification : Tauri debug demarre le
compagnon en mode Desktop et utilise les donnees et les journaux Desktop.

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
