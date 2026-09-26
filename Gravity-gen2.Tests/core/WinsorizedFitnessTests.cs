using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// `gain` is a SUM, so one +50% trade outranks a hundred +0.5% trades. Three live terms then pay
// again for the same shape: rrMult (avgWin/avgLoss), SortinoW — Sortino divides by DOWNSIDE
// deviation, so a monster winner lifts the numerator and leaves the denominator untouched — and
// TailRatioW, which is literally a p95/p5 bonus. Only SharpeW partially self-corrects, because its
// denominator does include the winner.
//
// The finalist screen measures exactly what that combination buys: 94-99% of the fold score lost
// when the best 1% of trades is deleted, on all three live genotypes. Winsorizing winners removes
// the payment at source — a genotype can no longer buy score with a lottery ticket, only with
// many moderate trades.
//
// WINNERS ONLY. Trimming both tails would flatter a strategy by deleting its worst losses, which
// is the opposite of the question being asked.
public class WinsorizedFitnessTests
{
    // The transform is ON by default now, so "off" has to be stated explicitly.
    private static readonly FitnessConfig Off = new() { WinsorizeWinnerPct = 0.0 };
    private static FitnessConfig On(double pct) => Off with { WinsorizeWinnerPct = pct };

    // 199 ordinary trades plus one monster: the classic concentrated book.
    private static List<double> Concentrated()
    {
        var r = new List<double>();
        for (int i = 0; i < 100; i++) r.Add(0.5);
        for (int i = 0; i < 99;  i++) r.Add(-0.5);
        r.Add(50.0);
        return r;
    }

    // Same trade count, same sign mix, edge spread evenly across the book.
    private static List<double> Broad()
    {
        var r = new List<double>();
        for (int i = 0; i < 100; i++) r.Add(1.0);
        for (int i = 0; i < 99;  i++) r.Add(-0.5);
        r.Add(1.0);
        return r;
    }

    [Fact]
    public void ZeroPct_IsABitIdenticalNoOp()
    {
        var r = Concentrated();
        Assert.Equal(FoldScoreHelper.Canonical(r, 0.02, 20, Off),
                     FoldScoreHelper.Canonical(r, 0.02, 20, On(0.0)));
    }

    [Fact]
    public void Winsorizing_CollapsesTheScoreOfAConcentratedBook()
    {
        var r = Concentrated();
        double raw = FoldScoreHelper.Canonical(r, 0.02, 20, Off);
        double win = FoldScoreHelper.Canonical(r, 0.02, 20, On(0.05));

        Assert.True(win < raw * 0.5,
            $"a book whose edge is one trade scored {raw:F3} raw and {win:F3} winsorized — " +
            "the cap is not removing the lottery payment");
    }

    [Fact]
    public void Winsorizing_BarelyTouchesABroadBook()
    {
        var r = Broad();
        double raw = FoldScoreHelper.Canonical(r, 0.02, 20, Off);
        double win = FoldScoreHelper.Canonical(r, 0.02, 20, On(0.05));

        Assert.True(win > raw * 0.9,
            $"a book with evenly spread edge lost too much: {raw:F3} raw vs {win:F3} winsorized");
    }

    // The discrimination the GA actually needs: before winsorizing, the concentrated book outranks
    // the broad one on identical trade counts. After, it must not.
    [Fact]
    public void Winsorizing_ReversesThePreferenceForConcentration()
    {
        double concRaw  = FoldScoreHelper.Canonical(Concentrated(), 0.02, 20, Off);
        double broadRaw = FoldScoreHelper.Canonical(Broad(),        0.02, 20, Off);
        Assert.True(concRaw > broadRaw, "fixture assumption: raw fitness prefers the lottery ticket");

        double concWin  = FoldScoreHelper.Canonical(Concentrated(), 0.02, 20, On(0.05));
        double broadWin = FoldScoreHelper.Canonical(Broad(),        0.02, 20, On(0.05));
        Assert.True(broadWin > concWin,
            $"after winsorizing, the lottery ticket still wins ({concWin:F3} vs {broadWin:F3})");
    }

    // ── The transform itself ─────────────────────────────────────────────────────────────────

    // Canonical walks `returns` and `maePct` index-by-index. A winsorization that sorted or filtered
    // would silently pair each trade's return with another trade's excursion.
    [Fact]
    public void WinsorizeWinners_PreservesLengthAndOrder()
    {
        var r   = new List<double> { -3.0, 0.5, 40.0, -1.0, 0.7, 0.6, 0.4, -2.0, 0.55, 0.45 };
        var got = FoldScoreHelper.WinsorizeWinners(r, 0.05);

        Assert.Equal(r.Count, got.Count);
        for (int i = 0; i < r.Count; i++)
        {
            Assert.True(got[i] <= r[i] + 1e-12, $"index {i} rose from {r[i]} to {got[i]}");
            Assert.True(Math.Sign(got[i]) == Math.Sign(r[i]) || got[i] == 0.0,
                $"index {i} changed sign: {r[i]} -> {got[i]}");
        }
    }

    [Fact]
    public void WinsorizeWinners_NeverTouchesLosses()
    {
        var r   = new List<double> { -30.0, -0.5, 1.0, 2.0, 3.0, 99.0 };
        var got = FoldScoreHelper.WinsorizeWinners(r, 0.05);

        Assert.Equal(-30.0, got[0]);
        Assert.Equal(-0.5,  got[1]);
    }

    [Fact]
    public void WinsorizeWinners_DoesNotMutateTheCallersList()
    {
        var r = new List<double> { 1.0, 2.0, 99.0 };
        FoldScoreHelper.WinsorizeWinners(r, 0.05);
        Assert.Equal(99.0, r[2]);
    }

    [Fact]
    public void WinsorizeWinners_AllLossesIsANoOp()
    {
        var r   = new List<double> { -1.0, -2.0, -3.0 };
        var got = FoldScoreHelper.WinsorizeWinners(r, 0.05);
        Assert.Equal(r, got);
    }
}
