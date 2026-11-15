# MVP 6.5-K — Communication hôte–invité

## Résumé

Le contrat de communication hôte–invité et un agent invité .NET 10 minimal sont implémentés pour les essais hors VM. Le transport disponible est uniquement une paire de flux mémoire bornés, utilisée par les tests et `--self-test`. Aucun transport HvSocket réel, aucune VM et aucune opération Hyper-V n'ont été ajoutés ou exécutés.

## Architecture et frontières de confiance

- Le contrat partagé définit des trames versionnées avec un en-tête fixe de 48 octets : magic `AP65`, version, type, SessionId UUID RFC 4122, séquence, longueur du payload, flags réservés et corrélation. Les champs numériques sont encodés en big-endian.
- Le protocole comprend `Hello`, `HelloAck`, `Ready`, `Status`, `Error`, `Stop` et `CleanupConfirmed`. Les codecs imposent les types, tailles et rôles de canal permis.
- Le handshake simulé commence par `Hello` de l'hôte. Il lie le challenge Control, les nonces, la durée restante, le rôle et les identifiants de session/VM dans une preuve HMAC. L'agent répond par `HelloAck`, puis `Ready` et son état. Les challenges Control et Frames sont distincts.
- L'agent n'accepte ensuite que `Stop` ou `Error`, avec une séquence exacte. Il n'interprète ni commande système ni message `Input`.
- Le lecteur de messages reconstitue les lectures partielles, borne les payloads et applique des délais aux lectures/écritures. L'agent impose une échéance locale non renouvelable, limitée par sa configuration et la durée annoncée dans le premier `Hello`.
- Le transport mémoire simule les coupures, les lectures partielles et les écritures bornées; il ne représente ni l'authentification de l'IPC réel, ni les permissions VMBus/HvSocket, ni l'isolation d'un invité.

L'empreinte `AgentDigest` est transportée et vérifiée par le pair de test, mais l'agent ne mesure pas lui-même son binaire et cette valeur ne constitue pas une attestation. Le HMAC avec des challenges partagés n'établit pas à lui seul l'identité de l'image Linux ni son isolation.

## Fichiers créés ou modifiés

- [`AIPlatform.Core/Studio/HyperVPreviewContracts.cs`](./AIPlatform.Core/Studio/HyperVPreviewContracts.cs) — limites et délais du transport de contrôle.
- [`AIPlatform.Core/Studio/HyperVPreviewProtocol.cs`](./AIPlatform.Core/Studio/HyperVPreviewProtocol.cs) — trames, codecs, handshake et preuve HMAC.
- [`AIPlatform.Core/Studio/HyperVPreviewTransport.cs`](./AIPlatform.Core/Studio/HyperVPreviewTransport.cs) — transport abstrait et lecteur de trames partielles.
- [`AIPlatform.PreviewGuestAgent/AIPlatform.PreviewGuestAgent.csproj`](./AIPlatform.PreviewGuestAgent/AIPlatform.PreviewGuestAgent.csproj) — exécutable autonome ciblant .NET 10.
- [`AIPlatform.PreviewGuestAgent/HyperVPreviewGuestAgent.cs`](./AIPlatform.PreviewGuestAgent/HyperVPreviewGuestAgent.cs) — cycle de vie mono-session de l'agent invité.
- [`AIPlatform.PreviewGuestAgent/InMemoryHyperVPreviewTransport.cs`](./AIPlatform.PreviewGuestAgent/InMemoryHyperVPreviewTransport.cs) — transport de test borné.
- [`AIPlatform.PreviewGuestAgent/Program.cs`](./AIPlatform.PreviewGuestAgent/Program.cs) — mode `--self-test`; tout lancement sans ce mode échoue fermé, faute de transport Linux.
- [`AIPlatform.Tests/HyperVPreviewGuestAgentTests.cs`](./AIPlatform.Tests/HyperVPreviewGuestAgentTests.cs) — tests de handshake, séquences, durée, déconnexion, erreurs et refus d'Input.
- [`AIPlatform.Tests/HyperVPreviewProtocolTests.cs`](./AIPlatform.Tests/HyperVPreviewProtocolTests.cs) — tests du format filaire, des validations et d'un fuzzing déterministe.
- [`AIPlatform.Tests/AIPlatform.Tests.csproj`](./AIPlatform.Tests/AIPlatform.Tests.csproj) — référence au projet d'agent.
- [`AIPlatform.slnx`](./AIPlatform.slnx) — ajout du projet d'agent à la solution.
- `MVP-6.5-K-IMPLEMENTATION.md` — ce rapport.

## Validation exécutée

Toutes les commandes ont utilisé les dépendances déjà restaurées (`--no-restore`); aucun téléchargement n'a été demandé.

- Tests Hyper-V Preview ciblés : **54 réussis, 0 échec**.
- Suite complète `AIPlatform.Tests` : **417 réussis, 0 échec, 9 ignorés**. Les tests ignorés incluent des scénarios Docker/intégration et n'ont pas été lancés.
- `dotnet run --project AIPlatform.PreviewGuestAgent/AIPlatform.PreviewGuestAgent.csproj --no-build -- --self-test` : **`SELF_TEST_OK`**.
- Les tests et le self-test utilisent uniquement le transport mémoire. Aucun Docker, Ollama, socket ou VM n'a été démarré.

## Garanties démontrées et limites restantes

**Démontré hors VM :**

- Le codec accepte le format versionné attendu et rejette versions/types inconnus, flags réservés, mauvaises tailles, sessions non correspondantes et payloads invalides.
- Le transport de test gère les lectures fragmentées; les tests couvrent les trames tronquées et trop grandes.
- L'agent refuse un challenge incorrect, une preuve invalide, une séquence rejouée, un type de message inattendu et l'Input; l'échéance expire même si aucun `Hello` n'arrive.
- Le handshake et l'arrêt simulés aboutissent aux états et messages attendus.
- Le nouveau projet n'ouvre pas de TCP/UDP et n'exécute pas de commandes. Il n'est pas référencé par la DI de production.
- La Preview existante reste fermée en l'absence de runtime sécurisé : les erreurs `PreviewUnavailableException` sont mappées en HTTP 503 et le navigateur contrôlé conserve `IsolationVerified == false`. `PreviewPlan` schema 1 n'a pas été modifié.

**Non démontré / non implémenté :**

- Aucun transport HvSocket Windows/Linux, contrôle des identités et permissions IPC réel, handshake réel ni canal Frames.
- Aucune construction de l'image invitée, vérification de signature, attestation du binaire ou mesure de digest.
- Aucune preuve d'isolation, de blocage réseau/fichiers, de nettoyage d'une VM réelle ou de résistance à un crash système.
- Le lancement normal est volontairement indisponible; le self-test est une simulation uniquement.

## Étapes nécessaires avant un essai VM

1. Verrouiller et construire reproductiblement l'appliance, puis vérifier provenance, signatures disponibles et empreintes des composants réels.
2. Implémenter et revoir séparément les transports HvSocket des deux côtés et le canal Frames, avec ACL/identités explicites et gestion des déconnexions.
3. Implémenter le vérificateur hôte qui valide digest, défis, séquences, durée absolue et réponses avant toute transition d'état; ne pas assimiler un handshake à une isolation vérifiée.
4. Ajouter des tests de protocole interopérables et de défaillance avant d'envisager une première autorisation VM.
5. Revalider ressources, droits et plan d'exécution dans la session réelle; toute création/démarrage nécessite une autorisation distincte. Garder HTTP 503 et `IsolationVerified == false` jusqu'à validation contrôlée de l'isolation réelle.
