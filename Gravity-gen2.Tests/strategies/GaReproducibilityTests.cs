using GravityGen2.Strategies.AccumulationGrid;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// End-to-end pins for the GA search-quality work:
//   · defect 1 — tournament pressure, asserted through a REAL GA's selector, not a copy of it
//   · defect 2 — the cataclysm firing from inside a real generation loop
//   · defect 3 — the constructor `seed` being the only source of randomness in a run
//
// Two GAs are exercised (GridGeneticAlgorithm and AccumulationGridGA) because both are
// single-timeframe (h1 only), so a few hundred synthetic candles drive the whole Run/Train path
// — including the Bayesian refinement stage — in well under a second.
//
// A NOTE ON WHAT SYNTHETIC DATA CAN AND CANNOT SHOW. A random walk does not produce enough
// ranging-grid sessions to clear MinTradesPerFold, so the fitness landscape these runs see is
// largely flat. That is fine for what is being pinned here — reproducibility and RNG plumbing
// are properties of the search, not of the landscape — but it is why the selection-pressure and
// RNG-stream assertions below drive the GA's selector directly against a synthetic population
// with hand-set fitnesses instead of inferring pressure from a training outcome.
public class GaReproducibilityTests
{
    private static Candle[] Synthetic(int count, int seed)
    {
        var rng = new Random(seed);
        var t   = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var arr = new Candle[count];
        double price = 100.0;
        for (int i = 0; i < count; i++)
        {
            price *= 1.0 + (rng.NextDouble() - 0.5) * 0.03 + Math.Sin(i / 60.0) * 0.001;
            double high = price * 1.006, low = price * 0.994;
            arr[i] = new Candle(t.AddHours(i), price, high, low, price, 1000);
        }
        return arr;
    }

    private static List<GridGeneticAlgorithm.CoinData> GridCoins()
    {
        var coins = new List<GridGeneticAlgorithm.CoinData>();
        foreach (int s in new[] { 11, 22 })
        {
            var arr   = Synthetic(900, s);
            int split = (int)(arr.Length * 0.8);
            coins.Add(new GridGeneticAlgorithm.CoinData(arr[..split], arr[split..]));
        }
        return coins;
    }

    private static List<AccumulationGridGA.CoinData> AccumCoins()
    {
        var coins = new List<AccumulationGridGA.CoinData>();
        foreach (int s in new[] { 33, 44 })
        {
            var arr   = Synthetic(900, s);
            int split = (int)(arr.Length * 0.8);
            coins.Add(new AccumulationGridGA.CoinData(arr[..split], arr[split..]));
        }
        return coins;
    }

    private static GridGenotype RunGrid(int? seed, int generations = 4, int cataclysmStagnantGens = 15) =>
        new GridGeneticAlgorithm(
            populationSize: 12, generations: generations, eliteCount: 4,
            migrationInterval: 100, verbose: false,
            cataclysmStagnantGens: cataclysmStagnantGens, seed: seed)
        .Run(GridCoins());

    private static AccumulationGridGenotype RunAccum(int? seed, int generations = 4) =>
        new AccumulationGridGA(
            MarketRegime.Bull, populationSize: 12, generations: generations, eliteCount: 3,
            verbose: false, seed: seed)
        .Train(AccumCoins()).Best;

    // Synthetic ranked population: index 0 is fittest, fitness strictly decreasing so there are
    // no ties and "bottom half" is exactly Fitness <= n/2.
    private static List<GridGenotype> RankedGridPopulation(int n = 60)
    {
        var shapeRng = new Random(1);          // gene values are irrelevant — selection reads Fitness
        var pop      = new List<GridGenotype>(n);
        for (int i = 0; i < n; i++)
        {
            var g = GridGenotype.Random(shapeRng);
            g.Fitness = n - i;
            pop.Add(g);
        }
        return pop;
    }

    // ── Defect 3: same seed → same result, different seed → different result ─────

    [Fact]
    public void GridGA_SameSeed_ProducesIdenticalGenotype()
    {
        var a = RunGrid(20260809);
        var b = RunGrid(20260809);
        Assert.Equal(a.ToString(), b.ToString());
        Assert.Equal(a.Fitness, b.Fitness);
    }

    [Fact]
    public void AccumulationGridGA_SameSeed_ProducesIdenticalGenotype()
    {
        var a = RunAccum(20260809);
        var b = RunAccum(20260809);
        Assert.Equal(a, b);   // record equality — every gene must match
    }

    [Fact]
    public void GridGA_DifferentSeeds_ExploreDifferentGenotypes()
    {
        var results = new[] { 1, 2, 3, 4 }.Select(s => RunGrid(s).ToString()).ToHashSet();
        Assert.True(results.Count >= 3,
            $"4 distinct seeds collapsed to {results.Count} distinct genotypes — the seed is not driving the search");
    }

    [Fact]
    public void AccumulationGridGA_DifferentSeeds_ExploreDifferentGenotypes()
    {
        var results = new[] { 1, 2, 3, 4 }.Select(s => RunAccum(s)).ToHashSet();
        Assert.True(results.Count >= 3,
            $"4 distinct seeds collapsed to {results.Count} distinct genotypes — the seed is not driving the search");
    }

    // An unseeded GA must still be random run-to-run — the fix must not have accidentally pinned
    // every run to one constant seed.
    [Fact]
    public void GridGA_NoSeed_StillVariesBetweenRuns()
    {
        var results = Enumerable.Range(0, 4).Select(_ => RunGrid(null).ToString()).ToHashSet();
        Assert.True(results.Count >= 2, "unseeded runs must not be constant");
    }

    // The landscape-independent version of the same claim: the GA's internal Random is fully
    // determined by the constructor seed, so the stream of selection decisions it makes is
    // reproducible. This holds even where the fitness landscape is flat, and it is the property
    // that actually makes a training run replayable.
    [Fact]
    public void GaRngStream_IsDeterminedEntirelyByTheConstructorSeed()
    {
        var pop = RankedGridPopulation();

        static double[] Draws(GridGeneticAlgorithm ga, List<GridGenotype> p) =>
            Enumerable.Range(0, 300).Select(_ => ga.TournamentSelect(p).Fitness).ToArray();

        var a = Draws(new GridGeneticAlgorithm(verbose: false, seed: 4242), pop);
        var b = Draws(new GridGeneticAlgorithm(verbose: false, seed: 4242), pop);
        var c = Draws(new GridGeneticAlgorithm(verbose: false, seed: 9999), pop);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void FadeShortGA_RngStream_IsDeterminedEntirelyByTheConstructorSeed()
    {
        var shapeRng = new Random(2);
        var pop      = new List<FadeShortGenotype>();
        for (int i = 0; i < 60; i++)
        {
            var g = FadeShortGenotype.Random(shapeRng);
            g.Fitness = 60 - i;
            pop.Add(g);
        }

        static double[] Draws(FadeShortGA ga, List<FadeShortGenotype> p) =>
            Enumerable.Range(0, 300).Select(_ => ga.TournamentSelect(p).Fitness).ToArray();

        var a = Draws(new FadeShortGA(verbose: false, seed: 4242), pop);
        var b = Draws(new FadeShortGA(verbose: false, seed: 4242), pop);
        var c = Draws(new FadeShortGA(verbose: false, seed: 9999), pop);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    // ── Defect 1: the GA's own selector really is tournament-2 by default ────────

    [Fact]
    public void GridGA_DefaultTournamentK_IsTwo_AndReachesTheBottomHalfFarMoreOftenThanK4()
    {
        var pop  = RankedGridPopulation();
        double bottomThreshold = pop.Count / 2.0;   // fitness = n - i, so bottom half is <= n/2
        const int draws = 20_000;

        static int CountBottom(GridGeneticAlgorithm ga, List<GridGenotype> p, double threshold, int draws)
        {
            int n = 0;
            for (int i = 0; i < draws; i++) if (ga.TournamentSelect(p).Fitness <= threshold) n++;
            return n;
        }

        double rateDefault = (double)CountBottom(new GridGeneticAlgorithm(verbose: false, seed: 5), pop, bottomThreshold, draws) / draws;
        double rate2       = (double)CountBottom(new GridGeneticAlgorithm(verbose: false, tournamentK: 2, seed: 5), pop, bottomThreshold, draws) / draws;
        double rate4       = (double)CountBottom(new GridGeneticAlgorithm(verbose: false, tournamentK: 4, seed: 5), pop, bottomThreshold, draws) / draws;

        // The default must BE tournament-2 — same seed, same population, identical behaviour.
        Assert.Equal(rate2, rateDefault, 10);

        // Theory: (1/2)^k, so 25% at k=2 and 6.25% at k=4.
        Assert.InRange(rate2, 0.235, 0.265);
        Assert.InRange(rate4, 0.055, 0.070);
        Assert.True(rate2 > rate4 * 3.0,
            $"k=2 bottom-half rate {rate2:P2} should be >3x the k=4 rate {rate4:P2}");
    }

    // ── Defect 2: the cataclysm fires from inside a real generation loop ─────────
    //
    // GaSearchTests pins the restart's semantics (survivors kept verbatim, everything else
    // redrawn, counter reset) against the shared implementation the GAs call. What is pinned
    // HERE is that a GA actually reaches that branch, and that the threshold gates it.
    //
    // Asserted on the GA's own verbose output because the effect is not visible in the returned
    // genotype: elitism guarantees the champion survives every restart, so on a flat synthetic
    // landscape the winner is (correctly) unchanged by restarting.
    [Fact]
    public void GridGA_Cataclysm_FiresWhenStagnant_AndNotWhenTheThresholdIsUnreachable()
    {
        Assert.Contains("CATACLYSM", CaptureGridRun(cataclysmStagnantGens: 1));
        Assert.DoesNotContain("CATACLYSM", CaptureGridRun(cataclysmStagnantGens: 10_000));

        // Threshold <= 0 disables restarts, same as a threshold nothing can reach.
        Assert.DoesNotContain("CATACLYSM", CaptureGridRun(cataclysmStagnantGens: 0));
    }

    private static string CaptureGridRun(int cataclysmStagnantGens)
    {
        var buffer = new StringWriter();
        var prev   = Console.Out;
        // Synchronized: Console.Out is process-global, so another test class running in parallel
        // could otherwise write into this StringWriter concurrently. Only the token below is
        // asserted on, so foreign output is harmless — but unsynchronised writes would not be.
        Console.SetOut(TextWriter.Synchronized(buffer));
        try
        {
            new GridGeneticAlgorithm(
                populationSize: 12, generations: 8, eliteCount: 4,
                migrationInterval: 100, verbose: true,
                cataclysmStagnantGens: cataclysmStagnantGens, seed: 555)
            .Run(GridCoins());
        }
        finally { Console.SetOut(prev); }
        return buffer.ToString();
    }

    // Same seed, restarts disabled two different ways — the runs must be bit-identical, i.e.
    // `threshold <= 0` really is a no-op rather than a differently-behaving branch.
    [Fact]
    public void GridGA_ZeroThreshold_IsEquivalentToAnUnreachableThreshold()
    {
        var disabled    = RunGrid(777, generations: 8, cataclysmStagnantGens: 0);
        var unreachable = RunGrid(777, generations: 8, cataclysmStagnantGens: 10_000);
        Assert.Equal(unreachable.ToString(), disabled.ToString());
    }
}
