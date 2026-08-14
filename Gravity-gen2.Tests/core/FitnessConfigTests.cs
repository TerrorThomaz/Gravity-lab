// Gravity-gen2.Tests/core/FitnessConfigTests.cs
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class FitnessConfigTests
{
    // NOTE ON THE DEFAULTS BELOW:
    // GainW/WrW/QualityW/FreqW/DdPenalty/RetentionW used to default to
    // 1.0/0.9/0.8/0.6/1.2/1.0 — but they were read NOWHERE, so those numbers never
    // reached the fitness formula. Now that FoldScoreHelper.Canonical actually consumes
    // them, the defaults are all 1.0 ("neutral"), chosen so a default FitnessConfig
    // reproduces the historical hardcoded score bit-for-bit. The old 0.9/0.8/0.6/1.2
    // must NOT be restored: they would be a real (and never-validated) change of the
    // fitness function.
    [Fact]
    public void Defaults_AllWeightsCorrect()
    {
        var cfg = new FitnessConfig();
        Assert.Equal("default", cfg.VariantId);
        Assert.Equal(1.0,   cfg.GainW);
        Assert.Equal(1.0,   cfg.WrW);
        Assert.Equal(1.0,   cfg.QualityW);
        Assert.Equal(1.0,   cfg.FreqW);
        Assert.Equal(1.0,   cfg.DdPenalty);
        Assert.Equal(1.0,   cfg.RetentionW);
        Assert.Equal(0.5,   cfg.SharpeW);
        Assert.Equal(0.0,   cfg.CalmarW);
        Assert.Equal(0.0,   cfg.PfW);
        Assert.Equal(0.3,   cfg.SortinoW);
        Assert.Equal(0.0,   cfg.AtrLow);
        Assert.Equal(9999.0,cfg.AtrHigh);
        Assert.Equal(0.3,   cfg.CVaRW);
        Assert.Equal(0.2,   cfg.TailRatioW);
        Assert.Equal(0.2,   cfg.RegimeDiversityW);
        Assert.Equal(0.05,  cfg.EmbargoPct);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var cfg = FitnessConfig.Load("nonexistent_file_xyz.json");
        Assert.Equal(new FitnessConfig(), cfg);
    }

    [Fact]
    public void Load_ValidJson_OverridesFields()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """{"SharpeW":0.7,"CalmarW":0.9,"AtrLow":1.5,"AtrHigh":9999}""");
        var cfg = FitnessConfig.Load(path);
        File.Delete(path);
        Assert.Equal(0.7,  cfg.SharpeW);
        Assert.Equal(0.9,  cfg.CalmarW);
        Assert.Equal(1.5,  cfg.AtrLow);
        Assert.Equal(1.0,  cfg.GainW);   // unchanged default
    }

    // ── Canonical fold-score weight wiring ────────────────────────────────────
    //
    // Every series below is kept UNDER FoldScoreHelper.MinTailSampleSize trades on
    // purpose: CVaRPenalty and TailRatioBonus both short-circuit to exactly 1.0 below
    // that, so these tests isolate the six canonical term weights from the tail terms.
    //
    // The hard-coded expected values are re-derived by hand from the documented formula
    // whenever the formula changes; they are NOT read off the implementation. If one of
    // them drifts without a matching, deliberate formula change, the "neutral defaults
    // are a no-op" guarantee has been broken and every stored genotype's fitness has
    // silently changed meaning.
    //
    // THEY WERE LAST RE-DERIVED WHEN THE SHARPE/SORTINO TERMS WERE FIXED. Canonical used
    // to call Simulator.SharpeRatio(returns, returns.Count), passing a TRADE count into a
    // parameter documented as a count of 5-minute-equivalent CANDLES; that multiplied the
    // term by sqrt(nTrades/288) — a hidden second frequency bonus on top of freqBonus.
    // The terms are now FoldScoreHelper.PerTradeSharpe / PerTradeSortino (plain
    // mean/stdev, scale-free in the trade count), which is why every number below moved
    // up: these 10–12-trade fixtures were being scaled by sqrt(12/288) ~= 0.2 before.

    private const int    MinTrades = 5;
    private const double PosFrac   = 1.0;

    // 12 trades, win rate 0.50, profit factor 2.00, rr 2.00, ends below its peak.
    private static List<double> Baseline() => new()
        { 3.0, -1.5, 4.0, -2.0, 5.0, -1.0, 2.5, -3.0, 6.0, -2.5, 1.5, -1.0 };

    // 12 trades, win rate 0.50, profit factor 4.00, rr 4.00 -> raw quality 1.806 (> 1).
    private static List<double> HighQuality() => new()
        { 8.0, -2.0, 8.0, -2.0, -2.0, 8.0, -2.0, 8.0, -2.0, 8.0, -2.0, 8.0 };

    // 10 trades, win rate 0.30 -> sits BELOW the 0.40 win-rate knee.
    private static List<double> LowWinRate() => new()
        { 10.0, -2.0, -2.0, -2.0, 10.0, -2.0, -2.0, -2.0, 10.0, -2.0 };

    // 10 trades that finish exactly at their equity peak -> retentionRaw == 1.0.
    private static List<double> EndsAtPeak() => new()
        { 2.0, -1.0, 3.0, -1.0, 4.0, -1.0, 3.0, -1.0, 5.0, 2.0 };

    // Runs +60% then gives back -54% -> retentionRaw is pinned at its 0.2 floor.
    private static List<double> HeavyGiveBack() => new()
        { 20.0, 20.0, 20.0, -18.0, -18.0, -18.0, 1.0, -1.0, 2.0, -1.0 };

    private static double Score(List<double> returns, FitnessConfig cfg)
        => FoldScoreHelper.Canonical(returns, PosFrac, MinTrades, cfg);

    private static double Score(List<double> returns) => Score(returns, new FitnessConfig());

    // ── The no-op guarantee ───────────────────────────────────────────────────

    [Fact]
    public void NeutralDefaults_ReproduceHistoricalHardcodedScore()
    {
        // Hand-derived from the pre-wiring formula:
        //   wr=0.5      -> wrMult   = 1 + (0.50-0.40)*3.0            = 1.3
        //   pf=2.0      -> pfMult   = 1 + (2.0-1.5)*0.5              = 1.25
        //   rr=2.0      -> rrMult   = (2.0-1.0)/1.5                  = 0.666...
        //   quality     = min(sqrt(1.25*0.666...), 2.5)              = 0.912870929...
        //   freqQuality = clamp((pf-1)/(FreqPfFull-1),0,1) * clamp(avg/FreqAvgFullPct,0,1)
        //                 Baseline is pf 2.00 and avg +0.9167%/trade, so the pf ramp is full and
        //                 the avg ramp is 0.9167 -> freqQuality = 0.9167, NOT 1.0. That is the
        //                 whole change: volume credit is now scaled by whether the trades earning
        //                 it are any good.
        //   freqBonus   = 1 + 0.15*freqQuality*ln(12/5)
        //   ddDiv       = 1 + maxDd*10
        //   retention   = max(0.2, gain/peakGain)
        //   base        = gain*100 * wrMult * quality * freqBonus / ddDiv * retention
        //   score       = base * (1 + 0.5*sharpe/3) * (1 + 0.3*sortino/4)
        //                 (CalmarW and PfW default to 0 -> those factors are exactly 1)
        Assert.Equal(12.382864178863091, Score(Baseline()), precision: 9);
    }

    [Fact]
    public void NeutralDefaults_ReproduceHistoricalHardcodedScore_HighQualitySeries()
    {
        // Second fixture, this one with raw quality ABOVE 1.0, so it also pins the
        // quality lerp on the other side of the neutral point.
        Assert.Equal(77.60830060738729, Score(HighQuality()), precision: 9);
    }

    [Fact]
    public void NeutralDefaults_ReproduceHistoricalHardcodedScore_BelowWinRateKnee()
    {
        Assert.Equal(10.799709715315416, Score(LowWinRate()), precision: 9);
    }

    [Fact]
    public void ExplicitNeutralWeights_MatchDefaults()
    {
        var neutral = new FitnessConfig() with
        {
            GainW = 1.0, WrW = 1.0, QualityW = 1.0,
            FreqW = 1.0, DdPenalty = 1.0, RetentionW = 1.0
        };
        Assert.Equal(Score(Baseline()), Score(Baseline(), neutral), precision: 12);
        Assert.Equal(Score(HighQuality()), Score(HighQuality(), neutral), precision: 12);
    }

    // ── Directional wiring: each weight must actually move the score ──────────

    [Fact]
    public void DdPenalty_Higher_LowersScoreForDrawdownBearingSeries()
    {
        var r = Baseline();
        double neutral = Score(r);
        double harsh   = Score(r, new FitnessConfig() with { DdPenalty = 2.5 });
        double lenient = Score(r, new FitnessConfig() with { DdPenalty = 0.0 });

        Assert.True(harsh < neutral, $"DdPenalty 2.5 ({harsh}) should score below 1.0 ({neutral})");
        Assert.True(lenient > neutral, $"DdPenalty 0.0 ({lenient}) should score above 1.0 ({neutral})");
        Assert.Equal(9.3708161353558506,  harsh,   precision: 9);
        Assert.Equal(15.760008954916664, lenient, precision: 9);
    }

    [Fact]
    public void FreqW_Higher_RaisesScoreForHighTradeCountSeries()
    {
        var r = Baseline();   // 12 trades vs MinTrades 5 -> log bonus is live
        double neutral = Score(r);
        double eager   = Score(r, new FitnessConfig() with { FreqW = 2.0 });
        double off     = Score(r, new FitnessConfig() with { FreqW = 0.0 });

        Assert.True(eager > neutral, $"FreqW 2.0 ({eager}) should score above 1.0 ({neutral})");
        Assert.True(off < neutral,   $"FreqW 0.0 ({off}) should score below 1.0 ({neutral})");
        Assert.Equal(13.713319466644776, eager, precision: 9);
        Assert.Equal(11.05240889108141,  off,   precision: 9);
    }

    [Fact]
    public void WrW_Higher_RaisesScoreForAboveKneeWinRate()
    {
        var r = Baseline();   // wr 0.50, above the 0.40 knee
        double neutral = Score(r);
        double eager   = Score(r, new FitnessConfig() with { WrW = 2.0 });
        double off     = Score(r, new FitnessConfig() with { WrW = 0.0 });

        Assert.True(eager > neutral, $"WrW 2.0 ({eager}) should score above 1.0 ({neutral})");
        Assert.True(off < neutral,   $"WrW 0.0 ({off}) should score below 1.0 ({neutral})");
        Assert.Equal(15.240448220139193, eager, precision: 9);
        Assert.Equal(9.5252801375869964,  off,   precision: 9);
    }

    [Fact]
    public void WrW_DoesNotTouchTheSubKneePenaltyRamp()
    {
        // Below wr 0.40 the multiplier is a disqualifying penalty (wr / 0.40), not a
        // reward — WrW must leave it alone, or WrW = 0 would REWARD a terrible win rate.
        var r = LowWinRate();
        double neutral = Score(r);
        Assert.Equal(neutral, Score(r, new FitnessConfig() with { WrW = 3.0 }), precision: 12);
        Assert.Equal(neutral, Score(r, new FitnessConfig() with { WrW = 0.0 }), precision: 12);
    }

    [Fact]
    public void QualityW_Higher_RaisesScoreWhenRawQualityExceedsOne()
    {
        var r = HighQuality();   // raw quality 1.806
        double neutral = Score(r);
        double eager   = Score(r, new FitnessConfig() with { QualityW = 1.3 });

        Assert.True(eager > neutral, $"QualityW 1.3 ({eager}) should score above 1.0 ({neutral})");
        Assert.Equal(86.50182675083921, eager, precision: 9);
    }

    [Fact]
    public void QualityW_Higher_LowersScoreWhenRawQualityIsBelowOne()
    {
        // NOTE ON THE FIXTURE: this used to use Baseline(), whose raw quality was 0.913 under
        // the old linear rrMult = (rr-1)/1.5. That ramp was replaced by a bounded hyperbola
        // anchored so rrMult(2.5) == 1.0, under which Baseline's raw quality is 1.069 — ABOVE
        // the neutral point, so the direction this test asserts legitimately flips. A fixture
        // whose raw quality is genuinely sub-1 is needed instead, or the test would silently
        // stop testing what its name says.
        //
        // 12 trades alternating +2.4 / -2.0: pf 1.20, rr 1.20, raw quality 0.533.
        var r = Enumerable.Range(0, 12).Select(i => i % 2 == 0 ? 2.4 : -2.0).ToList();
        double neutral = Score(r);
        double eager   = Score(r, new FitnessConfig() with { QualityW = 2.0 });
        double off     = Score(r, new FitnessConfig() with { QualityW = 0.0 });

        Assert.True(eager < neutral, $"QualityW 2.0 ({eager}) should score below 1.0 ({neutral})");
        Assert.True(off > neutral,   $"QualityW 0.0 ({off}) should score above 1.0 ({neutral})");
        Assert.Equal(0.76904327300006325, neutral, precision: 9);
        Assert.Equal(0.096130409125007574, eager,   precision: 9);
        Assert.Equal(1.441956136875119, off,     precision: 9);
    }

    [Fact]
    public void QualityW_CannotPunchThroughThe2Point5Cap()
    {
        // Weighting is applied BEFORE the cap on purpose: the cap exists to stop one
        // spectacular fold's quality reading from dominating fitness, so a large
        // QualityW must saturate against it rather than bypass it.
        var r = HighQuality();
        double big    = Score(r, new FitnessConfig() with { QualityW = 3.0 });
        double bigger = Score(r, new FitnessConfig() with { QualityW = 5.0 });

        Assert.Equal(big, bigger, precision: 12);
        Assert.Equal(119.90803365636894, big, precision: 9);
    }

    [Fact]
    public void RetentionW_Higher_LowersScoreForGiveBackSeries()
    {
        var r = Baseline();   // ends below its peak -> retentionRaw < 1.0
        double neutral = Score(r);
        double harsh   = Score(r, new FitnessConfig() with { RetentionW = 2.0 });

        Assert.True(harsh < neutral, $"RetentionW 2.0 ({harsh}) should score below 1.0 ({neutral})");
        Assert.Equal(10.13143432816071, harsh, precision: 9);
    }

    [Fact]
    public void RetentionW_IsNoOpWhenSeriesEndsAtItsPeak()
    {
        var r = EndsAtPeak();   // retentionRaw == 1.0 -> nothing for the weight to scale
        double neutral = Score(r);
        Assert.Equal(neutral, Score(r, new FitnessConfig() with { RetentionW = 3.0 }), precision: 12);
        Assert.Equal(50.46283209729822, neutral, precision: 9);
    }

    [Fact]
    public void GainW_ScalesScoreLinearly()
    {
        var r = Baseline();
        double neutral = Score(r);
        double eager   = Score(r, new FitnessConfig() with { GainW = 1.5 });

        Assert.True(eager > neutral, $"GainW 1.5 ({eager}) should score above 1.0 ({neutral})");
        Assert.Equal(neutral * 1.5, eager, precision: 9);
        Assert.Equal(18.574296268294638, eager, precision: 9);
    }

    // ── Defensive clamping of pathological configured values ─────────────────

    [Fact]
    public void NegativeDdPenalty_ClampsToZero_NeverDividesByZeroOrFlipsSign()
    {
        // ddDiv = 1 + maxDd*10*DdPenalty. An unclamped DdPenalty of -1/maxDd/10 would
        // make ddDiv exactly 0; anything beyond that flips the sign of the fold score.
        var r = Baseline();
        double off = Score(r, new FitnessConfig() with { DdPenalty = 0.0 });
        Assert.Equal(off, Score(r, new FitnessConfig() with { DdPenalty = -5.0 }),   precision: 12);
        Assert.Equal(off, Score(r, new FitnessConfig() with { DdPenalty = -1000.0 }), precision: 12);
        Assert.True(off > 0);
    }

    [Fact]
    public void NegativeWeights_ClampToZero_RatherThanInvertingTheirTerm()
    {
        var r = Baseline();
        Assert.Equal(Score(r, new FitnessConfig() with { WrW      = 0.0 }),
                     Score(r, new FitnessConfig() with { WrW      = -1.0 }), precision: 12);
        Assert.Equal(Score(r, new FitnessConfig() with { QualityW = 0.0 }),
                     Score(r, new FitnessConfig() with { QualityW = -1.0 }), precision: 12);
        Assert.Equal(Score(r, new FitnessConfig() with { FreqW    = 0.0 }),
                     Score(r, new FitnessConfig() with { FreqW    = -3.0 }), precision: 12);
    }

    [Fact]
    public void GainW_ZeroOrNegative_ZeroesTheScoreInsteadOfInvertingIt()
    {
        var r = Baseline();
        Assert.Equal(0.0, Score(r, new FitnessConfig() with { GainW = 0.0 }),  precision: 12);
        Assert.Equal(0.0, Score(r, new FitnessConfig() with { GainW = -2.0 }), precision: 12);
    }

    [Fact]
    public void LargeRetentionW_FloorsAtZero_RatherThanGoingNegative()
    {
        // retentionRaw is pinned at its 0.2 floor here, so 0.2*6 + (1-6) = -3.8 without
        // the guard — which would invert the sign of an otherwise-profitable fold.
        var r = HeavyGiveBack();
        Assert.True(Score(r) > 0);
        Assert.Equal(0.0, Score(r, new FitnessConfig() with { RetentionW = 6.0 }), precision: 12);
        Assert.True(Score(r, new FitnessConfig() with { RetentionW = 20.0 }) >= 0.0);
    }
}
