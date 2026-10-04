# Forward test of the live GA grid (frozen 2026-10-04, window starts 2026-10-05)

**Why.** Every augmentation so far assumed the GA grid's edge is real. Its own evidence is
"real-looking, unproven": raw deflated Sharpe 0.838, gated 0.223, and the roster was selected on
the same window. Everything up to 2026-10-04 has been seen (`docs/RESEARCH_REVIEW_2026-10.md`, S5).
The only clean evidence is data that does not exist yet. This is evidence tier T3.

## What is frozen

**Genotypes.** Copies in `genotypes/frozen/forward_2026-10-05/` with `SHA256SUMS`:

```
c98f4ca92f3055ff027c2491ffdcdf1fa5fde480fb3de6354895c6c582faede3  grid_best_genotype.json
c1cc7f79d0cd504d7c23d78abce53a577760f6ebff3c3034c7cd762be27a175e  grid_short_genotype.json
```

`edgetest` reads `genotypes/`, so **do not retrain Grid / GridShort during the window**. If they
must change, restore these copies before evaluating, and check the hashes.

**Code.** The commit that adds this file: simulators, costs, caps and the default crowding 0.5.

**Universes.** OosCoins (PRIMARY) and BacktestCoins (secondary), from `Config.cs` as of this
commit.

## Data, kept current daily

`gravity-cacherefresh.timer` runs `dotnet run -- cacherefresh` every day. It tops up the 15m candle
and funding caches for every configured symbol.

**Why daily:** Bybit serves no history for delisted coins, so a coin that dies inside the window
would otherwise drop out of the evidence. That would be survivorship bias inside the forward test.
The timer is `Persistent=true`, so a missed day runs at the next wake.

## Evaluation (the only command that counts)

```
GRAVITY_OFFLINE=1 GRAVITY_EDGE_FROM=2026-10-05 dotnet run -c Release -- edgetest
GRAVITY_OFFLINE=1 GRAVITY_EDGE_FROM=2026-10-05 GRAVITY_EDGE_UNIVERSE=backtest dotnet run -c Release -- edgetest
```

Read the **RAW (no gate)** row: per-trade PF, CAGR, annualised Sharpe, maxDD off the daily
mark-to-market curve. The simulators warm up on full history; only trades entered from 2026-10-05
are booked.

**Backtest reference** (the same rows over Dec 2021 → Oct 2026), for comparison only:

| | PF | CAGR | annSharpe | maxDD |
|---|---|---|---|---|
| OosCoins | 1.29 | 11.0% | 2.26 | 3.3% |
| BacktestCoins | 1.37 | 19.2% | 2.91 | 2.4% |

## Decision rule (written before the window opens)

| when | rule | outcome |
|---|---|---|
| 2027-01-05 (3 mo), 2027-04-05 (6 mo) | report only | no decision; the sample is too small |
| any time | OosCoins forward raw maxDD > 10% (3× the backtest) | **KILL-SWITCH**: stop and review |
| 2027-07-05 (9 mo) | OosCoins forward raw annSharpe < 0.5 OR PF < 1.05 | **RETIRE**: the edge is gone (backtest 2.26 / 1.29) |
| 2027-07-05 (9 mo) | otherwise | **CONTINUE** to 12 months |
| 2027-10-05 (12 mo) | annSharpe ≥ 2.0 AND PF ≥ 1.15 in BOTH universes (t ≈ 2 over one year) | **CONFIRMED** (tier T3) |
| 2027-10-05 (12 mo) | positive but short of that | **UNPROVEN**: keep running; no augmentation is built on it |

**Power, stated honestly.** At the backtest Sharpe of ~2, nine months can falsify the edge but
cannot confirm it. Confirmation at t ≈ 2 needs about a year *at that Sharpe*. A true Sharpe of 1
would need ~4 years. These thresholds are not to be relaxed after seeing the data.
