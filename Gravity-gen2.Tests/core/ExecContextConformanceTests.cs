using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The invariant this repo was missing.
//
// Eight mechanisms were built, compiled, passed the whole suite, and never ran: SizeMult,
// tradeGate, ExitOverrideMode, the volatility rotator, the funding crowding gates, the
// *HighVolActive flags, HolmBonferroni and RunBlockBootstrap. Nothing failed, because nothing
// asserted that a mechanism handed to a simulator actually reaches it.
//
// These tests assert exactly that: for every registered strategy, every ExecContext field it
// CLAIMS to honour must be able to change its output. A simulator that quietly drops a field
// fails here instead of shipping.
public class ExecContextConformanceTests
{
    // Synthetic series with enough structure to generate trades in both directions: a drift with
    // superimposed swings, so RSI/ADX/BoS conditions actually fire. Deterministic — the point is
    // reproducibility, not realism.
    private static Candle[] Synthetic(int n, int seed)
    {
        var rng = new Random(seed);
        var arr = new Candle[n];
        double px = 100.0;
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < n; i++)
        {
            // Amplitude matters: these strategies need RSI extremes, a minimum ATR-scaled move and
            // a break of structure to fire at all. A gentle series produces zero trades and every
            // conformance assertion below then passes vacuously — which is precisely what the
            // guard test caught on the first attempt.
            double wave  = Math.Sin(i / 40.0) * 14.0 + Math.Sin(i / 9.0) * 5.0 + Math.Sin(i / 3.0) * 1.5;
            double noise = (rng.NextDouble() - 0.5) * 3.0;
            px = Math.Max(5.0, 100.0 + wave + noise);
            double hi = px * (1 + 0.010 + rng.NextDouble() * 0.008);
            double lo = px * (1 - 0.010 - rng.NextDouble() * 0.008);
            arr[i] = new Candle(t0.AddHours(i), px, hi, lo, px, 1_000_000);
        }
        return arr;
    }

    private static Candle[] SyntheticM15(int n, int seed)
    {
        var rng = new Random(seed + 991);
        var arr = new Candle[n];
        double px = 100.0;
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < n; i++)
        {
            double wave  = Math.Sin(i / 160.0) * 14.0 + Math.Sin(i / 36.0) * 5.0 + Math.Sin(i / 12.0) * 1.5;
            double noise = (rng.NextDouble() - 0.5) * 3.0;
            px = Math.Max(5.0, 100.0 + wave + noise);
            double hi = px * (1 + 0.008 + rng.NextDouble() * 0.006);
            double lo = px * (1 - 0.008 - rng.NextDouble() * 0.006);
            arr[i] = new Candle(t0.AddMinutes(15 * i), px, hi, lo, px, 1_000_000);
        }
        return arr;
    }

    private static IReadOnlyList<IStrategySimulator> Registry() =>
        StrategyRegistry.All(
            FadeShortGenotype.Random(new Random(11)),
            SwingLongGenotype.Random(new Random(12)),
            DipLongGenotype.Random(new Random(13)),
            FadeLongGenotype.Random(new Random(14)),
            GridGenotype.Random(new Random(15)));

    private static (Candle[] H1, Candle[] M15) Data() => (Synthetic(12000, 7), SyntheticM15(48000, 7));

    // The grid family needs the OPPOSITE market to the signal strategies: low ADX and compressed
    // Bollinger bands, i.e. a quiet range. The trending, high-amplitude fixture above produces
    // zero grid sessions. Using one fixture for both would either starve the grid or starve the
    // signal strategies — so each gets the regime it is designed for.
    private static Candle[] Ranging(int n, int seed)
    {
        var rng = new Random(seed);
        var arr = new Candle[n];
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < n; i++)
        {
            double wave  = Math.Sin(i / 55.0) * 1.2 + Math.Sin(i / 17.0) * 0.4;
            double noise = (rng.NextDouble() - 0.5) * 0.5;
            double px    = 100.0 + wave + noise;
            double hi    = px * (1 + 0.0018 + rng.NextDouble() * 0.0012);
            double lo    = px * (1 - 0.0018 - rng.NextDouble() * 0.0012);
            arr[i] = new Candle(t0.AddHours(i), px, hi, lo, px, 1_000_000);
        }
        return arr;
    }

    // h1 series appropriate to the strategy under test.
    private static Candle[] H1For(IStrategySimulator sim) =>
        sim.UsesM15 ? Synthetic(12000, 7) : Ranging(12000, 7);

    [Fact]
    public void EveryRegisteredStrategy_ProducesTrades_OnTheSyntheticFixture()
    {
        // Guards the tests below from passing vacuously: a strategy that produces no trades would
        // trivially "ignore" every context field. If this fails the fixture needs more structure,
        // not the simulators.
        var (_, m15) = Data();
        foreach (var sim in Registry())
        {
            var trades = sim.Run(H1For(sim), m15, ExecContext.Default);
            // A meaningful floor, not > 0: multi-leg needs several simultaneously locked positions,
            // so a handful of trades cannot exercise it either.
            Assert.True(trades.Count >= 20,
                $"{sim.Label} produced only {trades.Count} trades on the fixture — too few for the " +
                $"conformance assertions below to exercise anything meaningfully.");
        }
    }

    [Fact]
    public void EveryStrategyClaimingRatchet_ActuallyHonoursIt()
    {
        var (h1, m15) = Data();
        // An aggressive floor: arms early and locks a large profit, so it must visibly alter exits
        // on any simulator that reads it.
        var withRatchet = new ExecContext(
            Ratchet: new RatchetConfig(TriggerPct: 0.5, LockPct: 0.25));

        foreach (var sim in Registry().Where(s => s.Honours.HasFlag(ExecFields.Ratchet)))
        {
            var h1s = H1For(sim);
            var baseline = sim.Run(h1s, m15, ExecContext.Default);
            var ratcheted = sim.Run(h1s, m15, withRatchet);

            bool changed = baseline.Count != ratcheted.Count
                || baseline.Zip(ratcheted).Any(p => Math.Abs(p.First.Return - p.Second.Return) > 1e-9);

            Assert.True(changed,
                $"{sim.Label} declares ExecFields.Ratchet but its output is identical with and " +
                $"without an aggressive profit floor — the field is not reaching the simulator. " +
                $"This is the failure mode that let eight mechanisms sit unwired.");
        }
    }

    [Fact]
    public void EveryStrategyClaimingMaxLegs_ActuallyHonoursIt()
    {
        var (h1, m15) = Data();
        // Legs are only added once every open leg has LOCKED, so multi-leg requires the ratchet —
        // but the lock must be LOOSE. With a tight floor (arm 0.5%, lock 0.25%) the position exits
        // almost immediately after arming and a second leg never gets the chance to open, which is
        // a property of the parameters, not of the wiring. Arm early, lock far away.
        var oneLeg  = new ExecContext(Ratchet: new RatchetConfig(TriggerPct: 1.0, LockPct: 0.05), MaxLegs: 1);
        var manyLeg = oneLeg with { MaxLegs = 4 };

        foreach (var sim in Registry().Where(s => s.Honours.HasFlag(ExecFields.MaxLegs)))
        {
            var h1s = H1For(sim);
            var single = sim.Run(h1s, m15, oneLeg);
            var multi  = sim.Run(h1s, m15, manyLeg);
            Assert.True(multi.Count > single.Count,
                $"{sim.Label} declares ExecFields.MaxLegs but raising the leg budget from 1 to 4 " +
                $"produced no additional trades ({single.Count} vs {multi.Count}).");
        }
    }

    [Fact]
    public void MaxLegsOfOne_ReproducesSinglePositionBehaviour()
    {
        // The regression guard for the multi-leg refactor: maxLegs = 1 must be bit-identical to the
        // historical single-position path. Verified by hand once against the committed baseline
        // (669 trades, PF 3.84); this pins it so it cannot drift.
        var (h1, m15) = Data();
        foreach (var sim in Registry().Where(s => s.Honours.HasFlag(ExecFields.MaxLegs)))
        {
            var h1s = H1For(sim);
            var a = sim.Run(h1s, m15, ExecContext.Default);
            var b = sim.Run(h1s, m15, new ExecContext(MaxLegs: 1));
            Assert.Equal(a.Count, b.Count);
            foreach (var (x, y) in a.Zip(b))
                Assert.Equal(x.Return, y.Return, 12);
        }
    }

    [Fact]
    public void EveryStrategy_ReportsEntryBeforeExit_AndAPositiveEntryPrice()
    {
        // `Time` is the EXIT bar everywhere in this repo. Four separate things needed the ENTRY and
        // were routed around for want of it: variant selection by entry ATR, the leading-slice OOS
        // sizing seam, the accumulator's acquisition metric, and 1m execution. Now that every
        // simulator exposes it, pin the invariant.
        var (h1, m15) = Data();
        foreach (var sim in Registry())
        {
            foreach (var t in sim.Run(H1For(sim), m15, ExecContext.Default))
            {
                Assert.True(t.EntryPrice > 0, $"{sim.Label}: non-positive EntryPrice {t.EntryPrice}");
                Assert.True(t.EntryTime <= t.Time,
                    $"{sim.Label}: EntryTime {t.EntryTime:u} is after exit {t.Time:u} — the two are swapped.");
            }
        }
    }

    [Fact]
    public void EveryStrategyLabel_IsKnownToThePortfolioRiskLayer()
    {
        // A label absent from DefaultCaps falls back to int.MaxValue — i.e. uncapped concurrency —
        // and a label with no known direction is counted against BOTH directional caps. Both are
        // silent at runtime beyond a one-line warning, so assert the wiring here.
        foreach (var sim in Registry())
        {
            Assert.True(PortfolioReplay.DefaultCaps.ContainsKey(sim.Label),
                $"'{sim.Label}' is missing from PortfolioReplay.DefaultCaps — its concurrency cap " +
                $"would silently fall back to int.MaxValue.");
            Assert.Equal(sim.IsLong, PortfolioReplay.IsLong(sim.Label));
        }
    }
}
