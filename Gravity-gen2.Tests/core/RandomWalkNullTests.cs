using GravityGen2.Strategies.HybridGrid;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// THE ENGINE GATE. On a driftless random walk there is no edge to find, so every simulator must
// lose approximately its costs. Any strategy that PROFITS on this data has found something in the
// simulator, not in the market — the ground truth is analytic, so there is nothing to argue about.
//
// This exists because the statistical apparatus could not see the defect it is built to catch.
// Deflated Sharpe, PBO, White's Reality Check, walk-forward gating and trial counting all ran
// clean while GridSimulator armed at bar i using ema[i]/atr[i] — which include close[i] — and then
// filled rungs that lows[i] had already reached DURING bar i. Fixing it took Grid from Sharpe 4.15
// to 0.67. Those statistics answer "given that this measurement is trustworthy, was the selection
// honest?" and never establish the antecedent. This test establishes the antecedent.
//
// The same defect class is measured in the literature: Zhang, Li, Peng & Chen (2026),
// "When Alpha Disappears: A One-Switch Benchmark for Decision-Time Leakage in Financial
// Backtests" (arXiv:2605.23959) toggle exactly this convention and report leakage gains of +5.41
// to +21.65 Sharpe against clean references of 0.44-0.68.
//
// ADD EVERY NEW SIMULATOR HERE. It is the cheapest test in the suite and the only one that checks
// the instrument rather than the result.
//
// NOT COVERED, DELIBERATELY: AccumulationGridSimulator still has the same-bar fill defect and would
// fail this gate. It is not live, not loaded by edgetest, and its activation is tied to the first
// fill so deferring the fill disables the strategy rather than fixing it (see the warning in that
// file). It must pass this test before being revived.
//
// The directional simulators are clean BY CONSTRUCTION and were audited 2026-09-24: all four
// dual-timeframe strategies take h1Ref = ih1 - 1 (the last fully-closed hourly bar) and enter at
// m15[nextBar].Open, so the decision strictly precedes the fill. They are not in this gate only
// because constructing a synthetic fixture that makes them trade is far more work than the grid
// family; if that changes, add them.
public class RandomWalkNullTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Driftless geometric random walk with a realistic intrabar range. No trend, no mean reversion,
    // no autocorrelation — the expected return of any position, before costs, is exactly zero.
    private static Candle[] RandomWalk(int n, int seed)
    {
        var rng = new Random(seed);
        var arr = new Candle[n];
        double px = 100.0;
        for (int i = 0; i < n; i++)
        {
            // Box-Muller for a symmetric, fat-free normal step. 1% per bar.
            double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
            double z  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            px *= Math.Exp(0.01 * z - 0.00005);          // -0.00005 offsets Ito drift of exp
            double half = px * (0.004 + rng.NextDouble() * 0.004);
            arr[i] = new Candle(T0.AddHours(i), px, px + half, px - half, px, 1_000_000);
        }
        return arr;
    }

    private static GridGenotype PermissiveGrid() => new()
    {
        EmaPeriod = 20, BbPeriod = 20, GridLevels = 3,
        AdxThreshold = 60, BbWidthMaxPct = 99,           // arm readily, so the sample is large
        GridStepAtrMult = 0.4, HardStopAtrMult = 40, BailOutAtrMult = 40,
        MaxHoldCandles = 120, TakeProfitAtrMult = 1.0,
        ReanchorAlpha = 0.0, RungSellFrac = 0, SlopeLookback = 20, SlopeThreshold = -1.0,
    };

    // Pool across seeds so the verdict rests on thousands of trades, not one path.
    //
    // The assertion is a t-STATISTIC, not `mean <= 0`. On a driftless walk a clean engine returns
    // -costs, which is significantly negative; but a naive `mean <= 0` check would also fire on
    // ordinary sampling noise roughly half the time whenever costs are small relative to dispersion.
    // A t-test keeps the teeth (a leak of the size we measured produces an enormous t) without the
    // false alarms.
    private static (int N, double Mean, double T) Pooled(
        Func<Candle[], IEnumerable<double>> run, int seeds = 40)
    {
        var all = new List<double>();
        for (int s = 0; s < seeds; s++) all.AddRange(run(RandomWalk(3000, 1000 + s)));
        if (all.Count < 2) return (all.Count, 0.0, 0.0);
        double mean = all.Average();
        double sd = Math.Sqrt(all.Select(x => (x - mean) * (x - mean)).Sum() / (all.Count - 1));
        return (all.Count, mean, sd > 1e-12 ? mean / (sd / Math.Sqrt(all.Count)) : 0.0);
    }

    [Fact]
    public void Grid_OnADriftlessRandomWalk_DoesNotProfit()
    {
        var (n, mean, t) = Pooled(c => GridSimulator.GetGridSessionReturns(PermissiveGrid(), c)
                                                    .Select(x => x.Return));
        Assert.True(n >= 500, $"fixture produced only {n} trades — too few to conclude anything");
        Assert.True(t < 2.0,
            $"Grid earned {mean:F4}% per trade (t={t:F2}) on a driftless random walk over {n} trades. " +
            "There is no edge in this data, so the P&L came from the simulator.");
    }

    [Fact]
    public void GridShort_OnADriftlessRandomWalk_DoesNotProfit()
    {
        var (n, mean, t) = Pooled(c => GridShortSimulator.GetGridShortSessionReturns(PermissiveGrid(), c)
                                                        .Select(x => x.Return));
        Assert.True(n >= 500, $"fixture produced only {n} trades — too few to conclude anything");
        Assert.True(t < 2.0,
            $"GridShort earned {mean:F4}% per trade (t={t:F2}) on a driftless random walk over {n} trades. " +
            "There is no edge in this data, so the P&L came from the simulator.");
    }

    // THE GATE MUST BE SHOWN TO WORK. A null control that has never rejected anything is not a
    // control. This reintroduces the exact defect via the fillOnArmBar flag and asserts the
    // property above is violated — so if someone "simplifies" the test into something that always
    // passes, this fails and says so.
    [Fact]
    public void TheNullControlActuallyDetectsSameBarFill()
    {
        // ONE-SWITCH LEAKAGE GAIN, the form Zhang et al. (2026) use: hold the data, genotype,
        // costs and exit logic fixed and toggle exactly one execution convention. The absolute
        // level on synthetic data is less informative than the DIFFERENCE, because the size of the
        // leak depends on how deep the intrabar range is relative to the grid step — which is a
        // property of the fixture, not of the defect.
        var honest = Pooled(c => GridSimulator.GetGridSessionReturns(PermissiveGrid(), c)
                                              .Select(x => x.Return));
        var leaked = Pooled(c => GridSimulator.GetGridSessionReturnsSameBarFill(PermissiveGrid(), c)
                                              .Select(x => x.Return));

        Assert.True(honest.N >= 500 && leaked.N >= 500, "diagnostic fixture produced too few trades");
        double gain = leaked.Mean - honest.Mean;
        Assert.True(gain > 0.05,
            $"leakage gain was only {gain:F4}%/trade (honest {honest.Mean:F4}% t={honest.T:F2}, " +
            $"same-bar {leaked.Mean:F4}% t={leaked.T:F2}). The null control can no longer detect the " +
            "defect it exists for — check the fixture's intrabar range against the grid step.");
    }
    // ── HybridGrid ────────────────────────────────────────────────────────────────────────────
    // Combines Grid with AccumulationGrid — the one simulator this gate excluded for failing it —
    // so it has to pass here, on BOTH sides, before any number it produces is read.
    private static HybridGridGenotype PermissiveHybrid() => new()
    {
        EmaPeriod = 20, SlopeLookback = 10, SlopeMinPct = 0.0, BiasBandAtr = 0.0, AdxMin = 0.0,
        GridStepAtrMult = 0.4, GridLevels = 3, TakeProfitAtrMult = 1.0, TrailStopAtrMult = 3.0,
        MaxHoldBars = 60,
    };

    [Theory]
    [InlineData(HybridGridSides.Both)]
    [InlineData(HybridGridSides.LongOnly)]
    [InlineData(HybridGridSides.ShortOnly)]
    public void HybridGrid_OnADriftlessRandomWalk_DoesNotProfit(HybridGridSides sides)
    {
        var (n, mean, t) = Pooled(c => HybridGridSimulator.GetHybridSessionReturns(PermissiveHybrid(), c, sides)
                                                          .Select(x => x.Return));
        Assert.True(n >= 500, $"fixture produced only {n} trades — too few to conclude anything");
        Assert.True(t < 2.0,
            $"HybridGrid({sides}) earned {mean:F4}% per session (t={t:F2}) on a driftless random walk " +
            $"over {n} sessions. There is no edge in this data, so the P&L came from the simulator.");

        // Per-rung too, and stricter. The session mean weights a 3-rung losing session the same as
        // a 1-rung winner, which on this fixture lifts the session figure ~0.2pp above the per-rung
        // one — so the session check alone sits near zero even on a clean engine. Per-rung is what
        // the account actually books, and a clean engine must lose its costs there, significantly.
        var (nr, meanR, tr) = Pooled(c => HybridGridSimulator.GetHybridReturns(PermissiveHybrid(), c, sides)
                                                            .Select(x => x.Return));
        Assert.True(tr < -2.0,
            $"HybridGrid({sides}) per-rung mean {meanR:F4}% (t={tr:F2}, n={nr}) — a clean engine " +
            "loses its costs on a random walk; failing to is the simulator handing out P&L.");
    }

    // Self-check for the HybridGrid gate: building bar i's levels from bar i's own ema/atr/close
    // (AccumulationGrid's defect) must be detectable as a gain over the honest engine.
    [Fact]
    public void TheNullControlDetectsTheHybridSameBarLeak()
    {
        var honest = Pooled(c => HybridGridSimulator.GetHybridSessionReturns(PermissiveHybrid(), c)
                                                    .Select(x => x.Return));
        var leaked = Pooled(c => HybridGridSimulator.GetHybridSessionReturnsSameBarLeak(PermissiveHybrid(), c)
                                                    .Select(x => x.Return));
        Assert.True(honest.N >= 500 && leaked.N >= 500, "diagnostic fixture produced too few trades");
        double gain = leaked.Mean - honest.Mean;
        Assert.True(gain > 0.05,
            $"leakage gain was only {gain:F4}%/trade (honest {honest.Mean:F4}% t={honest.T:F2}, " +
            $"leaked {leaked.Mean:F4}% t={leaked.T:F2}) — the gate cannot see the defect on this engine.");
    }

    // CAUSALITY BY TRUNCATION. A trade that closed at bar k must be identical whether or not bars
    // after k exist. Any use of future data — not only the same-bar kind — breaks this. Indicators
    // here are all causal recursions, so truncating the input cannot change any value before the cut.
    [Fact]
    public void HybridGrid_TradesClosedBeforeACut_DoNotDependOnLaterBars()
    {
        var full = RandomWalk(3000, 77);
        var all  = HybridGridSimulator.GetHybridReturns(PermissiveHybrid(), full);
        foreach (int cut in new[] { 900, 1700, 2500 })
        {
            var prefix = HybridGridSimulator.GetHybridReturns(PermissiveHybrid(), full.AsSpan(0, cut));
            DateTime cutTime = full[cut - 1].Time;
            // The prefix run force-closes whatever is open at its last bar; exclude those.
            var expected = all.Where(x => x.Time < cutTime).ToList();
            var actual   = prefix.Where(x => x.Time < cutTime).ToList();
            Assert.True(expected.Count > 20, $"too few trades before cut {cut}");
            Assert.Equal(expected, actual);
        }
    }
}
