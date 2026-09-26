using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The gate that decides which strategies route in which regime, re-fitted on a trailing window
// instead of once over all of history. genotypes/strategy_family_gate.json is the static version:
// fitted on the full training trade set and then used to gate those same trades, with
// FamilyConfidence = in-sample PF / 3 feeding position size directly. Nothing measured through it
// is out-of-sample, which is why it cannot support an edge claim.
public class RollingStrategyGateTests
{
    private static readonly DateTime T0 = new(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // A bar series in one regime, hourly, long enough to cover every trade time used below.
    private static RegimeBar[] Bars(MarketRegime regime, int hours) =>
        Enumerable.Range(0, hours)
                  .Select(i => new RegimeBar(T0.AddHours(i), regime, 0.8, 500, null))
                  .ToArray();

    // `n` trades for `strategy`, one per hour from hour `fromHour`, each returning `ret`.
    // Held one hour, so entry and exit sit in the same regime bucket.
    private static IEnumerable<(string Strategy, DateTime Entry, DateTime Exit, double Return)> Trades(
        string strategy, int fromHour, int n, double ret) =>
        Enumerable.Range(0, n).Select(i => (strategy, T0.AddHours(fromHour + i), T0.AddHours(fromHour + i + 1), ret));

    // A profitable block followed by a losing block: PF > 1 early, PF < 1 late.
    private static List<(string Strategy, DateTime Entry, DateTime Exit, double Return)> WinThenLose(
        string strategy, int winN, int loseN, int spacingHours = 1)
    {
        var all = new List<(string, DateTime, DateTime, double)>();
        for (int i = 0; i < winN; i++)
        {
            var e = T0.AddHours(i * spacingHours);
            all.Add((strategy, e, e.AddHours(1), i % 4 == 0 ? -1.0 : 2.0));
        }
        for (int i = 0; i < loseN; i++)
        {
            var e = T0.AddHours((winN + i) * spacingHours);
            all.Add((strategy, e, e.AddHours(1), i % 4 == 0 ? 2.0 : -1.0));
        }
        return all;
    }

    // LOOKAHEAD GUARD. The decision that applies at time t must not move when trades at or after t
    // are added. This is the property the static gate cannot have: it is fitted on everything.
    [Fact]
    public void Decision_IgnoresEveryTradeAtOrAfterThePeriodItApplies()
    {
        var bars = Bars(MarketRegime.Bull, 6000);
        var past = Trades("DipLong", 0, 400, 1.0).ToList();
        var withFuture = past.Concat(Trades("DipLong", 3000, 400, -5.0)).ToList();

        var at = T0.AddHours(2000);
        var a = RollingStrategyGate.ByRegime(past, bars);
        var b = RollingStrategyGate.ByRegime(withFuture, bars);

        Assert.Equal(a.IsActive("DipLong", MarketRegime.Bull, at),
                     b.IsActive("DipLong", MarketRegime.Bull, at));
        Assert.Equal(a.Confidence("DipLong", MarketRegime.Bull, at),
                     b.Confidence("DipLong", MarketRegime.Bull, at), 12);
    }

    // EDGE DECAY. This is the whole point of re-fitting: a strategy that stops working must route
    // off by itself, with no retrain and no human in the loop.
    [Fact]
    public void StrategyWhoseEdgeDecays_RoutesOffOnceTheTrailingWindowTurns()
    {
        var bars = Bars(MarketRegime.Bull, 20000);
        // 4h spacing so a 180-day trailing window holds ~1080 hours worth of trades, not all of them.
        var trades = WinThenLose("DipLong", winN: 900, loseN: 900, spacingHours: 4);
        var gate = RollingStrategyGate.ByRegime(trades, bars);

        // Early: the trailing window sees only the profitable block.
        Assert.True(gate.IsActive("DipLong", MarketRegime.Bull, T0.AddHours(3000)),
                    "profitable trailing window must route on");
        // Late: the trailing window sees only the losing block.
        Assert.False(gate.IsActive("DipLong", MarketRegime.Bull, T0.AddHours(8000)),
                     "losing trailing window must route off");
    }

    // COLD START. Before the trailing window holds enough trades there is no evidence either way,
    // and the conservative reading of no evidence is "off". The static gate has no such state:
    // it is fitted on all of history, so it is equally confident on day one.
    [Fact]
    public void BeforeEnoughHistory_TheGateIsOff()
    {
        var bars = Bars(MarketRegime.Bull, 6000);
        var gate = RollingStrategyGate.ByRegime(Trades("DipLong", 0, 400, 1.0).ToList(), bars);

        Assert.False(gate.IsActive("DipLong", MarketRegime.Bull, T0.AddHours(1)));
        Assert.Equal(0.0, gate.Confidence("DipLong", MarketRegime.Bull, T0.AddHours(1)), 12);
    }

    // Confidence must not be a rescaled in-sample profit factor. It sizes positions, so an
    // unbounded PF from a thin bucket would size hardest exactly where the sample is weakest.
    [Fact]
    public void Confidence_IsBoundedAndRisesWithTrailingProfitability()
    {
        var bars = Bars(MarketRegime.Bull, 20000);
        var weak   = RollingStrategyGate.ByRegime(WinThenLose("DipLong", 900, 0, 4), bars);
        var strong = RollingStrategyGate.ByRegime(
            Enumerable.Range(0, 900).Select(i =>
                ("DipLong", T0.AddHours(i * 4), T0.AddHours(i * 4 + 1), 2.0)).ToList(), bars);
        var at = T0.AddHours(3000);

        double cw = weak.Confidence("DipLong", MarketRegime.Bull, at);
        double cs = strong.Confidence("DipLong", MarketRegime.Bull, at);

        Assert.InRange(cw, 0.0, 1.0);
        Assert.InRange(cs, 0.0, 1.0);
        Assert.True(cs > cw, $"an all-winning window ({cs:F3}) must not size below a mixed one ({cw:F3})");
    }

    // The per-coin dimension: the same strategy can work on one symbol and not another, and the
    // screen must separate them. Nothing above exercises more than one bucket at a time.
    [Fact]
    public void BySymbol_ScreensEachCoinOnItsOwnTrailingRecord()
    {
        var trades = new List<(string, string, DateTime, double)>();
        for (int i = 0; i < 400; i++)
        {
            var exit = T0.AddHours(i * 4 + 1);
            trades.Add(("DipLong", "GOODUSDT", exit, i % 4 == 0 ? -1.0 : 2.0));   // PF 6
            trades.Add(("DipLong", "BADUSDT",  exit, i % 4 == 0 ?  2.0 : -1.0));  // PF 0.67
        }
        var screen = RollingStrategyGate.BySymbol(trades);
        var at = T0.AddHours(1200);

        Assert.True(screen.At("DipLong", "GOODUSDT", at).Active);
        Assert.False(screen.At("DipLong", "BADUSDT", at).Active);
    }

    // A strategy the gate has never seen must not be silently admitted at full size.
    [Fact]
    public void UnknownStrategy_IsOff()
    {
        var bars = Bars(MarketRegime.Bull, 6000);
        var gate = RollingStrategyGate.ByRegime(Trades("DipLong", 0, 400, 1.0).ToList(), bars);

        Assert.False(gate.IsActive("NeverTraded", MarketRegime.Bull, T0.AddHours(3000)));
        Assert.Equal(0.0, gate.Confidence("NeverTraded", MarketRegime.Bull, T0.AddHours(3000)), 12);
    }
}

// Risk-parity sizing needs each strategy's trailing volatility, measured on the SAME
// already-closed window the gate decides on. Sizing a strategy down because we observed its
// drawdown on the window being sized is the in-sample error the static family gate made.
public class RollingStrategyGateVolTests
{
    private static readonly DateTime T0 = new(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RollingStrategyGate Gate(double amplitude) =>
        RollingStrategyGate.BySymbol(
            Enumerable.Range(0, 900).Select(i =>
                ("S", "B", T0.AddHours(i * 4 + 1), i % 2 == 0 ? amplitude : -amplitude * 0.9)));

    [Fact]
    public void Verdict_ReportsTrailingVolatility_RisingWithReturnDispersion()
    {
        var at = T0.AddHours(3000);
        double calm   = Gate(1.0).At("S", "B", at).Vol;
        double wild   = Gate(5.0).At("S", "B", at).Vol;

        Assert.True(calm > 0, "a series with dispersion must report non-zero volatility");
        Assert.True(wild > calm * 3, $"5x the amplitude reported vol {wild:F3} against {calm:F3}");
    }

    // Vol must be available even when the gate says OFF — a strategy routed off still has a
    // measured risk profile, and the sizing layer reads it independently of the on/off decision.
    [Fact]
    public void Verdict_ReportsVolatility_EvenWhenGatedOff()
    {
        var losing = RollingStrategyGate.BySymbol(
            Enumerable.Range(0, 900).Select(i =>
                ("S", "B", T0.AddHours(i * 4 + 1), i % 2 == 0 ? 1.0 : -3.0)));
        var v = losing.At("S", "B", T0.AddHours(3000));

        Assert.False(v.Active);
        Assert.True(v.Vol > 0, "volatility must be measured regardless of the gate decision");
    }
}
