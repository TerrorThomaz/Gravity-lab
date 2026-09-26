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
            Series("BTCUSDT", 500), Series("ETHUSDT", 500), Series("AUSDT", 500),
        };
        Assert.Null(SymbolCrowdingCap.Build(daily, strength: 0.0));
    }

    [Fact]
    public void Build_ReturnsNullWithoutBothAnchors()
    {
        var daily = new List<SymbolCovariance.DailySeries> { Series("BTCUSDT", 500), Series("AUSDT", 500) };
        Assert.Null(SymbolCrowdingCap.Build(daily, strength: 1.0));
    }

    [Fact]
    public void MeanPairwise_TreatsUnknownSymbolsAsFullyCorrelated()
    {
        // A symbol with no anchored history must not dilute measured crowding: worst case.
        var trades = Longs(2);
        var cap = Correlated(trades, rho: 0.0, strength: 1.0);

        // Two independent sample paths measure near zero, never exactly zero.
        Assert.InRange(cap.MeanPairwise(new[] { "SYM0USDT" }, "SYM1USDT", T0), -0.2, 0.2);
        // An unlisted symbol is pinned at the worst case, exactly.
        Assert.Equal(1.0, cap.MeanPairwise(new[] { "SYM0USDT" }, "NOTLISTEDUSDT", T0), 12);
    }

    // The failure this design exists to fix: one matrix fitted before the book's first trade left
    // every later-listed coin at correlation 1.0 forever. Rolling re-estimation must let a coin in
    // once it has enough history of its own.
    [Fact]
    public void LateListedSymbol_IsWorstCaseAtFirst_ThenMeasuredOnceItHasHistory()
    {
        var daily = new List<SymbolCovariance.DailySeries>
        {
            Walk("BTCUSDT", seed: 1, from: -400, to: 300),
            Walk("ETHUSDT", seed: 2, from: -400, to: 300),
            Walk("OLDUSDT", seed: 3, from: -400, to: 300),
            Walk("NEWUSDT", seed: 4, from: -20,  to: 300),   // listed 20 days before T0
        };
        var cap = SymbolCrowdingCap.Build(daily, strength: 1.0)!;

        Assert.Equal(1.0, cap.MeanPairwise(new[] { "OLDUSDT" }, "NEWUSDT", T0), 12);
        // 200 days later NEWUSDT has well over minObs=60 days in the trailing window.
        Assert.InRange(cap.MeanPairwise(new[] { "OLDUSDT" }, "NEWUSDT", T0.AddDays(200)), -0.3, 0.3);
    }

    // Lookahead guard: data dated at or after the trade must not move the estimate that applies
    // to it. The same history with a violently co-moving future appended gives the same answer.
    [Fact]
    public void Estimate_IgnoresEverythingOnOrAfterTheTradesPeriod()
    {
        // One seed per symbol, so appending a future never shifts any symbol's past draws.
        List<SymbolCovariance.DailySeries> Book(bool withFuture)
            => new[] { "BTCUSDT", "ETHUSDT", "AUSDT", "BUSDT" }
                .Select((sym, i) => Walk(sym, seed: 20 + i, from: -400, to: withFuture ? 200 : 0,
                                         vol: d => d >= 0 ? 5.0 : 0.0))
                .ToList();

        var past   = SymbolCrowdingCap.Build(Book(false), 1.0)!;
        var future = SymbolCrowdingCap.Build(Book(true),  1.0)!;

        Assert.Equal(past.MeanPairwise(new[] { "AUSDT" }, "BUSDT", T0),
                     future.MeanPairwise(new[] { "AUSDT" }, "BUSDT", T0), 12);
    }

    // BTC and ETH perfectly collinear makes the two-factor normal matrix singular; the fallback to
    // BTC alone must still recover full correlation rather than NaN or a blown-up loading.
    [Fact]
    public void CollinearAnchors_FallBackToBtcAlone()
    {
        var trades = Longs(2);
        var cap = Correlated(trades, rho: 1.0, strength: 1.0, ethEqualsBtc: true);
        Assert.Equal(1.0, cap.MeanPairwise(new[] { "SYM0USDT" }, "SYM1USDT", T0), 6);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    // n simultaneous long trades, one per distinct symbol, all open for the same 100 hours.
    private static List<PortfolioReplay.Trade> Longs(int n)
        => Enumerable.Range(0, n)
            .Select(i => new PortfolioReplay.Trade(
                "diplong", T0.AddMinutes(i), TimeSpan.FromHours(100), 0.01, 0.5, $"SYM{i}USDT"))
            .ToList();

    // A crowding cap over those symbols with a prescribed co-movement, built by synthesising daily
    // series against a shared path that also drives the BTC anchor: symbols identical to it give
    // rho = 1, independent ones give rho ≈ 0. All history sits before T0.
    private static SymbolCrowdingCap Correlated(
        IReadOnlyList<PortfolioReplay.Trade> trades, double rho, double strength, bool ethEqualsBtc = false)
    {
        const int days = 500;
        var rng = new Random(11);
        var shared = new double[days];
        for (int d = 0; d < days; d++) shared[d] = rng.NextDouble() - 0.5;

        SymbolCovariance.DailySeries Path(string sym, Func<int, double> step)
        {
            var dts = new DateTime[days];
            var cl  = new double[days];
            double px = 100.0;
            for (int d = 0; d < days; d++)
            {
                px *= 1.0 + 0.01 * step(d);
                dts[d] = T0.AddDays(d - days).Date;
                cl[d]  = px;
            }
            return new SymbolCovariance.DailySeries(sym, dts, cl);
        }

        var daily = new List<SymbolCovariance.DailySeries>
        {
            Path("BTCUSDT", d => shared[d]),
            ethEqualsBtc ? Path("ETHUSDT", d => shared[d])
                         : Path("ETHUSDT", d => 0.7 * shared[d] + 0.3 * (rng.NextDouble() - 0.5)),
        };
        foreach (var t in trades.Select(t => t.Symbol).Distinct())
            daily.Add(Path(t, d => rho >= 1.0 ? shared[d]
                                 : rho <= 0.0 ? rng.NextDouble() - 0.5
                                 : rho * shared[d] + (1 - rho) * (rng.NextDouble() - 0.5)));
        return SymbolCrowdingCap.Build(daily, strength)!;
    }

    // Random walk over days [from, to) relative to T0; `vol` adds to the unit step size by day.
    private static SymbolCovariance.DailySeries Walk(
        string sym, int seed, int from, int to, Func<int, double>? vol = null)
    {
        var rng = new Random(seed);
        int n = to - from;
        var dts = new DateTime[n];
        var cl  = new double[n];
        double px = 100.0;
        for (int i = 0; i < n; i++)
        {
            int d = from + i;
            px *= 1.0 + 0.01 * (1.0 + (vol?.Invoke(d) ?? 0.0)) * (rng.NextDouble() - 0.5);
            dts[i] = T0.AddDays(d).Date;
            cl[i]  = px;
        }
        return new SymbolCovariance.DailySeries(sym, dts, cl);
    }

    private static SymbolCovariance.DailySeries Series(string sym, int days)
    {
        var d = new DateTime[days];
        var c = new double[days];
        for (int i = 0; i < days; i++) { d[i] = T0.AddDays(i).Date; c[i] = 100.0 + i; }
        return new SymbolCovariance.DailySeries(sym, d, c);
    }
}
