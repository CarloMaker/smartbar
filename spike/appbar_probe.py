# appbar_probe.py -- si puo' riservare la striscia della barra come fa la barra delle applicazioni di Windows?
# Registra una "application desktop toolbar" (SHAppBarMessage) sul bordo sinistro dello schermo principale con una
# finestra di prova nascosta, guarda cosa fanno lo spazio di lavoro e le finestre ingrandite (sola lettura), poi la
# toglie e controlla che tutto torni com'era. Le notifiche che Windows manda alla barra vengono stampate.
#   python appbar_probe.py            bordo sinistro, striscia larga 84
#   python appbar_probe.py right 120  altro bordo e larghezza
# Poi registra anche una seconda barra sullo stesso bordo (come l'istanza di prova accanto a quella dell'utente) e
# guarda che striscia le viene data; infine fa cambiare bordo alla prima e guarda che striscia le viene data.
# Per pochi secondi lo spazio di lavoro si restringe: le finestre ingrandite si stringono e poi tornano. Non si fa
# finche' l'utente non lascia fermi mouse e tastiera per 10 secondi, come per i click veri delle prove smoke.

import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

ctypes.WinDLL('shcore').SetProcessDpiAwareness(2)     # coordinate in pixel veri, su ogni schermo
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import park_probe as pp

u, kernel = pp.user32, pp.kernel32
shell32 = ctypes.WinDLL('shell32')

ABM_NEW, ABM_REMOVE, ABM_QUERYPOS, ABM_SETPOS, ABM_WINDOWPOSCHANGED = 0x0, 0x1, 0x2, 0x3, 0x9
ABE_LEFT, ABE_TOP, ABE_RIGHT, ABE_BOTTOM = 0, 1, 2, 3
ABN = {0: 'ABN_STATECHANGE', 1: 'ABN_FULLSCREENAPP', 2: 'ABN_WINDOWARRANGE', 3: 'ABN_POSCHANGED'}
CALLBACK = 0x8000 + 100         # WM_APP + 100: con questo Windows avvisa la barra
IDLE_MS = 10000                 # da quanto tempo l'utente non deve toccare mouse e tastiera
WAIT_S = int(os.environ.get('APPBAR_PROBE_WAIT', 90))    # quanto aspettare che l'utente si fermi, prima di rinunciare


class APPBARDATA(ctypes.Structure):
    _fields_ = [('cbSize', wt.DWORD), ('hWnd', wt.HWND), ('uCallbackMessage', wt.UINT), ('uEdge', wt.UINT),
                ('rc', wt.RECT), ('lParam', wt.LPARAM)]


class MONITORINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.DWORD), ('rcMonitor', wt.RECT), ('rcWork', wt.RECT), ('dwFlags', wt.DWORD)]


class LASTINPUTINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.UINT), ('dwTime', wt.DWORD)]


MONITORENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HANDLE, wt.HDC, ctypes.POINTER(wt.RECT), wt.LPARAM)
WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)

shell32.SHAppBarMessage = pp.proto(shell32, 'SHAppBarMessage', ctypes.c_size_t, wt.DWORD, ctypes.POINTER(APPBARDATA))
u.GetMonitorInfoW = pp.proto(u, 'GetMonitorInfoW', wt.BOOL, wt.HANDLE, ctypes.POINTER(MONITORINFO))
u.EnumDisplayMonitors = pp.proto(u, 'EnumDisplayMonitors', wt.BOOL, wt.HDC, ctypes.c_void_p, MONITORENUMPROC, wt.LPARAM)
u.EnumWindows = pp.proto(u, 'EnumWindows', wt.BOOL, pp.WNDENUMPROC, wt.LPARAM)
u.IsZoomed = pp.proto(u, 'IsZoomed', wt.BOOL, wt.HWND)
u.IsWindowVisible = pp.proto(u, 'IsWindowVisible', wt.BOOL, wt.HWND)
u.DestroyWindow = pp.proto(u, 'DestroyWindow', wt.BOOL, wt.HWND)
u.CreateWindowExW = pp.proto(u, 'CreateWindowExW', wt.HWND, wt.DWORD, ctypes.c_wchar_p, ctypes.c_wchar_p, wt.DWORD,
                             ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.HWND, wt.HMENU, wt.HINSTANCE,
                             ctypes.c_void_p)
u.SetWindowLongPtrW = pp.proto(u, 'SetWindowLongPtrW', ctypes.c_ssize_t, wt.HWND, ctypes.c_int, ctypes.c_ssize_t)
u.CallWindowProcW = pp.proto(u, 'CallWindowProcW', ctypes.c_ssize_t, ctypes.c_void_p, wt.HWND, wt.UINT, wt.WPARAM,
                             wt.LPARAM)
u.GetLastInputInfo = pp.proto(u, 'GetLastInputInfo', wt.BOOL, ctypes.POINTER(LASTINPUTINFO))
u.TranslateMessage = pp.proto(u, 'TranslateMessage', wt.BOOL, ctypes.POINTER(pp.MSG))
u.DispatchMessageW = pp.proto(u, 'DispatchMessageW', ctypes.c_ssize_t, ctypes.POINTER(pp.MSG))
u.PeekMessageW = pp.proto(u, 'PeekMessageW', wt.BOOL, ctypes.POINTER(pp.MSG), wt.HWND, wt.UINT, wt.UINT, wt.UINT)

notices = []        # le notifiche arrivate alle barre: (quale barra, nome, wParam, lParam)
others = []         # gli altri messaggi arrivati alle barre: (quale barra, messaggio)
old_procs = {}      # barra -> procedura di finestra di prima


def _wnd_proc(hwnd, msg, wparam, lparam):
    if msg == CALLBACK:
        notices.append((hwnd, ABN.get(wparam, hex(wparam)), wparam, lparam))
    elif msg not in (0x0000,):
        others.append((hwnd, hex(msg), wparam, lparam))
    return u.CallWindowProcW(old_procs[hwnd], hwnd, msg, wparam, lparam)


WNDPROC_CB = WNDPROC(_wnd_proc)


def idle_ms():
    info = LASTINPUTINFO()
    info.cbSize = ctypes.sizeof(info)
    return (kernel.GetTickCount() - info.dwTime) & 0xFFFFFFFF if u.GetLastInputInfo(ctypes.byref(info)) else 0


def work_areas():
    """Di ogni schermo: rettangolo, spazio di lavoro (senza barra delle applicazioni e altre barre), flags."""
    found = []

    @MONITORENUMPROC
    def cb(hmon, _hdc, _rect, _):
        info = MONITORINFO()
        info.cbSize = ctypes.sizeof(info)
        if u.GetMonitorInfoW(hmon, ctypes.byref(info)):
            m, w = info.rcMonitor, info.rcWork
            found.append(((m.left, m.top, m.right, m.bottom), (w.left, w.top, w.right, w.bottom), info.dwFlags))
        return True

    u.EnumDisplayMonitors(None, None, cb, 0)
    return found


def maximized(bounds):
    """Le finestre ingrandite e visibili sullo schermo dato: hwnd -> rettangolo. Sola lettura."""
    out = {}
    left, top, right, bottom = bounds

    @pp.WNDENUMPROC
    def cb(hwnd, _):
        if u.IsWindowVisible(hwnd) and u.IsZoomed(hwnd) and pp.is_cloaked(hwnd) <= 0:
            r = pp.rect(hwnd)
            if r[2] > left and r[0] < right and r[3] > top and r[1] < bottom:
                out[hwnd] = r
        return True

    u.EnumWindows(cb, 0)
    return out


def pump(seconds, pred=lambda: False):
    """Fa girare i messaggi delle barre per il tempo dato, o finche' pred() e' vero. Secondi attesi (None se tutto il tempo)."""
    started = time.perf_counter()
    end = started + seconds
    msg = pp.MSG()
    while True:
        while u.PeekMessageW(ctypes.byref(msg), None, 0, 0, pp.PM_REMOVE):
            u.TranslateMessage(ctypes.byref(msg))
            u.DispatchMessageW(ctypes.byref(msg))
        if pred():
            return time.perf_counter() - started
        if time.perf_counter() >= end:
            return None
        time.sleep(0.002)


def make_bar():
    """Una finestra di prova nascosta, che riceve le notifiche di Windows come le riceverebbe la barra."""
    hwnd = u.CreateWindowExW(pp.WS_EX_TOOLWINDOW | 0x08000000, 'STATIC', 'appbar-probe', pp.WS_POPUP,
                             0, 0, 10, 10, None, None, None, None)
    if not hwnd:
        raise ctypes.WinError(ctypes.get_last_error())

    old_procs[hwnd] = u.SetWindowLongPtrW(hwnd, -4, ctypes.cast(WNDPROC_CB, ctypes.c_void_p).value)   # GWLP_WNDPROC
    return hwnd


def appbar(message, hwnd, edge, rect):
    """Una domanda a Windows per la barra: (risposta, striscia)."""
    data = APPBARDATA(cbSize=ctypes.sizeof(APPBARDATA), hWnd=hwnd, uCallbackMessage=CALLBACK, uEdge=edge,
                      rc=wt.RECT(*rect))
    answer = shell32.SHAppBarMessage(message, ctypes.byref(data))
    return answer, (data.rc.left, data.rc.top, data.rc.right, data.rc.bottom)


def main():
    side = sys.argv[1] if len(sys.argv) > 1 else 'left'
    width = int(sys.argv[2]) if len(sys.argv) > 2 else 84
    edge = {'left': ABE_LEFT, 'top': ABE_TOP, 'right': ABE_RIGHT, 'bottom': ABE_BOTTOM}.get(side)
    if edge is None:
        print('Lato sconosciuto:', side)
        return 2

    before = work_areas()
    print('Prima, ogni schermo (rettangolo, spazio di lavoro, flags):', before, flush=True)
    home = next((item for item in before if item[2] & 1), before[0])
    left, top, right, bottom = home[0]
    windows = maximized(home[0])
    print(f'Finestre ingrandite sullo schermo principale: {len(windows)}', flush=True)

    end = time.time() + WAIT_S
    while idle_ms() < IDLE_MS:
        if time.time() >= end:
            print('L\'utente sta usando il PC: la prova non si fa adesso.')
            return 3
        time.sleep(0.5)

    # la striscia proposta: tutto il bordo dello schermo principale, largo come la barra
    if edge == ABE_LEFT:
        proposed = (left, top, left + width, bottom)
    elif edge == ABE_RIGHT:
        proposed = (right - width, top, right, bottom)
    elif edge == ABE_TOP:
        proposed = (left, top, right, top + width)
    else:
        proposed = (left, bottom - width, right, bottom)

    first = make_bar()
    second = None
    answer, _ = appbar(ABM_NEW, first, edge, proposed)
    if not answer:
        print('Windows ha rifiutato la registrazione della barra (ABM_NEW).')
        u.DestroyWindow(first)
        return 1

    try:
        _, strip = appbar(ABM_QUERYPOS, first, edge, proposed)
        print('Striscia proposta:', proposed, '-- dopo ABM_QUERYPOS:', strip, flush=True)
        appbar(ABM_SETPOS, first, edge, strip)
        print('Spazio di lavoro appena dopo ABM_SETPOS, senza aspettare:', work_areas(), flush=True)
        print('Stessa barra che rifà ABM_QUERYPOS:', appbar(ABM_QUERYPOS, first, edge, proposed)[1], flush=True)
        took = pump(1.0, lambda: work_areas() != before)
        print(f'Spazio di lavoro cambiato dopo {None if took is None else round(took * 1000)} ms; ora:', work_areas(), flush=True)
        pump(0.8)
        moved = maximized(home[0])
        changed = sum(1 for handle, r in moved.items() if windows.get(handle) != r)
        print(f'Finestre ingrandite: erano {len(windows)}, ora {len(moved)}; cambiata la misura di {changed}', flush=True)
        for handle, r in list(moved.items())[:3]:
            print(f'   {windows.get(handle)} -> {r}', flush=True)
        print('Notifiche arrivate alla prima barra fin qui:', notices, flush=True)
        print('Altri messaggi arrivati alle barre fin qui:', others[-12:], flush=True)

        # una seconda barra sullo stesso bordo, mentre la prima c'e' ancora
        second = make_bar()
        appbar(ABM_NEW, second, edge, proposed)
        _, strip2 = appbar(ABM_QUERYPOS, second, edge, proposed)
        appbar(ABM_SETPOS, second, edge, strip2)
        print('Seconda barra: striscia data da Windows:', strip2, '-- spazio di lavoro subito dopo:', work_areas(), flush=True)
        pump(0.6)
        print('   e dopo mezzo secondo:', work_areas(), flush=True)
        appbar(ABM_REMOVE, second, edge, proposed)
        print('Seconda barra tolta: spazio di lavoro subito dopo:', work_areas(), flush=True)
        pump(0.6)
        print('   e dopo mezzo secondo:', work_areas(), flush=True)
        u.DestroyWindow(second)
        del old_procs[second]
        second = None

        # --- la stessa barra che cambia bordo: prima a destra, poi di nuovo a sinistra ---
        other = ABE_RIGHT if edge == ABE_LEFT else ABE_LEFT if edge == ABE_RIGHT else None
        if other is not None:
            wide = (right - width, top, right, bottom) if edge == ABE_LEFT else (left, top, left + width, bottom)
            print('Cambio di bordo: QUERYPOS sull\'altro bordo mentre la barra e\' ancora registrata su questo:',
                  appbar(ABM_QUERYPOS, first, other, wide)[1], ' -- proposto:', wide, flush=True)
            move = appbar(ABM_QUERYPOS, first, other, wide)[1]
            appbar(ABM_SETPOS, first, other, move)
            print('   dopo SETPOS sul nuovo bordo: striscia=', move, 'spazio di lavoro=', work_areas(), flush=True)
            print('   QUERYPOS di nuovo sul nuovo bordo:', appbar(ABM_QUERYPOS, first, other, wide)[1], flush=True)
            print('   QUERYPOS sul bordo di prima:', appbar(ABM_QUERYPOS, first, edge, proposed)[1], ' -- proposto:', proposed,
                  flush=True)
            back = appbar(ABM_QUERYPOS, first, edge, proposed)[1]
            appbar(ABM_SETPOS, first, edge, back)
            print('   dopo SETPOS sul bordo di prima: striscia=', back, 'spazio di lavoro=', work_areas(), flush=True)
    finally:
        appbar(ABM_REMOVE, first, edge, proposed)
        print('Prima barra tolta: spazio di lavoro subito dopo:', work_areas(), flush=True)
        took = pump(3.0, lambda: work_areas() == before)
        print(f'Spazio di lavoro tornato com\'era dopo {None if took is None else round(took * 1000)} ms', flush=True)
        pump(0.5)
        after = work_areas()
        print('Alla fine:', after, '-- com\'era:', after == before, flush=True)
        if after != before:
            print('ATTENZIONE, non e\' tornato com\'era:', after, flush=True)
        print('Notifiche arrivate in tutto:', notices, flush=True)
        print('Altri messaggi arrivati in tutto:', others[-20:], flush=True)
        if second:
            u.DestroyWindow(second)
        u.DestroyWindow(first)
    return 0


if __name__ == '__main__':
    sys.exit(main())
