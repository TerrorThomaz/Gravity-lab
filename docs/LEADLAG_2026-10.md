# Lead-lag to BTC (2026-10-07, descriptive measurement, no trial)

Script `research/leadlag.py`, log `reports/leadlag_run.txt`. Pooled OLS: a follower's return over t+1..t+k
on BTC's return in bar t, controlling for BTC's own t+1..t+k return and the follower's bar-t return.

| data | k | lagged response to BTC(t) | beta to BTC's next move |
|---|---|---|---|
| 1m, 148 coins, 2025-08 → 2026-09 | 1 min | +0.153 | 1.07 |
| | 5 min | +0.295 | 1.33 |
| | 10 min | +0.425 | 1.41 |
| | 15 min | **+0.441** | 1.43 |
| 15m, 151 coins, 2020-03 → 2026-10 | 1 bar | +0.070 | 1.28 |
| | 4 bars | +0.075 | 1.28 |

**Event view.** BTC 1-minute moves ≥ 0.25% (4,114 events). The follower's move in BTC's direction
beyond its beta to BTC's own next move:

| next | 1 min | 2 min | 5 min | 10 min |
|---|---|---|---|---|
| excess move | +0.051% | +0.063% | +0.109% | **+0.158%** |

The cost bar is about 0.10% round trip (Hyperliquid taker).

**Followers DO lag BTC.** About 44% of a BTC minute move reaches the average follower only over the next
15 minutes. At 15m bars about 7% is still left.

**Caveats:**
- **Stale prices.** Minute closes of illiquid coins are last trades, so part of an apparent lag is
  non-synchronous trading, which cannot be traded at that price. Settling it needs order-book mids;
  the recorder logs those.
- **Pooled t-stats are inflated** (cross-coin correlation).
- **All 1m data lies in block B.**

**Forecast engine on BTC:**
- 4h rank IC +0.022; direction hit rate **50.6%**.
- The top decile hits 52.5%, at +0.057% per 4h.
- **No usable accuracy on BTC itself.** The follower trade needs no forecast, though: react to BTC's
  realised move.
