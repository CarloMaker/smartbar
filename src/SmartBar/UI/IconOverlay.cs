using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using SmartBar.Native;

namespace SmartBar.UI;

/// <summary>
/// Il velo sopra la barra, con l'icona del programma su ogni anteprima. Le anteprime le disegna Windows sopra la
/// finestra della barra, quindi sulla barra non si puo' disegnare niente dentro le tessere: il velo e' una
/// finestrella trasparente grande quanto la barra e attaccata subito sopra di lei. Si lascia attraversare dal mouse,
/// cosi' i click arrivano alla barra come prima.
/// </summary>
internal sealed class IconOverlay : Form
{
    private const int WsExLayered = 0x00080000;
    private const int WsExTransparent = 0x00000020;

    private readonly List<Badge> _badges = new();

    /// <summary>Un'icona: dove sta sullo schermo e che immagine ha.</summary>
    internal readonly record struct Badge(Rectangle Screen, Bitmap Icon);

    public IconOverlay()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.Opaque | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            // a strati, per la trasparenza per singolo pixel; il mouse la attraversa; non si attiva mai
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered | WsExTransparent | (int)Win32.WS_EX_NOACTIVATE | (int)Win32.WS_EX_TOOLWINDOW;
            return parameters;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = Win32.SetProp(Handle, "SmartBar.Overlay", (IntPtr)1);
    }

    // Su una finestra a strati il disegno normale di Windows Forms non si vede: passa tutto da Render.
    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    protected override void OnPaint(PaintEventArgs e)
    {
    }

    /// <summary>
    /// Diventa grande quanto la barra, le sta subito sopra e mostra le icone nelle posizioni date (sullo schermo).
    /// Le immagini non sono sue: non vanno distrutte qui.
    /// </summary>
    public void Follow(IntPtr rail, Rectangle screen, IEnumerable<Badge> badges)
    {
        _badges.Clear();
        _badges.AddRange(badges);
        Bounds = screen;
        if (!Visible)
        {
            Show();
        }

        StickAbove(rail);
        Render();
    }

    /// <summary>
    /// Subito sopra la barra, nell'ordine delle finestre: se un'altra finestra copre la barra deve coprire anche il velo.
    /// </summary>
    public void StickAbove(IntPtr rail)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        IntPtr above = Win32.GetWindow(rail, Win32.GW_HWNDPREV);
        if (above == Handle)
        {
            return;    // gia' al suo posto
        }

        // sopra la barra non c'e' nessuno (e' la prima fra quelle sempre in primo piano): si va in cima
        _ = Win32.SetWindowPos(Handle, above != IntPtr.Zero ? above : Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void Render()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0)
        {
            return;
        }

        using var image = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (Graphics graphics = Graphics.FromImage(image))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;

            foreach (Badge badge in _badges)
            {
                // una pastiglia scura con dentro l'icona: si legge anche sopra anteprime chiare
                var box = new RectangleF(badge.Screen.X - Left, badge.Screen.Y - Top, badge.Screen.Width, badge.Screen.Height);
                using (GraphicsPath shape = Drawing.RoundedRect(box, box.Width / 5f))
                using (var back = new SolidBrush(Color.FromArgb(178, 0, 0, 0)))
                {
                    graphics.FillPath(back, shape);
                }

                int inset = Math.Max(1, (int)Math.Round(box.Width / 8f));
                graphics.DrawImage(badge.Icon, Rectangle.Inflate(Rectangle.Round(box), -inset, -inset));
            }
        }

        LayeredWindow.Push(Handle, image);
    }
}
