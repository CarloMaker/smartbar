using System.Text;
using System.Runtime.InteropServices;

namespace SmartBar.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;

    public RECT(int left, int top, int right, int bottom)
    {
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }

    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

    public static implicit operator System.Drawing.Rectangle(RECT r) => new(r.Left, r.Top, r.Width, r.Height);
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPLACEMENT
{
    public int Length, Flags, ShowCmd;
    public POINT MinPosition, MaxPosition;
    public RECT NormalPosition;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int Size;
    public RECT Monitor, Work;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DWM_THUMBNAIL_PROPERTIES
{
    public uint Flags;
    public RECT Destination;
    public RECT Source;
    public byte Opacity;
    [MarshalAs(UnmanagedType.Bool)] public bool Visible;
    [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
}

[StructLayout(LayoutKind.Sequential)]
internal struct GUITHREADINFO
{
    public int Size;
    public uint Flags;
    public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
    public RECT CaretRect;
}

[ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
internal class TaskbarListClass
{
}

[ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList
{
    void HrInit();
    void AddTab(IntPtr hwnd);
    void DeleteTab(IntPtr hwnd);
    void ActivateTab(IntPtr hwnd);
    void SetActiveAlt(IntPtr hwnd);
}

/// <summary>Costanti e firme Win32 usate dall'app.</summary>
internal static class Win32
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    public const long WS_CHILD = 0x40000000L;
    public const long WS_CAPTION = 0x00C00000L;
    public const long WS_MINIMIZEBOX = 0x00020000L;
    public const long WS_EX_TOPMOST = 0x00000008L;
    public const long WS_EX_TRANSPARENT = 0x00000020L;   // il mouse passa attraverso
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_LAYERED = 0x00080000L;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const int GWL_STYLE = -16;
    public const int GWL_EXSTYLE = -20;

    public const uint WM_GETICON = 0x007F;
    public const int GCLP_HICON = -14;
    public const int GCLP_HICONSM = -34;

    public const uint GA_ROOT = 2;
    public const uint GW_HWNDNEXT = 2;
    public const uint GW_HWNDPREV = 3;
    public const uint GW_OWNER = 4;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_NOSENDCHANGING = 0x0400;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new(-2);

    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_SHOWMAXIMIZED = 3;
    public const int SW_SHOWNA = 8;
    public const int SW_RESTORE = 9;

    public const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;

    public const uint DWM_TNP_RECTDESTINATION = 0x1;
    public const uint DWM_TNP_OPACITY = 0x4;
    public const uint DWM_TNP_VISIBLE = 0x8;
    public const uint DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

    public const uint GUI_INMOVESIZE = 0x2;
    public const uint GUI_INMENUMODE = 0x4;
    public const uint GUI_SYSTEMMENUMODE = 0x8;
    public const uint GUI_POPUPMENUMODE = 0x10;

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    public static extern nint GetClassLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    public static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
        WinEventHook.WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetProp(IntPtr hwnd, string name, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetProp(IntPtr hwnd, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr RemoveProp(IntPtr hwnd, string name);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    [DllImport("dwmapi.dll")]
    public static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnailId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUnregisterThumbnail(IntPtr thumbnailId);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(IntPtr thumbnailId, ref DWM_THUMBNAIL_PROPERTIES properties);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rectValue, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int intValue, int size);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Come SendMessage, ma se la finestra non risponde entro il tempo stabilito rinuncia invece di restare ferma.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags,
        uint timeout, out UIntPtr result);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentProcessId();
}
