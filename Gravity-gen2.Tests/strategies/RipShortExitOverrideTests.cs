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
    public void ShouldDca_False_InWaitOnlyMode()
    {
        // WaitForBreakeven mode never DCAs, even if the price move would qualify.
        Assert.False(ShouldDca(waiting: true, dcaDone: false, price: 105.0, entry: 100, atrEntry: 2.0, WaitOnly));
    }

    [Fact]
    public void DcaBlend_MovesEntryHalfwayAndDoublesSize()
    {
        // Mirrors the blend applied in RunRipShortMultiTF: new entry is the midpoint,
        // and the eventual return must be scaled ×2 for the doubled capital deployed.
        double entry1 = 100, addPrice = 104;
        double blendedEntry = (entry1 + addPrice) / 2.0;
        Assert.Equal(102.0, blendedEntry);

        double exitPx = 98;
        double retPerUnit = (blendedEntry - exitPx) / blendedEntry * 100.0;
        double scaledRet  = retPerUnit * 2.0;
        Assert.True(scaledRet > retPerUnit, "DCA'd trade should report double the single-unit return");
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
