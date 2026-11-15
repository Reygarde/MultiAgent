# AIPlatform Studio — MVP 6.5-O : Finalisation de l'agent Linux

## Résumé

Le mode `--serve` et son orchestration de session sont implémentés. Une session complète a été exercée avec le protocole existant et un transport mémoire simulé; l'écouteur AF_VSOCK natif est câblé mais n'a pas été exécuté. Aucun build Linux, service systemd réel, socket natif ou VM n'a été lancé.

L'implémentation ne prouve donc ni le démarrage effectif de l'appliance, ni l'isolation d'une VM. HTTP 503 reste maintenu et `IsolationVerified` reste `false`.

## Architecture et fonctionnement

- `--self-test` reste un test local en mémoire. `--serve <chemin>` est un mode séparé, refusé hors Linux.
- Le mode service n'accepte qu'un credential systemd nommé `agent.json` dans `CREDENTIALS_DIRECTORY`. Le JSON versionné est limité à 4 Kio et exige des identifiants de session/VM, deux challenges de 32 octets distincts et une échéance UTC non expirée, distante d'au plus 170 secondes. Sous Linux, le fichier doit être lisible uniquement par son propriétaire.
- `HyperVPreviewGuestAgentService` ouvre uniquement le canal Control du catalogue existant, sur le port 50065. Le listener accepte une connexion, borne l'initialisation à cinq secondes et vérifie que le pair est le CID hôte 2. L'échéance est établie avant l'initialisation et son jeton d'expiration est partagé avec l'agent; aucune activité ou heartbeat ne la renouvelle.
- L'agent réutilise le protocole version 1 big-endian, le handshake et les preuves cryptographiques existantes. Après authentification, seules les commandes Stop et Error sont traitées. Input et les types inattendus sont rejetés; aucune commande ni code reçu n'est exécuté. Toute perte de transport arrête la session.
- Le digest SHA-256 est mesuré depuis le binaire en cours d'exécution et inclus dans le handshake. Les challenges et buffers de digest sont effacés à la fin de la session. Les journaux n'incluent ni challenge, ni credential.
- L'unité systemd utilise `preview-agent`, `LoadCredential`, `NoNewPrivileges`, un jeu de capacités vide, des limites de ressources et des familles d'adresses limitées à AF_UNIX/AF_VSOCK. `preview-agent.path` déclenche le service à l'arrivée de `/run/aiplatform-preview/session.json`; les redémarrages automatiques sont désactivés.
- Le helper offline prépare dans le futur rootfs les liens d'activation du target et du path unit, avec rollback des seuls liens et répertoires créés. Il n'installe ni n'active systemd sur l'hôte Windows.

## Fichiers créés ou modifiés

**Créés**

- `AIPlatform.PreviewGuestAgent/HyperVPreviewGuestAgentConfiguration.cs`
- `AIPlatform.PreviewGuestAgent/HyperVPreviewGuestAgentService.cs`
- `AIPlatform.Tests/HyperVPreviewGuestAgentServiceTests.cs`
- `tools/HyperVApplianceBuild/preview-agent.path`
- `tools/HyperVApplianceBuild/preview-agent.service`
- `tools/HyperVApplianceBuild/preview-session.target`

**Modifiés**

- `AIPlatform.PreviewGuestAgent/Program.cs`
- `tools/HyperVApplianceBuild/build-offline.sh`
- `tools/HyperVApplianceBuild/inputs.mjs`
- `tools/HyperVApplianceBuild/inputs.test.mjs`
- `tools/HyperVApplianceBuild/inputs.example.json`

`tools/HyperVApplianceBuild/preview-agent.sysusers` est réutilisé sans changement. Aucun fichier Verified v9, contrat `PreviewPlan` schema 1 ou branchement DI de production n'a été modifié.

## Tests et résultats

- Tests .NET ciblés invité/protocole/transport : **35 réussis, 0 échec**.
- Suite complète `AIPlatform.Tests` avec `--no-restore` : **445 réussis, 9 ignorés, 0 échec**. Les tests ignorés incluent des scénarios Docker et une préparation de correction manuelle; ils n'ont pas été activés.
- Tests offline du builder Node : **20 réussis, 0 échec**.
- `--self-test` : **SELF_TEST_OK**.
- `git diff --check` : aucune erreur d'espacement (Git signale seulement une normalisation CRLF/LF sur le script Bash).
- Aucun restore ou téléchargement de dépendances n'a été effectué.

Les tests automatisés couvrent notamment les credentials absents, incomplets, dupliqués, expirés ou trop longs; les permissions Linux; l'authentification réussie/échouée; la session complète en mémoire avec arrêt et confirmation; la commande Input; l'expiration sans heartbeat et pendant l'attente de connexion; l'annulation, la déconnexion et la libération du transport; ainsi que l'initialisation et le rollback du packaging simulé.

## Garanties démontrées et limites

**Démontré hors VM :** compilation .NET, handshake/authentification et cycle d'arrêt via transport mémoire, validation des paramètres et échéances, refus des entrées non autorisées, et cohérence structurelle des fichiers systemd/du packaging contrôlée par tests.

**Non démontré :**

- L'agent n'a pas été publié en binaire Linux x86_64 et les unités n'ont pas été validées ou démarrées par systemd. Le bundle d'exemple contient encore des versions, empreintes et dépendances nulles; il n'est pas constructible tel quel.
- Aucun appel à AF_VSOCK natif n'a été fait. La disponibilité réelle de cette famille de sockets, le sandboxing systemd, le handshake inter-OS, la chaîne Secure Boot et le démarrage de l'appliance restent à valider sur Linux.
- Aucun composant hôte/superviseur ne provisionne encore le credential éphémère `/run/aiplatform-preview/session.json`, ne garantit sa création atomique ni son retrait après arrêt. Sans cette intégration, le watcher n'a aucune session à lancer.
- Une échéance appliquée dans l'agent ne constitue pas une garantie contre un invité compromis, une horloge manipulée ou une terminaison forcée. SIGKILL peut interrompre le nettoyage logiciel; l'agent ne peut ni arrêter ni détruire la VM et `CleanupConfirmed` n'est pas une preuve de destruction de ressources Hyper-V.
- Les tests mémoire et les checks de packaging ne prouvent pas l'isolation d'une VM réelle.

## Prérequis avant un build Linux

1. Construire/publier le véritable binaire Linux x86_64 à l'emplacement attendu et figer son origine, sa version et son empreinte.
2. Compléter le lock avec les versions et empreintes réelles des paquets, index signés, certificats et outils; vérifier indépendamment les signataires et la fermeture des dépendances.
3. Faire approuver un bundle offline fermé, puis exécuter le builder uniquement dans l'environnement Linux amd64 jetable prévu et autorisé.
4. Examiner le manifeste et les rapports, comparer deux builds indépendants, puis vérifier séparément l'image et le démarrage Secure Boot avant toute autorisation d'usage.

## Prérequis distincts avant un premier essai Hyper-V

1. Terminer l'intégration du superviseur pour provisionner un credential par session, créer et protéger `/run/aiplatform-preview`, l'associer à une VM/session vérifiée et le supprimer/réconcilier même après panne.
2. Valider le binaire et les unités sur Linux natif, y compris systemd, les permissions, la politique d'arrêt et le transport AF_VSOCK sur le port/CID attendus.
3. Revoir les frontières de confiance, l'authentification des deux côtés et la récupération après crash; ne pas assimiler HvSocket/VSOCK à une preuve d'isolation.
4. Obtenir une autorisation explicite pour une VM jetable et isolée, un protocole d'observation indépendant et une procédure de destruction vérifiable. Aucun de ces essais n'est autorisé ou exécuté par ce lot.

