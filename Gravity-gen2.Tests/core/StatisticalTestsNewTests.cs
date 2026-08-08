using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class StatisticalTestsNewTests
{
    [Fact]
    public void HolmBonferroni_AllSignificant_AllRejected()
    {
        var pValues = new double[] { 0.001, 0.005, 0.01 };
        var rejected = StatisticalTests.HolmBonferroni(pValues, alpha: 0.05);
        Assert.All(rejected, r => Assert.True(r));
    }

    [Fact]
    public void HolmBonferroni_NoneSignificant_NoneRejected()
    {
        var pValues = new double[] { 0.10, 0.20, 0.30 };
        var rejected = StatisticalTests.HolmBonferroni(pValues, alpha: 0.05);
        Assert.All(rejected, r => Assert.False(r));
    }

    [Fact]
    public void HolmBonferroni_MixedCorrectly()
    {
        var pValues = new double[] { 0.01, 0.04, 0.03 };
        var rejected = StatisticalTests.HolmBonferroni(pValues, alpha: 0.05);
        Assert.True(rejected[0]);
        Assert.False(rejected[1]);
        Assert.False(rejected[2]);
    }

    [Fact]
    public void HolmBonferroni_AdjustedPValues_Monotonic()
    {
        var pValues = new double[] { 0.01, 0.04, 0.03, 0.10 };
        var adjusted = StatisticalTests.HolmBonferroniAdjustedPValues(pValues);
        Assert.Equal(4, adjusted.Length);
        Assert.True(adjusted[0] <= adjusted[2]);
        Assert.True(adjusted[2] <= adjusted[1]);
        Assert.True(adjusted[1] <= adjusted[3]);
    }

    [Fact]
    public void HolmBonferroni_SingleTest_MatchesRaw()
    {
        var pValues = new double[] { 0.03 };
        var adjusted = StatisticalTests.HolmBonferroniAdjustedPValues(pValues);
        Assert.Equal(0.03, adjusted[0], 5);
    }

    [Fact]
    public void CVaR_NormalDistribution_NegativeTail()
    {
        var returns = new List<double> { 5, 3, -1, -2, 4, -3, 2, -4, 1, -5 };
        double cvar = StatisticalTests.CVaR(returns, alpha: 0.20);
        Assert.True(cvar < 0, $"CVaR should be negative for tail losses, got {cvar}");
        Assert.Equal(-4.5, cvar, 1);
    }

    [Fact]
    public void CVaR_AllPositive_StillReturnsLowest()
    {
        var returns = new List<double> { 1, 2, 3, 4, 5 };
        double cvar = StatisticalTests.CVaR(returns, alpha: 0.20);
        Assert.Equal(1.0, cvar, 1);
    }

    [Fact]
    public void CVaR_EmptyList_ReturnsZero()
    {
        Assert.Equal(0, StatisticalTests.CVaR(new List<double>()));
    }
}

public class TimeEmbargoTests
{
    [Fact]
    public void ComputeFoldBoundaries_NoEmbargo_ContiguousFolds()
    {
        var bounds = FoldScoreHelper.ComputeFoldBoundaries(1000, 5, embargoPct: 0.0);
        Assert.Equal(5, bounds.Length);
        Assert.Equal(0, bounds[0].Start);
        Assert.Equal(200, bounds[0].End);
        Assert.Equal(200, bounds[1].Start);
        Assert.Equal(400, bounds[1].End);
    }

    [Fact]
    public void ComputeFoldBoundaries_WithEmbargo_GapsBetweenFolds()
    {
        var bounds = FoldScoreHelper.ComputeFoldBoundaries(1000, 5, embargoPct: 0.10);
        Assert.Equal(5, bounds.Length);
        Assert.Equal(0, bounds[0].Start);
        Assert.Equal(200, bounds[0].End);
        Assert.True(bounds[1].Start > 200, $"Fold 1 should start after embargo gap, got {bounds[1].Start}");
        Assert.Equal(400, bounds[1].End);
    }

    [Fact]
    public void ComputeFoldBoundaries_LastFold_ExtendsToEnd()
    {
        var bounds = FoldScoreHelper.ComputeFoldBoundaries(1000, 5, embargoPct: 0.05);
        Assert.Equal(1000, bounds[4].End);
    }

    [Fact]
    public void ComputeRegimeAwareFoldBoundaries_AllBull_BehavesLikeRegular()
    {
        var series = Enumerable.Range(0, 1000)
            .Select(i => new RegimeBar(DateTime.UtcNow.AddHours(i), MarketRegime.Bull, 0.8, i))
            .ToArray();
        var bounds = FoldScoreHelper.ComputeRegimeAwareFoldBoundaries(series, r => r == MarketRegime.Bull, 5, 0.0);
        Assert.Equal(5, bounds.Length);
        Assert.Equal(0, bounds[0].Start);
        Assert.True(bounds[4].End >= 990, $"Last fold should extend near end, got {bounds[4].End}");
    }

    [Fact]
    public void ComputeRegimeAwareFoldBoundaries_MixedRegimes_FoldsSpanActivePeriods()
    {
        var series = new List<RegimeBar>();
        for (int i = 0; i < 1000; i++)
        {
            var regime = (i / 100) % 2 == 0 ? MarketRegime.Bull : MarketRegime.Bear;
            series.Add(new RegimeBar(DateTime.UtcNow.AddHours(i), regime, 0.8, i % 100));
        }
        var bounds = FoldScoreHelper.ComputeRegimeAwareFoldBoundaries(series.ToArray(), r => r == MarketRegime.Bull, 5, 0.05);
        Assert.Equal(5, bounds.Length);
        Assert.Equal(0, bounds[0].Start);
        Assert.True(bounds[4].End >= 900, $"Last fold should extend near end, got {bounds[4].End}");
    }

    [Fact]
    public void ComputeRegimeAwareFoldBoundaries_InsufficientActiveData_FallsBackToRegular()
    {
        var series = Enumerable.Range(0, 100)
            .Select(i => new RegimeBar(DateTime.UtcNow.AddHours(i), MarketRegime.Bear, 0.8, i))
            .ToArray();
        var bounds = FoldScoreHelper.ComputeRegimeAwareFoldBoundaries(series, r => r == MarketRegime.Bull, 5, 0.05);
        Assert.Equal(5, bounds.Length);
        Assert.Equal(0, bounds[0].Start);
        Assert.Equal(100, bounds[4].End);
    }
}

// Walk-forward folds must be cut on CALENDAR TIME and mapped per-coin, because every coin's
// candle array has a different length and start date (and for the bear strategies is a
// regime-filtered concatenation). Sharing raw BTC indices across coins silently collapsed
// short-history coins into fold 0 and left the later folds empty.
public class TimeBasedFoldWindowTests
{
    private static readonly DateTime Base = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Candle[] MakeCandles(int count, int startHourOffset = 0) =>
        Enumerable.Range(0, count)
            .Select(i => new Candle(Base.AddHours(startHourOffset + i), 100, 101, 99, 100, 1000))
            .ToArray();

    private static RegimeBar[] MakeBullSeries(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new RegimeBar(Base.AddHours(i), MarketRegime.Bull, 0.8, i))
            .ToArray();

    [Fact]
    public void ComputeRegimeAwareFoldWindows_ProducesOrderedNonOverlappingWindows()
    {
        var windows = FoldScoreHelper.ComputeRegimeAwareFoldWindows(
            MakeBullSeries(1000), r => r == MarketRegime.Bull, 5, 0.05);

        Assert.Equal(5, windows.Length);
        Assert.Equal(DateTime.MinValue, windows[0].Start);   // opens before any coin's first candle
        Assert.Equal(DateTime.MaxValue, windows[4].End);     // closes after any coin's last candle
        for (int f = 0; f < 5; f++)
            Assert.True(windows[f].Start < windows[f].End, $"fold {f} window is empty");
        for (int f = 0; f < 4; f++)
            Assert.True(windows[f].End <= windows[f + 1].Start,
                $"fold {f} overlaps fold {f + 1}: {windows[f].End:o} > {windows[f + 1].Start:o}");
    }

    [Fact]
    public void RangeForWindow_SameWindow_MapsToSameCalendarDatesOnCoinsOfDifferentLength()
    {
        // Long coin: 1000 hourly bars from Base. Short coin: 200 hourly bars starting 800h later.
        var longCoin  = MakeCandles(1000);
        var shortCoin = MakeCandles(200, startHourOffset: 800);

        var winStart = Base.AddHours(850);
        var winEnd   = Base.AddHours(900);

        var (ls, le) = FoldScoreHelper.RangeForWindow(longCoin,  winStart, winEnd);
        var (ss, se) = FoldScoreHelper.RangeForWindow(shortCoin, winStart, winEnd);

        Assert.Equal(850, ls);
        Assert.Equal(900, le);
        Assert.Equal(50, ss);
        Assert.Equal(100, se);
        // Same calendar stretch on both coins despite completely different index spaces.
        Assert.Equal(longCoin[ls].Time, shortCoin[ss].Time);
        Assert.Equal(le - ls, se - ss);
    }

    [Fact]
    public void RangeForWindow_CoinWithNoDataInWindow_ReturnsEmptyRange()
    {
        var shortCoin = MakeCandles(200, startHourOffset: 800);
        var (s, e) = FoldScoreHelper.RangeForWindow(shortCoin, Base.AddHours(100), Base.AddHours(300));
        Assert.Equal(s, e);   // empty — the coin simply sits out this fold
    }

    [Fact]
    public void RangeForWindow_ShortCoinIsNotCollapsedIntoFoldZero()
    {
        // The regression: with index-space bounds, a coin shorter than a fold's Start index
        // was skipped in every later fold and its whole history landed in fold 0.
        var series  = MakeBullSeries(1000);
        var windows = FoldScoreHelper.ComputeRegimeAwareFoldWindows(series, r => r == MarketRegime.Bull, 5, 0.05);
        var shortCoin = MakeCandles(200, startHourOffset: 800);   // only the most RECENT stretch

        int nonEmptyFolds = 0, barsInFoldZero = 0;
        for (int f = 0; f < windows.Length; f++)
        {
            var (s, e) = FoldScoreHelper.RangeForWindow(shortCoin, windows[f].Start, windows[f].End);
            if (e > s) nonEmptyFolds++;
            if (f == 0) barsInFoldZero = e - s;
        }

        Assert.Equal(0, barsInFoldZero);   // recent-history coin has nothing in the OLDEST fold
        Assert.True(nonEmptyFolds >= 1, "short coin should land in the fold matching its dates");
    }

    [Fact]
    public void RangeForWindow_GappedConcatenation_StillBinarySearchesCorrectly()
    {
        // Bear-window training arrays are concatenations of ordered slices: ascending with gaps.
        var gapped = MakeCandles(100).Concat(MakeCandles(100, startHourOffset: 500)).ToArray();

        var (s, e) = FoldScoreHelper.RangeForWindow(gapped, Base.AddHours(50), Base.AddHours(550));
        Assert.Equal(50, s);
        Assert.Equal(150, e);          // 50 tail bars of block 1 + 50 head bars of block 2
        Assert.Equal(Base.AddHours(50), gapped[s].Time);

        // A window entirely inside the gap selects nothing.
        var (gs, ge) = FoldScoreHelper.RangeForWindow(gapped, Base.AddHours(200), Base.AddHours(400));
        Assert.Equal(gs, ge);
    }

    [Fact]
    public void RangeForWindow_UnboundedEnds_CoverWholeArray()
    {
        var coin = MakeCandles(500);
        var (s, e) = FoldScoreHelper.RangeForWindow(coin, DateTime.MinValue, DateTime.MaxValue);
        Assert.Equal(0, s);
        Assert.Equal(500, e);
    }

    [Fact]
    public void RangeForWindow_EmptyWindow_ReturnsEmptyRange()
    {
        var coin = MakeCandles(500);
        var (s, e) = FoldScoreHelper.RangeForWindow(coin, DateTime.MaxValue, DateTime.MaxValue);
        Assert.Equal(s, e);
    }

    [Fact]
    public void PerCoinFoldRange_SlicesEachCoinsOwnHistory()
    {
        var (s0, e0) = FoldScoreHelper.PerCoinFoldRange(1000, 5, 0, 0.0);
        Assert.Equal(0, s0);
        Assert.Equal(200, e0);

        var (s4, e4) = FoldScoreHelper.PerCoinFoldRange(1000, 5, 4, 0.0);
        Assert.Equal(800, s4);
        Assert.Equal(1000, e4);   // last fold always runs to the end

        // Every coin gets the same PROPORTION of its own array, whatever its length.
        var (ss, se) = FoldScoreHelper.PerCoinFoldRange(200, 5, 2, 0.0);
        Assert.Equal(80, ss);
        Assert.Equal(120, se);
    }

    [Fact]
    public void PerCoinFoldRange_WithEmbargo_GapsAfterFirstFold()
    {
        var (s0, _) = FoldScoreHelper.PerCoinFoldRange(1000, 5, 0, 0.10);
        var (s1, _) = FoldScoreHelper.PerCoinFoldRange(1000, 5, 1, 0.10);
        Assert.Equal(0, s0);                    // fold 0 never carries an embargo
        Assert.True(s1 > 200, $"fold 1 should start after the embargo gap, got {s1}");
    }
}

// CVaR is a SIGNED percent quantity: negative = loss, more negative = fatter left tail.
// The penalty multiplier must therefore fall as CVaR falls (an inverted version of this
// rewarded fat tails and punished safe strategies).
public class CVaRPenaltyTests
{
    // Flat +1% trades plus a left tail entirely at `worst`, sized so that the tail is
    // EXACTLY the alpha = 0.05 bucket (max(1, count*0.05) trades) and cvar5 == worst.
    //
    // The default count was 40, but CVaRPenalty is now gated at
    // FoldScoreHelper.MinTailSampleSize: below 100 returns, the 5% bucket holds fewer than
    // 5 observations and "expected shortfall" is an extreme order statistic rather than a
    // risk measure, so the term returns the neutral 1.0. The fixture is sized past the
    // gate so these tests exercise the penalty instead of the gate.
    private static List<double> WithTailTrades(double worst, int count = 120)
    {
        int tail = Math.Max(1, (int)(count * 0.05));
        var list = Enumerable.Repeat(1.0, count - tail).ToList();
        for (int i = 0; i < tail; i++) list.Add(worst);
        return list;
    }

    [Fact]
    public void CVaRPenalty_FatTail_ScoresLowerThanTightTail()
    {
        var cfg = new FitnessConfig();
        double fat   = FoldScoreHelper.CVaRPenalty(WithTailTrades(-12.0), cfg);
        double tight = FoldScoreHelper.CVaRPenalty(WithTailTrades(-0.5),  cfg);
        Assert.True(fat < tight,
            $"fat left tail must be penalised MORE than a tight one (fat={fat:F3}, tight={tight:F3})");
    }

    [Fact]
    public void CVaRPenalty_MonotonicallyDecreasingInTailDepth()
    {
        var cfg = new FitnessConfig();
        double prev = double.MaxValue;
        foreach (double worst in new[] { -0.5, -2.0, -3.0, -4.0, -5.0, -6.0, -20.0 })
        {
            double p = FoldScoreHelper.CVaRPenalty(WithTailTrades(worst), cfg);
            Assert.True(p <= prev + 1e-12, $"penalty rose as the tail got fatter at worst={worst}");
            prev = p;
        }
    }

    [Fact]
    public void CVaRPenalty_ShallowTail_NoPenalty()
    {
        Assert.Equal(1.0, FoldScoreHelper.CVaRPenalty(WithTailTrades(-2.0), new FitnessConfig()), 6);
        Assert.Equal(1.0, FoldScoreHelper.CVaRPenalty(WithTailTrades(-3.0), new FitnessConfig()), 6);
    }

    [Fact]
    public void CVaRPenalty_SaturatesAtWeight_NeverZeroOrNegative()
    {
        var cfg = new FitnessConfig(CVaRW: 0.3);
        double p = FoldScoreHelper.CVaRPenalty(WithTailTrades(-500.0), cfg);
        Assert.Equal(0.7, p, 6);                    // 1 - 1.0 * CVaRW, saturated
        Assert.True(p > 0, "penalty must never zero out or flip the sign of the fitness score");

        // Even an absurd weight cannot drive the multiplier to zero.
        double floored = FoldScoreHelper.CVaRPenalty(WithTailTrades(-500.0), new FitnessConfig(CVaRW: 5.0));
        Assert.True(floored >= 0.5, $"multiplier floor breached: {floored}");
    }

    [Fact]
    public void CVaRPenalty_Disabled_OrTooFewTrades_ReturnsOne()
    {
        Assert.Equal(1.0, FoldScoreHelper.CVaRPenalty(WithTailTrades(-50.0), new FitnessConfig(CVaRW: 0.0)), 6);
        Assert.Equal(1.0, FoldScoreHelper.CVaRPenalty(WithTailTrades(-50.0, count: 10), new FitnessConfig()), 6);
        // The gate is a RAMP, not a step. It used to switch at MinTailSampleSize, which paid the
        // GA to sit one trade under it: deleting a winning trade at n=100 raised the fold score
        // by 35.8%. Below TailRampLo the 1-in-20 tail bucket holds a single observation and the
        // term is fully neutral; at TailRampHi the bucket holds 5 and the term is fully on.
        Assert.Equal(1.0, FoldScoreHelper.CVaRPenalty(
            WithTailTrades(-50.0, count: FoldScoreHelper.TailRampLo), new FitnessConfig()), 6);
        // One trade under the old cliff: now almost fully on, not neutral.
        Assert.Equal(0.705, FoldScoreHelper.CVaRPenalty(
            WithTailTrades(-50.0, count: FoldScoreHelper.MinTailSampleSize - 1), new FitnessConfig()), 6);
        Assert.True(FoldScoreHelper.CVaRPenalty(
            WithTailTrades(-50.0, count: FoldScoreHelper.MinTailSampleSize), new FitnessConfig()) < 1.0);
    }
}
