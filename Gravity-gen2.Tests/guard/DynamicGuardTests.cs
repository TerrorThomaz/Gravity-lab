using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class DynamicGuardSessionTests
{
    [Fact]
    public void Placeholder_AlwaysPasses()
    {
        // Guard session requires live genotype + trade data.
        // This file is a placeholder — add integration tests once
        // a fixture-based test helper exists.
        Assert.True(true);
    }
}

/// <summary>
/// Covers the portfolio-drawdown long-entry gate (Simulator.SimulatePortfolioExposureCapped's
/// <c>ddLongEntryGatePct</c>) and the units of the gene that feeds it
/// (<see cref="DynamicGuardGenotype.DdEntryGatePct"/>).
///
/// The gate compares against currentDd = (peak - balance) / peak, a FRACTION in [0,1].
/// Before 2026-08 the gene was bounded {2, 15} — a percent scale — so every reachable value
/// demanded a &gt;200% drawdown and the gate was inert across its entire search space.
/// </summary>
public class DdEntryGateTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Big loser first (drives the portfolio ~10% below peak), then one more trade
    // two hours later whose strategy label decides whether the gate applies.
    private static List<(DateTime, double, double, TimeSpan, string)> Trades(string secondStrategy) =>
    [
        (T0,                 -70.0, 0.5, TimeSpan.FromHours(1), "fade_short"),
        (T0.AddHours(2),     +50.0, 0.5, TimeSpan.FromHours(1), secondStrategy),
    ];

    private static List<(DateTime, double, double, TimeSpan, string)> FirstTradeOnly() =>
    [
        (T0, -70.0, 0.5, TimeSpan.FromHours(1), "fade_short"),
    ];

    private static double Run(List<(DateTime, double, double, TimeSpan, string)> trades, double gate) =>
        Simulator.SimulatePortfolioExposureCapped(trades, ddLongEntryGatePct: gate).EndBalance;

    [Fact]
    public void OpeningLoss_DrivesPortfolioIntoRoughlyTenPercentDrawdown()
    {
        // Sanity-check the fixture: without this drawdown the gate tests prove nothing.
        var p = Simulator.SimulatePortfolioExposureCapped(FirstTradeOnly());
        Assert.InRange(p.MaxDrawdownPct, 8.0, 15.0);
    }

    [Fact]
    public void DdGate_BlocksLongEntry_WhenPortfolioDrawdownExceedsGate()
    {
        double afterLossOnly = Run(FirstTradeOnly(), DynamicGuardGenotype.DdGateDisabled);
        double gated         = Run(Trades("diplong"), 0.08);
        double ungated       = Run(Trades("diplong"), DynamicGuardGenotype.DdGateDisabled);

        // With an 8% gate and ~10% drawdown, the winning diplong never enters:
        // the balance is exactly what it was after the losing trade.
        Assert.Equal(afterLossOnly, gated, 10);
        Assert.True(ungated > gated,
            $"Ungated run should have taken the +50% diplong: {ungated:F4} vs {gated:F4}");
    }

    [Fact]
    public void DdGate_BlocksSwingLong_Too()
    {
        double afterLossOnly = Run(FirstTradeOnly(), DynamicGuardGenotype.DdGateDisabled);
        Assert.Equal(afterLossOnly, Run(Trades("swing_long"), 0.08), 10);
    }

    [Fact]
    public void DdGate_DoesNotBlockShortEntries()
    {
        double afterLossOnly = Run(FirstTradeOnly(), DynamicGuardGenotype.DdGateDisabled);
        double gatedShort    = Run(Trades("fade_short"), 0.08);

        // Same drawdown, same gate — but the gate is long-only, so the short still enters.
        Assert.True(gatedShort > afterLossOnly,
            $"Short entry should not be gated: {gatedShort:F4} should exceed {afterLossOnly:F4}");
        Assert.Equal(Run(Trades("fade_short"), DynamicGuardGenotype.DdGateDisabled), gatedShort, 10);
    }

    [Fact]
    public void DdGate_Disabled_SkipsNothing()
    {
        // At the disabled default a long behaves exactly like a short: nothing is skipped.
        double longRun  = Run(Trades("diplong"),    DynamicGuardGenotype.DdGateDisabled);
        double shortRun = Run(Trades("fade_short"), DynamicGuardGenotype.DdGateDisabled);
        Assert.Equal(shortRun, longRun, 10);
    }

    [Fact]
    public void DdGate_LooseGate_DoesNotFireBelowThreshold()
    {
        // Drawdown is ~10%; a 15% gate must not fire.
        double loose = Run(Trades("diplong"), 0.15);
        double off   = Run(Trades("diplong"), DynamicGuardGenotype.DdGateDisabled);
        Assert.Equal(off, loose, 10);
    }

    [Fact]
    public void OldPercentScaleValue_CouldNeverFire()
    {
        // Regression guard: 6.128 was the trained value under the old {2, 15} bounds.
        // Passed raw it means "612.8% drawdown" — the gate is dead.
        double stale = Run(Trades("diplong"), 6.128278240331621);
        double off   = Run(Trades("diplong"), DynamicGuardGenotype.DdGateDisabled);
        Assert.Equal(off, stale, 10);
    }
}

public class DdEntryGateBoundsTests
{
    private const int DdGateGene = DynamicGuardGenotype.DdGateGeneIndex;

    private const int DdGateEnableGene = DynamicGuardGenotype.DdGateEnableGeneIndex;

    private static DynamicGuardGenotype WithDdGate(double dd) =>
        new(24, 1.8, 12, -0.05, 0.4, 3.0, 12, 0.05, 2.5, dd, 1.0, 1.0, 1.0, 0.10, 1.0);

    [Fact]
    public void Bounds_AreFractions_NotPercentages()
    {
        double lo = DynamicGuardGenotype.Bounds[DdGateGene, 0];
        double hi = DynamicGuardGenotype.Bounds[DdGateGene, 1];
        Assert.Equal(0.02, lo, 10);
        Assert.Equal(0.15, hi, 10);
        // Every threshold the GA can draw must be a reachable drawdown, i.e. strictly below 100%.
        // "Off" is expressed by the separate switch gene, not by widening this bound.
        Assert.True(hi < DynamicGuardGenotype.DdGateDisabled,
            $"Upper bound {hi} must be below the disabled sentinel — otherwise the threshold is inert.");
    }

    // ── Defect 1: the search space must be able to say "gate off" ──────────────────────
    //
    // The DD gate is a risk control. If no individual the optimiser can construct is able to
    // express "off", the control is imposed rather than validated — the first training run
    // would be conducted on a space that cannot reject the feature. This is a regression
    // guard for exactly that: Bounds[9] alone was once {0.02, 0.15} while the "off" sentinel
    // was 1.0, i.e. outside every bound the GA and the TPE sampler draw from.

    [Fact]
    public void SearchSpace_CanExpressGateDisabled_AndGateEnabled()
    {
        // Drives DynamicGuardGA.RandomGenes itself — the real initialiser, not a copy of it —
        // because the claim under test is a property of the GA's actual starting population.
        // BayesianOptimizer.RandomPoint draws identically (uniform inside Bounds).
        var rng   = new Random(1234);
        int nDim  = DynamicGuardGenotype.Bounds.GetLength(0);
        int off = 0, live = 0;

        for (int i = 0; i < 400; i++)
        {
            var g = DynamicGuardGenotype.FromGenes(DynamicGuardGA.RandomGenes(nDim, rng));
            if (g.DdGateIsLive)
            {
                live++;
                Assert.InRange(g.DdEntryGatePct,
                    DynamicGuardGenotype.Bounds[DdGateGene, 0], DynamicGuardGenotype.Bounds[DdGateGene, 1]);
            }
            else
            {
                off++;
                Assert.Equal(DynamicGuardGenotype.DdGateDisabled, g.DdEntryGatePct, 10);
            }
        }

        Assert.True(off > 0, "The optimiser cannot construct an individual with the DD gate OFF — "
                           + "the gate would be mandatory across the whole search space.");
        Assert.True(live > 0, "The optimiser cannot construct an individual with the DD gate LIVE.");
        // The switch gene is uniform on [0, 1] with a 0.5 threshold, so a default-sized
        // population (40) must contain both states with overwhelming probability, not merely
        // "eventually over 400 draws". Anything far off 50/50 means the prior is skewed.
        Assert.InRange(live / 400.0, 0.35, 0.65);
    }

    [Fact]
    public void ThresholdGene_DeliberatelyCannotReachTheDisabledSentinel()
    {
        // Documents the design decision behind the two-gene split. Widening Bounds[9] to
        // {0.02, 1.0} would also let the search say "off", but it would tie the threshold's
        // mutation sigma (0.1 x range) and the TPE bandwidth (>= 0.03 x range) to a range
        // ~7.5x wider than the live band [0.02, 0.15] — every perturbation of a live gene
        // would fling it out of the band and the threshold could never be tuned.
        double lo = DynamicGuardGenotype.Bounds[DdGateGene, 0];
        double hi = DynamicGuardGenotype.Bounds[DdGateGene, 1];
        Assert.True(hi < DynamicGuardGenotype.DdGateDisabled);

        double gaSigma = (hi - lo) * 0.1;
        Assert.True(gaSigma < (hi - lo) / 4.0,
            "A GA mutation step must be small relative to the live band, or the threshold is untunable.");

        // What "off" costs in the widened-bounds alternative, for the record: the live band
        // would be a small minority of the range, i.e. a large flat plateau of equivalent
        // "off" values that the sampler would spend most of its budget in.
        double widenedRange = DynamicGuardGenotype.DdGateDisabled - lo;
        Assert.True((hi - lo) / widenedRange < 0.2);
    }

    [Fact]
    public void ClampingEveryGeneIntoBounds_PreservesGateOff()
    {
        // DynamicGuardGA clamps every mutated gene into Bounds. If "off" is not representable
        // inside Bounds, that clamp silently switches the gate on for every child.
        var genes = WithDdGate(DynamicGuardGenotype.DdGateDisabled).ToGenes();
        for (int d = 0; d < genes.Length; d++)
            genes[d] = Math.Clamp(genes[d],
                DynamicGuardGenotype.Bounds[d, 0], DynamicGuardGenotype.Bounds[d, 1]);

        Assert.Equal(DynamicGuardGenotype.DdGateDisabled,
                     DynamicGuardGenotype.FromGenes(genes).DdEntryGatePct, 10);
    }

    [Fact]
    public void EnableGene_IsBinaryAndBoundedZeroToOne()
    {
        Assert.Equal(0.0, DynamicGuardGenotype.Bounds[DdGateEnableGene, 0], 10);
        Assert.Equal(1.0, DynamicGuardGenotype.Bounds[DdGateEnableGene, 1], 10);
        // Canonical emitted values must sit either side of the decision threshold, and away
        // from the bounds edges so a Gaussian/KDE step can still cross the boundary.
        Assert.True(DynamicGuardGenotype.DdGateEnabledGene  > DynamicGuardGenotype.DdGateEnableThreshold);
        Assert.True(DynamicGuardGenotype.DdGateDisabledGene < DynamicGuardGenotype.DdGateEnableThreshold);
        Assert.InRange(DynamicGuardGenotype.DdGateEnabledGene,  0.0, 1.0);
        Assert.InRange(DynamicGuardGenotype.DdGateDisabledGene, 0.0, 1.0);
    }

    [Fact]
    public void EnableGene_FlipsBothWaysUnderMutation()
    {
        // A switch the mutation operator can never flip is no better than a missing gene:
        // the population locks into whatever the initial draw favoured.
        var rng = new Random(99);
        bool toOn = false, toOff = false;
        for (int i = 0; i < 2000; i++)
        {
            if (DynamicGuardGA.MutateGene(DdGateEnableGene, DynamicGuardGenotype.DdGateDisabledGene, rng)
                >= DynamicGuardGenotype.DdGateEnableThreshold) toOn = true;
            if (DynamicGuardGA.MutateGene(DdGateEnableGene, DynamicGuardGenotype.DdGateEnabledGene, rng)
                < DynamicGuardGenotype.DdGateEnableThreshold) toOff = true;
        }
        Assert.True(toOn,  "Mutation never turns the DD gate on.");
        Assert.True(toOff, "Mutation never turns the DD gate off.");
    }

    [Fact]
    public void GaBreedingChain_RevisitsBothGateStates_EvenFromACollapsedPopulation()
    {
        // Worst case for reachability: the population has collapsed onto a single gate-OFF
        // individual. Reproduces DynamicGuardGA.Run's inner loop exactly — ToGenes, MutateGene
        // on every gene, FromGenes — and asserts the lineage still visits both states. If it
        // does not, the "off" choice is absorbing and the GA never re-tests the gate.
        var rng   = new Random(7);
        int nDim  = DynamicGuardGenotype.Bounds.GetLength(0);
        var child = WithDdGate(DynamicGuardGenotype.DdGateDisabled);
        int live = 0, off = 0, flips = 0;
        bool wasLive = child.DdGateIsLive;

        for (int gen = 0; gen < 500; gen++)
        {
            var genes = child.ToGenes();
            for (int d = 0; d < nDim; d++)
                genes[d] = DynamicGuardGA.MutateGene(d, genes[d], rng);
            child = DynamicGuardGenotype.FromGenes(genes);

            if (child.DdGateIsLive) live++; else off++;
            if (child.DdGateIsLive != wasLive) flips++;
            wasLive = child.DdGateIsLive;
        }

        Assert.True(off  > 0, "The DD gate never turns off along a breeding chain.");
        Assert.True(live > 0, "The DD gate never turns on along a breeding chain — a population "
                            + "that starts off can never re-test it.");
        // Not just one lucky flip: the switch must mix, so both states get repeated evaluation.
        Assert.True(flips > 20, $"Only {flips} state changes in 500 generations — the switch barely mixes.");
        // And a live threshold must remain tunable while the gate is on.
        Assert.InRange(child.DdEntryGatePct,
            child.DdGateIsLive ? DynamicGuardGenotype.Bounds[DdGateGene, 0] : DynamicGuardGenotype.DdGateDisabled,
            child.DdGateIsLive ? DynamicGuardGenotype.Bounds[DdGateGene, 1] : DynamicGuardGenotype.DdGateDisabled);
    }

    [Theory]
    [InlineData(true)]   // an objective that prefers the gate ON
    [InlineData(false)]  // an objective that prefers the gate OFF
    public void BayesianRefinement_CanSelectEitherGateState(bool rewardLive)
    {
        // The GA hands off to BayesianOptimizer.Refine (DynamicGuardGA.Run), which samples and
        // perturbs inside Bounds. Drive the real TPE loop with a synthetic objective that pays
        // only for one gate state and check it lands there — evidence the switch gene survives
        // the handoff instead of being frozen by the sampler's bandwidth clamp.
        var rng  = new Random(2024);
        int nDim = DynamicGuardGenotype.Bounds.GetLength(0);

        double Objective(double[] genes) =>
            DynamicGuardGenotype.FromGenes(genes).DdGateIsLive == rewardLive ? 1.0 : 0.0;

        var seed = Enumerable.Range(0, 12)
            .Select(_ => DynamicGuardGA.RandomGenes(nDim, rng))
            .Select(g => (g, Objective(g)))
            .ToList();
        Assert.Contains(seed, s => s.Item2 == 1.0);   // sanity: the seed can see the rewarded state
        Assert.Contains(seed, s => s.Item2 == 0.0);

        var history = BayesianOptimizer.Refine(seed, DynamicGuardGenotype.Bounds, Objective, 40, rng);
        var best    = DynamicGuardGenotype.FromGenes(history.OrderByDescending(h => h.Fitness).First().Params);

        Assert.Equal(rewardLive, best.DdGateIsLive);
        Assert.Equal(1.0, history.Max(h => h.Fitness), 10);
        // Both states stay represented in the search, so neither is unreachable after the handoff.
        Assert.Contains(history, h => DynamicGuardGenotype.FromGenes(h.Params).DdGateIsLive);
        Assert.Contains(history, h => !DynamicGuardGenotype.FromGenes(h.Params).DdGateIsLive);
    }

    [Fact]
    public void MutatedGenes_StayInsideBounds()
    {
        var rng  = new Random(5);
        int nDim = DynamicGuardGenotype.Bounds.GetLength(0);
        for (int d = 0; d < nDim; d++)
        {
            double lo = DynamicGuardGenotype.Bounds[d, 0], hi = DynamicGuardGenotype.Bounds[d, 1];
            double v  = (lo + hi) / 2.0;
            for (int i = 0; i < 300; i++)
            {
                v = DynamicGuardGA.MutateGene(d, v, rng);
                Assert.InRange(v, lo, hi);
            }
        }
    }

    // ── Defect 3: round-trip identity and constructor invariant ───────────────────────

    // ── The 16-genes / 15-fields arity contract ───────────────────────────────────────
    //
    // Bounds carries one row per SEARCH gene (16); the record carries one property per VALUE
    // field (15). The DD gate is the single field driven by two genes. That mismatch is
    // deliberate but it is a trap, so it is pinned from three directions at once here, and
    // additionally asserted at type-load by DynamicGuardGenotype's static constructor.

    [Fact]
    public void GeneLayout_BoundsRows_ToGenesArity_AndGeneCount_AllAgree()
    {
        int n = DynamicGuardGenotype.GeneCount;
        Assert.Equal(16, n);                                        // 15 record fields + the DD-gate switch
        Assert.Equal(n, DynamicGuardGenotype.Bounds.GetLength(0));
        Assert.Equal(2, DynamicGuardGenotype.Bounds.GetLength(1));  // [min, max] per gene
        Assert.Equal(n, WithDdGate(0.07).ToGenes().Length);
        Assert.Equal(n, WithDdGate(DynamicGuardGenotype.DdGateDisabled).ToGenes().Length);

        // The two DD-gate genes must be distinct, in range, and the switch must be the extra
        // gene past the 15 value fields.
        Assert.Equal(9,     DdGateGene);
        Assert.Equal(15,    DdGateEnableGene);
        Assert.NotEqual(DdGateGene, DdGateEnableGene);
        Assert.InRange(DdGateGene,       0, n - 1);
        Assert.InRange(DdGateEnableGene, 0, n - 1);

        // Every bound must be a non-degenerate interval, or the corresponding gene is frozen.
        for (int d = 0; d < n; d++)
            Assert.True(DynamicGuardGenotype.Bounds[d, 1] > DynamicGuardGenotype.Bounds[d, 0],
                $"Bounds[{d}] is empty or inverted — gene {d} could never be searched.");
    }

    // A representative genotype for each state, with every field distinct (and inside its own
    // bound) so a mis-ordered ToGenes/FromGenes pair cannot round-trip by accident.
    // Indexed rather than passed by value so xUnit can serialise the theory cases.
    private static readonly DynamicGuardGenotype[] Representatives =
    [
        new(24, 1.8,  12, -0.05, 0.40, 3.0, 12, 0.05, 2.5, 0.07,                                // gate LIVE, mid-band
            0.04, 0.22, 0.18, 0.06, 0.55),
        new(6,  1.0,   2, -0.15, 0.00, 1.5,  0, 0.00, 1.5, 0.02,                                // gate LIVE at the tight edge
            0.03, 0.10, 0.05, 0.01, 0.20),
        new(96, 3.5,  48, -0.01, 0.80, 5.0, 48, 0.30, 5.0, 0.15,                                // gate LIVE at the loose edge
            0.15, 0.40, 0.50, 0.20, 1.00),
        new(31, 2.2,  19, -0.08, 0.33, 4.1, 27, 0.11, 3.3, DynamicGuardGenotype.DdGateDisabled, // gate OFF (sentinel)
            0.07, 0.19, 0.29, 0.13, 0.71),
        new(31, 2.2,  19, -0.08, 0.33, 4.1, 27, 0.11, 3.3, 6.128,                               // gate OFF (coerced legacy)
            0.07, 0.19, 0.29, 0.13, 0.71),
    ];

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ToGenes_FromGenes_RoundTripsTheWholeRecord(int idx)
    {
        var g = Representatives[idx];
        // DynamicGuardGA does exactly this round-trip on every parent→child copy, and
        // BayesianOptimizer seeds from ToGenes(). If it is not the identity, individuals
        // mutate silently between generations. Record equality covers all 15 fields, so a
        // swapped pair of genes anywhere in the ordering fails here.
        var genes = g.ToGenes();
        Assert.Equal(DynamicGuardGenotype.GeneCount, genes.Length);

        var back = DynamicGuardGenotype.FromGenes(genes);
        Assert.Equal(g with { Fitness = 0 }, back with { Fitness = 0 });
        Assert.Equal(g.DdEntryGatePct, back.DdEntryGatePct, 10);
        Assert.Equal(g.DdGateIsLive,   back.DdGateIsLive);
        Assert.Equal(genes, back.ToGenes());                 // and the round-trip is idempotent

        // Fitness rides alongside the genes rather than inside them.
        Assert.Equal(42.5, DynamicGuardGenotype.FromGenes(genes, 42.5).Fitness, 10);

        // Every gene must land inside its own bound, or the GA's clamp would silently move it.
        for (int d = 0; d < genes.Length; d++)
            Assert.InRange(genes[d], DynamicGuardGenotype.Bounds[d, 0], DynamicGuardGenotype.Bounds[d, 1]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void FromGenes_ReadsEachGeneIntoItsOwnField(int idx)
    {
        // Positional check of the full ordering: gene d, and only gene d, drives field d.
        var genes = Representatives[idx].ToGenes();
        var back  = DynamicGuardGenotype.FromGenes(genes);
        Assert.Equal(genes[0],  back.AtrLookback,            10);
        Assert.Equal(genes[1],  back.AtrTrigger,             10);
        Assert.Equal(genes[2],  back.MomLookback,            10);
        Assert.Equal(genes[3],  back.MomThreshold,           10);
        Assert.Equal(genes[4],  back.SizeFloor,              10);
        Assert.Equal(genes[5],  back.PanicTrigger,           10);
        Assert.Equal(genes[6],  back.RecoveryBars,           10);
        Assert.Equal(genes[7],  back.BullMomBypass,          10);
        Assert.Equal(genes[8],  back.EntryAtrGate,           10);
        Assert.Equal(genes[10], back.ConfLossCapMin,         10);
        Assert.Equal(genes[11], back.ConfLossCapMax,         10);
        Assert.Equal(genes[12], back.ProfitProtectThreshold, 10);
        Assert.Equal(genes[13], back.ProfitProtectDrawback,  10);
        Assert.Equal(genes[14], back.ProfitProtectFactor,    10);
        // Genes 9 and 15 fold into the one effective field.
        Assert.Equal(genes[DdGateEnableGene] >= DynamicGuardGenotype.DdGateEnableThreshold,
                     back.DdGateIsLive);
        Assert.Equal(back.DdGateIsLive ? genes[DdGateGene] : DynamicGuardGenotype.DdGateDisabled,
                     back.DdEntryGatePct, 10);
    }

    [Fact]
    public void PrimaryConstructor_EnforcesTheGateInvariant()
    {
        // A DynamicGuardGenotype cannot hold an out-of-range DdEntryGatePct, however it was
        // constructed — otherwise a raw value reaches Simulator.SimulatePortfolioExposureCapped.
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled, WithDdGate(6.128).DdEntryGatePct, 10);
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled, WithDdGate(0.5).DdEntryGatePct, 10);
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled, WithDdGate(-1.0).DdEntryGatePct, 10);
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled, WithDdGate(0.0).DdEntryGatePct, 10);
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled, WithDdGate(double.NaN).DdEntryGatePct, 10);
        Assert.Equal(0.07, WithDdGate(0.07).DdEntryGatePct, 10);
    }

    [Fact]
    public void WithExpression_EnforcesTheGateInvariant()
    {
        var g = WithDdGate(0.07) with { DdEntryGatePct = 6.128 };
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled, g.DdEntryGatePct, 10);
        Assert.Equal(0.09, (WithDdGate(0.07) with { DdEntryGatePct = 0.09 }).DdEntryGatePct, 10);
        // Unrelated `with` edits must not disturb a valid gate.
        Assert.Equal(0.07, (WithDdGate(0.07) with { Fitness = 12.0 }).DdEntryGatePct, 10);
    }

    [Fact]
    public void GeneIndex9_IsDdEntryGate_InEveryOrdering()
    {
        // An off-by-one here would rescale a completely different gene.
        var g = WithDdGate(0.077);
        Assert.Equal(0.077, g.ToGenes()[DdGateGene], 10);

        var genes = g.ToGenes();
        genes[DdGateGene] = 0.099;
        Assert.Equal(0.099, DynamicGuardGenotype.FromGenes(genes).DdEntryGatePct, 10);

        // Bounds carries one row per SEARCH gene: the 15 genotype fields plus the DD-gate switch.
        // Asserted against GeneCount rather than a hard-coded literal — a literal in three
        // places is what broke when the switch gene was added.
        Assert.Equal(DynamicGuardGenotype.GeneCount, DynamicGuardGenotype.Bounds.GetLength(0));
        Assert.Equal(DynamicGuardGenotype.GeneCount, g.ToGenes().Length);
        Assert.Equal(DynamicGuardGenotype.GeneCount - 1, DdGateEnableGene);

        // The threshold gene's row really is the DD-gate row in Bounds, not a neighbour's.
        Assert.Equal(0.02, DynamicGuardGenotype.Bounds[DdGateGene, 0], 10);
        Assert.Equal(0.15, DynamicGuardGenotype.Bounds[DdGateGene, 1], 10);
    }

    [Fact]
    public void GateOff_CanonicalisesToTheLoosestThreshold_NotTheHarshest()
    {
        // Flipping the switch back on must be the mildest possible perturbation. If an off
        // individual reported 0.02, every "try the gate on" mutation would land on the
        // near-permanently-blocking end and the search would reject "on" for the wrong reason.
        var genes = WithDdGate(DynamicGuardGenotype.DdGateDisabled).ToGenes();
        Assert.Equal(DynamicGuardGenotype.Bounds[DdGateGene, 1], genes[DdGateGene], 10);

        genes[DdGateEnableGene] = DynamicGuardGenotype.DdGateEnabledGene;
        Assert.Equal(DynamicGuardGenotype.Bounds[DdGateGene, 1],
                     DynamicGuardGenotype.FromGenes(genes).DdEntryGatePct, 10);
    }

    [Fact]
    public void FromGenes_SwitchGeneOverridesAnOtherwiseValidThreshold()
    {
        var genes = WithDdGate(0.07).ToGenes();
        Assert.Equal(0.07, DynamicGuardGenotype.FromGenes(genes).DdEntryGatePct, 10);

        genes[DdGateEnableGene] = DynamicGuardGenotype.DdGateDisabledGene;
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled,
                     DynamicGuardGenotype.FromGenes(genes).DdEntryGatePct, 10);
    }

    [Fact]
    public void FromGenes_LegacyFifteenGeneVector_ReadsThresholdAlone()
    {
        // Pre-switch gene vectors carry no gene 15; the threshold decides, and an unusable
        // threshold still coerces off.
        double[] live = [10, 2, 12, -0.1, 0.2, 3, 8, 0.1, 4, 0.06, 0.03, 0.1, 1.0, 0.1, 1.0];
        Assert.Equal(0.06, DynamicGuardGenotype.FromGenes(live).DdEntryGatePct, 10);

        double[] stale = [10, 2, 12, -0.1, 0.2, 3, 8, 0.1, 4, 6.128, 0.03, 0.1, 1.0, 0.1, 1.0];
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled,
                     DynamicGuardGenotype.FromGenes(stale).DdEntryGatePct, 10);
    }

    [Fact]
    public void DisabledSentinel_IsOneHundredPercentDrawdown()
    {
        Assert.Equal(1.0, DynamicGuardGenotype.DdGateDisabled, 10);
    }

    [Fact]
    public void FromGenes_ShortArray_DefaultsToDisabled()
    {
        var g = DynamicGuardGenotype.FromGenes([10, 2, 12, -0.1, 0.2, 3, 8, 0.1, 4]);
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled, g.DdEntryGatePct, 10);
    }

    // THE CLAMP POLICY (mirrors the comment on DynamicGuardGenotype.ClampDdEntryGate):
    // a DdEntryGatePct is kept verbatim only if it already lies inside the live band
    // [0.02, 0.15]; every other value — NaN, infinity, negative, zero, just under the band,
    // just over it, between the band and the sentinel, or a legacy percent-scale number —
    // coerces the gate OFF, and only the exact sentinel does so without being flagged coerced.
    //
    // These two cases used to assert edge-clamping (0.005 → 0.02, 0.90 → 0.15). They now
    // coerce off, for the same reason 6.128 does: under the two-gene design the GA expresses
    // "off" with the switch gene and only ever emits in-band thresholds at gene 9, so the only
    // way an out-of-band number reaches here is a stale file or a hand-written value — neither
    // of which carries any evidence about where the threshold belongs. Snapping 0.005 up to
    // 0.02 would switch on a never-validated long-entry blocker at its harshest setting
    // ("block DipLong/SwingLong whenever the book is 2% off its peak"), which is close to
    // "block them permanently"; coercing off is the only choice that changes no backtest number.
    [Theory]
    [InlineData(0.005)]   // just below the lower bound
    [InlineData(0.019)]   // a hair below the lower bound
    [InlineData(0.151)]   // a hair above the upper bound
    [InlineData(0.50)]    // between the band and the sentinel
    [InlineData(0.90)]    // just below the sentinel
    [InlineData(0.0)]     // "zero drawdown tolerated" — would block every long, forever
    [InlineData(-1.0)]    // negative: same, and not a drawdown at all
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ClampDdEntryGate_CoercesEveryOutOfBandValueOff(double raw)
    {
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled,
                     DynamicGuardGenotype.ClampDdEntryGate(raw, out bool coerced), 10);
        Assert.True(coerced, $"{raw} is outside the live band and must be reported as coerced.");
    }

    [Theory]
    [InlineData(0.02)]    // lower bound, inclusive
    [InlineData(0.06)]
    [InlineData(0.15)]    // upper bound, inclusive
    public void ClampDdEntryGate_KeepsEveryInBandValueVerbatim(double raw)
    {
        Assert.Equal(raw, DynamicGuardGenotype.ClampDdEntryGate(raw, out bool coerced), 10);
        Assert.False(coerced);
    }

    [Theory]
    [InlineData(6.128278240331621)]  // the value currently in genotypes/dynamic_guard_genotype.json
    [InlineData(15.0)]               // the old "disabled" default
    [InlineData(2.0)]                // the old lower bound
    public void ClampDdEntryGate_CoercesLegacyPercentScaleValuesToDisabled(double legacy)
    {
        // A gene selected while the gate was inert carries no evidence about where the
        // threshold belongs, so it is turned off rather than rescaled. Retrain required.
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled,
                     DynamicGuardGenotype.ClampDdEntryGate(legacy, out bool coerced), 10);
        Assert.True(coerced);
    }

    [Fact]
    public void ClampDdEntryGate_DisabledSentinel_IsNotFlaggedAsCoerced()
    {
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled,
                     DynamicGuardGenotype.ClampDdEntryGate(DynamicGuardGenotype.DdGateDisabled, out bool coerced), 10);
        Assert.False(coerced);
    }

    [Fact]
    public void Dto_LoadingStaleGenotype_YieldsSafeValue_NotSixPointOne()
    {
        // Mirrors deserializing the on-disk genotypes/dynamic_guard_genotype.json.
        var dto = new DynamicGuardGenotypeDto(
            AtrLookback: 10.893102237644037, AtrTrigger: 2.7143289968358144,
            MomLookback: 14.964250575421412, MomThreshold: -0.14231040883717525,
            SizeFloor: 0.15643331697329446, Fitness: 163.70335438082552,
            PanicTrigger: 4.99362855661092, RecoveryBars: 48, BullMomBypass: 0.259109737486338,
            EntryAtrGate: 4.892908329940958, DdEntryGatePct: 6.128278240331621,
            ConfLossCapMin: 0.03, ConfLossCapMax: 0.1,
            ProfitProtectThreshold: 0.19506004831112342,
            ProfitProtectDrawback: 0.07696920762600826,
            ProfitProtectFactor: 0.5374174420194605);

        var geno = dto.ToGenotype();
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled, geno.DdEntryGatePct, 10);
        Assert.False(geno.DdGateIsLive);
        // Other genes must pass through untouched.
        Assert.Equal(4.892908329940958, geno.EntryAtrGate, 10);
        Assert.Equal(0.5374174420194605, geno.ProfitProtectFactor, 10);
    }

    // Silent coercion would be worse than the bug: the shipped genotype file still carries the
    // stale percent-scale 6.128, and the operator has to learn that the guard needs retraining.
    // (Console.Error is only written by this class's own DTO tests, and xUnit runs the tests
    //  inside one class sequentially, so the capture cannot be polluted by another test.)
    [Fact]
    public void Dto_LoadingStaleGenotype_WarnsOnStderr_AndNamesTheRetrainCommand()
    {
        var original = Console.Error;
        var buffer   = new StringWriter();
        string output;
        try
        {
            Console.SetError(buffer);
            var geno = new DynamicGuardGenotypeDto(10, 2, 12, -0.1, 0.2, 0, DdEntryGatePct: 6.128278240331621)
                .ToGenotype();
            Assert.Equal(DynamicGuardGenotype.DdGateDisabled, geno.DdEntryGatePct, 10);
            output = buffer.ToString();
        }
        finally { Console.SetError(original); }

        Assert.Contains("[DynamicGuard]", output);
        Assert.Contains("6.128", output);
        Assert.Contains("dynamicguardtrain", output);
    }

    [Theory]
    [InlineData(DynamicGuardGenotype.DdGateDisabled)]  // the explicit "off" sentinel
    [InlineData(0.07)]                                 // a legitimate in-band threshold
    public void Dto_LegitimateValues_LoadSilently(double ddGate)
    {
        var original = Console.Error;
        var buffer   = new StringWriter();
        string output;
        try
        {
            Console.SetError(buffer);
            var geno = new DynamicGuardGenotypeDto(10, 2, 12, -0.1, 0.2, 0, DdEntryGatePct: ddGate).ToGenotype();
            Assert.Equal(ddGate, geno.DdEntryGatePct, 10);
            output = buffer.ToString();
        }
        finally { Console.SetError(original); }

        Assert.DoesNotContain("[DynamicGuard]", output);
    }

    [Fact]
    public void Dto_DefaultDdGate_IsDisabledFraction()
    {
        var geno = new DynamicGuardGenotypeDto(10, 2, 12, -0.1, 0.2, 0).ToGenotype();
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled, geno.DdEntryGatePct, 10);
    }

    [Fact]
    public void ToString_RendersFractionSensibly_NotSixHundredPercent()
    {
        var enabled = DynamicGuardGenotype.FromGenes(
            [10, 2, 12, -0.1, 0.2, 3, 8, 0.1, 4, 0.06, 0.03, 0.1, 1.0, 0.1, 1.0]);
        // Renders as a percentage of a fraction (≈6.0%), never the old "613%".
        Assert.Contains($"DdGate={0.06.ToString("P1")}", enabled.ToString());
        Assert.DoesNotContain("613", enabled.ToString());

        var disabled = DynamicGuardGenotype.FromGenes(
            [10, 2, 12, -0.1, 0.2, 3, 8, 0.1, 4, DynamicGuardGenotype.DdGateDisabled, 0.03, 0.1, 1.0, 0.1, 1.0]);
        Assert.Contains("DdGate=off", disabled.ToString());
    }
}

// The guard's objective used to be `(valCalmar + oosCalmar) / 2` — it was selected on the
// validation window AND on the never-trained OOS coins. Routing the split boundary through
// DataSplit fixed which bars were CALLED validation while the GA kept optimising against them.
public class GuardObjectiveIsTrainOnlyTests
{
    private static readonly System.DateTime T0 = new(2024, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);

    private static Candle[] FlatBtc(int n)
    {
        var c = new Candle[n];
        for (int i = 0; i < n; i++)
            c[i] = new Candle(T0.AddHours(i), 100, 101, 99, 100, 1000);
        return c;
    }

    private static System.Collections.Generic.List<(System.DateTime, double, double, System.TimeSpan, string)>
        Book(int n, double ret) =>
        System.Linq.Enumerable.Range(0, n)
            .Select(i => (T0.AddHours(i * 8), ret, 0.05, System.TimeSpan.FromHours(48), "grid"))
            .ToList();

    [Fact]
    public void Evaluate_ScoresOnlyTheListItIsGiven()
    {
        // A winning book and a losing book must not produce the same fitness. If Evaluate still
        // averaged a second, hidden list, the two would be dragged toward each other.
        var btc  = FlatBtc(900);
        var g    = DynamicGuardGenotype.FromGenes(
            System.Linq.Enumerable.Range(0, DynamicGuardGenotype.Bounds.GetLength(0))
                .Select(i => (DynamicGuardGenotype.Bounds[i, 0] + DynamicGuardGenotype.Bounds[i, 1]) / 2.0).ToArray());

        double win  = DynamicGuardGA.Evaluate(g, btc, Book(100, +2.0));
        double lose = DynamicGuardGA.Evaluate(g, btc, Book(100, -2.0));

        Assert.True(win > lose, $"winning book scored {win:F2}, losing book {lose:F2} — the objective is not reading its input");
        Assert.True(lose < 0, "a book that only loses cannot have positive Calmar");
    }

    [Fact]
    public void Evaluate_TakesExactlyOneTradeList()
    {
        // Compile-time guard. Two lists is how val and OOS both got into the objective; the fix is
        // that a caller holding a val list now has to make a visible decision instead of passing
        // it as a second argument. If this ever compiles with two lists again, the leak is back.
        var m = typeof(DynamicGuardGA).GetMethod("Evaluate",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(m);
        Assert.Equal(3, m!.GetParameters().Length);   // genotype, btcH1, trainTrades
    }
}

// The aggregate-Calmar objective rewarded switching the guard OFF: cutting exposure costs return
// immediately, while max-drawdown is a single-point statistic that contributes the same number
// whether or not the guard clipped the event. The segmented CVaR objective must not have that
// property.
public class GuardFitnessRewardsProtectionTests
{
    private static readonly System.DateTime T0 = new(2024, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);

    private static Candle[] FlatBtc(int n)
    {
        var c = new Candle[n];
        for (int i = 0; i < n; i++)
            c[i] = new Candle(T0.AddHours(i), 100, 101, 99, 100, 1000);
        return c;
    }

    [Fact]
    public void OneRuinedSegment_DragsFitnessBelowASteadyBook()
    {
        // Same total trade count. One book is uniformly decent; the other is decent for 90% of its
        // life and catastrophic in one contiguous slice — precisely the shape a guard exists for.
        // Under a single aggregate Calmar the second could score comparably, because its one bad
        // event sets max-DD once and its good segments carry the numerator.
        var btc = FlatBtc(3000);
        var g   = DynamicGuardGenotype.FromGenes(
            System.Linq.Enumerable.Range(0, DynamicGuardGenotype.Bounds.GetLength(0))
                .Select(i => (DynamicGuardGenotype.Bounds[i, 0] + DynamicGuardGenotype.Bounds[i, 1]) / 2.0).ToArray());

        var steady = new System.Collections.Generic.List<(System.DateTime, double, double, System.TimeSpan, string)>();
        var spiked = new System.Collections.Generic.List<(System.DateTime, double, double, System.TimeSpan, string)>();
        for (int i = 0; i < 400; i++)
        {
            var t = T0.AddHours(i * 4);
            steady.Add((t, +1.5, 0.05, System.TimeSpan.FromHours(48), "grid"));
            // Trades 200-259 are a sustained wipeout inside one contiguous segment.
            spiked.Add((t, i is >= 200 and < 260 ? -25.0 : +1.5, 0.05, System.TimeSpan.FromHours(48), "grid"));
        }

        double fSteady = DynamicGuardGA.Evaluate(g, btc, steady);
        double fSpiked = DynamicGuardGA.Evaluate(g, btc, spiked);

        Assert.True(fSpiked < fSteady,
            $"a book with one ruined segment ({fSpiked:F1}) must score below a steady one ({fSteady:F1}) — "
          + "the tail term is not reaching the objective");
    }

    [Fact]
    public void FitnessIsWeightedTowardTheWorstSegments()
    {
        // lambda on the CVaR term must be the majority, otherwise "protects on average" beats
        // "protects when it matters" and the guard drifts back to idle.
        Assert.True(DynamicGuardGA.FitnessLambda > 0.5);
        Assert.InRange(DynamicGuardGA.FitnessCVaRAlpha, 0.1, 0.6);
        Assert.True(DynamicGuardGA.FitnessSegments >= 5, "too few segments and the tail term has no resolution");
    }
}
