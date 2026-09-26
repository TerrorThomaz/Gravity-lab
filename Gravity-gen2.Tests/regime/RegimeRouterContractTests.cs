using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The routing/sizing contract, stated as properties rather than as a transcription of the
// implementation. Every test here pins something a backtest silently depended on and that
// RegimeRouterHmmTests could not catch, because it asserted the formula the code computes.
public class RegimeRouterContractTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly RegimeRouterGA.StrategyKind[] BaseKinds =
    {
        RegimeRouterGA.StrategyKind.FadeShort,
        RegimeRouterGA.StrategyKind.Grid,
        RegimeRouterGA.StrategyKind.GridShort,
        RegimeRouterGA.StrategyKind.DipLong,
        RegimeRouterGA.StrategyKind.FadeLong,
        RegimeRouterGA.StrategyKind.RipShort,
        RegimeRouterGA.StrategyKind.SwingLong,
        RegimeRouterGA.StrategyKind.AccumulationGrid,
    };

    private static RegimeRouterGenotype HmmGeno(double favAll = 0.0)
    {
        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        for (int s = 0; s < RegimeRouterGenotype.HmmStrategyRows; s++)
            fav[s * RegimeRouterGenotype.MaxHmmStates + 0] = favAll;
        return new RegimeRouterGenotype { Favorability = fav, Biases = bias, HmmStatesN = 4.0 };
    }

    private static RegimeBar[] Bars(MarketRegime regime, double conf, int duration, int n = 600)
    {
        var probs = new double[RegimeRouterGenotype.MaxHmmStates];
        probs[0] = 1.0;
        var bars = new RegimeBar[n];
        for (int i = 0; i < n; i++)
            bars[i] = new RegimeBar(T0.AddHours(i), regime, conf, duration, (double[])probs.Clone());
        return bars;
    }

    // A gate that declares `strategy` profitable in `regime` — the shape LongTrainCommands writes
    // to genotypes/strategy_family_gate.json and StrategyPipeline loads into every backtest.
    private static StrategyFamilyGating.Gate GateFor(string strategy, MarketRegime regime, double pf)
    {
        var prof = new Dictionary<MarketRegime, StrategyFamilyGating.RegimeProfitability>();
        foreach (MarketRegime r in Enum.GetValues<MarketRegime>())
            prof[r] = r == regime
                ? new StrategyFamilyGating.RegimeProfitability(pf, 0.6, 1.0, 100, true)
                : new StrategyFamilyGating.RegimeProfitability(0.5, 0.3, -1.0, 100, false);

        return new StrategyFamilyGating.Gate(
            new Dictionary<string, string> { [strategy] = "F0" },
            new List<StrategyFamilyGating.FamilyStats>
            {
                new("F0", new List<string> { strategy }, prof),
            },
            new Dictionary<MarketRegime, IReadOnlyList<string>>
            {
                [regime] = new List<string> { "F0" },
            },
            0.7);
    }

    // THE INVARIANT THE BOOKS VIOLATED. A trade admitted by the router must be sized above zero.
    // With a SIMFAM gate attached, IsActive routed through the gate while SizeGate fell through to
    // the legacy thresholds, so a trade the gate admitted was sized at exactly 0 — it took a
    // portfolio slot, entered the reported book, and contributed no P&L. 56% of the rows in
    // reports/fulltest_fulltest_oos_book_trades.csv were in that state.
    [Fact]
    public void SizeGate_IsPositiveExactlyWhenTheRouterIsActive_UnderAFamilyGate()
    {
        // Bull, but only 10 bars into it: the legacy confirmed-bull gates are not met, so a short
        // strategy is legacy-inactive here. The family gate says it is profitable in Bull anyway.
        var bars = Bars(MarketRegime.Bull, conf: 0.8, duration: 10);
        var session = new RegimeRouterSession(bars, null, HmmGeno())
            .WithGate(GateFor(nameof(RegimeRouterGA.StrategyKind.RipShort), MarketRegime.Bull, pf: 2.0));
        var t = T0.AddHours(300);

        bool sawActive = false, sawInactive = false;
        foreach (var kind in BaseKinds)
        {
            bool active = session.IsActive(kind, t);
            double size = session.SizeGate(kind, t);
            sawActive   |= active;
            sawInactive |= !active;

            if (active) Assert.True(size > 0.0, $"{kind}: router active but sized at {size}");
            else        Assert.Equal(0.0, size, 12);
        }
        // Guard against a vacuous pass: both branches of the invariant must have been exercised.
        Assert.True(sawActive && sawInactive);
    }

    // Size must never reward LOWER conviction. The retired rule mapped w<0.5 to full size and
    // w>=0.5 to w, so the size curve peaked at minimum conviction and dipped to its minimum at
    // w=0.5. Train (RegimeRouterGA.FilterActive) shared the rule, so the GA was paid to drive
    // favorability under 0.5 and switch its own sizing layer off.
    [Fact]
    public void SizeGate_IsNonDecreasingInConviction()
    {
        var bars = Bars(MarketRegime.Bull, conf: 0.9, duration: 500);
        var t = T0.AddHours(500);
        var favs = new[] { -1.0, -0.5, -0.2, 0.0, 0.2, 0.5, 1.0 };

        // One session per conviction level, so nothing carries between them.
        var sizes = favs.ToDictionary(
            fav => fav,
            fav => BaseKinds.ToDictionary(
                k => k,
                k => new RegimeRouterSession(bars, null, HmmGeno(fav)).SizeGate(k, t)));

        foreach (var kind in BaseKinds)
            for (int i = 1; i < favs.Length; i++)
                Assert.True(sizes[favs[i]][kind] >= sizes[favs[i - 1]][kind] - 1e-12,
                            $"{kind}: raising favorability {favs[i - 1]}→{favs[i]} CUT size " +
                            $"{sizes[favs[i - 1]][kind]:F4}→{sizes[favs[i]][kind]:F4}");

        // Guard against a vacuous pass: the curve must actually move, not sit flat at one value.
        var all = sizes.Values.SelectMany(d => d.Values).ToList();
        Assert.Contains(all, s => s == 0.0);
        Assert.Contains(all, s => s > 0.0);
    }

    // RegimeRouterSession is documented as a pre-computed O(1) per-trade lookup, but IsActive wrote
    // _wasActive and Weight wrote _previousWeight on every call. Backtests share one session across
    // every coin and replay each coin's timeline from the start, so the same trade got a different
    // answer depending on which coin was evaluated first.
    [Fact]
    public void Routing_IsIndependentOfTheOrderTradesAreQueriedIn()
    {
        // Alternating HMM state so the weight crosses the activate/deactivate thresholds.
        var bars = new RegimeBar[600];
        for (int i = 0; i < bars.Length; i++)
        {
            var probs = new double[RegimeRouterGenotype.MaxHmmStates];
            probs[(i / 50) % 2] = 1.0;
            bars[i] = new RegimeBar(T0.AddHours(i), MarketRegime.Bull, 0.9, 500, probs);
        }

        var fav = new double[RegimeRouterGenotype.HmmFavorabilityLen];
        var bias = new double[RegimeRouterGenotype.HmmStrategyRows];
        for (int s = 0; s < RegimeRouterGenotype.HmmStrategyRows; s++)
        {
            fav[s * RegimeRouterGenotype.MaxHmmStates + 0] =  1.0;   // state 0 → favourable
            fav[s * RegimeRouterGenotype.MaxHmmStates + 1] = -1.0;   // state 1 → unfavourable
        }
        var geno = new RegimeRouterGenotype { Favorability = fav, Biases = bias, HmmStatesN = 4.0 };

        var times = Enumerable.Range(0, 60).Select(i => T0.AddHours(i * 10)).ToList();
        var kind  = RegimeRouterGA.StrategyKind.DipLong;

        var ascending = new RegimeRouterSession(bars, null, geno);
        var forward   = times.ToDictionary(t => t, t => (ascending.IsActive(kind, t), ascending.SizeGate(kind, t)));

        var descending = new RegimeRouterSession(bars, null, geno);
        var backward   = new Dictionary<DateTime, (bool, double)>();
        foreach (var t in Enumerable.Reverse(times))
            backward[t] = (descending.IsActive(kind, t), descending.SizeGate(kind, t));

        foreach (var t in times)
            Assert.Equal(forward[t], backward[t]);

        // Guard against a vacuous pass: the gate must actually toggle over this window.
        Assert.Contains(true,  forward.Values.Select(v => v.Item1));
        Assert.Contains(false, forward.Values.Select(v => v.Item1));
    }
}
