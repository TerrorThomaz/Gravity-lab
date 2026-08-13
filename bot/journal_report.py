#!/usr/bin/env python3
"""Turn live_journal.json into the two numbers a multi-day papertrade run exists to produce:
what the router cost or saved, and what the guard would have cost or saved.

Both are gates, so their value is only visible in the counterfactual — the trades NOT taken.
Papertrade now records those (routed=false) alongside the real ones.

    python3 bot/journal_report.py [live_journal.json]
"""
import json
import sys
from collections import defaultdict


def load(path):
    with open(path) as f:
        return json.load(f)


def pf(rets):
    win = sum(r for r in rets if r > 0)
    loss = abs(sum(r for r in rets if r <= 0))
    if loss < 1e-9:
        return float("inf") if win > 0 else 0.0
    return win / loss


def summarise(label, rets):
    if not rets:
        return f"  {label:<26} no closed trades"
    n = len(rets)
    wr = sum(1 for r in rets if r > 0) / n
    return (f"  {label:<26} n={n:<5} WR={wr:>5.0%}  PF={pf(rets):>5.2f}  "
            f"sum={sum(rets):>+8.1f}%  avg={sum(rets)/n:>+6.2f}%")


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else "live_journal.json"
    events = load(path)

    exits = [e for e in events if e.get("evt") == "exit"]
    if not exits:
        print("No closed trades yet. The run needs positions to open AND close before any of "
              "this means anything — with 48-72h holds that is at least two to three days.")
        return

    taken = [e["pnl"] for e in exits if e.get("routed", True)]
    blocked = [e["pnl"] for e in exits if not e.get("routed", True)]

    span = (min(e["time"] for e in exits), max(e["time"] for e in exits))
    print(f"\n{'=' * 78}")
    print(f"  PAPERTRADE COUNTERFACTUAL   {span[0][:16]} → {span[1][:16]}")
    print(f"{'=' * 78}\n")

    print("── Router ──────────────────────────────────────────────────────────────────")
    print(summarise("taken (routed on)", taken))
    print(summarise("suppressed (routed off)", blocked))
    if blocked:
        verdict = "COST you" if sum(blocked) > 0 else "SAVED you"
        print(f"\n  The router {verdict} {abs(sum(blocked)):.1f}% of summed per-trade return.")
        print("  Per-trade sums are NOT portfolio return — positions overlap and are size-capped.")
        print("  Read the sign and the PF, not the magnitude.")
    else:
        print("\n  Nothing suppressed yet: the router allowed every strategy for this window.")

    print("\n── Guard ───────────────────────────────────────────────────────────────────")
    engaged = [e for e in exits if e.get("guardMult", 1.0) < 0.999 and e.get("routed", True)]
    idle = [e for e in exits if e.get("guardMult", 1.0) >= 0.999 and e.get("routed", True)]
    print(summarise("guard engaged", [e["pnl"] for e in engaged]))
    print(summarise("guard idle", [e["pnl"] for e in idle]))
    if engaged:
        # What the guard actually changed: it scaled these positions down, so it kept
        # (1 - mult) of each outcome off the book.
        avoided = sum(e["pnl"] * (1.0 - e.get("guardMult", 1.0)) for e in engaged)
        verdict = "COST you" if avoided > 0 else "SAVED you"
        print(f"\n  Sizing down {verdict} {abs(avoided):.1f}% of summed per-trade return "
              f"across {len(engaged)} positions.")
    else:
        print("\n  Guard never engaged in this window — it is idle, not helping or hurting.")

    print("\n── By strategy (taken only) ────────────────────────────────────────────────")
    per = defaultdict(list)
    for e in exits:
        if e.get("routed", True):
            per[e["strat"]].append(e["pnl"])
    for strat in sorted(per, key=lambda s: -sum(per[s])):
        print(summarise(strat, per[strat]))

    print("\n── By regime at entry ──────────────────────────────────────────────────────")
    reg = defaultdict(list)
    for e in exits:
        if e.get("routed", True):
            reg[e.get("regime") or "unknown"].append(e["pnl"])
    for r in sorted(reg, key=lambda k: -len(reg[k])):
        print(summarise(r, reg[r]))
    print()


if __name__ == "__main__":
    main()
