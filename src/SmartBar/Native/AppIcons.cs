using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SmartBar.Native;

/// <summary>
/// L'icona del programma di una finestra, come la mostra la barra delle applicazioni. Sulle anteprime parcheggiate
/// dice che programma e', quando l'anteprima e' scura o tutti i programmi si assomigliano.
/// </summary>
internal static class AppIcons
{
    private const int IconSmall = 0, IconBig = 1;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint AnswerTimeoutMs = 150;
    private const int SourceMax = 64;    // da qui in giu' il ridimensionamento resta netto

    /// <summary>
    /// L'icona in un'immagine quadrata (alta il doppio di <paramref name="pixels"/>: cosi' poi si riduce senza
    /// scalettature), o null se non si riesce ad avere.
    /// </summary>
    public static Bitmap? Of(IntPtr hwnd, int pixels)
    {
        IntPtr handle = AskedToWindow(hwnd);
        if (handle == IntPtr.Zero)
        {
            handle = OwnedByClass(hwnd);
        }

        if (handle != IntPtr.Zero)
        {
            // l'icona e' della finestra: qui la si copia in un'immagine nostra e non si distrugge
            using Icon icon = Icon.FromHandle(handle);
            if (Render(icon, pixels) is Bitmap copied)
            {
                return copied;
            }
        }

        // la finestra non ne ha: vale quella del suo programma
        uint processId = WindowTools.ProcessIdOf(hwnd);
        string? path = processId == 0 ? null : ProcessNames.PathOf(processId);
        if (path is not null && Icon.ExtractAssociatedIcon(path) is Icon program)
        {
            using Icon owned = program;
            return Render(owned, pixels);
        }

        return null;
    }

    /// <summary>Quella che il programma tiene pronta per la finestra (la stessa che vede la barra delle applicazioni).</summary>
    private static IntPtr AskedToWindow(IntPtr hwnd)
    {
        foreach (int which in new[] { IconBig, IconSmall })
        {
            IntPtr answered = Win32.SendMessageTimeoutW(hwnd, Win32.WM_GETICON, (IntPtr)which, IntPtr.Zero,
                SmtoAbortIfHung, AnswerTimeoutMs, out UIntPtr icon);
            if (answered != IntPtr.Zero && icon != UIntPtr.Zero)
            {
                return (IntPtr)icon.ToUInt64();
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Quella dichiarata dalla classe della finestra: la hanno le finestre classiche di Windows.</summary>
    private static IntPtr OwnedByClass(IntPtr hwnd)
    {
        nint small = Win32.GetClassLongPtr(hwnd, Win32.GCLP_HICONSM);
        if (small != 0)
        {
            return (IntPtr)small;
        }

        nint large = Win32.GetClassLongPtr(hwnd, Win32.GCLP_HICON);
        return large == 0 ? IntPtr.Zero : (IntPtr)large;
    }

    private static Bitmap? Render(Icon icon, int pixels)
    {
        int native = icon.Width > 0 ? icon.Width : 32;
        int box = Math.Max(pixels, Math.Min(native, SourceMax));
        using var large = new Bitmap(box, box, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(large))
        using (Bitmap image = icon.ToBitmap())
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(image, new Rectangle(0, 0, box, box));
        }

        var result = new Bitmap(pixels * 2, pixels * 2, PixelFormat.Format32bppPArgb);
        using Graphics resize = Graphics.FromImage(result);
        resize.InterpolationMode = InterpolationMode.HighQualityBicubic;
        resize.PixelOffsetMode = PixelOffsetMode.HighQuality;
        resize.DrawImage(large, new Rectangle(0, 0, result.Width, result.Height));
        return result;
    }
}
