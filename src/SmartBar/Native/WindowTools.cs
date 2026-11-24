using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SmartBar.Infrastructure;

namespace SmartBar.Native;

/// <summary>
/// Dove sono i pulsanti di sistema di una finestra (Riduci a icona, Ingrandisci, Chiudi e l'aiuto se c'e'),
/// in coordinate dello schermo. <see cref="Count"/> e' quanti sono.
/// </summary>
internal sealed record CaptionBand(int Left, int Top, int Right, int Bottom, int Count)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    /// <summary>Larghezza di uno di loro: il pulsante di parcheggio prende la stessa.</summary>
    public int Cell => Math.Max(1, Width / Math.Max(1, Count));

    /// <summary>
    /// Subito a sinistra dei pulsanti c'e' contenuto del programma (una scheda, un pulsante suo): da ferma
    /// il nostro pulsante e' solo una maniglia, perche' intero coprirebbe qualcosa.
    /// </summary>
    public bool ContentOnLeft { get; init; }

    /// <summary>
    /// L'angolo in alto a destra che la finestra aveva quando sono stati misurati: se poi la finestra si
    /// sposta, i pulsanti si spostano con lei (si somma la differenza).
    /// </summary>
    public int AnchorX { get; init; }
    public int AnchorY { get; init; }
}

/// <summary>I pulsanti di sistema di una finestra, e se la finestra stessa ha risposto alla domanda.</summary>
internal sealed record CaptionPlacement(CaptionBand Band, bool WindowAnswered);

/// <summary>Letture sulle finestre degli altri programmi.</summary>
internal static class WindowTools
{
    private const int RectSize = 16;
    private const int IntSize = sizeof(int);
    private const uint MonitorNearest = 2;

    public const int HitClient = 1;       // il contenuto del programma (per esempio le schede)
    private const int HitMinimize = 8, HitMaximize = 9, HitClose = 20, HitHelp = 21;

    private const uint WmNcHitTest = 0x0084;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint AnswerTimeoutMs = 60;
    private const int SeparatorMax = 3;   // una linea di separazione fra due pulsanti, al piu' cosi' larga

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    private static readonly Dictionary<uint, string?> ProcessNameCache = new();
    private static long _themeTick = -10000;
    private static bool _themeDark = true;

    public static string GetTitle(IntPtr hwnd)
    {
        var text = new StringBuilder(512);
        _ = Win32.GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static string GetClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        _ = Win32.GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    public static string? GetProcessName(IntPtr hwnd)
    {
        uint processId = ProcessIdOf(hwnd);
        if (processId == 0)
        {
            return null;
        }

        lock (ProcessNameCache)
        {
            if (ProcessNameCache.TryGetValue(processId, out string? known))
            {
                return known;
            }
        }

        string? name = null;
        try
        {
            name = ProcessNames.NameOf(processId);
        }
        catch (Exception)
        {
            // processo appena terminato o non leggibile: resta sconosciuto
        }

        lock (ProcessNameCache)
        {
            ProcessNameCache[processId] = name;
        }

        return name;
    }

    public static uint ThreadIdOf(IntPtr hwnd) => Win32.GetWindowThreadProcessId(hwnd, out _);

    public static uint ProcessIdOf(IntPtr hwnd)
    {
        _ = Win32.GetWindowThreadProcessId(hwnd, out uint processId);
        return processId;
    }

    public static string Describe(IntPtr hwnd) =>
        $"'{GetTitle(hwnd)}' [{GetClassName(hwnd)}] pid {ProcessIdOf(hwnd)}";

    public static bool HasStyle(IntPtr hwnd, int index, long flags) => (Win32.GetWindowLongPtr(hwnd, index) & flags) != 0;

    public static bool IsCloaked(IntPtr hwnd) =>
        Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, IntSize) == 0 && cloaked != 0;

    /// <summary>Tema della barra del titolo della finestra: il nostro pulsante deve aver lo stesso colore degli altri.</summary>
    public static bool IsAppThemeDark(IntPtr hwnd)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, out int dark, IntSize) == 0)
        {
            return dark != 0;
        }

        // barra del titolo disegnata dal programma: vale il tema dei programmi di Windows
        long tick = Environment.TickCount64;
        if (tick - _themeTick > 3000)
        {
            _themeTick = tick;
            try
            {
                object? light = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", null);
                _themeDark = light is not int value || value == 0;
            }
            catch (Exception)
            {
                _themeDark = true;
            }
        }

        return _themeDark;
    }

    /// <summary>
    /// Il contorno della finestra come si vede, senza i bordi invisibili che hanno ai lati le finestre normali
    /// (una massimizzata e' otto pixel piu' larga dello schermo per parte, ma non si vede).
    /// </summary>
    public static bool TryGetVisibleFrame(IntPtr hwnd, out RECT frame) =>
        Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out frame, RectSize) == 0;

    /// <summary>
    /// I pulsanti della barra del titolo come li misura Windows, in coordinate dello schermo.
    /// Da sola questa misura non basta: vedi <see cref="FindCaptionButtons"/>.
    /// </summary>
    public static bool TryGetCaptionButtons(IntPtr hwnd, out RECT bounds)
    {
        bounds = default;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CAPTION_BUTTON_BOUNDS, out RECT raw, RectSize) != 0)
        {
            return false;
        }

        if (raw.Right <= raw.Left || raw.Bottom <= raw.Top || !Win32.GetWindowRect(hwnd, out RECT window))
        {
            return false;
        }

        // Windows li riporta rispetto alla finestra; valori oltre la larghezza sono gia' coordinate di schermo
        bounds = raw.Right <= window.Width
            ? new RECT(window.Left + raw.Left, window.Top + raw.Top, window.Left + raw.Right, window.Top + raw.Bottom)
            : raw;
        return true;
    }

    /// <summary>
    /// La misura di Windows sta almeno nell'angolo in alto a destra della finestra, dove devono stare i pulsanti?
    /// Nei programmi che si disegnano da soli la barra del titolo sono numeri senza senso.
    /// </summary>
    public static bool IsPlausibleCaptionButtons(RECT buttons, in RECT window) =>
        buttons.Width >= 20 && buttons.Width <= 320 && buttons.Height >= 12 && buttons.Height <= 60
        && buttons.Right >= window.Right - 60 && buttons.Right <= window.Right + 8
        && buttons.Top >= window.Top - 1 && buttons.Top <= window.Top + 80;

    /// <summary>
    /// Che cosa c'e' in un punto di una finestra, chiesto alla finestra stessa: e' la domanda che le fa Windows
    /// quando ci si passa sopra col mouse. Null se la finestra non risponde.
    /// </summary>
    public static int? WhatIsAt(IntPtr hwnd, int x, int y)
    {
        IntPtr point = (IntPtr)(((long)y << 16) | (ushort)x);
        return Win32.SendMessageTimeoutW(hwnd, WmNcHitTest, IntPtr.Zero, point, SmtoAbortIfHung, AnswerTimeoutMs,
            out UIntPtr result) != IntPtr.Zero
            ? unchecked((int)result.ToUInt32())
            : null;
    }

    public static bool IsCaptionButton(int hit) => hit is HitMinimize or HitMaximize or HitClose or HitHelp;

    /// <summary>La finestra risponde "qui c'e' un pulsante di sistema": vero, falso, o null se non risponde.</summary>
    private static bool? ButtonAt(IntPtr hwnd, int x, int y)
    {
        int? hit = WhatIsAt(hwnd, x, y);
        return hit is null ? null : IsCaptionButton(hit.Value);
    }

    /// <summary>
    /// Dove sono i pulsanti di sistema della barra del titolo. Nessuna fonte lo dice con fiducia, quindi se ne
    /// mescolano due (misurate sulle finestre di questo computer, vedere spike/hit_probe.py):
    /// <list type="bullet">
    /// <item>la misura di Windows (<see cref="TryGetCaptionButtons"/>): l'estremita' destra e' giusta, ma la fascia
    /// viene 8 pixel piu' larga verso sinistra, e nei programmi che si disegnano la barra da soli non vale niente;</item>
    /// <item>le risposte della finestra (<see cref="WhatIsAt"/>): esatte dove il programma si disegna i pulsanti
    /// (Chrome, Opera, Rider, le finestre moderne di Windows), ma con la cornice di Windows riporta una fascia
    /// stretta e fuori posto, e c'e' chi non risponde affatto (Terminale, Discord).</item>
    /// </list>
    /// Con <paramref name="askWindow"/> falso vale solo la misura di Windows: costa nulla, e serve come stima
    /// finche' arriva quella precisa. Null se non si riesce a dire niente, o se la finestra era troppo occupata
    /// per rispondere (<see cref="CaptionPlacement.WindowAnswered"/> falso).
    /// </summary>
    public static CaptionPlacement? FindCaptionButtons(IntPtr hwnd, bool askWindow)
    {
        if (!Win32.GetWindowRect(hwnd, out RECT window) || window.Width <= 0 || window.Height <= 0)
        {
            return null;
        }

        uint dpi = Win32.GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? 1 : dpi / 96d;
        int standard = Px(46, scale);
        if (!TryGetVisibleFrame(hwnd, out RECT frame) || frame.Right <= frame.Left || frame.Bottom <= frame.Top)
        {
            frame = window;
        }

        bool hasMeasure = HasMeasure(hwnd, window, out RECT measured);
        int anchorX = window.Right, anchorY = window.Top;

        if (askWindow)
        {
            (Answer? answer, bool silent) = AskCaptionButtons(hwnd, window, scale);
            if (silent)
            {
                return null;
            }

            // una fila di pulsanti pari dice dove sono disegnati. Se Windows non la conferma dev'essere proprio
            // uniforme: la fascia stretta della cornice di Windows e' quasi pari anch'essa
            bool agreed = hasMeasure && Math.Abs(measured.Width - answer?.Width ?? 0) <= Px(14, scale);
            if (answer is not null && answer.IsEvenRow(!hasMeasure || agreed ? Px(4, scale) : Px(1, scale)))
            {
                int right = answer.Right;
                if (agreed && Math.Abs(measured.Right - right) <= Px(16, scale))
                {
                    // le due fonti concordano: l'estremita' destra piu' precisa e' quella di Windows
                    right = Math.Abs(measured.Right - frame.Right) <= 2 ? frame.Right : measured.Right;
                }

                return new CaptionPlacement(
                    new CaptionBand(right - answer.Width, answer.Top, right, answer.Bottom, answer.Count)
                    {
                        ContentOnLeft = answer.ContentOnLeft, AnchorX = anchorX, AnchorY = anchorY,
                    },
                    WindowAnswered: true);
            }

            // la finestra risponde, ma non con una fila di pulsanti: cornice di Windows, valgono le misure qui sotto
        }

        // cornice disegnata da Windows (o finestra che non dice niente di utile): i pulsanti sono tre (quattro
        // con l'aiuto), larghi come quelli di Windows, attaccati all'angolo in alto a destra di quello che si vede
        int count = 3, height = Px(30, scale);
        int top = frame.Top, edge = frame.Right;
        if (hasMeasure)
        {
            count = Math.Clamp((int)Math.Round((measured.Width - Px(8, scale)) / (double)standard), 1, 4);
            top = Math.Max(measured.Top, frame.Top);
            height = Math.Max(measured.Height, Px(22, scale));
            edge = Math.Abs(measured.Right - frame.Right) <= 2 ? frame.Right : measured.Right;
        }

        return new CaptionPlacement(
            new CaptionBand(edge - count * standard, top, edge, top + height, count) { AnchorX = anchorX, AnchorY = anchorY },
            WindowAnswered: askWindow);
    }

    private static bool HasMeasure(IntPtr hwnd, in RECT window, out RECT measure) =>
        TryGetCaptionButtons(hwnd, out measure) && IsPlausibleCaptionButtons(measure, window);

    private static int Px(double designPixels, double scale) => (int)Math.Round(designPixels * scale);

    /// <summary>Quello che la finestra risponde sui suoi pulsanti di sistema, in coordinate dello schermo.</summary>
    private sealed record Answer(int Left, int Top, int Right, int Bottom, int Count, int Separator, int Smallest, int Largest)
    {
        public int Width => Right - Left;

        public bool ContentOnLeft { get; init; }

        /// <summary>
        /// Le risposte descrivono una fila di pulsanti (da due a quattro, senza interruzioni, tutti larghi quasi
        /// uguale): solo allora sono dove i pulsanti sono davvero disegnati.
        /// </summary>
        public bool IsEvenRow(int tolerance) =>
            Count is >= 2 and <= 4 && Separator == 0 && Width <= 320 && Smallest >= 20 && Largest - Smallest <= tolerance;
    }

    /// <summary>
    /// Chiede alla finestra, punto per punto, che cosa c'e' nell'angolo in alto a destra. Si parte tredici pixel
    /// dentro il bordo destro (in mezzo al pulsante Chiudi) e si segue quella colonna su e giu' finche' risponde
    /// "e' un pulsante": quella e' l'altezza dei pulsanti. Poi si legge la riga di mezzo da destra a sinistra
    /// finche' i pulsanti finiscono. Secondo elemento del risultato: la finestra non ha risposto (era occupata),
    /// e si e' smesso di chiedere subito invece di aspettare.
    /// </summary>
    private static (Answer? Answer, bool Silent) AskCaptionButtons(IntPtr hwnd, in RECT window, double scale)
    {
        int probe = window.Right - Math.Min(Px(13, scale), window.Width / 4);
        int lowest = window.Top + Math.Min(Px(140, scale), window.Height / 2);

        int row = -1;
        for (int y = window.Top; y < lowest && row < 0; y += 2)
        {
            switch (ButtonAt(hwnd, probe, y))
            {
                case null:
                    return (null, true);
                case true:
                    row = y;
                    break;
            }
        }

        if (row < 0)
        {
            return (null, false);    // da nessuna parte, qui, risponde "e' un pulsante"
        }

        int top = row, bottom = row;
        bool silent = false;
        for (int y = row - 1; y >= window.Top; y--)
        {
            bool? button = ButtonAt(hwnd, probe, y);
            silent |= button is null;
            if (button != true)
            {
                break;
            }

            top = y;
        }

        for (int y = row + 1; y < Math.Min(window.Bottom, row + Px(80, scale)); y++)
        {
            bool? button = ButtonAt(hwnd, probe, y);
            silent |= button is null;
            if (button != true)
            {
                break;
            }

            bottom = y;
        }

        if (silent)
        {
            return (null, true);
        }

        // la riga in mezzo ai pulsanti letta da destra a sinistra: righe[i] e' quello che c'e' a i pixel dal bordo
        // destro (null: nessuna risposta). Si chiede man mano e si ricorda, per non chiedere due volte lo stesso
        int middle = (top + bottom) / 2;
        int span = Math.Min(window.Width / 2, Px(360, scale));
        int windowRight = window.Right;
        int?[] codes = new int?[span];
        int? CodeAt(int i) => i < span ? codes[i] ??= WhatIsAt(hwnd, windowRight - 1 - i, middle) : null;

        // il bordo (invisibile) a destra dei pulsanti e' lungo al massimo una quarantina di pixel
        int first = 0;
        for (; first < Px(40, scale) && CodeAt(first) is { } side && !IsCaptionButton(side); first++)
        {
        }

        if (first >= Px(40, scale))
        {
            return (null, false);
        }

        if (CodeAt(first) is null)
        {
            return (null, true);
        }

        int count = 0, separator = 0, smallest = int.MaxValue, largest = 0, cell = 0;
        int? current = null;
        int index;
        for (index = first; index < span; index++)
        {
            int? code = CodeAt(index);
            if (code is null)
            {
                return (null, true);
            }

            if (IsCaptionButton(code.Value))
            {
                if (code == current)
                {
                    cell++;
                    continue;
                }

                // comincia un pulsante diverso: il precedente e' finito
                (count, smallest, largest) = Closed(count, smallest, largest, current, cell);
                current = code;
                cell = 1;
                continue;
            }

            // qui non c'e' un pulsante: se entro tre pixel ne ricomincia uno, e' solo una linea di separazione
            int look = 1;
            for (; look <= SeparatorMax && CodeAt(index + look) is { } next && !IsCaptionButton(next); look++)
            {
            }

            if (look > SeparatorMax || CodeAt(index + look) is null)
            {
                break;  // i pulsanti sono finiti
            }

            separator += look;
            index += look - 1;     // al prossimo giro si riparte dal pulsante che ricomincia
        }

        (count, smallest, largest) = Closed(count, smallest, largest, current, cell);

        bool content = false;
        foreach (int away in new[] { Px(2, scale), Px(8, scale), Px(16, scale) })
        {
            if (CodeAt(index + away) == HitClient)
            {
                content = true;    // subito a sinistra c'e' il contenuto del programma
                break;
            }
        }

        return (new Answer(window.Right - index, top, window.Right - first, bottom + 1, count, separator, smallest, largest)
        {
            ContentOnLeft = content,
        }, false);

        // chiude il conto di un pulsante, se ne era cominciato uno
        static (int Count, int Smallest, int Largest) Closed(int count, int smallest, int largest, int? code, int width) =>
            code is null ? (count, smallest, largest) : (count + 1, Math.Min(smallest, width), Math.Max(largest, width));
    }

    /// <summary>Si puo' parcheggiare: visibile, riducibile a icona, con una barra del titolo.</summary>
    public static bool IsParkable(IntPtr hwnd) => WhyNotParkable(hwnd).Length == 0;

    /// <summary>Vuoto se la finestra si puo' parcheggiare, altrimenti il primo motivo per cui non si puo'.</summary>
    public static string WhyNotParkable(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd))
        {
            return "non è visibile";
        }

        if (Win32.IsIconic(hwnd))
        {
            return "è ridotta a icona";
        }

        uint processId = ProcessIdOf(hwnd);
        if (processId == 0 || processId == Win32.GetCurrentProcessId())
        {
            return "è una finestra nostra";
        }

        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE);
        if ((style & Win32.WS_CHILD) != 0 || Win32.GetAncestor(hwnd, Win32.GA_ROOT) != hwnd)
        {
            return "è interna a un'altra finestra";
        }

        if ((style & Win32.WS_MINIMIZEBOX) == 0)
        {
            return "non ha Riduci a icona";
        }

        long extended = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE);
        if ((extended & (Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE)) != 0)
        {
            return "è una finestrella ausiliaria";
        }

        if ((style & Win32.WS_CAPTION) == 0 && !TryGetCaptionButtons(hwnd, out _))
        {
            return "non ha barra del titolo";
        }

        if (IsCloaked(hwnd) || ShellClasses.Contains(GetClassName(hwnd)))
        {
            return "è una finestra di sistema";
        }

        if (!Win32.GetWindowRect(hwnd, out RECT rect))
        {
            return "è appena sparita";
        }

        if (rect.Width < 200 || rect.Height < 100)
        {
            return "è troppo piccola";
        }

        return IsFullScreen(hwnd, rect) ? "è a schermo intero" : string.Empty;
    }

    /// <summary>Finestra a schermo intero: copre anche la barra delle applicazioni e non c'e' posto per il pulsante.</summary>
    private static bool IsFullScreen(IntPtr hwnd, in RECT rect)
    {
        // una finestra ingrandita copre il monitor ma non e' a schermo intero: lascia vedere la barra delle applicazioni
        if (Win32.IsZoomed(hwnd))
        {
            return false;
        }

        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!Win32.GetMonitorInfo(Win32.MonitorFromWindow(hwnd, MonitorNearest), ref info))
        {
            return false;
        }

        bool coversMonitor = rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top
                             && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
        return coversMonitor && info.Work.Height < info.Monitor.Height;
    }

    public static bool IsForegroundOrOwned(IntPtr hwnd)
    {
        IntPtr foreground = Win32.GetForegroundWindow();
        return foreground == hwnd || Win32.GetWindow(foreground, Win32.GW_OWNER) == hwnd;
    }

    public static List<IntPtr> TopLevelWindows()
    {
        var windows = new List<IntPtr>();
        Win32.EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }
}
