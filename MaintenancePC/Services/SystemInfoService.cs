using System.IO;
using System.Management;
using MaintenancePC.Models;
using Microsoft.Win32;

namespace MaintenancePC.Services;

public sealed record InfoLine(string Label, string Value);

public sealed record VolumeInfo(string Name, string Text, double UsedPercent, Status Status);

public sealed record DiskHealth(string Name, string Kind, Status Status, string HealthText, string Details);

/// <summary>Infos système et santé des disques. Lecture seule (WMI + registre).</summary>
public static class SystemInfoService
{
    public static List<InfoLine> GetSystemInfo()
    {
        var lines = new List<InfoLine>();

        foreach (var os in Query(@"root\cimv2", "SELECT Caption, Version, OSArchitecture FROM Win32_OperatingSystem"))
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string release = key?.GetValue("DisplayVersion") as string ?? "";
            lines.Add(new("Windows", $"{Text(os, "Caption")} {release} ({Text(os, "OSArchitecture")}, build {Text(os, "Version")})".Replace("  ", " ")));
        }

        foreach (var pc in Query(@"root\cimv2", "SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem"))
        {
            lines.Add(new("Ordinateur", $"{Text(pc, "Manufacturer")} {Text(pc, "Model")}".Trim()));
            if (pc["TotalPhysicalMemory"] is { } memory)
                lines.Add(new("Mémoire (RAM)", Format.Size(Convert.ToInt64(memory))));
        }

        foreach (var cpu in Query(@"root\cimv2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
            lines.Add(new("Processeur", $"{Text(cpu, "Name")} ({cpu["NumberOfCores"]} cœurs, {cpu["NumberOfLogicalProcessors"]} threads)"));

        foreach (var gpu in Query(@"root\cimv2", "SELECT Name, DriverVersion FROM Win32_VideoController"))
            lines.Add(new("Carte graphique", $"{Text(gpu, "Name")} (pilote {Text(gpu, "DriverVersion")})"));

        return lines;
    }

    public static List<VolumeInfo> GetVolumes()
    {
        var volumes = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

                double used = 100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Disque local" : drive.VolumeLabel;
                volumes.Add(new VolumeInfo(
                    $"{label} ({drive.Name.TrimEnd('\\')})",
                    $"{Format.Size(drive.AvailableFreeSpace)} libres sur {Format.Size(drive.TotalSize)}",
                    used,
                    used >= 90 ? Status.Error : used >= 80 ? Status.Warning : Status.Ok));
            }
            catch (IOException)
            {
                // Lecteur devenu indisponible.
            }
        }
        return volumes;
    }

    /// <summary>Santé des disques d'après Windows (MSFT_PhysicalDisk) et compteurs de fiabilité SMART quand ils existent.</summary>
    public static (List<DiskHealth> Disks, string SmartSummary) GetDisks()
    {
        var disks = new List<DiskHealth>();

        foreach (var disk in Query(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk"))
        {
            string kind = Convert.ToInt32(disk["MediaType"] ?? 0) switch
            {
                3 => "Disque dur (HDD)",
                4 => "SSD",
                5 => "Mémoire persistante",
                _ => "Disque",
            };
            long size = Convert.ToInt64(disk["Size"] ?? 0L);

            var (status, healthText) = Convert.ToInt32(disk["HealthStatus"] ?? 5) switch
            {
                0 => (Status.Ok, "En bonne santé"),
                1 => (Status.Warning, "Attention : signes de faiblesse, sauvegarde tes données"),
                2 => (Status.Error, "En mauvais état : sauvegarde tes données rapidement"),
                _ => (Status.None, "État inconnu"),
            };

            disks.Add(new DiskHealth(Text(disk, "FriendlyName"), $"{kind}  ·  {Format.Size(size)}", status, healthText, ReadReliability(disk)));
        }

        return (disks, ReadSmartPrediction());
    }

    private static string ReadReliability(ManagementBaseObject disk)
    {
        var parts = new List<string>();
        try
        {
            if (disk is not ManagementObject physicalDisk) return "";
            foreach (ManagementBaseObject counter in physicalDisk.GetRelated("MSFT_StorageReliabilityCounter"))
            {
                using (counter)
                {
                    if (counter["Temperature"] is { } temperature && Convert.ToInt32(temperature) > 0)
                        parts.Add($"{temperature} °C");
                    if (counter["Wear"] is { } wear)
                        parts.Add($"usure {wear} %");
                    if (counter["PowerOnHours"] is { } hours)
                        parts.Add($"{hours} h de fonctionnement");
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Compteurs non disponibles pour ce disque (ou droits insuffisants).
        }
        return string.Join("  ·  ", parts);
    }

    private static string ReadSmartPrediction()
    {
        try
        {
            var statuses = Query(@"root\WMI", "SELECT PredictFailure FROM MSStorageDriver_FailurePredictStatus", rethrow: true);
            if (statuses.Count == 0) return "SMART : non disponible sur ce PC.";
            return statuses.Any(s => s["PredictFailure"] is true)
                ? "SMART : un disque annonce une panne prochaine. Sauvegarde tes données."
                : "SMART : aucune panne annoncée.";
        }
        catch (Exception)
        {
            return "SMART : non disponible sur ce PC.";
        }
    }

    private static string Text(ManagementBaseObject item, string property) =>
        (item[property] as string)?.Trim() ?? "";

    private static List<ManagementBaseObject> Query(string scope, string wql, bool rethrow = false)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(scope, wql);
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results) list.Add(item);
        }
        catch (Exception ex) when (!rethrow && ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Logger.Error($"Requête WMI « {wql} »", ex);
        }
        return list;
    }
}
