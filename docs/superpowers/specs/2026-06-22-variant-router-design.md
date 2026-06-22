---
name: variant-router-2026-06-22
description: Per-strategy variant system with ATR-based mini router, configurable fitness function, FastAPI training server, and structured JSON output logging for the frontend dashboard
metadata:
  type: project
---

# Variant Router + Configurable Fitness — Design Spec

**Date:** 2026-06-22
**Status:** Approved

---

## Context

The frontend prototype (from the design package) defines a multi-variant genotype registry per strategy (e.g. FadeShort: Default / High Vol / Low Vol). Currently all variants are draft slots — no training, no switching, no backtest results per variant. This spec defines the full system: how variants are trained with different fitness weights, how the VariantRouter picks the right genotype per coin per bar at runtime, how the frontend triggers training and reads results.

---

## Goals

1. Train named variants of each strategy with different fitness objectives (e.g. High Vol variant penalises drawdown more)
2. Select the active variant per-coin per-bar at runtime based on ATR ratio
3. Expose training control from the frontend via FastAPI (POST /api/train, SSE log stream)
4. Write structured JSON outputs from C# for the frontend to consume (backtest, training convergence, fulltest, live journal)

---

## Component Overview

| Component | Location | Language |
|---|---|---|
| `FitnessConfig` | `src/core/FitnessConfig.cs` | C# |
| `CalmarRatio`, `SortinoRatio` | `src/core/Simulator.cs` | C# |
| Extended FoldScore (all GAs) | each GA file | C# |
| `VariantRouter` | `src/core/VariantRouter.cs` | C# |
| `VariantSpec` | `src/core/VariantRouter.cs` | C# |
| CLI `--variant` arg | all train commands | C# |
| JSON output writers | `BacktestCommands`, `OosBacktest`, `FullTest`, GA files, `PapertradeCommands` | C# |
| `api_server.py` | `bot/api_server.py` | Python (FastAPI) |
| `store.js` variant registry | `frontend/store.js` | JS |

---

## 1. FitnessConfig

**File:** `src/core/FitnessConfig.cs`

```csharp
public record FitnessConfig(
    double GainW      = 1.0,
    double WrW        = 0.9,
    double QualityW   = 0.8,
    double FreqW      = 0.6,
    double DdPenalty  = 1.2,
    double RetentionW = 1.0,
    double SharpeW    = 0.7,
    double CalmarW    = 0.6,
    double PfW        = 0.5,
    double SortinoW   = 0.4,
    double AtrLow     = 0.0,
    double AtrHigh    = 9999.0
);
```

Serialised to/from JSON. C# reads `fitness_config.json` from repo root at training startup. If the file is absent, all defaults apply — existing behaviour is unchanged.

`AtrLow` / `AtrHigh` define the ATR ratio range this variant targets. Used for:
- Fold vol-weighting during training
- VariantRouter range matching at runtime
- Saved into the output genotype JSON as extra fields so the router can read them without a separate config file

**Preset examples:**

| Variant | AtrLow | AtrHigh | Notable weight changes |
|---|---|---|---|
| Default | 0.0 | 9999 | All defaults |
| High Vol | 1.5 | 9999 | CalmarW=1.2, DdPenalty=1.8, FreqW=0.3 |
| Low Vol | 0.0 | 0.8 | QualityW=1.4, FreqW=0.3, SharpeW=0.9 |

---

## 2. Extended Fitness Formula

**New methods in `Simulator.cs`:**

```csharp
// Peak-to-trough on cumulative return series. Returns magnitude (positive).
public static double MaxDrawdown(List<double> returns)
{
    double peak = 0, nav = 0, maxDd = 0;
    foreach (var r in returns) {
        nav += r;
        if (nav > peak) peak = nav;
        double dd = peak - nav;
        if (dd > maxDd) maxDd = dd;
    }
    return maxDd;
}

public static double CalmarRatio(List<double> returns, int candleCount)
{
    double ann   = returns.Average() * (candleCount / 4.0);
    double maxDD = MaxDrawdown(returns);
    return maxDD < 1e-10 ? 0 : ann / maxDD;
}

public static double SortinoRatio(List<double> returns, int candleCount)
{
    double mean    = returns.Average();
    double downDev = Math.Sqrt(returns.Where(r => r < 0).Select(r => r * r).DefaultIfEmpty(0).Average());
    return downDev < 1e-10 ? 0 : mean / downDev * Math.Sqrt(candleCount / 4.0);
}
```

`MaxDrawdown` operates on a list of per-trade returns (not NAV series), building a running NAV internally.

**FoldScore formula** (applied in all GA files — `FadeShortGA`, `SwingLongGA`, `DipLongGA`, `FadeLongGA`, `GridGA`):

```
FoldScore = gain × wrMult × qualityMult × freqBonus / ddDiv × retentionMult
          × (1 + cfg.SharpeW  × clamp(Sharpe  / 3.0,   0, 3))
          × (1 + cfg.CalmarW  × clamp(Calmar  / 2.0,   0, 3))
          × (1 + cfg.PfW      × clamp(PF - 1.0,        0, 3))
          × (1 + cfg.SortinoW × clamp(Sortino / 4.0,   0, 3))
          × volWeight
```

Where the existing multipliers (`wrMult`, `qualityMult`, etc.) are scaled by their corresponding `cfg.*W` values rather than hardcoded constants.

**`volWeight` per fold:**

```
volWeight = fraction of bars in fold where ATR_ratio ∈ [cfg.AtrLow, cfg.AtrHigh]
```

ATR ratio: `ATR14(barIndex) / mean(ATR14, last 100 bars)`. Computed on the 15m candle series.

For Default variant (`AtrLow=0, AtrHigh=9999`) `volWeight` is always 1.0 — no change to existing training behaviour.

---

## 3. Variant Training CLI

**Variant resolution order (each train command):**
1. `--variant <id>` command-line argument
2. `GRAVITY_VARIANT` environment variable
3. Falls back to `"default"`

**Startup sequence:**
1. Resolve variant id
2. Read `fitness_config.json` from repo root → deserialise into `FitnessConfig` (absent → use `new FitnessConfig()`)
3. Print: `Training FadeShort / variant=hival | CalmarW=1.2 DdPenalty=1.8 AtrRange=[1.5, ∞]`
4. Run GA with that config
5. Save genotype to `genotypes/{strategy}_{variant}_genotype.json`
   - `variant == "default"` → uses the existing constant from `Config.cs` (no path change)
   - Other variants → dynamic path: `$"genotypes/fade_short_hival_genotype.json"`
6. `AtrLow` / `AtrHigh` are added to each genotype DTO as optional fields (default 0.0 / 9999.0) so the VariantRouter can read the range directly from the saved JSON without a separate config file:
   ```csharp
   // Added to every *GenotypeDto:
   public double AtrLow  { get; init; } = 0.0;
   public double AtrHigh { get; init; } = 9999.0;
   ```

**Commands affected:** `train`, `swingLongtrain`, `diplongtrain`, `fadelongtrain`, `gridtrain`, `routertrain`, `dynamicguardtrain`

---

## 4. VariantRouter

**File:** `src/core/VariantRouter.cs`

```csharp
// Typed variant spec — each strategy uses its own concrete genotype type T.
public record VariantSpec<T>(string Id, double AtrLow, double AtrHigh, T? Genotype)
    where T : class;

public static class VariantRouter
{
    private const int AtrPeriod   = 14;
    private const int AtrBaseline = 100;  // bars for rolling mean

    public static T? Select<T>(ReadOnlyMemory<Candle> m15, int barIndex, VariantSpec<T>[] variants)
        where T : class;
    // Returns the matched variant's Genotype, or null to abstain.

    // Exposed for GA fold-weighting: fraction of bars in [low, high).
    public static double VolCoverage(ReadOnlyMemory<Candle> m15, double atrLow, double atrHigh);
}
```

Each strategy builds its own typed array: `VariantSpec<FadeShortGenotype>[]`, `VariantSpec<SwingLongGenotype>[]`, etc. No casting needed at call sites.

**Selection logic:**
1. Require `barIndex >= AtrPeriod + AtrBaseline` — abstain during warmup
2. Compute `ATR_ratio = ATR14[barIndex] / mean(ATR14, bars [barIndex-AtrBaseline .. barIndex-1])`
3. Filter variants to those where `AtrLow ≤ ratio < AtrHigh` and `Genotype != null`
4. If none match → return `null` (abstain)
5. If multiple match → prefer tightest range (`min(AtrHigh - AtrLow)`)
6. Default variant (`AtrHigh = 9999`) only wins when no other variant matches

**Wiring into simulators:** each strategy's inner bar-evaluation loop:
```csharp
var geno = VariantRouter.Select(m15, i, variants);
if (geno == null) continue; // abstain this bar
// cast geno to the strategy's genotype type and evaluate setup
```

**At startup (backtest + papertrade):** load all trained genotype files for a strategy. Build `VariantSpec[]` from each file's `AtrLow`/`AtrHigh` fields plus the deserialized genotype. Pass the array to the simulator.

**Papertrade display:** `SOLUSDT | SwingLong [liquid] ATR_ratio=0.81`

---

## 5. FastAPI Server

**File:** `bot/api_server.py`

Replaces `python3 -m http.server 8080`. Run with:
```bash
uvicorn bot.api_server:app --port 8080
```

**Endpoints:**

| Method | Path | Description |
|---|---|---|
| `GET /*` | Static | Serves `frontend/` directory |
| `POST /api/train` | Start | Writes `fitness_config.json`, spawns dotnet subprocess |
| `GET /api/train/stream` | SSE | Streams stdout from active subprocess, one line per event |
| `POST /api/stop` | Stop | Kills active subprocess |
| `GET /api/status` | Status | `{running, strategy, variant, startedAt, lastLine}` |

**`POST /api/train` request body:**
```json
{
  "strategy": "FadeShort",
  "variant": "hival",
  "fitnessConfig": {
    "CalmarW": 1.2, "DdPenalty": 1.8, "FreqW": 0.3,
    "AtrLow": 1.5, "AtrHigh": 9999
  }
}
```

**Subprocess management:**
- Only one training run at a time — 409 if already running
- `fitness_config.json` written before spawn, left on disk (overwritten on next run)
- Dotnet command: `dotnet run -- {command} --variant {variant}`
- `GRAVITY_VARIANT` env var also set for safety

**SSE format:** `data: <stdout line>\n\n` — standard EventSource protocol. Frontend connects with `new EventSource('/api/train/stream')`.

---

## 6. Output Logging (JSON files)

All four files are gitignored (runtime outputs). `store.js` already handles graceful fallback to mock data when files are absent.

### `backtest_results.json`
Written by `combinedbacktest` and `oosbacktest` after each run.

```json
{
  "timestamp": "2026-06-22T...",
  "val": {
    "FadeShort": {
      "default": { "sharpe": 2.41, "trades": 612, "winRate": 58.4, "pf": 1.62, "maxDD": -9.2, "ret": 118.3 },
      "hival":   { "sharpe": 2.18, "trades": 401, ... }
    }
  },
  "oos": {
    "FadeShort": { "default": { ... } }
  }
}
```

Frontend: Genotypes page variant comparison table.

### `training_progress.json`
Written by each GA every 10 generations. Overwritten each run.

```json
{
  "strategy": "FadeShort",
  "variant": "hival",
  "generation": 45,
  "bestFitness": 1.84,
  "meanFitness": 1.42,
  "population": [1.84, 1.79, 1.71, ...],
  "best": [0.60, 0.82, 1.10, ...],
  "mean": [0.40, 0.51, 0.72, ...]
}
```

Frontend: Training page convergence chart (polled every 3s). SSE covers the live text log.

### `fulltest_results.json`
Written by `FullTest.cs`. Structure matches `data.js` mock shapes exactly:
`backtest`, `oos`, `stats` (t-test, bootstrap, VC analysis), `stress` (crashes/rallies/worstCase), `wfvFolds`, `stratDists`, `yearByYear`, `routerImpact`.

Frontend: Terminal page statistical panels.

### `live_journal.json`
Appended by `PapertradeCommands.cs` each time a position closes.

```json
{ "symbol": "SOLUSDT", "strategy": "SwingLong", "variant": "liquid",
  "openTime": "2026-06-22T08:00:00Z", "closeTime": "2026-06-22T22:00:00Z",
  "entryPrice": 168.42, "exitPrice": 174.10, "returnPct": 3.37, "halfKelly": 0.042 }
```

---

## 7. Frontend Changes

### `store.js`
- Add `atrRange: [low, high]` to each variant definition
- Add `results: null` to each variant (populated from `backtest_results.json`)
- Add `POST /api/train` call in `GravStore.train(strategy, variant, fitnessConfig)`
- Add `EventSource('/api/train/stream')` handler

### `Gravity Training.html`
- Strategy + variant selector drives the POST body
- Fitness weight sliders (already designed) write into `fitnessConfig`
- Live log textarea connected to SSE stream
- Convergence chart polls `training_progress.json` every 3s

### `Gravity Genotypes.html`
- Per-variant results row from `backtest_results.json`
- Active variant indicator (which ATR range is currently live)
- "Train this variant" button → opens Training page with that variant pre-selected

---

## 8. Gitignore Additions

```
fitness_config.json
backtest_results.json
training_progress.json
fulltest_results.json
live_journal.json
```

(Note: `live_journal.json` and `livetrain_state.json` may already be gitignored — check before adding.)

---

## Implementation Order

1. **`FitnessConfig` + Simulator methods** — self-contained, no dependencies
2. **Extended FoldScore in all GAs** — depends on FitnessConfig
3. **CLI `--variant` arg + `fitness_config.json` read** — depends on FitnessConfig
4. **JSON output writers** (backtest, training_progress, fulltest, live_journal) — independent of router
5. **`VariantRouter`** — depends on Simulator (ATR), independent of FastAPI
6. **Wire VariantRouter into backtest + papertrade** — depends on VariantRouter
7. **`bot/api_server.py`** — independent of all C# changes
8. **Frontend wiring** — depends on API server + output files existing
