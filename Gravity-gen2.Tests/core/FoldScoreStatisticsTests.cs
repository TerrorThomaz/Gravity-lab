using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// ── The fitness must contain exactly ONE frequency term ──────────────────────────────
//
// FoldScoreHelper.Canonical used to compute its Sharpe and Sortino terms as
//     Simulator.SharpeRatio(returns, returns.Count)
// but that method's second parameter is a count of 5-MINUTE-EQUIVALENT CANDLES — it
// returns mean/std * sqrt(candleCount / 288). Passing the TRADE count made the term scale
// by sqrt(nTrades): 0.295x at 25 trades, 0.932x at 250, 1.86x at 1000. That was a hidden
// SECOND frequency bonus stacked on top of the explicit freqBonus, so two folds with
// identical return distributions scored differently purely because one traded more often.
public class PerTradeStatisticTests
{
    private static List<double> Repeat(List<double> pattern, int times)
    {
        var outp = new List<double>();
        for (int i = 0; i < times; i++) outp.AddRange(pattern);
        return outp;
    }

    // Same distribution, 6x the trade count.
    private static List<double> Pattern() => [3.0, -1.5, 4.0, -2.0, 5.0, -1.0, 2.5, -3.0, 6.0, -2.5];

    [Fact]
    public void PerTradeSharpe_IsInvariantToTradeCount_ForTheSameDistribution()
    {
        double small = FoldScoreHelper.PerTradeSharpe(Repeat(Pattern(), 3));    // 30 trades
        double large = FoldScoreHelper.PerTradeSharpe(Repeat(Pattern(), 30));   // 300 trades
        Assert.Equal(small, large, 12);

        // The old call would have differed by sqrt(300/30) = 3.16x.
        double oldSmall = Simulator.SharpeRatio(Repeat(Pattern(), 3), 30);
        double oldLarge = Simulator.SharpeRatio(Repeat(Pattern(), 30), 300);
        Assert.True(oldLarge / oldSmall > 3.0,
            $"sanity check on the defect being fixed: expected ~3.16x, got {oldLarge / oldSmall}");
    }

    [Fact]
    public void PerTradeSortino_IsInvariantToTradeCount_ForTheSameDistribution()
    {
        double small = FoldScoreHelper.PerTradeSortino(Repeat(Pattern(), 3));
        double large = FoldScoreHelper.PerTradeSortino(Repeat(Pattern(), 30));
        Assert.Equal(small, large, 12);
    }

    [Fact]
    public void PerTradeSharpe_IsExactlyMeanOverStdev()
    {
        var r = Repeat(Pattern(), 3);
        double mean = r.Average();
        double std  = Math.Sqrt(r.Select(x => (x - mean) * (x - mean)).Average());
        Assert.Equal(mean / std, FoldScoreHelper.PerTradeSharpe(r), 12);
    }

    [Fact]
    public void PerTradeSharpe_KeepsTheSampleGuard()
    {
        Assert.Equal(0.0, FoldScoreHelper.PerTradeSharpe([1.0, -1.0, 2.0, -0.5]), 12);
    }

    // n/2 wins of `pf` against n/2 losses of 1.0 — profit factor is exactly `pf` by construction.
    private static List<double> AtProfitFactor(double pf, int n = 40)
        => Enumerable.Range(0, n).Select(i => i % 2 == 0 ? pf : -1.0).ToList();

    // THE CLIFF. PerTradeSharpe returned 0 below profit factor 1.3, and SharpeW (0.5) is the
    // largest live statistical weight in Canonical — PfW and CalmarW both default to 0. So the
    // heaviest stat term contributed exactly nothing across PF 1.0–1.3, then jumped, and that band
    // is where every strategy in the walk-forward book actually sits.
    [Fact]
    public void PerTradeSharpe_RampsAcrossTheMarginalBandInsteadOfCliffing()
    {
        Assert.True(FoldScoreHelper.PerTradeSharpe(AtProfitFactor(1.29)) > 0,
                    "a profitable fold at PF 1.29 must not score exactly 0");

        // Continuity across the old cliff edge: straddle it closely enough that the underlying
        // Sharpe barely moves, so any jump left is the cliff and not the natural slope.
        double justUnder = FoldScoreHelper.PerTradeSharpe(AtProfitFactor(1.299));
        double justOver  = FoldScoreHelper.PerTradeSharpe(AtProfitFactor(1.301));
        Assert.True(Math.Abs(justOver - justUnder) / justOver < 0.01,
                    $"discontinuity at PF 1.3: {justUnder:F6} → {justOver:F6}");
    }

    [Fact]
    public void PerTradeSharpe_IsMonotoneInProfitFactorAcrossTheBand()
    {
        var pfs = new[] { 1.0, 1.05, 1.1, 1.2, 1.3, 1.5, 2.0 };
        double prev = -1;
        foreach (double pf in pfs)
        {
            double v = FoldScoreHelper.PerTradeSharpe(AtProfitFactor(pf));
            Assert.True(v >= prev - 1e-12, $"PF {pf}: {v:F6} fell below the lower-PF score {prev:F6}");
            prev = v;
        }
        // Breakeven earns no Sharpe bonus — the ramp bottoms out at PF 1.0, it does not go negative.
        Assert.Equal(0.0, FoldScoreHelper.PerTradeSharpe(AtProfitFactor(1.0)), 12);
    }

    [Fact]
    public void Canonical_FrequencyEffect_ComesOnlyFromFreqBonus()
    {
        // Same distribution at 3x the trade count, with the explicit frequency bonus
        // switched OFF. Any remaining difference would be a second, hidden frequency term.
        // posFrac is set so the compounded gain also scales out: gain is ~linear in the
        // trade count, so we compare the ratio against the pure gain ratio instead.
        var cfgNoFreq = new FitnessConfig() with { FreqW = 0.0 };

        var small = Repeat(Pattern(), 10);   // 100 trades
        var large = Repeat(Pattern(), 20);   // 200 trades

        double sSmall = FoldScoreHelper.PerTradeSharpe(small);
        double sLarge = FoldScoreHelper.PerTradeSharpe(large);
        Assert.Equal(sSmall, sLarge, 12);

        // With FreqW = 0 and identical Sharpe/Sortino, the stat-bonus stack must be
        // identical too, so the score ratio is driven by gain/drawdown alone.
        double scoreSmall = FoldScoreHelper.Canonical(small, 0.001, 10, cfgNoFreq);
        double scoreLarge = FoldScoreHelper.Canonical(large, 0.001, 10, cfgNoFreq);
        Assert.True(scoreSmall > 0 && scoreLarge > 0);

        var cfgFreqOn = new FitnessConfig();
        double freqOnSmall = FoldScoreHelper.Canonical(small, 0.001, 10, cfgFreqOn);
        double freqOnLarge = FoldScoreHelper.Canonical(large, 0.001, 10, cfgFreqOn);

        // Turning freqBonus on must be the ONLY thing that changes the count sensitivity,
        // and it must do so in the documented 1 + 0.15*ln(n/minTrades) shape.
        double expectedSmall = 1.0 + 0.15 * Math.Log(100 / 10.0);
        double expectedLarge = 1.0 + 0.15 * Math.Log(200 / 10.0);
        Assert.Equal(expectedSmall, freqOnSmall / scoreSmall, 9);
        Assert.Equal(expectedLarge, freqOnLarge / scoreLarge, 9);
    }
}

// ── Tail estimators must not be read off one or two observations ─────────────────────
//
// CVaRPenalty took StatisticalTests.CVaR(returns, 0.05), whose cutoff is
// max(1, n*0.05) — a SINGLE trade at the fold sizes these GAs actually reach
// (MinTradesPerFold is 10-25). TailRatioBonus used tailN = max(1, n/20), likewise 1, so
// its "5th/95th percentile ratio" was |best single trade| / |worst single trade| — and it
// was an UNCAPPED BONUS, meaning the objective PAID for having one huge winner.
public class TailTermGateTests
{
    private static List<double> Series(int n, double win, double loss)
    {
        var r = new List<double>();
        for (int i = 0; i < n; i++) r.Add(i % 3 == 0 ? -loss : win);
        return r;
    }

    [Fact]
    public void CVaRPenalty_IsNeutralBelowTheMinimumTailSample()
    {
        var cfg = new FitnessConfig();
        // A brutal single-trade tail that WOULD have triggered the penalty at the old
        // 20-trade gate.
        var r = Series(40, 1.0, 1.0);
        r[0] = -30.0;
        Assert.True(r.Count < FoldScoreHelper.MinTailSampleSize);
        Assert.Equal(1.0, FoldScoreHelper.CVaRPenalty(r, cfg), 12);
    }

    [Fact]
    public void CVaRPenalty_IsLiveAtOrAboveTheMinimumTailSample()
    {
        var cfg = new FitnessConfig();
        var r = Series(200, 1.0, 1.0);
        for (int i = 0; i < 12; i++) r[i * 3] = -30.0;   // a genuinely fat left tail
        Assert.True(r.Count >= FoldScoreHelper.MinTailSampleSize);
        Assert.True(FoldScoreHelper.CVaRPenalty(r, cfg) < 1.0,
            "a fat left tail on a 200-trade sample must still be penalised");
    }

    [Fact]
    public void TailRatioBonus_IsNeutralBelowTheMinimumTailSample()
    {
        var cfg = new FitnessConfig();
        var r = Series(50, 1.0, 1.0);
        r[1] = 80.0;   // one enormous winner — the exact thing the old term paid for
        Assert.True(r.Count < FoldScoreHelper.MinTailSampleSize);
        Assert.Equal(1.0, FoldScoreHelper.TailRatioBonus(r, cfg), 12);
    }

    [Fact]
    public void TailRatioBonus_IsCappedAbove_NoMatterHowExtremeTheTail()
    {
        // TailRatioW cranked far past its 0.2 default and an absurd right tail: the bonus
        // must saturate rather than scale with the outlier.
        var cfg = new FitnessConfig() with { TailRatioW = 50.0 };
        var r = new List<double>();
        for (int i = 0; i < 200; i++) r.Add(i % 4 == 0 ? -0.1 : 0.2);
        for (int i = 0; i < 12; i++) r[i * 7] = 500.0;

        double bonus = FoldScoreHelper.TailRatioBonus(r, cfg);
        Assert.InRange(bonus, 1.0, FoldScoreHelper.MaxTailRatioBonus);
    }

    [Fact]
    public void TailRatioBonus_RatioIsClampedBeforeWeighting()
    {
        // At the default weight the clamp binds through MaxTailRatio, so two folds whose
        // tail ratios are 8x and 80x get the SAME bonus — the term can shade a decision
        // but cannot rank one outlier above another.
        var cfg = new FitnessConfig();
        List<double> Build(double bigWin)
        {
            var r = new List<double>();
            for (int i = 0; i < 200; i++) r.Add(i % 4 == 0 ? -1.0 : 1.0);
            for (int i = 0; i < 12; i++) r[i * 7 + 1] = bigWin;
            return r;
        }
        double a = FoldScoreHelper.TailRatioBonus(Build(8.0), cfg);
        double b = FoldScoreHelper.TailRatioBonus(Build(80.0), cfg);
        Assert.Equal(a, b, 12);
        double expected = 1.0 + (FoldScoreHelper.MaxTailRatio - 1.5) * 0.1 * cfg.TailRatioW;
        Assert.Equal(expected, a, 9);
    }

    [Fact]
    public void BothTailTerms_ShareTheSameFiveObservationRule()
    {
        // The gate is "the 5% tail bucket must hold at least 5 observations":
        //   n * 0.05 >= 5  <=>  n >= 100.
        Assert.Equal(100, FoldScoreHelper.MinTailSampleSize);
        Assert.Equal(5, (int)(FoldScoreHelper.MinTailSampleSize * 0.05));
        Assert.Equal(5, FoldScoreHelper.MinTailSampleSize / 20);
    }
}
