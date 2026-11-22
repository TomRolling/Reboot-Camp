using System.Management;

namespace MaintenancePC.Services;

/// <summary>
/// Crée un point de restauration système via la classe WMI « SystemRestore » (root\default),
/// l'interface documentée par Microsoft pour cela.
/// </summary>
public static class RestorePointService
{
    // Types de point de restauration (documentation Microsoft, SystemRestore.CreateRestorePoint).
    public const int DeviceDriverInstall = 10;
    public const int ModifySettings = 12;

    private const uint BeginSystemChange = 100;
    private const uint ErrorServiceDisabled = 1058;

    public static Task<(bool Ok, string Message)> CreateAsync(string description, int type, CancellationToken token)
    {
        return Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var systemRestore = new ManagementClass(
                    new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
                using var parameters = systemRestore.GetMethodParameters("CreateRestorePoint");
                parameters["Description"] = description;
                parameters["RestorePointType"] = (uint)type;
                parameters["EventType"] = BeginSystemChange;

                using var result = systemRestore.InvokeMethod("CreateRestorePoint", parameters, null);
                uint code = Convert.ToUInt32(result["ReturnValue"]);

                if (code == 0)
                {
                    Logger.Info($"Point de restauration demandé : {description}");
                    return (true, "Point de restauration créé. (Windows n'en crée qu'un par 24 h : " +
                                  "s'il en existe déjà un récent, il est réutilisé.)");
                }

                string reason = code == ErrorServiceDisabled
                    ? "la protection du système est désactivée sur ce PC (Paramètres > Système > Informations système > Protection du système)"
                    : $"Windows a renvoyé le code {code}";
                Logger.Error($"Point de restauration refusé : {reason}");
                return (false, $"Point de restauration impossible : {reason}.");
            }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                Logger.Error("Point de restauration", ex);
                return (false, $"Point de restauration impossible : {ex.Message.Trim()}");
            }
        }, token);
    }
}
