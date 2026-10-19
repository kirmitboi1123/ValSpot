# ValSpot

Your music plays while you're **dead**, in the **buy phase**, or **back in the lobby** in VALORANT,
and fades out 3 seconds before the barriers drop. A small Windows tray app; nothing to set up.

**Download:** `ValSpot-share.zip` from the [latest release](https://github.com/kirmitboi1123/ValSpot/releases/latest).
Unzip it, run `ValSpot.exe`, play. Windows 10 or 11, 64-bit.

## What it does

| Moment | Music |
|---|---|
| Buy phase starts | fades in |
| A few seconds before the barriers drop (you pick how many) | fades out |
| You die and spectate a teammate | fades in, until the next round goes live |
| Round live and you're alive | off |
| Match over, back in the lobby | fades in (can be turned off) |
| Shooting Range | left alone |

Works with Spotify, or whatever is playing: YouTube Music, Apple Music, Tidal, a browser tab.
No Spotify Premium or login needed.

## Using it

1. Put `ValSpot.exe` somewhere it can stay, like Documents.
2. Run it. The settings window opens once; after that ValSpot lives in the tray next to the clock.
3. Open your music and play. Turn on **Start with Windows** and it waits in the tray until VALORANT opens.

The tray icon's dot shows what it's doing:

| Dot | Meaning |
|---|---|
| grey | waiting for VALORANT, or in the menus |
| blue | in a match, music off |
| green | music on |
| amber | you paused ValSpot |
| red | something needs a look (hover for details) |

Left-click the icon for settings, right-click for the menu.

**First launch:** the exe isn't code-signed, so Windows says "Windows protected your PC".
Click **More info**, then **Run anyway**.

## Settings

Everything is in the settings window: what plays when, how early to stop before the round,
fade length, which music app, start with Windows, update check, and pause.

Settings are saved in `%APPDATA%\ValSpot\settings.json`, with the log (`valspot.log`) and anything
it learned. Put an empty `settings.json` next to `ValSpot.exe` to keep everything in that folder
instead (portable mode).

## How it knows what's happening

**Round timing comes from VALORANT's own log** (`ShooterGame.log`), which the game writes anyway:

| What | Log line |
|---|---|
| Buy phase starts | `Gameplay started at local time 0.x` |
| Barriers drop | `Gameplay started at local time 30.x` or `45.x` |
| Round over | `OnRoundEnded for round 'N'` |
| Match start and end | the game moving to `InGame` or `MainMenu` |
| Game mode | the mode path just before the match starts |

Buy phases are 30 seconds, except 45 at load-in and after the side swap: after round 12 in
standard matches, after round 4 in Swiftplay. In overtime, the first round (round 25) gets 45
seconds and every later one 30. Other modes learn it: if a buy phase runs 45 seconds when ValSpot
expected 30, it remembers that round for that mode.

Started ValSpot in the middle of a match? It reads the match so far from the log and joins in.

**Your death comes from the screen**, because the log doesn't say when you die. While you spectate,
VALORANT shows **"< mouse > SWITCH PLAYER"** under your teammate's name. During live rounds,
ValSpot takes a tiny screenshot of that spot 8 times a second and compares it with two built-in
pictures of that line. It counts you as dead after 3 matching checks in a row, and alive again after
4 seconds without it.

| | Typical score |
|---|---|
| Dead | 0.8 to 1.0 |
| Alive | 0.2 or lower |
| Needed to count as dead | 0.60 |

The spot is worked out from the window size. VALORANT always draws a 16:9 picture and adds black
bars on wider screens, so every 16:9 size, the common ultrawides (21:9, 32:9 like the Samsung G9)
and 16:10 work from the first death. On any other size, ValSpot finds the line during your first
death and saves the position for good.

**Game not in English?** The line says different words. Right-click the tray icon, choose
**Learn my death screen**, die and spectate, click Capture, and drag a box around that line once.

## Is it safe with Vanguard?

It never touches the game: no memory reading, no injection, no input. It reads the log file
VALORANT already writes, and screenshots a small corner of the game window during live rounds,
only while VALORANT is the window you're in. Tabbing out keeps the music the way it was.

## Known limits

- Tested on standard matches (overtime included), Swiftplay and the Range. Deathmatch,
  Team Deathmatch, Spike Rush and Escalation are untested.
- Non-English clients need "Learn my death screen" once.
- Exclusive Fullscreen and 4:3 stretched are untested; Windowed Fullscreen is what was used.
- Fading in and out was tested on Spotify; other apps should work the same way.
- If you play or pause your music by hand, ValSpot doesn't fight you. It acts at the next event.
- The exe is 172 MB (67 MB zipped), because it carries its own .NET runtime.

## Something wrong?

Right-click the tray icon, choose **Open log folder**, and send `valspot.log`, plus the `diag`
folder if death music never worked. Every decision in the log comes with its reason.

## Building it

Source is in [`app/`](app/): C# and WPF on .NET 10, with NAudio for the volume fades.
With the .NET 10 SDK installed, run `app\build.ps1`. It makes `app\publish\ValSpot.exe`
(one self-contained file) and `app\ValSpot-share.zip`.

The exe has a test mode for checking changes without playing:

| Command | What it does |
|---|---|
| `--replay LOG` | Runs an old `ShooterGame.log` through the rules and prints every decision |
| `--eval --ffmpeg FFMPEG --height H CLIP` | Scores death detection on a recorded clip (`--canvas 5120x1440` adds G9 black bars) |
| `--check` | Prints the death score on the live screen once a second |
| `--media` | Shows which music app it would control |
| `--version` | Prints the version |

Add `--out FILE` to save the output; the exe is a windowed app, so it doesn't print to a console.

The older Python version (2.x) is documented in [docs/python-v2.md](docs/python-v2.md).

## License

ValSpot is free for personal and other noncommercial use under the
[PolyForm Noncommercial License 1.0.0](LICENSE.md). You can use it, change it and share it,
as long as it's not for money and the license comes along. Selling it, or using it in a paid
product or service, is not allowed.
