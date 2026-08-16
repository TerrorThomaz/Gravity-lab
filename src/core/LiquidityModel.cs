namespace TradingGA;

// Square-root market impact model. impact_bps = 8.0 × sigma × sqrt(participation).
// The only size-aware cost — without it, larger positions execute for free.
public static class LiquidityModel
{
    // Impact coefficient. The curve matters, not the level (level owned by Config.SlippageBps).
    public const double ImpactCoefficient = 8.0;

    // Above 25% participation, a fill is unrealistic — you are the market.
    public const double MaxRealisticParticipation = 0.25;

    // Extra slippage (pct) for one side. Zero/missing volume → 0 (flat slippage still applies).
    public static double ImpactPct(double orderNotional, double barNotional, double atrPct)
    {
        if (orderNotional <= 0 || barNotional <= 0) return 0.0;
        double participation = orderNotional / barNotional;
        double bps = ImpactCoefficient * Math.Max(atrPct, 0.1) * Math.Sqrt(participation);
        return bps / 100.0;
    }

    public static bool IsFillRealistic(double orderNotional, double barNotional)
        => barNotional > 0 && orderNotional / barNotional <= MaxRealisticParticipation;


    public static double MaxRealisticOrder(double barNotional)
        => Math.Max(0.0, barNotional * MaxRealisticParticipation);

    public record Row(double ParticipationPct, double ImpactPct, bool Realistic);

    // Cost curve for diagnostics.
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
