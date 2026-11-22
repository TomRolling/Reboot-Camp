using RebootCamp.Models;

namespace RebootCamp.Services;

/// <summary>
/// Mentions affichées dans l'onglet Désinstaller sur ce qu'il vaut mieux ne pas retirer.
/// Rouge = à ne pas désinstaller (Windows ou d'autres programmes en dépendent), orange = à garder de préférence.
/// Valable pour Windows 10 comme pour Windows 11 : les noms de paquets sont les mêmes.
/// </summary>
public static class UninstallAdvice
{
    // Applications Windows, reconnues par le nom technique de leur paquet (début du nom).
    private static readonly (string Package, Status Level, string Text)[] WindowsApps =
    [
        ("Microsoft.WindowsStore", Status.Error, "Ne pas désinstaller : c'est lui qui installe et met à jour les autres applications Windows"),
        ("Microsoft.DesktopAppInstaller", Status.Error, "Ne pas désinstaller : c'est winget, dont Reboot Camp a besoin pour les logiciels et les composants"),
        ("Microsoft.StorePurchaseApp", Status.Error, "Ne pas désinstaller : sert aux achats du Microsoft Store"),
        ("Microsoft.SecHealthUI", Status.Error, "Ne pas désinstaller : c'est l'écran de Sécurité Windows (antivirus, pare-feu)"),
        ("Microsoft.MicrosoftEdge", Status.Error, "Ne pas désinstaller : Windows et beaucoup d'applications s'en servent pour afficher des pages"),
        ("Microsoft.Win32WebViewHost", Status.Error, "Ne pas désinstaller : composant d'affichage utilisé par d'autres applications"),
        ("Microsoft.WindowsAppRuntime", Status.Error, "Ne pas désinstaller : d'autres applications en ont besoin pour démarrer"),
        ("Microsoft.VCLibs", Status.Error, "Ne pas désinstaller : d'autres applications en ont besoin pour démarrer"),
        ("Microsoft.UI.Xaml", Status.Error, "Ne pas désinstaller : d'autres applications en ont besoin pour démarrer"),
        ("Microsoft.NET.Native", Status.Error, "Ne pas désinstaller : d'autres applications en ont besoin pour démarrer"),
        ("MicrosoftWindows.Client", Status.Error, "Ne pas désinstaller : fait partie de l'interface de Windows"),
        ("Microsoft.Windows.ShellExperienceHost", Status.Error, "Ne pas désinstaller : fait partie de l'interface de Windows"),
        ("Microsoft.Windows.StartMenuExperienceHost", Status.Error, "Ne pas désinstaller : c'est le menu Démarrer"),
        ("Microsoft.AAD.BrokerPlugin", Status.Error, "Ne pas désinstaller : sert à la connexion aux comptes Microsoft"),
        ("Microsoft.AccountsControl", Status.Error, "Ne pas désinstaller : sert à la connexion aux comptes Microsoft"),

        ("NVIDIACorp.NVIDIAControlPanel", Status.Warning, "À garder : panneau de réglage de la carte graphique NVIDIA"),
        ("AdvancedMicroDevicesInc", Status.Warning, "À garder : panneau de réglage de la carte graphique AMD"),
        ("AppUp.IntelGraphics", Status.Warning, "À garder : panneau de réglage de la carte graphique Intel"),
        ("AppUp.IntelArcSoftware", Status.Warning, "À garder : panneau de réglage de la carte graphique Intel"),
        ("RealtekSemiconductorCorp", Status.Warning, "À garder : réglages du son Realtek"),
        ("DolbyLaboratories", Status.Warning, "À garder : réglages du son Dolby"),
        ("Microsoft.GamingServices", Status.Warning, "À garder si tu joues à des jeux du Microsoft Store ou du Game Pass"),
        ("Microsoft.GamingApp", Status.Warning, "À garder si tu joues à des jeux du Microsoft Store ou du Game Pass"),
        ("Microsoft.XboxIdentityProvider", Status.Warning, "À garder si tu joues à des jeux du Microsoft Store ou du Game Pass"),
        ("Microsoft.Xbox.TCUI", Status.Warning, "À garder si tu joues à des jeux du Microsoft Store ou du Game Pass"),
        ("Microsoft.WindowsTerminal", Status.Warning, "À garder de préférence : c'est le terminal par défaut de Windows 11"),
        ("Microsoft.Windows.Photos", Status.Warning, "À garder de préférence : c'est la visionneuse d'images par défaut"),
        ("Microsoft.WindowsNotepad", Status.Warning, "À garder de préférence : c'est le Bloc-notes de Windows"),
        ("Microsoft.ScreenSketch", Status.Warning, "À garder de préférence : c'est l'outil de capture d'écran (Win+Maj+S)"),
        ("Microsoft.HEIFImageExtension", Status.Warning, "À garder de préférence : sert à ouvrir les photos de smartphone"),
        ("Microsoft.HEVCVideoExtension", Status.Warning, "À garder de préférence : sert à lire certaines vidéos"),
        ("Microsoft.VP9VideoExtensions", Status.Warning, "À garder de préférence : sert à lire certaines vidéos"),
        ("Microsoft.WebpImageExtension", Status.Warning, "À garder de préférence : sert à afficher certaines images"),
        ("Microsoft.WebMediaExtensions", Status.Warning, "À garder de préférence : sert à lire certains sons et vidéos"),
    ];

    // Programmes classiques, reconnus par un morceau de leur nom affiché.
    private static readonly (string NamePart, Status Level, string Text)[] DesktopPrograms =
    [
        ("Microsoft Edge WebView2", Status.Error, "Ne pas désinstaller : beaucoup de programmes s'en servent pour afficher leur interface"),
        ("Microsoft Edge", Status.Error, "Ne pas désinstaller : Windows et beaucoup d'applications s'en servent pour afficher des pages"),
        ("Microsoft Visual C++", Status.Warning, "À garder : des jeux et logiciels en ont besoin pour démarrer"),
        // Les « Runtime » servent à faire tourner des logiciels ; les « SDK » ne servent qu'à en fabriquer (pas de mention).
        ("Microsoft .NET Runtime", Status.Warning, "À garder : des logiciels en ont besoin pour démarrer"),
        ("Microsoft .NET Host", Status.Warning, "À garder : des logiciels en ont besoin pour démarrer"),
        ("Microsoft .NET Framework", Status.Warning, "À garder : des logiciels en ont besoin pour démarrer"),
        ("Microsoft Windows Desktop Runtime", Status.Warning, "À garder : des logiciels en ont besoin pour démarrer"),
        ("Microsoft ASP.NET Core", Status.Warning, "À garder : des logiciels en ont besoin pour démarrer"),
        ("Microsoft DirectX", Status.Warning, "À garder : des jeux en ont besoin pour démarrer"),
        ("NVIDIA Graphics Driver", Status.Warning, "À garder : c'est le pilote de la carte graphique"),
        ("NVIDIA Pilote graphique", Status.Warning, "À garder : c'est le pilote de la carte graphique"),
        ("NVIDIA PhysX", Status.Warning, "À garder : des jeux en ont besoin"),
        ("AMD Software", Status.Warning, "À garder : c'est le pilote de la carte graphique"),
        ("AMD Chipset", Status.Warning, "À garder : pilotes de la carte mère"),
        ("Intel(R) Graphics", Status.Warning, "À garder : c'est le pilote de la carte graphique"),
        ("Intel(R) Chipset", Status.Warning, "À garder : pilotes de la carte mère"),
        ("Intel(R) Management Engine", Status.Warning, "À garder : composant de la carte mère"),
        ("Realtek", Status.Warning, "À garder : pilote du son ou du réseau"),
        ("Windows Driver Package", Status.Warning, "À garder : c'est un pilote de périphérique"),
        ("Microsoft Update Health Tools", Status.Warning, "À garder : sert à Windows Update"),
        ("Microsoft GameInput", Status.Warning, "À garder : des jeux s'en servent pour les manettes"),
    ];

    /// <summary>La mention à afficher pour ce programme, ou null s'il n'y a rien de particulier à dire.</summary>
    public static (Status Level, string Text)? For(InstalledProgram program)
    {
        if (program.Kind == ProgramKind.WindowsApp)
        {
            // Nom complet d'un paquet : « Nom_version_architecture__éditeur ».
            string package = program.Source.Split('_')[0];
            foreach (var (prefix, level, text) in WindowsApps)
                if (package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return (level, text);
            return null;
        }

        foreach (var (part, level, text) in DesktopPrograms)
            if (program.Name.Contains(part, StringComparison.OrdinalIgnoreCase)) return (level, text);
        return null;
    }
}
