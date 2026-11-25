using System.Drawing;
using System.Windows.Forms;
using SmartBar.Core;
using SmartBar.Infrastructure;
using SmartBar.Native;

namespace SmartBar.UI;

/// <summary>
/// La scheda che compare accanto alla tessera sotto il mouse, con l'anteprima grande e il titolo.
/// La barra resta stretta e ferma: e' la scheda a uscire verso l'interno dello schermo, cosi' le tessere non si spostano mai.
/// </summary>
internal sealed class CardForm : Form
{
    private const float Width96 = 220, Pad96 = 10, TitleHeight96 = 22, Gap96 = 6, Distance96 = 6, Icon96 = 16;

    private readonly RailForm _rail;
    private ParkedWindow? _window;
    private IntPtr _thumbnail;

    public CardForm(RailForm rail)
    {
        _rail = rail;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 32, 32);
        DoubleBuffered = true;
    }

    /// <summary>Click sull'anteprima grande: come il click sulla tessera, mette o toglie la puntina.</summary>
    public event Action<ParkedWindow>? PreviewClicked;

    /// <summary>Tasto destro sull'anteprima grande: la finestra esce dal parcheggio e torna dov'era.</summary>
    public event Action<ParkedWindow>? PreviewReleased;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)Win32.WS_EX_NOACTIVATE;
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = Win32.SetProp(Handle, "SmartBar.Card", (IntPtr)1);
        int round = Win32.DWMWCP_ROUND;
        _ = Win32.DwmSetWindowAttribute(Handle, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    /// <summary>La finestra della scheda, se il punto sta sopra la scheda.</summary>
    public ParkedWindow? WindowAt(Point screen) =>
        Visible && _window is not null && Bounds.Contains(screen) ? _window : null;

    /// <summary>Mostra la scheda di una finestra parcheggiata, o la nasconde se non serve più.</summary>
    public void ShowFor(ParkedWindow? window)
    {
        if (window is null || window.State != ParkState.Collapsed)
        {
            Hide();
            return;
        }

        if (!ReferenceEquals(window, _window))
        {
            Register(window);
            Invalidate();
        }

        Rectangle? tile = _rail.TileScreenRect(window);
        if (tile is null)
        {
            Hide();
            return;
        }

        int pad = Px(Pad96), width = Px(Width96);
        Rectangle panel = ScreenGeometry.PanelRect;
        int thumbnailWidth = width - 2 * pad;
        int thumbnailHeight = (int)Math.Round(thumbnailWidth * panel.Height / (float)panel.Width);
        int height = pad + thumbnailHeight + Px(Gap96) + Px(TitleHeight96) + pad;

        // attaccata alla tessera dal lato opposto al bordo, ma senza uscire dallo schermo
        int left = ScreenGeometry.BarSide == BarSide.Left
            ? _rail.Right + Px(Distance96)
            : _rail.Left - Px(Distance96) - width;
        int lowestTop = Math.Max(ScreenGeometry.WorkArea.Top, ScreenGeometry.WorkArea.Bottom - height);
        int top = Math.Clamp(tile.Value.Top - pad, ScreenGeometry.WorkArea.Top, lowestTop);
        Bounds = new Rectangle(left, top, width, height);

        if (_thumbnail != IntPtr.Zero)
        {
            var properties = new DWM_THUMBNAIL_PROPERTIES
            {
                Flags = Win32.DWM_TNP_RECTDESTINATION | Win32.DWM_TNP_OPACITY | Win32.DWM_TNP_VISIBLE
                        | Win32.DWM_TNP_SOURCECLIENTAREAONLY,
                Destination = new RECT(pad, pad, pad + thumbnailWidth, pad + thumbnailHeight),
                Opacity = 255,
                Visible = true,
                SourceClientAreaOnly = true,
            };
            _ = Win32.DwmUpdateThumbnailProperties(_thumbnail, ref properties);
        }

        if (!Visible)
        {
            Show();
        }
    }

    private void Register(ParkedWindow window)
    {
        if (_thumbnail != IntPtr.Zero)
        {
            _ = Win32.DwmUnregisterThumbnail(_thumbnail);
            _thumbnail = IntPtr.Zero;
        }

        _window = window;
        int result = Win32.DwmRegisterThumbnail(Handle, window.Hwnd, out IntPtr thumbnail);
        if (result == 0)
        {
            _thumbnail = thumbnail;
        }
        else
        {
            Log.Warn($"Anteprima della scheda rifiutata per {WindowTools.Describe(window.Hwnd)}");
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_window is null)
        {
            return;
        }

        // in basso: l'icona del programma e il titolo
        int line = Px(TitleHeight96);
        var title = new Rectangle(Px(Pad96), Height - Px(Pad96) - line, Width - 2 * Px(Pad96), line);
        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        if (_rail.IconOf(_window) is Bitmap icon)
        {
            int size = Px(Icon96);
            e.Graphics.DrawImage(icon, new Rectangle(title.Left, title.Top + (title.Height - size) / 2, size, size));
            title.X += size + Px(6);
            title.Width -= size + Px(6);
        }

        TextRenderer.DrawText(e.Graphics, _window.Title, Font, title, Color.FromArgb(225, 225, 225),
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_window is null || e.Button is not (MouseButtons.Left or MouseButtons.Right))
        {
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            PreviewReleased?.Invoke(_window);
        }
        else
        {
            PreviewClicked?.Invoke(_window);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _thumbnail != IntPtr.Zero)
        {
            _ = Win32.DwmUnregisterThumbnail(_thumbnail);
            _thumbnail = IntPtr.Zero;
        }

        base.Dispose(disposing);
    }

    private static int Px(float designPixels) => ScreenGeometry.Px(designPixels);
}
