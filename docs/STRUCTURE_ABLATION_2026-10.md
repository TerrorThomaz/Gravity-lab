# PRE-REGISTRATION: does market structure add information the open search could not see? (2026-10-05)

Written before any run on market data. Frozen at the commit that adds this file together with
`research/structure15.py`. Any change after that commit is a new trial and is logged as one.

## Why

`discover15.py` searched 2.0M 15m candidates with 29 features. All of them are **one-bar snapshots**:
window returns, EMA distances, RSI, ADX, BB width, ATR, range position, volume, funding, market
returns, breadth, hour, weekday. A break of structure is **stateful**: a swing level is confirmed
after the fact, stays relevant until it is broken, and can be 5 or 500 bars old. A depth-3 tree over
fixed windows cannot rebuild it. "The finder found nothing" therefore says nothing about structure:
the question was never asked. This trial asks it once.

## Prior (literature check, 2026-10-05, Sonnet web search, 3 searches; not all papers opened)

- **For, with a mechanism:** stop-loss orders cluster just beyond round numbers and take-profits at
  them (Osler 2000, FRBNY Staff Report 125, real FX order data). Simple technical rules carry
  some crypto predictability under multiple-testing control (Hudson & Urquhart 2021, *Annals of
  Operations Research* 297).
- **Against:** the same paper reports no out-of-sample predictability for Bitcoin. Round-number
  clustering in BTC is real but has no significant return pattern (Urquhart 2017, *Economics Letters*
  159). Rule-mining edges vanish after false-discovery control and costs (Bajgrowicz & Scaillet 2012,
  *JFE*; Sullivan-Timmermann-White 1999, from memory, not verified this session).
- **Untested:** BoS, change of character, liquidity sweeps, "third touch" and the other ICT /
  smart-money concepts have no rigorous test that the search found. Their prior is the null.
- **Implication:** expect an incremental IC below 0.01 if there is anything, at minutes-to-hours
  horizons (stop-order mechanics). Breakout and ATH effects overlap with momentum, which the snapshot
  features already carry. So the test is **incremental only**, and it needs a capacity placebo.

## Data and candidates (unchanged from discover15)

- Block A only: fills before 2025-07-01 (`SEAL`). Block B is not read. The script asserts it.
- `discover15.build` unchanged: 15m bars, rungs at close ∓ k·ATR14 (k = 1, 2, 3) with touch fills,
  k = 0 market entries, the same subsampling and **the same seed (20261009)**, so the candidate set
  is identical to discover15's. Costs, labels and market-hedged labels are unchanged.
- Model unchanged: the numpy GBM, one per side, depth 3, 150 rounds, quarterly walk-forward on
  BacktestCoins from 12 months in, purged by label end. OosCoins are scored, never trained on.

## The structure block (28 features, all known at the close of the arming bar)

Swing scales W ∈ {4, 16} 15m bars. A pivot high at bar p needs H[p] > max of the W bars before and
H[p] ≥ max of the W bars after. It becomes the standing level at bar **p + W**, when it is known.

| feature | per coin | on the market index | definition |
|---|---|---|---|
| `s_hi{W}`, `s_lo{W}` | ✓ | ✓ | (close − standing swing high / low) / ATR14 |
| `bos_dir{W}` | ✓ | ✓ | sign of the most recent break of structure: +1 first close above a standing high, −1 below a standing low |
| `bos_age{W}` | ✓ | ✓ | log(1 + bars since that break) |
| `hhhl{W}` | ✓ | ✓ | sign(high − previous high) + sign(low − previous low), from −2 to +2 |
| `sweep4` | ✓ | | +1 wick below an unbroken low and close back above; −1 the mirror |
| `tests_hi4`, `tests_lo4` | ✓ | | bars whose high (low) came within 0.25 ATR of the unbroken level, close not beyond |
| `pdh`, `pdl` | ✓ | | (close − prior UTC day high / low) / ATR |
| `rnd` | ✓ | | (close − nearest round number) / ATR; step = 10^(⌊log10 close⌋ − 1), two significant digits |
| `dath` | ✓ | | ln(close / all-time high so far) |
| `don_brk` | ✓ | | +1 close above the prior 96-bar high, −1 below the prior 96-bar low |

The market index is the equal-weight cumulative 15m log return, `market_path` in discover15. It has
no wicks, so it has no sweep or test features. Its "ATR" is the mean |15m move| over 14 bars.

**Instrument check:** `python3 research/structure15.py --selftest`. It checks that scrambling every
bar after t₀ changes no feature at or before t₀, and that pivot, BoS and sweep fire at exactly the
right bar on a hand-built path. It passed before this commit.

## Arms

| arm | features | role |
|---|---|---|
| A | base 29 | reproduces discover15 |
| **B** | base + all 28 structure features | **primary** |
| Bc / Bm | base + coin-only / market-only block | secondary, report only |
| P1–P5 | B with the structure block's rows permuted jointly (seeds 7000–7004) | capacity placebo: the same extra columns with no link to the outcome |

## Metric

Out-of-sample **rank IC** between prediction and net label, per (quarter × side), from the
walk-forward predictions. Each universe is reported separately. **ΔIC = IC(arm) − IC(A)** is paired
on the same quarter-side, with t across quarter-sides.

## PASS (all of it, H = 16 bars = 4h, written before the run)

"Structure carries information the search was blind to" iff, **in BOTH universes**:
1. mean ΔIC(B − A) > 0 with t ≥ 2;
2. ΔIC > 0 in both halves of the quarter-sides;
3. the real ΔIC beats all 5 placebo ΔICs.

**If FAIL:** report MDE80 = 2.8 · SE(ΔIC).
- MDE80 ≤ 0.01: "absent at the size the literature allows".
- MDE80 > 0.01: "unproven": the test could not have seen a literature-sized effect (the power-check
  rule).

**Not decided here:** tradeability. Arm B's selection table is printed in discover15's format, but
without label-permutation nulls. A PASS earns ONE new pre-registered trading trial using the
features that carried the gain (permutation importance is printed for arm B). That trial is judged
on block B once, and then forward.

## Trials

1 primary (B − A, H = 16) + 4 secondary (Bc, Bm, H = 4, H = 48) = **5**, added to the project ledger.
Not tuned: W, the 0.25-ATR touch band, the round-number step and the Donchian length are fixed above.

## RESULT (run once, 2026-10-05, pre-registration 4e35a6a): FAIL

Log: `reports/structure15_run.txt`. 1.20M BacktestCoins + 0.84M OosCoins candidates, identical to
discover15's. Out-of-sample rank IC, paired by quarter × side:

| H | univ | IC(A) | Δ(B − A) [t] | halves | MDE80 | placebos beaten |
|---|---|---|---|---|---|---|
| **16 (primary)** | BT | +0.0433 | **−0.0015 [−0.44]** | −0.0023 / −0.0008 | 0.0097 | **0/5** |
| **16 (primary)** | OOS | +0.0418 | **−0.0022 [−0.61]** | −0.0069 / +0.0025 | 0.0101 | **0/5** |
| 4 | BT / OOS | +0.048 / +0.049 | +0.0019 [+0.8] / +0.0003 [+0.1] | mixed | 0.006 / 0.007 | — |
| 48 | BT / OOS | +0.056 / +0.053 | −0.0009 [−0.2] / −0.0033 [−0.6] | mixed | 0.015 / 0.016 | — |

Secondary arms at H = 16:

| arm | BT Δ [t], halves | OOS Δ [t], halves |
|---|---|---|
| Bc (coin-only block) | +0.0041 [+2.25], +0.0021 / +0.0059 | +0.0033 [+1.60], +0.0013 / +0.0053 |
| Bm (market-only block) | −0.0052 [−1.61], −0.0056 / −0.0048 | −0.0059 [−1.73], −0.0107 / −0.0011 |

Permutation importance (arm B, BT): lbbw, disp16, k, mvol, hour, mz96, dow, then `m_s_lo16` (8th)
and `s_lo16` (9th). No BoS, sweep, test, round-number or prior-day feature is in the top 10.

**Verdict.**
- **PRIMARY FAIL.** The full structure block adds nothing at 4h in either universe, and it loses to
  every capacity placebo. MDE80 is 0.0097 / 0.0101, right at the literature's 0.01. So this is
  "absent at the size the literature allows" on BacktestCoins, and borderline on OosCoins.
- **The two halves of the block pull in opposite directions** (secondary, found by looking, not a
  pass):
  - Coin-level structure adds about +0.004 IC, the same sign in all four universe-halves (t 2.25 /
    1.60). That is +10% relative to a base IC that already failed to reach the cost line, so it is
    not tradeable. It is the only hint in the trial.
  - Market-index structure *hurts* (−0.005 to −0.006). Market features are identical for every
    coin at a timestamp, so a tree that splits on them is carving out time periods. Each
    walk-forward window holds only a few market regimes, which makes this the same sample limit
    that stopped discover1h from learning the trend. **Market-level structure cannot be tested by a
    learner here.** Like the trend, it would need one theory-backed, pre-registered rule.
- **Not done:** no trading trial is earned. The coin-level hint would need its own pre-registration,
  and at +0.004 IC it is not worth one.

Trials charged: 5 (1 primary + 4 secondary), as registered.
