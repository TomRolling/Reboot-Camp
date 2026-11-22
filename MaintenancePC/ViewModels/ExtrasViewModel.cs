using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaintenancePC.Services;

namespace MaintenancePC.ViewModels;

public sealed partial class ExtrasViewModel : PageViewModel
{
    private const int StartupTab = 0, SystemTab = 1, DisksTab = 2;

    [GeneratedRegex(@"(\d{1,3})(?:[.,]\d+)?\s*%")]
    private static partial Regex PercentRegex();

    private readonly HashSet<int> _loadedTabs = [];
    private bool _isLoading;

    public IReadOnlyList<SectionTab> Tabs { get; } =
        [new("Démarrage"), new("Système"), new("Disques"), new("Réparation")];

    public ObservableCollection<StartupEntry> StartupEntries { get; } = [];
    public ObservableCollection<InfoLine> InfoLines { get; } = [];
    public ObservableCollection<VolumeInfo> Volumes { get; } = [];
    public ObservableCollection<DiskHealth> Disks { get; } = [];
    public LogViewModel RepairLog { get; } = new();

    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial string StartupMessage { get; set; }
    [ObservableProperty] public partial string SmartSummary { get; set; }
    [ObservableProperty] public partial string RepairStatus { get; set; }

    public ExtrasViewModel() : base(
        "Extras", "", "Extras", "Démarrage, infos système, santé des disques et outils de réparation.")
    {
        StartupMessage = "";
        SmartSummary = "";
        RepairStatus = "Choisis un outil. La sortie s'affiche ici en direct.";
    }

    // Ces listes sont de simples lectures (registre, WMI) : on les charge quand l'utilisateur ouvre la section.
    public override void OnNavigatedTo() => _ = LoadTabAsync(SelectedTab, force: false);

    partial void OnSelectedTabChanged(int value) => _ = LoadTabAsync(value, force: false);

    [RelayCommand]
    private Task RefreshAsync() => LoadTabAsync(SelectedTab, force: true);

    private async Task LoadTabAsync(int tab, bool force)
    {
        if (_isLoading || (!force && _loadedTabs.Contains(tab))) return;
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
                    StartupMessage = entries.Count == 0 ? "Aucun programme au démarrage." : "";
                    break;

                case SystemTab:
                    var (lines, volumes) = await Task.Run(() => (SystemInfoService.GetSystemInfo(), SystemInfoService.GetVolumes()));
                    Replace(InfoLines, lines);
                    Replace(Volumes, volumes);
                    break;

                case DisksTab:
                    var (disks, smart) = await Task.Run(SystemInfoService.GetDisks);
                    Replace(Disks, disks);
                    SmartSummary = disks.Count == 0 ? "Impossible de lire l'état des disques." : smart;
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

    // ───────────────────────── Réparation ─────────────────────────

    [RelayCommand]
    private Task SfcAsync() => RunToolAsync(
        "Vérification des fichiers système (SFC)", "sfc.exe", "/scannow", Encoding.Unicode);   // sfc écrit en UTF-16

    [RelayCommand]
    private Task DismAsync() => RunToolAsync(
        "Réparation de l'image Windows (DISM)", "dism.exe", "/Online /Cleanup-Image /RestoreHealth", ProcessRunner.OemEncoding);

    private async Task RunToolAsync(string title, string tool, string arguments, Encoding encoding)
    {
        if (IsBusy) return;
        var token = BeginOperation("Extras", title);
        RepairLog.Clear();
        RepairLog.Add($"> {tool} {arguments}");
        RepairStatus = $"{title} en cours… Cela peut durer plusieurs minutes.";
        Logger.Info($"Lancement : {tool} {arguments}");

        var output = new Progress<string>(line =>
        {
            RepairLog.Add(line);
            var match = PercentRegex().Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out double percent))
                Progress = Math.Min(percent, 100);
        });

        try
        {
            int code = await ProcessRunner.RunAsync(ProcessRunner.SystemTool(tool), arguments, output, token, encoding);
            RepairStatus = code == 0
                ? $"{title} : terminé. Lis le résultat ci-dessous."
                : $"{title} : terminé avec le code {code}. Lis le détail ci-dessous.";
            Logger.Info($"{tool} terminé, code {code}");
        }
        catch (OperationCanceledException)
        {
            RepairStatus = $"{title} : annulé.";
        }
        catch (Win32Exception ex)
        {
            RepairStatus = $"Impossible de lancer {tool} : {ex.Message}";
            Logger.Error($"Lancement de {tool}", ex);
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
                "MaintenancePC - point manuel", RestorePointService.ModifySettings, token);
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
}
