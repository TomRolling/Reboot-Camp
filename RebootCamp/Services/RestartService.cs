using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RebootCamp.Services;

/// <summary>
/// Tout ce qui touche au redémarrage du PC. Règle : l'appli ne redémarre jamais sans un clic sur « Redémarrer maintenant ».
/// - Quand une opération a besoin d'un redémarrage, elle appelle <see cref="Need"/> ; la question est posée
///   une seule fois, à la fin de l'opération (<see cref="ProposeIfNeeded"/>).
/// - Pendant une installation, <see cref="Guard"/> dit à Windows de retenir tout redémarrage : certains
///   installeurs lancés en mode silencieux redémarrent le PC d'eux-mêmes, sans prévenir.
/// Tout se passe sur le fil de l'interface (les services sont attendus directement par les ViewModels) :
/// de simples champs suffisent, sans verrou.
/// </summary>
public static class RestartService
{
    /// <summary>Certains installeurs demandent le redémarrage juste après s'être terminés : la retenue dure encore un peu.</summary>
    private static readonly TimeSpan GuardTail = TimeSpan.FromSeconds(15);

    private static IntPtr _window;
    private static Action<string>? _ask;
    private static string? _reason;
    private static int _guards;
    private static bool _approvedByUser;

    /// <summary>Appelé par la fenêtre principale : elle seule sait afficher la question.</summary>
    public static void Attach(IntPtr window, Action<string> ask)
    {
        _window = window;
        _ask = ask;
    }

    /// <summary>Note qu'un redémarrage est nécessaire. La première raison donnée est celle qui sera affichée.</summary>
    public static void Need(string reason) => _reason ??= reason;

    /// <summary>À la fin d'une opération : pose la question si un redémarrage a été noté, puis l'oublie.</summary>
    public static void ProposeIfNeeded()
    {
        if (_reason is not { } reason) return;
        _reason = null;
        _ask?.Invoke(reason);
    }

    // ───────────────────────── Retenir les redémarrages pendant une installation ─────────────────────────

    /// <summary>
    /// À entourer d'un « using » autour du lancement d'un installeur. La retenue se prolonge quelques secondes
    /// après la fin, puis la question est posée si l'installeur a tenté de redémarrer pendant ce temps.
    /// </summary>
    public static IDisposable Guard()
    {
        _approvedByUser = false;   // un accord donné plus tôt ne vaut pas pour cette nouvelle installation
        if (++_guards == 1 && _window != IntPtr.Zero)
            ShutdownBlockReasonCreate(_window, $"{AppInfo.Name} installe des mises à jour");
        return new GuardScope();
    }

    private sealed class GuardScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _ = ReleaseLaterAsync();
        }
    }

    private static async Task ReleaseLaterAsync()
    {
        try
        {
            await Task.Delay(GuardTail, AppLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // L'appli se ferme.
        }
        if (--_guards == 0) StopBlocking();
        ProposeIfNeeded();
    }

    private static void StopBlocking()
    {
        if (_window != IntPtr.Zero) ShutdownBlockReasonDestroy(_window);
    }

    /// <summary>
    /// Appelé quand Windows demande à l'appli si elle accepte un arrêt ou un redémarrage.
    /// Pendant une installation, et sauf si c'est l'utilisateur qui vient de le demander ici : on refuse, on annule
    /// le redémarrage s'il est encore en attente, et on posera la question à la fin.
    /// </summary>
    /// <returns>Vrai si le redémarrage a été retenu.</returns>
    public static bool TryHoldRestart()
    {
        if (_guards == 0 || _approvedByUser) return false;

        Logger.Info("Un redémarrage du PC a été demandé pendant une installation : retenu.");
        Need("Un programme d'installation a voulu redémarrer le PC. Le redémarrage a été retenu pour te laisser choisir le moment.");
        try
        {
            RunShutdown("/a");   // annule un redémarrage programmé avec un délai ; sans effet (et sans danger) s'il n'y en a pas
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // shutdown.exe introuvable : le refus ci-dessus suffit.
        }
        return true;
    }

    /// <summary>Redémarre le PC. Appelé uniquement après un clic sur « Redémarrer maintenant ».</summary>
    public static void RestartNow()
    {
        Logger.Info("Redémarrage du PC demandé par l'utilisateur.");
        // L'utilisateur a dit oui : la retenue ne doit pas bloquer son propre redémarrage.
        _approvedByUser = true;
        StopBlocking();
        // Sans « /f » : Windows laisse aux autres programmes le temps de proposer d'enregistrer le travail en cours.
        RunShutdown("/r /t 0");
    }

    private static void RunShutdown(string arguments) =>
        Process.Start(new ProcessStartInfo(ProcessRunner.SystemTool("shutdown.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();

    // API Windows documentées (winuser.h) : texte affiché par Windows quand une appli retient l'arrêt du PC.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);
}
