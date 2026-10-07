"""What the strategies did inside the recorder's window, checked against what the recorder saw.

The strategies trade on candles; the recorder (bot/market_recorder.py) has what candles lack: the real
bid/ask at each minute and liquidations. For every simulated trade that ENTERED while the recorder was
running this reports:
  - the recorded half-spread at the entry and exit minutes (the backtest charges 5 bp per taker side);
  - the candle entry price vs the recorded mid at that minute (FadeShort only: its entry is the 15m open,
    a real timestamp; a grid's entry_time is its ARMING time and its price a mean of later rung fills);
  - liquidations in the coin and market-wide in the 60 min before entry;
  - whether the recorder was up at entry/exit (laptop sleep leaves holes), and trades still open at the
    end of the candles (the simulator closes them at the last bar: not a real exit).
Four days is anecdote, not evidence: no edge claim is possible from this window.

Run:  python3 research/recorder_replay.py TRADES.csv [TRADES2.csv ...]
"""
from __future__ import annotations

import glob
import os
import sys

import numpy as np
import pandas as pd

REC = os.path.expanduser("~/Gravity-lab/data/recorder")
MODEL_HALF_SPREAD_BP = 5.0          # TradeCosts: Config.SlippageBps = 10 round trip, 5 per taker side


def load(name: str, cols: list[str]) -> pd.DataFrame:
    fs = sorted(glob.glob(os.path.join(REC, "*", f"{name}.csv")))
    d = pd.concat([pd.read_csv(f, usecols=cols) for f in fs], ignore_index=True)
    d["minute"] = d.recv_ms // 60000
    return d


def main() -> int:
    tr = pd.concat([pd.read_csv(f, parse_dates=["entry_time", "exit_time"]).assign(universe=os.path.basename(f))
                    for f in sys.argv[1:]], ignore_index=True)
    book = load("book", ["recv_ms", "symbol", "mid", "spread_bps"]).drop_duplicates(["symbol", "minute"])
    tick = load("tickers", ["recv_ms", "symbol"])
    liq = load("liquidations", ["recv_ms", "symbol", "size", "price"])
    liq["usd"] = liq["size"] * liq.price
    up = np.unique(tick.minute.values)
    t0, t1 = pd.to_datetime(up[0] * 60000, unit="ms"), pd.to_datetime(up[-1] * 60000, unit="ms")
    candle_end = tr.exit_time.max()
    w = tr[tr.entry_time >= t0].drop_duplicates(["strategy", "symbol", "entry_time"]).copy()
    print(f"recorder {t0:%Y-%m-%d %H:%M} → {t1:%Y-%m-%d %H:%M} UTC, {len(up)} minutes up "
          f"({len(up) / ((t1 - t0).total_seconds() / 60 + 1):.0%}); candles end {candle_end:%Y-%m-%d %H:%M}")

    bk = book.set_index(["symbol", "minute"])
    def at(sym, ts):
        m = int(ts.value // 60_000_000_000)
        for k in (m, m + 1, m - 1):                      # recv lands within a minute of the bar time
            if (sym, k) in bk.index:
                return bk.loc[(sym, k)]
        return None

    rows = []
    for t in w.itertuples():
        e, x = at(t.symbol, t.entry_time), at(t.symbol, t.exit_time)
        em = int(t.entry_time.value // 60_000_000_000)
        lq = liq[(liq.minute >= em - 60) & (liq.minute < em)]
        rows.append(dict(
            strategy=t.strategy, symbol=t.symbol, entry=t.entry_time, exit=t.exit_time, ret=t.return_pct,
            still_open=t.exit_time >= candle_end - pd.Timedelta(hours=1),
            rec_entry=e is not None, rec_exit=x is not None,
            half_spread_entry_bp=e.spread_bps / 2 if e is not None else np.nan,
            half_spread_exit_bp=x.spread_bps / 2 if x is not None else np.nan,
            px_vs_mid_bp=(t.entry_price / e.mid - 1) * 1e4 if (e is not None and t.strategy == "FadeShort") else np.nan,
            liq_coin_1h_usd=lq[lq.symbol == t.symbol].usd.sum(), liq_mkt_1h_usd=lq.usd.sum(),
            stop=getattr(t, "stop", 0)))
    r = pd.DataFrame(rows)
    pd.set_option("display.width", 220); pd.set_option("display.max_rows", 300)
    closed = r[~r.still_open]
    print("\n── per strategy (closed trades; 'open' = still open at the last candle, P&L not real) ──")
    s = r.groupby("strategy").agg(trades=("ret", "size"), open=("still_open", "sum"),
                                  rec_up_entry=("rec_entry", "mean"), rec_up_exit=("rec_exit", "mean"),
                                  half_spread_med_bp=("half_spread_entry_bp", "median"),
                                  half_spread_p90_bp=("half_spread_entry_bp", lambda v: v.quantile(.9)),
                                  liq_coin_1h_med=("liq_coin_1h_usd", "median"))
    c = closed.groupby("strategy").agg(closed=("ret", "size"), mean_ret=("ret", "mean"), sum_ret=("ret", "sum"),
                                       win=("ret", lambda v: (v > 0).mean()), stops=("stop", "sum"))
    print(s.join(c).round(3).to_string())
    print(f"\nmodelled half-spread {MODEL_HALF_SPREAD_BP} bp per taker side; recorded median over all entries "
          f"{r.half_spread_entry_bp.median():.2f} bp, p90 {r.half_spread_entry_bp.quantile(.9):.2f} bp")
    fs = r.px_vs_mid_bp.dropna()
    if len(fs):
        print(f"FadeShort candle entry vs recorded mid: median {fs.median():+.1f} bp, |p90| {fs.abs().quantile(.9):.1f} bp (n {len(fs)})")
    print("\n── trades ──")
    print(r.assign(entry=r.entry.dt.strftime("%m-%d %H:%M"), exit=r.exit.dt.strftime("%m-%d %H:%M"))
           .drop(columns=["rec_entry", "rec_exit"]).round(2).sort_values("entry").to_string(index=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
