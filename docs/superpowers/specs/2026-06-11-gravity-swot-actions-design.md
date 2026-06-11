# Gravity-gen2 SWOT Actions — Design Spec
Date: 2026-06-11

## Scope

Seven implementation actions derived from the SWOT analysis:

1. Wire trained router into papertrade
2. Add DipLong + FadeLong + SwingLong to papertrade
3. Update CLAUDE.md
4. FadeLong bear-window training restriction
5. BayesianOptimizer post-GA refinement pass
6. Portfolio exposure cap (backtest + papertrade)
7. SwingLong strategy (bull-regime, alongside DipLong)

---

## Action 1 — Wire trained router into papertrade

**Problem:** `PapertradeCommands.cs` line 70 calls `RegimeRouter.Route(btcCandles, ethCandles)` — the rule-based overload that ignores any saved `regime_router_genotype.json`. Trained duration thresholds and confidence thresholds from `routertrain`/`coevolvetrain` are silently bypassed.

**Change:** At startup, attempt to load `Config.RouterGenoFile`. If found, store as `RegimeRouterGenotype? routerGeno`. In the regime routing block, call `RegimeRouter.Route(btcCandles, routerGeno, ethCandles)` when `routerGeno != null`, fall back to `RegimeRouter.Route(btcCandles, ethCandles)` otherwise. Print which path was taken at startup.

**Files:** `PapertradeCommands.cs`

---

## Action 2 — Add DipLong + FadeLong + SwingLong to papertrade

**Problem:** Three trained strategies are invisible in live monitoring.

**Prerequisites:** `GetDipLongTradeState` and `GetFadeLongTradeState` already exist (confirmed). `GetSwingLongTradeState` must be added as part of Action 7. All three take `(h1, m15)` arguments. DipLong, FadeLong, and SwingLong all require both 1h and 15m candles; papertrade currently only fetches 4h.

**Candle fetch change:** Fetch 15m candles per coin using `FetchFifteenMinCandlesCached` (batches: 5 ≈ 52 days; enough for all indicator warmup including EMA500 regime gate). Aggregate to 1h in the fetch loop. The existing 4h fetch for FadeShort/Grid can be removed once all strategies use 1h+15m, or kept as a fast path — prefer replacing it for consistency.

**Display:** Add three sections after the Grid section:
- `── SwingLong ──` gated on `ptRouting.DipLongActive`
- `── DipLong ──` gated on `ptRouting.DipLongActive`
- `── FadeLong ──` gated on `ptRouting.FadeLongActive`

Columns match FadeShort layout: Coin / State / Entry / Current / Unrealised / Bars / Stop / Target.

**Files:** `PapertradeCommands.cs`, `SwingSimulator.cs` (add GetSwingLongTradeState — DipLong and FadeLong TradeState methods already exist)

---

## Action 3 — Update CLAUDE.md

**Problem:** CLAUDE.md still describes the old single-strategy Swing architecture. Every future AI session starts with a broken mental model.

**Rewrite covers:**
- Strategy suite: FadeShort, Grid, DipLong, FadeLong, SwingLong (bidirectional)
- RegimeClassifier + RegimeRouter (BTC+ETH anchored)
- CoevolveGA (FadeLong + DipLong + Router, 4 cycles)
- All run modes including new ones
- Correct coin counts (93 backtest, 28 OOS)
- Saved genotype files

**Files:** `CLAUDE.md`

---

## Action 4 — FadeLong bear-window training restriction

**Problem:** FadeLong trains on 2022–2025 data which is predominantly bull. The bear-only fitness signal is diluted by long neutral/bull periods where FadeLong correctly produces no trades — but the GA still evaluates them as flat folds, noising the landscape.

**Change:** In `LongTrainCommands.RunFadeLongTrain`, after building the BTC regime series:
1. Call `session.GetWindows(MarketRegime.Bear, minBars: 200)` (200 h1 bars ≈ 8 days minimum bear run)
2. For each coin, filter training candles to those time windows before building `FadeLongGA.CoinData`
3. Apply the same window filter to val candles
4. If a coin has < 50 bear-window bars after filtering, skip it

This is not applied to the `coevolvetrain` path — coevolution already handles regime gating via soft weights. The standalone `fadelongtrain` mode gets the filtering.

**Constant:** `private const int BearWindowMinBars = 200;`

**Files:** `LongTrainCommands.cs`

---

## Action 5 — BayesianOptimizer post-GA refinement pass

**Problem:** `BayesianOptimizer` (TPE) is fully implemented but never called. GAs stop at their final elite without a local refinement step.

**Design:** After `GA.Run()` returns, apply 60 TPE iterations seeded from the top-5 elites:
1. Convert each elite to `double[]` using per-genotype `ToParams()` / `FromParams()` helpers
2. Seed history with `(params, fitness)` tuples from top-5
3. Call `BayesianOptimizer.Refine(history, bounds, evalFn, iterations: 60, rng)`
4. Convert best TPE result back to genotype; compare fitness; use whichever is better

Apply to: `FadeShortGA`, `FadeLongGA`, `DipLongGA`, `SwingLongGA`.
Skip: `RegimeRouterGA` (categorical/threshold genes don't benefit as much from KDE), `GridGA` (low parameter sensitivity).

`ToParams` / `FromParams` live as static helpers on each genotype class. Bounds arrays are defined once per genotype alongside the gene ranges already documented in the genotype comments.

**Files:** `SwingGenotype.cs`, `FadeLongGenotype.cs`, `DipLongGenotype.cs`, `SwingLongGenotype.cs`, `TrainCommands.cs`, `LongTrainCommands.cs`

---

## Action 6 — Portfolio exposure cap (backtest + papertrade)

**Architecture: two-pass portfolio replay**

Pass 1 (unchanged): each per-coin simulator runs independently, returns a `List<Trade>` with entry/exit times. No behavioral change here.

Pass 2 (new `PortfolioReplay` static class): walks all trades across all coins/strategies in chronological order. Maintains a per-strategy open-position counter. Before "executing" a trade, checks `openCount[strategy] < maxConcurrent[strategy]`. If over cap, the trade is marked skipped and excluded from metric calculations.

**Default caps** (not evolved — hard operational limits):
| Strategy | maxConcurrent |
|----------|--------------|
| FadeShort | 10 |
| SwingLong | 8 |
| DipLong   | 8 |
| FadeLong  | 8 |
| Grid      | 12 |

These are intentionally generous initially — enough to reduce catastrophic correlation events without frequently throttling normal operation.

**Integration points:**
- `CombinedBacktest.cs` and `OosBacktest.cs`: insert replay step after per-coin simulation loop, before metric aggregation
- Single-strategy backtest modes: same pattern
- Papertrade: simpler — count `InTrade == true` coins per strategy before the display loop; annotate signals at or above cap as `[CAP]` in the State column

**New file:** `PortfolioReplay.cs`

**Files:** `PortfolioReplay.cs` (new), `CombinedBacktest.cs`, `OosBacktest.cs`, `BacktestCommands.cs`, `PapertradeCommands.cs`

---

## Action 7 — SwingLong strategy

**Design:** Mirror of FadeShort using RSI bullish divergence + bullish BoS entry. Router-gated on `DipLongActive` (shared with DipLong — both are bull-regime long strategies).

### SwingLongGenotype (12 genes)

| Gene | Range | Notes |
|------|-------|-------|
| `EmaPeriod` | 20–100 | trend-direction EMA |
| `AdxThreshold` | 15–40 | trend strength gate |
| `LookbackCandles` | 12–120 | h1 bars to locate swing low |
| `RsiOversold` | 20–40 | RSI ceiling the swing low must clear |
| `RsiDivThreshold` | 5–15 | RSI must be this many pts above swing-low RSI |
| `MinDeclineAtrMult` | 3–10 | min decline (h1 ATR units) from recent high to low |
| `StopLossAtrMult` | 0.3–2.0 | ATR buffer below swing low |
| `TakeProfitAtrMult` | 2.0–10.0 | fixed profit target |
| `TrailingActivationAtrMult` | 1.0–4.0 | arm trail after this profit |
| `TrailingStopAtrMult` | 1.0–5.0 | trail distance from peak |
| `MaxHoldCandles` | 24–120 | h1 bars before forced exit |
| `PositionSizePct` | 1–5% | per-trade size |

### Entry logic

1. Bull regime gate: `DipLongActive` from router
2. Trend gate: `close > EMA` AND `ADX ≥ AdxThreshold`
3. Min decline: recent swing low ≥ `MinDeclineAtrMult × ATR` below recent high
4. RSI bullish divergence: current RSI ≥ swing-low RSI + `RsiDivThreshold` AND swing-low RSI ≤ `RsiOversold`
5. 1h BoS: `close > prevHigh`
6. 15m entry: `close > prev15mHigh`

### Exit logic

- Hard stop: `swingLow − StopLossAtrMult × ATR`
- Target: `entry + TakeProfitAtrMult × ATR`
- Trailing: armed after `TrailingActivationAtrMult × ATR` profit, trails `TrailingStopAtrMult × ATR` below peak
- MaxHold: forced exit at `MaxHoldCandles` h1 bars

### FoldScore

Identical to DipLongGA: `gain × wrMult × qualityMult × freqBonus / ddDiv × retentionMult`. `retentionMult` penalises giving back gains at fold end.

### New files

- `SwingLongGenotype.cs`
- `SwingLongGA.cs`
- Logic class `SwingLongSimulator` added to `SwingSimulator.cs`

### New run modes

- `swingLongtrain` — SwingLongGA, 93 coins, 5-fold WFV
- `swingLongbacktest` — OOS metrics on val set

### Not in scope for this iteration

- SwingLong not added to CoevolveGA (defer until independent edge is confirmed)
- Saved file: `swing_long_genotype.json`

---

## Implementation order

1. SwingLongGenotype + SwingLongSimulator + SwingLongGA (Action 7) — builds foundation used by other actions
2. FadeLong + DipLong GetTradeState methods if missing (prerequisite for Action 2)
3. Portfolio cap — PortfolioReplay.cs + backtest integration (Action 6)
4. BayesianOptimizer wiring — ToParams/FromParams + post-GA call (Action 5)
5. FadeLong bear-window training (Action 4)
6. Papertrade update — router genotype + all four long/short sections (Actions 1 + 2)
7. CLAUDE.md rewrite (Action 3)
