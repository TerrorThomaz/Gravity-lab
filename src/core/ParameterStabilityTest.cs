namespace TradingGA;

public static class ParameterStabilityTest
{
    public record StabilityResult(
        string ParameterName,
        double Mean,
        double StdDev,
        double CV,
        bool IsUnstable);

    public record StabilityReport(
        string StrategyName,
        List<StabilityResult> Results,
        double MaxCV,
        bool HasUnstableParams,
        double StabilityPenalty);

    public static StabilityReport TestFadeShort(
        IReadOnlyList<FadeShortGA.CoinData> coins,
        FitnessConfig? cfg = null,
        int seeds = 5)
    {
        cfg ??= new FitnessConfig();
        var vectors = new List<double[]>();

        for (int s = 0; s < seeds; s++)
        {
            var ga = new FadeShortGA(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(coins);
            vectors.Add(geno.ToVector());
        }

        return ComputeStability("FadeShort", vectors, FadeShortGenotype.ParameterNames, cfg);
    }

    public static StabilityReport TestDipLong(
        IReadOnlyList<DipLongGA.CoinData> coins,
        FitnessConfig? cfg = null,
        int seeds = 5)
    {
        cfg ??= new FitnessConfig();
        var vectors = new List<double[]>();

        for (int s = 0; s < seeds; s++)
        {
            var ga = new DipLongGA(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(coins);
            vectors.Add(geno.ToVector());
        }

        return ComputeStability("DipLong", vectors, DipLongGenotype.ParameterNames, cfg);
    }

    public static StabilityReport TestSwingLong(
        IReadOnlyList<SwingLongGA.CoinData> coins,
        FitnessConfig? cfg = null,
        int seeds = 5)
    {
        cfg ??= new FitnessConfig();
        var vectors = new List<double[]>();

        for (int s = 0; s < seeds; s++)
        {
            var ga = new SwingLongGA(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(coins);
            vectors.Add(geno.ToVector());
        }

        return ComputeStability("SwingLong", vectors, SwingLongGenotype.ParameterNames, cfg);
    }

    public static StabilityReport TestRipShort(
        IReadOnlyList<RipShortGA.CoinData> coins,
        FitnessConfig? cfg = null,
        int seeds = 5)
    {
        cfg ??= new FitnessConfig();
        var vectors = new List<double[]>();

        for (int s = 0; s < seeds; s++)
        {
            var ga = new RipShortGA(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(coins);
            vectors.Add(geno.ToVector());
        }

        return ComputeStability("RipShort", vectors, RipShortGenotype.ParameterNames, cfg);
    }

    public static StabilityReport TestFadeLong(
        IReadOnlyList<FadeLongGA.CoinData> coins,
        FitnessConfig? cfg = null,
        int seeds = 5)
    {
        cfg ??= new FitnessConfig();
        var vectors = new List<double[]>();

        for (int s = 0; s < seeds; s++)
        {
            var ga = new FadeLongGA(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(coins);
            vectors.Add(geno.ToVector());
        }

        return ComputeStability("FadeLong", vectors, FadeLongGenotype.ParameterNames, cfg);
    }

    public static StabilityReport TestGrid(
        IReadOnlyList<GridGeneticAlgorithm.CoinData> coins,
        FitnessConfig? cfg = null,
        int seeds = 5)
    {
        cfg ??= new FitnessConfig();
        var vectors = new List<double[]>();

        for (int s = 0; s < seeds; s++)
        {
            var ga = new GridGeneticAlgorithm(populationSize: 30, generations: 40, verbose: false, cfg: cfg);
            var geno = ga.Run(coins);
            vectors.Add(geno.ToVector());
        }

        return ComputeStability("Grid", vectors, GridGenotype.ParameterNames, cfg);
    }

    private static StabilityReport ComputeStability(
        string strategyName,
        List<double[]> vectors,
        string[] paramNames,
        FitnessConfig cfg)
    {
        if (vectors.Count == 0 || vectors[0].Length == 0)
            return new(strategyName, new List<StabilityResult>(), 0, false, 1.0);

        int dim = vectors[0].Length;
        var results = new List<StabilityResult>();
        double maxCV = 0;

        for (int d = 0; d < dim; d++)
        {
            var values = vectors.Select(v => v[d]).ToList();
            double mean = values.Average();
            double variance = values.Select(v => (v - mean) * (v - mean)).Average();
            double stdDev = Math.Sqrt(variance);
            double cv = Math.Abs(mean) > 1e-10 ? stdDev / Math.Abs(mean) : (stdDev > 1e-10 ? 10.0 : 0.0);
            bool isUnstable = cv > 0.20;
            maxCV = Math.Max(maxCV, cv);

            string name = d < paramNames.Length ? paramNames[d] : $"param_{d}";
            results.Add(new StabilityResult(name, mean, stdDev, cv, isUnstable));
        }

        double penalty = maxCV > 0.20 ? 1.0 - Math.Max(0, maxCV - 0.20) * 2.0 : 1.0;
        penalty = Math.Max(0.1, penalty);

        return new(strategyName, results, maxCV, maxCV > 0.20, penalty);
    }

    public static void PrintReport(StabilityReport report)
    {
        Console.WriteLine($"\n── Parameter Stability Report: {report.StrategyName} ──");
        Console.WriteLine($"  Max CV: {report.MaxCV:F3}  Unstable: {(report.HasUnstableParams ? "YES" : "no")}  Penalty: {report.StabilityPenalty:F3}");
        Console.WriteLine($"  {"Parameter",-20} {"Mean",10} {"StdDev",10} {"CV",8} {"Status",10}");
        foreach (var r in report.Results)
        {
            string status = r.IsUnstable ? "UNSTABLE" : "ok";
            Console.WriteLine($"  {r.ParameterName,-20} {r.Mean,10:F4} {r.StdDev,10:F4} {r.CV,8:F3} {status,10}");
        }
    }
}
