using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class StrategyFamilyGatingTests
{
    private static readonly DateTime Base = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static RegimeBar[] MakeRegimeSeries(params (MarketRegime regime, int hours)[] blocks)
    {
        var bars = new List<RegimeBar>();
        var t = Base;
        foreach (var (regime, hours) in blocks)
        {
            for (int i = 0; i < hours; i++)
            {
                bars.Add(new RegimeBar(t, regime, 0.8, 10, null));
                t = t.AddHours(1);
            }
        }
        return bars.ToArray();
    }

    private static Dictionary<string, IReadOnlyList<(DateTime Time, double Return)>> MakeTrades(
        params (string strategy, MarketRegime regime, int count, double avgReturn)[] specs)
    {
        var result = new Dictionary<string, IReadOnlyList<(DateTime Time, double Return)>>();
        var rng = new Random(42);
        foreach (var (strategy, regime, count, avgReturn) in specs)
        {
            var trades = new List<(DateTime, double)>();
            // Find bars matching this regime.
            var regimeBars = new List<DateTime>();
            var t = Base;
            for (int i = 0; i < 1000; i++)
            {
                // Simple regime assignment: first 300h = Bull, next 300h = Bear, next 300h = Ranging.
                MarketRegime r = i < 300 ? MarketRegime.Bull : i < 600 ? MarketRegime.Bear : MarketRegime.Ranging;
                if (r == regime) regimeBars.Add(t);
                t = t.AddHours(1);
            }
            for (int i = 0; i < Math.Min(count, regimeBars.Count); i++)
            {
                double ret = avgReturn + rng.NextGaussian() * Math.Abs(avgReturn) * 0.5;
                trades.Add((regimeBars[i], ret));
            }
            result[strategy] = trades;
        }
        return result;
    }

    [Fact]
    public void Build_TwoCorrelatedStrategies_SameFamily()
    {
        // Two strategies with identical returns → same family.
        var trades = new Dictionary<string, IReadOnlyList<(DateTime Time, double Return)>>();
        var rng = new Random(42);
        var t = Base;
        var returns = Enumerable.Range(0, 50).Select(_ => rng.NextDouble() * 4 - 1).ToList();
        trades["A"] = returns.Select((r, i) => (Base.AddHours(i), r)).ToList();
        trades["B"] = returns.Select((r, i) => (Base.AddHours(i), r)).ToList();

        var regimeSeries = MakeRegimeSeries((MarketRegime.Bull, 100));
        var gate = StrategyFamilyGating.Build(trades, regimeSeries, familyCorrThreshold: 0.5);

        Assert.Equal(gate.FamilyOf["A"], gate.FamilyOf["B"]);
    }

    [Fact]
    public void Build_UncorrelatedStrategies_DifferentFamilies()
    {
        // Two strategies with anti-correlated returns → different families.
        var trades = new Dictionary<string, IReadOnlyList<(DateTime Time, double Return)>>();
        var rng = new Random(42);
        var returnsA = Enumerable.Range(0, 50).Select(_ => rng.NextDouble() * 4 - 1).ToList();
        var returnsB = returnsA.Select(r => -r).ToList();
        trades["A"] = returnsA.Select((r, i) => (Base.AddHours(i), r)).ToList();
        trades["B"] = returnsB.Select((r, i) => (Base.AddHours(i), r)).ToList();

        var regimeSeries = MakeRegimeSeries((MarketRegime.Bull, 100));
        var gate = StrategyFamilyGating.Build(trades, regimeSeries, familyCorrThreshold: 0.5);

        Assert.NotEqual(gate.FamilyOf["A"], gate.FamilyOf["B"]);
    }

    [Fact]
    public void Build_ProfitableFamilyInBull_ActiveInBull()
    {
        var trades = MakeTrades(
            ("LongStrat", MarketRegime.Bull, 30, avgReturn: 2.0),
            ("ShortStrat", MarketRegime.Bear, 30, avgReturn: 2.0));

        var regimeSeries = MakeRegimeSeries(
            (MarketRegime.Bull, 300),
            (MarketRegime.Bear, 300),
            (MarketRegime.Ranging, 300));

        var gate = StrategyFamilyGating.Build(trades, regimeSeries);

        // LongStrat should be active in Bull (profitable there).
        Assert.True(StrategyFamilyGating.IsActive(gate, "LongStrat", MarketRegime.Bull));
        // ShortStrat should be active in Bear (profitable there).
        Assert.True(StrategyFamilyGating.IsActive(gate, "ShortStrat", MarketRegime.Bear));
    }

    [Fact]
    public void Build_LosingFamilyInRegime_NotActive()
    {
        var trades = MakeTrades(
            ("LongStrat", MarketRegime.Bull, 30, avgReturn: -1.0));  // losing in Bull

        var regimeSeries = MakeRegimeSeries(
            (MarketRegime.Bull, 300),
            (MarketRegime.Bear, 300));

        var gate = StrategyFamilyGating.Build(trades, regimeSeries);

        Assert.False(StrategyFamilyGating.IsActive(gate, "LongStrat", MarketRegime.Bull));
    }

    [Fact]
    public void Build_InsufficientTrades_NotActive()
    {
        var trades = MakeTrades(
            ("LongStrat", MarketRegime.Bull, 5, avgReturn: 5.0));  // only 5 trades, below min

        var regimeSeries = MakeRegimeSeries(
            (MarketRegime.Bull, 300));

        var gate = StrategyFamilyGating.Build(trades, regimeSeries);

        Assert.False(StrategyFamilyGating.IsActive(gate, "LongStrat", MarketRegime.Bull));
    }

    [Fact]
    public void FamilyConfidence_HigherPF_HigherConfidence()
    {
        var trades = MakeTrades(
            ("GoodStrat", MarketRegime.Bull, 30, avgReturn: 5.0),
            ("OkStrat", MarketRegime.Bull, 30, avgReturn: 1.0));

        var regimeSeries = MakeRegimeSeries((MarketRegime.Bull, 300));
        var gate = StrategyFamilyGating.Build(trades, regimeSeries);

        double confGood = StrategyFamilyGating.FamilyConfidence(gate, "GoodStrat", MarketRegime.Bull);
        double confOk = StrategyFamilyGating.FamilyConfidence(gate, "OkStrat", MarketRegime.Bull);

        Assert.True(confGood >= confOk);
    }

    [Fact]
    public void IsActive_UnknownStrategy_ReturnsFalse()
    {
        var trades = MakeTrades(("A", MarketRegime.Bull, 30, 2.0));
        var regimeSeries = MakeRegimeSeries((MarketRegime.Bull, 300));
        var gate = StrategyFamilyGating.Build(trades, regimeSeries);

        Assert.False(StrategyFamilyGating.IsActive(gate, "NonExistent", MarketRegime.Bull));
    }
}
