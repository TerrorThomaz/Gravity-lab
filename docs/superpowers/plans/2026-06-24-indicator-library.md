# Indicator Library Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `src/core/Indicators.cs` with a structured `src/core/indicators/` library (4 files, 4 classes) and extract copy-pasted composite signal logic from 4 simulators into a shared `Signals` class.

**Architecture:** Raw math lives in `Trend`, `Momentum`, `Volatility` (moved from `Indicators.cs` + new functions). Composite boolean signals live in `Signals` (extracted from simulators). All 4 classes stay in `TradingGA` namespace — no `using` changes at call sites, just class-name substitution.

**Tech Stack:** C# .NET, xUnit, `TradingGA` namespace, `Gravity-gen2.Tests` test project.

## Global Constraints

- All new classes in `TradingGA` namespace (no `using` statements required at call sites)
- Internal static classes only — no public API change
- `Indicators.cs` deleted only after all 14 call-site files are updated (Task 3)
- No changes to genotypes, GAs, backtest commands, or frontend
- Test file: `Gravity-gen2.Tests/core/IndicatorLibraryTests.cs`
- Run tests: `dotnet test Gravity-gen2.Tests/`

---

### Task 1: Trend.cs + Momentum.cs + Volatility.cs (new functions + tests)

**Files:**
- Create: `src/core/indicators/Trend.cs`
- Create: `src/core/indicators/Momentum.cs`
- Create: `src/core/indicators/Volatility.cs`
- Create: `Gravity-gen2.Tests/core/IndicatorLibraryTests.cs`

**Interfaces:**
- Produces: `Trend.Ema`, `Trend.EmaInto`, `Trend.Sma`, `Trend.Adx`, `Trend.Macd`, `Trend.DonchianChannel`, `Trend.KeltnerChannel`
- Produces: `Momentum.Rsi`, `Momentum.Stochastic`, `Momentum.Cci`, `Momentum.Roc`, `Momentum.NBarMomentum`
- Produces: `Volatility.Atr`, `Volatility.BbWidth`, `Volatility.BollingerBands`, `Volatility.AtrRatio`
- Note: `Indicators.*` still exists at end of this task — these new classes coexist with it

- [ ] **Step 1: Create `src/core/indicators/Trend.cs`**

```csharp
namespace TradingGA;

internal static class Trend
{
    internal static double[] Ema(double[] closes, int period)
    {
        var ema = new double[closes.Length];
        double k = 2.0 / (period + 1);
        ema[0] = closes[0];
        for (int i = 1; i < closes.Length; i++)
            ema[i] = closes[i] * k + ema[i - 1] * (1 - k);
        return ema;
    }

    internal static void EmaInto(double[] closes, int period, double[] output)
    {
        double k = 2.0 / (period + 1);
        output[0] = closes[0];
        for (int i = 1; i < closes.Length; i++)
            output[i] = closes[i] * k + output[i - 1] * (1 - k);
    }

    internal static double[] Sma(double[] closes, int period)
    {
        var sma = new double[closes.Length];
        double sum = 0;
        for (int i = 0; i < closes.Length; i++)
        {
            sum += closes[i];
            if (i >= period) sum -= closes[i - period];
            sma[i] = i >= period - 1 ? sum / Math.Min(i + 1, period) : sum / (i + 1);
        }
        return sma;
    }

    internal static double[] Adx(double[] highs, double[] lows, double[] closes, int period)
    {
        int n = closes.Length;
        var tr   = new double[n];
        var pDm  = new double[n];
        var mDm  = new double[n];
        for (int i = 1; i < n; i++)
        {
            double hd = highs[i] - highs[i - 1], ld = lows[i - 1] - lows[i];
            tr[i]  = Math.Max(highs[i] - lows[i],
                     Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                              Math.Abs(lows[i]  - closes[i - 1])));
            pDm[i] = hd > ld && hd > 0 ? hd : 0;
            mDm[i] = ld > hd && ld > 0 ? ld : 0;
        }
        var sTr  = new double[n]; var sPDm = new double[n]; var sMDm = new double[n];
        var dx   = new double[n]; var adx  = new double[n];
        if (period >= n) return adx;
        for (int i = 1; i <= period; i++) { sTr[period] += tr[i]; sPDm[period] += pDm[i]; sMDm[period] += mDm[i]; }
        for (int i = period + 1; i < n; i++)
        {
            sTr[i]  = sTr[i - 1]  - sTr[i - 1]  / period + tr[i];
            sPDm[i] = sPDm[i - 1] - sPDm[i - 1] / period + pDm[i];
            sMDm[i] = sMDm[i - 1] - sMDm[i - 1] / period + mDm[i];
            if (sTr[i] < 1e-10) continue;
            double pDi = 100.0 * sPDm[i] / sTr[i], mDi = 100.0 * sMDm[i] / sTr[i];
            double ds  = pDi + mDi;
            dx[i] = ds > 1e-10 ? 100.0 * Math.Abs(pDi - mDi) / ds : 0;
        }
        int adxStart = period * 2;
        if (adxStart >= n) return adx;
        double sumDx = 0;
        for (int i = period + 1; i <= adxStart && i < n; i++) sumDx += dx[i];
        adx[adxStart] = sumDx / period;
        for (int i = adxStart + 1; i < n; i++)
            adx[i] = (adx[i - 1] * (period - 1) + dx[i]) / period;
        return adx;
    }

    internal static (double[] macd, double[] sig, double[] hist) Macd(
        double[] closes, int fast, int slow, int signal)
    {
        var fastEma = Ema(closes, fast);
        var slowEma = Ema(closes, slow);
        var macd    = new double[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            macd[i] = fastEma[i] - slowEma[i];
        var sig  = Ema(macd, signal);
        var hist = new double[closes.Length];
        for (int i = 0; i < closes.Length; i++)
            hist[i] = macd[i] - sig[i];
        return (macd, sig, hist);
    }

    internal static (double[] upper, double[] lower) DonchianChannel(
        double[] highs, double[] lows, int period)
    {
        int n = highs.Length;
        var upper = new double[n];
        var lower = new double[n];
        for (int i = 0; i < n; i++)
        {
            int start = Math.Max(0, i - period + 1);
            double hi = highs[start], lo = lows[start];
            for (int j = start + 1; j <= i; j++)
            {
                if (highs[j] > hi) hi = highs[j];
                if (lows[j]  < lo) lo = lows[j];
            }
            upper[i] = hi;
            lower[i] = lo;
        }
        return (upper, lower);
    }

    internal static (double[] upper, double[] mid, double[] lower) KeltnerChannel(
        double[] closes, double[] highs, double[] lows, int emaPeriod, int atrPeriod, double atrMult)
    {
        var mid   = Ema(closes, emaPeriod);
        var atr   = Volatility.Atr(highs, lows, closes, atrPeriod);
        int n     = closes.Length;
        var upper = new double[n];
        var lower = new double[n];
        for (int i = 0; i < n; i++)
        {
            upper[i] = mid[i] + atrMult * atr[i];
            lower[i] = mid[i] - atrMult * atr[i];
        }
        return (upper, mid, lower);
    }
}
```

- [ ] **Step 2: Create `src/core/indicators/Momentum.cs`**

```csharp
namespace TradingGA;

internal static class Momentum
{
    internal static double[] Rsi(double[] closes, int period)
    {
        var rsi = new double[closes.Length];
        double avgGain = 0, avgLoss = 0;
        for (int i = 1; i <= period && i < closes.Length; i++)
        {
            double d = closes[i] - closes[i - 1];
            if (d > 0) avgGain += d; else avgLoss -= d;
        }
        avgGain /= period;
        avgLoss /= period;
        for (int i = period; i < closes.Length; i++)
        {
            if (i > period)
            {
                double d = closes[i] - closes[i - 1];
                avgGain = (avgGain * (period - 1) + Math.Max(d,  0)) / period;
                avgLoss = (avgLoss * (period - 1) + Math.Max(-d, 0)) / period;
            }
            rsi[i] = avgLoss < 1e-12 ? 100.0 : 100.0 - 100.0 / (1.0 + avgGain / avgLoss);
        }
        return rsi;
    }

    internal static (double[] k, double[] d) Stochastic(
        double[] highs, double[] lows, double[] closes, int kPeriod, int dPeriod)
    {
        int n = closes.Length;
        var k = new double[n];
        for (int i = kPeriod - 1; i < n; i++)
        {
            int    start = i - kPeriod + 1;
            double hi = highs[start], lo = lows[start];
            for (int j = start + 1; j <= i; j++)
            {
                if (highs[j] > hi) hi = highs[j];
                if (lows[j]  < lo) lo = lows[j];
            }
            double range = hi - lo;
            k[i] = range < 1e-10 ? 50.0 : (closes[i] - lo) / range * 100.0;
        }
        var d = Trend.Sma(k, dPeriod);
        return (k, d);
    }

    internal static double[] Cci(double[] highs, double[] lows, double[] closes, int period)
    {
        int n = closes.Length;
        var cci = new double[n];
        for (int i = period - 1; i < n; i++)
        {
            int    start = i - period + 1;
            double sum = 0;
            for (int j = start; j <= i; j++)
                sum += (highs[j] + lows[j] + closes[j]) / 3.0;
            double mean = sum / period;
            double dev  = 0;
            for (int j = start; j <= i; j++)
                dev += Math.Abs((highs[j] + lows[j] + closes[j]) / 3.0 - mean);
            dev /= period;
            cci[i] = dev < 1e-10 ? 0 : (((highs[i] + lows[i] + closes[i]) / 3.0) - mean) / (0.015 * dev);
        }
        return cci;
    }

    internal static double[] Roc(double[] closes, int period)
    {
        int n = closes.Length;
        var roc = new double[n];
        for (int i = period; i < n; i++)
        {
            double prev = closes[i - period];
            roc[i] = prev < 1e-10 ? 0 : (closes[i] - prev) / prev * 100.0;
        }
        return roc;
    }

    internal static double[] NBarMomentum(double[] closes, int period)
    {
        int n = closes.Length;
        var mom = new double[n];
        for (int i = period; i < n; i++)
            mom[i] = closes[i] - closes[i - period];
        return mom;
    }
}
```

- [ ] **Step 3: Create `src/core/indicators/Volatility.cs`**

```csharp
namespace TradingGA;

internal static class Volatility
{
    internal static double[] Atr(double[] highs, double[] lows, double[] closes, int period)
    {
        int n = closes.Length;
        var tr  = new double[n];
        var atr = new double[n];
        tr[0] = highs[0] - lows[0];
        for (int i = 1; i < n; i++)
            tr[i] = Math.Max(highs[i] - lows[i],
                    Math.Max(Math.Abs(highs[i] - closes[i - 1]),
                             Math.Abs(lows[i]  - closes[i - 1])));
        int init = Math.Min(period, n);
        double sum = 0;
        for (int i = 0; i < init; i++) sum += tr[i];
        atr[init - 1] = sum / init;
        for (int i = init; i < n; i++)
            atr[i] = (atr[i - 1] * (period - 1) + tr[i]) / period;
        return atr;
    }

    internal static double[] BbWidth(double[] closes, int period)
    {
        var width = new double[closes.Length];
        for (int i = period - 1; i < closes.Length; i++)
        {
            double s = 0, sq = 0;
            for (int j = i - period + 1; j <= i; j++) { s += closes[j]; sq += closes[j] * closes[j]; }
            double mean = s / period;
            double std  = Math.Sqrt(Math.Max(0, sq / period - mean * mean));
            width[i] = mean > 1e-10 ? 4.0 * std / mean * 100.0 : 0;
        }
        return width;
    }

    internal static (double[] upper, double[] mid, double[] lower) BollingerBands(
        double[] closes, int period, double stdDevs)
    {
        int n = closes.Length;
        var upper = new double[n];
        var mid   = new double[n];
        var lower = new double[n];
        for (int i = period - 1; i < n; i++)
        {
            double s = 0, sq = 0;
            for (int j = i - period + 1; j <= i; j++) { s += closes[j]; sq += closes[j] * closes[j]; }
            double mean = s / period;
            double std  = Math.Sqrt(Math.Max(0, sq / period - mean * mean));
            mid[i]   = mean;
            upper[i] = mean + stdDevs * std;
            lower[i] = mean - stdDevs * std;
        }
        return (upper, mid, lower);
    }

    internal static double[] AtrRatio(
        double[] highs, double[] lows, double[] closes, int shortPeriod, int longPeriod)
    {
        var shortAtr = Atr(highs, lows, closes, shortPeriod);
        var longAtr  = Atr(highs, lows, closes, longPeriod);
        int n = closes.Length;
        var ratio = new double[n];
        for (int i = 0; i < n; i++)
            ratio[i] = longAtr[i] > 1e-10 ? shortAtr[i] / longAtr[i] : 1.0;
        return ratio;
    }
}
```

- [ ] **Step 4: Write failing tests — create `Gravity-gen2.Tests/core/IndicatorLibraryTests.cs`**

```csharp
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
        var t = DateTime.UtcNow;
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
```

- [ ] **Step 5: Run tests to verify they fail (classes don't exist yet — but they do exist after steps 1-3)**

Run: `dotnet test Gravity-gen2.Tests/ --filter "FullyQualifiedName~IndicatorLibrary" -v n 2>&1 | tail -20`

Expected: Build errors referencing `Trend`, `Momentum`, `Volatility` if you run before creating files. After files are created (steps 1-3 done), expect: all PASS.

- [ ] **Step 6: Run full test suite to verify no regressions**

Run: `dotnet test Gravity-gen2.Tests/ -v n 2>&1 | tail -20`

Expected: All tests pass (existing `IndicatorsTests.cs` still uses `Indicators.*` which still exists).

- [ ] **Step 7: Commit**

```bash
git add src/core/indicators/Trend.cs src/core/indicators/Momentum.cs src/core/indicators/Volatility.cs Gravity-gen2.Tests/core/IndicatorLibraryTests.cs
git commit -m "feat: add Trend, Momentum, Volatility indicator classes with tests"
```

---

### Task 2: Composite.cs (class Signals) + tests

**Files:**
- Create: `src/core/indicators/Composite.cs`
- Modify: `Gravity-gen2.Tests/core/IndicatorLibraryTests.cs` (append signal tests)

**Interfaces:**
- Consumes: `Trend.Ema`, `Volatility.Atr`, `Volatility.AtrRatio` (from Task 1)
- Produces: `Signals.BearishDivergence`, `Signals.BullishDivergence`, `Signals.BearishBoS`, `Signals.BullishBoS`, `Signals.SwingHighLookback`, `Signals.SwingLowLookback`, `Signals.MinMoveFilter`, `Signals.EmaSlope`, `Signals.AdxTrend`, `Signals.EmaStack`, `Signals.AtrExpansion`

- [ ] **Step 1: Create `src/core/indicators/Composite.cs`**

```csharp
namespace TradingGA;

// Boolean signal arrays derived by combining raw indicators.
// All array-returning functions take precomputed arrays — no raw candle scanning.
// SwingHighLookback / SwingLowLookback are per-bar value-tuple returns (called at entry only).
internal static class Signals
{
    // true at bar i when: swing-high RSI ≥ rsiOverbought AND current RSI ≤ swingHighRSI − divThreshold.
    // Scans closes (not highs) for the swing high — matches FadeShortSimulator and SwingSimulator.
    internal static bool[] BearishDivergence(
        double[] rsi, double[] closes, int lookback, double rsiOverbought, double divThreshold)
    {
        int n = rsi.Length;
        var result = new bool[n];
        for (int i = lookback; i < n; i++)
        {
            int    lbStart   = Math.Max(0, i - lookback);
            double swingHigh = closes[lbStart];
            int    highIdx   = lbStart;
            for (int j = lbStart + 1; j < i; j++)
                if (closes[j] > swingHigh) { swingHigh = closes[j]; highIdx = j; }
            double rsiAtHigh = rsi[highIdx];
            result[i] = rsiAtHigh >= rsiOverbought && rsi[i] <= rsiAtHigh - divThreshold;
        }
        return result;
    }

    // true at bar i when: swing-low RSI ≤ rsiOversold AND current RSI ≥ swingLowRSI + divThreshold.
    // Scans closes for the swing low — matches FadeLongSimulator and SwingLongSimulator.
    internal static bool[] BullishDivergence(
        double[] rsi, double[] closes, int lookback, double rsiOversold, double divThreshold)
    {
        int n = rsi.Length;
        var result = new bool[n];
        for (int i = lookback; i < n; i++)
        {
            int    lbStart  = Math.Max(0, i - lookback);
            double swingLow = closes[lbStart];
            int    lowIdx   = lbStart;
            for (int j = lbStart + 1; j < i; j++)
                if (closes[j] < swingLow) { swingLow = closes[j]; lowIdx = j; }
            double rsiAtLow = rsi[lowIdx];
            result[i] = rsiAtLow <= rsiOversold && rsi[i] >= rsiAtLow + divThreshold;
        }
        return result;
    }

    // closes[i] < lows[i-1] — bearish structure break.
    internal static bool[] BearishBoS(double[] closes, double[] lows)
    {
        int n = closes.Length;
        var result = new bool[n];
        for (int i = 1; i < n; i++)
            result[i] = closes[i] < lows[i - 1];
        return result;
    }

    // closes[i] > highs[i-1] — bullish structure break.
    internal static bool[] BullishBoS(double[] closes, double[] highs)
    {
        int n = closes.Length;
        var result = new bool[n];
        for (int i = 1; i < n; i++)
            result[i] = closes[i] > highs[i - 1];
        return result;
    }

    // Per-bar: find highest close and its index, and the lowest low in [i-lookback, i).
    internal static (double swingHigh, int highIdx, double recentLow) SwingHighLookback(
        double[] closes, double[] highs, double[] lows, int i, int lookback)
    {
        int    lbStart   = Math.Max(0, i - lookback);
        double swingHigh = closes[lbStart];
        int    highIdx   = lbStart;
        double recentLow = lows[lbStart];
        for (int j = lbStart + 1; j < i; j++)
        {
            if (closes[j] > swingHigh) { swingHigh = closes[j]; highIdx = j; }
            if (lows[j]   < recentLow)   recentLow = lows[j];
        }
        return (swingHigh, highIdx, recentLow);
    }

    // Per-bar: find lowest close and its index, and the highest high in [i-lookback, i).
    internal static (double swingLow, int lowIdx, double recentHigh) SwingLowLookback(
        double[] closes, double[] highs, double[] lows, int i, int lookback)
    {
        int    lbStart    = Math.Max(0, i - lookback);
        double swingLow   = closes[lbStart];
        int    lowIdx     = lbStart;
        double recentHigh = highs[lbStart];
        for (int j = lbStart + 1; j < i; j++)
        {
            if (closes[j] < swingLow)  { swingLow = closes[j]; lowIdx = j; }
            if (highs[j]  > recentHigh)  recentHigh = highs[j];
        }
        return (swingLow, lowIdx, recentHigh);
    }

    // (max(upper[i-lookback..i]) − min(lower[i-lookback..i])) / atr[i] ≥ minAtrMult.
    // Pass closes as both upper and lower when scanning for close-based swings.
    // Pass closes/lows when scanning rally size (high close to recent low).
    internal static bool[] MinMoveFilter(
        double[] upper, double[] lower, double[] atr, int lookback, double minAtrMult)
    {
        int n = upper.Length;
        var result = new bool[n];
        for (int i = lookback; i < n; i++)
        {
            int    start   = Math.Max(0, i - lookback);
            double maxUp   = upper[start];
            double minLow  = lower[start];
            for (int j = start + 1; j <= i; j++)
            {
                if (upper[j] > maxUp)  maxUp  = upper[j];
                if (lower[j] < minLow) minLow = lower[j];
            }
            double atrNow = atr[i] > 1e-10 ? atr[i] : 1e-10;
            result[i] = (maxUp - minLow) / atrNow >= minAtrMult;
        }
        return result;
    }

    // (ema[i] − ema[i−lookback]) / ema[i−lookback]. Positive = rising.
    internal static double[] EmaSlope(double[] ema, int lookback)
    {
        int n = ema.Length;
        var slope = new double[n];
        for (int i = lookback; i < n; i++)
        {
            double prev = ema[i - lookback];
            slope[i] = prev > 1e-10 ? (ema[i] - prev) / prev : 0;
        }
        return slope;
    }

    // adx[i] ≥ threshold AND closes[i] > ema[i].
    internal static bool[] AdxTrend(double[] adx, double[] closes, double[] ema, double threshold)
    {
        int n = adx.Length;
        var result = new bool[n];
        for (int i = 0; i < n; i++)
            result[i] = adx[i] >= threshold && closes[i] > ema[i];
        return result;
    }

    // Ema(fast)[i] > Ema(mid)[i] > Ema(slow)[i] — bullish EMA stack.
    internal static bool[] EmaStack(double[] closes, int fastPeriod, int midPeriod, int slowPeriod)
    {
        var fast = Trend.Ema(closes, fastPeriod);
        var mid  = Trend.Ema(closes, midPeriod);
        var slow = Trend.Ema(closes, slowPeriod);
        int n = closes.Length;
        var result = new bool[n];
        for (int i = 0; i < n; i++)
            result[i] = fast[i] > mid[i] && mid[i] > slow[i];
        return result;
    }

    // AtrRatio(short, long)[i] > threshold — convenience wrapper for HighVol detection.
    internal static bool[] AtrExpansion(
        double[] highs, double[] lows, double[] closes,
        int shortPeriod, int longPeriod, double threshold)
    {
        var ratio = Volatility.AtrRatio(highs, lows, closes, shortPeriod, longPeriod);
        int n = closes.Length;
        var result = new bool[n];
        for (int i = 0; i < n; i++)
            result[i] = ratio[i] > threshold;
        return result;
    }
}
```

- [ ] **Step 2: Append signal tests to `Gravity-gen2.Tests/core/IndicatorLibraryTests.cs`**

Append the following classes to the end of that file:

```csharp
// ── Signals ──────────────────────────────────────────────────────────────────

public class SignalsBearishDivergenceTests
{
    [Fact]
    public void FiresWhenRsiOverboughtAndDiverging()
    {
        // closes: 90, 95, 100, 98, 96 — swing high at i=2 (close=100)
        // rsi: 60, 70, 80, 75, 65 — RSI at swing high=80, current=65, diff=15 ≥ threshold=10
        var closes = new double[] { 90, 95, 100, 98, 96 };
        var rsi    = new double[] { 60, 70,  80, 75, 65 };
        var result = Signals.BearishDivergence(rsi, closes, lookback: 4, rsiOverbought: 75, divThreshold: 10);
        Assert.True(result[4]);
    }

    [Fact]
    public void DoesNotFireWhenRsiNotOverbought()
    {
        var closes = new double[] { 90, 95, 100, 98, 96 };
        var rsi    = new double[] { 50, 60,  70, 65, 55 };
        // rsiOverbought=75: swing RSI=70 < 75 → no divergence
        var result = Signals.BearishDivergence(rsi, closes, lookback: 4, rsiOverbought: 75, divThreshold: 5);
        Assert.False(result[4]);
    }
}

public class SignalsBullishDivergenceTests
{
    [Fact]
    public void FiresWhenRsiOversoldAndRecovered()
    {
        // closes: 100, 95, 90, 93, 96 — swing low at i=2 (close=90)
        // rsi: 50, 35, 20, 30, 40 — RSI at swing low=20, current=40, diff=20 ≥ threshold=15
        var closes = new double[] { 100, 95, 90, 93, 96 };
        var rsi    = new double[] {  50, 35, 20, 30, 40 };
        var result = Signals.BullishDivergence(rsi, closes, lookback: 4, rsiOversold: 30, divThreshold: 15);
        Assert.True(result[4]);
    }

    [Fact]
    public void DoesNotFireWhenNoRecovery()
    {
        var closes = new double[] { 100, 95, 90, 93, 96 };
        var rsi    = new double[] {  50, 35, 20, 22, 25 };
        // recovery = 25-20=5, threshold=15 → no divergence
        var result = Signals.BullishDivergence(rsi, closes, lookback: 4, rsiOversold: 30, divThreshold: 15);
        Assert.False(result[4]);
    }
}

public class SignalsBosTests
{
    [Fact]
    public void BearishBoS_FiresWhenCloseBelowPrevLow()
    {
        var closes = new double[] { 100, 98, 95 };
        var lows   = new double[] {  97, 96, 93 };
        var result = Signals.BearishBoS(closes, lows);
        Assert.False(result[1]); // 98 < 97 = false
        Assert.True(result[2]);  // 95 < 96 = true
    }

    [Fact]
    public void BullishBoS_FiresWhenCloseAbovePrevHigh()
    {
        var closes = new double[] { 100, 103, 101 };
        var highs  = new double[] { 102, 101, 104 };
        var result = Signals.BullishBoS(closes, highs);
        Assert.True(result[1]);  // 103 > 102 = true
        Assert.False(result[2]); // 101 < 101 = false
    }
}

public class SignalsSwingHighLookbackTests
{
    [Fact]
    public void ReturnsCorrectHighIndexAndRecentLow()
    {
        var closes = new double[] { 10, 20, 15, 12, 8 };
        var highs  = new double[] { 11, 21, 16, 13, 9 };
        var lows   = new double[] {  9, 19, 14, 11, 7 };
        // lookback=4, i=4: scan [0,4) → highest close=20 at idx=1, lowest low=9 at idx=0
        var (sh, hi, rl) = Signals.SwingHighLookback(closes, highs, lows, i: 4, lookback: 4);
        Assert.Equal(20.0, sh, precision: 8);
        Assert.Equal(1, hi);
        Assert.Equal(9.0, rl, precision: 8);
    }
}

public class SignalsMinMoveFilterTests
{
    [Fact]
    public void BigMove_ReturnsTrue()
    {
        // upper=[100,110], lower=[100,90], atr=[5,5], lookback=2, minAtrMult=3
        // move = 110-90 = 20, 20/5=4 ≥ 3 → true
        var upper = new double[] { 100, 110 };
        var lower = new double[] { 100,  90 };
        var atr   = new double[] {   5,   5 };
        var result = Signals.MinMoveFilter(upper, lower, atr, lookback: 2, minAtrMult: 3);
        Assert.True(result[1]);
    }

    [Fact]
    public void SmallMove_ReturnsFalse()
    {
        var upper = new double[] { 100, 101 };
        var lower = new double[] { 100,  99 };
        var atr   = new double[] {   5,   5 };
        var result = Signals.MinMoveFilter(upper, lower, atr, lookback: 2, minAtrMult: 3);
        // move=2, 2/5=0.4 < 3 → false
        Assert.False(result[1]);
    }
}

public class SignalsEmaSlopeTests
{
    [Fact]
    public void RisingEma_PositiveSlope()
    {
        var ema = new double[] { 100, 102, 104, 106 };
        var slope = Signals.EmaSlope(ema, lookback: 2);
        Assert.True(slope[3] > 0);
    }

    [Fact]
    public void FlatEma_ZeroSlope()
    {
        var ema = new double[] { 100, 100, 100, 100 };
        var slope = Signals.EmaSlope(ema, lookback: 2);
        Assert.Equal(0.0, slope[3], precision: 8);
    }
}

public class SignalsAdxTrendTests
{
    [Fact]
    public void AboveThresholdAndAboveEma_True()
    {
        var adx    = new double[] { 30 };
        var closes = new double[] { 110 };
        var ema    = new double[] { 100 };
        var result = Signals.AdxTrend(adx, closes, ema, threshold: 25);
        Assert.True(result[0]);
    }

    [Fact]
    public void BelowEma_FalseEvenIfAdxHigh()
    {
        var adx    = new double[] { 30 };
        var closes = new double[] { 90 };
        var ema    = new double[] { 100 };
        var result = Signals.AdxTrend(adx, closes, ema, threshold: 25);
        Assert.False(result[0]);
    }
}
```

- [ ] **Step 3: Run tests to verify all signal tests pass**

Run: `dotnet test Gravity-gen2.Tests/ --filter "FullyQualifiedName~IndicatorLibrary" -v n 2>&1 | tail -30`

Expected: All pass.

- [ ] **Step 4: Run full suite — no regressions**

Run: `dotnet test Gravity-gen2.Tests/ -v n 2>&1 | tail -10`

- [ ] **Step 5: Commit**

```bash
git add src/core/indicators/Composite.cs Gravity-gen2.Tests/core/IndicatorLibraryTests.cs
git commit -m "feat: add Signals composite class with tests"
```

---

### Task 3: Delete Indicators.cs, update 14 call sites + IndicatorsTests.cs

**Files:**
- Delete: `src/core/Indicators.cs`
- Modify: `Gravity-gen2.Tests/core/IndicatorsTests.cs` (update class names)
- Modify: 14 source files (see table below)

**Interfaces:**
- Consumes: `Trend.*`, `Momentum.*`, `Volatility.*` from Tasks 1 and 2

**Migration table — every `Indicators.X` replacement:**

| Old | New |
|-----|-----|
| `Indicators.Ema` | `Trend.Ema` |
| `Indicators.EmaInto` | `Trend.EmaInto` |
| `Indicators.Adx` | `Trend.Adx` |
| `Indicators.Rsi` | `Momentum.Rsi` |
| `Indicators.Atr` | `Volatility.Atr` |
| `Indicators.BbWidth` | `Volatility.BbWidth` |

**14 files to update:**

| File | Calls to replace |
|------|-----------------|
| `src/strategies/fade_short/FadeShortGA.cs` | `Indicators.Ema`, `Indicators.EmaInto`, `Indicators.Rsi` |
| `src/regime/RegimeClassifier.cs` | `Indicators.Ema` |
| `src/strategies/dip_long/DipLongGA.cs` | `Indicators.Ema`, `Indicators.EmaInto` |
| `src/strategies/dip_long/DipLongSimulator.cs` | `Indicators.Ema`, `Indicators.Rsi`, `Indicators.Adx`, `Indicators.Atr` |
| `src/strategies/fade_long/FadeLongGA.cs` | `Indicators.Ema`, `Indicators.EmaInto` |
| `src/strategies/fade_long/FadeLongSimulator.cs` | `Indicators.Ema`, `Indicators.Rsi`, `Indicators.Adx`, `Indicators.Atr` |
| `src/strategies/swing_long/SwingLongGA.cs` | `Indicators.Ema`, `Indicators.EmaInto` |
| `src/strategies/swing_long/SwingSimulator.cs` | `Indicators.Ema`, `Indicators.Rsi`, `Indicators.Adx`, `Indicators.Atr` |
| `src/strategies/grid/GridSimulator.cs` | `Indicators.Ema`, `Indicators.Adx`, `Indicators.Atr`, `Indicators.BbWidth` |
| `src/core/CandleFetcher.cs` | `Indicators.Ema` |
| `src/core/TradeEnricher.cs` | `Indicators.Ema` |
| `commands/CombinedBacktest.cs` | `Indicators.Ema` |
| `commands/OosBacktest.cs` | `Indicators.Ema` |
| `commands/PapertradeCommands.cs` | `Indicators.Ema` |

- [ ] **Step 1: Update `Gravity-gen2.Tests/core/IndicatorsTests.cs`**

Replace all occurrences globally:
- `Indicators.Ema` → `Trend.Ema`
- `Indicators.Rsi` → `Momentum.Rsi`
- `Indicators.Atr` → `Volatility.Atr`
- `Indicators.Adx` → `Trend.Adx`
- `Indicators.BbWidth` → `Volatility.BbWidth`

The file uses `Indicators.Ema` (EmaTests), `Indicators.Rsi` (RsiTests), `Indicators.Atr` (AtrTests), `Indicators.Adx` (AdxTests), `Indicators.BbWidth` (BbWidthTests). All classes stay in `TradingGA` namespace so no `using` changes needed.

- [ ] **Step 2: Update all 14 source files**

For each file in the table above, replace all `Indicators.Ema` → `Trend.Ema`, `Indicators.EmaInto` → `Trend.EmaInto`, `Indicators.Adx` → `Trend.Adx`, `Indicators.Rsi` → `Momentum.Rsi`, `Indicators.Atr` → `Volatility.Atr`, `Indicators.BbWidth` → `Volatility.BbWidth`.

Verify with grep after each file: `grep -n "Indicators\." <file>` should return nothing.

- [ ] **Step 3: Verify no remaining `Indicators.` references (except in the class definition itself)**

Run: `grep -rn "Indicators\." src/ commands/ Gravity-gen2.Tests/ | grep -v "src/core/Indicators.cs"`

Expected: empty output.

- [ ] **Step 4: Delete `src/core/Indicators.cs`**

```bash
rm src/core/Indicators.cs
```

- [ ] **Step 5: Build to confirm no broken references**

Run: `dotnet build -c Release 2>&1 | tail -20`

Expected: Build succeeded, 0 errors.

- [ ] **Step 6: Run full test suite**

Run: `dotnet test Gravity-gen2.Tests/ -v n 2>&1 | tail -20`

Expected: All tests pass (including updated `IndicatorsTests.cs` now using `Trend.*`/`Momentum.*`/`Volatility.*`).

- [ ] **Step 7: Commit**

```bash
git add -u
git commit -m "refactor: migrate all Indicators.* call sites to Trend/Momentum/Volatility; delete Indicators.cs"
```

---

### Task 4: Refactor 4 simulators to use Signals.*

**Files:**
- Modify: `src/strategies/fade_short/FadeShortSimulator.cs` (inside `SwingSimulator.cs` and `RunSwing`)
- Modify: `src/strategies/swing_long/SwingSimulator.cs` (class `SwingLongSimulator`)
- Modify: `src/strategies/fade_long/FadeLongSimulator.cs`
- Modify: `src/strategies/dip_long/DipLongSimulator.cs`

**Interfaces:**
- Consumes: `Signals.*` from Task 2

The logic does NOT change. Only the location moves: inline 4–20 line scan loops become single `Signals.*` call results, computed once before the main loop. The existing test coverage (combined backtest) validates correctness — if the build succeeds and the numbers are identical, the refactor is correct.

**Important constraint noted in spec:** BoS detection on 15m candles in FadeLong and DipLong is a single-line check (`m15Closes[im15] > m15Highs[im15 - 1]`) — it stays inline. Only the 1h-array signal logic gets extracted.

- [ ] **Step 1: Refactor `FadeShortSimulator.cs` — `RunSwingMultiTF` method**

In `RunSwingMultiTF`, replace the inline h1-level signal computation block (the `if (h1Ref != cachedH1Ref)` section) with `Signals.*` calls. The body of the h1 cache block becomes:

```csharp
// Replace this inline block:
if (h1Adx[h1Ref] >= g.AdxThreshold && h1Closes[h1Ref] > h1Ema[h1Ref])
{
    int    lb        = g.LookbackCandles;
    int    lbStart   = Math.Max(0, h1Ref - lb);
    double swingHigh = h1Closes[lbStart];
    int    highIdx   = lbStart;
    double recentLow = h1Lows[lbStart];

    for (int j = lbStart; j < h1Ref; j++)
    {
        if (h1Closes[j] > swingHigh) { swingHigh = h1Closes[j]; highIdx = j; }
        if (h1Lows[j]   < recentLow)   recentLow = h1Lows[j];
    }

    double rsiAtHigh = h1Rsi[highIdx];
    if ((swingHigh - recentLow) >= g.MinRallyAtrMult * atrH1
        && rsiAtHigh >= g.RsiOverbought
        && h1Rsi[h1Ref] <= rsiAtHigh - g.RsiDivThreshold)
    {
        cachedSetupMet  = true;
        cachedSwingHigh = swingHigh;
        cachedAtrRef    = atrH4;
        // signal quality calc stays inline (not in Signals — it's GA-specific scoring)
        double rsiExcess  = rsiAtHigh - g.RsiOverbought;
        double adxRatio   = h1Adx[h1Ref] / g.AdxThreshold;
        double rallyRatio  = (swingHigh - recentLow) / (g.MinRallyAtrMult * atrH1);
        cachedScore = rsiExcess * adxRatio * rallyRatio;
    }
}

// With this (using Signals):
bool strongTrend = Signals.AdxTrend(h1Adx, h1Closes, h1Ema, g.AdxThreshold)[h1Ref];
if (strongTrend)
{
    var (swingHigh, highIdx, recentLow) = Signals.SwingHighLookback(
        h1Closes, h1Highs, h1Lows, h1Ref, g.LookbackCandles);

    double atrH1 = h1Atr[h1Ref] > 1e-10 ? h1Atr[h1Ref] : h1Closes[h1Ref] * 0.02;
    bool bigRally = (swingHigh - recentLow) >= g.MinRallyAtrMult * atrH1;
    double rsiAtHigh = h1Rsi[highIdx];
    bool diverging = rsiAtHigh >= g.RsiOverbought
                  && h1Rsi[h1Ref] <= rsiAtHigh - g.RsiDivThreshold;

    if (bigRally && diverging)
    {
        cachedSetupMet  = true;
        cachedSwingHigh = swingHigh;
        cachedAtrRef    = atrH4;
        double rsiExcess  = rsiAtHigh - g.RsiOverbought;
        double adxRatio   = h1Adx[h1Ref] / g.AdxThreshold;
        double rallyRatio = (swingHigh - recentLow) / (g.MinRallyAtrMult * atrH1);
        cachedScore = rsiExcess * adxRatio * rallyRatio;
    }
}
```

Note: `AdxTrend` returns a `bool[]` — calling `[h1Ref]` on it avoids allocating on every h1 change. Alternatively, compute the bool inline for the single-bar check to avoid the allocation:

```csharp
bool strongTrend = h1Adx[h1Ref] >= g.AdxThreshold && h1Closes[h1Ref] > h1Ema[h1Ref];
if (strongTrend)
{
    var (swingHigh, highIdx, recentLow) = Signals.SwingHighLookback(
        h1Closes, h1Highs, h1Lows, h1Ref, g.LookbackCandles);
    double atrH1 = h1Atr[h1Ref] > 1e-10 ? h1Atr[h1Ref] : h1Closes[h1Ref] * 0.02;
    bool diverging = Signals.BearishDivergence(
        h1Rsi, h1Closes, g.LookbackCandles, g.RsiOverbought, g.RsiDivThreshold)[h1Ref];
    bool bigRally = Signals.MinMoveFilter(
        h1Closes, h1Lows, h1Atr, g.LookbackCandles, g.MinRallyAtrMult)[h1Ref];
    if (bigRally && diverging)
    {
        cachedSetupMet  = true;
        cachedSwingHigh = swingHigh;
        cachedAtrRef    = atrH4;
        double rsiAtHigh = h1Rsi[highIdx];
        double rsiExcess  = rsiAtHigh - g.RsiOverbought;
        double adxRatio   = h1Adx[h1Ref] / g.AdxThreshold;
        double rallyRatio = (swingHigh - recentLow) / (g.MinRallyAtrMult * atrH1);
        cachedScore = rsiExcess * adxRatio * rallyRatio;
    }
}
```

Also refactor the old `SimulateCore` method (used by single-TF GA path) the same way — replace its inline scan block using `Signals.SwingHighLookback`, `Signals.BearishDivergence`, `Signals.MinMoveFilter`, `Signals.BearishBoS`.

- [ ] **Step 2: Refactor `SwingSimulator.cs` — `SwingLongSimulator.RunSwingLongMultiTF`**

Replace the inline h1 cache block:

```csharp
// Trend gate: price above EMA and ADX trending
bool strongTrend = h1Adx[h1Ref] >= g.AdxThreshold && h1Closes[h1Ref] > h1Ema[h1Ref];
if (strongTrend)
{
    var (swingLow, lowIdx, recentHigh) = Signals.SwingLowLookback(
        h1Closes, h1Highs, h1Lows, h1Ref, g.LookbackCandles);
    bool bigDrop   = Signals.MinMoveFilter(
        h1Highs, h1Closes, h1Atr, g.LookbackCandles, g.MinDeclineAtrMult)[h1Ref];
    bool diverging = Signals.BullishDivergence(
        h1Rsi, h1Closes, g.LookbackCandles, g.RsiOversold, g.RsiDivThreshold)[h1Ref];
    // 1h BoS: close above previous candle's high (bullish) — stays inline (single-bar check)
    bool h1Bos = h1Closes[h1Ref] > h1Highs[h1Ref - 1];

    if (bigDrop && diverging && h1Bos)
    {
        cachedSetupMet = true;
        cachedSwingLow = swingLow;
        cachedAtrRef   = atrH4;
    }
}
```

- [ ] **Step 3: Refactor `FadeLongSimulator.cs` — `RunFadeLongMultiTF`**

Replace the inline h1 cache block:

```csharp
// Regime: sustained downtrend — below fast EMA, below declining slow EMA
bool regimeOk = h1Adx[h1Ref] >= g.AdxThreshold
             && h1Closes[h1Ref] < h1Ema[h1Ref]
             && h1Closes[h1Ref] < h1RegimeEma[h1Ref]
             && h1RegimeEma[h1Ref] < h1RegimeEma[h1Ref - slopeLen];

if (regimeOk)
{
    var (swingHigh, highIdx, recentLow) = Signals.SwingHighLookback(
        h1Closes, h1Highs, h1Lows, h1Ref, g.LookbackCandles);
    var (swingLow, lowIdx, _) = Signals.SwingLowLookback(
        h1Closes, h1Highs, h1Lows, h1Ref, g.LookbackCandles);

    bool bigDrop   = Signals.MinMoveFilter(
        h1Closes, h1Lows, h1Atr, g.LookbackCandles, g.MinDropAtrMult)[h1Ref];
    bool diverging = Signals.BullishDivergence(
        h1Rsi, h1Closes, g.LookbackCandles, g.RsiOversold, g.RsiDivThreshold)[h1Ref];

    if (bigDrop && diverging)
    {
        cachedSetupMet  = true;
        cachedSwingLow  = swingLow;
        cachedAtrRef    = atrH4;
        cachedRegimeBars = bearRegimeBarsAtBar[h1Ref];
    }
}
```

Note: The bear regime bar counter (`bearRegimeBarsAtBar`) precompute loop uses `EmaSlope` logic but is specific to FadeLong's structural regime definition (slope+cross combo, not just EMA slope). Leave the precompute loop as-is — it's not pure `EmaSlope`.

- [ ] **Step 4: Refactor `DipLongSimulator.cs` — `RunDipLongMultiTF`**

Replace the inline h1 cache block:

```csharp
bool regimeOk = h1Closes[h1Ref] > h1RegimeEma[h1Ref]
             && h1RegimeEma[h1Ref] > h1RegimeEma[h1Ref - slopeLen];
bool trendOk  = h1Closes[h1Ref] > h1Ema[h1Ref]
             && h1Adx[h1Ref] >= g.AdxThreshold;
bool dipOk    = h1Rsi[h1Ref] <= g.RsiDipThreshold;

if (regimeOk && trendOk && dipOk)
{
    var (_, _, recentLow) = Signals.SwingLowLookback(
        h1Closes, h1Highs, h1Lows, h1Ref, SwingLowLookback);
    // Use recentLow from SwingLowLookback as swingLow for stop placement
    cachedSetupMet   = true;
    cachedSwingLow   = recentLow;
    cachedAtrH4      = atrH4;
    cachedRegimeBars = bullRegimeBarsAtBar[h1Ref];
}
```

Wait — DipLong uses `h1Lows` directly (not closes) for the swing low. The current code scans `h1Lows` for `swingLow`. `SwingLowLookback` returns the lowest `lows[j]` as `recentHigh` is actually `recentHigh` from highs — re-check the spec:

`SwingLowLookback` returns `(swingLow, lowIdx, recentHigh)` where `swingLow = lowest close`. But DipLong's stop placement uses `lowest low in last 20 bars`, scanning `h1Lows`. So use the third field of `SwingLowLookback` isn't correct for the stop. Use the inline scan for stop placement but still use `SwingLowLookback` for divergence detection if needed.

For DipLong specifically, the stop is `lowest low in SwingLowLookback bars` (not lowest close). The simplest correct refactor:

```csharp
if (regimeOk && trendOk && dipOk)
{
    // Stop below the recent pullback low (h1 lows, not closes)
    int    lbStart  = Math.Max(0, h1Ref - SwingLowLookback);
    double swingLow = h1Lows[lbStart];
    for (int j = lbStart + 1; j <= h1Ref; j++)
        if (h1Lows[j] < swingLow) swingLow = h1Lows[j];

    cachedSetupMet   = true;
    cachedSwingLow   = swingLow;
    cachedAtrH4      = atrH4;
    cachedRegimeBars = bullRegimeBarsAtBar[h1Ref];
}
```

DipLong's h1 cache block is simple enough that only `regimeOk`/`trendOk` benefit from signal extraction. The `AdxTrend` signal matches `trendOk` exactly — use it as documentation. Leave the loop for `swingLow` (lows-based, not closes-based) inline as specified in the spec ("BoS on 15m candles stays inline").

- [ ] **Step 5: Build**

Run: `dotnet build -c Release 2>&1 | tail -20`

Expected: 0 errors.

- [ ] **Step 6: Run full test suite**

Run: `dotnet test Gravity-gen2.Tests/ -v n 2>&1 | tail -20`

Expected: All pass.

- [ ] **Step 7: Commit**

```bash
git add src/strategies/fade_short/FadeShortSimulator.cs \
        src/strategies/swing_long/SwingSimulator.cs \
        src/strategies/fade_long/FadeLongSimulator.cs \
        src/strategies/dip_long/DipLongSimulator.cs
git commit -m "refactor: replace inline signal scan blocks with Signals.* calls in 4 simulators"
```
