# FORWARD PRE-REGISTRATION: two leads from 2026-10-07 (frozen at the commit that adds this file)

Both leads came from secondary or descriptive results on seen data:
- the cascade rebound from `GRID_OI_CASCADE_2026-10.md`;
- the lead-lag from `LEADLAG_2026-10.md`.

**Only data from 2026-10-08 00:00 UTC on counts.** Rules are fixed below; nothing is tuned afterwards.
Both are shadow tests: no orders.

## 1. Lead-lag follower (`research/leadlag_forward.py`)

**Data:** the recorder's per-minute ticker snapshots (`data/recorder/*/tickers.csv`). One REST call
covers all symbols, so quotes are synchronous; this removes the stale-last-trade doubt from the 1m
candle study.

**Event:** BTC mid moves ≥ 0.25% between consecutive snapshots. Events within 10 minutes of the
previous one are skipped.

**Followers (fixed now):** the 20 most liquid recorder symbols by median 24h turnover before
2026-10-07, BTC excluded. Liquidity only, no outcomes:
ETH, SOL, ZEC, XRP, NEAR, SAND, HYPE, QNT, SUI, ADA, WLD, DOGE, ENA, STRK, AAVE, ONDO, 1000PEPE,
UNI, BNB, TAO.

**Trade:**
- all followers in BTC's direction;
- enter at the NEXT snapshot (≈ 1 minute latency) at the ask for longs / bid for shorts;
- exit 10 snapshots later at the bid / ask;
- Bybit taker 0.055% per side;
- per-event P&L = the mean over the followers.

**Report only:**
- 0-latency entry;
- Hyperliquid fees (0.045%).

**Decision:** on or after **2027-01-07**, with ≥ 300 events.
- **PASS:** primary mean net > 0, with t ≥ 3 over events, and both halves > 0.
- **Otherwise RETIRE.** A PASS earns a paper-trading implementation, not live capital.

**Data dependency:** the recorder must run. Snapshot gaps inside a hold void that event; they are
not filled in.

## 2. Liquid-coin cascade rebound (`research/cascade_forward.py`)

**Rule:** `grid_oi_cascade`'s event study, frozen.
- **Universe:** BacktestCoins.
- **Event:** hourly price z ≤ −2 AND hourly OI-change z ≤ −2, each against the trailing 720h, strictly
  before.
- **Trade:** long at the next hour's open, exit at the close 8h later, taker 0.21% round trip.

**Variants:**
- **PRIMARY:** ungated.
- **Secondary:** gated, trading only when the frozen trend hybrid's logged signal for the last closed
  day is +1. This tests whether the bear-year loss (2022: −1.08 per event) is avoidable by a signal
  that already exists.

**Data:** OI after the backfill is fetched at evaluation time from Bybit's public history
(`--fetch`, into `data/external/oi/`). Candles come from the daily cacherefresh.

**Dates:**
- **2027-04-08:** report only.
- **2027-10-08: decision.**
  - **PASS:** primary mean net > 0, with day-clustered t ≥ 2, and both halves > 0.
  - **Otherwise RETIRE.** If the gated variant passes and the primary does not, it is reported as a
    candidate for a new forward test, not adopted.

**Expected sample:** about 700 events a year on BacktestCoins (4,037 events / 5.7 years in the study).
At the study's effect size (+0.44% net, t 2.5 over 5.7 years) a single year has low power. A FAIL at
12 months with a positive mean is "unproven", per the power-check rule.

**Trials:** 2 (one per lead), on the project ledger.
