using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// research/tradecosts.py mirrors TradeCosts and the funding floor so research labels are measured the
// way edgetest measures. These are the SAME numbers its selftest pins: if either side changes, both
// break. (Overlay post-mortem 2026-10-04: research EV used flat costs and was optimistic for deep rungs.)
public class TradeCostsParityTests
{
    [Fact]
    public void RoundTripPct_MatchesThePythonMirror()
    {
        Assert.Equal(0.21, TradeCosts.RoundTripPct(3.0, isStop: false, 0.18), 12);
        Assert.Equal(0.11 + 0.2 - (0.035 + 0.1), TradeCosts.RoundTripPct(6.0, false, 0.18, entryMaker: true), 12);
        Assert.Equal(0.31 + 0.18 * 6.0 - 0.135, TradeCosts.RoundTripPct(6.0, true, 0.18, entryMaker: true, exitMaker: true), 12);
        Assert.Equal(0.11 + 0.05 - 2 * (0.035 + 0.025), TradeCosts.RoundTripPct(1.5, false, 0.18, entryMaker: true, exitMaker: true), 12);
    }

    [Fact]
    public void SettlementCount_MatchesThePythonMirror()
    {
        var t0 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, FundingRateSession.CountSettlements(t0, t0.AddHours(8)));
        Assert.Equal(0, FundingRateSession.CountSettlements(t0.AddHours(1), t0.AddHours(7)));
        Assert.Equal(1, FundingRateSession.CountSettlements(t0.AddHours(8), t0.AddHours(16)));
        Assert.Equal(3, FundingRateSession.CountSettlements(t0.AddHours(7), t0.AddHours(25)));
        Assert.Equal(0, FundingRateSession.CountSettlements(t0.AddHours(5), t0.AddHours(5)));
    }
}
