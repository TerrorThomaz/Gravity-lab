namespace TradingGA;

// ── THE trade-cost model ─────────────────────────────────────────────────────────────
// Every cost a trade pays is priced here and nowhere else, at TRADE level, inside the
// simulators. That placement is the whole point, so read this before moving anything.
//
// WHY TRADE LEVEL AND NOT THE PORTFOLIO LAYER
//   Two consumers price the same trade:
//     · GA fitness      — scores the RAW per-trade returns the simulators emit. It never
//                         runs a portfolio simulation, so anything charged only in
//                         SimulatePortfolioExposure* is INVISIBLE to selection.
//     · backtest / live — feeds those same returns through SimulatePortfolioExposure*.
//   The simulator return is the ONLY object both consumers share. So the single place a
//   cost can live and still be seen by both is the trade return itself. "The portfolio
//   layer is authoritative" cannot be implemented as "only the portfolio layer charges",
//   or the GA goes right back to optimising a cheaper world than the one being reported.
//
//   Historically slippage was charged in BOTH places and the two disagreed: each simulator
//   applied its own ATR-proportional term (and three of them applied it once where the
//   comment claimed "each side", while two applied it twice), then
//   SimulatePortfolioExposure* subtracted a further flat Config.SlippageBps. GA selection
//   saw ~0.185pp of round-trip cost on a 3% ATR trade while the report showed ~0.285pp — a
//   54% gap between the objective being optimised and the number being published. Every
//   genotype in genotypes/ was selected under the cheaper number.
//
// THE RULE NOW
//   1. Slippage is charged EXACTLY ONCE per trade, here, on BOTH sides of the round trip.
//   2. Config.SlippageBps is its only magnitude authority. Every slippage figure in this
//      repo is that constant times a dimensionless shape; there is no second knob.
//   3. SimulatePortfolioExposure* charges NO slippage and takes no slippage parameter — it
//      was removed outright, so a call site that still tries to pass one fails to compile.
//   4. Exchange fees are separate, unchanged, and also charged exactly once — the
//      portfolio layer has never had a fee term.
public static class TradeCosts
{
    // Exchange taker fee for a full round trip: 2 × 0.055% Bybit taker.
    // NOT slippage, and not double-counted: this is the only fee term in the codebase.
    public const double FeeRoundTripPct = 0.11;

    // The ATR (as a % of price) at which Config.SlippageBps is quoted. A liquid perp's h4
    // ATR sits near 3% of price; that is the coin Config.SlippageBps was calibrated on.
    // This is a CALIBRATION ANCHOR, not a second magnitude knob — it fixes *where on the
    // volatility axis* the authority constant is measured, so changing Config.SlippageBps
    // still scales every slippage charge in the repo by the same factor.
    public const double ReferenceAtrPct = 3.0;

    // Slippage for ONE side of the trade, in percentage points.
    // Config.SlippageBps is a ROUND-TRIP figure, so a side costs half of it — which is what
    // finally makes the old "0.075% each side" comment true; it never was.
    // Scales linearly with the coin's own ATR: a meme perp at 8% ATR pays 2.7× what a liquid
    // major pays, which is the behaviour the per-simulator ATR terms were reaching for before
    // they drifted out of sync with each other and with Config.SlippageBps.
    public static double SlippagePerSidePct(double atrPct)
        => Config.SlippageBps / 100.0 / 2.0 * (atrPct / ReferenceAtrPct);

    // Slippage for the whole round trip = entry side + exit side. At ReferenceAtrPct this is
    // exactly Config.SlippageBps / 100 percentage points, i.e. bit-for-bit the charge the
    // portfolio layer used to apply — the migration moves the charge, it does not invent one.
    public static double SlippageRoundTripPct(double atrPct)
        => 2.0 * SlippagePerSidePct(atrPct);

    // Extra fill degradation when the exit is a STOP the market gapped through.
    // This is a different event from spread/impact slippage — the stop level simply is not
    // available — and it is zero on every non-stop exit, so it is not a second slippage term
    // competing with Config.SlippageBps. Its size scales with the coin's volatility, and
    // `stopGapAtrK` is the per-strategy shape: swing-family stops sit inside the noise band
    // (0.015–0.030) while a grid stop closes a whole ladder into a broken range (0.18).
    public static double StopGapPct(double stopGapAtrK, double atrPct)
        => stopGapAtrK * atrPct;

    // Total round-trip cost of one trade, in PERCENTAGE POINTS — the same unit as a trade's
    // `ret`, so simulators subtract it directly. Every simulator's TradeCost is a one-line
    // delegation to this; that is what keeps the eight strategies mutually comparable.
    public static double RoundTripPct(double atrPct, bool isStop, double stopGapAtrK)
        => FeeRoundTripPct
         + SlippageRoundTripPct(atrPct)
         + (isStop ? StopGapPct(stopGapAtrK, atrPct) : 0.0);

    // Convenience for the simulators, which hold ATR in price units and the entry price.
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

    // ── Gross-exposure guard — the reason this repo needs no liquidation model ───────────
    // There is NO liquidation model anywhere in the simulator. That is sound only while gross
    // notional stays a fraction of equity: at Config.MaxTotalExposurePct = 0.30 the book runs
    // at 0.30x gross leverage, so a maintenance-margin breach would need roughly a 40x larger
    // position before it could bind ahead of any modelled stop. Every exit in this codebase is
    // therefore a stop, a target, a trail or a timeout — never a forced liquidation.
    //
    // Above 1.0x that stops being true, and the failure is SILENT: the backtest keeps printing
    // clean stop-outs on paths where a real account would have been liquidated first, so raising
    // the cap to chase returns manufactures exactly the returns it is chasing. Refuse instead.
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

    // NOTE: these portfolio entry points take NO slippage parameter. Slippage is charged
    // exactly once, at trade level, by TradeCosts — see the header of this file for why it has
    // to live there. The parameter was removed rather than deprecated-and-ignored so that any
    // call site still trying to pass one fails to COMPILE instead of silently handing a number
    // to something that discards it.

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
        double maxPositionFrac     = 0.15)  // hard cap per position (e.g. 0.05 = 5% max each)
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
            double maxEurDeployable   = maxTotalExposurePct * balance;
            double headroomEur        = Math.Max(0, maxEurDeployable - currentEurDeployed);

            double currentDd = peak > balance ? (peak - balance) / peak : 0.0;
            double ddScale   = Math.Max(0.20, 1.0 - currentDd / drawdownBrakeAt);

            double desiredFrac = Math.Min(conf * kellyMultiplier, maxPositionFrac);
            double desiredEur  = desiredFrac * balance * ddScale;
            double posEur      = Math.Min(desiredEur, headroomEur);

            openPos.Add((entryTime + hold, posEur));

            totalPosSizeEur += posEur;
            // No slippage term: `ret` already carries it (TradeCosts, charged once per trade).
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
        GuardExposureCap(maxTotalExposurePct);

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

    // Strategy labels this portfolio layer treats specially.
    //
    // Hoisted out of the inline `strategy is "x" or "y"` expressions they used to live in. A
    // mistyped literal inside such an expression is INVISIBLE: it matches nothing, the branch
    // silently never fires, and no test or report distinguishes that from the strategy simply not
    // qualifying. One really was mistyped — this tested "fade_long" while every backtest labels
    // those trades "fadelong", so FadeLong escaped profit protection entirely.
    //
    // As named sets they can be asserted against PortfolioReplay's direction sets, which is the
    // authoritative answer to "which labels exist and which way do they point".
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
        double profitProtectFactor      = 1.0)   // size multiplier in protection mode; 1.0 = no reduction
    {
        GuardExposureCap(maxTotalExposurePct);

        if (trades.Count == 0) return new PortfolioResult(startBalance, startBalance, 0, startBalance, 0, 0, 0, 0, -1);

        var sorted = trades.OrderBy(t => t.EntryTime).ToList();
        double balance = startBalance, peak = startBalance, maxDd = 0, totalPosSizeEur = 0;
        int tradesToTenPct = -1;
        // Does the gross-exposure cap actually BIND? A cap that never fires is decoration, and the
        // "liquidation cannot bind at 0.30x" argument would then rest on something inert.
        int capBoundCount = 0; double peakGrossFrac = 0;
        // Sizes as ACTUALLY allocated (post-cap, post-ddScale), so the mark-to-market pass below
        // reflects the exposure cap under test rather than a nominal size.
        var mtmInput = new List<(DateTime, double, TimeSpan, double)>();
        var openPos = new List<(DateTime Close, double EurAllocated)>();

        for (int i = 0; i < sorted.Count; i++)
        {
            var (entryTime, ret, conf, hold, strategy) = sorted[i];

            openPos.RemoveAll(p => p.Close <= entryTime);

            // Portfolio DD entry gate: block new DipLong/SwingLong when portfolio is in drawdown
            bool isLong = DdGatedLongs.Contains(strategy);
            double currentDd = peak > balance ? (peak - balance) / peak : 0.0;
            if (isLong && currentDd > ddLongEntryGatePct) continue;

            // Profit protection: reduce long position size when portfolio has made gains and is giving them back.
            // Applies to all non-short longs (diplong, swing_long, fadelong).
            bool isProtectable = ProtectableLongs.Contains(strategy);
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
            if (posEur < desiredEur - 1e-9) capBoundCount++;
            { double gN = openPos.Sum(q => q.EurAllocated) + posEur;
              if (balance > 1e-9 && gN / balance > peakGrossFrac) peakGrossFrac = gN / balance; }

            openPos.Add((entryTime + hold, posEur));
            mtmInput.Add((entryTime, effectiveRet, hold, posEur));
            totalPosSizeEur += posEur;
            // No slippage term: `ret` already carries it (TradeCosts, charged once per trade).
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
                            + $"· gross peak {mtm.PeakGrossExposurePct:F1}% avg {mtm.AvgGrossExposurePct:F1}%");
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
        double maxPositionFrac     = 0.15)  // forwarded, not defaulted away — see below
    {
        GuardExposureCap(maxTotalExposurePct);

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
        // would be applied twice. maxPositionFrac is forwarded explicitly: omitting it
        // silently substituted this overload's callers with the inner overload's defaults.
        return SimulatePortfolioExposureCapped(
            adapted, maxTotalExposurePct, startBalance, drawdownBrakeAt,
            kellyMultiplier: 1.0, maxPositionFrac: maxPositionFrac);
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
