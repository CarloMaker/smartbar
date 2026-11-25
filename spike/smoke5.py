# smoke5.py -- quinta prova automatica di SmartBar: con due schermi.
#   una finestra che sta sul secondo schermo (anche di scala diversa) parcheggiata non si vede su nessuno schermo,
#   aperta riempie lo spazio accanto alla barra sullo schermo principale, rilasciata torna esattamente dov'era.
# Gira solo se ci sono almeno due schermi; con uno solo avvisa ed esce senza fallire.
# Tutto per comandi all'istanza di prova, senza mouse vero: si puo' girare accanto all'istanza dell'utente.
# Questo processo si dichiara "a scala per-monitor" prima di tutto: cosi' le coordinate sono pixel veri su ogni
# schermo (altrimenti Windows gliele fa vedere ridotte, sugli schermi con scala diversa da quella di sistema).
# Per le misure di sicurezza vedere smoke.py.

import ctypes
import os
import subprocess
import sys
import time

ctypes.WinDLL('shcore').SetProcessDpiAwareness(2)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import park_probe as pp
import smoke as s

TAG = 'smartbar-smoke5-%06x' % pp.random.getrandbits(24)
PLACE = (3000, 200, 1200, 900)      # dove mettere la finestra di prova: sullo schermo a destra del principale


def main():
    s.ONLY = []
    screens = s.monitors()
    if len(screens) < 2:
        print('un solo schermo: niente da provare qui')
        return
    if not any(left >= s.SCREEN_W for left, _t, _r, _b in screens):
        print('nessuno schermo a destra del principale: niente da provare qui')
        return

    base = pp.tasklist_pids()
    assert not base.get('smartbar.exe') or not s.REAL_INPUT, 'SmartBar è già in esecuzione: chiuderla prima della prova'
    os.makedirs(pp.OUT, exist_ok=True)
    sample = os.path.join(pp.OUT, TAG + '.txt')
    with open(sample, 'w', encoding='utf-8-sig') as f:
        f.write('\n'.join([TAG] + ['Riga %d: lorem ipsum dolor sit amet.' % i for i in range(3, 40)]))

    app = None
    notepad = 0
    try:
        subprocess.Popen([s.NOTEPAD, sample])
        notepad = s.wait_for(lambda: next((h for h, _p, t, c in pp.top_windows() if TAG in t and c == 'Notepad'), 0), 30)
        s.check('finestra di prova aperta', bool(notepad))
        if not notepad:
            raise RuntimeError('finestra di prova non aperta')
        pp.move(notepad, *PLACE)
        time.sleep(1.0)
        origin = pp.rect(notepad)
        s.check('finestra di prova messa sul secondo schermo', origin[0] >= s.SCREEN_W, f'rettangolo={origin} schermi={screens}')

        s.ONLY = [s.pid_of(notepad)]
        app = s.start_app()
        s.check('SmartBar (istanza di prova) si avvia e risponde ai comandi', s.wait_ready())
        s.use_side('left')        # questa prova assume la barra a sinistra

        s.check('parcheggiata una finestra del secondo schermo: non si vede su nessuno schermo',
                s.cmd('park', notepad) == 'ok' and s.off_screen(notepad), f'rettangolo={pp.rect(notepad)}')

        s.cmd('freeze', 'on')
        s.cmd('open', notepad)
        opened = s.wait_state(notepad, 'Expanded')
        time.sleep(0.5)
        s.check('aperta: riempie lo spazio accanto alla barra, sullo schermo principale',
                opened and pp.rect(notepad) == s.panel_rect(), f'rettangolo={pp.rect(notepad)} atteso={s.panel_rect()}')
        s.cmd('collapse-all')
        s.check('richiusa: di nuovo fuori da ogni schermo', s.wait_state(notepad, 'Collapsed') and s.off_screen(notepad),
                f'rettangolo={pp.rect(notepad)}')

        s.cmd('release', notepad)
        time.sleep(1.0)
        s.check('rilasciata: torna sul secondo schermo, esattamente dov\'era e della stessa misura',
                pp.rect(notepad) == origin, f'era={origin} ora={pp.rect(notepad)}')

        s.cmd('exit')
        app.wait(timeout=10)
        s.check('comando "exit": chiude l\'app', True)
    finally:
        if app is not None and app.poll() is None:
            app.kill()
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


if __name__ == '__main__':
    main()
    sys.exit(0 if not s.results or (all(s.results) and s.results) else 1)
