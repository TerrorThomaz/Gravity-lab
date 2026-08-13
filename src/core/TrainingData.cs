namespace TradingGA;

// THE train/validation/test split. Every trainer must obtain its data through this type.
//
// WHY THIS EXISTS
// Before this, fourteen call sites cut their own splits with THREE different ratios — 0.75/0.875
// in TrainCommands, 0.80 in GridCommands and LowVolTrainCommands, 0.75 in BacktestCommands and
// CoevolveGA. "Validation" therefore meant a different window depending on which command you ran,
// so a genotype trained by one command and reported by another was being scored on bars it had
// trained on, with nothing in the code saying so.
//
// Worse, two trainers had NO split at all: VolatilityWeightedRotatorGA and DynamicGuardGA both
// fit on the full series, including the bars combinedbacktest reports as "validation". Their
// results were leakage by construction.
//
// THE ENFORCEMENT
// SplitSeries deliberately does NOT expose the full array. There is no `.All` and no implicit
// conversion back to Candle[]. A trainer that wants data has to say which slice it wants, and
// asking for Test is a visible, greppable act rather than an accident. This is the same principle
// that made the slippage unification stick: make the wrong thing fail to compile rather than
// documenting that it is wrong.
//
// WHAT THE SPLIT MEANS
//   Train — the GA optimises here. Fold scoring, mutation, selection.
//   Val   — model SELECTION between configurations. Reading it repeatedly and adjusting is itself
//           overfitting, just with a human (or an assistant) as the search algorithm. Five rounds
//           of "measure on val, change a gene, re-measure" is not validation.
//   Test  — touched ONCE, at the end, to report. Not for choosing anything.
public static class DataSplit
{
    // One authority. Changing these changes what every trainer means by "validation", which is
    // why they are consts here and not parameters threaded through fourteen call sites.
    public const double TrainFraction = 0.75;
    public const double ValFraction   = 0.125;
    // Test is the remainder, so the three always sum to exactly 1.0 with no rounding gap.

    // Index boundaries for ANY time-ordered array. RegimeBar[], FundingBar[] and future series
    // types need the same split as Candle[] — a Candle-only splitter would push those callers
    // straight back to ad-hoc literals, which is the defect this class exists to remove.
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

        int trainEnd = (int)(series.Length * TrainFraction);
        int valEnd   = (int)(series.Length * (TrainFraction + ValFraction));
        return new SplitSeries(series[..trainEnd], series[trainEnd..valEnd], series[valEnd..]);
    }

    // 15m series aligned to the SAME time boundaries as an h1 split rather than the same
    // fractions. Splitting each timeframe by index independently puts the boundaries at different
    // instants, so a dual-timeframe strategy would train on h1 bars whose 15m counterparts sit in
    // validation — leakage that no ratio check would catch.
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

// Deliberately not a Candle[] and deliberately without an "everything" accessor.
public readonly record struct SplitSeries(Candle[] Train, Candle[] Val, Candle[] Test)
{
    public bool IsUsable => Train.Length > 0;

    // Train + Val, for the final refit once a configuration has been chosen. Named so that using
    // it is an explicit decision — this is legitimate ONLY after selection is finished, because
    // it consumes the window that selection was scored on.
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
