# smoke3.py -- terza prova automatica di SmartBar: la puntina e il tasto destro.
#   messa della puntina a una finestra aperta a tutta l'altezza -> resta esattamente dov'e, anche col mouse fuori;
#   seconda finestra -> si apre sopra, e pinnandola le due si dividono: la prima in alto, la seconda in basso;
#   terza -> toglie la puntina alla pinnata da piu' tempo (la prima) e prende il suo posto;
#   tolta la puntina a una delle due, quella che resta torna a tutta l'altezza;
#   un altro click toglie la puntina; Riduci a icona su una pinnata la richiude;
#   uscita dal parcheggio -> la finestra torna dov'era.
# Quasi tutto si comanda attraverso il canale di comando dell'istanza di prova (senza mouse); i click sulla barra
# si fanno col cursore solo se ci puo' arrivare, altrimenti spedendo alla barra gli stessi messaggi del mouse.
# Per le misure di sicurezza vedere smoke.py.
# Usa tre istanze di Mappa caratteri, messe in tre punti diversi dello schermo.

import subprocess
import sys
import time

import park_probe as pp
import smoke as s

u = s.u
CHARMAP = r'C:\Windows\System32\charmap.exe'
SW_MINIMIZE = 6
SLOT_GAP = 6
SIZE = (491, 437)
POSITIONS = [(60, 120), (640, 120), (1320, 120)]

_panel = None
_probes = []


def slot(index):
    """Rettangolo atteso di un posto delle pinnate: 0 in alto, 1 in basso."""
    left, top, right, bottom = _panel
    half = (bottom - top - SLOT_GAP) // 2
    return (left, top, right, top + half) if index == 0 else (left, top + half + SLOT_GAP, right, bottom)


def same(rect, expected):
    return all(abs(a - b) <= 2 for a, b in zip(rect, expected))


def original(index):
    x, y = POSITIONS[index]
    return (x, y, x + SIZE[0], y + SIZE[1])


def state(hwnd):
    return f'stato={s.state_of(hwnd)} rettangolo={pp.rect(hwnd)}'


def is_pinned(hwnd):
    return 'pinned=1' in s.state_of(hwnd)


def settled(hwnd, state_name, expected_rect):
    """A fine corsa, nello stato e nel posto previsti: aspetta che lo scorrimento sia finito."""
    def arrived():
        return True if s.is_state(hwnd, state_name) and same(pp.rect(hwnd), expected_rect) else None
    return s.wait_for(arrived, 4.0, step=0.05) is not None


def tile_center(index, count):
    """Centro della tessera index-esima della barra, che in quel momento ne ha count."""
    return s.tile_center(index, count)


def click_tile(index, count, button='left'):
    """Click sulla tessera: col mouse vero se il cursore ci puo' arrivare, altrimenti spedendo i messaggi del mouse
    alla finestra della barra (per l'app e' la stessa cosa: la barra riceve gli stessi messaggi)."""
    rail = s.app_windows()[1]
    cx, cy = tile_center(index, count)
    if s.when_idle('click vero col mouse sulla barra') and s.guard(cx, cy, rail):
        u.SetCursorPos(cx, cy)
        time.sleep(0.15)
        s.click(button)
    else:
        rx, ry, _r, _b = pp.rect(rail)
        s.post_click(rail, cx - rx, cy - ry, button)
    time.sleep(0.9)


def launch_charmap(index):
    probe = subprocess.Popen([CHARMAP])
    _probes.append(probe)
    hwnd = s.wait_for(lambda: next((h for h, pid, t, c in pp.top_windows() if pid == probe.pid and t), 0), 15)
    if hwnd:
        pp.move(hwnd, POSITIONS[index][0], POSITIONS[index][1], *SIZE)
    time.sleep(0.4)
    return hwnd


def main():
    global _panel
    base = pp.tasklist_pids()
    assert not base.get('smartbar.exe') or not s.REAL_INPUT, 'SmartBar è già in esecuzione: chiuderla prima della prova'
    app = None
    try:
        first, second, third = (launch_charmap(index) for index in range(3))
        s.check('tre finestre di prova aperte in tre punti diversi', all((first, second, third)),
              f'{pp.rect(first)} {pp.rect(second)} {pp.rect(third)}')
        if not (first and second and third):
            raise RuntimeError('finestre di prova non aperte')

        s.ONLY = [s.pid_of(first), s.pid_of(second), s.pid_of(third)]
        app = s.start_app()
        s.check('SmartBar (istanza di prova) si avvia e risponde ai comandi', s.wait_ready())
        s.use_side('left')        # questa prova assume la barra a sinistra
        s.cmd('freeze', 'on')

        s.check('parcheggiate due finestre', s.cmd('park', first) == 'ok' and s.cmd('park', second) == 'ok'
              and s.off_screen(first) and s.off_screen(second))

        s.cmd('open', first)
        s.check('senza puntine la prima si apre in tutto lo spazio accanto alla barra, a tutta l\'altezza',
              s.wait_state(first, 'Expanded') and s.is_open(first), f'{state(first)} atteso={s.panel_rect()}')
        _panel = pp.rect(first)

        s.cmd('pin', first)
        s.check('messa la puntina alla prima: resta esattamente dov\'era, a tutta l\'altezza', is_pinned(first)
              and settled(first, 'Expanded', _panel), f'{state(first)} atteso={_panel}')

        s.cmd('open', second)
        s.check('la seconda si apre sopra, a tutta l\'altezza anche lei, senza puntina', not is_pinned(second)
              and settled(second, 'Expanded', _panel), f'{state(second)} atteso={_panel}')

        s.cmd('pin', second)
        s.check('messa la puntina alla seconda: si dividono lo spazio, la prima in alto e la seconda in basso',
              s.wait_state(first, 'Expanded') and settled(first, 'Expanded', slot(0))
              and settled(second, 'Expanded', slot(1)),
              f'prima {state(first)} atteso={slot(0)} | seconda {state(second)} atteso={slot(1)}')

        s.check('parcheggiata una terza finestra', s.cmd('park', third) == 'ok' and s.off_screen(third))
        s.cmd('open', third)
        s.check('con due posti presi la terza si apre sopra, in tutto lo spazio', not is_pinned(third)
              and settled(third, 'Expanded', _panel), f'{state(third)} atteso={_panel}')

        s.cmd('pin', third)
        s.check('messa la puntina alla terza: la prima, pinnata da piu\' tempo, perde la puntina e si richiude; '
                'la terza prende il suo posto; la seconda resta',
              s.wait_state(first, 'Collapsed') and not is_pinned(first)
              and is_pinned(third) and settled(third, 'Expanded', slot(0))
              and is_pinned(second) and settled(second, 'Expanded', slot(1)),
              f'prima {state(first)} | seconda {state(second)} | terza {state(third)}')

        s.cmd('pin', second)
        s.check('un altro click toglie la puntina (la finestra resta aperta finche\' il mouse sta qui)',
              s.wait_state(second, 'Expanded') and not is_pinned(second) and same(pp.rect(second), slot(1)), state(second))

        s.cmd('freeze', 'off')        # il mouse non e' sulla finestra: per chi non ha la puntina e' come esserne uscito
        if s.cursor_on_test_area():
            # l'utente tiene il mouse sulla metà destra dello schermo: per l'app e' come averlo sulla finestra di prova
            s.skipped('uscendo il mouse la finestra senza puntina si richiude', 'il mouse dell\'utente sta sull\'area delle finestre di prova')
            s.cmd('collapse-all')      # stesso stato finale che darebbe l'uscita del mouse
        else:
            s.check('uscendo il mouse la finestra senza puntina si richiude; quella pinnata, rimasta sola, torna a tutta l\'altezza',
                    s.wait_state(second, 'Collapsed', 6) and is_pinned(third) and settled(third, 'Expanded', _panel),
                    f'seconda {state(second)} | terza {state(third)} atteso={_panel}')
        s.cmd('freeze', 'on')

        s.cmd('open', first)
        s.check('la prima si apre sopra, a tutta l\'altezza, senza puntina', not is_pinned(first)
              and settled(first, 'Expanded', _panel), f'{state(first)} atteso={_panel}')
        s.cmd('pin', first)
        s.check('messa la puntina alla prima: le due si dividono di nuovo, la terza in alto e la prima in basso',
              is_pinned(first) and settled(first, 'Expanded', slot(1))
              and is_pinned(third) and settled(third, 'Expanded', slot(0)),
              f'prima {state(first)} atteso={slot(1)} | terza {state(third)} atteso={slot(0)}')

        u.ShowWindow(first, SW_MINIMIZE)
        time.sleep(1.3)
        s.check('Riduci a icona su una finestra pinnata la richiude e toglie la puntina; quella rimasta sola torna a tutta l\'altezza',
              s.wait_state(first, 'Collapsed') and not is_pinned(first) and not u.IsIconic(first)
              and is_pinned(third) and settled(third, 'Expanded', _panel),
              f'prima {state(first)} ridotta={bool(u.IsIconic(first))} | terza {state(third)} atteso={_panel}')

        # uscita dal parcheggio
        s.cmd('release', third)
        time.sleep(0.4)
        s.check('uscita dal parcheggio di una finestra pinnata e aperta: torna dov\'era', same(pp.rect(third), original(2))
              and s.rail_visible() and s.wait_for(lambda: s.app_windows()[0].get(third), 6) is not None,
              f'terza={pp.rect(third)} atteso={original(2)}')
        s.cmd('release', second)
        time.sleep(0.4)
        s.check('e di una finestra richiusa nella barra', same(pp.rect(second), original(1)) and s.rail_visible(),
              f'seconda={pp.rect(second)} atteso={original(1)}')

        # click sulla barra: col cursore se ci puo' arrivare, altrimenti spedendo alla barra gli stessi messaggi del mouse
        s.cmd('freeze', 'off')
        click_tile(0, 1, button='left')
        s.check('click sulla tessera: mette la puntina e apre la finestra a tutta l\'altezza', is_pinned(first)
              and settled(first, 'Expanded', _panel), state(first))
        click_tile(0, 1, button='right')
        s.check('tasto destro sulla tessera: la finestra esce dal parcheggio e torna dov\'era',
                s.wait_for(lambda: s.app_windows()[0].get(first), 6) is not None and same(pp.rect(first), original(0))
                and not s.rail_visible(), f'prima={pp.rect(first)} atteso={original(0)}')

        s.cmd('restore-all')
        s.check('a fine prova tutte e tre sono a schermo nel loro posto',
              all(same(pp.rect(h), original(i)) for i, h in enumerate((first, second, third))) and not s.rail_visible(),
              f'{pp.rect(first)} {pp.rect(second)} {pp.rect(third)}')
        s.cmd('exit')
        app.wait(timeout=10)
        s.check('comando "exit": chiude l\'app', True)
    finally:
        if app is not None and app.poll() is None:
            app.kill()
        for probe in _probes:
            if probe.poll() is None:
                subprocess.run(['taskkill', '/PID', str(probe.pid), '/T', '/F'], capture_output=True)
        time.sleep(0.5)
        s.finish_test()


if __name__ == '__main__':
    main()
    sys.exit(0 if all(s.results) and s.results else 1)
