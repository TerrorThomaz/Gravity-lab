# bot/api_server.py
import asyncio, json, os, re, subprocess, sys
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
from fastapi import FastAPI, HTTPException
from fastapi.responses import StreamingResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

ROOT = Path(__file__).parent.parent   # repo root
BASELINE_PATH    = ROOT / "backtest_baseline.json"
FULLTEST_PATH    = ROOT / "fulltest_results.json"
REFRESH_INTERVAL = 6 * 3600  # run fulltest every 6 hours

# ── Training subprocess state ─────────────────────────────────────────────────
_proc:      subprocess.Popen | None = None
_proc_info: dict = {}

# ── Periodic baseline state ──────────────────────────────────────────────────
_baseline_proc: asyncio.subprocess.Process | None = None
_baseline_info: dict = {"status": "idle", "lastRun": None, "lastError": None}

STRATEGY_COMMANDS = {
    "FadeShort":  "train",
    "Grid":       "gridtrain",
    "SwingLong":  "swinglongtrain",
    "DipLong":    "diplongtrain",
    "FadeLong":   "fadelongtrain",
}

class TrainRequest(BaseModel):
    strategy:      str
    variant:       str = "default"
    fitnessConfig: dict = {}

# ── Parse structured progress from GA stdout lines ──────────────────────────
_GEN_RE = re.compile(r'Gen\s+(\d+)\s.*?F=([\d,.-]+)')

def _parse_progress(line: str) -> str | None:
    """Try to extract structured JSON from a GA log line."""
    m = _GEN_RE.search(line)
    if m:
        gen = int(m.group(1))
        fit = float(m.group(2).replace(',', '.'))
        prog_path = ROOT / "training_progress.json"
        extra = {}
        try:
            prog = json.loads(prog_path.read_text())
            if prog.get("generation", -1) == gen:
                extra["mean"] = prog.get("meanFitness", 0)
                extra["population"] = prog.get("population", [])
        except Exception:
            pass
        return json.dumps({"cycle": gen, "best": fit, "message": line, **extra})
    return None

# ── Periodic fulltest → baseline refresh ─────────────────────────────────────
_SHARPE_RE  = re.compile(r'Portfolio\s+Sharpe.*?([\d,.]+)', re.I)
_TRADES_RE  = re.compile(r'Total\s+trades.*?(\d+)', re.I)
_WR_RE      = re.compile(r'Win.?rate.*?([\d,.]+)%', re.I)
_AVG_RE     = re.compile(r'Avg.*?ret.*?([+-]?[\d,.]+)%', re.I)

def _run_fulltest_sync() -> tuple[int, str]:
    """Run fulltest in a blocking subprocess (called from executor)."""
    print("[baseline] subprocess starting: dotnet run -- fulltest", flush=True)
    try:
        proc = subprocess.Popen(
            ["dotnet", "run", "--", "fulltest"],
            cwd=str(ROOT), stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
        )
    except Exception as exc:
        print(f"[baseline] Popen failed: {exc}", flush=True)
        return 1, str(exc)
    print(f"[baseline] subprocess pid={proc.pid}, waiting…", flush=True)
    output, _ = proc.communicate()
    print(f"[baseline] subprocess done, code={proc.returncode}, output={len(output)} chars", flush=True)
    return proc.returncode, output

async def _run_fulltest():
    """Run `dotnet run -- fulltest` and update backtest_baseline.json from results."""
    global _baseline_proc
    if _baseline_info["status"] == "running":
        print("[baseline] skipping — previous run still active")
        return

    if _proc is not None and _proc.poll() is None:
        print("[baseline] skipping — training in progress")
        return

    print(f"[baseline] starting fulltest at {datetime.now(timezone.utc).isoformat()}")
    _baseline_info["status"] = "running"
    try:
        loop = asyncio.get_event_loop()
        returncode, output = await loop.run_in_executor(None, _run_fulltest_sync)
        print(f"[baseline] fulltest exited code={returncode}")

        if returncode == 0:
            # Prefer structured fulltest_results.json if it was written
            baseline = _read_fulltest_json()
            if baseline is None:
                baseline = _parse_fulltest_stdout(output)
            baseline["timestamp"] = datetime.now(timezone.utc).isoformat()
            BASELINE_PATH.write_text(json.dumps(baseline, indent=2))
            _baseline_info["lastRun"] = baseline["timestamp"]
            _baseline_info["lastError"] = None
            print(f"[baseline] updated — sharpe={baseline.get('sharpe')} trades={baseline.get('trades')}")
        else:
            _baseline_info["lastError"] = output[-500:] if len(output) > 500 else output
            print(f"[baseline] fulltest failed:\n{output[-300:]}")
    except Exception as exc:
        import traceback
        _baseline_info["lastError"] = str(exc)
        print(f"[baseline] error: {exc}")
        traceback.print_exc()
    finally:
        _baseline_info["status"] = "idle"

def _read_fulltest_json() -> dict | None:
    """Read fulltest_results.json and extract baseline fields."""
    try:
        data = json.loads(FULLTEST_PATH.read_text())
        bt = data.get("backtest", {})
        return {
            "sharpe":   bt.get("sharpe", 0),
            "trades":   bt.get("trades", 0),
            "win_rate": f"{bt.get('winRate', 0)}%",
            "avg_ret":  "—",
            "maxDD":    bt.get("maxDD", 0),
            "cagr":     bt.get("cagr", 0),
        }
    except Exception:
        return None

def _parse_fulltest_stdout(output: str) -> dict:
    """Fallback: parse Sharpe/trades from stdout if JSON wasn't written."""
    sharpe = trades = 0
    win_rate = avg_ret = "—"
    for line in output.splitlines():
        m = _SHARPE_RE.search(line)
        if m: sharpe = float(m.group(1).replace(',', '.'))
        m = _TRADES_RE.search(line)
        if m: trades = int(m.group(1))
        m = _WR_RE.search(line)
        if m: win_rate = m.group(1).replace(',', '.') + '%'
        m = _AVG_RE.search(line)
        if m: avg_ret = m.group(1).replace(',', '.') + '%'
    return {"sharpe": sharpe, "trades": trades, "win_rate": win_rate, "avg_ret": avg_ret}

async def _baseline_loop():
    """Background loop: run fulltest periodically."""
    print("[baseline] loop started, waiting 5s…", flush=True)
    await asyncio.sleep(5)
    while True:
        print("[baseline] loop tick — calling _run_fulltest", flush=True)
        await _run_fulltest()
        print(f"[baseline] next run in {REFRESH_INTERVAL}s", flush=True)
        await asyncio.sleep(REFRESH_INTERVAL)

# ── App lifecycle ────────────────────────────────────────────────────────────
@asynccontextmanager
async def lifespan(app):
    print("[lifespan] starting baseline loop", flush=True)
    task = asyncio.create_task(_baseline_loop())
    yield
    task.cancel()
    try:
        await task
    except asyncio.CancelledError:
        pass

app = FastAPI(lifespan=lifespan)

# ── API endpoints ─────────────────────────────────────────────────────────────
@app.post("/api/train")
async def start_training(req: TrainRequest):
    global _proc, _proc_info
    if _proc and _proc.poll() is None:
        raise HTTPException(409, "Training already in progress")
    if req.strategy not in STRATEGY_COMMANDS:
        raise HTTPException(400, f"Unknown strategy: {req.strategy}")
    if not re.match(r'^[a-zA-Z0-9_-]+$', req.variant):
        raise HTTPException(400, f"Invalid variant id: {req.variant!r}")

    cfg_path = ROOT / "fitness_config.json"
    cfg_path.write_text(json.dumps(req.fitnessConfig))

    cmd = ["dotnet", "run", "--", STRATEGY_COMMANDS[req.strategy], "--variant", req.variant]
    env = {**os.environ, "GRAVITY_VARIANT": req.variant}
    _proc = subprocess.Popen(
        cmd, cwd=str(ROOT), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, bufsize=1, env=env
    )
    _proc_info = {
        "strategy":  req.strategy,
        "variant":   req.variant,
        "startedAt": datetime.utcnow().isoformat(),
        "lastLine":  "",
    }
    return {"status": "started", "pid": _proc.pid}

@app.get("/api/train/stream")
async def stream_training():
    async def generate():
        if _proc is None:
            yield "data: No training process running\n\n"
            return
        loop = asyncio.get_event_loop()
        while True:
            line = await loop.run_in_executor(None, _proc.stdout.readline)
            if not line:
                yield "data: [DONE]\n\n"
                break
            stripped = line.rstrip()
            _proc_info["lastLine"] = stripped
            structured = _parse_progress(stripped)
            if structured:
                yield f"data: {structured}\n\n"
            else:
                yield f"data: {stripped}\n\n"
    return StreamingResponse(generate(), media_type="text/event-stream")

@app.post("/api/stop")
async def stop_training():
    global _proc
    if _proc is None or _proc.poll() is not None:
        return {"status": "not running"}
    _proc.terminate()
    try:
        _proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        _proc.kill()
    _proc = None
    return {"status": "stopped"}

@app.get("/api/status")
async def get_status():
    running = _proc is not None and _proc.poll() is None
    return {"running": running, **_proc_info}

@app.get("/api/baseline")
async def get_baseline():
    """Return current baseline + refresh status."""
    baseline = {}
    try:
        baseline = json.loads(BASELINE_PATH.read_text())
    except Exception:
        pass
    return {**baseline, "refresh": _baseline_info}

@app.post("/api/baseline/refresh")
async def trigger_baseline_refresh():
    """Manually trigger a fulltest refresh."""
    if _baseline_info["status"] == "running":
        raise HTTPException(409, "Baseline refresh already running")
    asyncio.create_task(_run_fulltest())
    return {"status": "started"}

# ── Serve repo-root JSON files and genotypes so ../X.json resolves ────────────
from fastapi.responses import FileResponse

SAFE_ROOT_FILES = {
    "backtest_baseline.json", "backtest_results.json", "fulltest_results.json",
    "livetrain_state.json", "live_journal.json", "training_progress.json",
}

@app.get("/{filename:path}.json")
async def serve_root_json(filename: str):
    """Serve whitelisted JSON from repo root, or genotypes/*.json."""
    name = filename + ".json"
    # genotypes/X.json
    if filename.startswith("genotypes/"):
        safe = re.match(r'^genotypes/[\w_.-]+$', filename)
        if safe:
            path = ROOT / name
            if path.is_file():
                return FileResponse(path, media_type="application/json")
    # root-level JSON
    elif name in SAFE_ROOT_FILES:
        path = ROOT / name
        if path.is_file():
            return FileResponse(path, media_type="application/json")
    raise HTTPException(404, "Not found")

# ── Static files (frontend) — mount last so API routes take priority ──────────
app.mount("/", StaticFiles(directory=str(ROOT / "frontend"), html=True), name="frontend")
