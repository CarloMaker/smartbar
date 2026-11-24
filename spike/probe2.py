# probe2.py -- secondo giro di verifica (usa solo Mappa caratteri come finestra di prova):
#   (A) modi di nascondere la finestra parcheggiata: quale lascia viva l'anteprima nella rotaia?
#       (il cloak di DWM non e' usabile: DwmSetWindowAttribute(13) risponde E_ACCESSDENIED a un'app normale)
#   (B) dentro una finestra incorporata si puo' cliccare e scrivere con input reale?
#   python probe2.py a   esegue solo (A)      python probe2.py b   esegue solo (B)

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys

import park_probe as pp

u, g, d = pp.user32, pp.gdi32, pp.dwmapi
VP = pp.VP


def make_window(x, y, w, h, topmost=True):
    ex = pp.WS_EX_TOOLWINDOW | (pp.WS_EX_TOPMOST if topmost else 0)
    return u.CreateWindowExW(ex, pp._CLASS, 'ParkProbe', pp.WS_POPUP | pp.WS_CLIPCHILDREN, x, y, w, h, None, None,
                             pp.kernel32.GetModuleHandleW(None), None)


def magenta_fraction(bgra, w, box):
    """Frazione di pixel colorati come lo sfondo magenta dentro box (x0, y0, x1, y1)."""
    x0, y0, x1, y1 = box
    n = hit = 0
    for yy in range(y0, y1, 4):
        row = bgra[yy * w * 4:(yy + 1) * w * 4]
        for i in range(x0 * 4, x1 * 4, 16):
            n += 1
            if abs(row[i] - pp.BG_BGR[0]) + abs(row[i + 1] - pp.BG_BGR[1]) + abs(row[i + 2] - pp.BG_BGR[2]) < 24:
                hit += 1
    return round(hit / n, 3)


def main():
    u.SetProcessDpiAwarenessContext(VP(-4))
    SW, SH = u.GetSystemMetrics(0), u.GetSystemMetrics(1)
    RAIL_W, RAIL_H, LEFT_W = pp.RAIL_W, 800, 540
    PANEL_W = SW // 2
    pp.BRUSH = g.CreateSolidBrush(0x00A828A0)
    wc = pp.WNDCLASSEXW()
    wc.cbSize = ctypes.sizeof(pp.WNDCLASSEXW)
    wc.lpfnWndProc = pp.WNDPROC_CB
    wc.hInstance = pp.kernel32.GetModuleHandleW(None)
    wc.hbrBackground = pp.BRUSH
    wc.lpszClassName = pp._CLASS
    u.RegisterClassExW(ctypes.byref(wc))
    top = (SH - RAIL_H) // 2
    pp.HOST = make_window(SW - RAIL_W, top, RAIL_W, RAIL_H)
    u.ShowWindow(pp.HOST, pp.SW_SHOWNOACTIVATE)
    base = pp.tasklist_pids().get('charmap.exe', set())
    helper = None
    saved = None
    p = subprocess.Popen([r'C:\Windows\System32\charmap.exe'])
    try:
        hwnd, pid = pp.wait_for(lambda pid, title, cls: pid == p.pid, 15)
        assert hwnd and pid not in base, 'finestra di prova non trovata'
        x, y, r, b = pp.rect(hwnd)
        cw, ch = r - x, b - y
        th = wt.HANDLE()
        assert d.DwmRegisterThumbnail(pp.HOST, hwnd, ctypes.byref(th)) >= 0, 'anteprima rifiutata'
        pp.layout_thumbs(RAIL_W, RAIL_H, [{'name': 'charmap', 'thumb': th.value}])

        if 'a' in sys.argv:
            # (A) nascosta / ridotta a icona / fuori schermo
            pp.move(hwnd, SW - RAIL_W - LEFT_W + 20, top + 120, cw, ch)
            pp.pump(0.8)
            x, y, r, b = pp.rect(hwnd)
            helper = make_window(x, y, cw, ch, topmost=False)   # fondo riconoscibile, sotto la finestra di prova
            u.SetWindowPos(helper, hwnd, x, y, cw, ch, pp.SWP_NOACTIVATE | pp.SWP_SHOWWINDOW)
            rx, ry, rw = SW - RAIL_W - LEFT_W, top, RAIL_W + LEFT_W

            def snap(label):
                pp.pump(1.2)
                img = pp.capture(rx, ry, rw, RAIL_H)
                tile = pp.region_stats(img, rw, LEFT_W + 20, 20, LEFT_W + 200, 240)
                print(f'[{label}] anteprima in rotaia: colori={tile[0]}', flush=True)
                pp.save_png(os.path.join(pp.OUT, 'p2_' + label + '.png'), img, rw, RAIL_H, 800)

            snap('A1_prima')
            hidden = [
                ('A2_nascosta', lambda: u.ShowWindow(hwnd, 0), lambda: u.ShowWindow(hwnd, 8)),          # SW_HIDE / SW_SHOWNA
                ('A3_ridotta_a_icona', lambda: u.ShowWindow(hwnd, 6), lambda: u.ShowWindow(hwnd, 9)),   # SW_MINIMIZE / SW_RESTORE
                ('A4_fuori_schermo', lambda: pp.move(hwnd, SW + 100, y, cw, ch), lambda: pp.move(hwnd, x, y, cw, ch)),
            ]
            for label, hide, show in hidden:
                hide()
                snap(label)
                show()
                pp.pump(0.8)
            snap('A5_di_nuovo_in_vista')
            u.ShowWindow(helper, 0)

        if 'b' in sys.argv:
            # (B) input reale dentro la finestra incorporata
            pp.move(pp.HOST, SW - PANEL_W, 0, PANEL_W, SH, pp.SWP_NOZORDER | pp.SWP_NOACTIVATE | pp.SWP_SHOWWINDOW)
            pp.set_thumb(th.value, None, False)
            saved, ok, err = pp.embed(hwnd, 0, 0, PANEL_W - RAIL_W, SH)
            pp.pump(2.0)
            print(f'[B] incorporata: ok={ok} err={err}', flush=True)
            print('[B] input reale (click + digitazione):', pp.interaction_test({'hwnd': hwnd}), flush=True)
            pp.unembed(hwnd, saved)
            saved = None
            pp.pump(0.8)
            print('[B] ripristinata:', pp.rect(hwnd), 'visibile=', bool(u.IsWindowVisible(hwnd)), flush=True)
    finally:
        if saved:
            pp.unembed(hwnd, saved)
        p.kill()
        pp.pump(0.3)
        for h in (pp.HOST, helper):
            if h:
                u.DestroyWindow(h)


if __name__ == '__main__':
    main()
