using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using SmartBar.Native;

namespace SmartBar.UI;

/// <summary>
/// Il pulsante di parcheggio: una finestrella nostra attaccata a sinistra di "Riduci a icona" di un altro programma.
/// Quel programma non viene toccato. Trasparente dove non disegna: sulla barra del titolo si vede solo il simbolo.
/// </summary>
internal sealed class CaptionButtonForm : Form
{
    private const float HandleWidth96 = 4, HandleHeight96 = 16, HandleZone96 = 8;
    private const int WsExLayered = 0x00080000;

    private readonly ToolTip _tip = new();
    private Rectangle _full;          // il pulsante intero, in coordinate dello schermo
    private bool _handleMode;
    private bool _hovering;
    private bool _dark = true;
    private uint _dpi = 96;

    public IntPtr Target { get; }

    public bool IsTopmost { get; set; }

    public event Action? Clicked;

    public CaptionButtonForm(IntPtr target)
    {
        Target = target;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        _tip.SetToolTip(this, "Parcheggia nella barra");
        _ = Win32.SetProp(Handle, "SmartBar.Target", target);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            // Finestra "a strati": il contenuto lo mandiamo noi, pixel per pixel (vedi LayeredWindow), perche'
            // Windows Forms da solo rende trasparente un solo colore, non i mezzi toni.
            // Niente attivazione: un click sul pulsante non deve togliere il fuoco alla finestra.
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered | (int)Win32.WS_EX_NOACTIVATE;
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation => true;

    /// <summary>Il pulsante intero, in coordinate dello schermo.</summary>
    public Rectangle FullBounds => _full;

    /// <summary>
    /// Da ferma si vede solo la maniglia (vedi <see cref="SetArea"/>): il pulsante intero si fa avanti al passaggio
    /// del mouse. Cosi' la finestrella occupa pochissimo posto sul contenuto del programma.
    /// </summary>
    public bool ShowsHandle => _handleMode && !_hovering;

    /// <summary>Dove sta adesso la finestrella, in coordinate dello schermo.</summary>
    public Rectangle CurrentBounds => ShowsHandle
        ? new Rectangle(_full.Right - Px(HandleZone96), _full.Top, Px(HandleZone96), _full.Height)
        : _full;

    public int Px(float designPixels) => (int)Math.Round(designPixels * _dpi / 96f);

    /// <summary>
    /// Dove sta il pulsante: attaccato a destra (subito a sinistra dei pulsanti della finestra), largo e alto come
    /// loro. Se subito a sinistra la finestra ha contenuto suo (una scheda, un pulsante suo), da ferma si vede solo
    /// una maniglia stretta e il pulsante intero si fa avanti al passaggio del mouse: cosi' non copre mai niente.
    /// </summary>
    public void SetArea(int right, int top, int width, int height, bool handleMode)
    {
        var full = new Rectangle(right - width, top, width, height);
        if (full == _full && handleMode == _handleMode)
        {
            return;
        }

        _full = full;
        _handleMode = handleMode;
        Bounds = CurrentBounds;    // si sposta senza toccare l'ordine delle finestre
    }

    public void SetDpi(uint dpi)
    {
        if (dpi == 0 || dpi == _dpi)
        {
            return;
        }

        _dpi = dpi;
        Render();
    }

    public void RefreshTheme()
    {
        bool dark = WindowTools.IsAppThemeDark(Target);
        if (dark == _dark)
        {
            return;
        }

        _dark = dark;
        Render();
    }

    // Su una finestra a strati il disegno normale di Windows Forms non si vede: passa tutto da Render.
    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    protected override void OnPaint(PaintEventArgs e)
    {
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Render();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Render();
    }

    private void Render()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0)
        {
            return;
        }

        float scale = _dpi / 96f;
        Color foreground = _dark ? Color.White : Color.FromArgb(20, 20, 20);

        using var image = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            // tutta l'area deve poter ricevere il click, anche dove non si vede niente: con alpha 1 e' cliccabile
            // ma praticamente invisibile (con alpha 0 sarebbe "bucata" e si cliccherebbe attraverso)
            using (var clickable = new SolidBrush(Color.FromArgb(1, foreground)))
            {
                graphics.FillRectangle(clickable, 0, 0, Width, Height);
            }

            if (ShowsHandle)
            {
                // da ferma, dove a sinistra c'e' contenuto del programma: solo una maniglia contro il bordo destro
                float handleWidth = HandleWidth96 * scale, handleHeight = HandleHeight96 * scale;
                var handle = new RectangleF(Width - handleWidth - 2f * scale, (Height - handleHeight) / 2f,
                    handleWidth, handleHeight);
                using GraphicsPath shape = Drawing.RoundedRect(handle, 2f * scale);
                using var fill = new SolidBrush(Color.FromArgb(120, foreground));
                graphics.FillPath(fill, shape);
            }
            else
            {
                if (_hovering)
                {
                    // come i pulsanti di Windows: al passaggio una pastiglia appena piu' chiara o piu' scura della barra
                    var pill = new RectangleF(2f * scale, 2f * scale, Width - 4f * scale - 1f, Height - 4f * scale - 1f);
                    using GraphicsPath shape = Drawing.RoundedRect(pill, 4f * scale);
                    using var highlight = new SolidBrush(_dark ? Color.FromArgb(30, Color.White) : Color.FromArgb(22, Color.Black));
                    graphics.FillPath(highlight, shape);
                }

                float glyphWidth = 14f * scale, glyphHeight = 11f * scale;
                var box = new RectangleF((Width - glyphWidth) / 2f, (Height - glyphHeight) / 2f, glyphWidth, glyphHeight);
                Drawing.DrawParkGlyph(graphics, box, Color.FromArgb(240, foreground), scale);
            }
        }

        LayeredWindow.Push(Handle, image);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        Hover(inside: true);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        Hover(inside: false);
    }

    /// <summary>
    /// Il cursore entra o esce. Con la maniglia il pulsante intero si fa avanti al passaggio e si ritira quando il
    /// cursore esce. Pubblico per le prove automatiche, che non possono muovere il mouse vero.
    /// </summary>
    public void Hover(bool inside)
    {
        _hovering = inside;
        Bounds = CurrentBounds;
        Render();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left)
        {
            Clicked?.Invoke();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
        }

        base.Dispose(disposing);
    }
}
