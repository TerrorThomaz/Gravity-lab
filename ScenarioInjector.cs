namespace TradingGA;

// Morphs a real Candle[] with a parameterised crash scenario.
// Returns a new array — originals are never modified.
//
// Crash shape: linear descent from anchor close over CrashDurationHours bars.
// ATR expansion: range inflated by AtrExpansionPeak (sin envelope, peaks at midpoint).
// Recovery: linear rise from trough to 50% of lost depth over RecoveryHours bars.
// Liquidity: volume reduced linearly during first LiquiditySqueezeHours bars.
//
// DeaggregateToM15: generates 4 equal 15m stubs per h1 bar for multi-TF strategies.
public static class ScenarioInjector
{
    // altBetaMult: 1.0 for BTC, g.AltBetaPct for all other coins.
    public static Candle[] Inject(Candle[] h1, ScenarioGenotype g, int injectionBar, double altBetaMult = 1.0)
    {
        var result    = h1.ToArray();
        int crashBars = Math.Max(1, (int)g.CrashDurationHours);
        int recovBars = (int)g.RecoveryHours;
        int liqBars   = (int)g.LiquiditySqueezeHours;
        double depth  = g.CrashDepthPct * altBetaMult;

        double peakClose = injectionBar > 0 ? h1[injectionBar - 1].Close : h1[0].Close;

        // Phase 1: crash
        for (int i = 0; i < crashBars && injectionBar + i < h1.Length; i++)
        {
            double t         = (double)(i + 1) / crashBars;
            double newClose  = peakClose * (1.0 - depth * t);
            double atrExpand = 1.0 + (g.AtrExpansionPeak - 1.0) * Math.Sin(Math.PI * (double)i / crashBars);
            double rawRange  = h1[injectionBar + i].High - h1[injectionBar + i].Low;
            double newRange  = rawRange * atrExpand;
            double volMult   = (i < liqBars) ? Math.Max(0.1, 1.0 - 0.8 * t) : 1.0;
            double newOpen   = i == 0 ? h1[injectionBar].Open : result[injectionBar + i - 1].Close;
            result[injectionBar + i] = new Candle(
                h1[injectionBar + i].Time,
                newOpen,
                newClose + newRange * 0.3,
                Math.Max(newClose - newRange * 0.7, 1e-6),
                newClose,
                h1[injectionBar + i].Volume * volMult);
        }

        // Phase 2: recovery
        int    troughBar   = injectionBar + crashBars;
        double troughPx    = troughBar > 0 && troughBar <= result.Length ? result[troughBar - 1].Close : peakClose * (1 - depth);
        double recovTarget = troughPx + (peakClose - troughPx) * 0.5;

        for (int i = 0; i < recovBars && troughBar + i < h1.Length; i++)
        {
            double t        = (double)(i + 1) / Math.Max(1, recovBars);
            double newClose = troughPx + (recovTarget - troughPx) * t;
            double rawRange = h1[troughBar + i].High - h1[troughBar + i].Low;
            double newOpen  = i == 0 ? result[troughBar - 1].Close : result[troughBar + i - 1].Close;
            result[troughBar + i] = new Candle(
                h1[troughBar + i].Time,
                newOpen,
                newClose + rawRange * 0.4,
                Math.Max(newClose - rawRange * 0.3, 1e-6),
                newClose,
                h1[troughBar + i].Volume);
        }

        return result;
    }

    // Deaggregates h1 into 4 equal 15m stubs per bar.
    public static Candle[] DeaggregateToM15(Candle[] h1)
    {
        var m15 = new Candle[h1.Length * 4];
        for (int i = 0; i < h1.Length; i++)
        {
            var bar = h1[i];
            for (int q = 0; q < 4; q++)
            {
                m15[i * 4 + q] = new Candle(
                    bar.Time + TimeSpan.FromMinutes(15 * q),
                    q == 0 ? bar.Open : bar.Close,
                    bar.High,
                    bar.Low,
                    bar.Close,
                    bar.Volume / 4.0);
            }
        }
        return m15;
    }
}
