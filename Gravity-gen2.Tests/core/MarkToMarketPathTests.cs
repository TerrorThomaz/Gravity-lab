using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Linear accrual carries a position as a straight line from 0 to its final return. A straight line
// has almost no variance and no interior trough, so it (a) hides every intra-hold drawdown and
// (b) inflates any Sharpe computed off the curve — worst for the longest holds, which is the grid
// family. edgetest's whole per-strategy ranking rests on this curve, so the bias is not cosmetic.
public class MarkToMarketPathTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // One position: opens at T0, closes 10 days later at +5%. Its price dives 20% at day 5 and
    // recovers. A path-aware mark must show that trough; linear accrual cannot.
    private static double UnrealisedPct(DateTime t)
    {
        double day = (t - T0).TotalDays;
        return day <= 5 ? -20.0 * (day / 5.0)          // down to -20% by day 5
                        : -20.0 + 25.0 * ((day - 5) / 5.0);  // back up to +5% by day 10
    }

    [Fact]
    public void LinearAccrual_HidesAnIntraHoldDrawdown()
    {
        var trades = new[] { (T0, 5.0, TimeSpan.FromDays(10), 1000.0) };
        var flat = MarkToMarket.Compute(trades, startBalance: 1000.0, step: TimeSpan.FromDays(1));

        // The position is the whole book and it dived 20%, yet linear accrual reports ~nothing.
        Assert.True(flat.MaxDrawdownPct < 1.0,
                    $"expected the linear model to hide the dive, saw {flat.MaxDrawdownPct:F2}%");
    }

    [Fact]
    public void PathAware_ShowsTheInterimDrawdownTheTradeActuallyTook()
    {
        var trades = new[]
        {
            new MarkToMarket.Position(T0, 5.0, TimeSpan.FromDays(10), 1000.0, UnrealisedPct),
        };
        var path = MarkToMarket.Compute(trades, startBalance: 1000.0, step: TimeSpan.FromDays(1));

        // 1000 EUR position down 20% against 1000 EUR of equity -> a ~20% peak-to-trough.
        Assert.InRange(path.MaxDrawdownPct, 15.0, 25.0);
    }

    // The existing caller (Simulator.SimulatePortfolioExposureCapped) passes no paths, and its
    // numbers must not move: the opt-in has to be a genuine no-op when omitted.
    [Fact]
    public void OmittingThePath_ReproducesLinearAccrualExactly()
    {
        var tuples = new[]
        {
            (T0, 5.0, TimeSpan.FromDays(10), 1000.0),
            (T0.AddDays(2), -3.0, TimeSpan.FromDays(4), 500.0),
        };
        var viaTuple = MarkToMarket.Compute(tuples, 1000.0, TimeSpan.FromDays(1));
        var viaRecord = MarkToMarket.Compute(
            tuples.Select(t => new MarkToMarket.Position(t.Item1, t.Item2, t.Item3, t.Item4)).ToList(),
            1000.0, TimeSpan.FromDays(1));

        Assert.Equal(viaTuple.MaxDrawdownPct, viaRecord.MaxDrawdownPct, 12);
        Assert.Equal(viaTuple.Curve.Count, viaRecord.Curve.Count);
        for (int i = 0; i < viaTuple.Curve.Count; i++)
            Assert.Equal(viaTuple.Curve[i].Equity, viaRecord.Curve[i].Equity, 12);
    }

    // Whatever the interior path, the position must still be BOOKED at its recorded net return —
    // the path marks the journey, the recorded return settles it (and carries the costs).
    [Fact]
    public void PathAware_StillSettlesAtTheRecordedNetReturn()
    {
        var trades = new[]
        {
            new MarkToMarket.Position(T0, 5.0, TimeSpan.FromDays(10), 1000.0, UnrealisedPct),
        };
        var path = MarkToMarket.Compute(trades, 1000.0, TimeSpan.FromDays(1));
        Assert.Equal(1050.0, path.Curve[^1].Equity, 6);
    }
}
