using System.IO;

namespace MaintenancePC.Services;

/// <summary>
/// Journal des actions dans %LOCALAPPDATA%\MaintenancePC\logs.
/// Rotation : quand le fichier dépasse 512 Ko il devient « maintenance.old.log » (l'ancien est écrasé),
/// donc le dossier ne dépasse jamais environ 1 Mo.
/// </summary>
public static class Logger
{
    private const long MaxFileSize = 512 * 1024;

    private static readonly Lock Gate = new();

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MaintenancePC", "logs");

    private static readonly string FilePath = Path.Combine(Folder, "maintenance.log");
    private static readonly string OldFilePath = Path.Combine(Folder, "maintenance.old.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null) =>
        Write("ERREUR", exception is null ? message : $"{message} : {exception.GetType().Name} - {exception.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);

                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxFileSize)
                    File.Move(FilePath, OldFilePath, overwrite: true);

                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Le journal ne doit jamais faire planter l'application.
        }
    }
}
