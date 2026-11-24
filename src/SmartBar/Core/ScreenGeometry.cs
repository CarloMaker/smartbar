using System.Drawing;
using System.Runtime.InteropServices;
using SmartBar.Infrastructure;
using SmartBar.Native;

namespace SmartBar.Core;

/// <summary>Da che parte dello schermo principale sta il parcheggio delle finestre.</summary>
internal enum ParkSide
{
    /// <summary>Oltre il bordo destro: le finestre scorrono dentro dalla destra. Il caso normale, con un solo schermo.</summary>
    Right,

    /// <summary>Sotto il bordo inferiore: le finestre scorrono su dal basso. Quando a destra c'e' un altro schermo.</summary>
    Below,

    /// <summary>Sopra il bordo superiore: le finestre scorrono giu' dall'alto.</summary>
    Above,

    /// <summary>
    /// Oltre il bordo destro dell'ultimo schermo. Restano invisibili, ma per Windows stanno vicino a quello schermo:
    /// se ha una scala diversa, i programmi ridimensionano la finestra. Solo quando non c'e' di meglio.
    /// </summary>
    FarRight,
}

/// <summary>
/// Misure degli schermi in pixel fisici. La barra e lo spazio delle finestre aperte stanno sul bordo destro dello
/// schermo principale. Le finestre parcheggiate stanno subito fuori da quello schermo, dal lato libero, cosi' non si
/// vedono: per Windows restano "dello" schermo principale e i programmi non le ridimensionano (se stessero oltre un
/// altro schermo con una scala diversa, cambierebbe la scala: il 2026-11-24, con un secondo schermo al 150%, le
/// finestre tornavano piu' piccole o non riempivano lo spazio).
/// Gli schermi possono cambiare mentre l'app gira (uno che si accende, si stacca, si sposta): le misure si rileggono
/// quando serve (<see cref="EnsureCurrent"/>), non valgono per sempre quelle dell'avvio. Quel giorno l'app era partita
/// con un solo schermo e parcheggiava 64 pixel oltre il bordo del principale: quando se n'e' acceso un secondo a
/// destra, le finestre parcheggiate comparivano sopra.
/// </summary>
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

    /// <summary>Lo schermo principale: qui stanno la barra e lo spazio delle finestre aperte.</summary>
    public static Rectangle HomeBounds { get; private set; }

    /// <summary>Lo schermo principale senza la barra delle applicazioni.</summary>
    public static Rectangle WorkArea { get; private set; }

    /// <summary>Lo schermo che finisce piu' a destra: serve solo quando si parcheggia oltre l'ultimo.</summary>
    public static Rectangle MonitorBounds { get; private set; }

    /// <summary>Lo spazio che occupa una finestra aperta: da metà schermo fino alla barra.</summary>
    public static Rectangle PanelRect { get; private set; }

    /// <summary>Da che parte si parcheggia, con gli schermi messi come sono adesso.</summary>
    public static ParkSide Side { get; private set; } = ParkSide.Right;

    /// <summary>Le finestre parcheggiate scorrono dentro e fuori di lato (altrimenti dal basso o dall'alto).</summary>
    public static bool SlidesSideways => Side is ParkSide.Right or ParkSide.FarRight;

    /// <summary>Oltre il bordo destro dell'ultimo schermo di quanto si parcheggia, quando si parcheggia li'.</summary>
    public static int ParkX => MonitorBounds.Right + Px(ParkMargin96);

    /// <summary>Angolo in alto a sinistra della barra: sotto i pulsanti delle finestre massimizzate.</summary>
    public static Point RailLocation => new(HomeBounds.Right - Px(RailWidth96), WorkArea.Top + Px(RailTopOffset96));

    private readonly record struct ScreenInfo(Rectangle Bounds, Rectangle Work, bool Primary);

    private static List<ScreenInfo> _screens =
        new() { new ScreenInfo(new Rectangle(0, 0, Win32.GetSystemMetrics(0), Win32.GetSystemMetrics(1)), Rectangle.Empty, true) };

    /// <summary>Come sono messi gli schermi adesso: se cambia, le misure non valgono piu'.</summary>
    private static (int X, int Y, int Width, int Height, int Count) _layout;

    static ScreenGeometry() => Refresh();

    /// <summary>Sono cambiati gli schermi dall'ultima volta che si e' guardato? Se si', rilegge le misure.</summary>
    public static bool EnsureCurrent()
    {
        if (Layout() == _layout)
        {
            return false;
        }

        Refresh();
        return true;
    }

    /// <summary>Rilegge gli schermi e rifà le misure.</summary>
    public static void Refresh()
    {
        _layout = Layout();

        var screens = new List<ScreenInfo>();
        _ = Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _dc, ref _rect, _data) =>
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (Win32.GetMonitorInfo(monitor, ref info))
            {
                screens.Add(new ScreenInfo(info.Monitor, info.Work, (info.Flags & Win32.MONITORINFOF_PRIMARY) != 0));
            }

            return true;
        }, IntPtr.Zero);

        if (screens.Count == 0)
        {
            // non si riesce a sapere (non dovrebbe succedere): restano le misure di prima
            Log.Warn("Windows non dice come sono messi gli schermi: restano le misure di prima");
            return;
        }

        _screens = screens;

        // lo schermo principale; se per qualche motivo non e' segnalato, vale il primo dell'elenco
        ScreenInfo home = screens.FirstOrDefault(screen => screen.Primary, screens[0]);
        HomeBounds = home.Bounds;
        WorkArea = home.Work;
        MonitorBounds = screens.Select(screen => screen.Bounds).OrderByDescending(bounds => bounds.Right).First();
        Side = ChooseSide(home, screens);

        // finestra aperta e barra insieme fanno mezza larghezza dello schermo
        int width = HomeBounds.Width / 2 - Px(RailWidth96);
        PanelRect = new Rectangle(HomeBounds.Right - Px(RailWidth96) - width, WorkArea.Top, width, WorkArea.Height);
    }

    /// <summary>
    /// Da che parte parcheggiare: il primo lato libero dello schermo principale, in quest'ordine: destra (le finestre
    /// scorrono dentro dalla destra, come sempre), sotto, sopra. Oltre l'ultimo schermo solo se sono tutti occupati.
    /// </summary>
    private static ParkSide ChooseSide(ScreenInfo home, List<ScreenInfo> screens)
    {
        var others = screens.Where(screen => screen.Bounds != home.Bounds).ToList();
        int safe = Px(ParkMargin96);

        // a destra e' libero se nessun altro schermo gli sta di fianco (si vedrebbero le finestre parcheggiate)
        bool rightBusy = others.Any(screen => screen.Bounds.Right > home.Bounds.Right
                                              && screen.Bounds.Bottom > home.Work.Top && screen.Bounds.Top < home.Work.Bottom);

        // sotto (o sopra) e' libero se, sotto il bordo, nessun altro schermo arriva in corrispondenza dello schermo
        // principale, lasciando un margine: una finestra parcheggiata li' resterebbe invisibile e piu' vicina allo
        // schermo principale che a qualsiasi altro, quindi nella sua scala
        bool belowBusy = others.Any(screen => screen.Bounds.Bottom > home.Bounds.Bottom
                                              && screen.Bounds.Left < home.Bounds.Right - safe && screen.Bounds.Right > home.Bounds.Left);
        bool aboveBusy = others.Any(screen => screen.Bounds.Top < home.Bounds.Top
                                              && screen.Bounds.Left < home.Bounds.Right - safe && screen.Bounds.Right > home.Bounds.Left);

        return !rightBusy ? ParkSide.Right : !belowBusy ? ParkSide.Below : !aboveBusy ? ParkSide.Above : ParkSide.FarRight;
    }

    /// <summary>
    /// Dove parcheggiare una finestra larga <paramref name="width"/> e alta <paramref name="height"/>: un punto in cui
    /// la finestra non si vede su nessuno schermo.
    /// </summary>
    public static Point ParkSpot(int width, int height)
    {
        if (Side == ParkSide.Below)
        {
            // una finestra larga quasi come lo schermo puo' arrivare sotto un altro schermo che scende piu' in basso:
            // si va sotto anche a quello
            int floor = _screens.Where(screen => screen.Bounds.Right > HomeBounds.Left && screen.Bounds.Left < HomeBounds.Left + width)
                                .Select(screen => screen.Bounds.Bottom).DefaultIfEmpty(HomeBounds.Bottom).Max();
            return new Point(HomeBounds.Left, floor + 1);
        }

        if (Side == ParkSide.Above)
        {
            int ceiling = _screens.Where(screen => screen.Bounds.Right > HomeBounds.Left && screen.Bounds.Left < HomeBounds.Left + width)
                                  .Select(screen => screen.Bounds.Top).DefaultIfEmpty(HomeBounds.Top).Min();
            return new Point(HomeBounds.Left, ceiling - 1 - height);
        }

        return new Point(ParkX, WorkArea.Top);
    }

    /// <summary>Il rettangolo si vede su almeno uno schermo (anche un solo pixel in comune)?</summary>
    public static bool IsOnScreen(in RECT rect)
    {
        var area = (Rectangle)rect;
        return _screens.Any(screen => screen.Bounds.Left < area.Right && screen.Bounds.Right > area.Left
                                      && screen.Bounds.Top < area.Bottom && screen.Bounds.Bottom > area.Top);
    }

    /// <summary>
    /// Il rettangolo sta nel posto dove si parcheggiava prima di guardare gli altri schermi, cioe' subito oltre il
    /// bordo destro del principale. Serve a ritrovare le finestre parcheggiate da una versione di prima, che con uno
    /// schermo acceso dopo si ritrovano in mezzo a quello.
    /// </summary>
    public static bool IsAtFirstSpot(in RECT rect) =>
        Math.Abs(rect.Left - (HomeBounds.Right + Px(ParkMargin96))) <= 16 && rect.Top <= WorkArea.Top + 16;

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

    /// <summary>Quant'e' lo spazio di tutti gli schermi insieme, e quanti sono: costa nulla da controllare.</summary>
    private static (int X, int Y, int Width, int Height, int Count) Layout() =>
        (Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN), Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN),
         Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN), Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN),
         Win32.GetSystemMetrics(Win32.SM_CMONITORS));

    public static int Px(float designPixels) => (int)Math.Round(designPixels * Scale);
}
