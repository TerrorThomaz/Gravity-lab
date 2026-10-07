# Pre-registration — FadeShort's market call as an alt-vs-BTC spread (2026-10-07)

Written and committed before the book was run. One trial.

## Why

Measured earlier (CLAUDE.md, market-neutral section): FadeShort's open-short breadth TIMES the market
(z +3.9 vs 20 time-shifted copies), but its per-coin shorts lose once hedged (−0.21%/trade, t −5.7:
the coins it picks keep outperforming), and the basket-short expression (`fsmarket`) is not significant
(+12.8%/yr, t_weekly 0.93, vol 35%, maxDD −49%): the basket's drift eats half the timing, and the same
signal on BTC alone LOSES (−6.2%/yr). So the information is about alts relative to the market leader,
not about the market's level. Express exactly that: short the alt basket, long BTC beta-neutral.

## Rule (fixed)

`fsmarket` unchanged except the book: at each hour, target gross = FadeShort open-short fraction
`act[t]` (same signal, known at the bar close, traded 1 bar later, re-traded on a 0.05 move or the daily
basket refresh, maker costs, real funding — the existing `run_fsmarket` machinery):
- alt leg: −act/N on each of the equal-weight top-40 liquid coins EXCLUDING BTCUSDT and ETHUSDT;
- BTC leg: +act·β on BTCUSDT, β = OLS beta of the alt basket's hourly return on BTC's over the
  trailing 30 days, recomputed at the daily refresh, clipped to [0, 3].

## Pass (S8 standalone route)

OosCoins (primary): t_weekly ≥ 2, AND net out-earns ≥ 19 of 20 time-shifted signal controls, AND
out-earns the CONSTANT spread of the same average size, AND net > 0 in both halves. BacktestCoins
(secondary): net > 0 (same sign). Survivorship (delisted alts missing) biases the alt short AGAINST
itself, so it cannot manufacture a pass.

Reported, not judged: by year, last 10%, the plain `fsmarket` rows beside it.
Script: `scripts/market_neutral_research.py fsmarket` (new `spread` basket), trade log via `GRAVITY_FS_TRADES`.
