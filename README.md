# valspot

Plays Spotify while you are **dead** or in the **buy phase** of a VALORANT match.
Pauses it everywhere else, including 3 seconds before the barriers drop.

Start it by double-clicking **`Start valspot.bat`** (or `valspot.exe` from the package), then play.
Close the window to stop.

## How it knows what is happening

| What | Where it comes from |
|---|---|
| Buy phase start | VALORANT's own log, `ShooterGame.log` (`Gameplay started at local time 0.x`) |
| Round 1 buy phase | Match load-in line in the same log (round 1 has no buy line; its 45 s buy starts at load-in) |
| Barriers drop | Same log (`Gameplay started at local time 30.x` / `45.x`) |
| Match over | Same log (`Transitioning ... to MainMenu`) |
| You died | The **"< mouse > SWITCH PLAYER"** prompt under the spectated teammate's name, bottom-left of the game window |
| Spotify | Windows media controls (same thing as the volume-flyout buttons). No Premium or login. |

Nothing reads or touches the game process. The log is opened with full sharing, so VALORANT can
still rename or delete it at launch. The screen check is two tiny boxes in the bottom-left of the
**VALORANT window**, 8 times a second, only during a live round and only while VALORANT is focused.

**Any monitor, any 16:9 resolution.** The window is found by its process name, and the templates
are stored at 1080p and scaled to the window's real height.

## Rules it follows

- **Buy phase starts**: play.
- **Buy phase has 3 s left** (30 s buy, or 45 s on round 1 and round 13): pause.
- **Barriers drop**: pause (safety net).
- **You die** (marker seen 3 frames in a row): play.
- **You get revived** (marker gone 4 s): pause.
- **Match ends / you leave**: pause.
- **Tabbed out** (Chrome, Discord...): the screen is ignored and the dead/alive state is held.
  Buy-phase timing comes from the log, so tabbing never affects it.
- Only **bomb-mode** matches start music on load-in. The Range does not.

Every decision is logged in `valspot.log` with the reason.

## Settings (`config.json`, written on first run)

| Key | Default | Meaning |
|---|---|---|
| `pause_lead_seconds` | 3 | How long before the barriers drop to pause |
| `death_threshold` | 0.60 | Marker score needed to count as dead (alive frames score at most 0.20; dead 0.79+ at 1080p, 1440p and 4K) |
| `death_confirm_frames` | 3 | Frames in a row the marker must show (at 8 per second) |
| `revive_frames` | 32 | Frames without the marker before "revived" (4 s) |
| `only_when_focused` | true | Only read the screen while VALORANT is the focused window |
| `dry_run` | false | true = log what it would do, never touch Spotify |

Delete `config.json` to get the defaults back. `config.v1.json` is the old v1 file, kept for reference.

## Sharing it: the exe

`build.bat` makes `dist\valspot.exe`, one file with Python, the libraries and the templates inside.
The friend package is `valspot-share.zip`: the exe plus `package\README.txt`.
The exe is unsigned, so Windows SmartScreen asks "More info / Run anyway" on first launch.

## Templates

`templates/` holds the two "SWITCH PLAYER" shapes: a larger one right after you die or switch
players, and a smaller one it settles into after a few seconds. Both were cut from Medal clips.

If a patch restyles the prompt, re-cut it from a screenshot or clip frame where you are dead
(coordinates are in that image's pixels; its height is stored, so any resolution works):

```bash
.venv/Scripts/python valspot.py template FRAME.png X Y W H --name switch_player
```

Then check it against clips, optionally rescaled to simulate another monitor:

```bash
.venv/Scripts/python valspot.py eval --ffmpeg "%LOCALAPPDATA%/Medal/recorder-2642.2986.1/ffmpeg7.exe" --clips "D:/Medal/Clips/Valorant/SOMECLIP.mp4" --height 1440
```

## Other modes

| Command | What it does |
|---|---|
| `valspot.py check` | Prints the live marker score once a second (dead should read 0.8+) |
| `valspot.py spotify` | Plays Spotify for 2 s, then pauses (control test) |
| `valspot.py replay LOG` | Feeds an old `ShooterGame.log` through the rules, dry-run |
| `valspot.py run --dry-run` | Real run, but only logs Spotify actions |

## Known limits

- English game client only: the marker is the text "SWITCH PLAYER".
- 4:3 stretched and other non-16:9 setups are untested.
- Exclusive Fullscreen is untested; Windowed Fullscreen is what was used.
- Overtime buy length is assumed to be 30 s. Swiftplay, Spike Rush, Deathmatch and customs are untested.
- If you pause or play Spotify by hand, valspot does not fight you. It only acts at the next event.
