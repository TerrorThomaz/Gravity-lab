using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// WalkForwardCommand builds its h1 series with FadeShortSimulator.AggregateCandles(m15, 4), which
// emits one h1 bar per NON-overlapping group of four 15m bars. That makes h1 index i correspond to
// m15 index 4i exactly, and the dual-timeframe simulators depend on the two spans covering the same
// calendar range. A misalignment here would not show up as an error — the report would simply carry
// numbers produced from mismatched timeframes — so the arithmetic is pinned.
public class WalkForwardM15RangeTests
{
    // 2000 m15 bars ⇒ 500 h1 bars, so every stage boundary below divides exactly.
    private const int M15Len = 2_000;

    [Fact]
    public void M15Range_MapsH1IndexToFourTimesM15Index()
    {
        var (start, len) = WalkForwardCommand.M15Range(M15Len, 100, 200);
        Assert.Equal(400, start);
        Assert.Equal(400, len);
    }

    [Fact]
    public void M15Range_TrainWindowStartsAtZero()
    {
        var (start, len) = WalkForwardCommand.M15Range(M15Len, 0, 125);
        Assert.Equal(0, start);
        Assert.Equal(500, len);
    }

    // Train [0, e) and test [e, f) must tile without overlapping: a shared 15m bar would let the
    // same candle feed both the in-sample and out-of-sample side of the efficiency ratio.
    [Fact]
    public void M15Range_AdjacentH1WindowsTileWithoutOverlap()
    {
        var (trainStart, trainLen) = WalkForwardCommand.M15Range(M15Len, 0, 100);
        var (testStart, testLen)   = WalkForwardCommand.M15Range(M15Len, 100, 200);

        Assert.Equal(trainStart + trainLen, testStart);
        Assert.Equal(400, testLen);
    }

    [Fact]
    public void M15Range_ClampsToArrayWhenH1RangeRunsPastTheM15Data()
    {
        // AggregateCandles drops a trailing partial group, so h1 can end just short of m15/4;
        // asking for bars beyond the array must truncate rather than throw.
        var (start, len) = WalkForwardCommand.M15Range(M15Len, 450, 600);
        Assert.Equal(1_800, start);
        Assert.Equal(200, len);   // 2000 - 1800, not the requested 600
    }

    [Fact]
    public void M15Range_StartBeyondArrayYieldsEmptyRatherThanNegativeLength()
    {
        var (start, len) = WalkForwardCommand.M15Range(M15Len, 700, 800);
        Assert.Equal(M15Len, start);
        Assert.Equal(0, len);
    }

    // The mapping must agree with the aggregation it claims to invert, not merely be self-consistent.
    [Fact]
    public void M15Range_AgreesWithAggregateCandlesTimestamps()
    {
        var m15 = new Candle[M15Len];
        var t0  = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < M15Len; i++)
            m15[i] = new Candle(t0.AddMinutes(15 * i), 100, 101, 99, 100, 1);

        var h1 = FadeShortSimulator.AggregateCandles(m15, 4);

        const int h1Start = 37, h1End = 91;
        var (start, len) = WalkForwardCommand.M15Range(m15.Length, h1Start, h1End);

        // First m15 bar of the window is the bar the first h1 bar opened on.
        Assert.Equal(h1[h1Start].Time, m15[start].Time);
        // Last m15 bar of the window is the bar immediately before the next h1 bar opens.
        Assert.Equal(h1[h1End].Time, m15[start + len].Time);
    }
}
