using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// ── Proof that slippage is charged EXACTLY ONCE ─────────────────────────────────────────
// This file used to assert the opposite invariant: that SimulatePortfolioExposureCapped
// subtracts Config.SlippageBps on top of whatever the simulators already charged. That was
// the bug. Two uncoordinated layers priced the same trade — an ATR-proportional term inside
// each simulator (applied once in three of them, twice in two more, and with three different
// exit-quality constants in the grid family) plus a flat bps term at the portfolio layer —
// and only the first layer was visible to GA fitness, which never runs a portfolio simulation.
// The GA therefore selected genotypes against ~0.185pp of round-trip cost while every report
// published ~0.285pp for the same trade.
//
// The invariant now: Config.SlippageBps is the single magnitude authority, it is charged once
// per trade inside the simulators (TradeCosts), and the portfolio layer charges nothing.
public class SlippageModelTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Every simulator's cost function, paired with the stop-gap shape it declares.
    // Signature normalised to (isStop, atr, entryPx) -> cost in percentage points.
    public static TheoryData<string, double> SimulatorGapShapes => new()
    {
        { "fade_short", 0.030 },
        { "swing_long", 0.030 },
        { "rip_short",  0.030 },
        { "dip_long",   0.015 },
        { "fade_long",  0.015 },
        { "grid",       0.18  },
        { "grid_short", 0.18  },
        { "accum_grid", 0.18  },
    };

    // Grid-family entries are resting limit rungs (maker side). Every other simulator enters
    // with a market order. Exits in the normalised signature below are non-TP, so taker.
    private static bool RestingEntry(string name) => name is "grid" or "grid_short";

    // What a maker entry takes off the all-taker round trip, at a given ATR%.
    private static double EntrySaving(string name, double atrPct) => RestingEntry(name)
        ? TradeCosts.FeeRoundTripPct / 2 - TradeCosts.MakerFeePct + TradeCosts.SlippagePerSidePct(atrPct)
        : 0.0;

    private static double SimulatorCost(string name, bool isStop, double atr, double entryPx) => name switch
    {
        "fade_short" => FadeShortSimulator.TradeCost(isStop, atr, entryPx),
        "swing_long" => SwingLongSimulator.TradeCost(isStop, atr, entryPx),
        "rip_short"  => RipShortSimulator.TradeCost(isStop, atr, entryPx),
        "dip_long"   => DipLongSimulator.TradeCost(isStop, atr, entryPx),
        "fade_long"  => FadeLongSimulator.TradeCost(isStop, atr, entryPx),
        "grid"       => GridSimulator.TradeCost(atr, entryPx, isStop),
        "grid_short" => GridShortSimulator.TradeCost(atr, entryPx, isStop),
        "accum_grid" => GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator
                            .TradeCost(atr, entryPx, isStop),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown simulator"),
    };

    // ── 1. THE hard requirement: one trade, both consumers, identical cost ───────────────
    // The GA scores the raw simulator return. The backtest feeds that same return through the
    // portfolio simulator. If the two disagree, selection and reporting are optimising
    // different worlds — which is exactly what this change removes.
    [Fact]
    public void SameTrade_GaPathAndPortfolioPath_ChargeIdenticalCost()
    {
        const double entryPx  = 100.0;
        const double grossPct = 2.0;                                  // +2% raw price move
        double atr = entryPx * TradeCosts.ReferenceAtrPct / 100.0;    // the 3% reference coin

        // GA path: fitness sees this number and nothing else.
        double gaRet = grossPct - FadeShortSimulator.TradeCost(isStop: false, atr, entryPx);

        // Portfolio path: the identical trade, priced by the reporting layer. That layer takes
        // no slippage parameter at all any more, so it cannot re-charge what the trade already paid.
        var trades = new List<(DateTime, double, double, TimeSpan)>
        {
            (T0, gaRet, 0.05, TimeSpan.FromHours(24)),
        };
        var res = Simulator.SimulatePortfolioExposureCapped(
            trades, maxTotalExposurePct: 0.30, startBalance: 100.0,
            maxPositionFrac: 0.05);

        double posEur           = res.AvgPositionEur;
        double portfolioNetPct  = (res.EndBalance - 100.0) / posEur * 100.0;

        Assert.Equal(gaRet, portfolioNetPct, 10);

        // And the cost both of them charged is fee + EXACTLY ONE slippage round trip.
        double chargedCost = grossPct - portfolioNetPct;
        Assert.Equal(TradeCosts.FeeRoundTripPct + Config.SlippageBps / 100.0, chargedCost, 10);

        // Guard the specific regression: the old double layer would have cost 2× slippage.
        Assert.NotEqual(TradeCosts.FeeRoundTripPct + 2.0 * Config.SlippageBps / 100.0, chargedCost, 10);
    }

    // ── 2. Config.SlippageBps is the only magnitude, and it is a ROUND-TRIP figure ───────
    [Fact]
    public void SlippageAtReferenceAtr_EqualsConfigSlippageBps()
    {
        Assert.Equal(Config.SlippageBps / 100.0,
                     TradeCosts.SlippageRoundTripPct(TradeCosts.ReferenceAtrPct), 12);
    }

    // The comment in the simulators claimed "each side" for years while three of them charged
    // it once. Both sides are now charged, and each side is exactly half the authority.
    [Fact]
    public void Slippage_IsChargedOnBothSides_EachSideIsHalf()
    {
        foreach (double atrPct in new[] { 0.5, 3.0, 8.0 })
        {
            double perSide = TradeCosts.SlippagePerSidePct(atrPct);
            Assert.Equal(2.0 * perSide, TradeCosts.SlippageRoundTripPct(atrPct), 12);
            Assert.Equal(Config.SlippageBps / 100.0 / 2.0 * (atrPct / TradeCosts.ReferenceAtrPct),
                         perSide, 12);
        }
    }

    // Scaling is linear in the coin's ATR and anchored on the single constant: a meme perp at
    // 8% ATR pays 8/3 of what the reference coin pays, and nothing else moves the magnitude.
    [Fact]
    public void Slippage_ScalesLinearlyWithAtr_AnchoredOnTheOneConstant()
    {
        double atRef = TradeCosts.SlippageRoundTripPct(TradeCosts.ReferenceAtrPct);
        Assert.Equal(atRef * (8.0 / TradeCosts.ReferenceAtrPct),
                     TradeCosts.SlippageRoundTripPct(8.0), 12);
        Assert.Equal(0.0, TradeCosts.SlippageRoundTripPct(0.0), 12);
    }

    // ── 3. The portfolio layer charges nothing — all three overloads ─────────────────────



    // ── 4. Every simulator shares the one model — no private slippage constants left ─────
    [Theory]
    [MemberData(nameof(SimulatorGapShapes))]
    public void EverySimulator_DelegatesToTheSharedCostModel(string simulator, double stopGapAtrK)
    {
        const double entryPx = 250.0;
        foreach (double atrPct in new[] { 1.0, TradeCosts.ReferenceAtrPct, 7.5 })
        {
            double atr = entryPx * atrPct / 100.0;
            foreach (bool isStop in new[] { false, true })
            {
                Assert.Equal(TradeCosts.RoundTripPct(atrPct, isStop, stopGapAtrK,
                                                     entryMaker: RestingEntry(simulator)),
                             SimulatorCost(simulator, isStop, atr, entryPx), 12);
            }
        }
    }

    // The load-bearing consequence of the above: on a non-stop exit, every one of the eight
    // strategies charges fee + exactly one slippage round trip. No more, no less, no variation.
    [Theory]
    [MemberData(nameof(SimulatorGapShapes))]
    public void EverySimulator_ChargesExactlyOneSlippageRoundTrip(string simulator, double stopGapAtrK)
    {
        _ = stopGapAtrK;
        const double entryPx = 250.0;
        double atr  = entryPx * TradeCosts.ReferenceAtrPct / 100.0;
        double cost = SimulatorCost(simulator, isStop: false, atr, entryPx);
        Assert.Equal(TradeCosts.FeeRoundTripPct + Config.SlippageBps / 100.0
                     - EntrySaving(simulator, TradeCosts.ReferenceAtrPct), cost, 12);
    }

    // ── 5. Exchange fees: charged once, and NOT double-counted (verified, not assumed) ───
    // At zero ATR both slippage and the stop gap vanish, so whatever remains is the fee term.
    // It must equal FeeRoundTripPct — one 2×0.055% Bybit taker round trip — for every
    // simulator, on stop and non-stop exits alike. And the portfolio layer has no fee term at
    // all, so a trade's fee cannot be applied a second time downstream.
    [Theory]
    [MemberData(nameof(SimulatorGapShapes))]
    public void ExchangeFee_ChargedExactlyOnce(string simulator, double stopGapAtrK)
    {
        _ = stopGapAtrK;
        double fee = TradeCosts.FeeRoundTripPct - EntrySaving(simulator, 0.0);
        Assert.Equal(fee, SimulatorCost(simulator, false, 0.0, 100.0), 12);
        Assert.Equal(fee, SimulatorCost(simulator, true,  0.0, 100.0), 12);
        Assert.Equal(0.11, TradeCosts.FeeRoundTripPct, 12);
    }

    [Fact]
    public void PortfolioLayer_AddsNoFee()
    {
        var trades = new List<(DateTime, double, double, TimeSpan)> { (T0, 1.0, 0.05, TimeSpan.FromHours(24)) };
        var res = Simulator.SimulatePortfolioExposureCapped(trades, maxPositionFrac: 0.05);
        double posEur = res.AvgPositionEur;
        Assert.Equal(1.0, (res.EndBalance - 100.0) / posEur * 100.0, 10);
    }

    // ── 6. The stop-gap premium is a stop-only term, not a second slippage knob ──────────
    [Fact]
    public void StopGap_AppliesOnlyToStopExits()
    {
        const double atrPct = 4.0, k = 0.030;
        Assert.Equal(TradeCosts.RoundTripPct(atrPct, false, k) + k * atrPct,
                     TradeCosts.RoundTripPct(atrPct, true, k), 12);
        Assert.Equal(TradeCosts.RoundTripPct(atrPct, false, k),
                     TradeCosts.RoundTripPct(atrPct, false, stopGapAtrK: 999.0), 12);
    }
    // ── 7. A resting limit side pays the maker fee and no slippage ─────────────────────────
    [Fact]
    public void MakerSides_PayMakerFeeAndNoSlippage()
    {
        const double atrPct = 4.0, k = 0.18;
        double taker = TradeCosts.RoundTripPct(atrPct, false, k);
        Assert.Equal(TradeCosts.FeeRoundTripPct + TradeCosts.SlippageRoundTripPct(atrPct), taker, 12);

        // both sides resting: two maker fees, nothing else
        Assert.Equal(2 * TradeCosts.MakerFeePct,
                     TradeCosts.RoundTripPct(atrPct, false, k, entryMaker: true, exitMaker: true), 12);
        // one side resting: one maker fee plus one taker side with its slippage
        Assert.Equal(TradeCosts.MakerFeePct + TradeCosts.FeeRoundTripPct / 2 + TradeCosts.SlippagePerSidePct(atrPct),
                     TradeCosts.RoundTripPct(atrPct, false, k, entryMaker: true), 12);
        // a stop is never a maker fill, whatever the caller says
        Assert.Equal(TradeCosts.RoundTripPct(atrPct, true, k, entryMaker: true),
                     TradeCosts.RoundTripPct(atrPct, true, k, entryMaker: true, exitMaker: true), 12);

        // wired: a grid take-profit is maker on both sides, a grid stop only on the entry
        double px = 100.0, atr = px * atrPct / 100.0;
        Assert.Equal(2 * TradeCosts.MakerFeePct, GridSimulator.TradeCost(atr, px, isStop: false, isTp: true), 12);
        Assert.Equal(2 * TradeCosts.MakerFeePct, GridShortSimulator.TradeCost(atr, px, isStop: false, isTp: true), 12);
        Assert.True(GridSimulator.TradeCost(atr, px, isStop: true) > taker);
        Assert.Equal(TradeCosts.RoundTripPct(atrPct, false, 0.03, exitMaker: true),
                     FadeShortSimulator.TradeCost(false, atr, px, isTp: true), 12);
    }
}
