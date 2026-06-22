# bot/api_server.py
import asyncio, json, os, re, subprocess, sys
from datetime import datetime
from pathlib import Path
from fastapi import FastAPI, HTTPException
from fastapi.responses import StreamingResponse
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

ROOT = Path(__file__).parent.parent   # repo root
app  = FastAPI()

# ── Training subprocess state ─────────────────────────────────────────────────
_proc:      subprocess.Popen | None = None
_proc_info: dict = {}

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

    # Write fitness_config.json
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
            _proc_info["lastLine"] = line.rstrip()
            yield f"data: {line.rstrip()}\n\n"
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

# ── Static files (frontend) — mount last so API routes take priority ──────────
app.mount("/", StaticFiles(directory=str(ROOT / "frontend"), html=True), name="frontend")
