# Handoff: bounce long + DCA, measured on real candles

**From:** cloud session 2026-10-01 (no exchange access, no .NET SDK). It produced only a synthetic study.
**To:** a local session with `dotnet` and Bybit data (`candle_cache/` populated or `api.bybit.com` reachable).
**Read first:** `docs/BOUNCE_DCA_STUDY_2026-10.md` (results) and `research/bounce_dca_study.py` (mechanics).

## The idea (the user's)

The idea is to go long on an oversold bounce. Bounces happen statistically, but two kinds of trade lose:

- the ones that keep falling;
- the ones whose bounce is too small to cover fees.

The proposed fix:

- **3 DCA buys** as a sidecar, which lower the average entry;
- **no stop loss**, so a losing position is never closed at a loss;
- a **rescue grid** under the last DCA, whose profits work the position back to break-even.

## What is already established (synthetic data, known answers)

- **DCA without a stop does not create edge; it reshapes the payoff.** The win rate goes to 97–99% in every world. The mean stays wherever the entry signal puts it, minus costs.
- **Driftless random walk** (pure zero-mean noise): the design lost −0.42% per trade (t = −2.2). 895 of 922 DCA'd positions recovered, with a median of 2 days. The **27 that never recovered lost −171 base units, against +89 for all the winners combined.**
- **Why zero-mean noise does not average out:** the payoff is asymmetric. Wins are capped at the +2% target. A loser keeps getting added to, up to 9.5× the base order, with no cap. This is a short-volatility payoff. A single stuck position is one bet, not many, so the noise never gets the chance to average out.
- **With an injected real bounce effect,** the design was profitable (t = +38). It still earned less per unit of reserved capital than a plain 8% stop (+4.3%/yr vs +8.8%/yr), because 9.5× the base order is reserved and sits mostly idle.
- **The rescue grid made things worse.** Variant E (3× DCA, no grid) did as well as or better than C everywhere.
- **A 30-day max hold** cut the worst bag from −102% to −59% and kept a 97% win rate.

**Agreed framing with the user:** the whole question is the rate and size of the "never recovers" positions on *real* coins. A model (logistic regression, or Bayesian logistic for shrinkage) should predict **"this one never comes back"**, not "the bounce comes".

**Break-even bar from the random-walk run:** a filter that wrongly rejects 20% of good entries must catch about **60%** of eventual dead bags. This needs re-deriving on real data.

## Tasks, in order. Stop at any gate that fails, and report.

### 1. Label every bounce entry on real data (no strategy yet)

- Universe: `Config.BacktestCoins` for fitting, `Config.OosCoins` held out untouched.
- Use the h1 candles the existing commands load (`CandleFetcher`, Bybit).
- Entry rule, as in the prototype: a drop of at least 4×ATR(14) over 24 bars, RSI(14) < 30, and close > the prior bar's high. Fill at the **next bar's open**.
- For each entry, simulate the C mechanics:
  - DCA rungs at 2, 4 and 6 ×ATR, sizes 1, 1.5 and 2;
  - TP at +2% net on the average cost;
  - pessimistic intrabar order: no sell on a bar where a buy filled;
  - 0.21% round-trip cost per unit plus funding via `FundingRateSession.PnlPct`.
- Record:
  - DCAs filled;
  - days to recovery, or **"never"**: still underwater at end of data or after N = 30/90/180 days (report all three);
  - MAE;
  - the final mark. **Do not drop positions that are still open at the end of the data.**
- Report per coin:
  - entries;
  - % needing a DCA;
  - % never recovering;
  - P&L split into winners vs dead bags, in base-order units.

**Gate 1:** compare the real-data split with the synthetic one.

- If dead bags lose less than the winners make **without any model**, the design has edge as-is. Go to task 3.
- Otherwise go to task 2.

### 2. Can the dead bags be predicted at entry?

Features, all computed from data available at the signal bar's close:

- BTC regime and its confidence (`RegimeClassifier.ClassifySeries`, or the HMM probabilities);
- funding rate at entry;
- the drop's depth (in ATR) and speed;
- distance below EMA200 and the EMA50 slope (the long-downtrend proxy);
- volume z-score on the drop;
- the coin's listing age and median notional (liquidity);
- BTC's own drawdown over the same 24h (idiosyncratic vs market-wide).

Model and validation:

- Fit logistic regression, plus a Bayesian logistic with a weakly informative prior if it is cheap to add. Target: "never recovers within N days".
- **Validation:** time-ordered walk-forward with a purge/embargo (`PurgedKFold` exists), **then** the untouched `OosCoins`. In-sample AUC means nothing here: there are only tens of dead bags, so overfitting is the default outcome.
- Record every feature set and threshold tried in `GaTrialCounter`. The deflated Sharpe depends on the number of trials.

**Gate 2:** on OOS coins, at the chosen threshold, the caught fraction of dead bags must clear the break-even bar for that threshold's false-rejection rate. Derive the bar from task 1's real numbers. Report a confusion matrix and the P&L with the filter applied, not just AUC.

If it fails, stop. Report that the design is not viable on these coins, and why.

### 3. Only if a gate passed: the C# strategy

- Write a new simulator under `src/strategies/bounce_dca/`. Mirror the conventions of `FadeLongSimulator`: decision on closed bar `ih1 - 1`, fill at the next open, `TradeCosts`, `FundingRateSession`.
- **Add it to `Gravity-gen2.Tests/core/RandomWalkNullTests.cs`.** It must not profit on the driftless walk (t < 2). This is non-negotiable per `CLAUDE.md`.
- Variants to carry: E (3× DCA, no grid, no stop) and D (E + max hold). Keep C (with grid) only if it beats E on real data.
- Add a label to `PortfolioReplay.DefaultCaps` and the directional map.
- Measure it in `edgetest`:
  - with `MarkToMarket` real price paths (linear accrual completely hides a months-long −70% bag);
  - per-strategy leave-one-out against the live book;
  - **per unit of reserved capital**, not per filled notional.
- Compare against plain variant A (same entry, 8% stop, no DCA). DCA has to beat A per unit of reserved capital, or it is just a costlier way to hold the same entry.

## Traps

- **Survivorship:** an open bag at the end of the data is a loss. Mark it.
- **Same-bar fills:** rungs placed after a fill go live on the next bar. On a bar with a buy fill, no sell fills.
- **Correlated DCA fills:** alts dump together, so all ladders fill in the same week. Any shared-reserve sizing must be replayed through the directional cap and `SymbolCrowdingCap`, not assumed independent.
- **Funding:** a long held for months in a bull market pays well above the 0.01%/8h floor. Use real per-symbol funding.
- **Precedent:** DipLong and FadeLong, both regime-filtered long entries, lost money in their own regime on about 4–5k trades and were disabled. A new long-entry filter has to beat that history out of sample.

## Deliverable back to the user

A short report covering:

- the task 1 table;
- the gate verdicts, with numbers;
- if task 3 ran, the `edgetest` rows for A, D and E.

Plain language. The user wants to know whether it makes money and why, not the methodology.
