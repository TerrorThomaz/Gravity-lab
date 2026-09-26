using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// THE BOX MUST BE THE BOX. Every genotype operator — Random, Mutate, ClampToBounds, FromVector —
// has to read its ranges from ONE table, or narrowing that table silently does nothing to the
// operators that actually generate the population.
//
// This was live in GridGenotype: `Bounds` declared BbPeriod [5,50], EmaPeriod [5,100] and
// BbWidthMaxPct [0.8,5.0], while FromVector hardcoded [10,50], [10,100] and [0.8,2.5]. GridGA
// hands `GridGenotype.Bounds` to the Bayesian optimiser and evaluates through FromVector, so the
// TPE surrogate proposed points in one box and was scored on their projection into a smaller one —
// it was fitted to mislabelled data across three dimensions, half the range in BbWidthMaxPct's case.
// Random and Mutate held a third copy of the numbers, agreeing by luck rather than by construction.
//
// The properties below are what "single source of truth" actually means, stated so a fourth copy
// cannot be introduced without failing.
public class GenotypeBoundsTests
{
    // ── Grid ─────────────────────────────────────────────────────────────────────────────────

    // The sharpest statement: a vector sitting exactly ON a declared bound must survive FromVector
    // unchanged. Any tighter internal clamp moves it, and this catches that with the gene named.
    //
    // Verified by reintroducing the old FromVector clamp for BbWidthMaxPct: it fails with
    // "Bounds declares [0.8, 5] but FromVector moved the edge value 5 to 2.5". Note this case is
    // VACUOUS for a pinned gene, where lo == hi and no tighter clamp can move the value —
    // Grid_RangeDefiningPeriods_ArePinnedNotSearched is what covers those.
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    [InlineData(5)] [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)]
    [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    public void Grid_FromVector_HonoursTheDeclaredBoundsExactly(int gene)
    {
        double lo = GridGenotype.Bounds[gene, 0], hi = GridGenotype.Bounds[gene, 1];
        string name = GridGeneNames[gene];

        foreach (double edge in new[] { lo, hi })
        {
            // A vector of midpoints, with the gene under test pushed to its declared edge.
            var v = Midpoint(GridGenotype.Bounds);
            v[gene] = edge;
            // adxCeiling must not bind, or gene 0's upper edge is clipped by an unrelated argument.
            double[] round = GridGenotype.FromVector(v, adxCeiling: double.MaxValue).ToVector();

            Assert.True(Math.Abs(round[gene] - edge) < 1e-9,
                $"{name}: Bounds declares [{lo}, {hi}] but FromVector moved the edge value {edge} " +
                $"to {round[gene]} — a second, tighter copy of this gene's range exists.");
        }
    }

    // Random must produce genotypes the box already accepts. If ClampToBounds moves a fresh draw,
    // Random is sampling from a different range than Bounds declares.
    [Fact]
    public void Grid_Random_IsAlwaysAClampToBoundsFixedPoint()
    {
        var rng = new Random(11);
        for (int i = 0; i < 400; i++)
        {
            var g = GridGenotype.Random(rng, seed: null, adxCeiling: double.MaxValue);
            AssertFixedPoint(g.ToVector(), g.ClampToBounds().ToVector(), GridGeneNames, "Random");
        }
    }

    // Same property for Mutate, which is where the GA spends most of its evaluations.
    [Fact]
    public void Grid_Mutate_IsAlwaysAClampToBoundsFixedPoint()
    {
        var rng = new Random(12);
        var g = GridGenotype.Random(rng, seed: null, adxCeiling: double.MaxValue);
        for (int i = 0; i < 400; i++)
        {
            g = g.Mutate(rng, 1.0, adxCeiling: double.MaxValue);
            AssertFixedPoint(g.ToVector(), g.ClampToBounds().ToVector(), GridGeneNames, "Mutate");
        }
    }

    // ── Pinned genes (step 4) ────────────────────────────────────────────────────────────────
    //
    // The committed Grid and GridShort genotypes both carry EmaPeriod=5 and BbPeriod=5 — the floor
    // of each range. A 5-bar "range centre" that also re-anchors to the live EMA is not a grid
    // around a range; the strategy's premise was optimised away. These two are now fixed on
    // convention, chosen WITHOUT consulting fitness, so they cost no trials and cannot drift.
    [Fact]
    public void Grid_RangeDefiningPeriods_ArePinnedNotSearched()
    {
        Assert.Equal(GridGenotype.Bounds[1, 0], GridGenotype.Bounds[1, 1]);  // BbPeriod
        Assert.Equal(GridGenotype.Bounds[3, 0], GridGenotype.Bounds[3, 1]);  // EmaPeriod

        var rng = new Random(13);
        for (int i = 0; i < 100; i++)
        {
            var g = GridGenotype.Random(rng, seed: null, adxCeiling: double.MaxValue)
                                .Mutate(rng, 1.0, adxCeiling: double.MaxValue);
            Assert.Equal((int)GridGenotype.Bounds[1, 0], g.BbPeriod);
            Assert.Equal((int)GridGenotype.Bounds[3, 0], g.EmaPeriod);
        }
    }

    // ── FadeShort payoff-ratio constraint (step 1) ───────────────────────────────────────────
    //
    // The committed genotype pairs StopLossAtrMult = 0.30 (the floor of [0.3, 2.0]) with
    // TakeProfitAtrMult = 23.27 (against a ceiling of 25). A 77:1 payoff ratio almost never reaches
    // its target, and when it does the win is enormous — which is precisely "99% of the edge lives
    // in the top 1% of trades", stated as arithmetic rather than as a statistic.
    //
    // A CONSTRAINT, not a penalty: an inexpressible genotype costs zero trials and cannot be traded
    // off against other terms, whereas a penalty can always be outbid by a large enough `gain`.

    [Fact]
    public void FadeShort_TheCommittedLotteryTicket_ProjectsBackIntoTheConstraint()
    {
        var lottery = new FadeShortGenotype
        {
            EmaPeriod = 46, AdxThreshold = 14.2, LookbackCandles = 243,
            RsiOverbought = 65, RsiDivThreshold = 5.4, MinRallyAtrMult = 16.3,
            StopLossAtrMult = 0.30, MaeAtrMult = 1.5, TakeProfitAtrMult = 23.27,
            TrailingActivationAtrMult = 3.64, TrailingStopAtrMult = 3.46,
            MaxHoldCandles = 72, PositionSizePct = 0.05,
            RegimeSustainBars = 0, RegimeEmaPeriod = 152, RegimeSlopeLookback = 51,
        };

        var c = lottery.ClampToBounds();
        Assert.True(c.TakeProfitAtrMult / c.StopLossAtrMult <= FadeShortGenotype.MaxPayoffRatio + 1e-9,
            $"77:1 genotype survived as {c.TakeProfitAtrMult / c.StopLossAtrMult:F1}:1");
    }

    [Fact]
    public void FadeShort_EveryOperator_RespectsThePayoffRatioCap()
    {
        var rng = new Random(14);
        var g = FadeShortGenotype.Random(rng);
        for (int i = 0; i < 400; i++)
        {
            AssertRatio(g, "Random/Mutate");
            AssertRatio(FadeShortGenotype.FromVector(g.ToVector()), "FromVector");
            AssertRatio(g.ClampToBounds(), "ClampToBounds");
            g = i % 2 == 0 ? g.Mutate(rng, 1.0) : FadeShortGenotype.Random(rng);
        }

        static void AssertRatio(FadeShortGenotype g, string via)
            => Assert.True(g.TakeProfitAtrMult / g.StopLossAtrMult <= FadeShortGenotype.MaxPayoffRatio + 1e-9,
                $"{via} produced TP {g.TakeProfitAtrMult:F2} / Stop {g.StopLossAtrMult:F2} = " +
                $"{g.TakeProfitAtrMult / g.StopLossAtrMult:F1}:1, over the {FadeShortGenotype.MaxPayoffRatio}:1 cap");
        }

    // The projection must never leave a gene outside its own range while satisfying the ratio.
    [Fact]
    public void FadeShort_RatioProjection_KeepsBothGenesInBounds()
    {
        var rng = new Random(15);
        for (int i = 0; i < 400; i++)
        {
            var g = FadeShortGenotype.Random(rng);
            Assert.InRange(g.StopLossAtrMult,   FadeShortGenotype.Bounds[6, 0], FadeShortGenotype.Bounds[6, 1]);
            Assert.InRange(g.TakeProfitAtrMult, FadeShortGenotype.Bounds[8, 0], FadeShortGenotype.Bounds[8, 1]);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static readonly string[] GridGeneNames =
    [
        "AdxThreshold", "BbPeriod", "BbWidthMaxPct", "EmaPeriod", "GridStepAtrMult", "GridLevels",
        "TakeProfitAtrMult", "HardStopAtrMult", "BailOutAtrMult", "MaxHoldCandles",
        "RungSellFrac", "ReanchorAlpha", "SlopeThreshold", "SlopeLookback",
    ];

    private static double[] Midpoint(double[,] bounds)
    {
        int n = bounds.GetLength(0);
        var v = new double[n];
        for (int i = 0; i < n; i++) v[i] = (bounds[i, 0] + bounds[i, 1]) / 2.0;
        return v;
    }

    private static void AssertFixedPoint(double[] raw, double[] clamped, string[] names, string op)
    {
        for (int i = 0; i < raw.Length; i++)
            Assert.True(Math.Abs(raw[i] - clamped[i]) < 1e-9,
                $"{op} produced {names[i]}={raw[i]}, which ClampToBounds moved to {clamped[i]} — " +
                $"{op} is sampling from a different range than Bounds declares.");
    }
}
