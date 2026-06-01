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

    // Exposure-capped portfolio sim using actual trade timestamps.
    // Tracks concurrent open positions: at each new trade entry, sums the allocated
    // fractions of all still-open positions and only uses remaining headroom under
    // maxTotalExposurePct. Half-Kelly per coin (coinConf) drives sizing; the cap
    // prevents simultaneous over-deployment across many coins.
    // HoldDuration per trade = strategy's MaxHoldCandles (1h bars for swing, 1h for grid).
    // Exposure-capped portfolio sim with hard real-time concurrent position tracking.
    // At each trade entry: sum currently open positions (in EUR). If adding the new
    // position at its half-Kelly size would breach maxTotalExposurePct × balance,
    // scale it down to fill the remaining headroom. Positions that have held longer
    // than their HoldDuration are considered closed.
    // This enforces a strict "max X% of capital deployed at any one time" rule.
    // drawdownBrakeAt: drawdown fraction at which position scale hits its 20% floor.
    // e.g. 0.30 → at 0% DD scale=1.0, at 15% DD scale=0.5, at 30%+ DD scale=0.20 (floor).
    // This models the live trading behaviour of reducing size during losing streaks,
    // protecting remaining capital and making the path back to peak easier.
    public static PortfolioResult SimulatePortfolioExposureCapped(
        List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration)> trades,
        double maxTotalExposurePct = 0.20,
        double startBalance        = 100.0,
        double drawdownBrakeAt     = 0.30)
    {
        if (trades.Count == 0) return new PortfolioResult(startBalance, startBalance, 0, startBalance, 0, 0, 0, 0, -1);

        var sorted = trades.OrderBy(t => t.EntryTime).ToList();

        double balance      = startBalance, peak = startBalance, maxDd = 0, totalPosSizeEur = 0;
        int tradesToTenPct  = -1;

        // Track open positions: (estimated close time, EUR allocated)
        var openPos = new List<(DateTime Close, double EurAllocated)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var (entryTime, ret, conf, hold) = sorted[i];

            // Remove positions that have closed by the time this trade opens
            openPos.RemoveAll(p => p.Close <= entryTime);

            double currentEurDeployed = openPos.Sum(p => p.EurAllocated);
            double maxEurDeployable   = maxTotalExposurePct * balance;
            double headroomEur        = Math.Max(0, maxEurDeployable - currentEurDeployed);

            // Drawdown brake: scale positions down when in drawdown to protect remaining capital.
            // Linear from 1.0 at no-DD to 0.20 at drawdownBrakeAt, floored at 0.20.
            double currentDd = peak > balance ? (peak - balance) / peak : 0.0;
            double ddScale   = Math.Max(0.20, 1.0 - currentDd / drawdownBrakeAt);

            // Desired position in EUR at half-Kelly, drawdown-scaled
            double desiredEur = conf * balance * ddScale;
            double posEur     = Math.Min(desiredEur, headroomEur);

            openPos.Add((entryTime + hold, posEur));

            totalPosSizeEur += posEur;
            balance += ret / 100.0 * posEur;

            if (tradesToTenPct < 0 && balance >= startBalance * 1.10)
                tradesToTenPct = i + 1;

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
            AvgPositionEur: sorted.Count > 0 ? totalPosSizeEur / sorted.Count : 0,
            TradesCount:    sorted.Count,
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
