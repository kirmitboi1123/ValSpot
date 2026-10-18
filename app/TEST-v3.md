# ValSpot 3 (C#) test sheet - 2026-10-18

Built and checked offline. This is the first test on your real setup. Takes one match.

The file to run: `val-spotify\app\publish\ValSpot.exe` (or unzip `app\ValSpot-share.zip`).
Close any older ValSpot first (the Python one in the tray/console, `dist*`, or `ValSpot\valspot.exe`).

## 1. First launch (2 minutes, no game needed)

| Do | Expect |
|---|---|
| Double-click `ValSpot.exe` | "Windows protected your PC" may appear: More info > Run anyway |
| | The settings window opens (dark, ValSpot logo, status "Waiting for VALORANT") |
| | A ValSpot icon appears in the tray with a **grey** dot, plus a "ValSpot is running" pop-up |
| Look at "Music app" | "Now: Spotify (playing)" or "(paused)" |
| Close the settings window | ValSpot keeps running in the tray |
| Left-click the tray icon | Settings open again |
| Double-click `ValSpot.exe` again | No second copy; the settings window comes to the front |

## 2. One match

| Moment | Expect (music) | Tray dot |
|---|---|---|
| VALORANT open, in menus | silent | grey |
| Load-in / round 1 buy | **fades in** over ~0.8 s | green |
| 3 s before barriers | **fades out**, then pauses | blue |
| Round live, alive | silent | blue |
| You die | **fades in** about 1 s after "SWITCH PLAYER" shows | green |
| First death only | pop-up "ValSpot calibrated" | |
| Next buy phase | keeps playing | green |
| Match end | fades out | grey |

## 3. Things to try on purpose

- **Alt-tab to Chrome while dead** for 10+ s: music keeps playing.
- **Spectate for 20+ s and switch players**: music never cuts out.
- **Right-click tray > Pause ValSpot**: dot turns amber, ValSpot stops touching your music. Resume after.
- **Settings > Stop before the round starts**: move to 5 s; the next buy phase should fade out 5 s early.
- **Settings > Fade in and out** off: the next change is an instant play/pause.
- **Learn my death screen** (optional): while dead and spectating, click Capture, switch to VALORANT within 5 s. Expect "Found it" with a green box on the SWITCH PLAYER line.

## 4. Check your volume afterwards

- Open the Windows volume mixer: Spotify should be back at the level it was before (fades always restore it).

## If something's wrong

Right-click tray > **Open log folder** and send `valspot.log` (and `diag\` if death music never worked).

## Known open before this test

- Never run against a live match yet: death detection is verified on your Medal clips, at 1080p, 1440p, 4K, simulated G9 and 16:10.
- The settings and learn windows were checked as off-screen renders, not clicked through on your screen.
- "Start with Windows" and the update check were not exercised (no release with v3 exists yet).
- Fades were checked on Spotify only (volume 1.00 -> 0.60 -> 1.00).
