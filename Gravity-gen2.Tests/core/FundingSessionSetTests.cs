using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// Covers the second mechanism that never reached the commands that matter: real, PER-SYMBOL
// funding rates. The two pre-existing call sites fetched "BTCUSDT" alone and applied that one
// series to ~190 symbols; the backtest commands constructed no session at all.
//
// Also covers the 8h assumption baked into the fetch depth: `Needed = 3504` was commented
// "3.2yr at 8h" and is 1.6yr for a 4h-settling symbol.
public class FundingSessionSetTests
{
    static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static FundingBar[] Series(double intervalHours, int count, double rate) =>
        Enumerable.Range(0, count)
                  .Select(i => new FundingBar(T0.AddHours(i * intervalHours), rate))
                  .ToArray();

    static CandleFetcher.FundingSessions Set(params (string Sym, FundingBar[] Bars)[] entries)
    {
        var sessions = new Dictionary<string, FundingRateSession>();
        var info     = new List<CandleFetcher.FundingSeriesInfo>();
        foreach (var (sym, bars) in entries)
        {
            info.Add(CandleFetcher.DescribeFundingSeries(sym, bars));
            if (bars.Length > 0) sessions[sym] = new FundingRateSession(bars);
        }
        return new CandleFetcher.FundingSessions(sessions, info);
    }

    // ── The core requirement: a symbol gets ITS OWN rates, never a neighbour's ────────────
    [Fact]
    public void For_SelectsTheSessionBelongingToTheRequestedSymbol()
    {
        var set = Set(
            ("BTCUSDT", Series(8, 100, 0.0001)),    // calm
            ("PEPEUSDT", Series(8, 100, 0.0025)));  // crowded long

        var btc  = set.For("BTCUSDT");
        var pepe = set.For("PEPEUSDT");

        Assert.NotNull(btc);
        Assert.NotNull(pepe);
        Assert.NotSame(btc, pepe);

        var t = T0.AddHours(50);
        Assert.Equal(0.0001, btc!.GetRate(t), 10);
        Assert.Equal(0.0025, pepe!.GetRate(t), 10);

        // The bug this replaces: BTC's rate applied to the alt would have said "not crowded".
        Assert.False(btc.IsCrowdedLong(t));
        Assert.True(pepe.IsCrowdedLong(t));
    }

    // A symbol with no funding history must map to null — which the simulators already handle as
    // "price at the interest-rate floor". Silently substituting some other symbol's series is
    // exactly the defect being removed, so an unknown symbol must NOT resolve to anything.
    [Fact]
    public void For_UnknownSymbol_ReturnsNullRatherThanBorrowingAnotherSeries()
    {
        var set = Set(("BTCUSDT", Series(8, 100, 0.0001)));
        Assert.Null(set.For("DOGEUSDT"));
        Assert.Null(set.For(""));
    }

    [Fact]
    public void For_SymbolWithEmptyHistory_ReturnsNull()
    {
        var set = Set(("BTCUSDT", Series(8, 100, 0.0001)), ("NEWUSDT", Array.Empty<FundingBar>()));
        Assert.Null(set.For("NEWUSDT"));
        Assert.NotNull(set.For("BTCUSDT"));
    }

    [Fact]
    public void PerSymbolSessions_ProduceDifferentFundingCostForTheSameTrade()
    {
        var set   = Set(("BTCUSDT", Series(8, 100, 0.0001)), ("PEPEUSDT", Series(8, 100, 0.0025)));
        var entry = T0.AddHours(1);
        var exit  = T0.AddHours(49);   // spans six 8h settlements

        double btcCost  = FundingRateSession.PnlPct(entry, exit, set.For("BTCUSDT"),  isLong: true);
        double pepeCost = FundingRateSession.PnlPct(entry, exit, set.For("PEPEUSDT"), isLong: true);

        Assert.True(btcCost  < 0, "a long pays a positive funding rate");
        Assert.True(pepeCost < 0);
        Assert.True(pepeCost < btcCost,
            $"the crowded alt must cost more than BTC: {pepeCost:F4} vs {btcCost:F4}");
    }

    // ── Interval detection: the 8h grid is not universal ─────────────────────────────────
    [Theory]
    [InlineData(8.0)]
    [InlineData(4.0)]
    [InlineData(2.0)]
    [InlineData(1.0)]
    public void MedianFundingIntervalHours_RecoversTheActualSpacing(double spacing)
    {
        var times = Series(spacing, 50, 0.0001).Select(b => b.Time).ToList();
        Assert.Equal(spacing, CandleFetcher.MedianFundingIntervalHours(times), 6);
    }

    // Median, not mean: an exchange outage shows up as one enormous gap and must not move the
    // estimate — otherwise a single hole in the history would silently change the fetch depth.
    [Fact]
    public void MedianFundingIntervalHours_IgnoresAnOutageGap()
    {
        var times = Series(4.0, 40, 0.0001).Select(b => b.Time).ToList();
        times.Add(times[^1].AddDays(30));      // one 30-day hole
        Assert.Equal(4.0, CandleFetcher.MedianFundingIntervalHours(times), 6);
    }

    [Fact]
    public void MedianFundingIntervalHours_TooFewRecords_ReturnsZero()
    {
        Assert.Equal(0.0, CandleFetcher.MedianFundingIntervalHours(Array.Empty<DateTime>()));
        Assert.Equal(0.0, CandleFetcher.MedianFundingIntervalHours(new[] { T0 }));
    }

    // ── Fetch depth is derived from the spacing, not from the 8h assumption ──────────────
    [Fact]
    public void RecordsNeededFor_ScalesWithTheObservedSpacing()
    {
        // At 8h the derivation reproduces the old hardcoded literal exactly — the fix changes
        // nothing for BTC/ETH, which is why the bug was invisible.
        Assert.Equal(3504, CandleFetcher.RecordsNeededFor(8.0, CandleFetcher.TargetFundingHistoryHours));

        // A 4h symbol needs TWICE the records for the same wall-clock history; a 2h symbol four
        // times. Under the old constant every symbol stopped at 3504 records, i.e. 1.6yr for a 4h
        // symbol and 0.8yr for a 2h one. Target chosen below the clamp so the ratio is visible.
        const double oneYear = 8760.0;
        Assert.Equal(1095, CandleFetcher.RecordsNeededFor(8.0, oneYear));
        Assert.Equal(2190, CandleFetcher.RecordsNeededFor(4.0, oneYear));
        Assert.Equal(4380, CandleFetcher.RecordsNeededFor(2.0, oneYear));
    }

    [Fact]
    public void RecordsNeededFor_UnknownSpacing_FallsBackToTheEightHourAssumption()
    {
        Assert.Equal(
            CandleFetcher.RecordsNeededFor(FundingRateSession.FundingIntervalHours, CandleFetcher.TargetFundingHistoryHours),
            CandleFetcher.RecordsNeededFor(0.0, CandleFetcher.TargetFundingHistoryHours));
    }

    [Fact]
    public void RecordsNeededFor_IsClampedSoAnHourlySymbolCannotRunAway()
    {
        Assert.Equal(CandleFetcher.MaxFundingRecords,
            CandleFetcher.RecordsNeededFor(1.0, CandleFetcher.TargetFundingHistoryHours));
        Assert.True(CandleFetcher.RecordsNeededFor(8.0, CandleFetcher.TargetFundingHistoryHours) <= CandleFetcher.MaxFundingRecords);
    }

    // ── Mismatch detection and reporting ─────────────────────────────────────────────────
    [Fact]
    public void DescribeFundingSeries_FlagsSymbolsOffTheModelSettlementGrid()
    {
        var eightH = CandleFetcher.DescribeFundingSeries("BTCUSDT", Series(8, 200, 0.0001));
        Assert.True(eightH.MatchesModelGrid);
        Assert.Equal(1.0, eightH.CostUndercountFactor, 6);

        var fourH = CandleFetcher.DescribeFundingSeries("ALTUSDT", Series(4, 200, 0.0001));
        Assert.False(fourH.MatchesModelGrid);
        // FundingRateSession counts 8h ticks, so on a 4h symbol it books half the settlements.
        Assert.Equal(2.0, fourH.CostUndercountFactor, 6);

        var hourly = CandleFetcher.DescribeFundingSeries("CAPUSDT", Series(1, 200, 0.0001));
        Assert.False(hourly.MatchesModelGrid);
        Assert.Equal(8.0, hourly.CostUndercountFactor, 6);
    }

    [Fact]
    public void DescribeFundingSeries_EmptySeries_IsNotReportedAsAMismatch()
    {
        var info = CandleFetcher.DescribeFundingSeries("NEWUSDT", Array.Empty<FundingBar>());
        Assert.Equal(0, info.Records);
        Assert.True(info.MatchesModelGrid);   // nothing to disagree with; not a false alarm
        Assert.Equal(0.0, info.SpanDays);
    }

    [Fact]
    public void DescribeFundingSeries_ReportsTheRealSpanNotTheTargetSpan()
    {
        var info = CandleFetcher.DescribeFundingSeries("ALTUSDT", Series(4, 100, 0.0001));
        Assert.Equal(100, info.Records);
        Assert.Equal(T0, info.First);
        Assert.Equal(T0.AddHours(99 * 4), info.Last);
        Assert.Equal(99 * 4 / 24.0, info.SpanDays, 6);
    }

    [Fact]
    public void OffGridSymbols_ListsOnlyTheMismatchedOnes()
    {
        var set = Set(
            ("BTCUSDT",  Series(8, 100, 0.0001)),
            ("ALTUSDT",  Series(4, 100, 0.0001)),
            ("CAPUSDT",  Series(1, 100, 0.0001)),
            ("NEWUSDT",  Array.Empty<FundingBar>()));

        var off = set.OffGridSymbols.Select(i => i.Symbol).ToList();
        Assert.Equal(2, off.Count);
        Assert.Contains("ALTUSDT", off);
        Assert.Contains("CAPUSDT", off);
        Assert.DoesNotContain("BTCUSDT", off);
        Assert.DoesNotContain("NEWUSDT", off);
    }

    [Fact]
    public void PrintSummary_SurfacesTheUndercountRatherThanHidingIt()
    {
        var set = Set(("BTCUSDT", Series(8, 100, 0.0001)), ("ALTUSDT", Series(4, 100, 0.0001)));

        var prev = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try { set.PrintSummary(); } finally { Console.SetOut(prev); }
        string output = sw.ToString();

        Assert.Contains("ALTUSDT", output);
        Assert.Contains("UNDERCOUNTED", output);
        Assert.DoesNotContain("BTCUSDT", output);   // on-grid symbols are not flagged
    }
}
