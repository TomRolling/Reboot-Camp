using CommunityToolkit.Mvvm.ComponentModel;
using RebootCamp.Models;

namespace RebootCamp.Services;

/// <summary>Une mise à jour de pilote proposée par Windows Update.</summary>
public sealed partial class DriverUpdate : ObservableObject
{
    /// <summary>L'objet COM IUpdate d'origine. Utilisé uniquement par <see cref="WindowsUpdateService"/>.</summary>
    internal object? ComUpdate { get; }

    /// <summary>Renseigné quand la ligne est le pilote graphique NVIDIA (téléchargé chez NVIDIA, pas via Windows Update).</summary>
    internal NvidiaDriver? Nvidia { get; }

    public string Title { get; }
    [ObservableProperty] public partial string Details { get; set; }

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial Status Status { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; }

    internal DriverUpdate(NvidiaDriver nvidia) : this(null, $"{nvidia.GpuName} - pilote NVIDIA {nvidia.LatestVersion}",
        $"NVIDIA  ·  version installée : {nvidia.InstalledVersion}  ·  {nvidia.SizeText}  ·  téléchargé depuis nvidia.com")
    {
        Nvidia = nvidia;
    }

    internal DriverUpdate(object? comUpdate, string title, string details)
    {
        ComUpdate = comUpdate;
        Title = title;
        Details = details;
        IsSelected = true;
        StatusText = "";
    }
}

public sealed record UpdateProgress(DriverUpdate Update, Status Status, string Message, double Percent);

public sealed record InstallSummary(int Succeeded, int Failed, bool RebootRequired, bool Cancelled);

/// <summary>
/// Recherche et installation de pilotes via l'API officielle Windows Update Agent (objets COM « Microsoft.Update.* »).
/// On utilise « dynamic » : C# appelle alors les méthodes COM par leur nom, comme le ferait un script PowerShell.
/// Tout tourne sur un thread de fond pour ne pas geler l'interface.
/// </summary>
public sealed class WindowsUpdateService
{
    // Valeurs documentées par Microsoft.
    private const int ServerSelectionWindowsUpdate = 2;   // ServerSelection.ssWindowsUpdate
    private const int ResultSucceeded = 2;                // OperationResultCode.orcSucceeded
    private const int ResultSucceededWithErrors = 3;      // OperationResultCode.orcSucceededWithErrors

    private dynamic? _session;

    private static dynamic CreateCom(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
                   ?? throw new InvalidOperationException($"Composant Windows Update introuvable ({progId}).");
        return Activator.CreateInstance(type)!;
    }

    /// <summary>Recherche « IsInstalled=0 and Type='Driver' ». Lecture seule. Peut prendre de quelques secondes à quelques minutes.</summary>
    public Task<List<DriverUpdate>> SearchAsync(CancellationToken token)
    {
        var search = Task.Run(() =>
        {
            _session ??= CreateCom("Microsoft.Update.Session");
            dynamic searcher = _session.CreateUpdateSearcher();
            searcher.ServerSelection = ServerSelectionWindowsUpdate;
            dynamic result = searcher.Search("IsInstalled=0 and Type='Driver'");

            var updates = new List<DriverUpdate>();
            int count = result.Updates.Count;
            for (int i = 0; i < count; i++)
            {
                dynamic update = result.Updates.Item(i);
                // Un pilote déjà installé qui attend seulement le redémarrage reste « non installé » pour Windows Update :
                // on ne le repropose pas.
                bool waitingForReboot = false;
                try { waitingForReboot = (bool)update.RebootRequired; } catch { }
                if (waitingForReboot) continue;
                updates.Add(new DriverUpdate((object)update, (string)update.Title, Describe(update)));
            }
            return updates;
        });

        // La recherche COM ne peut pas être interrompue : si on annule, on arrête simplement de l'attendre.
        return search.WaitAsync(token);
    }

    /// <summary>Nombre de mises à jour Windows (hors pilotes) en attente, et si un redémarrage est demandé. Lecture seule.</summary>
    public Task<(int Pending, bool RebootRequired)> GetPendingAsync(CancellationToken token)
    {
        var search = Task.Run(() =>
        {
            _session ??= CreateCom("Microsoft.Update.Session");
            dynamic searcher = _session.CreateUpdateSearcher();
            searcher.ServerSelection = ServerSelectionWindowsUpdate;
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
            dynamic systemInfo = CreateCom("Microsoft.Update.SystemInfo");
            return ((int)result.Updates.Count, (bool)systemInfo.RebootRequired);
        });
        return search.WaitAsync(token);
    }

    private static string Describe(dynamic update)
    {
        var parts = new List<string>();
        try { if ((string)update.DriverManufacturer is { Length: > 0 } maker) parts.Add(maker); } catch { }
        try { parts.Add("pilote du " + ((DateTime)update.DriverVerDate).ToString("dd/MM/yyyy")); } catch { }
        try { if ((long)(decimal)update.MaxDownloadSize is > 0 and var size) parts.Add(Format.Size(size)); } catch { }
        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// Télécharge puis installe les pilotes un par un. L'annulation est prise en compte entre deux étapes
    /// (un téléchargement ou une installation déjà commencés vont jusqu'au bout, pour ne rien laisser à moitié fait).
    /// </summary>
    public Task<InstallSummary> InstallAsync(
        IReadOnlyList<DriverUpdate> updates, IProgress<UpdateProgress> progress, CancellationToken token)
    {
        return Task.Run(() =>
        {
            _session ??= CreateCom("Microsoft.Update.Session");
            int succeeded = 0, failed = 0;
            bool reboot = false;

            for (int i = 0; i < updates.Count; i++)
            {
                if (token.IsCancellationRequested)
                    return new InstallSummary(succeeded, failed, reboot, Cancelled: true);

                var item = updates[i];
                dynamic update = item.ComUpdate!;
                double basePercent = i * 100.0 / updates.Count;
                double step = 100.0 / updates.Count;

                try
                {
                    if (!(bool)update.EulaAccepted) update.AcceptEula();

                    dynamic collection = CreateCom("Microsoft.Update.UpdateColl");
                    collection.Add(update);

                    if (!(bool)update.IsDownloaded)
                    {
                        progress.Report(new UpdateProgress(item, Status.Working, "Téléchargement…", basePercent));
                        dynamic downloader = _session.CreateUpdateDownloader();
                        downloader.Updates = collection;
                        int downloadCode = (int)downloader.Download().ResultCode;
                        if (downloadCode is not (ResultSucceeded or ResultSucceededWithErrors))
                            throw new InvalidOperationException($"téléchargement refusé (code {downloadCode})");
                    }

                    if (token.IsCancellationRequested)
                    {
                        progress.Report(new UpdateProgress(item, Status.None, "Annulé", basePercent));
                        return new InstallSummary(succeeded, failed, reboot, Cancelled: true);
                    }

                    progress.Report(new UpdateProgress(item, Status.Working, "Installation…", basePercent + step / 2));
                    dynamic installer = _session.CreateUpdateInstaller();
                    installer.Updates = collection;
                    dynamic installResult = installer.Install();
                    int installCode = (int)installResult.ResultCode;
                    reboot |= (bool)installResult.RebootRequired;

                    if (installCode is not (ResultSucceeded or ResultSucceededWithErrors))
                        throw new InvalidOperationException($"installation refusée (code {installCode})");

                    succeeded++;
                    Logger.Info($"Pilote installé : {item.Title}");
                    progress.Report(new UpdateProgress(item, Status.Ok, "Installé", basePercent + step));
                }
                catch (Exception ex)
                {
                    failed++;
                    Logger.Error($"Pilote en échec : {item.Title}", ex);
                    progress.Report(new UpdateProgress(item, Status.Error, "Échec : " + ex.Message.Trim(), basePercent + step));
                }
            }

            return new InstallSummary(succeeded, failed, reboot, Cancelled: false);
        });
    }
}
