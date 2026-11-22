using System.Runtime.InteropServices;

namespace RebootCamp.Services;

/// <summary>
/// Prévient qu'une opération est terminée quand l'utilisateur est parti faire autre chose : le bouton de l'appli
/// clignote dans la barre des tâches jusqu'à ce qu'il revienne dessus. Aucun son, aucune notification, aucune icône.
/// </summary>
public static class Attention
{
    private static IntPtr _window;

    public static void Attach(IntPtr window) => _window = window;

    public static void FlashIfInBackground()
    {
        if (_window == IntPtr.Zero || GetForegroundWindow() == _window) return;

        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = _window,
            Flags = FlashTray | FlashUntilForeground,
        };
        FlashWindowEx(ref info);
    }

    // API Windows documentées (winuser.h, FLASHWINFO).
    private const uint FlashTray = 0x2;              // FLASHW_TRAY : le bouton de la barre des tâches
    private const uint FlashUntilForeground = 0xC;   // FLASHW_TIMERNOFG : jusqu'au retour au premier plan

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
