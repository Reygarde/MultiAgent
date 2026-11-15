# AIPlatform Studio — MVP 6.5-M : Autorisation durable des canaux

## Résumé

Le superviseur durable est maintenant l'autorité qui autorise et réserve les
canaux HvSocket d'une session. Le client de Studio envoie le bail complet au
superviseur par IPC; celui-ci compare ce bail à son journal, vérifie l'état et
l'échéance absolue de la session, puis inscrit atomiquement le rôle du canal et
la séquence de commande dans le journal avant de répondre.

Ce lot ne crée ni ne démarre de VM, n'ouvre aucun socket HvSocket/AF_VSOCK,
n'enregistre aucun service Windows et ne branche pas le runtime à la DI de
production. La Preview demeure indisponible (HTTP 503) et
`IsolationVerified == false`.

## Architecture

### Réservation par IPC

- Le contrat IPC passe à la version 2 et ajoute `ReserveSocketChannel`. Une
  réservation exige un SessionId, une séquence de commande, un bail complet
  (`SessionId`, `VmId`, `ResourceId`) et un rôle `Control` ou `Frames`.
- `SupervisorNamedPipeLeaseAuthorizer` demande d'abord l'état de la session,
  puis envoie la réservation à la séquence suivante. En cas de concurrence et
  de réponse `OutOfSequence`, il relit l'état et réessaie de façon bornée.
  L'ensemble de l'autorisation expire après cinq secondes; les erreurs IPC,
  les données malformées, les réponses incohérentes et les refus restent
  explicites ou échouent fermés.
- Sous le verrou de coordination du superviseur, la commande valide le
  séquencement et l'anti-rejeu, compare le bail complet à celui du journal,
  exige l'état `Running`, puis vérifie l'expiration absolue à partir de
  l'uptime, de l'heure UTC et de l'époque de démarrage. Aucun rôle n'est
  réservé pour une session inconnue, falsifiée, expirée ou terminale.

### Persistance et connecteur

- Les rôles réservés sont ajoutés de manière append-only aux enregistrements
  durables du superviseur. La réservation et l'identifiant de commande sont
  écrits dans le même append/flush avant confirmation; un rôle ne peut pas être
  retiré, remplacé ou réservé deux fois. Les canaux Control et Frames sont
  distincts et chacun peut être consommé une fois par session.
- Les anciens enregistrements sans liste de canaux sont lus avec une liste
  vide; un test reconstruit un enregistrement au format antérieur et le recharge.
- Le connecteur Windows appelle désormais cette autorisation asynchrone avant
  la connexion, puis conserve son garde-fou local en mémoire. La réservation
  durable n'est jamais annulée si la connexion échoue ou si l'endpoint distant
  est inattendu; cela empêche la réutilisation ambiguë du même canal.
- Le contrat du client IPC est reproduit dans l'adaptateur Infrastructure,
  sans référence circulaire vers le projet exécutable du superviseur. Les tests
  vérifient l'interopérabilité réelle des deux codecs via un named pipe local.

## Fichiers créés ou modifiés

Créés :

- `AIPlatform.Infrastructure/Studio/SupervisorNamedPipeLeaseAuthorizer.cs`
- `MVP-6.5-M-IMPLEMENTATION.md`

Modifiés pour M :

- `AIPlatform.PreviewSupervisor/SupervisorContracts.cs` — version IPC 2,
  commande de réservation, bail/rôle et liste persistée des canaux.
- `AIPlatform.PreviewSupervisor/SupervisorIpcCodec.cs` — validation des formes
  v2, du bail et des rôles, et validation des réservations dans les réponses.
- `AIPlatform.PreviewSupervisor/DurableSupervisorJournal.cs` — validation de
  l'append-only des rôles, de l'absence de doublons et de la liaison à une
  séquence de commande.
- `AIPlatform.PreviewSupervisor/SupervisedPreviewCoordinator.cs` — autorisation
  atomique sous coordination, validation du bail exact et de l'expiration.
- `AIPlatform.Core/Studio/HyperVPreviewSocketContracts.cs` — authorizer
  asynchrone qui réserve explicitement un rôle.
- `AIPlatform.Infrastructure/Studio/WindowsHvSocketPreviewConnector.cs` —
  réservation supervisée avant toute connexion.
- `AIPlatform.Tests/HyperVPreviewIndependentSupervisorTests.cs` — validation
  IPC, réservation concurrente, bail falsifié, expiration, named pipe réel de
  test, persistance/reprise et compatibilité des anciens enregistrements.
- `AIPlatform.Tests/HyperVPreviewSocketTransportTests.cs` — tests du nouveau
  contrat d'authorizer et de la réservation conservée après échec socket simulé.

Les autres fichiers non suivis ou modifiés du workspace existaient avant M et
n'ont pas été inclus dans ce lot.

## Tests et validation

Commandes exécutées sans restauration ni installation :

- `dotnet test .\AIPlatform.Tests\AIPlatform.Tests.csproj --no-restore --filter "FullyQualifiedName~HyperVPreviewIndependentSupervisorTests|FullyQualifiedName~HyperVPreviewSocketTransportTests"` — **35 réussis, 0 échec, 0 ignoré**.
- `dotnet test .\AIPlatform.Tests\AIPlatform.Tests.csproj --no-restore` — **434 réussis, 0 échec, 9 ignorés**. Les tests ignorés sont des scénarios Docker/intégration ou manuels; ils n'ont pas été activés.
- `git diff --check` — aucune erreur d'espacement. L'avertissement de normalisation CRLF concernait le script appliance préexistant, non modifié par M.

Les nouveaux tests démontrent notamment :

- le refus de protocoles incompatibles, de rôles absents et de baux incohérents;
- la réservation concurrente de Control et Frames avec renumérotation après
  `OutOfSequence`;
- le refus d'un rôle déjà réservé et d'un `ResourceId` falsifié;
- le maintien des réservations lors de la récupération d'une session orpheline
  et après redémarrage simulé du coordinateur avec le journal;
- le refus d'une réservation après expiration;
- la consommation durable avant une connexion qui échoue dans le test du
  connecteur.

Le named pipe utilisé par les tests est un IPC local de test sous
`CurrentUserOnly`; toutes les opérations de ressource utilisent un adaptateur
Hyper-V factice. Ces résultats ne prouvent ni l'identité du binaire Studio,
ni la sûreté d'un canal HvSocket natif, ni le nettoyage d'une VM réelle.

## Garanties démontrées et limites

**Démontré dans ce lot :**

- Le superviseur est la seule autorité qui peut accorder une réservation;
  le client n'accède pas au journal exclusif.
- Le rôle accepté est borné à l'énumération existante et le bail transmis doit
  être égal au bail complet conservé dans le journal.
- Une session non `Running` ou expirée ne peut pas obtenir un nouveau canal.
- Les rôles acceptés sont append-only et survivent à la récupération d'une
  session orpheline et à la relecture du journal; un échec ultérieur de
  transport ne les libère pas.
- Le connecteur ne tente pas le socket avant l'autorisation durable.
- Les tests existants de fermeture Preview restent verts; aucun runtime n'a été
  ajouté à la DI de production.

**Limites non levées :**

- `PipeOptions.CurrentUserOnly` identifie l'utilisateur Windows, pas le
  processus Studio. Un autre processus sous le même compte peut tenter des
  commandes IPC ou provoquer un refus de service.
- Le client et le serveur maintiennent des DTOs de fil distincts; la version 2
  et les tests d'interopérabilité détectent les divergences, mais leur
  déploiement doit rester synchronisé. Les clients v1 sont refusés et doivent
  être mis à jour.
- Le journal avec checksum SHA-256 n'est pas protégé contre une modification
  malveillante par un acteur local ayant les mêmes droits. Les ACL et la
  protection de l'identité du superviseur restent à qualifier.
- Aucune API native HvSocket/AF_VSOCK, inscription de service, création de VM,
  opération Hyper-V ou connexion entre hôte et invité n'a été appelée.
- Une réponse IPC perdue après l'append peut laisser un canal consommé sans
  socket établi; le comportement est volontairement fail-closed et nécessite
  une nouvelle session plutôt qu'une libération automatique du rôle.
- Les mesures Windows communiquées par l'utilisateur n'ont pas été
  revérifiées dans ce lot; aucune vérification système n'était nécessaire à
  l'implémentation hors VM.

## Étapes recommandées avant un premier essai Hyper-V

1. Partager les contrats IPC via une frontière d'assembly stable ou générée,
   puis qualifier l'identité/ACL du superviseur et les ACL du journal au-delà
   de `CurrentUserOnly`.
2. Qualifier les deux transports natifs sur hôte et invité, avec leurs
   permissions, déconnexions, délais, endpoint exact et politiques de refus;
   ne pas assimiler HvSocket local à une authentification.
3. Terminer l'appliance, ses artefacts finaux, signatures, empreintes et
   provenance; ils ne sont pas fournis par M.
4. Ajouter et faire examiner l'adaptateur réel de ressources, sa réconciliation,
   ses confirmations de nettoyage et son comportement quand l'annulation est
   ignorée.
5. Revalider ressources et permissions dans la session d'exécution cible puis
   obtenir une autorisation distincte, explicite et limitée avant tout premier
   démarrage. Cette livraison n'est pas cette autorisation.
6. Garder HTTP 503, `IsolationVerified == false` et les runtimes hors DI de
   production jusqu'aux essais réels contrôlés et à leur revue indépendante.

**Le MVP 6.5-M est un prototype d'autorisation IPC durable testé hors VM; il ne
certifie pas l'isolation et ne déclare pas AIPlatform Studio prêt pour Hyper-V.**
