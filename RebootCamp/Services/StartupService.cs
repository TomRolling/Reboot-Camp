using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using RebootCamp.Models;
using Microsoft.Win32;

namespace RebootCamp.Services;

/// <summary>Un programme lancé au démarrage de Windows.</summary>
public sealed partial class StartupEntry : ObservableObject
{
    public string Name { get; }
    public string Command { get; }
    public string Source { get; }

    /// <summary>Conseil pour les programmes connus (vide si on ne sait pas).</summary>
    public string Advice { get; }
    public Status AdviceStatus { get; }

    /// <summary>L'icône du programme lancé, lue dans son fichier (null si on ne la trouve pas).</summary>
    public System.Windows.Media.ImageSource? Icon { get; }

    // Où se trouve l'interrupteur « activé / désactivé » de cette entrée (le même que celui du Gestionnaire des tâches).
    internal bool IsUserScope { get; }
    internal string ApprovedSubKey { get; }
    internal string ApprovedValueName { get; }

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    /// <summary>Appelé quand l'utilisateur bascule l'interrupteur.</summary>
    internal Action<StartupEntry, bool>? Changed { get; set; }

    partial void OnIsEnabledChanged(bool value) => Changed?.Invoke(this, value);

    internal StartupEntry(string name, string command, string source,
        bool isUserScope, string approvedSubKey, string approvedValueName, bool isEnabled)
    {
        Name = name;
        Command = command;
        Source = source;
        IsUserScope = isUserScope;
        ApprovedSubKey = approvedSubKey;
        ApprovedValueName = approvedValueName;
        IsEnabled = isEnabled;
        (Advice, AdviceStatus) = StartupService.Advise(name, command);
        Icon = IconService.ForCommand(command);
    }
}

/// <summary>
/// Liste les programmes de démarrage et les active/désactive exactement comme le Gestionnaire des tâches :
/// on écrit dans les clés « StartupApproved », sans jamais supprimer l'entrée d'origine.
/// </summary>
public static class StartupService
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    // Programmes connus qu'on peut désactiver sans rien casser : ils se lancent très bien à la main.
    private static readonly string[] SafeToDisable =
    [
        "discord", "steam", "epicgames", "epic games", "spotify", "teams", "skype", "riotclient", "riot client",
        "battle.net", "ubisoft", "uplay", "ea app", "eadesktop", "origin.exe", "gog galaxy", "adobe", "acrobat", "creative cloud",
        "ccleaner", "utorrent", "qbittorrent", "zoom", "slack", "whatsapp", "telegram", "jusched", "itunes", "icloud",
        "curseforge", "overwolf", "lunar client", "opera.exe", "msedge", "chrome", "brave", "wargaming", "rockstar",
        "bongocat", "mumu", "bluestacks", "ankama", "minecraft", "twitch", "vlc", "deezer",
    ];

    // Programmes qu'il vaut mieux laisser : sécurité, son, pavé tactile, carte graphique.
    private static readonly string[] BetterKeep =
    [
        "securityhealth", "windows security", "defender", "rtkaud", "realtek", "synaptics", "elantech", "etdctrl", "touchpad",
        "igfx", "nvidia", "radeon", "amd ", "bitdefender", "kaspersky", "avast", "avgui", "norton", "\\eset\\", "malwarebytes",
    ];

    internal static (string Advice, Status Status) Advise(string name, string command)
    {
        string text = $"{name} {command}".ToLowerInvariant();
        if (BetterKeep.Any(text.Contains)) return ("À garder", Status.Ok);
        if (text.Contains("vanguard")) return ("À garder si tu joues à Valorant", Status.None);
        if (SafeToDisable.Any(text.Contains)) return ("Peut être désactivé", Status.Warning);
        return ("", Status.None);
    }

    public static List<StartupEntry> Load()
    {
        var entries = new List<StartupEntry>();

        using (var user = UserContext.OpenUserRegistry())
            AddRunKey(entries, user, isUserScope: true, "Run", "Ton compte");
        using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            AddRunKey(entries, machine, isUserScope: false, "Run", "Tous les utilisateurs");
        using (var machine32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            AddRunKey(entries, machine32, isUserScope: false, "Run32", "Tous les utilisateurs (32 bits)");

        AddFolder(entries, UserContext.StartupFolder, isUserScope: true, "Dossier Démarrage");
        AddFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), isUserScope: false, "Dossier Démarrage (tous)");

        return [.. entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static void AddRunKey(List<StartupEntry> entries, RegistryKey root, bool isUserScope, string approvedName, string source)
    {
        try
        {
            using var run = root.OpenSubKey(RunPath);
            if (run is null) return;

            foreach (string name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                string command = run.GetValue(name)?.ToString() ?? "";
                string subKey = $@"{ApprovedPath}\{approvedName}";
                entries.Add(new StartupEntry(name, command, source, isUserScope, subKey, name, ReadEnabled(isUserScope, subKey, name)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Logger.Error($"Lecture de la clé Run ({source})", ex);
        }
    }

    private static void AddFolder(List<StartupEntry> entries, string folder, bool isUserScope, string source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        foreach (string file in Directory.GetFiles(folder))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            string subKey = $@"{ApprovedPath}\StartupFolder";
            entries.Add(new StartupEntry(
                Path.GetFileNameWithoutExtension(file), file, source, isUserScope, subKey, fileName, ReadEnabled(isUserScope, subKey, fileName)));
        }
    }

    /// <summary>Les clés StartupApproved sont toujours dans la vue 64 bits du registre.</summary>
    private static RegistryKey OpenRoot(bool isUserScope) => isUserScope
        ? UserContext.OpenUserRegistry()
        : RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    /// <summary>
    /// La valeur StartupApproved fait 12 octets. Premier octet pair (02, 06) = activé, impair (03) = désactivé.
    /// Pas de valeur du tout = activé.
    /// </summary>
    private static bool ReadEnabled(bool isUserScope, string subKey, string valueName)
    {
        try
        {
            using var root = OpenRoot(isUserScope);
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(valueName) is not byte[] { Length: > 0 } data || (data[0] & 1) == 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Active ou désactive l'entrée. Lève une exception si Windows refuse (droits insuffisants).</summary>
    public static void SetEnabled(StartupEntry entry, bool enabled)
    {
        var data = new byte[12];
        if (enabled)
        {
            data[0] = 0x02;
        }
        else
        {
            data[0] = 0x03;
            // Octets 4 à 11 : date de désactivation (format FILETIME), comme le fait le Gestionnaire des tâches.
            BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
        }

        using var root = OpenRoot(entry.IsUserScope);
        using var key = root.CreateSubKey(entry.ApprovedSubKey, writable: true);
        key.SetValue(entry.ApprovedValueName, data, RegistryValueKind.Binary);

        Logger.Info($"Démarrage : « {entry.Name} » {(enabled ? "activé" : "désactivé")}");
    }
}
