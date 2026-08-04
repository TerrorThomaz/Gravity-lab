using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class SlippageModelTests
{
    [Fact]
    public void Slippage_ZeroBps_MatchesNoSlippage()
    {
        var trades = new List<(DateTime, double, double, TimeSpan)>
        {
            (new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 5.0, 0.5, TimeSpan.FromHours(48)),
            (new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 3.0, 0.5, TimeSpan.FromHours(48)),
        };
        var noSlip = Simulator.SimulatePortfolioExposureCapped(trades, slippageBps: 0.0);
        var zeroSlip = Simulator.SimulatePortfolioExposureCapped(trades, slippageBps: 0.0);
        Assert.Equal(noSlip.EndBalance, zeroSlip.EndBalance, 10);
    }

    [Fact]
    public void Slippage_ReducesEndBalance()
    {
        var trades = new List<(DateTime, double, double, TimeSpan)>
        {
            (new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 5.0, 0.5, TimeSpan.FromHours(48)),
            (new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 3.0, 0.5, TimeSpan.FromHours(48)),
            (new DateTime(2024, 1, 3, 0, 0, 0, DateTimeKind.Utc), 4.0, 0.5, TimeSpan.FromHours(48)),
        };
        var noSlip = Simulator.SimulatePortfolioExposureCapped(trades, slippageBps: 0.0);
        var withSlip = Simulator.SimulatePortfolioExposureCapped(trades, slippageBps: 10.0);
        Assert.True(withSlip.EndBalance < noSlip.EndBalance,
            $"Slippage should reduce balance: {withSlip.EndBalance:F4} vs {noSlip.EndBalance:F4}");
    }

    [Fact]
    public void Slippage_ProportionalToPositionSize()
    {
        var trades = new List<(DateTime, double, double, TimeSpan)>
        {
            (new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0.0, 0.5, TimeSpan.FromHours(48)),
        };
        var noSlip = Simulator.SimulatePortfolioExposureCapped(trades, slippageBps: 0.0);
        var withSlip = Simulator.SimulatePortfolioExposureCapped(trades, slippageBps: 5.0);
        double slipCost = noSlip.EndBalance - withSlip.EndBalance;
        Assert.True(slipCost > 0, $"Slip cost should be positive, got {slipCost:F6}");
        double expectedSlip = 100.0 * 0.30 * 0.5 * (5.0 / 10000.0);
        Assert.True(Math.Abs(slipCost - expectedSlip) < 0.01,
            $"Slip cost {slipCost:F4} should be near {expectedSlip:F4}");
    }

    [Fact]
    public void Slippage_StrategyAware_ReducesEndBalance()
    {
        var trades = new List<(DateTime, double, double, TimeSpan, string)>
        {
            (new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), 5.0, 0.5, TimeSpan.FromHours(48), "fade_short"),
            (new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), 3.0, 0.5, TimeSpan.FromHours(48), "diplong"),
        };
        var noSlip = Simulator.SimulatePortfolioExposureCapped(trades, slippageBps: 0.0);
        var withSlip = Simulator.SimulatePortfolioExposureCapped(trades, slippageBps: 10.0);
        Assert.True(withSlip.EndBalance < noSlip.EndBalance,
            $"Strategy-aware slippage should reduce balance: {withSlip.EndBalance:F4} vs {noSlip.EndBalance:F4}");
    }
}
