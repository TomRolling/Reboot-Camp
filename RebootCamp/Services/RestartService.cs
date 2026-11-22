using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RebootCamp.Services;

/// <summary>
/// Tout ce qui touche au redémarrage du PC. Règle : l'appli ne redémarre jamais sans un clic sur « Redémarrer maintenant ».
/// - Quand une opération a besoin d'un redémarrage, elle appelle <see cref="Need"/> ; la question est posée
///   une seule fois, à la fin de l'opération (<see cref="ProposeIfNeeded"/>).
/// - Pendant une installation, <see cref="BeginGuard"/> dit à Windows de retenir tout redémarrage : certains
///   installeurs lancés en mode silencieux redémarrent le PC d'eux-mêmes, sans prévenir.
/// </summary>
public static class RestartService
{
    // Codes de sortie de winget (documentation Microsoft « returnCodes »).
    private const int WingetRebootToFinish = unchecked((int)0x8A150109);   // « Restart your PC to finish installation. »
    private const int WingetRebootToInstall = unchecked((int)0x8A15010A);  // « Installation failed. Restart your PC then try again. »
    private const int WingetRebootInitiated = unchecked((int)0x8A15010B);  // « Your PC will restart to finish installation. »

    private static readonly Lock Gate = new();
    private static IntPtr _window;
    private static Action<string>? _ask;
    private static string? _reason;
    private static int _guards;

    /// <summary>Vrai pendant une installation : la fenêtre principale refuse alors les demandes de redémarrage.</summary>
    public static bool IsGuarding => Volatile.Read(ref _guards) > 0;

    /// <summary>Appelé par la fenêtre principale : lui seule sait afficher la question.</summary>
    public static void Attach(IntPtr window, Action<string> ask)
    {
        _window = window;
        _ask = ask;
    }

    /// <summary>Vrai si ce code de sortie de winget signifie « il faut redémarrer ».</summary>
    public static bool IsRebootCode(int wingetExitCode) =>
        wingetExitCode is WingetRebootToFinish or WingetRebootToInstall or WingetRebootInitiated;

    /// <summary>Vrai si l'installation a réussi mais attend un redémarrage pour être terminée.</summary>
    public static bool IsSuccessPendingReboot(int wingetExitCode) =>
        wingetExitCode is WingetRebootToFinish or WingetRebootInitiated;

    /// <summary>Note qu'un redémarrage est nécessaire. La première raison donnée est celle qui sera affichée.</summary>
    public static void Need(string reason)
    {
        lock (Gate) _reason ??= reason;
    }

    /// <summary>À la fin d'une opération : pose la question si un redémarrage a été noté, puis l'oublie.</summary>
    public static void ProposeIfNeeded()
    {
        string? reason;
        lock (Gate)
        {
            reason = _reason;
            _reason = null;
        }
        if (reason is not null) _ask?.Invoke(reason);
    }

    // ───────────────────────── Retenir les redémarrages pendant une installation ─────────────────────────

    public static void BeginGuard()
    {
        if (Interlocked.Increment(ref _guards) == 1 && _window != IntPtr.Zero)
            ShutdownBlockReasonCreate(_window, $"{AppInfo.Name} installe des mises à jour");
    }

    public static void EndGuard()
    {
        if (Interlocked.Decrement(ref _guards) == 0 && _window != IntPtr.Zero)
            ShutdownBlockReasonDestroy(_window);
    }

    /// <summary>
    /// Windows vient de demander à l'appli si elle accepte un redémarrage alors qu'une installation tourne :
    /// on refuse, on annule le redémarrage s'il est encore en attente, et on posera la question à la fin.
    /// </summary>
    public static void OnRestartBlocked()
    {
        Logger.Info("Un redémarrage du PC a été demandé pendant une installation : retenu.");
        Need("Un programme d'installation a voulu redémarrer le PC. Le redémarrage a été retenu pour te laisser choisir le moment.");
        try
        {
            // « /a » annule un redémarrage programmé avec un délai. Sans effet (et sans danger) s'il n'y en a pas.
            Process.Start(new ProcessStartInfo(ProcessRunner.SystemTool("shutdown.exe"), "/a")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // shutdown.exe introuvable : le refus ci-dessus suffit.
        }
    }

    /// <summary>Redémarre le PC. Appelé uniquement après un clic sur « Redémarrer maintenant ».</summary>
    public static void RestartNow()
    {
        Logger.Info("Redémarrage du PC demandé par l'utilisateur.");
        // Sans « /f » : Windows laisse aux autres programmes le temps de proposer d'enregistrer le travail en cours.
        Process.Start(new ProcessStartInfo(ProcessRunner.SystemTool("shutdown.exe"), "/r /t 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();
    }

    // API Windows documentées (winuser.h) : texte affiché par Windows quand une appli retient l'arrêt du PC.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);
}
