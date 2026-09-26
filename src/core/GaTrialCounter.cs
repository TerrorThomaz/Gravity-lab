using System.Collections.Concurrent;
using System.Text.Json;

namespace TradingGA;

// How many candidate genotypes were actually evaluated to arrive at the committed genotypes.
//
// WHY THIS EXISTS. StatisticalTests.DeflatedSharpeRatio subtracts E[max SR] — the Sharpe a search
// of T trials is expected to produce from strategies with NO true edge. T is therefore not a
// detail; it decides the verdict. On the current three-strategy book:
//
//     T =   1,000  ->  DSR 0.971   significant
//     T =  50,000  ->  DSR 0.814   borderline
//     T = 100,000  ->  DSR 0.770   not significant
//
// Nothing recorded T, so every deflated Sharpe in this repo rested on a hand-waved constant. This
// counts the real thing: one Record per candidate evaluation, at each GA's fitness entry point.
//
// TRIALS ACCUMULATE ACROSS RUNS, and Save merges rather than replaces. A genotype that survived
// five retrains was selected from all five passes over the search space, not the last one. Letting
// a retrain reset the ledger would launder away the multiple-testing burden it just added — the
// one direction of error that makes a strategy look better than it is.
public sealed class GaTrialCounter
{
    public const string DefaultPath = "genotypes/ga_trials.json";

    // Used when no ledger exists yet. Deliberately large: with no evidence about the search size,
    // the conservative assumption is a big search, which DEFLATES the Sharpe rather than inflating
    // it. A missing file must never read as "nothing was searched".
    public const int ConservativeDefault = 50_000;

    // Process-wide instance. A static avoids threading a counter through nine GA constructors
    // for something that is pure instrumentation and never affects a fitness value.
    public static readonly GaTrialCounter Shared = new();

    private readonly ConcurrentDictionary<string, int> _counts = new();

    // Called once per candidate genotype evaluation. GAs evaluate populations under
    // Parallel.ForEach, so this has to be safe from many threads at once.
    public void Record(string strategyKey) =>
        _counts.AddOrUpdate(strategyKey, 1, (_, n) => n + 1);

    public int CountFor(string strategyKey) => _counts.TryGetValue(strategyKey, out int n) ? n : 0;
    public int Total => _counts.Values.Sum();

    public IReadOnlyDictionary<string, int> Snapshot() => new Dictionary<string, int>(_counts);

    // Merge this run's counts onto whatever the ledger already holds.
    public void Save(string path = DefaultPath)
    {
        var merged = new Dictionary<string, int>(Load(path));
        foreach (var (k, v) in _counts)
            merged[k] = merged.TryGetValue(k, out int prev) ? prev + v : v;

        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static IReadOnlyDictionary<string, int> Load(string path = DefaultPath)
    {
        if (!File.Exists(path)) return new Dictionary<string, int>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))
                   ?? new Dictionary<string, int>();
        }
        catch (JsonException) { return new Dictionary<string, int>(); }
    }

    // The T to hand DeflatedSharpeRatio. Falls back to ConservativeDefault on an empty ledger.
    public static int TotalFrom(IReadOnlyDictionary<string, int> ledger)
        => ledger.Count == 0 ? ConservativeDefault : ledger.Values.Sum();

    public static int LoadTotal(string path = DefaultPath) => TotalFrom(Load(path));
}
