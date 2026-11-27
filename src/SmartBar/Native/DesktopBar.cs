using System.Drawing;
using System.Runtime.InteropServices;
using SmartBar.Infrastructure;

namespace SmartBar.Native;

/// <summary>
/// La barra registrata come "barra del desktop" di Windows (SHAppBarMessage), come la barra delle applicazioni:
/// occupa una striscia su un bordo dello schermo e Windows la toglie alle altre finestre, che ingrandite si fermano
/// prima. Con la registrazione Windows sceglie un messaggio con cui avvisarla di quello che la riguarda
/// (<see cref="NoticeMessage"/>): lo ascolta RailForm.
/// </summary>
internal sealed class DesktopBar
{
    /// <summary>Il messaggio con cui Windows avvisa la barra: nel wParam uno dei valori ABN_ di Win32.</summary>
    public const int NoticeMessage = (int)0x8000 + 100;    // WM_APP + 100

    private readonly IntPtr _window;
    private bool _registered;

    /// <summary>L'ultima striscia che Windows le ha dato: serve solo a non ripetere la riga di log.</summary>
    private Rectangle _strip;

    public DesktopBar(IntPtr window) => _window = window;

    /// <summary>
    /// Occupa la striscia della barra sul bordo (<paramref name="edge"/>: uno dei valori ABE_ di Win32), larga
    /// <paramref name="rail"/>, oppure la lascia. Restituisce la striscia che Windows ha dato alla barra, vuota se
    /// l'ha lasciata o se Windows non ha voluto registrarla.
    /// </summary>
    public Rectangle Place(bool occupy, uint edge, Rectangle wholeEdge, int rail)
    {
        if (!occupy)
        {
            Remove();
            return Rectangle.Empty;
        }

        APPBARDATA data = Data();
        if (!_registered)
        {
            data.CallbackMessage = NoticeMessage;
            if (Win32.SHAppBarMessage(Win32.ABM_NEW, ref data) == UIntPtr.Zero)
            {
                Log.Warn("Windows non ha voluto registrare la barra come barra del desktop: le finestre possono coprirla.");
                return Rectangle.Empty;
            }

            _registered = true;
        }

        Rectangle strip = Find(edge, wholeEdge, rail, occupy: true);
        if (strip.Width <= 0)
        {
            // il bordo e' tutto occupato: meglio niente che una barra larga zero
            Remove();
            return Rectangle.Empty;
        }

        if (strip != _strip)
        {
            Log.Info($"La barra occupa la sua striscia sul bordo {Side(edge)}: {strip}");
            _strip = strip;
        }

        return strip;
    }

    /// <summary>
    /// Che striscia darebbe Windows alla barra adesso, senza occuparla. Serve quando Windows avvisa che sul bordo
    /// qualcosa e' cambiato: rifare tutto per scoprire che e' rimasto uguale farebbe rifare la barra per niente.
    /// </summary>
    public Rectangle Query(uint edge, Rectangle wholeEdge, int rail) =>
        _registered ? Find(edge, wholeEdge, rail, occupy: false) : Rectangle.Empty;

    public void Remove()
    {
        if (!_registered)
        {
            return;
        }

        APPBARDATA data = Data();
        _ = Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref data);
        _registered = false;
        _strip = Rectangle.Empty;
    }

    /// <summary>Da dire a Windows ogni volta che la finestra della barra cambia posto o misura: e' parte della registrazione.</summary>
    public void Moved()
    {
        if (!_registered)
        {
            return;
        }

        APPBARDATA data = Data();
        _ = Win32.SHAppBarMessage(Win32.ABM_WINDOWPOSCHANGED, ref data);
    }

    /// <summary>
    /// Windows non la conosce piu': capita quando Esplora risorse riparte (la barra delle applicazioni viene rifatta).
    /// Al prossimo giro di misure si registra di nuovo.
    /// </summary>
    public void Forget()
    {
        _registered = false;
        _strip = Rectangle.Empty;
    }

    /// <summary>
    /// La striscia per la barra. Prima si chiede quella di fuori, per tutto il bordo dello schermo: Windows la
    /// aggiusta (le toglie la parte della barra delle applicazioni o di un'altra barra, che pero' non le da' in
    /// cambio lo spazio piu' dentro). Se cosi' resta piu' stretta, il posto di fuori e' tenuto da un'altra barra
    /// e si chiede quella subito accanto.
    /// </summary>
    private Rectangle Find(uint edge, Rectangle wholeEdge, int rail, bool occupy)
    {
        APPBARDATA data = Data();
        data.Edge = edge;
        data.Rect = new RECT(wholeEdge.Left, wholeEdge.Top, wholeEdge.Right, wholeEdge.Bottom);
        _ = Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref data);

        if (data.Rect.Width < rail)
        {
            Rectangle narrow = data.Rect;
            int outer = edge == Win32.ABE_LEFT ? data.Rect.Left : data.Rect.Right - rail;
            data.Rect = new RECT(outer, wholeEdge.Top, outer + rail, wholeEdge.Bottom);
            _ = Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref data);
            Log.Info($"Sul bordo {Side(edge)} un'altra barra tiene il posto di fuori (proposto {wholeEdge}, Windows dava {narrow}): "
                     + $"la barra va in {(Rectangle)data.Rect}");
        }

        if (occupy)
        {
            _ = Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref data);    // da qui le finestre ingrandite si fermano prima
        }

        return data.Rect;
    }

    private static string Side(uint edge) => edge == Win32.ABE_LEFT ? "sinistro" : "destro";

    private APPBARDATA Data() => new() { Size = Marshal.SizeOf<APPBARDATA>(), Hwnd = _window };
}
