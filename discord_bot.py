#!/usr/bin/env python3
"""
Discord bridge for Gravity-gen2.

Starts papertrade automatically, posts a compact summary every cycle,
and proxies any channel message to Claude Code (which can edit files
and rerun the project).

Env vars required:
  DISCORD_TOKEN          – bot token
  DISCORD_OUTPUT_CHANNEL – channel ID for trade summaries
  DISCORD_COMMAND_CHANNEL – channel ID for Claude commands (can equal OUTPUT)
"""

import asyncio
import os
import re
import sys
from collections import deque
from datetime import datetime, timezone
from pathlib import Path

# Load .env from the project directory if it exists
try:
    from dotenv import load_dotenv
    load_dotenv(Path(__file__).parent / ".env")
except ImportError:
    pass  # python-dotenv not installed; rely on shell env

import discord

# ── Config ────────────────────────────────────────────────────────────────────
DISCORD_TOKEN           = os.environ["DISCORD_TOKEN"]
OUTPUT_CHANNEL_ID       = int(os.environ["DISCORD_OUTPUT_CHANNEL"])
COMMAND_CHANNEL_ID      = int(os.environ["DISCORD_COMMAND_CHANNEL"])
PROJECT_DIR             = os.path.dirname(os.path.abspath(__file__))
DOTNET_MODE             = os.environ.get("GRAVITY_MODE", "papertrade")
DOTNET_CMD              = ["dotnet", "run", "--", DOTNET_MODE]
INFO_TRIGGERS           = {"info", "more info", "full", "dump", "status", "show"}

# ── State ─────────────────────────────────────────────────────────────────────
trade_proc: asyncio.subprocess.Process | None = None
full_buffer: deque[str] = deque(maxlen=600)   # rolling last ~600 lines
current_cycle: list[str] = []
output_channel: discord.TextChannel | None = None
command_channel: discord.TextChannel | None = None
claude_busy = False

# ── ANSI / VT100 stripper ─────────────────────────────────────────────────────
ANSI_RE = re.compile(r"\x1b\[[0-9;]*[A-Za-z]|\x1b\[2J|\x1b\[H|\r")

def strip_ansi(s: str) -> str:
    return ANSI_RE.sub("", s)

# ── Summary formatter ─────────────────────────────────────────────────────────
# Genotype line:  Genotype: Pump=1.0% RSI(14,OB=72.5) ... F=1.2345
GENO_LINE_RE = re.compile(r"^Genotype:\s+(.+)$")
FITNESS_RE   = re.compile(r"F=([-\d.]+)")
# Papertrade table row
ROW_RE = re.compile(
    r"\s{2}(\w+USDT)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)"
)

def build_summary(lines: list[str]) -> str:
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    geno_str = ""
    fitness  = ""
    active, watching = [], []

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
            pnl_sign = "+" if unrealised.startswith("+") else ""
            active.append(
                f"  `{coin:<18}` {state:<14} entry {entry}  now {current}  **{unrealised}**  [{regime}]"
            )

    parts = [f"**Gravity-gen2 | Papertrade — {ts}**"]

    # Genotype fitness
    if fitness:
        parts.append(f"**Fitness:** `{fitness}`")
    if geno_str:
        # Trim to key params for the summary
        short = re.sub(r"F=[-\d.]+", "", geno_str).strip().rstrip(",")
        parts.append(f"**Genotype:** `{short}`")

    parts.append("")

    # Open trades
    if active:
        parts.append(f"**Open trades ({len(active)}):**")
        parts.extend(active)
    else:
        parts.append("**No open trades.**")

    # Watching
    parts.append(f"\n*Watching {len(watching)} coins • send `info` for full output*")
    return "\n".join(parts)

# ── Live-train summary formatter ──────────────────────────────────────────────
# Matches ranking rows:  ★  #1   1.234  Pump=1.0% RSI(14,...) F=1.234
RANK_ROW_RE = re.compile(r"[★ ]\s+#(\d+)\s+([-\d.]+)\s+(.+)")
CYCLE_INFO_RE = re.compile(r"Cycle (\d+)\s+\|.+?Next evolution in (\d+) cycle")

def build_livetrain_summary(lines: list[str]) -> str:
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    cycle_num, next_evo = "", ""
    rows = []

    for line in lines:
        cm = CYCLE_INFO_RE.search(line)
        if cm:
            cycle_num, next_evo = cm.group(1), cm.group(2)
            continue
        rm = RANK_ROW_RE.match(line)
        if rm:
            rank, score, geno = rm.group(1), rm.group(2), rm.group(3).strip()
            rows.append((int(rank), float(score), geno))

    parts = [f"**Gravity-gen2 | Live Train — {ts}**"]
    if cycle_num:
        parts.append(f"Cycle **{cycle_num}** • next evolution in **{next_evo}** cycles (~{int(next_evo)*5} min)")
    parts.append("")

    top = [r for r in rows if r[0] <= 5]
    if top:
        parts.append("**Top 5 genotypes:**")
        for rank, score, geno in top:
            star = "★" if rank == 1 else " "
            # Trim genotype to key params to fit Discord
            short = re.sub(r"F=[-\d.]+", "", geno).strip()[:80]
            parts.append(f"`{star} #{rank}` score={score:+.3f}  `{short}`")

    parts.append("\n*send `info` for full ranking*")
    return "\n".join(parts)

# ── Process management ────────────────────────────────────────────────────────
async def start_papertrade() -> None:
    global trade_proc, current_cycle
    current_cycle = []
    trade_proc = await asyncio.create_subprocess_exec(
        *DOTNET_CMD,
        cwd=PROJECT_DIR,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.STDOUT,
        env={**os.environ, "DOTNET_CLI_TELEMETRY_OPTOUT": "1"},
    )
    asyncio.create_task(read_stdout(), name="stdout-reader")
    print("[bot] papertrade started")

async def stop_papertrade() -> None:
    global trade_proc
    if trade_proc and trade_proc.returncode is None:
        trade_proc.terminate()
        try:
            await asyncio.wait_for(trade_proc.wait(), timeout=8)
        except asyncio.TimeoutError:
            trade_proc.kill()
    trade_proc = None
    print("[bot] papertrade stopped")

PAPERTRADE_HEADER  = re.compile(r"=== Gravity-gen2 \| PAPER TRADE")
LIVETRAIN_HEADER   = re.compile(r"=== Gravity-gen2 \| LIVE TRAIN")
CYCLE_HEADER       = re.compile(r"=== Gravity-gen2 \| (PAPER TRADE|LIVE TRAIN)")

async def read_stdout() -> None:
    global current_cycle
    if trade_proc is None or trade_proc.stdout is None:
        return

    try:
        async for raw in trade_proc.stdout:
            line = strip_ansi(raw.decode("utf-8", errors="replace").rstrip())
            if not line:
                continue
            full_buffer.append(line)

            if CYCLE_HEADER.search(line):
                if current_cycle:
                    if PAPERTRADE_HEADER.search(current_cycle[0]):
                        summary = build_summary(current_cycle)
                    else:
                        summary = build_livetrain_summary(current_cycle)
                    if output_channel:
                        await output_channel.send(summary)
                current_cycle = [line]
            else:
                current_cycle.append(line)
    except Exception as exc:
        print(f"[bot] stdout reader error: {exc}")

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
        return f"Claude exited with code {proc.returncode}.\n{stderr[:500]}"
    return stdout or "(Claude produced no output)"

# ── Discord helpers ───────────────────────────────────────────────────────────
async def send_chunked(channel: discord.TextChannel, text: str, code: bool = False) -> None:
    wrap = "```\n{}\n```" if code else "{}"
    limit = 1900
    for i in range(0, max(len(text), 1), limit):
        chunk = text[i : i + limit] or "(empty)"
        await channel.send(wrap.format(chunk))

# ── Bot events ────────────────────────────────────────────────────────────────
intents = discord.Intents.default()
intents.message_content = True
client = discord.Client(intents=intents)

@client.event
async def on_ready() -> None:
    global output_channel, command_channel
    output_channel  = client.get_channel(OUTPUT_CHANNEL_ID)   # type: ignore[assignment]
    command_channel = client.get_channel(COMMAND_CHANNEL_ID)  # type: ignore[assignment]
    print(f"[bot] logged in as {client.user}")
    print(f"[bot] output  channel id={OUTPUT_CHANNEL_ID}  → {output_channel}")
    print(f"[bot] command channel id={COMMAND_CHANNEL_ID} → {command_channel}")
    await start_papertrade()

@client.event
async def on_message(message: discord.Message) -> None:
    global claude_busy

    print(f"[msg] author={message.author} bot={message.author.bot} "
          f"channel_id={message.channel.id} content={message.content!r}")

    if message.author.bot:
        print("[msg] ignored: bot message")
        return
    if message.channel.id != COMMAND_CHANNEL_ID:
        print(f"[msg] ignored: wrong channel (expected {COMMAND_CHANNEL_ID})")
        return

    text = message.content.strip()
    if not text:
        print("[msg] ignored: empty content")
        return

    # ── Info dump ────────────────────────────────────────────────────────────
    if text.lower() in INFO_TRIGGERS:
        blob = "\n".join(list(full_buffer))
        await send_chunked(message.channel, blob or "(no output yet)", code=True)  # type: ignore[arg-type]
        return

    # ── Claude request ────────────────────────────────────────────────────────
    if claude_busy:
        await message.reply("Claude is already working — please wait.")
        return

    claude_busy = True
    try:
        await message.reply(f"Stopping papertrade and forwarding to Claude: *{text[:200]}*")
        await stop_papertrade()

        await message.channel.send("⚙️ Claude is working…")  # type: ignore[union-attr]
        result = await run_claude(text)

        await send_chunked(message.channel, result or "(no output)", code=True)  # type: ignore[arg-type]
        await message.channel.send("Restarting papertrade…")  # type: ignore[union-attr]
        await start_papertrade()
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
