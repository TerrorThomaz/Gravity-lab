using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Closes the last link in the path-risk chain. Grid and GridShort are covered by GridMaeTests;
// FadeShort is the one that reports per POSITION rather than per session, and it is the path the
// GA actually takes (FitnessFromCache -> GetFadeShortReturnsPrecomputedWithRegime).
//
// Without this, "FadeShort is wired" rests on the code compiling and a GA run not crashing —
// which is exactly the standard that let eight mechanisms in this repo ship without ever running.
public class FadeShortMaeTests
{
    // Same shape as the ExecContextConformance fixture, which is known to make these strategies
    // fire: a drift with superimposed swings, big enough for RSI extremes and an ATR-scaled rally.
    private static Candle[] Synthetic(int n, int seed)
    {
        var rng = new Random(seed);
        var arr = new Candle[n];
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < n; i++)
        {
            double wave  = Math.Sin(i / 40.0) * 14.0 + Math.Sin(i / 9.0) * 5.0 + Math.Sin(i / 3.0) * 1.5;
            double noise = (rng.NextDouble() - 0.5) * 3.0;
            double px    = Math.Max(5.0, 100.0 + wave + noise);
            arr[i] = new Candle(t0.AddHours(i), px,
                                px * (1 + 0.010 + rng.NextDouble() * 0.008),
                                px * (1 - 0.010 - rng.NextDouble() * 0.008),
                                px, 1_000_000);
        }
        return arr;
    }

    private static (List<double> Rets, List<double> Mae) Run()
    {
        var candles = Synthetic(9000, 7);
        var closes = candles.Select(c => c.Close).ToArray();
        var highs  = candles.Select(c => c.High).ToArray();
        var lows   = candles.Select(c => c.Low).ToArray();

        // Same indicator construction as FadeShortGA.BuildCache.
        var rsi = Momentum.Rsi(closes, FadeShortSimulator.RsiPeriod);
        var adx = Trend.Adx(highs, lows, closes, FadeShortSimulator.AdxPeriod);
        var atr = Volatility.Atr(highs, lows, closes, FadeShortSimulator.AtrPeriod);

        var g = new FadeShortGenotype
        {
            EmaPeriod = 20, LookbackCandles = 40, RsiOverbought = 55, RsiDivThreshold = 0.5,
            AdxThreshold = 8, MinRallyAtrMult = 0.3,
            StopLossAtrMult = 8.0, MaeAtrMult = 8.0, TakeProfitAtrMult = 8.0,
            TrailingActivationAtrMult = 8.0, TrailingStopAtrMult = 8.0,
            MaxHoldCandles = 60, PositionSizePct = 0.05,
            RegimeEmaPeriod = 100, RegimeSlopeLookback = 20, RegimeSustainBars = 0,
        };
        var ema = Trend.Ema(closes, g.EmaPeriod);

        var mae = new List<double>();
        var trades = FadeShortSimulator.GetFadeShortReturnsPrecomputedWithRegime(
            g, candles, closes, highs, lows, rsi, adx, atr, ema, 0, candles.Length, mae);

        return (trades.Select(t => t.Return).ToList(), mae);
    }

    [Fact]
    public void PrecomputedPath_EmitsOneExcursionPerTrade()
    {
        var (rets, mae) = Run();
        // Enough trades to clear Canonical's minTradesPerFold sentinel, so the score test below
        // is exercising the formula rather than the too-few-trades early return.
        Assert.True(rets.Count >= 5, $"fixture produced only {rets.Count} trades");
        Assert.Equal(rets.Count, mae.Count);
    }

    // The assertion that actually proves the term is live rather than merely plumbed: excursions
    // must be adverse (<= 0) and at least one must be strictly non-zero. An all-zero list would
    // leave the fitness term a no-op while looking perfectly wired.
    [Fact]
    public void Excursions_AreAdverseAndNotAllZero()
    {
        var (_, mae) = Run();
        Assert.All(mae, m => Assert.True(m <= 0.0, $"excursion {m} is not adverse for a short"));
        Assert.Contains(mae, m => m < -1e-9);
    }

    // And that FadeShort's REAL excursion values move the fold score down the path FadeShortGA
    // takes (CanonicalRegime, which also drops trades by regime sustain — so this exercises the
    // filter alignment too).
    //
    // The returns are synthetic and profitable on purpose. Canonical exits at `pf - 2.0` for any
    // fold with profit factor below 1.0, BEFORE the drawdown term is reached, so pairing the real
    // excursions with a losing fixture would compare two early-exit sentinels and prove nothing.
    // The excursions themselves are the real measured ones.
    [Fact]
    public void RealExcursions_LowerTheFoldScoreFadeShortGaWouldCompute()
    {
        var (_, mae) = Run();
        Assert.True(mae.Count >= 5);

        var profitable = Enumerable.Range(0, mae.Count)
                                   .Select(i => (Return: i % 3 == 0 ? -1.0 : 2.0, RegimeBars: 10))
                                   .ToList();

        double blind = FoldScoreHelper.CanonicalRegime(profitable, 0.05, 0, 5, new FitnessConfig());
        double aware = FoldScoreHelper.CanonicalRegime(profitable, 0.05, 0, 5, new FitnessConfig(),
                                                       maePct: mae);

        Assert.True(blind > 0, "fixture must clear the profit-factor gate for this to mean anything");
        Assert.True(aware < blind,
                    $"FadeShort's real excursions did not move the fold score: {aware:F6} vs {blind:F6}");
    }
}
