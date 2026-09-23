using System.Text.Json;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class RegimeRouterHmmTests
{
    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-6.0 * x));

    private static RegimeRouterGenotype HmmGeno(double[] fav, double[] biases, double statesN = 4.0) =>
        new() { Favorability = fav, Biases = biases, HmmStatesN = statesN };

    private static RegimeRouterGenotype MakeNeutralHmmGeno()
    {
        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        return HmmGeno(fav, bias, 4.0);
    }

    [Fact]
    public void ComputeWeights_OneHot_MatchesSigmoidExactly()
    {
        var rng = new Random(42);
        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        for (int i = 0; i < fav.Length; i++) fav[i] = rng.NextDouble() * 2 - 1;
        for (int i = 0; i < bias.Length; i++) bias[i] = rng.NextDouble() * 2 - 1;
        var geno = HmmGeno(fav, bias, 4.0);
        geno.StrategyFloorPct = 0.0;

        for (int r = 0; r < 4; r++)
        {
            var probs = new double[RegimeRouterGenotype.MaxHmmStates];
            probs[r] = 1.0;
            var w = RegimeRouter.ComputeWeights(probs, geno);

            for (int s = 0; s < RegimeRouterGenotype.HmmStrategyRows; s++)
            {
                double expected = Sigmoid(fav[s * RegimeRouterGenotype.MaxHmmStates + r] - bias[s]);
                Assert.Equal(expected, w[s], 12);
            }
        }
    }

    [Fact]
    public void ComputeWeights_ExtremeFavorability_PushesWeightsToExtremes()
    {
        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        for (int s = 0; s < RegimeRouterGenotype.HmmStrategyRows; s++)
        {
            fav[s * RegimeRouterGenotype.MaxHmmStates + 0] = 1.0;
            fav[s * RegimeRouterGenotype.MaxHmmStates + 1] = -1.0;
        }
        var geno = HmmGeno(fav, bias, 4.0);
        geno.StrategyFloorPct = 0.0;

        var probsPos = new double[RegimeRouterGenotype.MaxHmmStates];
        probsPos[0] = 1.0;
        var wPos = RegimeRouter.ComputeWeights(probsPos, geno);
        for (int s = 0; s < RegimeRouterGenotype.HmmStrategyRows; s++)
            Assert.InRange(wPos[s], 0.99, 1.0);

        var probsNeg = new double[RegimeRouterGenotype.MaxHmmStates];
        probsNeg[1] = 1.0;
        var wNeg = RegimeRouter.ComputeWeights(probsNeg, geno);
        for (int s = 0; s < RegimeRouterGenotype.HmmStrategyRows; s++)
            Assert.InRange(wNeg[s], 0.0, 0.01);
    }

    [Fact]
    public void ComputeWeights_ConvexMix_ExposureIsMean()
    {
        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        for (int s = 0; s < RegimeRouterGenotype.HmmStrategyRows; s++)
        {
            fav[s * RegimeRouterGenotype.MaxHmmStates + 0] = 0.8;
            fav[s * RegimeRouterGenotype.MaxHmmStates + 1] = 0.2;
        }
        var geno = HmmGeno(fav, bias, 4.0);

        var probs = new double[RegimeRouterGenotype.MaxHmmStates];
        probs[0] = 0.5;
        probs[1] = 0.5;
        var w = RegimeRouter.ComputeWeights(probs, geno);

        for (int s = 0; s < RegimeRouterGenotype.HmmStrategyRows; s++)
        {
            double exposure = 0.5 * 0.8 + 0.5 * 0.2;
            double expected = Sigmoid(exposure - bias[s]);
            Assert.Equal(expected, w[s], 12);
        }
    }

    [Fact]
    public void ComputeWeights_ShockOverride_FloorsShortsAt09_LeavesLongsUntouched()
    {
        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        for (int i = 0; i < bias.Length; i++) bias[i] = 0.99;
        var geno = HmmGeno(fav, bias, 4.0);

        var probs = new double[RegimeRouterGenotype.MaxHmmStates];
        probs[0] = 1.0;
        var w = RegimeRouter.ComputeWeights(probs, geno, btcStress: 1.0);

        Assert.InRange(w[0], 0.9, 1.0);
        Assert.InRange(w[2], 0.9, 1.0);
        Assert.InRange(w[5], 0.9, 1.0);

        Assert.InRange(w[1], 0.0, 0.5);
        Assert.InRange(w[3], 0.0, 0.5);
        Assert.InRange(w[4], 0.0, 0.5);
        Assert.InRange(w[6], 0.0, 0.5);
        Assert.InRange(w[7], 0.0, 0.5);
    }

    [Fact]
    public void GenotypeVectorRoundTrip_PreservesHmmGenes()
    {
        var rng = new Random(99);
        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        for (int i = 0; i < fav.Length; i++) fav[i] = rng.NextDouble() * 2 - 1;
        for (int i = 0; i < bias.Length; i++) bias[i] = rng.NextDouble() * 2 - 1;
        var geno = HmmGeno(fav, bias, 5.0);
        geno.BullMinBars = 77;
        geno.BullMinConf = 0.42;

        var v = geno.ToVector();
        var back = RegimeRouterGenotype.FromVector(v);

        Assert.Equal(geno.BullMinBars, back.BullMinBars);
        Assert.Equal(geno.BullMinConf, back.BullMinConf);
        Assert.NotNull(back.Favorability);
        Assert.NotNull(back.Biases);
        for (int i = 0; i < fav.Length; i++)
            Assert.Equal(fav[i], back.Favorability[i], 10);
        for (int i = 0; i < bias.Length; i++)
            Assert.Equal(bias[i], back.Biases[i], 10);
        Assert.Equal(5.0, back.HmmStatesN);
    }

    [Fact]
    public void Dto_NullFavorability_RoundTripsAsLegacy()
    {
        var json = """
        {
          "BullMinBars": 50,
          "BullMinConf": 0.5,
          "BearMinBars": 60,
          "BearMinConf": 0.4,
          "GridMaxConf": 0.3,
          "EthBlendWeight": 0.1,
          "Fitness": 1.0
        }
        """;
        var dto = JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(json)!;
        var geno = dto.ToGenotype();

        Assert.Null(geno.Favorability);
        Assert.Null(geno.Biases);
        Assert.False(geno.IsHmmGenotype);
    }

    [Fact]
    public void Dto_WithHmmGenes_RoundTrips()
    {
        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        fav[0] = 0.5;
        bias[0] = -0.3;
        var geno = HmmGeno(fav, bias, 5);
        geno.BullMinBars = 80;
        geno.Fitness = 2.5;

        var dto = RegimeRouterGenotypeDto.From(geno);
        string json = JsonSerializer.Serialize(dto);
        var back = JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(json)!.ToGenotype();

        Assert.True(back.IsHmmGenotype);
        Assert.Equal(0.5, back.Favorability[0]);
        Assert.Equal(-0.3, back.Biases[0]);
        Assert.Equal(5, back.HmmStatesN);
        Assert.Equal(80, back.BullMinBars);
    }

    // Hybrid routing (abbc8b2): the legacy regime gate decides on/off, the HMM weight only sizes.
    // Must match RegimeRouterGA.FilterActive, which trains on exactly this rule.
    [Fact]
    public void Session_HybridMode_LegacyGatesAndHmmOnlySizes()
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = new RegimeBar[600];
        var probs = new double[RegimeRouterGenotype.MaxHmmStates];
        probs[0] = 0.7; probs[1] = 0.3;
        for (int i = 0; i < bars.Length; i++)
            bars[i] = new RegimeBar(baseTime.AddHours(i), MarketRegime.Bull, 0.8, 10, (double[])probs.Clone());

        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        for (int s = 0; s < RegimeRouterGenotype.HmmStrategyRows; s++)
            fav[s * RegimeRouterGenotype.MaxHmmStates + 0] = 0.5;
        var geno = HmmGeno(fav, bias, 4.0);

        var session = new RegimeRouterSession(bars, null, geno);
        // Same bars and genotype minus the HMM probs → pure legacy threshold routing.
        var legacyBars = bars.Select(b => b with { HmmProbs = null }).ToArray();
        var legacy = new RegimeRouterSession(legacyBars, null, geno);
        var t = baseTime.AddHours(300);

        foreach (var kind in new[]
        {
            RegimeRouterGA.StrategyKind.FadeShort,
            RegimeRouterGA.StrategyKind.Grid,
            RegimeRouterGA.StrategyKind.DipLong,
            RegimeRouterGA.StrategyKind.FadeLong,
            RegimeRouterGA.StrategyKind.RipShort,
            RegimeRouterGA.StrategyKind.SwingLong,
        })
        {
            bool legacyActive = legacy.IsActive(kind, t);
            double w = session.Weight(kind, t);
            // w here is 0.89 — above both hysteresis thresholds, so the legacy gate alone decides.
            Assert.True(w >= geno.ActivateThreshold);
            Assert.Equal(legacyActive, session.IsActive(kind, t));
            Assert.Equal(legacyActive ? w : 0.0, session.SizeGate(kind, t), 12);
        }
        // Guard against a vacuous pass: at least one strategy must be legacy-active here.
        Assert.Contains(true, Enum.GetValues<RegimeRouterGA.StrategyKind>().Select(k => legacy.IsActive(k, t)));
    }

    [Fact]
    public void Session_LegacyBars_RouteViaThresholds()
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = new RegimeBar[600];
        for (int i = 0; i < bars.Length; i++)
            bars[i] = new RegimeBar(baseTime.AddHours(i), MarketRegime.Bull, 0.9, 500, null);

        var geno = RegimeRouterGenotype.Random(new Random(7));
        var session = new RegimeRouterSession(bars, null, geno);
        var t = baseTime.AddHours(300);

        double w = session.Weight(RegimeRouterGA.StrategyKind.DipLong, t);
        Assert.True(w == 1.0 || w == 0.0);
        Assert.Equal(w > 0.5, session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t));
    }

    [Fact]
    public void Session_HmmGenotypeWithNullProbBars_FallsBackToLegacyWithoutRecursion()
    {
        // Regression: HMM genotype + enabled env with bars lacking HmmProbs (warmup or
        // legacy series) used to recurse IsActive -> Weight -> IsActive. Must fall back
        // to the legacy threshold path and terminate.
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var bars = new RegimeBar[600];
        for (int i = 0; i < bars.Length; i++)
            bars[i] = new RegimeBar(baseTime.AddHours(i), MarketRegime.Bull, 0.9, 500, null);

        var geno = MakeNeutralHmmGeno();
        geno.BullMinBars = 50;
        geno.BullMinConf = 0.5;
        Assert.True(geno.IsHmmGenotype);

        var session = new RegimeRouterSession(bars, null, geno);
        var t = baseTime.AddHours(300);

        double w = session.Weight(RegimeRouterGA.StrategyKind.DipLong, t);
        Assert.True(w == 1.0 || w == 0.0);
        Assert.Equal(w > 0.5, session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t));
    }

    [Fact]
    public void GravityHmmZero_ForcesLegacyEvenWithProbs()
    {
        var prev = Environment.GetEnvironmentVariable("GRAVITY_HMM");
        try
        {
            Environment.SetEnvironmentVariable("GRAVITY_HMM", "0");

            var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var bars = new RegimeBar[600];
            var probs = new double[RegimeRouterGenotype.MaxHmmStates];
            probs[0] = 1.0;
            for (int i = 0; i < bars.Length; i++)
                bars[i] = new RegimeBar(baseTime.AddHours(i), MarketRegime.Bull, 0.9, 500, (double[])probs.Clone());

            var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
            var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
            var geno = HmmGeno(fav, bias, 4.0);

            var session = new RegimeRouterSession(bars, null, geno);
            var t = baseTime.AddHours(300);

            double w = session.Weight(RegimeRouterGA.StrategyKind.DipLong, t);
            Assert.True(w == 1.0 || w == 0.0, $"Expected 0 or 1 in legacy mode, got {w}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GRAVITY_HMM", prev);
        }
    }

    [Fact]
    public void GaSmoke_HmmMode_ReturnsFiniteFitness()
    {
        var baseTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int nBars = 600;
        var bars = new RegimeBar[nBars];
        var rng = new Random(123);
        for (int i = 0; i < nBars; i++)
        {
            var p = new double[RegimeRouterGenotype.MaxHmmStates];
            double s = 0;
            for (int j = 0; j < 4; j++) { p[j] = rng.NextDouble(); s += p[j]; }
            for (int j = 0; j < 4; j++) p[j] /= s;
            bars[i] = new RegimeBar(baseTime.AddHours(i), MarketRegime.Bull, 0.8, 10, p);
        }

        var trades = new List<RegimeRouterGA.TradeRecord>();
        var kinds = new[] { RegimeRouterGA.StrategyKind.FadeShort, RegimeRouterGA.StrategyKind.DipLong };
        for (int i = 0; i < 80; i++)
        {
            var kind = kinds[i % kinds.Length];
            var t = baseTime.AddHours(50 + i * 6);
            trades.Add(new RegimeRouterGA.TradeRecord(kind, t, rng.NextDouble() * 4 - 1, 0.1));
        }

        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        var seed = HmmGeno(fav, bias, 4.0);

        var ga = new RegimeRouterGA(populationSize: 6, generations: 2, verbose: false, seed: 42);
        var result = ga.Run(bars, null, trades, seed);

        Assert.True(double.IsFinite(result.Fitness));
        Assert.True(result.IsHmmGenotype);
    }

    [Fact]
    public void HmmBounds_AgreeWithVectorLength()
    {
        var rng = new Random(55);
        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        for (int i = 0; i < fav.Length; i++) fav[i] = rng.NextDouble() * 2 - 1;
        for (int i = 0; i < bias.Length; i++) bias[i] = rng.NextDouble() * 2 - 1;
        var geno = HmmGeno(fav, bias, 4.0);
        geno.BullMinBars = 60;
        geno.BullMinConf = 0.5;
        geno.BearMinBars = 50;
        geno.BearMinConf = 0.4;
        geno.RipShortBearMinBars = 70;
        geno.RipShortBearMinConf = 0.5;
        geno.GridMaxConf = 0.3;
        geno.EthBlendWeight = 0.1;
        geno.TransitionSizeMult = 0.5;
        geno.EarlyBullFromBearMult = 0.5;
        geno.EarlyBullFromRangingMult = 0.5;
        geno.EarlyBullBearCarry = 0.5;
        geno.FadeShortBearMinBars = 60;
        geno.FadeShortBearMinConf = 0.4;
        geno.FadeShortBearOnly = 0.5;

        var v = geno.ToVector();
        Assert.Equal(RegimeRouterGenotype.HmmBounds.GetLength(0), v.Length);

        var back = RegimeRouterGenotype.FromVector(v);
        Assert.Equal(v, back.ToVector());
    }

    [Fact]
    public void LegacyVectorLength_Unchanged()
    {
        var geno = RegimeRouterGenotype.Random(new Random(7));
        var v = geno.ToVector();
        Assert.Equal(15, v.Length);
        Assert.Equal(15, RegimeRouterGenotype.Bounds.GetLength(0));
    }

    [Fact]
    public void WeightOf_ActivationRecord_MatchesWeights()
    {
        var weights = new double[8] { 0.9, 0.1, 0.8, 0.7, 0.2, 0.6, 0.5, 0.3 };
        var act = new StrategyActivation(
            FadeShortActive: true, GridActive: false, GridShortActive: true,
            DipLongActive: true, FadeLongActive: false, RipShortActive: true,
            SwingLongActive: true, AccumulationGridActive: false,
            Regime: MarketRegime.Bull, Confidence: 0.8, Weights: weights);

        Assert.Equal(0.9, act.WeightOf(RegimeRouterGA.StrategyKind.FadeShort));
        Assert.Equal(0.1, act.WeightOf(RegimeRouterGA.StrategyKind.Grid));
        Assert.Equal(0.7, act.WeightOf(RegimeRouterGA.StrategyKind.DipLong));
    }

    [Fact]
    public void WeightOf_LegacyActivation_ReturnsBoolAsDouble()
    {
        var act = new StrategyActivation(
            FadeShortActive: true, GridActive: false, GridShortActive: true,
            DipLongActive: false, FadeLongActive: true, RipShortActive: false,
            SwingLongActive: true, AccumulationGridActive: false,
            Regime: MarketRegime.Bull, Confidence: 0.8);

        Assert.Equal(1.0, act.WeightOf(RegimeRouterGA.StrategyKind.FadeShort));
        Assert.Equal(0.0, act.WeightOf(RegimeRouterGA.StrategyKind.Grid));
        Assert.Equal(1.0, act.WeightOf(RegimeRouterGA.StrategyKind.FadeLong));
    }
}
