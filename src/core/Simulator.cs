namespace TradingGA;

// Trade-cost model: every cost charged at TRADE level (inside simulators), not at portfolio layer.
// GA fitness sees raw per-trade returns and never runs a portfolio sim, so costs charged only in
// SimulatePortfolioExposure* would be invisible to selection. Slippage charged exactly once per trade
// via Config.SlippageBps (sole magnitude authority). Exchange fees separate, also charged once.
public static class TradeCosts
{
    // Exchange fee: 2 × 0.055% Bybit taker — the default for every side not flagged maker.
    public const double FeeRoundTripPct = 0.11;

    // Bybit non-VIP perp maker fee, per side. A resting limit order fills AT its price, so a
    // maker side pays this and no slippage. Fill risk is not modelled: position sizes are small
    // relative to book depth, so a touched limit is treated as filled.
    public const double MakerFeePct = 0.02;

    // What one side saves by resting instead of crossing: the taker/maker fee gap plus its slippage.
    // Subtracted rather than re-summed so the all-taker path stays bit-identical.
    private static double MakerSavingPct(double atrPct)
        => FeeRoundTripPct / 2.0 - MakerFeePct + SlippagePerSidePct(atrPct);

    // Calibration anchor: ATR% at which Config.SlippageBps is quoted (~3% for a liquid perp).
    // Not a second knob — fixes where on the vol axis the authority constant is measured.
    public const double ReferenceAtrPct = 3.0;

    // Slippage per side (pct pts). Config.SlippageBps is round-trip, so one side = half.
    // Scales linearly with coin's ATR%.
    public static double SlippagePerSidePct(double atrPct)
        => Config.SlippageBps / 100.0 / 2.0 * (atrPct / ReferenceAtrPct);

    // Round-trip slippage. At ReferenceAtrPct this equals Config.SlippageBps/100 pct pts.
    public static double SlippageRoundTripPct(double atrPct)
        => 2.0 * SlippagePerSidePct(atrPct);

    // Extra degradation on stop exits (stop level not available). Zero on non-stop exits.
    // stopGapAtrK is per-strategy shape (swing: 0.015–0.030, grid: 0.18).
    public static double StopGapPct(double stopGapAtrK, double atrPct)
        => stopGapAtrK * atrPct;

    // Total round-trip cost (pct pts) — same unit as trade `ret`. Every simulator delegates here.
    // Participation impact: when barNotional>0 and Config.ChargeParticipationImpact is on,
    // adds size-aware term (impact ∝ sqrt(orderNotional/barNotional)). Without it, "deploy more capital" is unpriced.
    // entryMaker/exitMaker: that side was a resting limit order. A stop is never maker, so
    // exitMaker is ignored when isStop.
    public static double RoundTripPct(double atrPct, bool isStop, double stopGapAtrK,
                                      double barNotional = 0.0, double posFrac = 0.0,
                                      bool entryMaker = false, bool exitMaker = false)
    {
        double cost = FeeRoundTripPct
                    + SlippageRoundTripPct(atrPct)
                    + (isStop ? StopGapPct(stopGapAtrK, atrPct) : 0.0);
        if (entryMaker)            cost -= MakerSavingPct(atrPct);
        if (exitMaker && !isStop)  cost -= MakerSavingPct(atrPct);

        if (Config.ChargeParticipationImpact && barNotional > 0 && posFrac > 0)
        {
            double orderNotional = Config.ReferenceEquityUsd * posFrac;
            // Charged on both sides (entry + exit).
            cost += 2.0 * LiquidityModel.ImpactPct(orderNotional, barNotional, atrPct);
        }
        return cost;
    }

    // ATR in price units → pct.
    public static double AtrPct(double atr, double entryPx)
        => entryPx > 1e-12 ? atr / entryPx * 100.0 : 0.0;
}

public static class Simulator
{

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

    // Half-Kelly confidence from training statistics.
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

    // Gross-exposure guard: no liquidation model in this repo. Sound only while gross notional < equity.
    // At 0.30x leverage, margin breach would need ~40x larger position before binding ahead of any stop.
    // Above 1.0x, backtests would silently report clean stop-outs on paths that would be liquidated. Refuse instead.
    private const double MaxSupportableExposurePct = 1.0;

    private static void GuardExposureCap(double maxTotalExposurePct)
    {
        if (maxTotalExposurePct > MaxSupportableExposurePct)
            throw new ArgumentOutOfRangeException(
                nameof(maxTotalExposurePct), maxTotalExposurePct,
                $"maxTotalExposurePct > {MaxSupportableExposurePct:0.##} is not supported: this simulator has no " +
                "liquidation model. Below 1.0x gross leverage that omission is defensible because margin could " +
                "never bind before a modelled stop; above it, every backtest would silently report clean stop-outs " +
                "on paths that would have been liquidated. Add a liquidation model before raising the cap.");
    }

    // NOTE: portfolio entry points take NO slippage parameter. Slippage charged at trade level by TradeCosts.
    // Parameter removed (not deprecated) so stale call sites fail to compile.

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

    // Exposure-capped portfolio sim with concurrent position tracking.
    // kellyMultiplier: 1.0 = half-Kelly, 2.0 = full Kelly. drawdownBrakeAt: DD fraction where sizing hits 20% floor.
    public static PortfolioResult SimulatePortfolioExposureCapped(
        List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration)> trades,
        double maxTotalExposurePct = 0.30,
        double startBalance        = 100.0,
        double drawdownBrakeAt     = 0.15,
        double kellyMultiplier     = 1.0,
        double maxPositionFrac     = 0.15,  // hard cap per position (e.g. 0.05 = 5% max each)
        Func<DateTime, double>? dynamicCap = null)
    {
        GuardExposureCap(maxTotalExposurePct);

        if (trades.Count == 0) return new PortfolioResult(startBalance, startBalance, 0, startBalance, 0, 0, 0, 0, -1);

        var sorted = trades.OrderBy(t => t.EntryTime).ToList();

        double balance      = startBalance, peak = startBalance, maxDd = 0, totalPosSizeEur = 0;
        int tradesToTenPct  = -1;

        var openPos = new List<(DateTime Close, double EurAllocated)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var (entryTime, ret, conf, hold) = sorted[i];

            openPos.RemoveAll(p => p.Close <= entryTime);

            double currentEurDeployed = openPos.Sum(p => p.EurAllocated);
            double maxEurDeployable   = (dynamicCap != null
                                            ? Math.Min(dynamicCap(entryTime), MaxSupportableExposurePct)
                                            : maxTotalExposurePct) * balance;
            double headroomEur        = Math.Max(0, maxEurDeployable - currentEurDeployed);

            double currentDd = peak > balance ? (peak - balance) / peak : 0.0;
            double ddScale   = Math.Max(0.20, 1.0 - currentDd / drawdownBrakeAt);

            double desiredFrac = Math.Min(conf * kellyMultiplier, maxPositionFrac);
            double desiredEur  = desiredFrac * balance * ddScale;
            double posEur      = Math.Min(desiredEur, headroomEur);

            openPos.Add((entryTime + hold, posEur));

            totalPosSizeEur += posEur;
            // ret already carries slippage (TradeCosts).
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

    public record DDEvent(DateTime PeakTime, double PeakBalance, DateTime TroughTime, double TroughBalance)
    {
        public double DropPct => (PeakBalance - TroughBalance) / PeakBalance * 100.0;
    }

    // Like SimulatePortfolioExposureCapped but also returns equity curve and worst-DD event.
    public static (PortfolioResult Result, DDEvent? WorstDD, List<(DateTime Time, double Balance)> Curve)
        SimulateExposureCappedWithCurve(
            List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration)> trades,
            double maxTotalExposurePct = 0.30,
            double startBalance        = 100.0,
            double drawdownBrakeAt     = 0.15,
            double kellyMultiplier     = 1.0,
            double maxPositionFrac     = 0.15,
            Func<DateTime, double>? dynamicCap = null)
    {
        GuardExposureCap(maxTotalExposurePct);

        if (trades.Count == 0)
            return (new PortfolioResult(startBalance, startBalance, 0, startBalance, 0, 0, 0, 0, -1), null, []);

        var sorted = trades.OrderBy(t => t.EntryTime).ToList();
        double balance = startBalance, peak = startBalance, maxDd = 0, totalPosSizeEur = 0;
        int tradesToTenPct = -1;
        var openPos = new List<(DateTime Close, double EurAllocated)>();
        var curve   = new List<(DateTime, double)>(sorted.Count);

        // Peak / worst-DD tracking
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
            double maxEurDeployable   = (dynamicCap != null
                                            ? Math.Min(dynamicCap(entryTime), MaxSupportableExposurePct)
                                            : maxTotalExposurePct) * balance;
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

    // Strategy-aware overload with DD entry gate and confidence-scaled P&L caps for longs.
    // ddLongEntryGatePct: FRACTION [0,1] (NOT pct — 0.08 = block longs when down 8%+). 1.0 = disabled.
    // confLossCapMin/Max: effectiveCap = min + (max-min)*conf; loss floored at -cap. 1.0 defaults = disabled.

    // Strategy labels treated specially. Named sets (not inline string matches) so typos fail to compile.
    public static readonly HashSet<string> DdGatedLongs = new(StringComparer.OrdinalIgnoreCase)
        { "diplong", "swing_long" };
    public static readonly HashSet<string> ProtectableLongs = new(StringComparer.OrdinalIgnoreCase)
        { "diplong", "swing_long", "fadelong" };

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
        // Optional risk-budgeted cap: when supplied it REPLACES maxTotalExposurePct per-instant.
        // The scalar remains the ceiling, so a dynamic cap can only ever be as loose as the
        // statically-guarded bound — it cannot smuggle the book past what GuardExposureCap allows.
        Func<DateTime, double>? dynamicCap = null,
        // Optional per-strategy size weight (CovarianceSizing). Mean-normalised by construction,
        // so this REDISTRIBUTES size between strategies rather than changing gross exposure — the
        // exposure cap still binds the total. Keeping the two separable is what makes either one
        // measurable on its own.
        Func<string, double>? strategyWeight = null,
        double profitProtectFactor      = 1.0)   // size multiplier in protection mode; 1.0 = no reduction
    {
        GuardExposureCap(maxTotalExposurePct);

        if (trades.Count == 0) return new PortfolioResult(startBalance, startBalance, 0, startBalance, 0, 0, 0, 0, -1);

        var sorted = trades.OrderBy(t => t.EntryTime).ToList();
        double balance = startBalance, peak = startBalance, maxDd = 0, totalPosSizeEur = 0;
        int tradesToTenPct = -1;
        // Track whether the exposure cap actually binds.
        int capBoundCount = 0; double peakGrossFrac = 0, peakDesiredFrac = 0;
        var openDesired = new List<(DateTime Close, double Eur)>();
        // Sizes as actually allocated (post-cap, post-ddScale) for mark-to-market.
        var mtmInput = new List<(DateTime, double, TimeSpan, double)>();
        var openPos = new List<(DateTime Close, double EurAllocated)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var (entryTime, ret, conf, hold, strategy) = sorted[i];

            openPos.RemoveAll(p => p.Close <= entryTime);

            // DD entry gate: block new longs when portfolio is in drawdown.
            bool isLong = DdGatedLongs.Contains(strategy);
            double currentDd = peak > balance ? (peak - balance) / peak : 0.0;
            if (isLong && currentDd > ddLongEntryGatePct) continue;

            // Profit protection: reduce long size when portfolio has gains and is giving them back.
            bool isProtectable = ProtectableLongs.Contains(strategy);
            double portGain = (balance - startBalance) / startBalance;
            bool inProtectMode = isProtectable
                && profitProtectThreshold < 1.0
                && portGain >= profitProtectThreshold
                && currentDd >= profitProtectDrawback;

            // Confidence-scaled P&L cap on longs.
            double effectiveRet = ret;
            if (isLong && confLossCapMin < 1.0)
            {
                double cap = confLossCapMin + (confLossCapMax - confLossCapMin) * Math.Clamp(conf, 0.0, 1.0);
                effectiveRet = Math.Max(ret, -cap * 100.0);
            }

            // Dynamic cap REPLACES scalar (not bounded by it); MaxSupportableExposurePct backstops.
            double capNow = dynamicCap != null
                ? Math.Min(dynamicCap(entryTime), MaxSupportableExposurePct)
                : maxTotalExposurePct;
            double maxEurDeployable = capNow * balance;
            double headroomEur      = Math.Max(0, maxEurDeployable - openPos.Sum(p => p.EurAllocated));
            double ddScale          = Math.Max(0.20, 1.0 - currentDd / drawdownBrakeAt);

            double desiredFrac = Math.Min(conf * kellyMultiplier, maxPositionFrac);
            if (strategyWeight != null) desiredFrac = Math.Min(desiredFrac * strategyWeight(strategy), maxPositionFrac);
            double desiredEur  = desiredFrac * balance * ddScale;
            if (inProtectMode) desiredEur *= profitProtectFactor;
            double posEur      = Math.Min(desiredEur, headroomEur);
            if (posEur < desiredEur - 1e-9) capBoundCount++;
            // Desired size without cap (for diagnostics).
            openDesired.RemoveAll(d => d.Close <= entryTime);
            openDesired.Add((entryTime + hold, desiredEur));
            { double dG = openDesired.Sum(d => d.Eur);
              if (balance > 1e-9 && dG / balance > peakDesiredFrac) peakDesiredFrac = dG / balance; }
            { double gN = openPos.Sum(q => q.EurAllocated) + posEur;
              if (balance > 1e-9 && gN / balance > peakGrossFrac) peakGrossFrac = gN / balance; }

            openPos.Add((entryTime + hold, posEur));
            mtmInput.Add((entryTime, effectiveRet, hold, posEur));
            totalPosSizeEur += posEur;
            // ret already carries slippage (TradeCosts).
            balance += effectiveRet / 100.0 * posEur;

            if (tradesToTenPct < 0 && balance >= startBalance * 1.10)
                tradesToTenPct = i + 1;

            if (balance > peak) peak = balance;
            double dd = peak > 0 ? (peak - balance) / peak * 100.0 : 0;
            if (dd > maxDd) maxDd = dd;
        }

        if (Environment.GetEnvironmentVariable("GRAVITY_EXPOSURE") == "1")
        {
            var mtm = MarkToMarket.Compute(mtmInput, startBalance, TimeSpan.FromHours(1));
            Console.WriteLine($"  [EXPOSURE] cap {maxTotalExposurePct:P0} · bound on {capBoundCount}/{sorted.Count} "
                            + $"({(double)capBoundCount / sorted.Count:P1}) · realized DD {maxDd:F2}% "
                            + $"· MARK-TO-MARKET DD {mtm.MaxDrawdownPct:F2}% "
                            + $"· gross peak {mtm.PeakGrossExposurePct:F1}% avg {mtm.AvgGrossExposurePct:F1}%"
                            + $"· UNCAPPED demand would peak {peakDesiredFrac:P0}");
            CorrelatedShock.Print(
                CorrelatedShock.Run(mtmInput, startBalance, new[] { 10.0, 20.0, 30.0, 40.0 }, TimeSpan.FromHours(1)),
                maxTotalExposurePct);
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

    // Risk-capped overload: RiskCapFrac limits position so stop-out at worst-case loss ≤ risk budget.
    public static PortfolioResult SimulatePortfolioExposureCapped(
        List<(DateTime EntryTime, double Return, double CoinConf, double RiskCapFrac, TimeSpan HoldDuration)> trades,
        double maxTotalExposurePct = 0.30,
        double startBalance        = 100.0,
        double drawdownBrakeAt     = 0.15,
        double kellyMultiplier     = 1.0,
        double maxPositionFrac     = 0.15)  // forwarded, not defaulted away — see below
    {
        GuardExposureCap(maxTotalExposurePct);

        // Fold riskCap into conf: effectiveFrac = min(conf × km, riskCap).
        var adapted = trades
            .Select(t =>
            {
                double eff = Math.Min(t.CoinConf * kellyMultiplier,
                                      t.RiskCapFrac > 0 ? t.RiskCapFrac : t.CoinConf * kellyMultiplier);
                return (t.EntryTime, t.Return, eff, t.HoldDuration);
            })
            .ToList();
        // kellyMultiplier already folded into eff (must be 1.0 here to avoid double-application).
        return SimulatePortfolioExposureCapped(
            adapted, maxTotalExposurePct, startBalance, drawdownBrakeAt,
            kellyMultiplier: 1.0, maxPositionFrac: maxPositionFrac);
    }

    // Time-normalised Sharpe. candleCount = 5m-equivalent candles (288 = 1 day). Returns 0 if <5 trades or PF<1.3.
    // NOTE ON SCALE: this is a PER-TRADE ratio scaled by sqrt(candleCount / 288). It is not an
    // annualised Sharpe and must not be read as one — the scale factor depends on how many candles
    // the book covers, so the same edge prints a different number on a longer history. For a
    // portfolio Sharpe, take daily returns off the equity curve (EdgeTest does).
    public static double SharpeRatio(List<double> returns, int candleCount)
    {
        if (returns.Count < 5) return 0;
        double mean = returns.Average();
        double std  = Math.Sqrt(returns.Select(r => Math.Pow(r - mean, 2)).Average());
        return std < 1e-10 ? 0 : mean / std * Math.Sqrt(candleCount / 288.0);
    }

    // SharpeRatio's retired behaviour: 0 below a profit-factor floor of 1.3. Kept explicit for any
    // caller that genuinely wants "profitable enough to quote" — as a floor inside SharpeRatio it
    // meant a losing book and a flat one both printed 0.00 and the report could not tell them apart.
    public const double SharpeScreenMinPf = 1.3;

    public static double ScreenedSharpeRatio(List<double> returns, int candleCount)
    {
        if (returns.Count < 5) return 0;
        double grossProfit = returns.Where(r => r > 0).Sum();
        double grossLoss   = Math.Abs(returns.Where(r => r <= 0).Sum());
        if (grossLoss < 1e-10 || grossProfit / grossLoss < SharpeScreenMinPf) return 0;
        return SharpeRatio(returns, candleCount);
    }

    public static double SortinoRatio(List<double> returns, int candleCount)
    {
        if (returns.Count < 5) return 0;
        double mean       = returns.Average();
        var    negReturns = returns.Where(r => r < 0).ToList();
        if (negReturns.Count == 0) return mean > 0 ? 99.99 : 0;
        double downStd = Math.Sqrt(negReturns.Select(r => r * r).Average());
        // Clamped both ways: a losing book must be able to report a negative Sortino.
        return downStd < 1e-10 ? 0
             : Math.Clamp(mean / downStd * Math.Sqrt(candleCount / 288.0), -999.99, 999.99);
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
        // Synthetic equity curve at 1% fixed position so DD is in % of equity.
        const double pos = 0.01;
        double equity = 1.0, peak = 1.0, maxDdPct = 0;
        foreach (var r in returns)
        {
            equity += r / 100.0 * pos;
            if (equity > peak) peak = equity;
            double dd = (peak - equity) / peak * 100.0;   // percentage, not absolute
            if (dd > maxDdPct) maxDdPct = dd;
        }
        // Rescale to raw return space for numerator.
        double totalRetPct = (equity - 1.0) / pos;
        if (maxDdPct < 1e-10) return totalRetPct > 0 ? 99.99 : 0;
        // Annualise by 252 trade-equivalents/year.
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
