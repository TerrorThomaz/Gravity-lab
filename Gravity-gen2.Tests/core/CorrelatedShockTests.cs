using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class CorrelatedShockTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Step = TimeSpan.FromHours(1);

    [Fact]
    public void EquityHit_IsGrossTimesShock()
    {
        // The load-bearing arithmetic: at G% gross exposure, an X% correlated move costs G*X/100 of
        // equity. Two 25 EUR positions on a 100 EUR account = 50% gross; a -20% shock must cost 10%.
        var trades = new List<(DateTime, double, TimeSpan, double)>
        {
            (T0, 0.0, TimeSpan.FromHours(10), 25.0),
            (T0, 0.0, TimeSpan.FromHours(10), 25.0),
        };
        var rows = CorrelatedShock.Run(trades, 100.0, new[] { 20.0 }, Step);
        Assert.Single(rows);
        Assert.Equal(50.0, rows[0].GrossAtWorst,   1);
        Assert.Equal(10.0, rows[0].WorstEquityHit, 1);
    }

    [Fact]
    public void MoreConcurrency_MeansBiggerHit()
    {
        // The whole reason the exposure cap exists. Same position size, more of them open at once.
        static double Hit(int n)
        {
            var t = Enumerable.Range(0, n)
                .Select(_ => (T0, 0.0, TimeSpan.FromHours(10), 10.0)).ToList();
            return CorrelatedShock.Run(t, 100.0, new[] { 20.0 }, Step)[0].WorstEquityHit;
        }
        Assert.True(Hit(8) > Hit(2) * 3.5,
            $"eight concurrent positions ({Hit(8):F1}%) must hurt far more than two ({Hit(2):F1}%)");
    }

    [Fact]
    public void LongsAndShorts_AreNotNetted()
    {
        // Deliberate: a cascade gaps BOTH sides through their stops, so a book that nets flat on
        // paper is not protected. Returns of opposite sign must not cancel in the shock.
        var mixed = new List<(DateTime, double, TimeSpan, double)>
        {
            (T0, +5.0, TimeSpan.FromHours(10), 25.0),
            (T0, -5.0, TimeSpan.FromHours(10), 25.0),
        };
        var rows = CorrelatedShock.Run(mixed, 100.0, new[] { 20.0 }, Step);
        Assert.True(rows[0].WorstEquityHit > 9.0,
            $"opposite-signed positions netted to {rows[0].WorstEquityHit:F1}% — the harness is " +
            $"hedging away exactly the risk it exists to measure.");
    }

    [Fact]
    public void NoPositions_IsSafe()
    {
        var rows = CorrelatedShock.Run(new List<(DateTime, double, TimeSpan, double)>(),
                                       100.0, new[] { 20.0 }, Step);
        Assert.Empty(rows);
    }
}
