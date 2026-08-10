namespace TradingGA;

// Minimum-profit ratchet, shared by every simulator.
//
// Once a position has moved TriggerPct (or TriggerAtrMult x ATR%) in favour, its stop moves to
// LockPct in PROFIT and never returns through breakeven. It is a FLOOR, not a trail: the
// strategy's own trailing stop may still take the trade further out; the ratchet only removes
// the possibility of giving the whole move back.
//
// Why this lives in core rather than in each simulator: the long/short sign flips every
// comparison (a long's stop is BELOW entry and its favourable excursion is a HIGH; a short's is
// the mirror), and that is exactly the kind of duplication that produced six divergent copies of
// FundingPnl with an inverted sign in three of them. One implementation, one sign rule.
//
// ATR-scaling is supported because a flat percentage is the wrong shape on a universe whose ATR%
// spans ~1-8%: measured on RipShort, a flat 2% trigger armed on noise for volatile coins and cost
// 1.67pp/trade of truncated upside, while ATR-scaling recovered most of it. Percentage values act
// as absolute CEILINGS when ATR-scaling is on, so a violent coin cannot push the arm point out
// indefinitely.
public readonly record struct RatchetConfig(
    double TriggerPct     = 0.0,   // profit % that arms the floor (0 = disabled)
    double LockPct        = 0.0,   // profit % locked once armed
    double TriggerAtrMult = 0.0,   // if > 0, trigger = this x ATR%, capped by TriggerPct
    double LockAtrMult    = 0.0)   // if > 0, lock    = this x ATR%, capped by LockPct
{
    public bool Enabled => TriggerPct > 0.0 || TriggerAtrMult > 0.0;
}

public static class ExitRatchet
{
    // Has the position moved far enough in favour to arm the floor?
    //
    // bestPrice is the most FAVOURABLE price seen since entry — the running high for a long, the
    // running low for a short. Callers already track this for their trailing stop, so nothing new
    // has to be threaded through.
    public static bool ShouldArm(bool isLong, double entry, double atrEntry, double bestPrice, RatchetConfig cfg)
    {
        if (!cfg.Enabled || entry <= 1e-9) return false;
        double trig = cfg.TriggerPct;
        if (cfg.TriggerAtrMult > 0.0)
        {
            double atrPct = atrEntry / entry * 100.0;
            double ceil   = cfg.TriggerPct > 0.0 ? cfg.TriggerPct : double.MaxValue;
            trig = Math.Min(cfg.TriggerAtrMult * atrPct, ceil);
        }
        if (trig <= 0.0) return false;

        double excursionPct = isLong
            ? (bestPrice - entry) / entry * 100.0
            : (entry - bestPrice) / entry * 100.0;
        return excursionPct >= trig;
    }

    // The stop price once armed. A long locks BELOW entry-plus-profit (stop rises); a short locks
    // ABOVE entry-minus-profit (stop falls). Returns null when disabled.
    public static double? LockPrice(bool isLong, double entry, double atrEntry, RatchetConfig cfg)
    {
        if (!cfg.Enabled || entry <= 1e-9) return null;
        double lockPct = cfg.LockPct;
        if (cfg.LockAtrMult > 0.0)
        {
            double atrPct = atrEntry / entry * 100.0;
            double ceil   = cfg.LockPct > 0.0 ? cfg.LockPct : double.MaxValue;
            lockPct = Math.Min(cfg.LockAtrMult * atrPct, ceil);
        }
        if (lockPct <= 0.0) return null;
        return isLong ? entry * (1.0 + lockPct / 100.0)
                      : entry * (1.0 - lockPct / 100.0);
    }

    // Tighter of the existing stop and the armed floor, in the direction that reduces risk.
    // For a long the stop only ever moves UP; for a short only DOWN. Never loosens a stop.
    public static double Tighten(bool isLong, double currentStop, double lockPx) =>
        isLong ? Math.Max(currentStop, lockPx) : Math.Min(currentStop, lockPx);
}
