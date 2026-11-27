# appbar_move_probe.py -- la finestra di una barra del desktop viene spostata da Windows dopo che si e' mossa da sola?
# Registra una finestra di prova come barra del desktop (come la barra di SmartBar), la mostra in primo piano sulla sua
# striscia e poi la fa cambiare bordo, come fa SmartBar dal menu: ogni volta legge dove sta la finestra un attimo dopo.
#   A.  registerata a sinistra, finestra messa sulla striscia, avviso ABM_WINDOWPOSCHANGED;
#   B.  cambio di bordo a destra: QUERYPOS + SETPOS, finestra spostata sulla nuova striscia, avviso;
#   C.  stessa cosa, ma senza l'avviso ABM_WINDOWPOSCHANGED;
#   D.  registro prima, avviso e solo dopo si sposta la finestra.
# Per pochi secondi si vede una striscia grigia sul bordo e lo spazio di lavoro si restringe: come le altre prove, si fa
# solo da fermi (mouse e tastiera senza uso per 10 secondi).

import ctypes
import ctypes.wintypes as wt
import os
import sys
import time

ctypes.WinDLL('shcore').SetProcessDpiAwareness(2)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import park_probe as pp
import appbar_probe as ap

u = ap.u
ABE_LEFT, ABE_RIGHT = ap.ABE_LEFT, ap.ABE_RIGHT
ABM_NEW, ABM_QUERYPOS, ABM_SETPOS, ABM_REMOVE, ABM_WINDOWPOSCHANGED = (ap.ABM_NEW, ap.ABM_QUERYPOS, ap.ABM_SETPOS,
                                                                        ap.ABM_REMOVE, ap.ABM_WINDOWPOSCHANGED)
SWP_NOSIZE, SWP_NOMOVE = 0x0001, 0x0002

u.SetWindowPos = pp.proto(u, 'SetWindowPos', wt.BOOL, wt.HWND, ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                          ctypes.c_int, wt.UINT)


def main():
    before = ap.work_areas()
    (monitor, _work, _flags) = next(item for item in before if item[2] & 1)
    left, top, right, bottom = monitor
    rail = 84
    end = time.time() + int(os.environ.get('APPBAR_PROBE_WAIT', 90))
    while ap.idle_ms() < ap.IDLE_MS:
        if time.time() >= end:
            print('L\'utente sta usando il PC: la prova non si fa adesso.')
            return 3
        time.sleep(0.5)

    left_edge = (left, top, left + rail, bottom)
    right_edge = (right - rail, top, right, bottom)
    hwnd = ap.make_bar()
    ap.appbar(ABM_NEW, hwnd, ABE_LEFT, left_edge)

    def report(step):
        ap.pump(1.0)
        print(f'{step}: finestra={pp.rect(hwnd)} spazio di lavoro={ap.work_areas()[0][1]} avvisi={ap.notices[-3:]}', flush=True)
        ap.notices.clear()

    try:
        # A. registrata a sinistra; la finestra va sulla sua striscia e lo si dice a Windows
        _a, strip = ap.appbar(ABM_QUERYPOS, hwnd, ABE_LEFT, left_edge)
        ap.appbar(ABM_SETPOS, hwnd, ABE_LEFT, strip)
        u.SetWindowPos(hwnd, -1, strip[0], strip[1], rail, strip[3] - strip[1], pp.SWP_NOACTIVATE | pp.SWP_SHOWWINDOW)
        ap.appbar(ABM_WINDOWPOSCHANGED, hwnd, ABE_LEFT, strip)
        report('A. sinistra, finestra sulla striscia, con avviso (atteso %s)' % ((strip[0], strip[1], strip[0] + rail, strip[3]),))

        # B. cambio di bordo a destra, come fa SmartBar
        _b, strip = ap.appbar(ABM_QUERYPOS, hwnd, ABE_RIGHT, right_edge)
        ap.appbar(ABM_SETPOS, hwnd, ABE_RIGHT, strip)
        u.SetWindowPos(hwnd, -1, strip[0], strip[1], rail, strip[3] - strip[1], pp.SWP_NOACTIVATE | pp.SWP_SHOWWINDOW)
        ap.appbar(ABM_WINDOWPOSCHANGED, hwnd, ABE_RIGHT, strip)
        report('B. cambio di bordo a destra, con avviso (atteso %s)' % (strip,))

        # C. la finestra si rimette sulla striscia, stavolta senza avviso
        u.SetWindowPos(hwnd, -1, strip[0], strip[1], rail, strip[3] - strip[1], pp.SWP_NOACTIVATE)
        report('C. rimessa sulla striscia senza avviso (atteso %s)' % (strip,))

        # D. si cambia bordo e lo si dice a Windows mentre la finestra e' ancora sull'altra striscia; poi si sposta la finestra
        _d, back = ap.appbar(ABM_QUERYPOS, hwnd, ABE_LEFT, left_edge)
        ap.appbar(ABM_SETPOS, hwnd, ABE_LEFT, back)
        ap.appbar(ABM_WINDOWPOSCHANGED, hwnd, ABE_LEFT, back)
        u.SetWindowPos(hwnd, -1, back[0], back[1], rail, back[3] - back[1], pp.SWP_NOACTIVATE)
        report('D. tornata a sinistra, avviso prima di spostare (atteso %s)' % (back,))
    finally:
        ap.appbar(ABM_REMOVE, hwnd, ABE_LEFT, left_edge)
        u.DestroyWindow(hwnd)
        ap.pump(0.5)
        print('Spazio di lavoro alla fine:', ap.work_areas(), '-- com\'era:', ap.work_areas() == before, flush=True)
    return 0


if __name__ == '__main__':
    sys.exit(main())
