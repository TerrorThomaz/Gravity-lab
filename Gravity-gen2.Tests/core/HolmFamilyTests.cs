using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Regression suite for the multiple-testing family that CombinedBacktest hands to
// Holm-Bonferroni: how finely the Monte Carlo p-values are resolved, whether each
// strategy's p-value is reproducible independently of family membership, who gets
// excluded from the family, and the Holm arithmetic that consumes the vector.
public class HolmFamilyTests
{
    // The strictest step Holm can apply to a family of six strategies at α = 0.05.
    private const double StrictestHolmThreshold = 0.05 / 6.0;

    // ── Resolution of the p-value lattice ────────────────────────────────────────────────

    // MonteCarloTest.Run returns the Davison-Hinkley estimator (count+1)/(N+1), a lattice with
    // step and floor 1/(N+1). CombinedBacktest used to pass permutations: 1000, giving a floor
    // of 0.000999 — only ~8 attainable values below 0.008333, so the single most consequential
    // cell in the table was decided at about one significant figure.
    [Fact]
    public void FamilyWiseResampleCount_ResolvesTheStrictestHolmThreshold()
    {
        double minAttainableP = 1.0 / (MonteCarloTest.FamilyWiseResamples + 1.0);
        double steps = StrictestHolmThreshold / minAttainableP;
        Assert.True(steps >= 100,
            $"only {steps:F1} attainable p-steps below {StrictestHolmThreshold:F5} at " +
            $"{MonteCarloTest.FamilyWiseResamples} resamples");
    }

    // Resolution alone is not enough: the ESTIMATE's own sampling noise must also be small
    // against the threshold, otherwise two runs of the same trade set straddle the boundary.
    [Fact]
    public void FamilyWiseResampleCount_KeepsBootstrapNoiseWellBelowTheStrictestThreshold()
    {
        double p  = StrictestHolmThreshold;
        double se = Math.Sqrt(p * (1 - p) / MonteCarloTest.FamilyWiseResamples);
        Assert.True(se < 0.10 * p,
            $"se(p̂)={se:F6} is {se / p:P1} of the {p:F5} threshold at " +
            $"{MonteCarloTest.FamilyWiseResamples} resamples");
    }

    // ── Per-strategy seeding ─────────────────────────────────────────────────────────────

    [Fact]
    public void SeedForStrategy_IsStableAndDistinctPerName()
    {
        Assert.Equal(MonteCarloTest.SeedForStrategy("DipLong"), MonteCarloTest.SeedForStrategy("DipLong"));
        var names = new[] { "FadeShort", "Grid", "FadeLong", "DipLong", "SwingLong", "RipShort" };
        var seeds = names.Select(MonteCarloTest.SeedForStrategy).ToArray();
        Assert.Equal(names.Length, seeds.Distinct().Count());
        Assert.All(seeds, s => Assert.True(s >= 0));
    }

    // THE POINT OF THE FIX. Two families that differ in membership must still hand identical
    // p-values to Holm for the strategies they share. Under the old shared Random(42), dropping
    // "Grid" from the middle of the list shifted every later strategy's RNG stream and therefore
    // its p-value, with no change to any trade set.
    [Fact]
    public void FamilyPValue_DependsOnlyOnItsOwnTrades_NotOnWhichOtherStrategiesWereIncluded()
    {
        var fadeShort = Strategy("FadeShort", seed: 1, coins: 4, perCoin: 40, drift: +0.35);
        var grid      = Strategy("Grid",      seed: 2, coins: 3, perCoin: 30, drift: +0.05);
        var dipLong   = Strategy("DipLong",   seed: 3, coins: 5, perCoin: 25, drift: +0.60);

        var withGrid    = CombinedBacktest.BuildMonteCarloFamily(new[] { fadeShort, grid, dipLong }).Family;
        var withoutGrid = CombinedBacktest.BuildMonteCarloFamily(new[] { fadeShort, dipLong }).Family;

        Assert.Equal(3, withGrid.Count);
        Assert.Equal(2, withoutGrid.Count);
        Assert.Equal(PValueOf(withGrid, "FadeShort"), PValueOf(withoutGrid, "FadeShort"), 12);
        Assert.Equal(PValueOf(withGrid, "DipLong"),   PValueOf(withoutGrid, "DipLong"),   12);
    }

    // Order within the family must not matter either — the printed table is sorted by a fixed
    // strategy order, but the p-value must not be a function of that order.
    [Fact]
    public void FamilyPValue_IsInvariantToPositionInTheList()
    {
        var a = Strategy("FadeShort", seed: 5, coins: 3, perCoin: 30, drift: +0.30);
        var b = Strategy("RipShort",  seed: 6, coins: 3, perCoin: 30, drift: +0.10);

        var forward  = CombinedBacktest.BuildMonteCarloFamily(new[] { a, b }).Family;
        var reversed = CombinedBacktest.BuildMonteCarloFamily(new[] { b, a }).Family;

        Assert.Equal(PValueOf(forward, "FadeShort"), PValueOf(reversed, "FadeShort"), 12);
        Assert.Equal(PValueOf(forward, "RipShort"),  PValueOf(reversed, "RipShort"),  12);
    }

    [Fact]
    public void FamilyPValue_IsReproducibleAcrossRepeatedCalls()
    {
        var inputs = new[] { Strategy("SwingLong", seed: 9, coins: 4, perCoin: 20, drift: +0.40) };
        Assert.Equal(CombinedBacktest.BuildMonteCarloFamily(inputs).Family[0].PValue,
                     CombinedBacktest.BuildMonteCarloFamily(inputs).Family[0].PValue, 12);
    }

    // ── Family membership vs. the DSR/WRC print criterion ────────────────────────────────

    // A strategy that clears the coin minimum (so PrintReport gives it a DSR/WRC verdict) but
    // not the trade minimum must be reported as excluded, not silently dropped: its absence
    // shrinks m and weakens the correction for everyone still in the family.
    [Fact]
    public void ThinStrategyIsExcludedWithAReason_NotSilentlyDropped()
    {
        var healthy = Strategy("FadeShort", seed: 11, coins: 3, perCoin: 30, drift: +0.30);
        var thin    = Strategy("Grid",      seed: 12, coins: 3, perCoin: 2,  drift: +0.30); // 6 trades
        var oneCoin = Strategy("RipShort",  seed: 13, coins: 1, perCoin: 50, drift: +0.30);

        var (family, excluded) = CombinedBacktest.BuildMonteCarloFamily(new[] { healthy, thin, oneCoin });

        Assert.Single(family);
        Assert.Equal("FadeShort", family[0].Name);
        Assert.Equal(2, excluded.Count);
        Assert.Contains(excluded, e => e.Name == "Grid"     && e.Reason.Contains("6 trades"));
        Assert.Contains(excluded, e => e.Name == "RipShort" && e.Reason.Contains("coin"));
    }

    [Fact]
    public void FamilyAndExclusions_PartitionTheInputs()
    {
        var inputs = new[]
        {
            Strategy("FadeShort", seed: 21, coins: 3, perCoin: 30, drift: +0.30),
            Strategy("Grid",      seed: 22, coins: 3, perCoin: 1,  drift: +0.30),
            Strategy("DipLong",   seed: 23, coins: 0, perCoin: 0,  drift: +0.30),
        };
        var (family, excluded) = CombinedBacktest.BuildMonteCarloFamily(inputs);
        Assert.Equal(inputs.Length, family.Count + excluded.Count);
        Assert.Empty(family.Select(f => f.Name).Intersect(excluded.Select(e => e.Name)));
    }

    // ── Holm arithmetic on the resulting vector ──────────────────────────────────────────

    [Fact]
    public void Holm_MatchesTheTextbookWorkedExample()
    {
        double[] p = [0.01, 0.04, 0.03, 0.005];
        Assert.Equal([true, false, false, true], StatisticalTests.HolmBonferroni(p, 0.05));

        double[] adj = StatisticalTests.HolmBonferroniAdjustedPValues(p);
        Assert.Equal(0.03, adj[0], 10);
        Assert.Equal(0.06, adj[1], 10);
        Assert.Equal(0.06, adj[2], 10);
        Assert.Equal(0.02, adj[3], 10);
    }

    [Fact]
    public void Holm_TiedRawPValuesGetIdenticalAdjustedValuesAndVerdicts()
    {
        double[] p = [0.02, 0.02, 0.9];
        double[] adj = StatisticalTests.HolmBonferroniAdjustedPValues(p);
        Assert.Equal(adj[0], adj[1], 12);
        bool[] r = StatisticalTests.HolmBonferroni(p, 0.05);
        Assert.Equal(r[0], r[1]);
    }

    // The step-down rejection routine and the adjusted-p-value routine are separate
    // implementations; the printed table shows one and colours it by the other, so they must
    // agree on every vector: rejected[i] ⇔ adjusted[i] ≤ α.
    [Fact]
    public void Holm_RejectionAgreesWithAdjustedPValues()
    {
        var rng = new Random(7);
        for (int trial = 0; trial < 2000; trial++)
        {
            int m = 1 + rng.Next(8);
            var p = new double[m];
            for (int i = 0; i < m; i++) p[i] = Math.Round(rng.NextDouble(), 3);
            bool[] rej = StatisticalTests.HolmBonferroni(p, 0.05);
            double[] adj = StatisticalTests.HolmBonferroniAdjustedPValues(p);
            for (int i = 0; i < m; i++)
                Assert.Equal(rej[i], adj[i] <= 0.05);
        }
    }

    [Fact]
    public void Holm_AdjustedAreMonotoneInRawAndNeverSmallerThanRaw()
    {
        var rng = new Random(11);
        for (int trial = 0; trial < 2000; trial++)
        {
            int m = 2 + rng.Next(7);
            var p = new double[m];
            for (int i = 0; i < m; i++) p[i] = rng.NextDouble();
            double[] adj = StatisticalTests.HolmBonferroniAdjustedPValues(p);
            for (int i = 0; i < m; i++) Assert.True(adj[i] >= p[i] - 1e-12);
            for (int i = 0; i < m; i++)
                for (int j = 0; j < m; j++)
                    if (p[i] < p[j]) Assert.True(adj[i] <= adj[j] + 1e-12);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────

    private static double PValueOf(List<(string Name, double PValue)> family, string name) =>
        family.Single(f => f.Name == name).PValue;

    // Synthetic per-coin return lists with a controllable positive drift, shaped like the
    // (Name, PerCoin, GaTrials) rows CombinedBacktest builds.
    private static (string, List<(string Label, List<double> Returns)>, int) Strategy(
        string name, int seed, int coins, int perCoin, double drift)
    {
        var rng = new Random(seed);
        var byCoin = new List<(string Label, List<double> Returns)>();
        for (int c = 0; c < coins; c++)
        {
            var rets = new List<double>();
            for (int i = 0; i < perCoin; i++) rets.Add(drift + (rng.NextDouble() * 4.0 - 2.0));
            byCoin.Add(($"COIN{c}", rets));
        }
        return (name, byCoin, 10_000);
    }
}
