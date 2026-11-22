using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace MaintenancePC.Services;

/// <summary>Un programme lancé au démarrage de Windows.</summary>
public sealed partial class StartupEntry : ObservableObject
{
    public string Name { get; }
    public string Command { get; }
    public string Source { get; }

    // Où se trouve l'interrupteur « activé / désactivé » de cette entrée (le même que celui du Gestionnaire des tâches).
    internal RegistryHive ApprovedHive { get; }
    internal string ApprovedSubKey { get; }
    internal string ApprovedValueName { get; }

    [ObservableProperty] public partial bool IsEnabled { get; set; }

    /// <summary>Appelé quand l'utilisateur bascule l'interrupteur.</summary>
    internal Action<StartupEntry, bool>? Changed { get; set; }

    partial void OnIsEnabledChanged(bool value) => Changed?.Invoke(this, value);

    internal StartupEntry(string name, string command, string source,
        RegistryHive approvedHive, string approvedSubKey, string approvedValueName, bool isEnabled)
    {
        Name = name;
        Command = command;
        Source = source;
        ApprovedHive = approvedHive;
        ApprovedSubKey = approvedSubKey;
        ApprovedValueName = approvedValueName;
        IsEnabled = isEnabled;
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

    public static List<StartupEntry> Load()
    {
        var entries = new List<StartupEntry>();

        AddRunKey(entries, RegistryHive.CurrentUser, RegistryView.Registry64, "Run", "Ton compte");
        AddRunKey(entries, RegistryHive.LocalMachine, RegistryView.Registry64, "Run", "Tous les utilisateurs");
        AddRunKey(entries, RegistryHive.LocalMachine, RegistryView.Registry32, "Run32", "Tous les utilisateurs (32 bits)");

        AddFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Dossier Démarrage");
        AddFolder(entries, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "Dossier Démarrage (tous)");

        return [.. entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static void AddRunKey(List<StartupEntry> entries, RegistryHive hive, RegistryView view, string approvedName, string source)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var run = root.OpenSubKey(RunPath);
            if (run is null) return;

            foreach (string name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name)) continue;
                string command = run.GetValue(name)?.ToString() ?? "";
                string subKey = $@"{ApprovedPath}\{approvedName}";
                entries.Add(new StartupEntry(name, command, source, hive, subKey, name, ReadEnabled(hive, subKey, name)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Logger.Error($"Lecture de la clé Run ({source})", ex);
        }
    }

    private static void AddFolder(List<StartupEntry> entries, string folder, RegistryHive hive, string source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

        foreach (string file in Directory.GetFiles(folder))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

            string subKey = $@"{ApprovedPath}\StartupFolder";
            entries.Add(new StartupEntry(
                Path.GetFileNameWithoutExtension(file), file, source, hive, subKey, fileName, ReadEnabled(hive, subKey, fileName)));
        }
    }

    /// <summary>
    /// La valeur StartupApproved fait 12 octets. Premier octet pair (02, 06) = activé, impair (03) = désactivé.
    /// Pas de valeur du tout = activé.
    /// </summary>
    private static bool ReadEnabled(RegistryHive hive, string subKey, string valueName)
    {
        try
        {
            // Les clés StartupApproved sont toujours dans la vue 64 bits du registre.
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
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

        using var root = RegistryKey.OpenBaseKey(entry.ApprovedHive, RegistryView.Registry64);
        using var key = root.CreateSubKey(entry.ApprovedSubKey, writable: true);
        key.SetValue(entry.ApprovedValueName, data, RegistryValueKind.Binary);

        Logger.Info($"Démarrage : « {entry.Name} » {(enabled ? "activé" : "désactivé")}");
    }
}
