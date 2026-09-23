using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// GA fitness measures drawdown by walking TRADE RETURNS and booking each one whole. A trade that
// sank 30% before closing at +1% is indistinguishable from one that rose smoothly to +1%. The GA
// therefore cannot see path risk at all, and it acts on that blindness: retraining Grid under the
// corrected fitness moved MaxHoldCandles 53 -> 164, tripling time at risk for a fold score that
// could not register the cost. Out-of-sample that genotype was worse (Sharpe 1.34 -> 1.06).
//
// These pin the fix: per-trade maximum adverse excursion, fed into the same drawdown term.
public class FitnessPathRiskTests
{
    private const int    MinTrades = 5;
    private const double PosFrac   = 1.0;

    // 12 trades, all modestly profitable — a fold the old drawdown walk sees as almost riskless.
    private static List<double> Returns() =>
        Enumerable.Range(0, 12).Select(i => i % 3 == 0 ? -1.0 : 2.0).ToList();

    private static double Score(List<double> r, IReadOnlyList<double>? mae) =>
        FoldScoreHelper.Canonical(r, PosFrac, MinTrades, new FitnessConfig(), maePct: mae);

    // THE NO-OP GUARANTEE. Callers that cannot supply excursions (every simulator until it is
    // wired) must get bit-identical scores, or wiring one strategy would silently re-rank another.
    [Fact]
    public void OmittingExcursions_ScoresExactlyAsBefore()
    {
        var r = Returns();
        Assert.Equal(FoldScoreHelper.Canonical(r, PosFrac, MinTrades, new FitnessConfig()),
                     Score(r, null), 12);
    }

    // Zero excursion means "never underwater", which is what the old walk implicitly assumed.
    [Fact]
    public void ZeroExcursions_ScoreTheSameAsNoExcursions()
    {
        var r = Returns();
        Assert.Equal(Score(r, null), Score(r, r.Select(_ => 0.0).ToList()), 12);
    }

    // The defect, in one assertion: identical returns, but one fold got there through a 25%
    // drawdown on every trade. It must score lower.
    [Fact]
    public void DeepExcursions_ScoreLowerThanTheSameReturnsWithoutThem()
    {
        var r = Returns();
        double smooth = Score(r, r.Select(_ => 0.0).ToList());
        double violent = Score(r, r.Select(_ => -25.0).ToList());

        Assert.True(violent < smooth,
                    $"a fold that dived 25% on every trade scored {violent:F4} against {smooth:F4} for " +
                    "the same returns taken smoothly");
    }

    // Monotone: deeper excursions must never score higher, or the GA is paid to take path risk.
    [Fact]
    public void Score_IsMonotoneDecreasingInExcursionDepth()
    {
        var r = Returns();
        double prev = double.MaxValue;
        foreach (double depth in new[] { 0.0, -5.0, -10.0, -20.0, -40.0 })
        {
            double s = Score(r, r.Select(_ => depth).ToList());
            Assert.True(s <= prev + 1e-12, $"excursion {depth}% scored {s:F4}, above the shallower {prev:F4}");
            prev = s;
        }
    }

    // An excursion list of the wrong length is a wiring mistake. Silently ignoring it would make
    // the term vanish without anyone noticing, so it must be ignored LOUDLY — treated as absent —
    // rather than half-applied against mismatched trades.
    [Fact]
    public void MismatchedExcursionLength_IsIgnoredRatherThanMisapplied()
    {
        var r = Returns();
        Assert.Equal(Score(r, null), Score(r, new List<double> { -10.0, -10.0 }), 12);
    }
}

// CanonicalRegime drops trades whose RegimeBars fall under the sustain threshold. If the excursion
// list is not filtered by the SAME predicate the two fall out of alignment, Canonical sees a
// mismatched length and silently discards every excursion — the term vanishes with no error.
// This was a real defect: the parameter was added to CanonicalRegime and then not passed on.
public class CanonicalRegimeExcursionAlignmentTests
{
    private const int MinTrades = 5;

    private static List<(double Return, int RegimeBars)> Mixed() =>
        Enumerable.Range(0, 20)
                  .Select(i => (Return: i % 3 == 0 ? -1.0 : 2.0, RegimeBars: i % 2 == 0 ? 10 : 0))
                  .ToList();

    [Fact]
    public void CanonicalRegime_AppliesExcursionsAfterTheRegimeFilter()
    {
        var r = Mixed();
        var deep = r.Select(_ => -30.0).ToList();

        double without = FoldScoreHelper.CanonicalRegime(r, 1.0, 5, MinTrades, new FitnessConfig());
        double with    = FoldScoreHelper.CanonicalRegime(r, 1.0, 5, MinTrades, new FitnessConfig(),
                                                         maePct: deep);

        Assert.True(with < without,
                    $"excursions were dropped by the regime filter: {with:F4} vs {without:F4}");
    }

    [Fact]
    public void CanonicalRegime_WithoutExcursions_IsUnchanged()
    {
        var r = Mixed();
        Assert.Equal(FoldScoreHelper.CanonicalRegime(r, 1.0, 5, MinTrades, new FitnessConfig()),
                     FoldScoreHelper.CanonicalRegime(r, 1.0, 5, MinTrades, new FitnessConfig(), maePct: null), 12);
    }
}
