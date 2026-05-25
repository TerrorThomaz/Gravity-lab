#!/usr/bin/env python3
"""
gather_genes.py  —  train → backtest → status loop to accumulate gene candidates.

Each trainmulti run saves up to 3 candidates in candidates/ (filename includes
holdout Sharpe), so files accumulate across cycles.  best_genotype.json is only
overwritten when the new run beats the incumbent on holdout.

After each step a brief message is posted to DISCORD_KNOWLEDGE_CHANNEL using
the DISCORD_TOKEN_KNOWLEDGE bot (same credentials as knowledge_bot.py).

Usage:
  python3 gather_genes.py          # run forever, Ctrl-C to stop
  python3 gather_genes.py 5        # stop after 5 cycles

Log: gather.log  (gitignored)
"""

import datetime
import json
import os
import re
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

try:
    from dotenv import load_dotenv
    load_dotenv(Path(__file__).parent / ".env")
except ImportError:
    pass

DIR = os.path.dirname(os.path.abspath(__file__))
DLL = os.path.join(DIR, "bin", "Release", "net10.0", "Gravity-gen2.dll")
LOG = os.path.join(DIR, "gather.log")
ENV = {**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
ANSI_RE = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")

# ── Discord notifier ──────────────────────────────────────────────────────────

_DISCORD_TOKEN = os.environ.get("DISCORD_TOKEN_KNOWLEDGE") or os.environ.get("DISCORD_TOKEN", "")
_DISCORD_CH    = os.environ.get("DISCORD_KNOWLEDGE_CHANNEL", "")

def notify(content: str) -> None:
    """POST a plain-text message to DISCORD_KNOWLEDGE_CHANNEL."""
    if not _DISCORD_TOKEN or not _DISCORD_CH:
        return
    url  = f"https://discord.com/api/v10/channels/{_DISCORD_CH}/messages"
    body = json.dumps({"content": content}).encode()
    req  = urllib.request.Request(url, data=body, headers={
        "Authorization": f"Bot {_DISCORD_TOKEN}",
        "Content-Type": "application/json",
    })
    try:
        urllib.request.urlopen(req, timeout=10)
    except Exception as exc:
        print(f"  [discord] notify failed: {exc}")

# ── Helpers ───────────────────────────────────────────────────────────────────

def dotnet_cmd(mode: str) -> list[str]:
    return (["dotnet", "exec", DLL, mode] if os.path.exists(DLL)
            else ["dotnet", "run", "--", mode])

def ts() -> str:
    return datetime.datetime.now().strftime("%H:%M")

def find(pattern: str, text: str, default: str = "—") -> str:
    m = re.search(pattern, text)
    return m.group(1).strip() if m else default

def run_mode(mode: str, timeout: int, label: str) -> str:
    print(f"  [{ts()}] {label}…", end="", flush=True)
    t0 = time.time()
    try:
        r = subprocess.run(
            dotnet_cmd(mode), cwd=DIR, env=ENV,
            capture_output=True, text=True, timeout=timeout,
        )
        out = ANSI_RE.sub("", (r.stdout + r.stderr).strip())
    except subprocess.TimeoutExpired:
        out = f"[TIMEOUT after {timeout}s — process killed]"
    elapsed = int(time.time() - t0)
    with open(LOG, "a") as f:
        stamp = datetime.datetime.now().isoformat(timespec="seconds")
        f.write(f"\n{'='*60}\n[{stamp}] {mode}\n{'='*60}\n{out}\n")
    print(f" {elapsed}s")
    return out

def count_candidates() -> int:
    cdir = os.path.join(DIR, "candidates")
    if not os.path.isdir(cdir):
        return 0
    return sum(1 for f in os.listdir(cdir) if f.endswith(".json"))

# ── Main loop ─────────────────────────────────────────────────────────────────

max_cycles   = int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 0
best_holdout = 0.0
best_bt_sh   = 0.0
cycle        = 0

print(f"gather_genes.py  —  log → {LOG}  (Ctrl-C to stop)\n")
if _DISCORD_CH:
    print(f"Discord notifications → channel {_DISCORD_CH}\n")

try:
    while max_cycles == 0 or cycle < max_cycles:
        cycle += 1
        print(f"── Cycle {cycle} ──────────────────────────────────────── [{ts()}]")

        # ── Train ─────────────────────────────────────────────────────────────
        out        = run_mode("trainmulti", 1800, "trainmulti")
        holdout_sh = float(find(r"Holdout Sharpe:\s+([\d.]+)", out, "0"))
        retries    = len(re.findall(r"re-training with diversification", out))
        saved      = "✓ Saved" in out
        best_holdout = max(best_holdout, holdout_sh)
        n_cands    = count_candidates()
        print(f"     holdout={holdout_sh:.3f}  retries={retries}"
              f"  saved={'YES' if saved else 'no'}"
              f"  session_best={best_holdout:.3f}  candidates={n_cands}")

        notify(
            f"🧬 **Gravity cycle {cycle} — train**\n"
            f"holdout Sharpe: `{holdout_sh:.3f}` {'✅' if holdout_sh >= 0.30 else '⚠️'}  "
            f"retries: {retries}  saved: {'YES' if saved else 'no'}\n"
            f"session best: `{best_holdout:.3f}`  candidates on disk: {n_cands}"
        )

        # ── Backtest ──────────────────────────────────────────────────────────
        out    = run_mode("backtest", 600, "backtest")
        bt_sh  = find(r"Sharpe:\s+([\d.]+)", out)
        bt_tot = find(r"Total:\s+[\d.,]+€\s+\(([+\-][\d.]+%)\)", out)
        try:
            best_bt_sh = max(best_bt_sh, float(bt_sh))
        except ValueError:
            pass
        print(f"     portfolio Sharpe={bt_sh}  total={bt_tot}"
              f"  session_best={best_bt_sh:.2f}")

        notify(
            f"📊 **Gravity cycle {cycle} — backtest**\n"
            f"portfolio Sharpe: `{bt_sh}`  total: `{bt_tot}`\n"
            f"session best Sharpe: `{best_bt_sh:.2f}`"
        )

        # ── Status ────────────────────────────────────────────────────────────
        out    = run_mode("status", 120, "status")
        st_ret = find(r"Total:.*?\(([+\-][\d.]+%)\)", out)
        st_sh  = find(r"Sharpe:\s+([\d.]+)", out)
        print(f"     return={st_ret}  Sharpe={st_sh}\n")

        notify(
            f"💹 **Gravity cycle {cycle} — status**\n"
            f"return: `{st_ret}`  Sharpe: `{st_sh}`"
        )

except KeyboardInterrupt:
    pass

print(f"\nStopped after {cycle} cycle(s).")
print(f"  Best holdout Sharpe : {best_holdout:.3f}")
print(f"  Best backtest Sharpe: {best_bt_sh:.2f}")
print(f"  Candidates in dir   : {count_candidates()}")
print(f"\nTo promote a candidate: cp candidates/candidate_N_shX.XX.json best_genotype.json")
