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

VERSION = "2.0"

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


class DeathDetector:
    """Dead when any "SWITCH PLAYER" template matches near its spot."""

    def __init__(self, cfg: dict, screen):
        from PIL import Image
        import numpy as np
        self.screen = screen
        self.threshold = float(cfg["death_threshold"])
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
        self.hits = 0
        self.misses = 0
        if not self.templates:
            log("WARN no death templates - death detection is OFF")

    @property
    def enabled(self) -> bool:
        return bool(self.templates)

    def _patch(self, t, rect):
        """Template-scale brightness patch around the template spot."""
        from PIL import Image
        import numpy as np
        left, top, _, height = rect
        k = height / t["ref"]
        pw, ph = t["w"] + 2 * self.pad, t["h"] + 2 * self.pad
        box = (left + int(round((t["x"] - self.pad) * k)), top + int(round((t["y"] - self.pad) * k)),
               max(1, int(round(pw * k))), max(1, int(round(ph * k))))
        g = to_gray(self.screen.grab(box))
        if g.shape != (ph, pw):
            g = np.asarray(Image.fromarray(g, mode="F").resize((pw, ph), Image.BOX), dtype=np.float32)
        return bright(g)

    def score(self, rect) -> tuple[float, str]:
        best, name = -1.0, ""
        for t in self.templates:
            g = self._patch(t, rect)
            th, tw = t["h"], t["w"]
            for dy in range(g.shape[0] - th + 1):
                for dx in range(g.shape[1] - tw + 1):
                    c = correlation(g[dy:dy + th, dx:dx + tw], t["g"])
                    if c > best:
                        best, name = c, t["name"]
        return best, name

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
            log(f"game window {rect[2]}x{rect[3]} at {rect[0]},{rect[1]}")
        was = self.dead
        self.dead = self.det.check(self.dead, rect)
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
    det = DeathDetector(cfg, Screen())
    gw = GameWindow()
    if not det.enabled:
        return
    print("Printing the death-marker score once a second. Dead should read 0.8+.")
    while True:
        rect = gw.find_rect()
        if rect is None:
            print(f"{now_str()} VALORANT window not found", flush=True)
        else:
            s, name = det.score(rect)
            print(f"{now_str()} {rect[2]}x{rect[3]} score={s:+.3f} ({name}) "
                  f"{'DEAD' if s >= det.threshold else ''}", flush=True)
        time.sleep(1)


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
    """Run the real detector over video clips, optionally resized to another
    resolution first (simulates a different monitor)."""
    import subprocess
    import numpy as np
    th = float(cfg["death_threshold"])
    det = DeathDetector(cfg, None)
    H = int(args.height)
    W = H * 16 // 9
    rect = (0, 0, W, H)
    for clip in args.clips:
        cmd = [args.ffmpeg, "-hide_banner", "-loglevel", "error", "-threads", "1", "-i", clip,
               "-vf", f"fps=2,scale={W}:{H}:flags=bicubic", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]
        proc = subprocess.Popen(cmd, stdout=subprocess.PIPE)
        scores, i = [], 0
        while True:
            buf = proc.stdout.read(W * H * 3)
            if len(buf) < W * H * 3:
                break
            det.screen = ImageScreen(np.frombuffer(buf, np.uint8).reshape(H, W, 3))
            scores.append((i / 2, det.score(rect)[0]))
            i += 1
        proc.wait()
        dead = [t for t, s in scores if s >= th]
        print(f"{Path(clip).name} @{W}x{H}: frames={len(scores)} dead={len(dead)} "
              f"max={max(s for _, s in scores):+.3f} "
              f"max-alive={max([s for _, s in scores if s < th] or [0]):+.3f} "
              f"dead-span={f'{dead[0]}-{dead[-1]}s' if dead else '-'}")
        if args.verbose:
            print("  " + " ".join(f"{t:g}:{s:.2f}" for t, s in scores))


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
                    choices=["run", "check", "spotify", "replay", "eval", "template"])
    ap.add_argument("target", nargs="?", help="replay: log file / template: frame image")
    ap.add_argument("box", nargs="*", help="template: X Y W H")
    ap.add_argument("--name", default="marker")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--clips", nargs="*", default=[])
    ap.add_argument("--ffmpeg", default="ffmpeg")
    ap.add_argument("--height", default=1080)
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()
    cfg = load_config()
    {"run": mode_run, "check": mode_check, "spotify": mode_spotify, "replay": mode_replay,
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
