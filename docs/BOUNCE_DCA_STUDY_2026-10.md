# Bounce long + 3× DCA + rescue grid, with no stop loss: synthetic study (2026-10-01)

**Question.** Does "enter on an oversold bounce, average down 3 times, grid the bag back to
break-even, never close at a loss" have an edge of its own? Or does it only reshape the
payoff of whatever edge the entry already has?

**Answer: it reshapes the payoff. It creates no edge.** It turns many small losses into very
few total ones. The win rate goes to 97–99%, and the mean stays where the entry signal puts it,
minus extra costs. When the entry has no edge, the plan loses money while winning 98% of its
trades. When the entry has a real edge, the plan keeps it, but earns less per unit of capital
than a plain stop, because it must reserve 9.5× the base order to make the same +2%.

Script: `research/bounce_dca_study.py` (pure Python + numpy). Raw output: `research/bounce_dca_study.out.txt`.

## Why synthetic data

The container this was built in cannot reach any exchange API, so no real candles were
available. Synthetic paths have a property real data lacks: the right answer is known in advance.
On a driftless walk, the correct expected value of any strategy is −costs. Anything else is a bug
or an illusion. This is the same reasoning as `RandomWalkNullTests`.

Worlds, each 60 paths × 3 years of h1, Student-t(4) steps at ~1%/h (alt-like, ~94% annualised):

- `random_walk`: no drift and no edge. The null.
- `bleed`: −60%/yr drift, the typical alt in a bear market.
- `crash`: driftless, plus a −65% jump with no recovery about every 1.5 years (delist/exploit).
- `bounce_edge`: a real bounce is injected. After a >8% 24h drop, the next 24h recover 30% of it.

## Mechanics (the honest-execution rules matter)

- Entry: a drop ≥4×ATR over 24h, RSI(14) < 30, and the close above the prior bar's high. Fill
  at the **next** bar's open.
- 3 DCA rungs at 2, 4 and 6 ×ATR below the entry, sized 1, 1.5 and 2 (the base is 1).
- Rescue grid: after all 3 DCAs fill, 4 rungs of size 1, spaced 1.5×ATR apart, go live from the
  following bar. Each rung buys low, sells one step higher and re-arms. Its profit lowers the
  break-even.
- Exit: everything closes at a +2% net target on the average cost. Every variant uses the same
  target.
- **Pessimistic intrabar order.** On any bar where a buy fills, nothing sells. OHLC cannot say
  which came first, and assuming "down then back up in the same bar" is the defect that took Grid
  from Sharpe 0.67 to 4.15.
- Costs: 0.21% per round trip (fee 0.11 + slippage 0.10), plus funding at the 0.01%/8h floor on
  open notional.
- **Positions still open when the data ends are marked to market and counted.** Dropping them
  turns "never closes at a loss" into "never loses". That is the survivorship trap this design
  invites.

## Results

`mean%` is the mean return per trade as a percentage of reserved capital. `yr%/slot` is P&L per
calendar year on the capital one slot must reserve. `open` counts bags still underwater when the
data ends.

| world | variant | win% | mean% | t | worst% | open | yr%/slot |
|---|---|---|---|---|---|---|---|
| random_walk | A stop 8%, no DCA | 77.8 | −0.43 | −5.3 | −15 | 4 | −7.6 |
| random_walk | **C 3×DCA+grid, no stop** | **98.7** | **−0.42** | −2.2 | **−102** | 28 | −4.8 |
| random_walk | D = C + 30d max hold | 97.3 | −0.20 | −2.4 | −59 | 6 | −3.1 |
| bleed | A | 75.0 | −0.72 | −9.0 | −15 | 5 | −14.2 |
| bleed | **C** | **97.5** | **−1.75** | −4.9 | **−105** | 42 | −15.6 |
| bleed | D | 96.1 | −0.60 | −5.6 | −61 | 7 | −10.1 |
| crash | A | 77.5 | −0.58 | −6.1 | −68 | 1 | −10.8 |
| crash | **C** | **98.3** | **−1.01** | −3.9 | **−104** | 34 | −11.0 |
| bounce_edge | A | 87.8 | +0.67 | +9.0 | −22 | 4 | **+8.8** |
| bounce_edge | B no stop, no DCA | 99.3 | +1.86 | +36 | −58 | 13 | **+20.7** |
| bounce_edge | **C** | 99.8 | +0.35 | +38 | −15 | 6 | +4.3 |
| bounce_edge | E = C without the grid | 99.8 | +0.61 | +43 | −19 | 6 | +7.3 |

## What it means

1. **A 98% win rate is not evidence of anything.** In all three no-edge worlds, C wins 97–99%
   of trades and loses money. The losses sit in a handful of bags that never come back. 28–42
   of them were still open at the end, in 60 paths. Roughly every second coin-path ends holding a
   dead bag worth −100% of everything reserved for it.
2. **Not closing does not avoid the loss; it postpones the booking.** The −100% rows are
   positions that "never closed at a loss". The mark at the end is the loss.
3. **DCA amplifies the entry's edge in both directions.** With a real bounce edge, C is
   profitable (t = +38). Without one, C is unprofitable (t = −2 to −5). Whether this works is
   decided entirely by the entry signal. Measure that first.
4. **Capital efficiency is the hidden cost.** Even when the edge is real, C earns +4.3%/yr per
   slot against +8.8% for a plain 8% stop. The DCA and rescue capital must sit idle, reserved,
   for the rare trade that uses it. Pooling that reserve across coins does not escape this:
   alts dump together, so every coin's DCA ladder fills in the same week, exactly when the pool is
   needed.
5. **The rescue grid costs more than it saves.** E (no grid) beats C in `bounce_edge` and
   matches it elsewhere. Grid buys add exposure to a bag that is already underwater.
6. **A time cap (D) is the cheapest fix.** It cuts the worst outcome from −102% to −59% and
   leaves 6 bags open instead of 28 in the null world, and it keeps the 97% win rate. The cost
   is that it sometimes books a loss, which is the thing the proposal tried to avoid.

## Before this goes near C#

- Run variant **A** (entry + stop, no DCA) on real `BacktestCoins` candles. If the entry has no
  edge under the walk-forward gate, no DCA structure will rescue it.
- If it does, the sizing question becomes "C vs A per unit of reserved capital", measured in
  `edgetest` with `MarkToMarket` real paths. Linear accrual hides a 6-month −70% bag completely.
- Any C# simulator must join `RandomWalkNullTests`. The `random_walk` row above is the
  expected-to-pass reference: negative mean, t below +2.

## Real candles (2026-10-01): handoff tasks 1 and 2. Verdict: not viable, stopped at gate 2

Variant E (3x DCA at 2/4/6 ATR, sizes 1/1.5/2, TP +2% net, no stop) on real h1 candles with real
per-symbol funding. `research/bounce_dca_label.py` (task 1) reproduces this study's random-walk E row
trade for trade before touching real data. `research/bounce_dca_filter.py` (task 2) is the filter.

**Task 1, BacktestCoins (89 coins, 2,774-3,253 entries).** P&L is in base-order units, 5.5 reserved
per position.

| bag closed after | dead% | winners | dead bags | net | t_day | %/yr on reserve | random entries, net p50 |
|---|---|---|---|---|---|---|---|
| 30d | 3.0 | +128.9 | -122.1 | +6.8 | | | |
| 90d | 2.1 | +129.4 | -123.2 | +6.2 | | | |
| 180d | 1.5 | +125.6 | -106.4 | +19.2 | 0.57 | 0.9 | -28.4 |
| never (marked at the last close) | 0.7 | +118.5 | -55.2 | +63.3 | 3.02 | 3.0 | +3.4 |

- **The entry is real:** it beats random entries under the same mechanics in 20/20 seeds at both
  horizons.
- **The design is not:** any cap from 30 to 180 days leaves the book near zero. The +63 only appears
  when 19 bags are held up to 4.7 years on coins known to have survived. Four of them (FIL, MANA,
  LTC, STRK) are still at about -100% of their reserve. Delisted coins are absent from the cache, so
  even the 3%/yr is flattered.

**Task 2, predicting the dead bag at entry (180d target).** The plan was fixed before the first run
(see the script's docstring) and cost 3 trials (`ga_trials.json: bounce_dca_filter`).
- Model: L2 logistic (= Bayesian MAP), 13 features, monthly walk-forward, trained only on outcomes
  resolved before each refit. Thresholds reject the top 5/10/20% of risk.
- Controls: re-simulated with rejected signals masked, against random rejection of the same fraction.

| | AUC | reject 5% Δnet | 10% | 20% | dead bags caught at 10% |
|---|---|---|---|---|---|
| BacktestCoins walk-forward (dead 33/2,436) | 0.63 | -0.1 (beats 50% of random) | -1.7 (40%) | -5.8 (60%) | 3/33 |
| OosCoins, never fitted (dead 16/1,803) | 0.64 | +1.9 (80%) | +5.2 (100%) | +2.0 (100%) | 3/16 |

- **Gate 2 fails.** No threshold helps in the walk-forward. The OOS gains rest on 1-3 caught bags out
  of 16 and are worth about 0.4%/yr on reserve. Picking the 10% row because it won on OOS would be
  selecting on the test set.
- **What the model does see:** the largest coefficient is a *rising* EMA50 (+1.99 standardized). The
  bags that never return are first big dips after a run-up, i.e. tops, not capitulations. Bear regime
  is second (+0.55).
- **The task 1 break-even bar (catch > 1.18 x FRR) was not enough.** At 20% rejection, BacktestCoins
  catch 27% against a bar of 18% and the book still loses 5.8. A rejected good entry frees a slot for
  the next signal, so only a re-simulated book is a valid test.

Task 3 (C#) was not started, per the handoff's stop rule.
