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

    // Efficiency = OOS Sharpe / IS Sharpe. Uses PerTradeSharpe (no time normalisation) so a
    // perfectly-generalising strategy has expected efficiency 1.0. Overfit threshold: <0.5.
    // Windows with isSharpe ≤ 0.05 (PF<1.3 guard) are excluded from mean (not evidence of overfit).
    // Per-window efficiency clamped to [-1, 2] before averaging.
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
        // No usable windows → inconclusive, not overfit.
        bool isOverfit = !inconclusive && meanEff < 0.5;
        return new(strategyName, windows, meanEff, isOverfit, usable.Count, inconclusive);
    }

    // Embargo gap (bars) between train and test windows, mirroring FitnessConfig.EmbargoPct.
    private static int EmbargoBars(int stepSize, FitnessConfig cfg)
        => Math.Max(0, (int)(stepSize * cfg.EmbargoPct));

    // ── Per-coin expanding windows ───────────────────────────────────────────────
    // Window w (0..3): trains on first (1/4 + w/8) of coin's own history, tests on next 1/8.
    // Each coin uses its own array length (not the shortest coin's). Coins with <40 bars in a window are skipped.
    private const int WindowCount   = 4;
    private const int MinWindowBars = 40;

    // [TrainEnd, TestStart, TestEnd) for window w on a coin's own array. Null if history too short.
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
    // ×4 arithmetic valid only when arrays are same slice at two resolutions (DipLong, SwingLong).
    // FadeLong/RipShort: bear-window filtered independently per timeframe → map by TIMESTAMP instead.
    private static (int Start, int Len) M15RangeByFactor(int h1Start, int h1End, int m15Len)
    {
        int start = Math.Min(h1Start * 4, m15Len);
        int len   = Math.Max(0, Math.Min((h1End - h1Start) * 4, m15Len - start));
        return (start, len);
    }

    // Maps h1 index range to m15 by calendar time (binary search). Closes at MaxValue to score trailing candles.
    private static (int Start, int Len) M15RangeByTime(
        ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, int h1Start, int h1End)
    {
        if (h1.Length == 0 || h1Start >= h1.Length || h1End <= h1Start) return (0, 0);
        DateTime start = h1[h1Start].Time;
        DateTime end   = h1End < h1.Length ? h1[h1End].Time : DateTime.MaxValue;
        var (s, e) = FoldScoreHelper.RangeForWindow(m15, start, end);
        return (s, e - s);
    }

    // Shared driver for all strategies. TrainBars/TestBars are means across participating coins.
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

            // No coin long enough for this window — skip (GA throws on empty list).
            if (trainCoins.Count == 0) continue;

            var geno = train(trainCoins);

            double isSharpe  = sharpe(geno, trainCoins);
            double oosSharpe = sharpe(geno, testCoins);

            windows.Add(MakeWindow(w, trainBars.Average(), testBars.Average(), isSharpe, oosSharpe));
        }

        return BuildReport(strategyName, windows);
    }

    // ── FadeShort ── Single-timeframe, no h1→m15 mapping.
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

    // ── DipLong ── Plain percentage split; m15 index = 4 × h1 index.
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

    // ── SwingLong ── Same as DipLong (x4 mapping holds).
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

    // ── RipShort ── Bear-window filtered; m15 mapped by timestamp.
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

    // ── FadeLong ── Bear-window filtered; m15 mapped by timestamp (same as RipShort).
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

    // ── Grid ── Single-timeframe, no h1→m15 mapping.
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
