# PRE-REGISTRATION: cash-and-carry basis book (2026-10-06)

Written before any statistic of this book is computed. Frozen at the commit that adds this file
together with `research/basis_carry.py`. Read before freezing:
- data alignment;
- BTC's average funding: +12.8%/yr, mostly Bybit's 0.01%/8h floor;
- typical basis about −4 bp.

## Mechanism

Perp longs pay a funding premium to be long with leverage. A position that is long spot and short the
perp, at equal notional, has no price exposure. It collects funding and pays costs plus basis drift.
This is the lower-risk cousin of the repo's one live lead: the cross-sectional, factor-hedged
perp-only carry.

## Data

- **Spot:** Bybit spot hourly closes (`data/external/*_spot1h.csv`, from mid-2021).
- **Perp:** Bybit perp hourly closes.
- **Funding:** real per-symbol funding (`candle_cache`).
- **Universe:** coins that have spot, perp and real funding.

## Rule (fixed now)

- **Decision:** daily at 00:00 UTC. Trailing 7-day mean funding, annualised = F7.
- **Enter** when F7 > 15%/yr; **exit** when F7 < 5%/yr.
- **Size:** at most 20 positions, highest F7 first, each 5% of capital on the spot leg.
- **Execution:** at the close of the 00:00 bar.
- **P&L per day:** spot return − perp return (basis change) + funding received by the short perp.
- **Costs per round trip:** spot 0.1% + perp 0.055% per side, plus 2 × perp half-spread per side by
  liquidity quartile (recorder).
- **Capital:** unified margin, with the spot leg counted as perp collateral. Return is on capital.

## Comparisons

- **Baseline:** always-on BTC + ETH cash-and-carry, scaled to full capital. This is "just collect
  the funding floor".
- **Null:** the F7 signal shifted 30–180 days per coin, 20 times.

## PASS (BOTH universes)

1. net > 0 with weekly t ≥ 3;
2. both halves > 0;
3. last 12 months > 0;
4. beats ≥ 19 of the 20 shifts;
5. maxDD better than −15%.

**If PASS:** a forward shadow log, alongside the carry forward test.

**Trials:** 1.

## Known limits

- Spot spreads are assumed equal to perp spreads. The recorder only has perp books.
- No borrow, so no reverse carry when funding is negative.
- Survivors only.
