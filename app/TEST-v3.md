# ValSpot 3 (C#) test sheet - 2026-10-18 (updated for 3.0.1)

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
| Match end, back in the lobby | **fades in** (setting: Play when you're back in the lobby) | grey |

## 3. Things to try on purpose

- **Alt-tab to Chrome while dead** for 10+ s: music keeps playing.
- **Spectate for 20+ s and switch players**: music never cuts out.
- **Right-click tray > Pause ValSpot**: dot turns amber, ValSpot stops touching your music. Resume after.
- **Settings > Stop before the round starts**: move to 5 s; the next buy phase should fade out 5 s early.
- **Settings > Fade in and out** off: the next change is an instant play/pause.
- **Learn my death screen** (optional): while dead and spectating, click Capture, switch to VALORANT within 5 s. Expect "Found it" with a green box on the SWITCH PLAYER line.

## 3.0.1 additions

- **Swiftplay:** the round-5 buy phase (after the side swap) is 45 s; music should stop ~3 s before it ends, not 18 s early.
- **Start ValSpot mid-match:** quit it during a buy phase, start it again. Log says `caught up on the match so far: in a ... match, round N, buy phase` and music plays.
- **Shooting Range:** opening it no longer plays music for 27 s.
- **Learn my death screen:** says your screen is already set up (preset size). Only needed for non-English clients.

## 3.0.3 additions

- **Overtime:** round 25 (the first overtime round) is a 45 s buy phase; music should stop a few seconds before it ends, not 20 s early. Later overtime rounds are 30 s.
- **Round 13 after an overtime match:** still 45 s. (3.0.2 could mislearn round 24 as the side swap and then cut round 13 short.)
- **No more "learned:" lines** in valspot.log for standard matches or Swiftplay.

## 4. Check your volume afterwards

- Open the Windows volume mixer: Spotify should be back at the level it was before (fades always restore it).

## If something's wrong

Right-click tray > **Open log folder** and send `valspot.log` (and `diag\` if death music never worked).

## Known open before this test (3.0.1)

- Buy timing is confirmed live (your Swiftplay match). Death detection on a live match is not confirmed yet: it passed on your Medal clips at 1080p, 1440p, 4K, simulated G9 and 16:10.
- Lobby music and the mid-match catch-up were checked by replaying your log, not live.
- The update check ignores pre-releases, so 3.0.0 won't tell you 3.0.1 exists. Swap the exe by hand.
- Your hand-drawn marker from today is weak (scores ~0.80 when dead, barely at the bar). It no longer fires while alive. Settings > Forget what it learned removes it; the built-in sizes cover your 4K screen.
- Fades were checked on Spotify only.
