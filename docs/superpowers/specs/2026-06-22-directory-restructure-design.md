---
name: directory-restructure-2026-06-22
description: Full directory restructure for Gravity-gen2 — src/ by concern, mirrored test layout, Python in bot/, genotypes centralized, legacy archived
metadata:
  type: project
---

# Directory Restructure + Test Layout — Design Spec

**Date:** 2026-06-22
**Status:** Approved

## Context

The root directory contains 56+ `.cs` files, 20 JSON genotype files, 4 Python scripts, and several docs — all flat. Navigation requires memorizing the entire file list. No test suite exists. This spec defines the target structure and all moves required to get there.

## Target Structure

```
Gravity-gen2/
│
├── src/                                  ← all production C#
│   ├── core/
│   │   ├── Config.cs
│   │   ├── Indicators.cs
│   │   ├── Simulator.cs
│   │   ├── CandleFetcher.cs
│   │   ├── CoinCluster.cs
│   │   ├── Pumpsegmenter.cs
│   │   ├── FundingRateSession.cs
│   │   ├── PortfolioReplay.cs
│   │   ├── RankedPortfolioSim.cs
│   │   ├── TradeEnricher.cs
│   │   ├── StatisticalTests.cs
│   │   ├── GenotypeDto.cs
│   │   └── BayesianOptimizer.cs
│   │
│   ├── regime/
│   │   ├── RegimeClassifier.cs
│   │   ├── RegimeRouter.cs
│   │   ├── RegimeRouterGA.cs
│   │   └── RegimeRouterGenotype.cs
│   │
│   ├── strategies/
│   │   ├── fade_short/
│   │   │   ├── FadeShortGA.cs            ← renamed from SwingGA.cs
│   │   │   └── FadeShortGenotype.cs      ← renamed from SwingGenotype.cs
│   │   ├── fade_long/
│   │   │   ├── FadeLongGA.cs
│   │   │   ├── FadeLongGenotype.cs
│   │   │   └── FadeLongSimulator.cs
│   │   ├── dip_long/
│   │   │   ├── DipLongGA.cs
│   │   │   ├── DipLongGenotype.cs
│   │   │   └── DipLongSimulator.cs
│   │   ├── swing_long/
│   │   │   ├── SwingLongGA.cs
│   │   │   ├── SwingLongGenotype.cs
│   │   │   └── SwingSimulator.cs
│   │   └── grid/
│   │       ├── GridGA.cs
│   │       ├── GridGenotype.cs
│   │       └── GridSimulator.cs
│   │
│   ├── guard/
│   │   ├── DynamicGuardGA.cs
│   │   ├── DynamicGuardGenotype.cs
│   │   ├── DynamicGuardSession.cs
│   │   └── DynamicGuardTrainCommands.cs
│   │
│   └── coevolve/
│       └── CoevolveGA.cs
│
├── commands/                             ← CLI entry + all command handlers
│   ├── Program.cs
│   ├── TrainCommands.cs
│   ├── LongTrainCommands.cs
│   ├── GridCommands.cs
│   ├── BacktestCommands.cs
│   ├── PapertradeCommands.cs
│   ├── CombinedBacktest.cs
│   ├── OosBacktest.cs
│   ├── FullTest.cs
│   └── StressTestCommands.cs
│
├── Gravity-gen2.Tests/                   ← test project, mirrors src/ layout
│   ├── Gravity-gen2.Tests.csproj
│   ├── core/
│   │   ├── IndicatorsTests.cs
│   │   ├── SimulatorTests.cs
│   │   └── BayesianOptimizerTests.cs
│   ├── regime/
│   │   ├── RegimeClassifierTests.cs
│   │   └── RegimeRouterTests.cs
│   ├── strategies/
│   │   ├── fade_short/
│   │   │   └── FadeShortSimulatorTests.cs
│   │   ├── fade_long/
│   │   │   └── FadeLongSimulatorTests.cs
│   │   ├── dip_long/
│   │   │   └── DipLongSimulatorTests.cs
│   │   ├── swing_long/
│   │   │   └── SwingSimulatorTests.cs
│   │   └── grid/
│   │       └── GridSimulatorTests.cs
│   └── guard/
│       └── DynamicGuardTests.cs
│
├── genotypes/                            ← trained parameter files only
│   ├── fade_short_genotype.json
│   ├── fade_long_genotype.json
│   ├── dip_long_genotype.json
│   ├── swing_long_genotype.json
│   ├── grid_best_genotype.json
│   ├── regime_router_genotype.json
│   ├── dynamic_guard_genotype.json
│   ├── exit_modifier_genotype.json
│   ├── drawdown_guard_genotype.json
│   └── backtest_baseline.json
│
├── bot/                                  ← Python automation layer
│   ├── discord_bot.py
│   ├── knowledge_bot.py
│   ├── gather_genes.py
│   ├── swing_autotrain.py
│   └── requirements-discord.txt
│
├── legacy/                               ← archived, removed from Program.cs
│   ├── DrawdownGuardGA.cs
│   ├── DrawdownGuardGenotype.cs
│   ├── DrawdownGuardTrainCommands.cs
│   ├── ExitModifierGA.cs
│   ├── ExitModifierGenotype.cs
│   ├── ExitModifierTrainCommands.cs
│   ├── ScenarioGA.cs
│   ├── ScenarioGenotype.cs
│   ├── ScenarioInjector.cs
│   └── test.cs
│
├── docs/
│   ├── README.md                         ← rewrite to describe 5-strategy ensemble
│   ├── REGIME_ARCHITECTURE.md
│   └── superpowers/
│       ├── plans/
│       └── specs/
│
├── candidates/                           ← candidate genotype snapshots
│
├── CLAUDE.md                             ← stays at root (Claude Code convention)
├── Gravity-gen2.csproj
├── .gitignore
├── .env                                  ← gitignored
└── secrets.json                          ← gitignored
```

## File Moves (complete list)

### src/core/
- `Config.cs` → `src/core/Config.cs`
- `Indicators.cs` → `src/core/Indicators.cs`
- `Simulator.cs` → `src/core/Simulator.cs`
- `CandleFetcher.cs` → `src/core/CandleFetcher.cs`
- `CoinCluster.cs` → `src/core/CoinCluster.cs`
- `Pumpsegmenter.cs` → `src/core/Pumpsegmenter.cs`
- `FundingRateSession.cs` → `src/core/FundingRateSession.cs`
- `PortfolioReplay.cs` → `src/core/PortfolioReplay.cs`
- `RankedPortfolioSim.cs` → `src/core/RankedPortfolioSim.cs`
- `TradeEnricher.cs` → `src/core/TradeEnricher.cs`
- `StatisticalTests.cs` → `src/core/StatisticalTests.cs`
- `GenotypeDto.cs` → `src/core/GenotypeDto.cs`
- `BayesianOptimizer.cs` → `src/core/BayesianOptimizer.cs`

### src/regime/
- `RegimeClassifier.cs` → `src/regime/RegimeClassifier.cs`
- `RegimeRouter.cs` → `src/regime/RegimeRouter.cs`
- `RegimeRouterGA.cs` → `src/regime/RegimeRouterGA.cs`
- `RegimeRouterGenotype.cs` → `src/regime/RegimeRouterGenotype.cs`

### src/strategies/
- `SwingGA.cs` → `src/strategies/fade_short/FadeShortGA.cs` (rename)
- `SwingGenotype.cs` → `src/strategies/fade_short/FadeShortGenotype.cs` (rename)
- `FadeLongGA.cs` → `src/strategies/fade_long/FadeLongGA.cs`
- `FadeLongGenotype.cs` → `src/strategies/fade_long/FadeLongGenotype.cs`
- `FadeLongSimulator.cs` → `src/strategies/fade_long/FadeLongSimulator.cs`
- `DipLongGA.cs` → `src/strategies/dip_long/DipLongGA.cs`
- `DipLongGenotype.cs` → `src/strategies/dip_long/DipLongGenotype.cs`
- `DipLongSimulator.cs` → `src/strategies/dip_long/DipLongSimulator.cs`
- `SwingLongGA.cs` → `src/strategies/swing_long/SwingLongGA.cs`
- `SwingLongGenotype.cs` → `src/strategies/swing_long/SwingLongGenotype.cs`
- `SwingSimulator.cs` → `src/strategies/swing_long/SwingSimulator.cs`
- `GridGA.cs` → `src/strategies/grid/GridGA.cs`
- `GridGenotype.cs` → `src/strategies/grid/GridGenotype.cs`
- `GridSimulator.cs` → `src/strategies/grid/GridSimulator.cs`

### src/guard/
- `DynamicGuardGA.cs` → `src/guard/DynamicGuardGA.cs`
- `DynamicGuardGenotype.cs` → `src/guard/DynamicGuardGenotype.cs`
- `DynamicGuardSession.cs` → `src/guard/DynamicGuardSession.cs`
- `DynamicGuardTrainCommands.cs` → `src/guard/DynamicGuardTrainCommands.cs`

### src/coevolve/
- `CoevolveGA.cs` → `src/coevolve/CoevolveGA.cs`

### commands/
- `Program.cs` → `commands/Program.cs`
- `TrainCommands.cs` → `commands/TrainCommands.cs`
- `LongTrainCommands.cs` → `commands/LongTrainCommands.cs`
- `GridCommands.cs` → `commands/GridCommands.cs`
- `BacktestCommands.cs` → `commands/BacktestCommands.cs`
- `PapertradeCommands.cs` → `commands/PapertradeCommands.cs`
- `CombinedBacktest.cs` → `commands/CombinedBacktest.cs`
- `OosBacktest.cs` → `commands/OosBacktest.cs`
- `FullTest.cs` → `commands/FullTest.cs`
- `StressTestCommands.cs` → `commands/StressTestCommands.cs`

### genotypes/
- `fade_short_genotype.json` → `genotypes/fade_short_genotype.json`
- `fade_long_genotype.json` → `genotypes/fade_long_genotype.json`
- `dip_long_genotype.json` → `genotypes/dip_long_genotype.json`
- `swing_long_genotype.json` → `genotypes/swing_long_genotype.json`
- `swing_best_genotype.json` → `genotypes/swing_best_genotype.json`
- `swing_best_genotype_liquid.json` → `genotypes/swing_best_genotype_liquid.json`
- `swing_best_genotype_mid.json` → `genotypes/swing_best_genotype_mid.json`
- `grid_best_genotype.json` → `genotypes/grid_best_genotype.json`
- `regime_router_genotype.json` → `genotypes/regime_router_genotype.json`
- `dynamic_guard_genotype.json` → `genotypes/dynamic_guard_genotype.json`
- `exit_modifier_genotype.json` → `genotypes/exit_modifier_genotype.json`
- `drawdown_guard_genotype.json` → `genotypes/drawdown_guard_genotype.json`
- `backtest_baseline.json` → `genotypes/backtest_baseline.json`
- `best_genotype.json` → DELETE (gen1 legacy)
- `best_genotype_original.json` → DELETE (gen1 legacy, also gitignored)
- `regime_high_genotype.json` → DELETE (gen1 legacy)
- `regime_low_genotype.json` → DELETE (gen1 legacy)

### bot/
- `discord_bot.py` → `bot/discord_bot.py`
- `knowledge_bot.py` → `bot/knowledge_bot.py`
- `gather_genes.py` → `bot/gather_genes.py`
- `swing_autotrain.py` → `bot/swing_autotrain.py`
- `requirements-discord.txt` → `bot/requirements-discord.txt`

### docs/
- `README.md` → `docs/README.md`
- `REGIME_ARCHITECTURE.md` → `docs/REGIME_ARCHITECTURE.md`

### legacy/ (archived, removed from Program.cs)
- `DrawdownGuardGA.cs` → `legacy/DrawdownGuardGA.cs`
- `DrawdownGuardGenotype.cs` → `legacy/DrawdownGuardGenotype.cs`
- `DrawdownGuardTrainCommands.cs` → `legacy/DrawdownGuardTrainCommands.cs`
- `ExitModifierGA.cs` → `legacy/ExitModifierGA.cs`
- `ExitModifierGenotype.cs` → `legacy/ExitModifierGenotype.cs`
- `ExitModifierTrainCommands.cs` → `legacy/ExitModifierTrainCommands.cs`
- `ScenarioGA.cs` → `legacy/ScenarioGA.cs`
- `ScenarioGenotype.cs` → `legacy/ScenarioGenotype.cs`
- `ScenarioInjector.cs` → `legacy/ScenarioInjector.cs`
- `test.cs` → `legacy/test.cs`

## Genotype Path Updates Required

All hardcoded genotype paths in C# source must be updated from e.g. `"fade_long_genotype.json"` to `"genotypes/fade_long_genotype.json"`. The primary locations to update are:
- `commands/LongTrainCommands.cs`
- `commands/TrainCommands.cs`
- `commands/BacktestCommands.cs`
- `commands/CombinedBacktest.cs`
- `commands/OosBacktest.cs`
- `commands/FullTest.cs`
- `commands/PapertradeCommands.cs`
- `commands/GridCommands.cs`
- `src/guard/DynamicGuardTrainCommands.cs`
- `Config.cs` (if paths are centralized there)

The `discord_bot.py` subprocess call path may also need updating if it references genotype files directly.

## Rename: SwingGA → FadeShortGA

`SwingGA.cs` contains `class FadeShortGA` — the class was renamed but the file was not. As part of the move, the file is renamed to `FadeShortGA.cs`. No code changes needed inside the file. Same for `SwingGenotype.cs → FadeShortGenotype.cs` (contains `class FadeShortGenotype`).

## Program.cs: Remove Legacy Commands

Remove these three cases from the switch in `commands/Program.cs`:
```csharp
case "exitmodifiertrain": ...
case "drawdownguardtrain": ...
case "stresstest": ...   // (optional — keep if StressTest is still used)
```

## Test Project Priority

The test project currently has no tests. Priority order for first tests:
1. `core/IndicatorsTests.cs` — pure functions (RSI, ATR, EMA), no dependencies, fast
2. `core/SimulatorTests.cs` — KPI calculations on synthetic trade lists
3. `regime/RegimeClassifierTests.cs` — deterministic given candle arrays
4. `core/GenotypeDto.cs` — JSON serialization round-trips

Simulator tests use synthetic fixture candles (30–100 bars). No real API calls.

## .csproj Impact

The main `Gravity-gen2.csproj` uses implicit SDK globbing — no `<Compile>` entries. Moving files to subdirectories requires no `.csproj` changes.

The `legacy/` folder will be compiled by default since it's under the project root. Add an exclusion if legacy files cause compile errors:
```xml
<Compile Remove="legacy/**" />
```

## Discord Bot Path

`bot/discord_bot.py` launches the dotnet process from its working directory. After the move, ensure it still resolves the dotnet binary correctly. The `.env` file stays at root.
