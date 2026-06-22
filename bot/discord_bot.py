#!/usr/bin/env python3
"""
Discord bridge for Gravity-gen2.

Slash commands:
  /status         Portfolio P&L
  /backtest       1yr backtest on 14 coins
  /train          trainmulti on 5 coins
  /permtest       Permutation test (entry timing vs random)
  /stresstest     Per-period Sharpe across 5yr regimes
  /trainbayes     CMA-ES optimizer (runs in background, posts to output channel)
  /trainregimes   High-vol + low-vol regime pair training (background)
  /papertrade     Switch to papertrade mode
  /livetrain      Switch to livetrain mode
  /compare        Side-by-side candidate comparison
  /autoevolve     Overnight optimizer: fitness-based GA, profit-based candidate selection
  /stopevolve     Stop the background optimizer
  /journal        Live paper trade stats vs backtest baseline
  /info           Dump full output buffer

Text in command channel: forwarded to Claude Code for live code edits.

Env vars:
  DISCORD_TOKEN           – bot token
  DISCORD_OUTPUT_CHANNEL  – channel ID for trade summaries
  DISCORD_COMMAND_CHANNEL – channel ID for Claude + slash commands
  DISCORD_GUILD_ID        – (optional) guild ID for instant slash command sync
  GRAVITY_MODE            – starting mode: papertrade (default) or livetrain
"""

import asyncio
import json
import os
import re
import sys
from collections import deque
from datetime import datetime, timezone
from pathlib import Path

try:
    from dotenv import load_dotenv
    load_dotenv(Path(__file__).parent / ".env")
except ImportError:
    pass

import discord
from discord import app_commands

# ── Config ────────────────────────────────────────────────────────────────────
DISCORD_TOKEN          = os.environ["DISCORD_TOKEN"]
OUTPUT_CHANNEL_ID      = int(os.environ["DISCORD_OUTPUT_CHANNEL"])
COMMAND_CHANNEL_ID     = int(os.environ["DISCORD_COMMAND_CHANNEL"])
GUILD_ID               = int(os.environ["DISCORD_GUILD_ID"]) if os.environ.get("DISCORD_GUILD_ID") else None
PROJECT_DIR            = os.path.dirname(os.path.abspath(__file__))
DOTNET_DLL             = os.path.join(PROJECT_DIR, "bin", "Release", "net10.0", "Gravity-gen2.dll")
INFO_TRIGGERS          = {"info", "more info", "full", "dump", "show"}
LIVE_JOURNAL_FILE      = os.path.join(PROJECT_DIR, "live_journal.json")
BACKTEST_BASELINE_FILE = os.path.join(PROJECT_DIR, "backtest_baseline.json")

# ── State ─────────────────────────────────────────────────────────────────────
trade_proc:       asyncio.subprocess.Process | None = None
full_buffer:      deque[str]                        = deque(maxlen=600)
current_cycle:    list[str]                         = []
output_channel:   discord.TextChannel | None        = None
command_channel:  discord.TextChannel | None        = None
current_mode:     str                               = os.environ.get("GRAVITY_MODE", "papertrade")
claude_busy                                         = False
status_message:   discord.Message | None            = None
score_history:    deque[float]                      = deque(maxlen=6)
autoevolve_task:  asyncio.Task | None               = None
live_journal:     list[dict]                        = []
backtest_baseline: dict                             = {}

# ── ANSI stripper ─────────────────────────────────────────────────────────────
ANSI_RE = re.compile(r"\x1b\[[0-9;]*[A-Za-z]|\x1b\[2J|\x1b\[H|\r")
def strip_ansi(s: str) -> str:
    return ANSI_RE.sub("", s)

def chunk_text(text: str, limit: int = 1900) -> list[str]:
    return [text[i:i+limit] or "(empty)" for i in range(0, max(len(text), 1), limit)]

# ── Persistence helpers ───────────────────────────────────────────────────────
def load_journal() -> None:
    global live_journal
    try:
        if os.path.exists(LIVE_JOURNAL_FILE):
            with open(LIVE_JOURNAL_FILE) as f:
                live_journal = json.load(f)
    except Exception as exc:
        print(f"[bot] load_journal: {exc}")
        live_journal = []

def save_journal() -> None:
    try:
        with open(LIVE_JOURNAL_FILE, "w") as f:
            json.dump(live_journal, f, indent=2)
    except Exception as exc:
        print(f"[bot] save_journal: {exc}")

def load_baseline() -> None:
    global backtest_baseline
    try:
        if os.path.exists(BACKTEST_BASELINE_FILE):
            with open(BACKTEST_BASELINE_FILE) as f:
                backtest_baseline = json.load(f)
    except Exception as exc:
        print(f"[bot] load_baseline: {exc}")
        backtest_baseline = {}

def save_baseline(data: dict) -> None:
    global backtest_baseline
    backtest_baseline = data
    try:
        with open(BACKTEST_BASELINE_FILE, "w") as f:
            json.dump(data, f, indent=2)
    except Exception as exc:
        print(f"[bot] save_baseline: {exc}")

# ── Journal helpers ───────────────────────────────────────────────────────────
# Parses lines like:
#   ✓1   05-30 12:34→12:39  ADAUSDT          0.350000    0.355000    +1.43%     +2.15€
JOURNAL_RE = re.compile(
    r"^\s{2}([✓✗])(\d+)\s+"
    r"(\d{2}-\d{2} \d{2}:\d{2})→(\d{2}:\d{2})\s+"
    r"(\w+USDT)\s+"
    r"([\d.]+)\s+([\d.]+)\s+"
    r"([+-][\d.]+)%\s+"
    r"([+-][\d.]+)€"
)

def add_journal_entry(line: str) -> None:
    m = JOURNAL_RE.match(line)
    if not m:
        return
    win_sym, _num, open_t, close_t, coin, entry, exit_, ret_pct, pnl_eur = m.groups()
    key = f"{coin}|{open_t}"
    if any(e.get("key") == key for e in live_journal):
        return  # journal is reprinted every cycle — deduplicate
    live_journal.append({
        "key":        key,
        "win":        win_sym == "✓",
        "open_time":  open_t,
        "close_time": close_t,
        "coin":       coin,
        "entry":      float(entry),
        "exit":       float(exit_),
        "ret_pct":    float(ret_pct),
        "pnl_eur":    float(pnl_eur),
    })
    save_journal()

def compute_live_stats() -> dict:
    if not live_journal:
        return {}
    rets  = [e["ret_pct"] for e in live_journal]
    pnls  = [e["pnl_eur"] for e in live_journal]
    wins  = sum(1 for e in live_journal if e["win"])
    n     = len(live_journal)
    avg   = sum(rets) / n
    total = sum(pnls)
    wr    = wins / n * 100
    std   = (sum((r - avg) ** 2 for r in rets) / n) ** 0.5 if n >= 2 else 0.0
    sharpe = (avg / std) if std > 0 else 0.0  # per-trade ratio (not annualised)
    return {"n": n, "wins": wins, "wr": wr, "avg_ret": avg, "total_pnl": total, "sharpe": sharpe}

# ── Backtest baseline parsing ─────────────────────────────────────────────────
def parse_and_save_baseline(raw: str) -> None:
    sharpe_m = re.search(r"Sharpe:\s+([-\d.]+)", raw)
    trades_m = re.search(r"Trades:\s+(\d+)", raw)
    wr_m     = re.search(r"Win rate:\s+(\S+)", raw)
    avg_m    = re.search(r"Avg return:\s+(\S+)", raw)
    data = {
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "sharpe":    float(sharpe_m.group(1)) if sharpe_m else 0.0,
        "trades":    int(trades_m.group(1))   if trades_m else 0,
        "win_rate":  wr_m.group(1)            if wr_m     else "—",
        "avg_ret":   avg_m.group(1)           if avg_m    else "—",
    }
    save_baseline(data)

# ── Autoevolve ────────────────────────────────────────────────────────────────
# Matches output from dotnet autoevolve mode (RunAutoEvolve in Program.cs)
AUTOEVOLVE_ITER_RE   = re.compile(r"Iteration\s+(\d+).+Best profit so far:\s+([+\-\d.]+)%")
AUTOEVOLVE_PROFIT_RE = re.compile(r"Profit eval:\s+([+\-\d.]+)%\s+\(record:\s+([+\-\d.]+)%\)")
AUTOEVOLVE_DETAIL_RE = re.compile(r"Trades=(\d+)\s+H1=([+\-\d.]+)%\s+H2=([+\-\d.]+)%\s+Sharpe=([\d.]+)")
AUTOEVOLVE_SAVED_RE  = re.compile(r"★ New profit record — saved → (\S+)")
AUTOEVOLVE_GENO_RE   = re.compile(r"Genotype:\s+(.+)")
AUTOEVOLVE_WARN_RE   = re.compile(r"⚠\s+(.+)")

async def autoevolve_loop() -> None:
    """Stream dotnet autoevolve subprocess; post a Discord embed after each iteration.

    Evolution is fitness-based (trainblocks GA + CMA polish).
    Candidate selection is profit-based: only saved when 2yr portfolio profit improves.
    Never touches best_genotype.json — candidates go to candidates/ only.
    """
    cmd = (["dotnet", "exec", DOTNET_DLL, "autoevolve"]
           if os.path.exists(DOTNET_DLL) else
           ["dotnet", "run", "--", "autoevolve"])

    proc = await asyncio.create_subprocess_exec(
        *cmd,
        cwd=PROJECT_DIR,
        stdin=asyncio.subprocess.DEVNULL,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.STDOUT,
        env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
    )
    print(f"[autoevolve] subprocess PID={proc.pid}")

    iter_num     = 0
    idata: dict  = {}     # accumulator for the current iteration
    capture_geno = False  # next non-empty line after a save contains the genotype

    async def post_embed(n: int, d: dict) -> None:
        if output_channel is None or not d:
            return
        saved          = d.get("saved")
        overfit_reject = d.get("overfit_reject", False)
        profit_pct     = d.get("profit_pct")
        record_pct     = d.get("record_pct", d.get("best_so_far", 0.0))
        warnings       = d.get("warnings", [])

        color = (discord.Color.green()  if saved else
                 discord.Color.orange() if overfit_reject else
                 discord.Color.red())
        embed = discord.Embed(
            title=f"Gravity-gen2 — Autoevolve #{n}",
            color=color,
            timestamp=datetime.now(timezone.utc),
        )
        if profit_pct is not None:
            embed.add_field(name="Profit eval", value=f"`{profit_pct:+.2f}%`", inline=True)
            embed.add_field(name="Record",      value=f"`{record_pct:+.2f}%`", inline=True)
        if "trades" in d:
            embed.add_field(
                name="Eval stats",
                value=(f"Trades `{d['trades']}` • "
                       f"H1 `{d.get('h1', 0):+.2f}%` • "
                       f"H2 `{d.get('h2', 0):+.2f}%` • "
                       f"Sharpe `{d.get('sharpe', 0):.2f}`"),
                inline=False,
            )
        result = ("✓ New profit record — saved!"         if saved else
                  "⚠ Profit up but overfit — not saved" if overfit_reject else
                  "✗ No improvement")
        embed.add_field(name="Result", value=result, inline=False)
        if saved:
            embed.add_field(name="Candidate", value=f"`{Path(saved).name}`", inline=False)
        if "genotype" in d:
            embed.add_field(name="Genotype", value=f"`{d['genotype'][:200]}`", inline=False)
        if warnings:
            embed.add_field(
                name="⚠ Overfit flags",
                value="\n".join(f"• {w}" for w in warnings[:4]),
                inline=False,
            )
        await output_channel.send(embed=embed)

    try:
        async for raw in proc.stdout:
            line     = strip_ansi(raw.decode("utf-8", errors="replace").rstrip())
            stripped = line.strip()
            if not stripped:
                continue

            # Capture genotype line that immediately follows a save line
            if capture_geno:
                m = AUTOEVOLVE_GENO_RE.search(stripped)
                if m:
                    idata["genotype"] = m.group(1)
                capture_geno = False
                await post_embed(iter_num, idata)
                idata["posted"] = True
                continue

            # New iteration starting → post previous (fallback if not already posted)
            m = AUTOEVOLVE_ITER_RE.search(stripped)
            if m:
                if iter_num > 0 and idata and not idata.get("posted"):
                    await post_embed(iter_num, idata)
                iter_num = int(m.group(1))
                idata    = {"best_so_far": float(m.group(2))}
                print(f"[autoevolve] iteration {iter_num} — record {m.group(2)}%")
                continue

            m = AUTOEVOLVE_PROFIT_RE.search(stripped)
            if m:
                idata["profit_pct"] = float(m.group(1))
                idata["record_pct"] = float(m.group(2))
                continue

            m = AUTOEVOLVE_DETAIL_RE.search(stripped)
            if m:
                idata.update(trades=int(m.group(1)), h1=float(m.group(2)),
                             h2=float(m.group(3)), sharpe=float(m.group(4)))
                continue

            m = AUTOEVOLVE_SAVED_RE.search(stripped)
            if m:
                idata["saved"] = m.group(1)
                capture_geno = True   # next non-empty line is the genotype
                continue

            if "Profit improved but overfit" in stripped:
                idata["overfit_reject"] = True
                await post_embed(iter_num, idata)
                idata["posted"] = True
                continue

            if "No improvement" in stripped:
                await post_embed(iter_num, idata)
                idata["posted"] = True
                continue

            m = AUTOEVOLVE_WARN_RE.search(stripped)
            if m:
                idata.setdefault("warnings", []).append(m.group(1))

    except asyncio.CancelledError:
        print("[autoevolve] cancelled — terminating subprocess")
        proc.terminate()
        try:
            await asyncio.wait_for(proc.wait(), timeout=5.0)
        except asyncio.TimeoutError:
            proc.kill()
        raise
    except Exception as exc:
        print(f"[autoevolve] error: {exc}")
    finally:
        if proc.returncode is None:
            proc.terminate()
            await proc.wait()
        if iter_num > 0 and idata and not idata.get("posted") and output_channel:
            await post_embed(iter_num, idata)
    print("[autoevolve] subprocess ended")

# ── Papertrade summary ────────────────────────────────────────────────────────
GENO_LINE_RE = re.compile(r"^Genotype:\s+(.+)$")
FITNESS_RE   = re.compile(r"F=([-\d.]+)")
ROW_RE       = re.compile(
    r"\s{2}(\w+USDT)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)"
)

def build_summary(lines: list[str]) -> discord.Embed:
    geno_str, fitness = "", ""
    active: list[tuple] = []
    watching: list[str] = []
    unrealised_vals: list[float] = []

    for line in lines:
        gm = GENO_LINE_RE.match(line)
        if gm:
            geno_str = gm.group(1)
            fm = FITNESS_RE.search(geno_str)
            if fm:
                fitness = fm.group(1)
            continue
        rm = ROW_RE.match(line)
        if not rm:
            continue
        coin, regime, state, entry, current, unrealised, mode = rm.groups()
        if state.lower() == "watching":
            watching.append(coin)
        else:
            active.append((coin, regime, state, entry, current, unrealised))
            try:
                unrealised_vals.append(float(unrealised.replace("%", "")))
            except (ValueError, AttributeError):
                pass

    total_unreal = sum(unrealised_vals)
    if active:
        color = discord.Color.green() if total_unreal >= 0 else discord.Color.red()
    else:
        color = discord.Color.blurple()

    embed = discord.Embed(color=color, timestamp=datetime.now(timezone.utc))
    embed.set_author(name="Gravity-gen2 | Papertrade")

    if geno_str:
        short = re.sub(r"F=[-\d.]+", "", geno_str).strip().rstrip(",")
        f_str = f"  •  fitness `{fitness}`" if fitness else ""
        embed.description = f"`{short}`{f_str}"

    if active:
        pos_lines = []
        for coin, regime, state, entry, current, unrealised in active[:12]:
            arrow = "▲" if not unrealised.startswith("-") else "▼"
            pos_lines.append(f"{arrow} `{coin:<16}` **{unrealised}**  {entry} → {current}")
        embed.add_field(
            name=f"Open ({len(active)})  —  {total_unreal:+.2f}% unrealised",
            value="\n".join(pos_lines),
            inline=False,
        )
    else:
        embed.add_field(name="Positions", value="*No open trades*", inline=True)

    stats = compute_live_stats()
    n = stats.get("n", 0)
    if n > 0:
        wins = stats["wins"]
        embed.add_field(
            name=f"Journal  ({n} trade{'s' if n > 1 else ''})",
            value=(
                f"WR **{stats['wr']:.0f}%** ({wins}W / {n - wins}L)\n"
                f"Avg **{stats['avg_ret']:+.2f}%**  •  Total **{stats['total_pnl']:+.2f} €**"
            ),
            inline=True,
        )

    evo = "  •  🔄 evolving" if (autoevolve_task and not autoevolve_task.done()) else ""
    embed.set_footer(text=f"Watching {len(watching)} coins{evo}  •  /info for raw output")
    return embed

# ── Live-train summary ────────────────────────────────────────────────────────
RANK_ROW_RE     = re.compile(r"^\s+[★ ]\s+(\d+)\s+([-\d.]+)\s+(.+)")
CYCLE_INFO_RE   = re.compile(r"Cycle (\d+)\s+\|.+?Next evolution in (\d+) cycle")
AUTO_PROMOTE_RE = re.compile(r"=== AUTO-PROMOTE ===")

def build_livetrain_summary(lines: list[str]) -> discord.Embed:
    cycle_num, next_evo = "", ""
    rows: list[tuple] = []

    for line in lines:
        cm = CYCLE_INFO_RE.search(line)
        if cm:
            cycle_num, next_evo = cm.group(1), cm.group(2)
            continue
        rm = RANK_ROW_RE.match(line)
        if rm:
            rows.append((int(rm.group(1)), float(rm.group(2)), rm.group(3).strip()))

    top1 = next((r for r in rows if r[0] == 1), None)
    if top1:
        score_history.append(top1[1])

    trend = ""
    if len(score_history) >= 2:
        diff = score_history[-1] - score_history[-2]
        trend = " ↑" if diff > 0.001 else (" ↓" if diff < -0.001 else " →")

    color = (discord.Color.green() if (top1 and top1[1] > 0) else discord.Color.orange())
    embed = discord.Embed(color=color, timestamp=datetime.now(timezone.utc))
    embed.set_author(name="Gravity-gen2 | Live Train")

    desc_parts = []
    if cycle_num:
        desc_parts.append(f"Cycle **{cycle_num}**  •  next evo in **{next_evo}** cycles (~{int(next_evo)*5} min)")
    if top1:
        desc_parts.append(f"Top score **{top1[1]:+.3f}**{trend}")
    if len(score_history) >= 2:
        hist = "  ".join(f"`{s:+.3f}`" for s in list(score_history)[-6:])
        desc_parts.append(f"Trend {hist}")
    embed.description = "\n".join(desc_parts)

    top5 = [r for r in rows if r[0] <= 5]
    if top5:
        rank_lines = []
        for rank, score, geno in top5:
            tag  = "★" if rank == 1 else f"#{rank}"
            short = re.sub(r"F=[-\d.]+", "", geno).strip()[:72]
            rank_lines.append(f"`{tag}` **{score:+.3f}**  {short}")
        embed.add_field(name="Top 5", value="\n".join(rank_lines), inline=False)

    embed.set_footer(text="/info for full ranking")
    return embed

# ── Compare summary ───────────────────────────────────────────────────────────
CAND_ROW_RE   = re.compile(r"^\s{2}(\S+)\s+([-+\d.]+)\s+(\d+)\s+(\d+)/(\d+)")
CAND_LABEL_RE = re.compile(r"^\s{2}\[([^\]]+)\]$")
CAND_POS_RE   = re.compile(
    r"^\s{4}(\w+USDT)\s+(\S+)\s+entry=([\d.]+)\s+now=([\d.]+)\s+([+-][\d.]+%)"
)

def build_compare_summary(lines: list[str]) -> discord.Embed:
    candidates: list[dict] = []
    for line in lines:
        m = CAND_ROW_RE.match(line)
        if m:
            candidates.append({
                "label": m.group(1), "sharpe": float(m.group(2)),
                "open":  m.group(3), "trend":  m.group(4), "total": m.group(5),
            })

    positions: dict[str, list[str]] = {}
    cur_label: str | None = None
    for line in lines:
        lm = CAND_LABEL_RE.match(line)
        if lm:
            cur_label = lm.group(1)
            positions.setdefault(cur_label, [])
            continue
        pm = CAND_POS_RE.match(line)
        if pm and cur_label:
            arrow = "▲" if not pm.group(5).startswith("-") else "▼"
            positions[cur_label].append(
                f"{arrow} `{pm.group(1):<16}` **{pm.group(5)}**  {pm.group(3)} → {pm.group(4)}  [{pm.group(2)}]"
            )

    best_sh = max((c["sharpe"] for c in candidates), default=0.0)
    color   = discord.Color.green() if best_sh > 0 else discord.Color.red()

    embed = discord.Embed(color=color, timestamp=datetime.now(timezone.utc))
    embed.set_author(name="Gravity-gen2 | Compare")

    if candidates:
        cand_lines = []
        for c in candidates[:8]:
            star  = "★" if c["sharpe"] == best_sh else "◦"
            label = c["label"][:22]
            cand_lines.append(
                f"`{star}` `{label:<22}` Sh **{c['sharpe']:+.2f}**  open `{c['open']}`  up `{c['trend']}/{c['total']}`"
            )
        embed.add_field(name="Candidates — 20d Sharpe", value="\n".join(cand_lines), inline=False)

    any_open = any(v for v in positions.values())
    if any_open:
        for c in candidates:
            trades = positions.get(c["label"], [])
            if trades:
                label = c["label"][:28]
                embed.add_field(
                    name=f"{label}  —  {len(trades)} open",
                    value="\n".join(trades[:8]),
                    inline=False,
                )
    else:
        embed.set_footer(text="No open trades across any candidate")
    return embed

# ── Process management ────────────────────────────────────────────────────────
async def start_mode(mode: str | None = None) -> None:
    global trade_proc, current_cycle, current_mode, status_message
    if mode and mode != current_mode:
        current_mode = mode
        status_message = None
        score_history.clear()
    current_cycle = []
    cmd = (["dotnet", "exec", DOTNET_DLL, current_mode]
           if os.path.exists(DOTNET_DLL)
           else ["dotnet", "run", "--", current_mode])
    trade_proc = await asyncio.create_subprocess_exec(
        *cmd,
        cwd=PROJECT_DIR,
        stdin=asyncio.subprocess.DEVNULL,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.STDOUT,
        env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
    )
    asyncio.create_task(read_stdout(), name="stdout-reader")
    print(f"[bot] started mode={current_mode}")

async def stop_mode() -> None:
    global trade_proc, status_message
    if trade_proc and trade_proc.returncode is None:
        trade_proc.terminate()
        try:
            await asyncio.wait_for(trade_proc.wait(), timeout=8)
        except asyncio.TimeoutError:
            trade_proc.kill()
    trade_proc = None
    status_message = None
    print("[bot] process stopped")

PAPERTRADE_HEADER = re.compile(r"=== Gravity-gen2 \| PAPER TRADE")
COMPARE_HEADER    = re.compile(r"=== Gravity-gen2 \| COMPARE")
CYCLE_HEADER      = re.compile(r"=== Gravity-gen2 \| (PAPER TRADE|LIVE TRAIN|COMPARE)")

async def read_stdout() -> None:
    global current_cycle, status_message
    if trade_proc is None or trade_proc.stdout is None:
        return
    try:
        async for raw in trade_proc.stdout:
            line = strip_ansi(raw.decode("utf-8", errors="replace").rstrip())
            if not line:
                continue
            full_buffer.append(line)

            # Parse closed-trade journal lines from papertrade output
            if current_mode == "papertrade":
                add_journal_entry(line)

            # Alert on auto-promote events
            if AUTO_PROMOTE_RE.search(line) and command_channel:
                await command_channel.send(f"🚀 **AUTO-PROMOTE**: `{line.strip()}`")

            if CYCLE_HEADER.search(line):
                if current_cycle:
                    first = current_cycle[0]
                    if PAPERTRADE_HEADER.search(first):
                        summary = build_summary(current_cycle)
                    elif COMPARE_HEADER.search(first):
                        summary = build_compare_summary(current_cycle)
                    else:
                        summary = build_livetrain_summary(current_cycle)
                    if output_channel:
                        sent = False
                        if status_message is not None:
                            try:
                                await status_message.edit(content=None, embed=summary)
                                sent = True
                            except discord.NotFound:
                                status_message = None
                            except Exception as exc:
                                print(f"[bot] edit failed: {exc}")
                        if not sent:
                            status_message = await output_channel.send(embed=summary)
                current_cycle = [line]
            else:
                current_cycle.append(line)
    except Exception as exc:
        print(f"[bot] stdout reader error: {exc}")

# ── One-shot dotnet commands ──────────────────────────────────────────────────
async def run_dotnet_oneshot(mode: str, timeout: int = 300) -> str:
    if os.path.exists(DOTNET_DLL):
        cmd = ["dotnet", "exec", DOTNET_DLL, mode]
    else:
        cmd = ["dotnet", "run", "--", mode]
    proc = await asyncio.create_subprocess_exec(
        *cmd,
        cwd=PROJECT_DIR,
        stdin=asyncio.subprocess.DEVNULL,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.STDOUT,
        env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
    )
    try:
        out, _ = await asyncio.wait_for(proc.communicate(), timeout=timeout)
    except asyncio.TimeoutError:
        proc.kill()
        return f"Timed out after {timeout}s."
    return strip_ansi(out.decode("utf-8", errors="replace").strip())

def format_status_embed(raw: str) -> discord.Embed:
    def find(pattern: str, default: str = "—") -> str:
        m = re.search(pattern, raw)
        return m.group(1).strip() if m else default

    period  = find(r"Period:\s+(.+?)  \(")
    days    = find(r"Period:.+?\((\d+)d\)")
    trades  = find(r"Trades:\s+(\d+)")
    wr      = find(r"Win rate:\s+(\S+)")
    wl      = find(r"Win rate:.+?\((.+?)\)")
    avg     = find(r"Avg return:\s+(\S+)")
    sharpe  = find(r"Sharpe:\s+(\S+)")
    sortino = find(r"Sortino:\s+(\S+)")
    pf      = find(r"Profit factor:\s+(\S+)")
    calmar  = find(r"Calmar:\s+(\S+)")
    maxl    = find(r"Max consec. loss:\s+(\S+)")
    balance = find(r"Balance:\s+€(\S+)")
    total_r = find(r"Total:\s+€[\d.,]+\s+\(([^)]+)\)")
    total_e = find(r"Total:\s+€([\d.,]+)")
    max_dd  = find(r"Max drawdown:\s+(\S+)")
    fitness = find(r"Fitness:\s+(\S+)")
    source  = find(r"Source:\s+(\S+)")

    is_profit = "+" in total_r
    color = discord.Color.green() if is_profit else discord.Color.red()

    embed = discord.Embed(
        color=color,
        timestamp=datetime.now(timezone.utc),
    )
    embed.set_author(name="Gravity-gen2 | Portfolio Status")
    embed.description = (
        f"Period **{period}** ({days}d)  •  fitness `{fitness}`\n"
        f"Source `{source}`"
    )
    embed.add_field(
        name="Returns",
        value=(
            f"Sharpe **{sharpe}**  •  Sortino **{sortino}**\n"
            f"Calmar **{calmar}**  •  PF **{pf}**\n"
            f"Win rate **{wr}** ({wl})\n"
            f"Trades **{trades}**  •  Avg **{avg}%**\n"
            f"Max consec. loss **{maxl}**"
        ),
        inline=True,
    )
    embed.add_field(
        name="Portfolio  (€1 000 start)",
        value=(
            f"Balance **€{balance}**\n"
            f"Total **{total_r}**  (€{total_e})\n"
            f"Max drawdown **{max_dd}**"
        ),
        inline=True,
    )
    embed.set_footer(text="Fees: 0.055% taker ×2 + 0.050% slippage ×2 = 0.21%/trade")
    return embed

# ── Claude invocation ─────────────────────────────────────────────────────────
async def run_claude(prompt: str) -> str:
    cmd = ["claude", "-p", prompt, "--dangerously-skip-permissions"]
    print(f"[claude] running: {' '.join(cmd)}")
    proc = await asyncio.create_subprocess_exec(
        *cmd,
        cwd=PROJECT_DIR,
        stdin=asyncio.subprocess.DEVNULL,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    try:
        out, err = await asyncio.wait_for(proc.communicate(), timeout=300)
    except asyncio.TimeoutError:
        proc.kill()
        return "Claude timed out after 5 minutes."
    stdout = out.decode("utf-8", errors="replace").strip()
    stderr = err.decode("utf-8", errors="replace").strip()
    print(f"[claude] exit={proc.returncode} stdout={len(stdout)}ch stderr={stderr[:200]}")
    if proc.returncode != 0 and not stdout:
        return f"Claude exited {proc.returncode}.\n{stderr[:500]}"
    return stdout or "(Claude produced no output)"

async def send_chunked(channel: discord.abc.Messageable, text: str, code: bool = False) -> None:
    wrap = "```\n{}\n```" if code else "{}"
    for chunk in chunk_text(text):
        await channel.send(wrap.format(chunk))

# ── Bot ───────────────────────────────────────────────────────────────────────
intents = discord.Intents.default()
intents.message_content = True

class GravityBot(discord.Client):
    def __init__(self) -> None:
        super().__init__(intents=intents)
        self.tree = app_commands.CommandTree(self)

    async def setup_hook(self) -> None:
        if GUILD_ID:
            guild = discord.Object(id=GUILD_ID)
            self.tree.copy_global_to(guild=guild)
            await self.tree.sync(guild=guild)
            print(f"[bot] slash commands synced to guild {GUILD_ID} (instant)")
        else:
            await self.tree.sync()
            print("[bot] slash commands synced globally (up to 1h propagation — set DISCORD_GUILD_ID for instant)")

client = GravityBot()

# ── Slash commands ────────────────────────────────────────────────────────────
@client.tree.command(name="status", description="Portfolio P&L with fees, slippage & reinvestment for current best genotype")
async def slash_status(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    raw = await run_dotnet_oneshot("status", timeout=180)
    embed = format_status_embed(raw)
    await interaction.followup.send(embed=embed)

@client.tree.command(name="train", description="Train on 5 diverse coins to reduce overfitting (takes ~5 min)")
async def slash_train(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    await interaction.followup.send("Training started on WIF / SOL / DOGE / ETH / BONK — this takes ~5 min…")
    raw = await run_dotnet_oneshot("trainmulti", timeout=600)
    for chunk in chunk_text(raw):
        await interaction.followup.send(f"```\n{chunk}\n```")

@client.tree.command(name="backtest", description="Full 1-year backtest on 14 coins (takes ~1 min)")
async def slash_backtest(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    raw = await run_dotnet_oneshot("backtest", timeout=300)
    parse_and_save_baseline(raw)
    for chunk in chunk_text(raw):
        await interaction.followup.send(f"```\n{chunk}\n```")

@client.tree.command(name="papertrade", description="Switch to live papertrade mode")
async def slash_papertrade(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    await stop_mode()
    await start_mode("papertrade")
    await interaction.followup.send("Switched to **papertrade** mode.")

@client.tree.command(name="compare", description="Side-by-side live comparison of all candidates (refreshes every 5 min)")
async def slash_compare(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    await stop_mode()
    await start_mode("compare")
    await interaction.followup.send("Switched to **compare** mode — all candidates evaluated every 5 min.")

@client.tree.command(name="livetrain", description="Switch to live training mode (20 genotypes, evolves hourly)")
async def slash_livetrain(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    await stop_mode()
    await start_mode("livetrain")
    await interaction.followup.send("Switched to **livetrain** mode.")

@client.tree.command(name="autoevolve", description="Overnight optimizer: fitness-based GA evolution, profit-based candidate selection")
async def slash_autoevolve(interaction: discord.Interaction) -> None:
    global autoevolve_task
    await interaction.response.defer(thinking=True)
    if autoevolve_task and not autoevolve_task.done():
        await interaction.followup.send("⚠ Autoevolve is already running — use `/stopevolve` to stop it.")
        return
    autoevolve_task = asyncio.create_task(autoevolve_loop(), name="autoevolve")
    await interaction.followup.send(
        "✓ **Autoevolve started** — each iteration runs `trainblocks` (fitness-based GA) + CMA-ES polish, "
        "then evaluates 2yr portfolio profit. Candidates saved to `candidates/` only when profit improves. "
        "`best_genotype.json` is never touched. An embed is posted here after each iteration. "
        "Use `/stopevolve` to stop."
    )

@client.tree.command(name="stopevolve", description="Stop the background autoevolve optimizer")
async def slash_stopevolve(interaction: discord.Interaction) -> None:
    global autoevolve_task
    await interaction.response.defer(thinking=True)
    if autoevolve_task and not autoevolve_task.done():
        autoevolve_task.cancel()
        autoevolve_task = None
        await interaction.followup.send("✓ Autoevolve stopped.")
    else:
        await interaction.followup.send("Autoevolve is not running.")

@client.tree.command(name="journal", description="Live paper trade stats vs backtest baseline")
async def slash_journal(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    stats = compute_live_stats()
    n = stats.get("n", 0)

    color = (discord.Color.green() if stats.get("total_pnl", 0) >= 0 else discord.Color.red()) if n else discord.Color.greyple()
    embed = discord.Embed(color=color, timestamp=datetime.now(timezone.utc))
    embed.set_author(name="Gravity-gen2 | Live Journal")

    if n == 0:
        embed.description = "*No trades yet — closed positions appear here automatically.*"
    else:
        wins   = stats["wins"]
        avg    = stats["avg_ret"]
        total  = stats["total_pnl"]
        sharpe = stats["sharpe"]
        embed.add_field(
            name=f"Live  ({n} trade{'s' if n > 1 else ''})",
            value=(
                f"WR **{stats['wr']:.0f}%** ({wins}W / {n - wins}L)\n"
                f"Avg **{avg:+.2f}%**  •  Total **{total:+.2f} €**\n"
                f"Sharpe **{sharpe:.2f}**"
            ),
            inline=True,
        )

        if backtest_baseline:
            bt_sh  = backtest_baseline.get("sharpe", 0)
            bt_tr  = backtest_baseline.get("trades", 0)
            bt_ts  = backtest_baseline.get("timestamp", "")[:10]
            bt_wr  = backtest_baseline.get("win_rate", "—")
            bt_avg = backtest_baseline.get("avg_ret",  "—")
            embed.add_field(
                name=f"Backtest  ({bt_ts})",
                value=(
                    f"Sharpe **{bt_sh:.2f}**  •  Trades **{bt_tr}**\n"
                    f"WR **{bt_wr}**  •  Avg **{bt_avg}**"
                ),
                inline=True,
            )
        else:
            embed.add_field(
                name="Backtest",
                value="*No baseline — run `/backtest` first.*",
                inline=True,
            )

        recent = live_journal[-10:]
        rows = []
        for e in recent:
            icon = "✓" if e["win"] else "✗"
            rows.append(
                f"`{icon}` `{e['open_time']}` `{e['coin']:<16}` **{e['ret_pct']:+.2f}%**  {e['pnl_eur']:+.2f} €"
            )
        embed.add_field(
            name=f"Last {len(recent)} trades",
            value="\n".join(rows),
            inline=False,
        )

    await interaction.followup.send(embed=embed)

@client.tree.command(name="info", description="Dump the full raw output buffer")
async def slash_info(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    blob = "\n".join(list(full_buffer))
    for chunk in chunk_text(blob or "(no output yet)"):
        await interaction.followup.send(f"```\n{chunk}\n```")

@client.tree.command(name="permtest", description="Permutation test: does entry timing beat random shorts? (~5 min)")
async def slash_permtest(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    await interaction.followup.send("Permutation test running (~5 min, 1000 permutations × 14 coins)…")
    raw = await run_dotnet_oneshot("permtest", timeout=600)

    sig_m  = re.search(r"(\d+)/(\d+) coins show statistically significant", raw)
    real_m = re.search(r"Mean real Sharpe:\s+([-\d.]+)", raw)
    null_m = re.search(r"mean null Sharpe:\s+([-\d.]+)", raw)
    lift_m = re.search(r"Entry detection lift:\s+([+-]?[\d.]+)%", raw)

    sig_str  = f"{sig_m.group(1)}/{sig_m.group(2)}" if sig_m else "—"
    real_str = real_m.group(1) if real_m else "—"
    null_str = null_m.group(1) if null_m else "—"
    lift_str = lift_m.group(1) if lift_m else "—"

    sig_count = int(sig_m.group(1)) if sig_m else 0
    total     = int(sig_m.group(2)) if sig_m else 1
    color = discord.Color.green() if sig_count >= total // 2 else discord.Color.orange()

    embed = discord.Embed(
        title="Gravity-gen2 — Permutation Test",
        description=(
            "Tests whether pump-short entry timing beats randomly-timed shorts "
            "with the same exit logic (1000 permutations per coin)."
        ),
        color=color,
        timestamp=datetime.now(timezone.utc),
    )
    embed.add_field(name="Significant coins (p < 0.05)", value=f"**{sig_str}** coins", inline=True)
    embed.add_field(name="Real Sharpe vs Null",           value=f"`{real_str}` vs `{null_str}`", inline=True)
    embed.add_field(name="Entry timing lift",              value=f"**{lift_str}%** over random", inline=True)
    await interaction.followup.send(embed=embed)
    for chunk in chunk_text(raw):
        await interaction.followup.send(f"```\n{chunk}\n```")

@client.tree.command(name="stresstest", description="Per-period Sharpe across 5yr regimes (OOD robustness check, ~10 min)")
async def slash_stresstest(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    await interaction.followup.send("Stress test running (~10 min, 5yr × 8 coins × 9 periods)…")
    raw = await run_dotnet_oneshot("stresstest", timeout=900)

    worst_m    = re.search(r"Worst period Sharpe\s*:\s*([-\d.]+)", raw)
    best_m     = re.search(r"Best\s+period Sharpe\s*:\s*([-\d.]+)", raw)
    positive_m = re.search(r"Periods positive\s*:\s*(\d+)/(\d+)", raw)
    verdict_m  = re.search(r"(✓|!|⚠)\s*(.+)", raw.split("Periods positive")[1] if "Periods positive" in raw else "")

    worst_v = float(worst_m.group(1)) if worst_m else 0.0
    best_str  = best_m.group(1)       if best_m  else "—"
    pos_str   = f"{positive_m.group(1)}/{positive_m.group(2)}" if positive_m else "—"
    verdict   = verdict_m.group(0).strip() if verdict_m else "—"

    color = discord.Color.green() if worst_v >= 0 else (
            discord.Color.orange() if worst_v >= -0.5 else discord.Color.red())

    embed = discord.Embed(
        title="Gravity-gen2 — Stress Test (5yr, 9 Periods)",
        description="Per-period Sharpe from 2021 to 2025. Fragile genotypes fail in crash/bear periods.",
        color=color,
        timestamp=datetime.now(timezone.utc),
    )
    embed.add_field(name="Worst period",     value=f"`{worst_m.group(1) if worst_m else '—'}`", inline=True)
    embed.add_field(name="Best period",      value=f"`{best_str}`",                              inline=True)
    embed.add_field(name="Positive periods", value=f"**{pos_str}**",                             inline=True)
    embed.add_field(name="Verdict",          value=verdict,                                       inline=False)
    await interaction.followup.send(embed=embed)
    for chunk in chunk_text(raw):
        await interaction.followup.send(f"```\n{chunk}\n```")

@client.tree.command(name="trainbayes", description="CMA-ES optimizer — more efficient than GA (runs in background, ~20 min)")
async def slash_trainbayes(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    await interaction.followup.send(
        "CMA-ES training started (~20 min) — results will be posted to the output channel when done."
    )
    asyncio.create_task(_bg_trainbayes())

async def _bg_trainbayes() -> None:
    raw = await run_dotnet_oneshot("trainbayes", timeout=2400)
    if output_channel is None:
        return
    saved_m   = re.search(r"✓ Saved → (\S+)", raw)
    holdout_m = re.search(r"New\s+holdout:\s+([-\d.]+)", raw)
    embed = discord.Embed(
        title="Gravity-gen2 — CMA-ES Training Complete",
        color=discord.Color.green() if saved_m else discord.Color.orange(),
        timestamp=datetime.now(timezone.utc),
    )
    embed.add_field(name="Holdout Sharpe", value=holdout_m.group(1) if holdout_m else "—",                        inline=True)
    embed.add_field(name="Saved",          value="✓ yes" if saved_m else "✗ not saved (incumbent better)", inline=True)
    await output_channel.send(embed=embed)
    for chunk in chunk_text(raw):
        await output_channel.send(f"```\n{chunk}\n```")

@client.tree.command(name="trainregimes", description="Train high-vol + low-vol genotype pair (background, ~25 min)")
async def slash_trainregimes(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    await interaction.followup.send(
        "Regime pair training started (~25 min) — results will be posted to the output channel when done."
    )
    asyncio.create_task(_bg_trainregimes())

async def _bg_trainregimes() -> None:
    raw = await run_dotnet_oneshot("trainregimes", timeout=2400)
    if output_channel is None:
        return
    high_m = re.search(r"High genotype on high-vol:\s+([-\d.]+)", raw)
    low_m  = re.search(r"Low  genotype on low-vol:\s+([-\d.]+)",  raw)
    saved  = "regime_high_genotype.json" in raw and "regime_low_genotype.json" in raw
    embed = discord.Embed(
        title="Gravity-gen2 — Regime Pair Training Complete",
        description="High-vol + low-vol genotypes saved. Paper trade will auto-select based on current ATR.",
        color=discord.Color.green() if saved else discord.Color.orange(),
        timestamp=datetime.now(timezone.utc),
    )
    embed.add_field(name="High-vol Sharpe (own regime)", value=high_m.group(1) if high_m else "—", inline=True)
    embed.add_field(name="Low-vol Sharpe  (own regime)", value=low_m.group(1)  if low_m  else "—", inline=True)
    embed.add_field(name="Files saved",                  value="✓ yes" if saved else "✗ check output", inline=True)
    await output_channel.send(embed=embed)
    for chunk in chunk_text(raw):
        await output_channel.send(f"```\n{chunk}\n```")

# ── Events ────────────────────────────────────────────────────────────────────
@client.event
async def on_ready() -> None:
    global output_channel, command_channel
    output_channel  = client.get_channel(OUTPUT_CHANNEL_ID)   # type: ignore[assignment]
    command_channel = client.get_channel(COMMAND_CHANNEL_ID)  # type: ignore[assignment]
    print(f"[bot] logged in as {client.user}")
    print(f"[bot] output  channel id={OUTPUT_CHANNEL_ID}  → {output_channel}")
    print(f"[bot] command channel id={COMMAND_CHANNEL_ID} → {command_channel}")
    load_journal()
    load_baseline()
    print(f"[bot] journal: {len(live_journal)} trades loaded")
    print(f"[bot] baseline: {'loaded (' + str(backtest_baseline.get('timestamp','')[:10]) + ')' if backtest_baseline else 'not found'}")
    await start_mode()

@client.event
async def on_message(message: discord.Message) -> None:
    global claude_busy

    print(f"[msg] author={message.author} bot={message.author.bot} "
          f"channel_id={message.channel.id} content={message.content!r}")

    if message.author.bot:
        return
    if message.channel.id != COMMAND_CHANNEL_ID:
        print(f"[msg] ignored: wrong channel (expected {COMMAND_CHANNEL_ID})")
        return

    text = message.content.strip()
    if not text:
        return

    if text.lower() in INFO_TRIGGERS:
        blob = "\n".join(list(full_buffer))
        await send_chunked(message.channel, blob or "(no output yet)", code=True)  # type: ignore[arg-type]
        return

    if claude_busy:
        await message.reply("Claude is already working — please wait.")
        return

    claude_busy = True
    try:
        await message.reply(f"Stopping {current_mode} and forwarding to Claude: *{text[:200]}*")
        await stop_mode()
        await message.channel.send("⚙️ Claude is working…")  # type: ignore[union-attr]
        result = await run_claude(text)
        await send_chunked(message.channel, result or "(no output)", code=True)  # type: ignore[arg-type]
        await message.channel.send(f"Restarting {current_mode}…")  # type: ignore[union-attr]
        await start_mode()
    finally:
        claude_busy = False

# ── Entry point ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    missing = [v for v in ("DISCORD_TOKEN", "DISCORD_OUTPUT_CHANNEL", "DISCORD_COMMAND_CHANNEL")
               if not os.environ.get(v)]
    if missing:
        print(f"[bot] missing env vars: {', '.join(missing)}", file=sys.stderr)
        sys.exit(1)
    client.run(DISCORD_TOKEN)
