#!/usr/bin/env python3
"""
Discord bridge for Gravity-gen2.

Slash commands: /status /backtest /train /papertrade /livetrain /info
Text in command channel: forwarded to Claude Code for live code edits.

Env vars:
  DISCORD_TOKEN           – bot token
  DISCORD_OUTPUT_CHANNEL  – channel ID for trade summaries
  DISCORD_COMMAND_CHANNEL – channel ID for Claude + slash commands
  DISCORD_GUILD_ID        – (optional) guild ID for instant slash command sync
  GRAVITY_MODE            – starting mode: papertrade (default) or livetrain
"""

import asyncio
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
DISCORD_TOKEN      = os.environ["DISCORD_TOKEN"]
OUTPUT_CHANNEL_ID  = int(os.environ["DISCORD_OUTPUT_CHANNEL"])
COMMAND_CHANNEL_ID = int(os.environ["DISCORD_COMMAND_CHANNEL"])
GUILD_ID           = int(os.environ["DISCORD_GUILD_ID"]) if os.environ.get("DISCORD_GUILD_ID") else None
PROJECT_DIR        = os.path.dirname(os.path.abspath(__file__))
DOTNET_DLL         = os.path.join(PROJECT_DIR, "bin", "Release", "net10.0", "Gravity-gen2.dll")
INFO_TRIGGERS      = {"info", "more info", "full", "dump", "show"}

# ── State ─────────────────────────────────────────────────────────────────────
trade_proc: asyncio.subprocess.Process | None = None
full_buffer: deque[str] = deque(maxlen=600)
current_cycle: list[str] = []
output_channel: discord.TextChannel | None = None
command_channel: discord.TextChannel | None = None
current_mode: str = os.environ.get("GRAVITY_MODE", "papertrade")
claude_busy = False
status_message: discord.Message | None = None   # persistent summary (edit-in-place)
score_history: deque[float] = deque(maxlen=6)   # livetrain top-1 scores for trend

# ── ANSI stripper ─────────────────────────────────────────────────────────────
ANSI_RE = re.compile(r"\x1b\[[0-9;]*[A-Za-z]|\x1b\[2J|\x1b\[H|\r")
def strip_ansi(s: str) -> str:
    return ANSI_RE.sub("", s)

def chunk_text(text: str, limit: int = 1900) -> list[str]:
    return [text[i:i+limit] or "(empty)" for i in range(0, max(len(text), 1), limit)]

# ── Papertrade summary ────────────────────────────────────────────────────────
GENO_LINE_RE = re.compile(r"^Genotype:\s+(.+)$")
FITNESS_RE   = re.compile(r"F=([-\d.]+)")
ROW_RE       = re.compile(
    r"\s{2}(\w+USDT)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)\s{2,}(\S+)"
)

def build_summary(lines: list[str]) -> str:
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    geno_str, fitness = "", ""
    active, watching = [], []
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
            active.append(f"  `{coin:<18}` {state:<14} entry {entry}  now {current}  **{unrealised}**  [{regime}]")
            try:
                unrealised_vals.append(float(unrealised.replace("%", "")))
            except (ValueError, AttributeError):
                pass

    parts = [f"**Gravity-gen2 | Papertrade — {ts}**"]
    if fitness:
        parts.append(f"**Fitness:** `{fitness}`")
    if geno_str:
        short = re.sub(r"F=[-\d.]+", "", geno_str).strip().rstrip(",")
        parts.append(f"**Genotype:** `{short}`")
    parts.append("")
    if active:
        total_unreal = sum(unrealised_vals)
        parts.append(f"**Open trades ({len(active)}) — total unrealised: `{total_unreal:+.2f}%`:**")
        parts.extend(active)
    else:
        parts.append("**No open trades.**")
    parts.append(f"\n*Watching {len(watching)} coins • `/info` for full output*")
    return "\n".join(parts)

# ── Live-train summary ────────────────────────────────────────────────────────
# Format: "  ★  1      0.123  RSI(...)" or "     2      ..."
RANK_ROW_RE   = re.compile(r"^\s+[★ ]\s+(\d+)\s+([-\d.]+)\s+(.+)")
CYCLE_INFO_RE = re.compile(r"Cycle (\d+)\s+\|.+?Next evolution in (\d+) cycle")
AUTO_PROMOTE_RE = re.compile(r"=== AUTO-PROMOTE ===")

def build_livetrain_summary(lines: list[str]) -> str:
    ts = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    cycle_num, next_evo, rows = "", "", []

    for line in lines:
        cm = CYCLE_INFO_RE.search(line)
        if cm:
            cycle_num, next_evo = cm.group(1), cm.group(2)
            continue
        rm = RANK_ROW_RE.match(line)
        if rm:
            rows.append((int(rm.group(1)), float(rm.group(2)), rm.group(3).strip()))

    # Track score history for trend indicator
    top1 = next((r for r in rows if r[0] == 1), None)
    if top1:
        score_history.append(top1[1])

    # Compute trend arrow
    trend = ""
    if len(score_history) >= 2:
        diff = score_history[-1] - score_history[-2]
        trend = " ↑" if diff > 0.001 else (" ↓" if diff < -0.001 else " →")

    parts = [f"**Gravity-gen2 | Live Train — {ts}**"]
    if cycle_num:
        parts.append(f"Cycle **{cycle_num}** • next evolution in **{next_evo}** cycles (~{int(next_evo)*5} min)")
    if top1:
        parts.append(f"**Top score:** `{top1[1]:+.3f}`{trend}")
    if len(score_history) >= 2:
        hist_str = "  ".join(f"{s:+.3f}" for s in score_history)
        parts.append(f"**Trend:** `{hist_str}`")
    parts.append("")
    top5 = [r for r in rows if r[0] <= 5]
    if top5:
        parts.append("**Top 5 genotypes:**")
        for rank, score, geno in top5:
            star = "★" if rank == 1 else " "
            short = re.sub(r"F=[-\d.]+", "", geno).strip()[:80]
            parts.append(f"`{star} #{rank}` score={score:+.3f}  `{short}`")
    parts.append("\n*`/info` for full ranking*")
    return "\n".join(parts)

# ── Process management ────────────────────────────────────────────────────────
async def start_mode(mode: str | None = None) -> None:
    global trade_proc, current_cycle, current_mode, status_message
    if mode and mode != current_mode:
        current_mode = mode
        status_message = None   # new mode → new persistent message
        score_history.clear()
    current_cycle = []
    trade_proc = await asyncio.create_subprocess_exec(
        "dotnet", "run", "--", current_mode,
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
    status_message = None   # reset persistent message on stop
    print("[bot] process stopped")

PAPERTRADE_HEADER = re.compile(r"=== Gravity-gen2 \| PAPER TRADE")
CYCLE_HEADER      = re.compile(r"=== Gravity-gen2 \| (PAPER TRADE|LIVE TRAIN)")

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

            # Detect auto-promote and alert command channel
            if AUTO_PROMOTE_RE.search(line) and command_channel:
                await command_channel.send(f"🚀 **AUTO-PROMOTE**: `{line.strip()}`")

            if CYCLE_HEADER.search(line):
                if current_cycle:
                    is_paper = PAPERTRADE_HEADER.search(current_cycle[0])
                    summary = (build_summary(current_cycle)
                               if is_paper
                               else build_livetrain_summary(current_cycle))
                    if output_channel:
                        # Edit the persistent message if it exists; otherwise send new
                        sent = False
                        if status_message is not None:
                            try:
                                await status_message.edit(content=summary)
                                sent = True
                            except discord.NotFound:
                                status_message = None
                            except Exception as exc:
                                print(f"[bot] edit failed: {exc}")
                        if not sent:
                            status_message = await output_channel.send(summary)
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
        title="Gravity-gen2 — Portfolio Status",
        description=(
            f"**Period:** {period} ({days}d) • **Fitness:** `{fitness}`\n"
            f"**Source:** `{source}`\n"
            f"Fees: 0.055% taker ×2 + 0.050% slippage ×2 = **0.21%/trade**"
        ),
        color=color,
        timestamp=datetime.now(timezone.utc),
    )
    embed.add_field(
        name="Strategy (net of fees)",
        value=(
            f"Sharpe: **{sharpe}** | Sortino: **{sortino}**\n"
            f"PF: **{pf}** | Calmar: **{calmar}**\n"
            f"Win rate: **{wr}** ({wl})\n"
            f"Trades: **{trades}** | Avg: **{avg}%**\n"
            f"Max consec. loss: **{maxl}**"
        ),
        inline=True,
    )
    embed.add_field(
        name="Portfolio (€1000 start)",
        value=(
            f"Balance: **€{balance}**\n"
            f"Total: **€{total_e}** (**{total_r}**)\n"
            f"Max drawdown: **{max_dd}**"
        ),
        inline=True,
    )
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
    for chunk in chunk_text(raw):
        await interaction.followup.send(f"```\n{chunk}\n```")

@client.tree.command(name="papertrade", description="Switch to live papertrade mode")
async def slash_papertrade(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    await stop_mode()
    await start_mode("papertrade")
    await interaction.followup.send("Switched to **papertrade** mode.")

@client.tree.command(name="livetrain", description="Switch to live training mode (20 genotypes, evolves hourly)")
async def slash_livetrain(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    await stop_mode()
    await start_mode("livetrain")
    await interaction.followup.send("Switched to **livetrain** mode.")

@client.tree.command(name="info", description="Dump the full raw output buffer")
async def slash_info(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    blob = "\n".join(list(full_buffer))
    for chunk in chunk_text(blob or "(no output yet)"):
        await interaction.followup.send(f"```\n{chunk}\n```")

# ── Events ────────────────────────────────────────────────────────────────────
@client.event
async def on_ready() -> None:
    global output_channel, command_channel
    output_channel  = client.get_channel(OUTPUT_CHANNEL_ID)   # type: ignore[assignment]
    command_channel = client.get_channel(COMMAND_CHANNEL_ID)  # type: ignore[assignment]
    print(f"[bot] logged in as {client.user}")
    print(f"[bot] output  channel id={OUTPUT_CHANNEL_ID}  → {output_channel}")
    print(f"[bot] command channel id={COMMAND_CHANNEL_ID} → {command_channel}")
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

    # Text-based info dump (kept for convenience alongside /info)
    if text.lower() in INFO_TRIGGERS:
        blob = "\n".join(list(full_buffer))
        await send_chunked(message.channel, blob or "(no output yet)", code=True)  # type: ignore[arg-type]
        return

    # Everything else → Claude
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
