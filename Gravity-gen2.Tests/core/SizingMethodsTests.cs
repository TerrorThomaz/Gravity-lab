using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class SizingMethodsTests
{
    // ── Fractional Kelly ──

    [Fact]
    public void FractionalKelly_ShrinksWithSampleSize()
    {
        // With few trades the shrinkage factor < 1: adjusted Kelly < full Kelly.
        var small = SizingMethods.Compute(10, fullKelly: 0.3);
        var large = SizingMethods.Compute(1000, fullKelly: 0.3);

        Assert.True(small.AdjustedKelly < 0.3, "small sample must shrink Kelly below full");
        Assert.True(small.AdjustedKelly < large.AdjustedKelly, "more data must shrink less");
        // As n → ∞ it approaches full Kelly.
        Assert.InRange(large.AdjustedKelly, 0.29, 0.3);
        Assert.Equal(0.3, large.FullKelly, 6);
    }

    [Fact]
    public void FractionalKelly_ZeroForTinyOrNonPositive()
    {
        Assert.Equal(0, SizingMethods.Compute(1, fullKelly: 0.5).AdjustedKelly);
        Assert.Equal(0, SizingMethods.Compute(50, fullKelly: 0).AdjustedKelly);
    }

    // ── Volatility-targeted per-strategy weights ──

    [Fact]
    public void VolTarget_DownweightsHigherVolStrategy()
    {
        // Two strategies with identical gross return but different realized vol: the high-vol one
        // must get a smaller weight so its vol contribution normalises.
        var trades = new List<(string Strategy, double Return)>();
        foreach (var _ in Enumerable.Range(0, 100))
        {
            trades.Add(("low", 0.5));     // ~zero-vol constant winner
            trades.Add(("high", 4.0));    // swings ±4
            trades.Add(("high", -3.0));
        }

        var w = SizingMethods.VolTargetStrategyWeights(trades, targetVolPct: 1.5, volFloorPct: 0.5, volCapPct: 8.0, meanNormalise: false)!;
        double wHigh = w("high"), wLow = w("low");

        Assert.True(wHigh < wLow, $"expected high-vol strategy weight {wHigh} < low-vol {wLow}");
        // Vol-target sizes ∝ 1/vol: high vol ⇒ weight below the low-vol one.
        Assert.True(wLow > 1.0, $"low-vol weight should be raised above 1, got {wLow}");
    }

    [Fact]
    public void VolTarget_MeanNormalisedPreservesGrossExposure()
    {
        var trades = new List<(string Strategy, double Return)>();
        foreach (var _ in Enumerable.Range(0, 100))
        {
            trades.Add(("a", 1.0));
            trades.Add(("a", -1.0));
            trades.Add(("b", 0.2));
            trades.Add(("b", -0.2));
        }

        // Normalised variant → weights average to 1.0 (redistribution only).
        var w = SizingMethods.VolTargetStrategyWeights(trades, targetVolPct: 1.5, volFloorPct: 0.1, volCapPct: 8.0, meanNormalise: true)!;
        Assert.True(Math.Abs((w("a") + w("b")) / 2.0 - 1.0) < 1e-9,
            $"mean-normalised weights must average to 1.0, got {(w("a") + w("b")) / 2.0}");
    }

    [Fact]
    public void VolTarget_UnknownStrategy_Neutral()
    {
        var trades = new List<(string Strategy, double Return)> { ("a", 1.0), ("a", -1.0), ("a", 0.5) };
        var w = SizingMethods.VolTargetStrategyWeights(trades, meanNormalise: false)!;
        Assert.Equal(1.0, w("unknown"));
    }

    [Fact]
    public void VolTarget_VolClampedToBand()
    {
        // Strategy with absurdly low realized vol must not blow up to a huge weight — clamp at floor.
        var trades = new List<(string Strategy, double Return)>();
        for (int i = 0; i < 50; i++) trades.Add(("quiet", 1e-6));  // ~zero vol
        trades.Add(("quiet", -1e-6));

        var w = SizingMethods.VolTargetStrategyWeights(trades, targetVolPct: 1.5, volFloorPct: 0.5, volCapPct: 8.0, meanNormalise: false)!;
        // targetVolPct / volFloorPct = 3.0.
        Assert.Equal(3.0, w("quiet"), 6);
    }
}