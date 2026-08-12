using System;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class LiquidityModelTests
{
    [Fact]
    public void Impact_GrowsWithSize_ButSublinearly()
    {
        // The whole point: cost must depend on size (TradeCosts alone does not), and it must grow
        // as sqrt rather than linearly, because liquidity replenishes while you execute.
        double bar = 1_000_000, atr = 3.0;
        double a = LiquidityModel.ImpactPct(10_000, bar, atr);   // 1%
        double b = LiquidityModel.ImpactPct(40_000, bar, atr);   // 4% — 4x the size

        Assert.True(b > a, "impact must increase with order size");
        Assert.True(b < a * 4.0, "impact must be SUBLINEAR in size (square-root law)");
        Assert.Equal(2.0, b / a, 1);   // sqrt(4) = 2
    }

    [Fact]
    public void Impact_GrowsWithVolatility()
    {
        double bar = 1_000_000;
        Assert.True(LiquidityModel.ImpactPct(10_000, bar, 8.0)
                  > LiquidityModel.ImpactPct(10_000, bar, 2.0),
            "a violent coin must cost more to execute than a calm one at the same participation");
    }

    [Fact]
    public void OversizedOrders_AreFlaggedUnrealistic()
    {
        double bar = 1_000_000;
        Assert.True(LiquidityModel.IsFillRealistic(100_000, bar));   // 10%
        Assert.False(LiquidityModel.IsFillRealistic(400_000, bar));  // 40% — you ARE the market
        Assert.Equal(250_000, LiquidityModel.MaxRealisticOrder(bar), 0);
    }

    [Fact]
    public void MissingVolume_IsZeroNotInfinity()
    {
        // A data gap must not silently zero out a strategy or produce a NaN that poisons a fold
        // score. The flat TradeCosts slippage still applies underneath.
        Assert.Equal(0.0, LiquidityModel.ImpactPct(10_000, 0, 3.0));
        Assert.Equal(0.0, LiquidityModel.ImpactPct(0, 1_000_000, 3.0));
        Assert.False(LiquidityModel.IsFillRealistic(10_000, 0));
    }
}
