using System.Diagnostics;

namespace MaintenancePC.Services;

/// <summary>
/// Point central de la règle « fermer la fenêtre = quitter complètement ».
/// - Toutes les opérations longues reçoivent <see cref="Token"/> et s'arrêtent quand il est annulé.
/// - Tous les processus enfants (winget, dism, sfc…) sont enregistrés ici pour être tués à la fermeture.
/// </summary>
public static class AppLifetime
{
    private static readonly CancellationTokenSource Cts = new();
    private static readonly List<Process> ChildProcesses = [];
    private static readonly Lock Gate = new();

    /// <summary>Jeton annulé au moment où l'application se ferme.</summary>
    public static CancellationToken Token => Cts.Token;

    /// <summary>Crée un jeton annulé soit par le bouton Annuler de l'onglet, soit par la fermeture de l'application.</summary>
    public static CancellationTokenSource CreateOperation(CancellationToken external = default) =>
        CancellationTokenSource.CreateLinkedTokenSource(Cts.Token, external);

    /// <summary>À appeler juste après avoir démarré un processus enfant.</summary>
    public static void RegisterChildProcess(Process process)
    {
        lock (Gate)
        {
            // On oublie au passage les processus déjà terminés pour ne pas garder de références inutiles.
            ChildProcesses.RemoveAll(p => HasExited(p));
            ChildProcesses.Add(process);
        }
    }

    /// <summary>Annule toutes les tâches en cours et tue les processus enfants. Appelé à la fermeture de la fenêtre.</summary>
    public static void Shutdown()
    {
        Cts.Cancel();

        lock (Gate)
        {
            foreach (var process in ChildProcesses)
            {
                try
                {
                    if (!HasExited(process))
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Le processus s'est terminé entre-temps ou refuse d'être tué : on continue avec les autres.
                }
            }
            ChildProcesses.Clear();
        }
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return true; }
    }
}
