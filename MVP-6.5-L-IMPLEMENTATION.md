# MVP 6.5-L — Transports HvSocket

## Résumé

Les adaptateurs de connexion Windows AF_HYPERV et d'écoute invitée Linux AF_VSOCK sont préparés sans activation. Le transport de flux commun respecte `IHyperVPreviewTransport`, et les entrées/sorties de socket sont injectables pour les tests. Les connexions natives ne sont ouvertes que si les adaptateurs explicites sont appelés; ils ne sont pas enregistrés en DI et aucun code de l'application ne les appelle.

Aucune VM, inscription de service HvSocket, opération Hyper-V, connexion socket réelle, modification système, Docker ou Ollama n'a été exécuté.

## Fichiers créés

- [`AIPlatform.Core/Studio/HyperVPreviewSocketContracts.cs`](./AIPlatform.Core/Studio/HyperVPreviewSocketContracts.cs) — allowlist, formats d'adresses natifs, registre de session, abstraction de socket, transport de flux et délais.
- [`AIPlatform.Infrastructure/Studio/WindowsHvSocketPreviewConnector.cs`](./AIPlatform.Infrastructure/Studio/WindowsHvSocketPreviewConnector.cs) — connexion hôte AF_HYPERV, validation de bail et de l'endpoint distant.
- [`AIPlatform.PreviewGuestAgent/LinuxVsockPreviewListener.cs`](./AIPlatform.PreviewGuestAgent/LinuxVsockPreviewListener.cs) — écoute AF_VSOCK mono-connexion et validation du CID pair.
- [`AIPlatform.Tests/HyperVPreviewSocketTransportTests.cs`](./AIPlatform.Tests/HyperVPreviewSocketTransportTests.cs) — tests avec connecteur, écouteur et sockets factices.
- [`MVP-6.5-L-IMPLEMENTATION.md`](./MVP-6.5-L-IMPLEMENTATION.md) — ce rapport.

Aucun fichier canonique Verified v9, `PreviewPlan`, `.gitignore`, projet d'API ou configuration DI de production n'a été modifié.

## Architecture

### Mapping des services autorisés

Le mapping du plan F est le seul accepté :

| Canal | ServiceId Windows | Port AF_VSOCK invité |
|---|---|---:|
| Control | `0000c391-facb-11e6-bd58-64006a7986d3` | 50065 |
| Frames | `0000c392-facb-11e6-bd58-64006a7986d3` | 50066 |

Les valeurs sont issues du plan F et du template Linux documenté par Microsoft. Aucun identifiant arbitraire n'est accepté par les fabriques de endpoints. Les UUID du protocole restent RFC 4122 big-endian; les champs GUID des adresses Windows et les entiers de `sockaddr_vm` utilisent leur représentation native système.

### Hôte Windows

- `WindowsHvSocketPreviewConnector` reçoit un `HyperVPreviewVmLease`, un rôle et un délai maximal de cinq secondes.
- L'adaptateur exige un `IHyperVPreviewManagedLeaseAuthorizer` et refuse un bail non autorisé. Il réserve le couple session/canal avant la tentative; une tentative échouée ne libère pas la réservation.
- `HyperVPreviewWindowsSocketEndPoint` encode `SOCKADDR_HV` (36 octets) avec un VMID concret, sans wildcard, et le ServiceId correspondant au rôle.
- `WindowsHvSocketNativeConnector` utilise `AF_HYPERV` (34), `SOCK_STREAM` et `HV_PROTOCOL_RAW` (1). Il vérifie ensuite le VMID, le ServiceId et le rôle de l'endpoint distant avant de retourner le flux.
- La connexion n'inscrit pas le service dans `GuestCommunicationServices`. Sans inscription autorisée par une procédure distincte, l'appel réel est censé échouer.

### Invité Linux

- `LinuxVsockPreviewListener` ouvre uniquement le port associé au rôle demandé, accepte un seul pair puis ferme l'écouteur.
- `LinuxVsockNativeListenerFactory` utilise AF_VSOCK (40), `SOCK_STREAM`, protocole 0 et lie `sockaddr_vm` au CID wildcard de l'invité, sur le port fixe du canal.
- Le pair accepté doit présenter le CID hôte 2. Le message-channel existant conserve le rôle et le SessionId attendus et rejette une trame d'une autre session.
- L'agent invité existant ne lance pas cette écoute automatiquement. Aucun port TCP/UDP ni exécution de commande n'est introduit.

### Frontières et limites de sécurité

Le connecteur Windows est lié à un bail fourni par un authorizer, mais aucune implémentation de cet authorizer n'est branchée à un runtime ou à la DI de production. Le registre de sessions est en mémoire et ne remplace pas l'identifiant durable et non réutilisable géré par le superviseur du MVP I. Ces deux éléments doivent être reliés à l'autorité effective des baux avant tout essai réel.

Le CID 2, le VMID, le ServiceId et les challenges contrôlent le routage et la cohérence du protocole; ils ne prouvent pas l'identité d'un processus hôte, l'intégrité de l'agent, ni l'isolation de l'invité. HvSocket/AF_VSOCK n'est pas considéré comme sûr par sa seule localité. La politique de refus des autres endpoints VMBus/HvSocket, les services d'intégration et l'accès réseau/fichiers restent à démontrer séparément.

## Tests et validation

Commandes exécutées sans restauration ni téléchargement :

- `dotnet test AIPlatform.Tests\AIPlatform.Tests.csproj --no-restore --filter "FullyQualifiedName~HyperVPreviewSocketTransportTests"` : **12 réussis, 0 échec**.
- `dotnet test AIPlatform.Tests\AIPlatform.Tests.csproj --no-restore` : **429 réussis, 0 échec, 9 ignorés**. Les tests ignorés sont des scénarios Docker/intégration et n'ont pas été lancés.
- `git diff --check` : aucun problème d'espacement signalé; Git a affiché un avertissement de normalisation CRLF sur `tools/HyperVApplianceBuild/build-offline.sh`, fichier préexistant non modifié pour L.

Les doubles vérifient les lectures/écritures partielles, l'arrêt pendant lecture, les trames malformées, l'annulation, les timeouts, la validation d'identité des endpoints, la réservation d'une session, le refus d'un CID inattendu, l'acceptation unique et la fermeture explicite. Aucun test n'ouvre un socket AF_HYPERV/AF_VSOCK réel. Les tests valident la sérialisation des structures et les politiques, pas leur interopérabilité avec un noyau ou Hyper-V.

La Preview reste fermée : `PreviewUnavailableException` continue d'être exposée en HTTP 503 et `IsolationVerified` reste `false`. `PreviewPlan` schema 1 n'a pas été changé; les adaptateurs ne sont pas intégrés à la DI de production.

## Prérequis avant un premier test réel

1. Une implémentation de l'authorizer doit comparer le bail complet avec l'enregistrement actif du superviseur, et le registre de réutilisation doit survivre au redémarrage par l'autorité durable du MVP I.
2. Construire, verrouiller et vérifier l'image Linux et l'agent; confirmer les options noyau `CONFIG_VSOCKET` et `CONFIG_HYPERV_VSOCKETS` sur l'image exacte.
3. Vérifier la compatibilité des structures et des endpoints par un essai d'interopérabilité autorisé, avec les deux canaux indépendants et les délais d'arrêt.
4. Obtenir une autorisation séparée avant l'inscription temporaire des deux services Windows exacts. Vérifier les collisions sans modifier ni réutiliser de services existants; le code L ne réalise aucune inscription.
5. Revalider ressources, privilèges et plan d'essai dans le contexte réel. Aucun test réel ne doit être lancé avant cette autorisation.
6. Vérifier la liste de refus des autres canaux VMBus/HvSocket et les accès réseau/fichiers depuis l'invité; l'identité du pair et un handshake ne certifient pas l'isolation.
7. Conserver HTTP 503 et `IsolationVerified == false` jusqu'à des essais contrôlés réels et une revue indépendante; les tests simulés ne constituent pas une preuve d'isolation.

## Références techniques

- [Architecture MVP 6.5-E](./docs/preview-virtualisee-mvp65e.md)
- [Plan MVP 6.5-F](./docs/preview-hyperv-mvp65f.md)
- [Préparation MVP 6.5-G](./docs/preview-hyperv-mvp65g.md)
- [Livraison MVP 6.5-K](./MVP-6.5-K-IMPLEMENTATION.md)
- [Microsoft Learn — Make your own integration services](https://learn.microsoft.com/en-us/windows-server/virtualization/hyper-v/make-integration-service)
