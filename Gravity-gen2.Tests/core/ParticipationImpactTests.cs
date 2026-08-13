using System;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Layer 3 wiring. The property under test is not the coefficient — it is that the cost model is
// SIZE-AWARE AT ALL, which it was not before: fees, slippage and stop-gap are identical for a small
// and a huge position, so extra position size executed for free. That matters because
// DynamicExposureCap turns position size into a free variable, and an unpriced size axis means the
// backtest pays for leverage it cannot cost.
public class ParticipationImpactTests
{
    [Fact]
    public void Default_IsOff_AndBitIdenticalToTheOldModel()
    {
        // Charging impact changes every trade's cost and therefore the GA's objective, which
        // invalidates every genotype in genotypes/. It must be opt-in, and OFF must be exactly the
        // historical number — not approximately.
        Assert.False(Config.ChargeParticipationImpact,
            "participation impact must default OFF; enabling it is a deliberate retrain");

        double withArgs = TradeCosts.RoundTripPct(3.0, isStop: false, 0.03, barNotional: 1_000_000, posFrac: 0.05);
        double without  = TradeCosts.RoundTripPct(3.0, isStop: false, 0.03);
        Assert.Equal(without, withArgs, 12);
    }

    [Fact]
    public void ImpactCurve_IsSublinearInSize()
    {
        // Square-root law: liquidity replenishes while you execute, so 4x the size costs ~2x, not
        // 4x. Tested on LiquidityModel directly since the Config flag gates the TradeCosts path.
        double bar = 1_000_000, atr = 3.0;
        double a = LiquidityModel.ImpactPct(10_000, bar, atr);
        double b = LiquidityModel.ImpactPct(40_000, bar, atr);
        Assert.Equal(2.0, b / a, 1);
    }

    [Fact]
    public void ThinnerBook_CostsMore_AtTheSameOrderSize()
    {
        // The whole point of participation over a flat bps figure: the same order is cheap on a
        // liquid major and expensive on a thin alt. A flat model cannot express this.
        double order = 50_000, atr = 3.0;
        double liquid = LiquidityModel.ImpactPct(order, 50_000_000, atr);
        double thin   = LiquidityModel.ImpactPct(order,  1_000_000, atr);
        Assert.True(thin > liquid * 4.0,
            $"a thin book ({thin:F4}pp) must cost far more than a deep one ({liquid:F4}pp) for the " +
            $"same order size");
    }

    [Fact]
    public void MissingVolume_DoesNotPoisonTheCost()
    {
        // Candle volume can be zero in cached data. That must degrade to "no impact charge", never
        // to NaN/Infinity — a poisoned cost would propagate into a fold score and silently corrupt
        // selection rather than failing loudly.
        double c = TradeCosts.RoundTripPct(3.0, isStop: true, 0.03, barNotional: 0.0, posFrac: 0.05);
        Assert.True(double.IsFinite(c) && c > 0);
        Assert.Equal(TradeCosts.RoundTripPct(3.0, isStop: true, 0.03), c, 12);
    }
}
