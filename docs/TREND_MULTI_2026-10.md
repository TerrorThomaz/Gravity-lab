# PRE-REGISTRATION: multi-coin trend hybrid, own trend × inverse volatility (2026-10-07)

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
