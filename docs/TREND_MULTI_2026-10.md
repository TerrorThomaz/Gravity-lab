# PRE-REGISTRATION: multi-coin trend hybrid, own trend × inverse volatility (2026-10-06)

Written before any statistic of this book is computed. Frozen at the commit that adds this file
together with `research/trend_multi.py`. Data was fetched first; it carries no outcomes.

## Why

The forward-tested hybrid trades BTC + ETH on one shared market signal. It is the book's only real
hedge: alpha t 1.74 against the book, and positive on the book's worst days. The question is whether
the same mechanics over more coins improve it:
- each coin on its own trend (Moskowitz-Ooi-Pedersen);
- **inverse-volatility weights** (the user's "inverse risk").

## Universe (fixed now)

- **Coins:** BTC, ETH, BNB, XRP, ADA, LTC, EOS, XLM, TRX, ETC.
- **Why these:** prominent Binance USDT spot pairs by mid-2018, chosen for 2018 prominence and not for
  2026 survival. EOS's data ends in May 2025 (rebrand); it drops out.
- **Data:** Binance spot daily from 2017–18, plus USDT-M funding from 2019–20.

## Rule

- **Mechanics:** the hybrid's, unchanged:
  - 7 cohorts held 7 days, |gross| ≤ 1;
  - spot longs, perp shorts paying or receiving funding (floor where no real rate);
  - taker 0.105% per side on the net change.
- **Signal:** each coin's own 30-day log-return sign.
- **Weights:** w ∝ sign / trailing-60-day vol, Σ|w| = 1.

**Reported alongside:**
- the same coins on the shared market sign;
- equal-weight buy-and-hold;
- the frozen BTC + ETH hybrid.

## PASS

1. **No bleed:** Sharpe > 0 over the full window and in both halves.
2. **Better than the frozen hybrid:** Sharpe > the BTC + ETH hybrid's in the full window AND in both
   halves.
3. **Placebo:** the real Sharpe beats ≥ 90% of 100 per-coin signal shifts.

**Reported, not gated:**
- alpha t against the live book;
- the mean on the book's worst 5% days.

**If PASS:** a second forward shadow next to the frozen hybrid. The hybrid's own spec does not change.

**Trials:** 1.

## RESULT (run once, 2026-10-06, pre-registration 3bf211f): FAIL (second half below the hybrid)

Log: `reports/trend_multi_run.txt`. Window 2017-11-15 → 2026-10-06; median 10 coins live per day.

| book | CAGR | Sharpe (halves) | maxDD |
|---|---|---|---|
| **own trend × inverse vol, 10 coins** | +50.1% | **1.00** (1.28 / **0.60**) | −56% |
| market sign × inverse vol, 10 coins | +26.1% | 0.68 (1.04 / 0.13) | −72% |
| buy & hold, equal weight | +37.6% | 0.81 (1.05 / 0.46) | −86% |
| **BTC + ETH hybrid (frozen)** | +41.5% | **0.87** (0.97 / **0.75**) | −75% |

**Sharpe by year:**

| book | 2023 | 2024 | 2025 | 2026 |
|---|---|---|---|---|
| multi-coin | 0.0 | 1.3 | 0.4 | −0.2 |
| BTC + ETH hybrid | 1.0 | 0.6 | 0.9 | 0.6 |

**Controls and book fit:**
- **Placebo:** beats 100% of signal shifts (median +0.01).
- **Correlation with the hybrid:** 0.78; alpha t on the hybrid 1.56.
- **Against the live book:**
  - alpha t 1.11 for the multi-coin book, against **1.74** for the hybrid;
  - on the book's worst 5% days, +0.33%/day against **+0.40%/day** for the hybrid.

**Verdict: FAIL** on rule 2 (second half 0.60 < 0.75).
- **Own trends with inverse-vol weights are better than one shared market sign** (1.00 vs 0.68): the
  coin-level trend is real.
- **The edge is front-loaded in 2017–21.** Since 2023 the 10-coin book is weaker and less stable than
  BTC + ETH. It is also a worse hedge for this book: lower alpha against it, and smaller on its worst
  days.
- **Keep the frozen BTC + ETH hybrid.**
