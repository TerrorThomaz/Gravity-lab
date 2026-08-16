namespace TradingGA;

// Train/val/test split. Every trainer obtains data through this type.
// SplitSeries deliberately does NOT expose the full array — trainers must say which slice they want.
// Train: GA optimises here. Val: model selection (repeated reading = overfitting). Test: touched once at end.
public static class DataSplit
{
    // One authority for the split boundary. 0.80 = largest prefix any pre-centralisation trainer consumed,
    // so val starting at 0.80 was never trained on by any of them.
    public const double TrainFraction = 0.80;
    public const double ValFraction   = 0.10;
    // Test is the remainder, so the three always sum to exactly 1.0 with no rounding gap.

    // Report labels (derived from fractions, not literals).
    public static string TrainLabel => $"train {TrainFraction:P0}";
    public static string ValLabel   => $"val {ValFraction:P0}";
    public static string TestLabel  => $"test {1.0 - TrainFraction - ValFraction:P0}";

    // Index boundaries for any time-ordered array (not just Candle[]).
    public static (int TrainEnd, int ValEnd) Bounds(int length)
    {
        if (length < 20) return (length, length);
        return ((int)(length * TrainFraction), (int)(length * (TrainFraction + ValFraction)));
    }

    public static (T[] Train, T[] Val, T[] Test) SplitAny<T>(T[] series)
    {
        if (series is null || series.Length < 20) return ([], [], []);
        var (a, b) = Bounds(series.Length);
        return (series[..a], series[a..b], series[b..]);
    }

    public static SplitSeries Split(Candle[] series)
    {
        if (series is null || series.Length < 20)
            return new SplitSeries([], [], []);

        // Via Bounds (single source of boundary arithmetic).
        var (trainEnd, valEnd) = Bounds(series.Length);
        return new SplitSeries(series[..trainEnd], series[trainEnd..valEnd], series[valEnd..]);
    }

    // 15m series aligned to same TIME boundaries as h1 split (not same fractions).
    // Independent index splits put boundaries at different instants → leakage.
    public static SplitSeries SplitAligned(Candle[] m15, SplitSeries h1Split)
    {
        if (m15 is null || m15.Length == 0 || h1Split.Train.Length == 0)
            return new SplitSeries([], [], []);

        DateTime trainEndT = h1Split.Train[^1].Time;
        DateTime valEndT   = h1Split.Val.Length > 0 ? h1Split.Val[^1].Time : trainEndT;

        int a = UpperBound(m15, trainEndT);
        int b = UpperBound(m15, valEndT);
        return new SplitSeries(m15[..a], m15[a..b], m15[b..]);
    }

    private static int UpperBound(Candle[] bars, DateTime t)
    {
        int lo = 0, hi = bars.Length;
        while (lo < hi) { int m = (lo + hi) / 2; if (bars[m].Time <= t) lo = m + 1; else hi = m; }
        return lo;
    }
}

// No "everything" accessor — trainers must explicitly choose a slice.
public readonly record struct SplitSeries(Candle[] Train, Candle[] Val, Candle[] Test)
{
    public bool IsUsable => Train.Length > 0;

    // Train + Val: for final refit after configuration selection. Legitimate ONLY after selection is finished.
    public Candle[] TrainAndVal
    {
        get
        {
            var merged = new Candle[Train.Length + Val.Length];
            Train.CopyTo(merged, 0);
            Val.CopyTo(merged, Train.Length);
            return merged;
        }
    }

    public string Describe() => $"train={Train.Length} val={Val.Length} test={Test.Length}";
}
