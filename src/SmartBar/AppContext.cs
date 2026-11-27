using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using SmartBar.Core;
using SmartBar.Infrastructure;
using SmartBar.Native;
using SmartBar.UI;

namespace SmartBar;

/// <summary>Coordina i pezzi: ogni 15 millisecondi guarda dove sta il mouse e decide cosa aprire e richiudere.</summary>
internal sealed class AppContext : ApplicationContext
{
    private const int DwellMs = 250;    // quanto fermarsi su una tessera per aprire la finestra
    private const int LeaveMs = 200;    // quanto stare fuori dall'area attiva prima di richiudere
    private const int RecoverDelayMs = 1500;

    private readonly ParkingService _service = new();
    private readonly RailForm _rail;
    private readonly CardForm _card;
    private readonly CaptionButtonManager _buttons;
    private readonly WinEventHook _hook = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 15 };
    private readonly System.Windows.Forms.Timer _sweep = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _recover = new() { Interval = RecoverDelayMs };
    private readonly NotifyIcon _tray;
    private readonly Icon _trayIcon = Drawing.MakeTrayIcon();
    private ParkedWindow? _dwellTile;
    private long _dwellSince;
    private long _outsideSince;
    private bool _outside;
    private bool _disposed;
    private bool _frozen;

    public AppContext()
    {
        var clock = Stopwatch.StartNew();

        _rail = new RailForm(_service);
        _rail.Prepare();
        _card = new CardForm(_rail);
        _buttons = new CaptionButtonManager(_service.IsParked);
        _buttons.ParkRequested += OnParkRequested;
        _rail.PreviewClicked += _service.TogglePin;
        _card.PreviewClicked += _service.TogglePin;
        _rail.PreviewReleased += window => ReleaseWindow(window, bringToFront: true);
        _card.PreviewReleased += window => ReleaseWindow(window, bringToFront: true);
        _service.Changed += OnParkedChanged;
        _service.Updated += () => _card.Invalidate();
        _hook.Raised += OnWinEvent;
        _rail.DisplaysChanged += OnDisplaysChanged;
        CommandServer.Start(HandleCommand);
        StartupStep("finestre dell'app", clock);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Ripristina tutte le finestre", null, (_, _) => RestoreAll());
        menu.Items.Add(new ToolStripSeparator());

        // dove sta la barra: la scelta resta salvata; il segno di spunta mostra quella in corso
        var barOnLeft = new ToolStripMenuItem("Sul lato sinistro", null, (_, _) => MoveBar(BarSide.Left));
        var barOnRight = new ToolStripMenuItem("Sul lato destro", null, (_, _) => MoveBar(BarSide.Right));
        var barMenu = new ToolStripMenuItem("Barra");
        barMenu.DropDownItems.Add(barOnLeft);
        barMenu.DropDownItems.Add(barOnRight);
        menu.Items.Add(barMenu);
        menu.Opening += (_, _) =>
        {
            bool left = Settings.Side == BarSide.Left;
            barOnLeft.Checked = left;
            barOnRight.Checked = !left;
        };

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Esci", null, (_, _) => ExitApp());
        _tray = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "SmartBar",
            ContextMenuStrip = menu,
            Visible = Runtime.Suffix.Length == 0,    // l'istanza di prova non si fa vedere
        };
        StartupStep("icona di notifica", clock);

        _tick.Tick += OnTick;
        _tick.Start();
        _sweep.Tick += (_, _) =>
        {
            _buttons.Sweep();

            // rete di sicurezza per gli schermi cambiati senza che Windows avvertisse (per esempio: si e' solo
            // spostato uno). Costa poco: guarda cinque numeri e fa qualcosa solo se sono cambiati
            AdaptToScreens("nella rassegna");
        };
        _sweep.Start();
        _recover.Tick += OnRecover;
        _recover.Start();

        if (!_hook.IsAlive)
        {
            Log.Warn("Eventi delle finestre non disponibili: i pulsanti non seguiranno le finestre.");
        }

        // le finestre rimaste parcheggiate da un'esecuzione precedente si riprendono subito
        _service.RecoverFromStore(keepUnfound: true);
        StartupStep("finestre parcheggiate da riprendere", clock);
        _buttons.Sweep();
        StartupStep("pulsanti sulle finestre", clock);
        Log.Info("Pronta.");
    }

    /// <summary>Nel log i passi lenti dell'avvio: se l'app parte con fatica si vede dove perde tempo.</summary>
    private static void StartupStep(string step, Stopwatch clock)
    {
        if (clock.ElapsedMilliseconds >= 100)
        {
            Log.Info($"Avvio, {step}: {clock.ElapsedMilliseconds} ms");
        }

        clock.Restart();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            _service.SettleStates();
            _service.TickAnimations();

            // "freeze on" (le prove automatiche): il mouse non comanda, le finestre restano come sono state messe
            if (_frozen)
            {
                _rail.Hover = null;
                _card.ShowFor(null);
                return;
            }

            _ = Win32.GetCursorPos(out POINT point);
            var cursor = new Point(point.X, point.Y);
            long now = Stopwatch.GetTimestamp();

            // tessera sotto il mouse, sulla barra o sulla scheda
            ParkedWindow? tile = _rail.TileAtScreen(cursor) ?? _card.WindowAt(cursor);
            if (!ReferenceEquals(tile, _dwellTile))
            {
                _dwellTile = tile;
                _dwellSince = now;
            }

            _rail.Hover = _rail.Visible && _rail.Bounds.Contains(cursor) ? tile : null;
            _card.ShowFor(tile);

            if (tile is { State: ParkState.Collapsed or ParkState.Collapsing } && Milliseconds(_dwellSince, now) >= DwellMs)
            {
                _service.Expand(tile);
            }

            UpdateCollapse(cursor, now);
        }
        catch (Exception ex)
        {
            Log.Error("Errore nel ciclo dell'app", ex);
        }
    }

    private void UpdateCollapse(Point cursor, long now)
    {
        // le finestre con la puntina restano aperte per conto loro: valgono le regole del mouse solo per le altre
        var open = _service.Parked.Where(window => window.State != ParkState.Collapsed && !window.Pinned).ToList();
        if (open.Count == 0)
        {
            _outside = false;
            return;
        }

        bool inside = (_rail.Visible && _rail.Bounds.Contains(cursor))
                      || (_card.Visible && _card.Bounds.Contains(cursor))
                      || _service.IsCursorInsideActiveArea(cursor);

        bool waitsForMouse = false;
        foreach (ParkedWindow window in open)
        {
            if (!window.WaitForMouse)
            {
                continue;
            }

            if (inside)
            {
                window.WaitForMouse = false;    // da adesso valgono le regole del mouse
            }
            else
            {
                waitsForMouse = true;

                // aperta senza il mouse: si richiude quando il programma passa in secondo piano
                if (!WindowTools.IsForegroundOrOwned(window.Hwnd))
                {
                    _service.Collapse(window);
                }
            }
        }

        if (inside || waitsForMouse)
        {
            _outside = false;
            return;
        }

        if (!_outside)
        {
            _outside = true;
            _outsideSince = now;
        }
        else if (Milliseconds(_outsideSince, now) >= LeaveMs)
        {
            _service.CollapseAll();
        }
    }

    /// <summary>Dal menu dell'icona: la barra sull'altro lato dello schermo.</summary>
    private void MoveBar(BarSide side)
    {
        try
        {
            _rail.Hover = null;
            _card.ShowFor(null);
            _service.MoveBar(side);
        }
        catch (Exception ex)
        {
            Log.Error("Errore spostando la barra", ex);
        }
    }

    private void OnDisplaysChanged() => AdaptToScreens("perché Windows ha avvertito");

    /// <summary>Gli schermi sono cambiati mentre l'app gira: la barra e le finestre parcheggiate si adattano.</summary>
    private void AdaptToScreens(string why)
    {
        try
        {
            _service.AdaptToScreens();
        }
        catch (Exception ex)
        {
            Log.Error($"Errore riadattando le finestre agli schermi ({why})", ex);
        }
    }

    private void OnWinEvent(WinEvent e)
    {
        try
        {
            _service.HandleWinEvent(e);
            _buttons.HandleWinEvent(e);
        }
        catch (Exception ex)
        {
            Log.Error($"Errore gestendo l'evento di finestra {e.Id:X}", ex);
        }
    }

    private void OnParkRequested(IntPtr hwnd)
    {
        if (_service.Park(hwnd))
        {
            _buttons.Remove(hwnd);
        }
        else
        {
            _tray.ShowBalloonTip(4000, "SmartBar",
                "Non sono riuscito a parcheggiare questa finestra: forse il programma è avviato come amministratore.",
                ToolTipIcon.Warning);
        }
    }

    /// <summary>
    /// Tasto destro sull'anteprima: la finestra esce dal parcheggio e torna dov'era.
    /// Con <paramref name="bringToFront"/> viene anche in primo piano (dal tasto destro si', dal comando di prova no).
    /// </summary>
    private void ReleaseWindow(ParkedWindow window, bool bringToFront)
    {
        IntPtr hwnd = window.Hwnd;
        _service.Unpark(window, restoreOriginal: true);
        if (bringToFront)
        {
            _ = Win32.SetForegroundWindow(hwnd);
        }
    }

    /// <summary>Un comando arrivato da un altro processo (riga di comando, prove automatiche). Restituisce la risposta.</summary>
    private string HandleCommand(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts[0])
        {
            case "restore-all":
                RestoreAll();
                return "ok";
            case "collapse-all":
                _service.CollapseAll();
                return "ok";
            case "freeze":
                _frozen = parts.Length > 1 && parts[1] == "on";
                return "ok";
            case "side":
                // per le prove automatiche (dal menu si fa con la voce "Barra"): "side left" oppure "side right"
                if (parts.Length > 1 && Enum.TryParse(parts[1], ignoreCase: true, out BarSide wanted) && Enum.IsDefined(wanted))
                {
                    MoveBar(wanted);
                    return $"ok {Settings.Side}";
                }

                return "err lato sconosciuto";
            case "exit":
                // la risposta va data prima di chiudere: l'uscita si fa al passaggio successivo del ciclo
                SynchronizationContext.Current?.Post(_ => ExitApp(), null);
                return "ok";
        }

        if (parts.Length < 2 || !long.TryParse(parts[1], out long value))
        {
            return "err comando sconosciuto";
        }

        var hwnd = new IntPtr(value);
        if (parts[0] == "park")
        {
            return _service.Park(hwnd) ? "ok" : "err non parcheggiabile";
        }

        if (parts[0] == "measure")
        {
            return _buttons.Describe(hwnd);    // diagnostica in sola lettura
        }

        if (parts[0] == "hover")
        {
            // per le prove automatiche: come se il cursore entrasse ("on") o uscisse dal pulsante della finestra
            return _buttons.Hover(hwnd, parts.Length > 2 && parts[2] == "on") ? "ok" : "err niente pulsante";
        }

        ParkedWindow? window = _service.Parked.FirstOrDefault(candidate => candidate.Hwnd == hwnd);
        if (window is null)
        {
            return "err non parcheggiata";
        }

        switch (parts[0])
        {
            case "open":
                _service.Expand(window);
                return "ok";
            case "pin":
                _service.TogglePin(window);
                return "ok";
            case "release":
                ReleaseWindow(window, bringToFront: false);
                return "ok";
            case "state":
                return $"{window.State} pinned={(window.Pinned ? 1 : 0)} slot={window.Slot}";
            case "tile":
                // per le prove automatiche: dove sta la tessera di questa finestra sulla barra (sola lettura)
                return _rail.TileScreenRect(window) is Rectangle tile
                    ? $"ok {tile.Left} {tile.Top} {tile.Right} {tile.Bottom}"
                    : "err niente tessera";
        }

        return "err comando sconosciuto";
    }

    private void OnParkedChanged()
    {
        _dwellTile = null;
        _buttons.Sweep();
    }

    private void OnRecover(object? sender, EventArgs e)
    {
        _recover.Stop();

        // secondo tentativo, per i programmi che all'avvio stavano ancora partendo
        _ = _service.RecoverFromStore(keepUnfound: false);
        if (Runtime.Suffix.Length > 0)
        {
            return;    // l'istanza di prova non disturba con avvisi
        }

        _tray.ShowBalloonTip(6000, "SmartBar",
            _service.Parked.Count > 0
                ? $"Ho ritrovato {_service.Parked.Count} finestre parcheggiate dall'ultima volta."
                : "Per parcheggiare una finestra usa il pulsante accanto a Riduci a icona.",
            ToolTipIcon.Info);
    }

    private void RestoreAll() => _service.RestoreAll();

    private void ExitApp() => ExitThread();

    /// <summary>Da chiamare se l'app sta per cadere: rimette a posto le finestre parcheggiate.</summary>
    public void EmergencyRestore() => _service.RestoreAll();

    private static double Milliseconds(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            {
                _tick.Stop();
                _sweep.Stop();
                _recover.Stop();
                _hook.Dispose();
                _buttons.Dispose();
                RestoreAll();
                _card.Dispose();
                _rail.Dispose();
                _service.Dispose();
                _tray.Visible = false;
                _tray.Dispose();
                _trayIcon.Dispose();
                Log.Info("Chiusa.");
            }
            catch (Exception ex)
            {
                Log.Error("Errore durante la chiusura", ex);
            }
        }

        base.Dispose(disposing);
    }
}
