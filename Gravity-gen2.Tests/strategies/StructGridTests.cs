using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Exit mechanics of the frozen structural-default grid on hand-built paths (mirrors research/rulepath.py).
public class StructGridTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Flat at 100 with a 1-wide range (ATR14 → 1), then bar `dip` trades down to `low`.
    private static Candle[] Flat(int n, int dip = -1, double low = 100, double[]? closes = null)
    {
        var a = new Candle[n];
        for (int i = 0; i < n; i++)
        {
            double c = closes != null && i < closes.Length && closes[i] > 0 ? closes[i] : 100.0;
            double l = i == dip ? low : c - 0.5;
            a[i] = new Candle(T0.AddHours(i), c, Math.Max(c + 0.5, c), Math.Min(l, c - 0.5), c, 1_000_000);
        }
        return a;
    }

    [Fact]
    public void Long_FillsOnlyWhenTradedThrough_AndExitsAt24hClose()
    {
        var bars = Flat(80, dip: 40, low: 96.9);                 // level = 100 − 3·1 = 97; 96.9 trades through 97·(1−5bp)
        var r = StructGridSimulator.GetReturns(bars, isLong: true);
        Assert.Single(r);
        Assert.Equal(T0.AddHours(40), r[0].EntryTime);
        Assert.Equal(T0.AddHours(64), r[0].Time);                // close of fill bar + 24
        Assert.True(Math.Abs(r[0].EntryPrice - 97.0) < 0.05);
        Assert.True(r[0].Return > 2.0);                          // bought ~97, sold ~100
    }

    [Fact]
    public void Long_DoesNotFillOnATouchWithoutTradingThrough()
    {
        var bars = Flat(80, dip: 40, low: 96.99);                // touches 97 but not 5bp through
        Assert.Empty(StructGridSimulator.GetReturns(bars, isLong: true));
    }

    [Fact]
    public void Short_TrailingStopTriggersOnCloseAndExitsNextOpen()
    {
        var closes = new double[80];
        closes[40] = 100; closes[41] = 101; closes[42] = 104;    // rally through the short level (103) at bar 41's high
        var bars = Flat(80, closes: closes);
        bars[41] = new Candle(T0.AddHours(41), 100, 103.2, 99.5, 101, 1_000_000);   // fills at 103 (traded through 103·1.0005)
        var r = StructGridSimulator.GetReturns(bars, isLong: false);
        Assert.NotEmpty(r);
        Assert.Equal(T0.AddHours(41), r[0].EntryTime);
        Assert.True(r[0].Return > 0, $"short from 103 back to ~100 should profit, got {r[0].Return:F3}");
    }

    [Fact]
    public void Overlay_NoDecision_NoTrades()
    {
        var bars = Flat(80, dip: 40, low: 96.9);
        Assert.Empty(StructGridSimulator.GetOverlayReturns(bars, true, _ => null, hardStopAtr: 3.0));
    }

    [Fact]
    public void Overlay_TakeProfitIsAMakerFillAtTheTarget()
    {
        var bars = Flat(80, dip: 40, low: 96.9);                 // fill 97, a = 1 → TP 98 (TPSL1)
        bars[42] = new Candle(T0.AddHours(42), 97.5, 98.2, 97.4, 97.8, 1_000_000);
        for (int i = 41; i < 42; i++) bars[i] = new Candle(T0.AddHours(i), 97.2, 97.6, 97.0, 97.3, 1_000_000);
        var r = StructGridSimulator.GetOverlayReturns(bars, true, t => t == T0.AddHours(39) ? "TPSL1" : null, hardStopAtr: 3.0);
        Assert.Single(r);
        Assert.Equal(T0.AddHours(42), r[0].Time);
        Assert.True(r[0].Return > 0.9 && r[0].Return < 1.1, $"97 → 98 ≈ +1.03% less maker costs, got {r[0].Return:F3}");
    }

    [Fact]
    public void Overlay_HardStopFiresIntrabarBeforeAnyCloseRule()
    {
        var bars = Flat(80, dip: 40, low: 96.9);                 // fill 97, hard stop 97 − 3·1 = 94
        bars[41] = new Candle(T0.AddHours(41), 96.5, 96.8, 93.5, 96.0, 1_000_000);   // wicks through 94, closes back above
        var r = StructGridSimulator.GetOverlayReturns(bars, true, t => t == T0.AddHours(39) ? "TIME24" : null, hardStopAtr: 3.0);
        Assert.Single(r);
        Assert.Equal(T0.AddHours(41), r[0].Time);
        Assert.True(r[0].Return < -3.0, $"stopped at 94 from 97 ≈ −3.1% plus taker + gap, got {r[0].Return:F3}");
    }
}
