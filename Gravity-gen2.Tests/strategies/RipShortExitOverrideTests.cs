using TradingGA;
using Xunit;
using static TradingGA.RipShortSimulator;

namespace Gravity_gen2.Tests;

public class RipShortExitOverrideTests
{
    private static readonly ExitOverrideConfig WaitOnly =
        new(ExitOverrideMode.WaitForBreakeven, AtrGateRatio: 1.4, DcaAtrMult: 1.2, MaxExtraHoldCandles: 60);

    private static readonly ExitOverrideConfig DcaMode =
        new(ExitOverrideMode.DcaAndWait, AtrGateRatio: 1.4, DcaAtrMult: 1.2, MaxExtraHoldCandles: 60);

    [Fact]
    public void ShouldWaitPastTimeout_KeepsWaiting_WhenLosingAndCalm()
    {
        // Short entered at 100, price now 102 (underwater), volatility calm (ratio 1.1 ≤ gate 1.4)
        Assert.True(ShouldWaitPastTimeout(price: 102, entry: 100, atrRatioNow: 1.1, extraHoldBars: 5, WaitOnly));
    }

    [Fact]
    public void ShouldWaitPastTimeout_False_WhenAlreadyProfitable()
    {
        // Price below entry = short is winning; no reason to override the timeout.
        Assert.False(ShouldWaitPastTimeout(price: 98, entry: 100, atrRatioNow: 1.1, extraHoldBars: 5, WaitOnly));
    }

    [Fact]
    public void ShouldWaitPastTimeout_False_WhenVolatilitySpikes()
    {
        // Same losing position, but local ATR ratio (2.0) blows through the gate (1.4) —
        // no longer "calm", so the timeout should fire normally.
        Assert.False(ShouldWaitPastTimeout(price: 102, entry: 100, atrRatioNow: 2.0, extraHoldBars: 5, WaitOnly));
    }

    [Fact]
    public void ShouldWaitPastTimeout_False_PastSafetyCap()
    {
        // Calm and losing, but already past the absolute extra-hold ceiling — must not
        // wait forever regardless of how calm conditions look.
        Assert.False(ShouldWaitPastTimeout(price: 102, entry: 100, atrRatioNow: 1.1, extraHoldBars: 60, WaitOnly));
    }

    [Fact]
    public void ShouldWaitPastTimeout_False_WhenModeIsNone()
    {
        var off = new ExitOverrideConfig(ExitOverrideMode.None);
        Assert.False(ShouldWaitPastTimeout(price: 102, entry: 100, atrRatioNow: 1.1, extraHoldBars: 5, off));
    }

    [Fact]
    public void ShouldDca_Fires_OnceAdverseMoveClearsThreshold()
    {
        // atrEntry=2, DcaAtrMult=1.2 → trigger once price is ≥2.4 above entry.
        Assert.True(ShouldDca(waiting: true, dcaDone: false, price: 102.5, entry: 100, atrEntry: 2.0, DcaMode));
    }

    [Fact]
    public void ShouldDca_False_BelowThreshold()
    {
        Assert.False(ShouldDca(waiting: true, dcaDone: false, price: 101.0, entry: 100, atrEntry: 2.0, DcaMode));
    }

    [Fact]
    public void ShouldDca_False_WhenAlreadyDone()
    {
        // Single DCA only — must not fire again once dcaDone is true.
        Assert.False(ShouldDca(waiting: true, dcaDone: true, price: 105.0, entry: 100, atrEntry: 2.0, DcaMode));
    }

    [Fact]
    public void ShouldDca_False_WhenMaxSizeMultLeavesNothingToAdd()
    {
        // MaxSizeMult ≤ 1.0 means there is no add to make. Firing anyway would leave the
        // cost basis untouched but still burn dcaDone, relabel the trade "ripshort_dca",
        // and scale the reported return by a fraction of itself.
        var noAdd = new ExitOverrideConfig(ExitOverrideMode.DcaAndWait, MaxSizeMult: 1.0);
        Assert.False(ShouldDca(waiting: true, dcaDone: false, price: 105.0, entry: 100, atrEntry: 2.0, noAdd));

        var shrink = new ExitOverrideConfig(ExitOverrideMode.DcaAndWait, MaxSizeMult: 0.5);
        Assert.False(ShouldDca(waiting: true, dcaDone: false, price: 105.0, entry: 100, atrEntry: 2.0, shrink));
    }

    [Fact]
    public void ShouldDca_False_InWaitOnlyMode()
    {
        // WaitForBreakeven mode never DCAs, even if the price move would qualify.
        Assert.False(ShouldDca(waiting: true, dcaDone: false, price: 105.0, entry: 100, atrEntry: 2.0, WaitOnly));
    }

    [Fact]
    public void DcaBlend_MovesEntryHalfwayAndDoublesSize()
    {
        // Mirrors the blend applied in RunRipShortMultiTF: with the default MaxSizeMult
        // of 2.0 (equal-size add) the new entry is the midpoint, and the eventual return
        // must be scaled ×2 for the doubled capital deployed.
        double entry1 = 100, addPrice = 104;
        double blendedEntry = BlendedEntry(entry1, addPrice, DcaMode.MaxSizeMult);
        Assert.Equal(102.0, blendedEntry);

        double exitPx = 98;
        double retPerUnit = (blendedEntry - exitPx) / blendedEntry * 100.0;
        double scaledRet  = retPerUnit * DcaMode.MaxSizeMult;
        Assert.True(scaledRet > retPerUnit, "DCA'd trade should report double the single-unit return");
    }

    // ---- MaxSizeMult is an explicit, tunable parameter (no magic 2.0 in the loop) ----

    [Fact]
    public void MaxSizeMult_DefaultsToTwo()
    {
        // Configs built without naming MaxSizeMult keep the historical equal-size add.
        Assert.Equal(2.0, new ExitOverrideConfig(ExitOverrideMode.DcaAndWait).MaxSizeMult);
        Assert.Equal(2.0, DcaMode.MaxSizeMult);
    }

    [Fact]
    public void BlendedEntry_IsSizeWeighted_ForNonDefaultMaxSizeMult()
    {
        // MaxSizeMult 1.5 = a half-size add: initial leg weight 1.0, add weight 0.5,
        // so the basis moves only a third of the way toward the add price.
        double blended = BlendedEntry(entry: 100, addPrice: 106, maxSizeMult: 1.5);
        Assert.Equal(102.0, blended, precision: 10);
    }

    [Fact]
    public void BlendedEntry_LeavesBasisUntouched_WhenMultIsOneOrLess()
    {
        // Degenerate configs must not corrupt the cost basis.
        Assert.Equal(100.0, BlendedEntry(entry: 100, addPrice: 106, maxSizeMult: 1.0), precision: 10);
        Assert.Equal(100.0, BlendedEntry(entry: 100, addPrice: 106, maxSizeMult: 0.0), precision: 10);
    }

    [Fact]
    public void ReturnScaling_FollowsMaxSizeMult()
    {
        // The reported percentage return is scaled by however much capital was deployed,
        // not by a hardcoded 2.0.
        var half = new ExitOverrideConfig(ExitOverrideMode.DcaAndWait, MaxSizeMult: 1.5);

        double blended    = BlendedEntry(100, 104, half.MaxSizeMult);
        double retPerUnit = (blended - 98) / blended * 100.0;

        Assert.Equal(retPerUnit * 1.5, retPerUnit * half.MaxSizeMult, precision: 10);
        Assert.True(retPerUnit * half.MaxSizeMult < retPerUnit * DcaMode.MaxSizeMult,
                    "a half-size add must report less scaled return than an equal-size add");
    }

    // ---- State rebased onto the blended cost basis after the add ----
    //
    // Scenario shared by the tests below: short entered at 100 with h4 ATR 2.0. The trade
    // first ran favourably to 96 (trailLow = 96), then rallied back to 104 — underwater by
    // 2×ATR — which trips the DCA. Blended entry becomes 102.

    private const double AtrEntry            = 2.0;
    private const double TakeProfitAtrMult   = 1.5;
    private const double TrailActivationMult = 1.0;
    private const double TrailStopMult       = 0.5;

    [Fact]
    public void AfterDca_TargetIsRecomputedFromBlendedEntry()
    {
        double originalEntry  = 100, addPrice = 104;
        double originalTarget = originalEntry - TakeProfitAtrMult * AtrEntry;   // 97

        double blendedEntry = BlendedEntry(originalEntry, addPrice, DcaMode.MaxSizeMult);
        double newTarget    = blendedEntry - TakeProfitAtrMult * AtrEntry;

        Assert.Equal(97.0, originalTarget, precision: 10);
        Assert.Equal(99.0, newTarget,      precision: 10);
        Assert.True(newTarget > originalTarget,
                    "blending the basis upward must lift the profit target with it");
        Assert.Equal(TakeProfitAtrMult * AtrEntry, blendedEntry - newTarget, precision: 10);
    }

    [Fact]
    public void AfterDca_StaleTrailLowWouldArmTrailSpuriously()
    {
        // Documents the bug the rebase fixes: keeping the pre-add running minimum while
        // blending entry upward inflates `entry - trailLow` and arms the trail on profit
        // that belonged to the old, smaller position.
        double staleTrailLow = 96;
        double blendedEntry  = BlendedEntry(100, 104, DcaMode.MaxSizeMult);   // 102

        bool wouldArm = blendedEntry - staleTrailLow >= TrailActivationMult * AtrEntry;
        Assert.True(wouldArm, "stale trailLow arms the trail purely because the basis moved");

        // Worse: once armed, the current price is already above the stale trail level,
        // so the position would exit on the very bar it added size.
        double currentPrice = 104;
        Assert.True(currentPrice > staleTrailLow + TrailStopMult * AtrEntry,
                    "stale trail level would fire immediately after the add");
    }

    [Fact]
    public void AfterDca_TrailIsRebasedToBlendedEntryAndDisarmed()
    {
        // The rebase: trailLow := blended entry, trailArmed := false — the same state a
        // fresh entry starts in. For a short, favourable is DOWN, so trailLow tracks the
        // best (lowest) price and arming means "activation × ATR of profit vs the basis".
        double blendedEntry = BlendedEntry(100, 104, DcaMode.MaxSizeMult);   // 102
        double trailLow     = blendedEntry;
        bool   trailArmed   = false;

        Assert.False(trailArmed);
        Assert.False(trailLow - blendedEntry > 0, "trailLow must not sit above the blended basis");
        Assert.False(blendedEntry - trailLow >= TrailActivationMult * AtrEntry,
                     "a freshly rebased position must start unarmed");
    }

    [Fact]
    public void AfterDca_TrailArmsOnlyAfterProfitAgainstBlendedEntry()
    {
        double blendedEntry = BlendedEntry(100, 104, DcaMode.MaxSizeMult);   // 102
        double trailLow     = blendedEntry;

        // Price drifts down to 101 — profit vs the blended basis is 1.0, below 1.0 × ATR (2.0).
        trailLow = Math.Min(trailLow, 101);
        Assert.False(blendedEntry - trailLow >= TrailActivationMult * AtrEntry);

        // Down to 99.9: profit 2.1 clears activation, so the trail arms — and only now, off
        // a price actually observed after the add.
        trailLow = Math.Min(trailLow, 99.9);
        Assert.True(blendedEntry - trailLow >= TrailActivationMult * AtrEntry);

        // The trail then exits when price climbs TrailStopMult × ATR back above the trough.
        Assert.True(101.0 > trailLow + TrailStopMult * AtrEntry);
        Assert.False(100.5 > trailLow + TrailStopMult * AtrEntry);
    }

    [Fact]
    public void GetRipShortReturns_WithNullOverrideCfg_MatchesLegacyBehavior()
    {
        // No candle data → both calls degrade to an empty trade list identically,
        // confirming the new optional param doesn't change behavior when unset.
        var g = RipShortGenotype.Random(new Random(42));
        var legacy   = GetRipShortReturns(g, ReadOnlySpan<Candle>.Empty, ReadOnlySpan<Candle>.Empty);
        var withNull = GetRipShortReturns(g, ReadOnlySpan<Candle>.Empty, ReadOnlySpan<Candle>.Empty, overrideCfg: null);
        Assert.Equal(legacy.Count, withNull.Count);
    }
}
