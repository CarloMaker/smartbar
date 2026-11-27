# smoke7.py -- settima prova automatica di SmartBar: la striscia della barra, tolta alle finestre come fa la barra di Windows.
#   senza finestre parcheggiate lo spazio di lavoro non cambia; parcheggiata la prima finestra la barra occupa la sua
#   striscia del bordo per tutta l'altezza, e una finestra ingrandita si ferma prima; cambiando lato la striscia passa
#   sull'altro bordo; rilasciata l'ultima finestra parcheggiata lo spazio di lavoro torna com'era, e una finestra
#   ingrandita ripresa dal parcheggio e' subito della misura giusta.
# Con un programma a schermo intero non c'e' niente da provare qui: e' Windows che lo mette sopra anche alle finestre
# sempre in primo piano, barra compresa (vedi fullscreen_probe.py).
# L'istanza di prova si avvia con "--reserve": solo cosi occupa la striscia. Per la durata della prova (una decina di
# secondi) Windows stringe anche le finestre ingrandite di chi usa il PC, quindi la prova non parte finche' l'utente non
# lascia fermi mouse e tastiera per 10 secondi.
# Per il resto vale quanto le altre prove: solo finestre di prova (Blocco note e Mappa caratteri), comandi all'istanza
# di prova. Per le misure di sicurezza vedere smoke.py.

import ctypes
import os
import subprocess
import sys
import time

import park_probe as pp
import smoke as s

u = s.u
CHARMAP = r'C:\Windows\System32\charmap.exe'
SW_MAXIMIZE = 3
TAG = 'smartbar-smoke7-%06x' % pp.random.getrandbits(24)

u.ShowWindow = pp.proto(u, 'ShowWindow', ctypes.c_int, ctypes.c_void_p, ctypes.c_int)
u.IsZoomed = pp.proto(u, 'IsZoomed', ctypes.c_int, ctypes.c_void_p)


def same(rect, expected):
    """Una finestra ingrandita copre lo spazio di lavoro atteso (otto pixel di tolleranza per lato: le cornici di Windows
    sporgono di un paio di pixel oltre lo spazio; si deve vedere se mancano gli 84 della barra)."""
    return bool(rect) and all(abs(a - b) <= 8 for a, b in zip(rect, expected))


def idle(name):
    """Si puo' stringere lo spazio di lavoro di chi usa il PC? Solo se non tocca mouse e tastiera da 10 secondi.
    (Non vale s.when_idle: quello dipende anche da SMARTBAR_NO_REAL_INPUT, che qui non c'entra, non si manda input.)"""
    if s.idle_ms() >= s.IDLE_MS:
        return True
    s.skipped(name, 'l\'utente sta usando il PC')
    return False


def main():
    """Falso se la prova non si e' potuta fare (l'utente stava usando il PC)."""
    s.ONLY = []
    if not idle('prova della striscia riservata'):
        print('La prova non si e' + chr(39) + ' fatta: si rilancia quando il PC e' + chr(39) + ' libero.')
        return False

    base = pp.tasklist_pids()
    assert not base.get('smartbar.exe') or not s.REAL_INPUT, 'SmartBar è già in esecuzione: chiuderla prima della prova'
    os.makedirs(pp.OUT, exist_ok=True)
    sample = os.path.join(pp.OUT, TAG + '.txt')
    with open(sample, 'w', encoding='utf-8-sig') as f:
        f.write('\n'.join([TAG] + ['Riga %d: lorem ipsum dolor sit amet.' % i for i in range(3, 40)]))

    app = None
    probe = None
    charmap = 0
    try:
        subprocess.Popen([s.NOTEPAD, sample])
        notepad = s.wait_for(lambda: next((h for h, _p, t, c in pp.top_windows() if TAG in t and c == 'Notepad'), 0), 30)
        probe = subprocess.Popen([CHARMAP])
        charmap, _pid = pp.wait_for(lambda pid, title, _cls: pid == probe.pid and bool(title), 30)    # visibile, non solo creata
        s.check('finestre di prova aperte (Blocco note e Mappa caratteri)', bool(notepad) and bool(charmap),
                f'blocco note={bool(notepad)} mappa caratteri={bool(charmap)}')
        if not notepad or not charmap:
            raise RuntimeError('finestre di prova non aperte')

        # Blocco note riapre la finestra dov'era rimasta l'ultima volta: la si mette sullo schermo principale
        pp.move(notepad, 700, 150, 1100, 700)
        time.sleep(1.0)
        before = s.work_area()        # puo' esserci gia' la striscia dell'istanza dell'utente: i confronti sono relativi a questo

        s.ONLY = [s.pid_of(notepad), s.pid_of(charmap)]
        s.RESERVES = True
        app = s.start_app('--reserve')
        s.check('SmartBar (istanza di prova con --reserve) si avvia e risponde ai comandi', s.wait_ready())
        s.use_side('left')        # questa prova parte con la barra a sinistra
        s.check('senza finestre parcheggiate non c\'e\' la barra e lo spazio di lavoro resta com\'era',
                not s.rail_visible() and s.work_area() == before, f'spazio di lavoro={s.work_area()} com\'era={before}')

        u.ShowWindow(notepad, SW_MAXIMIZE)
        time.sleep(0.8)
        s.check('la finestra di prova ingrandita copre tutto lo spazio di lavoro', same(s.frame_rect(notepad), before),
                f'visibile={s.frame_rect(notepad)} spazio di lavoro={before}')

        # --- la barra occupa la sua striscia ---
        s.check('parcheggiata un\'altra finestra', s.cmd('park', charmap) == 'ok' and s.off_screen(charmap),
                f'rettangolo={pp.rect(charmap)}')
        taken = (before[0] + s.RAIL_W, before[1], before[2], before[3])       # lo spazio di lavoro senza la striscia a sinistra
        s.check('Windows toglie la striscia della barra alle finestre: lo spazio di lavoro si stringe di 84 pixel a sinistra',
                s.work_area() == taken, f'prima={before} ora={s.work_area()}')
        s.check('la barra copre tutta la striscia: dallo spazio di lavoro in alto fino alla barra di Windows',
                s.rail_rect() == (before[0], before[1], before[0] + s.RAIL_W, before[3]), f'barra={s.rail_rect()}')
        s.check('la finestra ingrandita si e\' fermata prima della barra: non ci passa sotto',
                same(s.frame_rect(notepad), taken), f'visibile={s.frame_rect(notepad)} atteso={taken}')

        s.cmd('freeze', 'on')
        s.cmd('open', charmap)
        s.check('la finestra parcheggiata aperta riempie lo spazio accanto alla barra',
                s.wait_state(charmap, 'Expanded') and s.is_open(charmap), f'rettangolo={pp.rect(charmap)} atteso={s.panel_rect()}')
        s.cmd('collapse-all')
        s.wait_state(charmap, 'Collapsed')

        # --- l'altro lato ---
        s.use_side('right')
        taken_right = (before[0], before[1], before[2] - s.RAIL_W, before[3])
        s.check('cambio lato: la striscia passa sul bordo destro',
                s.work_area() == taken_right and s.rail_rect() == (before[2] - s.RAIL_W, before[1], before[2], before[3]),
                f'spazio di lavoro={s.work_area()} barra={s.rail_rect()}')
        s.check('e la finestra ingrandita si ferma prima anche di li', same(s.frame_rect(notepad), taken_right),
                f'visibile={s.frame_rect(notepad)} atteso={taken_right}')
        s.use_side('left')
        s.check('tornando a sinistra la striscia torna sul bordo sinistro', s.work_area() == taken
                and s.rail_rect() == (before[0], before[1], before[0] + s.RAIL_W, before[3]),
                f'spazio di lavoro={s.work_area()} barra={s.rail_rect()}')

        # --- l'ultima finestra parcheggiata se ne va ---
        s.cmd('release', charmap)
        s.check('rilasciata l\'ultima finestra parcheggiata la barra sparisce e lo spazio di lavoro torna com\'era',
                not s.rail_visible() and s.work_area() == before, f'spazio di lavoro={s.work_area()} com\'era={before}')
        s.check('e la finestra ingrandita torna a coprire tutto lo spazio di lavoro', same(s.frame_rect(notepad), before),
                f'visibile={s.frame_rect(notepad)} spazio di lavoro={before}')

        # --- una finestra ingrandita parcheggiata e ripresa ---
        s.check('parcheggiata anche la finestra ingrandita', s.cmd('park', notepad) == 'ok' and s.off_screen(notepad)
                and s.work_area() == taken, f'rettangolo={pp.rect(notepad)} spazio di lavoro={s.work_area()}')
        s.cmd('release', notepad)
        s.check('ripresa subito: e\' di nuovo ingrandita a tutta larghezza, senza doversi riallargare dopo',
                bool(u.IsZoomed(notepad)) and same(s.frame_rect(notepad), before) and s.work_area() == before,
                f'ingrandita={bool(u.IsZoomed(notepad))} visibile={s.frame_rect(notepad)} spazio di lavoro={s.work_area()}')

        s.cmd('exit')
        app.wait(timeout=10)
        s.check('comando "exit": chiude l\'app', True)
        s.check('chiusa l\'app lo spazio di lavoro resta com\'era', s.work_area() == before, f'spazio di lavoro={s.work_area()}')
    finally:
        if app is not None and app.poll() is None:
            app.kill()
        if probe is not None and probe.poll() is None:
            subprocess.run(['taskkill', '/PID', str(probe.pid), '/T', '/F'], capture_output=True)
        for hwnd, pid, title, cls in pp.top_windows():
            if TAG in title and cls == 'Notepad':
                subprocess.run(['taskkill', '/PID', str(pid), '/T', '/F'], capture_output=True)
        time.sleep(0.5)
        try:
            os.remove(sample)
        except OSError:
            pass
        s.clean_notepad_sessions(TAG)
        s.finish_test()
    return True


if __name__ == '__main__':
    ran = main()
    sys.exit(0 if not ran or (all(s.results) and s.results) else 1)
