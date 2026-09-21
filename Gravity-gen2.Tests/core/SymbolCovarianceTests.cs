using TradingGA;
using Xunit;

namespace Gravity_gen2.Tests;

// The symbol-level covariance path turns ~190 candle arrays into one aligned return matrix. Every
// step is a place where a silent misalignment would produce plausible-looking numbers rather than an
// error, so the arithmetic is pinned here: day reduction, window selection, return alignment, and
// the two statistics the diagnostic actually reports on.
public class SymbolCovarianceTests
{
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Candle[] Hourly(DateTime start, int hours, Func<int, double> close)
        => Enumerable.Range(0, hours)
            .Select(i => new Candle(start.AddHours(i), close(i), close(i), close(i), close(i), 1))
            .ToArray();

    [Fact]
    public void ToDaily_TakesTheLastCloseOfEachUtcDay()
    {
        // 3 days of hourly bars; close = hour index, so each day's last close is 23, 47, 71.
        var d = SymbolCovariance.ToDaily("X", Hourly(T0, 72, i => i + 1.0));

        Assert.NotNull(d);
        Assert.Equal(3, d!.Days.Length);
        Assert.Equal(new[] { 24.0, 48.0, 72.0 }, d.Closes);
        Assert.Equal(T0.Date, d.First);
        Assert.Equal(T0.AddDays(2).Date, d.Last);
    }

    [Fact]
    public void ToDaily_KeepsAPartialFinalDay()
    {
        // 30 hours = one full day plus six hours; the stub day must still be emitted.
        var d = SymbolCovariance.ToDaily("X", Hourly(T0, 30, i => i + 1.0));
        Assert.Equal(2, d!.Days.Length);
        Assert.Equal(30.0, d.Closes[^1]);
    }

    [Fact]
    public void SelectWindow_PrefersTheLargerDataRectangle()
    {
        // One symbol with 800 days, nine with 700. Starting early keeps 1 symbol over 800 days
        // (area 800); starting at the later date keeps all 10 over 700 (area 7000) and must win.
        var series = new List<SymbolCovariance.DailySeries> { Daily("LONG", T0, 800) };
        for (int i = 0; i < 9; i++) series.Add(Daily($"S{i}", T0.AddDays(100), 700));

        var w = SymbolCovariance.SelectWindow(series, minWindowDays: 365, staleDays: 30);

        Assert.NotNull(w);
        Assert.Equal(T0.AddDays(100).Date, w!.Start);
        Assert.Equal(10, w.Kept.Length);
        Assert.Empty(w.Dropped);
    }

    [Fact]
    public void SelectWindow_DropsStaleSymbolsSoTheyCannotDragTheWindowBack()
    {
        // A delisted symbol whose data stops a year early must not shorten everyone else's window.
        var series = new List<SymbolCovariance.DailySeries>
        {
            Daily("LIVE1", T0, 900),
            Daily("LIVE2", T0, 900),
            Daily("DEAD",  T0, 400),   // ends ~500 days before the others
        };

        var w = SymbolCovariance.SelectWindow(series, minWindowDays: 365, staleDays: 30);

        Assert.NotNull(w);
        Assert.Contains("DEAD", w!.Dropped);
        Assert.Equal(2, w.Kept.Length);
        Assert.Equal(T0.AddDays(899).Date, w.End);
    }

    [Fact]
    public void SelectWindow_ReturnsNullWhenNoWindowMeetsTheFloor()
    {
        var series = new List<SymbolCovariance.DailySeries> { Daily("SHORT", T0, 100) };
        Assert.Null(SymbolCovariance.SelectWindow(series, minWindowDays: 365, staleDays: 30));
    }

    [Fact]
    public void BuildReturns_AlignsSeriesAndProducesOneFewerReturnThanDays()
    {
        var series = new List<SymbolCovariance.DailySeries>
        {
            Daily("A", T0, 400, i => 100.0 * Math.Pow(1.01, i)),   // +1% every day
            Daily("B", T0, 400, i => 50.0),                        // flat
        };
        var w  = SymbolCovariance.SelectWindow(series, minWindowDays: 365, staleDays: 30)!;
        var rm = SymbolCovariance.BuildReturns(series, w);

        Assert.NotNull(rm);
        Assert.Equal(new[] { "A", "B" }, rm!.Symbols);        // sorted by symbol
        Assert.Equal(w.Days - 1, rm.Returns[0].Length);
        Assert.Equal(rm.Returns[0].Length, rm.Returns[1].Length);
        Assert.All(rm.Returns[0], r => Assert.Equal(0.01, r, 9));
        Assert.All(rm.Returns[1], r => Assert.Equal(0.0, r, 12));
        Assert.Equal(0, rm.FilledGaps);
    }

    [Fact]
    public void BuildReturns_ForwardFillsMissingDaysAndCountsThem()
    {
        // B is missing every other day. Forward-fill books 0% on the gap rather than inventing a
        // move, and the count must surface so a mostly-filled matrix is visible in the report.
        var a = Daily("A", T0, 400);
        var bDays   = new List<DateTime>();
        var bCloses = new List<double>();
        for (int i = 0; i < 400; i += 2) { bDays.Add(T0.AddDays(i).Date); bCloses.Add(10.0); }
        var b = new SymbolCovariance.DailySeries("B", bDays.ToArray(), bCloses.ToArray());

        var series = new List<SymbolCovariance.DailySeries> { a, b };
        var w  = SymbolCovariance.SelectWindow(series, minWindowDays: 365, staleDays: 30)!;
        var rm = SymbolCovariance.BuildReturns(series, w)!;

        Assert.Equal(2, rm.Symbols.Length);
        Assert.True(rm.FilledGaps > 100, $"expected many filled gaps, got {rm.FilledGaps}");
    }

    [Fact]
    public void MeanOffDiagonal_IgnoresTheDiagonal()
    {
        // 3x3 with every off-diagonal 0.5: the diagonal 1.0s must not be averaged in.
        int k = 3;
        var corr = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++) corr[a * k + b] = a == b ? 1.0 : 0.5;

        Assert.Equal(0.5, SymbolCovariance.MeanOffDiagonal(corr, k), 12);
    }

    [Fact]
    public void Families_SeparatesTwoBlocksAndMergesWithinThem()
    {
        // Two blocks of two, correlated 0.9 inside and 0.1 across. At maxDistance 0.4 (merge while
        // average correlation > 0.6) that must give exactly two families.
        int k = 4;
        var corr = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++)
                corr[a * k + b] = a == b ? 1.0 : (a / 2 == b / 2 ? 0.9 : 0.1);

        var labels = SymbolCovariance.Families(corr, k, maxDistance: 0.40);

        Assert.Equal(labels[0], labels[1]);
        Assert.Equal(labels[2], labels[3]);
        Assert.NotEqual(labels[0], labels[2]);
        Assert.Equal(2, labels.Distinct().Count());
    }

    [Fact]
    public void Families_LabelsAreContiguousFromZero()
    {
        int k = 4;
        var corr = new double[k * k];
        for (int a = 0; a < k; a++)
            for (int b = 0; b < k; b++) corr[a * k + b] = a == b ? 1.0 : 0.0;   // nothing merges

        var labels = SymbolCovariance.Families(corr, k, maxDistance: 0.40);
        Assert.Equal(new[] { 0, 1, 2, 3 }, labels.Distinct().OrderBy(x => x).ToArray());
    }

    private static SymbolCovariance.DailySeries Daily(
        string sym, DateTime start, int days, Func<int, double>? close = null)
    {
        var d = new DateTime[days];
        var c = new double[days];
        for (int i = 0; i < days; i++) { d[i] = start.AddDays(i).Date; c[i] = close?.Invoke(i) ?? 100.0; }
        return new SymbolCovariance.DailySeries(sym, d, c);
    }
}
