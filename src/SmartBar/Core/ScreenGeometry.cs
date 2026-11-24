using System.Drawing;
using System.Windows.Forms;
using SmartBar.Native;

namespace SmartBar.Core;

/// <summary>Misure dello schermo in pixel fisici. La barra sta sul bordo destro del monitor più a destra.</summary>
internal static class ScreenGeometry
{
    public const float RailWidth96 = 84;
    public const float RailTopOffset96 = 48;
    private const float ParkMargin96 = 64;
    private const float SlotGap96 = 6;

    /// <summary>Quante finestre si possono tenere aperte con la puntina: una sopra, una sotto.</summary>
    public const int SlotCount = 2;

    /// <summary>Fattore di scala del sistema: converte i "pixel di progetto" (a 96 dpi) in pixel veri.</summary>
    public static float Scale { get; } = Win32.GetDpiForSystem() / 96f;

    public static Rectangle MonitorBounds { get; } = Screen.AllScreens.OrderByDescending(s => s.Bounds.Right).First().Bounds;

    public static Rectangle WorkArea { get; } = Screen.AllScreens.OrderByDescending(s => s.Bounds.Right).First().WorkingArea;

    /// <summary>Lo spazio che occupa una finestra aperta: da metà schermo fino alla barra.</summary>
    public static Rectangle PanelRect { get; }

    /// <summary>Oltre il bordo destro di quanto stanno le finestre parcheggiate.</summary>
    public static int ParkX => MonitorBounds.Right + Px(ParkMargin96);

    /// <summary>Angolo in alto a sinistra della barra: sotto i pulsanti delle finestre massimizzate.</summary>
    public static Point RailLocation => new(MonitorBounds.Right - Px(RailWidth96), WorkArea.Top + Px(RailTopOffset96));

    static ScreenGeometry()
    {
        // finestra aperta e barra insieme fanno mezza larghezza dello schermo
        int width = MonitorBounds.Width / 2 - Px(RailWidth96);
        PanelRect = new Rectangle(MonitorBounds.Right - Px(RailWidth96) - width, WorkArea.Top, width, WorkArea.Height);
    }

    /// <summary>
    /// Uno dei due posti delle finestre pinnate: lo spazio accanto alla barra diviso in alto (0) e basso (1),
    /// così due finestre pinnate non si coprono e la metà sinistra dello schermo resta libera.
    /// </summary>
    public static Rectangle GetSlot(int index)
    {
        int gap = Px(SlotGap96);
        int half = (PanelRect.Height - gap) / 2;
        return index == 0
            ? new Rectangle(PanelRect.Left, PanelRect.Top, PanelRect.Width, half)
            : new Rectangle(PanelRect.Left, PanelRect.Top + half + gap, PanelRect.Width, PanelRect.Height - half - gap);
    }

    public static int Px(float designPixels) => (int)Math.Round(designPixels * Scale);
}
