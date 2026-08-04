namespace TradingGA;

public static class ExpandingWindowValidation
{
    public record WindowResult(
        int WindowIndex,
        double TrainBars,
        double TestBars,
        double InSampleSharpe,
        double OutOfSampleSharpe,
        double Efficiency);

    public record ExpandingWindowReport(
        string StrategyName,
        List<WindowResult> Windows,
        double MeanEfficiency,
        bool IsOverfit);

    public static ExpandingWindowReport RunFadeShort(
        IReadOnlyList<FadeShortGA.CoinData> coins,
        FitnessConfig? cfg = null)
    {
        cfg ??= new FitnessConfig();
        var windows = new List<WindowResult>();

        int minLen = coins.Min(c => c.TrainCandles.Length);
        int stepSize = minLen / 8;
        if (stepSize < 100) stepSize = 100;

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd >= minLen) break;

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
                int testStart = Math.Min(trainEnd, arr.Length);
                int testLen = Math.Min(stepSize, arr.Length - testStart);
                if (testLen <= 0) return new FadeShortGA.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new FadeShortGA.CoinData(
                    new ReadOnlyMemory<Candle>(arr, testStart, testLen),
                    c.ValCandles, c.Weight);
            }).Where(c => c.TrainCandles.Length > 0).ToList();

            double oosSharpe = ComputeSharpeFadeShort(geno, testCoins);
            double efficiency = isSharpe > 1e-10 ? oosSharpe / isSharpe : 0;

            windows.Add(new WindowResult(w, trainEnd, stepSize, isSharpe, oosSharpe, efficiency));
        }

        double meanEff = windows.Count > 0 ? windows.Average(w => w.Efficiency) : 0;
        return new("FadeShort", windows, meanEff, meanEff < 0.5);
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

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd >= minLen) break;

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
                int testStart = Math.Min(trainEnd, h1.Length);
                int testLen = Math.Min(stepSize, h1.Length - testStart);
                if (testLen <= 0) return new DipLongGA.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty,
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new DipLongGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, testStart, testLen),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, testStart * 4, Math.Min(testLen * 4, m15.Length - testStart * 4)),
                    c.ValM15, c.Weight);
            }).Where(c => c.TrainH1.Length > 0).ToList();

            double oosSharpe = ComputeSharpeDipLong(geno, testCoins);
            double efficiency = isSharpe > 1e-10 ? oosSharpe / isSharpe : 0;

            windows.Add(new WindowResult(w, trainEnd, stepSize, isSharpe, oosSharpe, efficiency));
        }

        double meanEff = windows.Count > 0 ? windows.Average(w => w.Efficiency) : 0;
        return new("DipLong", windows, meanEff, meanEff < 0.5);
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

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd >= minLen) break;

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
                int testStart = Math.Min(trainEnd, h1.Length);
                int testLen = Math.Min(stepSize, h1.Length - testStart);
                if (testLen <= 0) return new SwingLongGA.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty,
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new SwingLongGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, testStart, testLen),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, testStart * 4, Math.Min(testLen * 4, m15.Length - testStart * 4)),
                    c.ValM15, c.Weight);
            }).Where(c => c.TrainH1.Length > 0).ToList();

            double oosSharpe = ComputeSharpeSwingLong(geno, testCoins);
            double efficiency = isSharpe > 1e-10 ? oosSharpe / isSharpe : 0;

            windows.Add(new WindowResult(w, trainEnd, stepSize, isSharpe, oosSharpe, efficiency));
        }

        double meanEff = windows.Count > 0 ? windows.Average(w => w.Efficiency) : 0;
        return new("SwingLong", windows, meanEff, meanEff < 0.5);
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

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd >= minLen) break;

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
                int testStart = Math.Min(trainEnd, h1.Length);
                int testLen = Math.Min(stepSize, h1.Length - testStart);
                if (testLen <= 0) return new RipShortGA.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty,
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new RipShortGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, testStart, testLen),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, testStart * 4, Math.Min(testLen * 4, m15.Length - testStart * 4)),
                    c.ValM15, c.Weight);
            }).Where(c => c.TrainH1.Length > 0).ToList();

            double oosSharpe = ComputeSharpeRipShort(geno, testCoins);
            double efficiency = isSharpe > 1e-10 ? oosSharpe / isSharpe : 0;

            windows.Add(new WindowResult(w, trainEnd, stepSize, isSharpe, oosSharpe, efficiency));
        }

        double meanEff = windows.Count > 0 ? windows.Average(w => w.Efficiency) : 0;
        return new("RipShort", windows, meanEff, meanEff < 0.5);
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

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd >= minLen) break;

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
                int testStart = Math.Min(trainEnd, h1.Length);
                int testLen = Math.Min(stepSize, h1.Length - testStart);
                if (testLen <= 0) return new FadeLongGA.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty,
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new FadeLongGA.CoinData(
                    new ReadOnlyMemory<Candle>(h1, testStart, testLen),
                    c.ValH1,
                    new ReadOnlyMemory<Candle>(m15, testStart * 4, Math.Min(testLen * 4, m15.Length - testStart * 4)),
                    c.ValM15, c.Weight);
            }).Where(c => c.TrainH1.Length > 0).ToList();

            double oosSharpe = ComputeSharpeFadeLong(geno, testCoins);
            double efficiency = isSharpe > 1e-10 ? oosSharpe / isSharpe : 0;

            windows.Add(new WindowResult(w, trainEnd, stepSize, isSharpe, oosSharpe, efficiency));
        }

        double meanEff = windows.Count > 0 ? windows.Average(w => w.Efficiency) : 0;
        return new("FadeLong", windows, meanEff, meanEff < 0.5);
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

        for (int w = 0; w < 4; w++)
        {
            int trainEnd = minLen / 4 + w * stepSize;
            if (trainEnd >= minLen) break;

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
                int testStart = Math.Min(trainEnd, arr.Length);
                int testLen = Math.Min(stepSize, arr.Length - testStart);
                if (testLen <= 0) return new GridGeneticAlgorithm.CoinData(
                    ReadOnlyMemory<Candle>.Empty, ReadOnlyMemory<Candle>.Empty, 0);
                return new GridGeneticAlgorithm.CoinData(
                    new ReadOnlyMemory<Candle>(arr, testStart, testLen),
                    c.ValCandles, c.Weight);
            }).Where(c => c.TrainCandles.Length > 0).ToList();

            double oosSharpe = ComputeSharpeGrid(geno, testCoins);
            double efficiency = isSharpe > 1e-10 ? oosSharpe / isSharpe : 0;

            windows.Add(new WindowResult(w, trainEnd, stepSize, isSharpe, oosSharpe, efficiency));
        }

        double meanEff = windows.Count > 0 ? windows.Average(w => w.Efficiency) : 0;
        return new("Grid", windows, meanEff, meanEff < 0.5);
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
        Console.WriteLine($"  Mean efficiency (OOS Sharpe / IS Sharpe): {report.MeanEfficiency:F3}");
        Console.WriteLine($"  Overfit flag (efficiency < 0.5): {(report.IsOverfit ? "YES — OVERFIT" : "no")}");
        Console.WriteLine($"  {"Window",8} {"Train bars",12} {"Test bars",10} {"IS Sharpe",10} {"OOS Sharpe",11} {"Efficiency",11}");
        foreach (var w in report.Windows)
        {
            Console.WriteLine($"  {w.WindowIndex,8} {w.TrainBars,12:F0} {w.TestBars,10:F0} {w.InSampleSharpe,10:F4} {w.OutOfSampleSharpe,11:F4} {w.Efficiency,11:F3}");
        }
    }
}
