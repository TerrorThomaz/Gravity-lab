namespace TradingGA;

public static class FoldScoreHelper
{
    public static double Canonical(
        List<double> returns,
        double posFrac,
        int minTradesPerFold,
        FitnessConfig cfg,
        double volWeight = 1.0,
        double statBonusCeiling = 1.0,
        // Per-trade maximum adverse excursion, in percent, NEGATIVE or zero, one entry per return.
        // OPT-IN: null (or a mismatched length, which is a wiring mistake) leaves the drawdown term
        // exactly as it was, so a strategy whose simulator does not yet report excursions is scored
        // bit-for-bit as before. Without this the fold score books each trade whole and a trade that
        // sank 30% before closing at +1% is indistinguishable from a smooth ride to +1% — which is
        // how a Grid retrain tripled MaxHoldCandles (53 -> 164) for a fold score that could not
        // register the added time at risk.
        IReadOnlyList<double>? maePct = null)
    {
        if (returns.Count < minTradesPerFold) return -1.0;

        // Cap the winners before anything reads them, so every downstream term — gain, pf, rr,
        // Sharpe, the tail terms — sees the same capped series. Index order and length are
        // preserved, which is what keeps the maePct walk below in lockstep.
        returns = WinsorizeWinners(returns, cfg.WinsorizeWinnerPct);

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

        var mae = maePct is { } m && m.Count == returns.Count ? m : null;

        double balance = 1.0, peak = 1.0, maxDd = 0.0;
        for (int i = 0; i < returns.Count; i++)
        {
            // Dip to the trade's worst point BEFORE settling it. Same sequential, non-overlapping
            // assumption the walk already made — this only adds the trough inside each hold.
            if (mae != null)
            {
                double trough = balance + Math.Min(0.0, mae[i]) / 100.0 * posFrac;
                double ddTrough = (peak - trough) / peak;
                if (ddTrough > maxDd) maxDd = ddTrough;
            }

            balance += returns[i] / 100.0 * posFrac;
            if (balance > peak) peak = balance;
            double dd = (peak - balance) / peak;
            if (dd > maxDd) maxDd = dd;
        }

        double gain = balance - 1.0;
        if (gain <= 0) return gain * 100 - 0.5;

        // Six term weights from FitnessConfig, each a no-op at 1.0. Floored at 0 (negative would invert the score's sign).
        double gainW      = Math.Max(0.0, cfg.GainW);
        double wrW        = Math.Max(0.0, cfg.WrW);
        double qualityW   = Math.Max(0.0, cfg.QualityW);
        double freqW      = Math.Max(0.0, cfg.FreqW);
        double ddW        = Math.Max(0.0, cfg.DdPenalty);
        double retentionW = Math.Max(0.0, cfg.RetentionW);

        // WrW scales the above-0.40 slope only; the sub-0.40 penalty ramp stays unweighted (WrW=0 must not erase it).
        double wrMult = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 3.0 * wrW;

        double pfMult = pf < 1.5 ? (pf - 1.0) / 0.5 : 1.0 + (pf - 1.5) * 0.5;
        int losses = returns.Count - wins;
        double avgWin  = wins   > 0 ? grossWins / wins   : 0;
        double avgLoss = losses > 0 ? grossLoss / losses : avgWin;
        double rr      = avgLoss > 1e-10 ? avgWin / avgLoss : (avgWin > 0 ? 5.0 : 1.0);
        double rrMult  = RrMultiplier(rr);
        // QualityW lerps deviation from 1.0; 2.5 cap applied AFTER weighting (so QualityW>1 can't punch through).
        // RrMultiplier is strictly positive on (0,inf) so the sqrt argument is never negative.
        // NaN screen: Math.Max(0, NaN) == NaN, which would poison the GA sort order.
        double pfTerm      = double.IsNaN(pfMult) ? 0.0 : Math.Max(0.0, pfMult);
        double qualityRaw  = Math.Sqrt(pfTerm * rrMult);
        double qualityMult = Math.Clamp(qualityRaw * qualityW + (1.0 - qualityW), 0.0, 2.5);

        // ddDiv >= 1.0 always (ddW floored at 0), so this division never blows up.
        double ddDiv = 1.0 + maxDd * 10.0 * ddW;
        // Frequency is paid for twice: `gain` scales linearly with trade count, and this log bonus adds more.
        // freqQuality gates the bonus on PF and avg return — at PF<=1 the bonus is 0 (adding losing trades earns nothing).
        // Set FreqW=0 for strategies with too many marginal trades; `gain` still rewards genuine volume.
        double avgReturn   = returns.Average();
        double freqQuality =
            Math.Clamp((pf - 1.0) / Math.Max(1e-9, cfg.FreqPfFull - 1.0), 0.0, 1.0) *
            Math.Clamp(avgReturn / Math.Max(1e-9, cfg.FreqAvgFullPct), 0.0, 1.0);

        double freqBonus = 1.0 + 0.15 * freqW * freqQuality
                         * Math.Log(Math.Max(1.0, returns.Count / (double)minTradesPerFold));

        // RetentionW lerps deviation from 1.0; 0.2 floor on raw ratio, 0.0 floor after to prevent negative multiplier.
        double peakGain     = peak - 1.0;
        double retentionRaw = peakGain > 0.01
            ? Math.Max(0.2, (balance - 1.0) / peakGain)
            : 1.0;
        double retentionMult = Math.Max(0.0, retentionRaw * retentionW + (1.0 - retentionW));

        // PER-TRADE Sharpe/Sortino — NOT Simulator.SharpeRatio, which multiplies by sqrt(candleCount/288).
        // Passing trade count there would create a hidden second frequency bonus. Fitness has exactly one: freqBonus.
        double sharpe  = PerTradeSharpe(returns);
        double calmar  = Simulator.CalmarRatio(returns);
        double pfStat  = Simulator.ProfitFactor(returns);
        double sortino = PerTradeSortino(returns);
        // Sample-size shrinkage: quality terms are blind to n (PF 7 on 30 trades = PF 7 on 3000).
        // Shrink each multiplier toward 1.0 by w = n/(n+k). Asymptotic, no cliff. No-op at QualityShrinkK=0.
        double shrinkW = cfg.QualityShrinkK <= 0.0
            ? 1.0
            : returns.Count / (returns.Count + cfg.QualityShrinkK);
        double Shrink(double mult) => 1.0 + (mult - 1.0) * shrinkW;

        double base_   = gain * 100.0 * gainW * wrMult * Shrink(qualityMult) * freqBonus / ddDiv * retentionMult;
        double score   = base_
            * Shrink(1.0 + cfg.SharpeW  * Math.Max(0, Math.Min(sharpe  / 3.0,  statBonusCeiling)))
            * Shrink(1.0 + cfg.CalmarW  * Math.Max(0, Math.Min(calmar  / 2.0,  statBonusCeiling)))
            * Shrink(1.0 + cfg.PfW      * Math.Max(0, Math.Min(pfStat - 1.0,   statBonusCeiling)))
            * Shrink(1.0 + cfg.SortinoW * Math.Max(0, Math.Min(sortino / 4.0,  statBonusCeiling)))
            * volWeight;

        score *= CVaRPenalty(returns, cfg);
        score *= TailRatioBonus(returns, cfg);

        return score;
    }

    // Cap every winning trade at the (1 - pct) quantile of the fold's OWN winning trades.
    //
    // WHY: `gain` is a sum, so a single +50% trade outranks a hundred +0.5% trades, and three
    // further terms used to pay again for the same shape (rrMult, Sortino, the p95/p5 tail bonus).
    // The finalist screen measures what that bought — 94-99% of the fold score lost when the best
    // 1% of trades is deleted, on every live genotype. A capped series cannot be bought with a
    // lottery ticket.
    //
    // ORDER AND LENGTH ARE PRESERVED. Canonical walks `returns` and `maePct` index-by-index, so a
    // transform that sorted or filtered would pair each trade's return with another trade's
    // excursion. This only lowers values in place.
    //
    // MONOTONE: min(r, cap) is non-decreasing in both r and cap, and cap is non-decreasing in every
    // return, so the whole vector is elementwise non-decreasing in its input. FitnessLandscapeTests'
    // gradient-bearing requirement therefore still holds.
    //
    // LOSSES ARE NEVER TOUCHED. Trimming both tails would flatter a strategy by deleting its worst
    // losses, which is the opposite of the question being asked.
    public static List<double> WinsorizeWinners(List<double> returns, double pct)
    {
        if (pct <= 0.0 || returns.Count == 0) return returns;

        var winners = returns.Where(r => r > 0).ToList();
        if (winners.Count < 2) return returns;      // a cap at the only winner is already a no-op

        winners.Sort();
        // Highest value kept. At pct=0.05 and 101 winners this is index 95, so the top 5 are capped.
        int idx = (int)Math.Floor((1.0 - pct) * (winners.Count - 1));
        double cap = winners[Math.Clamp(idx, 0, winners.Count - 1)];

        var outp = new List<double>(returns.Count);
        foreach (double r in returns) outp.Add(r > cap ? cap : r);
        return outp;
    }

    // Risk/reward leg: saturating hyperbola, strictly positive and increasing on (0, inf).
    // Replaced a linear ramp that was negative for rr<1 (collapsed the whole fold score to 0).
    // RrMultiplier(2.5) == 1.0 exactly (neutral); bounded above by MaxRrMult (1.6).
    public const double RrNeutral    = 2.5;   // rr at which the term is exactly 1.0 (no-op)
    public const double RrSaturation = 1.5;   // half-saturation constant of the hyperbola
    public const double MaxRrMult    = 1.0 + RrSaturation / RrNeutral;   // 1.6, the supremum

    public static double RrMultiplier(double rr)
    {
        // !(rr > 0) catches NaN too — NaN payoff ratio yields 0, not a NaN that poisons sort order.
        if (!(rr > 0.0)) return 0.0;
        if (double.IsPositiveInfinity(rr)) return MaxRrMult;
        return MaxRrMult * rr / (rr + RrSaturation);
    }

    // Per-trade Sharpe: mean/stdev of trade returns, no time normalisation.
    // Scale-free in trade count — two folds with the same distribution get the same number.
    // Guards: <5 returns → 0; ramped to zero as profit factor falls to 1.0.
    //
    // THIS WAS A CLIFF. The rule was `PF < 1.3 → 0`, inherited from Simulator.SharpeRatio. Since
    // Canonical exits early below PF 1.0, the live band starts at 1.0 — so the term was pinned at
    // zero across PF 1.0–1.3 and jumped at the boundary. That matters more than it looks: SharpeW
    // defaults to 0.5 while PfW and CalmarW default to 0.0, making this the LARGEST live
    // statistical weight in the fold score, and every strategy in the walk-forward book sits in
    // exactly that band (measured PF 1.00–1.05 per strategy). The heaviest stat term was therefore
    // contributing a constant, carrying no information about the genotypes actually being ranked.
    //
    // The ramp keeps the original intent — a barely-profitable fold should not collect a full
    // Sharpe bonus — without the discontinuity or the dead zone. Same shape as the tail-term ramp.
    public const double SharpePfRampLo = 1.0;   // no bonus at breakeven
    public const double SharpePfRampHi = 1.3;   // full bonus from here up (the old cliff edge)

    public static double PerTradeSharpe(List<double> returns)
    {
        if (returns.Count < 5) return 0;
        double grossProfit = returns.Where(r => r > 0).Sum();
        double grossLoss   = Math.Abs(returns.Where(r => r <= 0).Sum());
        if (grossLoss < 1e-10) return 0;

        double pf = grossProfit / grossLoss;
        double w  = Math.Clamp((pf - SharpePfRampLo) / (SharpePfRampHi - SharpePfRampLo), 0.0, 1.0);
        if (w <= 0.0) return 0;

        double mean = returns.Average();
        double std  = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Average());
        return std < 1e-10 ? 0 : w * mean / std;
    }

    // Per-trade Sortino: mean / downside-deviation, no time scaling. Guards match Simulator.SortinoRatio.
    public static double PerTradeSortino(List<double> returns)
    {
        if (returns.Count < 5) return 0;
        double mean       = returns.Average();
        var    negReturns = returns.Where(r => r < 0).ToList();
        if (negReturns.Count == 0) return mean > 0 ? 99.99 : 0;
        double downStd = Math.Sqrt(negReturns.Select(r => r * r).Average());
        return downStd < 1e-10 ? 0 : Math.Min(mean / downStd, 999.99);
    }

    // Tail terms read order statistics from the 5% buckets. Below 100 returns the bucket holds <5 observations
    // and the estimators degenerate. Both terms ramp toward neutral 1.0 as sample thins (no hard cliff).
    public const int MinTailSampleSize = 100;

    // Ramp: w(n) = clamp((n - TailRampLo) / (TailRampHi - TailRampLo), 0, 1).
    // TailRampLo=40: bucket holds 2 obs (below: 1 obs, degenerate). TailRampHi=100: bucket holds 5 obs (fully live).
    public const int TailRampLo = 40;
    public const int TailRampHi = MinTailSampleSize;

    // Tail term blend weight: 0 = fully neutral, 1 = fully live. Monotone non-decreasing in n.
    public static double TailTermWeight(int n)
        => Math.Clamp((n - (double)TailRampLo) / (TailRampHi - TailRampLo), 0.0, 1.0);

    // CVaR penalty: StatisticalTests.CVaR returns SIGNED % (more negative = worse).
    // Penalty grows as cvar5 falls below -3%, saturates at -6%. Floored at 0.5 (never zero/negative).
    public static double CVaRPenalty(List<double> returns, FitnessConfig cfg)
    {
        // Tail weight is 0 below 40 trades, so CVaR is never read when its bucket holds a single observation.
        double w = TailTermWeight(returns.Count);
        if (cfg.CVaRW <= 0 || w <= 0.0) return 1.0;
        double cvar5 = StatisticalTests.CVaR(returns, 0.05);

        const double tolerancePct = -3.0; // no penalty for tails shallower than this
        const double scalePct     =  3.0; // excess loss (pct) at which the penalty saturates

        if (cvar5 >= tolerancePct) return 1.0;
        double excess = Math.Min((tolerancePct - cvar5) / scalePct, 1.0); // 0..1
        double raw    = Math.Clamp(1.0 - excess * cfg.CVaRW, 0.5, 1.0);
        // Blend toward neutral 1.0; raw∈[0.5,1.0] and w∈[0,1], so result stays in [0.5,1.0].
        return 1.0 + w * (raw - 1.0);
    }

    // Right/left tail asymmetry bonus: |p95|/|p5|. Gated at MinTailSampleSize; ratio clamped at MaxTailRatio (5).
    // Multiplier clamped at MaxTailRatioBonus (1.5) so one outlier cannot dominate the score.
    public const double MaxTailRatio      = 5.0;
    public const double MaxTailRatioBonus = 1.5;

    public static double TailRatioBonus(List<double> returns, FitnessConfig cfg)
    {
        double w = TailTermWeight(returns.Count);
        if (cfg.TailRatioW <= 0 || w <= 0.0) return 1.0;
        var sorted = returns.OrderBy(r => r).ToList();
        int n = sorted.Count;
        int tailN = Math.Max(1, n / 20);
        double p5  = sorted[tailN - 1];
        double p95 = sorted[n - tailN];
        double absP5  = Math.Abs(p5);
        double absP95 = Math.Abs(p95);
        if (absP5 < 1e-10) return 1.0;
        double ratio = Math.Min(absP95 / absP5, MaxTailRatio);
        if (ratio <= 1.5) return 1.0;
        double bonus = 1.0 + (ratio - 1.5) * 0.1 * cfg.TailRatioW;
        double raw   = Math.Clamp(bonus, 1.0, MaxTailRatioBonus);
        // Same ramp as CVaRPenalty; raw∈[1.0, MaxTailRatioBonus] and w∈[0,1], so blend stays in range.
        return 1.0 + w * (raw - 1.0);
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
    // NOTE ON INDEX SPACES: Compute*FoldBoundaries return bounds in the INDEX SPACE OF THE ARRAY
    // THEY WERE COMPUTED FROM. Do NOT share across coins — index i is a different date on every coin.
    // Cross-coin folds go through ComputeRegimeAwareFoldWindows + RangeForWindow instead.
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

    // Regime-aware walk-forward folds as [Start, End) TIME windows. Same balancing as the index version,
    // but translated to timestamps so each coin maps the window onto its own array. Fold 0 opens at MinValue,
    // final fold closes at MaxValue (so trailing history is scored). Empty folds → (MaxValue, MaxValue).
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

    // Maps a [Start, End) time window onto a coin's candle array via binary search. Empty range if no candles in window.
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

    // Per-coin percentage folds (fallback when no BTC regime series). Each coin slices its own array;
    // embargo gap between folds.
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

    // ── Fold aggregation ─────────────────────────────────────────────────────────
    // fitness = λ·CVaR_α({s_f}) + (1−λ)·mean({s_f})  over ALL attempted folds.
    // CVaR_α = mean of worst ceil(α·K) folds (α=0.4). λ runs 0.4→0.8 as sample thins.
    // Monotone by construction: mean and CVaR are both non-decreasing in every fold score.
    // Fold vector is CONSTANT LENGTH: thin folds enter at ThinFoldScore (not dropped),
    // so "withdraw from a fold" is not a move in the search space.
    // Replaced `mean − stdMult·std` which was non-monotone (improving a fold could lower fitness).
    public const double DeadFoldFitness = -1000.0;

    // Thin-fold floor: substituted for attempted folds under MinTradesPerFold.
    // Must sit BELOW -2.0 (Canonical's worst output: pf=0 → pf-2.0 = -2.0), or "produce no trades" beats "trade and lose".
    // Must not be so low it dominates: -5.0 shades ranking without erasing gradient on surviving folds.
    public const double ThinFoldScore = -5.0;

    // Tail fraction over FOLD SCORES (not trade returns).
    public const double CVaRFoldAlpha = 0.4;
    // λ endpoints: thick sample → mean; thin sample → worst fold.
    public const double LambdaThick = 0.4;
    public const double LambdaThin  = 0.8;

    public static double AggregateFoldScores(
        IReadOnlyList<double> foldScores,
        IReadOnlyList<int> foldTradeCounts,
        int d,
        int attemptedFolds)
    {
        int k = foldScores.Count;

        // Caller cannot shrink vector below folds actually scored.
        int attempted = Math.Max(attemptedFolds, k);
        if (attempted == 0) return DeadFoldFitness;

        // Constant-length vector: scored folds + ThinFoldScore for each attempted fold under MinTradesPerFold.
        var scores = new double[attempted];
        var counts = new int[attempted];
        for (int i = 0; i < k; i++)
        {
            scores[i] = foldScores[i];
            counts[i] = i < foldTradeCounts.Count ? foldTradeCounts[i] : 0;
        }
        for (int i = k; i < attempted; i++)
        {
            scores[i] = ThinFoldScore;
            counts[i] = 0;   // thin fold genuinely thins the sample
        }

        double sum = 0.0;
        for (int i = 0; i < attempted; i++) sum += scores[i];
        double mean = sum / attempted;
        double cvar = WorstFoldMean(scores, CVaRFoldAlpha);

        // λ depends on trade COUNTS only, never fold scores — monotonicity preserved.
        double lambda = FoldLambda(counts, d);

        return lambda * cvar + (1.0 - lambda) * mean;
    }

    // Mean of worst ceil(alpha*k) fold scores (CVaR over fold distribution). Non-decreasing in every element.
    internal static double WorstFoldMean(IReadOnlyList<double> foldScores, double alpha)
    {
        int k = foldScores.Count;
        if (k == 0) return 0.0;
        int m = Math.Clamp((int)Math.Ceiling(alpha * k), 1, k);
        // Array.Sort on a copy (faster than OrderBy; called millions of times in monotonicity sweeps).
        var sorted = new double[k];
        for (int i = 0; i < k; i++) sorted[i] = foldScores[i];
        Array.Sort(sorted);
        double sum = 0;
        for (int i = 0; i < m; i++) sum += sorted[i];
        return sum / m;
    }

    // λ blend weight: thinness = clamp(7.5/max(1, avgN/d), 0.75, 2.0), remapped to [LambdaThick, LambdaThin].
    // Depends only on trade COUNTS, never fold scores.
    internal static double FoldLambda(IReadOnlyList<int> foldTradeCounts, int d)
    {
        double avgN     = foldTradeCounts.Count > 0 ? foldTradeCounts.Average() : 0.0;
        double thinness = Math.Clamp(7.5 / Math.Max(1.0, avgN / Math.Max(1, d)), 0.75, 2.0);
        return LambdaThick + (thinness - 0.75) / (2.0 - 0.75) * (LambdaThin - LambdaThick);
    }

    // ── Grid-family fitness shape ────────────────────────────────────────────────
    // Config transform (not a second formula) so Grid family uses Canonical like every other strategy.
    // WrW/6: session win rate is structurally high; DdPenalty×2: grid tail risk is whole-ladder stop-out;
    // QualityW=0: calibration choice (RrNeutral=2.5 is unreachable for grids); FreqW×0.5: half weight
    // since coin volatility also drives session count. Multiplicative so fitness_config.json still has effect.
    // NOTE: GridGA scores per-SESSION, portfolio consumes per-FILL — unit mismatch not yet fixed.
    public static FitnessConfig GridShape(FitnessConfig cfg) => cfg with
    {
        WrW       = cfg.WrW / 6.0,
        FreqW     = cfg.FreqW * 0.5,
        QualityW  = 0.0,
        DdPenalty = cfg.DdPenalty * 2.0,
    };

    // ── FadeShort fitness shape ──────────────────────────────────────────────────
    // FreqW=0: `gain` already rewards volume linearly; freqBonus was a second (weaker) payment.
    // WrW×2: once volume is no longer double-rewarded, accuracy is what the GA can buy.
    public static FitnessConfig FadeShortShape(FitnessConfig cfg) => cfg with
    {
        FreqW = 0.0,
        WrW   = cfg.WrW * 2.0,
    };

    public static double CanonicalRegime(
        List<(double Return, int RegimeBars)> returns,
        double posFrac,
        int sustainedBars,
        int minTradesPerFold,
        FitnessConfig cfg,
        double volWeight = 1.0,
        double statBonusCeiling = 1.0,
        // Per-trade maximum adverse excursion, in percent, NEGATIVE or zero, one entry per return.
        // OPT-IN: null (or a mismatched length, which is a wiring mistake) leaves the drawdown term
        // exactly as it was, so a strategy whose simulator does not yet report excursions is scored
        // bit-for-bit as before. Without this the fold score books each trade whole and a trade that
        // sank 30% before closing at +1% is indistinguishable from a smooth ride to +1% — which is
        // how a Grid retrain tripled MaxHoldCandles (53 -> 164) for a fold score that could not
        // register the added time at risk.
        IReadOnlyList<double>? maePct = null)
    {
        // The regime filter drops trades, so the excursion list has to be filtered by the SAME
        // predicate or the two fall out of alignment and Canonical silently discards a
        // mismatched-length list. Index-based rather than Where(), for exactly that reason.
        var mae = maePct is { } src && src.Count == returns.Count ? new List<double>(returns.Count) : null;
        var valid = new List<double>(returns.Count);
        for (int i = 0; i < returns.Count; i++)
        {
            if (returns[i].RegimeBars < sustainedBars) continue;
            valid.Add(returns[i].Return);
            mae?.Add(maePct![i]);
        }
        return Canonical(valid, posFrac, minTradesPerFold, cfg, volWeight, statBonusCeiling, mae);
    }

    public static double CanonicalRegimeStratified(
        List<(double Return, int RegimeBars, MarketRegime Regime)> returns,
        double posFrac,
        int sustainedBars,
        int minTradesPerFold,
        FitnessConfig cfg,
        double volWeight = 1.0,
        double statBonusCeiling = 1.0,
        // Per-trade maximum adverse excursion, in percent, NEGATIVE or zero, one entry per return.
        // OPT-IN: null (or a mismatched length, which is a wiring mistake) leaves the drawdown term
        // exactly as it was, so a strategy whose simulator does not yet report excursions is scored
        // bit-for-bit as before. Without this the fold score books each trade whole and a trade that
        // sank 30% before closing at +1% is indistinguishable from a smooth ride to +1% — which is
        // how a Grid retrain tripled MaxHoldCandles (53 -> 164) for a fold score that could not
        // register the added time at risk.
        IReadOnlyList<double>? maePct = null)
    {
        var mae = maePct is { } src && src.Count == returns.Count ? new List<double>(returns.Count) : null;
        var valid = new List<(double Return, int RegimeBars, MarketRegime Regime)>(returns.Count);
        for (int i = 0; i < returns.Count; i++)
        {
            if (returns[i].RegimeBars < sustainedBars) continue;
            valid.Add(returns[i]);
            mae?.Add(maePct![i]);
        }
        if (valid.Count < minTradesPerFold) return -1.0;

        var plainReturns = valid.Select(r => r.Return).ToList();
        double baseScore = Canonical(plainReturns, posFrac, minTradesPerFold, cfg, volWeight, statBonusCeiling, mae);
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
