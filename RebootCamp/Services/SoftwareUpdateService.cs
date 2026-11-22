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

    /// <summary>La version proposée par winget.</summary>
    public string Available { get; }

    /// <summary>Le nom tel que Windows l'enregistre : sert à retrouver l'icône du programme installé.</summary>
    public string InstalledName { get; }

    [ObservableProperty] public partial System.Windows.Media.ImageSource? Icon { get; set; }

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string Detail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    public partial Status Status { get; set; }

    /// <summary>Vrai si winget ne peut pas faire cette mise à jour sur ce PC : la ligne est mise à part, sans case à cocher.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    public partial bool IsBlocked { get; set; }

    /// <summary>Texte du bouton proposé sur une ligne bloquée (« Réessayer », « Désinstaller puis réinstaller »).</summary>
    [ObservableProperty] public partial string BlockedAction { get; set; }

    /// <summary>Vrai si « désinstaller puis réinstaller » peut débloquer cette mise à jour.</summary>
    public bool CanReinstall { get; private set; }

    public bool CanSelect => Status != Status.Ok && !IsBlocked;

    /// <summary>Met la ligne à part : orange, décochée, avec l'explication et le bouton qui convient.</summary>
    public void MarkBlocked(string reason, bool canReinstall)
    {
        CanReinstall = canReinstall;
        BlockedAction = canReinstall ? "Désinstaller puis réinstaller" : "Réessayer";
        IsSelected = false;
        Status = Status.Warning;
        Detail = reason;
        IsBlocked = true;
    }

    internal SoftwareUpdate(string name, string id, string current, string available)
    {
        // Certains éditeurs nomment la fiche de leur programme « Uninstall Truc » : on affiche « Truc ».
        Name = name.StartsWith("Uninstall ", StringComparison.OrdinalIgnoreCase) && name.Length > 12 ? name[10..].Trim() : name;
        Id = id;
        Available = available;
        InstalledName = name;
        BlockedAction = "";
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

    /// <summary>Résultat d'une tentative de mise à jour.</summary>
    /// <param name="Blocked">
    /// Vrai quand winget ne pourra pas faire cette mise à jour sur ce PC, même en réessayant : le message explique pourquoi.
    /// </param>
    /// <param name="CanReinstall">Vrai si « désinstaller puis réinstaller » peut la débloquer.</param>
    public sealed record UpgradeResult(bool Ok, string Message, bool Blocked = false, bool CanReinstall = false);

    public static async Task<UpgradeResult> UpgradeAsync(SoftwareUpdate update, IProgress<string> output, CancellationToken token)
    {
        var (outcome, code) = await Winget.RunAsync("upgrade", update.Id, update.Name, output, token, "--include-unknown");
        return Describe(outcome, code, "Mis à jour", "la mise à jour");
    }

    /// <summary>
    /// Pour une mise à jour que winget refuse parce que la nouvelle version s'installe autrement : désinstalle
    /// l'ancienne, puis installe la nouvelle. Si la désinstallation échoue, rien n'a changé sur le PC.
    /// </summary>
    public static async Task<UpgradeResult> ReinstallAsync(SoftwareUpdate update, IProgress<string> output, CancellationToken token)
    {
        var (removed, removeCode) = await Winget.UninstallAsync(update.Id, output, token);
        if (!removed)
            return new UpgradeResult(false,
                $"L'ancienne version n'a pas pu être désinstallée (code 0x{removeCode:X8}) : rien n'a changé.", Blocked: true, CanReinstall: true);

        var (outcome, code) = await Winget.RunAsync("install", update.Id, update.Name, output, token);
        var result = Describe(outcome, code, "Réinstallé dans sa nouvelle version", "l'installation");
        return result.Ok ? result
            : new UpgradeResult(false, "L'ancienne version est désinstallée, mais la nouvelle n'a pas pu s'installer. " + result.Message);
    }

    private static UpgradeResult Describe(WingetOutcome outcome, int code, string done, string action) => outcome switch
    {
        WingetOutcome.Done => new(true, done),
        WingetOutcome.AlreadyUpToDate => new(true, "Déjà à jour"),
        WingetOutcome.DoneAfterRestart => new(true, $"{done}, actif après un redémarrage"),
        WingetOutcome.RestartThenRetry => new(false, $"Redémarre le PC, puis relance {action}."),
        _ => new(false,
            Winget.Explain(code) ?? $"Échec (code 0x{code:X8}). Ferme le logiciel s'il est ouvert, puis réessaie.",
            Winget.IsBlocked(code), Winget.CanReinstall(code)),
    };
}
