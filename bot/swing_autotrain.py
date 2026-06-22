#!/usr/bin/env python3
"""
swing_autotrain.py  —  continuous swingtrain loop.

Runs swingtrain repeatedly, saves each result to swing_candidates/,
promotes to swing_best_genotype.json when fitness improves, and
notifies via Discord each round.

N_PARALLEL=1 because swingtrain already parallelises internally
across 12-19 coins — running two in parallel would starve both.

Usage:
  python3 swing_autotrain.py            # run forever
  python3 swing_autotrain.py 5          # stop after 5 rounds

Log: logs/swing_autotrain.log
"""

import asyncio
import datetime
import json
import os
import re
import shutil
import sys
import urllib.request
from pathlib import Path

try:
    from dotenv import load_dotenv
    load_dotenv(Path(__file__).resolve().parent.parent / ".env")
except ImportError:
    pass

DIR      = Path(__file__).resolve().parent.parent
DLL      = DIR / "bin" / "Release" / "net10.0" / "Gravity-gen2.dll"
BEST     = DIR / "swing_best_genotype.json"
CANDS    = DIR / "swing_candidates"
LOG      = DIR / "logs" / "swing_autotrain.log"
ENV      = {**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"}
ANSI_RE  = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")

N_PARALLEL = 1      # see module docstring
TIMEOUT    = 1200   # 20 min hard ceiling per run

_TOKEN = os.environ.get("DISCORD_TOKEN", "")
_CH    = os.environ.get("DISCORD_OUTPUT_CHANNEL", "")


def notify(content: str) -> None:
    if not _TOKEN or not _CH:
        return
    url  = f"https://discord.com/api/v10/channels/{_CH}/messages"
    body = json.dumps({"content": content}).encode()
    req  = urllib.request.Request(url, data=body, headers={
        "Authorization": f"Bot {_TOKEN}",
        "Content-Type":  "application/json",
    })
    try:
        urllib.request.urlopen(req, timeout=10)
    except Exception as exc:
        print(f"  [discord] {exc}")


def ts() -> str:
    return datetime.datetime.now().strftime("%H:%M")


def saved_fitness() -> float:
    """Fitness stored in swing_best_genotype.json, 0.0 if absent."""
    try:
        return float(json.loads(BEST.read_text()).get("Fitness", 0.0))
    except Exception:
        return 0.0


def parse_output(out: str) -> dict:
    r = {}

    # Genotype + val fitness from the "Best:" line (re-scored on held-out val)
    m = re.search(r"Best:\s+(.+?)\s+F=([\d.]+)", out)
    if m:
        r["geno"]    = m.group(1).strip()
        r["fitness"] = float(m.group(2))

    # Overfit check — val row
    m = re.search(
        r"Val\s+20%\s+Sh=([\d.]+)\s+Sort=([\d.]+)\s+PF=([\d.]+)"
        r"\s+WR=(\d+)%\s+Tr=(\d+)\s+Avg=([+-]?[\d.]+)%",
        out,
    )
    if m:
        r["val_sh"]   = float(m.group(1))
        r["val_sort"] = float(m.group(2))
        r["val_pf"]   = float(m.group(3))
        r["val_wr"]   = int(m.group(4))
        r["val_tr"]   = int(m.group(5))
        r["val_avg"]  = float(m.group(6))

    # How many coins passed the seed screen
    m = re.search(r"→ (\d+)/\d+ coins pass", out)
    if m:
        r["coins"] = int(m.group(1))

    return r


async def run_worker(worker_id: int) -> dict:
    cmd = (
        ["dotnet", "exec", str(DLL), "swingtrain"] if DLL.exists()
        else ["dotnet", "run", "--", "swingtrain"]
    )

    print(f"  [W{worker_id}] started {ts()}", flush=True)

    proc = await asyncio.create_subprocess_exec(
        *cmd, cwd=str(DIR), env=ENV,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.STDOUT,
    )

    try:
        raw, _ = await asyncio.wait_for(proc.communicate(), timeout=TIMEOUT)
    except asyncio.TimeoutError:
        proc.kill()
        print(f"  [W{worker_id}] TIMEOUT after {TIMEOUT}s")
        return {"worker": worker_id, "timed_out": True, "fitness": 0.0}

    out    = ANSI_RE.sub("", raw.decode("utf-8", errors="replace"))
    result = {"worker": worker_id, "timed_out": False, **parse_output(out)}

    fitness = result.get("fitness", 0.0)
    wr      = result.get("val_wr", 0)
    sort    = result.get("val_sort", 0.0)
    print(f"  [W{worker_id}] done {ts()}  F={fitness:.4f}  WR={wr}%  Sort={sort:.2f}")

    return result


def save_candidate(round_num: int, worker_id: int, fitness: float) -> str | None:
    """Copy swing_best_genotype.json → swing_candidates/ if fitness matches."""
    if not BEST.exists() or fitness <= 0:
        return None
    try:
        saved = float(json.loads(BEST.read_text()).get("Fitness", 0.0))
    except Exception:
        return None
    if abs(saved - fitness) > 0.1:
        return None   # file holds a different run; don't mislabel
    CANDS.mkdir(exist_ok=True)
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
    dst   = CANDS / f"round{round_num:03d}_w{worker_id}_f{fitness:.4f}_{stamp}.json"
    shutil.copy(BEST, dst)
    return dst.name


def log_round(round_num: int, results: list[dict], promoted: bool) -> None:
    LOG.parent.mkdir(exist_ok=True)
    with open(LOG, "a") as f:
        stamp = datetime.datetime.now().isoformat(timespec="seconds")
        f.write(f"\n── Round {round_num} [{stamp}] ──\n")
        for r in results:
            if r.get("timed_out"):
                f.write(f"  W{r['worker']}  TIMEOUT\n")
                continue
            f.write(
                f"  W{r['worker']}  F={r.get('fitness',0):.4f}  "
                f"WR={r.get('val_wr',0)}%  Sort={r.get('val_sort',0):.2f}  "
                f"PF={r.get('val_pf',0):.2f}  Avg={r.get('val_avg',0):+.2f}%  "
                f"Tr={r.get('val_tr',0)}  coins={r.get('coins','?')}\n"
            )
            f.write(f"         {r.get('geno','?')}\n")
        if promoted:
            f.write("  ★ NEW BEST promoted\n")


def candidates_snapshot() -> dict[str, float]:
    """Return {filename: fitness} for everything saved so far."""
    result = {}
    if not CANDS.exists():
        return result
    for f in CANDS.glob("*.json"):
        m = re.search(r"_f([\d.]+)_", f.name)
        result[f.name] = float(m.group(1)) if m else 0.0
    return result


async def main() -> None:
    max_rounds = int(sys.argv[1]) if len(sys.argv) > 1 and sys.argv[1].isdigit() else 0
    round_num  = 0

    print(f"swing_autotrain  —  {N_PARALLEL} worker(s) per round  log→{LOG}")
    print(f"Discord → channel {_CH or '(not configured)'}")
    print(f"Current best F={saved_fitness():.4f}\n")

    while max_rounds == 0 or round_num < max_rounds:
        round_num   += 1
        prev_fitness = saved_fitness()
        before_cands = candidates_snapshot()

        print(f"── Round {round_num} {'─'*40} [{ts()}]  (best F={prev_fitness:.4f})")

        t0      = datetime.datetime.now()
        results = await asyncio.gather(
            *[run_worker(i + 1) for i in range(N_PARALLEL)]
        )
        elapsed = int((datetime.datetime.now() - t0).total_seconds())

        fitnesses = [r.get("fitness", 0.0) for r in results if not r.get("timed_out")]
        new_fitness = saved_fitness()
        promoted    = new_fitness > prev_fitness + 1e-6

        # Save candidate for any run that improved the stored best
        for r in results:
            if r.get("timed_out"):
                continue
            fname = save_candidate(round_num, r["worker"], r.get("fitness", 0.0))
            if fname:
                r["candidate_file"] = fname

        after_cands = candidates_snapshot()
        new_files   = sorted(set(after_cands) - set(before_cands))

        print(f"\n  Round {round_num} done in {elapsed//60}m{elapsed%60}s")
        if fitnesses:
            print(f"  Fitness this round: {max(fitnesses):.4f}  {'★ NEW BEST' if promoted else ''}")
        print(f"  Candidates on disk: {len(after_cands)}  (+{len(new_files)} new)")

        # Convergence across recent candidates
        all_fits = sorted(after_cands.values(), reverse=True)
        if len(all_fits) >= 4:
            top4 = all_fits[:4]
            spread = top4[0] - top4[-1]
            verdict = ("converging ✓" if spread < 5
                       else "moderate spread" if spread < 15
                       else "high variance ⚠")
            print(f"  Top-4 spread: {top4[-1]:.2f}–{top4[0]:.2f}  ({verdict})")

        log_round(round_num, list(results), promoted)

        # Discord notification
        lines = [f"{'★ ' if promoted else ''}**Swing autotrain round {round_num}** — {elapsed//60}m{elapsed%60}s"]
        for r in sorted(results, key=lambda x: x.get("fitness", 0), reverse=True):
            if r.get("timed_out"):
                lines.append(f"  W{r['worker']}  TIMEOUT")
                continue
            star = "★" if promoted and r.get("candidate_file") else " "
            lines.append(
                f"  `{star} W{r['worker']}` F=`{r.get('fitness',0):.4f}`  "
                f"WR={r.get('val_wr',0)}%  Sort={r.get('val_sort',0):.1f}  "
                f"PF={r.get('val_pf',0):.2f}  Avg={r.get('val_avg',0):+.2f}%  "
                f"Tr={r.get('val_tr',0)}"
            )
        if promoted:
            best_r = max(results, key=lambda x: x.get("fitness", 0.0))
            lines.append(f"🧬 `{best_r.get('geno', '?')}`")
            lines.append(f"📈 F: `{prev_fitness:.4f}` → `{new_fitness:.4f}`")
        lines.append(f"📁 candidates: {len(after_cands)}")
        notify("\n".join(lines))

        print()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nStopped.")
