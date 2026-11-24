# smoke.py -- prova automatica del nucleo di SmartBar con una sola finestra di prova (Blocco note).
#
# Come prova senza disturbare chi usa il PC (qui e in smoke2.py e smoke3.py):
#  - l'app gira come "istanza di prova": nomi, canale di comando e cartella dati separati da quelli dell'utente, e
#    con "--only" riceve in carico solo i processi delle finestre di prova, cosi' non puo' parcheggiare le finestre
#    dell'utente;
#  - parcheggio, apertura, richiusura e rilascio si comandano attraverso il canale di comando dell'istanza, senza
#    mouse. Con "freeze on" il mouse non comanda e le finestre restano come sono state messe;
#  - i passaggi che richiedono il mouse vero (click sul pulsante, passaggio sulla barra) si fanno solo se non si
#    usano mouse e tastiera da almeno 10 secondi, altrimenti vengono saltati e viene detto;
#  - prima di ogni click vero si controlla che sotto il cursore ci sia esattamente la finestra nostra prevista.

import ctypes
import ctypes.wintypes as wt
import os
import re
import subprocess
import sys
import time

import park_probe as pp

u, kernel = pp.user32, pp.kernel32
H, VP = pp.H, pp.VP
u.GetProp = pp.proto(u, 'GetPropW', VP, H, ctypes.c_wchar_p)   # su Windows a 64 bit esiste solo GetPropW
pp.proto(u, 'SetCursorPos', wt.BOOL, ctypes.c_int, ctypes.c_int)
pp.proto(u, 'WindowFromPoint', H, pp.POINT)
pp.proto(u, 'GetWindow', H, H, wt.UINT)
pp.proto(u, 'SetForegroundWindow', wt.BOOL, H)
pp.proto(u, 'BringWindowToTop', wt.BOOL, H)
pp.proto(u, 'AttachThreadInput', wt.BOOL, wt.DWORD, wt.DWORD, wt.BOOL)
pp.proto(u, 'GetForegroundWindow', H)
pp.proto(u, 'GetShellWindow', H)
pp.proto(kernel, 'GetCurrentThreadId', wt.DWORD)
pp.proto(kernel, 'GetTickCount', wt.DWORD)
pp.proto(u, 'GetClassNameW', ctypes.c_int, H, wt.LPWSTR, ctypes.c_int)
pp.proto(u, 'GetAncestor', H, H, wt.UINT)
pp.proto(u, 'PostMessageW', wt.BOOL, H, wt.UINT, wt.WPARAM, wt.LPARAM)


class LASTINPUTINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.UINT), ('dwTime', wt.DWORD)]


pp.proto(u, 'GetLastInputInfo', wt.BOOL, ctypes.POINTER(LASTINPUTINFO))

APP = r'G:\Lab\SmartBar\src\SmartBar\bin\Debug\net10.0-windows\SmartBar.exe'
NOTEPAD = os.path.join(os.environ['LOCALAPPDATA'], 'Microsoft', 'WindowsApps', 'notepad.exe')
TAG = 'smartbar-smoke-%06x' % pp.random.getrandbits(24)
SCREEN_W = u.GetSystemMetrics(0)
PIPE = r'\\.\pipe\SmartBar.Test'
IDLE_MS = 10000          # da quanto tempo l'utente non deve toccare mouse e tastiera per i passaggi col mouse vero
TOOLTIP_CLASS = 'tooltips_class32'
LEFT_DOWN, LEFT_UP, RIGHT_DOWN, RIGHT_UP = 0x0002, 0x0004, 0x0008, 0x0010

ONLY = []        # pid dei processi di prova: l'istanza di prova si occupa solo di questi
APP_PID = 0      # pid dell'istanza di prova: le sue finestre si distinguono da quelle dell'istanza dell'utente
REAL_INPUT = os.environ.get('SMARTBAR_NO_REAL_INPUT') != '1'    # a 0 si prova solo a comandi, accanto all'istanza dell'utente
results = []
skips = []


def check(name, ok, detail=''):
    results.append(ok)
    print(f'[{"PASSA" if ok else "FALLISCE"}] {name}' + (f' -- {detail}' if detail else ''), flush=True)


def skipped(name, why):
    skips.append(name)
    print(f'[SALTATO] {name} -- {why}', flush=True)


def when_idle(name):
    """Si puo' usare il mouse vero? Solo se l'esecuzione lo permette e l'utente non tocca mouse e tastiera da 10 secondi."""
    if REAL_INPUT and idle_ms() >= IDLE_MS:
        return True
    skipped(name, 'mouse vero disattivato in questa esecuzione' if not REAL_INPUT else 'l\'utente sta usando il PC')
    return False


def front(hwnd, name):
    """Mouse vero su una finestra: solo quando si puo', e solo se riesce a portarla in primo piano."""
    if not when_idle(name):
        return False
    if not activate(hwnd):
        skipped(name, 'la finestra di prova non è venuta in primo piano')
        return False
    return True


def idle_ms():
    info = LASTINPUTINFO()
    info.cbSize = ctypes.sizeof(info)
    return (kernel.GetTickCount() - info.dwTime) & 0xFFFFFFFF if u.GetLastInputInfo(ctypes.byref(info)) else 0


def wait_for(fn, timeout, step=0.2):
    end = time.time() + timeout
    while time.time() < end:
        value = fn()
        if value:
            return value
        time.sleep(step)
    return None


def pid_of(hwnd):
    pid = wt.DWORD()
    u.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return pid.value


def start_app(*flags):
    global APP_PID
    app = subprocess.Popen([APP, '--test', '--only=' + ','.join(str(pid) for pid in ONLY), *flags])
    APP_PID = app.pid
    return app


def cmd(*parts):
    """Comando all'istanza di prova, per il suo canale (lo stesso della riga di comando). Restituisce la risposta."""
    request = (' '.join(str(part) for part in parts) + '\n').encode()
    end = time.time() + 5
    while True:
        try:
            with open(PIPE, 'r+b', buffering=0) as pipe:
                pipe.write(request)
                return pipe.readline().decode(errors='replace').strip()
        except OSError:
            # l'app e' ancora impegnata col comando precedente, o sta ancora partendo
            if time.time() >= end:
                raise
            time.sleep(0.05)


def wait_ready(timeout=15):
    def ready():
        try:
            return cmd('state', 0) or None
        except OSError:
            return None
    return wait_for(ready, timeout) is not None


def state_of(hwnd):
    return cmd('state', hwnd)


def button_info(hwnd):
    """Dal comando "measure" (sola lettura): dove sono i pulsanti di sistema della finestra secondo il programma e
    dov'e' il nostro. ((sinistra, alto, destra, basso) dei pulsanti di sistema, quanti sono, rettangolo del nostro
    pulsante adesso, rettangolo del pulsante intero, da ferma e' solo una maniglia)."""
    answer = cmd('measure', hwnd)
    found = re.search(r'pulsanti=\((-?\d+),(-?\d+),(-?\d+),(-?\d+)\)x(\d+)', answer)
    ours = re.search(r'nostro=\((-?\d+),(-?\d+),(-?\d+),(-?\d+)\) intero=\((-?\d+),(-?\d+),(-?\d+),(-?\d+)\) maniglia=(\d)',
                     answer)
    if not (found and ours):
        return None

    def rect(match, first):
        return tuple(int(match.group(first + i)) for i in range(4))

    return rect(found, 1), int(found.group(5)), rect(ours, 1), rect(ours, 5), ours.group(9) == '1'


def attached(hwnd):
    """Il pulsante di parcheggio com'e' adesso, se sta attaccato (due pixel di distanza) a sinistra dei pulsanti di
    sistema della sua finestra: None finche' la misura precisa non e' arrivata."""
    info = button_info(hwnd)
    return info if info and info[3][2] == info[0][0] - 2 else None


def visible_right(hwnd):
    """Il bordo destro della finestra come si vede, senza il bordo invisibile che hanno ai lati le finestre normali."""
    frame = pp.RECT()
    pp.dwmapi.DwmGetWindowAttribute(hwnd, 9, ctypes.byref(frame), 16)
    return frame.right


def is_state(hwnd, state):
    return state_of(hwnd).startswith(state)


def wait_state(hwnd, state, timeout=4.0):
    return wait_for(lambda: state_of(hwnd) if is_state(hwnd, state) else None, timeout, step=0.05) is not None


def off_screen(hwnd):
    return pp.rect(hwnd)[0] >= SCREEN_W


def is_open(hwnd):
    """La finestra occupa davvero metà schermo (una ridotta a icona sta a -32000 e non conta)."""
    x, y, r, b = pp.rect(hwnd)
    return x < SCREEN_W // 2 + 8 and r > SCREEN_W - 150 and b - y > 500


def covers(hwnd, x, y):
    if not hwnd or not u.IsWindowVisible(hwnd):
        return False
    left, top, right, bottom = pp.rect(hwnd)
    return left <= x < right and top <= y < bottom


def point_at(x, y):
    point = pp.POINT()
    point.X, point.Y = x, y
    return point


def guard(x, y, *allowed):
    """Sotto il cursore c'e' una delle finestre nostre previste? Vero o falso: chi chiama decide se rinunciare al click."""
    under = u.WindowFromPoint(point_at(x, y))
    if under in allowed:
        return True

    # sopra puo' esserci il suggerimento del pulsante: i suggerimenti si lasciano attraversare dai click, quindi si
    # guarda quale finestra viene dopo nell'ordine delle finestre e copre quel punto
    classes = {hwnd: cls for hwnd, _pid, _title, cls in pp.top_windows()}
    below = under
    while below and classes.get(below) == TOOLTIP_CLASS:
        below = u.GetWindow(below, 2)        # GW_HWNDNEXT: la finestra subito sotto
        while below and not covers(below, x, y):
            below = u.GetWindow(below, 2)
        if below in allowed:
            return True

    print(f'[NOTA] niente click vero a ({x}, {y}): c\'e\' {describe(under)} (per esempio un programma a schermo intero '
          f'che Windows mette sopra anche alle finestre in primo piano)', flush=True)
    return False


def post_click(hwnd, x, y, button='left'):
    """Click "spedito": i messaggi del mouse vanno dritti alla finestra nostra, senza muovere il cursore.
    Le coordinate sono dentro la finestra. Serve quando il cursore vero non puo' arrivarci (finestra coperta)."""
    lparam = ((y & 0xFFFF) << 16) | (x & 0xFFFF)
    down, up = (0x0201, 0x0202) if button == 'left' else (0x0204, 0x0205)   # WM_xBUTTONDOWN, WM_xBUTTONUP
    u.PostMessageW(hwnd, down, 1, lparam)
    time.sleep(0.05)
    u.PostMessageW(hwnd, up, 0, lparam)


def describe(hwnd):
    """Finestra, classe e finestra radice: per capire chi si e' messo sotto il cursore."""
    if not hwnd:
        return 'nessuna finestra'
    name = ctypes.create_unicode_buffer(256)
    u.GetClassNameW(hwnd, name, 256)
    root = u.GetAncestor(hwnd, 2)        # GA_ROOT
    return f'la finestra {hwnd} [{name.value}] dentro {root}'


def send(flags):
    arr = (pp.INPUT * 1)()
    arr[0].type = pp.INPUT_MOUSE
    arr[0].mi.dwFlags = flags
    u.SendInput(1, arr, ctypes.sizeof(pp.INPUT))


def click(button='left'):
    down, up = (LEFT_DOWN, LEFT_UP) if button == 'left' else (RIGHT_DOWN, RIGHT_UP)
    send(down)
    time.sleep(0.05)
    send(up)


def activate(hwnd):
    """Porta una finestra in primo piano come farebbe Alt-Tab. Vero se ci e' riuscita."""
    pp.pump(0.01)        # serve una coda di messaggi: senno' l'aggancio delle code di input fallisce
    foreground = u.GetForegroundWindow()
    mine = kernel.GetCurrentThreadId()
    other = u.GetWindowThreadProcessId(foreground, None) if foreground else 0
    attached = bool(other) and other != mine and bool(u.AttachThreadInput(mine, other, True))
    u.BringWindowToTop(hwnd)
    u.SetForegroundWindow(hwnd)
    if attached:
        u.AttachThreadInput(mine, other, False)
    time.sleep(0.2)
    return u.GetForegroundWindow() == hwnd


def app_windows():
    """(pulsanti: finestra -> pulsante nostro, barra, scheda): solo quelli dell'istanza di prova."""
    buttons, rail, card = {}, 0, 0
    for hwnd, pid, _title, _cls in pp.top_windows():
        if pid != APP_PID:
            continue
        target = u.GetProp(hwnd, 'SmartBar.Target')
        if target:
            buttons[target] = hwnd
        elif u.GetProp(hwnd, 'SmartBar.Rail'):
            rail = hwnd
        elif u.GetProp(hwnd, 'SmartBar.Card'):
            card = hwnd
    return buttons, rail, card


def rail_visible():
    rail = app_windows()[1]
    return bool(rail) and bool(u.IsWindowVisible(rail))


def rail_center():
    x, y, r, b = pp.rect(app_windows()[1])
    return (x + r) // 2, (y + b) // 2


def shot(name, x, y, w, h):
    pp.save_png(os.path.join(pp.OUT, name), pp.capture(x, y, w, h), w, h, 1100)


def clean_notepad_sessions(sample):
    """Blocco note salva le schede aperte: tolgo le sessioni nate durante la prova che puntano al file di prova."""
    tab = os.path.join(os.environ['LOCALAPPDATA'], 'Packages', 'Microsoft.WindowsNotepad_8wekyb3d8bbwe', 'LocalState', 'TabState')
    if not os.path.isdir(tab):
        return
    since = time.time() - 600
    for name in os.listdir(tab):
        path = os.path.join(tab, name)
        try:
            if os.path.getmtime(path) >= since and sample.encode('utf-16le') in open(path, 'rb').read():
                os.remove(path)
                print('rimossa sessione di prova di Blocco note', name, flush=True)
        except OSError:
            pass


def finish_test(log_lines=40):
    log_path = os.path.join(os.environ['LOCALAPPDATA'], 'SmartBar.Test', 'log.txt')
    if os.path.exists(log_path):
        print('--- log dell\'istanza di prova ---', flush=True)
        print('\n'.join(open(log_path, encoding='utf-8', errors='replace').read().splitlines()[-log_lines:]), flush=True)
    print(f'\n{sum(results)}/{len(results)} controlli passati, {len(skips)} saltati', flush=True)


def park(hwnd):
    """Parcheggia la finestra: col pulsante col mouse vero se l'utente e' fermo, altrimenti col comando."""
    if front(hwnd, 'parcheggio col pulsante, click vero'):
        button = wait_for(lambda: app_windows()[0].get(hwnd), 6)
        if not button:
            return False
        bx, by, br, bb = pp.rect(button)
        cx, cy = br - 2, (by + bb) // 2      # maniglia del pulsante (4 pixel larghi)
        u.SetCursorPos(cx, cy)
        time.sleep(0.3)
        if guard(cx, cy, button):
            click()
            time.sleep(1.2)
    return cmd('park', hwnd) == 'ok' and off_screen(hwnd)


def main():
    global ONLY
    os.makedirs(pp.OUT, exist_ok=True)
    log_path = os.path.join(os.environ['LOCALAPPDATA'], 'SmartBar.Test', 'log.txt')
    if os.path.exists(log_path):
        os.remove(log_path)

    base = pp.tasklist_pids()
    # sola prova a comandi (mouse vero disattivato) puo' girare accanto all'istanza dell'utente
    assert not base.get('smartbar.exe') or not REAL_INPUT, 'SmartBar è già in esecuzione: chiuderla prima della prova'
    before = base.get('notepad.exe', set())
    sample = os.path.join(pp.OUT, TAG + '.txt')
    with open(sample, 'w', encoding='utf-8-sig') as f:
        f.write('\n'.join([TAG, 'Finestra di prova di SmartBar.']
                          + ['Riga %d: lorem ipsum dolor sit amet, consectetur adipiscing elit.' % i for i in range(3, 40)]))

    app = None
    notepad = 0
    try:
        subprocess.Popen([NOTEPAD, sample])
        notepad = wait_for(lambda: next((h for h, _p, t, c in pp.top_windows() if TAG in t and c == 'Notepad'), 0), 30)
        check('finestra di prova aperta', bool(notepad))
        if not notepad:
            raise RuntimeError('finestra di prova non aperta')

        ONLY = [pid_of(notepad)]
        app = start_app()
        check('SmartBar (istanza di prova) si avvia e risponde ai comandi', wait_ready())

        button = wait_for(lambda: app_windows()[0].get(notepad), 10)
        check('il nostro pulsante compare accanto a Riduci a icona', bool(button), f'pulsante={pp.rect(button) if button else None}')

        # dove lo mette: attaccato ai pulsanti di sistema veri, che finiscono al bordo destro visibile della finestra
        info = wait_for(lambda: attached(notepad), 6)
        check('il pulsante sta attaccato (due pixel) a sinistra dei pulsanti di sistema della finestra', info is not None,
              f'misura={button_info(notepad)}')
        if info:
            (bl, bt, br_, bb_), count, now, full, handle = info
            edge = visible_right(notepad)
            check('i pulsanti di sistema finiscono al bordo destro visibile e sono tre da 46 pixel',
                  abs(br_ - edge) <= 2 and 136 <= br_ - bl <= 140 and count == 3,
                  f'pulsanti di sistema=({bl},{bt},{br_},{bb_}) bordo visibile={edge}')
            check("il nostro pulsante è largo e alto come uno di quelli", abs((full[2] - full[0]) - (br_ - bl) // 3) <= 1
                  and full[3] - full[1] == bb_ - bt, f'intero={full} sistema alto {bb_ - bt}')
            check('Blocco note ha le schede subito a sinistra: da ferma si vede solo la maniglia', handle
                  and now[2] - now[0] <= 10 and now[2] == full[2], f'adesso={now}')

        # il pulsante segue la finestra
        pp.move(notepad, 500, 300, 1100, 700)
        time.sleep(1.0)
        x, y, r, b = pp.rect(notepad)
        button = wait_for(lambda: app_windows()[0].get(notepad), 5)
        check('dopo lo spostamento il pulsante c\'è ancora', bool(button))
        if not button:
            raise RuntimeError('pulsante sparito dopo lo spostamento')
        info = wait_for(lambda: attached(notepad), 6)
        bx, by, br, bb = pp.rect(button)
        # finestra normale: il bordo visibile sta sette pixel dentro quello vero; tre pulsanti da 46, due di distanza
        check('il pulsante segue la finestra', info is not None and abs(br - (r - 7 - 138 - 2)) <= 3 and abs(by - y) <= 2,
              f'finestra={pp.rect(notepad)} pulsante={pp.rect(button)} misura={button_info(notepad)}')
        shot('smoke_1_pulsante.png', r - 520, y - 8, 520, 90)
        original = pp.rect(notepad)

        # parcheggio
        check('parcheggiata', park(notepad), f'rettangolo={pp.rect(notepad)}')
        parked = pp.rect(notepad)
        check('la finestra è oltre il bordo destro', parked[0] >= SCREEN_W, f'rettangolo={parked}')
        buttons, rail, _card = app_windows()
        rail_rect = pp.rect(rail) if rail else None
        check('la barra è visibile e stretta', bool(rail) and bool(u.IsWindowVisible(rail)) and rail_rect[2] - rail_rect[0] <= 90,
              f'barra={rail_rect}')
        check('il pulsante è sparito dalla finestra parcheggiata', notepad not in buttons)
        shot('smoke_2_barra.png', rail_rect[0] - 20, rail_rect[1] - 20, rail_rect[2] - rail_rect[0] + 40,
             rail_rect[3] - rail_rect[1] + 40)

        # apertura e richiusura comandate
        cmd('freeze', 'on')
        cmd('open', notepad)
        check('aperta: occupa metà schermo', wait_state(notepad, 'Expanded') and pp.rect(notepad)[0] < SCREEN_W // 2 + 8
              and pp.rect(notepad)[2] > SCREEN_W - 100 and pp.rect(notepad)[1] <= 0, f'rettangolo={pp.rect(notepad)}')
        shot('smoke_4_aperta.png', 1200, 0, 1360, 430)
        cmd('collapse-all')
        check('richiusa: torna oltre il bordo', wait_state(notepad, 'Collapsed') and off_screen(notepad), f'rettangolo={pp.rect(notepad)}')
        cmd('freeze', 'off')

        # passaggio sulla tessera col mouse vero (solo se l'utente non sta usando il PC)
        cx, cy = rail_center()
        if when_idle('passaggio sulla tessera, scheda e richiusura col mouse vero') and guard(cx, cy, rail):
            u.SetCursorPos(cx, cy)
            time.sleep(0.12)      # la scheda si vede solo prima che la finestra scorra fuori (sosta di 250 millisecondi)
            _buttons, _rail, card = app_windows()
            card_rect = pp.rect(card) if card and u.IsWindowVisible(card) else None
            check('passando sulla tessera compare la scheda con l\'anteprima grande', bool(card_rect), f'scheda={card_rect}')
            if card_rect:
                shot('smoke_3_scheda.png', card_rect[0] - 20, min(card_rect[1], rail_rect[1]) - 20,
                     rail_rect[2] - card_rect[0] + 40, max(card_rect[3], rail_rect[3]) - min(card_rect[1], rail_rect[1]) + 40)
            time.sleep(0.8)
            opened = pp.rect(notepad)
            check('fermandosi sulla tessera la finestra si apre', opened[0] < SCREEN_W // 2 + 8, f'rettangolo={opened}')
            u.SetCursorPos(300, 700)
            time.sleep(1.0)
            check('quando il mouse esce la finestra rientra nella barra', off_screen(notepad), f'rettangolo={pp.rect(notepad)}')

        # uscita dal parcheggio
        check('comando "release": la finestra esce dal parcheggio e torna dov\'era',
              cmd('release', notepad) == 'ok' and pp.rect(notepad) == original, f'era={original} ora={pp.rect(notepad)}')
        check('il pulsante di parcheggio è tornato', wait_for(lambda: app_windows()[0].get(notepad), 6) is not None)
        check('la barra si nasconde senza finestre parcheggiate', not rail_visible())

        cmd('exit')
        app.wait(timeout=10)
        check('comando "exit": chiude l\'app', True)
    finally:
        if app is not None and app.poll() is None:
            app.kill()
        now = pp.tasklist_pids().get('notepad.exe', set()) - before
        for pid in now:
            if pp.image_of_pid(pid) == 'notepad.exe':
                subprocess.run(['taskkill', '/PID', str(pid), '/T', '/F'], capture_output=True)
        time.sleep(1.0)
        try:
            os.remove(sample)     # il file di prova: ora che Blocco note e' chiuso si puo' togliere
        except OSError:
            pass
        clean_notepad_sessions(sample)
        finish_test()


if __name__ == '__main__':
    main()
    sys.exit(0 if all(results) and results else 1)
