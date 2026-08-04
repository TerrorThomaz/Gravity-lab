namespace TradingGA;

public static class FoldScoreHelper
{
    public static double Canonical(
        List<double> returns,
        double posFrac,
        int minTradesPerFold,
        FitnessConfig cfg,
        double volWeight = 1.0,
        double statBonusCeiling = 1.0)
    {
        if (returns.Count < minTradesPerFold) return -1.0;

        int wins = 0;
        double grossWins = 0, grossLoss = 0;
        foreach (var r in returns)
        {
            if (r > 0) { wins++; grossWins += r; }
            else         grossLoss -= r;
        }

        double wr = (double)wins / returns.Count;
        double pf = grossLoss > 1e-10 ? grossWins / grossLoss : (grossWins > 0 ? 5.0 : 0.0);

        if (pf < 1.0) return pf - 2.0;

        double balance = 1.0, peak = 1.0, maxDd = 0.0;
        foreach (var r in returns)
        {
            balance += r / 100.0 * posFrac;
            if (balance > peak) peak = balance;
            double dd = (peak - balance) / peak;
            if (dd > maxDd) maxDd = dd;
        }

        double gain = balance - 1.0;
        if (gain <= 0) return gain * 100 - 0.5;

        double wrMult = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 3.0;

        double pfMult = pf < 1.5 ? (pf - 1.0) / 0.5 : 1.0 + (pf - 1.5) * 0.5;
        int losses = returns.Count - wins;
        double avgWin  = wins   > 0 ? grossWins / wins   : 0;
        double avgLoss = losses > 0 ? grossLoss / losses : avgWin;
        double rr      = avgLoss > 1e-10 ? avgWin / avgLoss : (avgWin > 0 ? 5.0 : 1.0);
        double rrMult  = rr < 2.5 ? (rr - 1.0) / 1.5 : 1.0 + (rr - 2.5) * 0.3;
        double qualityMult = Math.Min(Math.Sqrt(pfMult * rrMult), 2.5);

        double ddDiv     = 1.0 + maxDd * 10.0;
        double freqBonus = 1.0 + 0.15 * Math.Log(Math.Max(1.0, returns.Count / (double)minTradesPerFold));

        double peakGain      = peak - 1.0;
        double retentionMult = peakGain > 0.01
            ? Math.Max(0.2, (balance - 1.0) / peakGain)
            : 1.0;

        int    n       = returns.Count;
        double sharpe  = Simulator.SharpeRatio(returns, n);
        double calmar  = Simulator.CalmarRatio(returns);
        double pfStat  = Simulator.ProfitFactor(returns);
        double sortino = Simulator.SortinoRatio(returns, n);
        double base_   = gain * 100.0 * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
        double score   = base_
            * (1.0 + cfg.SharpeW  * Math.Max(0, Math.Min(sharpe  / 3.0,  statBonusCeiling)))
            * (1.0 + cfg.CalmarW  * Math.Max(0, Math.Min(calmar  / 2.0,  statBonusCeiling)))
            * (1.0 + cfg.PfW      * Math.Max(0, Math.Min(pfStat - 1.0,   statBonusCeiling)))
            * (1.0 + cfg.SortinoW * Math.Max(0, Math.Min(sortino / 4.0,  statBonusCeiling)))
            * volWeight;

        score *= CVaRPenalty(returns, cfg);
        score *= TailRatioBonus(returns, cfg);

        return score;
    }

    public static double CVaRPenalty(List<double> returns, FitnessConfig cfg)
    {
        if (cfg.CVaRW <= 0 || returns.Count < 20) return 1.0;
        double cvar5 = StatisticalTests.CVaR(returns, 0.05);
        return 1.0 - Math.Max(0, cvar5 + 3.0) / 3.0 * cfg.CVaRW;
    }

    public static double TailRatioBonus(List<double> returns, FitnessConfig cfg)
    {
        if (cfg.TailRatioW <= 0 || returns.Count < 20) return 1.0;
        var sorted = returns.OrderBy(r => r).ToList();
        int n = sorted.Count;
        int tailN = Math.Max(1, n / 20);
        double p5  = sorted[tailN - 1];
        double p95 = sorted[n - tailN];
        double absP5  = Math.Abs(p5);
        double absP95 = Math.Abs(p95);
        if (absP5 < 1e-10) return 1.0;
        double ratio = absP95 / absP5;
        double bonus = ratio > 1.5 ? 1.0 + (ratio - 1.5) * 0.1 * cfg.TailRatioW : 1.0;
        return bonus;
    }

    public static double RegimeDiversityBonus(
        Dictionary<MarketRegime, List<double>> regimeReturns,
        FitnessConfig cfg)
    {
        if (cfg.RegimeDiversityW <= 0) return 1.0;
        int activeRegimes = regimeReturns.Count(r => r.Value.Count >= 10);
        if (activeRegimes <= 1) return 1.0;
        int profitableRegimes = regimeReturns
            .Where(r => r.Value.Count >= 10 && r.Value.Average() > 0)
            .Count();
        double diversity = (double)profitableRegimes / Math.Max(1, activeRegimes);
        return 1.0 + diversity * cfg.RegimeDiversityW;
    }

    public static (int Start, int End)[] ComputeFoldBoundaries(int totalBars, int folds, double embargoPct = 0.05)
    {
        int foldSize = totalBars / folds;
        int embargoBars = (int)(foldSize * embargoPct);
        var bounds = new (int Start, int End)[folds];
        for (int f = 0; f < folds; f++)
        {
            int start = f * foldSize + (f > 0 ? embargoBars : 0);
            int end = f == folds - 1 ? totalBars : (f + 1) * foldSize;
            bounds[f] = (start, end);
        }
        return bounds;
    }

    public static (int Start, int End)[] ComputeRegimeAwareFoldBoundaries(
        RegimeBar[] btcSeries,
        Func<MarketRegime, bool> isActive,
        int folds,
        double embargoPct = 0.05)
    {
        if (btcSeries.Length < 220 || folds < 2)
            return ComputeFoldBoundaries(btcSeries.Length, folds, embargoPct);

        int totalActive = 0;
        for (int i = 0; i < btcSeries.Length; i++)
            if (isActive(btcSeries[i].Regime)) totalActive++;

        if (totalActive < folds * 40)
            return ComputeFoldBoundaries(btcSeries.Length, folds, embargoPct);

        int targetPerFold = totalActive / folds;
        int embargoBars = Math.Max(1, (int)(targetPerFold * embargoPct));

        var result = new List<(int Start, int End)>();
        int foldStart = 0;
        int activeInFold = 0;
        bool inActiveSegment = false;

        for (int i = 0; i < btcSeries.Length; i++)
        {
            bool active = isActive(btcSeries[i].Regime);
            if (active)
            {
                if (!inActiveSegment) inActiveSegment = true;
                activeInFold++;
            }

            if (activeInFold >= targetPerFold && result.Count < folds - 1)
            {
                result.Add((foldStart, i + 1));
                int embargoEnd = Math.Min(i + 1 + embargoBars, btcSeries.Length);
                foldStart = embargoEnd;
                activeInFold = 0;
                i = embargoEnd - 1;
                inActiveSegment = false;
            }
        }

        if (foldStart < btcSeries.Length)
            result.Add((foldStart, btcSeries.Length));

        while (result.Count < folds)
            result.Add((btcSeries.Length, btcSeries.Length));

        return result.Take(folds).ToArray();
    }

    public static double CanonicalRegime(
        List<(double Return, int RegimeBars)> returns,
        double posFrac,
        int sustainedBars,
        int minTradesPerFold,
        FitnessConfig cfg,
        double volWeight = 1.0,
        double statBonusCeiling = 1.0)
    {
        var valid = returns.Where(r => r.RegimeBars >= sustainedBars).Select(r => r.Return).ToList();
        return Canonical(valid, posFrac, minTradesPerFold, cfg, volWeight, statBonusCeiling);
    }

    public static double CanonicalRegimeStratified(
        List<(double Return, int RegimeBars, MarketRegime Regime)> returns,
        double posFrac,
        int sustainedBars,
        int minTradesPerFold,
        FitnessConfig cfg,
        double volWeight = 1.0,
        double statBonusCeiling = 1.0)
    {
        var valid = returns.Where(r => r.RegimeBars >= sustainedBars).ToList();
        if (valid.Count < minTradesPerFold) return -1.0;

        var plainReturns = valid.Select(r => r.Return).ToList();
        double baseScore = Canonical(plainReturns, posFrac, minTradesPerFold, cfg, volWeight, statBonusCeiling);
        if (baseScore <= -1.0) return baseScore;

        var regimeBuckets = new Dictionary<MarketRegime, List<double>>();
        foreach (var r in valid)
        {
            if (!regimeBuckets.ContainsKey(r.Regime))
                regimeBuckets[r.Regime] = new List<double>();
            regimeBuckets[r.Regime].Add(r.Return);
        }

        baseScore *= RegimeDiversityBonus(regimeBuckets, cfg);
        return baseScore;
    }
}
