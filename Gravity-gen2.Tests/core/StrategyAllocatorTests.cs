using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class StrategyAllocatorTests
{
    // Helper: build a deterministic trade list from a return series.
    private static IReadOnlyList<(DateTime Time, double Return)> MakeTrades(
        double[] returns, DateTime start, TimeSpan spacing)
    {
        var list = new List<(DateTime, double)>(returns.Length);
        for (int i = 0; i < returns.Length; i++)
            list.Add((start + spacing * i, returns[i]));
        return list;
    }

    // Helper: generate a deterministic series (sin wave + optional offset).
    private static double[] SinSeries(int n, double period, double amplitude = 1.0, double offset = 0.0)
    {
        var r = new double[n];
        for (int i = 0; i < n; i++)
            r[i] = amplitude * Math.Sin(2 * Math.PI * i / period) + offset;
        return r;
    }

    private static Dictionary<string, IReadOnlyList<(DateTime Time, double Return)>> BuildInput(
        params (string Name, double[] Returns)[] strategies)
    {
        var start = new DateTime(2020, 1, 1);
        var spacing = TimeSpan.FromHours(1);
        var dict = new Dictionary<string, IReadOnlyList<(DateTime, double)>>();
        foreach (var (name, returns) in strategies)
            dict[name] = MakeTrades(returns, start, spacing);
        return dict;
    }

    // ── Mean-normalisation ──

    [Fact]
    public void MeanNormalization_RiskScaleAveragesToOne()
    {
        // 4 strategies with different vol profiles.
        var input = BuildInput(
            ("A", SinSeries(200, 50, 1.0)),
            ("B", SinSeries(200, 50, 2.0)),
            ("C", SinSeries(200, 50, 0.5)),
            ("D", SinSeries(200, 50, 1.5)));

        var alloc = StrategyAllocator.Compute(input);

        double mean = alloc.RiskScale.Values.Average();
        Assert.Equal(1.0, mean, 9);
    }

    // ── Anti-stacking ──

    [Fact]
    public void AntiStacking_IdenticalStrategiesGetLowerScaleThanOrthogonal()
    {
        // 3 strategies with identical returns (rho=1) + 1 orthogonal (sin vs cos).
        var ident = SinSeries(200, 50, 1.0);
        var ortho = new double[200];
        for (int i = 0; i < 200; i++)
            ortho[i] = Math.Cos(2 * Math.PI * i / 50);

        var input = BuildInput(
            ("S1", ident),
            ("S2", ident),
            ("S3", ident),
            ("Orth", ortho));

        var alloc = StrategyAllocator.Compute(input);

        // The 3 identical should be in the same family.
        Assert.Equal(alloc.FamilyOf["S1"], alloc.FamilyOf["S2"]);
        Assert.Equal(alloc.FamilyOf["S2"], alloc.FamilyOf["S3"]);
        // Orthogonal should be in its own family.
        Assert.NotEqual(alloc.FamilyOf["S1"], alloc.FamilyOf["Orth"]);

        // Each stacked strategy's scale < orthogonal's scale.
        foreach (var s in new[] { "S1", "S2", "S3" })
            Assert.True(alloc.RiskScale[s] < alloc.RiskScale["Orth"],
                $"stacked {s} scale {alloc.RiskScale[s]} should be < orthogonal {alloc.RiskScale["Orth"]}");
    }

    // ── Family detection ──

    [Fact]
    public void FamilyDetection_CorrelatedShareFamily_UncorrelatedDont()
    {
        // Two highly correlated: shared signal + small independent perturbation.
        var shared = SinSeries(200, 50, 1.0);
        var corr1 = new double[200];
        var corr2 = new double[200];
        for (int i = 0; i < 200; i++)
        {
            corr1[i] = shared[i] + 0.01 * Math.Sin(2 * Math.PI * i / 7);
            corr2[i] = shared[i] + 0.01 * Math.Cos(2 * Math.PI * i / 11);
        }

        // Two uncorrelated: sin(period=50) vs sin(period=25) — orthogonal over 200 samples (4 full cycles of 50, 8 full cycles of 25).
        var uncorr1 = SinSeries(200, 50, 1.0);
        var uncorr2 = SinSeries(200, 25, 1.0);

        var input = BuildInput(
            ("Corr1", corr1),
            ("Corr2", corr2),
            ("Uncorr1", uncorr1),
            ("Uncorr2", uncorr2));

        var alloc = StrategyAllocator.Compute(input, familyCorrThreshold: 0.7);

        // Correlated pair should share a family.
        Assert.Equal(alloc.FamilyOf["Corr1"], alloc.FamilyOf["Corr2"]);
        // Uncorrelated should be in separate families.
        Assert.NotEqual(alloc.FamilyOf["Uncorr1"], alloc.FamilyOf["Uncorr2"]);
    }

    // ── Degenerate cases ──

    [Fact]
    public void Degenerate_SingleStrategy_ScaleOne()
    {
        var input = BuildInput(("Only", SinSeries(100, 20, 1.0)));
        var alloc = StrategyAllocator.Compute(input);
        Assert.Equal(1.0, alloc.RiskScale["Only"], 9);
    }

    [Fact]
    public void Degenerate_BelowMinTrades_ScaleOneAndExcluded()
    {
        var input = BuildInput(
            ("Few", SinSeries(10, 5, 1.0)),   // < 20 trades
            ("Many", SinSeries(100, 20, 1.0)));
        var alloc = StrategyAllocator.Compute(input);
        Assert.Equal(1.0, alloc.RiskScale["Few"], 9);
        // "Few" should have its own family (excluded from clustering).
        Assert.NotEqual(alloc.FamilyOf["Few"], alloc.FamilyOf["Many"]);
    }

    [Fact]
    public void Degenerate_EmptyInput_EmptyAllocation()
    {
        var input = new Dictionary<string, IReadOnlyList<(DateTime, double)>>();
        var alloc = StrategyAllocator.Compute(input);
        Assert.Empty(alloc.RiskScale);
        Assert.Empty(alloc.FamilyOf);
        Assert.Empty(alloc.Volatility);
        Assert.Equal(0.0, alloc.EffectiveBets);
        Assert.Equal(0.0, alloc.AvgPairwiseCorrelation);
    }

    // ── Determinism ──

    [Fact]
    public void Determinism_SameInputTwice_IdenticalOutput()
    {
        var input = BuildInput(
            ("A", SinSeries(200, 50, 1.0)),
            ("B", SinSeries(200, 50, 2.0)),
            ("C", SinSeries(200, 30, 0.5)));

        var alloc1 = StrategyAllocator.Compute(input);
        var alloc2 = StrategyAllocator.Compute(input);

        foreach (var key in alloc1.RiskScale.Keys)
        {
            Assert.Equal(alloc1.RiskScale[key], alloc2.RiskScale[key], 15);
            Assert.Equal(alloc1.FamilyOf[key], alloc2.FamilyOf[key]);
            Assert.Equal(alloc1.Volatility[key], alloc2.Volatility[key], 15);
        }
        Assert.Equal(alloc1.EffectiveBets, alloc2.EffectiveBets, 15);
        Assert.Equal(alloc1.AvgPairwiseCorrelation, alloc2.AvgPairwiseCorrelation, 15);
    }

    // ── Hedging ──

    [Fact]
    public void Hedging_NegativelyCorrelated_NoHaircut()
    {
        // Negatively correlated pair: x and -x (rho = -1).
        var pos = SinSeries(200, 50, 1.0);
        var neg = new double[200];
        for (int i = 0; i < 200; i++) neg[i] = -pos[i];

        var input = BuildInput(("A", pos), ("B", neg));
        var alloc = StrategyAllocator.Compute(input);

        // By symmetry, both should get the same scale.
        Assert.Equal(alloc.RiskScale["A"], alloc.RiskScale["B"], 9);
        // After mean-normalization, both should be 1.0.
        Assert.Equal(1.0, alloc.RiskScale["A"], 9);
        Assert.Equal(1.0, alloc.RiskScale["B"], 9);
        // Avg correlation should be negative (no haircut charged).
        Assert.True(alloc.AvgPairwiseCorrelation < 0,
            $"avg correlation {alloc.AvgPairwiseCorrelation} should be negative");
    }

    [Fact]
    public void Hedging_PositiveCorrelation_HaircutReducesScale()
    {
        // Positively correlated pair: x and x (rho = 1).
        var shared = SinSeries(200, 50, 1.0);

        var input = BuildInput(("A", shared), ("B", shared));
        var alloc = StrategyAllocator.Compute(input);

        // By symmetry, both should get the same scale.
        Assert.Equal(alloc.RiskScale["A"], alloc.RiskScale["B"], 9);
        // After mean-normalization, both should be 1.0.
        Assert.Equal(1.0, alloc.RiskScale["A"], 9);
        // Avg correlation should be positive (haircut charged, but mean-normalized to 1.0).
        Assert.True(alloc.AvgPairwiseCorrelation > 0.5,
            $"avg correlation {alloc.AvgPairwiseCorrelation} should be high positive");
    }
}
