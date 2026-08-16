namespace TradingGA;

// Tail-risk and utility-based sizing. Two lenses, both per-strategy over that strategy's return
// series, both producing a `strategy → size multiplier` consumed by the portfolio's strategyWeight hook.
//
//   1. VaR / Expected-Shortfall-constrained sizing. A strategy's size is limited so its 1-day-style
//      tail loss (VnR p-th percentile of the per-trade return, or its mean shortfall ES_p) stays
//      within a capital budget. This is the "size so worst-case loss ≤ X% of equity" principle — the
//      same risk-budget idea as DynamicExposureCap but applied per-strategy from the actual trade
//      return distribution rather than from a ATR-ratio proxy.
//
//   2. CRRA (constant relative risk aversion) utility-maximizing balance fraction. Given the return
//      distribution r and a fractional Kelly base, the Kelly criterion maximises expected log-wealth.
//      CRRA with gamma > 1 is a tail-averse generalisation: it maximises E[W^(1-γ)/(1-γ)] and returns
//      a SMALLER fraction than Kelly, shrinking as γ grows. This is the standard "how much to bet when
//      a single data point can ruin you" formalism (Kelly is gamma→1).
//
// Both are built from the empirical strategy return series — no parametric distributional assumption,
// which matters for the fat left tails crypto actually produces.
public static class VaRSizing
{
    // ── 1. VaR / ES percentile basis ──────────────────────────────────────────────

    // p-th percentile (loss tail): returns the value such that p of returns fall at/below it.
    // For tail losses use p=0.05 (VaR95) — the value is typically negative.
    public static double Percentile(IReadOnlyList<double> returns, double p)
    {
        if (returns.Count == 0) return 0;
        var sorted = returns.OrderBy(x => x).ToArray();
        int idx = Math.Clamp((int)(p * sorted.Length), 0, sorted.Length - 1);
        return sorted[idx];
    }

    // Expected Shortfall at level p: mean of the worst p-fraction of returns (tail average).
    public static double ExpectedShortfall(IReadOnlyList<double> returns, double p = 0.05)
    {
        if (returns.Count == 0) return 0;
        var sorted = returns.OrderBy(x => x).ToArray();
        int n = Math.Max(1, (int)(p * sorted.Length));
        return sorted.Take(n).Average();
    }

    // VaR/ES-constrained strategy multiplier. Budget is expressed as a fraction of equity that the
    // strategy's tail loss is allowed to consume (e.g. 0.02 = 2%). The base size multiplier is set so
    //  |tailPct| × multiplier ≤ budget. Uses ES when useExpectedShortfall, else the raw p-percentile.
    public record Constrained(double Multiplier, double TailPct);

    public static Constrained Constrain(
        IReadOnlyList<double> returns,
        double budgetPct = 2.0,        // 2 PERCENT-POINTS of equity max tail hit for one strategy
                                       // (tail is measured in percent points too; see below)
        double p = 0.05,
        bool useExpectedShortfall = true,
        double maxMultiplier = 10.0)   // don't let a tactic with near-zero tail blow up sizing
    {
        if (returns.Count < 5) return new(1.0, 0);
        double tail = useExpectedShortfall
            ? ExpectedShortfall(returns, p)
            : Percentile(returns, p);
        // tail is in PERCENT POINTS (a -6.0 means a -6% tail), matching budgetPct in percent points,
        // so budget/tail is a clean dimensionless multiplier. (A budget fraction treated against a
        // percent-point tail would under-size by exactly 100× — the original bug.)
        double mult = tail < -1e-12
            ? Math.Min(budgetPct / Math.Abs(tail), maxMultiplier)
            : maxMultiplier;
        return new(mult, tail);
    }

    // ── 2. CRRA utility maximization ──────────────────────────────────────────────

    // g  : return vector (fraction), e.g. trade returns / 100.
    // γ  : coefficient of relative risk aversion. γ=1 → Kelly (log utility).
    // f  : fraction of capital allocated. Maximises E[U] over f via grid search (robust to
    //      non-concavity / multi-modal empirical distributions).
    // Returns the optimal fraction in [0, 1].
    public static double CrraOptimalFraction(IReadOnlyList<double> returns, double gamma, int grid = 2000)
    {
        if (returns.Count < 5) return 0;
        var r = returns.Select(x => x / 100.0).ToArray();   // per-trade pct → fraction
        double bestF = 0, bestU = double.NegativeInfinity;

        for (int i = 0; i <= grid; i++)
        {
            double f = (double)i / grid;   // 0..1
            if (f < 1e-6) continue;
            double u = 0;
            for (int j = 0; j < r.Length; j++)
            {
                double wealth = 1.0 + f * r[j];
                if (wealth <= 1e-9) { u = double.NegativeInfinity; break; }
                if (Math.Abs(gamma - 1.0) < 1e-9) u += Math.Log(wealth);
                else u += Math.Pow(wealth, 1.0 - gamma) / (1.0 - gamma);
            }
            if (u > bestU) { bestU = u; bestF = f; }
        }
        return bestF;
    }

    // Convenience: CRRA-based strategy multiplier. Converts the optimal fraction to a multiplier
    // relative to a reference full-size fraction (default 0.05). mult = optFrac / refFrac, capped.
    public static double CrraMultiplier(IReadOnlyList<double> returns, double gamma = 4.0, double refFrac = 0.05)
    {
        double optFrac = CrraOptimalFraction(returns, gamma);
        if (refFrac <= 1e-9) return 1.0;
        return Math.Clamp(optFrac / refFrac, 0.05, 10.0);
    }

    // One-stop: build a `strategy → size multiplier` map from strategy-tagged return series using a
    // chosen rule ("var" | "es" | "crra"); used by the combinedbacktest strategyWeight hook.
    public static Func<string, double>? BuildWeights(
        IReadOnlyList<(string Strategy, double Return)> trades,
        string rule,
        double gamma = 4.0,
        double budgetPct = 0.02,
        double refFrac = 0.05)
    {
        var byStrat = trades
            .GroupBy(t => t.Strategy, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(t => t.Return).ToArray());

        var weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in byStrat)
        {
            double mult = rule.ToLowerInvariant() switch
            {
                "var"  => Constrain(kv.Value, budgetPct, 0.05, useExpectedShortfall: false).Multiplier,
                "es"   => Constrain(kv.Value, budgetPct, 0.05, useExpectedShortfall: true).Multiplier,
                "crra" => CrraMultiplier(kv.Value, gamma, refFrac),
                _      => 1.0,
            };
            weights[kv.Key] = mult;
        }
        return weights.Count > 0
            ? s => weights.TryGetValue(s, out var w) ? w : 1.0
            : null;
    }
}