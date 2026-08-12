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
// spans roughly 1-8%: a flat trigger arms on noise for volatile coins and truncates their upside.
// Percentage values act as absolute CEILINGS when ATR-scaling is on, so a violent coin cannot push
// the arm point out indefinitely.
public readonly record struct RatchetConfig(
    double TriggerPct     = 0.0,   // profit % that arms the floor (0 = disabled)
    double LockPct        = 0.0,   // profit % locked once armed
    double TriggerAtrMult = 0.0,   // if > 0, trigger = this x ATR%, capped by TriggerPct
    double LockAtrMult    = 0.0,    // if > 0, lock    = this x ATR%, capped by LockPct
    double TrailAtrMult   = 0.0,    // if > 0, the floor FOLLOWS price at this x ATR once armed
    bool   FloorsTrailOnly = false) // true = floor the TRAILING STOP instead of the hard stop
{
    public bool Enabled => TriggerPct > 0.0 || TriggerAtrMult > 0.0;
}

// FloorsTrailOnly exists because the two mechanisms were COMPETING, not composing.
//
// Applied to the hard stop, the ratchet arms earlier than the strategy's own trail does at its
// gene value. The floor therefore binds long before the trail ever activates, the trail becomes
// dead weight, and the GA — having no gradient left on it — lets it drift: retraining under the
// ratchet showed it abandoning the trail and delegating exits to the floor entirely.
//
// With FloorsTrailOnly the trail governs WHEN to exit on a reversal (gene-tuned, as designed) and
// the ratchet only guarantees the exit level never sits below breakeven+lock. It takes effect only
// once the trail is armed, so it cannot pre-empt it. One mechanism with a floor, not two racing.
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

    // The stop price once armed.
    //
    // With TrailAtrMult > 0 the floor FOLLOWS the favourable excursion at a fixed ATR distance
    // instead of sitting still. This matters because a static floor truncates exactly the trades
    // worth holding: crypto majors drift upward over long horizons, so locking +0.5% and stopping
    // there gives up the rest of a long run. Symptom of the static version: average return per
    // trade falls while win rate rises, i.e. many small wins replacing a few large ones.
    //
    // The floor is the BETTER of the fixed minimum and the trailing level, and callers apply it
    // through Tighten(), which never loosens a stop — so it is monotone by construction and can
    // only ever move in the risk-reducing direction.
    public static double? LockPrice(bool isLong, double entry, double atrEntry, double bestPrice, RatchetConfig cfg)
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
        double fixedFloor = isLong ? entry * (1.0 + lockPct / 100.0)
                                   : entry * (1.0 - lockPct / 100.0);
        if (cfg.TrailAtrMult <= 0.0 || atrEntry <= 0.0) return fixedFloor;

        double trailFloor = isLong ? bestPrice - cfg.TrailAtrMult * atrEntry
                                   : bestPrice + cfg.TrailAtrMult * atrEntry;
        // Never worse than the guaranteed minimum: early in the move the trail sits below the
        // fixed floor, and the whole point of the ratchet is that it cannot come back through it.
        return isLong ? Math.Max(fixedFloor, trailFloor)
                      : Math.Min(fixedFloor, trailFloor);
    }

    // Tighter of the existing stop and the armed floor, in the direction that reduces risk.
    // For a long the stop only ever moves UP; for a short only DOWN. Never loosens a stop.
    public static double Tighten(bool isLong, double currentStop, double lockPx) =>
        isLong ? Math.Max(currentStop, lockPx) : Math.Min(currentStop, lockPx);

    // THE single place the production ratchet is configured.
    //
    // combinedbacktest, oosbacktest and papertrade each parsed GRAVITY_RATCHET themselves, with
    // DIFFERENT fallbacks — so the same env produced different exits per command, and a val-vs-OOS
    // gap partly measured configuration drift rather than generalisation. Papertrade parsed nothing
    // at all and ran with no ratchet, i.e. live signals came from a third mechanism set.
    //
    // ON by default. GRAVITY_RATCHET=0 disables; GRAVITY_RATCHET=<lockAtrMult> overrides the lock
    // distance. Any command that needs the production ratchet calls this rather than re-deriving it.
    public const double DefaultLockAtrMult = 1.0;

    // Strategies the production ratchet is NOT applied to by default.
    //
    // The floor helps COUNTER-trend strategies and hurts WITH-trend ones, and the mechanism is the
    // same in both cases: it caps the upside of a runner in exchange for protecting a giveback.
    // FadeShort/FadeLong/DipLong/SwingLong enter against a stretched move and their profit is a
    // snap-back that genuinely can evaporate, so the floor is worth its cost. RipShort enters WITH
    // an established downtrend and its winners are long continuation runs — measured, ratcheting it
    // cost portfolio return outright with no drawdown improvement to show for it.
    //
    // This is a default, not a prohibition: RipShort still accepts a RatchetConfig, the plumbing is
    // exercised, and passing one explicitly overrides this. GRAVITY_RATCHETALL=1 applies it
    // everywhere, which is how to re-test this decision after a retrain rather than by editing code.
    public static readonly HashSet<string> DefaultExcluded = new(StringComparer.OrdinalIgnoreCase)
        { "ripshort" };

    public static bool AppliesTo(string strategyLabel) =>
        Environment.GetEnvironmentVariable("GRAVITY_RATCHETALL") == "1"
        || !DefaultExcluded.Contains(strategyLabel);

    // The floor a given strategy gets. Two shapes, chosen by which way the strategy trades.
    //
    // WITH-TREND (RipShort): a BREAKEVEN floor that only engages once the strategy's own trail is
    // armed. It can never bind tighter than the gene-tuned trail — it only stops that trail from
    // exiting below entry. Pullbacks against the position are the normal texture of the trend it is
    // riding, so a floor that locks real profit takes the trade off before the trend resumes.
    //
    // COUNTER-TREND (everything else): a floor that locks actual profit. These enter against a
    // stretched move and their gain is a snap-back that genuinely can evaporate, so paying some
    // upside to guarantee the gain is worth it.
    //
    // Measured, the split is worth more than either setting applied uniformly: the breakeven form
    // raises RipShort above its no-ratchet baseline while the profit-locking form raises FadeShort
    // above its own — and each one applied to the other's strategies loses ground.
    // GRAVITY_BEFLOOR=1 serves the with-trend strategies the breakeven form instead of excluding
    // them. It is NOT the default, and the reason is worth recording because the two obvious
    // metrics disagree:
    //
    //   RipShort, no ratchet:       694 trades x +3.54%/trade   PF 6.50   portfolio 147.6%
    //   RipShort, breakeven floor:  741 trades x +3.20%/trade   PF 6.77   portfolio 137.4%
    //
    // The floor raises PROFIT FACTOR (it cuts losses harder than it cuts wins) while lowering
    // ABSOLUTE return (2371 vs 2457 summed trade return) at identical drawdown. PF is a ratio and
    // can improve while the thing you actually compound gets smaller. The portfolio is what pays,
    // so the default follows the portfolio — but the machinery stays wired and one env var away,
    // because after a RipShort retrain this comparison should be re-run, not assumed.
    public static RatchetConfig ForStrategy(string strategyLabel)
    {
        var cfg = FromEnvironment();
        if (!cfg.Enabled) return default;
        if (WithTrend.Contains(strategyLabel))
            return Environment.GetEnvironmentVariable("GRAVITY_BEFLOOR") == "1" ? BreakevenFloor(cfg)
                 : AppliesTo(strategyLabel)                                     ? cfg
                 : default;
        return cfg;
    }

    // Strategies that trade WITH the prevailing trend rather than against it.
    public static readonly HashSet<string> WithTrend = new(StringComparer.OrdinalIgnoreCase)
        { "ripshort" };

    // Same arm point, but the lock collapses to ~breakeven and the strategy's own trail governs
    // the exit level from there.
    private static RatchetConfig BreakevenFloor(RatchetConfig baseCfg) =>
        baseCfg with { LockPct = 0.3, LockAtrMult = 0.05, FloorsTrailOnly = true };

    // Arm point, in ATR multiples of favourable excursion. GRAVITY_RATCHETTRIG overrides it.
    //
    // This is the knob that decides whether the floor is cheap or expensive, and it was previously
    // hardcoded. At 1.5 the floor arms early in a move, so on a with-trend strategy it binds during
    // ordinary pullbacks and takes the trade off before the trend resumes — visible as MORE trades
    // at a HIGHER win rate and a LOWER average return, i.e. large winners chopped into small ones.
    // Push it out and the floor only ever engages on moves that have already run.
    public const double DefaultTriggerAtrMult = 1.5;

    public static RatchetConfig FromEnvironment()
    {
        static double Env(string k, double dflt) =>
            double.TryParse(Environment.GetEnvironmentVariable(k),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : dflt;

        double lockMult = Env("GRAVITY_RATCHET",     DefaultLockAtrMult);
        double trigMult = Env("GRAVITY_RATCHETTRIG", DefaultTriggerAtrMult);
        return lockMult > 0.0
            ? new RatchetConfig(TriggerPct: 8.0, LockPct: 3.0, TriggerAtrMult: trigMult, LockAtrMult: lockMult,
                                FloorsTrailOnly: Environment.GetEnvironmentVariable("GRAVITY_TRAILFLOOR") == "1")
            : default;
    }
}
