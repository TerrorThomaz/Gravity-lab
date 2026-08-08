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

    // kellyMultiplier: 1.0 = half-Kelly (default), 2.0 = full Kelly, etc.
    // drawdownBrakeAt: fraction of peak drawdown at which sizing reaches 20% floor.
    //   e.g. 0.15 → full size at 0% DD, half size at 7.5% DD, floor (20%) at 15%+ DD.
    //   Default 1.0 = effectively no brake (brake floor only kicks in at 100% DD = ruin).
    public static PortfolioResult SimulatePortfolio(
        List<(double Return, double CoinConf)> valTrades,
        double startBalance    = 100.0,
        double maxPositionPct  = 0.05,
        double kellyMultiplier = 1.0,
        double drawdownBrakeAt = 1.0)
    {
        double balance         = startBalance;
        double peak            = startBalance;
        double maxDd           = 0;
        double totalPosSizeEur = 0;
        int    tradesToTenPct  = -1;

        for (int t = 0; t < valTrades.Count; t++)
        {
            double currentDd = peak > balance ? (peak - balance) / peak : 0.0;
            double ddScale   = Math.Max(0.20, 1.0 - currentDd / drawdownBrakeAt);

            double posFrac  = Math.Min(valTrades[t].CoinConf * kellyMultiplier, maxPositionPct);
            double posEur   = posFrac * balance * ddScale;
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
    // kellyMultiplier: 1.0 = half-Kelly as stored in CoinConf, 2.0 = full Kelly.
    // drawdownBrakeAt: DD fraction where sizing hits 20% floor (0.15 = brake at 15% DD).
    // Concurrent position tracking: positions that overlapped in time each consume their
    // share of maxTotalExposurePct; new entries get whatever headroom remains.
    public static PortfolioResult SimulatePortfolioExposureCapped(
        List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration)> trades,
        double maxTotalExposurePct = 0.30,
        double startBalance        = 100.0,
        double drawdownBrakeAt     = 0.15,
        double kellyMultiplier     = 1.0,
        double maxPositionFrac     = 0.15,  // hard cap per position (e.g. 0.05 = 5% max each)
        double slippageBps         = 0.0)   // per-trade slippage in basis points (e.g. 5 = 0.05%)
    {
        if (trades.Count == 0) return new PortfolioResult(startBalance, startBalance, 0, startBalance, 0, 0, 0, 0, -1);

        var sorted = trades.OrderBy(t => t.EntryTime).ToList();

        double balance      = startBalance, peak = startBalance, maxDd = 0, totalPosSizeEur = 0;
        int tradesToTenPct  = -1;
        double slipFrac     = slippageBps / 10000.0;

        var openPos = new List<(DateTime Close, double EurAllocated)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var (entryTime, ret, conf, hold) = sorted[i];

            openPos.RemoveAll(p => p.Close <= entryTime);

            double currentEurDeployed = openPos.Sum(p => p.EurAllocated);
            double maxEurDeployable   = maxTotalExposurePct * balance;
            double headroomEur        = Math.Max(0, maxEurDeployable - currentEurDeployed);

            double currentDd = peak > balance ? (peak - balance) / peak : 0.0;
            double ddScale   = Math.Max(0.20, 1.0 - currentDd / drawdownBrakeAt);

            double desiredFrac = Math.Min(conf * kellyMultiplier, maxPositionFrac);
            double desiredEur  = desiredFrac * balance * ddScale;
            double posEur      = Math.Min(desiredEur, headroomEur);

            openPos.Add((entryTime + hold, posEur));

            totalPosSizeEur += posEur;
            balance += ret / 100.0 * posEur - slipFrac * posEur;

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

    public record DDEvent(DateTime PeakTime, double PeakBalance, DateTime TroughTime, double TroughBalance)
    {
        public double DropPct => (PeakBalance - TroughBalance) / PeakBalance * 100.0;
    }

    // Like SimulatePortfolioExposureCapped but also returns a per-trade equity curve and
    // the worst-DD event (peak/trough timestamps + balances) for post-hoc investigation.
    public static (PortfolioResult Result, DDEvent? WorstDD, List<(DateTime Time, double Balance)> Curve)
        SimulateExposureCappedWithCurve(
            List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration)> trades,
            double maxTotalExposurePct = 0.30,
            double startBalance        = 100.0,
            double drawdownBrakeAt     = 0.15,
            double kellyMultiplier     = 1.0,
            double maxPositionFrac     = 0.15)
    {
        if (trades.Count == 0)
            return (new PortfolioResult(startBalance, startBalance, 0, startBalance, 0, 0, 0, 0, -1), null, []);

        var sorted = trades.OrderBy(t => t.EntryTime).ToList();
        double balance = startBalance, peak = startBalance, maxDd = 0, totalPosSizeEur = 0;
        int tradesToTenPct = -1;
        var openPos = new List<(DateTime Close, double EurAllocated)>();
        var curve   = new List<(DateTime, double)>(sorted.Count);

        // Running peak / worst-DD tracking
        DateTime curPeakTime = sorted[0].EntryTime;
        double   curPeakBal  = startBalance;
        DateTime peakTime    = sorted[0].EntryTime;
        DateTime troughTime  = sorted[0].EntryTime;
        double   peakBal     = startBalance;
        double   troughBal   = startBalance;

        for (int i = 0; i < sorted.Count; i++)
        {
            var (entryTime, ret, conf, hold) = sorted[i];

            openPos.RemoveAll(p => p.Close <= entryTime);
            double currentEurDeployed = openPos.Sum(p => p.EurAllocated);
            double maxEurDeployable   = maxTotalExposurePct * balance;
            double headroomEur        = Math.Max(0, maxEurDeployable - currentEurDeployed);

            double currentDd = peak > balance ? (peak - balance) / peak : 0.0;
            double ddScale   = Math.Max(0.20, 1.0 - currentDd / drawdownBrakeAt);

            double desiredFrac = Math.Min(conf * kellyMultiplier, maxPositionFrac);
            double desiredEur  = desiredFrac * balance * ddScale;
            double posEur      = Math.Min(desiredEur, headroomEur);

            openPos.Add((entryTime + hold, posEur));
            totalPosSizeEur += posEur;
            balance += ret / 100.0 * posEur;
            curve.Add((entryTime, balance));

            if (tradesToTenPct < 0 && balance >= startBalance * 1.10)
                tradesToTenPct = i + 1;

            if (balance > peak)
            {
                peak        = balance;
                curPeakTime = entryTime;
                curPeakBal  = balance;
            }

            double dd = peak > 0 ? (peak - balance) / peak * 100.0 : 0;
            if (dd > maxDd)
            {
                maxDd      = dd;
                peakTime   = curPeakTime;
                peakBal    = curPeakBal;
                troughTime = entryTime;
                troughBal  = balance;
            }
        }

        var result = new PortfolioResult(
            StartBalance:   startBalance,
            EndBalance:     balance,
            RealizedProfit: 0,
            TotalValue:     balance,
            MaxDrawdownPct: maxDd,
            Confidence:     0,
            AvgPositionEur: sorted.Count > 0 ? totalPosSizeEur / sorted.Count : 0,
            TradesCount:    sorted.Count,
            TradesToTenPct: tradesToTenPct);

        DDEvent? ddEvent = maxDd > 0
            ? new DDEvent(peakTime, peakBal, troughTime, troughBal)
            : null;

        return (result, ddEvent, curve);
    }

    // Strategy-aware overload: includes per-trade strategy name so the simulator can apply
    // a portfolio-DD-based entry gate for long strategies (DipLong / SwingLong).
    // ddLongEntryGatePct: skip long entries when portfolio peak-to-trough DD exceeds this value.
    //   UNITS: a FRACTION in [0, 1], matching currentDd = (peak - balance) / peak below.
    //   NOT a percentage — 8 would mean "800% drawdown" and the gate could never fire.
    //   1.0 = gate disabled (default).  0.08 = block new longs when down 8%+ from peak.
    //   Fed by DynamicGuardGenotype.DdEntryGatePct, bounded {0.02, 0.15}; see
    //   DynamicGuardGenotype.ClampDdEntryGate for the legacy percent-scale coercion.
    // confLossCapMin/Max: confidence-scaled P&L cap for long trades.
    //   effectiveCap = min + (max - min) * conf  →  loss floored at -cap.
    //   Low confidence → tight cap; high confidence → wider cap.  1.0 defaults = disabled.
    public static PortfolioResult SimulatePortfolioExposureCapped(
        List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration, string Strategy)> trades,
        double maxTotalExposurePct      = 0.30,
        double startBalance             = 100.0,
        double drawdownBrakeAt          = 0.15,
        double kellyMultiplier          = 1.0,
        double maxPositionFrac          = 0.15,
        double ddLongEntryGatePct       = 1.0,
        double confLossCapMin           = 1.0,
        double confLossCapMax           = 1.0,
        double profitProtectThreshold   = 1.0,   // portfolio gain fraction that arms protection; 1.0 = disabled
        double profitProtectDrawback    = 0.10,  // drawback from peak that triggers protection
        double profitProtectFactor      = 1.0,   // size multiplier in protection mode; 1.0 = no reduction
        double slippageBps              = 0.0)   // per-trade slippage in basis points (e.g. 5 = 0.05%)
    {
        if (trades.Count == 0) return new PortfolioResult(startBalance, startBalance, 0, startBalance, 0, 0, 0, 0, -1);

        var sorted = trades.OrderBy(t => t.EntryTime).ToList();
        double balance = startBalance, peak = startBalance, maxDd = 0, totalPosSizeEur = 0;
        int tradesToTenPct = -1;
        var openPos = new List<(DateTime Close, double EurAllocated)>();
        double slipFrac = slippageBps / 10000.0;

        for (int i = 0; i < sorted.Count; i++)
        {
            var (entryTime, ret, conf, hold, strategy) = sorted[i];

            openPos.RemoveAll(p => p.Close <= entryTime);

            // Portfolio DD entry gate: block new DipLong/SwingLong when portfolio is in drawdown
            bool isLong = strategy is "diplong" or "swing_long";
            double currentDd = peak > balance ? (peak - balance) / peak : 0.0;
            if (isLong && currentDd > ddLongEntryGatePct) continue;

            // Profit protection: reduce long position size when portfolio has made gains and is giving them back.
            // Applies to all non-short longs (diplong, swing_long, fade_long).
            bool isProtectable = strategy is "diplong" or "swing_long" or "fade_long";
            double portGain = (balance - startBalance) / startBalance;
            bool inProtectMode = isProtectable
                && profitProtectThreshold < 1.0
                && portGain >= profitProtectThreshold
                && currentDd >= profitProtectDrawback;

            // Confidence-scaled P&L cap: limits how much a long trade can drag the portfolio.
            double effectiveRet = ret;
            if (isLong && confLossCapMin < 1.0)
            {
                double cap = confLossCapMin + (confLossCapMax - confLossCapMin) * Math.Clamp(conf, 0.0, 1.0);
                effectiveRet = Math.Max(ret, -cap * 100.0);
            }

            double maxEurDeployable = maxTotalExposurePct * balance;
            double headroomEur      = Math.Max(0, maxEurDeployable - openPos.Sum(p => p.EurAllocated));
            double ddScale          = Math.Max(0.20, 1.0 - currentDd / drawdownBrakeAt);

            double desiredFrac = Math.Min(conf * kellyMultiplier, maxPositionFrac);
            double desiredEur  = desiredFrac * balance * ddScale;
            if (inProtectMode) desiredEur *= profitProtectFactor;
            double posEur      = Math.Min(desiredEur, headroomEur);

            openPos.Add((entryTime + hold, posEur));
            totalPosSizeEur += posEur;
            balance += effectiveRet / 100.0 * posEur - slipFrac * posEur;

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
            TradesToTenPct: tradesToTenPct);
    }

    // Risk-capped overload: RiskCapFrac per trade limits effective position size so that
    // a stop-out at the coin's historical worst-case loss never exceeds the risk budget.
    // e.g. worstLoss=10%, riskBudget=1% → RiskCapFrac=0.10; at fullKelly conf=6% this
    // caps to 10%, so it doesn't bind here, but at worstLoss=20% cap=5% beats fullKelly.
    public static PortfolioResult SimulatePortfolioExposureCapped(
        List<(DateTime EntryTime, double Return, double CoinConf, double RiskCapFrac, TimeSpan HoldDuration)> trades,
        double maxTotalExposurePct = 0.30,
        double startBalance        = 100.0,
        double drawdownBrakeAt     = 0.15,
        double kellyMultiplier     = 1.0,
        double maxPositionFrac     = 0.15,  // forwarded, not defaulted away — see below
        double slippageBps         = 0.0)   // per-trade slippage in basis points (e.g. 5 = 0.05%)
    {
        // Fold riskCap into conf upfront: effectiveFrac = min(conf × km, riskCap)
        var adapted = trades
            .Select(t =>
            {
                double eff = Math.Min(t.CoinConf * kellyMultiplier,
                                      t.RiskCapFrac > 0 ? t.RiskCapFrac : t.CoinConf * kellyMultiplier);
                return (t.EntryTime, t.Return, eff, t.HoldDuration);
            })
            .ToList();
        // kellyMultiplier is already folded into `eff` above, so it must be 1.0 here or it
        // would be applied twice. maxPositionFrac and slippageBps are forwarded explicitly:
        // omitting them silently substituted this overload's callers with the inner
        // overload's defaults, dropping slippage entirely.
        return SimulatePortfolioExposureCapped(
            adapted, maxTotalExposurePct, startBalance, drawdownBrakeAt,
            kellyMultiplier: 1.0, maxPositionFrac: maxPositionFrac, slippageBps: slippageBps);
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
        if (negReturns.Count == 0) return mean > 0 ? 99.99 : 0;
        double downStd = Math.Sqrt(negReturns.Select(r => r * r).Average());
        return downStd < 1e-10 ? 0 : Math.Min(mean / downStd * Math.Sqrt(candleCount / 288.0), 999.99);
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
        // Build a synthetic equity curve at 1% fixed position so DD is in % of equity,
        // not in absolute return-sum units (original bug: peak-trough was not normalised).
        const double pos = 0.01;
        double equity = 1.0, peak = 1.0, maxDdPct = 0;
        foreach (var r in returns)
        {
            equity += r / 100.0 * pos;
            if (equity > peak) peak = equity;
            double dd = (peak - equity) / peak * 100.0;   // percentage, not absolute
            if (dd > maxDdPct) maxDdPct = dd;
        }
        // Rescale back to raw return space for the numerator so units are comparable across callers.
        double totalRetPct = (equity - 1.0) / pos;
        if (maxDdPct < 1e-10) return totalRetPct > 0 ? 99.99 : 0;
        // Annualise by assumed 252 trade-equivalents per year (consistent cross-strategy proxy).
        return totalRetPct * (252.0 / returns.Count) / maxDdPct;
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
