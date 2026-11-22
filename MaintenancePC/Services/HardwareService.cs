using System.Management;

namespace MaintenancePC.Services;

/// <summary>Web = ouvre une page ; AmdTool / IntelTool = télécharge et lance l'outil officiel du fabricant.</summary>
public enum LinkKind { Web, AmdTool, IntelTool }

public sealed record SupportLink(string Label, string Url, string ToolTip, LinkKind Kind = LinkKind.Web);

public sealed record GraphicsCard(string Name, string Vendor, string DriverVersion);

/// <summary>Reconnaît le matériel du PC pour proposer les bons liens, quel que soit l'ordinateur.</summary>
public static class HardwareService
{
    // Pages officielles des fabricants. NVIDIA, AMD, Asus, HP, ASRock et Microsoft ont été vérifiées ;
    // les autres sites refusent les vérifications automatiques, on pointe donc vers leur page d'accueil du support.
    private static readonly (string Keyword, string Label, string Url)[] Makers =
    [
        ("asus", "Support Asus", "https://www.asus.com/fr/support/"),
        ("dell", "Support Dell", "https://www.dell.com/support"),
        ("alienware", "Support Dell", "https://www.dell.com/support"),
        ("hp", "Support HP", "https://support.hp.com/fr-fr/drivers"),
        ("hewlett", "Support HP", "https://support.hp.com/fr-fr/drivers"),
        ("lenovo", "Support Lenovo", "https://support.lenovo.com"),
        ("micro-star", "Support MSI", "https://www.msi.com/support"),
        ("msi", "Support MSI", "https://www.msi.com/support"),
        ("acer", "Support Acer", "https://www.acer.com"),
        ("gigabyte", "Support Gigabyte", "https://www.gigabyte.com"),
        ("asrock", "Support ASRock", "https://www.asrock.com/support/index.fr.asp"),
        ("microsoft", "Support Surface", "https://support.microsoft.com/fr-fr/surface"),
    ];

    public static List<GraphicsCard> GetGraphicsCards()
    {
        var cards = new List<GraphicsCard>();
        foreach (var gpu in Query("SELECT Name, AdapterCompatibility, DriverVersion, PNPDeviceID FROM Win32_VideoController"))
        {
            string name = gpu["Name"] as string ?? "";
            string pnp = gpu["PNPDeviceID"] as string ?? "";
            // On ignore les cartes virtuelles (émulateurs, bureau à distance) : seules les vraies cartes PCI comptent.
            if (!pnp.StartsWith("PCI", StringComparison.OrdinalIgnoreCase)) continue;

            string vendor = pnp.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
                : pnp.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ? "AMD"
                : pnp.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ? "Intel"
                : gpu["AdapterCompatibility"] as string ?? "";
            cards.Add(new GraphicsCard(name, vendor, gpu["DriverVersion"] as string ?? ""));
        }
        return cards;
    }

    /// <summary>« Intel », « AMD » ou vide : sert à proposer l'outil de pilotes du bon fabricant (chipset).</summary>
    public static string GetProcessorVendor()
    {
        foreach (var cpu in Query("SELECT Manufacturer FROM Win32_Processor"))
        {
            string maker = cpu["Manufacturer"] as string ?? "";
            if (maker.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
            if (maker.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return "AMD";
        }
        return "";
    }

    /// <summary>Boutons « page du fabricant » adaptés à ce PC : carte(s) graphique(s) puis marque de l'ordinateur.</summary>
    public static List<SupportLink> GetSupportLinks()
    {
        var links = new List<SupportLink>();

        foreach (string vendor in GetGraphicsCards().Select(c => c.Vendor).Distinct())
        {
            var link = vendor switch
            {
                "NVIDIA" => new SupportLink("Pilotes NVIDIA", "https://www.nvidia.com/fr-fr/drivers/", "Page de téléchargement officielle de NVIDIA"),
                "AMD" => new SupportLink("Pilotes AMD", "https://www.amd.com/fr/support/download/drivers.html", "Page de téléchargement officielle d'AMD"),
                "Intel" => new SupportLink("Pilotes Intel", "https://www.intel.fr/", "Site officiel d'Intel (rubrique Support)"),
                _ => null,
            };
            if (link is not null) links.Add(link);
        }

        // Marque du PC ; sur un PC monté soi-même c'est la marque de la carte mère qui compte.
        var names = new List<string>();
        foreach (var pc in Query("SELECT Manufacturer FROM Win32_ComputerSystem")) names.Add(pc["Manufacturer"] as string ?? "");
        foreach (var board in Query("SELECT Manufacturer FROM Win32_BaseBoard")) names.Add(board["Manufacturer"] as string ?? "");

        foreach (string name in names)
        {
            var maker = Makers.FirstOrDefault(m => name.Contains(m.Keyword, StringComparison.OrdinalIgnoreCase));
            if (maker.Label is null || links.Any(l => l.Label == maker.Label)) continue;
            links.Add(new SupportLink(maker.Label, maker.Url, $"Site de support officiel ({name.Trim()})"));
            break;
        }

        return links;
    }

    private static List<ManagementBaseObject> Query(string wql)
    {
        var list = new List<ManagementBaseObject>();
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results) list.Add(item);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Logger.Error($"Requête WMI « {wql} »", ex);
        }
        return list;
    }
}
