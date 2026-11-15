# AIPlatform Studio — Linux appliance readiness

## Décision

**L’appliance n’est pas prête pour un premier essai Hyper-V.** Le travail s’arrête ici : il manque des composants de publication .NET Linux, un lock de build authentique et le bundle offline vérifié. Aucun téléchargement, build Linux, service systemd réel ou essai VM n’a été effectué.

## Implémentation réelle

- Le mode `--serve` existant reste distinct du self-test et refuse de s’exécuter hors Linux. Il charge exclusivement `agent.json` depuis `CREDENTIALS_DIRECTORY`, vérifie les identifiants/challenges et une expiration absolue plafonnée à 170 secondes.
- Le service invité utilise le listener AF_VSOCK Linux existant sur le canal Control (port 50065), accepte un seul pair et exige le CID hôte 2. Les données reçues ne peuvent pas lancer de commandes.
- La session expire aussi pendant l’attente de connexion; aucune activité ne renouvelle l’échéance. Cette vérification a corrigé une classification de timeout à la frontière de l’échéance.
- L’unité systemd existante charge le credential avec `LoadCredential`, applique un utilisateur dédié et une politique sans redémarrage automatique. Le path unit attend `/run/aiplatform-preview/session.json`. Les fichiers d’activation systemd sont intégrés au chemin de packaging offline.
- La vérification a confirmé qu’aucun composant du superviseur ne provisionne encore ce fichier éphémère. Le transport est câblé au listener natif dans le programme, mais il n’a pas été exécuté sur Linux.
- HTTP 503 est conservé; `ControlledPreviewBrowser.IsolationVerified` reste `false`. Aucun runtime Hyper-V n’a été ajouté à la DI de production; `PreviewPlan` schema 1 et l’archive Verified v9 restent inchangés.

## Artefacts et empreintes

- **Aucun artefact Linux n’a été produit.** La tentative de publication `linux-x64` avec `--no-restore` a échoué avec `NETSDK1047` : `project.assets.json` ne contient pas la cible `net10.0/linux-x64`.
- Le runtime pack `Microsoft.NETCore.App.Runtime.linux-x64` n’est pas dans le cache NuGet local. Aucun restore n’a été tenté, afin de ne pas télécharger de dépendance sans autorisation. Aucun binaire Linux, VHDX, manifeste de build ou SHA-256 correspondant n’est donc disponible.
- `tools/HyperVApplianceBuild` contient les scripts et templates, mais pas de bundle de build ni de lock complet. `inputs.example.json` reste volontairement invalide : empreintes et versions `null`, empreintes de signataires et listes de paquets/outils vides; les composants agent, navigateur et certificats réels ne sont pas fournis.
- Les fichiers `preview-agent.service`, `preview-agent.path`, `preview-agent.sysusers` et `preview-session.target` sont des templates, pas des unités installées ou validées par systemd.

## Validations effectuées

**Windows**

- Le projet invité et ses dépendances compilent dans le cadre des tests .NET ciblés.
- Les tests ciblés invité/protocole/transport : **35 réussis, 0 échec** après correction du timeout à l’échéance.
- Les tests Node du builder offline : **20 réussis, 0 échec**.
- `git diff --check` ne rapporte pas d’erreur d’espacement; il signale seulement une normalisation CRLF/LF sur `build-offline.sh`.

**Simulation seulement**

- Handshake, authentification, arrêt, confirmation et expiration sont testés avec un transport mémoire/factice.
- Le packaging est validé par fixtures synthétiques; ces tests ne vérifient ni les signatures réelles ni les propriétés d’une image construite.

**Linux réel / non testé**

- Aucun environnement Linux, systemd ou AF_VSOCK natif n’a été utilisé; `systemd-analyze` n’est pas disponible.
- Le binaire Linux, ses dépendances natives/permissions, les unités chargées par systemd, le démarrage de l’appliance, Secure Boot, le build offline, le VHDX et le nettoyage d’une VM ne sont pas testés.
- `--self-test` valide uniquement le transport mémoire et ne constitue pas un test AF_VSOCK.

## Blocages indispensables

1. **Publication .NET Linux sans téléchargement :** fournir au SDK .NET 10 les packs Runtime et AppHost `linux-x64` nécessaires dans un cache/source local approuvé, et autoriser une restauration strictement locale. Puis republier en Release, vérifier l’ELF x86_64, ses dépendances et permissions, et calculer son SHA-256.
2. **Inputs offline authentiques :** fournir le bundle complet et son lock vérifié, avec versions, empreintes, signatures/fingerprints réels, index et archives Debian, fermeture des dépendances, certificats Secure Boot, kernel, GRUB/shim, navigateur et outils du builder. Ne pas remplir les champs depuis l’exemple avec des valeurs inventées.
3. **Provisionnement d’une session :** intégrer au superviseur hôte une création atomique et protégée du credential éphémère dans `/run/aiplatform-preview/session.json`, son association vérifiée à la session/VM, et son retrait/récupération après arrêt ou panne. Ce fichier ne doit pas être incorporé dans l’image.
4. **Validation Linux :** disposer d’un environnement Linux x86_64 approuvé pour valider systemd et AF_VSOCK; aucun de ces essais ne peut être simulé comme preuve réelle.

## Actions exactes avant un premier essai Hyper-V

1. Satisfaire les trois prérequis ci-dessus sans réseau non autorisé, puis produire le binaire et le lock avec empreintes authentiques.
2. Dans un builder Linux offline dédié et autorisé, construire l’image, examiner le manifeste, la chaîne Secure Boot et le VHDX; comparer deux builds indépendants avant de qualifier la reproductibilité.
3. Sur Linux natif, valider les unités et permissions systemd, le credential éphémère, le port/CID AF_VSOCK, les erreurs, l’arrêt et l’expiration.
4. Compléter une revue de sécurité du superviseur/provisionneur et de la récupération après crash. Le handshake et HvSocket/VSOCK ne démontrent pas à eux seuls l’isolation.
5. Obtenir une autorisation distincte pour créer et démarrer une VM jetable, avec observation indépendante et procédure vérifiable de destruction. **Aucune VM n’est autorisée ni démarrée dans cette session.**

