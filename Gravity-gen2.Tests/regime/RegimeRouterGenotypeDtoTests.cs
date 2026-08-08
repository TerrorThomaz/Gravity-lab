using System.Text.Json;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// StrategyActivation.SizeMult was deleted (nothing sized on it), and with it the two genes that
// only ever scaled it: EarlyBearFromBullMult / EarlyBearFromRangingMult. Genotype JSON written
// before that removal still carries those keys on disk, and the file is NOT hand-edited — so the
// DTO must keep loading it. These tests pin that, plus the round-trip / gene-count invariants
// that the removal could have broken.
public class RegimeRouterGenotypeDtoTests
{
    // Verbatim shape of genotypes/regime_router_genotype.json as it exists on disk today,
    // including the two now-removed keys.
    private const string StaleJson = """
    {
      "BullMinBars": 50,
      "BullMinConf": 0.6299274066044116,
      "BearMinBars": 154.45647687065863,
      "BearMinConf": 0.6541974660653002,
      "GridMaxConf": 0.2567056993942268,
      "EthBlendWeight": 0.022482410636121056,
      "Fitness": 1.1657261937083983,
      "TransitionSizeMult": 0,
      "EarlyBullFromBearMult": 1,
      "EarlyBullFromRangingMult": 0.3066125304736788,
      "EarlyBullBearCarry": 0.8479225573960224,
      "RipShortBearMinBars": 50,
      "RipShortBearMinConf": 0.8,
      "EarlyBearFromBullMult": 1,
      "EarlyBearFromRangingMult": 0.3611760277209499
    }
    """;

    [Fact]
    public void StaleGenotypeJson_WithRemovedGenes_StillDeserializes()
    {
        var geno = JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(StaleJson)!.ToGenotype();

        // The surviving genes must come through untouched — the unknown keys are simply ignored,
        // not treated as positional and not allowed to shift anything.
        Assert.Equal(50, geno.BullMinBars);
        Assert.Equal(0.6299274066044116, geno.BullMinConf);
        Assert.Equal(154.45647687065863, geno.BearMinBars);
        Assert.Equal(0.6541974660653002, geno.BearMinConf);
        Assert.Equal(50, geno.RipShortBearMinBars);
        Assert.Equal(0.8, geno.RipShortBearMinConf);
        Assert.Equal(0.2567056993942268, geno.GridMaxConf);
        Assert.Equal(0.022482410636121056, geno.EthBlendWeight);
        Assert.Equal(0.0, geno.TransitionSizeMult);
        Assert.Equal(1.0, geno.EarlyBullFromBearMult);
        Assert.Equal(0.3066125304736788, geno.EarlyBullFromRangingMult);
        Assert.Equal(0.8479225573960224, geno.EarlyBullBearCarry);
    }

    [Fact]
    public void GenotypeSurvivesJsonRoundTrip()
    {
        var original = new RegimeRouterGenotype
        {
            BullMinBars              = 123,
            BullMinConf              = 0.42,
            BearMinBars              = 77,
            BearMinConf              = 0.31,
            RipShortBearMinBars      = 210,
            RipShortBearMinConf      = 0.55,
            GridMaxConf              = 0.28,
            EthBlendWeight           = 0.13,
            TransitionSizeMult       = 0.61,
            EarlyBullFromBearMult    = 0.72,
            EarlyBullFromRangingMult = 0.19,
            EarlyBullBearCarry       = 0.84,
            Fitness                  = 2.5,
        };

        string json = JsonSerializer.Serialize(RegimeRouterGenotypeDto.From(original));
        var back = JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(json)!.ToGenotype();

        Assert.Equal(original.ToVector(), back.ToVector());
        Assert.Equal(original.Fitness, back.Fitness);
    }

    // Bounds / ToVector / FromVector must all agree on the gene count, or the GA and the
    // Bayesian optimiser would silently index past each other.
    [Fact]
    public void VectorInterfaceAgreesWithBounds()
    {
        var rng  = new System.Random(7);
        var geno = RegimeRouterGenotype.Random(rng);
        double[] v = geno.ToVector();

        Assert.Equal(RegimeRouterGenotype.Bounds.GetLength(0), v.Length);
        Assert.Equal(12, v.Length);
        Assert.Equal(v, RegimeRouterGenotype.FromVector(v).ToVector());

        for (int i = 0; i < v.Length; i++)
        {
            Assert.InRange(v[i], RegimeRouterGenotype.Bounds[i, 0], RegimeRouterGenotype.Bounds[i, 1]);
        }
    }

    // Mutate must keep every gene inside its declared bounds, at any rate.
    [Fact]
    public void MutateStaysInBounds()
    {
        var rng  = new System.Random(11);
        var geno = RegimeRouterGenotype.Random(rng);

        for (int iter = 0; iter < 200; iter++)
        {
            geno = geno.Mutate(rng, 1.0);
            double[] v = geno.ToVector();
            Assert.Equal(RegimeRouterGenotype.Bounds.GetLength(0), v.Length);
            for (int i = 0; i < v.Length; i++)
                Assert.InRange(v[i], RegimeRouterGenotype.Bounds[i, 0], RegimeRouterGenotype.Bounds[i, 1]);
        }
    }
}
