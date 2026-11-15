# MVP 6.5-N — Préparation de l’appliance Linux

## Résumé

Le lot renforce le vérificateur de lock et le builder offline, prépare le packaging de l’agent invité et ajoute des tests Node exécutés sans construire l’appliance. Aucun lock complet ni artefact Linux réel n’a été inventé : l’exemple reste volontairement invalide, avec ses empreintes, versions et identités de signature manquantes.

Le builder, une VM, Hyper-V, Docker, Ollama et un transport HvSocket/AF_VSOCK réel n’ont pas été lancés. Aucun changement n’a été fait aux contrats Verified v9, à `PreviewPlan` schema 1 ou au code de production/API.

## Audit ciblé et changements

### Dépendances et intégrité

- Le lock exige maintenant les index Debian `Packages.gz` pour `trixie` et `trixie-security`. Le helper les décompresse avec une limite de taille, valide les stanzas essentielles, puis vérifie que chaque paquet verrouillé correspond à une seule entrée par dépôt, nom, version, architecture, chemin d’archive et SHA-256.
- Les fichiers, index et archives doivent toujours correspondre à l’inventaire et au digest du lock fourni séparément. La fermeture installée est analysée ligne par ligne : champs supplémentaires, doublons, lignes mal formées et statuts autres que `installed` sont refusés.
- La vérification de signature GPG des métadonnées et la vérification des binaires Secure Boot restent dans la recette de construction. Leur chaîne réelle n’a pas été exécutée : elle dépendra d’un lock complet et d’un environnement Linux offline approuvé.

### Agent invité et démarrage

- L’overlay doit installer un ELF Linux 64 bits x86_64 à l’emplacement fixe `/usr/local/lib/preview/agent`, ainsi que les unités systemd et le fichier sysusers comparés aux templates du dépôt.
- Le service est conditionné par `/etc/preview/agent-runtime-approved`; le builder vérifie que ce marqueur est absent de l’image. Le service n’est ni activé ni exécuté par le builder.
- Les unités limitent les familles d’adresses à `AF_UNIX` et `AF_VSOCK`, désactivent les privilèges supplémentaires et utilisent un compte système non interactif. Elles ne configurent aucun port TCP/UDP.
- La recette vérifie exactement le `fstab`, la configuration GRUB et la géométrie GPT retournée par `sfdisk`. Après conversion, elle contrôle les métadonnées VHDX, les dimensions, l’absence de parent, la taille allouée et la couverture de la carte d’allocation.

### Packaging transactionnel et manifeste

- L’installation de l’overlay refuse les destinations préexistantes, écrit en mode exclusif et retire en cas d’erreur uniquement les fichiers et répertoires créés par cette opération. Un rollback incomplet est remonté comme erreur.
- Le manifeste inclut le digest du lock, les paramètres d’entrée et les empreintes des artefacts retenus, tout en gardant `executionEligible: false` et l’étape `built-not-boot-validated`.
- La commande `compare-manifests` compare deux manifestes ayant les mêmes entrées verrouillées et produit un rapport de différences. Elle ne prouve pas la reproductibilité tant que deux builds indépendants n’ont pas été réalisés et comparés.

## Fichiers créés ou modifiés

Créés :

- [MVP-6.5-N-IMPLEMENTATION.md](./MVP-6.5-N-IMPLEMENTATION.md)
- [preview-agent.service](./tools/HyperVApplianceBuild/preview-agent.service)
- [preview-agent.sysusers](./tools/HyperVApplianceBuild/preview-agent.sysusers)
- [preview-session.target](./tools/HyperVApplianceBuild/preview-session.target)

Modifiés :

- [inputs.mjs](./tools/HyperVApplianceBuild/inputs.mjs)
- [inputs.test.mjs](./tools/HyperVApplianceBuild/inputs.test.mjs)
- [build-offline.sh](./tools/HyperVApplianceBuild/build-offline.sh)
- [inputs.example.json](./tools/HyperVApplianceBuild/inputs.example.json)

Les autres changements déjà présents dans le workspace, notamment les lots précédents, ont été conservés.

## Tests et résultats

Commande exécutée :

```text
node --check tools\HyperVApplianceBuild\inputs.mjs
node --check tools\HyperVApplianceBuild\inputs.test.mjs
node --test tools\HyperVApplianceBuild\inputs.test.mjs
git --no-pager diff --check -- tools/HyperVApplianceBuild
```

Résultat : **19 tests réussis, 0 échec, 0 ignoré**. Les fixtures d’index, ELF, manifestes, partitions et images sont synthétiques et réservées aux tests. Les tests couvrent notamment les dépendances absentes, les archives/index incohérents, les configurations et chemins agent non autorisés, les entrées malformées, les échecs partiels de packaging et rollback, le marqueur d’autorisation, la géométrie GPT/VHDX et la comparaison de manifestes.

Le contrôle `git diff --check` n’a signalé aucune erreur de whitespace; Git a affiché un avertissement de conversion CRLF/LF déjà associé au script shell. Le contrôle syntaxique `bash -n` n’a pas pu être effectué : le seul `bash` détecté délègue à WSL, dont l’exécutable Linux n’est pas disponible dans cette session. La recette n’a pas été exécutée.

## Garanties démontrées et limites

Démontré par tests locaux :

- le lock incomplet ou incohérent est refusé;
- les entrées Debian verrouillées sont recoupées avec les index gzip fournis;
- le packaging agent doit correspondre aux templates approuvés et l’ELF déclaré est vérifié pour son format, sa classe et son architecture;
- les tests de l’overlay refusent l’écrasement et démontrent le rollback dans des répertoires temporaires;
- les vérificateurs rejettent des configurations de démarrage et géométries synthétiques non conformes;
- deux manifestes peuvent être comparés sans transformer le résultat en autorisation d’exécution.

Non démontré :

- signatures réelles GPG, Secure Boot, intégrité des index par APT, fermeture réelle `dpkg`, compilation/publish de l’agent ou génération d’un VHDX;
- boot Gen2, Secure Boot effectif, isolation, démarrage du service, protocole hôte–invité ou nettoyage d’une VM;
- reproductibilité de deux builds indépendants.

Le contrôle du VHDX vérifie la commande de conversion demandant `subformat=fixed`, la taille et la couverture d’allocation observée. Il ne constitue pas un démarrage ni une preuve indépendante de l’implémentation interne de QEMU. Une image générée devra encore être examinée avec les outils réels et validée par un démarrage contrôlé distinct.

Le projet [AIPlatform.PreviewGuestAgent](./AIPlatform.PreviewGuestAgent/Program.cs) ne prend actuellement en charge que `--self-test` en mémoire; il indique explicitement que le transport HvSocket Linux n’est pas implémenté. Le `--serve` référencé par l’unité systemd n’existe donc pas encore. Le marqueur absent empêche le service de démarrer dans l’image préparée; les templates ne rendent pas l’agent fonctionnel.

## Blocages et autorisations restantes

### Avant un premier build Linux

1. Produire et faire examiner un lock complet avec versions, empreintes SHA-256, certificats, empreintes de signataires, index et fermeture de dépendances réels.
2. Fournir tous les artefacts vérifiés, notamment les binaires Secure Boot et un véritable artefact ELF Linux x86_64 de l’agent; vérifier que les sources des templates correspondent à ceux livrés.
3. Vérifier indépendamment la chaîne des certificats et des clés, puis calculer et approuver le digest final du lock.
4. Utiliser un constructeur Linux amd64 offline dédié, conforme aux prérequis de la recette. Aucun build n’est autorisé par le présent lot.

### Avant un premier démarrage Hyper-V

1. Obtenir une autorisation distincte après revue du résultat du build et de son manifeste.
2. Implémenter et tester le mode de service de l’agent, l’AF_VSOCK réel et son arrêt sûr, puis valider le protocole et les contrôles d’identité dans l’environnement invité.
3. Intégrer le cycle de vie au superviseur indépendant et démontrer la récupération/nettoyage avec un essai isolé contrôlé.
4. Vérifier séparément les prérequis Windows et l’état de l’hôte au moment de l’essai. Ils n’ont pas été revérifiés dans cette exécution.

HTTP 503 reste le comportement de la Preview indisponible; `IsolationVerified` reste à `false` ([ControlledPreviewBrowser.cs](./AIPlatform.Infrastructure/Studio/ControlledPreviewBrowser.cs)). Ni le runtime Hyper-V ni les transports réels ne sont enregistrés pour la production par ce lot.

## Prochain lot recommandé

Finaliser le mode de service Linux sans commandes arbitraires ni ports réseau, puis produire un artefact Linux x86_64 vérifiable et des tests de packaging reproductibles. Après revue explicite du lock réel, un premier build Linux offline pourra être autorisé séparément. Le démarrage Hyper-V devra rester un lot distinct, conditionné par l’examen de l’image et une autorisation explicite.
