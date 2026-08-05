using System.Collections;
using System.Reflection;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// FadeShort, Grid and GridShort used to cut their walk-forward folds on `minLen` — the
// length of the SHORTEST coin in the training set — and then apply those raw indices to
// every coin's array. Two things were wrong with that:
//
//   1. index i is a different calendar date on every coin, so "fold 3" was a different
//      stretch of market history per symbol;
//   2. every bar past minLen on every longer coin was silently discarded — with a 1000-bar
//      coin next to a 5000-bar coin, 80% of the long coin never entered the fitness at all.
//
// All three now slice each coin's OWN array via FoldScoreHelper.PerCoinFoldRange, which is
// alignment-safe by construction. These tests pin that behaviour.
public class PerCoinFoldAlignmentTests
{
    private const int    Folds       = 5;
    private const double EmbargoPct  = 0.05;

    private static (int Start, int End)[] AllFolds(int totalBars) =>
        Enumerable.Range(0, Folds)
            .Select(f => FoldScoreHelper.PerCoinFoldRange(totalBars, Folds, f, EmbargoPct))
            .ToArray();

    [Fact]
    public void PerCoinFolds_CoverEachCoinsOwnHistoryToTheLastBar()
    {
        foreach (int totalBars in new[] { 300, 1000, 5000, 12_345 })
        {
            var folds = AllFolds(totalBars);
            Assert.Equal(0, folds[0].Start);
            Assert.Equal(totalBars, folds[^1].End);

            int covered = folds.Sum(r => r.End - r.Start);
            Assert.True(covered >= (int)(totalBars * 0.9),
                $"{totalBars} bars: folds only covered {covered} bars — more than the embargo gaps were dropped");
        }
    }

    [Fact]
    public void PerCoinFolds_AreOrderedAndNonOverlapping_WithAnEmbargoGap()
    {
        var folds = AllFolds(5000);
        for (int f = 1; f < folds.Length; f++)
        {
            Assert.True(folds[f].Start > folds[f - 1].End,
                $"fold {f} starts at {folds[f].Start} but fold {f - 1} ends at {folds[f - 1].End} — no embargo gap");
        }
        foreach (var (start, end) in folds) Assert.True(end > start);
    }

    // The headline regression: two coins with very different history lengths must each get
    // folds sized from their OWN array, not both truncated to the shorter one.
    [Fact]
    public void PerCoinFolds_AreProportionalToEachCoinsOwnLength()
    {
        const int shortLen = 1_000;
        const int longLen  = 5_000;   // 5x the history of the short coin

        var shortFolds = AllFolds(shortLen);
        var longFolds  = AllFolds(longLen);

        // Both coins are fully covered — the long coin's tail is NOT thrown away.
        Assert.Equal(shortLen, shortFolds[^1].End);
        Assert.Equal(longLen,  longFolds[^1].End);

        for (int f = 0; f < Folds; f++)
        {
            int shortSpan = shortFolds[f].End - shortFolds[f].Start;
            int longSpan  = longFolds[f].End  - longFolds[f].Start;
            double ratio  = (double)longSpan / shortSpan;
            Assert.True(ratio > 4.5 && ratio < 5.5,
                $"fold {f}: long/short span ratio {ratio:F2} — folds are not proportional to each coin's own length");
        }
    }

    // Contrast: what the old shared-index-space code did. Kept as an executable record of
    // the defect so a regression back to shared bounds is obvious.
    [Fact]
    public void SharedMinLenBounds_DiscardMostOfTheLongerCoin()
    {
        const int shortLen = 1_000;
        const int longLen  = 5_000;

        var shared = FoldScoreHelper.ComputeFoldBoundaries(shortLen, Folds, EmbargoPct);

        // The shared bounds stop at the SHORT coin's length, so 4000 of the long coin's
        // 5000 bars are unreachable — regardless of which coin they are applied to.
        Assert.Equal(shortLen, shared[^1].End);
        int reachableOnLongCoin = shared[^1].End;
        Assert.True(reachableOnLongCoin < longLen / 2,
            "shared minLen bounds should demonstrably fail to reach the long coin's recent history");

        // And the per-coin folds fix exactly that.
        Assert.Equal(longLen, AllFolds(longLen)[^1].End);
    }

    [Fact]
    public void PerCoinFolds_TinyCoinProducesSubFortyBarFolds_SoTheGAsSkipIt()
    {
        // A genuinely tiny coin still gets folds, they are just too short to score. The GAs
        // drop it for that fold (`if (end - start < 40) continue;`) instead of dragging the
        // global fold count down to fit it.
        var folds = AllFolds(120);
        Assert.Contains(folds, r => r.End - r.Start < 40);
    }
}

// The fold count `k` used to be Math.Min(folds, minLen / 40) — one short symbol capped the
// number of folds for every other coin in the run. Now that each coin slices its own array,
// k comes off the MEDIAN length instead, and short coins are skipped per fold.
public class FoldCountDerivationTests
{
    private static int InvokeMedianLength<TGA>(int[] lengths)
    {
        var method = typeof(TGA).GetMethod("MedianLength",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"MedianLength not found on {typeof(TGA).Name}");
        return (int)method.Invoke(null, [lengths])!;
    }

    [Theory]
    [InlineData(new[] { 120, 4000, 5000 }, 4000)]
    [InlineData(new[] { 100, 200 },         200)]
    [InlineData(new[] { 3000 },            3000)]
    public void MedianLength_IgnoresASingleShortOutlier(int[] lengths, int expected)
    {
        Assert.Equal(expected, InvokeMedianLength<GridGeneticAlgorithm>(lengths));
        Assert.Equal(expected, InvokeMedianLength<GridShortGA>(lengths));
    }

    [Fact]
    public void FoldCount_FromMedian_IsNotCappedByTheShortestCoin()
    {
        int[] lengths = [120, 4000, 5000];

        int median = InvokeMedianLength<GridGeneticAlgorithm>(lengths);
        int kNew   = Math.Min(5, median / 40);
        int kOld   = Math.Min(5, lengths.Min() / 40);   // the pre-fix rule

        Assert.Equal(5, kNew);
        Assert.Equal(3, kOld);
        Assert.True(kNew > kOld,
            "a single 120-bar coin must no longer drag the whole run down to three folds");
    }
}

// End-to-end smoke: the three GAs' fold loops must run over coins of wildly different
// lengths without an index-range fault and must return a finite fitness.
public class MixedLengthFoldSmokeTests
{
    private static Candle[] Synthetic(int count, int seed)
    {
        var rng = new Random(seed);
        var t   = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var arr = new Candle[count];
        double price = 100.0;
        for (int i = 0; i < count; i++)
        {
            price *= 1.0 + (rng.NextDouble() - 0.5) * 0.02;
            double high = price * 1.004, low = price * 0.996;
            arr[i] = new Candle(t.AddHours(i), price, high, low, price, 1000);
        }
        return arr;
    }

    [Fact]
    public void FadeShortGA_FoldPath_HandlesCoinsOfDifferentLengths()
    {
        var gaType    = typeof(FadeShortGA);
        var cacheType = gaType.GetNestedType("CoinCache", BindingFlags.NonPublic)!;
        var build     = gaType.GetMethod("BuildCache", BindingFlags.NonPublic | BindingFlags.Static)!;
        var caches    = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(cacheType))!;

        foreach (var arr in new[] { Synthetic(400, 1), Synthetic(3000, 2) })
            caches.Add(build.Invoke(null, [(ReadOnlyMemory<Candle>)arr, 1.0])!);

        var fitnessMethod = gaType.GetMethod("FitnessFromCache", BindingFlags.NonPublic | BindingFlags.Static)!;
        double fitness = (double)fitnessMethod.Invoke(
            null, [FadeShortGenotype.Random(new Random(7)), caches, true, new FitnessConfig(), 5])!;

        Assert.True(double.IsFinite(fitness), $"FadeShort fold fitness was not finite ({fitness})");
    }

    [Fact]
    public void GridGA_FoldPath_HandlesCoinsOfDifferentLengths()
    {
        var ga    = new GridGeneticAlgorithm(populationSize: 4, generations: 1, verbose: false);
        var coins = new List<GridGeneticAlgorithm.CoinData>
        {
            new(Synthetic(400,  3), Synthetic(150, 4)),
            new(Synthetic(3000, 5), Synthetic(600, 6)),
        };

        var method = typeof(GridGeneticAlgorithm).GetMethod("Fitness",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        double fitness = (double)method.Invoke(ga, [GridGenotype.Random(new Random(11)), coins, false, 5])!;

        Assert.True(double.IsFinite(fitness), $"Grid fold fitness was not finite ({fitness})");
    }

    [Fact]
    public void GridShortGA_FoldPath_HandlesCoinsOfDifferentLengths()
    {
        var ga    = new GridShortGA(populationSize: 4, generations: 1, verbose: false);
        var coins = new List<GridShortGA.CoinData>
        {
            new(Synthetic(400,  7), Synthetic(150, 8)),
            new(Synthetic(3000, 9), Synthetic(600, 10)),
        };

        var method = typeof(GridShortGA).GetMethod("Fitness",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        double fitness = (double)method.Invoke(ga, [GridGenotype.Random(new Random(13)), coins, false, 5])!;

        Assert.True(double.IsFinite(fitness), $"GridShort fold fitness was not finite ({fitness})");
    }
}
