# probe3.py -- terzo giro: si puo' mettere un pulsante nostro accanto a "Riduci a icona" nelle finestre di altri
# programmi senza toccare quei programmi? Il pulsante e' una finestrella nostra incollata alla finestra e al suo
# posto nell'ordine delle finestre. Finestre di prova: Blocco note, Mappa caratteri, Edge (profilo temporaneo).

import ctypes
import ctypes.wintypes as wt
import os
import shutil
import subprocess
import time

import park_probe as pp

u, g, d, k = pp.user32, pp.gdi32, pp.dwmapi, pp.kernel32
VP, RECT = pp.VP, pp.RECT

DWMWA_CAPTION_BUTTON_BOUNDS = 5
EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_NAMECHANGE = 0x0003, 0x800C
EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND = 0x000A, 0x000B
EVENT_OBJECT_DESTROY = 0x8007
WINEVENT_SKIPOWNPROCESS = 2
GW_HWNDPREV = 3
SW_MAXIMIZE, SW_MINIMIZE = 3, 6
BUTTON_W, BUTTON_H, GAP = 46, 32, 4

WINEVENTPROC = ctypes.WINFUNCTYPE(None, wt.HANDLE, wt.DWORD, wt.HWND, wt.LONG, wt.LONG, wt.DWORD, wt.DWORD)
pp.proto(u, 'SetWinEventHook', wt.HANDLE, wt.UINT, wt.UINT, wt.HANDLE, WINEVENTPROC, wt.DWORD, wt.DWORD, wt.UINT)
pp.proto(u, 'UnhookWinEvent', wt.BOOL, wt.HANDLE)
pp.proto(u, 'GetWindow', pp.H, pp.H, wt.UINT)
pp.proto(u, 'SetForegroundWindow', wt.BOOL, pp.H)
pp.proto(u, 'BringWindowToTop', wt.BOOL, pp.H)
pp.proto(u, 'AttachThreadInput', wt.BOOL, wt.DWORD, wt.DWORD, wt.BOOL)
pp.proto(k, 'GetCurrentThreadId', wt.DWORD)

TRACKED = {}      # hwnd finestra di prova -> hwnd pulsante
NAMES = {}        # hwnd finestra di prova -> nome
MOVING = set()
OVERLAY_BRUSH = None
OVERLAY_CLASS = 'ParkProbeButton_' + pp.TAG


def _overlay_proc(hwnd, msg, wparam, lparam):
    if msg == pp.WM_PAINT:
        ps = pp.PAINTSTRUCT()
        hdc = u.BeginPaint(hwnd, ctypes.byref(ps))
        rc = RECT()
        u.GetClientRect(hwnd, ctypes.byref(rc))
        u.FillRect(hdc, ctypes.byref(rc), OVERLAY_BRUSH)
        u.EndPaint(hwnd, ctypes.byref(ps))
        return 0
    return u.DefWindowProcW(hwnd, msg, wparam, lparam)


OVERLAY_CB = pp.WNDPROC(_overlay_proc)


def hide(overlay):
    u.ShowWindow(overlay, 0)


def caption_buttons(target):
    """Rettangolo (schermo) dei pulsanti Riduci/Ingrandisci/Chiudi, oppure None se Windows non lo riporta."""
    br = RECT()
    hr = d.DwmGetWindowAttribute(target, DWMWA_CAPTION_BUTTON_BOUNDS, ctypes.byref(br), ctypes.sizeof(RECT))
    if hr < 0 or br.right <= br.left or br.bottom <= br.top:
        return None, hr
    x, y, r, b = pp.rect(target)
    if br.right <= r - x:        # coordinate relative alla finestra
        return (x + br.left, y + br.top, x + br.right, y + br.bottom), hr
    return (br.left, br.top, br.right, br.bottom), hr   # coordinate schermo


def place(target, overlay):
    """Pulsante subito a sinistra di 'Riduci a icona', nell'ordine delle finestre proprio sopra la sua."""
    x, y, r, b = pp.rect(target)
    bounds, _ = caption_buttons(target)
    if bounds:
        left = bounds[0] - BUTTON_W - GAP
        top = bounds[1] + (bounds[3] - bounds[1] - BUTTON_H) // 2
    else:
        # ripiego: tre pulsanti da 46 px attaccati al bordo destro, in alto
        left, top = r - 4 * BUTTON_W - GAP, y + 6
    above = u.GetWindow(target, GW_HWNDPREV)
    flags = pp.SWP_NOACTIVATE | pp.SWP_SHOWWINDOW
    if above == overlay:
        # gia' subito sopra la finestra: una finestra non puo' essere inserita dopo se stessa
        flags |= pp.SWP_NOZORDER
        above = None
    u.SetWindowPos(overlay, above, left, top, BUTTON_W, BUTTON_H, flags)


def on_event(hook, event, hwnd, id_object, id_child, thread, when):
    overlay = TRACKED.get(hwnd)
    if not overlay:
        return
    if event == EVENT_SYSTEM_MOVESIZESTART:
        MOVING.add(hwnd)
        hide(overlay)
    elif event == EVENT_SYSTEM_MOVESIZEEND:
        MOVING.discard(hwnd)
        place(hwnd, overlay)
    elif event == EVENT_OBJECT_DESTROY:
        hide(overlay)
        TRACKED.pop(hwnd, None)
    elif hwnd not in MOVING:
        if u.IsIconic(hwnd) or not u.IsWindowVisible(hwnd):
            hide(overlay)
        else:
            place(hwnd, overlay)


HOOK_CB = WINEVENTPROC(on_event)


def activate(hwnd):
    fg = u.GetForegroundWindow()
    mine = k.GetCurrentThreadId()
    fg_tid = u.GetWindowThreadProcessId(fg, None) if fg else 0
    attached = bool(fg_tid) and fg_tid != mine
    if attached:
        u.AttachThreadInput(mine, fg_tid, True)
    u.SetForegroundWindow(hwnd)
    u.BringWindowToTop(hwnd)
    if attached:
        u.AttachThreadInput(mine, fg_tid, False)


def report(target, label):
    overlay = TRACKED[target]
    bounds, _ = caption_buttons(target)
    print(f'   [{label}] finestra={pp.rect(target)} pulsanti_di_sistema={bounds} nostro={pp.rect(overlay)} '
          f'visibile={bool(u.IsWindowVisible(overlay))}', flush=True)


def crop(target, fname, height=96):
    x, y, r, b = pp.rect(target)
    pp.save_png(os.path.join(pp.OUT, fname), pp.capture(r - 560, max(0, y - 8), 560, height), 560, height, 1000)


def main():
    global OVERLAY_BRUSH
    u.SetProcessDpiAwarenessContext(VP(-4))
    OVERLAY_BRUSH = g.CreateSolidBrush(0x0000A5FF)
    wc = pp.WNDCLASSEXW()
    wc.cbSize = ctypes.sizeof(pp.WNDCLASSEXW)
    wc.lpfnWndProc = OVERLAY_CB
    wc.hInstance = k.GetModuleHandleW(None)
    wc.hbrBackground = OVERLAY_BRUSH
    wc.lpszClassName = OVERLAY_CLASS
    u.RegisterClassExW(ctypes.byref(wc))
    hook = u.SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_NAMECHANGE, None, HOOK_CB, 0, 0,
                             WINEVENT_SKIPOWNPROCESS)
    assert hook, 'SetWinEventHook fallita'

    base = pp.tasklist_pids()
    txt = os.path.join(pp.OUT, pp.TAG + '.txt')
    html = os.path.join(pp.OUT, pp.TAG + '.html')
    with open(txt, 'w', encoding='utf-8-sig') as f:
        f.write('\n'.join([pp.TAG, 'Finestra di prova per il pulsante.']
                          + ['Riga %d: lorem ipsum dolor sit amet, consectetur adipiscing elit.' % i for i in range(3, 40)]))
    with open(html, 'w', encoding='utf-8') as f:
        f.write(pp.HTML % pp.TAG)
    profile = os.path.join(pp.OUT, 'edge-profile')
    devnull = dict(stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    notepad_exe = os.path.join(os.environ['LOCALAPPDATA'], 'Microsoft', 'WindowsApps', 'notepad.exe')
    procs = {
        'notepad': subprocess.Popen([notepad_exe, txt], **devnull),
        'charmap': subprocess.Popen([r'C:\Windows\System32\charmap.exe'], **devnull),
        'edge': subprocess.Popen([r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
                                  '--user-data-dir=' + profile, '--no-first-run', '--no-default-browser-check',
                                  '--disable-sync', '--window-position=150,150', '--window-size=1200,1000',
                                  'file:///' + html.replace('\\', '/')], **devnull),
    }
    specs = [
        ('notepad', 'notepad.exe', 30, lambda pid, title, cls: pp.TAG in title and cls == 'Notepad'),
        ('charmap', 'charmap.exe', 15, lambda pid, title, cls: pid == procs['charmap'].pid),
        ('edge', 'msedge.exe', 60, lambda pid, title, cls: pp.TAG in title and cls == 'Chrome_WidgetWin_1'),
    ]
    kill = {name: {procs[name].pid} for name, _image, _timeout, _pred in specs}
    try:
        for name, image, timeout, pred in specs:
            hwnd, pid = pp.wait_for(pred, timeout)
            if not hwnd:
                print(f'[probe3] {name}: finestra non trovata, salto', flush=True)
                continue
            if pid in base.get(image, set()) or pid not in pp.tasklist_pids().get(image, set()):
                print(f'[probe3] {name}: finestra non verificata come di prova, salto', flush=True)
                continue
            kill[name].add(pid)
            # WS_EX_NOACTIVATE (0x08000000): un click sul pulsante non toglie l'attivazione alla finestra
            TRACKED[hwnd] = u.CreateWindowExW(pp.WS_EX_TOOLWINDOW | 0x08000000, OVERLAY_CLASS,
                                              'ParkProbeButton', pp.WS_POPUP, 0, 0, BUTTON_W, BUTTON_H, None, None,
                                              k.GetModuleHandleW(None), None)
            NAMES[hwnd] = name
        pp.pump(4.0)
        by_name = {name: hwnd for hwnd, name in NAMES.items()}
        for target, overlay in list(TRACKED.items()):
            name = NAMES[target]
            print(f'[probe3] {name}: hwnd={target} rettangolo={pp.rect(target)}', flush=True)
            activate(target)
            pp.pump(0.8)
            place(target, overlay)
            pp.pump(0.8)
            report(target, 'a_riposo')
            crop(target, f'p3_{name}_a_riposo.png')
            x, y, r, b = pp.rect(target)
            pp.move(target, x + 180, y + 120, (r - x) + 140, b - y)
            pp.pump(1.0)
            report(target, 'b_spostata')
            crop(target, f'p3_{name}_b_spostata.png')
            u.ShowWindow(target, SW_MAXIMIZE)
            pp.pump(1.5)
            report(target, 'c_massimizzata')
            crop(target, f'p3_{name}_c_massimizzata.png')
            u.ShowWindow(target, pp.SW_RESTORE)
            pp.pump(1.2)
            u.ShowWindow(target, SW_MINIMIZE)
            pp.pump(1.2)
            report(target, 'd_ridotta_a_icona')
            u.ShowWindow(target, pp.SW_RESTORE)
            pp.pump(1.5)
            report(target, 'e_ripristinata')

        # ordine delle finestre: un'altra finestra copre la barra del titolo di Blocco note -> il pulsante resta sotto
        if 'notepad' in by_name and 'charmap' in by_name:
            notepad, charmap = by_name['notepad'], by_name['charmap']
            button = TRACKED[notepad]
            x, y, r, b = pp.rect(notepad)
            pp.move(charmap, r - 620, max(0, y - 10), 491, 437)
            activate(charmap)
            pp.pump(1.0)
            print(f'[ordine] charmap sopra Blocco note: pulsante di Blocco note visibile='
                  f'{bool(u.IsWindowVisible(button))} rettangolo={pp.rect(button)}', flush=True)
            pp.save_png(os.path.join(pp.OUT, 'p3_ordine_coperta.png'), pp.capture(r - 560, max(0, y - 8), 560, 300),
                        560, 300, 1000)
            activate(notepad)
            pp.pump(1.0)
            print(f'[ordine] Blocco note di nuovo sopra: rettangolo={pp.rect(button)}', flush=True)
            pp.save_png(os.path.join(pp.OUT, 'p3_ordine_scoperta.png'), pp.capture(r - 560, max(0, y - 8), 560, 300),
                        560, 300, 1000)
    finally:
        u.UnhookWinEvent(hook)
        for overlay in list(TRACKED.values()):
            u.DestroyWindow(overlay)
        for (name, image, _timeout, _pred), pids in zip(specs, kill.values()):
            for pid in pids:
                if pp.image_of_pid(pid) == image:
                    subprocess.run(['taskkill', '/PID', str(pid), '/T', '/F'], capture_output=True)
        # Edge a volte si rilancia con un pid nuovo: chiudi comunque l'istanza creata col profilo temporaneo
        subprocess.run(['powershell', '-NoProfile', '-Command',
                        'Get-CimInstance Win32_Process | Where-Object { $_.Name -eq "msedge.exe" '
                        '-and $_.CommandLine -notmatch "--type=" -and $_.CommandLine -like "' + profile + '*" } '
                        '| ForEach-Object { taskkill.exe /PID $_.ProcessId /T /F }'], capture_output=True)
        time.sleep(1.5)
        shutil.rmtree(profile, ignore_errors=True)
        left = sorted({s[1] for s in specs if pp.tasklist_pids().get(s[1])})
        print('[probe3] pulizia fatta; processi di prova rimasti:', left or 'nessuno', flush=True)


if __name__ == '__main__':
    main()
