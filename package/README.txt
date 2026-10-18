ValSpot - Spotify plays while you're dead or in the buy phase (VALORANT)
=========================================================================

What it does
  - Buy phase starts .............. Spotify plays
  - 3 seconds before barriers drop  Spotify pauses
  - You die ........................ Spotify plays (until the next round goes live)
  - Round live and you're alive .... paused
  - Match over ..................... paused

You need
  - Windows 10 or 11
  - The Spotify desktop app (spotify.com or Microsoft Store), open and logged in
  - VALORANT display mode: Windowed Fullscreen
  - VALORANT language: English (it watches for the words "SWITCH PLAYER"
    that show under your teammate's name when you're spectating)

How to use it
  1. Unzip this folder somewhere normal, like Desktop or Documents.
  2. Open Spotify and pick a playlist (leave it paused).
  3. Double-click valspot.exe. A black window opens. Leave it open.
  4. Play. Close the black window to stop.

First launch: "Windows protected your PC"
  The exe isn't code-signed (that costs money), so Windows warns about it.
  Click "More info", then "Run anyway". Some antivirus apps flag small
  Python-built exes by mistake; that's the same reason.

Is it safe with Vanguard?
  It never touches the game. It reads the log file VALORANT already writes
  (in %LOCALAPPDATA%\VALORANT\Saved\Logs) and takes a tiny screenshot of
  the bottom-left corner while a round is live and VALORANT is the window
  you're in. Tabbing out to Chrome/Discord pauses the screen check and keeps
  the music the way it was.

Ultrawide / Samsung G9
  Works. VALORANT draws a 16:9 picture with black bars, and ValSpot looks
  inside it. The first time you die and spectate, it also double-checks the
  spot and saves it (the black window says "CALIBRATED").

If music never plays when you die
  1. Make sure VALORANT is in English and you're spectating a teammate
     (the words "SWITCH PLAYER" show bottom-left).
  2. Open a command prompt in this folder and run:   valspot.exe check
     Die and spectate. "spot" or "wide" should read 0.8 or higher.
  3. Still nothing: run   valspot.exe shot   , switch to VALORANT while
     spectating, and send the saved valspot-shot-*.png plus valspot.log
     to whoever gave you this.

If music never plays at all (not even in the buy phase)
  - Spotify must be the desktop app and open.
  - Send valspot.log; the first lines say whether Spotify was found.

Settings
  config.json (created on first run) next to the exe:
    pause_lead_seconds   how many seconds before the round to pause (3)
    dry_run              true = only log, never touch Spotify
  Delete config.json to get the defaults back.

Everything it decides is written to valspot.log with the reason.
