#!/usr/bin/env python3
"""
gather_genes.py  —  parallel random-seed trainblocks to explore gene space.

Runs N_PARALLEL independent 'gather' dotnet processes simultaneously.
Each starts from a random seed, finds its own solution, and saves directly
to candidates/ — never touches best_genotype.json.

Running parallel random starts answers the key question:
  If results cluster → the pattern is real and the GA reliably finds it.
  If results scatter → we've been finding noise from a single lucky draw.

Usage:
  python3 gather_genes.py            # run forever
  python3 gather_genes.py 5          # stop after 5 rounds

Log: logs/gather_genes.log
"""

import asyncio
import datetime
import json
import os
import re
import sys
import urllib.request
from pathlib import Path

try:
    from dotenv import load_dotenv
    load_dotenv(Path(__file__).parent / ".env")
except ImportError:
    pass

DIR     = os.path.dirname(os.path.abspath(__file__))
DLL     = os.path.join(DIR, "bin", "Release", "net10.0", "Gravity-gen2.dll")
LOG     = os.path.join(DIR, "logs", "gather_genes.log")
ENV     = {**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
ANSI_RE = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")

N_PARALLEL = 2      # concurrent gather workers per round
TIMEOUT    = 7200   # max seconds per worker — 2hr needed when running 2 parallel trainblocks

_TOKEN = os.environ.get("DISCORD_TOKEN", "")
_CH    = os.environ.get("DISCORD_OUTPUT_CHANNEL", "")


def notify(content: str) -> None:
    if not _TOKEN or not _CH:
        return
    url  = f"https://discord.com/api/v10/channels/{_CH}/messages"
    body = json.dumps({"content": content}).encode()
    req  = urllib.request.Request(url, data=body, headers={
        "Authorization": f"Bot {_TOKEN}",
        "Content-Type": "application/json",
    })
    try:
        urllib.request.urlopen(req, timeout=10)
    except Exception as exc:
        print(f"  [discord] {exc}")


def ts() -> str:
    return datetime.datetime.now().strftime("%H:%M")


def candidates_snapshot() -> dict[str, float]:
    """Return {filename: holdout_sharpe} for all current candidates."""
    d = Path(DIR) / "candidates"
    result = {}
    if not d.exists():
        return result
    for f in d.glob("*.json"):
        m = re.search(r"_sh([\d.]+)\.json$", f.name)
        result[f.name] = float(m.group(1)) if m else 0.0
    return result


async def run_worker(worker_id: int) -> dict:
    """Run one gather dotnet process. Returns parsed result dict."""
    cmd = (["dotnet", "exec", DLL, "gather"] if os.path.exists(DLL)
           else ["dotnet", "run", "--", "gather"])

    print(f"  [W{worker_id}] started {ts()}", flush=True)

    proc = await asyncio.create_subprocess_exec(
        *cmd, cwd=DIR, env=ENV,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.STDOUT,
    )

    try:
        raw, _ = await asyncio.wait_for(proc.communicate(), timeout=TIMEOUT)
    except asyncio.TimeoutError:
        proc.kill()
        print(f"  [W{worker_id}] TIMEOUT after {TIMEOUT}s")
        return {"worker": worker_id, "holdout": 0.0, "file": "", "geno": "", "timed_out": True}

    out = ANSI_RE.sub("", raw.decode("utf-8", errors="replace"))

    holdout = 0.0
    m = re.search(r"Holdout Sharpe:\s*([\d.]+)", out)
    if m:
        holdout = float(m.group(1))

    saved_file = ""
    m = re.search(r"✓\s+(candidates/\S+)", out)
    if m:
        saved_file = m.group(1)

    geno = ""
    m = re.search(r"Phase 3 → (.+)", out)
    if m:
        geno = m.group(1).strip()

    # Per-coin holdout breakdown
    coins = re.findall(r"(\w+USDT)\s+Sh=([\d.]+)\s+Tr=(\d+)", out)

    print(f"  [W{worker_id}] done {ts()}  holdout={holdout:.3f}  file={saved_file or '(none)'}")

    return {
        "worker":   worker_id,
        "holdout":  holdout,
        "file":     saved_file,
        "geno":     geno,
        "coins":    coins,
        "out":      out,
    }


def log_round(round_num: int, results: list[dict], new_files: list[str]) -> None:
    Path(LOG).parent.mkdir(exist_ok=True)
    with open(LOG, "a") as f:
        stamp = datetime.datetime.now().isoformat(timespec="seconds")
        f.write(f"\n── Round {round_num} [{stamp}] ──\n")
        for r in results:
            coins_str = "  ".join(
                f"{c}:{s}" for c, s, _ in r.get("coins", [])
            )
            f.write(f"  W{r['worker']}  holdout={r['holdout']:.3f}  {r['file'] or 'not saved'}\n")
            if coins_str:
                f.write(f"         {coins_str}\n")
        f.write(f"  new files: {new_files}\n")


async def main() -> None:
    max_rounds = int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 0
    round_num  = 0

    print(f"gather_genes  —  {N_PARALLEL} parallel workers  log→{LOG}")
    print(f"Discord → channel {_CH or '(not configured)'}\n")

    while max_rounds == 0 or round_num < max_rounds:
        round_num += 1
        before = candidates_snapshot()

        print(f"── Round {round_num} {'─'*40} [{ts()}]")
        print(f"  Launching {N_PARALLEL} parallel gather workers…\n")

        t0 = datetime.datetime.now()
        results = await asyncio.gather(
            *[run_worker(i + 1) for i in range(N_PARALLEL)]
        )
        elapsed = int((datetime.datetime.now() - t0).total_seconds())

        after     = candidates_snapshot()
        new_files = sorted(set(after) - set(before))

        print(f"\n  Round {round_num} done in {elapsed//60}m{elapsed%60}s")
        print(f"  New candidates: {len(new_files)}")
        for f in new_files:
            print(f"    {f}  (Sh={after[f]:.2f})")

        # Convergence check: how spread are the holdout Sharpes?
        holdouts = [r["holdout"] for r in results if not r.get("timed_out")]
        if len(holdouts) >= 2:
            spread = max(holdouts) - min(holdouts)
            verdict = ("consistent ✓" if spread < 0.5
                       else "moderate spread" if spread < 1.5
                       else "high variance ⚠")
            print(f"  Holdout spread: {min(holdouts):.2f}–{max(holdouts):.2f}  ({verdict})")

        log_round(round_num, list(results), new_files)

        # Discord notification
        lines = [f"🧬 **Gather round {round_num}** — {elapsed//60}m{elapsed%60}s"]
        for r in sorted(results, key=lambda x: x["holdout"], reverse=True):
            star = "★" if r["holdout"] == max(holdouts, default=0) else " "
            lines.append(f"  `{star} W{r['worker']}` holdout=`{r['holdout']:.3f}`  {r['file'] or 'not saved'}")
        if len(holdouts) >= 2:
            lines.append(f"Spread: `{min(holdouts):.2f}–{max(holdouts):.2f}` ({verdict})")
        lines.append(f"📁 candidates on disk: {len(after)}")
        notify("\n".join(lines))

        print()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nStopped.")
