namespace TradingGA;

// Shared GA primitives: tournament selection (k=2), CHC cataclysmic restart, reproducible RNG seeding.
// Thread safety: System.Random is not thread-safe — Parallel.ForEach bodies must not touch _rng.
internal static class GaSearch
{
    // k=2: weakest non-degenerate pressure, ~8.5 gen takeover time at N=80.
    internal const int DefaultTournamentK = 2;

    // Real elitism knob (not eliteCount, which sizes reporting/BO pool). 5 of 80 = 6.25%.
    internal const int DefaultEliteCarryOver = 5;


    internal const int DefaultCataclysmStagnantGens = 15;

    // k-way tournament (with replacement). k=1 = uniform random (allowed for diagnostics).
    internal static T Tournament<T>(IReadOnlyList<T> pop, int k, Random rng, Func<T, double> fitness)
    {
        if (pop.Count == 0) throw new ArgumentException("Empty population.", nameof(pop));
        var    best     = pop[rng.Next(pop.Count)];
        double bestFit  = fitness(best);
        for (int i = 1; i < Math.Max(1, k); i++)
        {
            var    c   = pop[rng.Next(pop.Count)];
            double cf  = fitness(c);
            if (cf > bestFit) { best = c; bestFit = cf; }
        }
        return best;
    }

    // Threshold <= 0 disables restarts.
    internal static bool ShouldCataclysm(int stagnantGens, int threshold)
        => threshold > 0 && stagnantGens >= threshold;

    // CHC restart: keep top `survivors`, redraw rest from randomFactory (unseeded). Resets stagnation.
    internal static List<T> Cataclysm<T>(
        IReadOnlyList<T> sortedPopulation,
        int              populationSize,
        int              survivors,
        Func<T>          randomFactory,
        ref int          stagnantGens)
    {
        if (populationSize <= 0) throw new ArgumentOutOfRangeException(nameof(populationSize));
        int keep = Math.Clamp(survivors, 1, Math.Min(sortedPopulation.Count, populationSize));

        var next = new List<T>(populationSize);
        for (int i = 0; i < keep; i++) next.Add(sortedPopulation[i]);
        while (next.Count < populationSize) next.Add(randomFactory());

        stagnantGens = 0;
        return next;
    }

    // Resolve RNG. Always draws a concrete seed so it can be printed and replayed.
    internal static (Random Rng, int Seed, bool Supplied) CreateRng(int? seed)
    {
        if (seed is int s) return (new Random(s), s, true);
        int drawn = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        return (new Random(drawn), drawn, false);
    }

    // Printed regardless of verbose — a run without an emitted seed cannot be reproduced.
    internal static void AnnounceSeed(string gaName, int seed, bool supplied)
        => Console.WriteLine(supplied
            ? $"  [seed] {gaName} rng seed = {seed} (supplied)"
            : $"  [seed] {gaName} rng seed = {seed} (auto-generated — rerun with --seed {seed} to reproduce)");

    // Parse `--seed N`. Null = draw one and print it.
    internal static int? ResolveSeed(string[]? args)
    {
        if (args == null) return null;
        int idx = Array.IndexOf(args, "--seed");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out int s)) return s;
        return null;
    }


    internal static void AnnounceCommandSeed(string command, int? seed)
        => Console.WriteLine(seed is int s
            ? $"  [seed] {command}: --seed {s} (all GAs in this run are seeded from it)"
            : $"  [seed] {command}: no --seed given; each GA draws and prints its own seed below");
}
