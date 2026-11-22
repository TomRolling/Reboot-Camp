using System.ComponentModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using RebootCamp.Models;

namespace RebootCamp.Services;

/// <summary>Un logiciel installé pour lequel winget connaît une version plus récente.</summary>
public sealed partial class SoftwareUpdate : ObservableObject
{
    public string Name { get; }
    public string Id { get; }
    public string Versions { get; }

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial Status Status { get; set; }
    [ObservableProperty] public partial string Detail { get; set; }

    internal SoftwareUpdate(string name, string id, string current, string available)
    {
        Name = name;
        Id = id;
        bool unknown = current.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
        Versions = unknown ? $"version actuelle inconnue  →  {available}" : $"{current}  →  {available}";
        Detail = Versions;
        // Si winget ne connaît pas la version installée, il ne sait pas vraiment s'il y a une mise à jour : décoché.
        IsSelected = !unknown;
    }
}

/// <summary>Mises à jour des logiciels installés, via « winget upgrade ».</summary>
public static partial class SoftwareUpdateService
{
    private const int WingetNoUpgrade = unchecked((int)0x8A15002B);

    [GeneratedRegex(@"\S+")]
    private static partial Regex WordRegex();

    /// <returns>La liste des mises à jour, ou null si winget est absent.</returns>
    public static async Task<List<SoftwareUpdate>?> ListAsync(CancellationToken token)
    {
        var lines = new List<string>();
        try
        {
            await ProcessRunner.RunAsync("winget",
                "upgrade --source winget --accept-source-agreements --disable-interactivity",
                new DirectProgress<string>(line => { lock (lines) lines.Add(line); }), token);
        }
        catch (Win32Exception)
        {
            return null;
        }
        return Parse(lines);
    }

    /// <summary>
    /// winget affiche un tableau à colonnes alignées. On repère le début de chaque colonne dans la ligne de titres
    /// (juste au-dessus de la ligne de tirets), puis on découpe chaque ligne à ces positions.
    /// </summary>
    internal static List<SoftwareUpdate> Parse(List<string> lines)
    {
        var updates = new List<SoftwareUpdate>();

        int separator = lines.FindIndex(l => l.Length > 20 && l.Trim().All(c => c == '-'));
        if (separator < 1) return updates;

        var columns = WordRegex().Matches(lines[separator - 1]).Select(m => m.Index).ToList();
        if (columns.Count < 4) return updates;
        int idStart = columns[1], versionStart = columns[2], availableStart = columns[3];
        int end = columns.Count > 4 ? columns[4] : int.MaxValue;

        for (int i = separator + 1; i < lines.Count; i++)
        {
            string line = lines[i];
            // Le premier tableau s'arrête à la ligne de résumé (« 29 upgrades available. »).
            if (line.Length <= availableStart || line[idStart - 1] != ' ') break;

            string name = line[..idStart].Trim();
            string id = line[idStart..versionStart].Trim();
            string current = line[versionStart..availableStart].Trim();
            string available = line[availableStart..Math.Min(end, line.Length)].Trim();

            if (id.Length == 0 || id.Contains(' ') || available.Length == 0) continue;
            // winget ne peut pas se mettre à jour lui-même pendant qu'il tourne (c'est le Microsoft Store qui s'en charge).
            if (id.Equals("Microsoft.AppInstaller", StringComparison.OrdinalIgnoreCase)) continue;

            updates.Add(new SoftwareUpdate(name, id, current, available));
        }
        return updates;
    }

    public static async Task<(bool Ok, string Message)> UpgradeAsync(SoftwareUpdate update, IProgress<string> output, CancellationToken token)
    {
        string arguments =
            $"upgrade --id {update.Id} --exact --source winget --silent --include-unknown " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity";

        output.Report($"> winget {arguments}");
        Logger.Info($"winget upgrade {update.Id}");

        int code = await ProcessRunner.RunAsync("winget", arguments, output, token);

        Logger.Info($"winget upgrade {update.Id} : code 0x{code:X8}");
        return code switch
        {
            0 => (true, "Mis à jour"),
            WingetNoUpgrade => (true, "Déjà à jour"),
            _ => (false, $"Échec (code 0x{code:X8}). Ferme le logiciel s'il est ouvert, puis réessaie."),
        };
    }
}
