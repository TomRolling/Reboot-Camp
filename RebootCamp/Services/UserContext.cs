using System.IO;
using System.Management;
using System.Security.Principal;
using Microsoft.Win32;

namespace RebootCamp.Services;

/// <summary>
/// Sur un compte standard, la fenêtre UAC demande le mot de passe d'un AUTRE compte (administrateur) :
/// l'appli tourne alors sous ce compte admin, et « mes fichiers temporaires » seraient ceux de l'admin.
/// Cette classe retrouve la personne réellement assise devant le PC et donne SES dossiers.
/// Quand c'est le même compte (cas le plus courant), tout est identique à d'habitude.
/// </summary>
public static class UserContext
{
    private sealed record State(
        bool IsDifferentUser, string InteractiveUser, string ElevatedUser,
        string? Sid, string LocalAppData, string RoamingAppData, string TempPath);

    private static readonly Lazy<State> Current = new(Detect);

    /// <summary>Vrai si l'appli tourne sous un autre compte que celui de la session ouverte.</summary>
    public static bool IsDifferentUser => Current.Value.IsDifferentUser;

    public static string InteractiveUser => Current.Value.InteractiveUser;
    public static string ElevatedUser => Current.Value.ElevatedUser;
    public static string LocalAppData => Current.Value.LocalAppData;
    public static string RoamingAppData => Current.Value.RoamingAppData;
    public static string TempPath => Current.Value.TempPath;

    public static string StartupFolder => IsDifferentUser
        ? Path.Combine(Current.Value.RoamingAppData, @"Microsoft\Windows\Start Menu\Programs\Startup")
        : Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    /// <summary>Le registre « HKEY_CURRENT_USER » de la personne devant le PC.</summary>
    public static RegistryKey OpenUserRegistry()
    {
        if (!IsDifferentUser || Current.Value.Sid is null)
            return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);

        // Le registre d'un autre utilisateur connecté se trouve sous HKEY_USERS\<son identifiant>.
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        return users.OpenSubKey(Current.Value.Sid, writable: true)
               ?? RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
    }

    private static State Detect()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string elevated = identity.Name;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var same = new State(false, elevated, elevated, identity.User?.Value, localAppData, roaming,
            Path.TrimEndingDirectorySeparator(Path.GetTempPath()));

        try
        {
            // Win32_ComputerSystem.UserName = la personne connectée à l'écran du PC.
            string? interactive = null;
            using (var searcher = new ManagementObjectSearcher("SELECT UserName FROM Win32_ComputerSystem"))
            using (var results = searcher.Get())
            {
                foreach (ManagementBaseObject item in results)
                    using (item) interactive = item["UserName"] as string;
            }
            if (string.IsNullOrEmpty(interactive)) return same;

            string sid = new NTAccount(interactive).Translate(typeof(SecurityIdentifier)).Value;
            if (sid == identity.User?.Value) return same;

            // Dossier du profil de cette personne, d'après la liste des profils de Windows.
            using var profiles = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
            string? profile = profiles?.GetValue("ProfileImagePath") as string;
            if (string.IsNullOrEmpty(profile) || !Directory.Exists(profile))
            {
                Logger.Error($"Profil de {interactive} introuvable : dossiers du compte {elevated} utilisés.");
                return same with { IsDifferentUser = true, InteractiveUser = interactive, Sid = null };
            }

            string userLocal = Path.Combine(profile, @"AppData\Local");
            return new State(true, interactive, elevated, sid, userLocal,
                Path.Combine(profile, @"AppData\Roaming"), Path.Combine(userLocal, "Temp"));
        }
        catch (Exception ex)
        {
            Logger.Error("Détection du compte utilisateur", ex);
            return same;
        }
    }
}
