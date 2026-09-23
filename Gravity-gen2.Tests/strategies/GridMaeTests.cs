using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The GA can only price path risk if the simulator reports it. These check the reporting end:
// GridSimulator must emit, per emitted session, how far underwater that session actually went.
public class GridMaeTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // A flat, quiet series (so the grid arms) that then sags hard and recovers. `dipAtrs` scales
    // how deep the sag goes; everything else is identical between calls.
    private static Candle[] Series(double dipFrac, int n = 900)
    {
        var c = new Candle[n];
        for (int i = 0; i < n; i++)
        {
            // Quiet oscillation to keep ADX low and BB narrow, so the grid opens.
            double baseline = 100.0 + Math.Sin(i / 9.0) * 0.35;
            // One slow excursion down and back, in the middle third.
            double dip = 0.0;
            if (i >= 400 && i < 600)
            {
                double phase = (i - 400) / 200.0;            // 0 -> 1
                dip = -dipFrac * 100.0 * Math.Sin(phase * Math.PI);
            }
            double close = baseline + dip;
            c[i] = new Candle(T0.AddHours(i), close, close + 0.15, close - 0.15, close, 1000);
        }
        return c;
    }

    private static (int Trades, double WorstMae) Run(double dipFrac)
    {
        var g = new GridGenotype
        {
            EmaPeriod = 20, BbPeriod = 20, GridLevels = 3,
            AdxThreshold = 60, BbWidthMaxPct = 99,        // permissive, so the grid arms
            GridStepAtrMult = 0.4, HardStopAtrMult = 40, BailOutAtrMult = 40,
            MaxHoldCandles = 400, TakeProfitAtrMult = 30,
            ReanchorAlpha = 0.0, RungSellFrac = 0, SlopeLookback = 20, SlopeThreshold = -1.0,
        };
        var mae = new List<double>();
        var trades = GridSimulator.GetGridSessionReturns(g, Series(dipFrac), null, mae);
        return (trades.Count, mae.Count == 0 ? 0.0 : mae.Min());
    }

    [Fact]
    public void MaeOut_IsOneEntryPerTrade_AndNeverPositive()
    {
        var g = new GridGenotype
        {
            EmaPeriod = 20, BbPeriod = 20, GridLevels = 3,
            AdxThreshold = 60, BbWidthMaxPct = 99,
            GridStepAtrMult = 0.4, HardStopAtrMult = 40, BailOutAtrMult = 40,
            MaxHoldCandles = 120, TakeProfitAtrMult = 30,
            ReanchorAlpha = 0.0, RungSellFrac = 0, SlopeLookback = 20, SlopeThreshold = -1.0,
        };
        var mae = new List<double>();
        var trades = GridSimulator.GetGridSessionReturns(g, Series(0.05), null, mae);

        Assert.NotEmpty(trades);
        Assert.Equal(trades.Count, mae.Count);
        Assert.All(mae, m => Assert.True(m <= 0.0, $"excursion {m} is not adverse"));
    }

    // The property that makes the term worth having: a deeper dive must report a deeper excursion,
    // even when the session ends in the same place.
    [Fact]
    public void DeeperPriceDive_ReportsDeeperAdverseExcursion()
    {
        var shallow = Run(0.02);
        var deep    = Run(0.08);

        Assert.True(shallow.Trades > 0 && deep.Trades > 0, "both fixtures must produce sessions");
        Assert.True(deep.WorstMae < shallow.WorstMae - 1e-9,
                    $"a 4x deeper dive reported {deep.WorstMae:F3}% against {shallow.WorstMae:F3}%");
    }

    // GridShort is the mirror: a SHORT grid loses when price RISES, so its adverse extreme is the
    // bar high. Same contract, opposite sign convention inside the simulator.
    [Fact]
    public void GridShort_ReportsAdverseExcursionsToo()
    {
        var g = new GridGenotype
        {
            EmaPeriod = 20, BbPeriod = 20, GridLevels = 3,
            AdxThreshold = 60, BbWidthMaxPct = 99,
            GridStepAtrMult = 0.4, HardStopAtrMult = 40, BailOutAtrMult = 40,
            MaxHoldCandles = 120, TakeProfitAtrMult = 30,
            ReanchorAlpha = 0.0, RungSellFrac = 0, SlopeLookback = 20, SlopeThreshold = 1.0,
        };
        var mae = new List<double>();
        var trades = GridShortSimulator.GetGridShortSessionReturns(g, Series(0.05), null, mae);

        Assert.NotEmpty(trades);
        Assert.Equal(trades.Count, mae.Count);
        Assert.All(mae, m => Assert.True(m <= 0.0, $"excursion {m} is not adverse"));
    }

    // Omitting the out-list must change nothing — every existing caller passes no list.
    [Fact]
    public void OmittingMaeOut_ReturnsTheSameTrades()
    {
        var g = new GridGenotype
        {
            EmaPeriod = 20, BbPeriod = 20, GridLevels = 3,
            AdxThreshold = 60, BbWidthMaxPct = 99,
            GridStepAtrMult = 0.4, HardStopAtrMult = 40, BailOutAtrMult = 40,
            MaxHoldCandles = 120, TakeProfitAtrMult = 30,
            ReanchorAlpha = 0.0, RungSellFrac = 0, SlopeLookback = 20, SlopeThreshold = -1.0,
        };
        var series = Series(0.05);
        var withOut = GridSimulator.GetGridSessionReturns(g, series, null, new List<double>());
        var without = GridSimulator.GetGridSessionReturns(g, series, null);

        Assert.Equal(without.Count, withOut.Count);
        for (int i = 0; i < without.Count; i++)
            Assert.Equal(without[i].Return, withOut[i].Return, 12);
    }
}
