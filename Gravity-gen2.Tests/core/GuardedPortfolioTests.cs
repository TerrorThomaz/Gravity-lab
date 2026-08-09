using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Covers the mechanism that was fully implemented but never reached combinedbacktest /
// oosbacktest: applying the trained DynamicGuard to a portfolio trade list, and reporting the
// guarded result ALONGSIDE the unguarded one rather than in place of it.
public class GuardedPortfolioTests
{
    static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static GuardedPortfolio.Trade Tr(int hoursFromStart, double ret, double conf, string strategy) =>
        new(T0.AddHours(hoursFromStart), ret, conf, TimeSpan.FromHours(48), strategy);

    // Flat 1h series: constant true range ⇒ ATR ratio pinned at 1.0 ⇒ no stress, no ATR block.
    static Candle[] CalmBtc(int bars = 4000)
    {
        var c = new Candle[bars];
        for (int i = 0; i < bars; i++)
            c[i] = new Candle(T0.AddHours(i - bars / 2), 100, 101, 99, 100, 1000);
        return c;
    }

    static DynamicGuardGenotype Geno(
        double sizeFloor     = 0.5,
        double atrTrigger    = 1.2,
        double panicTrigger  = 5.0,
        double entryAtrGate  = 999.0,
        double bullMomBypass = 0.0,
        double ddGate        = DynamicGuardGenotype.DdGateDisabled,
        double confCapMin    = 1.0,
        double confCapMax    = 1.0,
        double ppThreshold   = 1.0) =>
        new(AtrLookback: 10, AtrTrigger: atrTrigger, MomLookback: 10, MomThreshold: -0.05,
            SizeFloor: sizeFloor, PanicTrigger: panicTrigger, RecoveryBars: 0,
            BullMomBypass: bullMomBypass, EntryAtrGate: entryAtrGate, DdEntryGatePct: ddGate,
            ConfLossCapMin: confCapMin, ConfLossCapMax: confCapMax,
            ProfitProtectThreshold: ppThreshold, ProfitProtectDrawback: 0.10,
            ProfitProtectFactor: 1.0);

    // ── The claim that lets the "unguarded" column be compared to the caller's headline ──
    // GuardedPortfolio.Run computes BOTH columns through the 5-tuple simulator overload. That is
    // only legitimate if the 5-tuple overload with every guard knob at its no-op value is
    // identical to the 4-tuple overload the commands already print. If this test ever fails, the
    // side-by-side table is comparing two different code paths and the delta is meaningless.
    [Fact]
    public void GuardConfigOff_MatchesTheUnguardedFourTupleSimulator()
    {
        var raw = new List<GuardedPortfolio.Trade>
        {
            Tr(0,   5.0, 0.30, "diplong"),
            Tr(24, -8.0, 0.40, "swing_long"),
            Tr(48,  3.0, 0.20, "grid"),
            Tr(72, -4.0, 0.50, "ripshort"),
            Tr(96,  6.0, 0.35, "diplong"),
        };

        var fourTuple = raw.Select(t => (t.Time, t.Return, t.Conf, t.Hold)).ToList();
        var expected  = Simulator.SimulatePortfolioExposureCapped(
            fourTuple, 0.30, maxPositionFrac: 0.05, slippageBps: Config.SlippageBps);

        var off = GuardedPortfolio.PortfolioGuardConfig.Off;
        var actual = Simulator.SimulatePortfolioExposureCapped(
            GuardedPortfolio.Passthrough(raw), 0.30,
            maxPositionFrac:        0.05,
            ddLongEntryGatePct:     off.DdLongEntryGatePct,
            confLossCapMin:         off.ConfLossCapMin,
            confLossCapMax:         off.ConfLossCapMax,
            profitProtectThreshold: off.ProfitProtectThreshold,
            profitProtectDrawback:  off.ProfitProtectDrawback,
            profitProtectFactor:    off.ProfitProtectFactor,
            slippageBps:            Config.SlippageBps);

        Assert.Equal(expected.EndBalance,     actual.EndBalance,     10);
        Assert.Equal(expected.MaxDrawdownPct, actual.MaxDrawdownPct, 10);
        Assert.Equal(expected.TradesCount,    actual.TradesCount);
    }

    [Fact]
    public void PortfolioGuardConfig_FromNullGenotype_IsAllNoOps()
    {
        var off = GuardedPortfolio.PortfolioGuardConfig.From(null);
        Assert.Equal(GuardedPortfolio.PortfolioGuardConfig.Off, off);
        Assert.Equal(DynamicGuardGenotype.DdGateDisabled, off.DdLongEntryGatePct);
        Assert.Equal(1.0, off.ConfLossCapMin);
        Assert.Equal(1.0, off.ProfitProtectThreshold);
    }

    [Fact]
    public void PortfolioGuardConfig_FromGenotype_CarriesEveryPortfolioKnob()
    {
        var g   = Geno(ddGate: 0.08, confCapMin: 0.05, confCapMax: 0.20, ppThreshold: 0.25);
        var cfg = GuardedPortfolio.PortfolioGuardConfig.From(g);
        Assert.Equal(0.08, cfg.DdLongEntryGatePct, 10);
        Assert.Equal(0.05, cfg.ConfLossCapMin, 10);
        Assert.Equal(0.20, cfg.ConfLossCapMax, 10);
        Assert.Equal(0.25, cfg.ProfitProtectThreshold, 10);
        Assert.Equal(g.ProfitProtectFactor, cfg.ProfitProtectFactor, 10);
    }

    // ── Apply: only the guarded strategy set is scaled ────────────────────────────────────
    [Fact]
    public void Apply_ScalesOnlyGuardedStrategies_AndLeavesOthersUntouched()
    {
        var btc     = CalmBtc();
        var session = new DynamicGuardSession(btc, Geno(sizeFloor: 0.5, atrTrigger: 0.5)); // ATR ratio 1.0 > 0.5 ⇒ stressed

        var raw = new List<GuardedPortfolio.Trade>
        {
            Tr(0, 1.0, 0.40, "grid"),        // guarded
            Tr(1, 1.0, 0.40, "gridshort"),   // guarded
            Tr(2, 1.0, 0.40, "swing"),       // NOT guarded (FadeShort)
            Tr(3, 1.0, 0.40, "ripshort"),    // NOT guarded
        };

        var applied = GuardedPortfolio.Apply(raw, session);
        Assert.Equal(4, applied.Count);

        double mult = session.GetMult(raw[0].Time, "grid");
        Assert.True(mult < 1.0, $"expected a stress multiplier below 1.0, got {mult}");

        Assert.Equal(0.40 * mult, applied[0].Item3, 10);   // grid      → scaled
        Assert.Equal(0.40 * mult, applied[1].Item3, 10);   // gridshort → scaled
        Assert.Equal(0.40,        applied[2].Item3, 10);   // swing     → untouched
        Assert.Equal(0.40,        applied[3].Item3, 10);   // ripshort  → untouched
    }

    // The ATR entry gate must REMOVE the trade, not zero-size it: a blocked entry never happened
    // and must not occupy a slot in the exposure ledger.
    [Fact]
    public void Apply_AtrEntryGate_DropsBlockedLongsEntirely()
    {
        var btc = CalmBtc();
        // ATR ratio on a constant-range series is 1.0, so a gate of 0.5 blocks every diplong /
        // swing_long entry and nothing else.
        var session = new DynamicGuardSession(btc, Geno(entryAtrGate: 0.5));

        var raw = new List<GuardedPortfolio.Trade>
        {
            Tr(0, 1.0, 0.40, "diplong"),
            Tr(1, 1.0, 0.40, "swing_long"),
            Tr(2, 1.0, 0.40, "grid"),
            Tr(3, 1.0, 0.40, "ripshort"),
        };

        var applied = GuardedPortfolio.Apply(raw, session);
        Assert.Equal(2, applied.Count);
        Assert.DoesNotContain(applied, x => x.Item5 is "diplong" or "swing_long");
        Assert.Contains(applied, x => x.Item5 == "grid");
        Assert.Contains(applied, x => x.Item5 == "ripshort");
    }

    [Fact]
    public void Apply_PreservesStrategyLabel_SoTheSimulatorGatesCanStillSeeIt()
    {
        var session = new DynamicGuardSession(CalmBtc(), Geno());
        var raw     = new List<GuardedPortfolio.Trade> { Tr(0, 1.0, 0.4, "diplong") };
        var applied = GuardedPortfolio.Apply(raw, session);
        Assert.Equal("diplong", applied[0].Item5);
        Assert.Equal(TimeSpan.FromHours(48), applied[0].Item4);
    }

    // ── Run: both columns are produced from the same trade list ───────────────────────────
    [Fact]
    public void Run_ProducesBothGuardedAndUnguardedResults()
    {
        var ctx = new GuardedPortfolio.Context(
            new DynamicGuardSession(CalmBtc(), Geno(sizeFloor: 0.3, atrTrigger: 0.5)),
            Geno(sizeFloor: 0.3, atrTrigger: 0.5),
            GuardedPortfolio.PortfolioGuardConfig.From(Geno(sizeFloor: 0.3, atrTrigger: 0.5)));

        // Confidence deliberately BELOW maxPositionFrac (0.05). At conf ≥ the cap the position
        // size is pinned by the cap and shrinking conf changes nothing — the guard would look
        // inert for a reason that has nothing to do with the guard.
        var raw = new List<GuardedPortfolio.Trade>
        {
            Tr(0,  10.0, 0.04, "grid"),
            Tr(24, 10.0, 0.04, "grid"),
            Tr(48, 10.0, 0.04, "grid"),
        };

        var r = GuardedPortfolio.Run(raw, ctx, 0.30, Config.SlippageBps);

        Assert.Equal(3, r.UnguardedTrades);
        Assert.Equal(3, r.GuardedTrades);
        Assert.Equal(0, r.TradesRemoved);

        // Guard shrinks grid position sizes, so a purely winning set must end LOWER guarded.
        Assert.True(r.GuardedFivePct.EndBalance < r.UnguardedFivePct.EndBalance,
            $"guarded {r.GuardedFivePct.EndBalance:F4} should be below unguarded {r.UnguardedFivePct.EndBalance:F4}");
        Assert.True(r.GuardedKelly.EndBalance   < r.UnguardedKelly.EndBalance);

        // And the unguarded column must still equal the plain headline simulation.
        var headline = Simulator.SimulatePortfolioExposureCapped(
            raw.Select(t => (t.Time, t.Return, t.Conf, t.Hold)).ToList(),
            0.30, maxPositionFrac: 0.05, slippageBps: Config.SlippageBps);
        Assert.Equal(headline.EndBalance, r.UnguardedFivePct.EndBalance, 10);
    }

    // ── The reporting contract: BOTH numbers must appear, never one replacing the other ───
    [Fact]
    public void PrintComparison_ReportsGuardedAndUnguardedSideBySide()
    {
        var geno = Geno(sizeFloor: 0.3, atrTrigger: 0.5);
        var ctx  = new GuardedPortfolio.Context(
            new DynamicGuardSession(CalmBtc(), geno), geno,
            GuardedPortfolio.PortfolioGuardConfig.From(geno));

        var raw = new List<GuardedPortfolio.Trade>
        {
            Tr(0,  10.0, 0.04, "grid"),
            Tr(24, -5.0, 0.04, "grid"),
            Tr(48,  7.0, 0.04, "diplong"),
        };

        string output = Capture(() =>
            GuardedPortfolio.PrintComparison("UnitTest", raw, ctx, 0.30, Config.SlippageBps));

        Assert.Contains("Unguarded", output);
        Assert.Contains("Guarded",   output);
        Assert.Contains("5% per position", output);
        Assert.Contains("Kelly(15%)",      output);

        // Both magnitudes must actually be printed, not just the headings.
        var r = GuardedPortfolio.Run(raw, ctx, 0.30, Config.SlippageBps);
        Assert.Contains(GuardedPortfolio.ReturnPct(r.UnguardedFivePct).ToString("+0.0;-0.0"), output);
        Assert.Contains(GuardedPortfolio.ReturnPct(r.GuardedFivePct).ToString("+0.0;-0.0"),   output);

        // And the retrain caveat must be stated: the on-disk guard was selected while the DD
        // entry gate was inert, so the guarded column is not yet trustworthy.
        Assert.Contains("dynamicguardtrain", output);
    }

    [Fact]
    public void PrintComparison_WithNoGuard_SaysTheNumbersAreUnguarded()
    {
        string output = Capture(() =>
            GuardedPortfolio.PrintComparison("UnitTest", new List<GuardedPortfolio.Trade>(), null, 0.30, 10.0));
        Assert.Contains("UNGUARDED", output);
    }

    [Fact]
    public void TryLoad_WithMissingGenotypeFile_ReturnsNullAndExplains()
    {
        string output = Capture(() =>
            Assert.Null(GuardedPortfolio.TryLoad(CalmBtc(), Path.Combine(Path.GetTempPath(), "no_such_guard_genotype.json"))));
        Assert.Contains("dynamicguardtrain", output);
    }

    [Fact]
    public void TryLoad_WithInsufficientBtcHistory_ReturnsNull()
    {
        // Genotype path is irrelevant here — the BTC check must not be reachable only when a
        // genotype happens to exist, or a short BTC series would silently yield a half-built guard.
        var tmp = Path.Combine(Path.GetTempPath(), $"guard_{Guid.NewGuid():N}.json");
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(
            new DynamicGuardGenotypeDto(10, 1.2, 10, -0.05, 0.5, 1.0)));
        try
        {
            Assert.Null(Capture2(() => GuardedPortfolio.TryLoad(CalmBtc(10), tmp)));
        }
        finally { File.Delete(tmp); }
    }

    static string Capture(Action a)
    {
        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try { a(); } finally { Console.SetOut(prev); }
        return sw.ToString();
    }

    static T? Capture2<T>(Func<T?> f) where T : class
    {
        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try { return f(); } finally { Console.SetOut(prev); }
    }
}
