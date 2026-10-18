# valspot test sheet (2026-10-17, v2)

Built and checked offline tonight. This sheet is the first **live** test. Takes one match.

## Before the match

1. Open Spotify and pick a playlist. Leave it **paused**.
2. Double-click **`Start valspot.bat`**.
3. The window should print a line starting with `ready.` that shows `death templates: 2`.
   If it says `death templates: 0`, stop and tell Claude.

## During the match: what should happen

| Moment | Expected music | Log line to look for |
|---|---|---|
| Load-in / round 1 buy phase | **Plays** | `BUY phase start (expect 45s` |
| ~3 s before barriers drop | **Pauses** | `BUY phase nearly over` |
| Round is live, you are alive | Silent | `ROUND live` |
| You die | **Plays** within about 1 s of "SWITCH PLAYER" showing | `DEAD marker 'switch_player...' score 0.9x` |
| Teammate clutches or loses, next buy phase | **Keeps playing** | `BUY phase start (expect 30s` |
| You survive the round, next buy phase | **Plays** | `BUY phase start` |
| Round 13 (after half-time) | Plays, pauses ~42 s in | `expect 45s` |
| Match ends / you leave | **Pauses** | `MATCH end` |

## Things to try on purpose

- **Die early in a round** and spectate for 20+ seconds. Music must not cut out while you stay dead.
- **Switch the spectated player** a few times with the mouse. Music must keep playing.
- **Die, then alt-tab to Chrome for 10+ seconds.** Music must keep playing. The log shows `tabbed out (chrome.exe), holding state: dead`, then `VALORANT focused again` when you come back.
- **If you get a Sage res**: music should pause about 4 s after you are back.

## If something is wrong

- Note the **round number** and what the music did.
- Send Claude the file **`valspot.log`** from this folder. It records every decision and the reason.
- Music never plays on death: run `Start valspot.bat`, die, and check whether a `DEAD` line appears. If not, the marker moved and needs a re-cut from a Medal clip.

## Known open before this test

- Death detection is verified on Medal clips only (dead frames 0.90 to 0.998, alive at most 0.20). A live 4K grab is unverified.
- Overtime buy length and non-standard modes are untested.
