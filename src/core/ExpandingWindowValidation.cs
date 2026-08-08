namespace TradingGA;

public static class ExpandingWindowValidation
{
    public record WindowResult(
        int WindowIndex,
        double TrainBars,
        double TestBars,
        double InSampleSharpe,
        double OutOfSampleSharpe,
        double Efficiency,
        bool IsUsable);

    public record ExpandingWindowReport(
        string StrategyName,
        List<WindowResult> Windows,
        double MeanEfficiency,
        bool IsOverfit,
        int UsableWindows,
        bool IsInconclusive);

    // ── Efficiency guards ────────────────────────────────────────────────────────
    // (0) THE SHARPE USED HERE IS UN-NORMALISED (FoldScoreHelper.PerTradeSharpe =
    //     mean/stdev of the trade returns). It used to be Simulator.SharpeRatio(all,
    //     all.Count) — which multiplies by sqrt(candleCount / 288) and expects a count of
    //     5-minute-equivalent CANDLES, not the TRADE count that was being passed.
    //
    //     That defect biased this report's core verdict, not just its printed numbers.
    //     Efficiency is oosSharpe/isSharpe, and IS and OOS each carried the factor with
    //     DIFFERENT n: the expanding schedule trains on 1/4..5/8 of a coin and tests on
    //     1/8, so n_is / n_oos runs roughly 2x..5x. A strategy that generalises PERFECTLY
    //     — identical return distribution in and out of sample — therefore measured
    //         efficiency = sqrt(n_oos / n_is) = sqrt(1/2)..sqrt(1/5) = 0.707..0.447,
    //     mean 0.558, against an OVERFIT THRESHOLD OF 0.5. The flag was sitting inside
    //     its own bias band: perfect generalisation was one window-schedule away from
    //     being reported as overfitting.
    //
    //     With the time-scaling removed, both sides are the same scale-free statistic, so
    //     a perfectly-generalising strategy now has an EXPECTED EFFICIENCY OF 1.0 and the
    //     0.5 threshold means what it says: "OOS risk-adjusted return is less than half
    //     of in-sample". Absolute Sharpe values printed by this report are smaller than
    //     they used to be (no sqrt(n/288) inflation) and are NOT comparable to any
    //     efficiency figure recorded before this change.
    //
    // (1) PerTradeSharpe keeps Simulator.SharpeRatio's guards: it hard-returns 0 whenever
    //     the in-sample profit factor is under 1.3. A window can therefore score
    //     isSharpe == 0 for a reason that has nothing to do with overfitting, and the old
    //     code turned that into efficiency = 0, dragging the 4-window mean below the 0.5
    //     threshold and flagging OVERFIT spuriously. Such windows are now recorded but
    //     excluded from the mean, and their count is surfaced via UsableWindows.
    // (2) The ratio used to be unclamped: a small positive isSharpe against a negative
    //     oosSharpe produces an arbitrarily large negative number that single-handedly
    //     dominates a 4-window average. Per-window efficiency is clamped to [-1, 2]
    //     before averaging — 2.0 already means "OOS twice as good as IS", and anything
    //     below -1.0 is equally "fully broken out of sample".
    private const double MinUsableIsSharpe = 0.05;
    private const double EfficiencyFloor   = -1.0;
    private const double EfficiencyCeiling =  2.0;

    private static WindowResult MakeWindow(
        int windowIndex, double trainBars, double testBars, double isSharpe, double oosSharpe)
    {
        bool usable = isSharpe > MinUsableIsSharpe;
        double efficiency = usable
            ? Math.Clamp(oosSharpe / isSharpe, EfficiencyFloor, EfficiencyCeiling)
            : 0.0;
        return new WindowResult(windowIndex, trainBars, testBars, isSharpe, oosSharpe, efficiency, usable);
    }

    private static ExpandingWindowReport BuildReport(string strategyName, List<WindowResult> windows)
    {
        var usable = windows.Where(w => w.IsUsable).ToList();
        bool inconclusive = usable.Count == 0;
        double meanEff = inconclusive ? 0.0 : usable.Average(w => w.Efficiency);
        // Never assert OVERFIT off an empty sample: "no window had a usable in-sample
        // Sharpe" is inconclusive, not evidence of overfitting.
        bool isOverfit = !inconclusive && meanEff < 0.5;
        return new(strategyName, windows, meanEff, isOverfit, usable.Count, inconclusive);
    }

    // Embargo gap (in bars of the coin's own h1 array) inserted between that coin's
    // training window and its test window, mirroring FitnessConfig.EmbargoPct used by the
    // GA fold splitter. Without it the test window starts on the bar immediately after
    // training ends, so a trade opened near the train boundary and indicators with
    // multi-bar lookback leak straight across the seam. `stepSize` is the coin's OWN test
    // block length, so the gap scales with that coin's history like every other bound here.
    private static int EmbargoBars(int stepSize, FitnessConfig cfg)
        => Math.Max(0, (int)(stepSize * cfg.EmbargoPct));

    // ── Per-coin expanding windows ───────────────────────────────────────────────
    // Window w (0..3) trains on the first (1/4 + w/8) of a coin's own history and tests on
    // the following 1/8 of it, with an embargo gap in between. That is the same schedule
    // the original code described — trainEnd = len/4 + w*len/8, test block len/8 — but the
    // fractions are now evaluated against EACH COIN'S OWN array length instead of against
    // `minLen`, the length of the shortest coin in the set.
    //
    // Why this had to change: an index is not a date. Deriving stepSize and trainEnd from
    // the shortest coin and then applying those raw indices to every other coin meant
    //   (a) "window w" covered a different calendar stretch on every symbol whose history
    //       starts on a different day, so the windows were not a consistent stretch of
    //       market history across the set, and
    //   (b) every bar past minLen on every longer coin was unreachable, because the
    //       schedule never advanced beyond the shortest array.
    // The old `Math.Min(trainEnd, arr.Length)` clamps hid that mismatch instead of fixing
    // it. This mirrors FoldScoreHelper.PerCoinFoldRange, which the per-coin GAs use, and
    // the "NOTE ON INDEX SPACES" contract at the top of FoldScoreHelper's fold section.
    //
    // A coin whose own window would come out under MinWindowBars is skipped for that
    // window (the < 40 convention used by the GAs) rather than dragging the whole
    // schedule down for every other coin. Held-out coins carry empty training arrays and
    // are therefore skipped everywhere, instead of collapsing minLen to 0 and killing the
    // entire report as they used to.
    private const int WindowCount   = 4;
    private const int MinWindowBars = 40;

    // Returns the [0, TrainEnd) / [TestStart, TestEnd) split of a coin's own array for
    // window `w`, or null when that coin's history is too short to support the window.
    // Internal so the alignment contract can be unit-tested directly.
    internal static (int TrainEnd, int TestStart, int TestEnd)? WindowForCoin(
        int totalBars, int w, FitnessConfig cfg)
    {
        if (totalBars <= 0 || w < 0) return null;

        int stepSize = totalBars / 8;                 // test block: 1/8 of THIS coin
        int trainEnd = totalBars / 4 + w * stepSize;  // 1/4, 3/8, 1/2, 5/8 of THIS coin
        if (trainEnd < MinWindowBars || trainEnd >= totalBars) return null;

        int testStart = trainEnd + EmbargoBars(stepSize, cfg);
        if (testStart >= totalBars) return null;

        int testEnd = Math.Min(testStart + stepSize, totalBars);
        if (testEnd - testStart < MinWindowBars) return null;

        return (trainEnd, testStart, testEnd);
    }

    // ── h1 → m15 index mapping ───────────────────────────────────────────────────
    // ×4 arithmetic is only valid when the two arrays are the same slice of history at two
    // resolutions. That holds for DipLong and SwingLong: their train arrays come from a
    // plain percentage split of a full h1/m15 pair, and h1 is AggregateCandles(m15, 4), so
    // h1[i].Time == m15[4i].Time by construction.
    //
    // It does NOT hold for FadeLong and RipShort. Their training arrays are bear-window
    // FILTERED CONCATENATIONS produced independently for the two timeframes
    // (LongTrainCommands.FilterToWindows, ~line 354): each timeframe drops a different
    // number of candles at every window edge, so m15.Length != 4 * h1.Length and index
    // 4*i on m15 is not the same moment as index i on h1. Those two map by TIMESTAMP.
    private static (int Start, int Len) M15RangeByFactor(int h1Start, int h1End, int m15Len)
    {
        int start = Math.Min(h1Start * 4, m15Len);
        int len   = Math.Max(0, Math.Min((h1End - h1Start) * 4, m15Len - start));
        return (start, len);
    }

    // Maps the h1 index range [h1Start, h1End) onto the coin's own m15 array by calendar
    // time, via the same binary-search helper the regime-gated GAs use for their folds.
    // The window closes at DateTime.MaxValue when the h1 range runs to the end of the
    // array, so trailing m15 candles are scored rather than silently dropped.
    private static (int Start, int Len) M15RangeByTime(
        ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, int h1Start, int h1End)
    {
        if (h1.Length == 0 || h1Start >= h1.Length || h1End <= h1Start) return (0, 0);
        DateTime start = h1[h1Start].Time;
        DateTime end   = h1End < h1.Length ? h1[h1End].Time : DateTime.MaxValue;
        var (s, e) = FoldScoreHelper.RangeForWindow(m15, start, end);
        return (s, e - s);
    }

    // Shared driver for all six strategies: one copy of the window schedule, with the
    // strategy-specific parts (array length, slicing, GA run, Sharpe) passed in.
    //
    // TrainBars/TestBars in the result are the MEAN window lengths across the coins that
    // took part in that window — the windows are per-coin now, so a single number can only
    // ever be a summary.
    private static ExpandingWindowReport RunExpanding<TCoin, TGeno>(
        string strategyName,
        IReadOnlyList<TCoin> coins,
        FitnessConfig cfg,
        Func<TCoin, int> h1Length,
        Func<TCoin, int, int, TCoin> slice,
        Func<IReadOnlyList<TCoin>, TGeno> train,
        Func<TGeno, IReadOnlyList<TCoin>, double> sharpe)
    {
        var windows = new List<WindowResult>();

        for (int w = 0; w < WindowCount; w++)
        {
            var trainCoins = new List<TCoin>();
            var testCoins  = new List<TCoin>();
            var trainBars  = new List<int>();
            var testBars   = new List<int>();

            foreach (var c in coins)
            {
                var win = WindowForCoin(h1Length(c), w, cfg);
                if (win is null) continue;
                var (trainEnd, testStart, testEnd) = win.Value;

                trainCoins.Add(slice(c, 0, trainEnd));
                testCoins.Add(slice(c, testStart, testEnd));
                trainBars.Add(trainEnd);
                testBars.Add(testEnd - testStart);
            }

            // No coin in the set is long enough for this window — record nothing rather
            // than handing an empty coin list to the GA (which throws).
            if (trainCoins.Count == 0) continue;

            var geno = train(trainCoins);

            double isSharpe  = sharpe(geno, trainCoins);
            double oosSharpe = sharpe(geno, testCoins);

            windows.Add(MakeWindow(w, trainBars.Average(), testBars.Average(), isSharpe, oosSharpe));
        }

        return BuildReport(strategyName, windows);
    }

    // ── FadeShort ────────────────────────────────────────────────────────────────
    // Single-timeframe (CoinData carries one h1 array), so there is no h1→m15 mapping.
    public static ExpandingWindowReport RunFadeShort(
        IReadOnlyList<FadeShortGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        var fc = cfg ?? new FitnessConfig();
        return RunExpanding<FadeShortGA.CoinData, FadeShortGenotype>(
            "FadeShort", coins, fc,
            c => c.TrainCandles.Length,
            SliceFadeShort,
            tc => new FadeShortGA(populationSize: 30, generations: 40, verbose: false, cfg: fc).Run(tc),
            ComputeSharpeFadeShort);
    }

    private static FadeShortGA.CoinData SliceFadeShort(FadeShortGA.CoinData c, int start, int end)
        => new(c.TrainCandles.Slice(start, end - start), c.ValCandles, c.Weight);

    // ── DipLong ──────────────────────────────────────────────────────────────────
    // Train arrays are a plain percentage split of an aggregated h1/m15 pair, so the
    // m15 index is exactly 4 x the h1 index (see M15RangeByFactor).
    public static ExpandingWindowReport RunDipLong(
        IReadOnlyList<DipLongGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        var fc = cfg ?? new FitnessConfig();
        return RunExpanding<DipLongGA.CoinData, DipLongGenotype>(
            "DipLong", coins, fc,
            c => c.TrainH1.Length,
            SliceDipLong,
            tc => new DipLongGA(populationSize: 30, generations: 40, verbose: false, cfg: fc).Run(tc),
            ComputeSharpeDipLong);
    }

    private static DipLongGA.CoinData SliceDipLong(DipLongGA.CoinData c, int start, int end)
    {
        var (m15Start, m15Len) = M15RangeByFactor(start, end, c.TrainM15.Length);
        return new(
            c.TrainH1.Slice(start, end - start), c.ValH1,
            c.TrainM15.Slice(m15Start, m15Len),  c.ValM15, c.Weight);
    }

    // ── SwingLong ────────────────────────────────────────────────────────────────
    // Same plain percentage split as DipLong — the x4 mapping holds.
    public static ExpandingWindowReport RunSwingLong(
        IReadOnlyList<SwingLongGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        var fc = cfg ?? new FitnessConfig();
        return RunExpanding<SwingLongGA.CoinData, SwingLongGenotype>(
            "SwingLong", coins, fc,
            c => c.TrainH1.Length,
            SliceSwingLong,
            tc => new SwingLongGA(populationSize: 30, generations: 40, verbose: false, cfg: fc).Run(tc),
            ComputeSharpeSwingLong);
    }

    private static SwingLongGA.CoinData SliceSwingLong(SwingLongGA.CoinData c, int start, int end)
    {
        var (m15Start, m15Len) = M15RangeByFactor(start, end, c.TrainM15.Length);
        return new(
            c.TrainH1.Slice(start, end - start), c.ValH1,
            c.TrainM15.Slice(m15Start, m15Len),  c.ValM15, c.Weight);
    }

    // ── RipShort ─────────────────────────────────────────────────────────────────
    // Bear-window filtered concatenations: h1 and m15 are filtered independently, so the
    // m15 range is derived from timestamps, not from h1index x 4.
    public static ExpandingWindowReport RunRipShort(
        IReadOnlyList<RipShortGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        var fc = cfg ?? new FitnessConfig();
        return RunExpanding<RipShortGA.CoinData, RipShortGenotype>(
            "RipShort", coins, fc,
            c => c.TrainH1.Length,
            SliceRipShort,
            tc => new RipShortGA(populationSize: 30, generations: 40, verbose: false, cfg: fc).Run(tc),
            ComputeSharpeRipShort);
    }

    private static RipShortGA.CoinData SliceRipShort(RipShortGA.CoinData c, int start, int end)
    {
        var (m15Start, m15Len) = M15RangeByTime(c.TrainH1.Span, c.TrainM15.Span, start, end);
        return new(
            c.TrainH1.Slice(start, end - start), c.ValH1,
            c.TrainM15.Slice(m15Start, m15Len),  c.ValM15, c.Weight);
    }

    // ── FadeLong ─────────────────────────────────────────────────────────────────
    // Bear-window filtered concatenations, same as RipShort — timestamp mapping.
    public static ExpandingWindowReport RunFadeLong(
        IReadOnlyList<FadeLongGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        var fc = cfg ?? new FitnessConfig();
        return RunExpanding<FadeLongGA.CoinData, FadeLongGenotype>(
            "FadeLong", coins, fc,
            c => c.TrainH1.Length,
            SliceFadeLong,
            tc => new FadeLongGA(populationSize: 30, generations: 40, verbose: false, cfg: fc).Run(tc),
            ComputeSharpeFadeLong);
    }

    private static FadeLongGA.CoinData SliceFadeLong(FadeLongGA.CoinData c, int start, int end)
    {
        var (m15Start, m15Len) = M15RangeByTime(c.TrainH1.Span, c.TrainM15.Span, start, end);
        return new(
            c.TrainH1.Slice(start, end - start), c.ValH1,
            c.TrainM15.Slice(m15Start, m15Len),  c.ValM15, c.Weight);
    }

    // ── Grid ─────────────────────────────────────────────────────────────────────
    // Single-timeframe, like FadeShort — no h1→m15 mapping.
    public static ExpandingWindowReport RunGrid(
        IReadOnlyList<GridGeneticAlgorithm.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        var fc = cfg ?? new FitnessConfig();
        return RunExpanding<GridGeneticAlgorithm.CoinData, GridGenotype>(
            "Grid", coins, fc,
            c => c.TrainCandles.Length,
            SliceGrid,
            tc => new GridGeneticAlgorithm(populationSize: 30, generations: 40, verbose: false, cfg: fc).Run(tc),
            ComputeSharpeGrid);
    }

    private static GridGeneticAlgorithm.CoinData SliceGrid(GridGeneticAlgorithm.CoinData c, int start, int end)
        => new(c.TrainCandles.Slice(start, end - start), c.ValCandles, c.Weight);

    // ── Per-strategy in/out-of-sample Sharpe ─────────────────────────────────────

    private static double ComputeSharpeFadeShort(FadeShortGenotype ind, IReadOnlyList<FadeShortGA.CoinData> coins)
    {
        var all = new List<double>();
        foreach (var c in coins)
        {
            if (c.TrainCandles.Length < 100) continue;
            foreach (var t in FadeShortSimulator.GetFadeShortReturns(ind, c.TrainCandles.Span))
                all.Add(t.Return);
        }
        return all.Count >= 10 ? FoldScoreHelper.PerTradeSharpe(all) : 0;
    }

    private static double ComputeSharpeDipLong(DipLongGenotype ind, IReadOnlyList<DipLongGA.CoinData> coins)
    {
        var all = new List<double>();
        foreach (var c in coins)
        {
            if (c.TrainH1.Length < 100 || c.TrainM15.Length < 400) continue;
            foreach (var t in DipLongSimulator.GetDipLongReturns(ind, c.TrainH1.Span, c.TrainM15.Span))
                all.Add(t.Return);
        }
        return all.Count >= 10 ? FoldScoreHelper.PerTradeSharpe(all) : 0;
    }

    private static double ComputeSharpeSwingLong(SwingLongGenotype ind, IReadOnlyList<SwingLongGA.CoinData> coins)
    {
        var all = new List<double>();
        foreach (var c in coins)
        {
            if (c.TrainH1.Length < 100 || c.TrainM15.Length < 400) continue;
            foreach (var t in SwingLongSimulator.GetSwingLongReturns(ind, c.TrainH1.Span, c.TrainM15.Span))
                all.Add(t.Return);
        }
        return all.Count >= 10 ? FoldScoreHelper.PerTradeSharpe(all) : 0;
    }

    private static double ComputeSharpeRipShort(RipShortGenotype ind, IReadOnlyList<RipShortGA.CoinData> coins)
    {
        var all = new List<double>();
        foreach (var c in coins)
        {
            if (c.TrainH1.Length < 100 || c.TrainM15.Length < 400) continue;
            foreach (var t in RipShortSimulator.GetRipShortReturns(ind, c.TrainH1.Span, c.TrainM15.Span))
                all.Add(t.Return);
        }
        return all.Count >= 10 ? FoldScoreHelper.PerTradeSharpe(all) : 0;
    }

    private static double ComputeSharpeFadeLong(FadeLongGenotype ind, IReadOnlyList<FadeLongGA.CoinData> coins)
    {
        var all = new List<double>();
        foreach (var c in coins)
        {
            if (c.TrainH1.Length < 100 || c.TrainM15.Length < 400) continue;
            foreach (var t in FadeLongSimulator.GetFadeLongReturns(ind, c.TrainH1.Span, c.TrainM15.Span))
                all.Add(t.Return);
        }
        return all.Count >= 10 ? FoldScoreHelper.PerTradeSharpe(all) : 0;
    }

    private static double ComputeSharpeGrid(GridGenotype ind, IReadOnlyList<GridGeneticAlgorithm.CoinData> coins)
    {
        var all = new List<double>();
        foreach (var c in coins)
        {
            if (c.TrainCandles.Length < 100) continue;
            foreach (var t in GridSimulator.GetGridReturns(ind, c.TrainCandles.Span))
                all.Add(t.Return);
        }
        return all.Count >= 10 ? FoldScoreHelper.PerTradeSharpe(all) : 0;
    }

    public static void PrintReport(ExpandingWindowReport report)
    {
        Console.WriteLine($"\n── Expanding Window Validation: {report.StrategyName} ──");
        Console.WriteLine($"  Usable windows (IS Sharpe > {MinUsableIsSharpe:F2}): {report.UsableWindows}/{report.Windows.Count}");
        if (report.IsInconclusive)
        {
            Console.WriteLine("  Mean efficiency (OOS Sharpe / IS Sharpe): n/a");
            Console.WriteLine("  Overfit flag: INCONCLUSIVE — no window had a usable in-sample Sharpe");
            Console.WriteLine("    (PerTradeSharpe returns 0 when profit factor < 1.3, so this says");
            Console.WriteLine("     nothing about overfitting — the efficiency ratio simply has no denominator)");
        }
        else
        {
            Console.WriteLine($"  Mean efficiency (OOS Sharpe / IS Sharpe): {report.MeanEfficiency:F3}  (usable windows only, clamped to [{EfficiencyFloor:F1}, {EfficiencyCeiling:F1}])");
            Console.WriteLine($"  Overfit flag (efficiency < 0.5): {(report.IsOverfit ? "YES — OVERFIT" : "no")}");
            Console.WriteLine("    (Sharpe is per-trade mean/stdev, un-normalised, so a strategy that");
            Console.WriteLine("     generalises perfectly is expected at efficiency 1.0, not 0.45–0.71)");
        }
        Console.WriteLine($"  {"Window",8} {"Train bars",12} {"Test bars",10} {"IS Sharpe",10} {"OOS Sharpe",11} {"Efficiency",11} {"Usable",8}");
        foreach (var w in report.Windows)
        {
            string eff = w.IsUsable ? w.Efficiency.ToString("F3") : "—";
            Console.WriteLine($"  {w.WindowIndex,8} {w.TrainBars,12:F0} {w.TestBars,10:F0} {w.InSampleSharpe,10:F4} {w.OutOfSampleSharpe,11:F4} {eff,11} {(w.IsUsable ? "yes" : "no"),8}");
        }
        Console.WriteLine("    (bar counts are per-coin means — each coin expands through its OWN history,");
        Console.WriteLine("     so window w is 1/4 + w/8 of that coin's array, not a shared bar index)");
    }
}
