using GravityGen2.Strategies.AccumulationGrid;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The grid family used to book ZERO funding. Every other strategy priced perp funding through
// FundingRateSession.PnlPct — the real rate where the cache covers the trade, the interest-rate
// floor otherwise — while GridSimulator and AccumulationGridSimulator contained no funding term
// at all and took no session parameter. On holds up to the grid cap that is a systematic cost
// advantage of ~0.01%/8h, and it biased every Grid-vs-other-strategy comparison in grid's favour.
//
// Testing it without a rate series: funding is the only cost in these simulators that depends on
// WALL-CLOCK time. Every other term is index-based. So running the identical OHLC array twice,
// once with 1h timestamps and once with 8h timestamps, produces a bit-identical trade sequence
// with bit-identical gross returns and bit-identical TradeCost — and the only thing that can
// differ is the number of 8h settlements crossed. Before this change the two runs matched
// exactly. Now the longer wall-clock run must be strictly WORSE, which simultaneously proves
// the charge exists and that its sign is right: these are LONG grids, and a long pays.
public class GridFamilyFundingTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Gently oscillating, compressed, flat-trend series: low ADX, narrow Bollinger width —
    // the ranging regime the grid family is built for.
    private static Candle[] RangingSeries(int n, TimeSpan step)
    {
        var candles = new Candle[n];
        for (int i = 0; i < n; i++)
        {
            double close = 100.0 * (1.0 + 0.005 * Math.Sin(2.0 * Math.PI * i / 40.0));
            double open  = 100.0 * (1.0 + 0.005 * Math.Sin(2.0 * Math.PI * (i - 1) / 40.0));
            candles[i] = new Candle(T0 + step * i, open, Math.Max(open, close) * 1.0008,
                                    Math.Min(open, close) * 0.9992, close, 1000.0);
        }
        return candles;
    }

    private static GridGenotype Geno() => new()
    {
        // Test-only value, above the 8–20 gene bound. A noiseless sine reads as strongly
        // directional to ADX (≈36 here) even though it is the flattest possible range, so the
        // production threshold would gate every entry. This test is about the funding term, not
        // about the regime gate: opening the ADX gate is what lets the price path produce trades
        // at all, and it is identical in both runs being compared.
        AdxThreshold      = 100.0,
        BbPeriod          = 20,
        BbWidthMaxPct     = 2.5,
        EmaPeriod         = 20,
        GridStepAtrMult   = 0.3,
        GridLevels        = 1,
        TakeProfitAtrMult = 0.5,
        HardStopAtrMult   = 3.0,
        BailOutAtrMult    = 4.0,
        MaxHoldCandles    = 200,
    };

    [Fact]
    public void Grid_BooksFunding_LongerWallClockHoldCostsMore()
    {
        var geno  = Geno();
        var fast  = GridSimulator.GetGridReturns(geno, RangingSeries(600, TimeSpan.FromHours(1)));
        var slow  = GridSimulator.GetGridReturns(geno, RangingSeries(600, TimeSpan.FromHours(8)));

        Assert.NotEmpty(fast);
        Assert.Equal(fast.Count, slow.Count);   // identical price path ⇒ identical trade sequence

        // Long grid, funding fallback = interest-rate floor charged per 8h settlement crossed.
        // 8× the wall-clock duration ⇒ strictly more settlements ⇒ strictly worse return.
        double fastSum = fast.Sum(t => t.Return);
        double slowSum = slow.Sum(t => t.Return);
        Assert.True(slowSum < fastSum,
            $"Grid must pay funding: 8h-spaced run returned {slowSum:F4} vs 1h-spaced {fastSum:F4}. " +
            "Equality means the funding term is missing again.");

        // The gap is a whole number of interest-floor ticks, never a prorated fraction.
        double perTradeGap = (fastSum - slowSum) / fast.Count;
        Assert.True(perTradeGap > 0,
            $"Per-trade funding gap must be positive, got {perTradeGap:F6}");
    }

    [Fact]
    public void Grid_SessionReturns_AlsoBookFunding()
    {
        var geno = Geno();
        var fast = GridSimulator.GetGridSessionReturns(geno, RangingSeries(600, TimeSpan.FromHours(1)));
        var slow = GridSimulator.GetGridSessionReturns(geno, RangingSeries(600, TimeSpan.FromHours(8)));

        Assert.NotEmpty(fast);
        Assert.Equal(fast.Count, slow.Count);
        Assert.True(slow.Sum(t => t.Return) < fast.Sum(t => t.Return),
            "GridGA scores session returns — the funding charge must reach that path too, " +
            "or selection sees a cheaper world than the backtest reports.");
    }

    // Sign check, stated as the thing that would break if isLong were inverted: a long paying
    // the floor loses ~0.01pp per settlement. Inverting the flag would turn that into income
    // and make the slower run BETTER, which the assertion above already rules out. This test
    // pins the magnitude too, so a sign flip cannot hide behind a coincidence.
    [Fact]
    public void Grid_FundingMagnitude_MatchesTheInterestRateFloor()
    {
        var geno = Geno();
        var fast = GridSimulator.GetGridSessionReturns(geno, RangingSeries(600, TimeSpan.FromHours(1)));
        var slow = GridSimulator.GetGridSessionReturns(geno, RangingSeries(600, TimeSpan.FromHours(8)));

        double gap = fast.Sum(t => t.Return) - slow.Sum(t => t.Return);
        // Each extra settlement costs exactly FallbackIntervalPct; the total gap must be a
        // positive multiple of it (within floating-point noise on a sum of ~n terms).
        double ticks = gap / FundingRateSession.FallbackIntervalPct;
        Assert.True(ticks > 0, $"Expected a positive whole number of extra settlement ticks, got {ticks:F4}");
        Assert.True(Math.Abs(ticks - Math.Round(ticks)) < 1e-6,
            $"Funding must be charged in whole 8h ticks, never prorated — got {ticks:F8} ticks");
    }

    [Fact]
    public void AccumulationGrid_BooksFunding_LongerWallClockHoldCostsMore()
    {
        var geno = new AccumulationGridGenotype
        {
            EmaPeriod         = 20,
            GridStepAtrMult   = 0.3,
            MaxLevels         = 1,
            TakeProfitAtrMult = 0.5,
            StopLossAtrMult   = 5.0,
            MaxHoldBars       = 200,
            RegimeSustainBars = 1,
        };

        var fast = AccumulationGridSimulator.GetAccumulationReturns(
            geno, RangingSeries(600, TimeSpan.FromHours(1)), MarketRegime.Ranging);
        var slow = AccumulationGridSimulator.GetAccumulationReturns(
            geno, RangingSeries(600, TimeSpan.FromHours(8)), MarketRegime.Ranging);

        Assert.NotEmpty(fast);
        Assert.Equal(fast.Count, slow.Count);
        Assert.True(slow.Sum(t => t.Return) < fast.Sum(t => t.Return),
            $"AccumulationGrid must pay funding: {slow.Sum(t => t.Return):F4} vs {fast.Sum(t => t.Return):F4}");
    }

    // GridShort already priced funding, with isLong: false. Its gross return is
    // (entry − exit) / entry, so it is the SHORT side of the mirror and must keep that flag —
    // the trap that produced the original inverted-sign bug was reading direction off the file
    // name. Under the fallback both directions are charged the floor, so the short's longer run
    // must also be worse, not better.
    [Fact]
    public void GridShort_StillBooksFundingAsAShort()
    {
        var geno = Geno();
        var fast = GridShortSimulator.GetGridShortReturns(geno, RangingSeries(600, TimeSpan.FromHours(1)));
        var slow = GridShortSimulator.GetGridShortReturns(geno, RangingSeries(600, TimeSpan.FromHours(8)));

        Assert.NotEmpty(fast);
        Assert.Equal(fast.Count, slow.Count);
        Assert.True(slow.Sum(t => t.Return) < fast.Sum(t => t.Return),
            "GridShort's funding fallback is a cost for shorts too — see FundingRateSession.PnlPct.");
    }

    // Adding the parameter is source-compatible; the DEFAULT is not behaviour-neutral, and that
    // is deliberate. `funding: null` does not mean "no funding" — PnlPct's null branch charges
    // the interest-rate floor. Passing null explicitly must match omitting the argument.
    [Fact]
    public void NullFundingSession_MatchesOmittingTheArgument()
    {
        var geno    = Geno();
        var candles = RangingSeries(600, TimeSpan.FromHours(1));

        var omitted  = GridSimulator.GetGridReturns(geno, candles);
        var explicitNull = GridSimulator.GetGridReturns(geno, candles, funding: null);

        Assert.Equal(omitted.Count, explicitNull.Count);
        for (int i = 0; i < omitted.Count; i++)
            Assert.Equal(omitted[i].Return, explicitNull[i].Return, 12);

        // ...and the floor is genuinely non-zero, so the grid family is no longer free to hold.
        Assert.True(FundingRateSession.FallbackIntervalPct > 0);
        Assert.True(FundingRateSession.PnlPct(T0, T0.AddHours(24), null, isLong: true) < 0,
            "A long holding a full day must pay the interest-rate floor, not nothing.");
    }
}
