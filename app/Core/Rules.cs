using System;

namespace ValSpot.Core;

public enum Phase { Menu, Pregame, Buy, Combat, RoundEnd }

/// <summary>What the music should do and why. Same rules as the Python version:
/// buy phase starts -> play; PauseLeadSeconds before barriers drop -> pause; barriers drop ->
/// pause; you die -> play; revived -> pause; match over -> pause. Round 1's buy phase has no
/// log line, so it starts at load-in (45 s, bomb modes only); round 13's buy is also 45 s.</summary>
public sealed class Rules
{
    private readonly Settings _s;
    public Phase Phase { get; private set; } = Phase.Menu;
    public bool Dead { get; private set; }
    public bool InMatch { get; private set; }
    public string Mode { get; private set; } = "";
    public int? LastRound { get; private set; }
    public int RoundsThisMatch { get; private set; }
    public DateTime? PauseAt { get; private set; }
    public bool MusicWanted { get; private set; }

    /// <summary>(play, reason). Not raised while the user has ValSpot paused.</summary>
    public event Action<bool, string>? Music;
    public event Action? Changed;

    public Rules(Settings s) => _s = s;

    private void Want(bool play, string why)
    {
        MusicWanted = play;
        if (!_s.Paused) Music?.Invoke(play, why);
    }

    public void Handle(GameEvent ev, DateTime now)
    {
        switch (ev.Kind)
        {
            case GameEventKind.Mode:
                Mode = ev.Value;
                break;
            case GameEventKind.MatchStart:
                InMatch = true;
                LastRound = null;
                RoundsThisMatch = 0;
                Dead = false;
                Phase = Phase.Pregame;
                Log.Write($"MATCH start (mode={(Mode.Length > 0 ? Mode : "?")})");
                if (Mode == "Bomb") EnterBuy(now);   // measured 44.8-45.3 s load-in to barriers
                break;
            case GameEventKind.Pregame:
                Phase = Phase.Pregame;
                break;
            case GameEventKind.Buy:
                EnterBuy(now);
                break;
            case GameEventKind.Combat:
                Phase = Phase.Combat;
                PauseAt = null;
                Dead = false;
                Log.Write("ROUND live (barriers down)");
                Want(false, "round is live");
                break;
            case GameEventKind.RoundEnd:
                Phase = Phase.RoundEnd;
                LastRound = (int)ev.Number;
                RoundsThisMatch++;
                PauseAt = null;
                Log.Write($"ROUND {(int)ev.Number} ended (you were {(Dead ? "dead" : "alive")})");
                break;
            case GameEventKind.MatchEnd:
                if (InMatch || Phase != Phase.Menu)
                {
                    Log.Write($"MATCH end ({RoundsThisMatch} rounds)");
                    Want(false, "match ended");
                }
                InMatch = false;
                Phase = Phase.Menu;
                PauseAt = null;
                Dead = false;
                break;
        }
        Changed?.Invoke();
    }

    private void EnterBuy(DateTime now)
    {
        Phase = Phase.Buy;
        Dead = false;
        bool first = LastRound == null || LastRound == _s.HalfTimeRound;
        double length = first ? _s.FirstBuySeconds : _s.BuySeconds;
        double lead = Math.Clamp(_s.PauseLeadSeconds, 0, length);
        PauseAt = now.AddSeconds(length - lead);
        Log.Write($"BUY   phase start (expect {length:0}s, pausing at T+{length - lead:0.#}s)");
        if (_s.PlayInBuy) Want(true, "buy phase");
        else Want(false, "buy phase (buy-phase music is off)");
    }

    /// <summary>Called every poll. Returns true if the dead/alive state changed.</summary>
    public void Tick(DateTime now)
    {
        if (PauseAt is { } at && now >= at)
        {
            PauseAt = null;
            Log.Write("BUY   phase nearly over");
            Want(false, "round about to start");
            Changed?.Invoke();
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
}
