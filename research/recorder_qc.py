"""Quality check of the forward recorder's data (bot/market_recorder.py → data/recorder/<UTC day>/*.csv).

Before any study touches this data, check that it is complete and honest:
  coverage   snapshot cycles per day vs 1440, gaps > 3 minutes, symbols per cycle (tickers / book)
  book       spread and depth sanity, the share of crossed or empty books, imbalance range
  skew       recv_ms − exch_ms for book snapshots and liquidations (clock drift / latency); a
             negative skew means our clock is BEHIND the exchange's, which would make "what we
             knew when" optimistic
  liqs       events per day, per side, the largest notional
Exit code 1 if a hard check fails (coverage < 95% on a full day, or median |skew| > 5 s).

Run:  python3 research/recorder_qc.py [--root ~/Gravity-lab/data/recorder]
"""

from __future__ import annotations

import argparse
import glob
import os
import sys

import numpy as np
import pandas as pd


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", default=os.path.expanduser("~/Gravity-lab/data/recorder"))
    a = ap.parse_args()
    days = sorted(d for d in glob.glob(os.path.join(a.root, "20*")) if os.path.isdir(d))
    if not days:
        print(f"no recorder data under {a.root}"); return 1
    bad = False
    print(f"recorder QC — {a.root}, {len(days)} day(s)\n")
    print(f"  {'day':<11}{'cycles':>7}{'cover':>7}{'gaps>3m':>8}{'max gap':>9}{'tick syms':>10}{'book syms':>10}"
          f"{'book rows':>10}{'crossed':>8}{'med spr bp':>11}{'book skew med/p99 ms':>22}{'liqs':>7}{'liq skew med ms':>16}")
    for d in days:
        day = os.path.basename(d)
        tk = pd.read_csv(os.path.join(d, "tickers.csv")) if os.path.exists(os.path.join(d, "tickers.csv")) else pd.DataFrame()
        bk = pd.read_csv(os.path.join(d, "book.csv")) if os.path.exists(os.path.join(d, "book.csv")) else pd.DataFrame()
        lq = pd.read_csv(os.path.join(d, "liquidations.csv")) if os.path.exists(os.path.join(d, "liquidations.csv")) else pd.DataFrame()
        cyc = np.sort(tk.recv_ms.unique()) if len(tk) else np.array([])
        mins = pd.to_datetime(cyc, unit="ms").floor("min").unique() if len(cyc) else []
        start = pd.Timestamp(day)
        end = min(start + pd.Timedelta(days=1), pd.Timestamp.now("UTC").tz_localize(None))
        first = pd.to_datetime(cyc[0], unit="ms") if len(cyc) else end
        span_min = max(1, int((end - max(start, first.floor("min"))).total_seconds() // 60))
        cover = len(mins) / span_min
        gaps = np.diff(cyc) / 60000 if len(cyc) > 1 else np.array([0])
        n_gaps, max_gap = int((gaps > 3).sum()), float(gaps.max()) if len(gaps) else 0.0
        tsyms = tk.groupby("recv_ms").symbol.nunique().median() if len(tk) else 0
        bsyms = bk.groupby(bk.recv_ms // 60000).symbol.nunique().median() if len(bk) else 0
        crossed = float((bk.spread_bps <= 0).mean()) if len(bk) else float("nan")
        spr = float(bk.spread_bps.median()) if len(bk) else float("nan")
        bskew = (bk.recv_ms - bk.exch_ms) if len(bk) else pd.Series(dtype=float)
        lskew = (lq.recv_ms - lq.exch_ms) if len(lq) else pd.Series(dtype=float)
        full_day = first <= start + pd.Timedelta(minutes=5) and end >= start + pd.Timedelta(days=1)
        if (full_day and cover < 0.95) or (len(bskew) and abs(bskew.median()) > 5000):
            bad = True
        print(f"  {day:<11}{len(cyc):>7}{cover:>7.1%}{n_gaps:>8}{max_gap:>8.1f}m{tsyms:>10.0f}{bsyms:>10.0f}{len(bk):>10}"
              f"{crossed:>8.2%}{spr:>11.2f}{(f'{bskew.median():.0f} / {bskew.quantile(0.99):.0f}' if len(bskew) else '—'):>22}"
              f"{len(lq):>7}{(f'{lskew.median():.0f}' if len(lskew) else '—'):>16}")
    # whole-sample detail
    bk = pd.concat([pd.read_csv(f) for f in glob.glob(os.path.join(a.root, "20*", "book.csv"))], ignore_index=True)
    lq_files = glob.glob(os.path.join(a.root, "20*", "liquidations.csv"))
    lq = pd.concat([pd.read_csv(f) for f in lq_files], ignore_index=True) if lq_files else pd.DataFrame()
    print(f"\n  book: {bk.symbol.nunique()} symbols; imbalance (1%) range {bk.imb_10bp.min():+.2f} … {bk.imb_10bp.max():+.2f}, "
          f"median |imb| {bk.imb_10bp.abs().median():.2f}; median 1% depth bid ${bk.bid_usd_10bp.median():,.0f} / ask ${bk.ask_usd_10bp.median():,.0f}")
    neg = (bk.recv_ms - bk.exch_ms < 0).mean()
    print(f"  clock: {neg:.2%} of book snapshots have recv < exch (our clock behind the exchange's)")
    if len(lq):
        lq["usd"] = lq["size"] * lq["price"]
        print(f"  liquidations: {len(lq)} events, {lq.symbol.nunique()} symbols, side split {lq.side.value_counts().to_dict()}, "
              f"largest ${lq.usd.max():,.0f} ({lq.loc[lq.usd.idxmax(), 'symbol']})")
    print(f"\n  verdict: {'FAIL — fix before using this data' if bad else 'OK'}")
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
