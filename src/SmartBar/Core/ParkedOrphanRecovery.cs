using System.Runtime.InteropServices;
using SmartBar.Infrastructure;
using SmartBar.Native;

namespace SmartBar.Core;

/// <summary>Cosa fare delle finestre rimaste parcheggiate da un'esecuzione precedente.</summary>
internal static class ParkedOrphanRecovery
{
    /// <summary>
    /// Ritrova una finestra parcheggiata. Di norma e' ancora oltre il bordo destro. Se al momento della chiusura
    /// era aperta a schermo e' rimasta li, sempre in primo piano e senza pulsante nella barra delle applicazioni:
    /// vale anche quella, ma solo se e' in primo piano (una finestra che l'utente si e' gia' ripresa non lo e' piu').
    /// </summary>
    public static IntPtr Find(SavedWindow saved, List<IntPtr> windows)
    {
        var offScreen = new List<IntPtr>();
        IntPtr leftOpen = IntPtr.Zero;
        int leftOpenCount = 0;

        foreach (IntPtr hwnd in windows)
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd))
            {
                continue;
            }

            if (!string.Equals(WindowTools.GetProcessName(hwnd), saved.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string title = WindowTools.GetTitle(hwnd);
            bool sameWindow = saved.Title.Length == 0
                              || title.Contains(saved.Title, StringComparison.Ordinal)
                              || saved.Title.Contains(title, StringComparison.Ordinal);
            if (!sameWindow)
            {
                continue;
            }

            if (Win32.GetWindowRect(hwnd, out RECT rect) && rect.Left >= ScreenGeometry.MonitorBounds.Right - 1)
            {
                offScreen.Add(hwnd);
            }
            else if (WindowTools.HasStyle(hwnd, Win32.GWL_EXSTYLE, Win32.WS_EX_TOPMOST))
            {
                leftOpen = hwnd;
                leftOpenCount++;
            }
        }

        if (offScreen.Count > 0)
        {
            return offScreen[0];
        }

        return leftOpenCount == 1 ? leftOpen : IntPtr.Zero;
    }

    /// <summary>Riporta a schermo le finestre rimaste parcheggiate, senza far girare l'app.</summary>
    public static int Run()
    {
        List<SavedWindow> saved = ParkedStore.Load();
        if (saved.Count == 0)
        {
            return 0;
        }

        List<IntPtr> windows = WindowTools.TopLevelWindows();
        using var taskbar = new TaskbarButtons();
        int restored = 0;
        foreach (SavedWindow item in saved)
        {
            IntPtr hwnd = Find(item, windows);
            if (hwnd == IntPtr.Zero)
            {
                continue;
            }

            var placement = new WINDOWPLACEMENT
            {
                Length = Marshal.SizeOf<WINDOWPLACEMENT>(),
                ShowCmd = item.ShowCmd == Win32.SW_SHOWMAXIMIZED ? Win32.SW_SHOWMAXIMIZED : Win32.SW_SHOWNA,
                NormalPosition = new RECT(item.Left, item.Top, item.Right, item.Bottom),
            };
            _ = Win32.SetWindowPlacement(hwnd, ref placement);
            if (!item.WasTopmost)
            {
                _ = Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }

            taskbar.Show(hwnd);
            restored++;
            Log.Info($"Riportata a schermo '{item.Title}' ({item.ProcessName})");
        }

        ParkedStore.Clear();
        return restored;
    }
}
