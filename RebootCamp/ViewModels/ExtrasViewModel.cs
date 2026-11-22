using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

public sealed partial class ExtrasViewModel : PageViewModel
{
    private const int StartupTab = 0, SystemTab = 1, DisksTab = 2, SecurityTab = 3;

    [GeneratedRegex(@"(\d{1,3})(?:[.,]\d+)?\s*%")]
    private static partial Regex PercentRegex();

    private readonly WindowsUpdateService _windowsUpdate = new();
    private readonly Func<IEnumerable<string>> _scanSummary;
    private readonly HashSet<int> _loadedTabs = [];
    private bool _isLoading;

    public IReadOnlyList<SectionTab> Tabs { get; } =
        [new("Démarrage"), new("Système"), new("Disques"), new("Sécurité"), new("Espace disque"), new("Réparation")];

    public ObservableCollection<StartupEntry> StartupEntries { get; } = [];
    public ObservableCollection<InfoLine> InfoLines { get; } = [];
    public ObservableCollection<VolumeInfo> Volumes { get; } = [];
    public ObservableCollection<DiskHealth> Disks { get; } = [];
    public ObservableCollection<SecurityLine> SecurityLines { get; } = [];
    public ObservableCollection<FolderSize> Folders { get; } = [];
    public ObservableCollection<FolderSize> UserFolders { get; } = [];
    public ObservableCollection<FolderSize> BigFiles { get; } = [];
    public LogViewModel RepairLog { get; } = new();

    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial string StartupMessage { get; set; }
    [ObservableProperty] public partial string SmartSummary { get; set; }
    [ObservableProperty] public partial string RepairStatus { get; set; }
    [ObservableProperty] public partial string SpaceStatus { get; set; }
    [ObservableProperty] public partial bool HasSpaceResult { get; set; }
    [ObservableProperty] public partial string ReportMessage { get; set; }

    /// <param name="scanSummary">Fournit les résultats des autres onglets pour le rapport.</param>
    public ExtrasViewModel(Func<IEnumerable<string>> scanSummary) : base(
        "Extras", "", "Extras", "Démarrage, infos système, santé des disques, sécurité et outils de réparation.")
    {
        _scanSummary = scanSummary;
        StartupMessage = "";
        SmartSummary = "";
        ReportMessage = "";
        RepairStatus = "Choisis un outil. La sortie s'affiche ici en direct.";
        SpaceStatus = "Mesure la taille des dossiers du disque système. Rien n'est modifié. Peut durer quelques minutes.";
    }

    // Ces listes sont de simples lectures (registre, WMI) : on les charge quand l'utilisateur ouvre la section.
    public override void OnNavigatedTo() => _ = LoadTabAsync(SelectedTab, force: false);

    partial void OnSelectedTabChanged(int value) => _ = LoadTabAsync(value, force: false);

    [RelayCommand]
    private Task RefreshAsync() => LoadTabAsync(SelectedTab, force: true);

    private async Task LoadTabAsync(int tab, bool force)
    {
        if (tab > SecurityTab || _isLoading || (!force && _loadedTabs.Contains(tab))) return;
        _isLoading = true;
        try
        {
            switch (tab)
            {
                case StartupTab:
                    var entries = await Task.Run(StartupService.Load);
                    StartupEntries.Clear();
                    foreach (var entry in entries)
                    {
                        entry.Changed = OnStartupEntryChanged;
                        StartupEntries.Add(entry);
                    }
                    Tabs[StartupTab].Badge = entries.Count.ToString();
                    StartupMessage = entries.Count == 0
                        ? "Aucun programme au démarrage."
                        : "Désactiver un programme ne le supprime pas : il ne se lance simplement plus tout seul.";
                    break;

                case SystemTab:
                    var (lines, volumes) = await Task.Run(() => (GetSystemLines(), SystemInfoService.GetVolumes()));
                    Replace(InfoLines, lines);
                    Replace(Volumes, volumes);
                    break;

                case DisksTab:
                    var (disks, smart) = await Task.Run(SystemInfoService.GetDisks);
                    Replace(Disks, disks);
                    SmartSummary = disks.Count == 0 ? "Impossible de lire l'état des disques." : smart;
                    break;

                case SecurityTab:
                    Replace(SecurityLines, await Task.Run(SecurityService.GetStatus));
                    SecurityLines.Add(new("Windows Update", "Recherche en cours…", Status.Working));
                    _ = LoadWindowsUpdateLineAsync();
                    break;
            }
            _loadedTabs.Add(tab);
        }
        catch (Exception ex)
        {
            Logger.Error($"Chargement de la section Extras n°{tab}", ex);
        }
        finally
        {
            _isLoading = false;
        }

        // Si l'utilisateur a changé de section pendant le chargement, on charge celle qu'il regarde maintenant.
        if (tab != SelectedTab) await LoadTabAsync(SelectedTab, force: false);
    }

    private static List<InfoLine> GetSystemLines()
    {
        var lines = SystemInfoService.GetSystemInfo();
        lines.Add(new InfoLine("Batterie", SecurityService.GetBatteryHealth() ?? "Pas de batterie (PC fixe) ou information indisponible"));
        return lines;
    }

    /// <summary>La recherche Windows Update peut durer une minute : elle remplit sa ligne quand elle a fini.</summary>
    private async Task LoadWindowsUpdateLineAsync()
    {
        SecurityLine line;
        try
        {
            var (pending, reboot) = await _windowsUpdate.GetPendingAsync(AppLifetime.Token);
            line = reboot
                ? new("Windows Update", "Un redémarrage est nécessaire pour finir d'installer des mises à jour.", Status.Warning)
                : pending == 0
                    ? new("Windows Update", "Windows est à jour", Status.Ok)
                    : new("Windows Update", (pending == 1 ? "1 mise à jour en attente" : $"{pending} mises à jour en attente") + ". Clique sur « Ouvrir Windows Update ».", Status.Warning);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Logger.Error("État de Windows Update", ex);
            line = new("Windows Update", "État indisponible", Status.None);
        }

        var placeholder = SecurityLines.FirstOrDefault(l => l.Label == "Windows Update");
        if (placeholder is not null) SecurityLines[SecurityLines.IndexOf(placeholder)] = line;
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    private void OnStartupEntryChanged(StartupEntry entry, bool enabled)
    {
        try
        {
            StartupService.SetEnabled(entry, enabled);
            StartupMessage = $"« {entry.Name} » {(enabled ? "sera lancé" : "ne sera plus lancé")} au démarrage.";
        }
        catch (Exception ex)
        {
            Logger.Error($"Modification du démarrage de « {entry.Name} »", ex);
            StartupMessage = $"Impossible de modifier « {entry.Name} » : {ex.Message.Trim()}";

            // On remet l'interrupteur dans son état réel, sans redéclencher ce code.
            entry.Changed = null;
            entry.IsEnabled = !enabled;
            entry.Changed = OnStartupEntryChanged;
        }
    }

    [RelayCommand]
    private static void OpenWindowsUpdate() => Open("ms-settings:windowsupdate");

    [RelayCommand]
    private static void OpenWindowsSecurity() => Open("windowsdefender:");

    private static void Open(string target, string? arguments = null)
    {
        try
        {
            var info = arguments is null
                ? new ProcessStartInfo(target) { UseShellExecute = true }
                : new ProcessStartInfo(target, arguments) { UseShellExecute = false };
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error($"Ouverture de {target}", ex);
        }
    }

    // ───────────────────────── Espace disque ─────────────────────────

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ScanSpaceAsync()
    {
        if (IsBusy)
        {
            Cancel();
            return;
        }

        var token = BeginOperation("Extras", "Mesure de l'espace disque…");
        SpaceStatus = "Mesure en cours…";
        var progress = new Progress<string>(text => SpaceStatus = text);
        try
        {
            var result = await Task.Run(() => DiskUsageService.Scan(progress, token), token);
            Replace(Folders, result.Folders);
            Replace(UserFolders, result.UserFolders);
            Replace(BigFiles, result.BigFiles);
            HasSpaceResult = true;
            await Task.Yield();
            SpaceStatus = "Voici ce qui prend le plus de place. Clique sur une ligne pour l'ouvrir dans l'Explorateur.";
        }
        catch (OperationCanceledException)
        {
            SpaceStatus = "Mesure annulée.";
        }
        catch (Exception ex)
        {
            SpaceStatus = "La mesure a échoué : " + ex.Message.Trim();
            Logger.Error("Mesure de l'espace disque", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private static void OpenInExplorer(FolderSize item)
    {
        if (Directory.Exists(item.Path)) Open("explorer.exe", $"\"{item.Path}\"");
        else if (File.Exists(item.Path)) Open("explorer.exe", $"/select,\"{item.Path}\"");
    }

    // ───────────────────────── Réparation ─────────────────────────

    [RelayCommand]
    private Task SfcAsync() => RunToolsAsync("Vérification des fichiers système (SFC)", null,
        ("sfc.exe", "/scannow", Encoding.Unicode));   // sfc écrit en UTF-16

    [RelayCommand]
    private Task DismAsync() => RunToolsAsync("Réparation de l'image Windows (DISM)", null,
        ("dism.exe", "/Online /Cleanup-Image /RestoreHealth", ProcessRunner.OemEncoding));

    [RelayCommand]
    private Task NetworkAsync() => RunToolsAsync("Réparation de la connexion Internet",
        "La réparation de la connexion Internet ne prend effet qu'après un redémarrage du PC.",
        ("ipconfig.exe", "/flushdns", ProcessRunner.OemEncoding),
        ("netsh.exe", "winsock reset", ProcessRunner.OemEncoding));

    /// <param name="restartReason">Si l'outil n'agit qu'après un redémarrage : la phrase affichée pour le proposer.</param>
    private async Task RunToolsAsync(string title, string? restartReason, params (string Tool, string Arguments, Encoding Encoding)[] steps)
    {
        if (IsBusy) return;
        var token = BeginOperation("Extras", title);
        RepairLog.Clear();
        RepairStatus = $"{title} en cours… Cela peut durer plusieurs minutes.";

        var output = new Progress<string>(line =>
        {
            RepairLog.Add(line);
            var match = PercentRegex().Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out double percent))
                Progress = Math.Min(percent, 100);
        });

        try
        {
            int worst = 0;
            foreach (var (tool, arguments, encoding) in steps)
            {
                RepairLog.Add($"> {tool} {arguments}");
                Logger.Info($"Lancement : {tool} {arguments}");
                int code = await ProcessRunner.RunAsync(ProcessRunner.SystemTool(tool), arguments, output, token, encoding);
                Logger.Info($"{tool} terminé, code {code}");
                if (code != 0) worst = code;
            }
            await Task.Yield();
            RepairStatus = worst == 0
                ? $"{title} : terminé. Lis le résultat ci-dessous."
                : $"{title} : terminé avec le code {worst}. Lis le détail ci-dessous.";
            if (worst == 0 && restartReason is not null) RestartService.Need(restartReason);
        }
        catch (OperationCanceledException)
        {
            RepairStatus = $"{title} : annulé.";
        }
        catch (Win32Exception ex)
        {
            RepairStatus = $"Impossible de lancer l'outil : {ex.Message}";
            Logger.Error(title, ex);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task RestorePointAsync()
    {
        if (IsBusy) return;
        var token = BeginOperation("Extras", "Création d'un point de restauration…");
        RepairStatus = "Création d'un point de restauration… Cela peut durer une minute.";
        try
        {
            var (_, message) = await RestorePointService.CreateAsync(
                $"{AppInfo.Name} - point manuel", RestorePointService.ModifySettings, token);
            RepairStatus = message;
        }
        catch (OperationCanceledException)
        {
            RepairStatus = "Création du point de restauration annulée.";
        }
        finally
        {
            EndOperation();
        }
    }

    // ───────────────────────── Journal ─────────────────────────

    /// <summary>Ouvre dans l'Explorateur le dossier du journal (créé s'il n'existe pas encore).</summary>
    [RelayCommand]
    private void OpenLog()
    {
        try
        {
            Directory.CreateDirectory(Logger.Folder);
            Open("explorer.exe", $"\"{Logger.Folder}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportMessage = "Impossible d'ouvrir le dossier du journal : " + ex.Message;
        }
    }

    // ───────────────────────── Rapport ─────────────────────────

    /// <summary>Écrit un fichier texte résumant le PC : pratique à envoyer à quelqu'un qui dépanne à distance.</summary>
    [RelayCommand]
    private async Task ExportReportAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Enregistrer le rapport",
            FileName = $"Rapport-PC-{DateTime.Now:yyyy-MM-dd}.txt",
            Filter = "Fichier texte (*.txt)|*.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            ReportMessage = "Création du rapport…";
            var scanLines = _scanSummary().ToList();
            string report = await Task.Run(() => BuildReport(scanLines));
            await File.WriteAllTextAsync(dialog.FileName, report, Encoding.UTF8);
            ReportMessage = $"Rapport enregistré : {dialog.FileName}";
            Logger.Info($"Rapport exporté : {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ReportMessage = "Impossible d'enregistrer le rapport : " + ex.Message;
            Logger.Error("Export du rapport", ex);
        }
    }

    private static string BuildReport(List<string> scanLines)
    {
        var text = new StringBuilder();
        void Section(string title)
        {
            text.AppendLine();
            text.AppendLine($"== {title} ==");
        }

        text.AppendLine($"Rapport {AppInfo.Name} du {DateTime.Now:dd/MM/yyyy à HH:mm}");

        Section("Ordinateur");
        foreach (var line in GetSystemLines()) text.AppendLine($"{line.Label} : {line.Value}");

        Section("Espace disque");
        foreach (var volume in SystemInfoService.GetVolumes()) text.AppendLine($"{volume.Name} : {volume.Text}");

        Section("Santé des disques");
        var (disks, smart) = SystemInfoService.GetDisks();
        foreach (var disk in disks) text.AppendLine($"{disk.Name} ({disk.Kind}) : {disk.HealthText} {disk.Details}".TrimEnd());
        text.AppendLine(smart);

        Section("Sécurité");
        foreach (var line in SecurityService.GetStatus()) text.AppendLine($"{line.Label} : {line.Value}");

        Section("Résultats des analyses");
        if (scanLines.Count == 0) text.AppendLine("Aucune analyse lancée avant l'export.");
        foreach (string line in scanLines) text.AppendLine(line);

        Section("Programmes au démarrage");
        foreach (var entry in StartupService.Load())
            text.AppendLine($"[{(entry.IsEnabled ? "activé" : "désactivé")}] {entry.Name} - {entry.Command}");

        return text.ToString();
    }
}
