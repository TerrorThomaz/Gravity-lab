"""
Hyperliquid Paper Trade Bridge — FastAPI server.

Architecture:
  C# Gravity-gen2 => HyperliquidClient (HTTP) => bot/hyperliquid_server.py
  => hyperliquid-python-sdk => api.hyperliquid.xyz

This bridge handles:
  - Market data queries (OHLCV candles, orderbook, universe metadata, funding rates)
  - Account state (positions, margin, open orders)
  - Order placement & cancellation (EIP-712 signing via Python SDK)
  - Real-time candle streaming via WebSocket (zero REST weight cost)

Env vars (read from ../.env, or the process environment which takes precedence):
  HYPERLIQUID_PRIVATE_KEY      - signing key, 64 hex chars (0x prefix optional).
                                 HYPERLIQUID_API_KEY is accepted as an alias.
                                 This is a KEY, not an address — an address cannot sign.
  HYPERLIQUID_ACCOUNT_ADDRESS  - the FUNDED master account, required when the key above is an
                                 API/agent wallet. Agent wallets sign for an account but hold
                                 nothing, so without this every balance and position query
                                 targets the empty signer and reads zero.
  HYPERLIQUID_TESTNET          - "1" for testnet. Anything else means MAINNET, REAL FUNDS.
                                 Startup refuses to proceed if this is unset while a
                                 misspelled look-alike exists.
  HYPERLIQUID_API_URL          - override the API URL (defaults follow HYPERLIQUID_TESTNET)
"""

import asyncio
import json
import logging
import re
import os
import time
from datetime import datetime, timezone
from typing import Optional

import msgpack
from eth_account import Account
from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from hyperliquid.exchange import Exchange
from hyperliquid.info import Info
from hyperliquid.utils.constants import MAINNET_API_URL, TESTNET_API_URL
from hyperliquid.utils.signing import (
    action_hash,
    construct_phantom_agent,
    l1_payload,
    sign_inner,
    sign_l1_action,
)
from hyperliquid.utils.types import Cloid
from hyperliquid.websocket_manager import WebsocketManager

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)

# ── Configuration ─────────────────────────────────────────────────────────────

# Load .env from the repo root. Until this existed the bridge read os.environ ONLY, so the key had
# to be exported by whatever shell launched it — which meant it lived nowhere but that process's
# memory. The running instance was started 2026-08-16 with no supervisor and its key was not in
# .env, so a reboot would have destroyed it permanently. Existing environment wins over .env
# (override=False), so an explicit export still takes precedence.
try:
    from dotenv import load_dotenv
    _ENV_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".env")
    load_dotenv(_ENV_PATH, override=False)
except ImportError:
    logger.warning("python-dotenv not installed — .env will NOT be read, env vars must be exported")

# FAIL LOUDLY ON A MISSPELLED TESTNET FLAG.
# The old logic was `getenv("HYPERLIQUID_TESTNET","") != "1"` → anything unset or misspelled
# silently selected MAINNET. A typo is not a reason to point an automated trader at real money,
# and this exact typo (HYPRLIQUID_TESTNET, missing the E) was in .env when this was written.
_TESTNET_RAW = os.environ.get("HYPERLIQUID_TESTNET")
_LOOKALIKES = [k for k in os.environ
               if k != "HYPERLIQUID_TESTNET" and k.upper().replace("_", "").endswith("TESTNET")]
if _TESTNET_RAW is None and _LOOKALIKES:
    raise RuntimeError(
        f"HYPERLIQUID_TESTNET is unset but found look-alike var(s): {_LOOKALIKES}. "
        "Refusing to default to MAINNET on what is almost certainly a typo. "
        "Set HYPERLIQUID_TESTNET=1 (testnet) or HYPERLIQUID_TESTNET=0 (mainnet, real funds)."
    )

IS_MAINNET = (_TESTNET_RAW or "").lower() != "1"
BASE_URL = os.environ.get("HYPERLIQUID_API_URL", MAINNET_API_URL if IS_MAINNET else TESTNET_API_URL)
# HYPERLIQUID_API_KEY is accepted as an alias: Hyperliquid's own term for these is "API wallet",
# so that is the name people reach for. PRIVATE_KEY wins if both are set.
PRIVATE_KEY = os.environ.get("HYPERLIQUID_PRIVATE_KEY") or os.environ.get("HYPERLIQUID_API_KEY", "")

if not PRIVATE_KEY:
    raise RuntimeError(
        "HYPERLIQUID_PRIVATE_KEY (or HYPERLIQUID_API_KEY) is required. "
        "Note this is the API/agent wallet's PRIVATE KEY (64 hex chars), not its ADDRESS (40 hex)."
    )

# An address cannot sign. Catching it here turns a confusing downstream signing failure into one
# clear line at startup — a 40-hex value in this slot is always the address pasted by mistake.
_pk = PRIVATE_KEY[2:] if PRIVATE_KEY.lower().startswith("0x") else PRIVATE_KEY
if len(_pk) == 40:
    raise RuntimeError(
        "HYPERLIQUID_PRIVATE_KEY looks like an ADDRESS (40 hex chars), not a private key (64 hex). "
        "Use the API wallet's private key from app.hyperliquid.xyz/API."
    )
if len(_pk) != 64:
    raise RuntimeError(f"HYPERLIQUID_PRIVATE_KEY should be 64 hex chars, got {len(_pk)}.")

logger.info("Hyperliquid bridge: %s", "MAINNET — REAL FUNDS" if IS_MAINNET else "TESTNET")

wallet = Account.from_key(PRIVATE_KEY)
logger.info(f"Loaded wallet address: {wallet.address}")

# AGENT/API WALLETS: the signing key and the funded account are DIFFERENT addresses.
#
# An API wallet signs on behalf of a master account but holds nothing itself. Deriving the address
# from the key — which is all this did before — therefore queries the agent, so balance reads empty
# and open positions read zero no matter what the account actually holds. That would silently
# defeat the exchange-authoritative reconciliation in HyperliquidPaperTrade ("[RECON] exchange
# reports N open position(s)"), which is designed to trust the venue over local state: it would
# faithfully report 0 forever.
#
# Set HYPERLIQUID_ACCOUNT_ADDRESS to the MASTER account when signing with an agent key. Unset is
# still correct for a plain wallet that signs for itself, which is the previous behaviour.
ACCOUNT_ADDRESS = (os.environ.get("HYPERLIQUID_ACCOUNT_ADDRESS", "") or "").strip() or None
if ACCOUNT_ADDRESS and not re.fullmatch(r"0x[0-9a-fA-F]{40}", ACCOUNT_ADDRESS):
    raise RuntimeError(f"HYPERLIQUID_ACCOUNT_ADDRESS must be a 0x-prefixed 40-hex address, got {ACCOUNT_ADDRESS!r}")

# Initialize SDK components
info = Info(base_url=BASE_URL, skip_ws=False)
exchange = Exchange(wallet=wallet, base_url=BASE_URL, account_address=ACCOUNT_ADDRESS)

# Every balance/position query must use the FUNDED account, not the signer.
QUERY_ADDRESS = ACCOUNT_ADDRESS or wallet.address
if ACCOUNT_ADDRESS and ACCOUNT_ADDRESS.lower() != wallet.address.lower():
    logger.info("Agent mode: signing with %s on behalf of %s", wallet.address, ACCOUNT_ADDRESS)
else:
    logger.info("Self-signing mode: %s (set HYPERLIQUID_ACCOUNT_ADDRESS if this is an API wallet)",
                wallet.address)
ws_manager = info.ws_manager  # Background thread started by Info constructor

# Hyperliquid's own coin names are the source of truth (e.g. "BTC", "kBONK") — the caller sends
# the exact name to use. Blanket-uppercasing here breaks the "k" prefix on meme-coin perps
# (kBONK/kPEPE/kSHIB), which the SDK doesn't recognize once mangled to "KBONK" etc. Only strip a
# trailing USDT/USD suffix defensively for callers that still send a Binance-style symbol.
def normalize_coin(symbol: str) -> str:
    for suffix in ("USDT", "USD"):
        if symbol.upper().endswith(suffix):
            return symbol[: -len(suffix)]
    return symbol


# ── Candle Cache ──────────────────────────────────────────────────────────────
# Disk cache for historical candles. Matches the Bybit pattern used in CandleFetcher.
# Format: candle_cache/{coin}_{interval}.csv  — columns: timestamp_ms,open,high,low,close,volume

CACHE_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "candle_cache")
os.makedirs(CACHE_DIR, exist_ok=True)

import csv
from pathlib import Path


def _cache_path(coin: str, interval: str) -> str:
    """Path to cached candle file."""
    safe_coin = coin.replace("/", "_").replace("-", "_")
    return os.path.join(CACHE_DIR, f"{safe_coin}_{interval}.csv")


def load_cached_candles(coin: str, interval: str) -> list[dict]:
    """Load candles from disk cache. Returns empty list if no cache exists."""
    path = _cache_path(coin, interval)
    if not os.path.exists(path):
        return []
    candles = []
    with open(path, "r") as f:
        reader = csv.DictReader(f)
        for row in reader:
            candles.append({
                "T": int(row["T"]),
                "c": float(row["c"]),
                "h": float(row["h"]),
                "l": float(row["l"]),
                "o": float(row["o"]),
                "v": float(row["v"]),
                "n": int(row["n"]) if "n" in row else 0,
            })
    return candles


def save_cached_candles(coin: str, interval: str, candles: list[dict]):
    """Append new candles to disk cache. Creates file if none exists."""
    path = _cache_path(coin, interval)
    existing = load_cached_candles(coin, interval)
    if not existing:
        # New file — write header
        with open(path, "w", newline="") as f:
            writer = csv.DictWriter(f, fieldnames=["T", "o", "h", "l", "c", "v", "n"])
            writer.writeheader()
            for c in candles:
                writer.writerow({"T": c["T"], "o": c["o"], "h": c["h"], "l": c["l"], "c": c["c"], "v": c["v"], "n": c["n"]})
    else:
        # Append only candles newer than last cached timestamp
        last_ts = existing[-1]["T"]
        new_candles = [c for c in candles if c["T"] > last_ts]
        if new_candles:
            with open(path, "a", newline="") as f:
                writer = csv.DictWriter(f, fieldnames=["T", "o", "h", "l", "c", "v", "n"])
                for c in new_candles:
                    writer.writerow({"T": c["T"], "o": c["o"], "h": c["h"], "l": c["l"], "c": c["c"], "v": c["v"], "n": c["n"]})


def merge_with_cache(new_candles: list[dict], coin: str, interval: str) -> list[dict]:
    """Merge newly fetched candles with cached ones, deduplicating by timestamp."""
    existing = load_cached_candles(coin, interval)
    if not existing:
        return new_candles
    existing_ts = {c["T"] for c in existing}
    merged = existing + [c for c in new_candles if c["T"] not in existing_ts]
    # Sort by timestamp
    merged.sort(key=lambda c: c["T"])
    return merged


# ── WebSocket Candle Streaming ────────────────────────────────────────────────
# Active subscriptions: coin:interval -> callback list
active_candle_subs: dict[str, list] = {}
candle_buffer: dict[str, list] = {}  # coin:interval -> latest N candles in memory


def _on_candle_message(data: dict):
    """Callback for incoming candle WebSocket messages."""
    try:
        coin = data.get("data", {}).get("s", "")
        interval = data.get("data", {}).get("i", "")
        if not coin or not interval:
            return
        key = f"{coin}:{interval}"
        # Parse candle data (WS returns numeric types, not strings)
        candle = {
            "T": int(data["data"]["T"]),
            "t": int(data["data"]["t"]),
            "o": float(data["data"]["o"]),
            "h": float(data["data"]["h"]),
            "l": float(data["data"]["l"]),
            "c": float(data["data"]["c"]),
            "v": float(data["data"]["v"]),
            "n": int(data["data"].get("n", 0)),
        }
        # Store in buffer (keep last 5000 candles per coin:interval)
        if key not in candle_buffer:
            candle_buffer[key] = []
        candle_buffer[key].append(candle)
        if len(candle_buffer[key]) > 5000:
            candle_buffer[key] = candle_buffer[key][-5000:]
        # Persist to disk
        save_cached_candles(coin, interval, [candle])
        # Notify subscribers
        for cb in active_candle_subs.get(key, []):
            cb(candle)
    except Exception as e:
        logger.error(f"Error processing candle message: {e}")


# Subscribe to candles via the SDK's WebsocketManager
def subscribe_candle(coin: str, interval: str, callback=None):
    """Subscribe to 1m candle updates via WebSocket."""
    sub = {"type": "candle", "coin": coin, "interval": interval}
    key = f"{coin}:{interval}"
    if key not in active_candle_subs:
        active_candle_subs[key] = []
    if callback:
        active_candle_subs[key].append(callback)
    # Register with SDK manager
    ws_manager.subscribe(sub, _on_candle_message)


# ── FastAPI App ───────────────────────────────────────────────────────────────

app = FastAPI(title="Hyperliquid Paper Trade Bridge", version="1.0.0")

# This server signs and places real orders — CORS is for browser-context requests only (our own
# C# HttpClient isn't subject to it at all), but "*" with credentials is needless exposure for a
# server holding a live wallet. Restricted to the local client's own origin.
app.add_middleware(
    CORSMiddleware,
    allow_origins=["http://127.0.0.1", "http://localhost"],
    allow_credentials=True,
    allow_methods=["GET", "POST"],
    allow_headers=["*"],
)


@app.on_event("startup")
async def startup():
    """Warm up metadata on startup."""
    try:
        meta = await asyncio.to_thread(info.meta)
        logger.info(f"Loaded universe with {len(meta['universe'])} perpetual assets")
    except Exception as e:
        logger.error(f"Failed to load metadata: {e}")


@app.get("/health")
async def health():
    """Health check endpoint."""
    return {"status": "ok"}


# ── Market Data Endpoints ─────────────────────────────────────────────────────


@app.get("/api/ohlcv")
async def get_ohlcv(symbol: str, interval: str = "1h", limit: int = 500, startTime: Optional[int] = None, endTime: Optional[int] = None):
    """Fetch OHLCV candles.

    Uses disk cache first, then fetches fresh data from HL API if needed.
    Candles are saved to disk for future cycles.
    """
    coin = normalize_coin(symbol)

    # Try cache first
    cached = load_cached_candles(coin, interval)

    # If cache has enough recent data, serve from it
    if cached and limit <= len(cached):
        result = cached[-limit:]
        return _format_candles(result)

    # Fetch from API (paginated if needed)
    if endTime is None:
        endTime = int(time.time() * 1000)
    if startTime is None:
        # Default: go back far enough for the requested limit
        startTime = endTime - (limit * 60 * 1000)  # rough estimate for 1m candles

    all_candles = []
    current_start = startTime
    chunk_size = 5000  # HL max per request

    while len(all_candles) < limit:
        try:
            candles = await asyncio.to_thread(
                info.candles_snapshot,
                coin, interval, current_start, min(current_start + chunk_size * 60 * 1000, endTime)
            )
            if not candles:
                break
            all_candles.extend(candles)
            if len(candles) < chunk_size:
                break
            # Advance start time past the last candle's close
            current_start = candles[-1]["T"] + 1
        except Exception as e:
            logger.error(f"Error fetching candles for {coin}/{interval}: {e}")
            break

    # Merge with cache and save
    all_candles = merge_with_cache(all_candles, coin, interval)
    result = all_candles[-limit:]

    return _format_candles(result)


@app.post("/api/candleSnapshot")
async def post_candle_snapshot(payload: dict):
    """Fetch candle snapshot with pagination (HL native endpoint).
    
    Used by HyperliquidClient.FetchOhlcvPaginatedAsync for deep history retrieval.
    Body: {"symbol": "BTC", "interval": "1h", "startTime": 1234567890000, "endTime": 1234567890000}
    Returns raw HL format (keys: T, c, h, l, o, v, n) — NOT _format_candles().
    """
    try:
        coin = payload.get("symbol", "")
        interval = payload.get("interval", "1h")
        start_time = payload.get("startTime", 0)
        end_time = payload.get("endTime", int(time.time() * 1000))
        
        if not coin:
            raise HTTPException(status_code=400, detail="symbol required")
        
        coin = normalize_coin(coin)
        
        candles = await asyncio.to_thread(
            info.candles_snapshot,
            coin, interval, start_time, end_time
        )
        
        if candles:
            # Save to cache (raw format)
            save_cached_candles(coin, interval, candles)
        
        return candles  # Return raw HL format directly
        
    except Exception as e:
        logger.error(f"candleSnapshot error: {e}")
        raise HTTPException(status_code=500, detail=f"Candle snapshot failed: {str(e)}")


def _format_candles(raw_candles: list[dict]) -> list[dict]:
    """Convert raw HL candle dicts to unified format."""
    formatted = []
    for c in raw_candles:
        formatted.append({
            "time_ms": c["T"],
            "open": float(c["o"]),
            "high": float(c["h"]),
            "low": float(c["l"]),
            "close": float(c["c"]),
            "volume": float(c["v"]),
        })
    return formatted


@app.get("/api/orderbook")
async def get_orderbook(symbol: str, depth: int = 20):
    """Fetch L2 orderbook snapshot."""
    coin = normalize_coin(symbol)
    try:
        book = await asyncio.to_thread(info.l2_snapshot, coin)
        return {
            "coin": coin,
            "depth": depth,
            "bids": [{"price": float(b[0]), "size": float(b[1])} for b in book.get("levels", [[], []])[0][:depth]],
            "asks": [{"price": float(a[0]), "size": float(a[1])} for a in book.get("levels", [[], []])[1][:depth]],
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Orderbook error: {str(e)}")


@app.get("/api/universe")
async def get_universe():
    """Fetch all tradable perpetual assets."""
    try:
        meta = await asyncio.to_thread(info.meta)
        perps = []
        for asset in meta.get("universe", []):
            perps.append({
                "name": asset["name"],
                "szDecimals": asset["szDecimals"],
                "maxLeverage": asset["maxLeverage"],
            })
        return {"perpetuals": perps, "count": len(perps)}
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Universe error: {str(e)}")


@app.get("/api/funding")
async def get_funding(symbol: str, startTime: Optional[int] = None, endTime: Optional[int] = None):
    """Fetch funding rate history for a symbol."""
    coin = normalize_coin(symbol)
    try:
        if startTime is None:
            startTime = int(time.time() * 1000) - 7 * 24 * 60 * 60 * 1000  # last 7 days
        if endTime is None:
            endTime = int(time.time() * 1000)

        funding_history = await asyncio.to_thread(
            info.funding_history, coin, startTime, endTime
        )
        return {
            "coin": coin,
            "rates": [{"timestamp": f["fundingRateTime"], "rate": float(f["fundingRate"])} for f in funding_history],
            "current_rate": funding_history[-1]["fundingRate"] if funding_history else 0.0,
        }
    except Exception as e:
        # Funding is optional — return null gracefully
        return {"coin": coin, "rates": [], "current_rate": 0.0}


@app.get("/api/all-mids")
async def get_all_mids():
    """Get current mid prices for all perpetuals."""
    try:
        mids = await asyncio.to_thread(info.all_mids)
        return mids
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"All mids error: {str(e)}")


# ── User Account Endpoints ────────────────────────────────────────────────────


@app.get("/api/user/info")
async def get_user_info():
    """Fetch account state: positions, margin, leverage, open orders."""
    try:
        user_state = await asyncio.to_thread(info.user_state, QUERY_ADDRESS)
        # Extract position summary
        positions = []
        for pos in user_state.get("assetPositions", []):
            p = pos.get("position", {})
            positions.append({
                "coin": p.get("coin", ""),
                "szi": float(p.get("szi", 0)),
                "entryPx": float(p.get("entryPx", 0)),
                "unrealizedPnl": float(p.get("unrealizedPnl", 0)),
                "leverage": float(p.get("leverage", {}).get("value", 1)),
            })
        return {
            "address": QUERY_ADDRESS,
            "signer": wallet.address,
            "marginValue": float(user_state.get("marginSummary", {}).get("totalUsd", 0)),
            "positions": positions,
            "openOrders": await asyncio.to_thread(info.open_orders, QUERY_ADDRESS),
        }
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"User info error: {str(e)}")


# ── Trading Endpoints ─────────────────────────────────────────────────────────


@app.post("/api/user/order")
async def place_order(payload: dict):
    """Place a limit or market order.

    Payload:
      coin: str          - e.g. "BTC"
      side: str          - "B" (buy) or "A" (ask/sell)
      size: float        - contract size
      price: float       - limit price (ignored for market orders)
      isLimit: bool      - True for limit, False for market
      reduceOnly: bool   - True to close position
      tif: str           - "Gtc", "Ioc", or "Alo" (for limit orders)
      slippage: float    - max acceptable slippage for market orders (default 0.1%)
    """
    try:
        coin = payload.get("coin", "")
        side_str = payload.get("side", "")
        size = float(payload.get("size", 0))
        price = float(payload.get("price", 0))
        is_limit = payload.get("isLimit", True)
        reduce_only = payload.get("reduceOnly", False)
        tif = payload.get("tif", "Gtc")
        slippage = float(payload.get("slippage", 0.1))

        is_buy = side_str == "B"

        # Build order request (plain dict — SDK handles conversion internally)
        order_request = {
            "coin": coin,
            "is_buy": is_buy,
            "sz": size,
            "limit_px": price,
            "order_type": {"limit": {"tif": tif}} if is_limit else None,
            "reduce_only": reduce_only,
        }

        # Generate client-order-ID for tracking
        cloid = Cloid.from_int(int(time.time() * 1000) % (2**63))
        order_request["cloid"] = cloid

        # Place via SDK
        if is_limit:
            response = await asyncio.to_thread(
                exchange.order,
                name=coin,
                is_buy=is_buy,
                sz=size,
                limit_px=price,
                order_type={"limit": {"tif": tif}},
                reduce_only=reduce_only,
                cloid=cloid,
            )
        else:
            # Market order: use aggressive limit with slippage buffer
            # First get current mid price
            mids = await asyncio.to_thread(info.all_mids)
            mid_price = float(mids.get(coin, price))
            # Set aggressive limit price
            if is_buy:
                aggressive_price = mid_price * (1 + slippage / 100)
            else:
                aggressive_price = mid_price * (1 - slippage / 100)

            response = await asyncio.to_thread(
                exchange.market_open,
                coin=coin,
                is_buy=is_buy,
                sz=size,
                px=aggressive_price,
                slippage=slippage,
                cloid=cloid,
            )

        return {"status": "submitted", "response": response, "cloid": cloid.to_hex()}

    except Exception as e:
        logger.error(f"Order placement failed: {e}")
        raise HTTPException(status_code=500, detail=f"Order failed: {str(e)}")


@app.post("/api/user/cancel")
async def cancel_order(payload: dict):
    """Cancel a specific order by OID or Cloid.

    Payload:
      coin: str     - e.g. "BTC"
      oid: int      - order ID (legacy)
      cloid: str    - client-order-ID hex string (preferred)
    """
    try:
        coin = payload.get("coin", "")
        cloid_str = payload.get("cloid", "")
        oid = payload.get("oid")

        if cloid_str:
            cloid = Cloid.from_hex(cloid_str)
            response = await asyncio.to_thread(exchange.cancel_by_cloid, coin, cloid)
        elif oid is not None:
            response = await asyncio.to_thread(exchange.cancel, coin, int(oid))
        else:
            raise HTTPException(status_code=400, detail="Must provide either 'oid' or 'cloid'")

        return {"status": "cancelled", "response": response}

    except Exception as e:
        logger.error(f"Cancel failed: {e}")
        raise HTTPException(status_code=500, detail=f"Cancel failed: {str(e)}")


@app.post("/api/user/cancel_all")
async def cancel_all_orders():
    """Cancel all open orders for the account."""
    try:
        response = await asyncio.to_thread(exchange.cancel_all_orders)
        return {"status": "cancelled_all", "response": response}
    except Exception as e:
        logger.error(f"CancelAll failed: {e}")
        raise HTTPException(status_code=500, detail=f"CancelAll failed: {str(e)}")


# ── WebSocket Candle Endpoint ─────────────────────────────────────────────────
# Clients can subscribe to real-time 1m candles via this endpoint.
# Usage: POST /api/ws/subscribe with {"type":"candle","coin":"BTC","interval":"1m"}


@app.post("/api/ws/subscribe")
async def ws_subscribe(payload: dict):
    """Subscribe to a data stream via WebSocket.

    Supported types:
      - candle: {"coin":"BTC","interval":"1m"}
      - allMids: no additional params
      - l2Book: {"coin":"ETH"}
      - userEvents: {"user":"<address>"}
    """
    sub_type = payload.get("type", "")
    if sub_type == "candle":
        coin = payload.get("coin", "")
        interval = payload.get("interval", "1m")
        subscribe_candle(coin, interval)
        return {"status": "subscribed", "type": "candle", "coin": coin, "interval": interval}
    else:
        # Delegate to SDK's subscribe mechanism
        sub = payload
        ws_manager.subscribe(sub, lambda d: print(f"WS msg: {d}"))
        return {"status": "subscribed", "type": sub_type}


if __name__ == "__main__":
    import uvicorn
    port = int(os.environ.get("HYPERLIQUID_SERVER_PORT", "8765"))
    # Holds a live signing key with unauthenticated order-placement endpoints — default to
    # localhost-only so it isn't reachable from the LAN. HyperliquidClient.DefaultBaseUrl already
    # points at localhost, so the only caller that needs this is on the same machine.
    host = os.environ.get("HYPERLIQUID_SERVER_HOST", "127.0.0.1")
    logger.info(f"Starting Hyperliquid bridge on {host}:{port} (mainnet={IS_MAINNET})")
    uvicorn.run(app, host=host, port=port)
