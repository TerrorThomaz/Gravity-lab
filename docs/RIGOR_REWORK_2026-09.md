# Rigour rework — September 2026

**One sentence:** every headline number this repo produced was an artifact, the artifacts are now
removed, and what survives is a smaller, slower, defensible system.

Before: `oosbacktest` reported **PF 2.51, Sharpe 10.95, +1493%, `VERDICT A edge=A robustness=A`**.
After: **13.1% CAGR, Sharpe 6.00, 1.0% max drawdown, deflated Sharpe 1.000** on never-trained
coins, walk-forward gated, on three strategies instead of eight.

Those are not the same system measured twice. The first set could not have returned a negative
answer; the second can, and did, repeatedly.

---

## 1. Why the old numbers were wrong

Three independent problems, each sufficient on its own to void the result.

**The gate was fitted on what it gates.** `genotypes/strategy_family_gate.json` computes
(strategy, regime) profit factors over the whole training trade set, and `RegimeRouterSession`
then routes those same trades on the result — with `FamilyConfidence = in-sample PF / 3` sizing
the position directly. It already knows which regimes each strategy turned out to work in.

**"Sharpe" was not a Sharpe.** `Simulator.SharpeRatio` is a per-trade `mean/std` scaled by
`sqrt(candleCount / 288)`. The scale factor depends on how many candles the book spans, which is
why it printed 10.95. It also returned exactly `0` for any book with profit factor below 1.3, so a
losing book and a flat one were indistinguishable in every report.

**Nothing was deflated for the search.** Thousands of genotypes were evaluated to pick the ones in
`genotypes/`. The best of N random strategies has a high in-sample Sharpe by construction.
`StatisticalTests` had carried `DeflatedSharpeRatio`, `ProbabilityOfBacktestOverfitting` and
`WhitesRealityCheck` for months with nothing calling them on a portfolio book.

A fourth, found later: **`PF 2.51` was mostly an in-sample coin screen.** `oosbacktest` keeps
9,103 of 77,490 trades (12%) because each coin must first pass `PF ≥ 1.2 && Sortino ≥ 0.3` *on its
own data*. Measured honestly on a trailing window, per-coin selection has **no** out-of-sample
power here (PF 1.01 vs 1.00 raw). Remove the screen and PF goes 2.51 → 1.04.

---

## 2. Defects found and fixed

| # | Defect | Impact | Status |
|---|---|---|---|
| 1 | `IsActive` routed through the SIMFAM gate, `SizeGate` fell through to legacy thresholds and returned **0** | **56–71%** of rows in committed report CSVs were zero-notional: took a slot, entered the book, earned nothing | Fixed — one `Resolve()` |
| 2 | `SizeGate`: `w < 0.50 → 1.0` — size peaked at the router's **lowest** conviction | Train (`FilterActive`) shared the rule, paying the GA to drive favorability under 0.5 and neutralise its own sizing layer | Fixed — size is the conviction weight |
| 3 | `IsActive`/`Weight` mutated hysteresis state per call on a session shared across all coins | Same trade routed differently depending on which coin was evaluated first | Fixed — precomputed per bar in time order |
| 4 | `Simulator.SharpeRatio` returned 0 below PF 1.3; not annualised | Losing books printed `0.00`; 63 call sites, none used it as a screen | Fixed — `ScreenedSharpeRatio` keeps the old floor |
| 5 | `FoldScoreHelper.PerTradeSharpe` — same cliff, inside **GA selection** | `SharpeW=0.5` is the largest *live* stat weight (`PfW`/`CalmarW` default to 0) and every strategy sits at PF 1.00–1.20, so the heaviest term was a constant across the band being ranked | Fixed — ramps over [1.0, 1.3] |
| 6 | `MarkToMarket` accrued open P&L **linearly** across a hold | Hid every intra-hold drawdown; inflated Sharpe most for the longest holds (the grid family) | Fixed — opt-in real price paths |
| 7 | GA fitness had no visibility into intra-hold path risk | A path-blind retrain pushed Grid's `MaxHoldCandles` **53 → 164**; path-aware, the same GA chose **30** | Fixed — `maePct` in `Canonical` |
| 8 | No count of candidate evaluations | DSR is decided by trial count (0.971 at T=1,000 vs 0.770 at T=100,000) and the number was a guess | Fixed — `GaTrialCounter` |
| 9 | Per-cluster GA stage: **62%** of a `train` run's trials on ATR-band variants no OOS report ever loaded; `CoinCluster` buckets on ATR%, so "Liquid" means *low volatility* | Deflated the base genotype's Sharpe to pay for unmeasured variants | Retired to `docs/legacy/genotypes/` |
| 10 | `edgetest` replayed with only the global 20-slot cap, ignoring `PortfolioReplay.DefaultCaps` | **My own bug.** FadeShort held 8,919 slots on arrival order and starved 2,331 Grid/GridShort trades — a measurement artifact that looked like a defective strategy | Fixed — per-strategy caps bind first |
| 11 | `maePct` added to `CanonicalRegime` and then **ignored** | Introduced and caught in the same session; FadeShort routes through it, so it would have looked wired while doing nothing | Fixed + regression test |
| 12 | Test suite flaky under parallel collections — `Console.Out`, `SwingLongSimulator.BtcRegimeProbe` and `GRAVITY_HMM` all mutated concurrently | ~1 run in 3 failed, on a different test each time, all passing in isolation — red results could not be trusted | Fixed — `ProcessGlobalCollection`, 8/8 clean runs |

---

## 3. What was built

**`edgetest`** — the only command whose numbers survive scrutiny. Reports RAW / static-gate /
rolling-gate / coin-screen / combined / **risk-parity** against a **random-gate null control**,
each with per-trade PF and mean, then CAGR, annualised Sharpe, maxDD, Calmar and deflated Sharpe
computed from a **daily marked-to-market equity curve**.

**`RollingStrategyGate`** — refits every 30 days on the trailing 180 days of trades that had
already **closed** (entry time picks the bucket, exit time gates availability). No decision sees
its own outcome. It is simultaneously the **decay detector**: a dead strategy routes itself off
with no retrain, and back on if the edge returns. One mechanism, not a gate plus a monitor.

**`GaTrialCounter`** — one `Record` per candidate evaluation at each of the 9 GA fitness entry
points. Trials **accumulate** and the ledger **merges** rather than replaces: a genotype surviving
five retrains was selected from all five passes, and resetting would launder away the
multiple-testing burden a retrain just added. A missing ledger falls back to a deliberately large
default, because "we did not measure the search" must deflate, never inflate.

**Acceptance gate** — leave-one-out per strategy, with slot-crowding decomposition and the absolute
book *without* each one. Verdicts are three-way: **REJECT** only when strictly dominated (worse on
both return and risk; exit code 1), **TRADE-OFF** when it helps one and costs the other, **accept**
when it helps both. It was binary at first and flagged a strategy contributing +2.0pp CAGR as a
defect — a gate that cries wolf gets ignored, which is how `VERDICT A edge=A` happened.

**`FitnessLandscapeTests`** — guards the *class* of defect behind #4, #5 and the earlier
tail-ratio step: every GA-consumed statistic must be continuous, monotone and gradient-bearing
across its live domain. Verified by reintroducing the cliff, which fails it on both counts. The old
tests pinned *values*, and a value assertion cannot notice that a function has no gradient.

---

## 4. Results

Deflated Sharpe through the session. **Steps that lowered it were corrections, not regressions** —
that is the point.

| Step | DSR | Note |
|---|---|---|
| 6 strategies, linear accrual, guessed T=50,000 | 0.003 | baseline |
| → 4 strategies (DipLong, RipShort retired) | 0.119 | |
| → 3 strategies (SwingLong retired) | 0.790 | |
| → **real price paths** in `MarkToMarket` | 0.097 | **correction**: removed a Sharpe inflation of 42% |
| → measured trial count (17,172) | 0.147 | replaced a guess |
| → more retrains recorded (57,516) | 0.125 | honest cost of searching |
| → **per-strategy caps** (match production) | 0.941 | fixed my own harness bug |
| → **risk parity** sizing | **1.000** | |

Final books:

| Book | CAGR | annSharpe | maxDD | Calmar | DSR | Grade |
|---|---|---|---|---|---|---|
| RAW (no gate) | 31.8% | 1.13 | 34.8% | 0.91 | 0.059 | — |
| STATIC gate (in-sample) | 23.1% | 1.26 | 15.9% | 1.45 | 0.102 | — |
| ROLLING gate, flat sizing | 11.4% | 2.62 | 3.9% | 2.96 | 0.941 | B |
| **ROLLING gate + risk parity** | **13.1%** | **6.00** | **1.0%** | **13.48** | **1.000** | **A** |
| RANDOM gate (null control) | 5.6% | 0.97 | 8.0% | 0.70 | 0.025 | — |

Risk-parity multipliers (inverse trailing vol, mean-normalised): FadeShort **×0.25**, Grid
**×1.34**, GridShort **×1.42**. Trade-weighted mean is **0.955** — gross exposure is 4.5% *lower*,
so the extra return comes from compounding a smoother curve, not from leverage. Per-trade PF and
mean return are identical to the flat-sized book (1.19 / 0.269%): same trades, same gate, only the
size distribution changed.

**Roster:** 3 live — FadeShort, Grid, GridShort. Retired on `edgetest` evidence: DipLong (PF 0.95
in Bull, n=4,957 — loses in its *own* home regime), RipShort (PF 0.98 in Bear, n=3,939), SwingLong
(profitable in Bull but the largest drawdown contributor). FadeLong was already disabled. The
window was 48% Bull / 43% Bear, so none of this is window bad luck.

---

## 5. Things that are true and uncomfortable

- **`DSR 1.000` is a saturation value**, not a quality score — the normal CDF has pinned. `Calmar
  13.48` divides by a 0.97% drawdown and is arithmetically fragile. Read grade A as "clears the
  bar", nothing more.
- **The roster was chosen by looking at OOS results on this sample.** That is selection on the test
  set. The sample is `Config.OosCoins` — 100 never-trained symbols, of which **63 produce trades**
  (the rest fail the 300-h1-bar / 400-m15-bar length guards) — over **one** ~2,142-day span. The
  problem is breadth, not size: 63 cross-sectionally correlated coins over a single calendar period
  is much closer to one experiment than to 63, which is exactly what the effective-sample figure
  says — 14,842 trades collapse to **294** independent observations. Re-running on a *different
  time period* or a *disjoint set of coins* is the only thing that converts this from a measurement
  into a claim.
- **The trial ledger is a LOWER BOUND.** Pre-instrumentation history is unrecoverable, so the
  deflated Sharpe it produces is optimistic.
- **Two of three strategies are the same idea.** Without FadeShort the book is two grid variants.
  Inverse-vol will always load into them; per-strategy caps are what contains that today.
- **Production still uses the in-sample gate.** `combinedbacktest`, `oosbacktest` and `fulltest`
  load `strategy_family_gate.json` via `StrategyPipeline`. Only `edgetest` uses the rolling gate.
  **Every number outside `edgetest` is still contaminated.**

---

## 6. What comes next

Ordered by what unblocks the most.

### Now

1. ~~**Fix the test-suite flakiness.**~~ **DONE.** Three process-global resources were mutated by
   tests running concurrently: `Console.Out` (the `Capture` save/set/restore helpers interleave, so
   one test clobbers another's redirect mid-run), `SwingLongSimulator.BtcRegimeProbe` (`Dispose`
   clears it at class teardown, which does nothing against a concurrent test — the victim routes
   SwingLong through a gated probe, gets zero trades, and fails far from the cause), and
   `GRAVITY_HMM` (`RegimeRouter.HmmEnabled` reads it live, so any concurrent test expecting HMM
   routing silently took the legacy path). Fixed with one `DisableParallelization` collection;
   8 consecutive clean runs against a prior ~1-in-3 failure rate. Caveat: 8 clean runs is evidence,
   not proof — the three mechanisms are understood and closed, but a rarer race could remain.
2. **Validate on a disjoint sample** — either a different calendar period, or a set of coins that
   does not overlap the current 63. This is the single largest caveat on every number above: until
   it is done, the roster is a hypothesis fitted to the one sample it was selected on.
3. **Move production onto the rolling gate.** Replace the static `strategy_family_gate.json` path
   in `StrategyPipeline` so `combinedbacktest`/`oosbacktest`/`fulltest` stop reporting in-sample
   numbers. Expect every published figure to fall; that is the correction landing, not a
   regression.

### Next

4. **Family-level equity allocation.** Today's risk parity is crude: inverse-vol per *strategy*,
   correlation ignored. The right shape is to allocate equity to **families** first — so two grid
   variants share one budget rather than each claiming a full slice — then risk-parity within a
   family. `StrategyAllocator` already has Ledoit-Wolf shrunk covariance, ERC, SIMFAM clustering
   and a family cap. With three strategies the per-strategy caps substitute adequately; past a
   handful they will not.
5. **Wire `riskScale` into production sizing**, not just `edgetest`. `CLAUDE.md` documents the
   intended composition as `finalSize = base × routerWeight × riskScale × guardMult`; `riskScale`
   is the missing term.
6. **Retrain the live genotypes from scratch under instrumentation.** Only then is the trial count
   a real number rather than a lower bound. Note the standing hazard: of three retrains this
   session, only FadeShort's beat its incumbent — Grid's two both regressed and were reverted.

### Later

7. **Turn on `PfW`/`CalmarW`.** Two of four statistical fitness terms default to `0.0`; profit
   factor reaches fitness only through `qualityMult`, drawdown only through `ddDiv`.
8. **MAE for the remaining simulators** if any retired strategy is revived — only Grid, GridShort
   and FadeShort report excursions today.
9. **Decide the live path deliberately.** `HyperliquidPaperTrade` still reads cluster genotypes
   (now absent, so it falls back), and `PositionSizePct` is still a gene the GA optimises and live
   trading ignores.
10. **Address grid concentration** — either find a genuinely uncorrelated third family, or accept
    and document that this is a grid business.

---

## 7. The recurring failure mode

Three separate cliffs shipped in this codebase (tail-ratio step at n=100,
`Simulator.SharpeRatio`'s PF floor, `PerTradeSharpe` inheriting it), all for the same reason: a
guard written for **reporting** was copied into **selection**, and the tests pinned the value it
returned rather than the shape it needed.

Two rules worth keeping:

- **Any function a GA selects on must be continuous and monotone over its live domain**, and that
  property deserves a test of its own. `FitnessLandscapeTests` enforces it; add a case there for
  every new statistic in `Canonical`.
- **Any selection rule — gate, coin screen, sizing weight — must be fitted on a trailing window of
  already-closed trades**, never on the window it filters.

And one about measurement harnesses: `edgetest` produced a confident, wrong rejection of FadeShort
because it replayed *stricter* than production. A harness that does not match the system it
measures will invent defects as readily as it hides them.
