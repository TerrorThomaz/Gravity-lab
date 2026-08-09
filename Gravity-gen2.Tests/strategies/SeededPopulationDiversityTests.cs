using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Pins the seeded-initialisation contract of every strategy genotype factory.
//
// The defect these tests exist to prevent: `Random(rng, seed)` used to return a
// byte-identical copy of the seed for EVERY draw (the rng draw was evaluated and
// discarded). Since seeding is the normal path — every train command loads the
// saved genotype and passes it — a pop-80 seeded run started as
//   index 0      : the seed
//   indices 1-16 : tight rate-0.25 mutants installed by the GA's own Run block
//   indices 17-79: 63 byte-identical clones of the seed
// i.e. at most 17 distinct starting points, 78.75% duplicates. That is a
// 150-generation hill-climb from the previous genotype, not a GA, and it is the
// mechanism behind the repo's "a rerun can land in a worse basin" warning.
//
// The fix (mirroring RegimeRouterGenotype.Random) returns a LOOSE mutant of the
// seed 30% of the time and a fully independent random genotype otherwise.
//
// Threshold: at population 80, at least 40 (half) of the draws must be distinct.
// The expected value under the fix is ~80 — a rate-0.5 mutation over 10+ genes
// repeats its parent with probability 0.5^10 or less — so 40 is a deliberately
// slack floor that still fails hard on any regression toward cloning.
public class SeededPopulationDiversityTests
{
    private const int PopulationSize   = 80;
    private const int DistinctFloor    = 40;   // half of PopulationSize
    private const int RandomFloor      = 78;   // unseeded draws are continuous: collisions ≈ 0

    // Several independent RNG streams, so a pass cannot be a single lucky sequence.
    private static readonly int[] RngSeeds = [1, 7, 42, 12345];

    private static string Key(double[] v) => string.Join(",", v.Select(x => x.ToString("R")));

    private static int DistinctCount<T>(Func<Random, T?, T> factory, T? seed, Func<T, double[]> toVector, int rngSeed)
        where T : class
    {
        var rng = new Random(rngSeed);
        return Enumerable.Range(0, PopulationSize)
                         .Select(_ => Key(toVector(factory(rng, seed))))
                         .Distinct()
                         .Count();
    }

    // Core assertion set, applied to one factory + its own bounds table.
    private static void AssertHealthySeededInit<T>(
        string name,
        Func<Random, T?, T> factory,
        Func<Random, T> makeSeed,
        Func<T, double[]> toVector,
        double[,] bounds)
        where T : class
    {
        var seed = makeSeed(new Random(999));
        double[] seedVector = toVector(seed);

        foreach (int rngSeed in RngSeeds)
        {
            // 1. Seeded draws must be substantially diverse, not clones of the seed.
            int seededDistinct = DistinctCount(factory, seed, toVector, rngSeed);
            Assert.True(seededDistinct >= DistinctFloor,
                $"{name}: seeded draw produced only {seededDistinct} distinct genotypes out of " +
                $"{PopulationSize} (floor {DistinctFloor}) — the seed is short-circuiting the random draws.");

            // 2. The un-seeded path must still be fully random.
            int randomDistinct = DistinctCount<T>(factory, null, toVector, rngSeed);
            Assert.True(randomDistinct >= RandomFloor,
                $"{name}: un-seeded draw produced only {randomDistinct} distinct genotypes out of {PopulationSize}.");

            // 3. Exact copies of the seed must be a small minority, never the population.
            var rng = new Random(rngSeed);
            var draws = Enumerable.Range(0, PopulationSize).Select(_ => toVector(factory(rng, seed))).ToList();
            int exactClones = draws.Count(v => Key(v) == Key(seedVector));
            Assert.True(exactClones <= PopulationSize / 10,
                $"{name}: {exactClones}/{PopulationSize} seeded draws were byte-identical to the seed.");

            // 4. Every seeded draw stays inside the factory's OWN bounds table.
            foreach (double[] v in draws)
            {
                Assert.Equal(bounds.GetLength(0), v.Length);
                for (int i = 0; i < v.Length; i++)
                    Assert.InRange(v[i], bounds[i, 0], bounds[i, 1]);
            }
        }
    }

    // ── FadeShort ────────────────────────────────────────────────────────────────
    [Fact]
    public void FadeShort_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<FadeShortGenotype>(
            "FadeShort.Random",
            (rng, seed) => FadeShortGenotype.Random(rng, seed),
            rng => FadeShortGenotype.Random(rng),
            g => g.ToVector(),
            FadeShortGenotype.Bounds);

    [Fact]
    public void FadeShortLowVol_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<FadeShortGenotype>(
            "FadeShort.RandomLowVol",
            (rng, seed) => FadeShortGenotype.RandomLowVol(rng, seed),
            rng => FadeShortGenotype.RandomLowVol(rng),
            g => g.ToVector(),
            FadeShortGenotype.BoundsLowVol);

    [Fact]
    public void FadeShortHighVol_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<FadeShortGenotype>(
            "FadeShort.RandomHighVol",
            (rng, seed) => FadeShortGenotype.RandomHighVol(rng, seed),
            rng => FadeShortGenotype.RandomHighVol(rng),
            g => g.ToVector(),
            FadeShortGenotype.BoundsHighVol);

    // ── SwingLong ────────────────────────────────────────────────────────────────
    [Fact]
    public void SwingLong_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<SwingLongGenotype>(
            "SwingLong.Random",
            (rng, seed) => SwingLongGenotype.Random(rng, seed),
            rng => SwingLongGenotype.Random(rng),
            g => g.ToVector(),
            SwingLongGenotype.Bounds);

    [Fact]
    public void SwingLongLowVol_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<SwingLongGenotype>(
            "SwingLong.RandomLowVol",
            (rng, seed) => SwingLongGenotype.RandomLowVol(rng, seed),
            rng => SwingLongGenotype.RandomLowVol(rng),
            g => g.ToVector(),
            SwingLongGenotype.BoundsLowVol);

    [Fact]
    public void SwingLongHighVol_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<SwingLongGenotype>(
            "SwingLong.RandomHighVol",
            (rng, seed) => SwingLongGenotype.RandomHighVol(rng, seed),
            rng => SwingLongGenotype.RandomHighVol(rng),
            g => g.ToVector(),
            SwingLongGenotype.BoundsHighVol);

    // ── DipLong ──────────────────────────────────────────────────────────────────
    [Fact]
    public void DipLong_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<DipLongGenotype>(
            "DipLong.Random",
            (rng, seed) => DipLongGenotype.Random(rng, seed),
            rng => DipLongGenotype.Random(rng),
            g => g.ToVector(),
            DipLongGenotype.Bounds);

    [Fact]
    public void DipLongLowVol_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<DipLongGenotype>(
            "DipLong.RandomLowVol",
            (rng, seed) => DipLongGenotype.RandomLowVol(rng, seed),
            rng => DipLongGenotype.RandomLowVol(rng),
            g => g.ToVector(),
            DipLongGenotype.BoundsLowVol);

    [Fact]
    public void DipLongHighVol_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<DipLongGenotype>(
            "DipLong.RandomHighVol",
            (rng, seed) => DipLongGenotype.RandomHighVol(rng, seed),
            rng => DipLongGenotype.RandomHighVol(rng),
            g => g.ToVector(),
            DipLongGenotype.BoundsHighVol);

    // ── RipShort ─────────────────────────────────────────────────────────────────
    [Fact]
    public void RipShort_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<RipShortGenotype>(
            "RipShort.Random",
            (rng, seed) => RipShortGenotype.Random(rng, seed),
            rng => RipShortGenotype.Random(rng),
            g => g.ToVector(),
            RipShortGenotype.Bounds);

    [Fact]
    public void RipShortLowVol_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<RipShortGenotype>(
            "RipShort.RandomLowVol",
            (rng, seed) => RipShortGenotype.RandomLowVol(rng, seed),
            rng => RipShortGenotype.RandomLowVol(rng),
            g => g.ToVector(),
            RipShortGenotype.BoundsLowVol);

    [Fact]
    public void RipShortHighVol_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<RipShortGenotype>(
            "RipShort.RandomHighVol",
            (rng, seed) => RipShortGenotype.RandomHighVol(rng, seed),
            rng => RipShortGenotype.RandomHighVol(rng),
            g => g.ToVector(),
            RipShortGenotype.BoundsHighVol);

    // ── FadeLong ─────────────────────────────────────────────────────────────────
    [Fact]
    public void FadeLong_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<FadeLongGenotype>(
            "FadeLong.Random",
            (rng, seed) => FadeLongGenotype.Random(rng, seed),
            rng => FadeLongGenotype.Random(rng),
            g => g.ToVector(),
            FadeLongGenotype.Bounds);

    // ── Grid (shared by Grid and GridShort) ──────────────────────────────────────
    [Fact]
    public void Grid_SeededInit_IsDiverseAndInBounds() =>
        AssertHealthySeededInit<GridGenotype>(
            "Grid.Random",
            (rng, seed) => GridGenotype.Random(rng, seed),
            rng => GridGenotype.Random(rng),
            g => g.ToVector(),
            GridGenotype.Bounds);

    // Grid's ADX gene has a dynamic ceiling (swing.AdxThreshold − 1) that keeps the
    // grid and swing regimes from overlapping. A seeded draw must respect it too —
    // the seed-mutant branch threads adxCeiling through both clamp and mutation.
    [Fact]
    public void Grid_SeededInit_RespectsDynamicAdxCeiling()
    {
        const double adxCeiling = 13.0;
        var seed = GridGenotype.Random(new Random(999));   // AdxThreshold drawn up to 20
        var rng  = new Random(4242);

        var draws = Enumerable.Range(0, PopulationSize)
                              .Select(_ => GridGenotype.Random(rng, seed, adxCeiling))
                              .ToList();

        Assert.All(draws, g => Assert.InRange(g.AdxThreshold, 8.0, adxCeiling));

        int distinct = draws.Select(g => Key(g.ToVector())).Distinct().Count();
        Assert.True(distinct >= DistinctFloor,
            $"Grid.Random(adxCeiling): only {distinct} distinct genotypes out of {PopulationSize}.");
    }

    // A seed whose genes sit outside the target region must be pulled back in
    // before it is mutated — otherwise a seeded variant run would propose
    // genotypes outside the region the variant is defined on.
    [Fact]
    public void OutOfRegionSeed_IsClampedBeforeMutation()
    {
        // FadeShort's normal AdxThreshold range is 22–45; low-vol is 10–30.
        var wideSeed = new FadeShortGenotype { AdxThreshold = 45.0, MaxHoldCandles = 24, PositionSizePct = 0.05 };
        var rng = new Random(2024);

        for (int i = 0; i < 400; i++)
        {
            var g = FadeShortGenotype.RandomLowVol(rng, wideSeed);
            Assert.InRange(g.AdxThreshold, FadeShortGenotype.BoundsLowVol[1, 0], FadeShortGenotype.BoundsLowVol[1, 1]);
            Assert.InRange(g.MaxHoldCandles, (int)FadeShortGenotype.BoundsLowVol[11, 0], (int)FadeShortGenotype.BoundsLowVol[11, 1]);
        }
    }
}
