# Gravity-gen2 SWOT Actions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement 7 improvements: SwingLong strategy, BayesianOptimizer wiring, FadeLong bear-window training, portfolio concurrent cap, papertrade completion (all strategies + trained router), and CLAUDE.md rewrite.

**Architecture:** SwingLong mirrors FadeShort's divergence+BoS logic but long-direction, gated by the router's `DipLongActive` flag. Bayesian refinement seeds from GA elites using existing `ToVector`/`FromVector` on each genotype. Portfolio cap filters the combined trade list by per-strategy concurrent count before the existing `SimulatePortfolioExposureCapped` call.

**Tech Stack:** C# .NET 10, Bybit.Net, existing GA/simulator patterns throughout.

---

## File Map

| Action | Files Created | Files Modified |
|--------|--------------|----------------|
| SwingLong genotype | `SwingLongGenotype.cs` | `GenotypeDto.cs`, `Config.cs` |
| SwingLong simulator | — | `SwingSimulator.cs` |
| SwingLong GA | `SwingLongGA.cs` | — |
| SwingLong train modes | — | `LongTrainCommands.cs`, `Program.cs` |
| BayesianOptimizer wiring | — | `SwingGenotype.cs`, `TrainCommands.cs`, `LongTrainCommands.cs` |
| FadeLong bear-window | — | `LongTrainCommands.cs` |
| Portfolio cap | `PortfolioReplay.cs` | `CombinedBacktest.cs`, `OosBacktest.cs`, `BacktestCommands.cs` |
| Papertrade update | — | `PapertradeCommands.cs` |
| CLAUDE.md | — | `CLAUDE.md` |

---

## Task 1: SwingLongGenotype, Config entry, and DTO

**Files:**
- Create: `SwingLongGenotype.cs`
- Modify: `Config.cs` (add `SwingLongGenoFile`)
- Modify: `GenotypeDto.cs` (add `SwingLongGenotypeDto`)

- [ ] **Step 1: Add `SwingLongGenoFile` to Config.cs**

In `Config.cs`, after the `RouterGenoFile` line, add:

```csharp
public const string SwingLongGenoFile = "swing_long_genotype.json";
```

- [ ] **Step 2: Create `SwingLongGenotype.cs`**

```csharp
namespace TradingGA;

// Bidirectional swing long — mirror of FadeShort using RSI bullish divergence + bullish BoS.
// Router-gated on DipLongActive (shared with DipLong — both are bull-regime long strategies).
//
// Entry:  strong bull trend (ADX + EMA) · min decline from recent high
//         · RSI bullish divergence (price lower low but RSI higher = sellers losing steam)
//         · 1h close above prev high (BoS) · 15m close above prev 15m high (precision entry)
// Exit:   swing-low stop · fixed ATR target · trailing stop · max-hold timeout
//
// Fixed: RsiPeriod=7, AdxPeriod=7 (mirrors FadeShort constants).
// ATR exit sizing uses h4 ATR (aggregated from h1) to match multi-day holding timeframe.
public class SwingLongGenotype
{
    public int    EmaPeriod     { get; set; }   // 20–100   trend-direction EMA
    public double AdxThreshold  { get; set; }   // 15–40    trend strength gate

    public int    LookbackCandles   { get; set; }   // 12–120  h1 bars to locate swing low
    public double RsiOversold       { get; set; }   // 20–40   RSI ceiling the swing low must clear
    public double RsiDivThreshold   { get; set; }   // 5–15    RSI must be this many pts above swing-low RSI
    public double MinDeclineAtrMult { get; set; }   // 3–10    min decline (h1 ATR) from recent high to low

    public double StopLossAtrMult           { get; set; }   // 0.3–2.0   ATR buffer below swing low
    public double TakeProfitAtrMult         { get; set; }   // 2.0–10.0  fixed profit target
    public double TrailingActivationAtrMult { get; set; }   // 1.0–4.0   arm trail after this profit
    public double TrailingStopAtrMult       { get; set; }   // 1.0–5.0   trail distance from peak
    public int    MaxHoldCandles            { get; set; }   // 24–120    h1 bars before forced exit
    public double PositionSizePct           { get; set; }   // 0.01–0.05 fraction of capital per trade

    public double Fitness { get; set; } = double.MinValue;

    // Bounds for BayesianOptimizer — order matches ToVector/FromVector.
    public static readonly double[,] Bounds =
    {
        {  20, 100  }, // EmaPeriod
        {  15,  40  }, // AdxThreshold
        {  12, 120  }, // LookbackCandles
        {  20,  40  }, // RsiOversold
        { 5.0, 15.0 }, // RsiDivThreshold
        { 3.0, 10.0 }, // MinDeclineAtrMult
        { 0.3,  2.0 }, // StopLossAtrMult
        { 2.0, 10.0 }, // TakeProfitAtrMult
        { 1.0,  4.0 }, // TrailingActivationAtrMult
        { 1.0,  5.0 }, // TrailingStopAtrMult
        {  24, 120  }, // MaxHoldCandles
        {0.01, 0.05 }, // PositionSizePct
    };

    public double[] ToVector() =>
    [
        EmaPeriod, AdxThreshold, LookbackCandles, RsiOversold, RsiDivThreshold,
        MinDeclineAtrMult, StopLossAtrMult, TakeProfitAtrMult,
        TrailingActivationAtrMult, TrailingStopAtrMult, MaxHoldCandles, PositionSizePct,
    ];

    public static SwingLongGenotype FromVector(double[] v) => new()
    {
        EmaPeriod                 = Math.Clamp((int)Math.Round(v[0]),  20, 100),
        AdxThreshold              = Math.Clamp(v[1],  15.0, 40.0),
        LookbackCandles           = Math.Clamp((int)Math.Round(v[2]),  12, 120),
        RsiOversold               = Math.Clamp(v[3],  20.0, 40.0),
        RsiDivThreshold           = Math.Clamp(v[4],   5.0, 15.0),
        MinDeclineAtrMult         = Math.Clamp(v[5],   3.0, 10.0),
        StopLossAtrMult           = Math.Clamp(v[6],   0.3,  2.0),
        TakeProfitAtrMult         = Math.Clamp(v[7],   2.0, 10.0),
        TrailingActivationAtrMult = Math.Clamp(v[8],   1.0,  4.0),
        TrailingStopAtrMult       = Math.Clamp(v[9],   1.0,  5.0),
        MaxHoldCandles            = Math.Clamp((int)Math.Round(v[10]), 24, 120),
        PositionSizePct           = Math.Clamp(v[11], 0.01, 0.05),
    };

    public static SwingLongGenotype Random(System.Random rng, SwingLongGenotype? seed = null)
    {
        T Seed<T>(T random, T seeded) => seed == null ? random : seeded;
        return new()
        {
            EmaPeriod                 = Seed(rng.Next(20, 101),                    seed?.EmaPeriod                 ?? 50),
            AdxThreshold              = Seed(15.0 + rng.NextDouble() * 25.0,       seed?.AdxThreshold              ?? 25.0),
            LookbackCandles           = Seed(rng.Next(12, 121),                    seed?.LookbackCandles           ?? 48),
            RsiOversold               = Seed(20.0 + rng.NextDouble() * 20.0,       seed?.RsiOversold               ?? 30.0),
            RsiDivThreshold           = Seed(5.0  + rng.NextDouble() * 10.0,       seed?.RsiDivThreshold           ?? 8.0),
            MinDeclineAtrMult         = Seed(3.0  + rng.NextDouble() * 7.0,        seed?.MinDeclineAtrMult         ?? 5.0),
            StopLossAtrMult           = Seed(0.3  + rng.NextDouble() * 1.7,        seed?.StopLossAtrMult           ?? 0.8),
            TakeProfitAtrMult         = Seed(2.0  + rng.NextDouble() * 8.0,        seed?.TakeProfitAtrMult         ?? 5.0),
            TrailingActivationAtrMult = Seed(1.0  + rng.NextDouble() * 3.0,        seed?.TrailingActivationAtrMult ?? 2.0),
            TrailingStopAtrMult       = Seed(1.0  + rng.NextDouble() * 4.0,        seed?.TrailingStopAtrMult       ?? 2.0),
            MaxHoldCandles            = Seed(rng.Next(24, 121),                    seed?.MaxHoldCandles            ?? 42),
            PositionSizePct           = Seed(0.01 + rng.NextDouble() * 0.04,       seed?.PositionSizePct           ?? 0.03),
        };
    }

    public static SwingLongGenotype Crossover(SwingLongGenotype a, SwingLongGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            EmaPeriod                 = Pick(a.EmaPeriod,                 b.EmaPeriod),
            AdxThreshold              = Pick(a.AdxThreshold,              b.AdxThreshold),
            LookbackCandles           = Pick(a.LookbackCandles,           b.LookbackCandles),
            RsiOversold               = Pick(a.RsiOversold,               b.RsiOversold),
            RsiDivThreshold           = Pick(a.RsiDivThreshold,           b.RsiDivThreshold),
            MinDeclineAtrMult         = Pick(a.MinDeclineAtrMult,         b.MinDeclineAtrMult),
            StopLossAtrMult           = Pick(a.StopLossAtrMult,           b.StopLossAtrMult),
            TakeProfitAtrMult         = Pick(a.TakeProfitAtrMult,         b.TakeProfitAtrMult),
            TrailingActivationAtrMult = Pick(a.TrailingActivationAtrMult, b.TrailingActivationAtrMult),
            TrailingStopAtrMult       = Pick(a.TrailingStopAtrMult,       b.TrailingStopAtrMult),
            MaxHoldCandles            = Pick(a.MaxHoldCandles,            b.MaxHoldCandles),
            PositionSizePct           = Pick(a.PositionSizePct,           b.PositionSizePct),
        };
    }

    public SwingLongGenotype Mutate(System.Random rng, double rate)
    {
        double Nudge(double val, double min, double max, double scale)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + (rng.NextDouble() - 0.5) * scale, min, max);
        }
        int NudgeInt(int val, int min, int max, int step = 3)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + rng.Next(-step, step + 1), min, max);
        }
        return new SwingLongGenotype
        {
            EmaPeriod                 = NudgeInt(EmaPeriod,       20, 100, 10),
            AdxThreshold              = Nudge(AdxThreshold,       15.0, 40.0, 4.0),
            LookbackCandles           = NudgeInt(LookbackCandles, 12, 120, 8),
            RsiOversold               = Nudge(RsiOversold,        20.0, 40.0, 3.0),
            RsiDivThreshold           = Nudge(RsiDivThreshold,    5.0, 15.0, 2.0),
            MinDeclineAtrMult         = Nudge(MinDeclineAtrMult,  3.0, 10.0, 1.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,    0.3, 2.0, 0.3),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,  2.0, 10.0, 1.5),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.0, 4.0, 0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult, 1.0, 5.0, 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles,  24, 120, 12),
            PositionSizePct           = Nudge(PositionSizePct,    0.01, 0.05, 0.005),
        };
    }

    public SwingLongGenotype ClampToBounds() => new()
    {
        EmaPeriod                 = Math.Clamp(EmaPeriod,       20, 100),
        AdxThreshold              = Math.Clamp(AdxThreshold,    15.0, 40.0),
        LookbackCandles           = Math.Clamp(LookbackCandles, 12, 120),
        RsiOversold               = Math.Clamp(RsiOversold,     20.0, 40.0),
        RsiDivThreshold           = Math.Clamp(RsiDivThreshold, 5.0, 15.0),
        MinDeclineAtrMult         = Math.Clamp(MinDeclineAtrMult, 3.0, 10.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,  0.3, 2.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult, 2.0, 10.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.0, 4.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult, 1.0, 5.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,  24, 120),
        PositionSizePct           = Math.Clamp(PositionSizePct, 0.01, 0.05),
        Fitness = Fitness,
    };

    public override string ToString() =>
        $"EMA{EmaPeriod} RSI(7,OS={RsiOversold:F0},div≥{RsiDivThreshold:F0}pts) " +
        $"ADX(7,{AdxThreshold:F0}) Look={LookbackCandles} Decline≥{MinDeclineAtrMult:F1}A " +
        $"SL={StopLossAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars Pos={PositionSizePct:P0} F={Fitness:F4}";
}
```

- [ ] **Step 3: Add `SwingLongGenotypeDto` to `GenotypeDto.cs`**

Append after the last `}` closing the file (before the file end), following the same pattern as `DipLongGenotypeDto`:

```csharp
class SwingLongGenotypeDto
{
    public int    EmaPeriod                 { get; set; }
    public double AdxThreshold              { get; set; }
    public int    LookbackCandles           { get; set; }
    public double RsiOversold               { get; set; }
    public double RsiDivThreshold           { get; set; }
    public double MinDeclineAtrMult         { get; set; }
    public double StopLossAtrMult           { get; set; }
    public double TakeProfitAtrMult         { get; set; }
    public double TrailingActivationAtrMult { get; set; }
    public double TrailingStopAtrMult       { get; set; }
    public int    MaxHoldCandles            { get; set; }
    public double PositionSizePct           { get; set; }
    public double Fitness                   { get; set; }

    public static SwingLongGenotypeDto From(SwingLongGenotype g) => new()
    {
        EmaPeriod                 = g.EmaPeriod,
        AdxThreshold              = g.AdxThreshold,
        LookbackCandles           = g.LookbackCandles,
        RsiOversold               = g.RsiOversold,
        RsiDivThreshold           = g.RsiDivThreshold,
        MinDeclineAtrMult         = g.MinDeclineAtrMult,
        StopLossAtrMult           = g.StopLossAtrMult,
        TakeProfitAtrMult         = g.TakeProfitAtrMult,
        TrailingActivationAtrMult = g.TrailingActivationAtrMult,
        TrailingStopAtrMult       = g.TrailingStopAtrMult,
        MaxHoldCandles            = g.MaxHoldCandles,
        PositionSizePct           = g.PositionSizePct,
        Fitness                   = g.Fitness,
    };

    public SwingLongGenotype ToGenotype() => new SwingLongGenotype
    {
        EmaPeriod                 = EmaPeriod,
        AdxThreshold              = AdxThreshold,
        LookbackCandles           = LookbackCandles,
        RsiOversold               = RsiOversold,
        RsiDivThreshold           = RsiDivThreshold,
        MinDeclineAtrMult         = MinDeclineAtrMult,
        StopLossAtrMult           = StopLossAtrMult,
        TakeProfitAtrMult         = TakeProfitAtrMult,
        TrailingActivationAtrMult = TrailingActivationAtrMult,
        TrailingStopAtrMult       = TrailingStopAtrMult,
        MaxHoldCandles            = MaxHoldCandles,
        PositionSizePct           = PositionSizePct,
        Fitness                   = Fitness,
    };
}
```

- [ ] **Step 4: Build and verify**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add SwingLongGenotype.cs Config.cs GenotypeDto.cs
git commit -m "feat: SwingLongGenotype with Bounds/ToVector/FromVector + DTO + Config entry"
```

---

## Task 2: SwingLongSimulator

**Files:**
- Modify: `SwingSimulator.cs` (append new `SwingLongSimulator` static class at end of file)

- [ ] **Step 1: Append `SwingLongSimulator` to `SwingSimulator.cs`**

Read the end of the file to find the closing `}` of `FadeShortSimulator`, then append after it:

```csharp
// ── SwingLong simulator ──────────────────────────────────────────────────────────────────
// Mirror of FadeShortSimulator: RSI bullish divergence + bullish BoS.
// h1  = setup (EMA/RSI/ADX regime gate, swing-low lookback, RSI divergence)
// m15 = precision entry (bullish BoS: close > prev 15m high)
// h4  = exit sizing ATR (aggregated from h1; matches multi-day holding timeframe)
public static class SwingLongSimulator
{
    internal const int AtrPeriod = 14;
    internal const int RsiPeriod =  7;
    internal const int AdxPeriod =  7;

    private const double FeeExchange = 0.11;
    private const double SlipK       = 0.025;
    private const double SlipStopGap = 0.030;

    public record SwingLongTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,
        double Target,
        bool   TrailArmed,
        double TrailHigh,   // highest price seen since entry (trail reference for long)
        int    HoldCount);

    public static List<(DateTime Time, double Return, string Kind)> GetSwingLongReturns(
        SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (trades, _) = RunSwingLongMultiTF(g, h1, m15);
        return trades;
    }

    public static SwingLongTradeState GetSwingLongTradeState(
        SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var (_, state) = RunSwingLongMultiTF(g, h1, m15);
        return state;
    }

    private static (List<(DateTime, double, string)> Trades, SwingLongTradeState FinalState)
        RunSwingLongMultiTF(SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        int h1Warmup = Math.Max(Math.Max(g.EmaPeriod, RsiPeriod), AdxPeriod * 2 + 1)
                       + g.LookbackCandles + 2;

        if (h1.Length <= h1Warmup + 2 || m15.Length < (h1Warmup + 2) * 4)
            return ([], new SwingLongTradeState(false, 0, 0, 0, false, 0, 0));

        var h1Closes = CandleExt.Closes(h1);
        var h1Highs  = CandleExt.Highs(h1);
        var h1Lows   = CandleExt.Lows(h1);

        var h1Ema = Indicators.Ema(h1Closes, g.EmaPeriod);
        var h1Rsi = Indicators.Rsi(h1Closes, RsiPeriod);
        var h1Adx = Indicators.Adx(h1Highs, h1Lows, h1Closes, AdxPeriod);
        var h1Atr = Indicators.Atr(h1Highs, h1Lows, h1Closes, AtrPeriod);

        var h4      = FadeShortSimulator.AggregateCandles(h1.ToArray(), 4);
        var h4Highs = CandleExt.Highs(h4);
        var h4Lows  = CandleExt.Lows(h4);
        var h4Cls   = CandleExt.Closes(h4);
        var h4Atr   = Indicators.Atr(h4Highs, h4Lows, h4Cls, AtrPeriod);

        var m15Closes = CandleExt.Closes(m15);
        var m15Highs  = CandleExt.Highs(m15);

        var result = new List<(DateTime, double, string)>();

        bool   inTrade    = false;
        double entry      = 0;
        double hardStop   = 0;
        double target     = 0;
        double trailHigh  = 0;
        double atrEntry   = 0;
        bool   trailArmed = false;
        int    entryIH1   = 0;

        int    cachedH1Ref    = -1;
        bool   cachedSetupMet = false;
        double cachedSwingLow = 0;
        double cachedAtrRef   = 0;

        int m15Start = (h1Warmup + 1) * 4;
        int m15Limit = h1.Length * 4;

        for (int im15 = m15Start; im15 < Math.Min(m15.Length, m15Limit); im15++)
        {
            int ih1   = im15 / 4;
            int h1Ref = ih1 - 1;

            if (h1Ref < h1Warmup || h1Ref >= h1.Length) continue;

            double m15Price = m15Closes[im15];

            if (!inTrade)
            {
                if (h1Ref != cachedH1Ref)
                {
                    cachedH1Ref    = h1Ref;
                    cachedSetupMet = false;

                    double atrH1 = h1Atr[h1Ref] > 1e-10 ? h1Atr[h1Ref] : h1Closes[h1Ref] * 0.02;

                    int    h4Ref = h1Ref / 4;
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref] : atrH1 * 4;

                    // Trend gate: price above EMA and ADX trending
                    if (h1Adx[h1Ref] >= g.AdxThreshold && h1Closes[h1Ref] > h1Ema[h1Ref])
                    {
                        int    lb         = g.LookbackCandles;
                        int    lbStart    = Math.Max(0, h1Ref - lb);
                        double swingLow   = h1Closes[lbStart];
                        int    lowIdx     = lbStart;
                        double recentHigh = h1Highs[lbStart];

                        for (int j = lbStart; j < h1Ref; j++)
                        {
                            if (h1Closes[j] < swingLow)  { swingLow = h1Closes[j]; lowIdx = j; }
                            if (h1Highs[j]  > recentHigh)  recentHigh = h1Highs[j];
                        }

                        // Min decline filter: real pullback, not noise
                        bool bigDrop = (recentHigh - swingLow) >= g.MinDeclineAtrMult * atrH1;

                        // RSI bullish divergence: swingLow RSI was oversold AND current RSI recovered
                        double rsiAtLow = h1Rsi[lowIdx];
                        bool diverging  = rsiAtLow <= g.RsiOversold
                                       && h1Rsi[h1Ref] >= rsiAtLow + g.RsiDivThreshold;

                        // 1h BoS: close above previous candle's high (bullish)
                        bool h1Bos = h1Closes[h1Ref] > h1Highs[h1Ref - 1];

                        if (bigDrop && diverging && h1Bos)
                        {
                            cachedSetupMet = true;
                            cachedSwingLow = swingLow;
                            cachedAtrRef   = atrH4;
                        }
                    }
                }

                // 15m bullish BoS: close above previous 15m candle's high
                if (cachedSetupMet && m15Closes[im15] > m15Highs[im15 - 1])
                {
                    inTrade    = true;
                    entry      = m15Price;
                    atrEntry   = cachedAtrRef;
                    hardStop   = cachedSwingLow - g.StopLossAtrMult * atrEntry;
                    target     = entry + g.TakeProfitAtrMult * atrEntry;
                    trailHigh  = m15Price;
                    trailArmed = false;
                    entryIH1   = ih1;
                }
            }
            else
            {
                if (m15Price > trailHigh) trailHigh = m15Price;
                if (!trailArmed && trailHigh - entry >= g.TrailingActivationAtrMult * atrEntry)
                    trailArmed = true;

                int holdH1 = ih1 - entryIH1;

                bool hitStop   = m15Price <= hardStop;
                bool hitTarget = m15Price >= target;
                bool hitTrail  = trailArmed && m15Price < trailHigh - g.TrailingStopAtrMult * atrEntry;
                bool timedOut  = holdH1 >= g.MaxHoldCandles;

                if (hitStop || hitTarget || hitTrail || timedOut)
                {
                    double exitPx = hitStop   ? hardStop :
                                    hitTarget ? target   : m15Price;
                    double atrPct = atrEntry / entry * 100.0;
                    double slip   = SlipK * atrPct;
                    double stopSlip = hitStop ? SlipStopGap * atrPct : 0;
                    double cost   = FeeExchange + slip * 2 + stopSlip;
                    double ret    = (exitPx - entry) / entry * 100.0 - cost;
                    result.Add((m15[im15].Time, ret, "swing_long"));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx = m15Closes[^1];
            double atrPct  = atrEntry / entry * 100.0;
            double ret     = (finalPx - entry) / entry * 100.0 - (FeeExchange + SlipK * atrPct * 2);
            result.Add((m15[^1].Time, ret, "swing_long"));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new SwingLongTradeState(inTrade, entry, hardStop, target, trailArmed, trailHigh, finalHold));
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add SwingSimulator.cs
git commit -m "feat: SwingLongSimulator — bull divergence+BoS long, mirrors FadeShort logic"
```

---

## Task 3: SwingLongGA

**Files:**
- Create: `SwingLongGA.cs`

- [ ] **Step 1: Create `SwingLongGA.cs`**

```csharp
using System.Collections.Concurrent;

namespace TradingGA;

// SwingLong GA — mirrors DipLongGA structure but uses SwingLongGenotype.
// 5-fold walk-forward CV; FoldScore includes retentionMult to penalise giving back gains.
// No protection-mode genes (unlike DipLong): SwingLong is simpler and router-gated.
public class SwingLongGA
{
    public record CoinData(
        ReadOnlyMemory<Candle> TrainH1,  ReadOnlyMemory<Candle> ValH1,
        ReadOnlyMemory<Candle> TrainM15, ReadOnlyMemory<Candle> ValM15,
        double Weight = 1.0);

    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly int    _eliteCount;
    private readonly int    _migrationInterval;
    private readonly bool   _verbose;
    private readonly Random _rng = new();

    public SwingLongGA(
        int populationSize    = 60,
        int generations       = 100,
        int eliteCount        = 10,
        int migrationInterval = 10,
        bool verbose          = false)
    {
        _populationSize    = populationSize;
        _generations       = generations;
        _eliteCount        = eliteCount;
        _migrationInterval = migrationInterval;
        _verbose           = verbose;
    }

    private static double FoldScore(
        SwingLongGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15)
    {
        var trades = SwingLongSimulator.GetSwingLongReturns(g, h1, m15);
        if (trades.Count < 3) return -1.0;

        double balance = 1.0, peak = 1.0;
        foreach (var t in trades)
            balance *= 1.0 + t.Return / 100.0 * g.PositionSizePct;
        if (balance > peak) peak = balance;

        // Replay properly to track peak
        balance = 1.0; peak = 1.0;
        foreach (var t in trades)
        {
            balance *= 1.0 + t.Return / 100.0 * g.PositionSizePct;
            if (balance > peak) peak = balance;
        }

        double gain    = balance - 1.0;
        double peakGain = peak - 1.0;
        double wins    = trades.Count(t => t.Return > 0);
        double wr      = wins / trades.Count;
        double wrMult  = Math.Pow(wr / 0.50, 2.0);
        double ddDiv   = peak > 1e-10 ? Math.Max(1.0, (peak - Math.Min(balance, 1.0)) / peak * 10.0) : 1.0;
        double freqBonus  = Math.Min(1.5, 1.0 + (trades.Count - 3) * 0.05);
        double qualityMult = trades.Count > 0
            ? trades.Average(t => t.Return > 0 ? 1.0 + t.Return * 0.01 : 1.0 / (1.0 - t.Return * 0.01))
            : 1.0;

        double retentionMult = peakGain > 0.01
            ? Math.Max(0.2, gain / peakGain)
            : 1.0;

        return gain * 100.0 * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
    }

    private double Fitness(SwingLongGenotype g, IReadOnlyList<CoinData> coins, bool useValidation)
    {
        var scores = new ConcurrentBag<double>();
        Parallel.ForEach(coins, coin =>
        {
            var h1  = useValidation ? coin.ValH1.Span  : coin.TrainH1.Span;
            var m15 = useValidation ? coin.ValM15.Span : coin.TrainM15.Span;
            if (h1.Length < 50) return;
            double s = FoldScore(g, h1, m15);
            if (!double.IsNaN(s)) scores.Add(s * coin.Weight);
        });
        if (scores.IsEmpty) return -1.0;
        var list = scores.ToList();
        double mean = list.Average();
        double std  = Math.Sqrt(list.Select(s => (s - mean) * (s - mean)).Average());
        return mean - 0.75 * std;
    }

    public SwingLongGenotype Run(IReadOnlyList<CoinData> coins, SwingLongGenotype? seed = null)
    {
        if (coins.Count == 0) throw new ArgumentException("No training data.");

        if (_verbose && seed != null) Console.WriteLine($"  Seeding from: {seed}");

        var population = Enumerable
            .Range(0, _populationSize)
            .Select(_ => SwingLongGenotype.Random(_rng, seed))
            .ToList();

        if (seed != null)
        {
            var clamped = seed.ClampToBounds();
            population[0] = clamped;
            int seedCount = Math.Min(_populationSize / 5, _populationSize - 1);
            for (int s = 1; s <= seedCount; s++)
                population[s] = clamped.Mutate(_rng, 0.25);
        }

        List<SwingLongGenotype> eliteIsland    = new();
        double                  bestFitnessSeen = double.MinValue;
        int                     stagnantGens    = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            double baseMutRate  = 0.6 * (1.0 - (double)gen / _generations) + 0.05;
            double mutationRate = stagnantGens >= 15 ? Math.Min(baseMutRate * 2.0, 0.9) : baseMutRate;

            Parallel.ForEach(population, ind =>
                ind.Fitness = Fitness(ind, coins, useValidation: false));

            population  = population.OrderByDescending(g => g.Fitness).ToList();
            eliteIsland = population.Take(_eliteCount).ToList();

            double topFitness = eliteIsland.First().Fitness;
            if (topFitness > bestFitnessSeen + 1e-6) { bestFitnessSeen = topFitness; stagnantGens = 0; }
            else stagnantGens++;

            if (_verbose && (gen + 1) % _migrationInterval == 0)
            {
                string tag = stagnantGens >= 15 ? $" [STAGNANT×{stagnantGens} boost]" : "";
                Console.WriteLine($"Gen {gen + 1,3} — elite: {eliteIsland.First()}{tag}");
            }

            var nextGen = new List<SwingLongGenotype>();
            nextGen.AddRange(eliteIsland.Take(5));
            while (nextGen.Count < _populationSize)
            {
                var a = eliteIsland[_rng.Next(eliteIsland.Count)];
                var b = population[_rng.Next(Math.Min(population.Count, _populationSize / 2))];
                nextGen.Add(SwingLongGenotype.Crossover(a, b, _rng).Mutate(_rng, mutationRate));
            }
            population = nextGen;
        }

        // Final rescore on val set
        Parallel.ForEach(eliteIsland, ind =>
            ind.Fitness = Fitness(ind, coins, useValidation: true));
        return eliteIsland.OrderByDescending(g => g.Fitness).First();
    }
}
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add SwingLongGA.cs
git commit -m "feat: SwingLongGA with retentionMult FoldScore"
```

---

## Task 4: SwingLong train/backtest modes

**Files:**
- Modify: `LongTrainCommands.cs` (add `RunSwingLongTrain`)
- Modify: `Program.cs` (add `swingLongtrain` and `swingLongbacktest` cases)

- [ ] **Step 1: Add `RunSwingLongTrain` to `LongTrainCommands.cs`**

Append to the `LongTrainCommands` class (before its closing `}`):

```csharp
public static async Task RunSwingLongTrain(BybitRestClient client)
{
    Console.WriteLine($"=== Gravity-gen2 | SWINGLONG TRAIN (bull divergence long, 1h+15m, {Config.BacktestCoins.Length} coins, ~3yr) ===\n");

    Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
    var sem = new SemaphoreSlim(4);
    var fetchTasks = Config.BacktestCoins.Select(async sym =>
    {
        await sem.WaitAsync();
        try
        {
            var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
            var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
            return (sym, h1, m15.ToArray());
        }
        finally { sem.Release(); }
    });
    var fetched = await Task.WhenAll(fetchTasks);

    var coins = new List<SwingLongGA.CoinData>();
    foreach (var (sym, h1, m15) in fetched)
    {
        if (h1.Length < 150) continue;
        var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
        if (volUsd.Count > 0 && volUsd[volUsd.Count / 2] < Config.MinMedianVolUsdM) continue;

        // 20% val split from the end
        int valStart = (int)(h1.Length * 0.80);
        int m15Val   = valStart * 4;
        coins.Add(new SwingLongGA.CoinData(
            h1[..valStart],  h1[valStart..],
            m15[..m15Val],   m15[m15Val..]));
        Console.WriteLine($"  {sym}: {h1.Length} h1  val=[{valStart}..{h1.Length - 1}]");
    }
    if (coins.Count == 0) { Console.WriteLine("No data."); return; }

    SwingLongGenotype? seed = null;
    if (File.Exists(Config.SwingLongGenoFile))
    {
        var candidate = JsonSerializer.Deserialize<SwingLongGenotypeDto>(
            File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype();
        if (candidate.Fitness > 0) { seed = candidate; Console.WriteLine($"  Seeding: {seed}"); }
    }

    var best = new SwingLongGA(80, 150, verbose: true).Run(coins, seed);
    Console.WriteLine($"\nFrozen genotype:\n  {best}\n");
    File.WriteAllText(Config.SwingLongGenoFile,
        JsonSerializer.Serialize(SwingLongGenotypeDto.From(best),
            new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"  Saved → {Config.SwingLongGenoFile}");

    // Quick overfit check
    var tRet = coins.Where(cd => cd.TrainH1.Length > 0)
                    .SelectMany(cd => SwingLongSimulator.GetSwingLongReturns(best, cd.TrainH1.Span, cd.TrainM15.Span))
                    .Select(t => t.Return).ToList();
    var vRet = coins.Where(cd => cd.ValH1.Length > 0)
                    .SelectMany(cd => SwingLongSimulator.GetSwingLongReturns(best, cd.ValH1.Span, cd.ValM15.Span))
                    .Select(t => t.Return).ToList();
    Console.WriteLine($"\n  Train: {tRet.Count} trades  avg={( tRet.Count>0 ? tRet.Average():0 ):+0.000;-0.000}%");
    Console.WriteLine($"  Val:   {vRet.Count} trades  avg={( vRet.Count>0 ? vRet.Average():0 ):+0.000;-0.000}%");
    double vExp = vRet.Count > 0 ? vRet.Average() : 0;
    double tExp = tRet.Count > 0 ? tRet.Average() : 0;
    Console.WriteLine(vExp < tExp * 0.4 || vExp <= 0
        ? "  !! Possible overfit — val expectancy < 40% of train"
        : "  OK — val expectancy within acceptable range");
}
```

- [ ] **Step 2: Add run modes to `Program.cs`**

In the switch statement in `Program.cs`, add after the `diplongtrain` case:

```csharp
case "swingLongtrain":   await LongTrainCommands.RunSwingLongTrain(client);              break;
```

Also add the help line in the help block:
```csharp
Console.WriteLine("  dotnet run -- swingLongtrain      SwingLong GA: bull divergence+BoS long, 93 coins, ~3yr");
```

- [ ] **Step 3: Build and verify**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add LongTrainCommands.cs Program.cs
git commit -m "feat: swingLongtrain mode — SwingLong GA training pipeline"
```

---

## Task 5: BayesianOptimizer — add ToVector/FromVector/Bounds to FadeShortGenotype

FadeLongGenotype and DipLongGenotype already have `Bounds`, `ToVector()`, `FromVector()`. FadeShortGenotype (in `SwingGenotype.cs`) does not. Add them.

**Files:**
- Modify: `SwingGenotype.cs`

- [ ] **Step 1: Add Bayesian interface to `FadeShortGenotype` in `SwingGenotype.cs`**

After the `ClampToBounds()` method and before `ToString()`, insert:

```csharp
// ── Bayesian optimiser interface ──────────────────────────────────────────
// Order matches ToVector / FromVector.
public static readonly double[,] Bounds =
{
    {  20, 100  }, // EmaPeriod
    {  22,  45  }, // AdxThreshold
    {  12, 120  }, // LookbackCandles
    {  65,  80  }, // RsiOverbought
    { 5.0, 15.0 }, // RsiDivThreshold
    { 5.0, 12.0 }, // MinRallyAtrMult
    { 0.3,  2.0 }, // StopLossAtrMult
    { 1.5,  4.0 }, // MaeAtrMult
    { 2.0, 10.0 }, // TakeProfitAtrMult
    { 1.0,  4.0 }, // TrailingActivationAtrMult
    { 1.0,  5.0 }, // TrailingStopAtrMult
    {  24, 120  }, // MaxHoldCandles
    {0.01, 0.05 }, // PositionSizePct
};

public double[] ToVector() =>
[
    EmaPeriod, AdxThreshold, LookbackCandles,
    RsiOverbought, RsiDivThreshold, MinRallyAtrMult,
    StopLossAtrMult, MaeAtrMult, TakeProfitAtrMult,
    TrailingActivationAtrMult, TrailingStopAtrMult,
    MaxHoldCandles, PositionSizePct,
];

public static FadeShortGenotype FromVector(double[] v) => new()
{
    EmaPeriod        = Math.Clamp((int)Math.Round(v[0]),  20, 100),
    AdxThreshold     = Math.Clamp(v[1],  22.0, 45.0),
    LookbackCandles  = Math.Clamp((int)Math.Round(v[2]),  12, 120),
    RsiOverbought    = Math.Clamp(v[3],  65.0, 80.0),
    RsiDivThreshold  = Math.Clamp(v[4],   5.0, 15.0),
    MinRallyAtrMult  = Math.Clamp(v[5],   5.0, 12.0),
    StopLossAtrMult           = Math.Clamp(v[6],  0.3,  2.0),
    MaeAtrMult                = Math.Clamp(v[7],  1.5,  4.0),
    TakeProfitAtrMult         = Math.Clamp(v[8],  2.0, 10.0),
    TrailingActivationAtrMult = Math.Clamp(v[9],  1.0,  4.0),
    TrailingStopAtrMult       = Math.Clamp(v[10], 1.0,  5.0),
    MaxHoldCandles            = Math.Clamp((int)Math.Round(v[11]), 24, 120),
    PositionSizePct           = Math.Clamp(v[12], 0.01, 0.05),
};
```

- [ ] **Step 2: Build and verify**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 3: Commit**

```bash
git add SwingGenotype.cs
git commit -m "feat: add Bounds/ToVector/FromVector to FadeShortGenotype for Bayesian refinement"
```

---

## Task 6: Wire BayesianOptimizer after GA training

Apply a 60-iteration TPE refinement pass after each GA. FadeLong, DipLong, FadeShort already have `Bounds`/`ToVector`/`FromVector`. SwingLong added in Task 1/5.

**Files:**
- Modify: `TrainCommands.cs` (FadeShort)
- Modify: `LongTrainCommands.cs` (FadeLong, DipLong, SwingLong)

The helper pattern is the same for all four. For each GA:
1. Seed TPE with top-5 elite vectors
2. Evaluate via the same scorer used in the GA
3. Return best of GA elite vs TPE best

- [ ] **Step 1: Add Bayesian refinement to FadeShort in `TrainCommands.cs`**

After `var best = new FadeShortGA(80, 150, verbose: true).Run(coinData, seed);` and before the `Console.WriteLine($"\nFrozen genotype")` line, insert:

```csharp
Console.WriteLine("\n─── Bayesian refinement (60 TPE iterations) ───");
var rngBo  = new Random(42);
var boSeed = coinData.Take(5)
    .Select(cd => (cd.TrainCandles.ToArray(), cd.ValCandles.ToArray()))
    .ToList();
// Build seed history from GA elites by evaluating their vectors
var boHistory = new List<(double[] Params, double Fitness)>();
boHistory.Add((best.ToVector(), best.Fitness));
var boHistory2 = BayesianOptimizer.Refine(
    boHistory,
    FadeShortGenotype.Bounds,
    v =>
    {
        var g = FadeShortGenotype.FromVector(v);
        return coinData.SelectMany(cd =>
            FadeShortSimulator.GetFadeShortReturns(g, cd.TrainCandles.Span))
            .Select(t => t.Return)
            .DefaultIfEmpty(-1.0)
            .Average();
    },
    iterations: 60,
    rng: rngBo);
var boParams = boHistory2.OrderByDescending(h => h.Fitness).First().Params;
var boGeno   = FadeShortGenotype.FromVector(boParams);
boGeno.Fitness = best.Fitness; // will be rescored on val below
if (boGeno.Fitness > best.Fitness) { best = boGeno; Console.WriteLine($"  TPE improved: {best}"); }
else Console.WriteLine($"  GA elite kept (TPE did not improve)");
```

Note: the evaluate lambda for FadeShort uses single-TF candles (TrainCandles, not h1+m15) because it uses the 4h version. Verify `CoinData` field names in `FadeShortGA.CoinData` before applying — use the correct field that holds the training span.

- [ ] **Step 2: Add Bayesian refinement to FadeLong in `LongTrainCommands.cs`**

After `var flBest = new FadeLongGA(80, 150, verbose: true).Run(flCoins, flSeed);`, insert:

```csharp
Console.WriteLine("\n─── Bayesian refinement for FadeLong (60 TPE iterations) ───");
var flRng = new Random(42);
var flBoHistory = new List<(double[] Params, double Fitness)>
{
    (flBest.ToVector(), flBest.Fitness)
};
var flBoResult = BayesianOptimizer.Refine(
    flBoHistory,
    FadeLongGenotype.Bounds,
    v =>
    {
        var g  = FadeLongGenotype.FromVector(v);
        var ts = flCoins.Where(cd => cd.TrainH1.Length > 0)
                        .SelectMany(cd => FadeLongSimulator.GetFadeLongReturns(g, cd.TrainH1.Span, cd.TrainM15.Span))
                        .Select(t => t.Return).ToList();
        return ts.Count > 0 ? ts.Average() : -1.0;
    },
    iterations: 60,
    rng: flRng);
var flBoParams = flBoResult.OrderByDescending(h => h.Fitness).First().Params;
var flBoGeno   = FadeLongGenotype.FromVector(flBoParams);
flBoGeno.Fitness = flBest.Fitness;
if (flBoGeno.Fitness > flBest.Fitness) { flBest = flBoGeno; Console.WriteLine($"  TPE improved: {flBest}"); }
else Console.WriteLine($"  GA elite kept");
```

- [ ] **Step 3: Add Bayesian refinement to DipLong in `LongTrainCommands.cs`**

After `var dlBest = new DipLongGA(80, 150, verbose: true).Run(dlCoins, dlSeed);`, insert:

```csharp
Console.WriteLine("\n─── Bayesian refinement for DipLong (60 TPE iterations) ───");
var dlRng = new Random(42);
var dlBoHistory = new List<(double[] Params, double Fitness)>
{
    (dlBest.ToVector(), dlBest.Fitness)
};
var dlBoResult = BayesianOptimizer.Refine(
    dlBoHistory,
    DipLongGenotype.Bounds,
    v =>
    {
        var g  = DipLongGenotype.FromVector(v);
        var ts = dlCoins.Where(cd => cd.TrainH1.Length > 0)
                        .SelectMany(cd => DipLongSimulator.GetDipLongReturns(g, cd.TrainH1.Span, cd.TrainM15.Span))
                        .Select(t => t.Return).ToList();
        return ts.Count > 0 ? ts.Average() : -1.0;
    },
    iterations: 60,
    rng: dlRng);
var dlBoParams = dlBoResult.OrderByDescending(h => h.Fitness).First().Params;
var dlBoGeno   = DipLongGenotype.FromVector(dlBoParams);
dlBoGeno.Fitness = dlBest.Fitness;
if (dlBoGeno.Fitness > dlBest.Fitness) { dlBest = dlBoGeno; Console.WriteLine($"  TPE improved: {dlBest}"); }
else Console.WriteLine($"  GA elite kept");
```

- [ ] **Step 4: Add Bayesian refinement to SwingLong in `LongTrainCommands.cs`**

After `var best = new SwingLongGA(80, 150, verbose: true).Run(coins, seed);` in `RunSwingLongTrain`, insert:

```csharp
Console.WriteLine("\n─── Bayesian refinement (60 TPE iterations) ───");
var slRng = new Random(42);
var slBoHistory = new List<(double[] Params, double Fitness)>
{
    (best.ToVector(), best.Fitness)
};
var slBoResult = BayesianOptimizer.Refine(
    slBoHistory,
    SwingLongGenotype.Bounds,
    v =>
    {
        var g  = SwingLongGenotype.FromVector(v);
        var ts = coins.Where(cd => cd.TrainH1.Length > 0)
                      .SelectMany(cd => SwingLongSimulator.GetSwingLongReturns(g, cd.TrainH1.Span, cd.TrainM15.Span))
                      .Select(t => t.Return).ToList();
        return ts.Count > 0 ? ts.Average() : -1.0;
    },
    iterations: 60,
    rng: slRng);
var slBoParams = slBoResult.OrderByDescending(h => h.Fitness).First().Params;
var slBoGeno   = SwingLongGenotype.FromVector(slBoParams);
slBoGeno.Fitness = best.Fitness;
if (slBoGeno.Fitness > best.Fitness) { best = slBoGeno; Console.WriteLine($"  TPE improved: {best}"); }
else Console.WriteLine($"  GA elite kept");
```

- [ ] **Step 5: Build and verify**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 6: Commit**

```bash
git add TrainCommands.cs LongTrainCommands.cs
git commit -m "feat: BayesianOptimizer 60-iteration TPE refinement after FadeShort/FadeLong/DipLong/SwingLong GA"
```

---

## Task 7: FadeLong bear-window training restriction

Filter FadeLong training candles to BTC bear-regime windows, sharpening the fitness signal.

**Files:**
- Modify: `LongTrainCommands.cs`

- [ ] **Step 1: Add `GetBearWindows` helper to `LongTrainCommands.cs`**

At the top of the `LongTrainCommands` static class body, add:

```csharp
private const int BearWindowMinBars = 200; // ~8 days of 1h bars

private static List<(DateTime Start, DateTime End)> GetBearWindows(RegimeBar[] series, int minBars)
{
    var windows  = new List<(DateTime, DateTime)>();
    DateTime runStart = default;
    DateTime runEnd   = default;
    int      runLen   = 0;
    foreach (var bar in series)
    {
        if (bar.Regime == MarketRegime.Bear)
        {
            if (runLen == 0) runStart = bar.Time;
            runEnd = bar.Time;
            runLen++;
        }
        else if (runLen > 0)
        {
            if (runLen >= minBars) windows.Add((runStart, runEnd));
            runLen = 0;
        }
    }
    if (runLen >= minBars) windows.Add((runStart, runEnd));
    return windows;
}

private static T[] FilterToWindows<T>(T[] items, Func<T, DateTime> getTime,
    List<(DateTime Start, DateTime End)> windows)
{
    if (windows.Count == 0) return items;
    return items.Where(x => { var t = getTime(x); return windows.Any(w => t >= w.Start && t <= w.End); })
                .ToArray();
}
```

- [ ] **Step 2: In `RunFadeLongTrain`, fetch BTC and build bear windows**

In `RunFadeLongTrain`, after `var flFetched = await Task.WhenAll(flFetchTasks);`, add:

```csharp
// Build BTC bear windows to restrict training data to regime-relevant periods
var btcFetched = flFetched.FirstOrDefault(f => f.sym == "BTCUSDT");
var bearWindows = new List<(DateTime Start, DateTime End)>();
if (btcFetched.h1 is { Length: > 220 })
{
    var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcFetched.h1);
    bearWindows = GetBearWindows(btcSeries, BearWindowMinBars);
    Console.WriteLine($"  BTC bear windows ({BearWindowMinBars}+ bar runs): {bearWindows.Count}");
    foreach (var (s, e) in bearWindows)
        Console.WriteLine($"    {s:yyyy-MM-dd} → {e:yyyy-MM-dd}  ({(e - s).TotalDays:F0}d)");
}
else Console.WriteLine("  BTC data insufficient for bear-window filtering — using full history");
```

If BTCUSDT is not in `Config.BacktestCoins`, fetch it separately:
```csharp
// Only needed if BTCUSDT is not already in BacktestCoins
if (btcFetched.h1 == null)
{
    var btcM15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, "BTCUSDT", batches: 113);
    var btcH1  = FadeShortSimulator.AggregateCandles(btcM15.ToArray(), 4);
    var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
    bearWindows = GetBearWindows(btcSeries, BearWindowMinBars);
}
```

- [ ] **Step 3: Apply window filter in the coin-data-building loop**

In the loop that builds `flCoins`, replace the `flCoins.Add(new FadeLongGA.CoinData(...))` for training coins with:

```csharp
// Filter training candles to bear windows
Candle[] h1TrainRaw  = h1[..valStart];
Candle[] m15TrainRaw = m15[..(valStart * 4)];
Candle[] h1Train     = FilterToWindows(h1TrainRaw,  c => c.Time, bearWindows);
Candle[] m15Train    = FilterToWindows(m15TrainRaw, c => c.Time, bearWindows);

if (h1Train.Length < 50)
{
    Console.WriteLine($"  {sym}: skip training (< 50 bear-window bars after filter)");
    // Still add as held-out OOS with full history so val reporting works
    flCoins.Add(new FadeLongGA.CoinData(Array.Empty<Candle>(), h1, Array.Empty<Candle>(), m15));
    flHeld++;
    continue;
}

int m15ValEnd = Math.Min((valEnd + 1) * 4, m15.Length);
flCoins.Add(new FadeLongGA.CoinData(
    h1Train,               h1[valStart..(valEnd + 1)],
    m15Train,              m15[(valStart * 4)..m15ValEnd]));
```

The val split is unchanged — keep the last bear block as val exactly as before.

- [ ] **Step 4: Build and verify**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add LongTrainCommands.cs
git commit -m "feat: FadeLong training restricted to BTC bear-window candles only"
```

---

## Task 8: PortfolioReplay — per-strategy concurrent cap

A pre-processing filter applied to the combined trade list before `SimulatePortfolioExposureCapped`. Prevents more than N positions open simultaneously in any one strategy, reducing correlation risk.

Note: `CombinedBacktest` already calls `SimulatePortfolioExposureCapped` (EUR cap). The new filter adds a COUNT cap on top, applied first.

**Files:**
- Create: `PortfolioReplay.cs`
- Modify: `CombinedBacktest.cs`
- Modify: `OosBacktest.cs`

- [ ] **Step 1: Create `PortfolioReplay.cs`**

```csharp
namespace TradingGA;

// Filters a combined trade list by per-strategy concurrent position count.
// Trades are walked in entry-time order; a trade is skipped if its strategy
// already has maxConcurrent open positions at that entry time.
// HoldDuration is used to determine when each position closes.
//
// Apply BEFORE SimulatePortfolioExposureCapped so the EUR cap sees a realistic
// trade list that won't blow up with 93 simultaneously correlated positions.
public static class PortfolioReplay
{
    // Default caps per strategy kind (intentionally generous; reduces only catastrophic clustering)
    public static readonly Dictionary<string, int> DefaultCaps = new()
    {
        ["fade_short"] = 10,
        ["swing_long"] = 8,
        ["diplong"]    = 8,
        ["fadelong"]   = 8,
        ["grid"]       = 12,
    };

    public record Trade(
        string   Strategy,
        DateTime EntryTime,
        TimeSpan HoldDuration,
        double   Return,
        double   Conf);

    // Filter trades by concurrent position cap. Returns a new list with capped trades removed.
    public static List<Trade> FilterByConcurrentCap(
        IEnumerable<Trade> trades,
        Dictionary<string, int>? caps = null)
    {
        caps ??= DefaultCaps;
        var sorted  = trades.OrderBy(t => t.EntryTime).ToList();
        var result  = new List<Trade>(sorted.Count);
        // Per-strategy list of close times for currently open positions
        var openClose = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in sorted)
        {
            int cap = caps.TryGetValue(t.Strategy, out int c) ? c : int.MaxValue;

            if (!openClose.TryGetValue(t.Strategy, out var closes))
            {
                closes = new List<DateTime>();
                openClose[t.Strategy] = closes;
            }

            // Remove positions that have closed by this entry time
            closes.RemoveAll(ct => ct <= t.EntryTime);

            if (closes.Count < cap)
            {
                closes.Add(t.EntryTime + t.HoldDuration);
                result.Add(t);
            }
            // else: trade skipped — cap exceeded
        }

        return result;
    }
}
```

- [ ] **Step 2: Add SwingLong to `CombinedBacktest.cs` and apply the cap**

In `CombinedBacktest.cs`, after the DipLong trade collection block, add a SwingLong block following the same pattern as DipLong. Then, before the `allTrades.Sort(...)` line, insert the cap filter:

```csharp
// Apply per-strategy concurrent cap before portfolio simulation
var capInput = allTrades.Select(t =>
{
    // Estimate hold duration from strategy kind (conservative: use MaxHoldCandles at h1)
    var holdEst = t.Strategy switch
    {
        "swing"    => TimeSpan.FromHours(48),
        "grid"     => TimeSpan.FromHours(72),
        "fadelong" => TimeSpan.FromHours(72),
        "diplong"  => TimeSpan.FromHours(48),
        "swing_long" => TimeSpan.FromHours(48),
        _            => TimeSpan.FromHours(48),
    };
    return new PortfolioReplay.Trade(t.Strategy, t.Time, holdEst, t.Return, t.Conf);
}).ToList();

var capFiltered = PortfolioReplay.FilterByConcurrentCap(capInput);
int skipped = allTrades.Count - capFiltered.Count;
if (skipped > 0) Console.WriteLine($"  Concurrent cap removed {skipped} trades across all strategies");

// Replace allTrades with cap-filtered list for downstream metrics
allTrades = capFiltered
    .Select(t => (t.EntryTime, t.Return, t.Conf, t.Strategy))
    .ToList();
```

Also add SwingLong to the allTrades collection (before the cap step), following the DipLong pattern:
```csharp
// ── SwingLong ──────────────────────────────────────────────────────────────────
if (slG != null && (session == null || session.IsActive(RegimeRouterGA.StrategyKind.DipLong, /* use DipLong gate */ default)))
{
    // ... collect SwingLong trades same as DipLong block, using SwingLongSimulator.GetSwingLongReturns
}
```

Note: read the existing DipLong block in CombinedBacktest.cs carefully and mirror it exactly for SwingLong. The exact code depends on how the router session lookup is structured there — adapt accordingly.

- [ ] **Step 3: Apply the cap in `OosBacktest.cs`**

In `OosBacktest.cs`, after all four per-strategy trade lists are built and before metric aggregation, apply:

```csharp
// Apply per-strategy concurrent cap
if (allTrades.Count > 0)
{
    var capInput = allTrades.Select(t =>
        new PortfolioReplay.Trade(t.Strategy, t.Time,
            t.Strategy switch {
                "swing" or "swing_long" or "diplong" => TimeSpan.FromHours(48),
                "fadelong" or "grid" => TimeSpan.FromHours(72),
                _ => TimeSpan.FromHours(48)
            }, t.Return, t.Conf)
    ).ToList();
    var filtered = PortfolioReplay.FilterByConcurrentCap(capInput);
    Console.WriteLine($"  Concurrent cap: {allTrades.Count} → {filtered.Count} trades ({allTrades.Count - filtered.Count} removed)");
    allTrades = filtered.Select(t => (t.EntryTime, t.Return, t.Conf, t.Strategy)).ToList();
}
```

- [ ] **Step 4: Build and verify**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 5: Commit**

```bash
git add PortfolioReplay.cs CombinedBacktest.cs OosBacktest.cs
git commit -m "feat: PortfolioReplay concurrent cap — max N simultaneous positions per strategy"
```

---

## Task 9: Papertrade — trained router + all four strategies + cap annotation

**Files:**
- Modify: `PapertradeCommands.cs`

The papertrade loop currently fetches 4h candles for FadeShort + Grid. We switch to fetching 15m candles and aggregating to 1h, which all five strategies need.

- [ ] **Step 1: Load trained router genotype at startup**

After the Grid genotype loading block (`gridGPt`) and before `Console.WriteLine()`, add:

```csharp
RegimeRouterGenotype? routerGenoPt = null;
if (File.Exists(Config.RouterGenoFile))
{
    routerGenoPt = JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(
        File.ReadAllText(Config.RouterGenoFile))!.ToGenotype();
    Console.WriteLine($"Router genotype:   {routerGenoPt}  (trained)");
}
else Console.WriteLine("  (no router genotype — using rule-based routing)");

SwingLongGenotype? slGenoPt = null;
if (File.Exists(Config.SwingLongGenoFile))
{
    slGenoPt = JsonSerializer.Deserialize<SwingLongGenotypeDto>(
        File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype();
    Console.WriteLine($"SwingLong:         {slGenoPt}");
}

DipLongGenotype? dlGenoPt = null;
if (File.Exists(Config.DipLongGenoFile))
{
    dlGenoPt = JsonSerializer.Deserialize<DipLongGenotypeDto>(
        File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype();
    Console.WriteLine($"DipLong:           {dlGenoPt}");
}

FadeLongGenotype? flGenoPt = null;
if (File.Exists(Config.FadeLongGenoFile))
{
    flGenoPt = JsonSerializer.Deserialize<FadeLongGenotypeDto>(
        File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype();
    Console.WriteLine($"FadeLong:          {flGenoPt}");
}
```

- [ ] **Step 2: Replace candle fetch with 15m → 1h**

Replace the existing `FetchSwingCandles` fetch loop with:

```csharp
var coinData = new List<(string Sym, Candle[]? H1, Candle[]? M15, bool Passes, double AtrPct, double VolM)>();
foreach (var sym in coins)
{
    if (cts.Token.IsCancellationRequested) break;
    // 5 batches ≈ 52 days of 15m; enough for EMA500 warmup after agg to 1h
    var m15Raw = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 5);
    if (m15Raw.Count < 200) { coinData.Add((sym, null, null, false, 0, 0)); continue; }
    var m15 = m15Raw.ToArray();
    var h1  = FadeShortSimulator.AggregateCandles(m15, 4);
    var (passes, atrPct, volM) = CandleFetcher.CheckSwingCriteria(m15Raw);
    coinData.Add((sym, h1, m15, passes, atrPct, volM));
}
```

- [ ] **Step 3: Use trained router genotype in regime routing**

Replace the regime routing block (line ~70) with:

```csharp
StrategyActivation? ptRouting = null;
{
    var btcPt = coinData.FirstOrDefault(x => x.Sym == "BTCUSDT");
    var ethPt = coinData.FirstOrDefault(x => x.Sym == "ETHUSDT");
    if (btcPt.H1 is { Length: > 220 })
    {
        Candle[]? ethH1Pt = ethPt.H1 is { Length: > 220 } ? ethPt.H1 : null;
        ptRouting = routerGenoPt != null
            ? RegimeRouter.Route(btcPt.H1, routerGenoPt, ethH1Pt)  // trained thresholds
            : RegimeRouter.Route(btcPt.H1, ethH1Pt);               // rule-based fallback
        string routerTag = routerGenoPt != null ? "(trained)" : "(rule-based)";
        string sizeTag   = ptRouting.SizeMult < 1.0 ? $"  size×{ptRouting.SizeMult:F2}" : "";
        Console.WriteLine($"  Regime {routerTag}: {ptRouting.Regime}  conf={ptRouting.Confidence:P0}  →  {RegimeRouter.Describe(ptRouting)}{sizeTag}\n");
    }
}
```

- [ ] **Step 4: Update FadeShort section to use H1/M15**

Replace the candle access in the FadeShort loop from `candles` to `h1`/`m15` variables:

In the foreach loop, change:
```csharp
foreach (var (sym, candles, passes, atrPct, volM) in coinData)
{
    ...
    if (candles == null) ...
    var st = FadeShortSimulator.GetFadeShortTradeState(gForCoin, candles);
```
to:
```csharp
foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
{
    ...
    if (h1 == null || m15 == null) ...
    var st = FadeShortSimulator.GetFadeShortTradeState(gForCoin, h1, m15);
```

Update the Grid section similarly (Grid uses h1 only):
```csharp
var gst = GridSimulator.GetGridTradeState(gridGPt, h1);
```

- [ ] **Step 5: Add SwingLong, DipLong, FadeLong sections**

After the Grid section, add:

```csharp
// ── Count open positions for cap annotation ────────────────────────────
int slOpen = 0, dlOpen = 0, flOpen = 0;
// Pre-count so cap annotation is correct in the display loops below.

// ── SwingLong ─────────────────────────────────────────────────────────
bool slRoutedOn = ptRouting == null || ptRouting.DipLongActive;
if (slGenoPt != null && slRoutedOn && !cts.Token.IsCancellationRequested)
{
    Console.WriteLine();
    Console.WriteLine($"── SwingLong {new string('─', 92)}");
    Console.WriteLine($"{"Coin",-18}  {"State",-14} {"Entry",12}  {"Current",12}  {"Unrealised",11}  {"Bars",5}  {"Stop",12}  {"Target",12}");
    Console.WriteLine(new string('-', 105));
    foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
    {
        if (cts.Token.IsCancellationRequested) break;
        if (h1 == null || m15 == null) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }
        if (!passes) { Console.WriteLine($"  {sym,-18}  skip  ATR={atrPct:F1}% vol=${volM:F0}M"); continue; }
        double px = h1[^1].Close;
        var st = SwingLongSimulator.GetSwingLongTradeState(slGenoPt, h1, m15);
        string capTag = (st.InTrade && slOpen >= PortfolioReplay.DefaultCaps["swing_long"]) ? " [CAP]" : "";
        if (st.InTrade) slOpen++;
        string stateStr  = st.InTrade ? (st.TrailArmed ? "TRAIL ARMED" : $"LONG b{st.HoldCount}") : "watching";
        string entryStr  = st.InTrade ? $"{st.Entry:F4}" : "—";
        string unreal    = st.InTrade ? $"{(px - st.Entry) / st.Entry * 100.0:+0.00}%" : "—";
        string stopStr   = st.InTrade ? $"{st.HardStop:F4}" : "—";
        string targetStr = st.InTrade ? $"{st.Target:F4}" : "—";
        Console.WriteLine($"  {sym,-18}  {stateStr + capTag,-14} {entryStr,12}  {px,12:F4}  {unreal,11}  {(st.InTrade ? st.HoldCount.ToString() : "—"),5}  {stopStr,12}  {targetStr,12}");
    }
}

// ── DipLong ───────────────────────────────────────────────────────────
bool dlRoutedOn = ptRouting == null || ptRouting.DipLongActive;
if (dlGenoPt != null && dlRoutedOn && !cts.Token.IsCancellationRequested)
{
    Console.WriteLine();
    Console.WriteLine($"── DipLong {new string('─', 94)}");
    Console.WriteLine($"{"Coin",-18}  {"State",-14} {"Entry",12}  {"Current",12}  {"Unrealised",11}  {"Bars",5}  {"Stop",12}  {"Target",12}");
    Console.WriteLine(new string('-', 105));
    foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
    {
        if (cts.Token.IsCancellationRequested) break;
        if (h1 == null || m15 == null) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }
        if (!passes) { Console.WriteLine($"  {sym,-18}  skip  ATR={atrPct:F1}% vol=${volM:F0}M"); continue; }
        double px = h1[^1].Close;
        var st = DipLongSimulator.GetDipLongTradeState(dlGenoPt, h1, m15);
        string capTag = (st.InTrade && dlOpen >= PortfolioReplay.DefaultCaps["diplong"]) ? " [CAP]" : "";
        if (st.InTrade) dlOpen++;
        string stateStr  = st.InTrade ? (st.TrailArmed ? "TRAIL ARMED" : $"LONG b{st.HoldCount}") : "watching";
        string entryStr  = st.InTrade ? $"{st.Entry:F4}" : "—";
        string unreal    = st.InTrade ? $"{(px - st.Entry) / st.Entry * 100.0:+0.00}%" : "—";
        string stopStr   = st.InTrade ? $"{st.HardStop:F4}" : "—";
        string targetStr = st.InTrade ? $"{st.Target:F4}" : "—";
        Console.WriteLine($"  {sym,-18}  {stateStr + capTag,-14} {entryStr,12}  {px,12:F4}  {unreal,11}  {(st.InTrade ? st.HoldCount.ToString() : "—"),5}  {stopStr,12}  {targetStr,12}");
    }
}

// ── FadeLong ──────────────────────────────────────────────────────────
bool flRoutedOn = ptRouting == null || ptRouting.FadeLongActive;
if (flGenoPt != null && flRoutedOn && !cts.Token.IsCancellationRequested)
{
    Console.WriteLine();
    Console.WriteLine($"── FadeLong {new string('─', 93)}");
    Console.WriteLine($"{"Coin",-18}  {"State",-14} {"Entry",12}  {"Current",12}  {"Unrealised",11}  {"Bars",5}  {"Stop",12}  {"Target",12}");
    Console.WriteLine(new string('-', 105));
    foreach (var (sym, h1, m15, passes, atrPct, volM) in coinData)
    {
        if (cts.Token.IsCancellationRequested) break;
        if (h1 == null || m15 == null) { Console.WriteLine($"  {sym,-18}  (no data)"); continue; }
        if (!passes) { Console.WriteLine($"  {sym,-18}  skip  ATR={atrPct:F1}% vol=${volM:F0}M"); continue; }
        double px = h1[^1].Close;
        var st = FadeLongSimulator.GetFadeLongTradeState(flGenoPt, h1, m15);
        string capTag = (st.InTrade && flOpen >= PortfolioReplay.DefaultCaps["fadelong"]) ? " [CAP]" : "";
        if (st.InTrade) flOpen++;
        string stateStr  = st.InTrade ? (st.TrailArmed ? "TRAIL ARMED" : $"LONG b{st.HoldCount}") : "watching";
        string entryStr  = st.InTrade ? $"{st.Entry:F4}" : "—";
        string unreal    = st.InTrade ? $"{(px - st.Entry) / st.Entry * 100.0:+0.00}%" : "—";
        string stopStr   = st.InTrade ? $"{st.HardStop:F4}" : "—";
        string targetStr = st.InTrade ? $"{st.Target:F4}" : "—";
        Console.WriteLine($"  {sym,-18}  {stateStr + capTag,-14} {entryStr,12}  {px,12:F4}  {unreal,11}  {(st.InTrade ? st.HoldCount.ToString() : "—"),5}  {stopStr,12}  {targetStr,12}");
    }
}
```

Note: `FadeLongTradeState` has a `MaeStop` field in addition to `HardStop`. Use `Math.Min(st.HardStop, st.MaeStop)` for the effective stop display, as FadeShort does.

- [ ] **Step 6: Build and verify**

```bash
dotnet build -c Release 2>&1 | tail -5
```
Expected: `Build succeeded.`

- [ ] **Step 7: Commit**

```bash
git add PapertradeCommands.cs
git commit -m "feat: papertrade — trained router, 1h+15m candles, SwingLong/DipLong/FadeLong sections, [CAP] annotation"
```

---

## Task 10: CLAUDE.md rewrite

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Rewrite `CLAUDE.md`**

Replace the entire file contents with:

```markdown
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
dotnet run -- retrain            # FadeShort retrain on unknown coins (inverted screen, anti-overfit)

# Backtesting
dotnet run -- backtest           # FadeShort backtest: 93 coins, val 20%, 1h+15m dual-TF
dotnet run -- gridbacktest       # Grid backtest: 93 coins, val 20%
dotnet run -- combinedbacktest   # All strategies combined, shared capital, router-gated
dotnet run -- oosbacktest        # OOS backtest: 28 never-seen coins, full history, all strategies
dotnet run -- allcoinsbacktest   # Portfolio sim: BacktestCoins (val 20%) + OOS coins
dotnet run -- yearlybreakdown    # Per-year portfolio returns (full history)
dotnet run -- test               # Statistical edge validation

# Live
dotnet run -- papertrade         # Live signals (1h refresh), all strategies, router-gated

# Discord bot (requires .env)
.venv/bin/python discord_bot.py
```

No test suite. Validation is done by running `combinedbacktest` or `oosbacktest` after any simulator change.

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

`RegimeClassifier.cs` — multi-signal ensemble classifier producing Bull/Bear/Ranging/HighVol + confidence (0–1).
Signals: EMA stack (weight 3), EMA50 slope (1.5), ADX (1), 20-bar momentum (0.5), ATR vol ratio (0.5).
`ClassifySeriesWithDuration` is O(n) with per-bar duration counter (how many consecutive bars in current regime).

`RegimeRouter.cs` — BTC-anchored router (ETH as secondary confirmer, configurable blend weight).
Returns `StrategyActivation` with per-strategy flags + `SizeMult` (confidence-scaled position multiplier).
Two overloads: rule-based fallback and trained-genotype path (`RegimeRouter.Route(btcH1, routerGeno, ethH1)`).

`RegimeRouterSession` — pre-computed O(1) per-trade lookup for backtests. Build once, call `IsActive(kind, time)`.

### Cooperative coevolution

`CoevolveGA.cs` — 4-cycle loop: FadeLong and DipLong train against the router's current gating (soft bear/bull confidence gradient), then the router retrains on the evolved trade lists. Avoids train-then-gate misalignment.
`BayesianOptimizer.cs` — TPE post-GA refinement (60 iterations). Applied after FadeShort, FadeLong, DipLong, SwingLong GA runs.

### GA fitness (all long strategies)

5-fold walk-forward CV. FoldScore = `gain × wrMult × qualityMult × freqBonus / ddDiv × retentionMult`.
`retentionMult` penalises giving back gains at fold end (pushes toward tight trailing, not peak capture).

### Portfolio cap

`PortfolioReplay.cs` — filters combined trade list by per-strategy concurrent count before EUR exposure simulation. Default caps: FadeShort=10, SwingLong=8, DipLong=8, FadeLong=8, Grid=12.

### Candle fetching

`FetchFifteenMinCandlesCached(symbol, batches)` — 15m candles with disk cache (`candle_cache/{symbol}_15m.csv`). `batches: 113` ≈ 3.2yr. Incremental: only fetches new candles since last write.
`FetchSwingCandles(symbol, batches)` — legacy 4h candle fetch (kept for compatibility).

### Saved genotype files

| File | Strategy |
|------|----------|
| `fade_short_genotype.json` | FadeShort |
| `grid_best_genotype.json` | Grid |
| `fade_long_genotype.json` | FadeLong |
| `dip_long_genotype.json` | DipLong |
| `swing_long_genotype.json` | SwingLong |
| `regime_router_genotype.json` | RegimeRouter |

### Discord bot

`discord_bot.py` manages a single long-running dotnet subprocess (`papertrade`).
- Detects cycle boundaries by watching for `=== Gravity-gen2 | PAPER TRADE` headers in stdout
- Posts formatted summary to Discord each cycle
- Forwards text messages in command channel to `claude -p` for live code edits, then restarts

Required `.env`:
```
DISCORD_TOKEN=
DISCORD_OUTPUT_CHANNEL=
DISCORD_COMMAND_CHANNEL=
DISCORD_GUILD_ID=
GRAVITY_MODE=papertrade
```
```

- [ ] **Step 2: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: rewrite CLAUDE.md for 5-strategy regime-gated architecture"
```

---

## Self-Review Notes

- Task 6 (Bayesian, FadeShort): verify the exact field name for single-TF training candles in `FadeShortGA.CoinData` before applying the evaluate lambda. The 4h path uses a different candle field than the 1h+15m path.
- Task 8 (CombinedBacktest): the SwingLong trade collection block requires reading the DipLong block carefully to mirror it correctly — the router session lookup and confidence extraction must use the same pattern.
- Task 9 (Papertrade, FadeLong stop): `FadeLongTradeState` has both `HardStop` and `MaeStop`; display `Math.Min(st.HardStop, st.MaeStop)` as effective stop (mirrors how FadeShort displays `Math.Min(st.HardStop, st.MaeStop)`).
- Tasks are ordered so each builds on prior: genotype → simulator → GA → train mode → Bayesian → bear-window → cap → papertrade → docs.
