# PRE-REGISTRATION: covariance residual as a free selector inside Grid (2026-10-06)

Written before any outcome is read. Frozen at the commit that adds this file together with
`research/grid_resid_select.py`.

## Why

Covariance residual reversion carries the strongest information in the repo. The 1h residual
predicts the next 4h reversal at rank IC −0.10. But as a standalone book it can't pay its costs:
- the `resid` book was retired;
- in `j4big`, the hedge legs eat the timing.

Grid trades anyway and already pays its (maker) costs. So if selecting Grid's coins by residual adds
return, the information rides for free. This is the one covariance-reversion design that does not hit
the cost wall.

## Data

**Trade logs:** Grid / GridShort trades from `edgetest` on the current genotypes, one log per
universe:
- `GRAVITY_OFFLINE=1 [GRAVITY_EDGE_UNIVERSE=backtest] dotnet run -c Release -- edgetest`;
- the output is copied to `reports/edgetest_raw_trades_{oos,backtest}.csv`.

This is seen data: the Grid genotype was selected on this window. The trial tests a selector added on
top of Grid, not Grid itself.

**Residual:**
- hourly return minus its projection on $ + top-3 PCs of the trailing 30-day covariance, refit daily
  on data before the day (`anomaly_fade_bounce.residuals`, unchanged);
- `zscore24(res, n=4)` gives the 4h cumulative residual z, scaled by the trailing 720h std taken
  strictly before.

**Timing:** read at hourly bar `floor(entry_time) − 1h`. Grid's `entry_time` is its ARMING time, so
this bar has closed under either timestamp convention, possibly one hour stale (conservative).

## Test

- **Primary: Grid, z4, prefer the LOW tercile** (coins falling more than their co-movement explains).
- **Lift:** mean return of the low-tercile trades minus the mean of all Grid trades with a signal, with
  a week-clustered t. Terciles are cut on all trades with a signal. The cut uses no outcomes.
- **Controls:**
  1. 2,000 random same-size selections;
  2. 20 time-shifts of the signal (each trade reads its coin's residual 1–30 days away, random sign).

## PASS (all of it, in BOTH universes)

1. lift > 0 with week-clustered t ≥ 3;
2. lift > 0 in both halves of the trade window;
3. beats ≥ 95% of the random selections;
4. beats ≥ 19 of the 20 time-shifts.

**If PASS:** it becomes a candidate ranking rule for which coins Grid fills first when slots bind.
That rule is one forward-only trial, because Grid is frozen in the forward test until 2027.

**If FAIL:** residual reversion is closed as a Grid selector too.

## Secondary (report only)

- Grid with z24.
- GridShort, mirrored (prefer the HIGH tercile), with z4 and z24.

## Trials

1 primary + 3 secondary = **4**.

## RESULT (run once, 2026-10-06, pre-registration d64d691): FAIL (direction holds, power does not)

Log: `reports/grid_resid_select_run.txt`. Trade logs: `reports/edgetest_raw_trades_{oos,backtest}.csv`.
The Grid genotype is byte-identical to the frozen forward one.

| primary: Grid, z4, low tercile | terciles low / mid / high (all) | lift [t] | halves | random | time-shifts |
|---|---|---|---|---|---|
| OosCoins (5,644 trades) | +0.208 / +0.147 / +0.123 (+0.160) | **+0.049 [+1.17]** | +0.037 / +0.057 | 96.8% ✓ | 19/20 ✓ |
| BacktestCoins (7,772) | +0.200 / +0.151 / +0.191 (+0.181) | **+0.019 [+0.49]** | −0.003 / +0.036 | 80.3% ✗ | 17/20 ✗ |

- **Secondary.** Grid z24: OOS +0.006, BT +0.040 [+0.9]. GridShort mirrored: −0.05 / −0.09 on OOS,
  ~0 / +0.04 on BT.
- **Power.** SE(lift) ≈ 0.042%/trade on OOS, so MDE80 ≈ 0.12%/trade (≈ 70% of Grid's mean). The
  test could only have seen a lift that large.

**Verdict: FAIL** on t ≥ 3 in both universes.
- On OosCoins it passes every check except t: both halves positive, 97% of random selections, 19/20
  time-shifts. The low tercile earns +30% more per trade than the average.
- On BacktestCoins it is weaker and fails the controls. This matches the earlier split: small coins'
  residuals revert, liquid coins' continue.
- **Unproven, not absent.** The sign is consistent, but the effect is below this sample's resolution.
- **What would decide it:** forward data. Grid is frozen, so it can only be logged as a shadow ranking,
  not used live.
