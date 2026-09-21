using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The crowding cap charges a directional slot budget for correlation instead of counting heads.
// Two properties are load-bearing and are pinned here rather than argued in a comment: it is an
// EXACT no-op at strength 0, and it is ONE-SIDED — it can only ever reject a trade the headcount
// would have admitted. Everything else about it is tuning.
public class SymbolCrowdingCapTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // These tests are about the DIRECTIONAL budget, so the per-strategy cap is lifted out of the
    // way — "diplong" carries a default of 8, which would otherwise bind before the cap under test.
    private static readonly Dictionary<string, int> NoStrategyCap = new() { ["diplong"] = int.MaxValue };

    // ── EffectiveSlots: the whole cap reduces to this function ──────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(20)]
    [InlineData(200)]
    public void EffectiveSlots_AtStrengthZero_IsExactlyTheHeadcount(int n)
    {
        // Math.Pow(x, 0) == 1.0 for every finite x, but the early return makes it exact rather
        // than merely correct to within a rounding: this must be bit-for-bit n.
        foreach (double rho in new[] { -1.0, 0.0, 0.37, 1.0 })
            Assert.Equal((double)n, SymbolCrowdingCap.EffectiveSlots(n, rho, 0.0));
    }

    [Fact]
    public void EffectiveSlots_UncorrelatedBook_CostsExactlyItsHeadcount()
    {
        // rho = 0 → inflation 1 → n·1^strength = n, at any strength. An uncorrelated book is
        // never penalised, which is what makes this a crowding charge and not a blanket tightening.
        Assert.Equal(10.0, SymbolCrowdingCap.EffectiveSlots(10, 0.0, 1.0), 12);
        Assert.Equal(10.0, SymbolCrowdingCap.EffectiveSlots(10, 0.0, 0.5), 12);
    }

    [Fact]
    public void EffectiveSlots_FullyCorrelatedBook_CostsTheFullVarianceInflation()
    {
        // rho = 1, strength 1 → n·(1 + (n−1)) = n².  Ten identical positions are one bet held ten
        // times over, and cost a hundred slots.
        Assert.Equal(100.0, SymbolCrowdingCap.EffectiveSlots(10, 1.0, 1.0), 9);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-0.5)]
    public void EffectiveSlots_NegativeCorrelation_CannotBuyExtraSlots(double rho)
    {
        // The inflation floor at 1 is deliberate: crypto correlations converge toward 1 in the
        // drawdowns this exists to survive, so a hedged-looking estimate must never be trusted in
        // the loosening direction.
        Assert.Equal(8.0, SymbolCrowdingCap.EffectiveSlots(8, rho, 1.0), 12);
    }

    // The one-sidedness property, stated as a property rather than a example.
    [Fact]
    public void EffectiveSlots_IsNeverBelowTheHeadcount_AcrossTheGrid()
    {
        foreach (int n in new[] { 2, 3, 7, 20, 50 })
            foreach (double rho in new[] { -1.0, -0.2, 0.0, 0.15, 0.6, 0.95, 1.0 })
                foreach (double strength in new[] { 0.0, 0.25, 1.0, 2.0 })
                    Assert.True(SymbolCrowdingCap.EffectiveSlots(n, rho, strength) >= n,
                        $"n={n} rho={rho} strength={strength} produced fewer slots than the headcount");
    }

    [Fact]
    public void EffectiveSlots_IsMonotoneInCorrelationAndStrength()
    {
        Assert.True(SymbolCrowdingCap.EffectiveSlots(12, 0.8, 1.0) > SymbolCrowdingCap.EffectiveSlots(12, 0.3, 1.0));
        Assert.True(SymbolCrowdingCap.EffectiveSlots(12, 0.5, 1.0) > SymbolCrowdingCap.EffectiveSlots(12, 0.5, 0.4));
    }

    // ── End-to-end through PortfolioReplay ──────────────────────────────────────────────────

    [Fact]
    public void FilterByConcurrentCap_WithoutACrowdingCap_IsUnchanged()
    {
        var trades = Longs(12);
        var plain     = PortfolioReplay.FilterByConcurrentCap(trades, NoStrategyCap, directionalCap: 20);
        var nullCrowd = PortfolioReplay.FilterByConcurrentCap(trades, NoStrategyCap, directionalCap: 20, crowding: null);

        Assert.Equal(12, plain.Count);
        Assert.Equal(plain.Count, nullCrowd.Count);
    }

    [Fact]
    public void FilterByConcurrentCap_CrowdedBook_IsCutBelowTheHeadcountLimit()
    {
        // Twelve simultaneous longs on perfectly co-moving symbols, headcount cap 20. The headcount
        // admits all twelve; the crowding charge must not.
        var trades = Longs(12);
        var cap = Correlated(trades, rho: 1.0, strength: 1.0);

        var filtered = PortfolioReplay.FilterByConcurrentCap(trades, NoStrategyCap, directionalCap: 20, crowding: cap);

        // n fully-correlated positions cost n² slots, so a budget of 20 admits floor(sqrt(20)) = 4.
        Assert.True(filtered.Count < 12, $"crowded book should be cut, kept {filtered.Count}");
        Assert.InRange(filtered.Count, 3, 5);
    }

    // An independent book is charged almost nothing, where the co-moving one above lost two
    // thirds of its trades. Asserted as a comparison rather than an equality because the measured
    // correlation of independent sample paths is near zero, not exactly zero.
    [Fact]
    public void FilterByConcurrentCap_UncorrelatedBook_IsBarelyCut()
    {
        var trades = Longs(12);
        var loose  = PortfolioReplay.FilterByConcurrentCap(
            trades, NoStrategyCap, directionalCap: 20, crowding: Correlated(trades, 0.0, 1.0));
        var tight  = PortfolioReplay.FilterByConcurrentCap(
            trades, NoStrategyCap, directionalCap: 20, crowding: Correlated(trades, 1.0, 1.0));

        Assert.True(loose.Count >= 10, $"an independent book should survive the cap, kept {loose.Count}");
        Assert.True(loose.Count > tight.Count,
            $"independent ({loose.Count}) must survive better than co-moving ({tight.Count})");
    }

    // The property that matters most: whatever the correlation, the crowding cap never admits a
    // trade the plain headcount rejected.
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void FilterByConcurrentCap_NeverAdmitsMoreThanTheHeadcountAlone(double rho)
    {
        var trades = Longs(30);
        var baseline = PortfolioReplay.FilterByConcurrentCap(trades, NoStrategyCap, directionalCap: 8);
        var capped   = PortfolioReplay.FilterByConcurrentCap(
            trades, NoStrategyCap, directionalCap: 8, crowding: Correlated(trades, rho, strength: 1.0));

        Assert.True(capped.Count <= baseline.Count,
            $"rho={rho}: crowding kept {capped.Count} against a headcount-only {baseline.Count}");
    }

    [Fact]
    public void Build_ReturnsNullAtStrengthZero_SoTheOffStateIsUnambiguous()
    {
        var daily = new List<SymbolCovariance.DailySeries>
        {
            Series("AUSDT", 500), Series("BUSDT", 500),
        };
        Assert.Null(SymbolCrowdingCap.Build(daily, strength: 0.0));
    }

    [Fact]
    public void Build_ReturnsNullWhenAsOfLeavesTooLittlePriorHistory()
    {
        var daily = new List<SymbolCovariance.DailySeries> { Series("AUSDT", 500), Series("BUSDT", 500) };
        // Only 10 days precede the cutoff — far under the 365-day window floor.
        Assert.Null(SymbolCrowdingCap.Build(daily, strength: 1.0, asOf: T0.AddDays(10)));
    }

    [Fact]
    public void MeanPairwise_TreatsUnknownSymbolsAsFullyCorrelated()
    {
        // A symbol absent from the estimation window must not dilute measured crowding: worst case.
        var trades = Longs(2);
        var cap = Correlated(trades, rho: 0.0, strength: 1.0);

        // Two independent sample paths measure near zero, never exactly zero.
        Assert.InRange(cap.MeanPairwise(new[] { "SYM0USDT" }, "SYM1USDT"), -0.2, 0.2);
        // An unlisted symbol is pinned at the worst case, exactly.
        Assert.Equal(1.0, cap.MeanPairwise(new[] { "SYM0USDT" }, "NOTLISTEDUSDT"), 12);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    // n simultaneous long trades, one per distinct symbol, all open for the same 100 hours.
    private static List<PortfolioReplay.Trade> Longs(int n)
        => Enumerable.Range(0, n)
            .Select(i => new PortfolioReplay.Trade(
                "diplong", T0.AddMinutes(i), TimeSpan.FromHours(100), 0.01, 0.5, $"SYM{i}USDT"))
            .ToList();

    // A crowding cap over those symbols with a prescribed uniform pairwise correlation, built by
    // synthesising daily series: identical series give rho = 1, independent ones give rho ≈ 0.
    private static SymbolCrowdingCap Correlated(
        IReadOnlyList<PortfolioReplay.Trade> trades, double rho, double strength)
    {
        const int days = 500;
        var rng = new Random(11);
        var shared = new double[days];
        for (int d = 0; d < days; d++) shared[d] = rng.NextDouble() - 0.5;

        var daily = new List<SymbolCovariance.DailySeries>();
        foreach (var t in trades.Select(t => t.Symbol).Distinct())
        {
            var dts = new DateTime[days];
            var cl  = new double[days];
            double px = 100.0;
            for (int d = 0; d < days; d++)
            {
                double step = rho >= 1.0 ? shared[d] : (rho <= 0.0 ? rng.NextDouble() - 0.5
                             : rho * shared[d] + (1 - rho) * (rng.NextDouble() - 0.5));
                px *= 1.0 + 0.01 * step;
                dts[d] = T0.AddDays(d - days).Date;    // entirely before T0, so asOf: T0 keeps it
                cl[d]  = px;
            }
            daily.Add(new SymbolCovariance.DailySeries(t, dts, cl));
        }
        return SymbolCrowdingCap.Build(daily, strength, asOf: T0)!;
    }

    private static SymbolCovariance.DailySeries Series(string sym, int days)
    {
        var d = new DateTime[days];
        var c = new double[days];
        for (int i = 0; i < days; i++) { d[i] = T0.AddDays(i).Date; c[i] = 100.0 + i; }
        return new SymbolCovariance.DailySeries(sym, d, c);
    }
}
