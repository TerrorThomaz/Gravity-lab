using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// "Router as indicator": BTC regime reaches SwingLong's ENTRY test rather than gating the whole
// strategy from outside. The existing tradeGate only weights trades the simulator already took,
// so the strategy could be penalised for trading in the wrong regime but never decline to.
[Collection(ProcessGlobalCollection.Name)]   // sets SwingLongSimulator.BtcRegimeProbe, a process-global static
public class SwingLongBtcAlignTests : IDisposable
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void Dispose() => SwingLongSimulator.BtcRegimeProbe = null;   // static: must not leak

    private static Candle[] Series(int n, int stepMinutes, int seed)
    {
        var rng = new Random(seed);
        var c = new Candle[n];
        double px = 100.0;
        for (int i = 0; i < n; i++)
        {
            px *= 1.0 + Math.Sin(i / (stepMinutes == 60 ? 50.0 : 200.0)) * 0.006
                      + (rng.NextDouble() - 0.5) * 0.003;
            c[i] = new Candle(T0.AddMinutes(i * stepMinutes), px,
                              px * (1 + rng.NextDouble() * 0.006),
                              px * (1 - rng.NextDouble() * 0.006), px, 1_000_000);
        }
        return c;
    }

    private static SwingLongGenotype Base(double w) => new()
    {
        EmaPeriod = 50, AdxThreshold = 18.0, LookbackCandles = 40,
        RsiOversold = 35.0, RsiDivThreshold = 5.0, MinDeclineAtrMult = 5.0,
        StopLossAtrMult = 1.0, TakeProfitAtrMult = 6.0,
        TrailingActivationAtrMult = 2.5, TrailingStopAtrMult = 2.0,
        MaxHoldCandles = 60, PositionSizePct = 0.03,
        TimeStopBars = 20, TimeStopLossPct = 0.10,
        BtcAlignWeight = w,
    };

    private static int Trades(double w, Func<DateTime, double>? probe)
    {
        SwingLongSimulator.BtcRegimeProbe = probe;
        return SwingLongSimulator.GetSwingLongReturns(Base(w), Series(3000, 60, 5), Series(12000, 15, 5)).Count;
    }

    [Fact]
    public void WeightZero_IsANoOp_EvenUnderAMaximallyHostileProbe()
    {
        // A probe returning full BEAR confidence at every bar must not remove a single trade when
        // the gate is off. If it did, "the GA set the weight to 0" would not mean "BTC ignored".
        int noProbe  = Trades(0.0, null);
        int hostile  = Trades(0.0, _ => -1.0);
        Assert.Equal(noProbe, hostile);
        Assert.True(noProbe > 0, "fixture produced no trades — the comparison proves nothing");
    }

    [Fact]
    public void HighWeight_UnderBearBtc_RemovesTrades()
    {
        // The gate must actually bite: demanding BTC bullishness while BTC is bearish should
        // suppress entries. A gene that cannot change anything would also converge to 0 and be
        // indistinguishable from rejection.
        int off = Trades(0.0, _ => -1.0);
        int on  = Trades(1.0, _ => -1.0);
        Assert.True(on < off, $"gate is inert: off={off} on={on}");
    }

    [Fact]
    public void TheGateOnlyRemoves_NeverManufactures()
    {
        // One-sided by construction. A routing gate that could CREATE setups would be answering a
        // different question — and after the frequency-bonus fix, manufacturing trades is exactly
        // the behaviour we no longer want a gene able to reach for.
        int baseline = Trades(0.0, null);
        foreach (double w in new[] { 0.25, 0.5, 0.75, 1.0 })
            foreach (var probe in new Func<DateTime, double>[] { _ => -1.0, _ => 0.0, _ => 1.0 })
                Assert.True(Trades(w, probe) <= baseline,
                    $"weight {w} produced MORE trades than the ungated baseline {baseline}");
    }

    [Fact]
    public void BullBtcLetsTradesThrough_BearBtcDoesNot()
    {
        // The signal has to carry direction, not just magnitude: same weight, opposite regimes.
        int inBull = Trades(0.9, _ => +1.0);
        int inBear = Trades(0.9, _ => -1.0);
        Assert.True(inBull > inBear, $"direction ignored: bull={inBull} bear={inBear}");
    }

    [Fact]
    public void GeneSurvivesDtoAndVectorRoundTrips()
    {
        var g = Base(0.63);
        var json = System.Text.Json.JsonSerializer.Serialize(SwingLongGenotypeDto.From(g));
        var back = System.Text.Json.JsonSerializer.Deserialize<SwingLongGenotypeDto>(json)!.ToGenotype();
        Assert.Equal(0.63, back.BtcAlignWeight, 9);
        Assert.Equal(0.63, SwingLongGenotype.FromVector(g.ToVector()).BtcAlignWeight, 9);
    }
}
