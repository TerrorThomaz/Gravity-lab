using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// FadeShort's stops used to trigger on the 15m close but fill at the stop LEVEL — an execution no
// venue offers, and the whole of FadeShort's apparent edge (PF 1.10 → 0.97 under honest fills).
// Pinned so the optimistic fill cannot come back.
public class FadeShortStopFillTests
{
    [Fact]
    public void CloseTriggeredStop_FillsAtTheClose_NotTheLevel()
    {
        // short entered at 100, stop at 101, the bar CLOSES at 108: the bot sees it at 108 and fills there
        Assert.Equal(108.0, FadeShortSimulator.StopExitPx(false, close: 108, open: 100.5, hardLevel: 101, maeLevel: double.MaxValue));
    }

    [Fact]
    public void WickTriggeredStop_FillsAtTheTighterLevel_ButNoBetterThanAGapOpen()
    {
        Assert.Equal(101.0, FadeShortSimulator.StopExitPx(true, close: 100.2, open: 100.5, hardLevel: 101, maeLevel: 103));
        Assert.Equal(104.0, FadeShortSimulator.StopExitPx(true, close: 100.2, open: 104, hardLevel: 101, maeLevel: 103));   // gapped through both
    }
}
