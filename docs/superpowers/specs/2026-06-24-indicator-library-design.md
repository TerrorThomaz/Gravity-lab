# Indicator Library — Design

**Date**: 2026-06-24
**Status**: Approved

## Goal

Replace the single flat `src/core/Indicators.cs` with a structured library under `src/core/indicators/` (four files, four static classes). Extract composite signal logic that is currently copy-pasted inline across four simulators into a shared `Signals` class. The payoff: new strategies are assembled from named, tested building blocks instead of duplicating 20-line scan loops.

## Scope

- Create `src/core/indicators/Trend.cs`, `Momentum.cs`, `Volatility.cs`, `Composite.cs`
- Delete `src/core/Indicators.cs` after migrating all content
- Update all 14 call sites (listed below) to the new class names
- Refactor 4 simulators to replace inline signal logic with `Signals.*` calls
- Add unit tests for every new function
- No changes to genotypes, GAs, backtest commands, or the frontend

---

## 1. File Structure

```
src/core/indicators/
  Trend.cs        internal static class Trend
  Momentum.cs     internal static class Momentum
  Volatility.cs   internal static class Volatility
  Composite.cs    internal static class Signals
```

All files remain in the `TradingGA` namespace. No `using` statements needed at call sites.

---

## 2. `Trend.cs` — class `Trend`

Directional indicators: trend detection, moving averages, channel systems.

| Function | Signature | Notes |
|---|---|---|
| `Ema` | `(double[] closes, int period) → double[]` | Moved from `Indicators.cs`. Exponential MA, k=2/(period+1). |
| `EmaInto` | `(double[] closes, int period, double[] output) → void` | Moved. In-place write for GA perf. |
| `Sma` | `(double[] closes, int period) → double[]` | New. Simple MA with partial warmup fill. |
| `Adx` | `(double[] highs, double[] lows, double[] closes, int period) → double[]` | Moved from `Indicators.cs`. Wilder-smoothed ADX. |
| `Macd` | `(double[] closes, int fast, int slow, int signal) → (double[] macd, double[] sig, double[] hist)` | New. `macd = Ema(fast) − Ema(slow)`, `sig = Ema(macd, signal)`, `hist = macd − sig`. |
| `DonchianChannel` | `(double[] highs, double[] lows, int period) → (double[] upper, double[] lower)` | New. `upper[i] = max(highs[i-period+1..i])`, `lower[i] = min(lows[i-period+1..i])`. Breakout channels. |
| `KeltnerChannel` | `(double[] closes, double[] highs, double[] lows, int emaPeriod, int atrPeriod, double atrMult) → (double[] upper, double[] mid, double[] lower)` | New. `mid = Ema(closes, emaPeriod)`, `upper = mid + atrMult×ATR`, `lower = mid − atrMult×ATR`. |

---

## 3. `Momentum.cs` — class `Momentum`

Oscillators and rate-of-change measures.

| Function | Signature | Notes |
|---|---|---|
| `Rsi` | `(double[] closes, int period) → double[]` | Moved from `Indicators.cs`. Wilder-smoothed RSI. |
| `Stochastic` | `(double[] highs, double[] lows, double[] closes, int kPeriod, int dPeriod) → (double[] k, double[] d)` | New. `%K = (close − lowestLow) / (highestHigh − lowestLow) × 100`, `%D = Sma(K, dPeriod)`. |
| `Cci` | `(double[] highs, double[] lows, double[] closes, int period) → double[]` | New. `CCI = (typicalPrice − SMA(tp)) / (0.015 × meanDeviation)`. |
| `Roc` | `(double[] closes, int period) → double[]` | New. `(close[i] − close[i−period]) / close[i−period] × 100`. Percentage rate of change. |
| `NBarMomentum` | `(double[] closes, int period) → double[]` | New. `close[i] − close[i−period]`. Raw price difference. |

---

## 4. `Volatility.cs` — class `Volatility`

Range and volatility indicators.

| Function | Signature | Notes |
|---|---|---|
| `Atr` | `(double[] highs, double[] lows, double[] closes, int period) → double[]` | Moved from `Indicators.cs`. Wilder-smoothed ATR. |
| `BbWidth` | `(double[] closes, int period) → double[]` | Moved from `Indicators.cs`. `4σ / SMA × 100`. |
| `BollingerBands` | `(double[] closes, int period, double stdDevs) → (double[] upper, double[] mid, double[] lower)` | New. Full bands, not just width. `mid = Sma(period)`, `upper/lower = mid ± stdDevs×σ`. |
| `AtrRatio` | `(double[] highs, double[] lows, double[] closes, int shortPeriod, int longPeriod) → double[]` | New. `Atr(short) / Atr(long)`. Currently computed inline in `RegimeClassifier` as `atr14 / atr100`. Values >2.5 = HighVol. |

---

## 5. `Composite.cs` — class `Signals`

Boolean signal arrays derived by combining raw indicators. All functions take precomputed arrays (same pattern as existing indicators) — no raw candle scanning. The `i` index is the current bar being evaluated.

| Function | Signature | Notes |
|---|---|---|
| `BearishDivergence` | `(double[] rsi, double[] highs, int lookback, double rsiOverbought, double divThreshold) → bool[]` | `true` at bar `i` when: swing-high RSI ≥ `rsiOverbought` AND current RSI ≤ swingHighRSI − `divThreshold`. Extracted from `FadeShortSimulator` and `SwingSimulator` (identical logic, written twice). |
| `BullishDivergence` | `(double[] rsi, double[] lows, int lookback, double rsiOversold, double divThreshold) → bool[]` | Mirror: swing-low RSI ≤ `rsiOversold` AND current RSI ≥ swingLowRSI + `divThreshold`. Extracted from `FadeLongSimulator` and `SwingSimulator`. |
| `BearishBoS` | `(double[] closes, double[] lows) → bool[]` | `closes[i] < lows[i−1]`. Structure break to downside. Extracted from `FadeShortSimulator`, `SwingSimulator`. |
| `BullishBoS` | `(double[] closes, double[] highs) → bool[]` | `closes[i] > highs[i−1]`. Structure break to upside. Extracted from `FadeLongSimulator`, `DipLongSimulator`. |
| `SwingHighLookback` | `(double[] closes, double[] highs, double[] lows, int i, int lookback) → (double swingHigh, int highIdx, double recentLow)` | Scan `[i−lookback, i)`: find highest close and its index, and the lowest low. Returns a value tuple (not an array — called per-bar at entry only). Extracted from `FadeShortSimulator`, `FadeLongSimulator`. |
| `SwingLowLookback` | `(double[] closes, double[] highs, double[] lows, int i, int lookback) → (double swingLow, int lowIdx, double recentHigh)` | Mirror: lowest close + its index + highest high. Extracted from `FadeLongSimulator`, `DipLongSimulator`. |
| `MinMoveFilter` | `(double[] highs, double[] lows, double[] atr, int lookback, double minAtrMult) → bool[]` | `(max(highs) − min(lows)) / atr[i] ≥ minAtrMult` over `lookback` bars. Extracted from `FadeShortSimulator` (`bigRally`), `FadeLongSimulator` (`bigDrop`). |
| `EmaSlope` | `(double[] ema, int lookback) → double[]` | `(ema[i] − ema[i−lookback]) / ema[i−lookback]`. Positive = rising. Extracted from `DipLongSimulator`, `FadeLongSimulator`. |
| `AdxTrend` | `(double[] adx, double[] closes, double[] ema, double threshold) → bool[]` | `adx[i] ≥ threshold AND closes[i] > ema[i]`. Extracted from `FadeShortSimulator`, `SwingSimulator`, `FadeLongSimulator`. |
| `EmaStack` | `(double[] closes, int fastPeriod, int midPeriod, int slowPeriod) → bool[]` | `Ema(fast)[i] > Ema(mid)[i] > Ema(slow)[i]`. Bullish stack. Extracted from `RegimeClassifier`. |
| `AtrExpansion` | `(double[] highs, double[] lows, double[] closes, int shortPeriod, int longPeriod, double threshold) → bool[]` | `AtrRatio(short, long)[i] > threshold`. Convenience wrapper for HighVol detection. Extracted from `RegimeClassifier`. |

---

## 6. Simulator Refactoring

Four simulators get their inline signal blocks replaced with `Signals.*` calls. The logic does not change — only the location.

| File | Inline blocks replaced | Calls to |
|---|---|---|
| `FadeShortSimulator.cs` | `strongTrend`, swing-high scan, `bigRally`, `diverging`, `bos` | `Signals.AdxTrend`, `Signals.SwingHighLookback`, `Signals.MinMoveFilter`, `Signals.BearishDivergence`, `Signals.BearishBoS` |
| `SwingSimulator.cs` | `strongTrend`, swing-high scan, `bigRally`, `diverging`, `bos` | Same 5 as above |
| `FadeLongSimulator.cs` | `bearEmaBar` / `regimeOk`, swing scan, `bigDrop`, `diverging` | `Signals.EmaSlope`, `Signals.AdxTrend`, `Signals.SwingHighLookback`, `Signals.SwingLowLookback`, `Signals.MinMoveFilter`, `Signals.BullishDivergence` |
| `DipLongSimulator.cs` | `regimeBar`, `trendOk`, swing-low scan | `Signals.EmaSlope`, `Signals.AdxTrend`, `Signals.SwingLowLookback` |

Note: BoS detection in FadeLong/DipLong happens on 15m candles and is a single-line check — it stays inline rather than precomputing a bool[] over the full array.

---

## 7. Call-site Updates (`Indicators.*` → new class)

14 files updated to replace `Indicators.X` with `Trend.X`, `Momentum.X`, or `Volatility.X`:

| Old call | New call | Files affected |
|---|---|---|
| `Indicators.Ema` | `Trend.Ema` | `FadeShortGA`, `RegimeClassifier`, `DipLongGA`, `DipLongSimulator`, `FadeLongGA`, `FadeLongSimulator`, `SwingLongGA`, `SwingSimulator`, `GridSimulator`, `CandleFetcher`, `TradeEnricher`, `CombinedBacktest`, `OosBacktest`, `PapertradeCommands` |
| `Indicators.EmaInto` | `Trend.EmaInto` | `FadeShortGA`, `SwingLongGA`, `DipLongGA`, `FadeLongGA` |
| `Indicators.Adx` | `Trend.Adx` | `FadeLongSimulator`, `GridSimulator`, `SwingSimulator`, `DipLongSimulator` |
| `Indicators.Rsi` | `Momentum.Rsi` | `FadeShortGA`, `SwingSimulator`, `DipLongSimulator`, `FadeLongSimulator` |
| `Indicators.Atr` | `Volatility.Atr` | `SwingSimulator`, `DipLongSimulator`, `FadeLongSimulator`, `GridSimulator` |
| `Indicators.BbWidth` | `Volatility.BbWidth` | `GridSimulator` |

---

## 8. Tests

New test file: `Gravity-gen2.Tests/core/IndicatorLibraryTests.cs`

Each new function gets at least one deterministic test with a handcrafted price series:
- `Trend`: SMA correctness, MACD zero-line cross, Donchian high/low tracking, Keltner envelope symmetry
- `Momentum`: Stochastic %K at extreme prices (0 and 100), CCI formula verification, ROC sign, NBarMomentum
- `Volatility`: BollingerBands width matches BbWidth, AtrRatio >1 when vol expanding
- `Signals`: BearishDivergence fires only when RSI condition met, BullishDivergence mirror, BearishBoS on simple 3-bar series, SwingHighLookback returns correct index, MinMoveFilter threshold, EmaSlope sign, AdxTrend gates correctly

Existing `IndicatorsTests.cs` (if it exists) gets its call sites updated to the new class names.

---

## Spec Self-Review

- No TBDs or incomplete sections.
- `SwingHighLookback` and `SwingLowLookback` return value tuples (not arrays) because they are called per-bar at entry time only — this matches the current usage pattern in all simulators and avoids a full-array allocation for a rarely-fired operation.
- The BoS on 15m candles in FadeLong/DipLong is noted as staying inline — this is intentional (single line, different timeframe candle array, not worth the array allocation).
- `AtrRatio` in `Volatility` and `AtrExpansion` in `Signals` are complementary: `AtrRatio` returns the ratio array; `AtrExpansion` returns a boolean threshold check. RegimeClassifier uses the raw ratio; new strategies can use the bool convenience wrapper.
- All 14 call-site files are listed. The count was verified by `grep -rln "Indicators\."`.
