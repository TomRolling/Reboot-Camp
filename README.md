# MaintenancePC

Outil de maintenance Windows léger : un seul `.exe`, rien qui tourne en arrière-plan, rien de supprimé sans accord.

| Onglet | Ce qu'il fait |
|---|---|
| **Accueil** | Un bouton « Analyser » qui enchaîne les trois analyses et résume le résultat. |
| **Pilotes** | Liste les pilotes installés, repère les périphériques en erreur, cherche et installe les mises à jour proposées par Windows Update. |
| **Nettoyage** | Calcule l'espace récupérable (fichiers temporaires, caches, corbeille…), montre un aperçu, supprime seulement ce qui est coché. |
| **Composants** | Vérifie DirectX, Visual C++ et .NET, et les installe avec `winget`. |
| **Extras** | Programmes au démarrage, infos système, santé des disques, `sfc` / `DISM`, point de restauration. |

## Prérequis

- Windows 10 ou 11, 64 bits.
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (l'onglet Composants sait l'installer une fois l'appli lancée).
- `winget` pour l'onglet Composants (fourni avec Windows 11 ; sinon « Programme d'installation d'application » dans le Microsoft Store).
- Un compte administrateur : l'appli demande l'élévation (fenêtre UAC) au lancement.

## Compiler et publier

```powershell
# Lancer en développement (depuis un terminal administrateur)
dotnet run --project MaintenancePC

# Produire le .exe unique
dotnet publish MaintenancePC/MaintenancePC.csproj -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false
```

Le résultat est `MaintenancePC\bin\Release\net10.0-windows\win-x64\publish\MaintenancePC.exe` (environ 1 Mo). Ce fichier seul suffit, on peut le copier où on veut.

## Ce que l'appli ne fait jamais

- Aucun démarrage automatique : pas de clé Run, pas de tâche planifiée, pas de service, pas d'icône de notification.
- Aucune analyse au lancement : tout part d'un clic.
- Fermer la fenêtre arrête tout : les opérations en cours sont annulées et les processus lancés (winget, dism, sfc) sont tués.
- Pas de télémétrie, pas de publicité. Le réseau n'est utilisé que par Windows Update et winget, quand on le demande.
- Pas de « nettoyage du registre », pas de pilotes téléchargés ailleurs que sur Windows Update.

## Sécurité du nettoyage

- **Liste blanche** : seuls les dossiers déclarés dans `Services/CleanupService.cs` sont touchés.
- Les fichiers temporaires de moins de 24 h sont gardés (ils peuvent servir à un programme ouvert).
- Les liens et jonctions ne sont jamais suivis ; un fichier verrouillé est simplement ignoré.
- Cache des navigateurs : cache uniquement (jamais cookies, historique ou mots de passe), et seulement si le navigateur est fermé.
- **Mode simulation** : fait tout comme en vrai sans rien supprimer.
- Point de restauration proposé (coché par défaut) avant un nettoyage ou une installation de pilote.

## Journal

Les actions sont notées dans `%LOCALAPPDATA%\MaintenancePC\logs\maintenance.log` (1 Mo maximum, rotation automatique).

## Organisation du code

```
MaintenancePC/
  Services/     accès au système : WMI, Windows Update, registre, winget, suppression de fichiers
  ViewModels/   logique de chaque onglet (pattern MVVM, CommunityToolkit.Mvvm)
  Views/        écrans en XAML, sans logique
  Controls/     bouton rond animé, convertisseurs, petits comportements
  Themes/       couleurs (Dark.xaml) et styles (Controls.xaml)
```

Seules dépendances : `CommunityToolkit.Mvvm` et `System.Management`.
