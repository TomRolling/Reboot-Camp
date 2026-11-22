using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MaintenancePC.Services;

/// <summary>
/// Vérifie la signature numérique d'un fichier téléchargé avant de l'exécuter :
/// 1) Windows confirme que la signature est valide et que le fichier n'a pas été modifié (API WinVerifyTrust) ;
/// 2) on contrôle que le signataire est bien l'éditeur attendu.
/// </summary>
public static class SignatureVerifier
{
    public static bool IsSignedBy(string filePath, string expectedPublisher, out string details)
    {
        int trust = VerifyTrust(filePath);
        if (trust != 0)
        {
            details = $"signature absente ou invalide (code 0x{trust:X8})";
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057 // Seule méthode de .NET pour lire le signataire d'un exécutable.
            using var certificate = X509Certificate.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
            details = certificate.Subject;
            return certificate.Subject.Contains($"O={expectedPublisher}", StringComparison.OrdinalIgnoreCase)
                   || certificate.Subject.Contains($"O=\"{expectedPublisher}", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            details = "signataire illisible : " + ex.Message;
            return false;
        }
    }

    // ───────────── WinVerifyTrust (wintrust.dll), documenté par Microsoft ─────────────

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint UiNone = 2, RevokeNone = 0, ChoiceFile = 1, StateIgnore = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

    private static int VerifyTrust(string filePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = filePath,
        };
        IntPtr fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeNone,
                dwUnionChoice = ChoiceFile,
                pFile = fileInfoPointer,
                dwStateAction = StateIgnore,
            };
            Guid action = GenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
            Marshal.FreeHGlobal(fileInfoPointer);
        }
    }
}
