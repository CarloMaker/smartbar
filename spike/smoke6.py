# smoke6.py -- sesta prova automatica di SmartBar: la barra a sinistra o a destra, e i suoi margini.
#   con la barra a sinistra sta sul bordo sinistro, le finestre aperte nella metà sinistra e parcheggiate oltre il
#   bordo sinistro; cambiando lato (col comando, che dal menu fa lo stesso) tutto si ribalta e le finestre parcheggiate
#   si spostano; la scelta resta salvata da un riavvio all'altro; la barra copre tutto il bordo in altezza e, con
#   molte finestre parcheggiate, le anteprime ci stanno dentro rimpicciolite, fino alla barra di Windows.
# Tutto per comandi all'istanza di prova, senza mouse vero: si puo' girare accanto all'istanza dell'utente.
# Usa una finestra di Blocco note e sedici di Mappa caratteri.
# Per le misure di sicurezza vedere smoke.py.

import os
import subprocess
import sys
import time

import park_probe as pp
import smoke as s

CHARMAP = r'C:\Windows\System32\charmap.exe'
MANY = 16
TAG = 'smartbar-smoke6-%06x' % pp.random.getrandbits(24)


def rail_on(side):
    """La barra dell'istanza di prova sta sul bordo scelto e copre tutta l'altezza dello spazio di lavoro?"""
    rail = s.rail_rect()
    work = s.work_area()
    if not rail:
        return False
    return ((rail[0] == work[0] if side == 'left' else rail[2] == work[2])
            and rail[1] == work[1] and rail[3] == work[3])


def free_beyond(side):
    """Oltre il bordo (sinistro o destro) dello schermo principale non c'e' nessuno schermo?"""
    return all(left >= 0 for left, _t, _r, _b in s.monitors()) if side == 'left' \
        else all(right <= s.SCREEN_W for _l, _t, right, _b in s.monitors())


def parked_beyond(hwnd, side):
    """La finestra parcheggiata sta subito oltre quel bordo dello schermo principale."""
    rect = pp.rect(hwnd)
    return rect[2] <= 0 if side == 'left' else rect[0] >= s.SCREEN_W


def main():
    s.ONLY = []
    base = pp.tasklist_pids()
    assert not base.get('smartbar.exe') or not s.REAL_INPUT, 'SmartBar è già in esecuzione: chiuderla prima della prova'
    os.makedirs(pp.OUT, exist_ok=True)
    sample = os.path.join(pp.OUT, TAG + '.txt')
    with open(sample, 'w', encoding='utf-8-sig') as f:
        f.write('\n'.join([TAG] + ['Riga %d: lorem ipsum dolor sit amet.' % i for i in range(3, 40)]))

    app = None
    notepad = 0
    probes = []
    charmaps = []
    try:
        subprocess.Popen([s.NOTEPAD, sample])
        notepad = s.wait_for(lambda: next((h for h, _p, t, c in pp.top_windows() if TAG in t and c == 'Notepad'), 0), 30)
        for _ in range(MANY):
            probes.append(subprocess.Popen([CHARMAP]))
        for probe in probes:
            charmaps.append(s.wait_for(lambda: next((h for h, pid, t, c in pp.top_windows() if pid == probe.pid and t), 0), 20))
        charmaps = [h for h in charmaps if h]
        s.check('finestre di prova aperte (Blocco note e %d Mappa caratteri)' % MANY, bool(notepad) and len(charmaps) == MANY,
                f'blocco note={bool(notepad)} mappa caratteri={len(charmaps)}')
        if not notepad or len(charmaps) < MANY:
            raise RuntimeError('finestre di prova non aperte')

        # Blocco note riapre la finestra dov'era rimasta l'ultima volta: la si mette sullo schermo principale
        pp.move(notepad, 700, 150, 1100, 700)
        time.sleep(1.0)

        s.ONLY = [s.pid_of(notepad)] + [s.pid_of(h) for h in charmaps]
        app = s.start_app()
        s.check('SmartBar (istanza di prova) si avvia e risponde ai comandi', s.wait_ready())

        # --- barra a sinistra ---
        s.use_side('left')
        s.check('parcheggiata', s.cmd('park', notepad) == 'ok' and s.off_screen(notepad), f'rettangolo={pp.rect(notepad)}')
        s.check('con la barra a sinistra sta sul bordo sinistro, a tutta altezza', rail_on('left'), f'barra={s.rail_rect()}')
        single = s.tile_rect(notepad)
        s.check('la tessera sta in alto nella barra', bool(single) and single[1] <= s.rail_rect()[1] + s.PAD + 1,
              f'tessera={single} barra={s.rail_rect()}')
        if free_beyond('left'):
            s.check('la finestra parcheggiata sta oltre il bordo sinistro (nessuno schermo piu' + chr(39) + 'a sinistra)',
                    parked_beyond(notepad, 'left'), f'rettangolo={pp.rect(notepad)}')

        s.cmd('freeze', 'on')
        s.cmd('open', notepad)
        s.check('aperta: occupa la metà sinistra, accanto alla barra', s.wait_state(notepad, 'Expanded') and s.is_open(notepad),
                f'rettangolo={pp.rect(notepad)} atteso={s.panel_rect()}')

        # --- cambio lato a barra e finestre in moto... (qui: con una aperta e una chiusa) ---
        answer = s.use_side('right')
        s.check('cambio lato: la barra va sul bordo destro', answer.startswith('ok') and rail_on('right'), f'risposta={answer} barra={s.rail_rect()}')
        s.check('e la finestra aperta passa subito nella metà destra', s.wait_state(notepad, 'Expanded') and s.is_open(notepad),
                f'rettangolo={pp.rect(notepad)} atteso={s.panel_rect()}')
        s.cmd('collapse-all')
        s.check('richiusa: fuori da ogni schermo anche col lato destro', s.wait_state(notepad, 'Collapsed') and s.off_screen(notepad),
                f'rettangolo={pp.rect(notepad)} schermi={s.monitors()}')
        if free_beyond('right'):
            s.check('la finestra parcheggiata sta oltre il bordo destro (nessuno schermo piu' + chr(39) + 'a destra)',
                    parked_beyond(notepad, 'right'), f'rettangolo={pp.rect(notepad)}')

        s.use_side('left')
        s.cmd('open', notepad)
        s.check('tornando a sinistra la finestra si apre nella metà sinistra', s.wait_state(notepad, 'Expanded') and s.is_open(notepad),
                f'rettangolo={pp.rect(notepad)} atteso={s.panel_rect()}')

        # --- la scelta resta salvata ---
        s.use_side('right')
        s.cmd('exit')
        app.wait(timeout=10)
        app = s.start_app()
        s.check('SmartBar riaperta (seconda volta)', s.wait_ready())
        s.BAR_LEFT = True          # qui si fa finta di non sapere: lo si scopre dalla barra
        s.check('parcheggiata dopo il riavvio', s.cmd('park', notepad) == 'ok' and s.off_screen(notepad))
        s.BAR_LEFT = False
        s.check('dopo il riavvio la barra e' + chr(39) + ' ancora sul lato scelto (destro)', rail_on('right'), f'barra={s.rail_rect()}')
        s.use_side('left')

        # --- la barra a tutta altezza con molte finestre parcheggiate ---
        s.cmd('release', notepad)
        parked = [h for h in charmaps if s.cmd('park', h) == 'ok']
        time.sleep(1.0)
        rail = s.rail_rect()
        s.check('parcheggiate tutte le finestre di prova', len(parked) == MANY, f'parcheggiate={len(parked)}')
        s.check('con tante finestre la barra sta sempre sul bordo, a tutta altezza', rail_on('left'),
                f'barra={rail} spazio di lavoro={s.work_area()}')
        tiles = [s.tile_rect(h) for h in parked]
        s.check('e le anteprime ci stanno tutte dentro: l\'ultima finisce prima della barra di Windows',
                bool(rail) and all(tiles) and tiles[-1][3] <= rail[3] - s.PAD, f'ultima tessera={tiles[-1]} barra={rail}')
        s.check('con %d finestre parcheggiate le anteprime si sono rimpicciolite per starci' % len(parked),
                bool(single) and all(tiles) and all(t[3] - t[1] < single[3] - single[1] for t in tiles),
                f'con una finestra alta {single[3] - single[1] if single else None}, con tante {sorted({t[3] - t[1] for t in tiles if t})}')

        s.cmd('restore-all')
        s.check('a fine prova le finestre di prova sono tornate a schermo', all(not s.off_screen(h) for h in charmaps))
        s.cmd('exit')
        app.wait(timeout=10)
        s.check('comando "exit": chiude l\'app', True)
    finally:
        if app is not None and app.poll() is None:
            app.kill()
        for hwnd, pid, title, cls in pp.top_windows():
            if (TAG in title and cls == 'Notepad') or any(pid == probe.pid for probe in probes):
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
    sys.exit(0 if all(s.results) and s.results else 1)
