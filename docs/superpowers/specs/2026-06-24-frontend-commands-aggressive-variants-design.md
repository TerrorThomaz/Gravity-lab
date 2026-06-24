# Frontend Commands + Aggressive Variants — Design

**Date**: 2026-06-24
**Status**: Approved

## Goals

1. Every CLI command that can be run in the terminal is also triggerable from the frontend.
2. All data shown in the frontend is real (not mock/placeholder).
3. Each strategy gets an `aggressive` variant with a fitness config that prioritises frequency and raw gain over drawdown control, so the GA can evolve less conservative parameters (wider stops, lower entry thresholds, higher sizing).

## Scope

Out of scope: wiring the GA parameter sliders (popSize, mutation rate, etc.) to the backend — those require C# changes and are deferred. The sliders remain visible as reference.

---

## 1. Backend — `bot/api_server.py`

### 1a. Extend training commands

Add four missing strategies to `STRATEGY_COMMANDS`:

| Key | CLI arg |
|-----|---------|
| `Router` | `routertrain` |
| `Coevolve` | `coevolvetrain` |
| `DynamicGuard` | `dynamicguardtrain` |
| `Retrain` | `retrain` |

These use the same `/api/train` POST and `/api/train/stream` SSE endpoints as existing strategies. The `variant` field is ignored for these (always `default`); `fitnessConfig` is still written and available for future use.

### 1b. New `/api/run` endpoint (backtest + analysis commands)

A second process slot — `_run_proc` / `_run_info` — separate from `_proc` (training). The two slots are mutually exclusive: starting a run while training (or vice versa) returns 409.

**Whitelisted run commands** (key → CLI arg):

| Key | CLI arg |
|-----|---------|
| `backtest` | `backtest` |
| `gridbacktest` | `gridbacktest` |
| `combinedbacktest` | `combinedbacktest` |
| `oosbacktest` | `oosbacktest` |
| `allcoinsbacktest` | `allcoinsbacktest` |
| `yearlybreakdown` | `yearlybreakdown` |
| `fulltest` | `fulltest` |
| `test` | `test` |

**Endpoints**:
- `POST /api/run` — body `{ "command": "<key>" }`. Stops live papertrade (same as training), starts dotnet subprocess.
- `GET /api/run/stream` — SSE stream; each line is `data: <line>\n\n`; ends with `data: [DONE]\n\n`.
- `POST /api/run/stop` — terminate the run subprocess.
- `GET /api/run/status` — `{ running: bool, command: str, startedAt: str, lastLine: str }`.

Live papertrade auto-restarts after a run completes (same behaviour as post-training).

---

## 2. Frontend — `store.js`

Add an `aggressive` variant entry to every strategy's `VARIANTS` array:

```js
{ id: 'aggressive', label: 'Aggressive', file: null, active: false,
  tags: ['high-freq', 'draft'], atrRange: [0, 9999],
  notes: 'Train with Aggressive fitness preset. GA targets higher frequency and raw gain; DD penalty reduced so the GA explores wider stops and lower entry thresholds.' }
```

No new store methods needed — `addVariant`, `train`, `setActive` already cover the variant lifecycle.

---

## 3. Frontend — `Gravity Training.html`

### 3a. FITNESS PROFILE section (new, above FITNESS WEIGHTS)

Three preset buttons inline: **Conservative** · **Balanced** · **Aggressive**

Clicking a preset fills the fitness weight sliders with these values:

| Weight | Conservative | Balanced | Aggressive |
|--------|-------------|----------|------------|
| Gain | 1.0 | 1.0 | 1.3 |
| Win rate | 0.9 | 0.7 | 0.4 |
| Quality | 0.8 | 0.6 | 0.3 |
| Frequency | 0.6 | 0.8 | 1.2 |
| DD penalty | 1.2 | 1.0 | 0.7 |
| Retention | 1.0 | 0.7 | 0.4 |
| Sharpe W | 0.0 | 0.3 | 0.0 |
| Calmar W | 0.0 | 0.3 | 0.0 |

### 3b. COMMANDS section (new, at bottom of left panel)

Categorised one-click buttons grouped under `BACKTEST` and `ANALYSIS` headers. Clicking a command:
1. Sets a `runCmd` state variable and clears the log.
2. `POST /api/run { command: key }`.
3. Opens `EventSource('/api/run/stream')` and appends lines to the shared log.
4. Shows a STOP button that calls `POST /api/run/stop`.

The right-side main panel (log + fitness chart) is shared: when `status === 'running'` (training) the training stream is active; when `runStatus === 'running'` the run stream is active. Both use the same `log` state array.

### 3c. Status bar update

The existing status bar shows training state. Extend it to show run state too:
- `IDLE · ready` — nothing active
- `TRAINING FadeShort · aggressive · cycle N` — GA in progress
- `RUNNING combinedbacktest…` — run command in progress
- `COMPLETE · cycle N` / `COMPLETE · combinedbacktest done` — finished

---

## Data Flow

```
User clicks "AGGRESSIVE" preset
  → fitness sliders update to aggressive values

User selects "aggressive" variant + clicks TRAIN
  → POST /api/train { strategy, variant: "aggressive", fitnessConfig: { FreqW:1.2, GainW:1.3, DdPenalty:0.7, ... } }
  → api_server writes fitness_config.json
  → dotnet run -- train --variant aggressive
  → GA reads FitnessConfig.Load() → fitness_config.json
  → Saves result to genotypes/fade_short_aggressive_genotype.json
  → SSE stream → frontend log

User clicks "combinedbacktest"
  → POST /api/run { command: "combinedbacktest" }
  → dotnet run -- combinedbacktest
  → SSE stream → frontend log
  → (writes backtest_results.json which Terminal page can read)
```

---

## Spec Self-Review

- No TBDs or incomplete sections.
- Architecture is internally consistent: two process slots, same SSE pattern, shared log view.
- Scope is focused: no GA slider wiring, no new HTML pages, no C# changes (FitnessConfig already has all needed fields).
- Ambiguity resolved: "aggressive variant" = fitness preset + draft slot in store.js; actual parameters come from GA training, not hardcoded.
