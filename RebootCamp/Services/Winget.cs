namespace RebootCamp.Services;

/// <summary>Ce que winget a répondu, une fois son code de sortie traduit.</summary>
public enum WingetOutcome
{
    /// <summary>Installé ou mis à jour.</summary>
    Done,
    /// <summary>Rien à faire : le paquet est déjà à jour.</summary>
    AlreadyUpToDate,
    /// <summary>Réussi, mais actif seulement après un redémarrage du PC.</summary>
    DoneAfterRestart,
    /// <summary>Échec : il faut redémarrer le PC puis recommencer.</summary>
    RestartThenRetry,
    Failed,
}

/// <summary>
/// Le seul endroit qui installe ou met à jour un paquet avec winget : mêmes options partout, redémarrages
/// retenus pendant l'installation, et codes de sortie traduits une seule fois.
/// </summary>
public static class Winget
{
    // Codes de sortie documentés par Microsoft (winget « returnCodes »).
    private const int UpdateNotApplicable = unchecked((int)0x8A15002B);     // « No applicable update found »
    private const int RebootRequiredToFinish = unchecked((int)0x8A150109);  // « Restart your PC to finish installation. »
    private const int RebootRequiredForInstall = unchecked((int)0x8A15010A);// « Installation failed. Restart your PC then try again. »
    private const int RebootInitiated = unchecked((int)0x8A15010B);         // « Your PC will restart to finish installation. »

    public static WingetOutcome Interpret(int exitCode) => exitCode switch
    {
        0 => WingetOutcome.Done,
        UpdateNotApplicable => WingetOutcome.AlreadyUpToDate,
        RebootRequiredToFinish or RebootInitiated => WingetOutcome.DoneAfterRestart,
        RebootRequiredForInstall => WingetOutcome.RestartThenRetry,
        _ => WingetOutcome.Failed,
    };

    /// <param name="verb">« install » ou « upgrade ».</param>
    /// <param name="displayName">Nom montré à l'utilisateur si un redémarrage est nécessaire.</param>
    /// <param name="extraOptions">Options propres à l'appelant (ex. « --include-unknown »).</param>
    /// <exception cref="System.ComponentModel.Win32Exception">Si winget est introuvable.</exception>
    public static async Task<(WingetOutcome Outcome, int Code)> RunAsync(
        string verb, string id, string displayName, IProgress<string>? output, CancellationToken token, string extraOptions = "")
    {
        // Jamais « --allow-reboot » : l'appli ne redémarre le PC que sur un clic de l'utilisateur.
        string arguments =
            $"{verb} --id {id} --exact --source winget --silent {extraOptions} ".Replace("  ", " ") +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity";

        output?.Report($"> winget {arguments}");
        Logger.Info($"winget {verb} {id}");

        int code;
        using (RestartService.Guard())
            code = await ProcessRunner.RunAsync("winget", arguments, output, token);

        Logger.Info($"winget {verb} {id} : code 0x{code:X8}");

        var outcome = Interpret(code);
        if (outcome is WingetOutcome.DoneAfterRestart or WingetOutcome.RestartThenRetry)
            RestartService.Need($"« {displayName} » a besoin d'un redémarrage du PC pour finir de s'installer.");
        return (outcome, code);
    }
}
