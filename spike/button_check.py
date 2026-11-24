# button_check.py -- diagnostica in sola lettura: dove un'istanza di SmartBar (di solito quella dell'utente) ha messo i
# suoi pulsanti di parcheggio sulle finestre vere, confrontato con dove dovrebbero stare secondo misure indipendenti
# (le stesse fonti di hit_probe.py, rilette qui con un altro programma). Non muove e non clicca niente, non stampa titoli.
#   python button_check.py <pid dell'istanza di SmartBar>

import ctypes
import ctypes.wintypes as wt
import sys

import hit_probe as hp
import park_probe as pp

BUTTONS = (8, 9, 20, 21)
STANDARD = 46
u = pp.user32
u.GetPropW = pp.proto(u, 'GetPropW', pp.VP, pp.H, ctypes.c_wchar_p)


def expected(hwnd):
    """(pulsanti di sistema: sinistra, alto, destra, basso; da che fonte)."""
    left, top, right, _bottom = pp.rect(hwnd)
    frame = hp.dwm_rect(hwnd, 9)
    raw = hp.dwm_rect(hwnd, 5)
    width = raw.right - raw.left if raw else 0
    plausible = 20 <= width <= 320 and raw.right <= right - left + 8
    answer = hp.hit_band(hwnd, left, top, right)

    # la finestra risponde con una fila di pulsanti pari? Se Windows non la conferma dev'essere proprio uniforme
    cells = [n for c, n in answer['parts'] if c in BUTTONS] if answer else []
    breaks = sum(n for c, n in answer['parts'] if c not in BUTTONS) if answer else 0
    agreed = plausible and answer is not None and abs(width - answer['width']) <= 14
    tolerance = 4 if (not plausible or agreed) else 1
    if len(cells) >= 2 and breaks == 0 and max(cells) - min(cells) <= tolerance:
        edge = answer['right']
        if agreed:
            edge = frame.right if abs(left + raw.right - frame.right) <= 2 else left + raw.right
        return edge - answer['width'], answer['top'], edge, answer['bottom'], 'risposta della finestra'
    if plausible:
        edge = frame.right if abs(left + raw.right - frame.right) <= 2 else left + raw.right
        count = max(1, min(4, round((width - 8) / STANDARD)))
        top = max(top + raw.top, frame.top)
        return edge - count * STANDARD, top, edge, top + max(raw.bottom - raw.top, 22), 'misura di Windows'
    return frame.right - 3 * STANDARD, frame.top, frame.right, frame.top + 30, 'stima'


def main():
    pid = int(sys.argv[1])
    classes = {hwnd: cls for hwnd, _p, _t, cls in pp.top_windows()}
    buttons = {}
    for hwnd, owner, _title, _cls in pp.top_windows():
        if owner == pid:
            target = u.GetPropW(hwnd, 'SmartBar.Target')
            if target:
                buttons[target] = hwnd
    if not buttons:
        print('nessun pulsante dell\'istanza', pid)

    wrong = 0
    for target, button in sorted(buttons.items()):
        owner = wt.DWORD()
        u.GetWindowThreadProcessId(target, ctypes.byref(owner))
        process = pp.image_of_pid(owner.value) or '?'
        left, top, right, bottom, source = expected(target)
        bx, by, br, bb = pp.rect(button)
        handle = br - bx <= 10
        wide = right - left
        # un pixel di tolleranza: le due letture arrotondano diversamente
        ok = abs(br - (left - 2)) <= 1 and abs(by - top) <= 1 and abs((bb - by) - (bottom - top)) <= 1
        ok = ok and (handle or abs((br - bx) - (STANDARD if wide >= 130 else wide // 3)) <= 1)
        wrong += not ok
        print('%s %-22s %-26s %-22s pulsanti (%d,%d,%d,%d) nostro (%d,%d,%d,%d)%s' % (
            'ok  ' if ok else 'DIVE', process[:22], classes.get(target, '?')[:26], source, left, top, right, bottom,
            bx, by, br, bb, ' maniglia' if handle else ''))
    print('%d pulsanti, %d fuori posto' % (len(buttons), wrong))


if __name__ == '__main__':
    main()
