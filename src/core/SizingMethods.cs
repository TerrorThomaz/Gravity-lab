namespace TradingGA;

// Position sizing methods beyond the hardcoded half-Kelly. Professional funds adjust their sizing
// fraction based on estimation uncertainty (sample size, CI width), volatility targets, and regime
// confidence instead of using a fixed multiplier like 0.5× Kelly.
//
// These are pure functions — no state, no API calls. They feed into the simulator's sizing path
// via Config.KellyMultiplier overrides or CovarianceSizing weight adjustments.
public static class SizingMethods
{
    // ── 1. Fractional Kelly adjusted by estimation uncertainty ────────────────────
    //
    // Standard quant practice: Kelly fraction is reduced when estimated from few trades because
    // point estimates have high variance. This implements the sample-size-adjusted fractional Kelly
    // from "Kelly Criterion and Estimation Risk" (MacLean et al.):
    //
    //   k_adj = k_full × min(1, √(n / (n + d)))
    //
    // where d ≈ number of free parameters in the estimator (default 3: win rate, payoff ratio,
    // plus trend slope). With n → ∞, k_adj → k_full. With n = d, k_adj ≈ k_full / √2.
    public record FractionalResult(double FullKelly, double AdjustedKelly, double Fraction, int N);

    public static FractionalResult Compute(int nTrades, double fullKelly)
    {
        if (fullKelly <= 0 || nTrades < 2) return new(fullKelly, 0, 0, nTrades);

        // Degrees-of-freedom penalty: each strategy has ~3 tunable parameters (win rate, payoff
        // ratio, and trend/momentum slope). More complex strategies (grid, accumgrid) have more.
        const int dof = 3;
        double shrinkage = Math.Sqrt(Math.Min(1.0, nTrades / (double)(nTrades + dof)));

        return new(fullKelly, fullKelly * shrinkage, shrinkage, nTrades);
    }

    // Convenience overload: compute from raw return series (estimates Kelly internally).
    public static FractionalResult FromReturns(IReadOnlyList<double> returns)
    {
        var (k, _) = StrategyStats.KellyFraction(returns.ToList());
        return Compute(returns.Count, k);
    }

    // ── 2. Volatility-targeted sizing ─────────────────────────────────────────────
    //
    // Size_i = target_vol_pct / est_vol_i, capped at max_size_pct.
    // Normalizes positions to equal vol contribution regardless of individual coin volatility.
    // Standard practice in systematic macro and CTAs.
    public record VolTargetResult(double RawSize, double CappedSize, double EstVolPct);

    public static VolTargetResult Compute(
        double estVolPct,       // per-position estimated vol (e.g., ATR%)
        double targetVolPct,    // desired vol contribution per position (default 1.5% h1)
        double maxFracPct,      // hard cap (e.g., 5% per position)
        double fallbackFrac = 0.05)
    {
        if (estVolPct < 1e-9) return new(0, 0, 0);
        double raw = targetVolPct / estVolPct;
        double capped = Math.Min(raw, maxFracPct);
        return new(raw, capped, estVolPct);
    }

    // Convenience: compute from return series std dev.
    public static VolTargetResult FromReturns(
        IReadOnlyList<double> returns,
        double targetVolPct = 1.5,
        double maxFracPct = 0.05)
    {
        double vol = StdDev(returns.Select(r => r).ToArray());
        return Compute(vol, targetVolPct, maxFracPct);
    }

    // ── 3. Regime-modulated sizing ────────────────────────────────────────────────
    //
    // Modulates Kelly-based size by regime confidence and regime type. High-confidence Bull/Bear
    // → full size. Ranging → reduced. Transition zones (low conf) → further reduced.
    // Also penalizes HighVol regime where being wrong costs most (DynamicGuard territory).
    public record RegimeModulation(
        double ModMult,      // multiplier ∈ [0, 1] applied to base Kelly size
        MarketRegime Regime,
        double Confidence);

    public static RegimeModulation Compute(
        MarketRegime regime,
        double confidence,
        bool isTransitionZone = false)
    {
        double mult = 1.0;

        switch (regime)
        {
            case MarketRegime.Bull:
            case MarketRegime.Bear:
                // Linear ramp: low conf → reduced size even in directional regimes.
                mult = Math.Clamp(confidence, 0.3, 1.0);
                break;

            case MarketRegime.Ranging:
                // Grid-family strategies thrive here; directional bets don't. Reduce.
                mult = confidence > 0.6 ? 0.7 : 0.4;
                break;

            case MarketRegime.HighVol:
                // Directional bets unreliable; always reduce.
                mult = 0.2;
                break;
        }

        // Additional penalty in transition zones (low-duration regime switches).
        if (isTransitionZone) mult *= 0.7;

        return new(Math.Clamp(mult, 0.0, 1.0), regime, confidence);
    }

    // Convenience: combine fractional Kelly + regime modulation → final effective fraction.
    public static double EffectiveFraction(
        double fullKelly,
        int nTrades,
        MarketRegime regime,
        double confidence,
        bool isTransitionZone = false,
        double estVolPct = 0,
        double targetVolPct = 1.5,
        double maxFracPct = 0.05)
    {
        // Layer 1: sample-size-adjusted fractional Kelly.
        var frac = Compute(nTrades, fullKelly);

        // Layer 2: regime modulation.
        var reg = Compute(regime, confidence, isTransitionZone);

        // Layer 3: volatility targeting (if vol is provided).
        double volSize = 1.0;
        if (estVolPct > 1e-9)
        {
            var vt = Compute(estVolPct, targetVolPct, maxFracPct);
            volSize = vt.CappedSize / maxFracPct; // normalize to [0,1] relative to cap
        }

        // Combined: multiplicative layers. All three must agree for full size.
        return frac.AdjustedKelly * reg.ModMult * Math.Max(0.01, volSize);
    }

    // ── 4. Volatility-targeted portfolio sizing (per-strategy) ──────────────────────
    //
    // The portfolio sim sizes each trade as `min(conf*kelly, maxPositionFrac)` — that is Kelly-derived,
    // so a high-vol coin gets the SAME fraction as a low-vol coin, giving it a larger realized vol
    // contribution. Standard house practice normalizes by estimated vol: size_strategy ∝ targetVol /
    // estVol_strategy. This returns a `strategy → multiplier` map consumed by the simulator's
    // `strategyWeight` hook. NOT mean-normalised: the multiplication changes gross exposure on the
    // high/low-vol axis, and the exposure cap still binds the total. Set mean-normalise=true to
    // preserve gross exposure (a pure redistribution like CovarianceSizing).
    public static Func<string, double>? VolTargetStrategyWeights(
        IReadOnlyList<(string Strategy, double Return)> trades,
        double targetVolPct = 1.5,
        double volFloorPct = 0.5,   // clamp so a super-quiet strategy can't blow up sizing
        double volCapPct = 8.0,     // clamp so a volatile strategy can't go to ~0
        bool meanNormalise = false)
    {
        var byStrat = trades
            .GroupBy(t => t.Strategy)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Return).ToArray());

        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in byStrat)
        {
            double vol = StdDev(kv.Value);
            double estVol = Math.Clamp(vol, volFloorPct, volCapPct);
            weights[kv.Key] = targetVolPct / estVol;
        }
        if (weights.Count == 0) return null;

        if (meanNormalise)
        {
            double mean = weights.Values.Average();
            if (mean > 1e-12)
                foreach (var k in weights.Keys.ToList()) weights[k] /= mean;
        }

        return s => weights.TryGetValue(s, out var w) ? w : 1.0;
    }

    // ── 5. Blended sizing (return-shape × risk-cap) ───────────────────────────────
    //
    // The pure modes trade along a single axis: risk-parity/VaR chase return (higher DD), ES/vol
    // chase drawdown (lower return). A blend mixes the two weight maps per-strategy so the book keeps
    // the *shape* that produces return on the strategies where the edge is, while the risk weights
    // *cap* the strategies whose tail would add drawdown. This is the "risk overlay on top of an
    // allocation" pattern used by real multi-strategy books.
    //
    // Geometric blend (not arithmetic): w_mix(s) = w_ret(s)^α · w_risk(s)^(1−α), α ∈ [0,1].
    //   α → 1  : pure return-shape (risk-parity/VaR weights dominate) → high return, higher DD
    //   α → 0  : pure risk-cap (ES/vol weights dominate)             → low DD, lower return
    // Geometric is used because both maps are positive multiplicative factors on the Kelly base;
    // arithmetic would let a zero value in one map entirely zero a strategy. Choosing the result of
    // ^(radical products) for each strategy, then mean-normalising to ~1 so gross exposure stays on
    // the same scale as the pure modes for a fair comparison.
    public static Func<string, double>? BlendWeights(
        Func<string, double> retShape,      // return-oriented weight (risk-parity, var)
        Func<string, double> riskCap,       // risk-oriented weight (es, voltarget)
        IReadOnlyList<string> strategies,
        double alpha,
        bool meanNormalise = true)
    {
        var w = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in strategies)
        {
            double r = Math.Max(1e-6, retShape(s));
            double k = Math.Max(1e-6, riskCap(s));
            w[s] = Math.Pow(r, alpha) * Math.Pow(k, 1.0 - alpha);
        }
        if (w.Count == 0) return null;
        if (meanNormalise)
        {
            double mean = w.Values.Average();
            if (mean > 1e-12)
                foreach (var sn in w.Keys.ToList()) w[sn] /= mean;
        }
        return s => w.TryGetValue(s, out var v) ? v : 1.0;
    }

    // Evaluate a sizing weight map on a trade list through the exposure-capped portfolio sim, and
    // return (returnPct, maxDDPct). Used by the α-sweep to score each blend on the *actual* portfolio
    // outcome (not a proxy).
    public static (double ReturnPct, double MaxDDPct, double EndBalance) BacktestWithWeights(
        List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration, string Strategy)> trades,
        Func<string, double>? weight,
        double maxTotalExposurePct,
        double maxPositionFrac = 0.05)
    {
        var r = Simulator.SimulatePortfolioExposureCapped(
            trades, maxTotalExposurePct, maxPositionFrac: maxPositionFrac, strategyWeight: weight);
        return ((r.EndBalance - r.StartBalance) / r.StartBalance * 100.0, r.MaxDrawdownPct, r.EndBalance);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────
    private static double StdDev(double[] x)
    {
        if (x.Length < 2) return 0;
        double m = x.Average(), s = 0;
        foreach (var v in x) { double d = v - m; s += d * d; }
        return Math.Sqrt(s / (x.Length - 1));
    }
}