using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// A GA returns the argmax of a search over tens of thousands of candidates. On ~3 years of data the
// MinBTL budget is roughly 5 INDEPENDENT configurations, so the argmax is, by default, the best of
// N draws from noise. These two screens are the cheapest published tests that separate a genotype
// with a real basin from one sitting on a spike.
public class FinalistScreenTests
{
    // ── Outlier sensitivity ──────────────────────────────────────────────────────────────────
    // Falck, Rej & Thesmar (CFM, arXiv 2105.01380) found the sensitivity of in-sample performance
    // to a handful of best trades is an EX-ANTE predictor of out-of-sample decay, adding ~15% of
    // explanatory power on top of publication year. A genotype whose edge lives in a few trades has
    // no edge.

    private static double Mean(List<double> r) => r.Count == 0 ? 0.0 : r.Average();

    [Fact]
    public void ConcentratedEdge_ScoresHighSensitivity()
    {
        // 199 breakeven trades and one enormous winner: the entire edge is one trade.
        var r = Enumerable.Repeat(0.0, 199).Append(400.0).ToList();
        var s = FinalistScreen.OutlierSensitivity(r, Mean);

        Assert.True(s.DropTop1Pct > 0.9,
            $"deleting the top 1% removed only {s.DropTop1Pct:P0} of the score — expected almost all");
    }

    [Fact]
    public void BroadEdge_ScoresLowSensitivity()
    {
        // Every trade contributes equally: deleting the best 1% removes about 1%.
        var r = Enumerable.Repeat(1.0, 200).ToList();
        var s = FinalistScreen.OutlierSensitivity(r, Mean);

        Assert.True(s.DropTop1Pct < 0.05,
            $"a uniform series lost {s.DropTop1Pct:P0} when the top 1% was deleted — expected ~1%");
    }

    [Fact]
    public void OutlierSensitivity_DeletesWinnersOnly_NeverLosers()
    {
        // Deleting losers would flatter the score. The screen must only remove the top winners.
        var r = new List<double> { -50.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 20.0 };
        var s = FinalistScreen.OutlierSensitivity(r, Mean);

        // Dropping the single best (20.0) from 10 trades must LOWER the mean, not raise it.
        Assert.True(s.ScoreNoTop5Pct < s.ScoreFull);
    }

    // ── Perturbed fitness ────────────────────────────────────────────────────────────────────
    // A genotype whose neighbours all score similarly sits on a plateau; a lone peak is noise.
    // Overfitting produces sharp optima precisely because it is fitting individual trades.

    // A "genotype" here is just a point; fitness is a function of it, so the screen can be tested
    // without dragging a real genotype and simulator into a unit test.
    private static FinalistScreen.Robustness ScreenAt(double x, Func<double, double> fitness)
        => FinalistScreen.PerturbedFitness(
            x, fitness,
            (p, rng, mag) => p + (rng.NextDouble() * 2.0 - 1.0) * mag,
            magnitudes: new[] { 0.10, 0.20 }, samplesPerMagnitude: 40, seed: 7);

    [Fact]
    public void SharpSpike_ScoresLowRobustness()
    {
        // Fitness 100 exactly at 0, ~0 anywhere else: the classic overfit optimum.
        var s = ScreenAt(0.0, p => Math.Abs(p) < 1e-9 ? 100.0 : 0.0);
        Assert.True(s.Ratio < 0.2,
            $"a spike scored robustness {s.Ratio:F3} — expected near zero");
    }

    [Fact]
    public void BroadPlateau_ScoresHighRobustness()
    {
        // Fitness is flat across the whole neighbourhood: a real basin.
        var s = ScreenAt(0.0, _ => 42.0);
        Assert.True(s.Ratio > 0.95,
            $"a plateau scored robustness {s.Ratio:F3} — expected ~1.0");
    }

    [Fact]
    public void GentleSlope_ScoresBetweenTheTwo()
    {
        // Quadratic falloff: degrades, but gracefully.
        var s = ScreenAt(0.0, p => 100.0 - 50.0 * p * p);
        Assert.InRange(s.Ratio, 0.5, 1.0);
    }

    // The screen must report the WORST neighbour too — a genotype whose median neighbour is fine
    // but whose nearby minimum collapses is still a cliff edge, and the median alone hides that.
    [Fact]
    public void Robustness_ReportsTheWorstNeighbourNotJustTheMedian()
    {
        // Fine everywhere except a narrow trench just to the right.
        var s = ScreenAt(0.0, p => p is > 0.08 and < 0.12 ? -100.0 : 50.0);
        Assert.True(s.Ratio > 0.8, "median should still look healthy here");
        Assert.True(s.WorstRatio < 0.0, $"worst neighbour ratio {s.WorstRatio:F3} should expose the trench");
    }
}
