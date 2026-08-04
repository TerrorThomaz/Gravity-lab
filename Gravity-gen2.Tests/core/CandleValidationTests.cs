using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class CandleValidationTests
{
    private static Candle C(DateTime time, double o, double h, double l, double c, double v = 100)
        => new(time, o, h, l, c, v);

    [Fact]
    public void ValidateCandles_GoodData_ReturnsValid()
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<Candle>
        {
            C(baseTime,                  100, 105, 95, 102),
            C(baseTime.AddMinutes(15),   102, 108, 100, 106),
            C(baseTime.AddMinutes(30),   106, 110, 104, 108),
        };
        var result = CandleFetcher.ValidateCandles(candles, TimeSpan.FromMinutes(15));
        Assert.True(result.IsValid);
        Assert.Equal(0, result.BadOhlc);
        Assert.Equal(0, result.Gaps);
        Assert.Equal(0, result.ZeroPrices);
    }

    [Fact]
    public void ValidateCandles_BadOhlcHigh_Detected()
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<Candle>
        {
            C(baseTime, 100, 95, 90, 98),
        };
        var result = CandleFetcher.ValidateCandles(candles, TimeSpan.FromMinutes(15));
        Assert.False(result.IsValid);
        Assert.Equal(1, result.BadOhlc);
    }

    [Fact]
    public void ValidateCandles_BadOhlcLow_Detected()
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<Candle>
        {
            C(baseTime, 100, 110, 105, 108),
        };
        var result = CandleFetcher.ValidateCandles(candles, TimeSpan.FromMinutes(15));
        Assert.False(result.IsValid);
        Assert.Equal(1, result.BadOhlc);
    }

    [Fact]
    public void ValidateCandles_ZeroPrice_Detected()
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<Candle>
        {
            C(baseTime, 100, 110, 0, 105),
        };
        var result = CandleFetcher.ValidateCandles(candles, TimeSpan.FromMinutes(15));
        Assert.False(result.IsValid);
        Assert.Equal(1, result.ZeroPrices);
    }

    [Fact]
    public void ValidateCandles_TimestampGap_Detected()
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<Candle>
        {
            C(baseTime,                100, 105, 95, 102),
            C(baseTime.AddHours(2),    102, 108, 100, 106),
        };
        var result = CandleFetcher.ValidateCandles(candles, TimeSpan.FromMinutes(15));
        Assert.Equal(1, result.Gaps);
    }

    [Fact]
    public void ValidateCandles_PriceSpike_Detected()
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var candles = new List<Candle>
        {
            C(baseTime,                100, 105, 95, 100),
            C(baseTime.AddMinutes(15), 200, 210, 195, 205),
        };
        var result = CandleFetcher.ValidateCandles(candles, TimeSpan.FromMinutes(15));
        Assert.Equal(1, result.Spikes);
    }
}
