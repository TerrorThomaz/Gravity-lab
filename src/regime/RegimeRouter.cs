namespace TradingGA;

// Central strategy router: classifies BTC regime (primary) + ETH (secondary confirmer),
// blends the two signals, and returns per-strategy activation flags.
//
// The router gates strategies ON/OFF only — it does NOT size positions. A confidence-scaled
// SizeMult used to be computed here and exposed on StrategyActivation, but nothing ever sized
// on it (every backtest gates via RegimeRouterSession.IsActive; the single live consumer just
// printed it), and the scaling itself was never validated. It was removed rather than wired in,
// since wiring an unvalidated multiplier into position sizing would be a behaviour change, not
// a bugfix. Position sizing lives in DynamicGuard / PortfolioReplay, not here.
//
// Two-layer architecture:
//   Layer 1 — BTC regime → which strategy POOL is active (this class)
//   Layer 2 — per-coin internal gates (ADX, EMA slope, ATR filter) → which coins qualify within the pool
//
// FadeShort is router-gated: it is suppressed in confirmed Bull regimes (same threshold as DipLong),
// so shorting does not fight an established uptrend. In Bear/Ranging/HighVol it is always active.
//
// RipShort is a bear-regime trend-continuation short (shorts relief rallies in established
// downtrends). It has its own confirmed-bear gate (RipShortBearMinBars/RipShortBearMinConf,
// separate from FadeLong's BearMinBars/BearMinConf — sharing one gene pair let a disabled,
// underperforming FadeLong pull the threshold away from RipShort's true optimum). Being
// with-trend rather than a bounce play, it does NOT carry into the early-bull window.
//
// ETH acts as a secondary confirmer:
//   Agreement  → weighted blend (BTC 70% + ETH 30%), boosting confidence
//   Disagreement → BTC regime wins but ETH's contrary confidence reduces the blend

// WARNING: This record has a long run of consecutive bool parameters. It MUST be constructed
// using named arguments to prevent accidental parameter reordering. All construction sites
// must use explicit parameter names (e.g., FadeShortActive: true, GridActive: false, ...).
public record StrategyActivation(
    bool         FadeShortActive,
    bool         GridActive,
    bool         GridShortActive, // shares Grid's ranging/low-conf gate — same regime signal, opposite direction
    bool         DipLongActive,
    bool         FadeLongActive,
    bool         RipShortActive,
    bool         SwingLongActive,
    bool         AccumulationGridActive,
    MarketRegime Regime,
    double       Confidence,
    bool         FadeShortLowVolActive = false,
    bool         DipLongLowVolActive = false,
    bool         SwingLongLowVolActive = false,
    bool         RipShortLowVolActive = false,
    bool         FadeShortHighVolActive = false,
    bool         DipLongHighVolActive = false,
    bool         SwingLongHighVolActive = false,
    bool         RipShortHighVolActive = false,
    double       AtrRatio = 0.0);

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
    public static StrategyActivation Route(Candle[] btcH1, RegimeRouterGenotype geno, Candle[]? ethH1 = null, double atrRatio = 1.0)
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
        // Previous regime: look back `duration` bars to the bar before this run started.
        // Mirrors RegimeRouterSession's prevBar derivation exactly (clamped to bar 0, not
        // defaulted to Ranging on out-of-range) so live and backtest agree at series edges.
        int bar        = btcSeries.Length - 1;
        int prevBar    = Math.Max(0, bar - btc.Duration);
        var prevRegime = btcSeries[prevBar].Regime;
        return ActivateWithGeno(btc.Regime, blendedConf, btc.Duration, geno, prevRegime, atrRatio);
    }

    // Route using BTC as primary anchor (rule-based fallback, no trained genotype).
    // Pass ethH1 to enable the ETH secondary confirmer (needs ≥220 bars; skipped otherwise).
    public static StrategyActivation Route(Candle[] btcH1, Candle[]? ethH1 = null, double atrRatio = 1.0)
    {
        var (btcRegime, btcConf) = RegimeClassifier.Classify(btcH1);

        if (ethH1 == null || ethH1.Length < 220)
            return Activate(btcRegime, btcConf, atrRatio);

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

        return Activate(finalRegime, Math.Min(1.0, blendedConf), atrRatio);
    }

    // Human-readable summary line, e.g. "FadeShort ✓  Grid ✓  DipLong ✗"
    public static string Describe(StrategyActivation a)
    {
        var parts = new List<string>();
        if (a.FadeShortActive) parts.Add("FadeShort ✓");
        if (a.GridActive)      parts.Add("Grid ✓");
        else                   parts.Add("Grid ○");
        if (a.GridShortActive) parts.Add("GridShort ✓");
        else                   parts.Add("GridShort ○");
        if (a.DipLongActive)   parts.Add("DipLong ✓");
        else                   parts.Add("DipLong ✗");
        if (a.FadeLongActive)  parts.Add("FadeLong ✓");
        else                   parts.Add("FadeLong ✗");
        if (a.RipShortActive)  parts.Add("RipShort ✓");
        else                   parts.Add("RipShort ✗");
        if (a.SwingLongActive) parts.Add("SwingLong ✓");
        else                   parts.Add("SwingLong ✗");
        return string.Join("  ", parts);
    }

    // ── Activation helpers ────────────────────────────────────────────────────

    // Rule-based activation (no genotype — uses hard-coded defaults, no duration gate).
    private static StrategyActivation Activate(MarketRegime regime, double conf, double atrRatio = 1.0)
    {
        if (regime == MarketRegime.HighVol)
            return new StrategyActivation(
                FadeShortActive: true,
                GridActive: false,
                GridShortActive: false,
                DipLongActive: false,
                FadeLongActive: false,
                RipShortActive: false,
                SwingLongActive: false,
                AccumulationGridActive: false,
                Regime: regime,
                Confidence: conf,
                FadeShortLowVolActive: false,
                DipLongLowVolActive: false,
                SwingLongLowVolActive: false,
                RipShortLowVolActive: false,
                FadeShortHighVolActive: true,
                DipLongHighVolActive: true,
                SwingLongHighVolActive: true,
                RipShortHighVolActive: true,
                AtrRatio: atrRatio);

        bool fadeShort = !(regime == MarketRegime.Bull   && conf >= DirectionalMinConf);
        bool grid      = true;
        bool gridShort = true;
        bool dipLong   = regime == MarketRegime.Bull    && conf >= DirectionalMinConf;
        bool fadeLong  = regime == MarketRegime.Bear    && conf >= DirectionalMinConf;
        bool ripShort  = regime == MarketRegime.Bear    && conf >= DirectionalMinConf;
        bool swingLong = dipLong;
        bool accumulationGrid = (regime == MarketRegime.Bull && conf >= DirectionalMinConf)
                                || (regime == MarketRegime.Bear && conf >= DirectionalMinConf);

        // Low-vol variants: active when ATR ratio < 0.8 (low volatility regime)
        bool isLowVol = atrRatio < 0.8;
        bool fadeShortLowVol = isLowVol && fadeShort;
        bool dipLongLowVol   = isLowVol && dipLong;
        bool swingLongLowVol = isLowVol && swingLong;
        bool ripShortLowVol  = isLowVol && ripShort;

        // High-vol variants: active when ATR ratio > 1.5 (high volatility regime)
        bool isHighVol = atrRatio > 1.5;
        bool fadeShortHighVol = isHighVol && fadeShort;
        bool dipLongHighVol   = isHighVol && dipLong;
        bool swingLongHighVol = isHighVol && swingLong;
        bool ripShortHighVol  = isHighVol && ripShort;

        return new StrategyActivation(
            FadeShortActive: fadeShort,
            GridActive: grid,
            GridShortActive: gridShort,
            DipLongActive: dipLong,
            FadeLongActive: fadeLong,
            RipShortActive: ripShort,
            SwingLongActive: swingLong,
            AccumulationGridActive: accumulationGrid,
            Regime: regime,
            Confidence: conf,
            FadeShortLowVolActive: fadeShortLowVol,
            DipLongLowVolActive: dipLongLowVol,
            SwingLongLowVolActive: swingLongLowVol,
            RipShortLowVolActive: ripShortLowVol,
            FadeShortHighVolActive: fadeShortHighVol,
            DipLongHighVolActive: dipLongHighVol,
            SwingLongHighVolActive: swingLongHighVol,
            RipShortHighVolActive: ripShortHighVol,
            AtrRatio: atrRatio);
    }

    // Genotype-aware activation: uses trained thresholds + BTC duration gate + source-regime awareness.
    // Delegates the five per-strategy gates to ComputeActivation below, the single source of truth
    // shared with RegimeRouterSession.IsActive (the validated/backtested behavior). This function only
    // adds the HighVol short-circuit on top.
    //   FadeShort — suppressed once BTC has confirmed Bull (duration ≥ BullMinBars, conf ≥ BullMinConf);
    //               active in Bear/Ranging/early-Bull and in HighVol.
    //   Grid      — active while Ranging, OR blended confidence < GridMaxConf (ambiguous market),
    //               OR during an early-regime transition (Bull or Bear, duration < respective MinBars)
    //               when TransitionSizeMult > 0. NOT unconditionally on.
    //   DipLong   — active once BTC has confirmed Bull (duration ≥ BullMinBars, conf ≥ BullMinConf),
    //               OR during the early-bull window at EarlyBullFromBearMult/EarlyBullFromRangingMult
    //               fraction (requires conf ≥ BullMinConf and source regime Bear/Ranging respectively).
    //   FadeLong  — active once BTC has confirmed Bear (duration ≥ BearMinBars, conf ≥ BearMinConf),
    //               OR during the early-bull window as a Bear "carry-over" (bearCarry) at
    //               EarlyBullBearCarry fraction when the previous regime was Bear — NOT gated on conf.
    //   RipShort  — active once BTC has confirmed Bear (duration ≥ BearMinBars, conf ≥ BearMinConf).
    //               Unlike FadeLong, NO early-bull carry-over — RipShort is with-trend, so it has no
    //               business staying active once the regime tips toward Bull.
    private static StrategyActivation ActivateWithGeno(
        MarketRegime regime, double conf, int duration,
        RegimeRouterGenotype geno, MarketRegime prevRegime = MarketRegime.Ranging, double atrRatio = 1.0)
    {
        if (regime == MarketRegime.HighVol)
            return new StrategyActivation(
                FadeShortActive: true,
                GridActive: false,
                GridShortActive: false,
                DipLongActive: false,
                FadeLongActive: false,
                RipShortActive: false,
                SwingLongActive: false,
                AccumulationGridActive: false,
                Regime: regime,
                Confidence: conf,
                FadeShortLowVolActive: false,
                DipLongLowVolActive: false,
                SwingLongLowVolActive: false,
                RipShortLowVolActive: false,
                FadeShortHighVolActive: true,
                DipLongHighVolActive: true,
                SwingLongHighVolActive: true,
                RipShortHighVolActive: true,
                AtrRatio: atrRatio);

        var (fadeShort, grid, gridShort, dipLong, fadeLong, ripShort, swingLong, accumulationGrid, fadeShortLowVol, dipLongLowVol, swingLongLowVol, ripShortLowVol, fadeShortHighVol, dipLongHighVol, swingLongHighVol, ripShortHighVol) = ComputeActivation(regime, conf, duration, prevRegime, geno, atrRatio);

        return new StrategyActivation(
            FadeShortActive: fadeShort,
            GridActive: grid,
            GridShortActive: gridShort,
            DipLongActive: dipLong,
            FadeLongActive: fadeLong,
            RipShortActive: ripShort,
            SwingLongActive: swingLong,
            AccumulationGridActive: accumulationGrid,
            Regime: regime,
            Confidence: conf,
            FadeShortLowVolActive: fadeShortLowVol,
            DipLongLowVolActive: dipLongLowVol,
            SwingLongLowVolActive: swingLongLowVol,
            RipShortLowVolActive: ripShortLowVol,
            FadeShortHighVolActive: fadeShortHighVol,
            DipLongHighVolActive: dipLongHighVol,
            SwingLongHighVolActive: swingLongHighVol,
            RipShortHighVolActive: ripShortHighVol,
            AtrRatio: atrRatio);
    }

    // Shared gating logic — the single source of truth for genotype-based strategy activation.
    // Used identically by ActivateWithGeno (live Route path) and RegimeRouterSession.IsActive
    // (all backtests). Semantics match what was validated in backtesting; if the two paths ever
    // disagree again, this is the one place to fix it.
    internal static (bool FadeShort, bool Grid, bool GridShort, bool DipLong, bool FadeLong, bool RipShort, bool SwingLong, bool AccumulationGrid, bool FadeShortLowVol, bool DipLongLowVol, bool SwingLongLowVol, bool RipShortLowVol, bool FadeShortHighVol, bool DipLongHighVol, bool SwingLongHighVol, bool RipShortHighVol) ComputeActivation(
        MarketRegime regime, double conf, int duration, MarketRegime prevRegime, RegimeRouterGenotype geno, double atrRatio = 1.0)
    {
        bool inBullTransition = regime == MarketRegime.Bull && duration < (int)geno.BullMinBars;
        bool inTransition      = inBullTransition
                                || (regime == MarketRegime.Bear && duration < (int)geno.BearMinBars);

        bool earlyFromBear    = inBullTransition && prevRegime == MarketRegime.Bear
                                && conf >= geno.BullMinConf && geno.EarlyBullFromBearMult > 0;
        bool earlyFromRanging = inBullTransition && prevRegime == MarketRegime.Ranging
                                && conf >= geno.BullMinConf && geno.EarlyBullFromRangingMult > 0;
        bool bearCarry        = inBullTransition && prevRegime == MarketRegime.Bear
                                && geno.EarlyBullBearCarry > 0;

        // FadeShort: Bear-only when the gene is on, otherwise the legacy not-confirmed-Bull rule.
        //
        // The legacy rule leaves FadeShort running in Bear, Ranging, HighVol AND unconfident Bull.
        // That last bucket is where its losses live: on the time-embargoed held-out window it
        // scores Bear PF 5.28 (45 trades, +2.28%) against Bull PF 0.38 (84 trades, -0.86%), so
        // Bull carries ~65% of the trades and subtracts 72 points from a 102-point gross. The
        // blended ~1.2 PF is those two cancelling, which is why six retrains of the STRATEGY never
        // moved it — the problem was never the signal.
        //
        // Trained, not assumed: FadeShortBearMinBars/Conf let routertrain decide how strict
        // "confirmed bear" must be, and FadeShortBearOnly is read as a >0 ON/OFF switch (the same
        // convention as the transition genes) so the legacy behaviour stays reachable and the GA
        // can reject this if it does not pay.
        bool fadeShort = geno.FadeShortBearOnly > 0
            ? (regime == MarketRegime.Bear
               && duration >= (int)geno.FadeShortBearMinBars
               && conf >= geno.FadeShortBearMinConf)
            : !(regime == MarketRegime.Bull
                && duration >= (int)geno.BullMinBars
                && conf >= geno.BullMinConf);
        bool grid      = regime == MarketRegime.Ranging
                        || conf < geno.GridMaxConf
                        || (inTransition && geno.TransitionSizeMult > 0);
        // GridShort shares Grid's gate — same "is this genuinely ranging/ambiguous" signal,
        // just the opposite execution direction. No evidence yet that a split gene is
        // warranted (unlike RipShort/FadeLong, which had one when a disabled FadeLong was
        // actively dragging the shared threshold) — don't split preemptively.
        bool gridShort = grid;
        bool dipLong   = (regime == MarketRegime.Bull
                          && duration >= (int)geno.BullMinBars
                          && conf >= geno.BullMinConf)
                         || earlyFromBear || earlyFromRanging;
        bool swingLong = dipLong;
        bool fadeLong  = (regime == MarketRegime.Bear
                          && duration >= (int)geno.BearMinBars
                          && conf >= geno.BearMinConf)
                         || bearCarry;
        // RipShort is with-trend (shorts relief rallies in an established downtrend), so — unlike
        // FadeLong — it does NOT get a bearCarry into the early-bull window; confirmed-bear only.
        // Uses its OWN confirmed-bear gate (not the shared BearMinBars/BearMinConf) — sharing a
        // single threshold with FadeLong let a since-disabled FadeLong (PF=0.06 OOS) pull the
        // gate away from RipShort's true optimum.
        bool ripShort  = regime == MarketRegime.Bear
                         && duration >= (int)geno.RipShortBearMinBars
                         && conf >= geno.RipShortBearMinConf;

        // AccumulationGrid works in both bull and bear regimes (separate genotypes for each).
        // Active when in confirmed bull OR confirmed bear regime.
        bool accumulationGrid = (regime == MarketRegime.Bull
                                 && duration >= (int)geno.BullMinBars
                                 && conf >= geno.BullMinConf)
                                || (regime == MarketRegime.Bear
                                    && duration >= (int)geno.BearMinBars
                                    && conf >= geno.BearMinConf);

        // Low-vol variants: active when ATR ratio < 0.8 (low volatility regime)
        // They mirror the activation of their base counterparts but only in low-vol conditions
        bool isLowVol = atrRatio < 0.8;
        bool fadeShortLowVol = isLowVol && fadeShort;
        bool dipLongLowVol   = isLowVol && dipLong;
        bool swingLongLowVol = isLowVol && swingLong;
        bool ripShortLowVol  = isLowVol && ripShort;

        // High-vol variants: active when ATR ratio > 1.5 (high volatility regime)
        // They mirror the activation of their base counterparts but only in high-vol conditions
        bool isHighVol = atrRatio > 1.5;
        bool fadeShortHighVol = isHighVol && fadeShort;
        bool dipLongHighVol   = isHighVol && dipLong;
        bool swingLongHighVol = isHighVol && swingLong;
        bool ripShortHighVol  = isHighVol && ripShort;

        return (fadeShort, grid, gridShort, dipLong, fadeLong, ripShort, swingLong, accumulationGrid, fadeShortLowVol, dipLongLowVol, swingLongLowVol, ripShortLowVol, fadeShortHighVol, dipLongHighVol, swingLongHighVol, ripShortHighVol);
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

    // Graded position size for this strategy at this timestamp, in [0, 1].
    //
    // Returns 0 when the strategy is gated off — the boolean gate still decides IF, this only
    // decides HOW MUCH once active. Confidence at GradedConfStart funds at GradedSizeFloor and
    // ramps linearly to full size at GradedConfFull, so "just barely in regime" and "deeply in
    // regime" stop being the same bet.
    //
    // This is NOT the old SizeMult that was deleted in abad698. That one was computed on every
    // route, consumed by nothing but a JSON status field, and never validated. This is applied
    // at the point exposure is actually decided and is reported side by side against the boolean
    // baseline before anything adopts it.
    public double SizeMult(RegimeRouterGA.StrategyKind kind, DateTime tradeTime, double atrRatio = 1.0)
    {
        if (!IsActive(kind, tradeTime, atrRatio)) return 0.0;
        int bar = Lookup(tradeTime);
        double conf = Blend(_btc[bar], bar);
        double t = (conf - Config.GradedConfStart) / (Config.GradedConfFull - Config.GradedConfStart);
        return Config.GradedSizeFloor + (1.0 - Config.GradedSizeFloor) * Math.Clamp(t, 0.0, 1.0);
    }

    // Is this strategy active at the given trade timestamp?
    // Delegates to RegimeRouter.ComputeActivation — the same shared gate logic used by the live
    // Route()/ActivateWithGeno path — so live and backtest activation can never drift again.
    public bool IsActive(RegimeRouterGA.StrategyKind kind, DateTime tradeTime, double atrRatio = 1.0)
    {
        int bar = Lookup(tradeTime);
        var btc = _btc[bar];
        double conf = Blend(btc, bar);

        bool inBullTransition = btc.Regime == MarketRegime.Bull
                               && btc.Duration < (int)_geno.BullMinBars;

        // Previous regime for source-aware early-bull activation
        MarketRegime prevRegime = MarketRegime.Ranging;
        if (inBullTransition)
        {
            int prevBar = Math.Max(0, bar - btc.Duration);
            prevRegime  = _btc[prevBar].Regime;
        }

        var (fadeShort, grid, gridShort, dipLong, fadeLong, ripShort, swingLong, accumulationGrid, fadeShortLowVol, dipLongLowVol, swingLongLowVol, ripShortLowVol, fadeShortHighVol, dipLongHighVol, swingLongHighVol, ripShortHighVol) =
            RegimeRouter.ComputeActivation(btc.Regime, conf, btc.Duration, prevRegime, _geno, atrRatio);

        return kind switch
        {
            RegimeRouterGA.StrategyKind.FadeShort         => fadeShort,
            RegimeRouterGA.StrategyKind.Grid              => grid,
            RegimeRouterGA.StrategyKind.GridShort         => gridShort,
            RegimeRouterGA.StrategyKind.DipLong           => dipLong,
            RegimeRouterGA.StrategyKind.FadeLong          => fadeLong,
            RegimeRouterGA.StrategyKind.RipShort          => ripShort,
            RegimeRouterGA.StrategyKind.SwingLong         => swingLong,
            RegimeRouterGA.StrategyKind.AccumulationGrid  => accumulationGrid,
            RegimeRouterGA.StrategyKind.FadeShortLowVol   => fadeShortLowVol,
            RegimeRouterGA.StrategyKind.DipLongLowVol     => dipLongLowVol,
            RegimeRouterGA.StrategyKind.SwingLongLowVol   => swingLongLowVol,
            RegimeRouterGA.StrategyKind.RipShortLowVol    => ripShortLowVol,
            RegimeRouterGA.StrategyKind.FadeShortHighVol  => fadeShortHighVol,
            RegimeRouterGA.StrategyKind.DipLongHighVol    => dipLongHighVol,
            RegimeRouterGA.StrategyKind.SwingLongHighVol  => swingLongHighVol,
            RegimeRouterGA.StrategyKind.RipShortHighVol   => ripShortHighVol,
            _                                             => false,
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
            RegimeRouterGA.StrategyKind.SwingLong => btc.Regime switch
            {
                MarketRegime.Bull                             => conf,
                MarketRegime.Bear when conf > StepCutoffConf => 0.0,
                _                                             => 0.0,
            },
            RegimeRouterGA.StrategyKind.FadeShort => btc.Regime == MarketRegime.Bull && conf > StepCutoffConf ? 0.0 : 1.0,
            RegimeRouterGA.StrategyKind.Grid      => 1.0,
            RegimeRouterGA.StrategyKind.GridShort => 1.0,
            // RipShort shares FadeLong's bear gradient — it is also gated to Bear regimes only.
            RegimeRouterGA.StrategyKind.RipShort => btc.Regime switch
            {
                MarketRegime.Bear                             => conf,
                MarketRegime.Bull when conf > StepCutoffConf => 0.0,
                _                                             => 0.0,
            },
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
