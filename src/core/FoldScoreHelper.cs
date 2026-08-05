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

        // ── Tunable term weights (FitnessConfig) ──────────────────────────────
        // Each of the six weights below scales exactly ONE term of the canonical score
        // and is a bit-for-bit no-op at its default of 1.0 — see FitnessConfig for the
        // per-field contract. Weights are floored at 0 defensively: a hand-edited or
        // UI-generated fitness_config.json can carry any number, and a NEGATIVE weight
        // would flip the sign of its term (turning a reward into a punishment and
        // inverting the GA's gradient). Worse, a negative DdPenalty would drive ddDiv
        // to exactly 0 or below — dividing by ~0 or flipping the sign of the entire
        // fold score. None of the six has a meaningful negative interpretation, so 0
        // ("term switched off") is the floor. NaN, if someone writes one, propagates.
        double gainW      = Math.Max(0.0, cfg.GainW);
        double wrW        = Math.Max(0.0, cfg.WrW);
        double qualityW   = Math.Max(0.0, cfg.QualityW);
        double freqW      = Math.Max(0.0, cfg.FreqW);
        double ddW        = Math.Max(0.0, cfg.DdPenalty);
        double retentionW = Math.Max(0.0, cfg.RetentionW);

        // WrW scales the ABOVE-knee reward slope only. The sub-0.40 branch is a
        // disqualifying penalty ramp, not a reward, so it stays unweighted — otherwise
        // WrW = 0 would erase the penalty for a terrible win rate instead of just
        // switching the bonus off.
        double wrMult = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 3.0 * wrW;

        double pfMult = pf < 1.5 ? (pf - 1.0) / 0.5 : 1.0 + (pf - 1.5) * 0.5;
        int losses = returns.Count - wins;
        double avgWin  = wins   > 0 ? grossWins / wins   : 0;
        double avgLoss = losses > 0 ? grossLoss / losses : avgWin;
        double rr      = avgLoss > 1e-10 ? avgWin / avgLoss : (avgWin > 0 ? 5.0 : 1.0);
        double rrMult  = rr < 2.5 ? (rr - 1.0) / 1.5 : 1.0 + (rr - 2.5) * 0.3;
        // QualityW scales the multiplier's DEVIATION FROM 1.0, written in the lerp form
        // `raw*w + (1-w)` rather than `1 + (raw-1)*w` so that w == 1.0 collapses to
        // `raw*1.0 + 0.0` — exactly `raw`, with no rounding drift at any magnitude.
        //
        // Clamp placement, deliberately:
        //  · the 2.5 cap is applied AFTER weighting. The cap exists to stop one
        //    spectacular fold's quality reading from dominating fitness; letting a
        //    QualityW > 1 punch through it would defeat exactly that purpose.
        //  · the 0.0 floor stops a large QualityW from turning a sub-1.0 quality
        //    reading (pf just over 1.0, or rr under 1.0) into a NEGATIVE multiplier,
        //    which would flip the sign of the whole fold score.
        // At QualityW = 1.0 neither clamp changes anything: Sqrt is never negative, so
        // Clamp(raw, 0, 2.5) == Min(raw, 2.5), the original expression.
        double qualityRaw  = Math.Sqrt(pfMult * rrMult);
        double qualityMult = Math.Clamp(qualityRaw * qualityW + (1.0 - qualityW), 0.0, 2.5);

        // DdPenalty scales drawdown sensitivity. With ddW >= 0, ddDiv >= 1.0 always,
        // so this division can never blow up or change the score's sign.
        double ddDiv = 1.0 + maxDd * 10.0 * ddW;
        // FreqW scales the log trade-count bonus. The Math.Max(1.0, ...) inside the log
        // keeps it strictly one-sided — a fold that barely clears minTradesPerFold gets
        // no bonus, never a penalty (thin folds are already handled by the gate above).
        double freqBonus = 1.0 + 0.15 * freqW * Math.Log(Math.Max(1.0, returns.Count / (double)minTradesPerFold));

        // RetentionW scales retention's deviation from 1.0, same lerp form as quality.
        // The 0.2 floor stays on the RAW ratio — it defines how much end-of-fold
        // give-back the score is willing to look at at all, independent of weighting.
        // The 0.0 floor afterwards stops a RetentionW > 1 from driving a floored
        // retention (0.2) negative: 0.2*6 + (1-6) = -3.8 would invert the fold score.
        double peakGain     = peak - 1.0;
        double retentionRaw = peakGain > 0.01
            ? Math.Max(0.2, (balance - 1.0) / peakGain)
            : 1.0;
        double retentionMult = Math.Max(0.0, retentionRaw * retentionW + (1.0 - retentionW));

        int    n       = returns.Count;
        double sharpe  = Simulator.SharpeRatio(returns, n);
        double calmar  = Simulator.CalmarRatio(returns);
        double pfStat  = Simulator.ProfitFactor(returns);
        double sortino = Simulator.SortinoRatio(returns, n);
        // GainW scales the raw gain term. At 1.0 the `* gainW` factor is exact, so the
        // product below is bit-identical to the historical `gain * 100.0 * wrMult * ...`.
        double base_   = gain * 100.0 * gainW * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
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

    // CVaR (expected shortfall) left-tail penalty.
    //
    // UNITS — read before touching this. StatisticalTests.CVaR returns the SIGNED mean
    // of the worst `alpha` fraction of trade returns, expressed in PERCENT:
    //   cvar5 = -5.0  ->  the worst 5% of trades average a 5% LOSS  (fat, dangerous tail)
    //   cvar5 = -0.5  ->  the worst 5% barely lose anything          (tight, safe tail)
    // Negative = loss, and MORE negative = WORSE. The penalty must therefore grow as
    // cvar5 falls, never the other way round (an earlier version had the comparison
    // inverted and rewarded fat tails while punishing safe ones).
    //
    //   cvar5 >= -3%  ->  1.0                                  (tail inside tolerance)
    //   cvar5 <  -3%  ->  decreasing with the excess loss, saturating at cvar5 = -6%
    // The multiplier is floored at 0.5 so it can never reach 0 or go negative — that
    // would zero out or flip the sign of the whole fitness score.
    public static double CVaRPenalty(List<double> returns, FitnessConfig cfg)
    {
        if (cfg.CVaRW <= 0 || returns.Count < 20) return 1.0;
        double cvar5 = StatisticalTests.CVaR(returns, 0.05);

        const double tolerancePct = -3.0; // no penalty for tails shallower than this
        const double scalePct     =  3.0; // excess loss (pct) at which the penalty saturates

        if (cvar5 >= tolerancePct) return 1.0;
        double excess = Math.Min((tolerancePct - cvar5) / scalePct, 1.0); // 0..1
        return Math.Clamp(1.0 - excess * cfg.CVaRW, 0.5, 1.0);
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

    // ── Fold boundaries ───────────────────────────────────────────────────
    // NOTE ON INDEX SPACES: the two Compute*FoldBoundaries methods below return bounds
    // in the INDEX SPACE OF THE ARRAY THEY WERE COMPUTED FROM. They are only safe to
    // apply to that same array. They must NOT be shared across coins — every coin has a
    // different array length, a different first candle, and (for the bear strategies) a
    // regime-filtered concatenation of candles, so index i is a different calendar date
    // on every coin. Cross-coin walk-forward folds go through the TIME-based
    // ComputeRegimeAwareFoldWindows + RangeForWindow pair instead.
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

        for (int i = 0; i < btcSeries.Length; i++)
        {
            if (isActive(btcSeries[i].Regime)) activeInFold++;

            if (activeInFold >= targetPerFold && result.Count < folds - 1)
            {
                result.Add((foldStart, i + 1));
                int embargoEnd = Math.Min(i + 1 + embargoBars, btcSeries.Length);
                foldStart = embargoEnd;
                activeInFold = 0;
                i = embargoEnd - 1;
            }
        }

        if (foldStart < btcSeries.Length)
            result.Add((foldStart, btcSeries.Length));

        while (result.Count < folds)
            result.Add((btcSeries.Length, btcSeries.Length));

        return result.Take(folds).ToArray();
    }

    // Regime-aware walk-forward folds expressed as [Start, End) TIME windows.
    //
    // Same active-bar balancing and embargo logic as ComputeRegimeAwareFoldBoundaries —
    // the indices are simply translated into the BTC series' own timestamps so that each
    // coin can map the window onto its OWN candle array (see RangeForWindow). This is
    // what makes the folds comparable across coins: fold f covers the same calendar
    // stretch of market history for every symbol, regardless of how long that symbol's
    // array is or where it starts.
    //
    // Fold 0 opens at DateTime.MinValue and the final fold closes at DateTime.MaxValue so
    // that coin history extending slightly beyond the BTC series on either side is still
    // scored rather than silently dropped. Folds that came back empty in index space are
    // returned as an empty (MaxValue, MaxValue) window.
    public static (DateTime Start, DateTime End)[] ComputeRegimeAwareFoldWindows(
        RegimeBar[] btcSeries,
        Func<MarketRegime, bool> isActive,
        int folds,
        double embargoPct = 0.05)
    {
        var idx = ComputeRegimeAwareFoldBoundaries(btcSeries, isActive, folds, embargoPct);
        var windows = new (DateTime Start, DateTime End)[idx.Length];

        for (int f = 0; f < idx.Length; f++)
        {
            int s = idx[f].Start, e = idx[f].End;
            if (btcSeries.Length == 0 || s >= e || s >= btcSeries.Length)
            {
                windows[f] = (DateTime.MaxValue, DateTime.MaxValue);
                continue;
            }
            DateTime start = f == 0 ? DateTime.MinValue : btcSeries[s].Time;
            DateTime end   = e < btcSeries.Length ? btcSeries[e].Time : DateTime.MaxValue;
            windows[f] = (start, end);
        }
        return windows;
    }

    // Maps a [windowStart, windowEnd) time window onto a coin's own time-ordered candle
    // array, returning the half-open index range [Start, End) of the candles inside it.
    // Returns an empty range when the coin has no candles in the window.
    //
    // Candles are ascending in time (bear-window training arrays are concatenations of
    // ordered slices, so they are ascending with gaps — binary search is still valid).
    // Mirrors the binary-search pattern in RegimeBarLookup.TagRegimes.
    public static (int Start, int End) RangeForWindow(
        ReadOnlySpan<Candle> candles, DateTime windowStart, DateTime windowEnd)
    {
        if (candles.Length == 0 || windowEnd <= windowStart) return (0, 0);
        int s = LowerBound(candles, windowStart);
        int e = LowerBound(candles, windowEnd);
        if (e < s) e = s;
        return (s, e);
    }

    // First index i with candles[i].Time >= t, or candles.Length if there is none.
    private static int LowerBound(ReadOnlySpan<Candle> candles, DateTime t)
    {
        long ticks = t.Ticks;
        int lo = 0, hi = candles.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (candles[mid].Time.Ticks < ticks) lo = mid + 1;
            else                                 hi = mid;
        }
        return lo;
    }

    // Per-coin percentage folds — the fallback used when no BTC regime series is
    // available. Each coin slices its OWN array into `folds` equal parts, so index
    // alignment is trivially correct by construction. An embargo gap of
    // embargoPct x foldSize bars is dropped from the start of every fold after the first.
    public static (int Start, int End) PerCoinFoldRange(
        int totalBars, int folds, int fold, double embargoPct = 0.05)
    {
        if (folds < 1) folds = 1;
        if (totalBars <= 0) return (0, 0);

        int start = (int)((long)totalBars * fold / folds);
        int end   = fold >= folds - 1 ? totalBars : (int)((long)totalBars * (fold + 1) / folds);
        if (fold > 0) start += (int)(totalBars / folds * embargoPct);
        if (start > end) start = end;
        return (start, end);
    }

    // Combines the surviving walk-forward fold scores into one fitness value.
    //
    // Callers MUST pass only folds that actually reached MinTradesPerFold trades. An
    // under-populated fold returns the CONSTANT -1.0 sentinel from Canonical(), and
    // feeding constants into mean - stdMult*std inverts the gradient: past two dead
    // folds, raising a live fold's score LOWERS fitness (it widens the spread), so the
    // GA starts selecting for genotypes that trade badly or not at all.
    //
    //   0 surviving folds -> DeadFoldFitness (clearly worst, flat, nothing to exploit)
    //   1 surviving fold  -> that fold's score verbatim (monotone; no variance to penalise)
    //   2+                -> mean - stdMult * std, stdMult VC-proportional on avg N/d
    //                        over the SURVIVING folds only.
    public const double DeadFoldFitness = -1000.0;

    public static double AggregateFoldScores(
        IReadOnlyList<double> foldScores,
        IReadOnlyList<int> foldTradeCounts,
        int d)
    {
        if (foldScores.Count == 0) return DeadFoldFitness;
        if (foldScores.Count == 1) return foldScores[0];

        double mean = foldScores.Average();
        double std  = Math.Sqrt(foldScores.Select(s => (s - mean) * (s - mean)).Average());
        // VC-proportional penalty: stdMult = 0.75 when avgN/d >= 10, scales up to 2.0 as
        // N/d falls — prevents the GA from over-trusting fold scores when sample is thin.
        double avgN    = foldTradeCounts.Count > 0 ? foldTradeCounts.Average() : 0.0;
        double stdMult = Math.Clamp(7.5 / Math.Max(1.0, avgN / Math.Max(1, d)), 0.75, 2.0);
        return mean - stdMult * std;
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
