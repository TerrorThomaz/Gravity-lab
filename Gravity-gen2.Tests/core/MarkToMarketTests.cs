using System;
using System.Collections.Generic;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The property that matters: mark-to-market drawdown must RESPOND to concurrency, because that is
// exactly what the realized-at-close number cannot do.
public class MarkToMarketTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Step = TimeSpan.FromHours(1);

    [Fact]
    public void SimultaneousLosers_ShowDrawdown_ThatSequentialOnesDoNot()
    {
        // Identical trade SET — four losers and four winners, same sizes — differing only in
        // whether the losers overlap. Concurrent losers dig one deep trough; interleaved ones are
        // repaired by each winner before the next loss lands. Same trades, same net result, very
        // different risk, and realized-at-close accounting cannot tell them apart.
        //
        // (An earlier version of this test used losers ONLY, which was a bad test rather than a
        // failing implementation: with no recoveries, sequential losses accumulate to exactly the
        // same trough as simultaneous ones. The recoveries are what make concurrency observable.)
        var overlapping = new List<(DateTime, double, TimeSpan, double)>();
        var sequential  = new List<(DateTime, double, TimeSpan, double)>();
        for (int i = 0; i < 4; i++)
        {
            overlapping.Add((T0,                     -10.0, TimeSpan.FromHours(10), 10.0));
            overlapping.Add((T0.AddHours(20 + i),    +10.0, TimeSpan.FromHours(10), 10.0));

            sequential.Add((T0.AddHours(i * 40),     -10.0, TimeSpan.FromHours(10), 10.0));
            sequential.Add((T0.AddHours(i * 40 + 12),+10.0, TimeSpan.FromHours(10), 10.0));
        }

        var o = MarkToMarket.Compute(overlapping, 100.0, Step);
        var s = MarkToMarket.Compute(sequential,  100.0, Step);

        Assert.True(o.MaxDrawdownPct > s.MaxDrawdownPct,
            $"overlapping losers ({o.MaxDrawdownPct:F2}%) must show MORE drawdown than the same " +
            $"trades run sequentially ({s.MaxDrawdownPct:F2}%) — if these are equal the metric is " +
            $"still blind to concurrency, which is the entire reason this class exists.");
        Assert.True(o.PeakGrossExposurePct > s.PeakGrossExposurePct);
    }

    [Fact]
    public void DrawdownScalesWithPositionSize()
    {
        // The scale-invariance defect: in SimulatePortfolioExposureCapped every term is proportional
        // to balance, so percentage DD cannot move when the exposure cap changes. Here it must.
        static double DdAt(double eur)
        {
            var trades = new List<(DateTime, double, TimeSpan, double)>
            {
                (T0, -20.0, TimeSpan.FromHours(10), eur),
                (T0, -20.0, TimeSpan.FromHours(10), eur),
            };
            return MarkToMarket.Compute(trades, 100.0, Step).MaxDrawdownPct;
        }

        double small = DdAt(5.0), large = DdAt(20.0);
        Assert.True(large > small * 2.0,
            $"quadrupling position size moved drawdown only {small:F2}% -> {large:F2}%; " +
            $"this metric is supposed to be sensitive to leverage.");
    }

    [Fact]
    public void ClosedTrades_AreBookedExactlyOnce()
    {
        // A position must not be counted in both unrealized and realized at the moment it closes.
        var trades = new List<(DateTime, double, TimeSpan, double)>
        {
            (T0, 10.0, TimeSpan.FromHours(5), 10.0),
        };
        var r = MarkToMarket.Compute(trades, 100.0, Step);
        // +10% on a 10 EUR position = +1.00 EUR. A double-book would show 2.00 and hence no drawdown
        // profile at all; a profitable lone trade should simply never draw down.
        Assert.Equal(0.0, r.MaxDrawdownPct, 6);
    }

    [Fact]
    public void EmptyInput_IsHandled()
    {
        var r = MarkToMarket.Compute(new List<(DateTime, double, TimeSpan, double)>(), 100.0, Step);
        Assert.Equal(0.0, r.MaxDrawdownPct);
        Assert.Equal(0, r.Steps);
    }
}
