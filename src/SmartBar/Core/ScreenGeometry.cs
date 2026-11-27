using System.Drawing;
using System.Runtime.InteropServices;
using SmartBar.Infrastructure;
using SmartBar.Native;

namespace SmartBar.Core;

/// <summary>Da che parte dello schermo principale sta il parcheggio delle finestre.</summary>
internal enum ParkSide
{
    /// <summary>Oltre il bordo destro: le finestre scorrono dentro dalla destra. Quando la barra sta a destra e lì non c'e' un altro schermo.</summary>
    Right,

    /// <summary>Oltre il bordo sinistro: le finestre scorrono dentro dalla sinistra. Quando la barra sta a sinistra e lì non c'e' un altro schermo.</summary>
    Left,

    /// <summary>Sotto il bordo inferiore: le finestre scorrono su dal basso. Quando di lato c'e' un altro schermo.</summary>
    Below,

    /// <summary>Sopra il bordo superiore: le finestre scorrono giu' dall'alto.</summary>
    Above,

    /// <summary>
    /// Oltre il bordo dell'ultimo schermo a destra (o a sinistra). Restano invisibili, ma per Windows stanno vicino a
    /// quello schermo: se ha una scala diversa, i programmi ridimensionano la finestra. Solo quando non c'e' di meglio.
    /// </summary>
    FarRight,

    FarLeft,
}

/// <summary>
/// Misure degli schermi in pixel fisici. La barra e lo spazio delle finestre aperte stanno sul bordo sinistro o destro
/// dello schermo principale, come scelto dall'utente (<see cref="Settings.Side"/>). Le finestre parcheggiate stanno
/// subito fuori da quello schermo, dal lato libero, cosi' non si vedono: per Windows restano "dello" schermo
/// principale e i programmi non le ridimensionano (se stessero oltre un altro schermo con una scala diversa,
/// cambierebbe la scala: il 2026-11-24, con un secondo schermo al 150%, le finestre tornavano piu' piccole o non
/// riempivano lo spazio).
/// Gli schermi possono cambiare mentre l'app gira (uno che si accende, si stacca, si sposta): le misure si rileggono
/// quando serve (<see cref="EnsureCurrent"/>), non valgono per sempre quelle dell'avvio. Quel giorno l'app era partita
/// con un solo schermo e parcheggiava 64 pixel oltre il bordo del principale: quando se n'e' acceso un secondo a
/// destra, le finestre parcheggiate comparivano sopra.
///
/// Quando c'e' almeno una finestra parcheggiata la barra occupa la sua striscia del bordo per tutta l'altezza, come la
/// barra delle applicazioni: Windows la toglie alle altre finestre, che ingrandite si fermano prima (<see cref="Reserved"/>).
/// Senza finestre parcheggiate la barra non c'e' e la striscia torna alle finestre.
/// </summary>
internal static class ScreenGeometry
{
    public const float RailWidth96 = 84;

    private const float ParkMargin96 = 64;
    private const float SlotGap96 = 6;

    /// <summary>Quante finestre si possono tenere aperte con la puntina: una sopra, una sotto.</summary>
    public const int SlotCount = 2;

    /// <summary>Fattore di scala del sistema: converte i "pixel di progetto" (a 96 dpi) in pixel veri.</summary>
    public static float Scale { get; } = Win32.GetDpiForSystem() / 96f;

    /// <summary>Da che parte sta la barra, come scelto dall'utente.</summary>
    public static BarSide BarSide => Settings.Side;

    /// <summary>Lo schermo principale: qui stanno la barra e lo spazio delle finestre aperte.</summary>
    public static Rectangle HomeBounds { get; private set; }

    /// <summary>
    /// Lo spazio di lavoro dello schermo principale senza la barra delle applicazioni, tenendo per intero anche la
    /// striscia della barra: e' lo spazio che le finestre avrebbero se la barra non occupasse il suo bordo.
    /// </summary>
    public static Rectangle WorkArea { get; private set; }

    /// <summary>
    /// La striscia che la barra occupa sul bordo, per tutta l'altezza dello spazio di lavoro: finche' c'e', Windows
    /// non lascia che le altre finestre ci passino sopra. Vuota se la barra non e' a schermo (nessuna finestra
    /// parcheggiata), o se Windows non l'ha registrata.
    /// </summary>
    public static Rectangle Reserved { get; private set; }

    /// <summary>Dove sta la barra a schermo: la sua striscia; se non ne occupa una, il bordo dello spazio di lavoro.</summary>
    public static Rectangle BarRect { get; private set; }

    /// <summary>Gli schermi che finiscono piu' a destra e piu' a sinistra: servono solo quando si parcheggia oltre l'ultimo.</summary>
    public static Rectangle MonitorBounds { get; private set; }
    public static Rectangle LeftmostBounds { get; private set; }

    /// <summary>Lo spazio che occupa una finestra aperta: da metà schermo fino alla barra.</summary>
    public static Rectangle PanelRect { get; private set; }

    /// <summary>Da che parte si parcheggia, con gli schermi messi come sono adesso.</summary>
    public static ParkSide Side { get; private set; } = ParkSide.Right;

    /// <summary>Le finestre parcheggiate scorrono dentro e fuori di lato (altrimenti dal basso o dall'alto).</summary>
    public static bool SlidesSideways => Side is ParkSide.Right or ParkSide.Left or ParkSide.FarRight or ParkSide.FarLeft;

    private static IntPtr _homeMonitor;

    /// <summary>La finestra della barra, che chiede a Windows la sua striscia del bordo (null nell'istanza di prova senza "--reserve").</summary>
    private static DesktopBar? _bar;

    /// <summary>La barra e' a schermo e occupa la sua striscia.</summary>
    private static bool _barShown;

    private record struct ScreenInfo(IntPtr Monitor, Rectangle Bounds, Rectangle Work, bool Primary);

    private static List<ScreenInfo> _screens =
        new() { new ScreenInfo(IntPtr.Zero, new Rectangle(0, 0, Win32.GetSystemMetrics(0), Win32.GetSystemMetrics(1)), Rectangle.Empty, true) };

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

    /// <summary>
    /// La finestra della barra: da qui in poi, quando la barra e' a schermo, occupa la sua striscia del bordo. L'istanza
    /// di prova senza "--reserve" non la occupa (vedi Runtime.ReservesStrip).
    /// </summary>
    public static void AttachBar(IntPtr window)
    {
        if (Runtime.ReservesStrip)
        {
            _bar = new DesktopBar(window);
        }
    }

    /// <summary>La barra sta per chiudere: la sua striscia torna alle finestre subito (Windows la toglirebbe comunque alla chiusura).</summary>
    public static void DetachBar()
    {
        _bar?.Remove();
        _bar = null;
    }

    /// <summary>
    /// La barra e' comparsa a schermo e occupa la sua striscia, oppure e' sparita e la striscia torna alle finestre.
    /// Rifà le misure; falso se non c'era niente da cambiare.
    /// </summary>
    public static bool Reserve(bool shown)
    {
        if (shown == _barShown)
        {
            return false;
        }

        _barShown = shown;
        Refresh();
        return true;
    }

    /// <summary>
    /// Windows avvisa che la barra delle applicazioni o un'altra barra del desktop hanno cambiato posto o misura:
    /// la striscia della barra potrebbe essere un'altra. Rifà le misure solo se e' cambiata davvero.
    /// </summary>
    public static bool RecheckStrip()
    {
        if (!_barShown || _bar is null)
        {
            return false;
        }

        Rectangle strip = _bar.Query(EdgeOf(BarSide), OnEdge(HomeBounds), Px(RailWidth96));
        if (strip == Reserved)
        {
            return false;
        }

        Refresh();
        return true;
    }

    /// <summary>La finestra della barra ha cambiato posto o misura: va detto a Windows, fa parte della registrazione.</summary>
    public static void BarMoved() => _bar?.Moved();

    /// <summary>Esplora risorse e' ripartito e delle barre del desktop non se ne ricorda piu': la barra si registra di nuovo.</summary>
    public static void BarForgotten()
    {
        if (_bar is null)
        {
            return;
        }

        _bar.Forget();
        if (_barShown)
        {
            Refresh();
        }
    }

    /// <summary>Rilegge gli schermi e rifà le misure (da richiamare anche quando cambia il lato della barra o la sua striscia).</summary>
    public static void Refresh()
    {
        _layout = Layout();

        var screens = new List<ScreenInfo>();
        _ = Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _dc, ref _rect, _data) =>
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (Win32.GetMonitorInfo(monitor, ref info))
            {
                screens.Add(new ScreenInfo(monitor, info.Monitor, info.Work, (info.Flags & Win32.MONITORINFOF_PRIMARY) != 0));
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
        _homeMonitor = home.Monitor;
        MonitorBounds = screens.Select(screen => screen.Bounds).OrderByDescending(bounds => bounds.Right).First();
        LeftmostBounds = screens.Select(screen => screen.Bounds).OrderBy(bounds => bounds.Left).First();

        // La barra occupa (o lascia) la sua striscia: Windows cambia lo spazio di lavoro dentro la chiamata. Lo spazio
        // senza la nostra striscia si rilegge e, se la barra c'e', la si rimette dentro
        Reserved = _bar?.Place(_barShown, EdgeOf(BarSide), OnEdge(HomeBounds), Px(RailWidth96)) ?? Rectangle.Empty;
        Rectangle work = ReadWorkArea(home.Monitor);
        if (work.IsEmpty)
        {
            work = home.Work;
        }

        if (!Reserved.IsEmpty)
        {
            work = Rectangle.Union(work, Reserved);
        }

        WorkArea = work;
        BarRect = Reserved.IsEmpty ? OnEdge(work) : Reserved;
        Side = ChooseSide(new ScreenInfo(home.Monitor, home.Bounds, work, home.Primary), screens);

        // finestra aperta e barra insieme fanno mezza larghezza dello schermo; la barra sta sul bordo scelto
        int rail = Px(RailWidth96);
        int width = HomeBounds.Width / 2 - rail;
        int left = BarSide == BarSide.Left ? WorkArea.Left + rail : WorkArea.Right - rail - width;
        PanelRect = new Rectangle(left, WorkArea.Top, width, WorkArea.Height);
    }

    /// <summary>La striscia larga come la barra sul bordo scelto, per tutta l'altezza dell'area data.</summary>
    private static Rectangle OnEdge(Rectangle area)
    {
        int rail = Px(RailWidth96);
        return BarSide == BarSide.Left
            ? new Rectangle(area.Left, area.Top, rail, area.Height)
            : new Rectangle(area.Right - rail, area.Top, rail, area.Height);
    }

    /// <summary>Il bordo scelto, come lo chiama Windows (uno dei valori ABE_).</summary>
    private static uint EdgeOf(BarSide side) => side == BarSide.Left ? Win32.ABE_LEFT : Win32.ABE_RIGHT;

    private static Rectangle ReadWorkArea(IntPtr monitor)
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        return Win32.GetMonitorInfo(monitor, ref info) ? info.Work : Rectangle.Empty;
    }

    /// <summary>
    /// Da che parte parcheggiare: il primo lato libero, guardando prima quello dove sta la barra (cosi' le finestre
    /// scorrono dentro da quel lato), poi sotto, sopra, e da ultimo oltre l'ultimo schermo.
    /// </summary>
    private static ParkSide ChooseSide(ScreenInfo home, List<ScreenInfo> screens)
    {
        var others = screens.Where(screen => screen.Bounds != home.Bounds).ToList();
        int safe = Px(ParkMargin96);

        // di lato e' libero se nessun altro schermo gli sta di fianco (si vedrebbero le finestre parcheggiate)
        bool rightBusy = others.Any(screen => screen.Bounds.Right > home.Bounds.Right
                                              && screen.Bounds.Bottom > home.Work.Top && screen.Bounds.Top < home.Work.Bottom);
        bool leftBusy = others.Any(screen => screen.Bounds.Left < home.Bounds.Left
                                             && screen.Bounds.Bottom > home.Work.Top && screen.Bounds.Top < home.Work.Bottom);

        // sotto (o sopra) e' libero se, sotto il bordo, nessun altro schermo arriva in corrispondenza dello schermo
        // principale, lasciando un margine: una finestra parcheggiata li' resterebbe invisibile e piu' vicina allo
        // schermo principale che a qualsiasi altro, quindi nella sua scala
        bool belowBusy = others.Any(screen => screen.Bounds.Bottom > home.Bounds.Bottom
                                              && screen.Bounds.Left < home.Bounds.Right - safe && screen.Bounds.Right > home.Bounds.Left);
        bool aboveBusy = others.Any(screen => screen.Bounds.Top < home.Bounds.Top
                                              && screen.Bounds.Left < home.Bounds.Right - safe && screen.Bounds.Right > home.Bounds.Left);

        if (BarSide == BarSide.Left)
        {
            return !leftBusy ? ParkSide.Left : !belowBusy ? ParkSide.Below : !aboveBusy ? ParkSide.Above : ParkSide.FarLeft;
        }

        return !rightBusy ? ParkSide.Right : !belowBusy ? ParkSide.Below : !aboveBusy ? ParkSide.Above : ParkSide.FarRight;
    }

    /// <summary>
    /// Dove parcheggiare una finestra larga <paramref name="width"/> e alta <paramref name="height"/>: un punto in cui
    /// la finestra non si vede su nessuno schermo.
    /// </summary>
    public static Point ParkSpot(int width, int height)
    {
        switch (Side)
        {
            case ParkSide.Left:
                return new Point(HomeBounds.Left - 1 - width, WorkArea.Top);
            case ParkSide.FarLeft:
                return new Point(LeftmostBounds.Left - Px(ParkMargin96) - width, WorkArea.Top);
            case ParkSide.FarRight:
                return new Point(MonitorBounds.Right + Px(ParkMargin96), WorkArea.Top);
            case ParkSide.Below:
            case ParkSide.Above:
            {
                // una finestra larga quasi come lo schermo puo' arrivare sotto un altro schermo che scende piu' in basso:
                // si va sotto (o sopra) anche a quello
                List<Rectangle> touched = _screens.Where(screen => screen.Bounds.Right > HomeBounds.Left
                                                                 && screen.Bounds.Left < HomeBounds.Left + width)
                    .Select(screen => screen.Bounds).ToList();
                return Side == ParkSide.Below
                    ? new Point(HomeBounds.Left, touched.Select(bounds => bounds.Bottom).DefaultIfEmpty(HomeBounds.Bottom).Max() + 1)
                    : new Point(HomeBounds.Left, touched.Select(bounds => bounds.Top).DefaultIfEmpty(HomeBounds.Top).Min() - 1 - height);
            }

            default:
                return new Point(HomeBounds.Right + Px(ParkMargin96), WorkArea.Top);
        }
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
    /// così due finestre pinnate non si coprono e la metà opposta dello schermo resta libera.
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
