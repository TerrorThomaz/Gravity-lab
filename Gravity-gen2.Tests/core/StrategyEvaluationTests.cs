using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

public class StrategyEvaluationTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static StrategyEvaluation.Trade T(int i, double ret, MarketRegime reg = MarketRegime.Bull)
        => new(T0.AddHours(i), T0.AddHours(i + 1), ret, "s", "SYM", reg);

    [Fact]
    public void NoTrades_GradesF_RatherThanDividingByZero()
    {
        var r = StrategyEvaluation.Evaluate(Array.Empty<StrategyEvaluation.Trade>());
        Assert.Equal('F', r.Verdict.Overall);
        Assert.Equal(0, r.N);
    }

    [Fact]
    public void RegimeSplitEdge_IsCaughtByRobustness_NotHiddenByABlendedPF()
    {
        // The exact failure this grade exists for. FadeShort scored PF 5.28 in Bear and 0.38 in
        // Bull; the blended number sat near 1.2 and looked merely mediocre, so six retrains chased
        // the signal when the problem was that one regime was unprofitable.
        var trades = new List<StrategyEvaluation.Trade>();
        for (int i = 0; i < 60; i++)  trades.Add(T(i,      +3.0, MarketRegime.Bear));
        for (int i = 60; i < 160; i++) trades.Add(T(i,     -1.2, MarketRegime.Bull));

        var r = StrategyEvaluation.Evaluate(trades);
        Assert.True(r.ByRegime[MarketRegime.Bear].Pf > 1.0);
        Assert.True(r.ByRegime[MarketRegime.Bull].Pf < 1.0);
        Assert.Contains("regime", r.Verdict.Summary);
        Assert.NotEqual('A', r.Verdict.Robustness);
    }

    [Fact]
    public void ConsistentEdgeAcrossRegimes_ScoresRobustnessA()
    {
        // The regime cycle and the win/loss cycle must NOT share a period. An earlier version of
        // this fixture used `i % 3` for both, which put every loss in the same regime — so the
        // grade correctly returned C and the test was asserting the opposite of what it described.
        var trades = new List<StrategyEvaluation.Trade>();
        var regs = new[] { MarketRegime.Bull, MarketRegime.Bear, MarketRegime.Ranging };
        for (int i = 0; i < 300; i++)
            trades.Add(T(i, i % 4 == 0 ? -1.0 : +1.5, regs[i % 3]));

        var r = StrategyEvaluation.Evaluate(trades);
        Assert.Equal('A', r.Verdict.Robustness);
    }

    [Fact]
    public void RiskOfRuin_IsHighForAStrategyThatCanLoseHalfTheAccount()
    {
        // A negative-expectancy book must show real ruin probability, not a comfortable number.
        var trades = Enumerable.Range(0, 400).Select(i => T(i, i % 2 == 0 ? +5.0 : -8.0)).ToList();
        var r = StrategyEvaluation.Evaluate(trades, posFrac: 0.5, seed: 1);
        Assert.True(r.RiskOfRuin > 0.05, $"risk of ruin {r.RiskOfRuin:P1} is implausibly low for a losing book");
        Assert.NotEqual('A', r.Verdict.Risk);
    }

    [Fact]
    public void SteadyWinner_HasNegligibleRuinAndPositiveFifthPercentile()
    {
        var trades = Enumerable.Range(0, 600).Select(i => T(i, i % 4 == 0 ? -0.5 : +1.0)).ToList();
        var r = StrategyEvaluation.Evaluate(trades, posFrac: 0.05, seed: 1);
        Assert.True(r.RiskOfRuin < 0.01);
        Assert.True(r.P05PathReturn > 0, "a consistently profitable book should have a positive p05 path");
    }

    [Fact]
    public void VaRAndCVaR_AreTailMeasures_AndCVaRIsTheWorse()
    {
        // 30 bad trades out of 200, so the 5th-percentile index lands well inside the bad block
        // rather than exactly on its boundary. The earlier fixture used exactly 10 of 200, which
        // put the index on the first GOOD trade — testing an indexing convention, not the property.
        var trades = Enumerable.Range(0, 200).Select(i => T(i, i < 30 ? -20.0 : +1.0)).ToList();
        var r = StrategyEvaluation.Evaluate(trades);
        Assert.True(r.CVaR95 <= r.VaR95, "CVaR is the mean BEYOND VaR, so it cannot be less severe");
        Assert.True(r.VaR95 < 0);
    }

    [Fact]
    public void RegimeSwitchingBootstrap_IsWiderThanTheSampleWhenRegimesCluster()
    {
        // The point of clustering: a run of bad trades compounds. If the bootstrap broke the runs
        // apart, p05 would sit close to the median and the tail would be understated.
        var trades = new List<StrategyEvaluation.Trade>();
        for (int i = 0; i < 200; i++)  trades.Add(T(i, +2.0, MarketRegime.Bull));
        for (int i = 200; i < 260; i++) trades.Add(T(i, -6.0, MarketRegime.Bear));

        var r = StrategyEvaluation.Evaluate(trades, posFrac: 0.05, seed: 7);
        Assert.True(r.P95PathReturn - r.P05PathReturn > 5.0,
            "clustered regimes must produce a materially wide outcome distribution");
        Assert.True(r.P05PathReturn < r.MedianPathReturn);
    }

    [Fact]
    public void IsDeterministic_ForAGivenSeed()
    {
        var trades = Enumerable.Range(0, 200).Select(i => T(i, i % 3 == 0 ? -1.0 : +1.0)).ToList();
        var a = StrategyEvaluation.Evaluate(trades, seed: 42);
        var b = StrategyEvaluation.Evaluate(trades, seed: 42);
        Assert.Equal(a.RiskOfRuin, b.RiskOfRuin);
        Assert.Equal(a.MedianPathReturn, b.MedianPathReturn, 9);
    }

    [Fact]
    public void ThinSample_IsGradedDown_EvenWithAGreatProfitFactor()
    {
        // 15 spectacular trades is not evidence. Sample size is a separate axis precisely so a
        // small lucky run cannot buy an A.
        var trades = Enumerable.Range(0, 15).Select(i => T(i, +8.0)).ToList();
        var r = StrategyEvaluation.Evaluate(trades);
        Assert.Equal('F', r.Verdict.Sample);
        Assert.NotEqual('A', r.Verdict.Overall);
    }
}
