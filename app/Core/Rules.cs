using System.Collections.Generic;
using System;

namespace ValSpot.Core;

public enum Phase { Menu, Pregame, Buy, Combat, RoundEnd }

/// <summary>What the music should do and why. Same rules as the Python version:
/// buy phase starts -> play; PauseLeadSeconds before barriers drop -> pause; barriers drop ->
/// pause; you die -> play; revived -> pause; match over -> play (back in the lobby) or pause.
/// Round 1's buy phase has no log line, so it starts at load-in (45 s) in modes with a buy
/// phase. The side-swap buy is also 45 s; when it happens depends on the mode (after round 12
/// in standard, after round 4 in Swiftplay) and is learned automatically for other modes.</summary>
public sealed class Rules
{
    private readonly Settings _s;
    public Phase Phase { get; private set; } = Phase.Menu;
    public bool Dead { get; private set; }
    public bool InMatch { get; private set; }
    /// <summary>"Bomb", "Swiftplay", "Range", or the raw game-mode name for anything else.</summary>
    public string Mode { get; private set; } = "";
    public int? LastRound { get; private set; }
    public int RoundsThisMatch { get; private set; }
    public DateTime? PauseAt { get; private set; }
    public bool MusicWanted { get; private set; }
    private double _expectedBuy;
    private double _buyClock;   // game clock at the buy-phase start line (0 at load-in)

    /// <summary>When true (catching up on an already-running match), nothing is logged and no
    /// music commands are sent; only the state is rebuilt.</summary>
    public bool Quiet { get; set; }

    /// <summary>(play, reason). Not raised while the user has ValSpot paused, or while Quiet.</summary>
    public event Action<bool, string>? Music;
    public event Action? Changed;

    public Rules(Settings s) => _s = s;

    private void Say(string msg)
    {
        if (!Quiet) Log.Write(msg);
    }

    private void Want(bool play, string why)
    {
        MusicWanted = play;
        if (!_s.Paused && !Quiet) Music?.Invoke(play, why);
    }

    /// <summary>Turns "/Game/GameModes/_Development/Swiftplay_EndOfRoundCredits/Swiftplay_EoRCredits_GameMode"
    /// or "/Game/GameModes/Bomb/BombGameMode" into a short mode name.</summary>
    public static string ModeKind(string path)
    {
        if (path.Contains("Swiftplay", StringComparison.OrdinalIgnoreCase)) return "Swiftplay";
        if (path.Contains("ShootingRange", StringComparison.OrdinalIgnoreCase)) return "Range";
        if (path.Contains("/Bomb/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("BombGameMode", StringComparison.OrdinalIgnoreCase)) return "Bomb";
        int slash = path.LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// <summary>Modes where the match opens with a 45 s buy phase at load-in.</summary>
    public static bool BuyAtLoadIn(string mode) => mode is "Bomb" or "Swiftplay";

    /// <summary>Long 45 s buy phases mid-match, as the index of the round just ended. Standard
    /// matches: the side swap after round 12, and the first overtime round after round 24
    /// (every later overtime round is 30 s). Swiftplay: the side swap after round 4. Measured
    /// on the user's logs, 2026-10-18.</summary>
    private static readonly Dictionary<string, int[]> BuiltInLongBuys = new()
    {
        ["Bomb"] = new[] { 11, 23 },
        ["Swiftplay"] = new[] { 3 },
    };

    public bool LongBuyAfter(string mode, int round) =>
        (BuiltInLongBuys.TryGetValue(mode, out var b) && Array.IndexOf(b, round) >= 0)
        || (_s.LearnedLongBuys.TryGetValue(mode, out var l) && l.Contains(round));

    public void Handle(GameEvent ev, DateTime now)
    {
        switch (ev.Kind)
        {
            case GameEventKind.Mode:
                Mode = ModeKind(ev.Value);
                break;
            case GameEventKind.MatchStart:
                InMatch = true;
                LastRound = null;
                RoundsThisMatch = 0;
                Dead = false;
                Phase = Phase.Pregame;
                Say($"MATCH start (mode={(Mode.Length > 0 ? Mode : "?")})");
                _buyClock = 0;
                if (BuyAtLoadIn(Mode)) EnterBuy(now);   // measured 44.8-45.3 s load-in to barriers
                break;
            case GameEventKind.Pregame:
                Phase = Phase.Pregame;
                break;
            case GameEventKind.Buy:
                // the Shooting Range logs a "buy phase" line at load-in; it has no rounds
                if (Mode != "Range")
                {
                    _buyClock = ev.Number;
                    EnterBuy(now);
                }
                break;
            case GameEventKind.Combat when Mode == "Range":
                break;
            case GameEventKind.Combat:
                LearnBuyLength(ev.Number);
                Phase = Phase.Combat;
                PauseAt = null;
                Dead = false;
                Say("ROUND live (barriers down)");
                Want(false, "round is live");
                break;
            case GameEventKind.RoundEnd:
                Phase = Phase.RoundEnd;
                LastRound = (int)ev.Number;
                RoundsThisMatch++;
                PauseAt = null;
                Say($"ROUND {(int)ev.Number} ended (you were {(Dead ? "dead" : "alive")})");
                break;
            case GameEventKind.MatchEnd:
                if (InMatch || Phase != Phase.Menu)
                {
                    Say($"MATCH end ({RoundsThisMatch} rounds)");
                    if (_s.PlayInLobby) Want(true, "back in the lobby");
                    else Want(false, "match ended");
                }
                InMatch = false;
                Phase = Phase.Menu;
                PauseAt = null;
                Dead = false;
                break;
        }
        if (!Quiet) Changed?.Invoke();
    }

    /// <summary>The barrier-drop line says how long the buy phase really was ("local time 45.x").
    /// If a mid-match buy ran long when we expected a normal one, that round is this mode's
    /// side swap: remember it (saved), so the next match gets it right.</summary>
    private void LearnBuyLength(double barrierClock)
    {
        if (LastRound is not int n || barrierClock <= 0 || Phase != Phase.Buy) return;
        double actual = barrierClock - _buyClock;   // both on the game's clock, so a late start cancels out
        bool wasLong = actual >= (_s.BuySeconds + _s.FirstBuySeconds) / 2.0;
        bool expectedLong = _expectedBuy >= (_s.BuySeconds + _s.FirstBuySeconds) / 2.0;
        if (wasLong && !expectedLong && Mode.Length > 0)
        {
            if (!_s.LearnedLongBuys.TryGetValue(Mode, out var list)) _s.LearnedLongBuys[Mode] = list = new();
            if (!list.Contains(n))
            {
                list.Add(n);
                list.Sort();
                _s.Save();
            }
            Say($"learned: in {Mode}, round {n + 2} has a long buy phase ({actual:0} s); next time it stops on time");
        }
    }

    private void EnterBuy(DateTime now)
    {
        Phase = Phase.Buy;
        Dead = false;
        bool first = LastRound is not int r || LongBuyAfter(Mode, r);
        double length = first ? _s.FirstBuySeconds : _s.BuySeconds;
        _expectedBuy = length;
        double lead = Math.Clamp(_s.PauseLeadSeconds, 0, length);
        PauseAt = now.AddSeconds(length - lead);
        Say($"BUY   phase start (expect {length:0}s, pausing at T+{length - lead:0.#}s)");
        if (_s.PlayInBuy) Want(true, "buy phase");
        else Want(false, "buy phase (buy-phase music is off)");
    }

    /// <summary>Called every poll.</summary>
    public void Tick(DateTime now)
    {
        if (PauseAt is { } at && now >= at)
        {
            PauseAt = null;
            Say("BUY   phase nearly over");
            Want(false, "round about to start");
            if (!Quiet) Changed?.Invoke();
        }
    }

    public bool WantsDeathCheck => Phase == Phase.Combat;

    public void SetDead(bool dead, string why)
    {
        if (dead == Dead) return;
        Dead = dead;
        if (dead)
        {
            if (_s.PlayOnDeath) Want(true, why);
        }
        else Want(false, why);
        Changed?.Invoke();
    }

    /// <summary>One-line summary of where we are (used after catching up).</summary>
    public string Describe() => !InMatch
        ? "not in a match"
        : $"in a {(Mode.Length > 0 ? Mode : "?")} match, round {(LastRound ?? -1) + 2}, {Phase switch
        {
            Phase.Buy => "buy phase",
            Phase.Combat => "round live",
            Phase.RoundEnd => "round just ended",
            _ => "loading",
        }}";

    /// <summary>After catching up: send the one music command the current moment calls for.</summary>
    public void ApplyCurrent(string why)
    {
        if (!InMatch) return;   // in the menus: leave the user's music alone
        if (Phase == Phase.Buy && PauseAt is { } at && at > DateTime.Now)
            Want(_s.PlayInBuy, why + " (buy phase in progress)");
        else if (Phase == Phase.Combat)
            Want(false, why + " (round live)");
    }
}
