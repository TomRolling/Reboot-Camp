using System.Management;

namespace RebootCamp.Services;

/// <summary>Un pilote installé (une ligne de l'onglet « Tous les pilotes »).</summary>
public sealed record DriverInfo(string Name, string Manufacturer, string Version, string Date, string Category);

/// <summary>Un périphérique que Windows signale en erreur ou sans pilote.</summary>
public sealed record DeviceProblem(string Name, string Explanation, bool IsDisabledOnly)
{
    /// <summary>Orange pour un périphérique simplement désactivé, rouge pour une vraie erreur.</summary>
    public Models.Status Status => IsDisabledOnly ? Models.Status.Warning : Models.Status.Error;
}

/// <summary>Lecture des pilotes et des périphériques en erreur via WMI. Lecture seule : ne modifie rien.</summary>
public static class DriverService
{
    public static List<DriverInfo> ListDrivers(CancellationToken token)
    {
        var drivers = new HashSet<DriverInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceName, Manufacturer, DriverVersion, DriverDate, DeviceClass FROM Win32_PnPSignedDriver");
        using var results = searcher.Get();

        foreach (ManagementBaseObject item in results)
        {
            token.ThrowIfCancellationRequested();
            using (item)
            {
                if (item["DeviceName"] is not string name || string.IsNullOrWhiteSpace(name)) continue;

                string date = "";
                if (item["DriverDate"] is string rawDate)
                {
                    try { date = ManagementDateTimeConverter.ToDateTime(rawDate).ToString("dd/MM/yyyy"); }
                    catch { /* date illisible : on laisse vide */ }
                }

                // HashSet + record : les doublons exacts (10 « Périphérique d'entrée USB » identiques) sont fusionnés.
                drivers.Add(new DriverInfo(
                    name.Trim(),
                    (item["Manufacturer"] as string)?.Trim() ?? "",
                    item["DriverVersion"] as string ?? "",
                    date,
                    item["DeviceClass"] as string ?? ""));
            }
        }

        return [.. drivers.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public static List<DeviceProblem> ListProblems(CancellationToken token)
    {
        var problems = new List<DeviceProblem>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        using var results = searcher.Get();

        foreach (ManagementBaseObject item in results)
        {
            token.ThrowIfCancellationRequested();
            using (item)
            {
                int code = Convert.ToInt32(item["ConfigManagerErrorCode"]);
                string name = item["Name"] as string ?? item["DeviceID"] as string ?? "Périphérique inconnu";
                problems.Add(new DeviceProblem(name, $"{Explain(code)} (code {code})", IsDisabledOnly: code == 22));
            }
        }

        // Les vraies erreurs d'abord, les périphériques simplement désactivés ensuite.
        return [.. problems.OrderBy(p => p.IsDisabledOnly).ThenBy(p => p.Name)];
    }

    /// <summary>Traduction des codes d'erreur du Gestionnaire de périphériques les plus courants.</summary>
    private static string Explain(int code) => code switch
    {
        1 => "Périphérique mal configuré",
        3 => "Pilote endommagé ou mémoire insuffisante",
        10 => "Le périphérique ne peut pas démarrer",
        12 => "Pas assez de ressources libres",
        14 => "Redémarrage nécessaire",
        18 => "Le pilote doit être réinstallé",
        19 => "Configuration du pilote endommagée",
        21 => "Périphérique en cours de suppression",
        22 => "Périphérique désactivé",
        24 => "Périphérique absent ou mal installé",
        28 => "Aucun pilote installé",
        29 => "Désactivé dans le BIOS/UEFI",
        31 => "Windows ne peut pas charger le pilote",
        32 => "Pilote désactivé",
        37 => "Le pilote n'a pas pu s'initialiser",
        39 => "Pilote endommagé ou manquant",
        43 => "Arrêté par Windows car il a signalé des problèmes",
        48 => "Pilote bloqué car incompatible",
        52 => "Signature numérique du pilote non vérifiée",
        _ => "Périphérique en erreur",
    };
}
