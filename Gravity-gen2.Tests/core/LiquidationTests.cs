using System;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class LiquidationTests
{
    [Fact]
    public void AtCurrentExposure_LiquidationIsInert()
    {
        // The claim the whole no-liquidation-model argument rests on. At 30% gross the book is
        // 0.30x levered, so liquidation sits at a >300% adverse move — unreachable. If this ever
        // fails, the cap has moved into territory the rest of the simulator cannot represent.
        var c = Liquidation.CheckBook(30.0, new[] { 2.0, 3.0, 5.0 });
        Assert.False(c.AnyBinding);
        Assert.True(Liquidation.LiquidationMovePct(0.30, Liquidation.MmrAlts) > 100.0);
    }

    [Fact]
    public void AtTenX_ATypicalStopIsStillInsideLiquidation_ButThin()
    {
        // 10x: liquidation at ~9% adverse. A 2% stop still fires first, but the margin is thin
        // enough that a gap through the stop can reach the liquidation level.
        double liq = Liquidation.LiquidationMovePct(10.0, Liquidation.MmrAlts);
        Assert.InRange(liq, 8.0, 10.0);
        Assert.True(Liquidation.StopFiresFirst(2.0, 10.0, Liquidation.MmrAlts));
        Assert.False(Liquidation.StopFiresFirst(12.0, 10.0, Liquidation.MmrAlts));
    }

    [Fact]
    public void MaxSafeLeverage_ShrinksAsStopsWiden()
    {
        // The number to consult before levering a strategy: a wider stop must permit less leverage.
        double tight = Liquidation.MaxSafeLeverage(1.0, Liquidation.MmrAlts);
        double wide  = Liquidation.MaxSafeLeverage(8.0, Liquidation.MmrAlts);
        Assert.True(tight > wide,
            $"a 1% stop ({tight:F1}x) must permit more leverage than an 8% stop ({wide:F1}x)");
        Assert.True(wide >= 1.0, "never returns sub-1x — that would be nonsense as a ceiling");
    }

    [Fact]
    public void UncappedOosDemand_WouldBind()
    {
        // The measured worst case: OOS uncapped notional demand peaked near 950% of equity. At
        // 9.5x, liquidation sits around a 10% adverse move — inside a normal crypto day, and
        // inside several strategies' stop distances. This is what the cap is preventing.
        var c = Liquidation.CheckBook(950.0, new[] { 2.0, 5.0, 12.0 });
        Assert.True(c.AnyBinding,
            "at 9.5x book leverage a 12% stop must be unreachable — the exchange closes first");
        Assert.True(Liquidation.LiquidationMovePct(9.5, Liquidation.MmrAlts) < 12.0);
    }
}
