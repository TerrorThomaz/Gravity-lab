namespace TradingGA;

public static class Simulator
{
    public const double FeeRoundTrip = 0.21;

    public record PortfolioResult(
        double StartBalance,
        double EndBalance,
        double RealizedProfit,
        double TotalValue,
        double MaxDrawdownPct,
        double Confidence,
        double AvgPositionEur,
        int    TradesCount,
        int    TradesToTenPct
    );

    // Confidence via half-Kelly: derived from training statistics, applied to val trades.
    public static double ComputeConfidence(List<double> returns)
    {
        if (returns.Count < 5) return 0;
        var wins   = returns.Where(r => r > 0).ToList();
        var losses = returns.Where(r => r <= 0).ToList();
        if (wins.Count == 0) return 0;
        double p       = (double)wins.Count / returns.Count;
        double avgWin  = wins.Average();
        double avgLoss = losses.Count > 0 ? Math.Abs(losses.Average()) : avgWin;
        double b       = avgWin / avgLoss;
        double kelly   = (p * b - (1 - p)) / b;
        return Math.Clamp(kelly / 2.0, 0.0, 1.0);
    }

    public static PortfolioResult SimulatePortfolio(
        List<(double Return, double CoinConf)> valTrades,
        double startBalance   = 100.0,
        double maxPositionPct = 0.05)
    {
        double balance         = startBalance;
        double peak            = startBalance;
        double maxDd           = 0;
        double totalPosSizeEur = 0;
        int    tradesToTenPct  = -1;

        for (int t = 0; t < valTrades.Count; t++)
        {
            double posFrac = Math.Min(valTrades[t].CoinConf, maxPositionPct);
            double posEur  = posFrac * balance;
            totalPosSizeEur += posEur;
            balance += valTrades[t].Return / 100.0 * posEur;

            if (tradesToTenPct < 0 && balance >= startBalance * 1.10)
                tradesToTenPct = t + 1;

            if (balance > peak) peak = balance;
            double dd = peak > 0 ? (peak - balance) / peak * 100.0 : 0;
            if (dd > maxDd) maxDd = dd;
        }

        return new PortfolioResult(
            StartBalance:   startBalance,
            EndBalance:     balance,
            RealizedProfit: 0,
            TotalValue:     balance,
            MaxDrawdownPct: maxDd,
            Confidence:     0,
            AvgPositionEur: valTrades.Count > 0 ? totalPosSizeEur / valTrades.Count : 0,
            TradesCount:    valTrades.Count,
            TradesToTenPct: tradesToTenPct
        );
    }

    // Time-normalised Sharpe. candleCount = number of 5m-equivalent candles in the window;
    // 288 candles = 1 trading day. Returns 0 if < 5 trades or PF < 1.3.
    public static double SharpeRatio(List<double> returns, int candleCount)
    {
        if (returns.Count < 5) return 0;
        double grossProfit = returns.Where(r => r > 0).Sum();
        double grossLoss   = Math.Abs(returns.Where(r => r <= 0).Sum());
        if (grossLoss < 1e-10 || grossProfit / grossLoss < 1.3) return 0;
        double mean = returns.Average();
        double std  = Math.Sqrt(returns.Select(r => Math.Pow(r - mean, 2)).Average());
        return std < 1e-10 ? 0 : mean / std * Math.Sqrt(candleCount / 288.0);
    }

    public static double SortinoRatio(List<double> returns, int candleCount)
    {
        if (returns.Count < 5) return 0;
        double mean       = returns.Average();
        var    negReturns = returns.Where(r => r < 0).ToList();
        if (negReturns.Count == 0) return mean > 0 ? double.MaxValue : 0;
        double downStd = Math.Sqrt(negReturns.Select(r => r * r).Average());
        return downStd < 1e-10 ? 0 : mean / downStd * Math.Sqrt(candleCount / 288.0);
    }

    public static double ProfitFactor(List<double> returns)
    {
        double gross = returns.Where(r => r > 0).Sum();
        double loss  = Math.Abs(returns.Where(r => r <= 0).Sum());
        return loss < 1e-10 ? (gross > 0 ? 99.99 : 0) : Math.Min(gross / loss, 99.99);
    }

    public static double CalmarRatio(List<double> returns)
    {
        if (returns.Count < 5) return 0;
        double cumulative = 0, peak = 0, maxDd = 0;
        foreach (var r in returns)
        {
            cumulative += r;
            if (cumulative > peak) peak = cumulative;
            double dd = peak - cumulative;
            if (dd > maxDd) maxDd = dd;
        }
        if (maxDd < 1e-10) return cumulative > 0 ? double.MaxValue : 0;
        return cumulative * (252.0 / returns.Count) / maxDd;
    }

    public static int MaxConsecLosses(List<double> returns)
    {
        int max = 0, cur = 0;
        foreach (var r in returns)
        {
            if (r <= 0) { cur++; if (cur > max) max = cur; }
            else cur = 0;
        }
        return max;
    }
}
