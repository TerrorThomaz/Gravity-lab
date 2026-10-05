# PRE-REGISTRATION: structure as the entry trigger (2026-10-06)

Written before any outcome is read. Frozen at the commit that adds this file together with
`research/struct_events.py`. Event counts were read first, to set the subsample. Counts carry no
outcomes.

## Why

`structure15` (pre-registration 4e35a6a, FAIL) let structure features rank grid rungs and random
market entries. A break of structure is traded **at the break**, and almost none of those moments
were candidates. This trial makes the structure event the candidate. A learner then searches all
features for the context in which events pay. This is the direct test of "does BoS work, and when".

## Candidates (block A only, 15m bars, fills before 2025-07-01; the script asserts it)

| event | definition (all known at the close of bar t) | count BT / OOS | kept |
|---|---|---|---|
| `bos4` | first close beyond the standing W=4 swing high/low (`structure15.swings`) | 529,558 / 347,860 | 30% |
| `bos16` | the same, W=16 | 164,141 / 107,462 | 100% |
| `sweep4` | wick beyond an unbroken W=4 level, close back inside | 435,552 / 260,510 | 30% |
| `don96` | first close beyond the prior 96-bar (24h) high/low | 237,316 / 159,191 | 100% |

The subsample is for memory only (7 GB box) and uses a fixed seed (20261006).

- **Entry:** taker at the open of bar t+1, on **both** sides, so continuation ("with") and reversal
  ("against") are both candidates.
- **Label:** side · ln(C[t+H] / O[t+1]) − 0.21% (taker both ways), the same as discover15's k=0
  entries.
- **Horizons:** H = 4 / 16 / 48 bars. **H = 16 (4h) is primary.**
- **Market-hedged labels:** as in discover15.

## Model (discover15's, unchanged)

- 61 features: discover15's 29, structure15's 28, plus `ev` (event type) and `evdir` (event direction).
- Numpy GBM, one per side, depth 3, 150 rounds.
- Quarterly walk-forward trained on BacktestCoins only, purged by label end. OosCoins are scored,
  never trained on.
- Rule: trade if predicted net > 0.
- Runs on `wf_parallel` (deterministic for any worker count).

## Nulls and baselines

- 10 refits per quarter with the target permuted (H = 16), as in discover15.
- **Random entries:** the same count per coin, at random bars, both sides
  (`grid_paths.random_entries`). They give the raw baseline in the descriptive map.
- The all-candidate mean is the random-selection baseline for the model.

## PASS (H = 16, written before the run; discover15's bar)

Model-selected net > 0 with week-clustered **t ≥ 3 in BOTH universes**, for at least one side,
**beating all 10 label nulls** and **positive in both halves**. The market-hedged result is reported.
A selection whose hedged result is ≤ 0 is a market-timing call, not an event edge, and that is
reported as such.

The raw map (event × direction × side against random entries, H = 4 / 16 / 48) is **descriptive** and
decides nothing.

**If PASS:** one pre-registered trading trial on block B, then forward. **If FAIL:** structure events
carry no edge this learner can find at 1–12h, after structure15. Market-level structure remains open
only as a single hand-written rule.

## Trials

1 primary (H = 16) + 2 secondary horizons (H = 4, 48) = **3**, added to the project ledger.
