using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// ── Trend ────────────────────────────────────────────────────────────────────

public class TrendSmaTests
{
    [Fact]
    public void FlatSeries_ReturnsMean()
    {
        var closes = Enumerable.Repeat(5.0, 10).ToArray();
        var sma = Trend.Sma(closes, 3);
        for (int i = 2; i < sma.Length; i++)
            Assert.Equal(5.0, sma[i], precision: 8);
    }

    [Fact]
    public void Period3_KnownSequence()
    {
        // [1,2,3,4,5]: SMA3 at i=2: (1+2+3)/3=2, i=3: (2+3+4)/3=3, i=4: (3+4+5)/3=4
        var closes = new double[] { 1, 2, 3, 4, 5 };
        var sma = Trend.Sma(closes, 3);
        Assert.Equal(2.0, sma[2], precision: 8);
        Assert.Equal(3.0, sma[3], precision: 8);
        Assert.Equal(4.0, sma[4], precision: 8);
    }
}

public class TrendMacdTests
{
    [Fact]
    public void FlatSeries_MacdIsZero()
    {
        var closes = Enumerable.Repeat(100.0, 50).ToArray();
        var (macd, sig, hist) = Trend.Macd(closes, 12, 26, 9);
        // After warmup, all-flat → fast EMA = slow EMA = 100 → macd = 0
        for (int i = 30; i < macd.Length; i++)
            Assert.Equal(0.0, macd[i], precision: 6);
    }

    [Fact]
    public void RisingThenFlat_MacdPositiveThenConverges()
    {
        // First 30 bars rising, next 30 flat
        var closes = Enumerable.Range(0, 30).Select(i => (double)(100 + i))
            .Concat(Enumerable.Repeat(129.0, 30)).ToArray();
        var (macd, _, _) = Trend.Macd(closes, 12, 26, 9);
        // At peak of rise, fast EMA > slow EMA → macd > 0
        Assert.True(macd[29] > 0, $"Expected MACD > 0 at peak, got {macd[29]}");
    }
}

public class TrendDonchianTests
{
    [Fact]
    public void Period3_UpperTracksHighest()
    {
        var highs = new double[] { 1, 3, 2, 4, 1 };
        var lows  = new double[] { 0, 1, 0, 2, 0 };
        var (upper, lower) = Trend.DonchianChannel(highs, lows, 3);
        Assert.Equal(3.0, upper[2], precision: 8); // max(1,3,2)
        Assert.Equal(4.0, upper[3], precision: 8); // max(3,2,4)
        Assert.Equal(0.0, lower[2], precision: 8); // min(0,1,0)
        Assert.Equal(0.0, lower[3], precision: 8); // min(1,0,2)
    }
}

public class TrendKeltnerTests
{
    [Fact]
    public void FlatCandles_SymmetricAroundMid()
    {
        // Flat series → ATR = 0, so upper = lower = mid = close
        var closes = Enumerable.Repeat(100.0, 30).ToArray();
        var highs  = closes;
        var lows   = closes;
        var (upper, mid, lower) = Trend.KeltnerChannel(closes, highs, lows, 10, 14, 2.0);
        for (int i = 20; i < 30; i++)
        {
            Assert.Equal(mid[i], upper[i], precision: 6);
            Assert.Equal(mid[i], lower[i], precision: 6);
        }
    }
}

// ── Momentum ─────────────────────────────────────────────────────────────────

public class MomentumStochasticTests
{
    [Fact]
    public void CloseAtHigh_KIs100()
    {
        // close = high → %K = 100
        var highs  = new double[] { 10, 10, 10, 10, 10 };
        var lows   = new double[] {  5,  5,  5,  5,  5 };
        var closes = new double[] { 10, 10, 10, 10, 10 };
        var (k, _) = Momentum.Stochastic(highs, lows, closes, 3, 2);
        Assert.Equal(100.0, k[2], precision: 6);
    }

    [Fact]
    public void CloseAtLow_KIsZero()
    {
        var highs  = new double[] { 10, 10, 10, 10, 10 };
        var lows   = new double[] {  5,  5,  5,  5,  5 };
        var closes = new double[] {  5,  5,  5,  5,  5 };
        var (k, _) = Momentum.Stochastic(highs, lows, closes, 3, 2);
        Assert.Equal(0.0, k[2], precision: 6);
    }
}

public class MomentumCciTests
{
    [Fact]
    public void FlatSeries_CciIsZero()
    {
        var closes = Enumerable.Repeat(100.0, 20).ToArray();
        var cci = Momentum.Cci(closes, closes, closes, 10);
        for (int i = 9; i < cci.Length; i++)
            Assert.Equal(0.0, cci[i], precision: 6);
    }

    [Fact]
    public void AboveMean_CciPositive()
    {
        // closes alternating 90/110 for 9 bars, then spike to 150 → CCI > 0
        var closes = new double[] { 90, 110, 90, 110, 90, 110, 90, 110, 90, 150 };
        var cci = Momentum.Cci(closes, closes, closes, 10);
        Assert.True(cci[9] > 0, $"Expected CCI > 0 for close above mean, got {cci[9]}");
    }
}

public class MomentumRocTests
{
    [Fact]
    public void DoubledPrice_Roc100()
    {
        // price doubles over period=1: ROC = (200-100)/100 * 100 = 100%
        var closes = new double[] { 100, 200 };
        var roc = Momentum.Roc(closes, 1);
        Assert.Equal(100.0, roc[1], precision: 6);
    }

    [Fact]
    public void HalvedPrice_RocNeg50()
    {
        var closes = new double[] { 100, 50 };
        var roc = Momentum.Roc(closes, 1);
        Assert.Equal(-50.0, roc[1], precision: 6);
    }
}

public class MomentumNBarTests
{
    [Fact]
    public void KnownDifference()
    {
        var closes = new double[] { 10, 20, 30, 25, 15 };
        var mom = Momentum.NBarMomentum(closes, 2);
        Assert.Equal(20.0, mom[2], precision: 8); // 30-10
        Assert.Equal( 5.0, mom[3], precision: 8); // 25-20
        Assert.Equal(-15.0, mom[4], precision: 8); // 15-30
    }
}

// ── Volatility ────────────────────────────────────────────────────────────────

public class VolatilityBollingerBandsTests
{
    [Fact]
    public void Width_MatchesBbWidth()
    {
        var rng    = new Random(42);
        var closes = Enumerable.Range(0, 50).Select(_ => 100.0 + rng.NextDouble() * 10).ToArray();
        var (upper, mid, lower) = Volatility.BollingerBands(closes, 20, 2.0);
        var bbWidth = Volatility.BbWidth(closes, 20);
        // BbWidth = 4σ/SMA×100; upper−lower = 2×2σ = 4σ → should match
        for (int i = 19; i < closes.Length; i++)
        {
            double widthFromBands = mid[i] > 1e-10 ? (upper[i] - lower[i]) / mid[i] * 100.0 : 0;
            Assert.Equal(bbWidth[i], widthFromBands, precision: 5);
        }
    }

    [Fact]
    public void FlatSeries_ZeroWidth()
    {
        var closes = Enumerable.Repeat(50.0, 30).ToArray();
        var (upper, mid, lower) = Volatility.BollingerBands(closes, 20, 2.0);
        for (int i = 19; i < closes.Length; i++)
        {
            Assert.Equal(mid[i], upper[i], precision: 8);
            Assert.Equal(mid[i], lower[i], precision: 8);
        }
    }
}

public class VolatilityAtrRatioTests
{
    [Fact]
    public void ExpandingVolatility_RatioAboveOne()
    {
        // First 20 bars calm (range=1), last 30 bars volatile (range=10)
        // Short ATR (7) adapts faster → short > long → ratio > 1
        var calm     = Enumerable.Range(0, 20).Select(i => (h: 100.5, l: 99.5, c: 100.0));
        var volatile_ = Enumerable.Range(0, 30).Select(i => (h: 105.0, l: 95.0, c: 100.0));
        var all = calm.Concat(volatile_).ToArray();
        var h  = all.Select(x => x.h).ToArray();
        var l  = all.Select(x => x.l).ToArray();
        var cl = all.Select(x => x.c).ToArray();
        var ratio = Volatility.AtrRatio(h, l, cl, 7, 100);
        Assert.True(ratio[49] > 1.0, $"Expected AtrRatio > 1 in expanding vol, got {ratio[49]:F3}");
    }

    [Fact]
    public void FlatSeries_RatioIsOne()
    {
        var closes = Enumerable.Repeat(100.0, 120).ToArray();
        var ratio = Volatility.AtrRatio(closes, closes, closes, 14, 100);
        for (int i = 100; i < closes.Length; i++)
            Assert.Equal(1.0, ratio[i], precision: 3);
    }
}

// ── Trend: EmaInto ────────────────────────────────────────────────────────────

public class TrendEmaIntoTests
{
    [Fact]
    public void ProducesSameResultAsEma()
    {
        var closes = new double[] { 10, 20, 30, 25, 15 };
        var ema    = Trend.Ema(closes, 3);
        var output = new double[closes.Length];
        Trend.EmaInto(closes, 3, output);
        for (int i = 0; i < closes.Length; i++)
            Assert.Equal(ema[i], output[i], precision: 8);
    }

    [Fact]
    public void Period1_OutputEqualsCLoses()
    {
        var closes = new double[] { 5, 10, 7, 12 };
        var output = new double[closes.Length];
        Trend.EmaInto(closes, 1, output);
        for (int i = 0; i < closes.Length; i++)
            Assert.Equal(closes[i], output[i], precision: 8);
    }
}

// ── Trend: Adx ────────────────────────────────────────────────────────────────

public class TrendAdxTests
{
    [Fact]
    public void FlatCandles_AdxIsZero()
    {
        var t = DateTime.UtcNow;
        var candles = Enumerable.Range(0, 60)
            .Select(i => new Candle(t.AddHours(i), 100, 100, 100, 100, 1000))
            .ToArray();
        var h  = candles.Select(c => c.High).ToArray();
        var l  = candles.Select(c => c.Low).ToArray();
        var cl = candles.Select(c => c.Close).ToArray();
        var adx = Trend.Adx(h, l, cl, 14);
        for (int i = 28; i < adx.Length; i++)
            Assert.Equal(0.0, adx[i], precision: 6);
    }

    [Fact]
    public void StrongUptrend_AdxAbove25()
    {
        var t = DateTime.UtcNow;
        var candles = Enumerable.Range(0, 80)
            .Select(i => new Candle(t.AddHours(i), 100 + i, 101 + i, 99 + i, 100.5 + i, 1000))
            .ToArray();
        var h  = candles.Select(c => c.High).ToArray();
        var l  = candles.Select(c => c.Low).ToArray();
        var cl = candles.Select(c => c.Close).ToArray();
        var adx = Trend.Adx(h, l, cl, 14);
        Assert.True(adx[79] > 25, $"Expected ADX > 25 in strong trend, got {adx[79]:F2}");
    }

    [Fact]
    public void OutputLength_MatchesInput()
    {
        var h  = Enumerable.Range(0, 50).Select(i => (double)(100 + i)).ToArray();
        var l  = Enumerable.Range(0, 50).Select(i => (double)(99  + i)).ToArray();
        var cl = Enumerable.Range(0, 50).Select(i => (double)(100 + i)).ToArray();
        var adx = Trend.Adx(h, l, cl, 14);
        Assert.Equal(50, adx.Length);
    }
}

// ── Momentum: Rsi ─────────────────────────────────────────────────────────────

public class MomentumRsiTests
{
    [Fact]
    public void AllRising_Returns100()
    {
        var closes = Enumerable.Range(1, 20).Select(i => (double)i * 10).ToArray();
        var rsi = Momentum.Rsi(closes, 7);
        for (int i = 7; i < rsi.Length; i++)
            Assert.Equal(100.0, rsi[i], precision: 6);
    }

    [Fact]
    public void SymmetricUpDown_Returns50AtWarmup()
    {
        var closes = new double[] { 100, 101, 100 };
        var rsi = Momentum.Rsi(closes, 2);
        Assert.Equal(50.0, rsi[2], precision: 6);
    }

    [Fact]
    public void WilderSmoothing_ThreeDownAfterUp()
    {
        // period=2, closes=[100, 101, 100, 99]
        // After warmup: RSI[2]=50; i=3: avgGain=(0.5*1+0)/2=0.25, avgLoss=(0.5*1+1)/2=0.75
        // RSI[3]=100-100/(1+0.25/0.75)=25
        var closes = new double[] { 100, 101, 100, 99 };
        var rsi = Momentum.Rsi(closes, 2);
        Assert.Equal(25.0, rsi[3], precision: 6);
    }

    [Fact]
    public void OutputLength_MatchesInput()
    {
        var closes = Enumerable.Range(1, 30).Select(i => (double)i).ToArray();
        var rsi = Momentum.Rsi(closes, 14);
        Assert.Equal(closes.Length, rsi.Length);
    }
}
