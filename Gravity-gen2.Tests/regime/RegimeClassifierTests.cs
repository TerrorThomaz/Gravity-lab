using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class RegimeClassifierTests
{
    private static Candle[] FlatCandles(int count)
    {
        var t = DateTime.UtcNow;
        return Enumerable.Range(0, count)
            .Select(i => new Candle(t.AddHours(i), 100, 101, 99, 100, 1_000_000))
            .ToArray();
    }

    [Fact]
    public void ClassifySeries_FlatMarket_ReturnsSomeResult()
    {
        // Smoke test: classifier runs without throwing on a flat price series.
        var candles = FlatCandles(200);
        var results = RegimeClassifier.ClassifySeriesWithDuration(candles);
        Assert.Equal(candles.Length, results.Length);
    }

    [Fact]
    public void ClassifySeries_OutputLength_MatchesInput()
    {
        var candles = FlatCandles(100);
        var results = RegimeClassifier.ClassifySeriesWithDuration(candles);
        Assert.Equal(100, results.Length);
    }
}
