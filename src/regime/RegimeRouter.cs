namespace TradingGA;

// Central strategy router: classifies BTC regime (primary) + ETH (secondary confirmer),
// blends the two signals, and returns per-strategy activation flags + a position-size multiplier.
//
// Two-layer architecture:
//   Layer 1 — BTC regime → which strategy POOL is active (this class)
//   Layer 2 — per-coin internal gates (ADX, EMA slope, ATR filter) → which coins qualify within the pool
//
// FadeShort is router-gated: it is suppressed in confirmed Bull regimes (same threshold as DipLong),
// so shorting does not fight an established uptrend. In Bear/Ranging/HighVol it is always active.
//
// ETH acts as a secondary confirmer:
//   Agreement  → weighted blend (BTC 70% + ETH 30%), boosting confidence
//   Disagreement → BTC regime wins but ETH's contrary confidence reduces the blend
public record StrategyActivation(
    bool         FadeShortActive,
    bool         GridActive,
    bool         DipLongActive,
    bool         FadeLongActive,
    double       SizeMult,        // 1.0 = normal · 0.5 = HighVol · [0.60–1.00] scales with confidence
    MarketRegime Regime,
    double       Confidence);

public static class RegimeRouter
{
    // Hard-coded defaults used when no trained router genotype is available.
    private const double DirectionalMinConf = 0.45;
    private const double DefaultBullMinBars = 200;
    private const double DefaultBearMinBars = 200;
    private const double DefaultGridMaxConf = 0.45;
    private const double BtcWeight          = 0.70;
    private const double EthWeight          = 0.30;

    // Route using a trained RegimeRouterGenotype (preferred — uses learned thresholds + duration gate).
    public static StrategyActivation Route(Candle[] btcH1, RegimeRouterGenotype geno, Candle[]? ethH1 = null)
    {
        var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1);
        var btc       = btcSeries[^1];

        RegimeBar? eth = null;
        if (ethH1 != null && ethH1.Length >= 220)
        {
            var ethSeries = RegimeClassifier.ClassifySeriesWithDuration(ethH1);
            eth = ethSeries[^1];
        }

        double blendedConf = BlendConf(geno.EthBlendWeight, btc, eth);
        // Previous regime: look back duration bars to the bar before this run started
        var prevRegime = btcSeries.Length > btc.Duration
            ? btcSeries[^(btc.Duration + 1)].Regime
            : MarketRegime.Ranging;
        return ActivateWithGeno(btc.Regime, blendedConf, btc.Duration, geno, prevRegime);
    }

    // Route using BTC as primary anchor (rule-based fallback, no trained genotype).
    // Pass ethH1 to enable the ETH secondary confirmer (needs ≥220 bars; skipped otherwise).
    public static StrategyActivation Route(Candle[] btcH1, Candle[]? ethH1 = null)
    {
        var (btcRegime, btcConf) = RegimeClassifier.Classify(btcH1);

        if (ethH1 == null || ethH1.Length < 220)
            return Activate(btcRegime, btcConf);

        var (ethRegime, ethConf) = RegimeClassifier.Classify(ethH1);

        MarketRegime finalRegime;
        double       blendedConf;

        if (btcRegime == ethRegime)
        {
            finalRegime = btcRegime;
            blendedConf = BtcWeight * btcConf + EthWeight * ethConf;
        }
        else
        {
            // BTC leads; ETH contrary signal dampens conviction
            finalRegime = btcRegime;
            blendedConf = Math.Max(0.0, BtcWeight * btcConf - EthWeight * ethConf);
        }

        return Activate(finalRegime, Math.Min(1.0, blendedConf));
    }

    // Human-readable summary line, e.g. "Grid ✓  FadeShort ✓  size×0.78"
    public static string Describe(StrategyActivation a)
    {
        var parts = new List<string>();
        if (a.FadeShortActive) parts.Add("FadeShort ✓");
        if (a.GridActive)      parts.Add("Grid ✓");
        else                   parts.Add("Grid ○");
        if (a.DipLongActive)   parts.Add("DipLong ✓");
        else                   parts.Add("DipLong ✗");
        if (a.FadeLongActive)  parts.Add("FadeLong ✓");
        else                   parts.Add("FadeLong ✗");
        if (a.SizeMult < 1.0)  parts.Add($"  size×{a.SizeMult:F2}");
        return string.Join("  ", parts);
    }

    // ── Activation helpers ────────────────────────────────────────────────────

    // Rule-based activation (no genotype — uses hard-coded defaults, no duration gate).
    private static StrategyActivation Activate(MarketRegime regime, double conf)
    {
        if (regime == MarketRegime.HighVol)
            return new(true, false, false, false, 0.5, regime, conf);

        bool fadeShort = !(regime == MarketRegime.Bull   && conf >= DirectionalMinConf);
        bool grid      = true;
        bool dipLong   = regime == MarketRegime.Bull    && conf >= DirectionalMinConf;
        bool fadeLong  = regime == MarketRegime.Bear    && conf >= DirectionalMinConf;

        double sizeMult = regime == MarketRegime.Ranging
            ? 1.0
            : 0.60 + 0.40 * Math.Min(1.0, conf / 0.80);

        return new(fadeShort, grid, dipLong, fadeLong, sizeMult, regime, conf);
    }

    // Genotype-aware activation: uses trained thresholds + BTC duration gate + source-regime awareness.
    // Early-bull window (duration < BullMinBars):
    //   Grid forced ON at TransitionSizeMult.
    //   Longs gated by source regime: EarlyBullFromBearMult (Bear→Bull) or EarlyBullFromRangingMult (Ranging→Bull).
    //   FadeLong can carry over at EarlyBullBearCarry fraction when source was Bear.
    private static StrategyActivation ActivateWithGeno(
        MarketRegime regime, double conf, int duration,
        RegimeRouterGenotype geno, MarketRegime prevRegime = MarketRegime.Ranging)
    {
        if (regime == MarketRegime.HighVol)
            return new(true, false, false, false, 0.5, regime, conf);

        bool inBullTransition = regime == MarketRegime.Bull && duration < (int)geno.BullMinBars;

        bool earlyFromBear    = inBullTransition && prevRegime == MarketRegime.Bear
                                && conf >= geno.BullMinConf && geno.EarlyBullFromBearMult > 0;
        bool earlyFromRanging = inBullTransition && prevRegime == MarketRegime.Ranging
                                && conf >= geno.BullMinConf && geno.EarlyBullFromRangingMult > 0;

        bool fadeShort = !(regime == MarketRegime.Bull
                           && duration >= (int)geno.BullMinBars
                           && conf >= geno.BullMinConf);
        bool grid      = true;
        bool dipLong   = (regime == MarketRegime.Bull
                          && duration >= (int)geno.BullMinBars
                          && conf >= geno.BullMinConf)
                         || earlyFromBear || earlyFromRanging;
        bool fadeLong  = regime == MarketRegime.Bear
                          && conf >= geno.BearMinConf;

        double sizeMult = regime == MarketRegime.Ranging
            ? 1.0
            : 0.60 + 0.40 * Math.Min(1.0, conf / 0.80);

        if (inBullTransition && geno.TransitionSizeMult > 0 && !earlyFromBear && !earlyFromRanging)
            sizeMult *= geno.TransitionSizeMult;
        else if (earlyFromBear)
            sizeMult *= geno.EarlyBullFromBearMult;
        else if (earlyFromRanging)
            sizeMult *= geno.EarlyBullFromRangingMult;

        return new(fadeShort, grid, dipLong, fadeLong, sizeMult, regime, conf);
    }

    // ETH blend helper shared by both Route overloads.
    private static double BlendConf(double ethWeight, RegimeBar btc, RegimeBar? eth)
    {
        if (eth == null || ethWeight < 1e-6) return btc.Confidence;
        double w = ethWeight;
        return eth.Regime == btc.Regime
            ? (1 - w) * btc.Confidence + w * eth.Confidence
            : Math.Max(0.0, (1 - w) * btc.Confidence - w * eth.Confidence);
    }
}

// Pre-computed session for per-trade regime lookup.
// Build once per backtest / papertrade cycle, then call IsActive for each trade.
public class RegimeRouterSession
{
    private readonly RegimeBar[]              _btc;
    private readonly RegimeBar[]?             _eth;
    private readonly RegimeRouterGenotype     _geno;
    private readonly Dictionary<long, int>   _idx;

    public RegimeRouterSession(RegimeBar[] btcSeries, RegimeBar[]? ethSeries, RegimeRouterGenotype geno)
    {
        _btc  = btcSeries;
        _eth  = ethSeries;
        _geno = geno;
        _idx  = new Dictionary<long, int>(btcSeries.Length);
        for (int i = 0; i < btcSeries.Length; i++)
            _idx.TryAdd(HourKey(btcSeries[i].Time), i);
    }

    // Is this strategy active at the given trade timestamp?
    public bool IsActive(RegimeRouterGA.StrategyKind kind, DateTime tradeTime)
    {
        int bar = Lookup(tradeTime);
        var btc = _btc[bar];
        double conf = Blend(btc, bar);

        bool inBullTransition = btc.Regime == MarketRegime.Bull
                               && btc.Duration < (int)_geno.BullMinBars;
        bool inTransition     = inBullTransition
                             || (btc.Regime == MarketRegime.Bear
                                 && btc.Duration < (int)_geno.BearMinBars);

        // Previous regime for source-aware early-bull activation
        MarketRegime prevRegime = MarketRegime.Ranging;
        if (inBullTransition)
        {
            int prevBar = Math.Max(0, bar - btc.Duration);
            prevRegime  = _btc[prevBar].Regime;
        }

        bool earlyFromBear    = inBullTransition && prevRegime == MarketRegime.Bear
                                && conf >= _geno.BullMinConf && _geno.EarlyBullFromBearMult > 0;
        bool earlyFromRanging = inBullTransition && prevRegime == MarketRegime.Ranging
                                && conf >= _geno.BullMinConf && _geno.EarlyBullFromRangingMult > 0;
        bool bearCarry        = inBullTransition && prevRegime == MarketRegime.Bear
                                && _geno.EarlyBullBearCarry > 0;

        return kind switch
        {
            RegimeRouterGA.StrategyKind.FadeShort => !(btc.Regime == MarketRegime.Bull
                                                       && btc.Duration >= (int)_geno.BullMinBars
                                                       && conf >= _geno.BullMinConf),
            RegimeRouterGA.StrategyKind.Grid      => btc.Regime == MarketRegime.Ranging
                                                     || conf < _geno.GridMaxConf
                                                     || (inTransition && _geno.TransitionSizeMult > 0),
            RegimeRouterGA.StrategyKind.DipLong   => (btc.Regime == MarketRegime.Bull
                                                      && btc.Duration >= (int)_geno.BullMinBars
                                                      && conf >= _geno.BullMinConf)
                                                     || earlyFromBear || earlyFromRanging,
            RegimeRouterGA.StrategyKind.FadeLong  => (btc.Regime == MarketRegime.Bear
                                                      && btc.Duration >= (int)_geno.BearMinBars
                                                      && conf >= _geno.BearMinConf)
                                                     || bearCarry,
            _                                     => false,
        };
    }

    // Soft weight for coevolution training gates: returns a value in [0,1] based on regime confidence.
    // Ignores the router's trained duration/confidence thresholds so the strategy GA always has a
    // gradient to walk on. A step cutoff at StepCutoffConf removes trades in clearly opposite regimes.
    //   Bear period (any conf)   → FadeLong weight = conf   (gradient 0→1)
    //   Strong bull (conf>0.65)  → FadeLong weight = 0      (step cutoff)
    //   Ranging / HighVol        → FadeLong weight = 0
    // Mirror applies for DipLong (bull vs bear).
    private const double StepCutoffConf = 0.65;
    public double GetWeight(RegimeRouterGA.StrategyKind kind, DateTime tradeTime)
    {
        int bar = Lookup(tradeTime);
        var btc = _btc[bar];
        double conf = Blend(btc, bar);

        return kind switch
        {
            RegimeRouterGA.StrategyKind.FadeLong => btc.Regime switch
            {
                MarketRegime.Bear                             => conf,
                MarketRegime.Bull when conf > StepCutoffConf => 0.0,
                _                                             => 0.0,
            },
            RegimeRouterGA.StrategyKind.DipLong => btc.Regime switch
            {
                MarketRegime.Bull                             => conf,
                MarketRegime.Bear when conf > StepCutoffConf => 0.0,
                _                                             => 0.0,
            },
            RegimeRouterGA.StrategyKind.FadeShort => btc.Regime == MarketRegime.Bull && conf > StepCutoffConf ? 0.0 : 1.0,
            RegimeRouterGA.StrategyKind.Grid      => 1.0,
            _                                     => 0.0,
        };
    }

    // Returns contiguous DateTime spans where BTC regime == target for at least minBars consecutive bars.
    // Returns the FULL extent of each qualifying run (from first bar to last bar of that run),
    // so callers get the complete regime window, not just the tail where Duration≥minBars.
    // Used by DipLong co-refinement to filter training data to router-approved windows.
    public List<(DateTime Start, DateTime End)> GetWindows(MarketRegime target, int minBars)
    {
        var windows = new List<(DateTime, DateTime)>();
        DateTime runStart = default;
        DateTime runEnd   = default;
        int      runLen   = 0;

        foreach (var bar in _btc)
        {
            if (bar.Regime == target)
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

    // ── Private ───────────────────────────────────────────────────────────────
    private static long HourKey(DateTime t) => t.Ticks / TimeSpan.TicksPerHour;

    private int Lookup(DateTime t)
    {
        long key = HourKey(t);
        if (_idx.TryGetValue(key, out int bar)) return bar;
        // Only search backwards — never return a future bar whose candle has not yet closed
        for (int d = 1; d <= 4; d++)
        {
            if (_idx.TryGetValue(key - d, out bar)) return bar;
        }
        return _btc.Length - 1;
    }

    private double Blend(RegimeBar btc, int bar)
    {
        if (_eth == null || _geno.EthBlendWeight < 1e-6) return btc.Confidence;
        var eth = _eth[Math.Clamp(bar, 0, _eth.Length - 1)];
        double w = _geno.EthBlendWeight;
        return eth.Regime == btc.Regime
            ? (1 - w) * btc.Confidence + w * eth.Confidence
            : Math.Max(0.0, (1 - w) * btc.Confidence - w * eth.Confidence);
    }
}
