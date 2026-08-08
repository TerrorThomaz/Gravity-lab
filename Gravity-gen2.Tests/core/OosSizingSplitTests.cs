using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Position size in this system IS the half-Kelly confidence: Simulator.ComputeConfidence's
// output is used directly as the fraction of equity per position. The OOS backtest used to
// derive that fraction from a coin's FULL out-of-sample trade series and then score that very
// series — every position sized by the realised performance of the window being measured.
//
// OosBacktest now carves a leading slice out of each coin's own trades: the first
// SizingFraction of trades set the size and are discarded, the remainder is scored. These
// tests pin the property that makes that a real fix rather than a relabelling — the sizing
// must be blind to everything it is applied to.
public class OosSizingSplitTests
{
    // 20 trades → sizing slice = floor(20 * 0.30) = 6, scored = 14.
    private const int Total = 20;

    private static List<double> Series(double leadingValue, double trailingValue)
    {
        int sizingCount = OosBacktest.SizingSliceCount(Total);
        var s = new List<double>();
        for (int i = 0; i < Total; i++)
            // Alternate sign inside each slice so both wins and losses exist and
            // ComputeConfidence produces a non-degenerate Kelly.
            s.Add((i < sizingCount ? leadingValue : trailingValue) * (i % 4 == 3 ? -0.5 : 1.0));
        return s;
    }

    [Fact]
    public void LeadingSliceConfidence_DoesNotMoveWhenTheScoredSliceChanges()
    {
        // Same leading slice, wildly different trailing slices: a fat winner, a bloodbath,
        // and a flat tail. The sizing decision must be identical in all three.
        double confRich = OosBacktest.LeadingSliceConfidence(Series(2.0,  9.0),  out int nRich, out bool fbRich);
        double confPoor = OosBacktest.LeadingSliceConfidence(Series(2.0, -9.0),  out int nPoor, out bool fbPoor);
        double confFlat = OosBacktest.LeadingSliceConfidence(Series(2.0,  0.01), out int nFlat, out bool fbFlat);

        Assert.False(fbRich);
        Assert.False(fbPoor);
        Assert.False(fbFlat);
        Assert.Equal(nRich, nPoor);
        Assert.Equal(nRich, nFlat);

        Assert.Equal(confRich, confPoor, 12);
        Assert.Equal(confRich, confFlat, 12);

        // And it is a live number, not a constant that happens to be stable.
        Assert.True(confRich > 0, $"expected a positive half-Kelly from a profitable leading slice, got {confRich}");

        // The in-sample value it used to return would have differed across these three
        // series — proof the assertion above is not vacuous.
        double inSampleRich = Simulator.ComputeConfidence(Series(2.0,  9.0));
        double inSamplePoor = Simulator.ComputeConfidence(Series(2.0, -9.0));
        Assert.NotEqual(inSampleRich, inSamplePoor, 6);
    }

    [Fact]
    public void LeadingSliceConfidence_TracksTheLeadingSlice()
    {
        // Changing the slice it IS allowed to see must change the sizing, otherwise the
        // "unchanged" assertion above would pass for a hardcoded constant. Kelly is scale
        // invariant, so the two leading slices differ in shape (win rate and win/loss
        // ratio), not just in magnitude. Identical trailing slices in both.
        var tail = Enumerable.Repeat(1.0, 14).ToList();
        var strongHead = new List<double> {  4.0,  4.0,  4.0,  4.0, -1.0,  4.0 };
        var weakHead   = new List<double> {  1.0, -2.0, -2.0,  1.0, -2.0,  1.0 };

        double strong = OosBacktest.LeadingSliceConfidence(strongHead.Concat(tail).ToList(), out _, out bool fbStrong);
        double weak   = OosBacktest.LeadingSliceConfidence(weakHead.Concat(tail).ToList(),   out _, out bool fbWeak);

        Assert.False(fbStrong);
        Assert.False(fbWeak);
        Assert.True(strong > weak, $"a stronger leading slice must size up: strong={strong} weak={weak}");
    }

    [Fact]
    public void SizingSliceCount_IsTheFlooredLeadingFraction()
    {
        Assert.Equal(0, OosBacktest.SizingSliceCount(0));
        Assert.Equal(0, OosBacktest.SizingSliceCount(3));    // 0.9  → 0
        Assert.Equal(3, OosBacktest.SizingSliceCount(10));   // 3.0  → 3
        Assert.Equal(6, OosBacktest.SizingSliceCount(20));   // 6.0  → 6
        Assert.Equal(30, OosBacktest.SizingSliceCount(100));

        // Never consumes the whole series — there is always something left to score.
        foreach (int n in new[] { 1, 5, 7, 33, 251, 4_000 })
            Assert.True(OosBacktest.SizingSliceCount(n) < n, $"n={n} left no trades to score");
    }

    [Fact]
    public void LeadingSliceConfidence_ThinSliceFallsBackToTheFixedConstant_NotTheInSampleValue()
    {
        // 12 trades → sizing slice of 3, below ComputeConfidence's 5-trade floor.
        var thin = new List<double> { 5, 5, 5, -1, 4, 6, -2, 5, 5, 5, -1, 7 };
        double conf = OosBacktest.LeadingSliceConfidence(thin, out int sizingCount, out bool usedFallback);

        Assert.Equal(3, sizingCount);
        Assert.True(usedFallback);
        Assert.Equal(OosBacktest.FallbackConf, conf, 12);

        // The in-sample Kelly on this (very profitable) series is much larger — the fallback
        // must not drift toward it, and must stay put when the series does not.
        double inSample = Simulator.ComputeConfidence(thin);
        Assert.True(inSample > conf, $"fallback {conf} should be well below the in-sample {inSample}");

        var thinAltered = new List<double>(thin);
        for (int i = 3; i < thinAltered.Count; i++) thinAltered[i] = -8;
        double confAltered = OosBacktest.LeadingSliceConfidence(thinAltered, out _, out bool fb2);
        Assert.True(fb2);
        Assert.Equal(conf, confAltered, 12);
    }

    [Fact]
    public void SizeThenScore_ScoredTradesNeverIncludeTheSizingSlice()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var trades = new List<(DateTime Time, double Return, string Kind)>();
        for (int i = 0; i < Total; i++)
            trades.Add((t0.AddHours(i), i < 6 ? 1.0 + i : -3.0, "x"));

        var split = OosBacktest.SizeThenScore(trades, t => t.Time, t => t.Return);

        Assert.Equal(6, split.SizingCount);
        Assert.Equal(Total - 6, split.Scored.Count);
        Assert.All(split.Scored, t => Assert.True(t.Time >= t0.AddHours(6),
            $"scored trade at {t.Time} came from the sizing slice"));

        // Losing tail, profitable head: an in-sample Kelly would be crushed by the tail,
        // the leading-slice Kelly is not.
        Assert.True(split.Conf > 0);
        Assert.Equal(0, Simulator.ComputeConfidence(trades.Select(t => t.Return).ToList()));
    }

    [Fact]
    public void SizeThenScore_SlicesChronologicallyEvenWhenInputIsUnordered()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var ordered = new List<(DateTime Time, double Return, string Kind)>();
        for (int i = 0; i < Total; i++)
            ordered.Add((t0.AddHours(i), i < 6 ? 2.0 : -1.0, "x"));

        var shuffled = ordered.OrderBy(t => t.Time.Ticks % 7).ThenByDescending(t => t.Time).ToList();
        Assert.NotEqual(ordered[0].Time, shuffled[0].Time);   // genuinely out of order

        var fromOrdered  = OosBacktest.SizeThenScore(ordered,  t => t.Time, t => t.Return);
        var fromShuffled = OosBacktest.SizeThenScore(shuffled, t => t.Time, t => t.Return);

        Assert.Equal(fromOrdered.Conf, fromShuffled.Conf, 12);
        Assert.Equal(fromOrdered.Scored.Select(t => t.Time), fromShuffled.Scored.Select(t => t.Time));
    }

    [Fact]
    public void SizeThenScore_EmptyAndTinySeriesDoNotThrow()
    {
        var empty = new List<(DateTime Time, double Return, string Kind)>();
        var split = OosBacktest.SizeThenScore(empty, t => t.Time, t => t.Return);
        Assert.Equal(0, split.SizingCount);
        Assert.Empty(split.Scored);
        Assert.True(split.UsedFallback);
        Assert.Equal(OosBacktest.FallbackConf, split.Conf, 12);

        var one = new List<(DateTime Time, double Return, string Kind)>
            { (new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 1.0, "x") };
        var oneSplit = OosBacktest.SizeThenScore(one, t => t.Time, t => t.Return);
        Assert.Equal(0, oneSplit.SizingCount);
        Assert.Single(oneSplit.Scored);
        Assert.True(oneSplit.UsedFallback);
    }
}
