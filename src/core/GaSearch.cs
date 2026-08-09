namespace TradingGA;

// Shared search-control primitives for every GA in this repo.
//
// The three things collected here were previously copy-pasted (and silently mis-tuned) into
// each GA:
//
//  1. TOURNAMENT SELECTION. Every GA had its own `TournamentSelect(pop, int k = 4)`. At the
//     production population size (N = 80) Goldberg & Deb's takeover time for tournament-k is
//     (ln N + ln ln N) / ln k, i.e. ~4.2 generations at k = 4 against a 150-generation budget:
//     68.4% of parents came from the top quartile and only 6.25% from the bottom half, so the
//     population was effectively a single point by generation ~5 and the remaining ~140
//     generations were a fixed-step creep walk around it. k = 2 takes ~8.5 generations, which
//     roughly doubles the exploration phase for no extra evaluation cost — see DefaultTournamentK.
//
//  2. THE CATACLYSM. The old escape mechanism doubled the per-gene mutation PROBABILITY on
//     stagnation. That cannot work: the per-gene step size is a fixed creep (~±7% of range)
//     that never anneals, so a doubled rate on a fixed step cannot cross a basin wider than the
//     step — it only makes more genes take the same short hop. Worse, elitism makes the
//     best-seen fitness monotone and the stagnation counter only reset on a strict global-best
//     improvement, so once the boost fired it stayed on for the rest of the run: a latch, not a
//     pulse. Cataclysm() replaces it with the CHC restart: keep the top few individuals, redraw
//     everything else uniformly from the genotype's own random factory, and reset the counter so
//     the next restart is a fresh pulse.
//
//  3. RNG SEEDING. Every GA had `private readonly Random _rng = new()`. Two runs of the same
//     train command therefore produced different genotypes with no way to reproduce a good one —
//     the mechanical cause of the "a rerun can land in a worse basin and silently regress an
//     already-validated genotype" warning in CLAUDE.md. CreateRng() always resolves a CONCRETE
//     integer seed (drawing one explicitly when the caller supplies none) so that AnnounceSeed()
//     can print it and the run can be reproduced after the fact with `--seed N`.
//
// THREAD SAFETY: System.Random is not thread-safe. Every GA evaluates its population under
// Parallel.ForEach; those bodies must never touch the GA's _rng. See the warning comment at
// each Parallel.ForEach site.
internal static class GaSearch
{
    // ── Selection ────────────────────────────────────────────────────────────────

    // Default tournament size. k = 2 is the weakest non-degenerate tournament pressure and is
    // the standard choice when the generation budget is large relative to the population: at
    // N = 80 it gives a takeover time of ~8.5 generations instead of tournament-4's ~4.2, and
    // it draws roughly 25% of parents from the bottom half of the population instead of 6.25%.
    internal const int DefaultTournamentK = 2;

    // Default number of individuals copied UNCHANGED into the next generation. This — not the
    // `eliteCount` constructor parameter, which only sizes the reporting slice / BO seed set /
    // final-winner pool — is the real elitism knob. 5 of 80 = 6.25%, the value that was
    // hardcoded as a literal `Take(5)` in every GA before it was made settable.
    internal const int DefaultEliteCarryOver = 5;

    // Generations without a strict global-best improvement before the cataclysm fires.
    internal const int DefaultCataclysmStagnantGens = 15;

    // k-way tournament: draw k individuals uniformly WITH replacement, return the fittest.
    // k = 1 degenerates to a uniform random draw (no selection pressure at all), which is a
    // legitimate setting for a diagnostic run, so it is allowed rather than clamped away.
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

    // ── Cataclysm ────────────────────────────────────────────────────────────────

    // True when the stagnation counter has reached the restart threshold.
    // A threshold <= 0 disables restarts entirely.
    internal static bool ShouldCataclysm(int stagnantGens, int threshold)
        => threshold > 0 && stagnantGens >= threshold;

    // CHC-style cataclysmic restart.
    //
    // `sortedPopulation` MUST already be sorted best-first — the survivors are simply its head,
    // which is how the elite carry-over survives the restart: the same individuals the normal
    // reproduction path would have copied forward are the ones kept here, so the best-so-far
    // genotype is never destroyed by a restart. (The caller's eliteIsland is captured from the
    // same sorted list before this is called, so the BO seed set and the final winner are safe
    // even if the restart lands somewhere worse.)
    //
    // Everything past the survivors is redrawn from `randomFactory`, which callers must wire to
    // the genotype's UNSEEDED random overload — a restart that resamples a neighbourhood of the
    // incumbent seed would defeat the whole point of restarting.
    //
    // stagnantGens is reset to 0 so this is a PULSE. The old mutation-rate boost latched on
    // forever because it read a counter that elitism could never bring back down.
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

    // ── Seeding ──────────────────────────────────────────────────────────────────

    // Resolve a GA's RNG. When the caller supplies no seed we draw ONE explicitly instead of
    // leaving `new Random()` implicit, so the value can be printed and replayed.
    internal static (Random Rng, int Seed, bool Supplied) CreateRng(int? seed)
    {
        if (seed is int s) return (new Random(s), s, true);
        int drawn = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
        return (new Random(drawn), drawn, false);
    }

    // One line, printed at the start of every GA run — this is the reproducibility record.
    // Printed regardless of the GA's `verbose` flag: a run whose seed was never emitted is a
    // run that cannot be reproduced, which is the defect this exists to close.
    internal static void AnnounceSeed(string gaName, int seed, bool supplied)
        => Console.WriteLine(supplied
            ? $"  [seed] {gaName} rng seed = {seed} (supplied)"
            : $"  [seed] {gaName} rng seed = {seed} (auto-generated — rerun with --seed {seed} to reproduce)");

    // Parse `--seed N` out of a command's argv. Returns null when absent or unparseable,
    // which means "draw one and print it". Mirrors TrainCommands.ResolveVariant's `--variant`.
    internal static int? ResolveSeed(string[]? args)
    {
        if (args == null) return null;
        int idx = Array.IndexOf(args, "--seed");
        if (idx >= 0 && idx + 1 < args.Length && int.TryParse(args[idx + 1], out int s)) return s;
        return null;
    }

    // Header line for a train command, printed before any GA is constructed. When the user
    // passed --seed the GAs below inherit it; otherwise each GA draws and announces its own.
    internal static void AnnounceCommandSeed(string command, int? seed)
        => Console.WriteLine(seed is int s
            ? $"  [seed] {command}: --seed {s} (all GAs in this run are seeded from it)"
            : $"  [seed] {command}: no --seed given; each GA draws and prints its own seed below");
}
