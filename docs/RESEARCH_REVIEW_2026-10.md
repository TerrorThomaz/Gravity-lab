# Research review and revised spec (2026-10-04)

A reevaluation of the edge-discovery programme (`docs/EDGE_DISCOVERY_SCOPE_2026-10.md`, 26 commits,
14 recorded failures, 4 formal pre-registrations) and the spec to run the next phase under.

## 1. Scorecard

| question | answer | confidence |
|---|---|---|
| Is there a single-coin entry edge in candles (any gate, any of ~40 indicators, 15m / 1h, 1h–7d holds)? | No, not one that beats costs | High: many searches, nulls, positive controls |
| Can the machinery find rules? | Yes: it rediscovers "buy dips in strength, sell rips in weakness" consistently | High |
| Do those rules add value on top of the live GA grid? | No: overlay v1 and v2 both rejected by `edgetest` | High |
| Does trend-following exist here? | Weakly: t ≈ 2 at 3–7d in block A; failed block B | Medium |
| Is the live GA grid itself proven? | **No.** Raw DSR 0.838, gated 0.223, selected on the same window | Medium |

## 2. What worked: keep it

- **Null controls before statistics.** Random-walk gates (now covering StructGrid and the overlay),
  shuffled bars, permuted labels, time shifts.
- **Positive controls.** The trend control exposed three blind spots that every null had passed.
- **Pre-registration with a commit hash before the run,** and results committed whatever their
  sign.
- **`edgetest` as the authority:** mark-to-market daily equity, caps, ATR-scaled costs, and the
  acceptance gate. It caught what realised-P&L books hid (the −205% tail, the 24% maxDD).
- **Parity tests between languages** (`TradeCostsParityTests`, `StructGridTests` ↔ the `rulepath2`
  selftest).

**Traps caught this programme.** Each one passed at least one null:
- day-mean t (look-ahead in the weights);
- deepest-rung labelling;
- a hedge window starting before the fill;
- cap tie-break by exit bar;
- a stop-gap premium double-counted;
- research flat costs vs the authority;
- the per-side split hiding a regime rule;
- fixture bugs (χ², NaN).

## 3. What went wrong with the approach

1. **Two measurement engines.** Python research had its own costs, funding, fills and P&L
   accounting, and the C# authority had another. **Every Python win died in C#**: struct grid,
   overlay v1, the deep-rung "edge". Weeks of results were measured on the friendlier engine.
2. **Strawman baselines.** The rule-path work beat a static 1-ATR grid and random rungs, never the
   live GA grid. The only question that matters ("does this improve the live book?") was asked
   last.
3. **The wrong objective.** Every model optimised per-trade EV. Risk (volatility, MAE, correlated
   concurrency) entered only at the final book stage. Deep rungs are one correlated bet in a flush.
4. **The holdout budget was burned.** 2024-01 → 2025-06 was viewed by ~6 comparisons, and block B
   by 3 trials plus the overlays. **There is no clean out-of-sample window left in the past.**
5. **Reactive forking.** Each result spawned the next hypothesis (shorter exits, cluster gates, …).
   Pre-registration kept each step honest, but the *sequence* is a garden of forking paths, and the
   research trials are not in `ga_trials.json`.
6. **Improving an unproven base.** All augmentation work assumed the GA grid's edge is real. Its own
   evidence (DSR 0.838 raw, selected on this window) is "real-looking, unproven".
7. **The information ceiling.** More than ten searches on the same candles return the same answer:
   small, mostly market-level information that does not beat costs. More search on candles is
   unlikely to change that.

## 4. Revised spec

**S1 — One measurement engine.**
- A research label or book that a C# strategy will be judged on uses `research/tradecosts.py`
  (costs and funding floor), mark-to-market daily equity, and the same stops as the C# simulator.
- Anything promising reaches `edgetest` *inside the same study*, and a parity test is written when
  the C# side is.

**S2 — The baseline is the live book.**
- The primary outcome of any augmentation is the `edgetest` acceptance-gate verdict against the
  unchanged GA Grid + GridShort.
- Strawman baselines may be reported, never used as the pass criterion.

**S3 — Book-level objective from the start.**
- Models are scored on book Sharpe, maxDD and CVaR, not per-trade EV.
- Correlated-exposure features and exposure budgets are part of every policy.

**S4 — Evidence tiers** (codifying what was applied ad hoc):

| tier | what | bar |
|---|---|---|
| T1 | theory-backed, ONE trial (a published factor, a textbook rule) | t ≥ 2 in both universes, both halves, beats placebo |
| T2 | searched or learned | t ≥ 3, DSR ≥ 0.95 with research trials counted, beats permuted nulls in both universes |
| T3 | adoption into the live book | T1 or T2, plus ≥ 6 months of forward shadow data passing a rule frozen before it starts |

**S5 — A data ledger.**
- `docs/DATA_LEDGER.md` records which windows have been used for what.
- **2020-01 → 2026-10-04 is now "seen".** The only clean evidence comes from data after the freeze
  date: forward candles and recorder data.

**S6 — A research trial ledger.** Every pre-registered research trial is appended to a ledger file,
and the DSR for any T2 claim counts them, alongside `ga_trials.json`.

**S7 — No new candle-only searches** unless they bring a new information source, a new universe or
a new horizon. Re-running the same features through a different model is out.

**S8 — Acceptance is two-way, and power is always reported** (added 2026-10-04, after
`research/power_check.py`).
- A strategy passes if it adds to the live book OR stands on its own:
  - **Book addition:** alpha t ≥ 2 against the live book's daily P&L.
  - **Standalone:** t ≥ 2 and beats its placebo.
- **Both routes require "no bleed":** net Sharpe > 0 over the full window and in both halves. A
  hedge that loses money in general is insurance we would pay for through every adverse stretch,
  and is rejected however well it correlates.
- **Market neutralisation:** every sleeve reports its beta and alpha against buy-and-hold of its own
  universe.
  - A return explained by beta (alpha ≤ 0) fails both routes.
  - The standalone route also needs Sharpe ≥ buy-and-hold's: about B&H's return at less risk passes;
    less return at the same risk does not. (User, 2026-10-04.)
- New sleeves enter at a fixed risk budget (default 20%), not equal risk.
- Every verdict states the test's power at Sharpe 0.5 and 1.0. "Not significant" alone is no
  longer a finding.

## 5. Recommended priorities

1. **Prove or disprove the base.** Forward shadow evidence for the live GA grid, against a frozen
   rule written now (e.g. "forward Sharpe > 1 and day-clustered t > 2 after 9 months, else
   retire"). Cheapest, and everything else depends on it.
   - This is logging, not live trading, but it needs the user's go-ahead (live/forward work is
     deferred).
2. **Recorder data quality, then a first look** after ~3 months: book imbalance, liquidations, OI
   at 1-minute resolution. Check coverage, gaps and clock skew first (receive-time stamps exist for
   this reason).
3. **Option 2 kill-test before any C#.** Dump the GA grid's own sessions with the arming-time market
   state. Under authority costs, test whether ANY feature (including the risk ones) separates
   winning sessions from losing ones beyond permuted nulls. If nothing does, option 2 waits for the
   recorder data.
4. **Stop:** further candle-only edge searches, more deep-rung variants, and any new use of the
   2024+ window as evidence.
