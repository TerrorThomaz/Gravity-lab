"""Python mirror of the C# cost authority: TradeCosts.RoundTripPct + the FundingRateSession floor.

Why: research labels used flat costs and real funding, while the authority (edgetest) charges
ATR-scaled slippage, a stop-gap premium on intrabar stops, and the funding FLOOR for the grid family.
Deep rungs fill when ATR is highest, so the research EV was systematically optimistic exactly there
(overlay post-mortem, 2026-10-04). Any research label that a C# simulator will be judged on must use
this module. Gravity-gen2.Tests/core/TradeCostsParityTests.cs pins the same numbers on the C# side.

Mirrors src/core/Simulator.cs (TradeCosts) and src/core/FundingRateSession.cs (PnlPct, null branch).
Participation impact is off (no bar notional is passed by the grid simulators).
"""

from __future__ import annotations

import numpy as np

FEE_RT, MAKER_FEE, SLIPPAGE_BPS, REF_ATR_PCT = 0.11, 0.02, 10.0, 3.0   # TradeCosts / Config
FLOOR_PCT, INTERVAL_H = 0.01, 8.0                                    # FundingRateSession


def slippage_per_side(atr_pct):
    return SLIPPAGE_BPS / 100.0 / 2.0 * (np.asarray(atr_pct, float) / REF_ATR_PCT)


def round_trip_pct(atr_pct, is_stop, stop_gap_k=0.18, entry_maker=False, exit_maker=False):
    """TradeCosts.RoundTripPct (vectorised). is_stop / exit_maker may be arrays."""
    atr_pct = np.asarray(atr_pct, float)
    is_stop = np.asarray(is_stop, bool)
    exit_maker = np.asarray(exit_maker, bool)
    saving = FEE_RT / 2.0 - MAKER_FEE + slippage_per_side(atr_pct)
    cost = FEE_RT + 2.0 * slippage_per_side(atr_pct) + np.where(is_stop, stop_gap_k * atr_pct, 0.0)
    if entry_maker:
        cost = cost - saving
    return cost - np.where(exit_maker & ~is_stop, saving, 0.0)


def atr_pct(atr, px):
    """TradeCosts.AtrPct."""
    return np.where(np.asarray(px) > 1e-12, np.asarray(atr) / np.asarray(px) * 100.0, 0.0)


def settlements(entry_ns, exit_ns):
    """FundingRateSession.CountSettlements on the 8h UTC grid: ticks strictly after entry and ≤ exit."""
    step = int(INTERVAL_H * 3600 * 1e9)
    e, x = np.asarray(entry_ns, np.int64), np.asarray(exit_ns, np.int64)
    return np.where(x > e, x // step - e // step, 0)


def funding_floor_pct(entry_ns, exit_ns):
    """PnlPct with funding == null: −floor per settlement crossed, either direction."""
    return -FLOOR_PCT * settlements(entry_ns, exit_ns)


def selftest() -> int:
    # Values pinned identically in TradeCostsParityTests.cs.
    assert abs(round_trip_pct(3.0, False) - 0.21) < 1e-12                                  # all taker, at reference ATR
    assert abs(round_trip_pct(6.0, False, entry_maker=True) - (0.11 + 0.2 - (0.035 + 0.1))) < 1e-12
    assert abs(round_trip_pct(6.0, True, entry_maker=True, exit_maker=True) - (0.31 + 0.18 * 6.0 - 0.135)) < 1e-12  # stop: exit maker ignored
    assert abs(round_trip_pct(1.5, False, entry_maker=True, exit_maker=True) - (0.11 + 0.05 - 2 * (0.035 + 0.025))) < 1e-12
    h = int(3600 * 1e9)
    assert settlements(0, 8 * h) == 1 and settlements(1 * h, 7 * h) == 0 and settlements(8 * h, 16 * h) == 1
    assert settlements(7 * h, 25 * h) == 3 and settlements(5 * h, 5 * h) == 0
    print("tradecosts selftest: OK (RoundTripPct and settlement counting pinned; C# parity in TradeCostsParityTests)")
    return 0


if __name__ == "__main__":
    raise SystemExit(selftest())
