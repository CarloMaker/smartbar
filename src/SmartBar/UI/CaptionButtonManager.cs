using System.Windows.Forms;
using SmartBar.Core;
using SmartBar.Infrastructure;
using SmartBar.Native;

namespace SmartBar.UI;

/// <summary>Mette, sposta e toglie il pulsante di parcheggio sulle finestre degli altri programmi.</summary>
internal sealed class CaptionButtonManager : IDisposable
{
    /// <summary>
    /// Finestre che subito a sinistra dei pulsanti di sistema tengono schede o pulsanti propri. Di solito lo si
    /// scopre chiedendo alla finestra (vedi WindowTools.FindCaptionButtons), ma queste non lo dicono: per loro
    /// da ferma il pulsante e' una maniglia.
    /// </summary>
    private static readonly HashSet<string> HandleModeClasses = new(StringComparer.Ordinal)
    {
        "Chrome_WidgetWin_1",               // Chrome, Edge e i programmi fatti con Electron
        "Notepad",
        "CASCADIA_HOSTING_WINDOW_CLASS",    // Terminale Windows
    };

    private const int Gap96 = 2;    // fra il nostro pulsante e "Riduci a icona"

    private readonly Dictionary<IntPtr, CaptionButtonForm> _buttons = new();
    private readonly Dictionary<IntPtr, Measure> _measures = new();
    private readonly HashSet<IntPtr> _asking = new();
    private readonly Func<IntPtr, bool> _isParked;
    private readonly SynchronizationContext _sync;

    public event Action<IntPtr>? ParkRequested;

    public CaptionButtonManager(Func<IntPtr, bool> isParked)
    {
        _isParked = isParked;
        _sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    }

    /// <summary>
    /// Dove sono i pulsanti di sistema di una finestra quando essa ha una certa misura: cambia misura, si rimesura.
    /// </summary>
    private sealed class Measure
    {
        public int Width, Height;
        public uint Dpi;
        public CaptionBand? Band;

        /// <summary>La finestra ha risposto alla domanda sulla posizione precisa (le risponde quasi subito, in secondo piano).</summary>
        public bool Answered;
    }

    /// <summary>Passa in rassegna tutte le finestre: rete di sicurezza per gli eventi che non arrivano.</summary>
    public void Sweep()
    {
        var seen = new HashSet<IntPtr>();
        foreach (IntPtr hwnd in WindowTools.TopLevelWindows())
        {
            if (IsEligible(hwnd))
            {
                _ = Add(hwnd);      // falso se il pulsante c'era gia': la finestra va comunque tenuta d'occhio
                seen.Add(hwnd);
            }
        }

        foreach (IntPtr gone in _buttons.Keys.Where(hwnd => !seen.Contains(hwnd)).ToList())
        {
            Remove(gone);
        }
    }

    public void HandleWinEvent(WinEvent e)
    {
        if (_buttons.TryGetValue(e.Hwnd, out CaptionButtonForm? button))
        {
            switch (e.Id)
            {
                case WinEventIds.EVENT_SYSTEM_MOVESIZESTART:
                    // mentre l'utente trascina la finestra il pulsante resterebbe indietro: si nasconde e torna al rilascio
                    button.Hide();
                    break;

                case WinEventIds.EVENT_SYSTEM_MOVESIZEEND:
                case WinEventIds.EVENT_OBJECT_UNCLOAKED:
                case WinEventIds.EVENT_OBJECT_LOCATIONCHANGE:
                    Place(e.Hwnd);
                    break;

                case WinEventIds.EVENT_SYSTEM_FOREGROUND:
                    button.RefreshTheme();
                    if (_measures.TryGetValue(e.Hwnd, out Measure? measure) && !measure.Answered)
                    {
                        // non aveva risposto (era occupata): ora che e' in primo piano le si chiede di nuovo
                        _measures.Remove(e.Hwnd);
                    }

                    Place(e.Hwnd);
                    break;

                case WinEventIds.EVENT_OBJECT_STATECHANGE:
                    if (Win32.IsIconic(e.Hwnd))
                    {
                        button.Hide();
                    }
                    else
                    {
                        Place(e.Hwnd);
                    }

                    break;

                case WinEventIds.EVENT_OBJECT_CLOAKED:
                    button.Hide();
                    break;

                case WinEventIds.EVENT_OBJECT_HIDE:
                case WinEventIds.EVENT_OBJECT_DESTROY:
                    Remove(e.Hwnd);
                    break;
            }

            return;
        }

        if (e.Id is WinEventIds.EVENT_OBJECT_SHOW or WinEventIds.EVENT_SYSTEM_FOREGROUND)
        {
            _ = Add(e.Hwnd);
        }
    }

    public void Remove(IntPtr hwnd)
    {
        _measures.Remove(hwnd);
        _asking.Remove(hwnd);
        if (_buttons.Remove(hwnd, out CaptionButtonForm? button))
        {
            button.Dispose();
        }
    }

    /// <summary>
    /// Per le prove e la diagnostica, in sola lettura (le stesse domande che Windows fa a ogni movimento del mouse):
    /// dove sono i pulsanti di sistema di una finestra e dov'e' il nostro. Vale per qualsiasi finestra.
    /// </summary>
    public string Describe(IntPtr hwnd)
    {
        const string unknown = "pulsanti=?";
        CaptionPlacement? placement = WindowTools.FindCaptionButtons(hwnd, askWindow: true);
        if (placement is null)
        {
            return unknown;
        }

        string found = $"pulsanti=({placement.Band.Left},{placement.Band.Top},{placement.Band.Right},{placement.Band.Bottom})"
                       + $"x{placement.Band.Count} risposta={(placement.WindowAnswered ? 1 : 0)}"
                       + $" contenuto={(placement.Band.ContentOnLeft ? 1 : 0)}";
        if (!_buttons.TryGetValue(hwnd, out CaptionButtonForm? button))
        {
            return found;
        }

        Rectangle now = button.CurrentBounds, full = button.FullBounds;
        return $"{found} nostro=({now.Left},{now.Top},{now.Right},{now.Bottom})"
               + $" intero=({full.Left},{full.Top},{full.Right},{full.Bottom}) maniglia={(button.ShowsHandle ? 1 : 0)}";
    }

    /// <summary>
    /// Per le prove automatiche (il mouse vero non sempre si puo' usare): come se il cursore entrasse nel pulsante di
    /// una finestra o ne uscisse. Falso se quella finestra non ha il pulsante.
    /// </summary>
    public bool Hover(IntPtr hwnd, bool inside)
    {
        if (!_buttons.TryGetValue(hwnd, out CaptionButtonForm? button) || button.IsDisposed)
        {
            return false;
        }

        button.Hover(inside);
        return true;
    }

    private bool IsEligible(IntPtr hwnd) =>
        Runtime.IsAllowedProcess(WindowTools.ProcessIdOf(hwnd)) && !_isParked(hwnd) && WindowTools.IsParkable(hwnd);

    /// <summary>Crea il pulsante se serve. Falso se la finestra non era da pulsante o ce l'aveva gia'.</summary>
    private bool Add(IntPtr hwnd)
    {
        if (_buttons.ContainsKey(hwnd) || !IsEligible(hwnd))
        {
            return false;
        }

        var button = new CaptionButtonForm(hwnd);
        button.Clicked += () =>
        {
            Log.Info($"Click sul pulsante di {WindowTools.Describe(hwnd)}");
            ParkRequested?.Invoke(hwnd);
        };
        _buttons[hwnd] = button;
        Place(hwnd);
        return true;
    }

    /// <summary>
    /// Pulsante subito a sinistra di "Riduci a icona", largo e alto come i pulsanti di quella finestra, e nell'ordine
    /// delle finestre proprio sopra la sua: se un'altra finestra la copre, copre anche il pulsante.
    /// </summary>
    public void Place(IntPtr hwnd)
    {
        if (!_buttons.TryGetValue(hwnd, out CaptionButtonForm? button) || button.IsDisposed)
        {
            return;
        }

        if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd) || WindowTools.IsCloaked(hwnd)
            || !Win32.GetWindowRect(hwnd, out RECT window))
        {
            if (button.Visible)
            {
                button.Hide();
            }

            return;
        }

        button.SetDpi(Win32.GetDpiForWindow(hwnd));
        if (MeasureOf(hwnd, window).Band is not { } band)
        {
            return;
        }

        // la misura vale per il posto che la finestra aveva allora: nel frattempo puo' essersi spostata
        int shiftX = window.Right - band.AnchorX, shiftY = window.Top - band.AnchorY;
        bool handleMode = HandleModeClasses.Contains(WindowTools.GetClassName(hwnd)) || band.ContentOnLeft;
        button.SetArea(
            right: band.Left + shiftX - button.Px(Gap96),
            top: band.Top + shiftY,
            width: Math.Clamp(band.Cell, button.Px(30), button.Px(60)),
            height: Math.Clamp(band.Height, button.Px(18), button.Px(64)),
            handleMode);

        // il pulsante sta nella stessa fascia della sua finestra: se quella e' sempre in primo piano, anche lui
        bool targetTopmost = WindowTools.HasStyle(hwnd, Win32.GWL_EXSTYLE, Win32.WS_EX_TOPMOST);
        if (targetTopmost != button.IsTopmost)
        {
            _ = Win32.SetWindowPos(button.Handle, targetTopmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            button.IsTopmost = targetTopmost;
        }

        IntPtr above = Win32.GetWindow(hwnd, Win32.GW_HWNDPREV);
        uint flags = Win32.SWP_NOACTIVATE;
        if (above == button.Handle)
        {
            // gia' al suo posto: una finestra non puo' essere messa dopo se stessa
            flags |= Win32.SWP_NOZORDER;
            above = IntPtr.Zero;
        }

        Rectangle now = button.CurrentBounds;
        _ = Win32.SetWindowPos(button.Handle, above, now.Left, now.Top, now.Width, now.Height, flags);
        if (!button.Visible)
        {
            button.Show();
        }
    }

    /// <summary>
    /// La misura che vale per la finestra com'e' adesso. Se non c'e': subito quella rapida di Windows (costa nulla),
    /// e la precisa, chiedendo alla finestra, arriva un attimo dopo da un filo in secondo piano: sono un centinaio
    /// di domande per finestra, e una finestra occupata potrebbe farci aspettare: il pulsante non deve fermarsi.
    /// </summary>
    private Measure MeasureOf(IntPtr hwnd, in RECT window)
    {
        uint dpi = Win32.GetDpiForWindow(hwnd);
        if (_measures.TryGetValue(hwnd, out Measure? known) && known.Width == window.Width && known.Height == window.Height
            && known.Dpi == dpi)
        {
            return known;
        }

        var measure = new Measure
        {
            Width = window.Width,
            Height = window.Height,
            Dpi = dpi,
            Band = WindowTools.FindCaptionButtons(hwnd, askWindow: false)?.Band,
        };
        _measures[hwnd] = measure;
        AskLater(hwnd, measure);
        return measure;
    }

    private async void AskLater(IntPtr hwnd, Measure measure)
    {
        if (!_asking.Add(hwnd))
        {
            return;    // c'e' gia' una domanda in corso per questa finestra: a risposta fatta se ne riparla
        }

        try
        {
            CaptionPlacement? placement = await Task.Run(() => WindowTools.FindCaptionButtons(hwnd, askWindow: true));
            _sync.Post(_ => Answered(hwnd, measure, placement), null);
        }
        catch (Exception ex)
        {
            Log.Error($"Posizione dei pulsanti di {WindowTools.Describe(hwnd)} non letta", ex);
            _sync.Post(_ => _asking.Remove(hwnd), null);
        }
    }

    private void Answered(IntPtr hwnd, Measure measure, CaptionPlacement? placement)
    {
        _asking.Remove(hwnd);
        if (placement is null
            || !_measures.TryGetValue(hwnd, out Measure? current) || !ReferenceEquals(current, measure)
            || !_buttons.ContainsKey(hwnd))
        {
            // intanto la finestra ha cambiato misura, o non c'e' piu', o era troppo occupata per rispondere: si tengono le stime
            return;
        }

        measure.Band = placement.Band;
        measure.Answered = placement.WindowAnswered;
        Place(hwnd);
    }

    public void Dispose()
    {
        foreach (CaptionButtonForm button in _buttons.Values)
        {
            button.Dispose();
        }

        _buttons.Clear();
        _measures.Clear();
    }
}
