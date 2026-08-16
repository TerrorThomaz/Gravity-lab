using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class CovarianceMatrixTests
{
    private static double[] DiagonalMatrix(int k, double v)
    {
        var m = new double[k * k];
        for (int i = 0; i < k; i++) m[i * k + i] = v;
        return m;
    }

    [Fact]
    public void Sample_IsSymmetric_AndHasVarianceOnDiagonal()
    {
        var a = new double[] { 1, 2, 3, 4 };
        var b = new double[] { 2, 4, 6, 8 };   // perfectly correlated with a
        var cov = CovarianceMatrix.Sample([a, b])!;
        Assert.Equal(1.6667, cov[0 * 2 + 0], 3);  // sample variance of [1,2,3,4]
        Assert.Equal(cov[0 * 2 + 1], cov[1 * 2 + 0], 6); // symmetric
        // Perfect correlation ⇒ correlation ≈ 1.
        var corr = CovarianceMatrix.Correlations(cov, 2)!;
        Assert.Equal(1.0, corr[0 * 2 + 1], 6);
    }

    [Fact]
    public void Sample_NullOnMisalignedOrTooShort()
    {
        Assert.Null(CovarianceMatrix.Sample([new double[] { 1, 2, 3 }, new double[] { 1, 2 }]));
        Assert.Null(CovarianceMatrix.Sample([new double[] { 1.0 }]));
        Assert.Null(CovarianceMatrix.Sample(Array.Empty<double[]>()));
    }

    [Fact]
    public void Shrink_ReachesScalarTargetAtLambdaOne()
    {
        // A far-from-diag sample covariance should shrink to λ=1 → avg_diag × I.
        var cov = new double[] { 4, 3, 3, 4 };   // correlated
        var shrunk = CovarianceMatrix.Shrink(cov, 2, lambda: 1.0)!;
        Assert.Equal(4.0, shrunk[0 * 2 + 0], 6);
        Assert.Equal(0.0, shrunk[0 * 2 + 1], 6);  // off-diag killed
    }

    [Fact]
    public void Variance_MatchesQuadraticForm()
    {
        var cov = new double[] { 4, 1, 1, 2 };
        var w = new double[] { 0.6, 0.4 };
        // σ² = 0.36*4 + 2*0.6*0.4*1 + 0.16*2
        double expected = 0.36 * 4 + 2 * 0.6 * 0.4 * 1 + 0.16 * 2;
        Assert.Equal(expected, CovarianceMatrix.Variance(cov, 2, w), 4);
    }

    [Fact]
    public void MarginalRisk_ContributionsSumToPortfolioVol()
    {
        var cov = new double[] { 4, 1, 1, 2 };
        var w = new double[] { 0.6, 0.4 };
        var sigP = CovarianceMatrix.Volatility(cov, 2, w);
        var (_, ctr) = CovarianceMatrix.MarginalRisk(cov, 2, w)!.Value;
        Assert.Equal(sigP, ctr.Sum(), 4);   // Σ CTR = σ_p
    }

    [Fact]
    public void EffectiveBets_OneForPerfectCorrelation_EqualForOrthogonal()
    {
        // Perfectly correlated two assets (var 1, rho=0.99) ≈ one effective bet.
        var corr = new double[] { 1, 0.99, 0.99, 1 };
        double effCorrelated = CovarianceMatrix.EffectiveBets(corr, 2);
        Assert.InRange(effCorrelated, 1.0, 1.5);

        // Orthogonal identical variance → two effective bets.
        var orth = new double[] { 1, 0, 0, 1 };
        Assert.Equal(2.0, CovarianceMatrix.EffectiveBets(orth, 2), 2);
    }
}

public class RiskParityTests
{
    [Fact]
    public void EqualRiskContribution_EqualizesContributionForDecoupledAssets()
    {
        // Two assets same vol, uncorrelated: ERC should split 50/50.
        var cov = new double[] { 1, 0, 0, 1 };
        var rp = RiskParity.EqualRiskContribution(cov, 2);
        Assert.Equal(0.5, rp.Weights[0], 3);
        Assert.Equal(0.5, rp.Weights[1], 3);
        // Contributions equal.
        Assert.Equal(rp.Contribution[0], rp.Contribution[1], 3);
    }

    [Fact]
    public void EqualRiskContribution_NotEqualNotional_WhenVolsDiffer()
    {
        // Asset A has 4× the variance of B → B gets more weight (inverse-vol orientation).
        var cov = new double[] { 4, 0, 0, 1 };
        var rp = RiskParity.EqualRiskContribution(cov, 2);
        Assert.True(rp.Weights[1] > rp.Weights[0], "low-vol asset should get more weight under ERC");
        Assert.True(rp.Contribution[0] > 0 && rp.Contribution[1] > 0);
    }

    [Fact]
    public void InverseVol_WeightsRespectVolRatios()
    {
        var cov = new double[] { 4, 0, 0, 1 };
        var w = RiskParity.InverseVol(cov, 2);
        // w_a ∝ 1/σ_a: σ~2 vs σ~1 → weights 1/2 : 1 → ratio 2.
        Assert.Equal(2.0, w[1] / w[0], 3);
        Assert.Equal(1.0, w.Sum(), 6);
    }

    [Fact]
    public void EqualRiskContribution_SumToOne()
    {
        var cov = new double[] { 2, 0.5, 0.5, 3 };
        var rp = RiskParity.EqualRiskContribution(cov, 2);
        Assert.Equal(1.0, rp.Weights.Sum(), 4);
    }
}

public class VaRSizingTests
{
    private static List<double> SymmetricWithTails()
    {
        var l = new List<double>();
        foreach (var _ in Enumerable.Range(0, 80)) l.Add(1.0);
        foreach (var _ in Enumerable.Range(0, 80)) l.Add(-1.0);
        for (int i = 0; i < 20; i++) l.Add(-6.0);   // left fat tail
        return l;
    }

    [Fact]
    public void Percentile_AndExpectedShortfall_DifferOnLeftTail()
    {
        // n=160 → tail 5% = 8 worst. 4 deep losers at -10 with the next 100+ losers at -2 means
        // VaR (8th worst) = -2 but ES (mean of worst 8) mixes four -10 with four -2 → ES < VaR.
        var r = new List<double>();
        for (int i = 0; i < 80; i++) r.Add(1.0);
        for (int i = 0; i < 76; i++) r.Add(-1.0);
        for (int i = 0; i < 4; i++) r.Add(-10.0);   // deep tail
        double var95 = VaRSizing.Percentile(r, 0.05);
        double es95 = VaRSizing.ExpectedShortfall(r, 0.05);
        Assert.True(es95 < var95, $"ES ({es95}) should be deeper in the tail than VaR ({var95})");
        Assert.True(var95 < 0);
    }

    [Fact]
    public void Constrain_LargerTailGetsSmallerMultiplier()
    {
        var mild = new List<double>(); for (int i = 0; i < 100; i++) { mild.Add(1); mild.Add(-1); }
        var nasty = new List<double>(); for (int i = 0; i < 90; i++) { nasty.Add(1); nasty.Add(-1); }
        for (int i = 0; i < 10; i++) nasty.Add(-15);   // brutal tail

        var m1 = VaRSizing.Constrain(mild, budgetPct: 2.0, p: 0.05).Multiplier;
        var m2 = VaRSizing.Constrain(nasty, budgetPct: 2.0, p: 0.05).Multiplier;
        Assert.True(m2 < m1, $"nasty tail ({m2}) must size smaller than mild ({m1})");
    }

    [Fact]
    public void Crra_FractionShrinksWithRiskAversion()
    {
        // Positive-expectation, moderate tail so CRRA gives a non-trivial fraction that responds to γ.
        var r = new List<double>();
        for (int i = 0; i < 90; i++) r.Add(2.0);
        for (int i = 0; i < 80; i++) r.Add(-1.0);
        for (int i = 0; i < 10; i++) r.Add(-5.0);

        double g2 = VaRSizing.CrraOptimalFraction(r, gamma: 2.0, grid: 10000);
        double g8 = VaRSizing.CrraOptimalFraction(r, gamma: 8.0, grid: 10000);
        Assert.True(g8 < g2, $"higher gamma ({g8}) must bet less than low gamma ({g2})");
        Assert.InRange(g2, 0.0, 1.0);
        Assert.True(g2 >= g8, "CRRA optimal fraction must be monotone non-increasing in γ");
        Assert.True(g2 > 0, "positive-expectation strategy should have non-zero optimal fraction");
    }

    [Fact]
    public void CrraGammaOne_ApproachesBaseKelly()
    {
        // Constant +100%/-100% (like heads/tails doubled) has Kelly fraction that maximises log.
        var r = new List<double> { 100, -100, 100, -100, 100, -100, 100, -100, 100, -100 };
        double g1 = VaRSizing.CrraOptimalFraction(r, gamma: 1.0);
        // Theoretical Kelly for even-money with p=0.5 is f=0 — betting nothing maximises E[log].
        // More precisely, log utility says bet everything on a fair coin-flip is ruin-risky → 0.
        Assert.InRange(g1, 0.0, 0.6);
    }

    [Fact]
    public void BuildWeights_UsesRule()
    {
        var trades = new List<(string, double)>();
        foreach (var _ in Enumerable.Range(0, 50)) { trades.Add(("a", 1.0)); trades.Add(("a", -1.0)); }
        foreach (var _ in Enumerable.Range(0, 10)) trades.Add(("a", -20.0));   // left tail
        var w = VaRSizing.BuildWeights(trades, "es")!;
        double esMult = w("a");
        var w2 = VaRSizing.BuildWeights(trades, "var")!;
        // ES is deeper than VaR ⇒ ES constraint is tighter ⇒ ES multiplier ≤ VaR multiplier.
        Assert.True(esMult <= w2("a") + 1e-9, $"ES ({esMult}) should size ≤ VaR ({w2("a")})");
        Assert.Equal(1.0, w("nonexistent"));
    }

    [Fact]
    public void BlendWeights_RangesFromShapeToRiskAsAlphaGoesZeroToOne()
    {
        // Strategy "A": high return, high tail (riskparity-ish gives it high weight; es gives low).
        // Strategy "B": low return, low tail. Blend at alpha=1 = pure shape, alpha=0 = pure risk.
        Func<string, double> shape = s => s == "A" ? 2.0 : 0.5;   // return-shape favours A
        Func<string, double> risk  = s => s == "A" ? 0.2 : 1.8;   // risk-cap punishes A's tail

        var blendLow = SizingMethods.BlendWeights(shape, risk, ["A", "B"], alpha: 0.0, meanNormalise: false)!;
        var blendHigh = SizingMethods.BlendWeights(shape, risk, ["A", "B"], alpha: 1.0, meanNormalise: false)!;

        // Alpha=1 (pure shape) → A dominates; alpha=0 (pure risk) → B dominates.
        Assert.True(blendHigh("A") > blendHigh("B"), "alpha=1 must favour the return-shape winner (A)");
        Assert.True(blendLow("B") > blendLow("A") - 1e-9, "alpha=0 must favour the risk-cheap winner (B)");
        // At alpha=0 the value should equal the risk map.
        Assert.Equal(risk("A"), blendLow("A"), 9);
        // At alpha=1 the value should equal the shape map.
        Assert.Equal(shape("A"), blendHigh("A"), 9);
    }

    [Fact]
    public void BlendWeights_MeanNormalisedPreservesScale()
    {
        Func<string, double> shape = s => s == "A" ? 3.0 : 1.0;
        Func<string, double> risk  = s => s == "A" ? 2.0 : 0.5;
        var w = SizingMethods.BlendWeights(shape, risk, ["A", "B"], alpha: 0.5, meanNormalise: true)!;
        // Mean of the two strategies ≈ 1.0.
        Assert.True(Math.Abs((w("A") + w("B")) / 2.0 - 1.0) < 1e-9,
            $"mean-normalised blend must average ~1, got {(w("A") + w("B")) / 2.0}");
    }
}