# Forward test: carry, plain vs volatility-managed (frozen 2026-10-05, window starts 2026-10-05)

**Why.**
- Carry is the live book's tail. On the book's worst 5% days it loses −1.95%/day.
- On BacktestCoins it bleeds since 2024 (half-2 Sharpe −0.16), so it may be decaying.
- Vol-managed carry (trial A) would lift the book from Sharpe 1.63 to 1.93 and halve its maxDD, but it
  failed its pre-registration on that bleed.
- Everything to 2026-10-04 is seen. Only forward data can settle either question.

## Spec (frozen at the commit that adds this file)

- **plain:** `market_neutral_research.run_carry` with `CarryParams(band=0.005)`. Quantile funding
  legs projected off the dollar direction and the top 3 PCs; daily rebalance; real funding; taker
  costs.
- **A (vol-managed):** `research/carry_hedge.py`. Each day the targets are multiplied by
  min(1, σ_ref/σ_7d), computed from plain carry's daily net strictly before the day (7d std; σ_ref =
  its 180-day median).
- **Universes:** OosCoins (PRIMARY, the live book's) and BacktestCoins (decay check).

## Data and evaluation

- No logger is needed. The spec is deterministic on exchange data, and `gravity-cacherefresh.timer`
  archives the inputs daily: 15m candles and funding for every configured symbol, including coins
  that delist.
- Evaluate from the frozen worktree `~/Gravity-lab-forward`:
  `python3 research/carry_hedge.py --forward --trades <edgetest trade log of the forward window>`.
  The trade log comes from `GRAVITY_OFFLINE=1 GRAVITY_EDGE_FROM=2026-10-05 dotnet run -c Release -- edgetest`,
  and it is used for the book line.

**Backtest reference** (2021-12 → 2026-10, OosCoins):

| | Sharpe | maxDD | worst-1% day |
|---|---|---|---|
| plain | 0.57 | −21.4% | −2.48% |
| A | 0.67 | −18.1% | −2.08% |

## Decision rule (written before the window opens)

| when | rule | outcome |
|---|---|---|
| 3 / 6 / 9 months | report only | no decision |
| any time | OosCoins plain carry drawdown worse than −21.4% (the backtest's) | **KILL-SWITCH**: stop and review |
| 2027-10-05 (12 mo) | OosCoins plain net Sharpe ≤ 0 OR alpha vs buy-and-hold ≤ 0 (both benchmarks) | **RETIRE carry** (it bleeds) |
| 2027-10-05 | BacktestCoins plain net Sharpe ≤ 0 as well | recorded as **decay** evidence: carry's premium is shrinking |
| 2027-10-05 | carry kept AND A has net Sharpe > 0, a better worst-1% day than plain, and a book Sharpe ≥ the plain book's | **A replaces plain** in the live book |

**Honest limits.**
- Twelve months of carry at Sharpe ~0.6 gives t ≈ 0.6: a screen against bleeding, not proof.
- The A-vs-plain comparison rests on a handful of stress episodes inside one year.
- Neither variant is refit during the window.
