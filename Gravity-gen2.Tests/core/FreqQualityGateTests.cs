using System;
using System.Collections.Generic;
using System.Linq;
using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The frequency bonus used to be unconditional on trade quality, so any gene that could loosen an
// entry filter was paid for the extra volume regardless of whether the extra trades had an edge.
// RipShort's regime-adapt gene did exactly that: +27% trades at an unchanged train PF (7.10 vs
// 7.03) collected ~25% more fitness, while held-out PF went 1.00 -> 0.95.
public class FreqQualityGateTests
{
    private static List<double> Book(int n, double win, double loss, double winRate)
    {
        var r = new List<double>(n);
        for (int i = 0; i < n; i++) r.Add(i < (int)(n * winRate) ? win : loss);
        return r;
    }

    private static double Score(List<double> rets) =>
        FoldScoreHelper.Canonical(rets, posFrac: 0.05, minTradesPerFold: 20, cfg: new FitnessConfig());

    [Fact]
    public void MoreBreakevenTrades_NoLongerBuyFitness()
    {
        // PF ~1.0: the trades are a coin flip. Tripling their count must not raise the score,
        // because at breakeven freqQuality is 0 and the bonus term collapses to 1.0.
        var few  = Book(60,  1.0, -1.0, 0.5);
        var many = Book(180, 1.0, -1.0, 0.5);
        Assert.True(Score(many) <= Score(few) + 1e-9,
            $"breakeven volume still pays: few={Score(few):F3} many={Score(many):F3}");
    }

    [Fact]
    public void MoreGoodTrades_StillPay()
    {
        // The incentive to trade must survive — this removes the UNEARNED payment, not the
        // reward for genuine volume. A strong book tripled should score higher.
        var few  = Book(60,  2.0, -1.0, 0.65);
        var many = Book(180, 2.0, -1.0, 0.65);
        Assert.True(Score(many) > Score(few),
            $"good volume stopped paying: few={Score(few):F3} many={Score(many):F3}");
    }

    [Fact]
    public void LosingTradesAddedToAWinner_DoNotRaiseTheScore()
    {
        // The exact exploit: keep the winners, bolt on marginal losers. Per-trade edge falls,
        // count rises. Under the old term the log bonus could outweigh the dilution.
        var clean   = Book(80, 2.0, -1.0, 0.70);
        var diluted = new List<double>(clean);
        diluted.AddRange(Enumerable.Repeat(-0.2, 120));   // 120 small losers
        Assert.True(Score(diluted) < Score(clean),
            $"dilution still rewarded: clean={Score(clean):F3} diluted={Score(diluted):F3}");
    }

    [Fact]
    public void FreqWZero_IsStillABitForBitNoOp()
    {
        var rets = Book(200, 2.0, -1.0, 0.65);
        var off  = new FitnessConfig(FreqW: 0.0);
        double a = FoldScoreHelper.Canonical(rets, 0.05, 20, cfg: off);
        double b = FoldScoreHelper.Canonical(rets, 0.05, 20, cfg: off);
        Assert.Equal(a, b, 12);
        // And changing the gate anchors must not move a score when the term is switched off.
        double c = FoldScoreHelper.Canonical(rets, 0.05, 20,
            cfg: new FitnessConfig(FreqW: 0.0, FreqPfFull: 5.0, FreqAvgFullPct: 9.0));
        Assert.Equal(a, c, 12);
    }
}
