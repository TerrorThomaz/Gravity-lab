namespace TradingGA;

// Liquidation / maintenance-margin model — Layer 1.
//
// REDUNDANT TODAY, ON PURPOSE
// At Config.MaxTotalExposurePct = 0.30 the book runs at 0.30x gross, equity is 3.3x notional, and
// no combination of modelled moves reaches a maintenance-margin breach before a stop fires. This
// class will report "never binds" for the current configuration and that is the correct answer.
//
// WHY BUILD IT ANYWAY
// Two futures make it load-bearing, and both are things this system is actively moving toward:
//
//   1. PER-STRATEGY LEVERAGE. A strategy with a high win rate and a tight stop is the obvious
//      candidate for leverage (RipShort at 77% WR, SwingLong at 85%). The moment any position is
//      levered, gross notional decouples from equity and the "3.3x cushion" argument evaporates —
//      for that position specifically, not for the book average.
//   2. RISING CONCURRENT DEMAND. Uncapped notional demand already peaks at 285% on validation and
//      335-950% on OOS. Today the cap absorbs it silently. Anything that raises the cap — a wider
//      risk budget, calmer markets under DynamicExposureCap, more strategies, shorter bars — walks
//      toward the region where this matters.
//
// Building it while the risk work is fresh costs one file. Discovering it is needed after wiring
// leverage costs a blown account, because the failure is SILENT: the backtest keeps printing clean
// stop-outs on paths where a real account was already closed out by the exchange.
//
// WHAT IT MODELS
// Bybit-style isolated-margin maintenance requirement. A position is liquidated when its remaining
// margin falls below the maintenance requirement on its notional. Crucially the exchange closes at
// the LIQUIDATION price, not at your stop — so the loss is bounded by margin posted, not by the
// stop distance the strategy chose. That is the asymmetry the current simulator cannot express.
public static class Liquidation
{
    // Maintenance margin rate. Bybit tiers this by notional and symbol; 0.5% is the majors' first
    // tier and 1.0% covers most alt perps. Deliberately a flat conservative pair rather than a
    // fitted table: a fitted MMR would imply a precision the rest of this model does not have.
    public const double MmrMajors = 0.005;
    public const double MmrAlts   = 0.010;

    // Adverse move (as a fraction of entry) at which a position of the given leverage is
    // liquidated. leverage = notional / margin posted.
    //
    //   liqMove = 1/leverage - mmr
    //
    // At 1x with 0.5% MMR this is 99.5% — i.e. unreachable, which is why the model is inert today.
    // At 10x it is 9.5%, well inside a normal crypto day. At 20x it is 4.5%, inside an hour.
    public static double LiquidationMovePct(double leverage, double mmr)
    {
        if (leverage <= 1e-9) return double.PositiveInfinity;
        return Math.Max(0.0, 1.0 / leverage - mmr) * 100.0;
    }

    // Does a stop at stopDistancePct fire BEFORE liquidation? This is the property the whole
    // no-liquidation-model argument rests on, so make it checkable rather than assumed.
    //
    // Returns true when the stop is safely inside the liquidation level. When it returns FALSE the
    // strategy's own risk control is fictional: the exchange closes the position first, at a worse
    // price, and the stop never executes.
    public static bool StopFiresFirst(double stopDistancePct, double leverage, double mmr)
        => stopDistancePct < LiquidationMovePct(leverage, mmr);

    // Maximum leverage at which a given stop still fires before liquidation, with a safety factor
    // for gap risk (a stop is not guaranteed to fill at its level in a fast market).
    //
    // This is the number to consult before putting leverage on a strategy: it converts "this
    // strategy has a 2% stop" into "therefore it cannot exceed Nx".
    public static double MaxSafeLeverage(double stopDistancePct, double mmr, double gapSafetyFactor = 2.0)
    {
        double required = stopDistancePct / 100.0 * gapSafetyFactor;
        if (required <= 0) return double.PositiveInfinity;
        double lev = 1.0 / (required + mmr);
        return Math.Max(1.0, lev);
    }

    public record BookCheck(
        bool   AnyBinding,          // did liquidation bind anywhere in this book?
        double WorstLeverage,       // highest position leverage seen
        double TightestMarginPct,   // smallest gap between stop and liquidation, in points
        string Verdict);

    // Whole-book check. grossPct is gross notional as a % of equity; stopPcts are the per-strategy
    // stop distances in percent.
    //
    // Book-level leverage is the right input because margin is posted against total equity: ten
    // unlevered positions at 10% of equity each behave like one 1.0x-levered book, and it is the
    // BOOK that gets liquidated, not a position in isolation.
    public static BookCheck CheckBook(double grossPct, IReadOnlyList<double> stopPcts, double mmr = MmrAlts)
    {
        double leverage = grossPct / 100.0;
        double liqMove  = LiquidationMovePct(leverage, mmr);

        double tightest = double.PositiveInfinity;
        bool binding = false;
        foreach (double stop in stopPcts)
        {
            double margin = liqMove - stop;
            if (margin < tightest) tightest = margin;
            if (margin <= 0) binding = true;
        }
        if (stopPcts.Count == 0) tightest = liqMove;

        string verdict = binding                ? "LIQUIDATION BINDS — stops are fiction"
                       : tightest < 5.0         ? "thin margin — a gap could liquidate"
                       : leverage < 1.0         ? "inert (equity exceeds notional)"
                                                : "safe";
        return new BookCheck(binding, leverage, tightest, verdict);
    }

    public static void Print(double grossPct, IReadOnlyList<double> stopPcts, double mmr = MmrAlts)
    {
        var c = CheckBook(grossPct, stopPcts, mmr);
        Console.WriteLine($"\n── Liquidation check (gross {grossPct:F1}% of equity · MMR {mmr:P2}) ──────────");
        Console.WriteLine($"  book leverage {c.WorstLeverage:F2}x · liquidation at "
                        + $"{LiquidationMovePct(c.WorstLeverage, mmr):F1}% adverse move");
        Console.WriteLine($"  tightest stop-to-liquidation margin: {c.TightestMarginPct:F1} points");
        Console.WriteLine($"  {c.Verdict}");
    }
}
