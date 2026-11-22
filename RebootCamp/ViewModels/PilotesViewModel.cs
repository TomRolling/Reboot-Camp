using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

/// <summary>Un sous-onglet avec son compteur (« Mises à jour 2 »).</summary>
public sealed partial class SectionTab(string title) : ObservableObject
{
    public string Title { get; } = title;
    [ObservableProperty] public partial string Badge { get; set; } = "";
}

public sealed partial class PilotesViewModel : PageViewModel
{
    private readonly WindowsUpdateService _windowsUpdate = new();
    private List<DriverInfo> _allDrivers = [];

    // Ce qui a été installé depuis l'ouverture de l'appli : jamais reproposé, même avant le redémarrage du PC.
    private readonly HashSet<string> _installedThisSession = [];
    private string _nvidiaInstalledThisSession = "";

    public ObservableCollection<DriverUpdate> Updates { get; } = [];
    public ObservableCollection<DeviceProblem> Problems { get; } = [];
    public ObservableCollection<DriverInfo> Drivers { get; } = [];

    /// <summary>Boutons « page du fabricant », affichés seulement quand ils servent (carte AMD/Intel, périphérique en erreur).</summary>
    public ObservableCollection<SupportLink> Links { get; } = [];

    public IReadOnlyList<SectionTab> Tabs { get; } =
        [new("Mises à jour"), new("Problèmes"), new("Tous les pilotes")];

    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial bool HasScanned { get; set; }
    [ObservableProperty] public partial bool CreateRestorePoint { get; set; }
    [ObservableProperty] public partial bool BackupDrivers { get; set; }
    [ObservableProperty] public partial string FilterText { get; set; }
    [ObservableProperty] public partial string UpdatesMessage { get; set; }

    /// <summary>Nombre de vraies erreurs (les périphériques simplement désactivés ne comptent pas).</summary>
    public int ErrorCount => Problems.Count(p => !p.IsDisabledOnly);

    public PilotesViewModel() : base(
        "Pilotes", "", "Prêt à analyser",
        "Liste les pilotes, repère les périphériques en erreur et cherche les mises à jour.")
    {
        CreateRestorePoint = true;
        BackupDrivers = true;
        FilterText = "";
        UpdatesMessage = "";
    }

    /// <summary>Vrai tant qu'il reste au moins un pilote à installer (sinon la barre d'action disparaît).</summary>
    public bool HasPendingUpdates => Updates.Any(u => u.Status != Status.Ok);

    private void RefreshPending()
    {
        OnPropertyChanged(nameof(HasPendingUpdates));
        Tabs[0].Badge = Updates.Count(u => u.Status != Status.Ok).ToString();
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

            Report(35, "Recherche des périphériques en erreur…");
            var problems = await Task.Run(() => DriverService.ListProblems(token), token);

            Report(50, "Recherche de mises à jour sur Windows Update…");
            var updates = new List<DriverUpdate>();
            var notes = new List<string>();
            try
            {
                updates = await _windowsUpdate.SearchAsync(token);
                updates.RemoveAll(u => _installedThisSession.Contains(u.Title));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                notes.Add("Windows Update n'a pas pu être interrogé : " + ex.Message.Trim());
                Logger.Error("Recherche Windows Update", ex);
            }

            // Carte graphique NVIDIA : on demande directement à NVIDIA quel est le dernier pilote.
            bool nvidiaLinkNeeded = false;
            var cards = await Task.Run(HardwareService.GetGraphicsCards, token);
            foreach (var card in cards.Where(c => c.Vendor == "NVIDIA"))
            {
                Report(80, "Recherche du dernier pilote NVIDIA…");
                try
                {
                    var driver = await NvidiaDriverService.CheckAsync(card, token);
                    if (driver is null)
                    {
                        nvidiaLinkNeeded = true;
                        notes.Add($"{card.Name} : carte non reconnue par le service NVIDIA, utilise le bouton « Pilotes NVIDIA » ci-dessous.");
                    }
                    else if (driver.LatestVersion == _nvidiaInstalledThisSession)
                        notes.Add($"{card.Name} : pilote NVIDIA {driver.LatestVersion} installé, redémarre le PC pour terminer.");
                    else if (driver.IsNewer)
                        updates.Insert(0, new DriverUpdate(driver));
                    else
                        notes.Add($"{card.Name} : pilote NVIDIA à jour ({driver.InstalledVersion}).");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    nvidiaLinkNeeded = true;
                    notes.Add("Le service NVIDIA n'a pas répondu, utilise le bouton « Pilotes NVIDIA » ci-dessous.");
                    Logger.Error("Recherche du pilote NVIDIA", ex);
                }
                break;   // une seule carte NVIDIA suffit : le pilote est commun
            }
            // AMD et Intel : pas de service pour connaître le dernier pilote, mais un outil officiel qui s'en charge.
            string cpu = await Task.Run(HardwareService.GetProcessorVendor, token);
            bool amd = cpu == "AMD" || cards.Any(c => c.Vendor == "AMD");
            bool intel = cpu == "Intel" || cards.Any(c => c.Vendor == "Intel");
            if (amd) notes.Add("Matériel AMD : le bouton ci-dessous lance l'outil officiel d'AMD, qui installe les pilotes Radeon et chipset.");
            if (intel) notes.Add("Matériel Intel : le bouton ci-dessous installe l'assistant officiel d'Intel (graphique, chipset, Wi-Fi, Bluetooth).");

            Report(100, "Analyse terminée.");

            Updates.Clear();
            foreach (var update in updates) Updates.Add(update);
            Problems.Clear();
            foreach (var problem in problems) Problems.Add(problem);
            ApplyFilter();

            if (updates.Count == 0) notes.Insert(0, "Aucune mise à jour de pilote à installer.");
            UpdatesMessage = string.Join(Environment.NewLine, notes);

            // Liens vers les fabricants : uniquement ceux qui servent à quelque chose sur ce PC.
            bool hasErrors = problems.Any(p => !p.IsDisabledOnly);
            Links.Clear();
            foreach (var link in await Task.Run(HardwareService.GetSupportLinks, token))
            {
                bool useful = link.Label switch
                {
                    "Pilotes NVIDIA" => nvidiaLinkNeeded,
                    "Pilotes AMD" or "Pilotes Intel" => false,   // remplacés par les outils officiels ci-dessous
                    _ => hasErrors,   // support du fabricant du PC : utile quand un périphérique est en erreur
                };
                if (useful) Links.Add(link);
            }
            if (amd)
            {
                Links.Add(new SupportLink("Mettre à jour avec l'outil AMD", "",
                    "Télécharge l'outil officiel « Auto-Detect and Install » sur amd.com, vérifie sa signature et l'ouvre", LinkKind.AmdTool));
            }
            if (intel)
            {
                Links.Add(new SupportLink("Installer l'assistant de pilotes Intel", "",
                    "Installe « Intel Driver & Support Assistant » avec winget. Il reste ensuite en arrière-plan (désactivable dans Extras > Démarrage)", LinkKind.IntelTool));
            }

            RefreshPending();
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
            // ── Filets de sécurité avant de toucher aux pilotes ──
            if (CreateRestorePoint)
            {
                SubText = "Création d'un point de restauration…";
                var (ok, message) = await RestorePointService.CreateAsync(
                    $"{AppInfo.Name} - avant installation de pilotes", RestorePointService.DeviceDriverInstall, token);
                if (!ok)
                {
                    Headline = "Installation non lancée";
                    SubText = message + " Décoche la case pour installer quand même.";
                    return;
                }
            }
            if (BackupDrivers)
            {
                SubText = "Sauvegarde des pilotes actuels (quelques minutes)…";
                var (ok, message) = await DriverBackupService.ExportAsync(new Progress<string>(_ => { }), token);
                if (!ok)
                {
                    Headline = "Installation non lancée";
                    SubText = message + " Décoche la case pour installer quand même.";
                    return;
                }
            }

            int succeeded = 0, failed = 0;
            bool reboot = false, cancelled = false;

            // ── Pilotes Windows Update ──
            var fromWindowsUpdate = selected.Where(u => u.Nvidia is null).ToList();
            if (fromWindowsUpdate.Count > 0)
            {
                var progress = new Progress<UpdateProgress>(p =>
                {
                    p.Update.Status = p.Status;
                    p.Update.StatusText = p.Message;
                    Report(p.Percent, $"{p.Message} {p.Update.Title}");
                });
                var summary = await _windowsUpdate.InstallAsync(fromWindowsUpdate, progress, token);
                foreach (var done in fromWindowsUpdate.Where(u => u.Status == Status.Ok)) _installedThisSession.Add(done.Title);
                succeeded += summary.Succeeded;
                failed += summary.Failed;
                reboot |= summary.RebootRequired;
                cancelled = summary.Cancelled;
            }

            // ── Pilote graphique NVIDIA (en dernier : l'écran clignote pendant son installation) ──
            foreach (var item in selected.Where(u => u.Nvidia is not null))
            {
                if (cancelled || token.IsCancellationRequested) { cancelled = true; break; }

                item.Status = Status.Working;
                var progress = new Progress<(double Percent, string Message)>(p =>
                {
                    item.StatusText = p.Percent >= 0 ? $"{p.Percent:0} %" : "En cours…";
                    if (p.Percent >= 0) Report(p.Percent, p.Message);
                    else { Progress = -1; RingText = "···"; SubText = p.Message; RingCaption = ""; }
                });
                var (ok, message) = await NvidiaDriverService.DownloadAndInstallAsync(item.Nvidia!, progress, token);
                item.Status = ok ? Status.Ok : Status.Error;
                item.StatusText = ok ? "Installé" : "Échec";
                if (ok)
                {
                    _nvidiaInstalledThisSession = item.Nvidia!.LatestVersion;
                    item.IsSelected = false;
                    item.Details = $"NVIDIA  ·  version {item.Nvidia!.LatestVersion} installée (avant : {item.Nvidia.InstalledVersion})";
                }
                if (ok) { succeeded++; reboot = true; } else failed++;
                SubText = message;
                Logger.Info($"Pilote NVIDIA : {message}");
                if (!ok) UpdatesMessage = message;
            }

            Headline = failed == 0
                ? Plural(succeeded, "pilote installé", "pilotes installés")
                : $"{Plural(succeeded, "pilote installé", "pilotes installés")}, {failed} en échec";
            SubText = failed > 0 && UpdatesMessage.Length > 0 ? UpdatesMessage
                : reboot ? "Redémarre le PC pour terminer l'installation."
                : cancelled ? "Installation interrompue à ta demande."
                : "Installation terminée.";
        }
        catch (OperationCanceledException)
        {
            foreach (var item in selected.Where(u => u.Status == Status.Working))
            {
                item.Status = Status.None;
                item.StatusText = "Annulé";
            }
            Headline = "Installation annulée";
            SubText = "L'opération en cours a été arrêtée.";
        }
        catch (Exception ex)
        {
            Headline = "L'installation a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Installation de pilotes", ex);
        }
        finally
        {
            RefreshPending();
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task OpenLinkAsync(SupportLink link)
    {
        if (link.Kind == LinkKind.Web)
        {
            try { Process.Start(new ProcessStartInfo(link.Url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Logger.Error($"Ouverture de {link.Url}", ex); }
            return;
        }

        if (IsBusy) return;
        bool isAmd = link.Kind == LinkKind.AmdTool;
        string tool = isAmd ? "l'outil AMD" : "l'assistant Intel";
        var token = BeginOperation($"Préparation de {tool}…", "Préparation…");
        try
        {
            // Filet de sécurité. S'il échoue (protection du système désactivée), on continue : c'est noté dans le journal.
            if (CreateRestorePoint)
            {
                SubText = "Création d'un point de restauration…";
                await RestorePointService.CreateAsync(
                    $"{AppInfo.Name} - avant pilotes " + (isAmd ? "AMD" : "Intel"), RestorePointService.DeviceDriverInstall, token);
            }

            (bool ok, string message) = isAmd
                ? await VendorToolService.RunAmdToolAsync(new Progress<(double Percent, string Message)>(p =>
                    {
                        if (p.Percent >= 0) Report(p.Percent, p.Message);
                        else { Progress = -1; RingText = "···"; SubText = p.Message; }
                    }), token)
                : await VendorToolService.InstallIntelAssistantAsync(new Progress<string>(line => SubText = line), token);

            await Task.Yield();
            Headline = ok ? (isAmd ? "Outil AMD lancé" : "Assistant Intel installé") : (isAmd ? "Outil AMD indisponible" : "Assistant Intel non installé");
            SubText = message;
        }
        catch (OperationCanceledException)
        {
            Headline = "Opération annulée";
            SubText = "Rien n'a été installé.";
        }
        catch (Exception ex)
        {
            Headline = "L'opération a échoué";
            SubText = ex.Message.Trim();
            Logger.Error(tool, ex);
        }
        finally
        {
            EndOperation();
        }
    }

    internal static string Plural(int count, string singular, string plural) =>
        count == 0 ? $"Aucun{(singular.StartsWith("mise") || singular.StartsWith("installation") ? "e" : "")} {singular}"
        : count == 1 ? $"1 {singular}" : $"{count} {plural}";
}
