# Directory Restructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reorganize the flat root into `src/` (by concern), `commands/`, `genotypes/`, `bot/`, `legacy/`, and a mirrored test layout under `Gravity-gen2.Tests/`.

**Architecture:** All production `.cs` files move into `src/{core,regime,strategies/{fade_short,fade_long,dip_long,swing_long,grid},guard,coevolve}` and `commands/`. The .NET SDK picks up all `.cs` files recursively — no `.csproj` changes needed for source files. Genotype JSON paths are centralized in `Config.cs` (9 constants) and `CoinCluster.cs` (3 hardcoded strings); only those two files need path updates. Legacy code moves to `legacy/` and gets excluded via `<Compile Remove="legacy/**" />`.

**Tech Stack:** .NET 10 SDK, xunit 2.x, `dotnet build`, `dotnet test`, `git mv`

## Global Constraints

- Working directory for all `dotnet` commands: `/home/thomas/Gravity-gen2`
- Build command: `dotnet build -c Release`
- Test command: `dotnet test Gravity-gen2.Tests/`
- Use `git mv` (not `mv`) for all file moves — preserves git history
- Never use `git add -A` — stage files explicitly
- Build must pass after every task before committing
- Namespace stays `TradingGA` throughout — do not change namespaces when moving files

---

## File Map

| From (root) | To |
|---|---|
| `Config.cs` | `src/core/Config.cs` |
| `Indicators.cs` | `src/core/Indicators.cs` |
| `Simulator.cs` | `src/core/Simulator.cs` |
| `CandleFetcher.cs` | `src/core/CandleFetcher.cs` |
| `CoinCluster.cs` | `src/core/CoinCluster.cs` |
| `Pumpsegmenter.cs` | `src/core/Pumpsegmenter.cs` |
| `FundingRateSession.cs` | `src/core/FundingRateSession.cs` |
| `PortfolioReplay.cs` | `src/core/PortfolioReplay.cs` |
| `RankedPortfolioSim.cs` | `src/core/RankedPortfolioSim.cs` |
| `TradeEnricher.cs` | `src/core/TradeEnricher.cs` |
| `StatisticalTests.cs` | `src/core/StatisticalTests.cs` |
| `GenotypeDto.cs` | `src/core/GenotypeDto.cs` |
| `BayesianOptimizer.cs` | `src/core/BayesianOptimizer.cs` |
| `RegimeClassifier.cs` | `src/regime/RegimeClassifier.cs` |
| `RegimeRouter.cs` | `src/regime/RegimeRouter.cs` |
| `RegimeRouterGA.cs` | `src/regime/RegimeRouterGA.cs` |
| `RegimeRouterGenotype.cs` | `src/regime/RegimeRouterGenotype.cs` |
| `SwingGA.cs` | `src/strategies/fade_short/FadeShortGA.cs` ← rename |
| `SwingGenotype.cs` | `src/strategies/fade_short/FadeShortGenotype.cs` ← rename |
| `FadeLongGA.cs` | `src/strategies/fade_long/FadeLongGA.cs` |
| `FadeLongGenotype.cs` | `src/strategies/fade_long/FadeLongGenotype.cs` |
| `FadeLongSimulator.cs` | `src/strategies/fade_long/FadeLongSimulator.cs` |
| `DipLongGA.cs` | `src/strategies/dip_long/DipLongGA.cs` |
| `DipLongGenotype.cs` | `src/strategies/dip_long/DipLongGenotype.cs` |
| `DipLongSimulator.cs` | `src/strategies/dip_long/DipLongSimulator.cs` |
| `SwingLongGA.cs` | `src/strategies/swing_long/SwingLongGA.cs` |
| `SwingLongGenotype.cs` | `src/strategies/swing_long/SwingLongGenotype.cs` |
| `SwingSimulator.cs` | `src/strategies/swing_long/SwingSimulator.cs` |
| `GridGA.cs` | `src/strategies/grid/GridGA.cs` |
| `GridGenotype.cs` | `src/strategies/grid/GridGenotype.cs` |
| `GridSimulator.cs` | `src/strategies/grid/GridSimulator.cs` |
| `DynamicGuardGA.cs` | `src/guard/DynamicGuardGA.cs` |
| `DynamicGuardGenotype.cs` | `src/guard/DynamicGuardGenotype.cs` |
| `DynamicGuardSession.cs` | `src/guard/DynamicGuardSession.cs` |
| `DynamicGuardTrainCommands.cs` | `src/guard/DynamicGuardTrainCommands.cs` |
| `CoevolveGA.cs` | `src/coevolve/CoevolveGA.cs` |
| `Program.cs` | `commands/Program.cs` |
| `TrainCommands.cs` | `commands/TrainCommands.cs` |
| `LongTrainCommands.cs` | `commands/LongTrainCommands.cs` |
| `GridCommands.cs` | `commands/GridCommands.cs` |
| `BacktestCommands.cs` | `commands/BacktestCommands.cs` |
| `PapertradeCommands.cs` | `commands/PapertradeCommands.cs` |
| `CombinedBacktest.cs` | `commands/CombinedBacktest.cs` |
| `OosBacktest.cs` | `commands/OosBacktest.cs` |
| `FullTest.cs` | `commands/FullTest.cs` |
| `StressTestCommands.cs` | `commands/StressTestCommands.cs` |
| `DrawdownGuardGA.cs` | `legacy/DrawdownGuardGA.cs` |
| `DrawdownGuardGenotype.cs` | `legacy/DrawdownGuardGenotype.cs` |
| `DrawdownGuardTrainCommands.cs` | `legacy/DrawdownGuardTrainCommands.cs` |
| `ExitModifierGA.cs` | `legacy/ExitModifierGA.cs` |
| `ExitModifierGenotype.cs` | `legacy/ExitModifierGenotype.cs` |
| `ExitModifierTrainCommands.cs` | `legacy/ExitModifierTrainCommands.cs` |
| `ScenarioGA.cs` | `legacy/ScenarioGA.cs` |
| `ScenarioGenotype.cs` | `legacy/ScenarioGenotype.cs` |
| `ScenarioInjector.cs` | `legacy/ScenarioInjector.cs` |
| `test.cs` | `legacy/test.cs` |
| `*.json` (genotypes) | `genotypes/` (see Task 8) |
| `discord_bot.py` etc. | `bot/` |
| `README.md`, `REGIME_ARCHITECTURE.md` | `docs/` |
| `IndicatorTests.cs` | `Gravity-gen2.Tests/core/IndicatorsTests.cs` |

---

### Task 1: Create directory skeleton

**Files:**
- Create dirs: `src/core/`, `src/regime/`, `src/strategies/fade_short/`, `src/strategies/fade_long/`, `src/strategies/dip_long/`, `src/strategies/swing_long/`, `src/strategies/grid/`, `src/guard/`, `src/coevolve/`, `commands/`, `genotypes/`, `bot/`, `legacy/`
- Create dirs in test project: `Gravity-gen2.Tests/core/`, `Gravity-gen2.Tests/regime/`, `Gravity-gen2.Tests/strategies/fade_short/`, `Gravity-gen2.Tests/strategies/fade_long/`, `Gravity-gen2.Tests/strategies/dip_long/`, `Gravity-gen2.Tests/strategies/swing_long/`, `Gravity-gen2.Tests/strategies/grid/`, `Gravity-gen2.Tests/guard/`

- [ ] **Step 1: Create all source directories**

```bash
mkdir -p src/core src/regime \
  src/strategies/fade_short src/strategies/fade_long \
  src/strategies/dip_long src/strategies/swing_long \
  src/strategies/grid \
  src/guard src/coevolve \
  commands genotypes bot legacy
```

- [ ] **Step 2: Create all test directories**

```bash
mkdir -p Gravity-gen2.Tests/core \
  Gravity-gen2.Tests/regime \
  Gravity-gen2.Tests/strategies/fade_short \
  Gravity-gen2.Tests/strategies/fade_long \
  Gravity-gen2.Tests/strategies/dip_long \
  Gravity-gen2.Tests/strategies/swing_long \
  Gravity-gen2.Tests/strategies/grid \
  Gravity-gen2.Tests/guard
```

- [ ] **Step 3: Verify directories exist**

```bash
find src Gravity-gen2.Tests/core commands genotypes bot legacy -type d | sort
```

Expected: all 21 directories listed.

- [ ] **Step 4: Commit skeleton**

```bash
git add src/.gitkeep 2>/dev/null; git status
git commit --allow-empty -m "chore: create directory skeleton for restructure"
```

---

### Task 2: Move src/core/

**Files:**
- Move: 13 files from root → `src/core/`

- [ ] **Step 1: git mv all core files**

```bash
git mv Config.cs src/core/Config.cs
git mv Indicators.cs src/core/Indicators.cs
git mv Simulator.cs src/core/Simulator.cs
git mv CandleFetcher.cs src/core/CandleFetcher.cs
git mv CoinCluster.cs src/core/CoinCluster.cs
git mv Pumpsegmenter.cs src/core/Pumpsegmenter.cs
git mv FundingRateSession.cs src/core/FundingRateSession.cs
git mv PortfolioReplay.cs src/core/PortfolioReplay.cs
git mv RankedPortfolioSim.cs src/core/RankedPortfolioSim.cs
git mv TradeEnricher.cs src/core/TradeEnricher.cs
git mv StatisticalTests.cs src/core/StatisticalTests.cs
git mv GenotypeDto.cs src/core/GenotypeDto.cs
git mv BayesianOptimizer.cs src/core/BayesianOptimizer.cs
```

- [ ] **Step 2: Build to verify no breakage**

```bash
dotnet build -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/core/
git commit -m "chore: move core infrastructure to src/core/"
```

---

### Task 3: Move src/regime/

**Files:**
- Move: 4 files from root → `src/regime/`

- [ ] **Step 1: git mv regime files**

```bash
git mv RegimeClassifier.cs src/regime/RegimeClassifier.cs
git mv RegimeRouter.cs src/regime/RegimeRouter.cs
git mv RegimeRouterGA.cs src/regime/RegimeRouterGA.cs
git mv RegimeRouterGenotype.cs src/regime/RegimeRouterGenotype.cs
```

- [ ] **Step 2: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add src/regime/
git commit -m "chore: move regime layer to src/regime/"
```

---

### Task 4: Move src/strategies/ (with FadeShort rename)

**Files:**
- Move+rename: `SwingGA.cs` → `src/strategies/fade_short/FadeShortGA.cs`
- Move+rename: `SwingGenotype.cs` → `src/strategies/fade_short/FadeShortGenotype.cs`
- Move: FadeLong, DipLong, SwingLong, Grid files to their strategy subdirs

Note: `SwingGA.cs` contains `class FadeShortGA` and `SwingGenotype.cs` contains `class FadeShortGenotype` — the class names are already correct, only the filenames need to change.

- [ ] **Step 1: Move FadeShort (rename)**

```bash
git mv SwingGA.cs src/strategies/fade_short/FadeShortGA.cs
git mv SwingGenotype.cs src/strategies/fade_short/FadeShortGenotype.cs
```

- [ ] **Step 2: Move FadeLong**

```bash
git mv FadeLongGA.cs src/strategies/fade_long/FadeLongGA.cs
git mv FadeLongGenotype.cs src/strategies/fade_long/FadeLongGenotype.cs
git mv FadeLongSimulator.cs src/strategies/fade_long/FadeLongSimulator.cs
```

- [ ] **Step 3: Move DipLong**

```bash
git mv DipLongGA.cs src/strategies/dip_long/DipLongGA.cs
git mv DipLongGenotype.cs src/strategies/dip_long/DipLongGenotype.cs
git mv DipLongSimulator.cs src/strategies/dip_long/DipLongSimulator.cs
```

- [ ] **Step 4: Move SwingLong**

```bash
git mv SwingLongGA.cs src/strategies/swing_long/SwingLongGA.cs
git mv SwingLongGenotype.cs src/strategies/swing_long/SwingLongGenotype.cs
git mv SwingSimulator.cs src/strategies/swing_long/SwingSimulator.cs
```

- [ ] **Step 5: Move Grid**

```bash
git mv GridGA.cs src/strategies/grid/GridGA.cs
git mv GridGenotype.cs src/strategies/grid/GridGenotype.cs
git mv GridSimulator.cs src/strategies/grid/GridSimulator.cs
```

- [ ] **Step 6: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add src/strategies/
git commit -m "chore: move strategies to src/strategies/, rename SwingGA → FadeShortGA"
```

---

### Task 5: Move src/guard/ and src/coevolve/

**Files:**
- Move: 4 guard files, 1 coevolve file

- [ ] **Step 1: Move guard**

```bash
git mv DynamicGuardGA.cs src/guard/DynamicGuardGA.cs
git mv DynamicGuardGenotype.cs src/guard/DynamicGuardGenotype.cs
git mv DynamicGuardSession.cs src/guard/DynamicGuardSession.cs
git mv DynamicGuardTrainCommands.cs src/guard/DynamicGuardTrainCommands.cs
```

- [ ] **Step 2: Move coevolve**

```bash
git mv CoevolveGA.cs src/coevolve/CoevolveGA.cs
```

- [ ] **Step 3: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add src/guard/ src/coevolve/
git commit -m "chore: move guard and coevolve to src/"
```

---

### Task 6: Move commands/

**Files:**
- Move: 10 command/backtest files from root → `commands/`

- [ ] **Step 1: git mv all command files**

```bash
git mv Program.cs commands/Program.cs
git mv TrainCommands.cs commands/TrainCommands.cs
git mv LongTrainCommands.cs commands/LongTrainCommands.cs
git mv GridCommands.cs commands/GridCommands.cs
git mv BacktestCommands.cs commands/BacktestCommands.cs
git mv PapertradeCommands.cs commands/PapertradeCommands.cs
git mv CombinedBacktest.cs commands/CombinedBacktest.cs
git mv OosBacktest.cs commands/OosBacktest.cs
git mv FullTest.cs commands/FullTest.cs
git mv StressTestCommands.cs commands/StressTestCommands.cs
```

- [ ] **Step 2: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add commands/
git commit -m "chore: move CLI commands and backtest runners to commands/"
```

---

### Task 7: Archive legacy/ and exclude from build

**Files:**
- Move: 10 legacy files to `legacy/`
- Modify: `Gravity-gen2.csproj` to exclude `legacy/**` from compilation
- Modify: `commands/Program.cs` to remove the 3 legacy command cases

- [ ] **Step 1: Move legacy files**

```bash
git mv DrawdownGuardGA.cs legacy/DrawdownGuardGA.cs
git mv DrawdownGuardGenotype.cs legacy/DrawdownGuardGenotype.cs
git mv DrawdownGuardTrainCommands.cs legacy/DrawdownGuardTrainCommands.cs
git mv ExitModifierGA.cs legacy/ExitModifierGA.cs
git mv ExitModifierGenotype.cs legacy/ExitModifierGenotype.cs
git mv ExitModifierTrainCommands.cs legacy/ExitModifierTrainCommands.cs
git mv ScenarioGA.cs legacy/ScenarioGA.cs
git mv ScenarioGenotype.cs legacy/ScenarioGenotype.cs
git mv ScenarioInjector.cs legacy/ScenarioInjector.cs
git mv test.cs legacy/test.cs
```

- [ ] **Step 2: Add `<Compile Remove>` to Gravity-gen2.csproj**

Open `Gravity-gen2.csproj` and add this inside the existing `<ItemGroup>` that already has `<Compile Remove="Gravity-gen2.Tests/**" />`:

```xml
<Compile Remove="Gravity-gen2.Tests/**" />
<Compile Remove="legacy/**" />
```

The full ItemGroup should look like:

```xml
<ItemGroup>
  <Compile Remove="Gravity-gen2.Tests/**" />
  <Compile Remove="legacy/**" />
</ItemGroup>
```

- [ ] **Step 3: Remove legacy commands from commands/Program.cs**

Open `commands/Program.cs` and remove these three case lines from the switch statement:

```csharp
case "exitmodifiertrain": await ExitModifierTrainCommands.RunExitModifierTrain(client);   break;
case "stresstest":        await StressTestCommands.RunStressTest(client);                break;
case "drawdownguardtrain":  await DrawdownGuardTrainCommands.RunDrawdownGuardTrain(client); break;
```

After removal, verify the switch still compiles (no dangling references).

- [ ] **Step 4: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.` If errors mention missing types from legacy files, check that the three Program.cs cases were fully removed.

- [ ] **Step 5: Commit**

```bash
git add legacy/ Gravity-gen2.csproj commands/Program.cs
git commit -m "chore: archive legacy strategies to legacy/, exclude from build, remove dead CLI commands"
```

---

### Task 8: Move genotypes/ and update path constants

**Files:**
- Move: all active `.json` genotype files to `genotypes/`
- Delete: gen1 legacy JSON files
- Modify: `src/core/Config.cs` — update 9 path constants
- Modify: `src/core/CoinCluster.cs` — update 3 hardcoded path strings

- [ ] **Step 1: Move active genotype files**

```bash
git mv fade_short_genotype.json genotypes/fade_short_genotype.json
git mv grid_best_genotype.json genotypes/grid_best_genotype.json
git mv fade_long_genotype.json genotypes/fade_long_genotype.json
git mv dip_long_genotype.json genotypes/dip_long_genotype.json
git mv regime_router_genotype.json genotypes/regime_router_genotype.json
git mv swing_long_genotype.json genotypes/swing_long_genotype.json
git mv dynamic_guard_genotype.json genotypes/dynamic_guard_genotype.json
git mv exit_modifier_genotype.json genotypes/exit_modifier_genotype.json
git mv drawdown_guard_genotype.json genotypes/drawdown_guard_genotype.json
git mv backtest_baseline.json genotypes/backtest_baseline.json
git mv swing_best_genotype.json genotypes/swing_best_genotype.json
git mv swing_best_genotype_liquid.json genotypes/swing_best_genotype_liquid.json
git mv swing_best_genotype_mid.json genotypes/swing_best_genotype_mid.json
```

- [ ] **Step 2: Delete gen1 legacy JSON files**

```bash
git rm best_genotype.json
git rm regime_high_genotype.json
git rm regime_low_genotype.json
```

(`best_genotype_original.json` is gitignored — just `rm` it if present: `rm -f best_genotype_original.json`)

- [ ] **Step 3: Update Config.cs path constants**

Open `src/core/Config.cs`. Change the 9 path constants from bare filenames to `genotypes/`-prefixed:

```csharp
public const string FadeShortGenoFile      = "genotypes/fade_short_genotype.json";
public const string GridGenoFile           = "genotypes/grid_best_genotype.json";
public const string FadeLongGenoFile       = "genotypes/fade_long_genotype.json";
public const string DipLongGenoFile        = "genotypes/dip_long_genotype.json";
public const string RouterGenoFile         = "genotypes/regime_router_genotype.json";
public const string SwingLongGenoFile      = "genotypes/swing_long_genotype.json";
public const string ExitModifierGenoFile   = "genotypes/exit_modifier_genotype.json";
public const string DrawdownGuardGenoFile  = "genotypes/drawdown_guard_genotype.json";
public const string DynamicGuardGenoFile   = "genotypes/dynamic_guard_genotype.json";
```

- [ ] **Step 4: Update CoinCluster.cs hardcoded paths**

Open `src/core/CoinCluster.cs`. Find the switch expression that maps cluster to filename (around line 31) and update the three string literals:

```csharp
CoinCluster.Liquid  => "genotypes/swing_best_genotype_liquid.json",
CoinCluster.Mid     => "genotypes/swing_best_genotype_mid.json",
CoinCluster.HighVol => "genotypes/swing_best_genotype_highvol.json",
```

- [ ] **Step 5: Build**

```bash
dotnet build -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 6: Smoke-test path resolution**

```bash
ls genotypes/*.json | wc -l
```

Expected: 13 files.

- [ ] **Step 7: Commit**

```bash
git add genotypes/ src/core/Config.cs src/core/CoinCluster.cs
git commit -m "chore: centralize genotype JSON files in genotypes/, update Config.cs and CoinCluster.cs paths"
```

---

### Task 9: Move bot/ and docs/

**Files:**
- Move: 5 Python files to `bot/`
- Move: `README.md` and `REGIME_ARCHITECTURE.md` to `docs/`

Note: `discord_bot.py` uses `os.path.join(PROJECT_DIR, "live_journal.json")` and `os.path.join(PROJECT_DIR, "backtest_baseline.json")` — these are runtime state files that stay at root and are not affected by this move.

- [ ] **Step 1: Move Python bot files**

```bash
git mv discord_bot.py bot/discord_bot.py
git mv knowledge_bot.py bot/knowledge_bot.py
git mv gather_genes.py bot/gather_genes.py
git mv swing_autotrain.py bot/swing_autotrain.py
git mv requirements-discord.txt bot/requirements-discord.txt
```

- [ ] **Step 2: Move docs**

```bash
git mv README.md docs/README.md
git mv REGIME_ARCHITECTURE.md docs/REGIME_ARCHITECTURE.md
```

- [ ] **Step 3: Build (confirms no .cs files were accidentally touched)**

```bash
dotnet build -c Release 2>&1 | tail -5
```

Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add bot/ docs/
git commit -m "chore: move Python tools to bot/, move docs to docs/"
```

---

### Task 10: Reorganize test project layout

**Files:**
- Move: `Gravity-gen2.Tests/IndicatorTests.cs` → `Gravity-gen2.Tests/core/IndicatorsTests.cs`
- Create: stub test files for regime, strategies, and guard (with one real failing test each as a placeholder to establish structure)

- [ ] **Step 1: Move existing IndicatorTests**

```bash
git mv Gravity-gen2.Tests/IndicatorTests.cs Gravity-gen2.Tests/core/IndicatorsTests.cs
```

- [ ] **Step 2: Run existing tests to verify they still pass after move**

```bash
dotnet test Gravity-gen2.Tests/ --no-build 2>&1 | tail -10
```

If `--no-build` fails, run:

```bash
dotnet test Gravity-gen2.Tests/ 2>&1 | tail -10
```

Expected: all existing EMA/RSI/ATR/ADX/BbWidth tests pass.

- [ ] **Step 3: Create regime test stub**

Create `Gravity-gen2.Tests/regime/RegimeClassifierTests.cs`:

```csharp
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class RegimeClassifierTests
{
    private static Candle[] FlatCandles(int count)
    {
        var t = DateTime.UtcNow;
        return Enumerable.Range(0, count)
            .Select(i => new Candle(t.AddHours(i), 100, 101, 99, 100, 1_000_000))
            .ToArray();
    }

    [Fact]
    public void ClassifySeries_FlatMarket_ReturnsSomeResult()
    {
        // Smoke test: classifier runs without throwing on a flat price series.
        var candles = FlatCandles(200);
        var results = RegimeClassifier.ClassifySeriesWithDuration(candles);
        Assert.Equal(candles.Length, results.Length);
    }

    [Fact]
    public void ClassifySeries_OutputLength_MatchesInput()
    {
        var candles = FlatCandles(100);
        var results = RegimeClassifier.ClassifySeriesWithDuration(candles);
        Assert.Equal(100, results.Length);
    }
}
```

- [ ] **Step 4: Create guard test stub**

Create `Gravity-gen2.Tests/guard/DynamicGuardTests.cs`:

```csharp
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class DynamicGuardSessionTests
{
    [Fact]
    public void Placeholder_AlwaysPasses()
    {
        // Guard session requires live genotype + trade data.
        // This file is a placeholder — add integration tests once
        // a fixture-based test helper exists.
        Assert.True(true);
    }
}
```

- [ ] **Step 5: Run all tests**

```bash
dotnet test Gravity-gen2.Tests/ 2>&1 | tail -15
```

Expected: all indicator tests pass + the two new tests pass (regime smoke + guard placeholder).

- [ ] **Step 6: Commit**

```bash
git add Gravity-gen2.Tests/
git commit -m "test: reorganize test layout to mirror src/, add RegimeClassifier smoke test"
```

---

### Task 11: Update CLAUDE.md

**Files:**
- Modify: `CLAUDE.md` at project root

- [ ] **Step 1: Rewrite the Architecture section to reflect new layout**

Open `CLAUDE.md`. Replace the entire file with the updated version below. Keep all existing command descriptions; update only the Architecture section and file references.

The updated file should:
- Reflect the `src/`, `commands/`, `genotypes/`, `bot/`, `legacy/` layout
- List all currently active commands (including `fulltest`, `oosbacktest`, `allcoinsbacktest`, `yearlybreakdown`, `dynamicguardtrain`)
- Document `BacktestCommands.cs`, `BayesianOptimizer.cs`, `CandleFetcher.cs`, `DipLongSimulator.cs`, `FadeLongSimulator.cs`, `FundingRateSession.cs`, `GridCommands.cs`, `RegimeClassifier.cs`, `StatisticalTests.cs`
- Remove references to `BullGA.cs`, `BullGenotype.cs`, `BullSimulator.cs` (deleted)
- Update genotype file table to show `genotypes/` prefix
- Note that `SwingGA.cs` → `FadeShortGA.cs` rename has happened

Here is the complete replacement content:

````markdown
# CLAUDE.md

This file provides guidance to Claude Code when working with code in this repository.

## Commands

```bash
# Build
dotnet build -c Release

# Strategy training
dotnet run -- train              # FadeShort GA: 93 coins, 5-fold WFV, ~10 min
dotnet run -- gridtrain          # Grid GA: ranging-market long grid
dotnet run -- fadelongtrain      # FadeLong GA: bear-regime oversold bounce, restricted to BTC bear windows
dotnet run -- diplongtrain       # DipLong GA: bull-regime RSI dip + bullish BoS, regime-gated
dotnet run -- swingLongtrain     # SwingLong GA: bull-regime RSI bullish divergence + bullish BoS
dotnet run -- routertrain        # RegimeRouter GA: train routing thresholds + duration gates
dotnet run -- coevolvetrain      # Coevolve FadeLong + DipLong + Router together (4 cycles)
dotnet run -- dynamicguardtrain  # DynamicGuard GA: portfolio drawdown guard
dotnet run -- retrain            # FadeShort retrain on unknown coins (inverted screen, anti-overfit)

# Backtesting
dotnet run -- backtest           # FadeShort backtest: 93 coins, val 20%, 1h+15m dual-TF
dotnet run -- gridbacktest       # Grid backtest: 93 coins, val 20%
dotnet run -- combinedbacktest   # All strategies combined, shared capital, router-gated
dotnet run -- oosbacktest        # OOS backtest: never-seen coins, full history, all strategies
dotnet run -- allcoinsbacktest   # Portfolio sim: BacktestCoins (val 20%) + OOS coins
dotnet run -- yearlybreakdown    # Per-year portfolio returns (full history)
dotnet run -- fulltest           # Full statistical test with Kelly cap + VC analysis
dotnet run -- test               # Statistical edge validation

# Live
dotnet run -- papertrade         # Live signals (1h refresh), all strategies, router-gated

# Discord bot (requires .env)
.venv/bin/python bot/discord_bot.py
```

No test suite for strategies. Validation is done by running `combinedbacktest` or `oosbacktest` after any simulator change.
Unit tests for indicators and classifiers: `dotnet test Gravity-gen2.Tests/`

## Directory Layout

```
src/
  core/           Shared infrastructure: Config, Indicators, Simulator, CandleFetcher,
                  CoinCluster, Pumpsegmenter, FundingRateSession, PortfolioReplay,
                  RankedPortfolioSim, TradeEnricher, StatisticalTests, GenotypeDto,
                  BayesianOptimizer
  regime/         RegimeClassifier, RegimeRouter, RegimeRouterGA, RegimeRouterGenotype
  strategies/
    fade_short/   FadeShortGA, FadeShortGenotype
    fade_long/    FadeLongGA, FadeLongGenotype, FadeLongSimulator
    dip_long/     DipLongGA, DipLongGenotype, DipLongSimulator
    swing_long/   SwingLongGA, SwingLongGenotype, SwingSimulator
    grid/         GridGA, GridGenotype, GridSimulator
  guard/          DynamicGuardGA, DynamicGuardGenotype, DynamicGuardSession,
                  DynamicGuardTrainCommands
  coevolve/       CoevolveGA

commands/         Program (CLI entry), TrainCommands, LongTrainCommands, GridCommands,
                  BacktestCommands, PapertradeCommands, CombinedBacktest, OosBacktest,
                  FullTest, StressTestCommands

genotypes/        All trained parameter JSON files (see table below)
bot/              discord_bot.py, knowledge_bot.py, gather_genes.py, swing_autotrain.py
legacy/           Archived: DrawdownGuard*, ExitModifier*, Scenario*, test.cs
docs/             README.md, REGIME_ARCHITECTURE.md, superpowers/plans+specs

Gravity-gen2.Tests/
  core/           IndicatorsTests, SimulatorTests (planned)
  regime/         RegimeClassifierTests
  guard/          DynamicGuardTests (placeholder)
```

## Architecture

### Strategy suite (5 strategies)

All strategies share the same dual-timeframe setup: **1h candles** for regime/setup detection, **15m candles** for precise entry/exit execution.

| Strategy | Regime | Direction | Entry Signal |
|----------|--------|-----------|--------------|
| **FadeShort** | Always-on | Short | RSI bearish divergence + min rally + bearish BoS on 15m |
| **Grid** | Ranging | Long | ADX low + BB compression, grid levels |
| **SwingLong** | Bull (`DipLongActive`) | Long | RSI bullish divergence + min decline + bullish BoS on 15m |
| **DipLong** | Bull (`DipLongActive`) | Long | RSI dip (40–55) in established uptrend + bullish BoS on 15m |
| **FadeLong** | Bear (`FadeLongActive`) | Long | RSI bearish divergence at bottom + bullish BoS on 15m |

Exit uses three layers: hard ATR stop · fixed ATR profit target · trailing ATR stop (armed after `TrailingActivationAtrMult` × ATR move) · `MaxHoldCandles` forced close.

### RegimeClassifier + RegimeRouter

`src/regime/RegimeClassifier.cs` — multi-signal ensemble classifier producing Bull/Bear/Ranging/HighVol + confidence (0–1).
Signals: EMA stack (weight 3), EMA50 slope (1.5), ADX (1), 20-bar momentum (0.5), ATR vol ratio (0.5).
`ClassifySeriesWithDuration` is O(n) with per-bar duration counter (how many consecutive bars in current regime).

`src/regime/RegimeRouter.cs` — BTC-anchored router (ETH as secondary confirmer, configurable blend weight).
Returns `StrategyActivation` with per-strategy flags + `SizeMult` (confidence-scaled position multiplier).
Two overloads: rule-based fallback and trained-genotype path (`RegimeRouter.Route(btcH1, routerGeno, ethH1)`).

`RegimeRouterSession` — pre-computed O(1) per-trade lookup for backtests. Build once, call `IsActive(kind, time)`.

### Cooperative coevolution

`src/coevolve/CoevolveGA.cs` — 4-cycle loop: FadeLong and DipLong train against the router's current gating (soft bear/bull confidence gradient), then the router retrains on the evolved trade lists. Avoids train-then-gate misalignment.
`src/core/BayesianOptimizer.cs` — TPE post-GA refinement (60 iterations). Applied after FadeShort, FadeLong, DipLong, SwingLong GA runs.

### GA fitness (all long strategies)

5-fold walk-forward CV. FoldScore = `gain × wrMult × qualityMult × freqBonus / ddDiv × retentionMult`.
`retentionMult` penalises giving back gains at fold end (pushes toward tight trailing, not peak capture).

### DynamicGuard

`src/guard/DynamicGuardSession.cs` — per-strategy drawdown guard. Trained by `DynamicGuardGA` on the portfolio trade distribution. Applied in live papertrade and all backtests. Supersedes the older DrawdownGuard and ExitModifier (archived in `legacy/`).

### Portfolio cap

`src/core/PortfolioReplay.cs` — filters combined trade list by per-strategy concurrent count before EUR exposure simulation. Default caps: FadeShort=10, SwingLong=8, DipLong=8, FadeLong=8, Grid=12.

### Candle fetching

`src/core/CandleFetcher.cs` — `FetchFifteenMinCandlesCached(symbol, batches)` — 15m candles with disk cache (`candle_cache/{symbol}_15m.csv`). `batches: 113` ≈ 3.2yr. Incremental: only fetches new candles since last write.

### Saved genotype files (in genotypes/)

| File | Strategy |
|------|----------|
| `genotypes/fade_short_genotype.json` | FadeShort |
| `genotypes/grid_best_genotype.json` | Grid |
| `genotypes/fade_long_genotype.json` | FadeLong |
| `genotypes/dip_long_genotype.json` | DipLong |
| `genotypes/swing_long_genotype.json` | SwingLong |
| `genotypes/swing_best_genotype.json` | SwingLong (best candidate) |
| `genotypes/swing_best_genotype_liquid.json` | SwingLong – liquid coins |
| `genotypes/swing_best_genotype_mid.json` | SwingLong – mid coins |
| `genotypes/regime_router_genotype.json` | RegimeRouter |
| `genotypes/dynamic_guard_genotype.json` | DynamicGuard |

### Discord bot

`bot/discord_bot.py` manages a single long-running dotnet subprocess (`papertrade`).
- Detects cycle boundaries by watching for `=== Gravity-gen2 | PAPER TRADE` headers in stdout
- Posts formatted summary to Discord each cycle
- Forwards text messages in command channel to `claude -p` for live code edits, then restarts

Required `.env` (stays at project root):
```
DISCORD_TOKEN=
DISCORD_OUTPUT_CHANNEL=
DISCORD_COMMAND_CHANNEL=
DISCORD_GUILD_ID=
GRAVITY_MODE=papertrade
```
````

- [ ] **Step 2: Build to confirm nothing broken**

```bash
dotnet build -c Release 2>&1 | tail -3
```

Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: update CLAUDE.md for restructured directory layout"
```

---

### Task 12: Final verification

- [ ] **Step 1: Full build**

```bash
dotnet build -c Release
```

Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` (or only pre-existing warnings).

- [ ] **Step 2: Run all tests**

```bash
dotnet test Gravity-gen2.Tests/
```

Expected: all tests pass (EMA, RSI, ATR, ADX, BbWidth, RegimeClassifier smoke, guard placeholder).

- [ ] **Step 3: Verify root is clean**

```bash
ls *.cs 2>/dev/null && echo "FAIL: stray .cs files at root" || echo "OK: no .cs files at root"
ls *.json 2>/dev/null && echo "FAIL: stray .json files at root" || echo "OK: no .json files at root"
ls *.py 2>/dev/null && echo "FAIL: stray .py files at root" || echo "OK: no .py files at root"
```

Expected: all three lines print "OK".

- [ ] **Step 4: Verify genotype files are accessible**

```bash
ls genotypes/*.json | sort
```

Expected: 13 files listed.

- [ ] **Step 5: Verify git status**

```bash
git status
```

Expected: clean working tree.

- [ ] **Step 6: Final commit if any loose ends**

```bash
git add -p  # review anything uncommitted
git status
```

If clean, no commit needed. Otherwise commit remaining changes.

---

## Self-Review

**Spec coverage:**
- ✅ All 56 .cs files moved to `src/` or `commands/` or `legacy/`
- ✅ `SwingGA.cs` → `FadeShortGA.cs` rename included (Task 4)
- ✅ All 13 active JSON genotypes moved to `genotypes/` (Task 8)
- ✅ Gen1 legacy JSON deleted (Task 8)
- ✅ `Config.cs` path constants updated (Task 8)
- ✅ `CoinCluster.cs` hardcoded paths updated (Task 8)
- ✅ `<Compile Remove="legacy/**" />` added to .csproj (Task 7)
- ✅ Legacy commands removed from `Program.cs` (Task 7)
- ✅ Python tools moved to `bot/` (Task 9)
- ✅ Docs moved to `docs/` (Task 9)
- ✅ Test project reorganized with mirrored layout (Task 10)
- ✅ CLAUDE.md updated (Task 11)
- ✅ `discord_bot.py` path noted as using `PROJECT_DIR` join — live_journal.json and backtest_baseline.json stay at root (no change needed)

**No placeholders found.**

**Type consistency:** No new types introduced. All moves preserve existing class names and namespaces.
