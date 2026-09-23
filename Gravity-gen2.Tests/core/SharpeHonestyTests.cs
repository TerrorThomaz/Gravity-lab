using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Simulator.SharpeRatio returned exactly 0 for any series with profit factor < 1.3, so a losing
// strategy reported "Sharpe 0.00" — indistinguishable from a flat one, and never negative. It is
// read at 63 report sites and none of them used it as a screen, so the floor only ever hid losses.
public class SharpeHonestyTests
{
    private static List<double> Losing() =>
        Enumerable.Range(0, 60).Select(i => i % 3 == 0 ? 2.0 : -2.0).ToList();

    private static List<double> Winning() =>
        Enumerable.Range(0, 60).Select(i => i % 3 == 0 ? -2.0 : 2.0).ToList();

    [Fact]
    public void SharpeRatio_IsNegativeForALosingSeries()
    {
        double sharpe = Simulator.SharpeRatio(Losing(), 2880);
        Assert.True(sharpe < 0, $"a series that loses money reported Sharpe {sharpe:F4}");
    }

    // The distortion in one assertion: a losing book and a mildly profitable one both printed 0.00,
    // so the report could not tell them apart.
    [Fact]
    public void SharpeRatio_SeparatesALosingBookFromAProfitableOne()
    {
        Assert.True(Simulator.SharpeRatio(Winning(), 2880) > Simulator.SharpeRatio(Losing(), 2880));
    }

    [Fact]
    public void SortinoRatio_IsNegativeForALosingSeries()
    {
        double sortino = Simulator.SortinoRatio(Losing(), 2880);
        Assert.True(sortino < 0, $"a series that loses money reported Sortino {sortino:F4}");
    }

    // The screen that the floor was standing in for, kept explicit so a caller that wants
    // "profitable enough to be worth quoting" can still say so without the statistic lying.
    [Fact]
    public void ScreenedSharpe_StillReturnsZeroBelowTheProfitFactorFloor()
    {
        Assert.Equal(0.0, Simulator.ScreenedSharpeRatio(Losing(), 2880), 12);
        Assert.Equal(Simulator.SharpeRatio(Winning(), 2880),
                     Simulator.ScreenedSharpeRatio(Winning(), 2880), 12);
    }
}
