"""
valspot - plays Spotify only while you are dead or in the buy phase of a
VALORANT match, and pauses it everywhere else.

Sources of truth
  * Round phases : VALORANT's own live log (ShooterGame.log).
  * Your death   : the "< mouse > SWITCH PLAYER" prompt that VALORANT shows
                   under the spectated teammate's name once you are dead.
                   Read from the game window only while it is focused, at
                   any resolution (templates are stored at 1080p and scaled).
  * Spotify      : Windows media controls (the volume-flyout buttons).

Nothing here reads or writes the game process. It opens the log file with
full sharing (VALORANT can still rename/delete it) and takes tiny screenshots.

Usage (exe or script)
  valspot                 run (default; what double-clicking does)
  valspot check           print the live death-marker score once a second
  valspot shot            save a screenshot of the game window in 5 s
  valspot spotify         play 2 s then pause (Spotify control test)
  valspot replay LOG      feed an old ShooterGame.log through the rules (dry-run)
  valspot eval --clips CLIP... --ffmpeg FFMPEG [--height 1440]
                          score video clips with the real detector
  valspot template FRAME X Y W H --name N
                          cut a new marker template out of a screenshot
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import queue
import re
import sys
import threading
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

VERSION = "2.1.2"

# numpy's math library starts one worker thread per CPU core and reserves a
# buffer for each (762 MB reserved on a 32-thread CPU). valspot never needs
# them; one thread keeps it at ~23 MB reserved / 4 threads.
for _var in ("OPENBLAS_NUM_THREADS", "OMP_NUM_THREADS", "MKL_NUM_THREADS"):
    os.environ.setdefault(_var, "1")

FROZEN = getattr(sys, "frozen", False)
APP_DIR = Path(sys.executable).resolve().parent if FROZEN else Path(__file__).resolve().parent
RES_DIR = Path(getattr(sys, "_MEIPASS", APP_DIR))       # bundled templates live here in the exe
CONFIG_PATH = APP_DIR / "config.json"
EVENT_LOG = APP_DIR / "valspot.log"

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def _dpi_aware():
    """Physical pixels everywhere, so window rects match screenshot pixels."""
    import ctypes
    try:
        ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # per-monitor v2
        return
    except Exception:  # noqa: BLE001
        pass
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception:  # noqa: BLE001
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:  # noqa: BLE001
            pass


if os.name == "nt":
    _dpi_aware()

DEFAULT_TEMPLATES = [
    # x, y, size are in a 1080-pixel-tall frame; scaled to the real window height.
    {"file": "templates/switch_player.png", "x": 120, "y": 853, "ref_height": 1080},
    {"file": "templates/switch_player_small.png", "x": 118, "y": 849, "ref_height": 1080},
]

DEFAULT_CONFIG = {
    "log_path": "%LOCALAPPDATA%/VALORANT/Saved/Logs/ShooterGame.log",
    "pause_lead_seconds": 3.0,
    "buy_seconds": 30,
    "first_buy_seconds": 45,
    "half_time_round": 11,
    "poll_hz": 8,
    "death_threshold": 0.60,
    "death_confirm_frames": 3,
    "revive_frames": 32,
    "search_pad": 4,
    "only_when_focused": True,
    "calibrate_threshold": 0.80,
    "calibrate_interval": 2.0,
    "diagnostics": True,
    "diagnostics_interval": 8.0,
    "diagnostics_keep": 40,
    "dry_run": False,
    "templates": DEFAULT_TEMPLATES,
}


# --------------------------------------------------------------------------
# small helpers
# --------------------------------------------------------------------------
def now_str() -> str:
    return datetime.now().strftime("%H:%M:%S.%f")[:-3]


def log(msg: str) -> None:
    line = f"[{now_str()}] {msg}"
    print(line, flush=True)
    try:
        with EVENT_LOG.open("a", encoding="utf-8") as f:
            f.write(line + "\n")
    except OSError:
        pass


def load_config() -> dict:
    cfg = json.loads(json.dumps(DEFAULT_CONFIG))     # deep copy
    if CONFIG_PATH.exists():
        with CONFIG_PATH.open(encoding="utf-8") as f:
            cfg.update(json.load(f))
    else:
        try:
            CONFIG_PATH.write_text(json.dumps(DEFAULT_CONFIG, indent=2), encoding="utf-8")
        except OSError:
            pass
    cfg["log_path"] = os.path.expandvars(cfg["log_path"])
    return cfg


def resolve(rel: str) -> Path:
    """A user-supplied file next to the exe wins over the bundled copy."""
    p = APP_DIR / rel
    return p if p.exists() else RES_DIR / rel


# --------------------------------------------------------------------------
# Spotify via Windows media sessions (SMTC)
# --------------------------------------------------------------------------
class Spotify:
    """play()/pause() are idempotent: they look at the real state first."""

    PLAYING = 4

    def __init__(self, dry_run: bool = False):
        self.dry_run = dry_run
        self._loop = asyncio.new_event_loop()
        threading.Thread(target=self._loop.run_forever, daemon=True).start()
        self._ok = True
        try:
            from winrt.windows.media.control import (  # noqa: F401
                GlobalSystemMediaTransportControlsSessionManager,
            )
        except ImportError:
            self._ok = False
            log("WARN Windows media controls not available; Spotify control disabled")

    def _run(self, coro, timeout=5.0):
        return asyncio.run_coroutine_threadsafe(coro, self._loop).result(timeout)

    async def _session(self):
        from winrt.windows.media.control import (
            GlobalSystemMediaTransportControlsSessionManager as Mgr,
        )
        mgr = await Mgr.request_async()
        for s in mgr.get_sessions():
            if "spotify" in (s.source_app_user_model_id or "").lower():
                return s
        return None

    async def _status(self):
        s = await self._session()
        return None if s is None else s.get_playback_info().playback_status

    async def _now_playing(self):
        s = await self._session()
        if s is None:
            return None
        p = await s.try_get_media_properties_async()
        return f"{p.artist} - {p.title}"

    async def _do(self, action: str):
        s = await self._session()
        if s is None:
            return "Spotify not open"
        playing = s.get_playback_info().playback_status == self.PLAYING
        if action == "play":
            if playing:
                return "already"
            ok = await s.try_play_async()
        else:
            if not playing:
                return "already"
            ok = await s.try_pause_async()
        return "ok" if ok else "refused"

    def status(self):
        if not self._ok:
            return None
        try:
            return self._run(self._status())
        except Exception as e:  # noqa: BLE001
            log(f"WARN spotify status failed: {e!r}")
            return None

    def now_playing(self):
        if not self._ok:
            return None
        try:
            return self._run(self._now_playing())
        except Exception as e:  # noqa: BLE001
            return f"? ({e!r})"

    def play(self, why: str):
        self._act("play", why)

    def pause(self, why: str):
        self._act("pause", why)

    def _act(self, action: str, why: str):
        if self.dry_run or not self._ok:
            log(f"SPOTIFY {action.upper():5} (dry-run) <- {why}")
            return
        try:
            res = self._run(self._do(action))
        except Exception as e:  # noqa: BLE001
            res = f"error {e!r}"
        log(f"SPOTIFY {action.upper():5} [{res}] <- {why}")


# --------------------------------------------------------------------------
# VALORANT log
# --------------------------------------------------------------------------
RE_STAMP = re.compile(r"^\[(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})\.(\d{2}):(\d{3})\]")
RE_GAMEPLAY = re.compile(r"Gameplay started at local time ([\d.]+)")
RE_ROUND_END = re.compile(r"OnRoundEnded for round '(\d+)'")
RE_FLOW = re.compile(r"LogGameFlowStateManager: .*Transitioning from (\w+) to (\w+)")
RE_MODE = re.compile(r"/Game/GameModes/(\w+)/")


@dataclass
class Event:
    kind: str           # mode | match_start | pregame | buy | combat | round_end | match_end
    value: object = None
    stamp: float = 0.0  # log timestamp (the log is in UTC; only differences matter)


def parse_line(line: str) -> Event | None:
    m = RE_GAMEPLAY.search(line)
    if m:
        t = float(m.group(1))
        if t < 5.0:
            return Event("buy", t)
        if t >= 20.0:
            return Event("combat", t)
        return None
    m = RE_ROUND_END.search(line)
    if m:
        return Event("round_end", int(m.group(1)))
    m = RE_MODE.search(line)
    if m:
        return Event("mode", m.group(1))
    m = RE_FLOW.search(line)
    if m:
        dst = m.group(2)
        if dst == "InGame":
            return Event("match_start", dst)
        if dst in ("TransitionToMainMenu", "MainMenu"):
            return Event("match_end", dst)
        if dst in ("TransitionToPregame", "Pregame"):
            return Event("pregame", dst)
    return None


def line_stamp(line: str) -> float:
    m = RE_STAMP.match(line)
    if not m:
        return 0.0
    y, mo, d, h, mi, s, ms = (int(x) for x in m.groups())
    try:
        return datetime(y, mo, d, h, mi, s, ms * 1000).timestamp()
    except ValueError:
        return 0.0


def open_shared(path: Path):
    """Open read-only with FILE_SHARE_READ|WRITE|DELETE, so VALORANT can still
    rename or delete its log while we follow it (a plain open() blocks that)."""
    import ctypes
    import msvcrt
    from ctypes import wintypes
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.CreateFileW.restype = wintypes.HANDLE
    k32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                                wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    GENERIC_READ, SHARE_ALL, OPEN_EXISTING = 0x80000000, 0x1 | 0x2 | 0x4, 3
    h = k32.CreateFileW(str(path), GENERIC_READ, SHARE_ALL, None, OPEN_EXISTING, 0x80, None)
    if h in (None, wintypes.HANDLE(-1).value):
        raise FileNotFoundError(str(path))
    fd = msvcrt.open_osfhandle(h, os.O_RDONLY)
    return os.fdopen(fd, "rb")


class LogTailer(threading.Thread):
    """Follows ShooterGame.log; reopens when VALORANT starts a new one."""

    def __init__(self, path: str, out: queue.Queue):
        super().__init__(daemon=True)
        self.path = Path(path)
        self.out = out

    def run(self):
        f, ident, buf = None, None, b""
        while True:
            try:
                st = os.stat(self.path)
            except OSError:
                if f:
                    f.close()
                    f = None
                    log("VALORANT log gone (game closed?); waiting")
                time.sleep(2)
                continue
            if f is None or st.st_ino != ident or st.st_size < f.tell():
                first_open = ident is None
                if f:
                    f.close()
                    f = None
                    log("VALORANT started a new log; following it")
                else:
                    log(f"following {self.path}")
                try:
                    f = open_shared(self.path)
                except OSError:
                    time.sleep(1)
                    continue
                ident = os.fstat(f.fileno()).st_ino or st.st_ino
                # first open: skip history; a fresh log after rotation: read it all
                f.seek(0, os.SEEK_END if first_open else os.SEEK_SET)
                buf = b""
            chunk = f.read(65536)
            if not chunk:
                time.sleep(0.1)
                continue
            buf += chunk
            *lines, buf = buf.split(b"\n")
            for raw in lines:
                line = raw.decode("utf-8", "replace")
                ev = parse_line(line)
                if ev:
                    ev.stamp = line_stamp(line)
                    self.out.put(ev)


# --------------------------------------------------------------------------
# game window + screen
# --------------------------------------------------------------------------
class GameWindow:
    """Finds the VALORANT window and its client area in screen pixels.
    Plain Win32 queries; nothing is sent to the game."""

    GAME_EXE = "valorant-win64-shipping.exe"

    def __init__(self):
        import ctypes
        from ctypes import wintypes
        self.ct, self.wt = ctypes, wintypes
        self.u32 = ctypes.WinDLL("user32", use_last_error=True)
        self.k32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.u32.GetForegroundWindow.restype = wintypes.HWND
        self.u32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
        self.u32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
        self.u32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
        self.u32.IsWindowVisible.argtypes = [wintypes.HWND]
        self.u32.IsIconic.argtypes = [wintypes.HWND]
        self.k32.OpenProcess.restype = wintypes.HANDLE
        self.k32.QueryFullProcessImageNameW.argtypes = [
            wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD)]
        self._cache_hwnd = None
        self._cache_ok = False
        self.last_exe = ""

    def _exe_of(self, hwnd) -> str:
        ct, wt = self.ct, self.wt
        pid = wt.DWORD()
        self.u32.GetWindowThreadProcessId(hwnd, ct.byref(pid))
        h = self.k32.OpenProcess(0x1000, False, pid.value)   # QUERY_LIMITED_INFORMATION
        if not h:
            return ""
        try:
            buf = ct.create_unicode_buffer(1024)
            n = wt.DWORD(len(buf))
            ok = self.k32.QueryFullProcessImageNameW(h, 0, buf, ct.byref(n))
            return os.path.basename(buf.value) if ok else ""
        finally:
            self.k32.CloseHandle(h)

    def rect(self, hwnd):
        """(left, top, width, height) of the client area, or None."""
        ct, wt = self.ct, self.wt
        r = wt.RECT()
        if not self.u32.GetClientRect(hwnd, ct.byref(r)):
            return None
        p = wt.POINT(0, 0)
        if not self.u32.ClientToScreen(hwnd, ct.byref(p)):
            return None
        w, h = r.right - r.left, r.bottom - r.top
        return (p.x, p.y, w, h) if w > 100 and h > 100 else None

    def focused_rect(self):
        """Client rect if VALORANT is the foreground window, else None."""
        hwnd = self.u32.GetForegroundWindow()
        if hwnd != self._cache_hwnd:
            self._cache_hwnd = hwnd
            self.last_exe = self._exe_of(hwnd) if hwnd else ""
            self._cache_ok = self.last_exe.lower() == self.GAME_EXE
        if not self._cache_ok or self.u32.IsIconic(hwnd):
            return None
        return self.rect(hwnd)

    def find_rect(self):
        """Client rect of the biggest visible VALORANT window, focused or not."""
        ct, wt = self.ct, self.wt
        found = []
        proto = ct.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)

        def cb(hwnd, _):
            if self.u32.IsWindowVisible(hwnd) and self._exe_of(hwnd).lower() == self.GAME_EXE:
                r = self.rect(hwnd)
                if r:
                    found.append(r)
            return True
        self.u32.EnumWindows(proto(cb), 0)
        return max(found, key=lambda r: r[2] * r[3]) if found else None


class Screen:
    def __init__(self):
        import mss
        self._mss = mss.MSS() if hasattr(mss, "MSS") else mss.mss()

    def grab(self, box):
        """box = (left, top, width, height) in screen pixels. Returns HxWx3 RGB."""
        import numpy as np
        x, y, w, h = box
        shot = self._mss.grab({"left": x, "top": y, "width": w, "height": h})
        return np.frombuffer(shot.rgb, dtype=np.uint8).reshape(shot.height, shot.width, 3)


class ImageScreen:
    """Stands in for Screen when testing on still frames / video."""

    def __init__(self, arr):
        self.arr = arr

    def grab(self, box):
        x, y, w, h = box
        return self.arr[y:y + h, x:x + w]


# --------------------------------------------------------------------------
# death detector
# --------------------------------------------------------------------------
def to_gray(arr):
    import numpy as np
    return (0.299 * arr[..., 0] + 0.587 * arr[..., 1] + 0.114 * arr[..., 2]).astype(np.float32)


def bright(g, lo: float = 130.0, span: float = 90.0):
    """Keep only near-white pixels (HUD text), 0..1, so the coloured
    background behind the prompt (abilities, smokes, flashes) doesn't matter."""
    import numpy as np
    return np.clip((g - lo) / span, 0.0, 1.0)


def correlation(a, b) -> float:
    import numpy as np
    a = a - a.mean()
    b = b - b.mean()
    den = float(np.sqrt((a * a).sum() * (b * b).sum()))
    return float((a * b).sum() / den) if den else 0.0


class _Prepared:
    """FFT and integral images of one search image, shared by all templates."""

    def __init__(self, img, fshape):
        import numpy as np
        self.img = img
        self.fshape = fshape
        self.F = np.fft.rfft2(img, fshape)
        self.ii = np.pad(img, ((1, 0), (1, 0))).cumsum(0).cumsum(1)
        self.i2 = np.pad(img * img, ((1, 0), (1, 0))).cumsum(0).cumsum(1)


_TPL_FFT: dict = {}


def ncc_map(img, tpl, prep: "_Prepared | None" = None):
    """Normalised cross-correlation of tpl at every valid offset in img
    (same result as correlation() per window), via FFT + integral images."""
    import numpy as np
    H, W = img.shape
    h, w = tpl.shape
    if H < h or W < w:
        return np.full((1, 1), -1.0, np.float32)
    t0 = tpl - tpl.mean()
    tn = float(np.sqrt((t0 * t0).sum()))
    if tn == 0:
        return np.zeros((H - h + 1, W - w + 1), np.float32)
    fshape = prep.fshape if prep is not None else (H + h - 1, W + w - 1)
    key = (id(tpl), fshape)
    Ft = _TPL_FFT.get(key)
    if Ft is None:
        Ft = np.fft.rfft2(t0[::-1, ::-1], fshape)
        if prep is not None:
            _TPL_FFT[key] = Ft
    Fi = prep.F if prep is not None else np.fft.rfft2(img, fshape)
    num = np.fft.irfft2(Fi * Ft, fshape)[h - 1:H, w - 1:W]
    if prep is not None:
        ii, i2 = prep.ii, prep.i2
    else:
        ii = np.pad(img, ((1, 0), (1, 0))).cumsum(0).cumsum(1)
        i2 = np.pad(img * img, ((1, 0), (1, 0))).cumsum(0).cumsum(1)
    s = ii[h:, w:] - ii[:-h, w:] - ii[h:, :-w] + ii[:-h, :-w]
    s2 = i2[h:, w:] - i2[:-h, w:] - i2[h:, :-w] + i2[:-h, :-w]
    var = np.maximum(s2 - s * s / (h * w), 0.0)
    den = np.sqrt(var) * tn
    out = np.where(den > 1e-6, num / np.maximum(den, 1e-6), 0.0)
    return out.astype(np.float32)


def _fast_len(n: int) -> int:
    """Smallest 2^a 3^b 5^c >= n (FFT-friendly size)."""
    best = 1 << max(0, (n - 1).bit_length())
    p5 = 1
    while p5 < best:
        p35 = p5
        while p35 < best:
            p = p35
            while p < n:
                p *= 2
            best = min(best, p)
            p35 *= 3
        p5 *= 5
    return best


def viewport(rect):
    """The 16:9 picture VALORANT draws inside its window. Super-ultrawide
    (e.g. Samsung G9 5120x1440) and ultrawide get black bars left/right;
    taller-than-16:9 windows get bars top/bottom."""
    left, top, W, H = rect
    if W * 9 > H * 16 + 9:            # wider than 16:9 -> pillarbox
        vw = int(round(H * 16 / 9))
        return (left + (W - vw) // 2, top, vw, H)
    if W * 9 < H * 16 - 16:           # taller than 16:9 -> letterbox
        vh = int(round(W * 9 / 16))
        return (left, top + (H - vh) // 2, W, vh)
    return rect


class DeathDetector:
    """Dead when a "SWITCH PLAYER" template matches near its spot.

    Where to look = a transform (ox, oy, k): screen = window origin + (ox, oy)
    + template_ref_xy * k. It comes from a saved calibration for this window
    size if there is one, else from the 16:9 viewport geometry. Until a death
    has been seen once, wide_search() also scans the whole bottom-left of the
    window at a few scales; a consistent hit is saved as the calibration."""

    def __init__(self, cfg: dict, screen):
        from PIL import Image
        import numpy as np
        self.cfg = cfg
        self.screen = screen
        self.threshold = float(cfg["death_threshold"])
        self.wide_threshold = float(cfg.get("calibrate_threshold", 0.80))
        self.confirm = int(cfg["death_confirm_frames"])
        self.revive = int(cfg["revive_frames"])
        self.pad = int(cfg["search_pad"])
        self.templates = []
        for t in cfg.get("templates", []):
            p = resolve(t["file"])
            if not p.exists():
                log(f"WARN template missing: {t['file']}")
                continue
            img = np.array(Image.open(p).convert("RGB"))
            self.templates.append({"name": Path(t["file"]).stem, "x": int(t["x"]), "y": int(t["y"]),
                                   "ref": float(t.get("ref_height", 1080)),
                                   "g": bright(to_gray(img)), "w": img.shape[1], "h": img.shape[0]})
        self.calibration = dict(cfg.get("calibration", {}))   # "WxH" -> {ox, oy, k}
        self.hits = 0
        self.misses = 0
        self._wide_last = None        # (ox, oy, k) of the previous wide hit
        self._wide_count = 0
        if not self.templates:
            log("WARN no death templates - death detection is OFF")

    @property
    def enabled(self) -> bool:
        return bool(self.templates)

    # -- where to look ------------------------------------------------------
    @staticmethod
    def key(rect) -> str:
        return f"{rect[2]}x{rect[3]}"

    def calibrated(self, rect) -> bool:
        return self.key(rect) in self.calibration

    def transform(self, rect, ref: float = 1080.0):
        """(ox, oy, k) relative to the window's client origin."""
        c = self.calibration.get(self.key(rect))
        if c:
            return float(c["ox"]), float(c["oy"]), float(c["k"])
        vx, vy, vw, vh = viewport(rect)
        return float(vx - rect[0]), float(vy - rect[1]), vh / ref

    def save_calibration(self, rect, tr, why: str):
        ox, oy, k = tr
        key = self.key(rect)
        self.calibration[key] = {"ox": round(float(ox), 1), "oy": round(float(oy), 1), "k": round(float(k), 4)}
        try:
            raw = json.loads(CONFIG_PATH.read_text(encoding="utf-8")) if CONFIG_PATH.exists() else {}
            raw.setdefault("calibration", {})[key] = self.calibration[key]
            CONFIG_PATH.write_text(json.dumps(raw, indent=2), encoding="utf-8")
        except OSError as e:
            log(f"WARN could not save calibration: {e!r}")
        log(f"CALIBRATED for {key}: offset {ox:.0f},{oy:.0f} scale {k:.3f} ({why}) - saved to config.json")

    # -- fast check at the known spot ----------------------------------------
    def _box(self, t, rect, tr):
        ox, oy, k = tr
        pw, ph = t["w"] + 2 * self.pad, t["h"] + 2 * self.pad
        return (rect[0] + int(round(ox + (t["x"] - self.pad) * k)),
                rect[1] + int(round(oy + (t["y"] - self.pad) * k)),
                max(1, int(round(pw * k))), max(1, int(round(ph * k)))), (pw, ph)

    def _prep(self, g, size):
        from PIL import Image
        import numpy as np
        pw, ph = size
        if g.shape != (ph, pw):
            g = np.asarray(Image.fromarray(g, mode="F").resize((pw, ph), Image.BOX), dtype=np.float32)
        return bright(g)

    def _patch(self, t, rect, tr):
        box, size = self._box(t, rect, tr)
        return self._prep(to_gray(self.screen.grab(box)), size)

    def score(self, rect) -> tuple[float, str]:
        """One screenshot covering every template's spot, then match each."""
        import numpy as np
        jobs = []
        for t in self.templates:
            box, size = self._box(t, rect, self.transform(rect, t["ref"]))
            jobs.append((t, box, size))
        if not jobs:
            return -1.0, ""
        x0 = min(b[0] for _, b, _ in jobs)
        y0 = min(b[1] for _, b, _ in jobs)
        x1 = max(b[0] + b[2] for _, b, _ in jobs)
        y1 = max(b[1] + b[3] for _, b, _ in jobs)
        whole = to_gray(self.screen.grab((x0, y0, x1 - x0, y1 - y0)))
        best, name = -1.0, ""
        for t, (bx, by, bw, bh), size in jobs:
            g = np.ascontiguousarray(whole[by - y0:by - y0 + bh, bx - x0:bx - x0 + bw])
            s = float(ncc_map(self._prep(g, size), t["g"]).max())
            if s > best:
                best, name = s, t["name"]
        return best, name

    # -- wide search (calibration) -------------------------------------------
    def wide_search(self, rect):
        """Scan the bottom-left of the whole window at a few scales.
        Returns (score, (ox, oy, k), template name) of the best hit."""
        from PIL import Image
        import numpy as np
        left, top, W, H = rect
        # the prompt sits ~79 % down and ~6 % across the 16:9 picture; on a
        # 32:9 window that picture starts 25 % in, so 40 % x 40 % covers all cases
        gx, gy = 0, int(H * 0.58)
        gw, gh = int(W * 0.40), int(H * 0.40)
        raw = to_gray(self.screen.grab((left + gx, top + gy, gw, gh)))
        base_k = viewport(rect)[3] / 1080.0
        ks = sorted({round(base_k * f, 4) for f in (0.88, 1.0, 1.12)} | {round(H / 1080.0, 4)})
        mh = max(t["h"] for t in self.templates)
        mw = max(t["w"] for t in self.templates)
        best = (-1.0, None, "")
        for k in ks:
            sw, sh = max(1, int(round(gw / k))), max(1, int(round(gh / k)))
            img = raw if k == 1.0 else np.asarray(
                Image.fromarray(raw, mode="F").resize((sw, sh), Image.BOX), dtype=np.float32)
            img = bright(img)
            fshape = (_fast_len(sh + mh - 1), _fast_len(sw + mw - 1))
            prep = _Prepared(img, fshape)
            for t in self.templates:
                m = ncc_map(img, t["g"], prep)
                iy, ix = np.unravel_index(int(m.argmax()), m.shape)
                s = float(m[iy, ix])
                if s > best[0]:
                    # template top-left in window coords -> transform origin
                    px, py = gx + ix * k, gy + iy * k
                    best = (s, (px - t["x"] * k, py - t["y"] * k, k), t["name"])
        return best

    def wide_step(self, rect) -> bool:
        """One calibration attempt. True once a hit is confirmed and saved."""
        s, tr, name = self.wide_search(rect)
        if s < self.wide_threshold or tr is None:
            self._wide_count = 0
            self._wide_last = None
            return False
        same = (self._wide_last is not None
                and abs(tr[0] - self._wide_last[0]) <= 4 * tr[2]
                and abs(tr[1] - self._wide_last[1]) <= 4 * tr[2]
                and abs(tr[2] - self._wide_last[2]) < 0.05)
        self._wide_count = self._wide_count + 1 if same else 1
        self._wide_last = tr
        if self._wide_count >= 2:
            self.save_calibration(rect, tr, f"found '{name}' score {s:.2f} twice")
            self._wide_count = 0
            return True
        return False

    # -- state ------------------------------------------------------------------
    def reset(self):
        self.hits = 0
        self.misses = 0

    def check(self, dead: bool, rect) -> bool:
        """New dead/alive state: `confirm` frames with the marker to go dead,
        `revive` frames without it to come back alive."""
        s, name = self.score(rect)
        if s >= self.threshold:
            self.misses = 0
            self.hits += 1
            if not dead and self.hits >= self.confirm:
                log(f"DEAD  marker '{name}' score {s:.2f}")
                if not self.calibrated(rect):
                    self.save_calibration(rect, self.transform(rect), "seen at the expected spot")
                return True
        else:
            self.hits = 0
            if dead:
                self.misses += 1
                if self.misses >= self.revive:
                    log(f"ALIVE again (marker gone, best {s:.2f})")
                    self.misses = 0
                    return False
        return dead


class Diagnostics:
    """Until the death prompt has been found once for this window size, keep a
    few small snapshots of the bottom-left of the game window (local only, in
    diag/) so a failing setup can be diagnosed from one match. Also warns when
    the screen capture comes back black."""

    def __init__(self, cfg: dict):
        self.every = float(cfg.get("diagnostics_interval", 8.0))
        self.keep = int(cfg.get("diagnostics_keep", 40))
        self.out = APP_DIR / "diag"
        self.last = 0.0
        self.announced = False
        self.black = 0
        self.black_warned = False

    def tick(self, t: float, rect, screen):
        from PIL import Image
        import numpy as np
        if t - self.last < self.every:
            return
        self.last = t
        left, top, W, H = rect
        box = (left, top + int(H * 0.45), int(W * 0.55), H - int(H * 0.45))
        try:
            arr = screen.grab(box)
        except Exception as e:  # noqa: BLE001
            log(f"WARN screen capture failed: {e!r}")
            return
        mean = float(np.asarray(arr, dtype=np.float32).mean())
        if mean < 3.0:
            self.black += 1
            if self.black >= 3 and not self.black_warned:
                self.black_warned = True
                log("WARN the screenshot of the VALORANT window is BLACK. Windows isn't letting "
                    "valspot see the game picture on this PC, so death detection can't work. "
                    "Send valspot.log and the diag folder to whoever gave you this.")
        else:
            self.black = 0
        try:
            self.out.mkdir(exist_ok=True)
            img = Image.fromarray(np.ascontiguousarray(arr))
            if img.width > 1280:
                img = img.resize((1280, max(1, img.height * 1280 // img.width)), Image.BILINEAR)
            name = f"{datetime.now().strftime('%Y%m%d-%H%M%S')}_{W}x{H}_mean{mean:.0f}.jpg"
            img.save(self.out / name, quality=80)
            files = sorted(self.out.glob("*.jpg"))
            for old in files[:-self.keep]:
                old.unlink()
            if not self.announced:
                self.announced = True
                log(f"diagnostics: saving a snapshot of the bottom-left every {self.every:.0f}s "
                    f"to {self.out} until the death prompt is found once (max {self.keep}, local only)")
        except OSError as e:
            log(f"WARN could not save diagnostic snapshot: {e!r}")


# --------------------------------------------------------------------------
# the rules
# --------------------------------------------------------------------------
class Controller:
    def __init__(self, cfg: dict, spotify: Spotify, detector: DeathDetector | None):
        self.cfg = cfg
        self.sp = spotify
        self.det = detector
        self.phase = "menu"            # menu | pregame | buy | combat | round_end
        self.dead = False
        self.last_round: int | None = None
        self.pause_at: float | None = None
        self.in_match = False
        self.mode = ""
        self.rounds_this_match = 0
        self.window = None             # callable -> rect or None (not focused)
        self.window_name = lambda: ""
        self._had_window = True
        self._last_res = None
        self._last_rect = None
        self._wide_at = 0.0
        self.diag = Diagnostics(cfg) if cfg.get("diagnostics", True) else None

    def handle(self, ev: Event, t: float):
        k = ev.kind
        if k == "mode":
            self.mode = str(ev.value)
        elif k == "match_start":
            self.in_match = True
            self.last_round = None
            self.rounds_this_match = 0
            self.dead = False
            self.phase = "pregame"
            log(f"MATCH start (mode={self.mode or '?'})")
            if self.mode == "Bomb":
                # round 1 has no buy line in the log; its 45 s buy phase
                # starts at load-in (measured 44.8-45.3 s over 4 matches)
                self._enter_buy(t)
        elif k == "pregame":
            self.phase = "pregame"
        elif k == "buy":
            self._enter_buy(t)
        elif k == "combat":
            self.phase = "combat"
            self.pause_at = None
            self.dead = False
            if self.det:
                self.det.reset()
            log("ROUND live (barriers down)")
            self.sp.pause("round is live")
        elif k == "round_end":
            self.phase = "round_end"
            self.last_round = int(ev.value)
            self.rounds_this_match += 1
            self.pause_at = None
            log(f"ROUND {ev.value} ended (you were {'dead' if self.dead else 'alive'})")
        elif k == "match_end":
            if self.in_match or self.phase != "menu":
                log(f"MATCH end ({self.rounds_this_match} rounds)")
                if self.det and self._last_rect and not self.det.calibrated(self._last_rect):
                    log(f"NOTE death prompt not seen yet at {self.det.key(self._last_rect)}; "
                        "still calibrating (needs one death where you spectate a teammate)")
                self.sp.pause("match ended")
            self.in_match = False
            self.phase = "menu"
            self.pause_at = None
            self.dead = False

    def _enter_buy(self, t: float):
        self.phase = "buy"
        self.dead = False
        if self.det:
            self.det.reset()
        first = self.last_round is None or self.last_round == int(self.cfg["half_time_round"])
        length = float(self.cfg["first_buy_seconds"] if first else self.cfg["buy_seconds"])
        lead = float(self.cfg["pause_lead_seconds"])
        self.pause_at = t + max(0.0, length - lead)
        log(f"BUY   phase start (expect {length:.0f}s, pausing at T+{length - lead:.0f}s)")
        self.sp.play("buy phase")

    def tick(self, t: float):
        if self.pause_at is not None and t >= self.pause_at:
            self.pause_at = None
            log("BUY   phase nearly over")
            self.sp.pause("round about to start")
        if self.phase != "combat" or not (self.det and self.det.enabled):
            return
        rect = self.window() if self.window else None
        if rect is None:
            # tabbed out: the screen shows something else, so hold the state
            if self._had_window:
                self._had_window = False
                self.det.reset()
                log(f"tabbed out ({self.window_name() or 'other window'}), holding state: "
                    f"{'dead' if self.dead else 'alive'}")
            return
        if not self._had_window:
            self._had_window = True
            self.det.reset()
            log("VALORANT focused again, watching screen")
        if rect[2:] != self._last_res:
            self._last_res = rect[2:]
            vp = viewport(rect)
            ox, oy, k = self.det.transform(rect)
            log(f"game window {rect[2]}x{rect[3]} at {rect[0]},{rect[1]}; 16:9 picture {vp[2]}x{vp[3]}; "
                f"{'calibrated' if self.det.calibrated(rect) else 'not calibrated yet'} "
                f"(offset {ox:.0f},{oy:.0f} scale {k:.3f})")
        self._last_rect = rect
        was = self.dead
        self.dead = self.det.check(self.dead, rect)
        # one-time calibration: until a death has been seen for this window
        # size, also scan the whole bottom-left about once a second
        if (not self.dead and not self.det.calibrated(rect)
                and t - self._wide_at >= float(self.cfg.get("calibrate_interval", 2.0))):
            self._wide_at = t
            if self.det.wide_step(rect):
                self.dead = True
        if self.diag and not self.det.calibrated(rect):
            self.diag.tick(t, rect, self.det.screen)
        if self.dead and not was:
            self.sp.play("you died")
        elif was and not self.dead:
            self.sp.pause("revived")


# --------------------------------------------------------------------------
# modes
# --------------------------------------------------------------------------
def mode_run(cfg: dict, args):
    print(f"valspot {VERSION} - Spotify plays while you're dead or in the buy phase.")
    print("Keep this window open while you play. Close it to stop.\n")
    sp = Spotify(dry_run=cfg["dry_run"] or args.dry_run)
    det = None
    try:
        det = DeathDetector(cfg, Screen())
    except Exception as e:  # noqa: BLE001
        log(f"WARN screen capture unavailable ({e!r}); death detection OFF")
    ctl = Controller(cfg, sp, det)
    gw = GameWindow()
    if cfg.get("only_when_focused", True):
        ctl.window = gw.focused_rect
        ctl.window_name = lambda: gw.last_exe
    else:
        ctl.window = gw.find_rect
    import platform
    log(f"valspot {VERSION} on Windows {platform.version()}; config {CONFIG_PATH}")
    if not Path(cfg["log_path"]).exists():
        log(f"NOTE VALORANT log not found yet at {cfg['log_path']} (fine if the game isn't running)")
    q: queue.Queue = queue.Queue()
    LogTailer(cfg["log_path"], q).start()
    st = sp.status()
    log(f"ready. Spotify: {'not open' if st is None else ('playing' if st == 4 else 'open')}; "
        f"death templates: {len(det.templates) if det else 0}")
    if st is None:
        log("NOTE open Spotify, or nothing will play")
    period = 1.0 / float(cfg["poll_hz"])
    while True:
        t = time.time()
        try:
            while True:
                ctl.handle(q.get_nowait(), t)
        except queue.Empty:
            pass
        ctl.tick(t)
        time.sleep(period)


def mode_check(cfg: dict, args):
    """Live diagnostics: where it looks, the marker score, and a wide search."""
    det = DeathDetector(cfg, Screen())
    gw = GameWindow()
    if not det.enabled:
        return
    print("Once a second: marker score at the expected spot, plus a wide search.")
    print("While you're dead and spectating, either should read 0.8+.")
    print()
    while True:
        rect = gw.find_rect()
        if rect is None:
            print(f"{now_str()} VALORANT window not found", flush=True)
        else:
            ox, oy, k = det.transform(rect)
            import numpy as np
            mean = float(np.asarray(det.screen.grab((rect[0], rect[1] + rect[3] // 2, rect[2] // 2, rect[3] // 2)),
                                    dtype=np.float32).mean())
            s, name = det.score(rect)
            ws, wtr, wname = det.wide_search(rect)
            where = f"{wtr[0]:.0f},{wtr[1]:.0f} x{wtr[2]:.2f}" if wtr else "-"
            print(f"{now_str()} window {rect[2]}x{rect[3]} "
                  f"{'calibrated' if det.calibrated(rect) else 'uncalibrated'} "
                  f"brightness={mean:.0f}{' (BLACK - capture blocked?)' if mean < 3 else ''} "
                  f"spot={s:+.2f} wide={ws:+.2f} ({where}) "
                  f"{'DEAD' if max(s, ws) >= det.threshold else ''}", flush=True)
        time.sleep(1)


def mode_shot(cfg: dict, args):
    """Save a screenshot of the VALORANT window 5 s from now (to send for help)."""
    from PIL import Image
    gw = GameWindow()
    print("Switch to VALORANT now (die and spectate a teammate). Capturing in 5 s...")
    time.sleep(5)
    rect = gw.find_rect()
    if rect is None:
        print("VALORANT window not found.")
        return
    out = APP_DIR / f"valspot-shot-{datetime.now().strftime('%H%M%S')}.png"
    Image.fromarray(Screen().grab(rect)).save(out)
    print(f"Saved {out.name} ({rect[2]}x{rect[3]}).")


def mode_spotify(cfg: dict, args):
    sp = Spotify()
    log(f"status={sp.status()} now={sp.now_playing()}")
    sp.play("test")
    time.sleep(2)
    sp.pause("test")
    time.sleep(1)
    log(f"status={sp.status()}")


def mode_template(cfg: dict, args):
    """Cut X Y W H out of FRAME; coordinates are stored relative to its height."""
    from PIL import Image
    x, y, w, h = [int(v) for v in args.box]
    img = Image.open(args.target).convert("RGB")
    rel = f"templates/{args.name}.png"
    (APP_DIR / "templates").mkdir(exist_ok=True)
    img.crop((x, y, x + w, y + h)).save(APP_DIR / rel)
    raw = json.loads(CONFIG_PATH.read_text(encoding="utf-8")) if CONFIG_PATH.exists() else dict(DEFAULT_CONFIG)
    raw["templates"] = [t for t in raw.get("templates", []) if t["file"] != rel]
    raw["templates"].append({"file": rel, "x": x, "y": y, "ref_height": img.height})
    CONFIG_PATH.write_text(json.dumps(raw, indent=2), encoding="utf-8")
    log(f"template {rel} saved ({w}x{h} at {x},{y} in a {img.height}p frame)")


def mode_eval(cfg: dict, args):
    """Run the real detector + calibration over video clips.
      --height H        content (16:9 picture) height, e.g. 1440
      --canvas WxH      window size; content centered, black bars around it
                        (Samsung G9: --height 1440 --canvas 5120x1440)
      --naive           pretend the 16:9 geometry is unknown, so only the
                        wide-search calibration can find the prompt"""
    import subprocess
    import numpy as np
    global CONFIG_PATH, viewport
    CONFIG_PATH = APP_DIR / "_eval_config.json"          # never touch the real config
    if args.naive:
        viewport = lambda rect: rect                      # noqa: E731
    th = float(cfg["death_threshold"])
    ch = int(args.height)
    cw = ch * 16 // 9
    W, H = (int(v) for v in args.canvas.split("x")) if args.canvas else (cw, ch)
    rect = (0, 0, W, H)
    try:
        for clip in args.clips:
            cfg2 = dict(cfg)
            cfg2["calibration"] = {}
            det = DeathDetector(cfg2, None)
            vf = f"fps=2,scale={cw}:{ch}:flags=bicubic,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:black"
            cmd = [args.ffmpeg, "-hide_banner", "-loglevel", "error", "-threads", "1", "-i", clip,
                   "-vf", vf, "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]
            proc = subprocess.Popen(cmd, stdout=subprocess.PIPE)
            rows, i, cal_at, wide_ms = [], 0, None, []
            while True:
                buf = proc.stdout.read(W * H * 3)
                if len(buf) < W * H * 3:
                    break
                det.screen = ImageScreen(np.frombuffer(buf, np.uint8).reshape(H, W, 3))
                s = det.score(rect)[0]
                wide = None
                if not det.calibrated(rect):
                    t0 = time.perf_counter()
                    ok = det.wide_step(rect)
                    wide_ms.append((time.perf_counter() - t0) * 1000)
                    wide = det._wide_last
                    if ok:
                        cal_at = i / 2
                        s = det.score(rect)[0]
                rows.append((i / 2, s, wide))
                i += 1
            proc.wait()
            dead = [t for t, sc, _ in rows if sc >= th]
            print(f"{Path(clip).name} window {W}x{H} picture {cw}x{ch}{' naive' if args.naive else ''}: "
                  f"frames={len(rows)} dead={len(dead)} "
                  f"dead-span={f'{dead[0]}-{dead[-1]}s' if dead else '-'} "
                  f"max-not-dead={max([sc for _, sc, _ in rows if sc < th] or [0]):+.2f} "
                  f"calibrated-at={cal_at if cal_at is not None else '-'}s "
                  f"saved={det.calibration.get(det.key(rect))} "
                  f"wide-search={np.mean(wide_ms):.0f}ms/frame" if wide_ms else "")
            if args.verbose:
                print("  " + " ".join(f"{t:g}:{sc:.2f}" for t, sc, _ in rows))
    finally:
        try:
            CONFIG_PATH.unlink()
        except OSError:
            pass


def mode_replay(cfg: dict, args):
    sp = Spotify(dry_run=True)
    ctl = Controller(cfg, sp, None)
    events = []
    with open(args.target, encoding="utf-8", errors="replace") as f:
        for line in f:
            ev = parse_line(line)
            if ev:
                ev.stamp = line_stamp(line)
                events.append(ev)
    log(f"replay: {len(events)} events")
    for ev in events:
        ctl.tick(ev.stamp)
        ctl.handle(ev, ev.stamp)


def main():
    ap = argparse.ArgumentParser(prog="valspot")
    ap.add_argument("mode", nargs="?", default="run",
                    choices=["run", "check", "shot", "spotify", "replay", "eval", "template"])
    ap.add_argument("target", nargs="?", help="replay: log file / template: frame image")
    ap.add_argument("box", nargs="*", help="template: X Y W H")
    ap.add_argument("--name", default="marker")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--clips", nargs="*", default=[])
    ap.add_argument("--ffmpeg", default="ffmpeg")
    ap.add_argument("--height", default=1080)
    ap.add_argument("--canvas", default="")
    ap.add_argument("--naive", action="store_true")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()
    cfg = load_config()
    {"run": mode_run, "check": mode_check, "shot": mode_shot, "spotify": mode_spotify, "replay": mode_replay,
     "eval": mode_eval, "template": mode_template}[args.mode](cfg, args)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
    except Exception as e:  # noqa: BLE001
        import traceback
        traceback.print_exc()
        log(f"CRASH {e!r}")
        if FROZEN:
            input("\nvalspot crashed (details above and in valspot.log). Press Enter to close.")
