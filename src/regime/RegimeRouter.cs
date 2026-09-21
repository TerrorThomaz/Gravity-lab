namespace TradingGA;

// BTC-anchored strategy router. Returns per-strategy boolean activation flags only — no sizing.
// ETH is a secondary confirmer (agreement boosts confidence, disagreement dampens it).
// Live path and backtests both delegate to ComputeActivation, the single source of truth.

// WARNING: many consecutive bools — always construct with named arguments.
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
    double       SizingMult = 1.0, // continuous sizing modifier ∈ [0, 1] by regime confidence/type
    bool         FadeShortLowVolActive = false,
    bool         DipLongLowVolActive = false,
    bool         SwingLongLowVolActive = false,
    bool         RipShortLowVolActive = false,
    bool         FadeShortHighVolActive = false,
    bool         DipLongHighVolActive = false,
    bool         SwingLongHighVolActive = false,
    bool         RipShortHighVolActive = false,
    double       AtrRatio = 0.0,
    double[]?    Weights = null)
{
    public double WeightOf(RegimeRouterGA.StrategyKind kind)
    {
        if (Weights is { Length: RegimeRouterGenotype.HmmStrategyRows })
        {
            int idx = kind switch
            {
                RegimeRouterGA.StrategyKind.FadeShort         => 0,
                RegimeRouterGA.StrategyKind.Grid              => 1,
                RegimeRouterGA.StrategyKind.GridShort         => 2,
                RegimeRouterGA.StrategyKind.DipLong           => 3,
                RegimeRouterGA.StrategyKind.FadeLong          => 4,
                RegimeRouterGA.StrategyKind.RipShort          => 5,
                RegimeRouterGA.StrategyKind.SwingLong         => 6,
                RegimeRouterGA.StrategyKind.AccumulationGrid  => 7,
                RegimeRouterGA.StrategyKind.FadeShortLowVol   => 0,
                RegimeRouterGA.StrategyKind.DipLongLowVol     => 3,
                RegimeRouterGA.StrategyKind.SwingLongLowVol   => 6,
                RegimeRouterGA.StrategyKind.RipShortLowVol    => 5,
                RegimeRouterGA.StrategyKind.FadeShortHighVol  => 0,
                RegimeRouterGA.StrategyKind.DipLongHighVol    => 3,
                RegimeRouterGA.StrategyKind.SwingLongHighVol  => 6,
                RegimeRouterGA.StrategyKind.RipShortHighVol   => 5,
                _ => -1,
            };
            return idx >= 0 ? Weights[idx] : 0.0;
        }
        bool active = kind switch
        {
            RegimeRouterGA.StrategyKind.FadeShort         => FadeShortActive,
            RegimeRouterGA.StrategyKind.Grid              => GridActive,
            RegimeRouterGA.StrategyKind.GridShort         => GridShortActive,
            RegimeRouterGA.StrategyKind.DipLong           => DipLongActive,
            RegimeRouterGA.StrategyKind.FadeLong          => FadeLongActive,
            RegimeRouterGA.StrategyKind.RipShort          => RipShortActive,
            RegimeRouterGA.StrategyKind.SwingLong         => SwingLongActive,
            RegimeRouterGA.StrategyKind.AccumulationGrid  => AccumulationGridActive,
            RegimeRouterGA.StrategyKind.FadeShortLowVol   => FadeShortLowVolActive,
            RegimeRouterGA.StrategyKind.DipLongLowVol     => DipLongLowVolActive,
            RegimeRouterGA.StrategyKind.SwingLongLowVol   => SwingLongLowVolActive,
            RegimeRouterGA.StrategyKind.RipShortLowVol    => RipShortLowVolActive,
            RegimeRouterGA.StrategyKind.FadeShortHighVol  => FadeShortHighVolActive,
            RegimeRouterGA.StrategyKind.DipLongHighVol    => DipLongHighVolActive,
            RegimeRouterGA.StrategyKind.SwingLongHighVol  => SwingLongHighVolActive,
            RegimeRouterGA.StrategyKind.RipShortHighVol   => RipShortHighVolActive,
            _ => false,
        };
        return active ? 1.0 : 0.0;
    }
}

public static class RegimeRouter
{
    // BtcStress level at which short strategies are force-activated. Constant, not a gene.
    public const double ShortOverrideStress = 0.5;

    public static bool HmmEnabled => Environment.GetEnvironmentVariable("GRAVITY_HMM") != "0";

    // Sigmoid steepness constant for HMM weight computation.
    private const double WeightSteepness = 6.0;

    // Short-strategy indices in HmmStrategies order: FadeShort=0, GridShort=2, RipShort=5.
    private static readonly bool[] IsShortRow = [true, false, true, false, false, true, false, false];

    internal static double[] ComputeWeights(double[] probs, RegimeRouterGenotype geno, double btcStress = 0.0, double[]? dailyPrior = null)
    {
        int maxR = Math.Min(probs.Length, RegimeRouterGenotype.MaxHmmStates);
        var weights = new double[RegimeRouterGenotype.HmmStrategyRows];

        var blendedProbs = probs;
        if (dailyPrior != null && dailyPrior.Length >= maxR)
        {
            double blendFactor = geno.BlendFactor;
            blendedProbs = new double[probs.Length];
            for (int i = 0; i < maxR; i++)
                blendedProbs[i] = blendFactor * probs[i] + (1 - blendFactor) * dailyPrior[i];
            for (int i = maxR; i < probs.Length; i++)
                blendedProbs[i] = probs[i];
        }

        double floorPct = geno.StrategyFloorPct;

        for (int s = 0; s < RegimeRouterGenotype.HmmStrategyRows; s++)
        {
            double exposure = 0.0;
            for (int r = 0; r < maxR; r++)
                exposure += blendedProbs[r] * geno.Favorability![s * RegimeRouterGenotype.MaxHmmStates + r];
            double bias = geno.Biases![s];
            double rawW = 1.0 / (1.0 + Math.Exp(-WeightSteepness * (exposure - bias)));
            double w = Math.Max(floorPct, rawW);
            if (btcStress >= ShortOverrideStress && IsShortRow[s])
                w = Math.Max(w, 0.9);
            weights[s] = w;
        }
        return weights;
    }

    private const double DirectionalMinConf = 0.45;
    private const double DefaultBullMinBars = 200;
    private const double DefaultBearMinBars = 200;
    private const double DefaultGridMaxConf = 0.45;
    private const double BtcWeight          = 0.70;
    private const double EthWeight          = 0.30;

    // Route using a trained genotype.
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
        // Previous regime: look back `duration` bars. Must match RegimeRouterSession.
        int bar        = btcSeries.Length - 1;
        int prevBar    = Math.Max(0, bar - btc.Duration);
        var prevRegime = btcSeries[prevBar].Regime;
        return ActivateWithGeno(btc.Regime, blendedConf, btc.Duration, geno, prevRegime, atrRatio);
    }

    // HMM-aware live route. When an HMM genotype is supplied and HMM mode is on, annotates the
    // BTC series with state probabilities, computes continuous per-strategy weights from the
    // favorability matrix, and returns them in StrategyActivation.Weights. The boolean flags are
    // derived as weight > 0.5 so existing consumers keep working. Falls back to the threshold
    // route when HMM is disabled or the genotype is absent.
    public static StrategyActivation Route(Candle[] btcH1, RegimeRouterGenotype geno, HmmGenotype hmm, Candle[]? ethH1 = null, double atrRatio = 1.0, double[]? dailyPrior = null)
    {
        if (!HmmEnabled || !geno.IsHmmGenotype || btcH1.Length < RegimeClassifier.Warmup)
            return Route(btcH1, geno, ethH1, atrRatio);

        var series = HmmAnnotator.Annotate(btcH1, hmm, geno.SmoothingAlpha);
        var btc    = series[^1];
        if (btc.HmmProbs == null)
            return Route(btcH1, geno, ethH1, atrRatio);

        var weights = ComputeWeights(btc.HmmProbs, geno, 0.0, dailyPrior);

        bool On(int row) => weights[row] > 0.5;
        bool isLowVol  = atrRatio < 0.8;
        bool isHighVol = atrRatio > 1.5;

        return new StrategyActivation(
            FadeShortActive: On(0),
            GridActive: On(1),
            GridShortActive: On(2),
            DipLongActive: On(3),
            FadeLongActive: On(4),
            RipShortActive: On(5),
            SwingLongActive: On(6),
            AccumulationGridActive: On(7),
            Regime: btc.Regime,
            Confidence: btc.Confidence,
            SizingMult: ComputeSizingMult(btc.Regime, btc.Confidence, btc.Duration),
            FadeShortLowVolActive: isLowVol && On(0),
            DipLongLowVolActive: isLowVol && On(3),
            SwingLongLowVolActive: isLowVol && On(6),
            RipShortLowVolActive: isLowVol && On(5),
            FadeShortHighVolActive: isHighVol && On(0),
            DipLongHighVolActive: isHighVol && On(3),
            SwingLongHighVolActive: isHighVol && On(6),
            RipShortHighVolActive: isHighVol && On(5),
            AtrRatio: atrRatio,
            Weights: weights);
    }

    // Rule-based fallback (no trained genotype).
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
            // BTC leads; ETH contrary signal dampens
            finalRegime = btcRegime;
            blendedConf = Math.Max(0.0, BtcWeight * btcConf - EthWeight * ethConf);
        }

        return Activate(finalRegime, Math.Min(1.0, blendedConf), atrRatio);
    }


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

    // Rule-based activation (no genotype, no duration gate).
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

        bool isLowVol = atrRatio < 0.8;
        bool fadeShortLowVol = isLowVol && fadeShort;
        bool dipLongLowVol   = isLowVol && dipLong;
        bool swingLongLowVol = isLowVol && swingLong;
        bool ripShortLowVol  = isLowVol && ripShort;

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

    // Genotype-aware activation. Delegates to ComputeActivation; adds HighVol short-circuit.
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
                SizingMult: 0.2, // HighVol regime → minimal sizing
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
            SizingMult: ComputeSizingMult(regime, conf, duration),
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

    // Continuous sizing modifier ∈ [0,1] by regime confidence/type. High-confidence Bull/Bear → 1.0;
    // Ranging → reduced for directional bets; transition zones → further reduced; HighVol → minimal.
    private static double ComputeSizingMult(MarketRegime regime, double conf, int duration)
    {
        double mult = 1.0;

        switch (regime)
        {
            case MarketRegime.Bull:
            case MarketRegime.Bear:
                // Linear ramp: low conf → reduced size even in directional regimes.
                mult = Math.Clamp(conf, 0.3, 1.0);
                break;

            case MarketRegime.Ranging:
                // Grid-family strategies thrive here; directional bets don't. Reduce.
                mult = conf > 0.6 ? 0.7 : 0.4;
                break;

            case MarketRegime.HighVol:
                // Directional bets unreliable; always reduce.
                mult = 0.2;
                break;
        }

        // Additional penalty in transition zones (low-duration regime switches).
        if (duration < 10) mult *= 0.7;

        return Math.Clamp(mult, 0.0, 1.0);
    }

    // Single source of truth for genotype-based activation. Shared by live Route and backtests.
    internal static (bool FadeShort, bool Grid, bool GridShort, bool DipLong, bool FadeLong, bool RipShort, bool SwingLong, bool AccumulationGrid, bool FadeShortLowVol, bool DipLongLowVol, bool SwingLongLowVol, bool RipShortLowVol, bool FadeShortHighVol, bool DipLongHighVol, bool SwingLongHighVol, bool RipShortHighVol) ComputeActivation(
        MarketRegime regime, double conf, int duration, MarketRegime prevRegime, RegimeRouterGenotype geno,
        double atrRatio = 1.0, double btcStress = 0.0)
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

        // FadeShort: Bear-only when gene is on, otherwise legacy not-confirmed-Bull rule.
        // BTC-shock override: btcStress >= threshold force-activates shorts (only adds, never disables).
        bool btcShock = btcStress >= ShortOverrideStress;

        // WARNING: override sits outside the ternary — `btcShock || cond ? A : B` parses wrong.
        bool fadeShort = btcShock || (geno.FadeShortBearOnly > 0
            ? (regime == MarketRegime.Bear
               && duration >= (int)geno.FadeShortBearMinBars
               && conf >= geno.FadeShortBearMinConf)
            : !(regime == MarketRegime.Bull
                && duration >= (int)geno.BullMinBars
                && conf >= geno.BullMinConf));
        bool grid      = regime == MarketRegime.Ranging
                        || conf < geno.GridMaxConf
                        || (inTransition && geno.TransitionSizeMult > 0);
        // GridShort shares Grid's gate — same ranging/ambiguous signal, opposite direction.
        bool gridShort = grid || btcShock;
        bool dipLong   = (regime == MarketRegime.Bull
                          && duration >= (int)geno.BullMinBars
                          && conf >= geno.BullMinConf)
                         || earlyFromBear || earlyFromRanging;
        bool swingLong = dipLong;
        bool fadeLong  = (regime == MarketRegime.Bear
                          && duration >= (int)geno.BearMinBars
                          && conf >= geno.BearMinConf)
                         || bearCarry;
        // RipShort: with-trend, no bearCarry. Own confirmed-bear gate (separate from FadeLong's).
        bool ripShort  = btcShock || (regime == MarketRegime.Bear
                         && duration >= (int)geno.RipShortBearMinBars
                         && conf >= geno.RipShortBearMinConf);

        // AccumulationGrid: confirmed bull OR confirmed bear.
        bool accumulationGrid = (regime == MarketRegime.Bull
                                 && duration >= (int)geno.BullMinBars
                                 && conf >= geno.BullMinConf)
                                || (regime == MarketRegime.Bear
                                    && duration >= (int)geno.BearMinBars
                                    && conf >= geno.BearMinConf);

        bool isLowVol = atrRatio < 0.8;
        bool fadeShortLowVol = isLowVol && fadeShort;
        bool dipLongLowVol   = isLowVol && dipLong;
        bool swingLongLowVol = isLowVol && swingLong;
        bool ripShortLowVol  = isLowVol && ripShort;

        bool isHighVol = atrRatio > 1.5;
        bool fadeShortHighVol = isHighVol && fadeShort;
        bool dipLongHighVol   = isHighVol && dipLong;
        bool swingLongHighVol = isHighVol && swingLong;
        bool ripShortHighVol  = isHighVol && ripShort;

        return (fadeShort, grid, gridShort, dipLong, fadeLong, ripShort, swingLong, accumulationGrid, fadeShortLowVol, dipLongLowVol, swingLongLowVol, ripShortLowVol, fadeShortHighVol, dipLongHighVol, swingLongHighVol, ripShortHighVol);
    }


    private static double BlendConf(double ethWeight, RegimeBar btc, RegimeBar? eth)
    {
        if (eth == null || ethWeight < 1e-6) return btc.Confidence;
        double w = ethWeight;
        return eth.Regime == btc.Regime
            ? (1 - w) * btc.Confidence + w * eth.Confidence
            : Math.Max(0.0, (1 - w) * btc.Confidence - w * eth.Confidence);
    }
}

// Pre-computed per-trade regime lookup. Build once, call IsActive per trade.
public class RegimeRouterSession
{
    private readonly RegimeBar[]              _btc;
    private Candle[]?                         _btcBars;
    private double                            _shockLookbackBars = 24;
    private readonly RegimeBar[]?             _eth;
    private readonly RegimeRouterGenotype     _geno;
    private readonly Dictionary<long, int>   _idx;
    private StrategyFamilyGating.Gate?        _gate;

    private readonly bool[] _wasActive = new bool[RegimeRouterGenotype.HmmStrategyRows];
    private readonly double[] _previousWeight = new double[RegimeRouterGenotype.HmmStrategyRows];
    private double[]? _dailyPrior;
    private RegimeBar[]? _legacyBtc;

    public RegimeRouterSession(RegimeBar[] btcSeries, RegimeBar[]? ethSeries, RegimeRouterGenotype geno)
    {
        _btc  = btcSeries;
        _eth  = ethSeries;
        _geno = geno;
        _idx  = new Dictionary<long, int>(btcSeries.Length);
        for (int i = 0; i < btcSeries.Length; i++)
            _idx.TryAdd(HourKey(btcSeries[i].Time), i);
        for (int i = 0; i < _wasActive.Length; i++) _wasActive[i] = true;
        for (int i = 0; i < _previousWeight.Length; i++) _previousWeight[i] = 1.0;
    }

    public RegimeRouterSession WithDailyPrior(double[]? dailyPrior)
    {
        _dailyPrior = dailyPrior;
        return this;
    }

    public RegimeRouterSession WithLegacyRegime(RegimeBar[]? legacyBtc)
    {
        _legacyBtc = legacyBtc;
        return this;
    }

    // Attach a SIMFAM family gate. When present and HMM mode is on, routing uses the gate
    // (empirical regime→family profitability) instead of the favorability matrix.
    public RegimeRouterSession WithGate(StrategyFamilyGating.Gate? gate)
    {
        _gate = gate;
        return this;
    }

    // Graded size in [0,1]. Returns 0 when gated off. Ramps from GradedSizeFloor to 1.0
    // across [GradedConfStart, GradedConfFull].
    public double SizeMult(RegimeRouterGA.StrategyKind kind, DateTime tradeTime, double atrRatio = 1.0)
    {
        if (!IsActive(kind, tradeTime, atrRatio)) return 0.0;
        int bar = Lookup(tradeTime);
        double conf = Blend(_btc[bar], bar);
        double t = (conf - Config.GradedConfStart) / (Config.GradedConfFull - Config.GradedConfStart);
        return Config.GradedSizeFloor + (1.0 - Config.GradedSizeFloor) * Math.Clamp(t, 0.0, 1.0);
    }

    // Delegates to ComputeActivation (shared with live Route). GRAVITY_NOSHOCK=1 disables shock override.
    public RegimeRouterSession WithBtcBars(Candle[]? bars)
    {
        _btcBars = Environment.GetEnvironmentVariable("GRAVITY_NOSHOCK") == "1" ? null : bars;
        return this;
    }

    private double StressAt(DateTime t)
    {
        if (_btcBars is not { Length: > 1 }) return 0.0;
        var b = _btcBars;
        int lo = 0, hi = b.Length - 1;
        if (t <= b[0].Time) return 0.0;
        if (t < b[^1].Time)
            while (lo < hi) { int m = (lo + hi + 1) / 2; if (b[m].Time <= t) lo = m; else hi = m - 1; }
        else lo = b.Length - 1;
        int i0 = Math.Max(0, lo - (int)_shockLookbackBars);
        double p0 = b[i0].Close;
        double move = p0 > 1e-12 ? (b[lo].Close - p0) / p0 * 100.0 : 0.0;
        return VolatilityWeightedRotator.BtcStress(move);
    }

    public bool IsActive(RegimeRouterGA.StrategyKind kind, DateTime tradeTime, double atrRatio = 1.0)
    {
        if (RegimeRouter.HmmEnabled && _geno.IsHmmGenotype && _btc[Lookup(tradeTime)].HmmProbs != null)
        {
            if (_gate != null)
            {
                int bar = Lookup(tradeTime);
                var regime = _btc[bar].Regime;
                if (_gate.FamilyOf.ContainsKey(kind.ToString()))
                    return StrategyFamilyGating.IsActive(_gate, kind.ToString(), regime);
            }

            bool legacyActive = LegacyIsActive(kind, tradeTime, atrRatio);
            if (!legacyActive) return false;

            double currentWeight = Weight(kind, tradeTime, atrRatio);
            if (currentWeight < 0.50) return true;

            int baseIdx = GetBaseIndex(kind);
            if (baseIdx < 0) return false;

            bool wasActive = _wasActive[baseIdx];
            double threshold = wasActive ? _geno.DeactivateThreshold : _geno.ActivateThreshold;
            bool isActive = currentWeight >= threshold;
            _wasActive[baseIdx] = isActive;
            return isActive;
        }

        return LegacyIsActive(kind, tradeTime, atrRatio);
    }

    private static int GetBaseIndex(RegimeRouterGA.StrategyKind kind) => kind switch
    {
        RegimeRouterGA.StrategyKind.FadeShort         => 0,
        RegimeRouterGA.StrategyKind.Grid              => 1,
        RegimeRouterGA.StrategyKind.GridShort         => 2,
        RegimeRouterGA.StrategyKind.DipLong           => 3,
        RegimeRouterGA.StrategyKind.FadeLong          => 4,
        RegimeRouterGA.StrategyKind.RipShort          => 5,
        RegimeRouterGA.StrategyKind.SwingLong         => 6,
        RegimeRouterGA.StrategyKind.AccumulationGrid  => 7,
        RegimeRouterGA.StrategyKind.FadeShortLowVol   => 0,
        RegimeRouterGA.StrategyKind.DipLongLowVol     => 3,
        RegimeRouterGA.StrategyKind.SwingLongLowVol   => 6,
        RegimeRouterGA.StrategyKind.RipShortLowVol    => 5,
        RegimeRouterGA.StrategyKind.FadeShortHighVol  => 0,
        RegimeRouterGA.StrategyKind.DipLongHighVol    => 3,
        RegimeRouterGA.StrategyKind.SwingLongHighVol  => 6,
        RegimeRouterGA.StrategyKind.RipShortHighVol   => 5,
        _ => -1,
    };

    private bool LegacyIsActive(RegimeRouterGA.StrategyKind kind, DateTime tradeTime, double atrRatio = 1.0)
    {
        int bar = Lookup(tradeTime);
        var btc = _legacyBtc != null ? _legacyBtc[bar] : _btc[bar];
        double conf = Blend(btc, bar);

        bool inBullTransition = btc.Regime == MarketRegime.Bull
                               && btc.Duration < (int)_geno.BullMinBars;


        MarketRegime prevRegime = MarketRegime.Ranging;
        if (inBullTransition)
        {
            int prevBar = Math.Max(0, bar - btc.Duration);
            prevRegime  = (_legacyBtc != null ? _legacyBtc : _btc)[prevBar].Regime;
        }

        var (fadeShort, grid, gridShort, dipLong, fadeLong, ripShort, swingLong, accumulationGrid, fadeShortLowVol, dipLongLowVol, swingLongLowVol, ripShortLowVol, fadeShortHighVol, dipLongHighVol, swingLongHighVol, ripShortHighVol) =
            RegimeRouter.ComputeActivation(btc.Regime, conf, btc.Duration, prevRegime, _geno, atrRatio,
                                           StressAt(tradeTime));

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

    public double Weight(RegimeRouterGA.StrategyKind kind, DateTime tradeTime, double atrRatio = 1.0)
    {
        int bar = Lookup(tradeTime);
        var btc = _btc[bar];

        if (RegimeRouter.HmmEnabled && _geno.IsHmmGenotype && btc.HmmProbs != null)
        {
            if (_gate != null)
            {
                var regime = btc.Regime;
                double conf = StrategyFamilyGating.FamilyConfidence(_gate, kind.ToString(), regime);

                bool gateLowVol = kind is RegimeRouterGA.StrategyKind.FadeShortLowVol
                                       or RegimeRouterGA.StrategyKind.DipLongLowVol
                                       or RegimeRouterGA.StrategyKind.SwingLongLowVol
                                       or RegimeRouterGA.StrategyKind.RipShortLowVol;
                bool gateHighVol = kind is RegimeRouterGA.StrategyKind.FadeShortHighVol
                                        or RegimeRouterGA.StrategyKind.DipLongHighVol
                                        or RegimeRouterGA.StrategyKind.SwingLongHighVol
                                        or RegimeRouterGA.StrategyKind.RipShortHighVol;

                if (gateLowVol && atrRatio >= 0.8) return 0.0;
                if (gateHighVol && atrRatio <= 1.5) return 0.0;

                return conf;
            }

            double stress = Environment.GetEnvironmentVariable("GRAVITY_NOSHOCK") == "1" ? 0.0 : StressAt(tradeTime);
            var weights = RegimeRouter.ComputeWeights(btc.HmmProbs, _geno, stress, _dailyPrior);

            int baseIdx = GetBaseIndex(kind);
            if (baseIdx < 0) return 0.0;

            double w = weights[baseIdx];

            if (btc.Duration < (int)_geno.MinHoldBars)
                w = _previousWeight[baseIdx];
            else
                _previousWeight[baseIdx] = w;

            bool isLowVolKind = kind is RegimeRouterGA.StrategyKind.FadeShortLowVol
                                     or RegimeRouterGA.StrategyKind.DipLongLowVol
                                     or RegimeRouterGA.StrategyKind.SwingLongLowVol
                                     or RegimeRouterGA.StrategyKind.RipShortLowVol;
            bool isHighVolKind = kind is RegimeRouterGA.StrategyKind.FadeShortHighVol
                                      or RegimeRouterGA.StrategyKind.DipLongHighVol
                                      or RegimeRouterGA.StrategyKind.SwingLongHighVol
                                      or RegimeRouterGA.StrategyKind.RipShortHighVol;

            if (isLowVolKind && atrRatio >= 0.8) return 0.0;
            if (isHighVolKind && atrRatio <= 1.5) return 0.0;

            return w;
        }

        return LegacyIsActive(kind, tradeTime, atrRatio) ? 1.0 : 0.0;
    }

    public double SizeGate(RegimeRouterGA.StrategyKind kind, DateTime tradeTime, double atrRatio = 1.0)
    {
        if (RegimeRouter.HmmEnabled && _geno.IsHmmGenotype && _btc[Lookup(tradeTime)].HmmProbs != null)
        {
            bool legacyActive = LegacyIsActive(kind, tradeTime, atrRatio);
            if (!legacyActive) return 0.0;

            double w = Weight(kind, tradeTime, atrRatio);
            if (w < 0.50) return 1.0;
            return w;
        }

        return IsActive(kind, tradeTime, atrRatio) ? 1.0 : 0.0;
    }

    // Soft weight for coevolution gates: confidence-based gradient, step cutoff at 0.65.
    // Ignores router's trained thresholds so the strategy GA always has a gradient.
    // In HMM mode, returns the continuous weight directly.
    private const double StepCutoffConf = 0.65;
    public double GetWeight(RegimeRouterGA.StrategyKind kind, DateTime tradeTime)
    {
        if (RegimeRouter.HmmEnabled && _geno.IsHmmGenotype)
            return Weight(kind, tradeTime);

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
            // RipShort shares FadeLong's bear gradient.
            RegimeRouterGA.StrategyKind.RipShort => btc.Regime switch
            {
                MarketRegime.Bear                             => conf,
                MarketRegime.Bull when conf > StepCutoffConf => 0.0,
                _                                             => 0.0,
            },
            _                                     => 0.0,
        };
    }

    // Contiguous spans where regime == target for ≥ minBars. Returns full run extent.
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

    private static long HourKey(DateTime t) => t.Ticks / TimeSpan.TicksPerHour;

    private int Lookup(DateTime t)
    {
        long key = HourKey(t);
        if (_idx.TryGetValue(key, out int bar)) return bar;
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
