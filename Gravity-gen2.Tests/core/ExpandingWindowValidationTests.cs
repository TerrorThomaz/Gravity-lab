using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// ExpandingWindowValidation cuts its walk-forward windows PER COIN, as fractions of each
// coin's own array, instead of deriving absolute bar indices from the shortest coin in the
// set and applying them to everyone. These tests pin that contract: two coins with
// different history lengths must each expand through their OWN history.
public class ExpandingWindowValidationTests
{
    private static readonly FitnessConfig NoEmbargo = new FitnessConfig() with { EmbargoPct = 0.0 };

    // 4000 and 16000 bars: every fraction below divides exactly, so the expected indices
    // are unambiguous.
    private const int ShortLen = 4_000;
    private const int LongLen  = 16_000;

    [Fact]
    public void WindowForCoin_TrainEnd_ScalesWithEachCoinsOwnLength()
    {
        // Schedule: trainEnd = len/4 + w*len/8  ->  1/4, 3/8, 1/2, 5/8 of the coin.
        var expectedShort = new[] { 1_000, 1_500, 2_000, 2_500 };
        var expectedLong  = new[] { 4_000, 6_000, 8_000, 10_000 };

        for (int w = 0; w < 4; w++)
        {
            var s = ExpandingWindowValidation.WindowForCoin(ShortLen, w, NoEmbargo);
            var l = ExpandingWindowValidation.WindowForCoin(LongLen,  w, NoEmbargo);

            Assert.NotNull(s);
            Assert.NotNull(l);
            Assert.Equal(expectedShort[w], s!.Value.TrainEnd);
            Assert.Equal(expectedLong[w],  l!.Value.TrainEnd);

            // The long coin is 4x the short one, so every bound is 4x — proportional to
            // its own length, not clipped to the shorter coin.
            Assert.Equal(4 * s.Value.TrainEnd,  l!.Value.TrainEnd);
            Assert.Equal(4 * s.Value.TestStart, l.Value.TestStart);
            Assert.Equal(4 * s.Value.TestEnd,   l.Value.TestEnd);
        }
    }

    [Fact]
    public void WindowForCoin_LongCoinUsesBarsBeyondTheShortCoinsLength()
    {
        // The old code cut the schedule on minLen (the shortest coin), so no window ever
        // reached past bar 4000 on the 16000-bar coin. Now the long coin's very first
        // window already trains to bar 4000 and TESTS beyond it.
        var l0 = ExpandingWindowValidation.WindowForCoin(LongLen, 0, NoEmbargo);
        Assert.NotNull(l0);
        Assert.True(l0!.Value.TestEnd > ShortLen,
            $"long coin's first test window should extend past the short coin's length, got {l0.Value.TestEnd}");

        var l3 = ExpandingWindowValidation.WindowForCoin(LongLen, 3, NoEmbargo);
        Assert.NotNull(l3);
        Assert.True(l3!.Value.TrainEnd > ShortLen,
            $"long coin's last window should train well past the short coin's length, got {l3.Value.TrainEnd}");
    }

    [Fact]
    public void WindowForCoin_NeverRunsPastTheCoinsOwnArray()
    {
        foreach (int len in new[] { 400, 1_234, ShortLen, LongLen })
        {
            for (int w = 0; w < 4; w++)
            {
                var win = ExpandingWindowValidation.WindowForCoin(len, w, new FitnessConfig());
                if (win is null) continue;
                var (trainEnd, testStart, testEnd) = win.Value;

                Assert.True(trainEnd > 0);
                Assert.True(trainEnd <= testStart);   // train and test never overlap
                Assert.True(testStart < testEnd);
                Assert.True(testEnd <= len,
                    $"len={len} w={w}: testEnd {testEnd} ran past the coin's own array");
            }
        }
    }

    [Fact]
    public void WindowForCoin_Windows_ExpandMonotonically()
    {
        int prevTrainEnd = 0;
        for (int w = 0; w < 4; w++)
        {
            var win = ExpandingWindowValidation.WindowForCoin(LongLen, w, new FitnessConfig());
            Assert.NotNull(win);
            Assert.True(win!.Value.TrainEnd > prevTrainEnd,
                $"window {w} must train on more history than window {w - 1}");
            prevTrainEnd = win.Value.TrainEnd;
        }
    }

    [Fact]
    public void WindowForCoin_EmbargoGapSeparatesTrainFromTest()
    {
        var cfg = new FitnessConfig(); // EmbargoPct = 0.05
        var win = ExpandingWindowValidation.WindowForCoin(LongLen, 0, cfg);
        Assert.NotNull(win);

        int stepSize = LongLen / 8;                 // 2000
        int expectedGap = (int)(stepSize * cfg.EmbargoPct); // 100
        Assert.True(expectedGap > 0);
        Assert.Equal(expectedGap, win!.Value.TestStart - win.Value.TrainEnd);

        // Embargo scales with the coin's own step size too.
        var shortWin = ExpandingWindowValidation.WindowForCoin(ShortLen, 0, cfg);
        Assert.NotNull(shortWin);
        Assert.Equal((int)(ShortLen / 8 * cfg.EmbargoPct),
                     shortWin!.Value.TestStart - shortWin.Value.TrainEnd);
    }

    [Fact]
    public void WindowForCoin_TooShortCoinIsSkipped_LongCoinStillGetsWindows()
    {
        // A coin that cannot support a >= 40-bar train and test block is skipped for that
        // window instead of forcing the schedule down for every other coin.
        Assert.Null(ExpandingWindowValidation.WindowForCoin(0,   0, NoEmbargo));
        Assert.Null(ExpandingWindowValidation.WindowForCoin(100, 0, NoEmbargo));  // step 12 bars
        Assert.NotNull(ExpandingWindowValidation.WindowForCoin(LongLen, 0, NoEmbargo));
    }
}
