using System;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The regime-adapt genes are an EXPERIMENT: does the GA want per-regime parameter scaling?
// That question is only answerable if "off" is genuinely off. If RegimeAdaptStrength = 0 perturbed
// results even slightly, a GA converging near zero would be ambiguous — rejection or just a small
// preferred nudge. These tests pin the no-op.
public class RipShortRegimeAdaptTests
{
    private static Candle[] Series(int n, int stepMinutes, int seed)
    {
        var rng = new Random(seed);
        var c = new Candle[n];
        double px = 100.0;
        var t = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < n; i++)
        {
            // CYCLING trend, not one long bear. The first version of this fixture was a single
            // persistent downtrend, so bearRegimeBars at every entry ran far past any pivot the
            // gene can express (max 300) — maturity saturated at 1.0, adapt was 0 everywhere, and
            // the test reported the gene inert when the fixture simply never gave it a young
            // regime to act on. Adaptation only has leverage EARLY in a regime; a fixture that
            // never has an early regime cannot exercise it.
            double cycle = Math.Sin(i / (stepMinutes == 60 ? 50.0 : 200.0));
            px *= 1.0 + cycle * 0.006 + (rng.NextDouble() - 0.5) * 0.003;
            double hi = px * (1 + rng.NextDouble() * 0.006);
            double lo = px * (1 - rng.NextDouble() * 0.006);
            c[i] = new Candle(t.AddMinutes(i * stepMinutes), px, hi, lo, px, 1_000_000);
        }
        return c;
    }

    private static RipShortGenotype With(double strength, int pivot = 300)
    {
        var g = Base();
        g.RegimeAdaptStrength  = strength;
        g.RegimeAdaptPivotBars = pivot;
        return g;
    }

    private static RipShortGenotype Base() => new()
    {
        RegimeLongEmaPeriod = 100, RegimeSlopeLookback = 20, EmaPeriod = 30,
        AdxThreshold = 18.0, RsiRallyThreshold = 45.0,
        StopLossAtrMult = 1.0, TakeProfitAtrMult = 6.0,
        TrailingActivationAtrMult = 2.5, TrailingStopAtrMult = 2.0,
        MaxHoldCandles = 60, PositionSizePct = 0.03,
        TimeStopBars = 20, TimeStopLossPct = 0.10, RegimeSustainedBars = 10,
        RegimeAdaptPivotBars = 300, RegimeAdaptStrength = 0.0,
    };

    [Fact]
    public void StrengthZero_IsABitForBitNoOp()
    {
        var h1  = Series(3000, 60, 7);
        var m15 = Series(12000, 15, 7);

        var off = RipShortSimulator.GetRipShortReturns(With(0.0), h1, m15);
        // Pivot must not matter at all when strength is zero — if it does, adapt is leaking in.
        var offOtherPivot = RipShortSimulator.GetRipShortReturns(
            With(0.0, 300), h1, m15);

        Assert.Equal(off.Count, offOtherPivot.Count);
        for (int i = 0; i < off.Count; i++)
        {
            Assert.Equal(off[i].Time, offOtherPivot[i].Time);
            Assert.Equal(off[i].Return, offOtherPivot[i].Return, 12);
        }
    }

    [Fact]
    public void NonZeroStrength_ActuallyChangesBehaviour()
    {
        // The other half: a gene that cannot move the results would ALSO converge to zero, and
        // would look exactly like rejection. This proves the GA has something real to select on.
        var h1  = Series(3000, 60, 11);
        var m15 = Series(12000, 15, 11);

        var off  = RipShortSimulator.GetRipShortReturns(With(0.0), h1, m15);
        var tight = RipShortSimulator.GetRipShortReturns(With(0.4), h1, m15);
        var loose = RipShortSimulator.GetRipShortReturns(With(-0.4), h1, m15);

        Assert.True(off.Count > 0, "fixture produced no trades — the test proves nothing");
        bool tightDiffers = tight.Count != off.Count
            || tight.Zip(off).Any(p => Math.Abs(p.First.Return - p.Second.Return) > 1e-9);
        bool looseDiffers = loose.Count != off.Count
            || loose.Zip(off).Any(p => Math.Abs(p.First.Return - p.Second.Return) > 1e-9);
        Assert.True(tightDiffers && looseDiffers,
            $"adaptation is inert: off={off.Count} tight={tight.Count} loose={loose.Count}");
    }

    [Fact]
    public void GenesSurviveTheDtoRoundTrip()
    {
        // A gene the DTO drops reads back as 0 — "disabled". The GA would appear to reject
        // adaptation when in fact training saved it and loading threw it away. This is the exact
        // failure that wrote AtrLow/AtrHigh = [0, 9999] into every high-vol genotype.
        var g = With(-0.27, 175);
        var json = System.Text.Json.JsonSerializer.Serialize(RipShortGenotypeDto.From(g));
        var back = System.Text.Json.JsonSerializer.Deserialize<RipShortGenotypeDto>(json)!.ToGenotype();

        Assert.Equal(g.RegimeAdaptPivotBars, back.RegimeAdaptPivotBars);
        Assert.Equal(g.RegimeAdaptStrength,  back.RegimeAdaptStrength, 9);
    }

    [Fact]
    public void ClampAndVectorRoundTripsKeepTheGenes()
    {
        // ClampToBounds and FromVector are object initialisers: a field omitted from either is
        // silently reset to 0, which for a signed gene means "disabled" rather than "invalid".
        var g = With(0.31, 210);
        Assert.Equal(0.31, g.ClampToBounds().RegimeAdaptStrength, 9);
        Assert.Equal(210,  g.ClampToBounds().RegimeAdaptPivotBars);
        var v = g.ToVector();
        Assert.Equal(0.31, RipShortGenotype.FromVector(v).RegimeAdaptStrength, 9);
        Assert.Equal(210,  RipShortGenotype.FromVector(v).RegimeAdaptPivotBars);
    }
}
