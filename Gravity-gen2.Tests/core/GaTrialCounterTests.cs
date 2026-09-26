using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The deflated Sharpe is decided by how many candidates were evaluated to pick the committed
// genotype: at 1,000 trials the current book scores DSR 0.971 (significant), at 100,000 it scores
// 0.770 (not). Nothing in this repo recorded that number, so every DSR ever quoted rested on a
// guess. These pin the counter that replaces the guess.
public class GaTrialCounterTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"ga_trials_{Guid.NewGuid():N}.json");

    [Fact]
    public void Record_CountsPerStrategyAndInTotal()
    {
        var c = new GaTrialCounter();
        for (int i = 0; i < 7; i++) c.Record("fade_short");
        for (int i = 0; i < 3; i++) c.Record("grid");

        Assert.Equal(7, c.CountFor("fade_short"));
        Assert.Equal(3, c.CountFor("grid"));
        Assert.Equal(10, c.Total);
    }

    // GAs evaluate their populations under Parallel.ForEach, so the counter is written from many
    // threads at once. A lost increment understates the trial count, which inflates DSR — the one
    // direction that must never happen by accident.
    [Fact]
    public void Record_LosesNoIncrementsUnderParallelEvaluation()
    {
        var c = new GaTrialCounter();
        Parallel.For(0, 10_000, _ => c.Record("fade_short"));
        Assert.Equal(10_000, c.CountFor("fade_short"));
    }

    // Trials ACCUMULATE across runs. Every retrain is another pass over the search space, and the
    // genotype that ends up committed was chosen with all of that behind it. Resetting per run
    // would let a retrain launder away the multiple-testing burden it just added.
    [Fact]
    public void Save_AccumulatesOntoAnExistingLedgerRatherThanReplacingIt()
    {
        string path = TempPath();
        try
        {
            var first = new GaTrialCounter();
            for (int i = 0; i < 5; i++) first.Record("fade_short");
            first.Save(path);

            var second = new GaTrialCounter();
            for (int i = 0; i < 4; i++) second.Record("fade_short");
            for (int i = 0; i < 2; i++) second.Record("grid");
            second.Save(path);

            var ledger = GaTrialCounter.Load(path);
            Assert.Equal(9, ledger["fade_short"]);
            Assert.Equal(2, ledger["grid"]);
        }
        finally { File.Delete(path); }
    }

    // A missing ledger must not silently read as "no search happened" — that would make DSR
    // maximally permissive exactly when the evidence is absent.
    [Fact]
    public void Load_MissingLedger_IsEmptyAndTotalFallsBackToTheConservativeDefault()
    {
        var ledger = GaTrialCounter.Load(TempPath());
        Assert.Empty(ledger);
        Assert.Equal(GaTrialCounter.ConservativeDefault, GaTrialCounter.TotalFrom(ledger));
    }

    [Fact]
    public void TotalFrom_SumsTheLedgerWhenItHasEntries()
    {
        var ledger = new Dictionary<string, int> { ["fade_short"] = 1200, ["grid"] = 800 };
        Assert.Equal(2000, GaTrialCounter.TotalFrom(ledger));
    }
}
