using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The simulator has no liquidation model. That omission is defensible only while gross
// notional stays below ~1.0x equity — at Config.MaxTotalExposurePct = 0.30 a maintenance-margin
// breach could never bind before a modelled stop. Above 1.0x the omission becomes a silent
// falsification: backtests keep printing clean stop-outs on paths a real account would have
// been liquidated out of. The guard makes that failure loud instead.
public class ExposureCapGuardTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<(DateTime, double, double, TimeSpan)> Trades() =>
    [
        (T0,            5.0, 0.5, TimeSpan.FromHours(48)),
        (T0.AddDays(1), 3.0, 0.5, TimeSpan.FromHours(48)),
    ];

    [Fact]
    public void ProductionCap_IsWellInsideTheSupportedRange()
    {
        Assert.True(Config.MaxTotalExposurePct <= 1.0,
            $"Config.MaxTotalExposurePct = {Config.MaxTotalExposurePct} exceeds what a simulator " +
            "with no liquidation model can honestly report.");
        Assert.Equal(0.30, Config.MaxTotalExposurePct, 10);
    }

    [Theory]
    [InlineData(0.05)]
    [InlineData(0.30)]
    [InlineData(1.0)]
    public void CapAtOrBelowOne_IsAccepted(double cap)
    {
        var r = Simulator.SimulatePortfolioExposureCapped(Trades(), maxTotalExposurePct: cap);
        Assert.Equal(2, r.TradesCount);
    }

    [Theory]
    [InlineData(1.0001)]
    [InlineData(3.0)]
    [InlineData(10.0)]
    public void CapAboveOne_Throws_PlainOverload(double cap)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Simulator.SimulatePortfolioExposureCapped(Trades(), maxTotalExposurePct: cap));
        Assert.Contains("liquidation model", ex.Message);
    }

    [Fact]
    public void CapAboveOne_Throws_StrategyAwareOverload()
    {
        var trades = new List<(DateTime, double, double, TimeSpan, string)>
        {
            (T0, 5.0, 0.5, TimeSpan.FromHours(48), "fade_short"),
        };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Simulator.SimulatePortfolioExposureCapped(trades, maxTotalExposurePct: 2.0));
        Assert.Contains("liquidation model", ex.Message);
    }

    [Fact]
    public void CapAboveOne_Throws_RiskCapOverload()
    {
        var trades = new List<(DateTime, double, double, double, TimeSpan)>
        {
            (T0, 5.0, 0.5, 0.05, TimeSpan.FromHours(48)),
        };
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Simulator.SimulatePortfolioExposureCapped(trades, maxTotalExposurePct: 2.0));
        Assert.Contains("liquidation model", ex.Message);
    }

    [Fact]
    public void CapAboveOne_Throws_CurveOverload()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Simulator.SimulateExposureCappedWithCurve(Trades(), maxTotalExposurePct: 2.0));
        Assert.Contains("liquidation model", ex.Message);
    }

    // The guard must fire before the empty-list early return, or a caller could smuggle an
    // unsupportable cap through on a run that happens to produce no trades.
    [Fact]
    public void CapAboveOne_Throws_EvenWithNoTrades()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Simulator.SimulatePortfolioExposureCapped(
                new List<(DateTime, double, double, TimeSpan)>(), maxTotalExposurePct: 5.0));
    }
}
