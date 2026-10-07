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

## Results (run 2026-10-07/08) — FAIL on t_weekly; every control passes

FadeShort is `.DISABLED` on the roster, so its raw trades were dumped with the genotype restored in a
throwaway copy (never committed): `reports/edgetest_raw_trades_fs_{oos,backtest}.csv`.

| maker, net %/yr | spread (this trial) | time-shift median | constant, same size | plain `fsmarket` (basket short) |
|---|---|---|---|---|
| OosCoins | **+11.4**, t_weekly **1.12**, vol 24.6%, Sharpe 0.46, maxDD −45% | +0.8 (real beats 20/20) | +4.4 | +11.1, t 0.83, vol 34.9%, shift −10.8, const −6.7 |
| BacktestCoins | +12.6, t 1.42, vol 22.0%, Sharpe 0.58 | +2.4 (20/20) | +6.8 | — |

Halves OOS +9.5 / +13.1 (both > 0); by year OOS 2021 −16.8, 2022 −8.1, 2023 +24.3, 2024 +36.8, 2025 +35.8,
**2026 −25.5**; last 10% −29.9%/yr (BacktestCoins −8.3%/yr).

Read: the spread did what theory said — vol 35% → 25%, the drift turned from a cost into a small tailwind
(constant row −6.7 → +4.4), and the timing still beats every shifted copy in both universes. But the timing
premium over the constant position shrank from ~18 to ~7 pts/yr: much of FadeShort's call is about the
alt LEVEL, which BTC shares, so the hedge removes some of the information along with the drift. t 1.12 < 2
→ FAIL. And it is decaying: 2026 is the worst year in both expressions, as with FadeShort's own trades.
