# MVP 6.5-I — Superviseur indépendant

## Statut

Prototype exécutable et testé hors VM. Il ne contient aucun adaptateur Hyper-V réel et ne rend pas la Preview disponible. Le MVP 6.5-I n'est pas terminé.

## Architecture et frontières de confiance

- `AIPlatform.PreviewSupervisor` est un exécutable .NET 10 distinct. Sans `--simulate`, il refuse de démarrer (code de sortie 78). Les modes de simulation ne pilotent que des ressources factices situées dans le répertoire temporaire utilisateur.
- Le canal IPC est un named pipe local créé avec `PipeOptions.CurrentUserOnly`. Il authentifie l'identité Windows de l'utilisateur, pas l'identité du binaire Studio : un autre processus du même utilisateur peut se connecter. Ce niveau d'authentification est une limite connue, non une preuve d'identité Studio.
- Les messages sont versionnés, limités à 8 Kio et validés contre les champs du contrat. Les commandes autorisées sont création, état, heartbeat, arrêt et nouvelle tentative de nettoyage. L'identifiant de session est généré par le superviseur; aucun champ client ne permet de choisir ou de renouveler le TTL.
- Les commandes de session sont séquencées et protégées contre le rejeu. Les messages inconnus, malformés, dupliqués ou hors séquence sont refusés.
- Le journal est append-only, à enregistrements bornés et contrôlés par SHA-256, avec flush write-through et verrou exclusif du processus. Une fin de fichier incomplète peut être récupérée; une corruption détectée provoque un refus de démarrage. Le checksum ne protège pas contre une modification malveillante par le même utilisateur et aucun ACL personnalisé du journal n'est provisionné.
- Après redémarrage, toute session non terminale est récupérée par identifiant de session, puis arrêtée et détruite; elle n'est jamais reprise. Une session terminale n'est pas réactivable. Une absence de ressource ne peut être considérée comme confirmée que si l'adaptateur réel respecte le contrat autoritatif de `FindBySessionIdAsync`.

## Cycle de vie, expiration et nettoyage

- Le TTL maximal est de 170 secondes, défini à la création et non prolongeable. Le superviseur combine l'uptime monotone et l'époque UTC de démarrage pour repérer les changements de démarrage; les heartbeats n'affectent pas l'échéance.
- L'expiration est armée avant l'allocation. Les appels de création et de démarrage reçoivent un jeton d'annulation borné par l'échéance absolue.
- Le nettoyage confirme l'état, tente un arrêt gracieux, force l'arrêt si nécessaire, puis détruit la ressource. L'état terminal n'est journalisé qu'après confirmation de destruction ou confirmation autoritative qu'aucune ressource n'existe.
- Les appels de nettoyage reçoivent des jetons bornés; l'expiration réserve une marge cible de 10 secondes. Une erreur ou une confirmation manquante conserve `CleanupFailed` et permet une nouvelle tentative contrôlée, y compris après cette marge. Cela ne constitue pas une garantie de temps réel : un adaptateur qui ignore l'annulation, une panne de l'hôte ou un retard d'ordonnancement peut dépasser les délais.
- Une correction de l'horloge peut entraîner une expiration anticipée. Aucun test ne démontre l'arrêt d'une VM réelle dans les 180 secondes.

## Fonctionnalités démontrées avec simulateur

- Processus superviseur distinct qui reste actif après la terminaison forcée du client Studio simulé.
- Expiration après déconnexion du client, sans prolongation par heartbeat.
- Récupération au redémarrage du superviseur et destruction simulée d'une ressource orpheline.
- Récupération d'une allocation simulée créée après l'intention durable, mais avant la journalisation du bail.
- Annulation d'une création simulée qui respecte le jeton et nettoyage de l'allocation partielle.
- Rejeu, séquences invalides, sessions inconnues, TTL excessif, trames trop grandes, champs inconnus ou dupliqués, verrouillage concurrent du journal, échec de nettoyage et nouvelle tentative.

Ces tests démontrent le comportement du code avec adaptateurs factices et processus locaux de test uniquement. Ils ne démontrent ni les permissions effectives d'un canal IPC en déploiement, ni la récupération, l'arrêt ou le nettoyage d'une VM Hyper-V.

## Fichiers de cette livraison

Créés :

- `AIPlatform.PreviewSupervisor/AIPlatform.PreviewSupervisor.csproj`
- `AIPlatform.PreviewSupervisor/Program.cs`
- `AIPlatform.PreviewSupervisor/SupervisorContracts.cs`
- `AIPlatform.PreviewSupervisor/DurableSupervisorJournal.cs`
- `AIPlatform.PreviewSupervisor/SupervisedPreviewCoordinator.cs`
- `AIPlatform.PreviewSupervisor/SupervisorIpcCodec.cs`
- `AIPlatform.PreviewSupervisor/SupervisorNamedPipe.cs`
- `AIPlatform.PreviewSupervisor/SimulatedPersistentVmControl.cs`
- `AIPlatform.Tests/HyperVPreviewIndependentSupervisorTests.cs`
- `MVP-6.5-I-IMPLEMENTATION.md`

Modifiés :

- `AIPlatform.slnx` — ajout du projet superviseur.
- `AIPlatform.Tests/AIPlatform.Tests.csproj` — référence de projet pour les tests.

Les autres changements visibles dans l'arbre de travail relèvent des incréments précédents MVP 6.5-G/H et ne sont pas inclus dans cette liste.

## Validation

- Tests ciblés : `dotnet test .\AIPlatform.Tests\AIPlatform.Tests.csproj --no-restore --filter "FullyQualifiedName~HyperVPreviewIndependentSupervisor"` — **18 réussis, 0 échec**.
- Suite complète : `dotnet test .\AIPlatform.Tests\AIPlatform.Tests.csproj --no-restore` — **395 réussis, 0 échec, 9 ignorés**. Les tests ignorés incluent des scénarios nécessitant Docker/Ollama; aucun de ces services n'a été lancé.
- `git diff --check` ne signale pas d'erreur d'espacement. La vérification des processus n'a trouvé aucun processus superviseur de test résiduel.
- Le projet superviseur n'est pas enregistré dans l'injection de dépendances de production. Les endpoints Preview restent indisponibles (503) et `ControlledPreviewBrowser.IsolationVerified` reste `false`.
- Aucun appel Hyper-V, aucune création/démarrage de VM, aucun service HvSocket, aucune élévation, installation système, opération Docker ou Ollama n'a été effectué.

## Limites et étapes avant un essai Hyper-V

1. Concevoir et faire examiner l'identité/ACL du named pipe et les ACL du journal; `CurrentUserOnly` ne distingue pas Studio des autres processus du même utilisateur.
2. Implémenter un adaptateur Hyper-V réel avec identité de ressource stable, découverte non ambiguë, annulation et confirmations explicites pour chaque opération. Le soumettre à une revue de sécurité distincte.
3. Tester le superviseur et l'adaptateur sur l'hôte autorisé : crash aux frontières d'allocation, redémarrage, délais, arrêt forcé, états inconnus, panne Hyper-V et nettoyage après échec.
4. Définir le lancement et l'arrêt du superviseur par Studio, avec contrôle d'accès et récupération au démarrage; ne pas le connecter aux endpoints Preview avant validation.
5. Soumettre séparément le plan canonique et son empreinte pour autorisation avant toute opération Hyper-V réelle.

Les protections existantes — `PreviewPlan` schema 1, canonicals Verified v9, digest Docker, autorisations Preview et état `IsolationVerified` — ne sont pas modifiées par cette livraison.
