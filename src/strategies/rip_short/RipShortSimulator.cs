namespace TradingGA;

// Rip-short simulator — regime-gated bear trend-continuation strategy.
//
// Direction-mirror of DipLongSimulator: same dual-EMA regime filter and dual-TF
// setup, flipped to the short side. DipLong buys RSI dips in confirmed uptrends;
// RipShort shorts RSI relief rallies ("rips") in confirmed downtrends and rides
// the next leg down.
//
// The bear regime filter (RegimeLongEma must be falling AND close below it) keeps
// the strategy silent during bull markets — the exact periods where DipLong /
// SwingLong operate.
//
// Entry (1h setup + 15m trigger):
//   1. Bear regime  — close < RegimeLongEma AND RegimeLongEma[now] < RegimeLongEma[slopeLookback bars ago]
//   2. Trend gate   — close < EmaPeriod EMA AND ADX(7) ≥ threshold
//   3. Rally setup  — RSI(7) ≥ RsiRallyThreshold (relief bounce within the downtrend)
//   4. Bearish BoS  — 15m close < previous 15m low (sellers committed to continuation)
//
// Exit (15m) — WICK-TRIGGERED stop/target (pessimistic): shorts into bear rallies
//   are squeeze-prone, so intrabar highs/lows trigger the hard levels, not closes.
//   · Hard stop : recentSwingHigh + StopLossAtrMult × h4ATR  (highest high in last 20 h1 bars)
//                 triggered when the 15m HIGH ≥ stop
//   · Fixed TP  : entry − TakeProfitAtrMult × h4ATR, triggered when the 15m LOW ≤ target
//                 (same bar touches both → STOP wins)
//   · Trailing  : armed after TrailingActivationAtrMult × ATR profit; exits at market
//                 when 15m close climbs TrailingStopAtrMult × ATR above the trough
//   · TimeStop  : after TimeStopBars, tolerated adverse (above-entry) move narrows to 0 at MaxHoldCandles
//   · Timeout   : MaxHoldCandles h1 bars
//
// Funding: Bybit settles perp funding every 8h (00:00/08:00/16:00 UTC). A short
//   RECEIVES funding when the rate is positive and PAYS when negative. Computed by
//   FundingRateSession.PnlPct(..., isLong: false) — the single implementation shared
//   by every strategy; see the sign-rule block in src/core/FundingRateSession.cs.
//   Without a rate series the short fallback charges the interest-rate floor as a cost
//   (same as longs), not zero. This is deliberate: shorts genuinely pay in crowded-short
//   bear regimes where RipShort operates, and RipShort trains with funding: null, so
//   this fallback IS its fitness landscape. Zero would improve selection on an
//   unsupportable assumption. See the full rationale in FundingRateSession.cs:107-118.
//
// RegimeBarsActive returned per trade: consecutive h1 bars where the bear regime
// was confirmed at entry (used by RipShortGA for regime-conditional FoldScore).
public static class RipShortSimulator
{
    private const int AtrPeriod         = 14;
    private const int RsiPeriod         = 7;
    private const int AdxPeriod         = 7;
    private const int SwingHighLookback = 20;   // h1 bars to locate the recent rally high for stop placement

    // Cost model: see TradeCosts in src/core/Simulator.cs. Fee + slippage on BOTH sides
    // (magnitude from Config.SlippageBps alone) + this gap premium on stop exits only.
    // RipShort used to be the only simulator that charged its ATR slip term twice while three
    // others charged it once; the two-sided charge now lives in TradeCosts for every strategy,
    // so this file no longer expresses a slippage magnitude of its own.
    private const double StopGapAtrK = 0.030;

    // Experimental loss-mitigation override, applied post-hoc on top of a frozen
    // genotype (not GA-evolved — sparse bear-window data already overfits the
    // existing 14-gene search, adding more dimensions would make that worse).
    // MaxHoldCandles is a time-based exit, independent of the hard ATR stop —
    // suppressing it while calm+losing just gives the trade more time; the hard
    // stop (fixed at entry) remains the tail-risk backstop regardless of mode.
    //
    // DORMANT: every production call site passes overrideCfg = null. Only unit
    // tests construct a non-null config.
    public enum ExitOverrideMode
    {
        /// <summary>No override — production behaviour: forced exit at MaxHoldCandles.</summary>
        None,

        /// <summary>
        /// Suppress the MaxHoldCandles time exit while the short is underwater and local
        /// volatility is calm. Deployed size is unchanged (1x), so the per-trade size
        /// accounting stays honest; only the holding period is extended (by at most
        /// MaxExtraHoldCandles bars).
        /// Caveat: backtest callers derive PortfolioReplay.Trade.HoldDuration from the
        /// genotype's MaxHoldCandles, so a waiting trade occupies its concurrency slot for
        /// longer than the replay believes. That understates occupancy but never
        /// understates size.
        /// </summary>
        WaitForBreakeven,

        /// <summary>
        /// WaitForBreakeven plus a single size-increasing add once price has moved
        /// DcaAtrMult x ATR further against the position.
        ///
        /// !! MUST NOT BE ENABLED IN PRODUCTION — BROKEN EXPOSURE ACCOUNTING !!
        /// The add multiplies deployed capital by <see cref="ExitOverrideConfig.MaxSizeMult"/>
        /// (default 2.0) but the position is still reported as ONE trade: the returned
        /// tuple carries a single percentage return scaled by that multiplier, with no
        /// per-trade size field. Portfolio-level concurrency caps in
        /// src/core/PortfolioReplay.cs (RipShort concurrent = 8, Config.MaxDirectionalConcurrent
        /// = 20) count each trade exactly once, so with this mode active real short
        /// exposure can reach MaxSizeMult x the modelled cap — both per-strategy and in
        /// the aggregate same-direction budget. Enabling it is only safe once the trade
        /// record carries a per-trade size multiplier that PortfolioReplay consumes when
        /// counting concurrency and sizing EUR exposure.
        ///
        /// The "ripshort_dca" trade kind is the audit trail for trades that took the add.
        /// </summary>
        DcaAndWait
    }

    /// <summary>
    /// MaxLossPct: absolute per-trade loss cap in percent (0 = disabled). Applied as a SECOND
    /// stop alongside the ATR stop; whichever is tighter wins.
    ///
    /// Why shorts specifically: the hard stop is swingHigh + StopLossAtrMult x ATR_at_entry, so
    /// it is volatility-RELATIVE and fixed at entry. Enter during a violent bear rally and the
    /// stop sits far away, so the loss scales with exactly the volatility that is about to hurt.
    /// A long's worst case is bounded (-100%); a short's is not, which is why an absolute cap
    /// belongs on this side of the book and why averaging down does not.
    ///
    /// Measured: the tail trades never arm the trailing stop at all (arming needs a favourable
    /// excursion of TrailingActivationAtrMult x ATR, which a -18% trade never had), so neither
    /// trailing nor a move-to-breakeven rule can reach them. Only an absolute cap can.
    /// </summary>
    /// <summary>
    /// Minimum-profit ratchet (LockTriggerPct / LockProfitPct, 0 = disabled).
    ///
    /// Once the position is LockTriggerPct in profit, the stop moves to LockProfitPct in profit
    /// and never goes back. It is a FLOOR, not a trail: the trailing stop may still take the
    /// trade further out, but it can no longer come back through breakeven.
    ///
    /// Why this is not redundant with the existing trailing stop: arming that trail does NOT
    /// guarantee a profitable exit, because the trail DISTANCE can exceed the ACTIVATION
    /// distance. Measured on the live genotype — RipShort arms at 2.13 x ATR but trails by
    /// 3.20 x ATR, so a trade can move in your favour, arm the trail, and still stop out
    /// 1.07 x ATR at a LOSS. The other four strategies happen to have distance &lt; activation
    /// and are structurally safe; RipShort, which carries the worst tail, is the one that is not.
    ///
    /// Percent rather than ATR is deliberate on the short side: an ATR-relative level widens
    /// exactly when a squeeze makes volatility spike.
    /// </summary>
    /// <summary>
    /// ATR-AWARE loss cap. When MaxLossAtrMult &gt; 0 the cap level becomes
    ///     clamp(MaxLossAtrMult x ATR%, MaxLossPctFloor, MaxLossPct)
    /// instead of the flat MaxLossPct.
    ///
    /// Why a flat percentage is the wrong shape: ATR% ranges roughly 1-8% across this universe,
    /// so one number is simultaneously far outside the noise on a liquid perp and INSIDE the
    /// noise on a meme perp. Measured — a flat 6% cap raised trade count 48% (462 -&gt; 682) and
    /// halved average return, because on high-ATR coins it was being brushed by ordinary
    /// movement rather than protecting against anything.
    ///
    /// Slippage compounds it: exit slippage scales linearly with the coin's own ATR
    /// (TradeCosts.SlippagePerSidePct) and stop exits pay an extra ATR-scaled gap premium
    /// (StopGapAtrK), so in a volatile squeeze the REALISED loss overshoots the intended level
    /// by more, exactly when the cap is meant to be doing its job. A cap set inside the noise
    /// buys premature exits and still does not bound the tail.
    ///
    /// The ceiling (MaxLossPct) is retained rather than dropped: a short's downside is unbounded,
    /// so ATR-scaling alone would let the cap widen without limit in precisely the regime it
    /// exists to survive. Scale with volatility, but never past an absolute line.
    /// </summary>
    /// <summary>
    /// ATR-scaled ratchet. When LockTriggerAtrMult &gt; 0 the arm/lock levels become
    ///     trigger% = LockTriggerAtrMult x ATR%,  lock% = LockProfitAtrMult x ATR%
    /// with LockTriggerPct / LockProfitPct acting as absolute CEILINGS.
    ///
    /// Same reasoning that fixed the loss cap: a flat 2% arm is noise on an 8%-ATR perp and a
    /// real move on a 1%-ATR one, so one number arms far too early on exactly the coins whose
    /// moves are largest. The grid showed it — trigger 2.0 produced 643 trades and 2.32% avg
    /// against trigger 6.0's 447 trades and 3.77%: arming early converts running winners into
    /// small locked ones, and it is the TRIGGER, not the lock size, that does the damage.
    /// Scaling the trigger by the coin's own ATR is the direct fix.
    /// </summary>
    /// <summary>Tuning for the dormant post-hoc exit override. Null at every production call site.</summary>
    /// <param name="Mode">Which override behaviour to apply (see <see cref="ExitOverrideMode"/>).</param>
    /// <param name="AtrGateRatio">Local h4 ATR ratio must be ≤ this to keep waiting ("calm").</param>
    /// <param name="DcaAtrMult">Adverse move (in ATR) that triggers the single DCA add.</param>
    /// <param name="MaxExtraHoldCandles">Absolute safety cap on extra bars beyond MaxHoldCandles.</param>
    /// <param name="MaxSizeMult">
    /// Total deployed size after the single DCA add, as a multiple of the initial leg
    /// (2.0 = equal-size add / doubled capital). Drives both the size-weighted blended
    /// cost basis and the return scaling of the reported trade.
    /// WARNING: this multiplier is invisible to portfolio-level exposure accounting —
    /// see the <see cref="ExitOverrideMode.DcaAndWait"/> remarks before using it.
    /// </param>
    public record ExitOverrideConfig(
        ExitOverrideMode Mode,
        double AtrGateRatio        = 1.4,
        double DcaAtrMult          = 1.2,
        int    MaxExtraHoldCandles = 60,
        double MaxSizeMult         = 2.0,
        double MaxLossPct          = 0.0,
        double LockTriggerPct      = 0.0,
        double LockProfitPct       = 0.0,
        double MaxLossAtrMult      = 0.0,
        double MaxLossPctFloor     = 0.0,
        double LockTriggerAtrMult  = 0.0,
        double LockProfitAtrMult   = 0.0);

    // EntryTime/EntryPrice: `Time` is the EXIT bar. EntryPrice is the BLENDED entry when a DCA
    // add has fired (see BlendedEntry), which is the economically correct basis. Appended as
    // NAMED fields so existing consumers compile unchanged.
    public static List<(DateTime Time, double Return, string Kind, DateTime EntryTime, double EntryPrice)> GetRipShortReturns(
        RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding = null,
        ExitOverrideConfig? overrideCfg = null, RatchetConfig ratchet = default)
    {
        var (trades, _) = RunRipShortMultiTF(g, h1, m15, funding, overrideCfg, ratchet);
        return trades.Select(t => (t.Item1, t.Item2, t.Item3, t.Item5, t.Item6)).ToList();
    }

    // 4-tuple variant carrying RegimeBarsActive — consumed by RipShortGA for
    // regime-conditional FoldScore filtering.
    internal static List<(DateTime Time, double Return, string Kind, int RegimeBarsActive, DateTime EntryTime, double EntryPrice)> GetRipShortReturnsWithRegime(
        RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding = null,
        ExitOverrideConfig? overrideCfg = null, RatchetConfig ratchet = default)
    {
        var (trades, _) = RunRipShortMultiTF(g, h1, m15, funding, overrideCfg, ratchet);
        return trades;
    }

    public record RipShortTradeState(
        bool   InTrade,
        double Entry,
        double HardStop,
        double Target,
        bool   TrailArmed,
        double TrailLow,    // lowest 15m close seen since entry (trail reference for a short)
        int    HoldCount);

    public static RipShortTradeState GetRipShortTradeState(
        RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding = null)
    {
        var (_, state) = RunRipShortMultiTF(g, h1, m15, funding);
        return state;
    }

    private static (List<(DateTime, double, string, int, DateTime, double)> Trades, RipShortTradeState FinalState)
        RunRipShortMultiTF(RipShortGenotype g, ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, FundingRateSession? funding,
        ExitOverrideConfig? overrideCfg = null, RatchetConfig ratchet = default)
    {
        int h1Warmup = Math.Max(
                           Math.Max(g.RegimeLongEmaPeriod, Math.Max(g.EmaPeriod, RsiPeriod + 2)),
                           AdxPeriod * 2 + 1)
                       + g.RegimeSlopeLookback + 3;

        if (h1.Length <= h1Warmup + 2 || m15.Length < (h1Warmup + 2) * 4)
            return ([], new RipShortTradeState(false, 0, 0, 0, false, 0, 0));

        var h1Closes = CandleExt.Closes(h1);
        var h1Highs  = CandleExt.Highs(h1);
        var h1Lows   = CandleExt.Lows(h1);

        var h1RegimeEma = Trend.Ema(h1Closes, g.RegimeLongEmaPeriod);
        var h1Ema       = Trend.Ema(h1Closes, g.EmaPeriod);
        var h1Rsi       = Momentum.Rsi(h1Closes, RsiPeriod);
        var h1Adx       = Trend.Adx(h1Highs, h1Lows, h1Closes, AdxPeriod);

        var h4      = FadeShortSimulator.AggregateCandles(h1.ToArray(), 4);
        var h4Highs = CandleExt.Highs(h4);
        var h4Lows  = CandleExt.Lows(h4);
        var h4Cls   = CandleExt.Closes(h4);
        var h4Atr   = Volatility.Atr(h4Highs, h4Lows, h4Cls, AtrPeriod);

        // Local h4 ATR ratio (current vs trailing 20-bar average) — same "is
        // volatility elevated" signal DynamicGuardSession computes from BTC, but
        // measured on the traded coin itself since that's what actually matters
        // for whether it's safe to keep waiting on this position.
        double[] h4AtrRatio = new double[h4Atr.Length];
        if (overrideCfg != null)
        {
            const int atrRatioLookback = 20;
            double atrSum = 0;
            for (int i = 0; i < h4Atr.Length; i++)
            {
                atrSum += h4Atr[i];
                if (i >= atrRatioLookback) atrSum -= h4Atr[i - atrRatioLookback];
                double avgAtr = atrSum / Math.Min(i + 1, atrRatioLookback);
                h4AtrRatio[i] = avgAtr > 1e-10 ? h4Atr[i] / avgAtr : 1.0;
            }
        }

        var m15Closes = CandleExt.Closes(m15);
        var m15Highs  = CandleExt.Highs(m15);
        var m15Lows   = CandleExt.Lows(m15);

        // Precompute consecutive bear-regime bar count at each h1 bar.
        // A bar qualifies when: close < RegimeLongEma AND RegimeLongEma falling over slopeLookback bars.
        // Counter resets to 0 on the first bar that breaks the regime condition.
        int[] bearRegimeBarsAtBar = new int[h1.Length];
        int bearRunning = 0;
        int slopeLen = g.RegimeSlopeLookback;
        var regimeSlope = Signals.EmaSlope(h1RegimeEma, g.RegimeSlopeLookback);
        for (int i = h1Warmup; i < h1.Length; i++)
        {
            bool regimeBar = h1Closes[i] < h1RegimeEma[i] && regimeSlope[i] < 0;
            bearRunning = regimeBar ? bearRunning + 1 : 0;
            bearRegimeBarsAtBar[i] = bearRunning;
        }

        var result = new List<(DateTime, double, string, int, DateTime, double)>();

        bool     inTrade         = false;
        double   entry           = 0;
        double   hardStop        = 0;
        double   target          = 0;
        double   trailLow        = 0;
        double   atrEntry        = 0;
        bool     trailArmed      = false;
        bool     lockArmed       = false;
        int      entryIH1        = 0;
        int      entryRegimeBars = 0;
        DateTime entryTime       = default;
        bool     dcaDone         = false;
        double   dcaSizeMult     = 1.0;
        bool     everWaited      = false;

        int    cachedH1Ref     = -1;
        bool   cachedSetupMet  = false;
        double cachedSwingHigh = 0;
        double cachedAtrH4     = 0;
        int    cachedRegimeBars = 0;

        int m15Start = (h1Warmup + 1) * 4;
        int m15Limit = h1.Length * 4;

        for (int im15 = m15Start; im15 < Math.Min(m15.Length, m15Limit); im15++)
        {
            int ih1   = im15 / 4;
            int h1Ref = ih1 - 1;

            if (h1Ref < h1Warmup || h1Ref >= h1.Length) continue;

            double m15Price = m15Closes[im15];

            if (!inTrade)
            {
                if (h1Ref != cachedH1Ref)
                {
                    cachedH1Ref    = h1Ref;
                    cachedSetupMet = false;

                    // Use the previous completed h4 bar — the current h4 bar aggregates future h1 bars
                    int    h4Ref = Math.Max(0, h1Ref / 4 - 1);
                    double atrH4 = h4Ref < h4Atr.Length && h4Atr[h4Ref] > 1e-10
                                 ? h4Atr[h4Ref]
                                 : h1Closes[h1Ref] * 0.08;

                    // Bear regime: close below long EMA AND long EMA is trending downward
                    bool regimeOk = h1Closes[h1Ref] < h1RegimeEma[h1Ref]
                                 && h1RegimeEma[h1Ref] < h1RegimeEma[h1Ref - slopeLen];

                    // Short-term trend: confirmed downtrend with momentum
                    bool trendOk = h1Closes[h1Ref] < h1Ema[h1Ref]
                                && h1Adx[h1Ref] >= g.AdxThreshold;

                    // Rally zone: RSI bounced up into 40–60 range (relief rally, not new low)
                    bool rallyOk = h1Rsi[h1Ref] >= g.RsiRallyThreshold;

                    if (regimeOk && trendOk && rallyOk)
                    {
                        // Stop above the recent rally high — if price exceeds it, the rip became a reversal
                        int    lbStart   = Math.Max(0, h1Ref - SwingHighLookback);
                        double swingHigh = h1Highs[lbStart];
                        for (int j = lbStart + 1; j <= h1Ref; j++)
                            if (h1Highs[j] > swingHigh) swingHigh = h1Highs[j];

                        cachedSetupMet   = true;
                        cachedSwingHigh  = swingHigh;
                        cachedAtrH4      = atrH4;
                        cachedRegimeBars = bearRegimeBarsAtBar[h1Ref];
                    }
                }

                // 15m bearish BoS: close below previous 15m candle's low.
                // Enter at the OPEN of the next 15m bar — the BoS is only confirmed at bar close.
                if (cachedSetupMet && m15Closes[im15] < m15Lows[im15 - 1])
                {
                    int nextBar = im15 + 1;
                    if (nextBar >= Math.Min(m15.Length, m15Limit)) continue;
                    inTrade         = true;
                    entry           = m15[nextBar].Open;
                    atrEntry        = cachedAtrH4;
                    hardStop        = cachedSwingHigh + g.StopLossAtrMult * atrEntry;
                    target          = entry - g.TakeProfitAtrMult * atrEntry;
                    trailLow        = entry;
                    trailArmed      = false;
                    lockArmed       = false;
                    entryIH1        = nextBar / 4;
                    entryRegimeBars = cachedRegimeBars;
                    entryTime       = m15[nextBar].Time;
                    dcaDone         = false;
                    dcaSizeMult     = 1.0;
                }
            }
            else
            {
                if (m15Price < trailLow) trailLow = m15Price;
                if (!trailArmed && entry - trailLow >= g.TrailingActivationAtrMult * atrEntry)
                    trailArmed = true;

                int holdH1 = ih1 - entryIH1;

                // Wick-triggered hard levels (pessimistic — intrabar extremes trigger)
                // Absolute loss cap (opt-in). Wick-triggered like the ATR stop, and deliberately
                // evaluated on the SAME bar so the tighter of the two always wins.
                double capPct = overrideCfg?.MaxLossPct ?? 0.0;
                if (capPct > 0.0 && (overrideCfg?.MaxLossAtrMult ?? 0.0) > 0.0 && entry > 1e-9)
                {
                    double atrPctNow = atrEntry / entry * 100.0;
                    capPct = Math.Clamp(overrideCfg!.MaxLossAtrMult * atrPctNow,
                                        overrideCfg.MaxLossPctFloor, overrideCfg.MaxLossPct);
                }
                double capPx = capPct > 0.0 ? entry * (1.0 + capPct / 100.0) : double.MaxValue;
                bool hitCap    = m15Highs[im15] >= capPx;

                // Minimum-profit ratchet: arm on favourable excursion, then floor the stop at a
                // guaranteed profit. For a short the stop sits ABOVE entry, so "tighter" is lower.
                // Production ratchet (ExecContext) takes precedence; the ExitOverrideConfig form
                // below is the experiment path that the exit-override table sweeps.
                //
                // These were two separate implementations of the same rule — RipShort could only
                // ever ratchet through overrideCfg, so on the PRODUCTION path it had no profit floor
                // at all while every other signal strategy did. The shared ExitRatchet is the one
                // the sign rule is tested against; a second copy is how a short ends up with a
                // long's comparison operator.
                double lockPx = double.MaxValue;
                if (ratchet.Enabled)
                {
                    if (!lockArmed && ExitRatchet.ShouldArm(false, entry, atrEntry, trailLow, ratchet))
                        lockArmed = true;
                    if (lockArmed && ExitRatchet.LockPrice(false, entry, atrEntry, trailLow, ratchet) is double rsLk)
                        lockPx = rsLk;
                }
                else
                {
                    double lockTrig = overrideCfg?.LockTriggerPct ?? 0.0;
                    double lockProf = overrideCfg?.LockProfitPct  ?? 0.0;
                    if ((overrideCfg?.LockTriggerAtrMult ?? 0.0) > 0.0 && entry > 1e-9)
                    {
                        double atrPctE = atrEntry / entry * 100.0;
                        double tCeil = lockTrig > 0 ? lockTrig : double.MaxValue;
                        double pCeil = lockProf > 0 ? lockProf : double.MaxValue;
                        lockTrig = Math.Min(overrideCfg!.LockTriggerAtrMult * atrPctE, tCeil);
                        lockProf = Math.Min(overrideCfg.LockProfitAtrMult  * atrPctE, pCeil);
                    }
                    if (lockTrig > 0.0 && !lockArmed && entry > 1e-9
                        && (entry - trailLow) / entry * 100.0 >= lockTrig)
                        lockArmed = true;
                    if (lockArmed && lockProf > 0.0)
                        lockPx = entry * (1.0 - lockProf / 100.0);
                }

                bool hitStop   = m15Highs[im15] >= hardStop || hitCap
                                 || (lockArmed && m15Highs[im15] >= lockPx);
                bool hitTarget = m15Lows[im15]  <= target;
                bool hitTrail  = trailArmed && m15Price > trailLow + g.TrailingStopAtrMult * atrEntry;
                bool pastMaxHold = holdH1 >= g.MaxHoldCandles;

                // Loss-mitigation override: past MaxHoldCandles, still losing, and calm
                // enough (local ATR ratio ≤ gate) → keep waiting for breakeven instead of
                // forcing the exit. Hard stop/target/trail above are untouched and remain
                // the real risk cap; this only suppresses the time-based exit.
                bool waiting = false;
                if (pastMaxHold && overrideCfg != null)
                {
                    int    h4RefNow    = Math.Min(h4AtrRatio.Length - 1, Math.Max(0, ih1 / 4 - 1));
                    double atrRatioNow = h4AtrRatio.Length > 0 ? h4AtrRatio[h4RefNow] : 1.0;
                    waiting = ShouldWaitPastTimeout(m15Price, entry, atrRatioNow, holdH1 - g.MaxHoldCandles, overrideCfg);
                    if (waiting) everWaited = true;

                    // Never add on a bar that has already triggered an exit level: the extra
                    // capital would never actually be deployed, yet the reported return would
                    // still be scaled by MaxSizeMult. It also keeps exitPx below consistent
                    // with the target level that triggered the exit.
                    bool exitingThisBar = hitStop || hitTarget || hitTrail;

                    if (!exitingThisBar && ShouldDca(waiting, dcaDone, m15Price, entry, atrEntry, overrideCfg))
                    {
                        // Rebase the position onto the size-weighted blended cost basis and then
                        // treat it exactly like a fresh entry at that basis:
                        //  · target   — recomputed from the blended entry. Left anchored to the
                        //               original (lower) entry it would demand a move the enlarged
                        //               position never needed to make.
                        //  · trailing — for a SHORT the favourable direction is DOWN, so trailLow
                        //               tracks the best (lowest) price seen and the trail arms on
                        //               `entry - trailLow >= TrailingActivationAtrMult x ATR`.
                        //               Blending entry UPWARD against a running minimum accumulated
                        //               before the add would inflate that difference and arm (or
                        //               even immediately fire) the trail on profit belonging to the
                        //               old, smaller position. So reset trailLow to the blended
                        //               entry and clear trailArmed — the same state a fresh entry
                        //               starts in — which makes arming mean "activation x ATR of
                        //               profit against the blended cost basis" and guarantees the
                        //               trail can only fire off a price actually observed after
                        //               the add.
                        //  · hardStop — deliberately NOT rebased: it is the tail-risk backstop and
                        //               stays fixed at the original entry's swing high.
                        entry       = BlendedEntry(entry, m15Price, overrideCfg.MaxSizeMult);
                        target      = entry - g.TakeProfitAtrMult * atrEntry;
                        trailLow    = entry;
                        trailArmed  = false;
                        dcaDone     = true;
                        dcaSizeMult = overrideCfg.MaxSizeMult;   // total deployed capital, as a multiple
                                                                 // of the initial leg (see ExitOverrideMode.DcaAndWait)
                    }
                }
                bool timedOut = pastMaxHold && !waiting;

                // Time-decay stop: after TimeStopBars bars, tolerated adverse (above-entry) move
                // narrows linearly from TimeStopLossPct down to 0% at MaxHoldCandles.
                bool hitTimeStop = false;
                if (!hitStop && !hitTarget && !hitTrail && !timedOut && !waiting
                    && holdH1 >= g.TimeStopBars && g.MaxHoldCandles > g.TimeStopBars)
                {
                    double progress       = (double)(holdH1 - g.TimeStopBars) / (g.MaxHoldCandles - g.TimeStopBars);
                    double maxAdverseRatio = g.TimeStopLossPct * (1.0 - progress);
                    hitTimeStop = (m15Price - entry) / entry > maxAdverseRatio;
                }

                if (hitStop || hitTarget || hitTrail || timedOut || hitTimeStop)
                {
                    // Same bar touches both stop and target → STOP wins (pessimistic)
                    // Fill at whichever stop level actually triggered — the ATR stop or the
                    // absolute cap, whichever is TIGHTER. Filling at hardStop when the cap fired
                    // would report a loss the position never took.
                    double stopPx = capPx < hardStop ? capPx : hardStop;
                    if (lockArmed && lockPx < stopPx) stopPx = lockPx;
                    double exitPx = hitStop   ? stopPx :
                                    hitTarget ? target   : m15Price;
                    double fundingPnl = FundingRateSession.PnlPct(entryTime, m15[im15].Time, funding, isLong: false);
                    double ret = ((entry - exitPx) / entry * 100.0 - TradeCost(hitStop, atrEntry, entry) + fundingPnl) * dcaSizeMult;
                    string kind = dcaDone ? "ripshort_dca" : everWaited ? "ripshort_wait" : "ripshort";
                    result.Add((m15[im15].Time, ret, kind, entryRegimeBars, entryTime, entry));
                    inTrade = false;
                }
            }
        }

        if (inTrade)
        {
            double finalPx    = m15Closes[^1];
            double fundingPnl = FundingRateSession.PnlPct(entryTime, m15[^1].Time, funding, isLong: false);
            double ret = ((entry - finalPx) / entry * 100.0 - TradeCost(false, atrEntry, entry) + fundingPnl) * dcaSizeMult;
            string kind = dcaDone ? "ripshort_dca" : everWaited ? "ripshort_wait" : "ripshort";
            result.Add((m15[^1].Time, ret, kind, entryRegimeBars, entryTime, entry));
        }

        int finalHold = inTrade ? h1.Length - 1 - entryIH1 : 0;
        return (result, new RipShortTradeState(inTrade, entry, hardStop, target, trailArmed, trailLow, finalHold));
    }

    // Pure decision: keep waiting past MaxHoldCandles instead of forcing the timeout exit?
    // Short is underwater (price > entry), local volatility is calm, and the safety cap
    // on extra bars hasn't been reached.
    internal static bool ShouldWaitPastTimeout(
        double price, double entry, double atrRatioNow, int extraHoldBars, ExitOverrideConfig cfg)
    {
        if (cfg.Mode == ExitOverrideMode.None) return false;
        bool losing     = price > entry;
        bool calmEnough = atrRatioNow <= cfg.AtrGateRatio;
        bool underCap   = extraHoldBars < cfg.MaxExtraHoldCandles;
        return losing && calmEnough && underCap;
    }

    // Pure decision: trigger the single DCA add? Only while waiting, only once per
    // trade, only after price has moved DcaAtrMult×ATR further against the position.
    // MaxSizeMult ≤ 1.0 means there is no add to make: taking it would leave the cost
    // basis untouched (see BlendedEntry) yet still burn dcaDone, relabel the trade
    // "ripshort_dca", and scale the reported return by a fraction of itself.
    internal static bool ShouldDca(
        bool waiting, bool dcaDone, double price, double entry, double atrEntry, ExitOverrideConfig cfg) =>
        waiting && cfg.Mode == ExitOverrideMode.DcaAndWait && !dcaDone
        && cfg.MaxSizeMult > 1.0
        && price - entry >= cfg.DcaAtrMult * atrEntry;

    // Pure helper: size-weighted blended cost basis after the single DCA add.
    // The initial leg carries size 1.0 and the add carries (maxSizeMult - 1.0), so
    // maxSizeMult = 2.0 reduces to the equal-size midpoint (entry + addPrice) / 2.
    // maxSizeMult ≤ 1.0 means "no add" and leaves the cost basis untouched.
    internal static double BlendedEntry(double entry, double addPrice, double maxSizeMult)
    {
        double addSize = Math.Max(0.0, maxSizeMult - 1.0);
        return (entry + addPrice * addSize) / (1.0 + addSize);
    }

    internal static double TradeCost(bool isStop, double atrEntry, double entryPx)
        => TradeCosts.RoundTripPct(TradeCosts.AtrPct(atrEntry, entryPx), isStop, StopGapAtrK);

}
