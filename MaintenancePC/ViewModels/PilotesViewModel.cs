using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaintenancePC.Models;
using MaintenancePC.Services;

namespace MaintenancePC.ViewModels;

/// <summary>Un sous-onglet avec son compteur (« Mises à jour 2 »).</summary>
public sealed partial class SectionTab(string title) : ObservableObject
{
    public string Title { get; } = title;
    [ObservableProperty] public partial string Badge { get; set; } = "";
}

public sealed partial class PilotesViewModel : PageViewModel
{
    // Adresses vérifiées (pages officielles des fabricants).
    private const string NvidiaUrl = "https://www.nvidia.com/fr-fr/drivers/";
    private const string AsusUrl = "https://www.asus.com/fr/support/";

    private readonly WindowsUpdateService _windowsUpdate = new();
    private List<DriverInfo> _allDrivers = [];

    public ObservableCollection<DriverUpdate> Updates { get; } = [];
    public ObservableCollection<DeviceProblem> Problems { get; } = [];
    public ObservableCollection<DriverInfo> Drivers { get; } = [];
    public IReadOnlyList<SectionTab> Tabs { get; } =
        [new("Mises à jour"), new("Problèmes"), new("Tous les pilotes")];

    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial bool HasScanned { get; set; }
    [ObservableProperty] public partial bool CreateRestorePoint { get; set; }
    [ObservableProperty] public partial string FilterText { get; set; }
    [ObservableProperty] public partial string UpdatesMessage { get; set; }

    /// <summary>Nombre de vraies erreurs (les périphériques simplement désactivés ne comptent pas).</summary>
    public int ErrorCount => Problems.Count(p => !p.IsDisabledOnly);

    public PilotesViewModel() : base(
        "Pilotes", "", "Prêt à analyser",
        "Liste les pilotes, repère les périphériques en erreur et cherche les mises à jour sur Windows Update.")
    {
        CreateRestorePoint = true;
        FilterText = "";
        UpdatesMessage = "";
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        Drivers.Clear();
        string filter = FilterText.Trim();
        foreach (var driver in _allDrivers)
        {
            if (filter.Length == 0
                || driver.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || driver.Manufacturer.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            {
                Drivers.Add(driver);
            }
        }
    }

    /// <summary>Le gros bouton rond : lance l'analyse, ou annule l'opération en cours.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RingAsync()
    {
        if (IsBusy) Cancel();
        else await RunScanAsync();
    }

    public async Task RunScanAsync(CancellationToken external = default)
    {
        if (IsBusy) return;
        var token = BeginOperation("Analyse en cours…", "Lecture des pilotes installés…", external);
        try
        {
            Report(5, "Lecture des pilotes installés…");
            _allDrivers = await Task.Run(() => DriverService.ListDrivers(token), token);

            Report(40, "Recherche des périphériques en erreur…");
            var problems = await Task.Run(() => DriverService.ListProblems(token), token);

            Report(60, "Recherche de mises à jour sur Windows Update…");
            List<DriverUpdate> updates;
            string updatesMessage = "Aucune mise à jour de pilote proposée par Windows Update.";
            try
            {
                updates = await _windowsUpdate.SearchAsync(token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                updates = [];
                updatesMessage = "Windows Update n'a pas pu être interrogé : " + ex.Message.Trim();
                Logger.Error("Recherche Windows Update", ex);
            }

            Report(100, "Analyse terminée.");

            Updates.Clear();
            foreach (var update in updates) Updates.Add(update);
            Problems.Clear();
            foreach (var problem in problems) Problems.Add(problem);
            ApplyFilter();

            UpdatesMessage = updatesMessage;
            Tabs[0].Badge = Updates.Count.ToString();
            Tabs[1].Badge = Problems.Count.ToString();
            Tabs[2].Badge = _allDrivers.Count.ToString();
            SelectedTab = Updates.Count > 0 ? 0 : ErrorCount > 0 ? 1 : SelectedTab;
            HasScanned = true;

            Headline = (Updates.Count, ErrorCount) switch
            {
                (0, 0) => "Tes pilotes sont à jour",
                (0, var e) => Plural(e, "périphérique en erreur", "périphériques en erreur"),
                (var u, _) => Plural(u, "mise à jour disponible", "mises à jour disponibles"),
            };
            SubText = $"{_allDrivers.Count} pilotes installés  ·  {Plural(ErrorCount, "périphérique en erreur", "périphériques en erreur")}";
            Logger.Info($"Analyse des pilotes : {Updates.Count} mise(s) à jour, {ErrorCount} erreur(s)");
        }
        catch (OperationCanceledException)
        {
            Headline = "Analyse annulée";
            SubText = "Clique sur le bouton pour recommencer.";
        }
        catch (Exception ex)
        {
            Headline = "L'analyse a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Analyse des pilotes", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        var selected = Updates.Where(u => u.IsSelected && u.Status != Status.Ok).ToList();
        if (IsBusy || selected.Count == 0) return;

        var token = BeginOperation("Installation en cours…", "Préparation…");
        try
        {
            if (CreateRestorePoint)
            {
                SubText = "Création d'un point de restauration…";
                var (ok, message) = await RestorePointService.CreateAsync(
                    "MaintenancePC - avant installation de pilotes", RestorePointService.DeviceDriverInstall, token);
                if (!ok)
                {
                    Headline = "Installation non lancée";
                    SubText = message + " Décoche la case pour installer quand même.";
                    return;
                }
            }

            var progress = new Progress<UpdateProgress>(p =>
            {
                p.Update.Status = p.Status;
                p.Update.StatusText = p.Message;
                Report(p.Percent, $"{p.Message} {p.Update.Title}");
            });
            var summary = await _windowsUpdate.InstallAsync(selected, progress, token);

            Headline = summary.Failed == 0
                ? Plural(summary.Succeeded, "pilote installé", "pilotes installés")
                : $"{Plural(summary.Succeeded, "pilote installé", "pilotes installés")}, {summary.Failed} en échec";
            SubText = summary.RebootRequired ? "Redémarre le PC pour terminer l'installation."
                : summary.Cancelled ? "Installation interrompue à ta demande."
                : "Installation terminée.";
        }
        catch (OperationCanceledException)
        {
            Headline = "Installation annulée";
            SubText = "Aucun pilote n'a été installé.";
        }
        catch (Exception ex)
        {
            Headline = "L'installation a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Installation de pilotes", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand] private static void OpenNvidia() => OpenInBrowser(NvidiaUrl);
    [RelayCommand] private static void OpenAsus() => OpenInBrowser(AsusUrl);

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Logger.Error($"Ouverture de {url}", ex); }
    }

    internal static string Plural(int count, string singular, string plural) =>
        count == 0 ? $"Aucun{(singular.StartsWith("mise") ? "e" : "")} {singular}" : count == 1 ? $"1 {singular}" : $"{count} {plural}";
}
