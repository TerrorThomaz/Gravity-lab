using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Pins the three search-control primitives in src/core/GaSearch.cs.
//
// These are statistical / structural assertions, not fixed-sequence golden values: the point of
// the tournament test in particular is the SHAPE of the selection-pressure distribution, which
// must keep holding if the implementation is rewritten, not a specific stream of draws.
public class GaSearchTests
{
    // Population of 80 with strictly decreasing fitness, i.e. index 0 is the fittest.
    // Fitness is (80 - i) so ties are impossible and "bottom half" is exactly index >= 40.
    private static List<(int Index, double Fitness)> RankedPopulation(int n = 80) =>
        Enumerable.Range(0, n).Select(i => (Index: i, Fitness: (double)(n - i))).ToList();

    // ── Defect 1: selection pressure ─────────────────────────────────────────────

    // Under a k-way tournament over a uniformly-drawn sample, the probability that the winner
    // comes from the bottom half is (1/2)^k: every one of the k draws must land there.
    // k = 4 → 6.25%, k = 2 → 25%. The claim under test is that dropping k from 4 to 2 gives the
    // bottom half of the population MEANINGFULLY more reproductive probability — that is the
    // whole mechanism by which the exploration phase is lengthened.
    [Fact]
    public void Tournament_K2_DrawsFromBottomHalf_FarMoreOftenThanK4()
    {
        var pop  = RankedPopulation();
        int half = pop.Count / 2;
        const int draws = 40_000;

        // One RNG per arm, both seeded, so the test is deterministic while still being a
        // frequency assertion over many draws rather than a golden sequence.
        var rng2 = new Random(12345);
        var rng4 = new Random(12345);

        int bottom2 = 0, bottom4 = 0;
        for (int i = 0; i < draws; i++)
        {
            if (GaSearch.Tournament(pop, 2, rng2, x => x.Fitness).Index >= half) bottom2++;
            if (GaSearch.Tournament(pop, 4, rng4, x => x.Fitness).Index >= half) bottom4++;
        }

        double rate2 = (double)bottom2 / draws;
        double rate4 = (double)bottom4 / draws;

        // Theory: 0.25 and 0.0625. Tolerances are ~8 standard errors wide at n = 40,000.
        Assert.InRange(rate2, 0.235, 0.265);
        Assert.InRange(rate4, 0.055, 0.070);

        // The headline claim, stated independently of the exact rates so it survives a
        // reimplementation: k = 2 reaches the bottom half at least three times as often.
        Assert.True(rate2 > rate4 * 3.0,
            $"k=2 bottom-half rate {rate2:P2} should be >3x the k=4 rate {rate4:P2}");
    }

    // k = 2 must still be genuine selection — strictly stronger than a uniform draw (50%) and
    // strictly weaker than always taking the best. This guards against a future "fix" that
    // removes pressure entirely while the previous test still passes on a k comparison.
    [Fact]
    public void Tournament_K2_IsWeakerThanTruncationButStrongerThanUniform()
    {
        var pop  = RankedPopulation();
        int half = pop.Count / 2;
        var rng  = new Random(99);

        int bottom = 0;
        const int draws = 20_000;
        for (int i = 0; i < draws; i++)
            if (GaSearch.Tournament(pop, 2, rng, x => x.Fitness).Index >= half) bottom++;

        double rate = (double)bottom / draws;
        Assert.True(rate < 0.50, "k=2 must apply more pressure than a uniform draw");
        Assert.True(rate > 0.05, "k=2 must apply less pressure than truncation-to-top-decile");
    }

    [Fact]
    public void Tournament_K1_IsAUniformDraw()
    {
        var pop  = RankedPopulation();
        int half = pop.Count / 2;
        var rng  = new Random(7);

        int bottom = 0;
        const int draws = 20_000;
        for (int i = 0; i < draws; i++)
            if (GaSearch.Tournament(pop, 1, rng, x => x.Fitness).Index >= half) bottom++;

        Assert.InRange((double)bottom / draws, 0.47, 0.53);
    }

    [Fact]
    public void Tournament_AlwaysReturnsAMemberOfThePopulation()
    {
        var pop = RankedPopulation(5);
        var rng = new Random(3);
        for (int i = 0; i < 200; i++)
            Assert.Contains(GaSearch.Tournament(pop, 2, rng, x => x.Fitness), pop);
    }

    // ── Defect 2: the cataclysm ──────────────────────────────────────────────────

    [Fact]
    public void Cataclysm_KeepsSurvivorsVerbatim_ReplacesTheRest_AndResetsTheCounter()
    {
        // Sorted best-first, exactly as every GA hands it in.
        var sorted = Enumerable.Range(0, 40).Select(i => $"incumbent{i}").ToList();

        int stagnant = 17;
        int fresh    = 0;
        var next = GaSearch.Cataclysm(sorted, populationSize: 40, survivors: 5,
                                      randomFactory: () => $"fresh{fresh++}",
                                      stagnantGens: ref stagnant);

        Assert.Equal(40, next.Count);

        // Elite carry-over survives the restart: the top 5 are the SAME objects, in order.
        // This is how the best-so-far genotype is not destroyed by a restart.
        Assert.Equal(sorted.Take(5), next.Take(5));

        // Everything past the survivors came from the random factory — no incumbent leaked in.
        Assert.All(next.Skip(5), g => Assert.StartsWith("fresh", g));
        Assert.Equal(35, fresh);

        // PULSE, not latch: the counter is cleared so the next restart needs a fresh stagnation
        // run. The old mutation-rate boost read a counter that elitism could never bring back
        // down, so once it fired it stayed on for the rest of the run.
        Assert.Equal(0, stagnant);
    }

    [Fact]
    public void Cataclysm_SurvivorCountIsClampedToAtLeastOneAndAtMostThePopulation()
    {
        var sorted = Enumerable.Range(0, 10).Select(i => i.ToString()).ToList();

        int stagnant = 20;
        var zero = GaSearch.Cataclysm(sorted, 10, survivors: 0, () => "x", ref stagnant);
        Assert.Equal("0", zero[0]);              // at least the champion always survives
        Assert.Equal(9, zero.Count(g => g == "x"));

        stagnant = 20;
        var all = GaSearch.Cataclysm(sorted, 10, survivors: 999, () => "x", ref stagnant);
        Assert.Equal(sorted, all);               // nothing to redraw, nothing lost
    }

    [Theory]
    [InlineData(0,  15, false)]
    [InlineData(14, 15, false)]
    [InlineData(15, 15, true)]
    [InlineData(40, 15, true)]
    [InlineData(99,  0, false)]   // threshold <= 0 disables restarts entirely
    [InlineData(99, -1, false)]
    public void ShouldCataclysm_FiresOnlyAtOrAboveAPositiveThreshold(int stagnant, int threshold, bool expected)
        => Assert.Equal(expected, GaSearch.ShouldCataclysm(stagnant, threshold));

    // ── Defect 3: seeding ────────────────────────────────────────────────────────

    [Fact]
    public void CreateRng_WithSeed_IsReproducible_AndReportsTheSeedBack()
    {
        var (a, seedA, suppliedA) = GaSearch.CreateRng(4242);
        var (b, seedB, suppliedB) = GaSearch.CreateRng(4242);

        Assert.Equal(4242, seedA);
        Assert.Equal(4242, seedB);
        Assert.True(suppliedA);
        Assert.True(suppliedB);

        for (int i = 0; i < 50; i++) Assert.Equal(a.Next(), b.Next());
    }

    // The key property for reproducing an unseeded run after the fact: even with no seed
    // supplied, a CONCRETE integer seed is drawn and handed back so it can be printed.
    // `new Random()` left implicit would give nothing to print.
    [Fact]
    public void CreateRng_WithoutSeed_StillYieldsAConcreteReplayableSeed()
    {
        var (rng, drawnSeed, supplied) = GaSearch.CreateRng(null);
        Assert.False(supplied);

        var replay = new Random(drawnSeed);
        for (int i = 0; i < 50; i++) Assert.Equal(rng.Next(), replay.Next());
    }

    [Fact]
    public void CreateRng_WithoutSeed_DrawsADifferentSeedEachTime()
    {
        var seeds = Enumerable.Range(0, 20).Select(_ => GaSearch.CreateRng(null).Seed).ToHashSet();
        // 20 draws over [1, int.MaxValue) colliding would be a broken source, not bad luck.
        Assert.True(seeds.Count >= 19, $"expected ~20 distinct auto seeds, got {seeds.Count}");
    }

    [Fact]
    public void ResolveSeed_ParsesTheFlag()
    {
        Assert.Equal(77, GaSearch.ResolveSeed(["train", "--seed", "77"]));
        Assert.Equal(-5, GaSearch.ResolveSeed(["train", "--variant", "x", "--seed", "-5"]));
        Assert.Equal(9,  GaSearch.ResolveSeed(["--seed", "9", "--variant", "x"]));
    }

    [Fact]
    public void ResolveSeed_ReturnsNullWhenAbsentOrUnparseable()
    {
        Assert.Null(GaSearch.ResolveSeed(null));
        Assert.Null(GaSearch.ResolveSeed([]));
        Assert.Null(GaSearch.ResolveSeed(["train"]));
        Assert.Null(GaSearch.ResolveSeed(["train", "--seed"]));          // flag with no value
        Assert.Null(GaSearch.ResolveSeed(["train", "--seed", "abc"]));   // unparseable value
    }
}
