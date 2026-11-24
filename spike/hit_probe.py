# hit_probe.py -- diagnostica in sola lettura su dove stanno i pulsanti Riduci/Ingrandisci/Chiudi delle finestre.
# Non muove, non clicca, non scrive niente e non stampa i titoli. Tre fonti a confronto:
#   - risposta: che cosa risponde la finestra alla domanda "che cosa c'e' in questo punto" (WM_NCHITTEST, la stessa
#     che le fa Windows quando ci si passa sopra col mouse);
#   - Windows: la misura che ne da' il sistema (DWMWA_CAPTION_BUTTON_BOUNDS);
#   - disegno: dove sono davvero disegnati i simboli -, [], x (la finestra si disegna da se con PrintWindow).
#   python hit_probe.py [filtro ...]            per ogni finestra, le risposte riga per riga nell'angolo in alto a destra
#   python hit_probe.py --compare [filtro ...]  per ogni finestra, il confronto fra le tre fonti
#   python hit_probe.py --exact [filtro ...]    bordi esatti (passo 1 pixel) della fascia ricavata dalle risposte
# Il filtro e' un pezzo del nome della classe o del programma.

import ctypes
import ctypes.wintypes as wt
import itertools
import os
import sys
import time

import park_probe as pp

user32 = pp.user32
WM_NCHITTEST = 0x0084
SMTO_ABORTIFHUNG = 0x0002
DWMWA_CAPTION_BUTTON_BOUNDS = 5
DWMWA_EXTENDED_FRAME_BOUNDS = 9
BUTTONS = (8, 9, 20, 21)
NAMES = {-1: 'NOWHERE', 0: 'TRANSPARENT', 1: 'CLIENT', 2: 'CAPTION', 3: 'SYSMENU', 4: 'GROWBOX', 5: 'MENU', 6: 'HSCROLL',
         7: 'VSCROLL', 8: 'MIN', 9: 'MAX', 10: 'LEFT', 11: 'RIGHT', 12: 'TOP', 13: 'TOPLEFT', 14: 'TOPRIGHT',
         15: 'BOTTOM', 16: 'BOTTOMLEFT', 17: 'BOTTOMRIGHT', 18: 'BORDER', 19: 'REDUCE', 20: 'CLOSE', 21: 'HELP'}

SendMessageTimeoutW = pp.proto(user32, 'SendMessageTimeoutW', ctypes.c_size_t, wt.HWND, wt.UINT, ctypes.c_size_t,
                               ctypes.c_ssize_t, wt.UINT, wt.UINT, ctypes.POINTER(ctypes.c_size_t))
GetDpiForWindow = pp.proto(user32, 'GetDpiForWindow', wt.UINT, wt.HWND)
GetWindowLongW = pp.proto(user32, 'GetWindowLongW', wt.LONG, wt.HWND, ctypes.c_int)
DwmGetWindowAttribute = pp.proto(pp.dwmapi, 'DwmGetWindowAttribute', ctypes.c_long, wt.HWND, wt.DWORD, ctypes.c_void_p,
                                 wt.DWORD)
CreateDIBSection = pp.proto(pp.gdi32, 'CreateDIBSection', wt.HBITMAP, wt.HDC, ctypes.POINTER(pp.BITMAPINFO), wt.UINT,
                            ctypes.POINTER(ctypes.c_void_p), wt.HANDLE, wt.DWORD)
GetWindowDC = pp.proto(user32, 'GetWindowDC', wt.HDC, wt.HWND)
PrintWindow = pp.proto(user32, 'PrintWindow', wt.BOOL, wt.HWND, wt.HDC, wt.UINT)
CreateCompatibleDC = pp.proto(pp.gdi32, 'CreateCompatibleDC', wt.HDC, wt.HDC)
SelectObject = pp.proto(pp.gdi32, 'SelectObject', wt.HGDIOBJ, wt.HDC, wt.HGDIOBJ)
DeleteObject = pp.proto(pp.gdi32, 'DeleteObject', wt.BOOL, wt.HGDIOBJ)
DeleteDC = pp.proto(pp.gdi32, 'DeleteDC', wt.BOOL, wt.HDC)
CROP_W, CROP_H, SCALE = 340, 64, 3


def what(hwnd, x, y, timeout=40):
    """Codice del punto (x, y) della finestra, None se non risponde."""
    out = ctypes.c_size_t()
    r = SendMessageTimeoutW(hwnd, WM_NCHITTEST, 0, ((y & 0xFFFF) << 16) | (x & 0xFFFF), SMTO_ABORTIFHUNG, timeout,
                            ctypes.byref(out))
    return ctypes.c_int32(out.value).value if r else None


def runs_of(codes):
    """Sequenza consolidata: lista (codice, quantita'), nell'ordine."""
    return [(c, sum(1 for _ in g)) for c, g in itertools.groupby(codes) if c is not None]


def show(runs):
    return ' '.join('%s:%d' % (NAMES.get(code, str(code)), n) for code, n in runs)


def row(hwnd, right, y, span=240, step=2, timeout=40):
    """Sequenza dei codici lungo una riga, da destra a sinistra: lista (codice, quantita') consolidata."""
    codes = [what(hwnd, x, y, timeout) for x in range(right - 1, right - 1 - span, -step)]
    return None if None in codes else [(c, n * step) for c, n in runs_of(codes)]


def band(runs):
    """Fascia dei pulsanti in una sequenza letta da destra a sinistra: dal primo pulsante finche' ce ne sono,
    saltando interruzioni di al massimo 3 pixel. (inizio, fine, [(codice, lunghezza)])."""
    start = next((k for k, (c, _n) in enumerate(runs) if c in BUTTONS), None)
    if start is None:
        return None
    index, gap, width = start, 0, 0
    while index < len(runs):
        code, length = runs[index]
        if code in BUTTONS:
            gap = 0
        elif gap + length > 3:
            break
        else:
            gap += length
        width += length
        index += 1
    return width, runs[start:index]


def hit_band(hwnd, left, top, right):
    """Fascia dei pulsanti ricavata dalle risposte della finestra: dizionario o None."""
    # quanto e' alta: la colonna a 12 pixel dal bordo destro (in mezzo al pulsante Chiudi)
    column = [what(hwnd, right - 13, y) for y in range(top - 1, top + 70)]
    if column[0] is None or None in column:
        return None
    rows = [top - 1 + i for i, c in enumerate(column) if c in BUTTONS]
    if not rows:
        return None
    middle = (min(rows) + max(rows)) // 2
    line = [what(hwnd, x, middle) for x in range(right - 1, right - 421, -1)]
    if None in line:
        return None
    runs = runs_of(line)
    found = band(runs)
    if not found:
        return None
    width, part = found
    band_left = right - sum(n for _c, n in runs[:next(k for k, (c, _n) in enumerate(runs) if c in BUTTONS)]) - width
    return dict(left=band_left, right=band_left + width, width=width, parts=part, top=min(rows), bottom=max(rows) + 1)


def dwm_rect(hwnd, attribute):
    raw = pp.RECT()
    return raw if DwmGetWindowAttribute(hwnd, attribute, ctypes.byref(raw), 16) == 0 else None


def grab(hwnd, w, h):
    """La finestra si disegna da se (PrintWindow) in un'immagine BGRA; vuoto se non riesce."""
    bmi = pp.BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(pp.BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth, bmi.bmiHeader.biHeight, bmi.bmiHeader.biPlanes, bmi.bmiHeader.biBitCount = w, -h, 1, 32
    bits = ctypes.c_void_p()
    bmp = CreateDIBSection(None, ctypes.byref(bmi), 0, ctypes.byref(bits), None, 0)
    mdc = CreateCompatibleDC(None)
    old = SelectObject(mdc, bmp)
    wdc = GetWindowDC(hwnd)
    ok = PrintWindow(hwnd, mdc, 2)
    data = ctypes.string_at(bits, w * h * 4) if ok else b''
    user32.ReleaseDC(hwnd, wdc)
    pp.gdi32.SelectObject(mdc, old)
    DeleteDC(mdc)
    DeleteObject(bmp)
    return data


def clusters(data, w):
    """Colonne dell'angolo in alto a destra dove il disegno cambia rispetto a qualche pixel piu' a sinistra:
    dove ci sono simboli o bordi. Lista (inizio, fine) in coordinate della finestra."""
    cols = []
    for sx in range(max(0, w - CROP_W), w):
        marks = 0
        for sy in range(0, CROP_H):
            s = (sy * w + sx) * 4
            s2 = (sy * w + max(0, sx - 6)) * 4
            lum = (data[s] * 299 + data[s + 1] * 587 + data[s + 2] * 114) // 1000
            lum2 = (data[s2] * 299 + data[s2 + 1] * 587 + data[s2 + 2] * 114) // 1000
            marks += abs(lum - lum2) > 40
        cols.append(marks > 0)
    groups, start = [], None
    for i, flag in enumerate(cols + [False]):
        if flag and start is None:
            start = i
        elif not flag and start is not None:
            groups.append((w - CROP_W + start, w - CROP_W + i))
            start = None
    return groups


def save_crop(path, data, w, h):
    """Angolo in alto a destra ingrandito 3 volte, con tacche verdi ogni 10 pixel dal bordo destro della finestra."""
    ruler = 22
    ow, oh = CROP_W * SCALE, CROP_H * SCALE + ruler
    img = bytearray(ow * oh * 4)
    for j in range(oh):
        for i in range(ow):
            sx, sy = w - CROP_W + i // SCALE, j // SCALE
            o = (j * ow + i) * 4
            inside = 0 <= sx < w and sy < h and j < CROP_H * SCALE
            img[o:o + 4] = data[(sy * w + sx) * 4:(sy * w + sx) * 4 + 3] + b'\xff' if inside else b'\x70\x40\xd0\xff'
    for k in range(0, CROP_W, 10):
        tall = 14 if k % 50 == 0 else 7
        for j in range(oh - tall, oh):
            for d in range(SCALE):
                i = (CROP_W - 1 - k) * SCALE + d
                img[(j * ow + i) * 4:(j * ow + i) * 4 + 4] = b'\x00\xff\x00\xff'
    pp.save_png(path, bytes(img), ow, oh, max_w=2000)


def windows(wanted):
    """Finestre visibili, non ridotte a icona, con Riduci a icona, abbastanza grandi. (hwnd, programma, classe, rettangolo)."""
    for hwnd, pid, _title, cls in pp.top_windows():
        if not hwnd or pid == os.getpid() or not user32.IsWindowVisible(hwnd) or user32.IsIconic(hwnd):
            continue
        if not GetWindowLongW(hwnd, pp.GWL_STYLE) & pp.WS_MINIMIZEBOX:
            continue
        name = pp.image_of_pid(pid) or '?'
        if wanted and not any(w in cls.lower() or w in name for w in wanted):
            continue
        left, top, right, bottom = pp.rect(hwnd)
        if right - left >= 600 and bottom - top >= 300:
            yield hwnd, name, cls, (left, top, right, bottom)


def compare(hwnd, name, cls, rect):
    left, top, right, bottom = rect
    w = right - left
    frame = dwm_rect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS)
    measured = dwm_rect(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS)
    found = hit_band(hwnd, left, top, right)
    if not found:
        return 'nessun pulsante nelle risposte (o non risponde)'
    parts = ' '.join('%s:%d' % (NAMES.get(c, c), n) for c, n in found['parts'])
    data = grab(hwnd, w, bottom - top)
    if not data:
        return 'risposta: x %d..%d (larga %d: %s) | PrintWindow non riuscito' % (found['left'], found['right'], found['width'], parts)
    centers = [(a + b) // 2 for a, b in clusters(data, w)]
    system = 'nessuna' if not measured or measured.right <= measured.left else 'x %d..%d (larga %d)' % (
        left + measured.left, left + measured.right, measured.right - measured.left)
    edge = frame.right - left
    return ('risposta: x %d..%d della finestra (larga %d: %s), alta y+0..%d | Windows: %s | cornice visibile fino a x=%d\n'
            '      disegnati (la finestra si ridisegna, riferimento incerto di +/-8 px), centri dei simboli: %s -> '
            'px dal bordo visibile: %s | px dall\'inizio della risposta: %s' % (
                found['left'] - left, found['right'] - left, found['width'], parts, found['bottom'] - top - 1, system, edge,
                centers, [edge - c for c in centers], [c - (found['left'] - left) for c in centers]))


WindowFromPoint = pp.proto(user32, 'WindowFromPoint', wt.HWND, wt.POINT)
GetAncestor = pp.proto(user32, 'GetAncestor', wt.HWND, wt.HWND, wt.UINT)


def on_screen(hwnd, rect):
    """Che cosa si vede davvero nell'angolo in alto a destra dello schermo, se quella finestra e' in primo piano lì:
    simboli disegnati (centri, in coordinate della finestra) e chi copre la zona altrimenti."""
    left, top, right, bottom = rect
    frame = dwm_rect(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS)
    edge = frame.right - left
    x0, width = frame.right - CROP_W, CROP_W
    covering = {WindowFromPoint(wt.POINT(x0 + dx, top + 18)) for dx in (20, 60, 100, 140, 180, 240, 300)}
    roots = {GetAncestor(h, 2) if h else 0 for h in covering}
    if not roots <= {hwnd}:
        return 'coperta da: %s' % ', '.join(sorted({WindowTools_class(h) for h in roots if h != hwnd})) or 'altro'
    data = pp.capture(x0, top, width, CROP_H)
    centers = [(a + b) // 2 - (left - x0) for a, b in clusters(data, width)]
    return 'a schermo, centri dei simboli: %s -> px dal bordo visibile (%d): %s' % (
        centers, edge, [edge - c for c in centers])


_WindowTools_class_cache = {}


def WindowTools_class(hwnd):
    if hwnd not in _WindowTools_class_cache:
        name = ctypes.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, name, 256)
        _WindowTools_class_cache[hwnd] = '%s/%s' % (name.value[:24], pp.image_of_pid(pp.user32.GetWindowThreadProcessId(hwnd, wt.DWORD())) or '?')
    return _WindowTools_class_cache[hwnd]


def main():
    args = sys.argv[1:]
    mode = args[0] if args and args[0].startswith('--') else '--rows'
    wanted = [a.lower() for a in args if not a.startswith('--')]
    for hwnd, name, cls, rect in windows(wanted):
        left, top, right, bottom = rect
        print('%s [%s] finestra=(%d,%d,%d,%d) dpi=%d' % (name, cls[:34], left, top, right, bottom, GetDpiForWindow(hwnd)))
        if mode == '--compare':
            print('   ' + compare(hwnd, name, cls, rect))
        elif mode == '--screen':
            found = hit_band(hwnd, left, top, right)
            answer = 'nessuna' if not found else 'x %d..%d (larga %d: %s)' % (
                found['left'] - left, found['right'] - left, found['width'],
                ' '.join('%s:%d' % (NAMES.get(c, c), n) for c, n in found['parts']))
            print('   risposta della finestra: %s | %s' % (answer, on_screen(hwnd, rect)))
        elif mode == '--exact':
            column = [what(hwnd, right - 13, y) for y in range(top - 1, top + 70)]
            if column[0] is None:
                print('   non risponde')
                continue
            print('   colonna: ' + show(runs_of(column)))
            rows = [top - 1 + i for i, c in enumerate(column) if c in BUTTONS]
            for y in sorted({min(rows), (min(rows) + max(rows)) // 2, max(rows)}):
                line = [what(hwnd, x, y) for x in range(right - 1, right - 331, -1)]
                print('   riga y=%d (top+%d): ' % (y, y - top) + ('non risponde' if None in line else show(runs_of(line))))
        else:
            start = time.perf_counter()
            answered = what(hwnd, right - 12, top + 16, 400) is not None
            if not answered:
                print('    non risponde')
                continue
            for offset in (0, 1, 2, 4, 8, 12, 16, 22, 28, 32, 36, 40, 48, 60):
                runs = row(hwnd, right, top + offset)
                print('    y+%-3d %s' % (offset, 'non risponde' if runs is None else show(runs)))
                if runs is None:
                    break
            print('    tempo %.0f ms' % ((time.perf_counter() - start) * 1000))


if __name__ == '__main__':
    main()
