using System.Management;
using MaintenancePC.Models;

namespace MaintenancePC.Services;

public sealed record SecurityLine(string Label, string Value, Status Status);

/// <summary>État de la sécurité et de la batterie. Lecture seule : rien n'est modifié.</summary>
public static class SecurityService
{
    public static List<SecurityLine> GetStatus()
    {
        var lines = new List<SecurityLine>();

        // ── Antivirus (Centre de sécurité Windows) ──
        var antivirus = Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct");
        if (antivirus.Count == 0)
        {
            lines.Add(new("Antivirus", "Aucun antivirus détecté", Status.Error));
        }
        foreach (var product in antivirus)
        {
            // Dans « productState », le 2e octet vaut 0x10 quand la protection est active.
            int state = Convert.ToInt32(product["productState"] ?? 0);
            bool enabled = ((state >> 8) & 0xFF) is 0x10 or 0x11;
            lines.Add(new("Antivirus", $"{product["displayName"]} : {(enabled ? "actif" : "inactif")}",
                enabled ? Status.Ok : Status.Warning));
        }
        // Un antivirus inactif n'est pas grave si un autre est actif (Defender se met en veille quand il y en a un autre).
        if (lines.Any(l => l.Status == Status.Ok))
            lines = [.. lines.Select(l => l.Status == Status.Warning ? l with { Status = Status.None } : l)];

        // ── Microsoft Defender : protection en temps réel et âge des définitions ──
        foreach (var defender in Query(@"root\Microsoft\Windows\Defender",
                     "SELECT AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureAge FROM MSFT_MpComputerStatus"))
        {
            if (defender["AntivirusEnabled"] is not true) continue;
            bool realTime = defender["RealTimeProtectionEnabled"] is true;
            int age = Convert.ToInt32(defender["AntivirusSignatureAge"] ?? 0);
            lines.Add(new("Protection en temps réel", realTime ? "Activée" : "Désactivée", realTime ? Status.Ok : Status.Error));
            lines.Add(new("Définitions de virus",
                age == 0 ? "À jour (aujourd'hui)" : (age == 1 ? "Datent d'hier" : $"Datent de {age} jours"), age > 7 ? Status.Warning : Status.Ok));
        }

        // ── Pare-feu ──
        var profiles = Query(@"root\StandardCimv2", "SELECT Name, Enabled FROM MSFT_NetFirewallProfile");
        if (profiles.Count > 0)
        {
            var disabled = profiles.Where(p => Convert.ToInt32(p["Enabled"] ?? 0) != 1).Select(p => p["Name"] as string).ToList();
            lines.Add(disabled.Count == 0
                ? new("Pare-feu Windows", "Activé", Status.Ok)
                : new("Pare-feu Windows", $"Désactivé pour : {string.Join(", ", disabled)} (normal si un autre pare-feu est installé)", Status.Warning));
        }

        // ── Version de Windows ──
        if (Environment.OSVersion.Version.Build < 22000)
        {
            lines.Add(new("Windows 10",
                "Ne reçoit plus de mises à jour de sécurité gratuites depuis octobre 2025. Pense à passer à Windows 11 si le PC est compatible.",
                Status.Warning));
        }

        return lines;
    }

    /// <summary>Santé de la batterie d'un portable, ou null sur un PC fixe.</summary>
    public static string? GetBatteryHealth()
    {
        try
        {
            long design = 0, full = 0;
            foreach (var item in Query(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData", log: false))
                design += Convert.ToInt64(item["DesignedCapacity"] ?? 0);
            foreach (var item in Query(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", log: false))
                full += Convert.ToInt64(item["FullChargedCapacity"] ?? 0);
            if (design <= 0 || full <= 0) return null;

            int health = (int)Math.Min(100, Math.Round(full * 100.0 / design));
            string advice = health >= 80 ? "en bon état" : health >= 60 ? "usée" : "très usée, à remplacer bientôt";
            return $"{health} % de sa capacité d'origine ({advice})";
        }
        catch
        {
            return null;
        }
    }

    private static List<ManagementBaseObject> Query(string scope, string wql, bool log = true)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results) list.Add(item);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            if (log) Logger.Error($"Requête WMI « {wql} »", ex);
        }
        return list;
    }
}
