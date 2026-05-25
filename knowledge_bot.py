#!/usr/bin/env python3
"""
Gravity Knowledge Bot

Two channels, one bot:
  KNOWLEDGE — daily post at KNOWLEDGE_POST_HOUR UTC:
                · On This Day in history/science
                · Science concept of the day
                · Recent science headlines (BBC RSS)
              /dailyknowledge  to post on-demand

  TEACHER   — interactive Claude teacher with per-channel memory:
                /topic <subject>    start a lesson + intro
                /explain <concept>  deep-dive a concept
                /quiz [topic]       5-question quiz
                plain messages → answered by Claude as teacher

Env vars (add to .env):
  DISCORD_TOKEN_KNOWLEDGE   bot token — must be a SEPARATE application from the trading bot
  ANTHROPIC_API_KEY         Anthropic API key
  DISCORD_KNOWLEDGE_CHANNEL channel ID for daily posts
  DISCORD_TEACHER_CHANNEL   channel ID for teacher chat
  DISCORD_GUILD_ID          (shared) for instant slash command sync
  KNOWLEDGE_POST_HOUR       UTC hour for daily post (default: 8)
"""

import asyncio
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import deque
from datetime import datetime, time, timezone
from pathlib import Path

try:
    from dotenv import load_dotenv
    load_dotenv(Path(__file__).parent / ".env")
except ImportError:
    pass

import aiohttp
import anthropic
import discord
from discord import app_commands
from discord.ext import tasks

# ── Config ────────────────────────────────────────────────────────────────────
DISCORD_TOKEN      = os.environ.get("DISCORD_TOKEN_KNOWLEDGE") or os.environ["DISCORD_TOKEN"]
KNOWLEDGE_CH_ID    = int(os.environ["DISCORD_KNOWLEDGE_CHANNEL"])
TEACHER_CH_ID      = int(os.environ["DISCORD_TEACHER_CHANNEL"])
GUILD_ID           = int(os.environ["DISCORD_GUILD_ID"]) if os.environ.get("DISCORD_GUILD_ID") else None
ANTHROPIC_API_KEY  = os.environ["ANTHROPIC_API_KEY"]
POST_HOUR          = int(os.environ.get("KNOWLEDGE_POST_HOUR", "8"))

# Use Sonnet for teacher (fast + cheap), can bump to Opus for richer content
TEACHER_MODEL  = "claude-sonnet-4-6"
DAILY_MODEL    = "claude-sonnet-4-6"

SCIENCE_RSS    = "https://feeds.bbci.co.uk/news/science_and_environment/rss.xml"

claude = anthropic.AsyncAnthropic(api_key=ANTHROPIC_API_KEY)

# ── Teacher state (per channel) ───────────────────────────────────────────────
# topic:   current subject being taught in the channel
# history: list of {"role": "user"/"assistant", "content": "..."} — last 20 msgs
teacher_topic:   dict[int, str]        = {}
teacher_history: dict[int, deque]      = {}

MAX_HISTORY = 20  # message pairs kept in memory

def get_history(ch_id: int) -> list[dict]:
    if ch_id not in teacher_history:
        teacher_history[ch_id] = deque(maxlen=MAX_HISTORY)
    return list(teacher_history[ch_id])

def push_history(ch_id: int, role: str, content: str) -> None:
    if ch_id not in teacher_history:
        teacher_history[ch_id] = deque(maxlen=MAX_HISTORY)
    teacher_history[ch_id].append({"role": role, "content": content})

def teacher_system(ch_id: int) -> str:
    topic = teacher_topic.get(ch_id)
    topic_line = f" The current lesson topic is: **{topic}**." if topic else ""
    return (
        "You are an enthusiastic, patient, and knowledgeable teacher.{topic_line} "
        "Explain concepts clearly at the student's level — ask a follow-up question "
        "at the end of your response to check understanding or deepen the discussion. "
        "When giving a quiz, present 5 numbered questions, then wait for answers before revealing solutions. "
        "Use concrete examples, analogies, and real-world applications. "
        "Keep responses focused and under 400 words unless a deep explanation is explicitly requested."
    ).format(topic_line=topic_line)

# ── Claude helpers ────────────────────────────────────────────────────────────
async def ask_claude(system: str, messages: list, model: str = TEACHER_MODEL,
                     max_tokens: int = 1200) -> str:
    try:
        resp = await claude.messages.create(
            model=model, max_tokens=max_tokens,
            system=system, messages=messages,
        )
        return resp.content[0].text
    except Exception as exc:
        return f"*(Claude error: {exc})*"

def chunk_text(text: str, limit: int = 1900) -> list[str]:
    paragraphs, current = text.split("\n\n"), ""
    chunks = []
    for para in paragraphs:
        if len(current) + len(para) + 2 > limit:
            if current:
                chunks.append(current.strip())
            current = para
        else:
            current = (current + "\n\n" + para).strip() if current else para
    if current:
        chunks.append(current.strip())
    return chunks or ["(empty)"]

# ── RSS fetch ─────────────────────────────────────────────────────────────────
async def fetch_science_headlines() -> list[str]:
    try:
        async with aiohttp.ClientSession() as session:
            async with session.get(SCIENCE_RSS, timeout=aiohttp.ClientTimeout(total=8)) as r:
                text = await r.text()
        root = ET.fromstring(text)
        return [item.findtext("title", "").strip()
                for item in root.findall(".//item")[:5]
                if item.findtext("title")]
    except Exception:
        return []

# ── Daily knowledge post ──────────────────────────────────────────────────────
async def build_daily_embed() -> discord.Embed:
    today     = datetime.now(timezone.utc)
    date_str  = today.strftime("%B %d, %Y")
    month_day = today.strftime("%B %d")
    headlines = await fetch_science_headlines()
    news_ctx  = ("Recent science headlines:\n" + "\n".join(f"• {h}" for h in headlines)
                 if headlines else "")

    prompt = (
        f"Today is {date_str}. Write a daily knowledge post with exactly three sections.\n\n"
        f"## On This Day — {month_day}\n"
        f"Two or three notable events that occurred on {month_day} in history (any year). "
        f"Prioritise science, technology, exploration, and medicine. Include the year for each event.\n\n"
        f"## Science Concept of the Day\n"
        f"Explain one fascinating scientific concept, discovery, or natural phenomenon in 3–4 engaging sentences. "
        f"Make it surprising or counterintuitive where possible.\n\n"
        f"## Did You Know?\n"
        f"One unexpected, little-known, or mind-bending scientific fact — one or two sentences.\n\n"
        f"{news_ctx}\n\n"
        f"Write the three sections using those exact ## headings. Be accurate, engaging, and specific."
    )

    raw = await ask_claude(
        "You are an engaging science communicator and historian. "
        "Your writing is accurate, vivid, and makes readers want to learn more.",
        [{"role": "user", "content": prompt}],
        model=DAILY_MODEL, max_tokens=1000,
    )

    embed = discord.Embed(
        title=f"📚 Daily Knowledge — {date_str}",
        color=discord.Color.blue(),
        timestamp=today,
    )

    # Parse ## sections
    sections = re.split(r"\n##\s+", "\n" + raw)
    for sec in sections:
        sec = sec.strip()
        if not sec:
            continue
        lines = sec.split("\n", 1)
        heading = lines[0].strip()
        body    = lines[1].strip() if len(lines) > 1 else ""
        if heading and body:
            embed.add_field(name=heading, value=body[:1020], inline=False)

    if not embed.fields:
        embed.description = raw[:4000]

    if headlines:
        embed.add_field(
            name="📰 Science Headlines (BBC)",
            value="\n".join(f"• {h}" for h in headlines[:4]),
            inline=False,
        )

    embed.set_footer(text="Use /topic to start a lesson • /quiz for a challenge • /explain <concept>")
    return embed

# ── Discord bot ───────────────────────────────────────────────────────────────
intents = discord.Intents.default()
intents.message_content = True

class KnowledgeBot(discord.Client):
    def __init__(self) -> None:
        super().__init__(intents=intents)
        self.tree = app_commands.CommandTree(self)

    async def setup_hook(self) -> None:
        if GUILD_ID:
            guild = discord.Object(id=GUILD_ID)
            self.tree.copy_global_to(guild=guild)
            await self.tree.sync(guild=guild)
            print(f"[knowledge] slash commands synced to guild {GUILD_ID}")
        else:
            await self.tree.sync()
            print("[knowledge] slash commands synced globally (up to 1h)")
        daily_post.start()

client = KnowledgeBot()

# ── Scheduled daily post ──────────────────────────────────────────────────────
@tasks.loop(time=time(hour=POST_HOUR, minute=0, tzinfo=timezone.utc))
async def daily_post() -> None:
    ch = client.get_channel(KNOWLEDGE_CH_ID)
    if ch is None:
        print(f"[knowledge] daily post: channel {KNOWLEDGE_CH_ID} not found")
        return
    async with ch.typing():
        embed = await build_daily_embed()
    await ch.send(embed=embed)  # type: ignore[union-attr]
    print(f"[knowledge] daily post sent at {datetime.now(timezone.utc):%H:%M UTC}")

@daily_post.before_loop
async def before_daily() -> None:
    await client.wait_until_ready()

# ── Slash commands ────────────────────────────────────────────────────────────
@client.tree.command(name="dailyknowledge", description="Post the daily knowledge update now")
async def slash_daily(interaction: discord.Interaction) -> None:
    await interaction.response.defer(thinking=True)
    embed = await build_daily_embed()
    await interaction.followup.send(embed=embed)

@client.tree.command(name="topic", description="Start a lesson on a subject")
@app_commands.describe(subject="The topic you want to learn about")
async def slash_topic(interaction: discord.Interaction, subject: str) -> None:
    ch_id = interaction.channel_id
    teacher_topic[ch_id]   = subject
    teacher_history[ch_id] = deque(maxlen=MAX_HISTORY)

    await interaction.response.defer(thinking=True)

    prompt = (
        f"Start an engaging introductory lesson on: **{subject}**.\n\n"
        f"Structure it as:\n"
        f"1. A one-sentence hook that makes it immediately interesting\n"
        f"2. Core concept explained clearly (3–4 sentences)\n"
        f"3. One real-world example or application\n"
        f"4. End with a question to check the student's starting understanding"
    )
    reply = await ask_claude(teacher_system(ch_id),
                             [{"role": "user", "content": prompt}])
    push_history(ch_id, "user", prompt)
    push_history(ch_id, "assistant", reply)

    embed = discord.Embed(
        title=f"📖 Lesson: {subject}",
        description=reply,
        color=discord.Color.green(),
    )
    embed.set_footer(text="Ask questions freely • /explain <concept> • /quiz to test yourself")
    await interaction.followup.send(embed=embed)

@client.tree.command(name="explain", description="Deep-dive explanation of a concept")
@app_commands.describe(concept="The concept to explain")
async def slash_explain(interaction: discord.Interaction, concept: str) -> None:
    ch_id = interaction.channel_id
    await interaction.response.defer(thinking=True)

    prompt = (
        f"Give a thorough, layered explanation of: **{concept}**\n\n"
        f"Cover: what it is, why it works that way, a concrete analogy, "
        f"and one surprising implication or edge case."
    )
    messages = get_history(ch_id) + [{"role": "user", "content": prompt}]
    reply = await ask_claude(teacher_system(ch_id), messages, max_tokens=1500)
    push_history(ch_id, "user", prompt)
    push_history(ch_id, "assistant", reply)

    embed = discord.Embed(
        title=f"🔍 {concept}",
        color=discord.Color.orange(),
    )
    for chunk in chunk_text(reply):
        embed.add_field(name="​", value=chunk, inline=False)
    await interaction.followup.send(embed=embed)

@client.tree.command(name="quiz", description="Get a 5-question quiz on the current topic (or specify one)")
@app_commands.describe(topic="Topic to quiz on (defaults to current lesson topic)")
async def slash_quiz(interaction: discord.Interaction, topic: str | None = None) -> None:
    ch_id     = interaction.channel_id
    quiz_topic = topic or teacher_topic.get(ch_id)
    await interaction.response.defer(thinking=True)

    if not quiz_topic:
        await interaction.followup.send(
            "No active topic. Use `/topic <subject>` first, or pass a topic to `/quiz`.")
        return

    prompt = (
        f"Create a 5-question quiz on: **{quiz_topic}**\n\n"
        f"Mix question types: 2 multiple-choice (A/B/C/D), 2 short-answer, 1 true/false.\n"
        f"Number them 1–5. Do NOT include the answers yet — wait for the student to respond."
    )
    messages = get_history(ch_id) + [{"role": "user", "content": prompt}]
    reply = await ask_claude(teacher_system(ch_id), messages, max_tokens=800)
    push_history(ch_id, "user", prompt)
    push_history(ch_id, "assistant", reply)

    embed = discord.Embed(
        title=f"❓ Quiz — {quiz_topic}",
        description=reply,
        color=discord.Color.purple(),
    )
    embed.set_footer(text="Answer in chat — I'll score your responses")
    await interaction.followup.send(embed=embed)

# ── Message handler (teacher channel) ────────────────────────────────────────
@client.event
async def on_ready() -> None:
    print(f"[knowledge] logged in as {client.user}")
    print(f"[knowledge] knowledge ch={KNOWLEDGE_CH_ID}  teacher ch={TEACHER_CH_ID}")
    print(f"[knowledge] daily post scheduled at {POST_HOUR:02d}:00 UTC")

@client.event
async def on_message(message: discord.Message) -> None:
    if message.author.bot:
        return
    if message.channel.id != TEACHER_CH_ID:
        return

    text = message.content.strip()
    if not text:
        return

    ch_id = message.channel.id
    async with message.channel.typing():
        push_history(ch_id, "user", text)
        messages = get_history(ch_id)
        reply = await ask_claude(teacher_system(ch_id), messages)
        push_history(ch_id, "assistant", reply)

    # Send as plain text chunks (teacher channel feels more conversational than embeds)
    for chunk in chunk_text(reply):
        await message.channel.send(chunk)  # type: ignore[union-attr]

# ── Entry point ───────────────────────────────────────────────────────────────
if __name__ == "__main__":
    missing = [v for v in ("ANTHROPIC_API_KEY", "DISCORD_KNOWLEDGE_CHANNEL", "DISCORD_TEACHER_CHANNEL")
               if not os.environ.get(v)]
    if not os.environ.get("DISCORD_TOKEN_KNOWLEDGE") and not os.environ.get("DISCORD_TOKEN"):
        missing.append("DISCORD_TOKEN_KNOWLEDGE")
    if missing:
        print(f"[knowledge] missing env vars: {', '.join(missing)}", file=sys.stderr)
        sys.exit(1)
    client.run(DISCORD_TOKEN)
