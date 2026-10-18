ValSpot 3 - your music plays while you're dead, in the buy phase, or back in the lobby
============================================================================

What it does
  - Buy phase starts .............. music fades in
  - Seconds before barriers drop .. music fades out (you pick how many)
  - You die ....................... music fades in, until the next round goes live
  - Round live and you're alive ... silent
  - Back in the lobby ............. music fades in (can be turned off)
  - Shooting Range ................ left alone

Works with Spotify, or whatever app is playing (YouTube Music, Apple Music,
Tidal, a browser...). Any monitor and resolution, ultrawide and G9 included.
Standard matches and Swiftplay; other modes learn their timing by themselves.
Started it in the middle of a match? It reads the match so far and joins in.

How to use it
  1. Put ValSpot.exe somewhere it can stay (Desktop, Documents...).
  2. Double-click it. The settings window opens once; after that ValSpot
     lives in the tray next to the clock.
  3. Open your music, play VALORANT. That's it.
  Tip: turn on "Start with Windows" and it waits in the tray until VALORANT opens.

The tray icon's dot
  grey  = waiting for VALORANT / in the menus
  blue  = in a match, music off
  green = music on (buy phase or you're dead)
  amber = you paused ValSpot
  red   = something needs a look (hover for details)
  Left-click the icon for settings, right-click for the menu.

First launch: "Windows protected your PC"
  The exe isn't code-signed, so Windows warns about it.
  Click "More info", then "Run anyway".

Death detection
  ValSpot watches for the "SWITCH PLAYER" line VALORANT shows under your
  teammate's name while you spectate. Common screen sizes (all 16:9, 21:9,
  32:9 like the Samsung G9, 16:10) are built in, so nothing to set up.
  Game not in English, or it never notices you died? Right-click the tray
  icon > "Learn my death screen", die and spectate, click Capture, and
  drag a box around that line once.

Is it safe with Vanguard?
  It never touches the game. It reads the log file VALORANT already writes
  and takes a tiny screenshot of the bottom-left of the game window during
  live rounds, only while VALORANT is the window you're in. Tabbing out
  keeps the music the way it was.

Where your settings live
  %APPDATA%\ValSpot  (settings.json, valspot.log, anything it learned).
  Put an empty settings.json next to ValSpot.exe to keep everything in
  that folder instead (portable mode).

Something wrong? Right-click the tray icon > Open log folder, and send
valspot.log (plus the "diag" folder if death music never worked).
