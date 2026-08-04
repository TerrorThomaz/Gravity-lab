namespace TradingGA;

public static class MonteCarloTest
{
    public record MonteCarloResult(
        double ActualSharpe,
        double MeanShuffledSharpe,
        double StdShuffledSharpe,
        double PValue,
        bool IsSignificant,
        int Permutations);

    public static MonteCarloResult Run(
        List<double> returns,
        int permutations = 1000,
        Random? rng = null)
    {
        rng ??= new Random(42);

        if (returns.Count < 10)
            return new(0, 0, 0, 1.0, false, 0);

        double actualSharpe = Simulator.SharpeRatio(returns, returns.Count);

        int countAtOrAbove = 0;
        var allShuffledSharpes = new double[permutations];

        for (int p = 0; p < permutations; p++)
        {
            var shuffled = returns.ToList();
            for (int i = shuffled.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }
            double shuffledSharpe = Simulator.SharpeRatio(shuffled, shuffled.Count);
            allShuffledSharpes[p] = shuffledSharpe;
            if (shuffledSharpe >= actualSharpe)
                countAtOrAbove++;
        }

        double pValue = (double)countAtOrAbove / permutations;
        double meanShuffled = allShuffledSharpes.Average();
        double stdShuffled = Math.Sqrt(allShuffledSharpes.Select(s => (s - meanShuffled) * (s - meanShuffled)).Average());

        return new(actualSharpe, meanShuffled, stdShuffled, pValue, pValue <= 0.05, permutations);
    }

    public static void PrintReport(MonteCarloResult result, string strategyName)
    {
        Console.WriteLine($"\n── Monte Carlo Permutation Test: {strategyName} ──");
        Console.WriteLine($"  Actual Sharpe:     {result.ActualSharpe:F4}");
        Console.WriteLine($"  Mean shuffled:     {result.MeanShuffledSharpe:F4}");
        Console.WriteLine($"  Std shuffled:      {result.StdShuffledSharpe:F4}");
        Console.WriteLine($"  p-value:           {result.PValue:F4}  ({result.Permutations} permutations)");
        Console.WriteLine($"  Significant (p≤0.05): {(result.IsSignificant ? "YES" : "NO")}");
        if (!result.IsSignificant)
            Console.WriteLine("  !! Edge may not be statistically significant — strategy could be a data artifact");
    }
}
