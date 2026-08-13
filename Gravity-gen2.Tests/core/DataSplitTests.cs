using System;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The split is the thing that makes every other measurement in this repo mean anything. These
// tests pin the properties that were violated in practice, not hypothetical ones.
public class DataSplitTests
{
    private static Candle[] Series(int n, TimeSpan step)
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, n)
            .Select(i => new Candle(t0 + step * i, 100, 101, 99, 100, 1000))
            .ToArray();
    }

    [Fact]
    public void SlicesAreContiguous_NonOverlapping_AndCoverEverything()
    {
        // No bar may appear in two slices and none may be lost. The old ad-hoc splits used three
        // different ratios across fourteen sites, so "validation" meant different windows in
        // different commands and a genotype could be scored on bars it trained on.
        var s = DataSplit.Split(Series(1000, TimeSpan.FromHours(1)));
        Assert.Equal(1000, s.Train.Length + s.Val.Length + s.Test.Length);
        Assert.True(s.Train[^1].Time < s.Val[0].Time,  "train must end before val begins");
        Assert.True(s.Val[^1].Time   < s.Test[0].Time, "val must end before test begins");
    }

    [Fact]
    public void SplitIsChronological_NeverShuffled()
    {
        // Random splits leak the future into the past. Time series must be cut by time.
        var s = DataSplit.Split(Series(500, TimeSpan.FromHours(1)));
        foreach (var slice in new[] { s.Train, s.Val, s.Test })
            for (int i = 1; i < slice.Length; i++)
                Assert.True(slice[i].Time > slice[i - 1].Time);
    }

    [Fact]
    public void FifteenMinuteSplit_AlignsToTheSameINSTANTS_NotTheSameFractions()
    {
        // The subtle one. Splitting each timeframe by its own index fractions puts the boundaries
        // at different moments, so a dual-timeframe strategy trains on h1 bars whose 15m
        // counterparts live in validation. A ratio check would never catch it.
        var h1  = Series(1000, TimeSpan.FromHours(1));
        var m15 = Series(4000, TimeSpan.FromMinutes(15));

        var h1s  = DataSplit.Split(h1);
        var m15s = DataSplit.SplitAligned(m15, h1s);

        Assert.True(m15s.Train[^1].Time <= h1s.Train[^1].Time,
            "no 15m training bar may sit after the h1 training boundary");
        Assert.True(m15s.Val[0].Time > h1s.Train[^1].Time,
            "15m validation must begin strictly after the h1 training window");
    }

    [Fact]
    public void TrainAndVal_IsAnExplicitOptIn_AndConcatenatesInOrder()
    {
        // Legitimate for a final refit AFTER selection, never during it. Named so that using it
        // shows up in a diff.
        var s = DataSplit.Split(Series(800, TimeSpan.FromHours(1)));
        var tv = s.TrainAndVal;
        Assert.Equal(s.Train.Length + s.Val.Length, tv.Length);
        for (int i = 1; i < tv.Length; i++) Assert.True(tv[i].Time > tv[i - 1].Time);
    }

    [Fact]
    public void FractionsSumToOne_SoNoBarsAreOrphaned()
        => Assert.True(DataSplit.TrainFraction + DataSplit.ValFraction < 1.0,
            "test must be a non-empty remainder");

    [Fact]
    public void TooShortSeries_YieldsAnUnusableSplit_RatherThanASilentlyTinyTrainSet()
    {
        var s = DataSplit.Split(Series(5, TimeSpan.FromHours(1)));
        Assert.False(s.IsUsable);
    }
}
