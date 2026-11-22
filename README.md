# Reboot Camp

Outil de maintenance Windows : un seul `.exe`, rien qui tourne en arrière-plan, rien de supprimé sans accord.
Fonctionne sur Windows 10 et Windows 11 (64 bits), sans rien installer avant.

![L'écran d'accueil de Reboot Camp après une analyse](docs/capture.png)

## Télécharger

**[Télécharger RebootCamp.exe](https://github.com/TomRolling/Reboot-Camp/releases/latest/download/RebootCamp.exe)** (dernière version, environ 60 Mo)

1. Double-cliquer sur le fichier téléchargé.
2. Windows affiche « Windows a protégé votre ordinateur » : cliquer sur **Informations complémentaires**, puis **Exécuter quand même**. Cet écran apparaît parce que le fichier n'a pas de signature payante, pas parce qu'il est dangereux. Il ne revient pas aux lancements suivants.
3. Windows demande les droits administrateur : répondre **Oui**.

Toutes les versions sont sur la page [Releases](https://github.com/TomRolling/Reboot-Camp/releases).

## Ce que fait l'appli

| Onglet | Ce qu'il fait |
|---|---|
| **Accueil** | Un bouton « Analyser » qui enchaîne toutes les analyses et résume le résultat. |
| **Pilotes** | Liste les pilotes, repère les périphériques en erreur, installe les mises à jour de Windows Update et le dernier pilote graphique NVIDIA. |
| **Logiciels** | Met à jour les logiciels installés (navigateur, Discord, VLC…) avec `winget`. |
| **Nettoyage** | Calcule l'espace récupérable, montre un aperçu, supprime seulement ce qui est coché. |
| **Composants** | Vérifie DirectX, Visual C++ et .NET : manquant, à mettre à jour ou à jour. Installe ou met à jour la sélection avec `winget`. |
| **Désinstaller** | Liste les programmes et les applications Windows, lance leur désinstalleur officiel, puis propose de supprimer les dossiers et raccourcis laissés derrière. Signale ce qu'il vaut mieux garder. |
| **Extras** | Démarrage, infos système et batterie, santé des disques, sécurité, espace disque, réparation, rapport à exporter. |

## Utilisation

Il n'y a rien à installer : .NET est inclus dans le fichier. On peut le ranger où on veut (Bureau, clé USB…) et le supprimer pour s'en débarrasser.

Rien ne se lance tout seul : chaque analyse part d'un bouton, et rien n'est supprimé ou installé sans un aperçu et une confirmation.

La fin d'une action se voit : le bouton rond passe au vert avec une coche et « TERMINÉ », et le bouton de l'appli clignote dans la barre des tâches si on est parti sur une autre fenêtre.

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

Pour publier une version : augmenter `<Version>` dans le `.csproj`, faire un commit, puis lancer `.\publier.ps1`. Le script fabrique le `.exe`, envoie le code sur GitHub et crée la Release étiquetée `vX.Y.Z` avec le `.exe` en pièce jointe (il faut l'outil `gh`, connecté avec `gh auth login`). Le dépôt doit être public et renseigné dans `Services/UpdateService.cs`.

## Ce que l'appli ne fait jamais

- Aucun démarrage automatique : pas de clé Run, pas de tâche planifiée, pas de service, pas d'icône de notification.
- Aucune analyse au lancement : tout part d'un clic. Seule la vérification de version ci-dessus est automatique.
- Fermer la fenêtre arrête tout : les opérations en cours sont annulées et les processus lancés (winget, dism, sfc) sont tués. Deux exceptions, parce que les interrompre ferait plus de mal que de bien : une installation de pilote NVIDIA déjà commencée et un désinstalleur déjà lancé vont jusqu'au bout.
- Aucun redémarrage du PC sans accord : quand il en faut un, une fenêtre propose « Redémarrer maintenant » ou « Plus tard ». Pendant une installation, l'appli retient aussi les redémarrages que certains installeurs déclenchent d'eux-mêmes (un installeur qui force l'arrêt de Windows ne peut toutefois pas être bloqué).
- Pas de télémétrie, pas de publicité. Le réseau n'est utilisé que par Windows Update, winget et le service de pilotes de NVIDIA, quand on le demande.
- Pas de « nettoyage du registre » : le registre de Windows est lu (c'est là que se trouve la liste des programmes), jamais modifié. Pas de BIOS ni de firmware.

## Logiciels : quand une mise à jour ne passe pas

L'onglet Logiciels s'appuie sur `winget`, l'outil de Microsoft. Trois cas où il ne peut pas tout faire, et ce que l'appli en fait :

- **L'éditeur a changé son fichier d'installation** sans que winget l'ait revérifié : winget refuse de l'installer, par sécurité, et l'appli ne contourne jamais cette vérification. La ligne passe en orange avec un bouton « Réessayer » ; en attendant, le logiciel se met souvent à jour tout seul quand on l'ouvre.
- **La nouvelle version s'installe autrement que l'ancienne** : winget ne peut pas la poser par-dessus. L'appli propose « Désinstaller puis réinstaller », après confirmation.
- **Le logiciel annonce mal son numéro de version** : winget le croit ancien alors qu'il est à jour, et le reproposerait à chaque analyse. Une fois la mise à jour faite, l'appli ne le propose plus et l'indique en bas de la liste (bouton « Réafficher »).

Ces lignes ne sont pas comptées dans les mises à jour à faire. L'appli s'en souvient d'une ouverture à l'autre dans `%LOCALAPPDATA%\RebootCamp\logiciels.json`, et réessaie d'elle-même dès que winget propose une version plus récente.

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
- Un aperçu est toujours affiché avant de supprimer : seul ce qui est coché part.

## Désinstaller un programme

L'onglet sépare les **programmes** classiques des **applications Windows** (Microsoft Store et applications fournies avec Windows).

1. La désinstallation passe par le moyen officiel : le désinstalleur fourni par le programme, ou la commande de Windows pour une application du Store. Un programme à la fois, après confirmation, avec un point de restauration proposé avant.
2. L'appli cherche ensuite ce que le programme a laissé sur le disque : son dossier d'installation, les dossiers à son nom dans `Program Files`, `ProgramData` et `AppData`, son dossier du menu Démarrer et ses raccourcis du Bureau.
3. Elle montre cette liste avec des cases à cocher. Rien n'est supprimé avant le clic sur « Supprimer la sélection ».

Garde-fous de cette recherche (`Services/LeftoverService.cs`) :

- jamais un dossier général (`Program Files` lui-même, « Microsoft », « Common Files »…) ni quoi que ce soit dans le dossier de Windows ;
- jamais un dossier dans lequel un autre programme encore installé se trouve, ni un dossier qui porte le nom d'une autre version encore installée ;
- les liens et jonctions ne sont jamais suivis ;
- un fichier verrouillé est confié à Windows, qui le supprime lui-même au prochain redémarrage. L'appli ne se relance pas au démarrage pour autant.

Des mentions en couleur signalent ce qu'il vaut mieux ne pas retirer, sur Windows 10 comme sur Windows 11 : en rouge ce dont Windows ou d'autres programmes dépendent (Microsoft Store, winget, Edge et WebView2…), en orange ce qu'il est préférable de garder (pilotes, Visual C++, .NET, panneaux de réglage NVIDIA, AMD ou Intel…).

Limite à connaître : l'appli ne retire pas les réglages qu'un programme a pu laisser dans le registre. C'est voulu : supprimer des entrées du registre « au jugé » est la première cause de dégâts des outils de ce genre.

## Tests automatiques

Le dossier `RebootCamp.Tests` contient des tests qui vérifient ces règles sur de faux dossiers temporaires, sans jamais toucher aux vrais fichiers ni désinstaller quoi que ce soit : rien hors de la liste blanche, liens jamais suivis, fichiers récents gardés, fichiers verrouillés ignorés, dossiers généraux et dossiers d'autres programmes jamais proposés après une désinstallation.

```powershell
dotnet test
```

`publier.ps1` les lance avant chaque publication et s'arrête si l'un d'eux échoue. Ils ne font pas partie du `.exe`.

## Comptes non administrateurs

Si la personne a un compte standard et tape le mot de passe d'un compte administrateur, l'appli tourne sous ce compte administrateur. Elle le détecte, l'affiche sur l'accueil, et vise quand même les fichiers temporaires, les caches de navigateur et le démarrage de la personne connectée. Sa corbeille n'est pas accessible dans ce cas.

## Journal

Les actions sont notées dans `%LOCALAPPDATA%\RebootCamp\logs\maintenance.log` (1 Mo maximum, rotation automatique).
Le bouton « Ouvrir le journal » de l'onglet Extras ouvre ce dossier : en cas de souci, c'est ce fichier qu'il faut envoyer, avec le rapport exporté depuis le même onglet.

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

Seules dépendances de l'appli : `CommunityToolkit.Mvvm` et `System.Management`. Le projet de tests utilise en plus `xunit`.

## Licence

[MIT](LICENSE) : le code peut être réutilisé librement, sans garantie.
