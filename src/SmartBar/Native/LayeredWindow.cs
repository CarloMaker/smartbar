using System.Drawing;
using System.Runtime.InteropServices;

namespace SmartBar.Native;

/// <summary>
/// Finestra "a strati" con trasparenza per singolo pixel: i punti non disegnati lasciano vedere, e pure cliccare
/// attraverso, la finestra che sta sotto. Windows Forms da solo sa solo rendere trasparente un colore.
/// </summary>
internal static class LayeredWindow
{
    private const byte SourceOver = 0;
    private const byte AlphaPerPixel = 1;
    private const uint UpdateWithAlpha = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Width, Height;

        public SIZE(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOperation, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, IntPtr destinationPoint, ref SIZE size,
        IntPtr sourceDc, ref POINT sourcePoint, int colorKey, ref BLENDFUNCTION blend, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr gdiObject);

    /// <summary>
    /// Manda alla finestra l'immagine disegnata. L'immagine deve essere a 32 bit "premoltiplicati"
    /// (Format32bppPArgb): e' la forma in cui Windows si aspetta i colori semitrasparenti.
    /// </summary>
    public static void Push(IntPtr hwnd, Bitmap image)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memory = CreateCompatibleDC(screen);
        IntPtr bits = image.GetHbitmap(Color.FromArgb(0));
        IntPtr previous = SelectObject(memory, bits);
        try
        {
            var size = new SIZE(image.Width, image.Height);
            var origin = new POINT();
            var blend = new BLENDFUNCTION { BlendOperation = SourceOver, SourceConstantAlpha = 255, AlphaFormat = AlphaPerPixel };
            _ = UpdateLayeredWindow(hwnd, screen, IntPtr.Zero, ref size, memory, ref origin, 0, ref blend, UpdateWithAlpha);
        }
        finally
        {
            _ = SelectObject(memory, previous);
            _ = DeleteObject(bits);
            _ = DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
