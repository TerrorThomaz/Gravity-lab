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
    // (1) Simulator.SharpeRatio hard-returns 0 whenever the in-sample profit factor is
    //     under 1.3 (Simulator.cs:369). A window can therefore score isSharpe == 0 for a
    //     reason that has nothing to do with overfitting, and the old code turned that
    //     into efficiency = 0, dragging the 4-window mean below the 0.5 threshold and
    //     flagging OVERFIT spuriously. Such windows are now recorded but excluded from
    //     the mean, and their count is surfaced via UsableWindows.
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

    // Embargo gap (in h1 bars) inserted between the training window and the test window,
    // mirroring FitnessConfig.EmbargoPct used by the GA fold splitter. Without it the test
    // window starts on the bar immediately after training ends, so a trade opened near the
    // train boundary and indicators with multi-bar lookback leak straight across the seam.
    private static int EmbargoBars(int stepSize, FitnessConfig cfg)
        => Math.Max(0, (int)(stepSize * cfg.EmbargoPct));

    public static ExpandingWindowReport RunFadeShort(
        IReadOnlyList<FadeShortGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        cfg ??= new FitnessConfig();
        var windows = new List<WindowResult>();

        int minLen = coins.Min(c => c.TrainCandles.Length);
        int stepSize = minLen / 8;
        if (stepSize < 100) stepSize = 100;
        int embargo = EmbargoBars(stepSize, cfg);

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd + embargo >= minLen) break;

            var trainCoins = coins.Select(c =>
            {
                var arr = c.TrainCandles.ToArray();
                return new FadeShortGA.CoinData(
                    new ReadOnlyMemory<Candle>(arr, 0, Math.Min(trainEnd, arr.Length)),
                    c.ValCandles, c.Weight);
            }).ToList();

            var ga = new FadeShortGA(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(trainCoins);

            double isSharpe = ComputeSharpeFadeShort(geno, trainCoins);

            var testCoins = coins.Select(c =>
            {
                var arr = c.TrainCandles.ToArray();
                int testStart = Math.Min(trainEnd + embargo, arr.Length);
                int testLen = Math.Min(stepSize, arr.Length - testStart);
                if (testLen <= 0) return new FadeShortGA.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new FadeShortGA.CoinData(
                    new ReadOnlyMemory<Candle>(arr, testStart, testLen),
                    c.ValCandles, c.Weight);
            }).Where(c => c.TrainCandles.Length > 0).ToList();

            double oosSharpe = ComputeSharpeFadeShort(geno, testCoins);

            windows.Add(MakeWindow(w, trainEnd, stepSize, isSharpe, oosSharpe));
        }

        return BuildReport("FadeShort", windows);
    }

    public static ExpandingWindowReport RunDipLong(
        IReadOnlyList<DipLongGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        cfg ??= new FitnessConfig();
        var windows = new List<WindowResult>();

        int minLen = coins.Min(c => c.TrainH1.Length);
        int stepSize = minLen / 8;
        if (stepSize < 100) stepSize = 100;
        int embargo = EmbargoBars(stepSize, cfg);

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd + embargo >= minLen) break;

            var trainCoins = coins.Select(c =>
            {
                var h1 = c.TrainH1.ToArray();
                var m15 = c.TrainM15.ToArray();
                return new DipLongGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, 0, Math.Min(trainEnd, h1.Length)),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, 0, Math.Min(trainEnd * 4, m15.Length)),
                    c.ValM15, c.Weight);
            }).ToList();

            var ga = new DipLongGA(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(trainCoins);

            double isSharpe = ComputeSharpeDipLong(geno, trainCoins);

            var testCoins = coins.Select(c =>
            {
                var h1 = c.TrainH1.ToArray();
                var m15 = c.TrainM15.ToArray();
                // testStart already includes the embargo gap; the m15 offset is the usual
                // h1index*4 so the two timeframes stay aligned across the gap.
                int testStart = Math.Min(trainEnd + embargo, h1.Length);
                int testLen = Math.Min(stepSize, h1.Length - testStart);
                int m15Start = Math.Min(testStart * 4, m15.Length);
                int m15Len = Math.Max(0, Math.Min(testLen * 4, m15.Length - m15Start));
                if (testLen <= 0) return new DipLongGA.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty,
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new DipLongGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, testStart, testLen),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, m15Start, m15Len),
                    c.ValM15, c.Weight);
            }).Where(c => c.TrainH1.Length > 0).ToList();

            double oosSharpe = ComputeSharpeDipLong(geno, testCoins);

            windows.Add(MakeWindow(w, trainEnd, stepSize, isSharpe, oosSharpe));
        }

        return BuildReport("DipLong", windows);
    }

    public static ExpandingWindowReport RunSwingLong(
        IReadOnlyList<SwingLongGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        cfg ??= new FitnessConfig();
        var windows = new List<WindowResult>();

        int minLen = coins.Min(c => c.TrainH1.Length);
        int stepSize = minLen / 8;
        if (stepSize < 100) stepSize = 100;
        int embargo = EmbargoBars(stepSize, cfg);

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd + embargo >= minLen) break;

            var trainCoins = coins.Select(c =>
            {
                var h1 = c.TrainH1.ToArray();
                var m15 = c.TrainM15.ToArray();
                return new SwingLongGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, 0, Math.Min(trainEnd, h1.Length)),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, 0, Math.Min(trainEnd * 4, m15.Length)),
                    c.ValM15, c.Weight);
            }).ToList();

            var ga = new SwingLongGA(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(trainCoins);

            double isSharpe = ComputeSharpeSwingLong(geno, trainCoins);

            var testCoins = coins.Select(c =>
            {
                var h1 = c.TrainH1.ToArray();
                var m15 = c.TrainM15.ToArray();
                // testStart already includes the embargo gap; the m15 offset is the usual
                // h1index*4 so the two timeframes stay aligned across the gap.
                int testStart = Math.Min(trainEnd + embargo, h1.Length);
                int testLen = Math.Min(stepSize, h1.Length - testStart);
                int m15Start = Math.Min(testStart * 4, m15.Length);
                int m15Len = Math.Max(0, Math.Min(testLen * 4, m15.Length - m15Start));
                if (testLen <= 0) return new SwingLongGA.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty,
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new SwingLongGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, testStart, testLen),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, m15Start, m15Len),
                    c.ValM15, c.Weight);
            }).Where(c => c.TrainH1.Length > 0).ToList();

            double oosSharpe = ComputeSharpeSwingLong(geno, testCoins);

            windows.Add(MakeWindow(w, trainEnd, stepSize, isSharpe, oosSharpe));
        }

        return BuildReport("SwingLong", windows);
    }

    private static double ComputeSharpeFadeShort(FadeShortGenotype ind, IReadOnlyList<FadeShortGA.CoinData> coins)
    {
        var all = new List<double>();
        foreach (var c in coins)
        {
            if (c.TrainCandles.Length < 100) continue;
            foreach (var t in FadeShortSimulator.GetFadeShortReturns(ind, c.TrainCandles.Span))
                all.Add(t.Return);
        }
        return all.Count >= 10 ? Simulator.SharpeRatio(all, all.Count) : 0;
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
        return all.Count >= 10 ? Simulator.SharpeRatio(all, all.Count) : 0;
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
        return all.Count >= 10 ? Simulator.SharpeRatio(all, all.Count) : 0;
    }

    public static ExpandingWindowReport RunRipShort(
        IReadOnlyList<RipShortGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        cfg ??= new FitnessConfig();
        var windows = new List<WindowResult>();

        int minLen = coins.Min(c => c.TrainH1.Length);
        int stepSize = minLen / 8;
        if (stepSize < 100) stepSize = 100;
        int embargo = EmbargoBars(stepSize, cfg);

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd + embargo >= minLen) break;

            var trainCoins = coins.Select(c =>
            {
                var h1 = c.TrainH1.ToArray();
                var m15 = c.TrainM15.ToArray();
                return new RipShortGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, 0, Math.Min(trainEnd, h1.Length)),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, 0, Math.Min(trainEnd * 4, m15.Length)),
                    c.ValM15, c.Weight);
            }).ToList();

            var ga = new RipShortGA(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(trainCoins);

            double isSharpe = ComputeSharpeRipShort(geno, trainCoins);

            var testCoins = coins.Select(c =>
            {
                var h1 = c.TrainH1.ToArray();
                var m15 = c.TrainM15.ToArray();
                // testStart already includes the embargo gap; the m15 offset is the usual
                // h1index*4 so the two timeframes stay aligned across the gap.
                int testStart = Math.Min(trainEnd + embargo, h1.Length);
                int testLen = Math.Min(stepSize, h1.Length - testStart);
                int m15Start = Math.Min(testStart * 4, m15.Length);
                int m15Len = Math.Max(0, Math.Min(testLen * 4, m15.Length - m15Start));
                if (testLen <= 0) return new RipShortGA.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty,
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new RipShortGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, testStart, testLen),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, m15Start, m15Len),
                    c.ValM15, c.Weight);
            }).Where(c => c.TrainH1.Length > 0).ToList();

            double oosSharpe = ComputeSharpeRipShort(geno, testCoins);

            windows.Add(MakeWindow(w, trainEnd, stepSize, isSharpe, oosSharpe));
        }

        return BuildReport("RipShort", windows);
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
        return all.Count >= 10 ? Simulator.SharpeRatio(all, all.Count) : 0;
    }

    public static ExpandingWindowReport RunFadeLong(
        IReadOnlyList<FadeLongGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        cfg ??= new FitnessConfig();
        var windows = new List<WindowResult>();

        int minLen = coins.Min(c => c.TrainH1.Length);
        int stepSize = minLen / 8;
        if (stepSize < 100) stepSize = 100;
        int embargo = EmbargoBars(stepSize, cfg);

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd + embargo >= minLen) break;

            var trainCoins = coins.Select(c =>
            {
                var h1 = c.TrainH1.ToArray();
                var m15 = c.TrainM15.ToArray();
                return new FadeLongGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, 0, Math.Min(trainEnd, h1.Length)),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, 0, Math.Min(trainEnd * 4, m15.Length)),
                    c.ValM15, c.Weight);
            }).ToList();

            var ga = new FadeLongGA(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(trainCoins);

            double isSharpe = ComputeSharpeFadeLong(geno, trainCoins);

            var testCoins = coins.Select(c =>
            {
                var h1 = c.TrainH1.ToArray();
                var m15 = c.TrainM15.ToArray();
                // testStart already includes the embargo gap; the m15 offset is the usual
                // h1index*4 so the two timeframes stay aligned across the gap.
                int testStart = Math.Min(trainEnd + embargo, h1.Length);
                int testLen = Math.Min(stepSize, h1.Length - testStart);
                int m15Start = Math.Min(testStart * 4, m15.Length);
                int m15Len = Math.Max(0, Math.Min(testLen * 4, m15.Length - m15Start));
                if (testLen <= 0) return new FadeLongGA.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty,
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new FadeLongGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, testStart, testLen),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, m15Start, m15Len),
                    c.ValM15, c.Weight);
            }).Where(c => c.TrainH1.Length > 0).ToList();

            double oosSharpe = ComputeSharpeFadeLong(geno, testCoins);

            windows.Add(MakeWindow(w, trainEnd, stepSize, isSharpe, oosSharpe));
        }

        return BuildReport("FadeLong", windows);
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
        return all.Count >= 10 ? Simulator.SharpeRatio(all, all.Count) : 0;
    }

    public static ExpandingWindowReport RunGrid(
        IReadOnlyList<GridGeneticAlgorithm.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        cfg ??= new FitnessConfig();
        var windows = new List<WindowResult>();

        int minLen = coins.Min(c => c.TrainCandles.Length);
        int stepSize = minLen / 8;
        if (stepSize < 100) stepSize = 100;
        int embargo = EmbargoBars(stepSize, cfg);

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd + embargo >= minLen) break;

            var trainCoins = coins.Select(c =>
            {
                var arr = c.TrainCandles.ToArray();
                return new GridGeneticAlgorithm.CoinData(
                    new ReadOnlyMemory<Candle>(arr, 0, Math.Min(trainEnd, arr.Length)),
                    c.ValCandles, c.Weight);
            }).ToList();

            var ga = new GridGeneticAlgorithm(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(trainCoins);

            double isSharpe = ComputeSharpeGrid(geno, trainCoins);

            var testCoins = coins.Select(c =>
            {
                var arr = c.TrainCandles.ToArray();
                int testStart = Math.Min(trainEnd + embargo, arr.Length);
                int testLen = Math.Min(stepSize, arr.Length - testStart);
                if (testLen <= 0) return new GridGeneticAlgorithm.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new GridGeneticAlgorithm.CoinData(
                    new ReadOnlyMemory<Candle>(arr, testStart, testLen),
                    c.ValCandles, c.Weight);
            }).Where(c => c.TrainCandles.Length > 0).ToList();

            double oosSharpe = ComputeSharpeGrid(geno, testCoins);

            windows.Add(MakeWindow(w, trainEnd, stepSize, isSharpe, oosSharpe));
        }

        return BuildReport("Grid", windows);
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
        return all.Count >= 10 ? Simulator.SharpeRatio(all, all.Count) : 0;
    }

    public static void PrintReport(ExpandingWindowReport report)
    {
        Console.WriteLine($"\n── Expanding Window Validation: {report.StrategyName} ──");
        Console.WriteLine($"  Usable windows (IS Sharpe > {MinUsableIsSharpe:F2}): {report.UsableWindows}/{report.Windows.Count}");
        if (report.IsInconclusive)
        {
            Console.WriteLine("  Mean efficiency (OOS Sharpe / IS Sharpe): n/a");
            Console.WriteLine("  Overfit flag: INCONCLUSIVE — no window had a usable in-sample Sharpe");
            Console.WriteLine("    (Simulator.SharpeRatio returns 0 when profit factor < 1.3, so this says");
            Console.WriteLine("     nothing about overfitting — the efficiency ratio simply has no denominator)");
        }
        else
        {
            Console.WriteLine($"  Mean efficiency (OOS Sharpe / IS Sharpe): {report.MeanEfficiency:F3}  (usable windows only, clamped to [{EfficiencyFloor:F1}, {EfficiencyCeiling:F1}])");
            Console.WriteLine($"  Overfit flag (efficiency < 0.5): {(report.IsOverfit ? "YES — OVERFIT" : "no")}");
        }
        Console.WriteLine($"  {"Window",8} {"Train bars",12} {"Test bars",10} {"IS Sharpe",10} {"OOS Sharpe",11} {"Efficiency",11} {"Usable",8}");
        foreach (var w in report.Windows)
        {
            string eff = w.IsUsable ? w.Efficiency.ToString("F3") : "—";
            Console.WriteLine($"  {w.WindowIndex,8} {w.TrainBars,12:F0} {w.TestBars,10:F0} {w.InSampleSharpe,10:F4} {w.OutOfSampleSharpe,11:F4} {eff,11} {(w.IsUsable ? "yes" : "no"),8}");
        }
    }
}
