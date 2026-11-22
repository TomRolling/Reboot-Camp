# Reboot Camp — Outil de maintenance Windows

> Nom choisi par Tom le 22/11/2026 : **Reboot Camp** (projet, dossiers, fichier `RebootCamp.exe`). Le nom affiché à l'écran est défini dans `Services/AppInfo.cs`.

## Le projet
Application de bureau Windows (un seul `.exe`) inspirée de Driver Booster + Advanced SystemCare, mais **légère, propre et honnête**.
Onglets : **Accueil**, **Pilotes**, **Logiciels**, **Nettoyage**, **Composants**, **Désinstaller**, **Extras**.
Utilisateur : Tom, BTS SIO, PC portable Asus (Windows, GPU NVIDIA). Toutes les réponses et l'interface sont **en français**.

## Façon de travailler (OBLIGATOIRE)
- On avance **phase par phase** (voir plus bas). À la fin de chaque phase : compiler, lancer, tester, faire un commit, puis **enchaîner sur la suivante sans s'arrêter** (décision de Tom du 22/11/2026 : il teste le résultat final et dit ensuite ce qu'il faut changer). Les mentions « stop » de la liste des phases ne s'appliquent plus.
- Si quelque chose n'est pas clair, **pose une question** au lieu de supposer.
- Avant d'utiliser un identifiant `winget` ou une API système, **vérifie qu'il existe** (`winget search`, doc Microsoft). Rien d'inventé.
- Explique brièvement ce que tu as fait à chaque phase (Tom veut comprendre le code).

## Stack technique
- **C# / WPF / .NET 10 (LTS)**, pattern MVVM léger (CommunityToolkit.Mvvm autorisé, rien d'autre sans demander).
- Accès système : `System.Management` (WMI/CIM), API COM Windows Update Agent (`Microsoft.Update.Session`), registre, `winget` en ligne de commande.
- Manifeste avec `requireAdministrator` (nécessaire pour pilotes et nettoyage système).
- Publication : `dotnet publish RebootCamp/RebootCamp.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true`
  → un seul `.exe` autonome d'environ 60 Mo qui embarque .NET : il démarre sur n'importe quel Windows 10/11 64 bits sans rien installer (décision de Tom du 22/11/2026, pour pouvoir le donner à ses proches).
- Doit fonctionner sur **Windows 10 et Windows 11**, sur n'importe quel PC (fixe ou portable, NVIDIA/AMD/Intel, toutes marques) et pour un utilisateur débutant.

## Règles « appli clean » (NON NÉGOCIABLES)
- **Aucun** démarrage automatique : pas de clé Run, pas de tâche planifiée, pas de service, pas d'icône dans la barre des tâches/zone de notification.
- **Fermer la fenêtre = quitter complètement.** Annuler toutes les tâches en cours (`CancellationToken`), tuer les processus enfants (winget, dism…), puis `Application.Shutdown()`. Vérifier dans le Gestionnaire des tâches qu'il ne reste **aucun processus**.
- **Rien ne tourne tout seul** : aucun scan au lancement, tout se déclenche par un bouton.
  Seule exception (demande de Tom du 22/11/2026) : à l'ouverture, une requête à GitHub pour savoir si une version plus récente de l'appli existe (`UpdateService`). Rien n'est téléchargé sans clic.
- Pas de télémétrie, pas de pub, pas de connexion réseau sauf Windows Update et winget quand Tom le demande.
- Tout en `async` : l'interface ne doit jamais geler. Barre de progression + bouton Annuler sur les opérations longues.
- Consommation au repos ≈ 0 % CPU, RAM la plus faible possible.

## Mise à jour de l'appli
- Via les Releases du dépôt GitHub **public** `TomRolling/Reboot-Camp` (`UpdateService.Repository`) : étiquette `vX.Y.Z` + `RebootCamp.exe` en pièce jointe.
- **Publication automatique** (depuis le 22/11/2026) : chaque `git push` sur `main` lance `.github/workflows/publication.yml` → tests, `.exe` autonome, Release. Le numéro est `VERSION_DE_BASE.N` (N = numéro de l'exécution), base actuelle **1.2**. Ne jamais baisser la base : un numéro plus petit que la dernière Release ne serait jamais proposé aux applis installées. Un push qui ne touche que des `.md`, `docs/`, `LICENSE` ou `.gitignore` ne publie rien.
- Conséquence : **tout push de code publie une version**. Ne pousser que du code qui compile et dont les tests passent ; le `<Version>` du `.csproj` ne sert plus qu'aux fabrications locales.
- `publier.ps1` ne fait que fabriquer le `.exe` en local (dossier `sortie/`), pour l'essayer.
- L'exe n'est pas signé : SmartScreen avertit au premier lancement d'un fichier téléchargé par un navigateur. Seule une signature de code le supprime vraiment.

## Règles de sécurité
- **Point de restauration système** proposé (case cochée par défaut) avant toute installation de pilote ou gros nettoyage.
- **Toujours un aperçu avant suppression** : liste des catégories, taille estimée, cases à cocher. On supprime seulement ce qui est coché.
- Le nettoyage ne supprime **que** dans une liste blanche de dossiers connus (ci-dessous). Jamais ailleurs. Jamais de « nettoyage du registre » : le registre est lu, jamais modifié.
- Seule autre suppression autorisée (demande de Tom du 22/11/2026) : les **traces d'un programme qu'on vient de désinstaller**, avec les garde-fous de `LeftoverService` (voir l'onglet Désinstaller).
- Fichiers verrouillés/en cours d'utilisation : on les ignore sans planter.
- **Jamais de redémarrage du PC sans accord** (demande de Tom du 22/11/2026, après qu'un installeur lancé par winget a redémarré son PC) : quand un redémarrage est nécessaire, une fenêtre propose « Redémarrer maintenant » ou « Plus tard » (`RestartService`, `Views/ConfirmDialog`). Toute installation par winget passe par `Services/Winget.cs` (`Winget.RunAsync`), qui retient les redémarrages demandés par les installeurs (`RestartService.Guard`) et traduit les codes de sortie : ne jamais appeler `winget install/upgrade` ailleurs, ni passer `--allow-reboot`.
- **Tests automatiques** (ajoutés le 22/11/2026, accord de Tom pour xUnit) : `RebootCamp.Tests` vérifie les règles du nettoyage sur de faux dossiers. Lancer `dotnet test` après toute modification de `CleanupService` ; `publier.ps1` refuse de publier si un test échoue.
- Journal des actions dans `%LOCALAPPDATA%\RebootCamp\logs` (rotation, max ~1 Mo).

## Design
- Thème sombre moderne, barre latérale à gauche avec les 4 onglets + un onglet Accueil.
- Style **épuré et minimaliste**, facile à lire : peu de texte, gros chiffres, une action principale évidente par écran. Quelqu'un qui n'y connaît rien doit comprendre.
- **Animations** pendant les analyses et mises à jour (anneau qui tourne, barres de progression fluides, transitions entre onglets), dans l'esprit de Driver Booster. Aucune animation au repos (0 % CPU).
- Accueil : résumé (nb de pilotes à mettre à jour, espace récupérable, composants manquants) rempli seulement après un bouton « Analyser tout ».
- **Icône de l'appli** (choisie par Tom le 22/11/2026) : flèche de redémarrage bleue + coche blanche sur fond sombre. `Assets/app.ico` contient neuf tailles (16 à 256 px) ; le même dessin est tracé en vecteurs dans la barre latérale (`MainWindow.xaml`) et sert d'image au README (`docs/icone.png`). Les trois doivent rester identiques.
- Gros bouton d'action par onglet, listes claires avec icônes d'état (OK / à mettre à jour / erreur).
- **Icônes des programmes** dans les listes où il y en a une vraie (demande de Tom du 22/11/2026) : Logiciels, Désinstaller, Extras > Démarrage. `IconService` les lit sur le PC (fichier `.exe`/`.ico` du programme, logo du paquet pour une application Windows) ; rien n'est téléchargé. Style `AppIcon`, avec une icône neutre quand rien n'est trouvé.
- **La fin d'une action doit se voir** (demande de Tom du 22/11/2026) : le bouton rond passe au vert avec une coche et « TERMINÉ » (orange si une partie a échoué), le titre commence par « Terminé : » ou « Nettoyage terminé : », et le bouton de l'appli clignote dans la barre des tâches si la fenêtre n'est pas au premier plan. Dans le code : `MarkDone()` avant `EndOperation()`.
- Un échec winget connu est expliqué en français sous le nom du logiciel (`Winget.Explain`), pas seulement dans le journal.
- Chaque onglet a son propre libellé sur le bouton rond et son propre titre (« Analyser les pilotes », « Analyser les logiciels », « Analyser avant de nettoyer », « Vérifier les composants ») : on doit savoir sur quelle page on est sans regarder la barre latérale (demande de Tom du 22/11/2026).

## Contenu des onglets

### 1. Pilotes
- Scan : liste de tous les périphériques (`Win32_PnPSignedDriver`) avec nom, fabricant, version, date du pilote.
- Détection des périphériques **sans pilote ou en erreur** (`Win32_PnPEntity`, `ConfigManagerErrorCode != 0`) en rouge.
- Mises à jour disponibles via l'API Windows Update : recherche `IsInstalled=0 and Type='Driver'`, affichage, installation de la sélection.
- Bouton « Page du fabricant » pour la carte graphique NVIDIA et le support Asus (ouvre le navigateur).
- Pilote graphique NVIDIA : recherché et téléchargé directement chez NVIDIA (`NvidiaDriverService`), signature « NVIDIA Corporation » vérifiée avant exécution, installation silencieuse sur clic (demande de Tom du 22/11/2026). AMD et Intel n'ont pas de service pour connaître le dernier pilote : l'appli télécharge et lance l'outil officiel du fabricant (`VendorToolService` : « Auto-Detect and Install » d'AMD, signature vérifiée ; « Intel Driver & Support Assistant » via winget).
- Jamais de BIOS ni de firmware. Pas de base de pilotes maison, pas de téléchargement depuis des sites tiers.
- Avant installation : point de restauration + sauvegarde des pilotes (`pnputil /export-driver`), cases cochées par défaut.

### 2. Nettoyage (liste blanche)
- Fichiers temporaires utilisateur (`%TEMP%`) et Windows (`C:\Windows\Temp`)
- Cache Windows Update (`SoftwareDistribution\Download`, arrêter/redémarrer le service `wuauserv` proprement)
- Cache Optimisation de la distribution
- Rapports d'erreurs Windows (WER), vidages mémoire / minidumps
- Cache des miniatures
- Corbeille (`SHEmptyRecycleBin`)
- Cache des navigateurs Chrome / Edge / Firefox : **cache uniquement**, jamais cookies/historique/mots de passe, et seulement si le navigateur est fermé (sinon prévenir)
- Option avancée (décochée par défaut, prévenir que c'est long) : `DISM /Online /Cleanup-Image /StartComponentCleanup`
- Afficher l'espace libéré à la fin.

### 3. Composants
- Détecter et installer/mettre à jour via `winget` :
  - DirectX End-User Runtime (anciennes DLL pour les jeux, détecter via `d3dx9_43.dll`)
  - Visual C++ Redistributables (toutes versions x86 + x64, détecter via le registre)
  - .NET Desktop Runtime / .NET Framework
- Trois états par composant (demande de Tom du 22/11/2026) : **manquant** (rouge), **mise à jour disponible** (orange, d'après `winget upgrade`), **à jour** (vert). Cases à cocher sur ce qui est à faire, « Tout cocher / décocher » et un seul bouton « Installer la sélection », comme dans Logiciels.
- Vérifier que `winget` est présent, sinon expliquer comment l'obtenir.
- Afficher la sortie de winget dans un petit panneau de log.

### 3 bis. Logiciels (ajouté le 22/11/2026)
- Liste les logiciels installés qui ont une mise à jour (`winget upgrade`), cases à cocher, mise à jour de la sélection, journal winget.
- « Soit ça marche, soit ce n'est pas proposé comme à faire » (demande de Tom du 22/11/2026). Le carnet `UpdateMemory` (`%LOCALAPPDATA%\RebootCamp\logiciels.json`) retient chaque tentative, pour la version proposée à ce moment-là :
  - mise à jour réussie que winget repropose quand même (numéro de version mal lu, ex. Lunar Client « 3.7.28-ow ») : le logiciel n'est plus listé, une phrase le dit en bas, bouton « Réafficher » ;
  - mise à jour que winget ne peut pas faire (`Winget.IsBlocked`) : ligne mise à part en orange, sans case, non comptée sur l'accueil, avec l'explication et un bouton — « Réessayer » (empreinte de l'installeur non vérifiée) ou « Désinstaller puis réinstaller » (technologie d'installation différente, après confirmation).
  - Ne jamais contourner la vérification d'empreinte de winget (`--ignore-security-hash`).

### 3 ter. Désinstaller (ajouté le 22/11/2026, demande de Tom : « comme Revo Uninstaller, mais sans toucher au registre »)
- Deux catégories : **Programmes** (liste des programmes installés de Windows, lue dans le registre) et **Applications Windows** (Microsoft Store et applications fournies avec Windows, via `Get-AppxPackage` + `Get-StartApps`).
- Désinstallation par le moyen officiel : le désinstalleur du programme (`msiexec /x … /passive /norestart` pour Windows Installer), `Remove-AppxPackage` pour une application Windows. Un programme à la fois, après confirmation. Annuler n'interrompt jamais un désinstalleur en cours : on cesse seulement de l'attendre.
- Ensuite, recherche des **traces** laissées sur le disque (`LeftoverService`) : dossier d'installation déclaré, dossiers au nom exact du programme dans Program Files / ProgramData / AppData, menu Démarrer, raccourcis du Bureau. Aperçu avec cases à cocher ; on ne supprime que ce qui est coché.
- Garde-fous : jamais un dossier général (Program Files, « Microsoft », « Common Files »…), jamais dans le dossier de Windows, jamais un dossier où vit un autre programme encore installé ou qui porte le nom d'une autre version installée, jamais à travers un lien ou une jonction.
- Fichier verrouillé : confié à Windows pour suppression au prochain redémarrage (`MoveFileEx` + `MOVEFILE_DELAY_UNTIL_REBOOT`), puis la fenêtre de redémarrage est proposée. L'appli ne se relance pas au démarrage.
- **Mentions** sur ce qu'il vaut mieux garder (`UninstallAdvice`) : rouge « Ne pas désinstaller » (Microsoft Store, winget, Edge/WebView2…), orange « À garder » (pilotes, Visual C++, .NET Runtime, panneaux NVIDIA/AMD/Intel…). Valable Windows 10 et 11. Un programme en rouge demande une confirmation appuyée.
- Point de restauration avant chaque désinstallation, case cochée par défaut.

### 4. Extras
- **Sécurité** (lecture seule) : antivirus, Defender, pare-feu, mises à jour Windows en attente.
- **Espace disque** : taille des dossiers du disque système et plus gros fichiers, sans rien supprimer.
- **Rapport** : export d'un fichier texte résumant le PC, pour le dépannage à distance.
- **Réparation réseau** : `ipconfig /flushdns` + `netsh winsock reset`.
- **Démarrage** : programmes lancés au démarrage (clés Run HKCU/HKLM, dossier Démarrage), activer/désactiver comme le Gestionnaire des tâches (clés `StartupApproved`), sans rien supprimer. Conseil affiché pour les programmes connus.
- **Infos système** : CPU, GPU, RAM, version de Windows, disques et espace libre, santé de la batterie (portables).
- **Santé du disque** : `MSFT_PhysicalDisk` (HealthStatus) + statut SMART si dispo.
- **Réparation** : boutons pour `sfc /scannow` et `DISM /RestoreHealth` avec la sortie en direct.
- **Point de restauration** : bouton pour en créer un manuellement.

## Phases
0. Vérifier l'environnement (`dotnet --version`, SDK .NET 10, `winget`), créer la solution, Git init. → **stop, validation**
1. Squelette : fenêtre, thème sombre, navigation entre onglets vides, fermeture propre testée. → **stop**
2. Onglet Pilotes. → **stop**
3. Onglet Nettoyage (d'abord en mode « simulation » qui n'efface rien, puis le vrai). → **stop**
   Le mode simulation a été retiré de l'écran le 22/11/2026 (demande de Tom : inutile dans l'appli publiée). Il reste dans `CleanupService` pour les tests automatiques.
4. Onglet Composants. → **stop**
5. Onglet Extras + écran Accueil. → **stop**
6. Publication du `.exe`, test du lancement à froid, vérification qu'aucun processus ne reste après fermeture, README. → **stop**
