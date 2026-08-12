using System.Collections.Generic;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Strategy labels are plain strings compared with ==, so a label that exists in one place and not
// another fails SILENTLY — the branch never fires and nothing distinguishes that from the branch
// legitimately not applying.
//
// This really happened: Simulator tested `strategy is "fade_long"` while every backtest labels
// those trades "fadelong", so the profit-protection branch was dead for FadeLong for as long as
// FadeLong has been enabled. Nothing failed, because a string literal that matches nothing is
// indistinguishable from a strategy that simply never qualifies.
//
// PortfolioReplay's direction sets are the authoritative label registry (they are what the
// concurrency caps and directional caps key off). Every other place that enumerates labels must
// agree with it.
public class StrategyLabelConsistencyTests
{
    public static IEnumerable<object[]> LabelSets() => new[]
    {
        new object[] { nameof(Simulator.DdGatedLongs),     Simulator.DdGatedLongs },
        new object[] { nameof(Simulator.ProtectableLongs), Simulator.ProtectableLongs },
    };

    [Theory]
    [MemberData(nameof(LabelSets))]
    public void EveryLabelSimulatorTreatsAsLong_IsAKnownLongLabel(string setName, HashSet<string> labels)
    {
        Assert.NotEmpty(labels);
        foreach (var label in labels)
        {
            // IsLong returns null for an UNKNOWN label, which is exactly what a typo produces —
            // so this distinguishes "misspelled" from "genuinely a short strategy".
            var direction = PortfolioReplay.IsLong(label);
            Assert.True(direction.HasValue,
                $"{setName} contains '{label}', which PortfolioReplay has never heard of. Either it " +
                $"is a typo (the branch guarded by this set is dead), or a new strategy was added " +
                $"without registering it in PortfolioReplay.");
            Assert.True(direction!.Value,
                $"{setName} contains '{label}', but PortfolioReplay classifies it as a SHORT strategy.");
        }
    }

    [Fact]
    public void EveryLabelSimulatorTreatsAsLong_HasAConcurrencyCap()
    {
        // A label missing from DefaultCaps falls back to int.MaxValue — uncapped concurrency, with
        // only a one-line warning at runtime.
        foreach (var label in Simulator.ProtectableLongs)
            Assert.True(PortfolioReplay.DefaultCaps.ContainsKey(label),
                $"'{label}' has no entry in PortfolioReplay.DefaultCaps — its concurrency cap would " +
                $"silently fall back to int.MaxValue.");
    }
}
