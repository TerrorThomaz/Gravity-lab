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

## RESULT (run once, 2026-10-06, pre-registration 2a9cef8): FAIL, plus a data defect found after the run

Log: `reports/basis_carry_run.txt`.

**Post-result defects. Both are disclosed, and both rows are kept in the log.**
1. **Bad spot data.** ZEC's delisted Bybit spot pair printed at about 1.5% of the perp from November 2023,
   then relisted on 2025-02-03. That created a fake +2,880% "basis" day. DASH spot has similar junk
   prints. Added after the run, as a data filter that does not look at outcomes:
   - |spot/perp − 1| ≤ 2% at both ends of a day, else no P&L and a forced exit;
   - a new position needs ≥ $200k of 24h spot turnover.
2. **A bug in the comparison only.** The always-on baseline used an infinite signal, which the exit
   check read as "no data", so it churned daily. Fixed by using a finite signal.

| | as pre-registered | + data filter | always-on BTC + ETH (full capital) |
|---|---|---|---|
| OOS (30 coins) | +31.9%/yr (fake: ZEC 2025), t 1.1 | **+0.97%/yr**, Sharpe 1.69, t 2.1, last 12m −0.15% | — (not in OosCoins) |
| BT (79 coins) | +6.16%/yr, t 6.9, last 12m −0.05% | **+4.51%/yr**, Sharpe 5.35, t 6.0, maxDD −0.95%, last 12m −0.05% | **+7.28%/yr**, Sharpe 8.0, maxDD −1.27%, last 12m +2.17% |

- **By year (BT, filtered):** 2021 +5.4, 2022 −0.1, 2023 +4.0, 2024 +13.8, 2025 +0.5, 2026 +0.0.
- **By year (always-on BTC + ETH):** 2021 +8.6, 2022 +2.3, 2023 +8.6, 2024 +12.3, 2025 +4.9, 2026 YTD +1.5.
- **Time-shift null:** beats 20/20 in both universes (funding persistence is real).

**Verdict: FAIL.** OOS t is 2.1, and on BT the last 12 months are ≈ 0, which fails rule 3.
- **The timed alt book is beaten by doing nothing clever.** Always-on BTC + ETH cash-and-carry earns more
  per unit of capital, at lower risk. What is being earned is Bybit's 0.01%/8h funding floor (about
  11%/yr gross), not a timing edge.
- **It has decayed too.** About 8–12%/yr in 2021 and 2023–24, 4.9% in 2025, and about 2%/yr annualised in
  2026. That is now below a stablecoin savings rate.
- **Lesson:** spot data on smaller coins needs a coherence filter before any basis study. The filter
  belongs in `fetch_external` consumers by default.
