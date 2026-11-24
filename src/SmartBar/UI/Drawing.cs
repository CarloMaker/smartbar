using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SmartBar.UI;

internal static class Drawing
{
    public static readonly Color Accent = Color.FromArgb(76, 194, 255);

    public static GraphicsPath RoundedRect(RectangleF bounds, float radius)
    {
        float diameter = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        var arc = new RectangleF(bounds.Location, new SizeF(diameter, diameter));
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Barra a destra dentro un riquadro: la stessa figura del pulsante di parcheggio.</summary>
    public static void DrawParkGlyph(Graphics graphics, RectangleF box, Color color, float scale)
    {
        using var frame = new Pen(color, 1.5f * scale);
        using (GraphicsPath shape = RoundedRect(box, 2.5f * scale))
        {
            graphics.DrawPath(frame, shape);
        }

        var side = new RectangleF(box.Right - 6f * scale, box.Top + 2f * scale, 4f * scale, box.Height - 4f * scale);
        using var fill = new SolidBrush(color);
        using (GraphicsPath shape = RoundedRect(side, 1f * scale))
        {
            graphics.FillPath(fill, shape);
        }
    }

    public static Icon MakeTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using (var background = new SolidBrush(Color.FromArgb(32, 32, 32)))
            {
                graphics.FillEllipse(background, 1, 1, 30, 30);
            }

            DrawParkGlyph(graphics, new RectangleF(8, 10, 14, 12), Color.White, 1f);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
