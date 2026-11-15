# AIPlatform Studio — MVP 6.5-J

## Résumé

Le lot ajoute une inspection Hyper-V Windows en lecture seule et une couche de contrôle préparatoire qui refuse les mutations sans autorisation explicite. Aucun backend Windows de création, démarrage, arrêt ou destruction n'est fourni. Le runtime n'est pas enregistré dans la DI de production.

**Le premier essai VM reste bloqué** : l'inspection locale a échoué avec un refus d'accès Hyper-V et la mémoire libre mesurée était de 2,56 Gio, sous le seuil de 4,5 Gio établi par le plan F. Aucune VM ni opération mutante n'a été exécutée.

## 1. Architecture et frontières de confiance

### Inspection Windows en lecture seule

`WindowsHyperVHostInspector` lance un script PowerShell constant depuis le chemin système, sans incorporer de données externes dans les commandes. Le script ne fait que :

- vérifier/importer le module Hyper-V ;
- appeler `Get-VMHost` et `Get-VM` pour lire l'hôte et l'inventaire ;
- lire la mémoire via CIM et l'espace libre du lecteur système ;
- retourner un résultat JSON versionné, limité en taille et validé strictement.

Les limites comprennent 5 secondes pour la requête, 1 Mio de sortie et 4 096 VM dans l'inventaire. Les doublons JSON, les champs inconnus, les identifiants invalides et les réponses incohérentes sont rejetés. Les erreurs sont distinguées (`AccessDenied`, `HyperVUnavailable`, `TimedOut`, `QueryFailed`, etc.). Le refus d'accès CIM (`CimException.StatusCode == 2`) est reconnu explicitement.

L'identification d'une ressource repose sur le VMID GUID et la concordance avec le journal durable de session. Le marqueur d'appartenance est lu dans `Notes` et doit correspondre au format exact `AIPlatformPreview/65J/{session}/{vm}`. Ce marqueur en texte clair n'est **pas** une preuve cryptographique et ne suffit jamais seul à autoriser une opération.

### Contrôle préparatoire des mutations

`GuardedHyperVVmControl` sépare l'inspection, l'autorisation, le backend de mutation et le vérificateur de nettoyage :

- chaque opération exige une session et un bail identiques à ceux persistés dans `DurableSupervisorJournal` ;
- les mutations exigent une autorisation à usage unique, liée à l'opération, à la session et au VMID ;
- les valeurs par défaut refusent toute autorisation, désactivent le backend et ne confirment jamais le nettoyage ;
- l'identité d'une VM utilisateur, un bail falsifié, une intention d'allocation ambiguë ou un orphelin entraînent un refus et une réconciliation explicite ;
- une annulation ou un délai dépassé produit un résultat **inconnu**, et non une affirmation que l'opération a été annulée ;
- une destruction ne peut être confirmée que si l'absence du VM et le nettoyage des artefacts sont tous deux vérifiés.

Les garde-fous de ressources reprennent au minimum 4,5 Gio de mémoire libre et 15 Gio sur le lecteur système. Le lanceur borne chaque opération mutante à 10 secondes par défaut, au maximum 30 secondes ; il ne prétend pas pouvoir interrompre de force un appel Hyper-V bloqué.

## 2. Fichiers créés pour le lot

- `AIPlatform.PreviewSupervisor/WindowsHyperVHostInspector.cs` — inspection Windows en lecture seule, exécution bornée et décodage strict du résultat.
- `AIPlatform.PreviewSupervisor/GuardedHyperVVmControl.cs` — contrats de mutation, refus par défaut, autorisation, contrôle d'identité et réconciliation prudente.
- `AIPlatform.Tests/GuardedHyperVVmControlTests.cs` — tests avec inspecteur, journal, autorisation et backend fictifs.
- `MVP-6.5-J-IMPLEMENTATION.md` — présent rapport.

Aucun fichier canonique Verified v9, aucun contrat `PreviewPlan` schema 1, aucun endpoint Preview et aucune configuration de production n'ont été modifiés dans ce lot.

## 3. Fonctionnalités et tests

Comportements couverts par des tests hors VM :

- décodage d'un refus d'accès structuré, rejet des propriétés JSON dupliquées et validation des identifiants VM/session ;
- refus des sessions inconnues, VM utilisateur non gérée et baux falsifiés ;
- refus sans autorisation à usage unique et lorsque le seuil RAM échoue ;
- détection d'une création partielle comme orphelin non journalisé, sans destruction automatique ;
- traitement d'une création qui ignore l'annulation : résultat inconnu, puis réconciliation de l'orphelin lorsqu'il devient visible ;
- création/démarrage nominal avec des adaptateurs entièrement simulés ;
- tests existants du runtime et du superviseur, y compris les scénarios de redémarrage/crash simulés.

Validation exécutée sans restauration de dépendances :

- Build : `dotnet build .\AIPlatform.PreviewSupervisor\AIPlatform.PreviewSupervisor.csproj --no-restore` — réussi, 0 avertissement, 0 erreur.
- Tests Hyper-V : `dotnet test .\AIPlatform.Tests\AIPlatform.Tests.csproj --no-restore --filter "FullyQualifiedName~HyperVPreview|FullyQualifiedName~GuardedHyperVVmControl"` — 54 réussis.
- Garde-fou Preview : `dotnet test .\AIPlatform.Tests\AIPlatform.Tests.csproj --no-restore --filter "FullyQualifiedName~RealConfigurationFailsClosedWithoutSecureRuntime|FullyQualifiedName~InvalidSessionsCannotLaunchProcess"` — 5 réussis.

Les tests Hyper-V utilisent uniquement des doubles en mémoire ; les tests de sécurité Preview vérifient l'échec fermé sans lancer Docker. La suite générale n'a pas été lancée.

## 4. Prérequis Hyper-V observés

Mesure en lecture seule lors de ce passage :

- mémoire physique libre : **2,56 Gio**, inférieure au minimum de **4,5 Gio** ;
- espace libre du lecteur système : **96,52 Gio**, supérieur au garde-fou logiciel de 15 Gio ;
- module PowerShell Hyper-V : présent ;
- `Get-VMHost` : refus d'accès. L'exception locale était `Microsoft.HyperV.PowerShell.VirtualizationException`, avec une exception CIM interne de statut 2 (`AccessDenied`). Le script de l'inspecteur traduit ce statut en résultat explicite `AccessDenied`.

La version exacte de Windows, l'appartenance aux groupes Hyper-V, l'état TPM/Secure Boot, l'image appliance construite et ses empreintes n'ont pas été revérifiés dans ce passage. Les informations d'une autre session ne sont pas traitées comme une preuve de l'état courant.

## 5. Garanties démontrées et limites

**Démontré par inspection/tests :**

- l'adaptateur hôte ne contient que des appels de découverte en lecture seule ;
- une indisponibilité ou un refus d'accès empêche les mutations ;
- les identités sont comparées au journal durable et les cas ambigus ne sont pas nettoyés automatiquement ;
- les résultats simulés confirment les refus, limites et erreurs décrits ci-dessus ;
- la Preview reste en échec fermé : `ControlledPreviewBrowser.IsolationVerified` reste `false`, et le test `RealConfigurationFailsClosedWithoutSecureRuntime` confirme HTTP 503 ;
- aucun adaptateur Hyper-V n'a été ajouté à la DI de production.

**Non démontré / non implémenté :**

- aucune mutation Hyper-V réelle, aucun démarrage VM, aucun canal HvSocket et aucun nettoyage réel n'ont été testés ;
- le backend Windows de mutation, les ACL/identités d'autorisation administratives et le vérificateur réel de suppression des artefacts restent à implémenter ;
- les tests simulés ne prouvent ni l'isolation d'une VM réelle ni la résistance aux défaillances de l'hyperviseur ;
- le marqueur `Notes` est falsifiable par un acteur local suffisamment privilégié ; la protection s'appuie aussi sur le VMID et le journal, mais n'est pas une attestation cryptographique ;
- l'annulation d'un appel Hyper-V ne garantit pas son arrêt ; après timeout, le résultat reste indéterminé jusqu'à réconciliation ;
- l'inventaire et l'état de l'hôte ne sont actuellement pas consultables avec les permissions du contexte observé.

La DI API existante continue d'utiliser son runtime Preview antérieur, dont le navigateur contrôlé ne vérifie pas l'isolation. Le nouvel adaptateur Hyper-V n'est pas branché à cette chaîne ; le statut 503 et `IsolationVerified == false` sont conservés.

## 6. Conditions avant un premier prototype

1. Obtenir les permissions Hyper-V nécessaires par une procédure administrateur explicitement autorisée ; aucune élévation ou modification de groupe n'a été demandée ici.
2. Libérer manuellement assez de mémoire et mesurer à nouveau **au moins 4,5 Gio** juste avant tout essai. La mesure actuelle interdit une création/démarrage.
3. Vérifier de nouveau la version Windows prise en charge, les prérequis matériels, Secure Boot et les autorisations effectives dans le contexte destiné à l'essai.
4. Construire l'appliance selon une procédure reproductible, vérifier provenance, signatures disponibles, manifeste et empreintes des composants ; aucun binaire ou modèle n'a été téléchargé ou construit ici.
5. Implémenter et faire auditer séparément le backend Windows et le nettoyage, avec une politique d'autorisation explicite, une identité de session durable et un protocole de récupération des opérations indéterminées.
6. Faire examiner le plan final et obtenir une autorisation explicite avant toute opération Hyper-V mutante. Le lot présent ne constitue pas cette autorisation.

**Décision pour ce lot :** l'adaptateur de découverte est préparé et les contrôles sont testés hors VM, mais le prototype Hyper-V n'est pas autorisé ni prêt à démarrer.
