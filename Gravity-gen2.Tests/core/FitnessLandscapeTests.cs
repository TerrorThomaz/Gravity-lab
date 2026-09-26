using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// GUARD AGAINST THE CLIFF CLASS OF DEFECT.
//
// Three separate times this codebase put a HARD GATE inside a function the GA selects on:
//   · the tail-ratio term, a step at n=100 (fixed: ramped over 40..100)
//   · Simulator.SharpeRatio, 0 below profit factor 1.3 (fixed: screen split out)
//   · FoldScoreHelper.PerTradeSharpe, the same 1.3 cut-off inherited by copy (fixed: ramped)
//
// Each was invisible because the unit tests PINNED THE VALUE the function returned rather than
// asking whether the function was shaped like something a search can climb. A pinned value cannot
// notice that the gradient is zero across the band the GA actually operates in.
//
// So these test the SHAPE, not the values: every statistic the fold score consumes must be
// continuous and monotone over its live domain. A new statistic added to Canonical should get a
// case here; a guard clause that returns a constant will fail it.
public class FitnessLandscapeTests
{
    // Canonical exits early below profit factor 1.0, so the live domain starts there. The measured
    // walk-forward book sits at PF 1.00–1.15, which is why a dead zone here is not academic.
    private const double LiveDomainLo = 1.0;
    private const double LiveDomainHi = 3.0;

    // n/2 wins of `pf` against n/2 losses of 1.0 — profit factor is exactly `pf` by construction.
    private static List<double> AtProfitFactor(double pf, int n = 40)
        => Enumerable.Range(0, n).Select(i => i % 2 == 0 ? pf : -1.0).ToList();

    private static IEnumerable<double> LiveDomain(int steps = 60)
        => Enumerable.Range(0, steps + 1)
                     .Select(i => LiveDomainLo + (LiveDomainHi - LiveDomainLo) * i / steps);

    public static TheoryData<string, Func<List<double>, double>> GaConsumedStatistics() => new()
    {
        { nameof(FoldScoreHelper.PerTradeSharpe),  FoldScoreHelper.PerTradeSharpe },
        { nameof(FoldScoreHelper.PerTradeSortino), FoldScoreHelper.PerTradeSortino },
        { nameof(Simulator.ProfitFactor),          Simulator.ProfitFactor },
    };

    // NO DEAD ZONES. A statistic that returns the same value across a stretch of the live domain
    // gives the GA no gradient there — genotypes that differ really are ranked identically.
    [Theory]
    [MemberData(nameof(GaConsumedStatistics))]
    public void Statistic_HasGradientAcrossTheLiveDomain(string name, Func<List<double>, double> stat)
    {
        var values = LiveDomain().Select(pf => stat(AtProfitFactor(pf))).ToList();
        int flat = 0, longestFlat = 0;
        for (int i = 1; i < values.Count; i++)
        {
            if (Math.Abs(values[i] - values[i - 1]) < 1e-12) longestFlat = Math.Max(longestFlat, ++flat);
            else flat = 0;
        }
        // A tenth of the domain is a generous allowance; the PF<1.3 cliff pinned a full third.
        Assert.True(longestFlat < values.Count / 10,
                    $"{name}: {longestFlat} consecutive flat steps of {values.Count} — the GA cannot " +
                    "distinguish genotypes across that band.");
    }

    // NO CLIFFS. A discontinuity is a place the GA is paid to sit just past, and a place where a
    // tiny genotype change flips the fold score by a lot.
    [Theory]
    [MemberData(nameof(GaConsumedStatistics))]
    public void Statistic_HasNoDiscontinuityInTheLiveDomain(string name, Func<List<double>, double> stat)
    {
        var pts = LiveDomain(200).Select(pf => (pf, v: stat(AtProfitFactor(pf)))).ToList();
        var steps = Enumerable.Range(1, pts.Count - 1)
                              .Select(i => Math.Abs(pts[i].v - pts[i - 1].v)).ToList();
        double median = steps.OrderBy(x => x).ElementAt(steps.Count / 2);
        if (median < 1e-12) return;   // constant statistic: the dead-zone test owns that case

        for (int i = 0; i < steps.Count; i++)
            Assert.True(steps[i] < median * 25,
                        $"{name}: jump of {steps[i]:F6} at pf≈{pts[i + 1].pf:F3} against a median step " +
                        $"of {median:F6} — that is a cliff, not a slope.");
    }

    // MONOTONE. More profitable must never score lower, or the GA is selected against performance
    // somewhere in its own operating range.
    [Theory]
    [MemberData(nameof(GaConsumedStatistics))]
    public void Statistic_IsMonotoneInProfitFactor(string name, Func<List<double>, double> stat)
    {
        double prev = double.NegativeInfinity;
        foreach (double pf in LiveDomain())
        {
            double v = stat(AtProfitFactor(pf));
            Assert.True(v >= prev - 1e-12, $"{name}: score fell as profit factor rose to {pf:F3}");
            prev = v;
        }
    }

    // The whole fold score, end to end: a strictly better fold must score strictly higher.
    [Fact]
    public void Canonical_IsMonotoneInProfitFactorAcrossTheLiveDomain()
    {
        double prev = double.NegativeInfinity;
        foreach (double pf in LiveDomain(40))
        {
            double score = FoldScoreHelper.Canonical(AtProfitFactor(pf), 1.0, 5, new FitnessConfig());
            Assert.True(score >= prev - 1e-9,
                        $"Canonical fell as profit factor rose to {pf:F3} ({prev:F6} -> {score:F6})");
            prev = score;
        }
    }
}
