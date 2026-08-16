using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class PredictiveDistributionTests
{
    private static Candle[] MakeSeries(Random rng, int n, double driftPerBarPct, double volPct)
    {
        var candles = new Candle[n];
        double price = 100.0;
        for (int i = 0; i < n; i++)
        {
            double ret = driftPerBarPct / 100.0 + (rng.NextDouble() * 2 - 1) * volPct / 100.0;
            double o = price;
            price = price * (1 + ret / 100.0);
            candles[i] = new Candle(
                DateTime.UtcNow.AddHours(-(n - i)),
                o,
                Math.Max(o, price) * 1.001,
                Math.Min(o, price) * 0.999,
                price,
                1e6);
        }
        return candles;
    }

    [Fact]
    public void Neutral_WhenTooShort()
    {
        var rng = new Random(42);
        var series = MakeSeries(rng, PredictiveDistribution.Warmup + PredictiveDistribution.MinSamples - 1, 0, 1.0);

        var dist = PredictiveDistribution.Predict(series);

        Assert.True(dist.IsNeutral);
        Assert.Equal(0, dist.SampleSize);
    }

    [Fact]
    public void Predict_OnLongRandomWalk_ProducesNonNeutral()
    {
        var rng = new Random(123);
        var series = MakeSeries(rng, 3500, 0, 1.5);

        var dist = PredictiveDistribution.Predict(series);

        Assert.False(dist.IsNeutral);
        Assert.True(dist.SampleSize > 0);
        Assert.InRange(dist.ProbPositive, 0.4, 0.6);
    }

    [Fact]
    public void KeyInvariants_Hold()
    {
        var rng = new Random(77);
        var series = MakeSeries(rng, 3500, 0.01, 1.5);

        var dist = PredictiveDistribution.Predict(series);
        Assert.False(dist.IsNeutral, "expected non-neutral distribution on 3500-bar series");

        Assert.InRange(dist.ProbPositive, 0.0, 1.0);
        Assert.True(dist.StdPct >= 0, $"StdPct must be >= 0, got {dist.StdPct}");
        Assert.True(dist.CVaR5Pct <= 0.01, $"CVaR5Pct should be negative or near zero, got {dist.CVaR5Pct}");
        Assert.True(dist.SampleSize >= PredictiveDistribution.MinSamples,
            $"SampleSize {dist.SampleSize} must be >= MinSamples {PredictiveDistribution.MinSamples}");
    }

    [Fact]
    public void ComputeValidated_ReturnsBars_WithReasonableRealized()
    {
        var rng = new Random(999);
        var series = MakeSeries(rng, 3500, 0, 1.5);

        var bars = PredictiveDistribution.ComputeValidated(series);

        Assert.True(bars.Count > 1000, $"expected >1000 validated bars, got {bars.Count}");

        foreach (var b in bars)
        {
            Assert.True(Enum.IsDefined(typeof(MarketRegime), (int)b.Regime),
                $"Regime {(int)b.Regime} is not a valid MarketRegime value");
            Assert.True(double.IsFinite(b.RealizedPct), $"RealizedPct must be finite, got {b.RealizedPct}");
            Assert.True(Math.Abs(b.RealizedPct) < 15,
                $"|RealizedPct| {Math.Abs(b.RealizedPct):F2} exceeds 15%");
        }

        double meanPredicted = bars.Average(b => b.MeanPct);
        double meanRealized = bars.Average(b => b.RealizedPct);
        Assert.True(Math.Abs(meanPredicted - meanRealized) < 5.0,
            $"mean predicted ({meanPredicted:F4}) and mean realized ({meanRealized:F4}) diverge by more than 5%");
    }

    [Fact]
    public void WarmupBars_AreSkipped()
    {
        var rng = new Random(55);
        int n = 3500;
        var series = MakeSeries(rng, n, 0, 1.5);

        var bars = PredictiveDistribution.ComputeValidated(series);
        Assert.NotEmpty(bars);

        var firstPredictionTime = bars[0].Time;
        int candlesBeforeFirst = series.Count(c => c.Time < firstPredictionTime);

        Assert.True(candlesBeforeFirst >= PredictiveDistribution.Warmup,
            $"first prediction at {firstPredictionTime} has only {candlesBeforeFirst} candles before it, " +
            $"expected >= {PredictiveDistribution.Warmup}");
    }

    [Fact]
    public void PreparedQuery_MatchesSinglePredict()
    {
        var rng = new Random(2024);
        var series = MakeSeries(rng, 3500, 0.01, 1.5);
        DateTime last = series[^1].Time;

        var viaPredict    = PredictiveDistribution.Predict(series);
        var viaPredictAt  = PredictiveDistribution.PredictAt(series, last);
        var prepared      = PredictiveDistribution.Prepare(series);
        var viaPrepared   = prepared.Query(last);

        Assert.False(viaPredict.IsNeutral);
        Assert.False(viaPrepared.IsNeutral);

        // All three routes to a distribution at the trailing bar should agree closely.
        Assert.True(Math.Abs(viaPredict.ProbPositive - viaPrepared.ProbPositive) < 1e-9,
            $"Predict.ProbPositive {viaPredict.ProbPositive} != Prepared.Query {viaPrepared.ProbPositive}");
        Assert.True(Math.Abs(viaPredictAt.ProbPositive - viaPrepared.ProbPositive) < 1e-9,
            $"PredictAt.ProbPositive {viaPredictAt.ProbPositive} != Prepared.Query {viaPrepared.ProbPositive}");

        // Querying a mid-series time must not throw and must respect warmup (returns neutral early).
        var early = prepared.Query(series[PredictiveDistribution.Warmup - 10].Time);
        Assert.True(early.IsNeutral, "query before warmup should be neutral");
    }
}
