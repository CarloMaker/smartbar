# smoke4.py -- quarta prova automatica di SmartBar: il pulsante di parcheggio al suo posto.
#   dove sono i pulsanti di sistema lo dice il programma stesso (comando "measure", sola lettura): il nostro pulsante
#   sta due pixel a sinistra di quelli, largo e alto come uno di quelli, e i pulsanti di sistema finiscono al bordo
#   destro visibile della finestra;
#   con le schede subito a sinistra (Blocco note) da ferma e' una maniglia di 8 pixel, al passaggio del mouse si fa
#   avanti intero, uscito il mouse torna maniglia;
#   con la cornice di Windows (Mappa caratteri) si vede intero da subito;
#   il disegno si vede davvero: le finestrelle sono "a strati", se nessuno le disegnasse sarebbero invisibili. Per
#   vederle si fa disegnare ciascuna finestra da se (PrintWindow) e si compongono le immagini in spike/out.
# Tutto per comandi all'istanza di prova (il passaggio del mouse compreso: comando "hover"), senza mouse vero: si puo'
# girare accanto all'istanza dell'utente. Per le misure di sicurezza vedere smoke.py.

import os
import subprocess
import sys
import time

import hit_probe as hp
import park_probe as pp
import smoke as s

CHARMAP = r'C:\Windows\System32\charmap.exe'
TAG = 'smartbar-smoke4-%06x' % pp.random.getrandbits(24)


def drawn_pixels(hwnd):
    """Quanti pixel chiari ha il disegno di una finestrella nostra (-1 se non si fa disegnare). L'area trasparente
    che serve solo a prendere il click, nel disegno che la finestra fa di se' esce nera."""
    x, y, r, b = pp.rect(hwnd)
    data = hp.grab(hwnd, r - x, b - y)
    if not data:
        return -1
    return sum(1 for i in range(0, len(data), 4) if (data[i] + data[i + 1] + data[i + 2]) / 3 >= 90)


def shape(hwnd):
    """(larghezza adesso, larghezza intera, e' una maniglia) del pulsante di una finestra, se sta attaccato a quelli
    di sistema; None altrimenti."""
    info = s.attached(hwnd)
    if not info:
        return None
    now, full = info[2], info[3]
    return now[2] - now[0], full[2] - full[0], info[4]


def wait_shape(hwnd, wanted, timeout=4):
    return s.wait_for(lambda: (lambda found: found if found and wanted(found) else None)(shape(hwnd)), timeout)


def composite(name, target, *ours):
    """Immagine dell'angolo in alto a destra della finestra con sopra le nostre finestrelle, come le si vedrebbe."""
    left, top, right, bottom = pp.rect(target)
    width, height = right - left, bottom - top
    data = bytearray(hp.grab(target, width, height))
    for hwnd in ours:
        x, y, r, b = pp.rect(hwnd)
        overlay = hp.grab(hwnd, r - x, b - y)
        if not overlay:
            continue
        for j in range(b - y):
            for i in range(r - x):
                sx, sy = x - left + i, y - top + j
                if not (0 <= sx < width and 0 <= sy < height):
                    continue
                alpha = overlay[(j * (r - x) + i) * 4 + 3] / 255
                for channel in range(3):
                    at = (sy * width + sx) * 4 + channel
                    data[at] = int(overlay[(j * (r - x) + i) * 4 + channel] * alpha + data[at] * (1 - alpha))
    hp.save_crop(os.path.join(pp.OUT, name), bytes(data), width, height)
    print('immagine:', os.path.join(pp.OUT, name), flush=True)


def main():
    s.ONLY = []
    base = pp.tasklist_pids()
    assert not base.get('smartbar.exe') or not s.REAL_INPUT, 'SmartBar è già in esecuzione: chiuderla prima della prova'
    os.makedirs(pp.OUT, exist_ok=True)
    sample = os.path.join(pp.OUT, TAG + '.txt')
    with open(sample, 'w', encoding='utf-8-sig') as f:
        f.write('\n'.join([TAG] + ['Riga %d: lorem ipsum dolor sit amet.' % i for i in range(3, 40)]))

    app = probe = None
    notepad = charmap = 0
    try:
        subprocess.Popen([s.NOTEPAD, sample])
        notepad = s.wait_for(lambda: next((h for h, _p, t, c in pp.top_windows() if TAG in t and c == 'Notepad'), 0), 30)
        probe = subprocess.Popen([CHARMAP])
        charmap = s.wait_for(lambda: next((h for h, pid, t, c in pp.top_windows() if pid == probe.pid and t), 0), 15)
        s.check('finestre di prova aperte (Blocco note e Mappa caratteri)', bool(notepad) and bool(charmap))
        if not (notepad and charmap):
            raise RuntimeError('finestre di prova non aperte')

        # Blocco note riapre la finestra dov'era rimasta l'ultima volta, magari su un altro schermo (di scala diversa,
        # dove i conti tornano al pixel vicino): le finestre di prova si mettono sullo schermo principale, dove le
        # misure di questa prova sono note
        pp.move(notepad, 700, 150, 1100, 700)
        pp.move(charmap, 200, 300, 491, 437)
        time.sleep(1.0)

        s.ONLY = [s.pid_of(notepad), probe.pid]
        app = s.start_app()
        s.check('SmartBar (istanza di prova) si avvia e risponde ai comandi', s.wait_ready())
        s.use_side('left')        # non dipende dal lato, ma la scelta resta salvata fra una prova e l'altra

        # --- Blocco note: le schede arrivano fino ai pulsanti di sistema ---
        button = s.wait_for(lambda: s.app_windows()[0].get(notepad), 10)
        info = s.wait_for(lambda: s.attached(notepad), 6)
        s.check('Blocco note: il pulsante sta attaccato (due pixel) a sinistra dei pulsanti di sistema',
                info is not None, f'misura={s.button_info(notepad)}')
        if info:
            band, count, now, full, handle = info
            edge = s.visible_right(notepad)
            s.check('i pulsanti di sistema finiscono al bordo destro visibile e sono tre da 46 pixel',
                    abs(band[2] - edge) <= 2 and 136 <= band[2] - band[0] <= 140 and count == 3,
                    f'pulsanti di sistema={band} bordo visibile={edge}')
            s.check("il nostro pulsante è largo e alto come uno di quelli",
                    abs((full[2] - full[0]) - (band[2] - band[0]) // 3) <= 1 and full[3] - full[1] == band[3] - band[1],
                    f'intero={full} pulsanti di sistema alti {band[3] - band[1]}')
            s.check('da ferma si vede solo la maniglia (8 pixel) contro il bordo destro', handle
                    and now[2] - now[0] == 8 and now[2] == full[2] and now[3] - now[1] == full[3] - full[1], f'adesso={now}')
        s.check('la maniglia si vede', 30 <= drawn_pixels(button) <= 80, f'pixel chiari={drawn_pixels(button)}')
        composite('smoke4_maniglia.png', notepad, button)

        # --- al passaggio del mouse il pulsante si fa avanti intero (comando di prova: col mouse vero si fa cosi' quando
        # l'utente non sta usando il PC) ---
        s.cmd('hover', notepad, 'on')
        grown = wait_shape(notepad, lambda w: not w[2] and w[0] == w[1])
        s.check('passando il mouse sulla maniglia il pulsante si fa avanti intero, restando attaccato', grown is not None,
                f'stato={shape(notepad)}')
        s.check('e si vede il simbolo', drawn_pixels(button) > 80, f'pixel chiari={drawn_pixels(button)}')
        composite('smoke4_pulsante_intero.png', notepad, button)

        # --- uscito il mouse torna la maniglia ---
        s.cmd('hover', notepad, 'off')
        back = wait_shape(notepad, lambda w: w[2] and w[0] == 8)
        s.check('uscito il mouse torna la maniglia', back is not None, f'stato={shape(notepad)}')
        s.check('di nuovo solo la maniglia', 30 <= drawn_pixels(button) <= 80, f'pixel chiari={drawn_pixels(button)}')

        # --- Mappa caratteri: cornice disegnata da Windows, a sinistra dei pulsanti non c'e' niente del programma ---
        cbutton = s.wait_for(lambda: s.app_windows()[0].get(charmap), 10)
        cinfo = s.wait_for(lambda: s.attached(charmap), 6)
        s.check('Mappa caratteri: il pulsante sta attaccato a sinistra dei pulsanti di sistema, che finiscono al bordo visibile',
                cinfo is not None and abs(cinfo[0][2] - s.visible_right(charmap)) <= 2, f'misura={s.button_info(charmap)}')
        cshape = shape(charmap)
        s.check('con la cornice di Windows il pulsante si vede intero da subito',
                cshape is not None and not cshape[2] and cshape[0] == cshape[1] and drawn_pixels(cbutton) > 80,
                f'stato={cshape} pixel chiari={drawn_pixels(cbutton)}')
        composite('smoke4_pulsante_cornice.png', charmap, cbutton)

        s.cmd('exit')
        app.wait(timeout=10)
        s.check('comando "exit": chiude l\'app', True)
    finally:
        if app is not None and app.poll() is None:
            app.kill()
        for hwnd, pid, title, cls in pp.top_windows():
            # solo le finestre di prova: si riconoscono dal titolo univoco o dal processo lanciato qui
            if (TAG in title and cls == 'Notepad') or (probe is not None and pid == probe.pid):
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
