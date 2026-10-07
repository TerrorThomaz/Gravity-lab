using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// HMM-range Grid prototype. Segmentation is tested from a synthetic P(ranging) series through
// HmmRangeLevels.FromProbabilities, so none of this depends on a trained HMM — only the mapping
// "probability series → levels → fills" is under test.
public class HmmRangeGridTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ±amp% sine around 100, 40-bar period: a clean range with support ≈ 100−amp, resistance ≈ 100+amp.
    private static Candle[] Sine(int n, double amp = 5.0, int offset = 0)
    {
        var c = new Candle[n];
        for (int i = 0; i < n; i++)
        {
            double close = 100.0 + amp * Math.Sin(2.0 * Math.PI * (i + offset) / 40.0);
            double open  = 100.0 + amp * Math.Sin(2.0 * Math.PI * (i + offset - 1) / 40.0);
            c[i] = new Candle(T0.AddHours(i), open, Math.Max(open, close) + 0.1, Math.Min(open, close) - 0.1, close, 1000);
        }
        return c;
    }

    private static double[] Const(int n, double v) => Enumerable.Repeat(v, n).ToArray();

    private static GridGenotype Geno(int levels = 3) => new() { GridLevels = levels };

    [Fact]
    public void Segmentation_UsesHysteresis_NotASingleThreshold()
    {
        var h1 = Sine(6);
        // 0.65 opens, 0.50 sits between exit (0.4) and enter (0.6) so it HOLDS, 0.35 closes,
        // and 0.50 again must NOT reopen.
        var lv = HmmRangeLevels.FromProbabilities(h1, [0.1, 0.65, 0.5, 0.5, 0.35, 0.5]);
        Assert.Equal([false, true, true, true, false, false], lv.InRange);
        Assert.Equal([0, 1, 2, 3, 0, 0], lv.SegBars);
        Assert.True(double.IsNaN(lv.Support[0]) && double.IsNaN(lv.Support[4]));
    }

    [Fact]
    public void Levels_AreRunningExtremesOfTheSegmentOnly()
    {
        var h1 = Sine(80);
        var p  = Const(80, 1.0);
        for (int i = 0; i < 10; i++) p[i] = 0.0;   // bars 0..9 must not leak into the levels
        var lv = HmmRangeLevels.FromProbabilities(h1, p);

        for (int i = 10; i < 80; i++)
        {
            var seg = h1[10..(i + 1)];
            Assert.Equal(seg.Min(c => c.Low),  lv.Support[i],    12);
            Assert.Equal(seg.Max(c => c.High), lv.Resistance[i], 12);
        }
    }

    [Fact]
    public void Levels_AreCausal_FutureBarsDoNotChangeThePast()
    {
        var a = Sine(120);
        var b = (Candle[])a.Clone();
        for (int i = 60; i < 120; i++) b[i] = b[i] with { Low = 1.0, High = 1000.0 };   // wild future
        var p  = Const(120, 1.0);
        var la = HmmRangeLevels.FromProbabilities(a, p);
        var lb = HmmRangeLevels.FromProbabilities(b, p);
        for (int i = 0; i < 60; i++)
        {
            Assert.Equal(la.Support[i],    lb.Support[i]);
            Assert.Equal(la.Resistance[i], lb.Resistance[i]);
        }
    }

    [Fact]
    public void CleanRange_BuysBelowMidAndTakesProfit_WithoutStopping()
    {
        var h1 = Sine(600);
        var lv = HmmRangeLevels.FromProbabilities(h1, Const(600, 1.0));
        var r  = HmmRangeGridSimulator.Run(Geno(), h1, lv);

        Assert.True(r.Sessions >= 1);
        Assert.Equal(0, r.StopOuts);
        Assert.NotEmpty(r.Trades);
        Assert.All(r.Trades, t => Assert.True(t.EntryPrice < 100.0 + 1e-9, $"bought above range mid: {t.EntryPrice}"));
        // A ±5% range with TP at half the width is ~5% per round trip; costs are ~0.1–0.3%.
        Assert.True(r.Trades.Average(t => t.Return) > 1.0);
    }

    [Fact]
    public void NoEntry_UntilTheSegmentHasHeld_MinSegBars()
    {
        var h1 = Sine(200);
        var p  = Const(200, 0.0);
        for (int i = 50; i < 200; i++) p[i] = 1.0;
        var lv = HmmRangeLevels.FromProbabilities(h1, p);
        var prm = new HmmRangeGridParams(MinSegBars: 24);
        var r  = HmmRangeGridSimulator.Run(Geno(), h1, lv, prm);

        Assert.NotEmpty(r.Trades);
        // Segment opens at bar 50, qualifies at the CLOSE of bar 73 (SegBars = 24), so the
        // earliest session can open on bar 74 — never on the qualifying bar itself.
        Assert.All(r.Trades, t => Assert.True(t.EntryTime >= h1[74].Time, $"entered at {t.EntryTime}"));
    }

    [Fact]
    public void Breakdown_StopsOut_AndDoesNotReenterTheSameSegment()
    {
        // 200 bars of range, then a steady slide to 60 — HMM (synthetically) still says "ranging",
        // which is exactly the case the hard stop exists for.
        var range = Sine(200);
        var h1 = new Candle[300];
        Array.Copy(range, h1, 200);
        for (int i = 200; i < 300; i++)
        {
            double open = i == 200 ? range[199].Close : h1[i - 1].Close;
            double close = open - 0.6;
            h1[i] = new Candle(T0.AddHours(i), open, open + 0.05, close - 0.05, close, 1000);
        }
        var lv = HmmRangeLevels.FromProbabilities(h1, Const(300, 1.0));
        var r  = HmmRangeGridSimulator.Run(Geno(), h1, lv);

        Assert.Equal(1, r.StopOuts);
        var lastExit = r.Trades.Max(t => t.Time);
        var stopTime = r.Trades.Where(t => t.Return < 0).Min(t => t.Time);
        Assert.Equal(stopTime, lastExit);   // nothing traded after the stop in this segment
    }

    [Fact]
    public void RegimeExit_FlattensAtNextBarOpen()
    {
        var h1 = Sine(400);
        var p  = Const(400, 1.0);
        for (int i = 300; i < 400; i++) p[i] = 0.0;
        var lv = HmmRangeLevels.FromProbabilities(h1, p);
        var r  = HmmRangeGridSimulator.Run(Geno(), h1, lv, new HmmRangeGridParams(MaxHoldBars: 10_000));

        Assert.Equal(1, r.RegimeExits);
        // Range left at the close of bar 300 → flatten on bar 301's open; no trade after that.
        Assert.All(r.Trades, t => Assert.True(t.Time <= h1[301].Time));
    }
}
