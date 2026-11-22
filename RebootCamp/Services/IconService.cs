using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace RebootCamp.Services;

/// <summary>
/// Retrouve l'icône d'un programme pour l'afficher dans les listes (Logiciels, Désinstaller, Démarrage).
/// Rien n'est téléchargé : les icônes sont lues dans les fichiers déjà présents sur le PC (le .exe du programme,
/// son fichier .ico, ou le logo fourni avec une application Windows). Quand rien n'est trouvé, la liste affiche
/// une icône neutre à la place.
/// </summary>
public static class IconService
{
    // Une même icône sert souvent plusieurs fois (analyse relancée, même programme dans deux listes).
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] IconFileTypes = [".exe", ".ico", ".dll", ".lnk", ".cpl"];

    /// <summary>
    /// L'icône d'un fichier, tel que l'Explorateur l'afficherait. Accepte la forme « chemin,numéro » que les
    /// programmes utilisent pour désigner une icône précise à l'intérieur d'un fichier.
    /// </summary>
    public static ImageSource? ForFile(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        return Cache.GetOrAdd(location.Trim(), static key =>
        {
            var (path, index) = SplitIconLocation(key);
            if (!IconFileTypes.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) || !File.Exists(path)) return null;
            try
            {
                return index != 0 ? FromIndex(path, index) ?? FromShell(path) : FromShell(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ArgumentException)
            {
                return null;
            }
        });
    }

    /// <summary>L'icône du programme lancé par une ligne de commande (programmes au démarrage).</summary>
    public static ImageSource? ForCommand(string? command) =>
        string.IsNullOrWhiteSpace(command) ? null : ForFile(UninstallService.SplitCommand(command).File);

    /// <summary>
    /// L'icône d'un programme classique : celle qu'il a déclarée à Windows, sinon celle du plus gros .exe de son
    /// dossier d'installation (en général le programme lui-même).
    /// </summary>
    public static ImageSource? ForProgram(string declaredIcon, string installLocation)
    {
        if (ForFile(declaredIcon) is { } declared) return declared;
        if (installLocation.Length == 0 || !Directory.Exists(installLocation)) return null;
        try
        {
            var main = new DirectoryInfo(installLocation).EnumerateFiles("*.exe")
                .Where(file => !file.Name.StartsWith("unins", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.Length)
                .FirstOrDefault();
            return ForFile(main?.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Le logo d'une application Windows (Microsoft Store). Son dossier contient un fichier « AppxManifest.xml »
    /// qui nomme le logo, fourni en plusieurs tailles : on prend celle prévue pour les listes.
    /// </summary>
    public static ImageSource? ForWindowsApp(string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return null;
        return Cache.GetOrAdd("appx:" + installLocation, _ =>
        {
            try
            {
                string manifest = Path.Combine(installLocation, "AppxManifest.xml");
                if (!File.Exists(manifest)) return null;

                var document = XDocument.Load(manifest);
                // « Square44x44Logo » est l'icône de la liste des applications ; « Logo » est celle du Store.
                string? logo = document.Descendants().Where(e => e.Name.LocalName == "VisualElements")
                                   .Select(e => e.Attribute("Square44x44Logo")?.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v))
                               ?? document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value;
                if (string.IsNullOrEmpty(logo)) return null;

                return BestLogoFile(Path.Combine(installLocation, logo)) is { } file ? FromImage(file) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException
                                           or NotSupportedException or ArgumentException)
            {
                return null;
            }
        });
    }

    /// <summary>
    /// « Assets\Logo.png » n'existe pas toujours tel quel : Windows range « Logo.scale-200.png »,
    /// « Logo.targetsize-48_altform-unplated.png »… On préfère une version sans fond, d'environ 48 pixels.
    /// </summary>
    internal static string? BestLogoFile(string declaredPath)
    {
        string? folder = Path.GetDirectoryName(declaredPath);
        if (folder is null || !Directory.Exists(folder)) return null;

        string baseName = Path.GetFileNameWithoutExtension(declaredPath), extension = Path.GetExtension(declaredPath);
        var candidates = Directory.GetFiles(folder, baseName + "*" + extension)
            .Where(file => Path.GetFileName(file).Length == baseName.Length + extension.Length
                           || Path.GetFileName(file)[baseName.Length] == '.')
            .ToList();
        if (candidates.Count == 0) return null;

        string[] preferred =
        [
            "targetsize-48_altform-unplated", "targetsize-64_altform-unplated", "targetsize-32_altform-unplated",
            "targetsize-48", "targetsize-64", "scale-200", "scale-150", "scale-100",
        ];
        foreach (string wanted in preferred)
        {
            var match = candidates.FirstOrDefault(file =>
                Path.GetFileName(file).Contains("." + wanted + ".", StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return candidates.FirstOrDefault(file => !file.Contains("contrast-", StringComparison.OrdinalIgnoreCase)) ?? candidates[0];
    }

    /// <summary>« C:\App\app.exe,2 » → (« C:\App\app.exe », 2). Sans numéro : 0.</summary>
    internal static (string Path, int Index) SplitIconLocation(string location)
    {
        string text = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
        int comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..].Trim(), out int index))
            return (text[..comma].Trim().Trim('"'), index);
        return (text, 0);
    }

    // ───────────────────────── Lecture des images ─────────────────────────

    private static ImageSource? FromImage(string file)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;   // le fichier est lu puis refermé tout de suite
        image.DecodePixelWidth = 64;
        image.UriSource = new Uri(file);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static ImageSource? FromShell(string path)
    {
        var info = new ShFileInfo();
        IntPtr result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiLargeIcon);
        return result == IntPtr.Zero ? null : FromHandle(info.hIcon);
    }

    private static ImageSource? FromIndex(string path, int index)
    {
        var large = new IntPtr[1];
        return ExtractIconEx(path, index, large, null, 1) > 0 ? FromHandle(large[0]) : null;
    }

    /// <summary>Copie l'icône Windows dans une image de l'appli, puis rend l'icône à Windows.</summary>
    private static ImageSource? FromHandle(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();   // figée : utilisable depuis l'interface même si elle a été lue en arrière-plan
            return image;
        }
        finally
        {
            DestroyIcon(icon);
        }
    }

    // API Windows documentées (shellapi.h, winuser.h).
    private const uint ShgfiIcon = 0x100, ShgfiLargeIcon = 0x0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
