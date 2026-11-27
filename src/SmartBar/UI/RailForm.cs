using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using SmartBar.Core;
using SmartBar.Infrastructure;
using SmartBar.Native;

namespace SmartBar.UI;

/// <summary>
/// La barra sul bordo destro o sinistro, come sceglie l'utente: una tessera con l'anteprima viva per ogni finestra parcheggiata.
/// Copre tutta l'altezza del bordo e, come la barra delle applicazioni, occupa la sua striscia: Windows non lascia che
/// le altre finestre ci passino sopra (vedi ScreenGeometry.Reserved).
/// Le anteprime le disegna Windows sopra il contenuto della finestra, quindi la barra non e' trasparente
/// e dentro le tessere non si disegna niente (solo attorno). L'icona del programma di ogni anteprima sta su
/// un velo trasparente attaccato subito sopra la barra (vedi IconOverlay).
/// </summary>
internal sealed class RailForm : Form
{
    private const float Pad96 = 6, Gap96 = 6, Margin96 = 3;
    private const float Badge96 = 24, BadgeFloor96 = 10;   // la pastiglia con l'icona: intera, e quando l'anteprima e' bassa
    private const int Icon96 = 18;
    private const int FlashMs = 1300;    // quanto dura il lampo del colore dell'app quando la barra compare

    private static readonly Color IdleFrame = Color.FromArgb(122, 122, 122);
    private static readonly Color HoverFrame = Color.FromArgb(190, 190, 190);
    private static readonly Color OpenFrame = Color.FromArgb(235, 235, 235);

    private readonly ParkingService _service;
    private readonly IconOverlay _overlay = new();
    private readonly Dictionary<IntPtr, IntPtr> _thumbnails = new();
    private readonly Dictionary<IntPtr, Bitmap?> _icons = new();    // per finestra: la lettiamo una volta sola
    private readonly List<Tile> _tiles = new();
    private readonly System.Windows.Forms.Timer _flash = new() { Interval = 25 };
    private long _flashStart;
    private ParkedWindow? _hover;

    /// <summary>Sopra c'e' un programma a schermo intero (un video, un gioco): la barra sta sotto le finestre, come la barra di Windows.</summary>
    private bool _covered;

    /// <summary>Si stanno rifacendo le misure per un avviso di Windows: gli avvisi che arrivano intanto non lo rifanno.</summary>
    private bool _settling;

    private sealed record Tile(ParkedWindow Window, Rectangle Bounds, Rectangle Thumb);

    public RailForm(ParkingService service)
    {
        _service = service;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        DoubleBuffered = true;
        _service.Changed += SyncThumbnails;
        _service.Updated += OnServiceUpdated;
        _flash.Tick += OnFlash;
    }

    /// <summary>
    /// Crea subito la finestra della barra, ancora nascosta. Se la creasse al primo parcheggio, si troverebbe a
    /// mostrarla nel mezzo della propria creazione: capita cosi' che un'anteprima resti duplicata sotto le altre.
    /// </summary>
    public void Prepare() => _ = Handle;

    /// <summary>Tessera sotto il mouse: viene evidenziata.</summary>
    public ParkedWindow? Hover
    {
        get => _hover;
        set
        {
            if (ReferenceEquals(value, _hover))
            {
                return;
            }

            _hover = value;
            Invalidate();
        }
    }

    /// <summary>Click sull'anteprima di una finestra parcheggiata: mette o toglie la puntina.</summary>
    public event Action<ParkedWindow>? PreviewClicked;

    /// <summary>Tasto destro sull'anteprima: la finestra esce dal parcheggio e torna dov'era.</summary>
    public event Action<ParkedWindow>? PreviewReleased;

    /// <summary>Windows avverte che gli schermi sono cambiati (uno si e' acceso, staccato, spostato).</summary>
    public event Action? DisplaysChanged;

    private const int WmDisplayChange = 0x007E;
    private const int WmWindowPosChanged = 0x0047;

    /// <summary>Lo manda Esplora risorse quando riparte: la barra delle applicazioni e' stata rifatta.</summary>
    private static readonly int WmTaskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WmDisplayChange)
        {
            DisplaysChanged?.Invoke();
        }
        else if (m.Msg == DesktopBar.NoticeMessage)
        {
            OnDesktopNotice(m);
        }
        else if (m.Msg == WmWindowPosChanged)
        {
            ScreenGeometry.BarMoved();
        }
        else if (m.Msg == WmTaskbarCreated)
        {
            ScreenGeometry.BarForgotten();
        }
    }

    /// <summary>
    /// Gli avvisi che Windows manda a chi occupa un bordo dello schermo: arrivano col messaggio scelto alla
    /// registrazione, e nel wParam c'e' quale avviso e'.
    /// </summary>
    private void OnDesktopNotice(Message m)
    {
        switch ((uint)m.WParam)
        {
            case Win32.ABN_FULLSCREENAPP:
                // E' comparso, o e' finito, un programma a schermo intero. A chi occupa un bordo Windows lo dice ma non
                // lo sposta: la barra va in fondo alle finestre e poi torna in cima, come la barra delle applicazioni.
                // (Su Windows 11 26200, il PC per cui e' nata l'app, l'avviso e' arrivato solo "finito"; con un vero
                // programma a schermo intero e' Windows stesso a metterlo sopra le finestre in primo piano.)
                SetCovered(m.LParam != IntPtr.Zero);
                break;

            case Win32.ABN_STATECHANGE:
            case Win32.ABN_POSCHANGED:
                // La barra delle applicazioni ha cambiato posto, misura o modo (per esempio e' passata alla scomparsa),
                // o e' cambiata un'altra barra: la striscia di questa potrebbe essere un'altra
                if (_settling)
                {
                    break;
                }

                _settling = true;
                try
                {
                    if (ScreenGeometry.RecheckStrip() && Visible)
                    {
                        LayoutTiles();
                    }
                }
                finally
                {
                    _settling = false;
                }

                break;
        }
    }

    /// <summary>Con un programma a schermo intero in corso la barra va in fondo alle finestre; finito, torna in cima.</summary>
    private void SetCovered(bool covered)
    {
        if (covered == _covered)
        {
            return;    // Windows lo ripete a ogni registrazione: conta solo quando cambia
        }

        Log.Info(covered ? "Programma a schermo intero: la barra va sotto le finestre"
                         : "Finito il programma a schermo intero: la barra torna in cima");
        _covered = covered;
        _overlay.Covered = covered;
        if (covered)
        {
            _overlay.Hide();
            TopMost = false;
            _ = Win32.SetWindowPos(Handle, Win32.HWND_BOTTOM, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        else
        {
            TopMost = true;
            if (Visible)
            {
                BringToTop();
                PlaceBadges();
            }
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            // niente attivazione: toccare la barra non deve togliere il fuoco alla finestra su cui si lavora.
            // Finestra di servizio, come nell'esempio di Microsoft per le barre del desktop: i programmi che rimettono in
            // ordine le finestre le lasciano stare. Il 2026-11-27 sul PC dell'utente, senza, un altro programma la
            // spostava dentro lo spazio di lavoro ogni volta che la striscia cambiava (molto probabilmente FancyZones di
            // PowerToys, che al cambio dello spazio di lavoro sposta le finestre e non guarda quelle di servizio)
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW);
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = Win32.SetProp(Handle, "SmartBar.Rail", (IntPtr)1);
        int round = Win32.DWMWCP_ROUND;
        _ = Win32.DwmSetWindowAttribute(Handle, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        ScreenGeometry.AttachBar(Handle);   // con "--reserve" (o da sola, nell'istanza dell'utente): da qui occupa la sua striscia
        SyncThumbnails();
    }

    public ParkedWindow? TileAtScreen(Point screen)
    {
        if (!Visible)
        {
            return null;
        }

        Point client = PointToClient(screen);
        return _tiles.FirstOrDefault(tile => tile.Bounds.Contains(client))?.Window;
    }

    public Rectangle? TileScreenRect(ParkedWindow window)
    {
        Tile? tile = _tiles.FirstOrDefault(candidate => ReferenceEquals(candidate.Window, window));
        return tile is null ? null : RectangleToScreen(tile.Bounds);
    }

    /// <summary>Rimette la barra in cima: le finestre parcheggiate quando si aprono vanno sotto di lei.</summary>
    public void BringToTop()
    {
        if (_covered)
        {
            return;    // c'e' un programma a schermo intero: la barra sta apposta sotto
        }

        _ = Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        if (_overlay.Visible)
        {
            _overlay.StickAbove(Handle);
        }
    }

    /// <summary>L'icona del programma di una finestra parcheggiata (null se non si e' riusciti ad averla).</summary>
    public Bitmap? IconOf(ParkedWindow window)
    {
        if (!_icons.TryGetValue(window.Hwnd, out Bitmap? icon))
        {
            icon = AppIcons.Of(window.Hwnd, Px(Icon96));
            _icons[window.Hwnd] = icon;
        }

        return icon;
    }

    /// <summary>
    /// Ogni volta che la barra compare fa un lampo del colore dell'app e torna in cima alle finestre: e' stretta e
    /// scura, su uno sfondo scuro sparirebbe, e chi parcheggia una finestra deve vedere dov'e' andata.
    /// </summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible || Disposing)
        {
            _flash.Stop();
            return;
        }

        _flashStart = Stopwatch.GetTimestamp();
        _flash.Start();
        BringToTop();
    }

    private void OnFlash(object? sender, EventArgs e)
    {
        if (Milliseconds(_flashStart, Stopwatch.GetTimestamp()) >= FlashMs)
        {
            _flash.Stop();
        }

        Invalidate();
    }

    private static double Milliseconds(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button is not (MouseButtons.Left or MouseButtons.Right))
        {
            return;
        }

        Tile? tile = _tiles.FirstOrDefault(candidate => candidate.Bounds.Contains(e.Location));
        if (tile is null)
        {
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            PreviewReleased?.Invoke(tile.Window);
        }
        else
        {
            PreviewClicked?.Invoke(tile.Window);
        }
    }

    private void OnServiceUpdated()
    {
        if (IsDisposed)
        {
            return;
        }

        Invalidate();
        if (_service.HasExpanded)
        {
            BringToTop();
        }
    }

    private void SyncThumbnails()
    {
        if (IsDisposed)
        {
            return;
        }

        var wanted = new HashSet<IntPtr>(_service.Parked.Select(window => window.Hwnd));
        foreach (IntPtr gone in _thumbnails.Keys.Where(handle => !wanted.Contains(handle)).ToList())
        {
            _ = Win32.DwmUnregisterThumbnail(_thumbnails[gone]);
            _ = _thumbnails.Remove(gone);
            if (_icons.Remove(gone, out Bitmap? icon))
            {
                icon?.Dispose();
            }
        }

        foreach (ParkedWindow window in _service.Parked)
        {
            if (_thumbnails.ContainsKey(window.Hwnd))
            {
                continue;
            }

            if (Win32.DwmRegisterThumbnail(Handle, window.Hwnd, out IntPtr thumbnail) == 0)
            {
                _thumbnails[window.Hwnd] = thumbnail;
            }
            else
            {
                Log.Warn($"Anteprima rifiutata per {WindowTools.Describe(window.Hwnd)}");
            }
        }

        _hover = null;
        LayoutTiles();
    }

    private void LayoutTiles()
    {
        _tiles.Clear();
        int count = _service.Parked.Count;

        // con la prima finestra parcheggiata la barra occupa la sua striscia del bordo, con l'ultima che se ne va la lascia
        _ = ScreenGeometry.Reserve(count > 0);
        if (count == 0)
        {
            _overlay.Hide();
            Hide();
            return;
        }

        int pad = Px(Pad96), gap = Px(Gap96), margin = Px(Margin96);
        Rectangle bar = ScreenGeometry.BarRect;
        int innerWidth = bar.Width - 2 * pad;
        Rectangle panel = ScreenGeometry.PanelRect;
        float aspect = panel.Height / (float)panel.Width;

        // l'anteprima ha la forma della finestra aperta e riempie la larghezza della barra
        int thumbnailWidth = innerWidth - 2 * margin;
        int thumbnailHeight = (int)Math.Round(thumbnailWidth * aspect);

        // con tante finestre parcheggiate le tessere si abbassano pur di stare dentro la barra, che va fino alla barra delle applicazioni
        int available = bar.Height - 2 * pad - (count - 1) * gap;
        if (count * (thumbnailHeight + 2 * margin) > available)
        {
            thumbnailHeight = Math.Max(Px(24), available / count - 2 * margin);
            thumbnailWidth = (int)Math.Round(thumbnailHeight / aspect);
        }

        int tileHeight = thumbnailHeight + 2 * margin;
        int y = pad;
        foreach (ParkedWindow window in _service.Parked)
        {
            var bounds = new Rectangle(pad, y, innerWidth, tileHeight);
            var thumb = new Rectangle(bounds.Left + (bounds.Width - thumbnailWidth) / 2, bounds.Top + margin,
                thumbnailWidth, thumbnailHeight);
            _tiles.Add(new Tile(window, bounds, thumb));
            y += tileHeight + gap;
        }

        Bounds = bar;
        foreach (Tile tile in _tiles)
        {
            UpdateThumbnail(tile);
        }

        Invalidate();
        if (!Visible)
        {
            Show();
        }

        PlaceBadges();
    }

    /// <summary>
    /// L'icona del programma in basso a sinistra di ogni anteprima, come una linguetta: quando le anteprime sono scure,
    /// o i programmi si assomigliano, e' l'unico modo di capire che finestra e'. Con anteprime basse la pastiglia si
    /// rimpicciolisce, per non coprirle.
    /// </summary>
    private void PlaceBadges()
    {
        var badges = new List<IconOverlay.Badge>();
        foreach (Tile tile in _tiles)
        {
            if (IconOf(tile.Window) is not Bitmap icon)
            {
                continue;
            }

            int full = Px(Badge96);
            int size = Math.Min(full, Math.Min(tile.Thumb.Width / 2,
                Math.Max(Px(BadgeFloor96), tile.Thumb.Height * full / Px(77))));
            var corner = new Rectangle(tile.Thumb.Left + Px(3), tile.Thumb.Bottom - Px(3) - size, size, size);
            badges.Add(new IconOverlay.Badge(RectangleToScreen(corner), icon));
        }

        _overlay.Follow(Handle, Bounds, badges);
    }

    private void UpdateThumbnail(Tile tile)
    {
        if (!_thumbnails.TryGetValue(tile.Window.Hwnd, out IntPtr thumbnail))
        {
            return;
        }

        var properties = new DWM_THUMBNAIL_PROPERTIES
        {
            Flags = Win32.DWM_TNP_RECTDESTINATION | Win32.DWM_TNP_OPACITY | Win32.DWM_TNP_VISIBLE
                    | Win32.DWM_TNP_SOURCECLIENTAREAONLY,
            Destination = new RECT(tile.Thumb.Left, tile.Thumb.Top, tile.Thumb.Right, tile.Thumb.Bottom),
            Opacity = 255,
            Visible = true,
            SourceClientAreaOnly = true,
        };
        int result = Win32.DwmUpdateThumbnailProperties(thumbnail, ref properties);
        if (result != 0)
        {
            Log.Warn($"Anteprima non aggiornata: 0x{result:X8}");
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float scale = ScreenGeometry.Scale;

        // contorno del colore dell'app, sempre: la barra si vede anche su uno sfondo scuro. Quando la barra e'
        // appena comparsa l'interno si illumina dello stesso colore e sfuma in un lampo (le anteprime sono disegnate
        // da Windows sopra, nelle tessere non si vede niente di questo)
        double elapsed = Milliseconds(_flashStart, Stopwatch.GetTimestamp());
        int glow = _flash.Enabled && elapsed < FlashMs ? (int)(150 * (1 - elapsed / FlashMs)) : 0;
        var whole = new RectangleF(1.5f * scale, 1.5f * scale, ClientSize.Width - 3f * scale, ClientSize.Height - 3f * scale);
        using (GraphicsPath edge = Drawing.RoundedRect(whole, Px(8)))
        {
            if (glow > 0)
            {
                using var light = new SolidBrush(Color.FromArgb(glow, Drawing.Accent));
                graphics.FillPath(light, edge);
            }

            using var pen = new Pen(Color.FromArgb(glow > 0 ? 255 : 150, Drawing.Accent), 2f * scale);
            graphics.DrawPath(pen, edge);
        }

        foreach (Tile tile in _tiles)
        {
            if (tile.Window.Pinned)
            {
                // con la puntina la tessera cambia colore: si riempie tutt'attorno all'anteprima
                using GraphicsPath shape = Drawing.RoundedRect(tile.Bounds, Px(7));
                using var fill = new SolidBrush(Drawing.Accent);
                graphics.FillPath(fill, shape);
                continue;
            }

            bool hover = ReferenceEquals(tile.Window, _hover);
            bool open = tile.Window.State != ParkState.Collapsed;

            // cornice disegnata attorno all'anteprima, non sotto: l'anteprima la coprirebbe
            Rectangle frame = Rectangle.Inflate(tile.Thumb, open || hover ? Px(3) : Px(1), open || hover ? Px(3) : Px(1));
            using GraphicsPath outline = Drawing.RoundedRect(frame, Px(5));
            using var pen = new Pen(open ? OpenFrame : hover ? HoverFrame : IdleFrame, (open || hover ? 2f : 1f) * scale);
            graphics.DrawPath(pen, outline);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _flash.Stop();
            _flash.Dispose();
            ScreenGeometry.DetachBar();
            _service.Changed -= SyncThumbnails;
            _service.Updated -= OnServiceUpdated;
            foreach (IntPtr thumbnail in _thumbnails.Values)
            {
                _ = Win32.DwmUnregisterThumbnail(thumbnail);
            }

            _thumbnails.Clear();
            foreach (Bitmap? icon in _icons.Values)
            {
                icon?.Dispose();
            }

            _icons.Clear();
            _overlay.Dispose();
        }

        base.Dispose(disposing);
    }

    private static int Px(float designPixels) => ScreenGeometry.Px(designPixels);
}
