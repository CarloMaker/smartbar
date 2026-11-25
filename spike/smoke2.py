# smoke2.py -- seconda prova automatica di SmartBar: i comportamenti "particolari" del nucleo.
#   Riduci a icona su una finestra aperta = la richiude nella barra senza ridurla davvero;
#   scelta con Alt-Tab = si apre, e in secondo piano si richiude;
#   trascinamento fuori = torna finestra normale; Ingrandisci = esce dal parcheggio;
#   chiusura brusca dell'app = al riavvio ritrova le finestre parcheggiate.
# Usa Mappa caratteri come finestra di prova (barra del titolo semplice, nessuno stato salvato).
# Per le misure di sicurezza vedere smoke.py. I passaggi col mouse vero si fanno solo a utente fermo.

import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

import park_probe as pp
import smoke as s

u, kernel = s.u, pp.kernel32
CHARMAP = r'C:\Windows\System32\charmap.exe'
SW_MINIMIZE, SW_MAXIMIZE, SW_RESTORE = 6, 3, 9
LEFT_DOWN, LEFT_UP = pp.MOUSEEVENTF_LEFTDOWN, pp.MOUSEEVENTF_LEFTUP
POSITION = (700, 300)
SIZE = (491, 437)


def send(flags):
    arr = (pp.INPUT * 1)()
    arr[0].type = pp.INPUT_MOUSE
    arr[0].mi.dwFlags = flags
    u.SendInput(1, arr, ctypes.sizeof(pp.INPUT))


def park(hwnd):
    """Parcheggia la finestra: col pulsante col mouse vero se l'utente e' fermo, altrimenti col comando."""
    if s.front(hwnd, 'parcheggio col pulsante, click vero'):
        button = s.wait_for(lambda: s.app_windows()[0].get(hwnd), 6)
        if not button:
            return False
        bx, by, br, bb = pp.rect(button)
        cx, cy = (bx + br) // 2, (by + bb) // 2
        u.SetCursorPos(cx, cy)
        time.sleep(0.3)
        if s.guard(cx, cy, button):
            s.click()
            time.sleep(1.2)
    return s.cmd('park', hwnd) == 'ok' and s.off_screen(hwnd)


def main():
    base = pp.tasklist_pids()
    assert not base.get('smartbar.exe') or not s.REAL_INPUT, 'SmartBar è già in esecuzione: chiuderla prima della prova'
    app = None
    probe = None
    try:
        probe = subprocess.Popen([CHARMAP])
        charmap = s.wait_for(lambda: next((h for h, p, t, c in pp.top_windows() if p == probe.pid and t), 0), 15)
        s.check('finestra di prova aperta', bool(charmap))
        if not charmap:
            raise RuntimeError('finestra di prova non aperta')
        pp.move(charmap, POSITION[0], POSITION[1], *SIZE)
        time.sleep(0.4)

        s.ONLY = [probe.pid]
        app = s.start_app()
        s.check('SmartBar (istanza di prova) si avvia e risponde ai comandi', s.wait_ready())
        s.use_side('left')        # questa prova assume la barra a sinistra
        s.check('parcheggiata', park(charmap))

        # Riduci a icona su una finestra parcheggiata aperta
        s.cmd('freeze', 'on')
        s.cmd('open', charmap)
        s.check('aperta: occupa lo spazio accanto alla barra', s.wait_state(charmap, 'Expanded') and s.is_open(charmap),
              f'stato={s.state_of(charmap)} rettangolo={pp.rect(charmap)}')
        u.ShowWindow(charmap, SW_MINIMIZE)
        time.sleep(1.3)
        s.check('Riduci a icona la richiude nella barra senza ridurla davvero', s.wait_state(charmap, 'Collapsed')
              and s.off_screen(charmap) and not u.IsIconic(charmap),
              f'stato={s.state_of(charmap)} ridotta={bool(u.IsIconic(charmap))}')
        s.cmd('freeze', 'off')

        if s.front(charmap, 'Alt-Tab, trascinamento fuori, Ingrandisci (servono mouse e primo piano)'):
            # scelta con Alt-Tab, poi il programma passa in secondo piano
            time.sleep(1.0)
            s.check('scelta con Alt-Tab si apre', s.is_state(charmap, 'Expanded') and s.is_open(charmap),
                  f'stato={s.state_of(charmap)} rettangolo={pp.rect(charmap)}')
            s.activate(u.GetShellWindow())
            s.check('quando il programma passa in secondo piano si richiude', s.wait_state(charmap, 'Collapsed')
                  and s.off_screen(charmap), f'stato={s.state_of(charmap)} rettangolo={pp.rect(charmap)}')

            # trascinamento fuori dal pannello, dalla barra del titolo
            s.activate(charmap)
            s.check('scelta con Alt-Tab si apre di nuovo', s.is_state(charmap, 'Expanded') and s.is_open(charmap))
            x, y, r, b = pp.rect(charmap)
            if s.guard(x + 150, y + 15, charmap):
                u.SetCursorPos(x + 150, y + 15)
                time.sleep(0.4)
                send(LEFT_DOWN)
                time.sleep(0.3)
                for step in range(1, 13):
                    u.SetCursorPos(x + 150 - 25 * step, y + 15 + 17 * step)
                    time.sleep(0.05)
                time.sleep(0.3)
                send(LEFT_UP)
                time.sleep(1.3)
                moved = pp.rect(charmap)
                s.check('trascinata fuori dal pannello diventa una finestra normale', abs(moved[0] - (x - 300)) <= 12
                      and abs(moved[1] - (y + 204)) <= 12 and not s.rail_visible()
                      and s.wait_for(lambda: s.app_windows()[0].get(charmap), 6) is not None,
                      f'era=({x}, {y}) ora=({moved[0]}, {moved[1]}) barra_visibile={s.rail_visible()}')
            else:
                s.skipped('trascinamento fuori dal pannello', 'la finestra di prova è coperta: il cursore non ci arriva')

            # Ingrandisci
            s.check('si può parcheggiare di nuovo', s.cmd('park', charmap) == 'ok' and s.off_screen(charmap))
            s.cmd('freeze', 'on')
            s.cmd('open', charmap)
            s.check('aperta', s.wait_state(charmap, 'Expanded'), f'stato={s.state_of(charmap)}')
            u.ShowWindow(charmap, SW_MAXIMIZE)
            time.sleep(1.3)
            s.check('Ingrandisci la fa uscire dal parcheggio', u.IsZoomed(charmap) and not s.rail_visible()
                  and s.wait_for(lambda: s.app_windows()[0].get(charmap), 6) is not None,
                  f'ingrandita={bool(u.IsZoomed(charmap))} barra_visibile={s.rail_visible()}')
            u.ShowWindow(charmap, SW_RESTORE)
            time.sleep(0.8)
            s.cmd('freeze', 'off')
        else:
            s.cmd('collapse-all')

        # chiusura brusca dell'app, poi riavvio
        original = (POSITION[0], POSITION[1], POSITION[0] + SIZE[0], POSITION[1] + SIZE[1])
        s.check('si può parcheggiare (di nuovo)', s.cmd('park', charmap) == 'ok' and s.off_screen(charmap))
        app.kill()
        app.wait(timeout=10)
        time.sleep(0.5)
        s.check('con l\'app chiusa bruscamente la finestra resta oltre il bordo', s.off_screen(charmap))
        app = s.start_app()
        s.check('al riavvio l\'app ritrova la finestra parcheggiata', s.wait_ready() and s.wait_for(s.rail_visible, 10)
              and s.off_screen(charmap) and s.is_state(charmap, 'Collapsed'),
              f'stato={s.state_of(charmap)} rettangolo={pp.rect(charmap)}')

        s.check('comando "restore-all": la riporta dov\'era prima del parcheggio',
              s.cmd('restore-all') == 'ok' and pp.rect(charmap) == original, f'era={original} ora={pp.rect(charmap)}')
        s.cmd('exit')
        app.wait(timeout=10)
        s.check('comando "exit": chiude l\'app', True)
    finally:
        if app is not None and app.poll() is None:
            app.kill()
        if probe is not None and probe.poll() is None:
            subprocess.run(['taskkill', '/PID', str(probe.pid), '/T', '/F'], capture_output=True)
        time.sleep(0.5)
        s.finish_test()


if __name__ == '__main__':
    main()
    sys.exit(0 if all(s.results) and s.results else 1)
