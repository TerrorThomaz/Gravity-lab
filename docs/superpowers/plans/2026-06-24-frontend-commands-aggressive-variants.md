# Frontend Commands + Aggressive Variants Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add all CLI commands to the Training page frontend, fix fitness weight submission, add fitness profile presets, and register an `aggressive` draft variant for every strategy.

**Architecture:** Backend gets a second process slot (`_run_proc`) and four new endpoints under `/api/run` for non-training commands. The Training page gains a FITNESS PROFILE preset selector (which also fixes the incomplete `fitnessConfig` sent to the backend), a COMMANDS section in the left rail for one-click backtest/analysis runs, and an `aggressive` variant appears in every strategy's variant list via `store.js`.

**Tech Stack:** Python/FastAPI (api_server.py), vanilla JS + React (JSX via Babel, no build step), CSS custom properties.

## Global Constraints

- All frontend files use the IBM Plex Mono / IBM Plex Sans fonts and `oklch` CSS colour tokens already defined in each HTML `<style>` block — add new CSS using the same tokens (`var(--bg)`, `var(--panel)`, etc.).
- React is loaded from CDN as `React` / `ReactDOM` globals; JSX is transpiled by `@babel/standalone` in-browser. No `import` statements.
- The API server is started with `python3 -m uvicorn bot.api_server:app --port 8080` from the repo root. Restart it to pick up Python changes.
- No C# changes in this plan — `FitnessConfig` already has all required fields.
- Test endpoints with `curl http://localhost:8080/...` (server must be running).

---

## File Map

| File | What changes |
|------|-------------|
| `bot/api_server.py` | Add `_run_proc`/`_run_info` globals, `RUN_COMMANDS` dict, `RunRequest` model, and four endpoints: `POST /api/run`, `GET /api/run/stream`, `POST /api/run/stop`, `GET /api/run/status`. Update `_live_monitor_loop` to gate on `_run_proc`. |
| `frontend/store.js` | Add `aggressive` variant entry to each of the 5 strategies in `VARIANTS`. |
| `frontend/Gravity Training.html` | (1) Fix `startTraining` to send all 10 fitness weights. (2) Add `FW_PRESETS`, `activePreset` state, preset button CSS + JSX. (3) Add `runStatus`/`runCmd` state, `runESRef`, `startRun`/`stopRun` functions, command button CSS + JSX section, updated status bar + log label. |

---

### Task 1: Backend — `/api/run` infrastructure

**Files:**
- Modify: `bot/api_server.py`

**Interfaces:**
- Produces:
  - `POST /api/run` body `{ "command": str }` → `{ "status": "started", "pid": int }` or 400/409
  - `GET /api/run/stream` → SSE, lines `data: <text>\n\n`, ends `data: [DONE]\n\n`
  - `POST /api/run/stop` → `{ "status": "stopped" | "not running" }`
  - `GET /api/run/status` → `{ "running": bool, "command": str|null, "startedAt": str|null, "lastLine": str, "lastError": str|null }`

- [ ] **Step 1: Read the file to understand insertion points**

  Open `bot/api_server.py`. The existing globals are near the top (lines ~17–28). `STRATEGY_COMMANDS` is at lines ~43–49. `TrainRequest` model is at ~51–54. Endpoints start at ~265. `_live_monitor_loop` is at ~215. Note these positions before editing.

- [ ] **Step 2: Add `_run_proc` globals and `RUN_COMMANDS` dict**

  In `bot/api_server.py`, after the `_live_proc` / `_live_info` block (around line 27), add:

  ```python
  # ── Run command subprocess state (backtests, analysis, system trains) ─────
  _run_proc: subprocess.Popen | None = None
  _run_info: dict = {"status": "idle", "command": None, "startedAt": None, "lastLine": "", "lastError": None}
  ```

  After `STRATEGY_COMMANDS` (around line 49), add:

  ```python
  RUN_COMMANDS = {
      # System trains (no variant/fitnessConfig concept)
      "routertrain":        "routertrain",
      "coevolvetrain":      "coevolvetrain",
      "dynamicguardtrain":  "dynamicguardtrain",
      "retrain":            "retrain",
      # Backtests
      "backtest":           "backtest",
      "gridbacktest":       "gridbacktest",
      "combinedbacktest":   "combinedbacktest",
      "oosbacktest":        "oosbacktest",
      "allcoinsbacktest":   "allcoinsbacktest",
      "yearlybreakdown":    "yearlybreakdown",
      # Analysis
      "fulltest":           "fulltest",
      "test":               "test",
  }
  ```

- [ ] **Step 3: Add `RunRequest` model**

  After the `TrainRequest` model (around line 54), add:

  ```python
  class RunRequest(BaseModel):
      command: str
  ```

- [ ] **Step 4: Add the four `/api/run` endpoints**

  After the `GET /api/status` endpoint (around line 333), add:

  ```python
  @app.post("/api/run")
  async def start_run(req: RunRequest):
      global _run_proc, _run_info, _live_paused
      if _run_proc is not None and _run_proc.poll() is None:
          raise HTTPException(409, "Command already running")
      if _proc is not None and _proc.poll() is None:
          raise HTTPException(409, "Training in progress")
      if req.command not in RUN_COMMANDS:
          raise HTTPException(400, f"Unknown command: {req.command!r}")

      _live_paused = True
      _stop_live_proc()

      cmd = ["dotnet", "run", "--", RUN_COMMANDS[req.command]]
      _run_proc = subprocess.Popen(
          cmd, cwd=str(ROOT), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
          text=True, bufsize=1, encoding="utf-8", errors="replace",
      )
      _run_info = {
          "status":    "running",
          "command":   req.command,
          "startedAt": datetime.utcnow().isoformat(),
          "lastLine":  "",
          "lastError": None,
      }
      return {"status": "started", "pid": _run_proc.pid}


  @app.get("/api/run/stream")
  async def stream_run():
      async def generate():
          global _live_paused
          if _run_proc is None:
              yield "data: No command running\n\n"
              return
          loop = asyncio.get_event_loop()
          while True:
              line = await loop.run_in_executor(None, _run_proc.stdout.readline)
              if not line:
                  _run_info["status"] = "done"
                  _live_paused = False
                  yield "data: [DONE]\n\n"
                  break
              stripped = line.rstrip()
              _run_info["lastLine"] = stripped
              yield f"data: {stripped}\n\n"
      return StreamingResponse(generate(), media_type="text/event-stream")


  @app.post("/api/run/stop")
  async def stop_run():
      global _run_proc, _live_paused
      if _run_proc is None or _run_proc.poll() is not None:
          return {"status": "not running"}
      _run_proc.terminate()
      try:
          _run_proc.wait(timeout=5)
      except subprocess.TimeoutExpired:
          _run_proc.kill()
      _run_proc = None
      _run_info["status"] = "stopped"
      _live_paused = False
      return {"status": "stopped"}


  @app.get("/api/run/status")
  async def get_run_status():
      running = _run_proc is not None and _run_proc.poll() is None
      return {"running": running, **_run_info}
  ```

- [ ] **Step 5: Gate `_live_monitor_loop` on `_run_proc`**

  In `_live_monitor_loop` (around line 231), after the existing `_proc` check, add a `_run_proc` check:

  ```python
  # existing check:
  if _proc is not None and _proc.poll() is None:
      print("[live] training in progress, deferring restart", flush=True)
      await asyncio.sleep(60)
      continue
  # ADD this block right after:
  if _run_proc is not None and _run_proc.poll() is None:
      print("[live] command running, deferring restart", flush=True)
      await asyncio.sleep(60)
      continue
  ```

- [ ] **Step 6: Restart server and smoke-test with curl**

  ```bash
  # terminal 1 — restart server
  python3 -m uvicorn bot.api_server:app --port 8080 --reload

  # terminal 2 — test unknown command (expect 400)
  curl -s -X POST http://localhost:8080/api/run \
    -H "Content-Type: application/json" \
    -d '{"command":"notreal"}' | python3 -m json.tool
  # Expected: {"detail":"Unknown command: 'notreal'"}

  # test status (expect idle)
  curl -s http://localhost:8080/api/run/status | python3 -m json.tool
  # Expected: {"running":false,"status":"idle","command":null,...}

  # test stop when nothing running (expect not running)
  curl -s -X POST http://localhost:8080/api/run/stop | python3 -m json.tool
  # Expected: {"status":"not running"}
  ```

- [ ] **Step 7: Commit**

  ```bash
  git add bot/api_server.py
  git commit -m "feat: /api/run endpoint — backtest + analysis commands from frontend"
  ```

---

### Task 2: `store.js` — `aggressive` variant for all strategies

**Files:**
- Modify: `frontend/store.js`

**Interfaces:**
- Produces: Each strategy's `VARIANTS` array gains an `{ id:'aggressive', ... }` entry with `file:null`, `active:false`, `tags:['high-freq','draft']`.
- Consumed by: Training page variant selector (reads `GravStore.state.variants[stratKey]`).

- [ ] **Step 1: Add `aggressive` variant to `FadeShort`**

  In `frontend/store.js` inside the `VARIANTS` object, locate the `FadeShort` array. After the `loval` entry (last entry), add:

  ```js
  { id:'aggressive', label:'Aggressive', file:null, active:false,
    tags:['high-freq','draft'], atrRange:[0, 9999],
    notes:'Higher-frequency short fade. Train with Aggressive preset: lower RSI threshold, wider stops, reduced DD penalty. GA explores less conservative parameter space.' },
  ```

- [ ] **Step 2: Add `aggressive` variant to `Grid`**

  In the `Grid` array, after the `tight` entry, add:

  ```js
  { id:'aggressive', label:'Aggressive', file:null, active:false,
    tags:['high-freq','draft'], atrRange:[0, 9999],
    notes:'Smaller step size and tighter grid spacing — more positions at once. Train with Aggressive preset.' },
  ```

- [ ] **Step 3: Add `aggressive` variant to `SwingLong`**

  In the `SwingLong` array, after the `bull_run` entry, add:

  ```js
  { id:'aggressive', label:'Aggressive', file:null, active:false,
    tags:['bull','high-freq','draft'], atrRange:[0, 9999],
    notes:'Lower divergence threshold and looser BoS confirmation. More signals in borderline setups. Train with Aggressive preset.' },
  ```

- [ ] **Step 4: Add `aggressive` variant to `DipLong`**

  In the `DipLong` array, after the `deep` entry, add:

  ```js
  { id:'aggressive', label:'Aggressive', file:null, active:false,
    tags:['bull','high-freq','draft'], atrRange:[0, 9999],
    notes:'Wider RSI dip band and lower trend confirmation requirement. Train with Aggressive preset.' },
  ```

- [ ] **Step 5: Add `aggressive` variant to `FadeLong`**

  In the `FadeLong` array, after the `cascade` entry, add:

  ```js
  { id:'aggressive', label:'Aggressive', file:null, active:false,
    tags:['bear','high-freq','draft'], atrRange:[0, 9999],
    notes:'Fires on shallower bear oversold bounces. Train with Aggressive preset.' },
  ```

- [ ] **Step 6: Verify in browser**

  Open `http://localhost:8080/Gravity%20Training.html`, select each strategy in turn, and confirm "Aggressive" appears in the variant list with a `draft` label and no green dot.

- [ ] **Step 7: Commit**

  ```bash
  git add frontend/store.js
  git commit -m "feat: aggressive draft variant registered for all 5 strategies"
  ```

---

### Task 3: Training page — fix `fitnessConfig` submission + fitness profile presets

**Files:**
- Modify: `frontend/Gravity Training.html`

**Interfaces:**
- Consumes: `fw` state — already has all 10 keys: `gainW`, `wrW`, `qualityW`, `freqW`, `ddPenalty`, `retentionW`, `sharpeW`, `calmarW`, `pfW`, `sortinoW`.
- Produces: `fitnessConfig` object now includes all 10 weights (mapped to PascalCase for C# `FitnessConfig`). New `FW_PRESETS` constant. New `activePreset` state. New preset-button CSS classes + JSX section.

**Bug fixed here:** `startTraining` currently only sends 4 of the 10 `fw` values. The other 6 (`GainW`, `WrW`, `QualityW`, `FreqW`, `DdPenalty`, `RetentionW`) are never sent, so the C# GA always uses its `FitnessConfig` defaults regardless of slider position.

- [ ] **Step 1: Fix `startTraining` to send all 10 weights**

  Locate the `fitnessConfig` object literal inside `startTraining` (around line 262). Replace:

  ```js
  const fitnessConfig = {
    VariantId:  variantId,
    SharpeW:    fw.sharpeW,
    CalmarW:    fw.calmarW,
    PfW:        fw.pfW,
    SortinoW:   fw.sortinoW,
    AtrLow:     activeVar?.atrRange?.[0] ?? 0,
    AtrHigh:    activeVar?.atrRange?.[1] ?? 9999,
  };
  ```

  With:

  ```js
  const fitnessConfig = {
    VariantId:   variantId,
    GainW:       fw.gainW,
    WrW:         fw.wrW,
    QualityW:    fw.qualityW,
    FreqW:       fw.freqW,
    DdPenalty:   fw.ddPenalty,
    RetentionW:  fw.retentionW,
    SharpeW:     fw.sharpeW,
    CalmarW:     fw.calmarW,
    PfW:         fw.pfW,
    SortinoW:    fw.sortinoW,
    AtrLow:      activeVar?.atrRange?.[0] ?? 0,
    AtrHigh:     activeVar?.atrRange?.[1] ?? 9999,
  };
  ```

- [ ] **Step 2: Add `FW_PRESETS` constant**

  After `DEFAULT_FW` (around line 186), add:

  ```js
  const FW_PRESETS = {
    conservative: { gainW:1.0, wrW:0.9, qualityW:0.8, freqW:0.6, ddPenalty:1.2, retentionW:1.0, sharpeW:0.0, calmarW:0.0, pfW:0.0, sortinoW:0.0 },
    balanced:     { gainW:1.0, wrW:0.7, qualityW:0.6, freqW:0.8, ddPenalty:1.0, retentionW:0.7, sharpeW:0.3, calmarW:0.3, pfW:0.2, sortinoW:0.0 },
    aggressive:   { gainW:1.3, wrW:0.4, qualityW:0.3, freqW:1.2, ddPenalty:0.7, retentionW:0.4, sharpeW:0.0, calmarW:0.0, pfW:0.0, sortinoW:0.0 },
  };
  ```

- [ ] **Step 3: Add `activePreset` state**

  In `TrainingApp`, after `const [fw, setFw] = useState(DEFAULT_FW);`, add:

  ```js
  const [activePreset, setActivePreset] = useState('');
  ```

- [ ] **Step 4: Reset `activePreset` when a slider moves**

  The existing `setFwP` helper is:
  ```js
  const setFwP = (k,v) => setFw(f=>({...f,[k]:v}));
  ```
  Replace with:
  ```js
  const setFwP = (k,v) => { setFw(f=>({...f,[k]:v})); setActivePreset(''); };
  ```

- [ ] **Step 5: Add preset button CSS**

  In the `<style>` block, after `.gt-fw-formula` rule (around line 85), add:

  ```css
  /* fitness profile presets */
  .gt-preset-row{display:flex;gap:5px;margin-bottom:12px;}
  .gt-preset-btn{flex:1;padding:5px 4px;font-family:var(--mono);font-size:10px;
    background:var(--panel-2);border:1px solid var(--line);border-radius:3px;
    color:var(--ink-dim);cursor:pointer;letter-spacing:.03em;transition:all .12s;}
  .gt-preset-btn:hover{border-color:var(--line);color:var(--ink);}
  .gt-preset-btn.on{border-color:var(--accent);color:var(--accent);background:var(--accent-s);}
  ```

- [ ] **Step 6: Add preset buttons JSX**

  In the FITNESS WEIGHTS section JSX (around line 396), replace the opening `<div className="gt-sect">` block that starts with `<div className="gt-sect-title">FITNESS WEIGHTS</div>` to insert the preset row **before** the formula div:

  ```jsx
  {/* fitness weights */}
  <div className="gt-sect">
    <div className="gt-sect-title">FITNESS WEIGHTS</div>
    {/* profile presets */}
    <div className="gt-preset-row">
      {['conservative','balanced','aggressive'].map(p=>(
        <button key={p} className={"gt-preset-btn"+(activePreset===p?' on':'')}
          onClick={()=>{ setFw(FW_PRESETS[p]); setActivePreset(p); }}>
          {p[0].toUpperCase()+p.slice(1)}
        </button>
      ))}
    </div>
    <div className="gt-fw-formula">
      {/* keep existing formula div content unchanged */}
    ```

  The rest of the section (formula div, sliders) stays exactly as is.

- [ ] **Step 7: Verify preset buttons in browser**

  Open `http://localhost:8080/Gravity%20Training.html`. In the FITNESS WEIGHTS section:
  - Click "Aggressive" → Frequency bonus slider should jump to 1.2, DD penalty to 0.7, "Aggressive" button highlighted.
  - Move any slider → button highlight clears.
  - Click "Conservative" → sliders reset to `gainW:1.0, ddPenalty:1.2` etc.

- [ ] **Step 8: Commit**

  ```bash
  git add "frontend/Gravity Training.html"
  git commit -m "feat: fix fitnessConfig submission + fitness profile presets (Conservative/Balanced/Aggressive)"
  ```

---

### Task 4: Training page — Commands section + run stream

**Files:**
- Modify: `frontend/Gravity Training.html`

**Interfaces:**
- Consumes: `POST /api/run`, `GET /api/run/stream`, `POST /api/run/stop` (from Task 1).
- Produces: New `runStatus` / `runCmd` state, `startRun(cmd)` / `stopRun()` functions, COMMANDS section JSX in left rail, updated status bar and log header.

- [ ] **Step 1: Add run state + ref**

  In `TrainingApp`, after `const [log, setLog] = useState([]);`, add:

  ```js
  const [runStatus, setRunStatus] = useState('idle'); // idle | running | done
  const [runCmd, setRunCmd] = useState('');
  const runESRef = useRef(null);
  ```

- [ ] **Step 2: Add `startRun` function**

  After `stopTraining` (around line 317), add:

  ```js
  async function startRun(cmd) {
    setRunStatus('running');
    setRunCmd(cmd);
    setLog([]);
    try {
      await fetch('/api/run', {
        method: 'POST',
        headers: {'Content-Type':'application/json'},
        body: JSON.stringify({command: cmd}),
      });
    } catch {}
    if (runESRef.current) runESRef.current.close();
    const es = new EventSource('/api/run/stream');
    runESRef.current = es;
    es.onmessage = e => {
      if (e.data === '[DONE]') { es.close(); runESRef.current = null; setRunStatus('done'); return; }
      setLog(prev => [...prev.slice(-80), `[${ts()}] ${e.data}`]);
    };
    es.onerror = () => { es.close(); runESRef.current = null; setRunStatus('done'); };
  }

  function stopRun() {
    if (runESRef.current) { runESRef.current.close(); runESRef.current = null; }
    fetch('/api/run/stop', {method:'POST'}).catch(()=>{});
    setRunStatus('idle');
  }
  ```

- [ ] **Step 3: Add command button CSS**

  In the `<style>` block, after the `.gt-preset-btn` rules added in Task 3, add:

  ```css
  /* run commands panel */
  .gt-run-cat{font-family:var(--mono);font-size:9px;color:var(--ink-faint);
    font-weight:700;letter-spacing:.10em;margin:8px 0 4px;}
  .gt-run-grid{display:grid;grid-template-columns:1fr 1fr;gap:4px;margin-bottom:4px;}
  .gt-run-btn{padding:5px 7px;font-family:var(--mono);font-size:10px;
    background:var(--panel-2);border:1px solid var(--line);border-radius:3px;
    color:var(--ink-dim);cursor:pointer;text-align:left;letter-spacing:.01em;
    transition:all .12s;line-height:1.3;}
  .gt-run-btn:hover:not(:disabled){border-color:var(--accent);color:var(--ink);}
  .gt-run-btn:disabled{opacity:.35;cursor:not-allowed;}
  .gt-run-btn.active{color:var(--pos);border-color:color-mix(in oklch,var(--pos),transparent 50%);
    background:color-mix(in oklch,var(--pos),transparent 88%);}
  ```

- [ ] **Step 4: Define `RUN_CMD_GROUPS` constant**

  After `FW_PRESETS` (from Task 3 Step 2), add:

  ```js
  const RUN_CMD_GROUPS = [
    { cat: 'SYSTEM TRAIN', cmds: [
      { key:'routertrain',        lbl:'Router train' },
      { key:'coevolvetrain',      lbl:'Coevolve' },
      { key:'dynamicguardtrain',  lbl:'Guard train' },
      { key:'retrain',            lbl:'Retrain OOS' },
    ]},
    { cat: 'BACKTEST', cmds: [
      { key:'backtest',           lbl:'FadeShort BT' },
      { key:'gridbacktest',       lbl:'Grid BT' },
      { key:'combinedbacktest',   lbl:'Combined BT' },
      { key:'oosbacktest',        lbl:'OOS BT' },
      { key:'allcoinsbacktest',   lbl:'All coins BT' },
      { key:'yearlybreakdown',    lbl:'Yearly breakdown' },
    ]},
    { cat: 'ANALYSIS', cmds: [
      { key:'fulltest',           lbl:'Full test' },
      { key:'test',               lbl:'Stat test' },
    ]},
  ];
  ```

- [ ] **Step 5: Add COMMANDS section JSX**

  In the left `<aside className="gt-ctrl">`, after the COMMAND PREVIEW section (the closing `</div>` around line 438), add a new section:

  ```jsx
  {/* commands */}
  <div className="gt-sect">
    <div className="gt-sect-title">COMMANDS</div>
    {RUN_CMD_GROUPS.map(group=>(
      <div key={group.cat}>
        <div className="gt-run-cat">{group.cat}</div>
        <div className="gt-run-grid">
          {group.cmds.map(c=>(
            <button key={c.key}
              className={"gt-run-btn"+(runCmd===c.key&&runStatus==='running'?' active':'')}
              disabled={status==='running'||runStatus==='running'}
              onClick={runCmd===c.key&&runStatus==='running'?stopRun:()=>startRun(c.key)}>
              {c.lbl}
              {runCmd===c.key&&runStatus==='running'&&<span style={{float:'right',fontSize:9}}>■</span>}
            </button>
          ))}
        </div>
      </div>
    ))}
  </div>
  ```

- [ ] **Step 6: Update the status bar**

  Locate the status bar `<span className="gt-status-lbl">` (around line 447). Replace its content:

  ```jsx
  {runStatus==='running'
    ? `RUNNING ${runCmd.toUpperCase()}…`
    : status==='idle'
      ? 'IDLE · ready to train'
      : status==='running'
        ? `TRAINING ${stratKey.toUpperCase()} · ${activeVar?.label||'default'} · cycle ${cycle}`
        : `COMPLETE · cycle ${cycle}`
  }
  ```

  Update the status dot class:

  ```jsx
  <span className={"gt-status-dot "+(runStatus==='running'?'running':status)} />
  ```

- [ ] **Step 7: Update the log header + disable TRAIN when run is active**

  Locate the log section (around line 524). Change:
  ```jsx
  <div className="gt-log-head">TRAINING LOG</div>
  ```
  To:
  ```jsx
  <div className="gt-log-head">{runStatus==='running'?`OUTPUT · ${runCmd}`:'TRAINING LOG'}</div>
  ```

  Locate the `▶ START TRAINING` button (around line 451). Add `disabled`:
  ```jsx
  <button className={"gt-start-btn "+status}
    disabled={runStatus==='running'}
    onClick={status==='running'?stopTraining:startTraining}>
    {status==='running'?'■ STOP TRAINING':'▶ START TRAINING'}
  </button>
  ```

- [ ] **Step 8: End-to-end test**

  With the server running:
  1. Open `http://localhost:8080/Gravity%20Training.html`
  2. Confirm COMMANDS section appears at the bottom of the left rail with three categories
  3. Click **"Stat test"** (fast command). Status bar should change to `RUNNING TEST…`, the log fills with output, all other command buttons are disabled.
  4. Wait for `[DONE]` — status bar reverts to IDLE, buttons re-enable.
  5. Select FadeShort + Aggressive variant + click "Aggressive" preset → sliders update → click START TRAINING → verify the backend log shows `FitnessConfig` with `FreqW=1.2`, `GainW=1.3` (check server stdout).
  6. While training, verify COMMANDS buttons are disabled.

- [ ] **Step 9: Commit**

  ```bash
  git add "frontend/Gravity Training.html"
  git commit -m "feat: Commands section + run stream — all CLI commands callable from Training page"
  ```

---

## Self-Review

**Spec coverage:**
- ✅ All CLI commands callable from frontend — Task 1 (backend) + Task 4 (UI)
- ✅ Backtest commands: `backtest`, `gridbacktest`, `combinedbacktest`, `oosbacktest`, `allcoinsbacktest`, `yearlybreakdown` in `RUN_COMMANDS`
- ✅ Analysis: `fulltest`, `test` in `RUN_COMMANDS`
- ✅ System trains: `routertrain`, `coevolvetrain`, `dynamicguardtrain`, `retrain` in `RUN_COMMANDS`
- ✅ `aggressive` variant for all 5 strategies — Task 2
- ✅ Fitness profile presets (Conservative / Balanced / Aggressive) — Task 3
- ✅ `fitnessConfig` bug fixed — Task 3 Step 1
- ✅ Mutual exclusion (409 if training blocks run, run blocks training) — Task 1 Step 4
- ✅ Papertrade auto-deferred during runs — Task 1 Step 5

**No placeholders found.**

**Type consistency:** `fw.gainW` → `GainW` (PascalCase, matches C# `FitnessConfig` record field names). `RUN_CMD_GROUPS[*].key` values match `RUN_COMMANDS` dict keys exactly. `runStatus` string literals (`'idle'`, `'running'`, `'done'`) used consistently across state, JSX, and handler functions.
