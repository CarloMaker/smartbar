# park_probe.py -- spike di fattibilita' del "parcheggio" di finestre di altri programmi.
# Usa soltanto finestre di prova lanciate da questo script (Blocco note, Mappa caratteri, Edge con profilo temporaneo).
#   python -u park_probe.py main   lancia le finestre di prova ed esegue le prove
#   python -u park_probe.py check  verifica l'esito dell'ultima prova e ripulisce

import ctypes
import ctypes.wintypes as wt
import json
import os
import random
import shutil
import struct
import subprocess
import sys
import time
import traceback
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'out')
STATE = os.path.join(OUT, 'state.json')
TAG = 'park-probe-%06x' % random.getrandbits(24)

user32 = ctypes.WinDLL('user32', use_last_error=True)
gdi32 = ctypes.WinDLL('gdi32', use_last_error=True)
dwmapi = ctypes.WinDLL('dwmapi', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)


def proto(dll, name, restype, *argtypes):
    fn = getattr(dll, name)
    fn.restype = restype
    fn.argtypes = list(argtypes)
    return fn


# --- costanti ---
WS_POPUP = 0x80000000
WS_CHILD = 0x40000000
WS_CLIPCHILDREN = 0x02000000
WS_CAPTION = 0x00C00000
WS_THICKFRAME = 0x00040000
WS_MINIMIZEBOX = 0x00020000
WS_MAXIMIZEBOX = 0x00010000
WS_SYSMENU = 0x00080000
WS_EX_TOPMOST = 0x00000008
WS_EX_TOOLWINDOW = 0x00000080
GWL_STYLE = -16
GWL_EXSTYLE = -20
SWP_NOZORDER, SWP_FRAMECHANGED = 0x4, 0x20
SWP_NOACTIVATE, SWP_SHOWWINDOW = 0x10, 0x40
HWND_TOP = 0
SW_RESTORE = 9
SW_SHOWNOACTIVATE = 4
WM_PAINT, WM_SETTEXT = 0x000F, 0x000C
PM_REMOVE = 1
SM_CXSCREEN, SM_CYSCREEN = 0, 1
DWMWA_CLOAKED = 14
SRCCOPY, CAPTUREBLT = 0x00CC0020, 0x40000000
DWM_TNP_RECTDESTINATION, DWM_TNP_OPACITY = 0x1, 0x4
DWM_TNP_VISIBLE, DWM_TNP_SOURCECLIENTAREAONLY = 0x8, 0x10
RAIL_W = 220
BG_BGR = (160, 40, 200)      # colore (B, G, R) dello sfondo della rotaia
PROCESS_QUERY_LIMITED_INFORMATION, SYNCHRONIZE = 0x1000, 0x100000
INPUT_MOUSE, INPUT_KEYBOARD = 0, 1
MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP = 0x0002, 0x0004
KEYEVENTF_KEYUP, KEYEVENTF_UNICODE = 0x0002, 0x0004

HTML = '''<!doctype html><html><head><meta charset="utf-8"><title>%s</title>
<style>body{margin:0;font-family:"Segoe UI",Arial;display:flex;height:100vh}
div{flex:1;display:flex;align-items:center;justify-content:center;font-size:46px;color:#fff;font-weight:700}
</style></head><body>
<div style="background:#c0392b">ROSSO</div><div style="background:#27ae60">VERDE</div>
<div style="background:#2980b9">BLU</div><div style="background:#f1c40f;color:#222">GIALLO</div>
</body></html>'''


# --- strutture ---
class RECT(ctypes.Structure):
    _fields_ = [('left', wt.LONG), ('top', wt.LONG), ('right', wt.LONG), ('bottom', wt.LONG)]


class POINT(ctypes.Structure):
    _fields_ = [('x', wt.LONG), ('y', wt.LONG)]


class MSG(ctypes.Structure):
    _fields_ = [('hwnd', wt.HWND), ('message', wt.UINT), ('wParam', wt.WPARAM), ('lParam', wt.LPARAM),
                ('time', wt.DWORD), ('pt', POINT)]


class PAINTSTRUCT(ctypes.Structure):
    _fields_ = [('hdc', wt.HDC), ('fErase', wt.BOOL), ('rcPaint', RECT), ('fRestore', wt.BOOL),
                ('fIncUpdate', wt.BOOL), ('rgbReserved', ctypes.c_byte * 32)]


class GUITHREADINFO(ctypes.Structure):
    _fields_ = [('cbSize', wt.DWORD), ('flags', wt.DWORD), ('hwndActive', wt.HWND), ('hwndFocus', wt.HWND),
                ('hwndCapture', wt.HWND), ('hwndMenuOwner', wt.HWND), ('hwndMoveSize', wt.HWND),
                ('hwndCaret', wt.HWND), ('rcCaret', RECT)]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [('dx', wt.LONG), ('dy', wt.LONG), ('mouseData', wt.DWORD), ('dwFlags', wt.DWORD),
                ('time', wt.DWORD), ('dwExtraInfo', ctypes.c_void_p)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [('wVk', wt.WORD), ('wScan', wt.WORD), ('dwFlags', wt.DWORD), ('time', wt.DWORD),
                ('dwExtraInfo', ctypes.c_void_p)]


class INPUT_UNION(ctypes.Union):
    _fields_ = [('mi', MOUSEINPUT), ('ki', KEYBDINPUT)]


class INPUT(ctypes.Structure):
    _anonymous_ = ('u',)
    _fields_ = [('type', wt.DWORD), ('u', INPUT_UNION)]


WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)
WNDENUMPROC = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


class WNDCLASSEXW(ctypes.Structure):
    _fields_ = [('cbSize', wt.UINT), ('style', wt.UINT), ('lpfnWndProc', WNDPROC), ('cbClsExtra', ctypes.c_int),
                ('cbWndExtra', ctypes.c_int), ('hInstance', wt.HANDLE), ('hIcon', wt.HANDLE), ('hCursor', wt.HANDLE),
                ('hbrBackground', wt.HANDLE), ('lpszMenuName', wt.LPCWSTR), ('lpszClassName', wt.LPCWSTR),
                ('hIconSm', wt.HANDLE)]


class DWM_THUMBNAIL_PROPERTIES(ctypes.Structure):
    _fields_ = [('dwFlags', wt.DWORD), ('rcDestination', RECT), ('rcSource', RECT), ('opacity', wt.BYTE),
                ('fVisible', wt.BOOL), ('fSourceClientAreaOnly', wt.BOOL)]


class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [('biSize', wt.DWORD), ('biWidth', wt.LONG), ('biHeight', wt.LONG), ('biPlanes', wt.WORD),
                ('biBitCount', wt.WORD), ('biCompression', wt.DWORD), ('biSizeImage', wt.DWORD),
                ('biXPelsPerMeter', wt.LONG), ('biYPelsPerMeter', wt.LONG), ('biClrUsed', wt.DWORD),
                ('biClrImportant', wt.DWORD)]


class BITMAPINFO(ctypes.Structure):
    _fields_ = [('bmiHeader', BITMAPINFOHEADER), ('bmiColors', wt.DWORD * 3)]


# --- firme ---
VP, H = ctypes.c_void_p, wt.HWND
proto(user32, 'SetProcessDpiAwarenessContext', wt.BOOL, VP)
proto(user32, 'SetThreadDpiHostingBehavior', wt.BOOL, ctypes.c_int)
proto(user32, 'GetSystemMetrics', ctypes.c_int, ctypes.c_int)
proto(user32, 'RegisterClassExW', wt.WORD, ctypes.POINTER(WNDCLASSEXW))
proto(user32, 'CreateWindowExW', H, wt.DWORD, wt.LPCWSTR, wt.LPCWSTR, wt.DWORD, ctypes.c_int, ctypes.c_int,
      ctypes.c_int, ctypes.c_int, H, wt.HANDLE, wt.HANDLE, VP)
proto(user32, 'DefWindowProcW', ctypes.c_ssize_t, H, wt.UINT, wt.WPARAM, wt.LPARAM)
proto(user32, 'BeginPaint', wt.HDC, H, ctypes.POINTER(PAINTSTRUCT))
proto(user32, 'EndPaint', wt.BOOL, H, ctypes.POINTER(PAINTSTRUCT))
proto(user32, 'GetClientRect', wt.BOOL, H, ctypes.POINTER(RECT))
proto(user32, 'FillRect', ctypes.c_int, wt.HDC, ctypes.POINTER(RECT), wt.HANDLE)
proto(user32, 'PeekMessageW', wt.BOOL, ctypes.POINTER(MSG), H, wt.UINT, wt.UINT, wt.UINT)
proto(user32, 'TranslateMessage', wt.BOOL, ctypes.POINTER(MSG))
proto(user32, 'DispatchMessageW', ctypes.c_ssize_t, ctypes.POINTER(MSG))
proto(user32, 'ShowWindow', wt.BOOL, H, ctypes.c_int)
proto(user32, 'DestroyWindow', wt.BOOL, H)
proto(user32, 'SetWindowPos', wt.BOOL, H, H, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT)
proto(user32, 'GetWindowRect', wt.BOOL, H, ctypes.POINTER(RECT))
proto(user32, 'GetWindowLongPtrW', ctypes.c_ssize_t, H, ctypes.c_int)
proto(user32, 'SetWindowLongPtrW', ctypes.c_ssize_t, H, ctypes.c_int, ctypes.c_ssize_t)
proto(user32, 'GetParent', H, H)
proto(user32, 'SetParent', H, H, H)
proto(user32, 'EnumWindows', wt.BOOL, WNDENUMPROC, wt.LPARAM)
proto(user32, 'GetWindowThreadProcessId', wt.DWORD, H, ctypes.POINTER(wt.DWORD))
proto(user32, 'GetWindowTextW', ctypes.c_int, H, wt.LPWSTR, ctypes.c_int)
proto(user32, 'GetClassNameW', ctypes.c_int, H, wt.LPWSTR, ctypes.c_int)
proto(user32, 'FindWindowExW', H, H, H, wt.LPCWSTR, wt.LPCWSTR)
proto(user32, 'IsWindow', wt.BOOL, H)
proto(user32, 'IsWindowVisible', wt.BOOL, H)
proto(user32, 'IsIconic', wt.BOOL, H)
proto(user32, 'IsZoomed', wt.BOOL, H)
proto(user32, 'GetDC', wt.HDC, H)
proto(user32, 'ReleaseDC', ctypes.c_int, H, wt.HDC)
proto(user32, 'FindWindowExW', H, H, H, wt.LPCWSTR, wt.LPCWSTR)
proto(user32, 'GetCursorPos', wt.BOOL, ctypes.POINTER(POINT))
proto(user32, 'SetCursorPos', wt.BOOL, ctypes.c_int, ctypes.c_int)
proto(user32, 'WindowFromPoint', H, POINT)
proto(user32, 'GetForegroundWindow', H)
proto(user32, 'GetGUIThreadInfo', wt.BOOL, wt.DWORD, ctypes.POINTER(GUITHREADINFO))
proto(user32, 'SendMessageW', ctypes.c_ssize_t, H, wt.UINT, wt.WPARAM, wt.LPARAM)
proto(user32, 'SendInput', wt.UINT, wt.UINT, ctypes.POINTER(INPUT), ctypes.c_int)
proto(gdi32, 'CreateSolidBrush', wt.HANDLE, wt.DWORD)
proto(gdi32, 'CreateCompatibleDC', wt.HDC, wt.HDC)
proto(gdi32, 'CreateCompatibleBitmap', wt.HANDLE, wt.HDC, ctypes.c_int, ctypes.c_int)
proto(gdi32, 'SelectObject', wt.HANDLE, wt.HDC, wt.HANDLE)
proto(gdi32, 'BitBlt', wt.BOOL, wt.HDC, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.HDC,
      ctypes.c_int, ctypes.c_int, wt.DWORD)
proto(gdi32, 'GetDIBits', ctypes.c_int, wt.HDC, wt.HANDLE, wt.UINT, wt.UINT, VP, ctypes.POINTER(BITMAPINFO), wt.UINT)
proto(gdi32, 'DeleteObject', wt.BOOL, wt.HANDLE)
proto(gdi32, 'DeleteDC', wt.BOOL, wt.HDC)
proto(dwmapi, 'DwmRegisterThumbnail', ctypes.c_long, H, H, ctypes.POINTER(wt.HANDLE))
proto(dwmapi, 'DwmUnregisterThumbnail', ctypes.c_long, wt.HANDLE)
proto(dwmapi, 'DwmUpdateThumbnailProperties', ctypes.c_long, wt.HANDLE, ctypes.POINTER(DWM_THUMBNAIL_PROPERTIES))
proto(dwmapi, 'DwmSetWindowAttribute', ctypes.c_long, H, wt.DWORD, VP, wt.DWORD)
proto(dwmapi, 'DwmGetWindowAttribute', ctypes.c_long, H, wt.DWORD, VP, wt.DWORD)
proto(kernel32, 'GetModuleHandleW', wt.HANDLE, wt.LPCWSTR)
proto(kernel32, 'OpenProcess', wt.HANDLE, wt.DWORD, wt.BOOL, wt.DWORD)
proto(kernel32, 'WaitForSingleObject', wt.DWORD, wt.HANDLE, wt.DWORD)
proto(kernel32, 'CloseHandle', wt.BOOL, wt.HANDLE)

HOST = None
BRUSH = None
_CLASS = 'ParkProbeHost_' + TAG


def hr(hr_value, what):
    if hr_value < 0:
        raise ctypes.WinError(hr_value & 0xFFFFFFFF, what)


def _wnd_proc(hwnd, msg, wparam, lparam):
    if msg == WM_PAINT:
        ps = PAINTSTRUCT()
        hdc = user32.BeginPaint(hwnd, ctypes.byref(ps))
        rc = RECT()
        user32.GetClientRect(hwnd, ctypes.byref(rc))
        user32.FillRect(hdc, ctypes.byref(rc), BRUSH)
        user32.EndPaint(hwnd, ctypes.byref(ps))
        return 0
    return user32.DefWindowProcW(hwnd, msg, wparam, lparam)


WNDPROC_CB = WNDPROC(_wnd_proc)


def pump(seconds):
    end = time.time() + seconds
    msg = MSG()
    while True:
        while user32.PeekMessageW(ctypes.byref(msg), None, 0, 0, PM_REMOVE):
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        if time.time() >= end:
            return
        time.sleep(0.01)


def top_windows():
    out = []

    @WNDENUMPROC
    def cb(hwnd, _):
        if hwnd:
            pid = wt.DWORD()
            user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
            t = ctypes.create_unicode_buffer(512)
            user32.GetWindowTextW(hwnd, t, 512)
            c = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, c, 256)
            out.append((hwnd, pid.value, t.value, c.value))
        return True

    user32.EnumWindows(cb, 0)
    return out


def wait_for(pred, timeout):
    end = time.time() + timeout
    while time.time() < end:
        for hwnd, pid, title, cls in top_windows():
            if title and user32.IsWindowVisible(hwnd) and pred(pid, title, cls):
                return hwnd, pid
        pump(0.4)
    return None, None


def tasklist_pids():
    out = subprocess.run(['tasklist', '/FO', 'CSV', '/NH'], capture_output=True, text=True,
                         errors='replace').stdout
    res = {}
    for line in out.splitlines():
        parts = line.split('","')
        if len(parts) >= 2 and parts[0].startswith('"') and parts[1].isdigit():
            res.setdefault(parts[0][1:].lower(), set()).add(int(parts[1]))
    return res


def image_of_pid(pid):
    out = subprocess.run(['tasklist', '/FI', 'PID eq %d' % pid, '/FO', 'CSV', '/NH'], capture_output=True,
                         text=True, errors='replace').stdout
    parts = out.split('","')
    if len(parts) >= 2 and parts[1].strip().isdigit():
        return parts[0].lstrip('"').lower()
    return None


def rect(hwnd):
    r = RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def move(hwnd, x, y, w, h, flags=SWP_NOZORDER | SWP_NOACTIVATE):
    return user32.SetWindowPos(hwnd, None, x, y, w, h, flags)


def ensure_normal(hwnd):
    if user32.IsIconic(hwnd) or user32.IsZoomed(hwnd):
        user32.ShowWindow(hwnd, SW_RESTORE)
        pump(0.5)


def cloak(hwnd, on):
    """Nasconde la finestra a schermo, in Alt-Tab e nella barra delle applicazioni senza ridurla a icona."""
    v = wt.DWORD(1 if on else 0)
    return dwmapi.DwmSetWindowAttribute(hwnd, DWMWA_CLOAKED, ctypes.byref(v), 4)


def is_cloaked(hwnd):
    v = wt.DWORD()
    if dwmapi.DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, ctypes.byref(v), 4) < 0:
        return -1
    return v.value


def set_thumb(thumb, dest=None, visible=True):
    p = DWM_THUMBNAIL_PROPERTIES()
    p.dwFlags = DWM_TNP_VISIBLE | DWM_TNP_OPACITY | DWM_TNP_SOURCECLIENTAREAONLY
    if dest is not None:
        p.dwFlags |= DWM_TNP_RECTDESTINATION
        p.rcDestination = dest
    p.opacity = 255
    p.fVisible = visible
    p.fSourceClientAreaOnly = True
    hr(dwmapi.DwmUpdateThumbnailProperties(thumb, ctypes.byref(p)), 'DwmUpdateThumbnailProperties')


def layout_thumbs(panel_w, panel_h, active):
    """Colloca le anteprime nella rotaia di destra; restituisce il rettangolo di ogni tessera (coordinate finestra)."""
    rail_x0 = panel_w - RAIL_W
    tile_h = min(270, (panel_h - 20) // max(1, len(active)))
    tiles = {}
    for i, s in enumerate(active):
        top = 10 + i * tile_h
        r = RECT(rail_x0 + 20, top + 10, rail_x0 + RAIL_W - 20, top + tile_h - 30)
        set_thumb(s['thumb'], r, True)
        tiles[s['name']] = (r.left, r.top, r.right, r.bottom)
    return tiles


def capture(x, y, w, h):
    sdc = user32.GetDC(None)
    mdc = gdi32.CreateCompatibleDC(sdc)
    bmp = gdi32.CreateCompatibleBitmap(sdc, w, h)
    old = gdi32.SelectObject(mdc, bmp)
    gdi32.BitBlt(mdc, 0, 0, w, h, sdc, x, y, SRCCOPY | CAPTUREBLT)
    gdi32.SelectObject(mdc, old)
    bmi = BITMAPINFO()
    bmi.bmiHeader.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bmi.bmiHeader.biWidth = w
    bmi.bmiHeader.biHeight = -h
    bmi.bmiHeader.biPlanes = 1
    bmi.bmiHeader.biBitCount = 32
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mdc, bmp, 0, h, buf, ctypes.byref(bmi), 0)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mdc)
    user32.ReleaseDC(None, sdc)
    return buf.raw[:w * h * 4]


def save_png(path, bgra, w, h, max_w=1000):
    f = max(1, -(-w // max_w))
    w2, h2 = len(range(0, w, f)), len(range(0, h, f))
    raw = bytearray()
    for y in range(0, h, f):
        row = bgra[y * w * 4:(y + 1) * w * 4]
        rgb = bytearray(w2 * 3)
        rgb[0::3], rgb[1::3], rgb[2::3] = row[2::4 * f], row[1::4 * f], row[0::4 * f]
        raw.append(0)
        raw += rgb

    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)

    data = (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w2, h2, 8, 2, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(bytes(raw), 6)) + chunk(b'IEND', b''))
    with open(path, 'wb') as fh:
        fh.write(data)


def region_stats(bgra, w, x0, y0, x1, y1, step=3):
    colors = set()
    lum = []
    for y in range(y0, y1, step):
        row = bgra[y * w * 4:(y + 1) * w * 4]
        for i in range(x0 * 4, x1 * 4, step * 4):
            b, g, r = row[i], row[i + 1], row[i + 2]
            colors.add((r << 16) | (g << 8) | b)
            lum.append(0.114 * b + 0.587 * g + 0.299 * r)
    if not lum:
        return 0, 0.0, 0.0
    m = sum(lum) / len(lum)
    sd = (sum((v - m) ** 2 for v in lum) / len(lum)) ** 0.5
    return len(colors), round(m, 1), round(sd, 1)


def content_bbox(bgra, w, x0, y0, x1, y1):
    """Rettangolo dei pixel diversi dallo sfondo della rotaia = area coperta dall'anteprima."""
    minx = miny = maxx = maxy = None
    for y in range(y0, y1, 2):
        row = bgra[y * w * 4:(y + 1) * w * 4]
        xs = [x for x in range(x0, x1, 2)
              if abs(row[x * 4] - BG_BGR[0]) + abs(row[x * 4 + 1] - BG_BGR[1]) + abs(row[x * 4 + 2] - BG_BGR[2]) > 24]
        if xs:
            miny = y if miny is None else miny
            maxy = y
            minx = min(xs) if minx is None else min(minx, min(xs))
            maxx = max(xs) if maxx is None else max(maxx, max(xs))
    if minx is None:
        return None
    return (minx - x0, miny - y0, maxx - x0, maxy - y0)


def stage(label, fname, tiles):
    x, y, r, b = rect(HOST)
    w = r - x
    bgra = capture(x, y, w, b - y)
    for name, (x0, y0, x1, y1) in tiles.items():
        st = region_stats(bgra, w, x0, y0, x1, y1)
        bb = content_bbox(bgra, w, x0, y0, x1, y1)
        size = '%dx%d' % (bb[2] - bb[0], bb[3] - bb[1]) if bb else 'vuota'
        print(f'[{label}] {name:8s} colori={st[0]:5d} anteprima={size}', flush=True)
    save_png(os.path.join(OUT, fname), bgra, w, b - y)


def embed(subject, x, y, w, h):
    saved = (user32.GetWindowLongPtrW(subject, GWL_STYLE) & 0xFFFFFFFF,
             user32.GetWindowLongPtrW(subject, GWL_EXSTYLE) & 0xFFFFFFFF, rect(subject))
    user32.SetThreadDpiHostingBehavior(1)
    ctypes.set_last_error(0)
    user32.SetParent(subject, HOST)
    err = ctypes.get_last_error()
    new_style = (saved[0] | WS_CHILD) & ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU)
    user32.SetWindowLongPtrW(subject, GWL_STYLE, new_style)
    move(subject, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW)
    return saved, user32.GetParent(subject) == HOST, err


def unembed(subject, saved):
    st, ex, (x, y, r, b) = saved
    user32.SetThreadDpiHostingBehavior(1)
    user32.SetParent(subject, None)
    user32.SetWindowLongPtrW(subject, GWL_STYLE, st)
    user32.SetWindowLongPtrW(subject, GWL_EXSTYLE, ex)
    user32.SetWindowPos(subject, HWND_TOP, x, y, r - x, b - y, SWP_FRAMECHANGED | SWP_SHOWWINDOW | SWP_NOACTIVATE)


def send_mouse_click():
    arr = (INPUT * 2)()
    for i, flag in enumerate((MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP)):
        arr[i].type = INPUT_MOUSE
        arr[i].mi.dwFlags = flag
    user32.SendInput(2, arr, ctypes.sizeof(INPUT))


def send_text(text):
    arr = (INPUT * (2 * len(text)))()
    for i, ch in enumerate(text):
        for j, flag in enumerate((KEYEVENTF_UNICODE, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)):
            arr[2 * i + j].type = INPUT_KEYBOARD
            arr[2 * i + j].ki.wScan = ord(ch)
            arr[2 * i + j].ki.dwFlags = flag
    user32.SendInput(len(arr), arr, ctypes.sizeof(INPUT))


proto(user32, 'IsWindowEnabled', wt.BOOL, H)
proto(user32, 'EnumChildWindows', wt.BOOL, H, WNDENUMPROC, wt.LPARAM)


def describe(hwnd):
    t = ctypes.create_unicode_buffer(128)
    user32.GetWindowTextW(hwnd, t, 128)
    c = ctypes.create_unicode_buffer(128)
    user32.GetClassNameW(hwnd, c, 128)
    pid = wt.DWORD()
    user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
    return (f'{c.value} pid={pid.value} rect={rect(hwnd)} visibile={bool(user32.IsWindowVisible(hwnd))} '
            f'attiva={bool(user32.IsWindowEnabled(hwnd))} testo=<{t.value}>')


def children(hwnd):
    out = []

    @WNDENUMPROC
    def cb(h, _):
        out.append(describe(h))
        return True

    user32.EnumChildWindows(hwnd, cb, 0)
    return out


def interaction_test(subject):
    """Clicca davvero dentro la finestra incorporata e digita tre lettere: l'input reale arriva?"""
    # la casella "Caratteri da copiare" di Mappa caratteri e' un RichEdit; l'Edit semplice e' un campo nascosto
    edit = user32.FindWindowExW(subject['hwnd'], None, 'RICHEDIT50W', None) \
        or user32.FindWindowExW(subject['hwnd'], None, 'Edit', None)
    if not edit:
        return 'casella di testo non trovata'
    r = RECT()
    user32.GetWindowRect(edit, ctypes.byref(r))
    cx, cy = (r.left + r.right) // 2, (r.top + r.bottom) // 2
    old = POINT()
    user32.GetCursorPos(ctypes.byref(old))
    user32.SetCursorPos(cx, cy)
    pump(0.3)
    at = user32.WindowFromPoint(POINT(cx, cy))
    if at != edit:
        user32.SetCursorPos(old.x, old.y)
        for line in children(subject['hwnd']):
            print('[input] figlio:', line, flush=True)
        print('[input] controllo atteso:', describe(edit), flush=True)
        print('[input] sotto il cursore:', describe(at), flush=True)
        return 'sotto il cursore non c\'e\' il controllo atteso: click non inviato'
    send_mouse_click()
    pump(0.8)
    tid = user32.GetWindowThreadProcessId(edit, None)

    def focused():
        info = GUITHREADINFO()
        info.cbSize = ctypes.sizeof(GUITHREADINFO)
        return user32.GetGUIThreadInfo(tid, ctypes.byref(info)) and info.hwndFocus

    focus_ok = focused() == edit
    host_fg = user32.GetForegroundWindow() == HOST
    result = f'dopo_il_click fuoco_sul_controllo={bool(focus_ok)} host_in_primo_piano={bool(host_fg)}'
    attached = False
    if not focus_ok:
        # rimedio noto per le finestre incorporate: le code di input dei due thread restano agganciate
        # finche' si lavora nella finestra incorporata, e il fuoco si da' al controllo
        proto(kernel32, 'GetCurrentThreadId', wt.DWORD)
        proto(user32, 'AttachThreadInput', wt.BOOL, wt.DWORD, wt.DWORD, wt.BOOL)
        proto(user32, 'SetFocus', H, H)
        mine = kernel32.GetCurrentThreadId()
        attached = bool(user32.AttachThreadInput(mine, tid, True))
        user32.SetFocus(edit)
        pump(0.6)
        focus_ok = focused() == edit
        host_fg = user32.GetForegroundWindow() == HOST
        result += f' | code_agganciate={attached} fuoco_sul_controllo={bool(focus_ok)} host_in_primo_piano={bool(host_fg)}'
    # la digitazione va inviata solo se il fuoco e' davvero nel controllo, altrimenti finirebbe altrove
    if focus_ok and host_fg:
        send_text('ABC')
        pump(0.8)
        buf = ctypes.create_unicode_buffer(64)
        user32.SendMessageW(edit, 0x000D, 64, ctypes.cast(buf, VP).value)   # WM_GETTEXT
        result += f' testo_letto_dal_controllo=<{buf.value}>'
        empty = ctypes.c_wchar_p('')
        user32.SendMessageW(edit, WM_SETTEXT, 0, ctypes.cast(empty, VP).value)
    else:
        result += ' digitazione_non_inviata'
    if attached:
        user32.AttachThreadInput(mine, tid, False)
    user32.SetCursorPos(old.x, old.y)
    return result


def kill_processes(subjects):
    for s in subjects:
        for pid in s.get('kill_pids', ()):
            img = image_of_pid(pid)
            if img and img == s['image']:
                subprocess.run(['taskkill', '/PID', str(pid), '/T', '/F'], capture_output=True)


def cleanup(subjects):
    for s in subjects:
        try:
            if s.get('saved') and not s.get('restored'):
                unembed(s['hwnd'], s['saved'])
            if s.get('cloaked_now'):
                cloak(s['hwnd'], False)
        except Exception as e:  # solo finestre di prova: un errore qui non deve bloccare il resto
            print('[pulizia] errore su', s['name'], e, flush=True)
    pump(0.8)
    kill_processes(subjects)
    if HOST:
        user32.DestroyWindow(HOST)
    time.sleep(1.5)
    shutil.rmtree(os.path.join(OUT, 'edge-profile'), ignore_errors=True)
    left = sorted({s['image'] for s in subjects if tasklist_pids().get(s['image'])})
    print('[pulizia] processi di prova rimasti:', left or 'nessuno', flush=True)


def liveness(label, fname, subjects, tiles):
    """Cambia la forma di una finestra nascosta e guarda se l'anteprima la segue."""
    s = next((o for o in subjects if o.get('thumb') and o['name'] == 'notepad'), None) or \
        next((o for o in subjects if o.get('thumb')), None)
    if not s:
        return
    x, y, r, b = rect(s['hwnd'])
    print(f'[{label}] finestra {s["name"]} prima: {r - x}x{b - y}', flush=True)
    move(s['hwnd'], x, y, (r - x) + 260, b - y)
    pump(1.5)
    print(f'[{label}] finestra {s["name"]} dopo: {rect(s["hwnd"])[2] - x}x{b - y}', flush=True)
    stage(label, fname, tiles)
    move(s['hwnd'], x, y, r - x, b - y)
    pump(0.8)


def main():
    global HOST, BRUSH
    os.makedirs(OUT, exist_ok=True)
    user32.SetProcessDpiAwarenessContext(VP(-4))  # per-monitor v2: coordinate in pixel fisici
    SW, SH = user32.GetSystemMetrics(SM_CXSCREEN), user32.GetSystemMetrics(SM_CYSCREEN)
    PANEL_W = SW // 2
    RAIL_H = min(1000, SH - 100)
    print(f'[probe] build Windows {sys.getwindowsversion().build}, schermo {SW}x{SH}, tag {TAG}', flush=True)

    BRUSH = gdi32.CreateSolidBrush(0x00A828A0)
    hinst = kernel32.GetModuleHandleW(None)
    wc = WNDCLASSEXW()
    wc.cbSize = ctypes.sizeof(WNDCLASSEXW)
    wc.lpfnWndProc = WNDPROC_CB
    wc.hInstance = hinst
    wc.hbrBackground = BRUSH
    wc.lpszClassName = _CLASS
    user32.RegisterClassExW(ctypes.byref(wc))
    HOST = user32.CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, _CLASS, 'ParkProbeHost',
                                  WS_POPUP | WS_CLIPCHILDREN, SW - RAIL_W, (SH - RAIL_H) // 2, RAIL_W, RAIL_H,
                                  None, None, hinst, None)
    user32.ShowWindow(HOST, SW_SHOWNOACTIVATE)

    base = tasklist_pids()
    txt = os.path.join(OUT, TAG + '.txt')
    html = os.path.join(OUT, TAG + '.html')
    with open(txt, 'w', encoding='utf-8-sig') as f:
        f.write('\n'.join([TAG, 'Finestra di prova per il parcheggio.']
                          + ['Riga %d: lorem ipsum dolor sit amet, consectetur adipiscing elit.' % i for i in range(3, 40)]))
    with open(html, 'w', encoding='utf-8') as f:
        f.write(HTML % TAG)

    notepad_exe = os.path.join(os.environ['LOCALAPPDATA'], 'Microsoft', 'WindowsApps', 'notepad.exe')
    edge_exe = r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
    profile = os.path.join(OUT, 'edge-profile')
    devnull = dict(stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    procs = {
        'notepad': subprocess.Popen([notepad_exe, txt], **devnull),
        'charmap': subprocess.Popen([r'C:\Windows\System32\charmap.exe'], **devnull),
        'edge': subprocess.Popen([edge_exe, '--user-data-dir=' + profile, '--no-first-run', '--no-default-browser-check',
                                  '--disable-sync', '--window-position=150,150', '--window-size=1200,1000',
                                  'file:///' + html.replace('\\', '/')], **devnull),
    }
    specs = [
        ('notepad', 'notepad.exe', 30, lambda pid, title, cls: TAG in title and cls == 'Notepad'),
        ('charmap', 'charmap.exe', 15, lambda pid, title, cls: pid == procs['charmap'].pid),
        ('edge', 'msedge.exe', 60, lambda pid, title, cls: TAG in title and cls == 'Chrome_WidgetWin_1'),
    ]
    subjects = []
    try:
        now = None
        for name, image, timeout, pred in specs:
            # ogni finestra viene riconosciuta solo per titolo/classe univoci: le altre non vengono mai toccate
            hwnd, pid = wait_for(pred, timeout)
            s = {'name': name, 'image': image, 'kill_pids': {procs[name].pid}}
            subjects.append(s)
            if not hwnd:
                print(f'[probe] {name}: finestra non trovata, salto', flush=True)
                continue
            now = now or tasklist_pids()
            owned = pid not in base.get(image, set()) and pid in now.get(image, set())
            s.update({'hwnd': hwnd, 'pid': pid, 'owned': owned, 'thumb': None, 'saved': None, 'restored': True,
                      'cloaked_now': False})
            if owned:
                s['kill_pids'].add(pid)
            print(f'[probe] {name}: hwnd={hwnd} pid={pid} (avviato da me: {procs[name].pid}) rettangolo={rect(hwnd)} '
                  f'di-prova={owned}', flush=True)
        found = [s for s in subjects if s.get('hwnd')]
        for s in found:
            ensure_normal(s['hwnd'])
        pump(4.0)
        for s in found:
            th = wt.HANDLE()
            res = dwmapi.DwmRegisterThumbnail(HOST, s['hwnd'], ctypes.byref(th))
            if res < 0:
                print(f"[probe] anteprima DWM rifiutata per {s['name']}: hr=0x{res & 0xFFFFFFFF:08X}", flush=True)
                continue
            s['thumb'] = th.value
            s['orig'] = rect(s['hwnd'])
        found = [s for s in found if s['thumb']]

        # T1: anteprime vive con le finestre in vista
        tiles = layout_thumbs(RAIL_W, RAIL_H, found)
        pump(1.5)
        stage('T1 in vista', '01_anteprime.png', tiles)

        # T2a: finestre spostate fuori schermo
        for s in found:
            x, y, r, b = s['orig']
            move(s['hwnd'], SW + 100, y, r - x, b - y)
        pump(1.5)
        stage('T2 fuori schermo', '02_fuori_schermo.png', tiles)
        liveness('T2 fuori schermo, ridimensiono', '02b_fuori_schermo_resize.png', subjects, tiles)

        # T2b: finestre al loro posto ma nascoste (cloak)
        for s in found:
            x, y, r, b = s['orig']
            move(s['hwnd'], x, y, r - x, b - y)
            res = cloak(s['hwnd'], True)
            s['cloaked_now'] = res >= 0
            if res < 0:
                print(f"[T2 cloak] {s['name']}: cloak rifiutato, hr=0x{res & 0xFFFFFFFF:08X}", flush=True)
        pump(1.5)
        print('[T2 cloak] valore DWMWA_CLOAKED:', {s['name']: is_cloaked(s['hwnd']) for s in found}, flush=True)
        stage('T2 cloak', '03_cloak.png', tiles)
        liveness('T2 cloak, ridimensiono', '03b_cloak_resize.png', subjects, tiles)
        for s in found:
            cloak(s['hwnd'], False)
            s['cloaked_now'] = False

        # T4: incorporamento nel pannello a meta' schermo
        move(HOST, SW - PANEL_W, 0, PANEL_W, SH, SWP_NOACTIVATE | SWP_SHOWWINDOW)
        for s in found:
            if not s['owned']:
                print(f"[T4] {s['name']}: saltata (non verificata come finestra di prova)", flush=True)
                continue
            try:
                ensure_normal(s['hwnd'])
                set_thumb(s['thumb'], None, False)
                layout_thumbs(PANEL_W, SH, [o for o in found if o is not s])
                saved, ok, err = embed(s['hwnd'], 0, 0, PANEL_W - RAIL_W, SH)
                s['saved'], s['restored'] = saved, False
                pump(2.5)
                x, y, r, b = rect(HOST)
                w = r - x
                bgra = capture(x, y, w, b - y)
                st = region_stats(bgra, w, 40, 40, PANEL_W - RAIL_W - 40, SH - 40, step=9)
                print(f"[T4] {s['name']:8s} SetParent ok={ok} err={err} rettangolo={rect(s['hwnd'])} "
                      f'contenuto: colori={st[0]} lum_media={st[1]} lum_devst={st[2]}', flush=True)
                save_png(os.path.join(OUT, '04_incorporato_%s.png' % s['name']), bgra, w, b - y)
                save_png(os.path.join(OUT, '04b_cima_%s.png' % s['name']), bgra[:160 * w * 4], w, 160)
                if s['name'] == 'charmap':
                    print('[T4] input reale (click + digitazione):', interaction_test(s), flush=True)
            except Exception:
                print(f"[T4] {s['name']}: errore {traceback.format_exc().splitlines()[-1]}", flush=True)
            finally:
                if not s['restored'] and s['saved']:
                    unembed(s['hwnd'], s['saved'])
                    s['restored'] = True
                    pump(1.0)
                    print(f"[T4] {s['name']:8s} ripristinata: esiste={bool(user32.IsWindow(s['hwnd']))} "
                          f"rettangolo={rect(s['hwnd'])} originario={s['orig']} "
                          f"visibile={bool(user32.IsWindowVisible(s['hwnd']))}", flush=True)

        move(HOST, SW - RAIL_W, (SH - RAIL_H) // 2, RAIL_W, RAIL_H, SWP_NOACTIVATE | SWP_SHOWWINDOW)
        tiles = layout_thumbs(RAIL_W, RAIL_H, [s for s in found if user32.IsWindow(s['hwnd'])])
        pump(1.5)
        stage('T5 di nuovo in rotaia', '05_di_nuovo_rotaia.png', tiles)

        # T6 (rischio): la finestra resta incorporata e il processo host muore senza ripristinare
        victim = next((s for s in found if s['owned'] and s['name'] == 'notepad' and user32.IsWindow(s['hwnd'])), None) \
            or next((s for s in found if s['owned'] and user32.IsWindow(s['hwnd'])), None)
        if not victim:
            print('[T6] nessuna finestra di prova disponibile, prova saltata', flush=True)
            raise SystemExit(0)
        ensure_normal(victim['hwnd'])
        move(HOST, SW - PANEL_W, 0, PANEL_W, SH, SWP_NOACTIVATE | SWP_SHOWWINDOW)
        set_thumb(victim['thumb'], None, False)
        saved, ok, err = embed(victim['hwnd'], 0, 0, PANEL_W - RAIL_W, SH)
        pump(1.0)
        with open(STATE, 'w') as f:
            json.dump({'hwnd': victim['hwnd'], 'pid': victim['pid'], 'image': victim['image'], 'name': victim['name'],
                       'owned_pids': sorted(set().union(*[s['kill_pids'] for s in subjects])),
                       'images': sorted({s['image'] for s in subjects})}, f)
        print(f"[T6] '{victim['name']}' incorporata (ok={ok}); il processo host esce ora senza ripristinare", flush=True)
        os._exit(0)
    except SystemExit:
        raise
    except Exception:
        traceback.print_exc()
        raise
    finally:
        cleanup(subjects)


def check():
    with open(STATE) as f:
        st = json.load(f)
    time.sleep(2.5)
    hwnd, pid = st['hwnd'], st['pid']
    still = user32.IsWindow(hwnd)
    owner = wt.DWORD()
    if still:
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
    print(f"[check] finestra '{st['name']}' dopo la morte del host: esiste={bool(still)} stesso_processo={owner.value == pid}")
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, False, pid)
    if h:
        print(f"[check] processo {st['image']} pid={pid}: in esecuzione={kernel32.WaitForSingleObject(h, 0) == 0x102}")
        kernel32.CloseHandle(h)
    else:
        print(f"[check] processo {st['image']} pid={pid}: non piu' apribile (errore {ctypes.get_last_error()})")
    n = sum(1 for w, p, t, c in top_windows() if p == pid and t and user32.IsWindowVisible(w))
    print(f'[check] finestre visibili rimaste per quel processo: {n}')

    for p in st['owned_pids']:
        img = image_of_pid(p)
        if img and img in st['images']:
            subprocess.run(['taskkill', '/PID', str(p), '/T', '/F'], capture_output=True)
    time.sleep(1.5)
    shutil.rmtree(os.path.join(OUT, 'edge-profile'), ignore_errors=True)
    left = [i for i in st['images'] if tasklist_pids().get(i)]
    print('[check] pulizia fatta; processi di prova rimasti:', left or 'nessuno')


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == 'check':
        check()
    else:
        main()
