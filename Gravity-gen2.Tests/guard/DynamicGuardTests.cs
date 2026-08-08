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

    [Fact]
    public void Bounds_AreFractions_NotPercentages()
    {
        double lo = DynamicGuardGenotype.Bounds[DdGateGene, 0];
        double hi = DynamicGuardGenotype.Bounds[DdGateGene, 1];
        Assert.Equal(0.02, lo, 10);
        Assert.Equal(0.15, hi, 10);
        // Anything the GA can draw must be a reachable drawdown, i.e. strictly below 100%.
        Assert.True(hi < DynamicGuardGenotype.DdGateDisabled,
            $"Upper bound {hi} must be below the disabled sentinel — otherwise the gate is inert.");
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

    [Fact]
    public void ClampDdEntryGate_KeepsInRangeValues()
    {
        Assert.Equal(0.06, DynamicGuardGenotype.ClampDdEntryGate(0.06, out bool coerced), 10);
        Assert.False(coerced);
    }

    [Theory]
    [InlineData(0.005, 0.02)]   // below lower bound → clamped up
    [InlineData(0.90,  0.15)]   // above upper bound but below sentinel → clamped down
    public void ClampDdEntryGate_ClampsOutOfRangeFractions(double raw, double expected)
    {
        Assert.Equal(expected, DynamicGuardGenotype.ClampDdEntryGate(raw, out bool coerced), 10);
        Assert.True(coerced);
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
        // Other genes must pass through untouched.
        Assert.Equal(4.892908329940958, geno.EntryAtrGate, 10);
        Assert.Equal(0.5374174420194605, geno.ProfitProtectFactor, 10);
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
