# Regime-Gated Strategy Architecture

## Overview

Each bar, a shared **RegimeDetector** classifies the market state. Strategies only fire in their intended regime — no strategy needs to generalise over the full window.

```
RegimeDetector(LongEmaPeriod, SlopeLookback)
     │
     ├── Bull    → DipLong fires  (FadeShort fires with less frequency)
     ├── Bear    → FadeLong fires (FadeShort still fires on dead-cat shorts)
     └── Neutral → FadeShort + Grid only
```

**Key benefit:** DipLong is not penalised for not firing in 2022. FadeLong is not penalised for the 2024 bull. Each GA's fitness landscape is clean because it only sees data from its intended regime.

---

## Regime Detector

Two evolved genes (part of DipLong and FadeLong genotypes):

| Gene | Range | Purpose |
|------|-------|---------|
| `RegimeLongEmaPeriod` | 100–500h | Bull/bear baseline EMA |
| `RegimeSlopeLookback` | 10–60h | How far back to measure EMA slope |

**Bull condition:**
```
close > RegimeLongEma  AND  RegimeLongEma[now] > RegimeLongEma[N bars ago]
```

**Bear condition:**
```
close < RegimeLongEma  AND  RegimeLongEma[now] < RegimeLongEma[N bars ago]
```

The GA discovers optimal values — whether 200h or 300h works better, whether slope needs 20 or 50 bars — without hardcoding.

---

## DipLong Strategy (new)

Complement of FadeShort. Both fire within the same uptrend regime but at opposite ends of the RSI cycle:

- **FadeShort**: RSI overbought (70+) → fade the extension short
- **DipLong**: RSI pulled back (40–50) → buy the dip in the trend

### Genotype (11 genes)

| Gene | Range | Notes |
|------|-------|-------|
| `RegimeLongEmaPeriod` | 100–500h | Bull market gate EMA |
| `RegimeSlopeLookback` | 10–60h | EMA slope detection window |
| `EmaPeriod` | 20–100h | Short-term trend EMA (entry context) |
| `AdxThreshold` | 15–35 | Trend strength gate |
| `RsiDipThreshold` | 35–55 | Max RSI for dip entry |
| `StopLossAtrMult` | 0.5–2.0 | Hard stop below swing low |
| `TakeProfitAtrMult` | 2.0–10.0 | Fixed profit target |
| `TrailingActivationAtrMult` | 1.5–5.0 | When to arm trailing stop |
| `TrailingStopAtrMult` | 1.0–5.0 | Trail distance |
| `MaxHoldCandles` | 10–60 | Max bars before forced exit |
| `PositionSizePct` | 1–5% | Per-trade size |

### Entry Logic

1. **Regime gate**: `close > RegimeLongEma` AND `RegimeLongEma` slope rising
2. **Trend gate**: `close > ShortEma` AND `ADX ≥ AdxThreshold`
3. **Setup**: 1h RSI ≤ `RsiDipThreshold` (pullback in trend)
4. **Trigger**: 15m bullish Break of Structure
5. **Stop**: below most recent 1h swing low (clean invalidation)

### Why this works

In a strong bull market, every RSI dip to the 40–50 zone gets bought. The stop (below swing low) is tight and well-defined. FadeShort struggles in this environment because extensions are immediately reclaimed — DipLong compensates.

In a bear market, `close < RegimeLongEma` with declining slope → DipLong never fires → zero bear-market risk.

---

## Kept Profits Fitness

Current `FoldScore` penalises the worst drawdown trough (`ddDiv`) but doesn't penalise giving back gains at the *end* of a period.

### Add `retentionMult` to FoldScore (all strategies)

```csharp
double peakGain  = peak - 1.0;
double finalGain = balance - 1.0;
double retentionMult = peakGain > 0.01
    ? Math.Max(0.2, finalGain / peakGain)
    : 1.0;

return gain * 100.0 * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
```

### Effect

| Scenario | retentionMult | Impact |
|----------|--------------|--------|
| Peaked +25%, ended +25% | 1.00 | No change |
| Peaked +25%, ended +12% | 0.48 | Score nearly halved |
| Peaked +25%, ended +2%  | 0.08 → clamped 0.20 | Heavily penalised |

The GA learns to prefer exits that lock in gains rather than riding reversals back toward zero. In practice pushes toward: tighter trailing stops, higher TP targets, shorter MaxHold.

Apply to **all** GAs (FadeShort, DipLong, FadeLong, Grid) — kept profits is universally desirable.

---

## Risk Management Genes

A small cluster of portfolio-level genes co-evolved with each strategy. Controls position scaling when the portfolio is in a profitable state being threatened:

| Gene | Range | Purpose |
|------|-------|---------|
| `ProfitLockThreshold` | 5–30% | Activate protection when portfolio is up X% |
| `DrawbackTolerance` | 2–15% | Allow Y% drawback from peak before scaling down |
| `ProtectedSizeFactor` | 0.2–0.8 | In protection mode, multiply all sizes by Z |

### Fitness signal

```
keptProfitScore = (finalEquity - 1.0) / (peakEquity - 1.0)
```

What fraction of peak profits were retained at period end. Combined with `retentionMult` in FoldScore, this creates a consistent signal across all fitness evaluations.

---

## Portfolio Complement Matrix

| Market | FadeShort | DipLong | FadeLong | Grid |
|--------|-----------|---------|----------|------|
| Strong bull | Low freq (trend too strong to fade) | **Primary** | Silent | Silent |
| Moderate bull | **Active** | **Active** | Silent | Silent |
| Neutral/range | **Active** | Silent | Silent | **Active** |
| Bear rally | **Active** (dead-cat shorts) | Silent | Silent | Reduced |
| Strong bear | Low freq | Silent | **Active** | Silent |

---

## Implementation Checklist

- [ ] `DipLongGenotype.cs` — 11 genes listed above
- [ ] `DipLongSimulator.cs` — bull regime gate, RSI dip entry, 15m bullish BoS, swing-low stop
- [ ] `DipLongGA.cs` — fold structure with `retentionMult` in FoldScore
- [ ] Modify `FoldScore` in `BullGA.cs`, `FadeLongGA.cs`, `SwingGA.cs` to add `retentionMult`
- [ ] Add `diplong` train/backtest modes to `Program.cs`
- [ ] Add risk management genes to `DipLongGenotype` and evaluate fitness signal

---

## Notes

- FadeLong is structurally correct but its test window (2022–2025) is mostly bull. Bear-only strategy will show low OOS trade count in bull periods — this is expected, not a bug.
- The `retentionMult` clamp floor of 0.2 prevents one catastrophic fold from zeroing out an otherwise good strategy.
- DipLong and FadeShort are NOT competitive — they use the same regime (uptrend) but fire at opposite RSI extremes. Running both simultaneously is additive, not redundant.
