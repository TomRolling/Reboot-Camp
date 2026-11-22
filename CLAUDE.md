# Reboot Camp — Outil de maintenance Windows

> Nom public choisi par Tom le 22/11/2026 : **Reboot Camp** (fichier `RebootCamp.exe`, dossiers de données `RebootCamp`). Le projet, le dossier et les espaces de noms gardent le nom interne `MaintenancePC`. Le nom affiché vient de `Services/AppInfo.cs` et du `.csproj`.

## Le projet
Application de bureau Windows (un seul `.exe`) inspirée de Driver Booster + Advanced SystemCare, mais **légère, propre et honnête**.
4 onglets : **Pilotes**, **Nettoyage**, **Composants**, **Extras**.
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
- Publication : `dotnet publish MaintenancePC/MaintenancePC.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true`
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
- Via les Releases d'un dépôt GitHub **public** : étiquette `vX.Y.Z` + le `.exe` en pièce jointe. Le dépôt est à renseigner dans `UpdateService.Repository` (vide = fonction désactivée).
- `publier.ps1` fabrique le `.exe` et crée la Release si `gh` est installé.
- L'exe n'est pas signé : SmartScreen avertit au premier lancement d'un fichier téléchargé par un navigateur. Seule une signature de code le supprime vraiment.

## Règles de sécurité
- **Point de restauration système** proposé (case cochée par défaut) avant toute installation de pilote ou gros nettoyage.
- **Toujours un aperçu avant suppression** : liste des catégories, taille estimée, cases à cocher. On supprime seulement ce qui est coché.
- On ne supprime **que** dans une liste blanche de dossiers connus (ci-dessous). Jamais ailleurs. Jamais de « nettoyage du registre ».
- Fichiers verrouillés/en cours d'utilisation : on les ignore sans planter.
- Journal des actions dans `%LOCALAPPDATA%\RebootCamp\logs` (rotation, max ~1 Mo).

## Design
- Thème sombre moderne, barre latérale à gauche avec les 4 onglets + un onglet Accueil.
- Style **épuré et minimaliste**, facile à lire : peu de texte, gros chiffres, une action principale évidente par écran. Quelqu'un qui n'y connaît rien doit comprendre.
- **Animations** pendant les analyses et mises à jour (anneau qui tourne, barres de progression fluides, transitions entre onglets), dans l'esprit de Driver Booster. Aucune animation au repos (0 % CPU).
- Accueil : résumé (nb de pilotes à mettre à jour, espace récupérable, composants manquants) rempli seulement après un bouton « Analyser tout ».
- Gros bouton d'action par onglet, listes claires avec icônes d'état (OK / à mettre à jour / erreur).

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
- Vérifier que `winget` est présent, sinon expliquer comment l'obtenir.
- Afficher la sortie de winget dans un petit panneau de log.

### 3 bis. Logiciels (ajouté le 22/11/2026)
- Liste les logiciels installés qui ont une mise à jour (`winget upgrade`), cases à cocher, mise à jour de la sélection, journal winget.

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
4. Onglet Composants. → **stop**
5. Onglet Extras + écran Accueil. → **stop**
6. Publication du `.exe`, test du lancement à froid, vérification qu'aucun processus ne reste après fermeture, README. → **stop**
