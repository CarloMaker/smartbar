using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using SmartBar.Infrastructure;
using SmartBar.Native;

namespace SmartBar.Core;

internal enum ParkState
{
    /// <summary>Nel parcheggio, fuori da tutti gli schermi: si vede solo l'anteprima nella barra.</summary>
    Collapsed,

    /// <summary>Sta scorrendo dentro.</summary>
    Expanding,

    /// <summary>Aperta accanto alla barra, sopra le altre finestre.</summary>
    Expanded,

    /// <summary>Sta scorrendo fuori.</summary>
    Collapsing,
}

internal sealed class ParkedWindow
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; set; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public WINDOWPLACEMENT OriginalPlacement { get; set; }
    public bool WasTopmost { get; set; }
    public ParkState State { get; set; } = ParkState.Collapsed;
    public bool IsDragging { get; set; }

    /// <summary>Aperta senza il mouse (Alt-Tab, un avviso): si richiude quando il programma passa in secondo piano.</summary>
    public bool WaitForMouse { get; set; }

    /// <summary>Con la puntina: resta aperta anche se il mouse esce.</summary>
    public bool Pinned { get; set; }

    /// <summary>
    /// Quale posto tiene da pinnata: la metà alta (0), la metà bassa (1), oppure tutta l'altezza quando e' da sola.
    /// -1 se non e' pinnata.
    /// </summary>
    public int Slot { get; set; } = -1;

    /// <summary>Ordine di puntura: serve a capire quale e' la piu' vecchia quando i posti sono finiti.</summary>
    internal int PinOrder;
    internal bool StateCheckPending;
    internal bool IsMoving;
    internal int FromX, FromY, ToX, ToY;
    internal long MoveStart;
    internal ParkState? MoveEndState;
    internal long ReparkSince;
    internal int ReparkCount;
}

/// <summary>
/// Decide dove stanno le finestre parcheggiate e quando si aprono. Non incorpora mai le finestre:
/// restano finestre normali degli altri programmi, solo spostate nel parcheggio, fuori dallo schermo.
/// </summary>
internal sealed class ParkingService : IDisposable
{
    private const int AnimationMs = 220;
    private const int SwShownoactivate = 4;    // riapre una finestra ridotta a icona dov'era, senza attivarla
    private const int SlotWholePanel = -2;     // pinnata da sola: occupa tutta l'altezza accanto alla barra
    private const int ReparkWindowMs = 10000;
    private const int MaxReparks = 20;
    private const uint MoveNoZOrder = Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOSENDCHANGING;
    private const uint ZOrderOnly = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE;
    private const uint BusyFlags = Win32.GUI_INMOVESIZE | Win32.GUI_INMENUMODE | Win32.GUI_SYSTEMMENUMODE
                                  | Win32.GUI_POPUPMENUMODE;
    private static readonly int PlacementLength = Marshal.SizeOf<WINDOWPLACEMENT>();

    private readonly Dictionary<IntPtr, ParkedWindow> _parked = new();
    private readonly TaskbarButtons _taskbar = new();
    private int _pinCounter;

    /// <summary>Una finestra è entrata o uscita dal parcheggio.</summary>
    public event Action? Changed;

    /// <summary>Una finestra parcheggiata ha cambiato stato, titolo o puntina.</summary>
    public event Action? Updated;

    public IReadOnlyCollection<ParkedWindow> Parked => _parked.Values;

    public bool HasExpanded => _parked.Values.Any(window => window.State != ParkState.Collapsed);

    public bool AnyPinned => _parked.Values.Any(window => window.Pinned);

    public bool IsParked(IntPtr hwnd) => _parked.ContainsKey(hwnd);

    /// <summary>Toglie la finestra dallo schermo e la mette nella barra. Falso se il programma lo rifiuta.</summary>
    public bool Park(IntPtr hwnd)
    {
        if (_parked.ContainsKey(hwnd))
        {
            return true;
        }

        // prima di scegliere il posto: se nel frattempo sono cambiati gli schermi, vale la misura nuova
        AdaptToScreens();

        if (!WindowTools.IsParkable(hwnd))
        {
            Log.Warn($"Non parcheggiabile {WindowTools.Describe(hwnd)}: {WindowTools.WhyNotParkable(hwnd)}");
            return false;
        }

        // con "--only" si parcheggiano solo le finestre dei processi indicati: le prove automatiche non devono
        // poter toccare le finestre dell'utente, nemmeno per sbaglio
        if (!Runtime.IsAllowedProcess(WindowTools.ProcessIdOf(hwnd)))
        {
            return false;
        }

        var clock = Stopwatch.StartNew();
        Step(clock, "controllo se si puo' parcheggiare");
        var placement = new WINDOWPLACEMENT { Length = PlacementLength };
        _ = Win32.GetWindowPlacement(hwnd, ref placement);
        bool wasTopmost = WindowTools.HasStyle(hwnd, Win32.GWL_EXSTYLE, Win32.WS_EX_TOPMOST);
        bool wasMaximized = placement.ShowCmd == Win32.SW_SHOWMAXIMIZED || Win32.IsZoomed(hwnd);

        if (wasMaximized)
        {
            // una finestra massimizzata non si lascia né spostare né ridimensionare
            _ = Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
            placement.ShowCmd = Win32.SW_SHOWMAXIMIZED;
        }

        // se era la finestra in primo piano, il primo piano passa a quella che sta sotto: la parcheggiata resta
        // aperta ma invisibile, e la tastiera non deve scrivere in una finestra che non si vede.
        // Se non lo era, il primo piano non si tocca
        if (Win32.GetForegroundWindow() == hwnd)
        {
            IntPtr next = FindWindowToActivate(hwnd);
            _ = Win32.SetForegroundWindow(next != IntPtr.Zero ? next : Win32.GetShellWindow());
        }

        // resta della dimensione che ha: ridimensionata qui, fuori schermo, il programma non la ridisegnerebbe
        // e l'anteprima resterebbe nera. Le dimensioni del posto le prende quando si apre.
        if (!MoveToParking(hwnd))
        {
            Log.Warn($"Spostamento rifiutato parcheggiando {WindowTools.Describe(hwnd)}: errore {Marshal.GetLastWin32Error()}");
            return false;
        }

        Step(clock, "passaggio del primo piano e spostamento oltre il bordo");
        if (!wasTopmost)
        {
            _ = Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, ZOrderOnly);
        }

        Step(clock, "ordine delle finestre");
        _taskbar.Hide(hwnd);
        Step(clock, "pulsante nella barra delle applicazioni");
        _parked[hwnd] = new ParkedWindow
        {
            Hwnd = hwnd,
            Title = WindowTools.GetTitle(hwnd),
            ProcessName = WindowTools.GetProcessName(hwnd) ?? string.Empty,
            OriginalPlacement = placement,
            WasTopmost = wasTopmost,
        };
        Log.Info($"Parcheggiata {WindowTools.Describe(hwnd)}");
        RaiseChanged();
        // qui dentro c'e' anche la prima comparsa della barra e la rassegna dei pulsanti sulle finestre
        Step(clock, "aggiornamento della barra dopo un parcheggio");
        return true;
    }

    /// <summary>
    /// Apre la finestra facendola scorrere fuori dal bordo dove sta la barra. Senza puntina occupa tutto lo spazio
    /// accanto alla barra; se c'e' gia' una finestra pinnata si apre nell'altro posto, per non coprirla.
    /// </summary>
    public void Expand(ParkedWindow window, bool waitForMouse = false)
    {
        if (window.State is ParkState.Expanding or ParkState.Expanded || window.IsDragging)
        {
            return;
        }

        // senza puntina se ne apre una sola alla volta
        foreach (ParkedWindow other in _parked.Values.ToList())
        {
            if (other != window && !other.Pinned && other.State != ParkState.Collapsed)
            {
                Collapse(other);
            }
        }

        // prima di scegliere il posto: se e' rimasta una sola pinnata a meta' altezza perche' l'altra e' andata via,
        // torna a tutta l'altezza
        ReflowPinned();

        Rectangle target;
        if (window.Pinned)
        {
            // una pinnata sta dove l'hanno messa: a tutta l'altezza o in una delle due metà
            target = window.Slot >= 0 ? ScreenGeometry.GetSlot(window.Slot) : ScreenGeometry.PanelRect;
        }
        else if (AnyPinned)
        {
            // se c'e' un posto libero si apre li'. Se i posti sono tutti presi, o l'unica pinnata occupa tutta
            // l'altezza, si apre sopra: ma solo finche' il mouse resta qui
            window.Slot = FreeSlot();
            target = window.Slot >= 0 ? ScreenGeometry.GetSlot(window.Slot) : ScreenGeometry.PanelRect;
        }
        else
        {
            window.Slot = -1;
            target = ScreenGeometry.PanelRect;
        }

        ExpandTo(window, target, waitForMouse);
    }

    /// <summary>Richiude la finestra nella barra. Una finestra richiusa non e' piu' pinnata.</summary>
    public void Collapse(ParkedWindow window)
    {
        // se e' gia' in corso non va riavviata: rifarla da capo a ogni passaggio non la farebbe finire mai
        if (window.State is ParkState.Collapsed or ParkState.Collapsing || window.IsDragging)
        {
            return;
        }

        if (window.Pinned)
        {
            Unpin(window);
        }

        if (!Win32.GetWindowRect(window.Hwnd, out RECT current))
        {
            return;
        }

        window.WaitForMouse = false;

        // scorre fuori dal lato da cui e' entrata: di lato, o verso il basso (l'alto)
        Point spot = ScreenGeometry.ParkSpot(current.Width, current.Height);
        StartMove(window, current.Left, current.Top,
            ScreenGeometry.SlidesSideways ? spot.X : current.Left,
            ScreenGeometry.SlidesSideways ? current.Top : spot.Y, ParkState.Collapsing, ParkState.Collapsed);

        // se con questa che se ne va e' rimasta una sola pinnata a meta' altezza, torna a tutta l'altezza
        ReflowPinned();
    }

    /// <summary>Richiude le finestre aperte senza puntina.</summary>
    public void CollapseAll()
    {
        foreach (ParkedWindow window in _parked.Values.ToList())
        {
            if (!window.Pinned)
            {
                Collapse(window);
            }
        }
    }

    /// <summary>
    /// Gli schermi sono cambiati mentre l'app girava (uno si e' acceso, staccato, spostato): rilegge le misure e
    /// rimette al suo posto, con quelle nuove, ogni finestra parcheggiata. Senza, una finestra parcheggiata oltre il
    /// bordo di uno schermo puo' ritrovarsi in mezzo a uno schermo appena comparso. Vero se ha rifatto qualcosa.
    /// </summary>
    public bool AdaptToScreens()
    {
        if (!ScreenGeometry.EnsureCurrent())
        {
            return false;
        }

        Log.Info($"Cambiati gli schermi: la barra sta sul bordo {BarName()} di {ScreenGeometry.HomeBounds}, "
                 + $"le finestre parcheggiate {SideName(ScreenGeometry.Side)}");
        PutParkedInPlace();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Sposta la barra sull'altro lato dello schermo: la scelta resta salvata. La barra, lo spazio delle finestre
    /// aperte e il parcheggio si ribaltano, e le finestre parcheggiate si spostano senza farle scorrevolere.
    /// </summary>
    public void MoveBar(BarSide side)
    {
        if (Settings.Side == side)
        {
            return;
        }

        Settings.Set(side);
        ScreenGeometry.Refresh();
        Log.Info($"Barra spostata sul bordo {BarName()} di {ScreenGeometry.HomeBounds}: "
                 + $"le finestre parcheggiate {SideName(ScreenGeometry.Side)}");
        PutParkedInPlace();
        RaiseChanged();
    }

    private static string BarName() => Settings.Side == BarSide.Left ? "sinistro" : "destro";

    /// <summary>Ogni finestra parcheggiata va dove le dicono le misure di adesso, senza animazioni.</summary>
    private void PutParkedInPlace()
    {
        foreach (ParkedWindow window in _parked.Values.ToList())
        {
            if (window.IsDragging)
            {
                continue;    // l'utente la sta trascinando: si sistema quando la lascia
            }

            if (!Win32.IsWindow(window.Hwnd))
            {
                Remove(window);
                continue;
            }

            // un movimento in corso si conclude qui, senza farlo scorrere fino al posto che non vale piu'
            window.IsMoving = false;
            window.MoveEndState = null;

            if (window.State is ParkState.Expanded or ParkState.Expanding)
            {
                Rectangle target = ExpectedRect(window);
                _ = Win32.SetWindowPos(window.Hwnd, Win32.HWND_TOPMOST, target.Left, target.Top, target.Width,
                    target.Height, Win32.SWP_NOACTIVATE | Win32.SWP_NOSENDCHANGING | Win32.SWP_NOOWNERZORDER);
                window.State = ParkState.Expanded;
            }
            else
            {
                _ = MoveToParking(window.Hwnd);
                FinishMove(window, ParkState.Collapsed);
            }
        }
    }

    /// <summary>Mette la puntina a una finestra parcheggiata, o la toglie se ce l'ha gia'.</summary>
    public void TogglePin(ParkedWindow window)
    {
        if (window.Pinned)
        {
            Unpin(window);
            return;
        }

        Pin(window);
    }

    /// <summary>Avanza di un fotogramma le finestre che stanno scorrendo. Da chiamare circa 60 volte al secondo.</summary>
    public void TickAnimations()
    {
        long now = Stopwatch.GetTimestamp();
        foreach (ParkedWindow window in _parked.Values.ToList())
        {
            if (!window.IsMoving)
            {
                continue;
            }

            if (window.IsDragging)
            {
                continue;
            }

            if (!Win32.IsWindow(window.Hwnd))
            {
                Remove(window);
                continue;
            }

            float progress = Math.Clamp((float)((now - window.MoveStart) * 1000.0 / Stopwatch.Frequency) / AnimationMs, 0f, 1f);
            float eased = 1f - MathF.Pow(1f - progress, 3f);
            int x = (int)Math.Round(window.FromX + (window.ToX - window.FromX) * eased);
            int y = (int)Math.Round(window.FromY + (window.ToY - window.FromY) * eased);
            if (Win32.GetWindowRect(window.Hwnd, out RECT current) && (x != current.Left || y != current.Top))
            {
                _ = Win32.SetWindowPos(window.Hwnd, IntPtr.Zero, x, y, 0, 0, MoveNoZOrder | Win32.SWP_NOSIZE);
            }

            if (progress >= 1f)
            {
                FinishMove(window, window.MoveEndState);
            }
        }
    }

    /// <summary>Toglie la finestra dal parcheggio. Con <paramref name="restoreOriginal"/> torna dov'era.</summary>
    public void Unpark(ParkedWindow window, bool restoreOriginal)
    {
        if (!_parked.Remove(window.Hwnd))
        {
            return;
        }

        IntPtr hwnd = window.Hwnd;
        if (Win32.IsWindow(hwnd))
        {
            if (!window.WasTopmost)
            {
                _ = Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, ZOrderOnly);
            }

            if (restoreOriginal)
            {
                WINDOWPLACEMENT placement = window.OriginalPlacement;
                placement.Length = PlacementLength;
                placement.ShowCmd = placement.ShowCmd == Win32.SW_SHOWMAXIMIZED ? Win32.SW_SHOWMAXIMIZED : Win32.SW_SHOWNA;
                WindowTools.PutBack(hwnd, ref placement);
                if (Win32.IsIconic(hwnd))
                {
                    _ = Win32.ShowWindow(hwnd, SwShownoactivate);
                }
            }

            _taskbar.Show(hwnd);
        }

        ReflowPinned();
        Log.Info($"Ripresa {WindowTools.Describe(hwnd)}");
        RaiseChanged();
    }

    public void RestoreAll()
    {
        foreach (ParkedWindow window in _parked.Values.ToList())
        {
            Unpark(window, restoreOriginal: true);
        }
    }

    public void HandleWinEvent(WinEvent e)
    {
        if (_parked.TryGetValue(e.Hwnd, out ParkedWindow? window))
        {
            HandleParkedEvent(window, e);
            return;
        }

        // un programma parcheggiato apre un avviso o un dialogo: la sua finestra si apre da sola, altrimenti
        // l'avviso comparirebbe oltre il bordo e resterebbe invisibile
        if (e.Id == WinEventIds.EVENT_OBJECT_SHOW && Win32.IsWindowVisible(e.Hwnd))
        {
            IntPtr owner = Win32.GetWindow(e.Hwnd, Win32.GW_OWNER);
            if (owner != IntPtr.Zero && _parked.TryGetValue(owner, out ParkedWindow? parkedOwner)
                                     && parkedOwner.State == ParkState.Collapsed)
            {
                Log.Info($"Si apre da sola per un avviso: {WindowTools.Describe(owner)}");
                Expand(parkedOwner, waitForMouse: true);
            }
        }
    }

    /// <summary>
    /// Il mouse è ancora nell'area che tiene aperte le finestre senza puntina (le finestre aperte, i loro menu
    /// e dialoghi)? La barra e la scheda le controlla chi chiama.
    /// </summary>
    public bool IsCursorInsideActiveArea(Point cursor)
    {
        var open = _parked.Values.Where(window => window.State != ParkState.Collapsed && !window.Pinned).ToList();
        foreach (ParkedWindow window in open)
        {
            if (Win32.GetWindowRect(window.Hwnd, out RECT rect) && rect.Contains(cursor.X, cursor.Y))
            {
                return true;
            }
        }

        // fuori dalle finestre: non chiudere mentre c'e' un menu, un dialogo o una selezione col mouse in corso
        foreach (ParkedWindow window in open)
        {
            if (HasVisibleOwnedWindow(window.Hwnd) || IsThreadBusy(window.Hwnd))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Guarda a stato stabile le finestre parcheggiate i cui eventi di cambio stato sono appena arrivati.
    /// Da chiamare a ogni passaggio del ciclo.
    /// </summary>
    public void SettleStates()
    {
        foreach (ParkedWindow window in _parked.Values.ToList())
        {
            if (!window.StateCheckPending)
            {
                continue;
            }

            window.StateCheckPending = false;
            if (!Win32.IsWindow(window.Hwnd))
            {
                continue;
            }

            if (Win32.IsIconic(window.Hwnd))
            {
                // "Riduci a icona" su una finestra parcheggiata vuol dire "richiudila nella barra":
                // ridotta a icona davvero l'anteprima sparisce
                Log.Info($"Ridotta a icona, la richiudo nella barra: {WindowTools.Describe(window.Hwnd)}");
                ShowWithoutActivation(window.Hwnd);
                Collapse(window);
            }
            else if (Win32.IsZoomed(window.Hwnd) && window.State != ParkState.Collapsed)
            {
                // "Ingrandisci": occupa tutto lo schermo, quindi esce dal parcheggio e resta una finestra normale
                Log.Info($"Ingrandita, esce dal parcheggio: {WindowTools.Describe(window.Hwnd)}");
                Unpark(window, restoreOriginal: false);
            }
        }
    }

    /// <summary>
    /// Ritrova le finestre parcheggiate da un'esecuzione precedente e le rimette nella barra.
    /// Con <paramref name="keepUnfound"/> quelle non trovate restano nell'elenco per un secondo tentativo
    /// (un programma ancora in avvio non ha ancora creato la sua finestra).
    /// </summary>
    public int RecoverFromStore(bool keepUnfound)
    {
        var clock = Stopwatch.StartNew();
        List<SavedWindow> saved = ParkedStore.Load();
        Step(clock, "lettura dell'elenco salvato");
        if (saved.Count == 0)
        {
            return 0;
        }

        var unfound = new List<SavedWindow>();
        List<IntPtr> windows = WindowTools.TopLevelWindows();
        int recovered = 0;
        foreach (SavedWindow item in saved)
        {
            IntPtr hwnd = ParkedOrphanRecovery.Find(item, windows);
            Step(clock, "ricerca della finestra da riprendere");
            if (hwnd == IntPtr.Zero)
            {
                Log.Info($"Non trovo (ancora) la finestra parcheggiata '{item.Title}' ({item.ProcessName})");
                unfound.Add(item);
                continue;
            }

            if (_parked.ContainsKey(hwnd))
            {
                continue;    // gia' ripresa al primo tentativo
            }

            _ = MoveToParking(hwnd);
            Step(clock, "spostamento della finestra nel parcheggio");
            if (!item.WasTopmost)
            {
                _ = Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, ZOrderOnly);
            }

            Step(clock, "ordine delle finestre");
            _taskbar.Hide(hwnd);
            Step(clock, "pulsante nella barra delle applicazioni");
            _parked[hwnd] = new ParkedWindow
            {
                Hwnd = hwnd,
                Title = WindowTools.GetTitle(hwnd),
                ProcessName = item.ProcessName,
                WasTopmost = item.WasTopmost,
                OriginalPlacement = new WINDOWPLACEMENT
                {
                    Length = PlacementLength,
                    ShowCmd = item.ShowCmd,
                    NormalPosition = new RECT(item.Left, item.Top, item.Right, item.Bottom),
                },
            };
            recovered++;
        }

        Log.Info($"Recuperate {recovered} finestre parcheggiate da un'esecuzione precedente");
        RaiseChanged();
        // qui dentro parte anche la prima rassegna delle finestre, che crea i pulsanti: e' il passo che pesa
        Step(clock, "barra e primi pulsanti sulle finestre");
        if (keepUnfound)
        {
            ParkedStore.Save(_parked.Values.Select(ToSaved).Concat(unfound));
        }

        return recovered;
    }

    /// <summary>Nel log i passi lenti di un'operazione: si vede dove si perde tempo senza riempirlo quando va bene.</summary>
    private static void Step(Stopwatch clock, string step)
    {
        if (clock.ElapsedMilliseconds >= 100)
        {
            Log.Info($"Lento, {step}: {clock.ElapsedMilliseconds} ms");
        }

        clock.Restart();
    }

    public void Dispose() => _taskbar.Dispose();

    private void Pin(ParkedWindow window)
    {
        var others = _parked.Values.Where(other => other.Pinned && other != window).ToList();
        ParkedWindow? evicted = null;
        int slot;
        Rectangle target;

        if (others.Count == 0)
        {
            // e' l'unica pinnata: resta com'e', a tutta l'altezza
            slot = SlotWholePanel;
            target = ScreenGeometry.PanelRect;
        }
        else
        {
            slot = FreeSlot();
            if (slot >= 0)
            {
                target = ScreenGeometry.GetSlot(slot);
            }
            else if (others.Count == 1 && others[0].Slot == SlotWholePanel)
            {
                // l'altra occupa tutta l'altezza: le resta la metà alta e questa va in quella bassa
                others[0].Slot = 0;
                MoveTo(others[0], ScreenGeometry.GetSlot(0));
                Log.Info($"'{others[0].Title}' lascia la metà bassa a '{window.Title}'");
                slot = 1;
                target = ScreenGeometry.GetSlot(1);
            }
            else
            {
                // i posti sono tutti e due presi: la puntina passa dalla pinnata da piu' tempo a questa
                evicted = others.OrderBy(other => other.PinOrder).First();
                slot = evicted.Slot;
                target = ScreenGeometry.GetSlot(slot);
                Log.Info($"La puntina passa da '{evicted.Title}' a '{window.Title}'");
            }
        }

        window.Pinned = true;
        window.Slot = slot;
        window.PinOrder = ++_pinCounter;
        window.WaitForMouse = false;
        Log.Info($"Pinnata '{window.Title}' " + PlaceName(slot));

        if (window.State is ParkState.Collapsed or ParkState.Collapsing)
        {
            ExpandTo(window, target, waitForMouse: false);
        }
        else
        {
            MoveTo(window, target);
        }

        if (evicted is not null)
        {
            // solo ora, con la nuova gia' pinnata: cosi' l'altra pinnata non si allarga a tutta altezza per niente
            Unpin(evicted);
            Collapse(evicted);
        }

        Updated?.Invoke();
    }

    private void Unpin(ParkedWindow window)
    {
        window.Pinned = false;
        window.Slot = -1;
        Log.Info($"Tolta la puntina a '{window.Title}'");
        Updated?.Invoke();
    }

    private int FreeSlot()
    {
        // una pinnata a tutta l'altezza occupa tutti e due i posti
        if (_parked.Values.Any(window => window.Pinned && window.Slot == SlotWholePanel))
        {
            return -1;
        }

        for (int slot = 0; slot < ScreenGeometry.SlotCount; slot++)
        {
            if (!_parked.Values.Any(window => window.Pinned && window.Slot == slot))
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>
    /// Rimette in ordine le pinnate: se ne e' rimasta una sola, a meta' altezza perche' l'altra e' andata via,
    /// torna a tutta l'altezza.
    /// </summary>
    private void ReflowPinned()
    {
        var pinned = _parked.Values.Where(window => window.Pinned).ToList();
        if (pinned.Count != 1 || pinned[0].Slot == SlotWholePanel || pinned[0].State == ParkState.Collapsed)
        {
            return;
        }

        pinned[0].Slot = SlotWholePanel;
        Log.Info($"'{pinned[0].Title}' e' rimasta sola con la puntina: torna a tutta l'altezza");
        MoveTo(pinned[0], ScreenGeometry.PanelRect);
    }

    /// <summary>Porta una finestra gia' aperta in un altro posto: le dimensioni subito, poi scorre fin li.</summary>
    private void MoveTo(ParkedWindow window, Rectangle target)
    {
        if (!Win32.GetWindowRect(window.Hwnd, out RECT current))
        {
            return;
        }

        // le dimensioni subito: ridimensionandola mentre scorre il contenuto saltellerebbe
        _ = Win32.SetWindowPos(window.Hwnd, IntPtr.Zero, current.Left, current.Top, target.Width, target.Height, MoveNoZOrder);
        StartMove(window, current.Left, current.Top, target.Left, target.Top, window.State,
            window.State == ParkState.Expanding ? ParkState.Expanded : null);
    }

    private static string PlaceName(int slot) => slot switch
    {
        SlotWholePanel => "a tutta l'altezza",
        0 => "nella metà alta",
        _ => "nella metà bassa",
    };

    private void ExpandTo(ParkedWindow window, Rectangle target, bool waitForMouse)
    {
        if (Win32.IsIconic(window.Hwnd))
        {
            ShowWithoutActivation(window.Hwnd);
        }

        if (!Win32.GetWindowRect(window.Hwnd, out RECT current))
        {
            return;
        }

        window.WaitForMouse = waitForMouse;

        // le dimensioni le prende subito: ridimensionandola mentre scorre il contenuto saltellerebbe.
        // Se arriva dal parcheggio prende subito anche la posizione sull'altro asse, per non attraversare lo schermo
        // in diagonale: chi entra di lato si mette subito all'altezza giusta, chi sale (o scende) alla larghezza giusta
        bool fromParking = window.State == ParkState.Collapsed;
        bool sideways = ScreenGeometry.SlidesSideways;
        int startX = !sideways && fromParking ? target.Left : current.Left;
        int startY = sideways && fromParking ? target.Top : current.Top;
        _ = Win32.SetWindowPos(window.Hwnd, Win32.HWND_TOPMOST, startX, startY, target.Width, target.Height,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOSENDCHANGING | Win32.SWP_NOOWNERZORDER);
        StartMove(window, startX, startY, target.Left, target.Top, ParkState.Expanding, ParkState.Expanded);
    }

    private static string SideName(ParkSide side) => side switch
    {
        ParkSide.Right => "oltre il bordo destro",
        ParkSide.Left => "oltre il bordo sinistro",
        ParkSide.Below => "sotto il bordo inferiore",
        ParkSide.Above => "sopra il bordo superiore",
        ParkSide.FarLeft => "oltre il bordo sinistro dell'ultimo schermo",
        _ => "oltre il bordo destro dell'ultimo schermo",
    };

    /// <summary>Fa scorrere la finestra da un punto a un altro. <paramref name="after"/>: lo stato a fine corsa.</summary>
    private void StartMove(ParkedWindow window, int fromX, int fromY, int toX, int toY, ParkState during, ParkState? after)
    {
        if (window.IsDragging)
        {
            return;
        }

        window.State = during;
        if (fromX == toX && fromY == toY)
        {
            // gia' al suo posto: non c'e' niente da far scorrere
            FinishMove(window, after);
            return;
        }

        window.IsMoving = true;
        window.FromX = fromX;
        window.FromY = fromY;
        window.ToX = toX;
        window.ToY = toY;
        window.MoveStart = Stopwatch.GetTimestamp();
        window.MoveEndState = after;
        Updated?.Invoke();
    }

    private void FinishMove(ParkedWindow window, ParkState? after)
    {
        window.IsMoving = false;
        window.MoveEndState = null;
        if (after == ParkState.Collapsed)
        {
            if (!window.WasTopmost)
            {
                _ = Win32.SetWindowPos(window.Hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, ZOrderOnly);
            }

            window.State = ParkState.Collapsed;
        }
        else if (after == ParkState.Expanded)
        {
            window.State = ParkState.Expanded;

            // a fine corsa sta esattamente nel suo posto. Se il programma si e' ridimensionato da solo mentre
            // arrivava (per esempio per un cambio di scala passando da uno schermo all'altro), lo si rimette
            Rectangle target = ExpectedRect(window);
            if (Win32.GetWindowRect(window.Hwnd, out RECT rect)
                && (Math.Abs(rect.Left - target.Left) > 1 || Math.Abs(rect.Top - target.Top) > 1
                    || rect.Width != target.Width || rect.Height != target.Height))
            {
                _ = Win32.SetWindowPos(window.Hwnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height, MoveNoZOrder);
            }
        }

        Updated?.Invoke();
    }

    private void HandleParkedEvent(ParkedWindow window, WinEvent e)
    {
        switch (e.Id)
        {
            case WinEventIds.EVENT_SYSTEM_MOVESIZESTART:
                window.IsDragging = true;
                break;

            case WinEventIds.EVENT_SYSTEM_MOVESIZEEND:
                window.IsDragging = false;

                // trascinata fuori dallo spazio che le spetta: torna una finestra normale, dove l'utente l'ha lasciata
                if (window.State != ParkState.Collapsed && !IsWhereExpected(window))
                {
                    Unpark(window, restoreOriginal: false);
                }

                break;

            case WinEventIds.EVENT_OBJECT_DESTROY:
            case WinEventIds.EVENT_OBJECT_HIDE:
                Remove(window);
                break;

            case WinEventIds.EVENT_OBJECT_STATECHANGE:
                // l'evento arriva prima che la riduzione a icona o l'ingrandimento siano fatti sul serio:
                // se ne occupa il ciclo al passaggio successivo, a stato stabile
                window.StateCheckPending = true;
                break;

            case WinEventIds.EVENT_SYSTEM_FOREGROUND:
                // scelta con Alt-Tab o dalla barra delle applicazioni
                if (window.State == ParkState.Collapsed)
                {
                    Expand(window, waitForMouse: true);
                }

                break;

            case WinEventIds.EVENT_OBJECT_LOCATIONCHANGE:
                window.StateCheckPending = true;

                // ridotta a icona una finestra sta a (-32000, -32000): non e' tornata a schermo
                if (window.State == ParkState.Collapsed && !window.IsDragging && !Win32.IsIconic(window.Hwnd)
                    && Win32.GetWindowRect(window.Hwnd, out RECT rect) && ScreenGeometry.IsOnScreen(rect))
                {
                    KeepParked(window);
                }

                break;

            case WinEventIds.EVENT_OBJECT_NAMECHANGE:
                window.Title = WindowTools.GetTitle(window.Hwnd);
                Updated?.Invoke();
                break;
        }
    }

    /// <summary>Il posto che la finestra dovrebbe occupare: il suo posto da pinnata, oppure tutto lo spazio.</summary>
    private static Rectangle ExpectedRect(ParkedWindow window) =>
        window.Slot >= 0 ? ScreenGeometry.GetSlot(window.Slot) : ScreenGeometry.PanelRect;

    private static bool IsWhereExpected(ParkedWindow window)
    {
        Rectangle expected = ExpectedRect(window);
        return Win32.GetWindowRect(window.Hwnd, out RECT rect)
               && Math.Abs(rect.Left - expected.Left) <= 8 && Math.Abs(rect.Top - expected.Top) <= 8;
    }

    /// <summary>Il programma si è rimesso a schermo da solo: torna oltre il bordo. Se insiste, si arrende.</summary>
    private void KeepParked(ParkedWindow window)
    {
        long now = Stopwatch.GetTimestamp();
        if ((now - window.ReparkSince) * 1000.0 / Stopwatch.Frequency > ReparkWindowMs)
        {
            window.ReparkSince = now;
            window.ReparkCount = 0;
        }

        if (++window.ReparkCount > MaxReparks)
        {
            Log.Warn($"{WindowTools.Describe(window.Hwnd)} si rimette a schermo da solo: la tolgo dal parcheggio");
            Unpark(window, restoreOriginal: true);
            return;
        }

        _ = MoveToParking(window.Hwnd);
    }

    /// <summary>
    /// Porta la finestra nel parcheggio senza cambiarle la misura (resta della dimensione che ha: ridimensionata
    /// fuori schermo, il programma non la ridisegnerebbe e l'anteprima resterebbe nera). Falso se non ci riesce.
    /// </summary>
    private static bool MoveToParking(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out RECT rect))
        {
            return false;
        }

        Point spot = ScreenGeometry.ParkSpot(rect.Width, rect.Height);
        return Win32.SetWindowPos(hwnd, IntPtr.Zero, spot.X, spot.Y, 0, 0,
            MoveNoZOrder | Win32.SWP_NOSIZE | Win32.SWP_NOOWNERZORDER);
    }

    private void Remove(ParkedWindow window)
    {
        _ = _parked.Remove(window.Hwnd);
        Log.Info($"Sparita {WindowTools.Describe(window.Hwnd)}");
        ReflowPinned();
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        ParkedStore.Save(_parked.Values.Select(ToSaved));
        Changed?.Invoke();
    }

    private static SavedWindow ToSaved(ParkedWindow window)
    {
        RECT normal = window.OriginalPlacement.NormalPosition;
        return new SavedWindow(window.ProcessName, window.Title, window.OriginalPlacement.ShowCmd,
            normal.Left, normal.Top, normal.Right, normal.Bottom, window.WasTopmost);
    }

    /// <summary>Riapre una finestra ridotta a icona dov'era, senza attivarla.</summary>
    private static void ShowWithoutActivation(IntPtr hwnd) =>
        _ = Win32.ShowWindow(hwnd, SwShownoactivate);

    private static IntPtr FindWindowToActivate(IntPtr hwnd)
    {
        for (IntPtr below = Win32.GetWindow(hwnd, Win32.GW_HWNDNEXT); below != IntPtr.Zero;
             below = Win32.GetWindow(below, Win32.GW_HWNDNEXT))
        {
            if (IsActivatable(below))
            {
                return below;
            }
        }

        return IntPtr.Zero;
    }

    private static bool IsActivatable(IntPtr hwnd)
    {
        if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd) || WindowTools.IsCloaked(hwnd))
        {
            return false;
        }

        if (WindowTools.HasStyle(hwnd, Win32.GWL_EXSTYLE, Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE))
        {
            return false;
        }

        uint processId = WindowTools.ProcessIdOf(hwnd);
        return processId != 0 && processId != Win32.GetCurrentProcessId() && WindowTools.GetTitle(hwnd).Length > 0;
    }

    private static bool HasVisibleOwnedWindow(IntPtr hwnd)
    {
        bool found = false;
        Win32.EnumWindows((candidate, _) =>
        {
            if (Win32.IsWindowVisible(candidate) && Win32.GetWindow(candidate, Win32.GW_OWNER) == hwnd)
            {
                found = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Il thread del programma ha un menu aperto o ha preso il mouse (selezione in corso).</summary>
    private static bool IsThreadBusy(IntPtr hwnd)
    {
        var info = new GUITHREADINFO { Size = Marshal.SizeOf<GUITHREADINFO>() };
        if (!Win32.GetGUIThreadInfo(WindowTools.ThreadIdOf(hwnd), ref info))
        {
            return false;
        }

        return (info.Flags & BusyFlags) != 0 || info.Capture != IntPtr.Zero || info.MenuOwner != IntPtr.Zero;
    }
}
