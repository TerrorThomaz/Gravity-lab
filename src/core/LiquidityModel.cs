namespace TradingGA;

// Participation-rate liquidity model — Layer 3, built from what the data actually supports.
//
// WHAT WE HAVE AND WHAT WE DO NOT
// There is no order-book data in this repo and no realistic way to backfill 3 years of L2 depth for
// ~190 perps. Candle carries OHLCV, so depth must be inferred from BAR VOLUME. That rules out
// modelling the book directly and rules IN the thing that actually drives cost: what fraction of
// available volume your order represents.
//
// THE GAP THIS CLOSES
// TradeCosts.SlippagePerSidePct scales with the coin's ATR but is completely INDEPENDENT OF SIZE —
// a 2 EUR position and a 200 EUR position pay identical basis points. That was harmless while every
// position was a fixed small fraction of a small book. It stops being harmless as soon as position
// sizes grow, which is precisely what DynamicExposureCap does: raising the cap in calm markets
// makes positions bigger, and a size-blind cost model will happily report that as free profit.
//
// So this is the term that stops the exposure work from being self-justifying. Without it, "deploy
// more capital" always looks better, because the extra size costs nothing to execute.
//
// THE MODEL
// Square-root market impact, the standard empirical form (Almgren-Chriss and successors):
//
//     impact_bps = ImpactCoefficient * sigma * sqrt(participation)
//     participation = orderNotional / barNotional
//
// Square root, not linear: liquidity replenishes while you execute, so doubling size costs about
// 1.41x rather than 2x. This is the shape every published equity and crypto impact study converges
// on, and getting the SHAPE right matters far more than the coefficient, which is not identifiable
// from OHLCV anyway.
//
// The coefficient is deliberately a single conservative constant rather than a fitted per-symbol
// table. Fitting it to bar data would imply a precision that does not exist — bar volume includes
// the whole market's activity, not the depth available to one taker — and this codebase has been
// bitten repeatedly by numbers that looked fitted but were not validated.
public static class LiquidityModel
{
    // Impact in basis points at 100% participation and 1% bar volatility. Calibrated so that a
    // 1% participation trade on a typical bar costs a few bps — the same order as the existing
    // flat slippage — and a 20% participation trade costs multiples of it. The point of this
    // model is the CURVE, not the level; the level is already owned by Config.SlippageBps.
    public const double ImpactCoefficient = 8.0;

    // Participation above which a fill should be considered unrealistic rather than merely
    // expensive. At 25% of a bar's volume you are the market, and a backtest that books a fill
    // there is describing a trade that could not have happened at that price.
    public const double MaxRealisticParticipation = 0.25;

    // Extra slippage, in percentage points, for ONE side of a trade of the given size.
    //
    // barNotional is the bar's traded value in the same currency as orderNotional. Zero or missing
    // volume returns 0 rather than infinity: a data gap must not silently zero out a strategy, and
    // the flat TradeCosts slippage still applies underneath.
    public static double ImpactPct(double orderNotional, double barNotional, double atrPct)
    {
        if (orderNotional <= 0 || barNotional <= 0) return 0.0;
        double participation = orderNotional / barNotional;
        double bps = ImpactCoefficient * Math.Max(atrPct, 0.1) * Math.Sqrt(participation);
        return bps / 100.0;
    }

    public static bool IsFillRealistic(double orderNotional, double barNotional)
        => barNotional > 0 && orderNotional / barNotional <= MaxRealisticParticipation;

    // Largest order that stays inside the realistic-participation bound.
    public static double MaxRealisticOrder(double barNotional)
        => Math.Max(0.0, barNotional * MaxRealisticParticipation);

    public record Row(double ParticipationPct, double ImpactPct, bool Realistic);

    // A cost curve for a given bar liquidity and volatility. Used to show what the model implies
    // rather than asserting it in a comment.
    public static IReadOnlyList<Row> Curve(double barNotional, double atrPct,
                                           IReadOnlyList<double> participations)
    {
        var rows = new List<Row>();
        foreach (double p in participations)
        {
            double order = barNotional * p;
            rows.Add(new Row(p * 100.0, ImpactPct(order, barNotional, atrPct),
                             IsFillRealistic(order, barNotional)));
        }
        return rows;
    }
}
