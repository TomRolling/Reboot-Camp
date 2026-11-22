# Reboot Camp

Outil de maintenance Windows : un seul `.exe`, rien qui tourne en arrière-plan, rien de supprimé sans accord.
Fonctionne sur Windows 10 et Windows 11 (64 bits), sans rien installer avant.

| Onglet | Ce qu'il fait |
|---|---|
| **Accueil** | Un bouton « Analyser » qui enchaîne toutes les analyses et résume le résultat. |
| **Pilotes** | Liste les pilotes, repère les périphériques en erreur, installe les mises à jour de Windows Update et le dernier pilote graphique NVIDIA. |
| **Logiciels** | Met à jour les logiciels installés (navigateur, Discord, VLC…) avec `winget`. |
| **Nettoyage** | Calcule l'espace récupérable, montre un aperçu, supprime seulement ce qui est coché. |
| **Composants** | Vérifie DirectX, Visual C++ et .NET, et les installe avec `winget`. |
| **Extras** | Démarrage, infos système et batterie, santé des disques, sécurité, espace disque, réparation, rapport à exporter. |

## Utilisation

Copier `RebootCamp.exe` sur le PC et double-cliquer. Windows demande les droits administrateur (fenêtre UAC) : répondre Oui.
Il n'y a rien à installer : .NET est inclus dans le fichier.

`winget` est nécessaire pour les onglets Logiciels et Composants. Il est fourni avec Windows 11 et les Windows 10 récents ; sinon, installer « Programme d'installation d'application » depuis le Microsoft Store.

## Compiler et publier

Il faut le SDK .NET 10.

```powershell
# Lancer en développement (depuis un terminal administrateur)
dotnet run --project RebootCamp

# Produire le .exe autonome (environ 60 Mo)
dotnet publish RebootCamp/RebootCamp.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true
```

Le résultat est `RebootCamp\bin\Release\net10.0-windows\win-x64\publish\RebootCamp.exe`.

## Mises à jour de l'appli

À l'ouverture, l'appli demande à GitHub s'il existe une version plus récente. Si oui, un encart « Mettre à jour » apparaît dans la barre latérale : un clic télécharge la nouvelle version, vérifie son empreinte, remplace le fichier et relance l'appli.

Pour publier une version : augmenter `<Version>` dans le `.csproj`, lancer `.\publier.ps1`, puis créer sur GitHub une Release étiquetée `vX.Y.Z` avec le `.exe` en pièce jointe (le script le fait tout seul si `gh` est installé). Le dépôt doit être public et renseigné dans `Services/UpdateService.cs`.

## Ce que l'appli ne fait jamais

- Aucun démarrage automatique : pas de clé Run, pas de tâche planifiée, pas de service, pas d'icône de notification.
- Aucune analyse au lancement : tout part d'un clic. Seule la vérification de version ci-dessus est automatique.
- Fermer la fenêtre arrête tout : les opérations en cours sont annulées et les processus lancés (winget, dism, sfc) sont tués. Seule exception : une installation de pilote NVIDIA déjà commencée va jusqu'au bout, car l'interrompre laisserait l'écran sans pilote.
- Pas de télémétrie, pas de publicité. Le réseau n'est utilisé que par Windows Update, winget et le service de pilotes de NVIDIA, quand on le demande.
- Pas de « nettoyage du registre », pas de BIOS ni de firmware.

## Pilotes : d'où viennent-ils ?

- **Windows Update** pour tous les périphériques (y compris chipset et processeur).
- **NVIDIA** pour la carte graphique GeForce : l'appli interroge le service de nvidia.com, télécharge uniquement depuis `download.nvidia.com`, vérifie la signature numérique « NVIDIA Corporation », puis lance l'installeur en mode silencieux.
- **AMD** (Radeon et chipset Ryzen) : un bouton télécharge l'outil officiel « Auto-Detect and Install » sur amd.com, vérifie sa signature et l'ouvre. C'est lui qui choisit et installe le pilote.
- **Intel** (graphique, chipset, Wi-Fi, Bluetooth) : un bouton installe l'assistant officiel « Intel Driver & Support Assistant » avec winget. Cet assistant reste ensuite en arrière-plan ; on peut le désactiver dans Extras > Démarrage.

Avant toute installation, l'appli propose un point de restauration et une sauvegarde des pilotes actuels dans `C:\ProgramData\RebootCamp\SauvegardePilotes`.

## Sécurité du nettoyage

- **Liste blanche** : seuls les dossiers déclarés dans `Services/CleanupService.cs` sont touchés.
- Les fichiers temporaires de moins de 24 h sont gardés (ils peuvent servir à un programme ouvert).
- Les liens et jonctions ne sont jamais suivis ; un fichier verrouillé est simplement ignoré.
- Cache des navigateurs : cache uniquement (jamais cookies, historique ou mots de passe), et seulement si le navigateur est fermé.
- **Mode simulation** : fait tout comme en vrai sans rien supprimer.

## Comptes non administrateurs

Si la personne a un compte standard et tape le mot de passe d'un compte administrateur, l'appli tourne sous ce compte administrateur. Elle le détecte, l'affiche sur l'accueil, et vise quand même les fichiers temporaires, les caches de navigateur et le démarrage de la personne connectée. Sa corbeille n'est pas accessible dans ce cas.

## Journal

Les actions sont notées dans `%LOCALAPPDATA%\RebootCamp\logs\maintenance.log` (1 Mo maximum, rotation automatique).

## Organisation du code

```
RebootCamp/
  Services/     accès au système : WMI, Windows Update, registre, winget, NVIDIA, suppression de fichiers
  ViewModels/   logique de chaque onglet (pattern MVVM, CommunityToolkit.Mvvm)
  Views/        écrans en XAML, sans logique
  Controls/     bouton rond animé, convertisseurs, petits comportements
  Themes/       couleurs (Dark.xaml) et styles (Controls.xaml)
  Assets/       icône de l'application
```

Seules dépendances : `CommunityToolkit.Mvvm` et `System.Management`.
