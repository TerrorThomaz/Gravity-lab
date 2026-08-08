using System.Text.RegularExpressions;
using TradingGA;
using Xunit;

namespace GravityGen2.Tests.Core;

// Guards the post-GA refinement fix.
//
// Every train command used to run a 60-iteration TPE pass AFTER its GA, optimising the raw
// mean per-trade return over the unfolded training span, and then overwrote the GA's winner
// whenever that number happened to exceed the GA's fold-aggregated fitness. Two unrelated
// scales, so which genotype got written to genotypes/*.json was decided by accidental
// magnitude. Those six passes were deleted; refinement now happens only inside the GAs,
// where the canonical (private) Fitness is reachable and selection/acceptance use the same
// function on the same data.
public class RefinementObjectiveTests
{
    // ── 1. Structural guard ───────────────────────────────────────────────────
    // No command may drive an optimiser itself. A command can only reach the public
    // simulators, never a GA's private Fitness, so any optimiser loop written at the command
    // layer is optimising a proxy objective — and its accept/reject test against the
    // GA-returned Fitness is then a comparison between two different functions.
    // If a future outer refinement pass is genuinely wanted, the correct shape is for the GA
    // to expose its own evaluator (e.g. an internal EvaluateForRefinement(genotype) calling
    // Fitness(..., useValidation: false)); this test should then be updated to require that
    // the command feeds THAT evaluator to the optimiser, not a hand-rolled returns average.
    [Fact]
    public void NoCommandDrivesTheOptimiserWithItsOwnObjective()
    {
        var commandsDir = Path.Combine(RepoRoot(), "commands");
        Assert.True(Directory.Exists(commandsDir), $"commands/ not found under {RepoRoot()}");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(commandsDir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                // Comments are allowed to mention the removed pattern (they document it).
                if (lines[i].TrimStart().StartsWith("//")) continue;
                if (lines[i].Contains("BayesianOptimizer."))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Command-layer optimiser call(s) found — a command cannot evaluate a GA's canonical "
            + "fitness, so this reintroduces the incommensurable-comparison bug:\n  "
            + string.Join("\n  ", offenders));
    }

    // ── 2. Cold start ─────────────────────────────────────────────────────────
    // Documents defect 3 of the removed pass: BayesianOptimizer.Suggest returns a uniform
    // RandomPoint while history.Count < 12, so seeding Refine with a single observation
    // spends its first 11 iterations sampling the whole bounds box at random. Seeding from a
    // GA's full elite island (what the inner passes do) skips that entirely and stays in the
    // elite region from iteration 0.
    [Fact]
    public void SingleObservationSeedMakesTheFirstIterationsUniformRandom()
    {
        const int dims = 3, iterations = 11;
        var bounds = new double[dims, 2];
        for (int d = 0; d < dims; d++) { bounds[d, 0] = 0.0; bounds[d, 1] = 100.0; }
        const double center = 50.0;

        int FarPoints(List<(double[] Params, double Fitness)> seed, int rngSeed)
        {
            var seen = new List<double[]>();
            BayesianOptimizer.Refine(seed, bounds,
                v => { seen.Add((double[])v.Clone()); return 0.0; },
                iterations, new Random(rngSeed));
            // Only the points this call proposed, not the seed.
            return seen.Count(p => p.Any(x => Math.Abs(x - center) > 25.0));
        }

        // One observation — the shape the deleted outer pass used.
        var coldSeed = new List<(double[], double)> { (new[] { center, center, center }, 1.0) };
        int coldFar = FarPoints(coldSeed, 1234);

        // Twelve observations clustered around the same point — the shape an elite island has.
        var eliteSeed = new List<(double[], double)>();
        var jitter = new Random(99);
        for (int i = 0; i < 12; i++)
        {
            var p = new double[dims];
            for (int d = 0; d < dims; d++) p[d] = center + (jitter.NextDouble() - 0.5);
            eliteSeed.Add((p, 1.0 + i * 0.01));
        }
        int eliteFar = FarPoints(eliteSeed, 1234);

        Assert.True(coldFar >= 6,
            $"expected a 1-observation seed to scatter uniformly, got {coldFar}/{iterations} far points");
        Assert.True(eliteFar == 0,
            $"expected an elite-island seed to stay local, got {eliteFar}/{iterations} far points");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Gravity-gen2.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
