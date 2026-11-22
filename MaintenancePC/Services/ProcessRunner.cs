using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace MaintenancePC.Services;

/// <summary>Variante de Progress&lt;T&gt; qui appelle l'action immédiatement, sur le thread qui signale.</summary>
public sealed class DirectProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}

/// <summary>
/// Lance un programme en ligne de commande (winget, dism, sfc…) sans fenêtre,
/// renvoie sa sortie ligne par ligne et le tue si l'opération est annulée ou si l'appli se ferme.
/// </summary>
public static class ProcessRunner
{
    /// <summary>Encodage des outils Windows classiques comme DISM (page de codes « OEM », 850 en français).</summary>
    public static Encoding OemEncoding
    {
        get
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentUICulture.TextInfo.OEMCodePage); }
            catch { return Encoding.UTF8; }
        }
    }

    /// <summary>Chemin complet d'un outil de C:\Windows\System32 (évite de lancer un faux « dism.exe » placé ailleurs).</summary>
    public static string SystemTool(string name) => Path.Combine(Environment.SystemDirectory, name);

    /// <returns>Le code de sortie du programme.</returns>
    /// <exception cref="OperationCanceledException">Si l'opération a été annulée (le programme est alors tué).</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Si le programme est introuvable.</exception>
    public static async Task<int> RunAsync(
        string fileName, string arguments, IProgress<string>? output, CancellationToken token, Encoding? encoding = null)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        AppLifetime.RegisterChildProcess(process);

        // Si on annule : on tue le programme et tous ses propres enfants.
        using var registration = token.Register(() =>
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Déjà terminé.
            }
        });

        await Task.WhenAll(
            PumpAsync(process.StandardOutput, output),
            PumpAsync(process.StandardError, output)).ConfigureAwait(false);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        token.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, IProgress<string>? output)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            line = line.Replace("\0", "").TrimEnd();
            // On ignore les lignes vides et les caractères d'animation (- \ | /) de winget.
            if (line.Trim().Length > 1)
                output?.Report(line);
        }
    }
}
