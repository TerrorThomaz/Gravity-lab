using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TradingGA;

public static class PerformanceEvaluator
{
    // ============================================================
    // 1. TRADING PERFORMANCE METRICS
    // ============================================================
    
    public static double TotalReturnPct(List<double> returns)
    {
        if (returns.Count == 0) return 0;
        double total = returns.Sum();
        // Total Return (%) = cumulative return percentage
        // For log returns would be exp(sum)-1, but these are already % returns
        return total;
    }
    
    public static double AnnualizedReturnPct(List<double> returns, int totalCandles, double candlesPerYear = 252 * 6) // 6 periods per day for 4h? Actually depends on TF
    {
        if (returns.Count == 0) return 0;
        double totalRet = returns.Sum() / 100.0; // Convert to decimal
        double years = totalCandles / candlesPerYear;
        if (years <= 0) return 0;
        double annualized = Math.Pow(1 + totalRet, 1.0 / years) - 1;
        return annualized * 100;
    }
    
    public static double SharpeRatio(List<double> returns, int candleCount, double riskFreeRate = 0)
    {
        if (returns.Count < 5) return 0;
        double mean = returns.Average() / 100.0; // Convert to decimal
        double std = Math.Sqrt(returns.Select(r => Math.Pow(r / 100.0 - mean, 2)).Average());
        if (std < 1e-10) return 0;
        
        double years = candleCount / (252.0 * 6.0); // Assuming 4h candles (6 per day)
        double sharpe = (mean - riskFreeRate) / std * Math.Sqrt(1.0 / years);
        return sharpe;
    }
    
    public static double SortinoRatio(List<double> returns, int candleCount, double riskFreeRate = 0)
    {
        if (returns.Count < 5) return 0;
        double mean = returns.Average() / 100.0;
        var negReturns = returns.Where(r => r < 0).Select(r => r / 100.0).ToList();
        if (negReturns.Count == 0) return mean > 0 ? double.MaxValue : 0;
        
        double downStd = Math.Sqrt(negReturns.Select(r => r * r).Average());
        if (downStd < 1e-10) return 0;
        
        double years = candleCount / (252.0 * 6.0);
        double sortino = (mean - riskFreeRate) / downStd * Math.Sqrt(1.0 / years);
        return sortino;
    }
    
    public static double MaxDrawdownPct(List<double> returns)
    {
        double cumulative = 0, peak = 0, maxDd = 0;
        foreach (var r in returns)
        {
            cumulative += r;
            if (cumulative > peak) peak = cumulative;
            double dd = peak - cumulative;
            if (dd > maxDd) maxDd = dd;
        }
        return maxDd;
    }
    
    public static double CalmarRatio(List<double> returns, int totalCandles)
    {
        if (returns.Count < 5) return 0;
        double totalRet = returns.Sum() / 100.0;
        double maxDd = MaxDrawdownPct(returns) / 100.0;
        if (maxDd < 1e-10) return totalRet > 0 ? double.MaxValue : 0;
        
        double years = totalCandles / (252.0 * 6.0);
        double annualRet = Math.Pow(1 + totalRet, 1.0 / years) - 1;
        return annualRet / maxDd;
    }
    
    public static double WinRate(List<double> returns)
    {
        if (returns.Count == 0) return 0;
        return (double)returns.Count(r => r > 0) / returns.Count * 100;
    }
    
    public static double ProfitFactor(List<double> returns)
    {
        double grossProfit = returns.Where(r => r > 0).Sum();
        double grossLoss = Math.Abs(returns.Where(r => r <= 0).Sum());
        if (grossLoss < 1e-10) return grossProfit > 0 ? 999.99 : 0;
        return grossProfit / grossLoss;
    }
    
    public static double AverageTradeReturn(List<double> returns)
    {
        return returns.Count > 0 ? returns.Average() : 0;
    }
    
    public static double Expectancy(List<double> returns, double positionSizePct = 0.05)
    {
        // Expected return per trade as % of account
        double avgRetPct = returns.Average();
        return avgRetPct * (positionSizePct / 100.0);
    }
    
    public static int MaxConsecutiveLosses(List<double> returns)
    {
        int max = 0, curr = 0;
        foreach (var r in returns)
        {
            if (r <= 0) { curr++; if (curr > max) max = curr; }
            else curr = 0;
        }
        return max;
    }
    
    public static int MaxConsecutiveWins(List<double> returns)
    {
        int max = 0, curr = 0;
        foreach (var r in returns)
        {
            if (r > 0) { curr++; if (curr > max) max = curr; }
            else curr = 0;
        }
        return max;
    }
    
    public static double RecoveryFactor(List<double> returns)
    {
        double totalRet = returns.Sum() / 100.0;
        double maxDd = MaxDrawdownPct(returns) / 100.0;
        if (maxDd < 1e-10) return totalRet > 0 ? double.MaxValue : 0;
        return totalRet / maxDd;
    }
    
    // ============================================================
    // 2. MACHINE LEARNING / ROBUSTNESS METRICS
    // ============================================================
    
    public class WalkForwardResult
    {
        public double InSampleSharpe { get; set; }
        public double OutOfSampleSharpe { get; set; }
        public double WalkForwardRatio { get; set; }
        public double PerformanceDegradation { get; set; }
        public bool IsOverfit => WalkForwardRatio > 1.5;
    }
    
    public static WalkForwardResult WalkForwardAnalysis(
        List<double> allReturns,
        int inSamplePct = 70,
        int nFolds = 3)
    {
        var result = new WalkForwardResult();
        
        int foldSize = allReturns.Count / nFolds;
        var isSharpeList = new List<double>();
        var oosSharpeList = new List<double>();
        
        for (int fold = 0; fold < nFolds; fold++)
        {
            int oosStart = fold * foldSize;
            int oosEnd = Math.Min((fold + 1) * foldSize, allReturns.Count);
            
            var isReturns = allReturns.Take(oosStart).Concat(allReturns.Skip(oosEnd)).ToList();
            var oosReturns = allReturns.Skip(oosStart).Take(foldSize).ToList();
            
            if (isReturns.Count >= 5 && oosReturns.Count >= 5)
            {
                isSharpeList.Add(SharpeRatio(isReturns, isReturns.Count));
                oosSharpeList.Add(SharpeRatio(oosReturns, oosReturns.Count));
            }
        }
        
        if (isSharpeList.Count > 0)
        {
            result.InSampleSharpe = isSharpeList.Average();
            result.OutOfSampleSharpe = oosSharpeList.Average();
            result.WalkForwardRatio = result.OutOfSampleSharpe > 0 
                ? result.InSampleSharpe / result.OutOfSampleSharpe 
                : double.MaxValue;
            result.PerformanceDegradation = result.InSampleSharpe - result.OutOfSampleSharpe;
        }
        
        return result;
    }
    
    public static double FitnessStabilityIndex(List<double> fitnessHistory)
    {
        if (fitnessHistory.Count < 2) return 0;
        double sumRelChange = 0;
        for (int i = 1; i < fitnessHistory.Count; i++)
        {
            if (Math.Abs(fitnessHistory[i - 1]) > 1e-10)
            {
                sumRelChange += Math.Abs(fitnessHistory[i] - fitnessHistory[i - 1]) / Math.Abs(fitnessHistory[i - 1]);
            }
        }
        return sumRelChange / (fitnessHistory.Count - 1);
    }
    
    public static double RollingSharpeStdDev(List<double> returns, int windowSize = 20)
    {
        if (returns.Count < windowSize) return 0;
        
        var rollingSharpe = new List<double>();
        for (int i = windowSize; i <= returns.Count; i++)
        {
            var window = returns.GetRange(i - windowSize, windowSize);
            rollingSharpe.Add(SharpeRatio(window, windowSize));
        }
        
        if (rollingSharpe.Count < 2) return 0;
        double mean = rollingSharpe.Average();
        double variance = rollingSharpe.Select(s => Math.Pow(s - mean, 2)).Average();
        return Math.Sqrt(variance);
    }
    
    public static double ParameterSensitivity(Dictionary<string, List<double>> paramPerformance)
    {
        // paramPerformance: param name -> list of returns for different param values
        // Returns average coefficient of variation across parameters
        if (paramPerformance.Count == 0) return 0;
        
        double totalCV = 0;
        foreach (var kvp in paramPerformance)
        {
            var perf = kvp.Value;
            if (perf.Count < 2) continue;
            
            double mean = perf.Average();
            double std = Math.Sqrt(perf.Select(p => Math.Pow(p - mean, 2)).Average());
            if (Math.Abs(mean) > 1e-10)
                totalCV += std / Math.Abs(mean);
        }
        
        return totalCV / paramPerformance.Count;
    }
    
    public static double TrainTestCorrelation(List<double> trainReturns, List<double> testReturns)
    {
        // Correlation of rolling Sharpe or daily returns
        int minLen = Math.Min(trainReturns.Count, testReturns.Count);
        if (minLen < 5) return 0;
        
        var trainSubset = trainReturns.Take(minLen).ToList();
        var testSubset = testReturns.Take(minLen).ToList();
        
        double trainMean = trainSubset.Average();
        double testMean = testSubset.Average();
        
        double numerator = 0, trainVar = 0, testVar = 0;
        for (int i = 0; i < minLen; i++)
        {
            double trainDiff = trainSubset[i] - trainMean;
            double testDiff = testSubset[i] - testMean;
            numerator += trainDiff * testDiff;
            trainVar += trainDiff * trainDiff;
            testVar += testDiff * testDiff;
        }
        
        if (trainVar < 1e-10 || testVar < 1e-10) return 0;
        return numerator / Math.Sqrt(trainVar * testVar);
    }
    
    public static double ProbabilityOfOutperformance(List<double> returnsA, List<double> returnsB, int nSamples = 10000)
    {
        // Bayesian bootstrap: P(Sharpe_A > Sharpe_B | data)
        if (returnsA.Count < 5 || returnsB.Count < 5) return 0.5;
        
        int n = Math.Min(returnsA.Count, returnsB.Count);
        var sharpeDiff = new List<double>();
        
        var rand = new Random(42);
        for (int sample = 0; sample < nSamples; sample++)
        {
            // Dirichlet(1,...,1) weights = exponential(1)
            var weightsA = Enumerable.Range(0, n).Select(_ => -Math.Log(rand.NextDouble())).ToArray();
            var weightsB = Enumerable.Range(0, n).Select(_ => -Math.Log(rand.NextDouble())).ToArray();
            
            double sumWtA = weightsA.Sum();
            double sumWtB = weightsB.Sum();
            
            double meanA = returnsA.Take(n).Zip(weightsA, (r, w) => r * w / sumWtA).Sum() / 100.0;
            double meanB = returnsB.Take(n).Zip(weightsB, (r, w) => r * w / sumWtB).Sum() / 100.0;
            
            double varA = returnsA.Take(n).Zip(weightsA, (r, w) => Math.Pow(r / 100.0 - meanA, 2) * w / sumWtA).Sum();
            double varB = returnsB.Take(n).Zip(weightsB, (r, w) => Math.Pow(r / 100.0 - meanB, 2) * w / sumWtB).Sum();
            
            double sharpeA = varA > 0 ? meanA / Math.Sqrt(varA) : 0;
            double sharpeB = varB > 0 ? meanB / Math.Sqrt(varB) : 0;
            
            sharpeDiff.Add(sharpeA - sharpeB);
        }
        
        return (double)sharpeDiff.Count(d => d > 0) / nSamples;
    }
    
    public static double DeflatedSharpeRatio(List<double> returns, int nTrials = 1000)
    {
        // Adjust Sharpe for multiple testing / GA search
        double rawSharpe = SharpeRatio(returns, returns.Count);
        if (rawSharpe <= 0) return 0;
        
        // Estimate variance of maximum Sharpe under null
        double expectedMaxSharpe = Math.Sqrt(2 * Math.Log(nTrials)) / Math.Sqrt(returns.Count);
        double adjustedSharpe = rawSharpe - expectedMaxSharpe;
        
        return Math.Max(0, adjustedSharpe);
    }
    
    // ============================================================
    // 3. STATISTICAL COMPARISON TESTS
    // ============================================================
    
    public class StatisticalTestResult
    {
        public string TestName { get; set; }
        public double Statistic { get; set; }
        public double PValue { get; set; }
        public bool SignificantAt95 => PValue < 0.05;
        public string Interpretation { get; set; }
    }
    
    public static StatisticalTestResult PairedTTest(List<double> returnsA, List<double> returnsB)
    {
        int n = Math.Min(returnsA.Count, returnsB.Count);
        var differences = Enumerable.Range(0, n).Select(i => returnsA[i] - returnsB[i]).ToList();
        
        double meanDiff = differences.Average();
        double stdDiff = Math.Sqrt(differences.Select(d => Math.Pow(d - meanDiff, 2)).Average());
        
        double tStat = stdDiff > 0 ? meanDiff / (stdDiff / Math.Sqrt(n)) : 0;
        
        // Approximate p-value using t-distribution (simplified)
        double pValue = 2 * (1 - StudentsTCDF(Math.Abs(tStat), n - 1));
        
        return new StatisticalTestResult
        {
            TestName = "Paired t-test",
            Statistic = tStat,
            PValue = pValue,
            Interpretation = pValue < 0.05 ? "Significant difference" : "No significant difference"
        };
    }
    
    public static StatisticalTestResult MannWhitneyUTest(List<double> returnsA, List<double> returnsB)
    {
        var combined = returnsA.Select(r => new { Value = r, Group = "A" })
            .Concat(returnsB.Select(r => new { Value = r, Group = "B" }))
            .OrderBy(x => x.Value)
            .ToList();
        
        int n1 = returnsA.Count, n2 = returnsB.Count;
        double rankSumA = combined.Select((x, i) => new { x.Group, Rank = i + 1 })
            .Where(x => x.Group == "A")
            .Sum(x => x.Rank);
        
        double u = rankSumA - (n1 * (n1 + 1.0) / 2);
        double expectedU = n1 * n2 / 2.0;
        double stdU = Math.Sqrt(n1 * n2 * (n1 + n2 + 1.0) / 12.0);
        
        double z = (u - expectedU) / stdU;
        double pValue = 2 * (1 - StudentsTCDF(Math.Abs(z), 1000)); // Approx normal
        
        return new StatisticalTestResult
        {
            TestName = "Mann-Whitney U",
            Statistic = u,
            PValue = pValue,
            Interpretation = pValue < 0.05 ? "Different distributions" : "Same distribution"
        };
    }
    
    private static double StudentsTCDF(double t, int df)
    {
        // Approximation for two-tailed p-value
        double x = df / (df + t * t);
        double p = 1 - 0.5 * (1 + Math.Sign(t) * Math.Sqrt(1 - x));
        return p * 2; // Two-tailed
    }
    
    // ============================================================
    // 4. COMPREHENSIVE EVALUATION
    // ============================================================
    
    public class StrategyPerformance
    {
        public string Name { get; set; }
        
        // Trading metrics
        public int TotalTrades { get; set; }
        public double TotalReturn { get; set; }
        public double AnnualizedReturn { get; set; }
        public double Sharpe { get; set; }
        public double Sortino { get; set; }
        public double MaxDrawdown { get; set; }
        public double Calmar { get; set; }
        public double WinRate { get; set; }
        public double ProfitFactor { get; set; }
        public double AvgTradeReturn { get; set; }
        public double ExpectancyPerTrade { get; set; }
        public int MaxConsecutiveLosses { get; set; }
        public int MaxConsecutiveWins { get; set; }
        public double RecoveryFactor { get; set; }
        
        // ML metrics
        public WalkForwardResult WalkForward { get; set; }
        public double RollingSharpeStability { get; set; }
        public double TrainTestCorrelation { get; set; }
        public double DeflatedSharpe { get; set; }
        
        // Composite score
        public double OverallScore { get; set; }
    }
    
    public static StrategyPerformance EvaluateStrategy(
        string name,
        List<double> returns,
        int totalCandles,
        List<double> fitnessHistory = null,
        List<double> trainReturns = null,
        List<double> testReturns = null)
    {
        var perf = new StrategyPerformance { Name = name };
        
        // Trading metrics
        perf.TotalTrades = returns.Count;
        perf.TotalReturn = TotalReturnPct(returns);
        perf.AnnualizedReturn = AnnualizedReturnPct(returns, totalCandles);
        perf.Sharpe = SharpeRatio(returns, totalCandles);
        perf.Sortino = SortinoRatio(returns, totalCandles);
        perf.MaxDrawdown = MaxDrawdownPct(returns);
        perf.Calmar = CalmarRatio(returns, totalCandles);
        perf.WinRate = WinRate(returns);
        perf.ProfitFactor = ProfitFactor(returns);
        perf.AvgTradeReturn = AverageTradeReturn(returns);
        perf.ExpectancyPerTrade = Expectancy(returns);
        perf.MaxConsecutiveLosses = MaxConsecutiveLosses(returns);
        perf.MaxConsecutiveWins = MaxConsecutiveWins(returns);
        perf.RecoveryFactor = RecoveryFactor(returns);
        
        // ML metrics
        perf.WalkForward = WalkForwardAnalysis(returns);
        perf.RollingSharpeStability = RollingSharpeStdDev(returns);
        perf.TrainTestCorrelation = (trainReturns != null && testReturns != null) 
            ? TrainTestCorrelation(trainReturns, testReturns) 
            : 0;
        perf.DeflatedSharpe = DeflatedSharpeRatio(returns);
        
        // Composite score (normalized 0-100)
        double score = 0;
        score += Math.Min(100, Math.Max(0, perf.Sharpe * 20)) * 0.20;
        score += Math.Min(100, Math.Max(0, (perf.WinRate - 50) * 2)) * 0.15;
        score += Math.Min(100, Math.Max(0, (perf.ProfitFactor - 1) * 50)) * 0.15;
        score += (100 - Math.Min(100, perf.MaxDrawdown)) * 0.10;
        score += (100 - Math.Min(100, perf.RollingSharpeStability * 50)) * 0.10;
        score += (perf.DeflatedSharpe / 3.0 * 100) * 0.15;
        score += (perf.WalkForward?.IsOverfit == false ? 100 : 50) * 0.15;
        
        perf.OverallScore = score;
        
        return perf;
    }
    
    // ============================================================
    // 5. MAIN TEST HARNESS
    // ============================================================
    
    public static void RunComparison(
        List<double> gridReturns,
        List<double> swingReturns,
        int totalCandles,
        string gridName = "Grid Trading",
        string swingName = "Swing Trading",
        List<double> gridFitnessHistory = null,
        List<double> swingFitnessHistory = null)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("\n╔════════════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    TRADING ALGORITHM COMPARISON                     ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        
        // Evaluate both strategies
        var gridPerf = EvaluateStrategy(gridName, gridReturns, totalCandles, gridFitnessHistory);
        var swingPerf = EvaluateStrategy(swingName, swingReturns, totalCandles, swingFitnessHistory);
        
        // Print trading metrics table
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n┌──────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│                    TRADING PERFORMANCE METRICS                      │");
        Console.WriteLine("├────────────────────────────────┬─────────────────┬─────────────────┤");
        Console.WriteLine("│ Metric                         │ Grid            │ Swing           │");
        Console.WriteLine("├────────────────────────────────┼─────────────────┼─────────────────┤");
        Console.ResetColor();
        
        PrintMetricRow("Total Trades", gridPerf.TotalTrades, swingPerf.TotalTrades);
        PrintMetricRow("Total Return %", gridPerf.TotalReturn, swingPerf.TotalReturn, "F2", true);
        PrintMetricRow("Annualized Return %", gridPerf.AnnualizedReturn, swingPerf.AnnualizedReturn, "F2", true);
        PrintMetricRow("Sharpe Ratio", gridPerf.Sharpe, swingPerf.Sharpe, "F3");
        PrintMetricRow("Sortino Ratio", gridPerf.Sortino, swingPerf.Sortino, "F3");
        PrintMetricRow("Max Drawdown %", gridPerf.MaxDrawdown, swingPerf.MaxDrawdown, "F2", false);
        PrintMetricRow("Calmar Ratio", gridPerf.Calmar, swingPerf.Calmar, "F3");
        PrintMetricRow("Win Rate %", gridPerf.WinRate, swingPerf.WinRate, "F1");
        PrintMetricRow("Profit Factor", gridPerf.ProfitFactor, swingPerf.ProfitFactor, "F3");
        PrintMetricRow("Avg Trade Return %", gridPerf.AvgTradeReturn, swingPerf.AvgTradeReturn, "F3", true);
        PrintMetricRow("Expectancy (% of acc)", gridPerf.ExpectancyPerTrade * 100, swingPerf.ExpectancyPerTrade * 100, "F4");
        PrintMetricRow("Max Cons Losses", gridPerf.MaxConsecutiveLosses, swingPerf.MaxConsecutiveLosses);
        PrintMetricRow("Max Cons Wins", gridPerf.MaxConsecutiveWins, swingPerf.MaxConsecutiveWins);
        PrintMetricRow("Recovery Factor", gridPerf.RecoveryFactor, swingPerf.RecoveryFactor, "F3");
        
        // Print ML metrics table
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("├────────────────────────────────┼─────────────────┼─────────────────┤");
        Console.WriteLine("│               MACHINE LEARNING / ROBUSTNESS METRICS                 │");
        Console.WriteLine("├────────────────────────────────┼─────────────────┼─────────────────┤");
        Console.ResetColor();
        
        PrintMetricRow("Walk-Forward Ratio", gridPerf.WalkForward?.WalkForwardRatio ?? 0, 
                       swingPerf.WalkForward?.WalkForwardRatio ?? 0, "F3", false);
        PrintMetricRow("IS Sharpe", gridPerf.WalkForward?.InSampleSharpe ?? 0, 
                       swingPerf.WalkForward?.InSampleSharpe ?? 0, "F3");
        PrintMetricRow("OOS Sharpe", gridPerf.WalkForward?.OutOfSampleSharpe ?? 0, 
                       swingPerf.WalkForward?.OutOfSampleSharpe ?? 0, "F3");
        PrintMetricRow("Perf Degradation", gridPerf.WalkForward?.PerformanceDegradation ?? 0, 
                       swingPerf.WalkForward?.PerformanceDegradation ?? 0, "F3", false);
        PrintMetricRow("Overfit Risk", gridPerf.WalkForward?.IsOverfit == true ? "HIGH" : "LOW",
                       swingPerf.WalkForward?.IsOverfit == true ? "HIGH" : "LOW");
        PrintMetricRow("Rolling Sharpe σ", gridPerf.RollingSharpeStability, swingPerf.RollingSharpeStability, "F4", false);
        PrintMetricRow("Train-Test ρ", gridPerf.TrainTestCorrelation, swingPerf.TrainTestCorrelation, "F3");
        PrintMetricRow("Deflated Sharpe", gridPerf.DeflatedSharpe, swingPerf.DeflatedSharpe, "F3");
        
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("├────────────────────────────────┼─────────────────┼─────────────────┤");
        Console.WriteLine("│                    COMPOSITE & STATISTICAL                         │");
        Console.WriteLine("├────────────────────────────────┼─────────────────┼─────────────────┤");
        Console.ResetColor();
        
        PrintMetricRow("Overall Score (0-100)", gridPerf.OverallScore, swingPerf.OverallScore, "F1");
        
        // Statistical tests
        Console.WriteLine("├────────────────────────────────┴─────────────────┴─────────────────┤");
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("│                      STATISTICAL SIGNIFICANCE TESTS                 │");
        Console.ResetColor();
        
        var tTest = PairedTTest(gridReturns, swingReturns);
        var mwTest = MannWhitneyUTest(gridReturns, swingReturns);
        var pob = ProbabilityOfOutperformance(swingReturns, gridReturns); // P(swing better than grid)
        
        Console.WriteLine($"│  Paired t-test:        t = {tTest.Statistic:F4}, p = {tTest.PValue:F4}  {(tTest.SignificantAt95 ? "✓ SIGNIFICANT" : "✗ not significant")}");
        Console.WriteLine($"│  Mann-Whitney U:       U = {mwTest.Statistic:F1}, p = {mwTest.PValue:F4}  {(mwTest.SignificantAt95 ? "✓ SIGNIFICANT" : "✗ not significant")}");
        Console.WriteLine($"│  P(swing > grid):      {pob * 100:F1}%  {(pob > 0.95 ? "✓ SWING STRONGLY PREFERRED" : pob > 0.8 ? "✓ Swing preferred" : "↺ Too close")}");
        
        // Winner determination
        Console.WriteLine("├────────────────────────────────────────────────────────────────────┤");
        Console.ForegroundColor = ConsoleColor.Green;
        string winner = gridPerf.OverallScore > swingPerf.OverallScore ? gridName : swingName;
        string winnerColor = gridPerf.OverallScore > swingPerf.OverallScore ? "Green" : "Cyan";
        Console.ForegroundColor = winnerColor == "Green" ? ConsoleColor.Green : ConsoleColor.Cyan;
        Console.WriteLine($"│  🏆 WINNER: {winner} (Score: {Math.Max(gridPerf.OverallScore, swingPerf.OverallScore):F1})");
        Console.ResetColor();
        
        // Risk warnings
        if (gridPerf.MaxDrawdown > 30 || swingPerf.MaxDrawdown > 30)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("│  ⚠ WARNING: Max drawdown exceeds 30% - reduce position sizing");
        }
        if (gridPerf.WalkForward?.IsOverfit == true || swingPerf.WalkForward?.IsOverfit == true)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("│  ⚠ WARNING: Strategy shows overfitting - retune with more data");
        }
        
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("└────────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
    }
    
    private static void PrintMetricRow(string metric, double gridVal, double swingVal, string format = "F2", bool higherBetter = true)
    {
        string gridStr = gridVal.ToString(format);
        string swingStr = swingVal.ToString(format);
        
        Console.Write($"│ {metric,-30} │ ");
        
        // Color code better value
        bool gridBetter = higherBetter ? gridVal > swingVal : gridVal < swingVal;
        
        if (gridBetter)
            Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"{gridStr,15}");
        Console.ResetColor();
        
        Console.Write(" │ ");
        
        if (!gridBetter)
            Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"{swingStr,15}");
        Console.ResetColor();
        
        Console.WriteLine(" │");
    }
    
    private static void PrintMetricRow(string metric, int gridVal, int swingVal)
    {
        Console.WriteLine($"│ {metric,-30} │ {gridVal,15} │ {swingVal,15} │");
    }
    
    private static void PrintMetricRow(string metric, string gridVal, string swingVal)
    {
        Console.WriteLine($"│ {metric,-30} │ {gridVal,15} │ {swingVal,15} │");
    }
    
    // ============================================================
    // 6. QUICK TEST EXAMPLE
    // ============================================================
    
    public static void QuickTest()
    {
        // Example usage with simulated returns
        Console.WriteLine("PERFORMANCE EVALUATOR - READY");
        Console.WriteLine("Usage: RunComparison(gridReturns, swingReturns, totalCandles)");
        Console.WriteLine("\nExpected workflow:");
        Console.WriteLine("1. Run GA training on in-sample data");
        Console.WriteLine("2. Get trade returns from GridSimulator.GetGridSessionReturns()");
        Console.WriteLine("3. Get trade returns from SwingSimulator.GetSwingReturns()");
        Console.WriteLine("4. Call PerformanceEvaluator.RunComparison()");
    }
}