# fullscreen_probe.py -- che fa Windows quando c'e' un programma a schermo intero e sul bordo c'e' una barra del desktop?
# Mette sul bordo sinistro una finestra di prova visibile e "sempre in primo piano", registrata come barra del desktop
# (come la barra di SmartBar). Poi porta in primo piano, una volta per tipo, una finestra a schermo intero e guarda:
#   - quali avvisi manda Windows alla barra (ABN_FULLSCREENAPP con vero o falso);
#   - chi sta sopra la striscia della barra: la barra o il programma a schermo intero (Windows da solo, senza che la
#     barra faccia niente);
#   - chi sta sopra la barra delle applicazioni: se il programma la copre, per Windows e' davvero a schermo intero.
# Tipi di programma a schermo intero: una finestra di prova senza cornice (quella che usava smoke7.py) e Edge avviato a
# schermo intero con un profilo temporaneo (come nelle prove di fattibilita' del parcheggio).
# Porta finestre in primo piano: si fa solo da fermi (mouse e tastiera senza uso per 10 secondi). Edge viene chiuso:
# solo quello avviato qui, riconosciuto dal profilo temporaneo sulla riga di comando.
# Com'e' andata il 2026-11-27 (Windows 11 26200): Edge a schermo intero sta sopra la barra "sempre in primo piano" per conto
# di Windows, che lo mette sopra anche alla barra delle applicazioni, e ci resta anche quando perde il primo piano;
# l'avviso ABN_FULLSCREENAPP non e' arrivato. La finestra di prova senza cornice non viene considerata a schermo intero, e
# essendo di classe Static WindowFromPoint non la vede: per lei "chi sta sopra" non dice niente.

import ctypes
import ctypes.wintypes as wt
import os
import shutil
import subprocess
import sys
import time

ctypes.WinDLL('shcore').SetProcessDpiAwareness(2)     # coordinate in pixel veri
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import park_probe as pp
import smoke as s
import appbar_probe as ap

u = s.u
EDGE = r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
TAG = 'fullscreen-probe-%06x' % pp.random.getrandbits(24)
ABE_LEFT, ABM_NEW, ABM_QUERYPOS, ABM_SETPOS, ABM_REMOVE = ap.ABE_LEFT, ap.ABM_NEW, ap.ABM_QUERYPOS, ap.ABM_SETPOS, ap.ABM_REMOVE
WS_EX_TOPMOST, WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE, WS_EX_APPWINDOW = 0x8, 0x80, 0x08000000, 0x40000
SWP_NOSIZE, SWP_NOMOVE = 0x0001, 0x0002
IDLE_MS = 10000

u.SetWindowPos = pp.proto(u, 'SetWindowPos', wt.BOOL, wt.HWND, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                          ctypes.c_int, wt.UINT)
u.WindowFromPoint = pp.proto(u, 'WindowFromPoint', wt.HWND, pp.POINT)
u.GetAncestor = pp.proto(u, 'GetAncestor', wt.HWND, wt.HWND, wt.UINT)
u.GetWindowLongW = pp.proto(u, 'GetWindowLongW', wt.LONG, wt.HWND, ctypes.c_int)


def describe(hwnd):
    """Che finestra e': una di quelle di questa prova, o di chi e' (classe e processo)."""
    if not hwnd:
        return 'nessuna finestra'
    root = u.GetAncestor(hwnd, 2)
    for name, handle in WINDOWS.items():
        if handle == root:
            return name
    classes = {h: c for h, _p, _t, c in pp.top_windows()}
    pids = {h: p for h, p, _t, _c in pp.top_windows()}
    image = pp.image_of_pid(pids.get(root, 0)) if pids.get(root) else None
    return f'la finestra {root} [{classes.get(root)}] di {image}'


WINDOWS = {}      # nome -> finestra di prova


def make(name, exstyle, x, y, w, h):
    """Una finestra di prova senza cornice, con gli stili estesi dati (fa niente che sia nata con quelli della barra
    di prima: si cambiano tutti)."""
    hwnd = ap.make_bar()
    u.SetWindowPos(hwnd, None, x, y, w, h, pp.SWP_NOZORDER | pp.SWP_NOACTIVATE)
    _ = u.SetWindowLongPtrW(hwnd, -20, exstyle)      # GWL_EXSTYLE
    WINDOWS[name] = hwnd
    return hwnd


def show(hwnd, topmost=False):
    u.SetWindowPos(hwnd, -1 if topmost else None, 0, 0, 0, 0,
                   SWP_NOMOVE | SWP_NOSIZE | pp.SWP_NOACTIVATE | pp.SWP_SHOWWINDOW
                   | (0 if topmost else pp.SWP_NOZORDER))


def on_top(x, y):
    point = pp.POINT()
    point.X, point.Y = x, y
    return u.GetAncestor(u.WindowFromPoint(point), 2)


def phase(name, subject, seconds):
    """Porta la finestra in primo piano e guarda cosa fa Windows per un po'."""
    ap.notices.clear()
    front = s.activate(subject)
    ap.pump(seconds)
    left, top, right, bottom = SCREEN
    bar = WINDOWS['barra']
    print(f'--- {name}: portata in primo piano={front}, lo e\' davvero={u.GetForegroundWindow() == subject}', flush=True)
    print('   avvisi arrivati alla barra:', [(notice, lParam) for _h, notice, _w, lParam in ap.notices], flush=True)
    print('   chi sta sopra la striscia della barra:', describe(on_top(40, (top + bottom) // 2)), flush=True)
    print('   chi sta sopra la barra delle applicazioni:', describe(on_top((left + right) // 2, bottom - 24)), flush=True)
    print('   la barra e\' ancora "sempre in primo piano":', bool(u.GetWindowLongW(bar, -20) & WS_EX_TOPMOST),
          '-- sta sopra il programma:', bool(on_top(40, (top + bottom) // 2) == bar), flush=True)
    print('   stili della finestra portata in primo piano: stile=%s estesi=%s rettangolo=%s'
          % (hex(u.GetWindowLongW(subject, -16) & 0xFFFFFFFF), hex(u.GetWindowLongW(subject, -20) & 0xFFFFFFFF),
             pp.rect(subject)), flush=True)
    return [(notice, lParam) for _h, notice, _w, lParam in ap.notices]


def main():
    before = ap.work_areas()
    print('Spazio di lavoro all\'inizio:', before, flush=True)
    end = time.time() + int(os.environ.get('APPBAR_PROBE_WAIT', 90))
    while ap.idle_ms() < IDLE_MS:
        if time.time() >= end:
            print('L\'utente sta usando il PC: la prova non si fa adesso.')
            return 3
        time.sleep(0.5)

    (monitor, work, _flags) = next(item for item in before if item[2] & 1)
    left, top, right, bottom = monitor
    SCREEN[:] = monitor
    edge_proc = None
    profile = os.path.join(pp.OUT, 'edge-profile-fullscreen')
    page = os.path.join(pp.OUT, TAG + '.html')
    bar = calm = borderless = fullscreen = 0
    try:
        # la barra: visibile, sempre in primo piano, registrata come barra del desktop
        bar = make('barra', WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, 0, 0, 84, bottom - top)
        ap.appbar(ABM_NEW, bar, ABE_LEFT, (left, top, left + 84, bottom))
        _answer, strip = ap.appbar(ABM_QUERYPOS, bar, ABE_LEFT, (left, top, left + 84, bottom))
        ap.appbar(ABM_SETPOS, bar, ABE_LEFT, strip)
        show(bar, topmost=True)
        calm = make('finestra normale', 0, left + 500, top + 200, 500, 300)
        show(calm)
        ap.pump(1.0)
        print('Striscia della barra:', strip, '-- spazio di lavoro:', ap.work_areas(), flush=True)
        print('Avvisi alla barra dopo la registrazione:', ap.notices, flush=True)

        # --- una finestra di prova senza cornice, grande quanto lo schermo (quella di smoke7.py) ---
        results = []
        for name, exstyle in (('finestra di prova senza cornice', 0),
                              ('finestra di prova col pulsante nella barra di Windows', WS_EX_APPWINDOW)):
            borderless = make(name, exstyle, left, top, right - left, bottom - top)
            show(borderless)
            results.append(phase(name, borderless, 2.5))
            u.DestroyWindow(borderless)
            borderless = 0
            results.append(phase('di nuovo la finestra normale', calm, 1.5))

        # --- Edge avviato a schermo intero (profilo temporaneo) ---
        with open(page, 'w', encoding='utf-8') as f:
            f.write(pp.HTML % TAG)
        devnull = dict(stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        edge_proc = subprocess.Popen([EDGE, '--user-data-dir=' + profile, '--no-first-run', '--no-default-browser-check',
                                      '--disable-sync', '--start-fullscreen', 'file:///' + page.replace('\\', '/')], **devnull)
        fullscreen = s.wait_for(lambda: next((h for h, _p, t, c in pp.top_windows()
                                              if TAG in t and c == 'Chrome_WidgetWin_1'), 0), 60)
        if not fullscreen:
            print('Edge non ha aperto la finestra: niente da provare.', flush=True)
            return 1
        WINDOWS['Edge a schermo intero'] = fullscreen
        ap.pump(1.5)
        print('Rettangolo di Edge:', pp.rect(fullscreen), flush=True)
        results.append(phase('Edge a schermo intero', fullscreen, 3.0))
        results.append(phase('di nuovo la finestra normale', calm, 2.0))
        print('Riepilogo avvisi, in ordine di fase:', results, flush=True)
    finally:
        if edge_proc is not None:
            # solo l'Edge avviato qui: si riconosce dal profilo temporaneo sulla riga di comando (a volte riparte con un pid nuovo)
            subprocess.run(['powershell', '-NoProfile', '-Command',
                            'Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "msedge.exe" '
                            '-and $_.CommandLine -notmatch "--type=" -and $_.CommandLine -like "*' + profile + '*" } '
                            '| ForEach-Object { taskkill.exe /PID $_.ProcessId /T /F }'], capture_output=True)
            time.sleep(1.0)
            shutil.rmtree(profile, ignore_errors=True)
        for handle in (borderless, calm, bar):
            if handle:
                u.DestroyWindow(handle)
        if bar:
            ap.appbar(ABM_REMOVE, bar, ABE_LEFT, (0, 0, 0, 0))
        try:
            os.remove(page)
        except OSError:
            pass
        ap.pump(0.5)
        print('Spazio di lavoro alla fine:', ap.work_areas(), '-- com\'era:', ap.work_areas() == before, flush=True)
    return 0


SCREEN = [None]

if __name__ == '__main__':
    sys.exit(main())
