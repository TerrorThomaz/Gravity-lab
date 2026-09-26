namespace TradingGA;

// POST-GA FINALIST SCREEN — two cheap tests that separate a genotype with a real basin from the
// best of N draws from noise.
//
// WHY THIS EXISTS. A GA returns the argmax of a search over tens of thousands of candidates. The
// Minimum Backtest Length bound (Bailey, Borwein, López de Prado & Zhu) says MinBTL < 2·ln(N)/
// E[max SR]²; inverted for ~3.2 years of data at E[max SR]=1 the budget is about FIVE independent
// configurations. genotypes/ga_trials.json records 116,461 evaluations. GA trials are correlated so
// the effective count is far below the raw one — but it is not five, and the argmax of that search
// is by default the best of many draws from noise.
//
// Neither screen can establish that a genotype is good. Both can establish that one is fragile,
// which is the cheaper and more actionable direction.
//
// WHAT THEY DO NOT FIX: the fitness function is itself an unlogged multiple-testing surface, and so
// is the choice of window, sizing scheme and roster. Walk-forward closes parameter circularity; it
// does nothing for design circularity. See docs/RIGOR_REWORK_2026-09.md §6.
public static class FinalistScreen
{
    // THE SCREEN MUST NOT GRADE ITS OWN HOMEWORK.
    //
    // FitnessConfig.WinsorizeWinnerPct caps winning trades before scoring, precisely so the GA
    // cannot buy fitness with a lottery ticket. Measuring outlier sensitivity through that same cap
    // would be circular: the transform flattens the top of the distribution, so deleting the top 1%
    // would necessarily look harmless and the screen would report health it did not verify.
    //
    // The screen therefore always scores on the RAW series. A genotype selected under winsorization
    // still has to show, on uncapped returns, that its edge does not live in a handful of trades.
    public static FitnessConfig ScreenCfg(FitnessConfig cfg) => cfg with { WinsorizeWinnerPct = 0.0 };

    // ── 1. Outlier sensitivity ───────────────────────────────────────────────────────────────
    //
    // Falck, Rej & Thesmar (CFM, arXiv:2105.01380) recoded 72 published equity strategies and found
    // that the sensitivity of in-sample performance to a handful of best trades is an EX-ANTE
    // predictor of out-of-sample Sharpe decay, adding ~15% of explanatory power on top of
    // publication year. A genotype whose edge lives in a few trades has no edge.
    //
    // Only WINNERS are deleted. Trimming both tails would flatter a strategy by removing its worst
    // losses, which is the opposite of the question being asked.
    public readonly record struct Outlier(
        double ScoreFull,
        double ScoreNoTop1Pct,
        double ScoreNoTop5Pct,
        double DropTop1Pct,      // fraction of the score lost, so higher = more fragile
        double DropTop5Pct);

    public static Outlier OutlierSensitivity(List<double> returns, Func<List<double>, double> score)
    {
        double full = score(returns);

        static List<double> WithoutTop(List<double> r, double frac)
        {
            int drop = (int)Math.Ceiling(r.Count * frac);
            if (drop <= 0 || drop >= r.Count) return new List<double>(r);
            // Descending by return, skip the best `drop`. Losers are never removed.
            return r.OrderByDescending(x => x).Skip(drop).ToList();
        }

        double no1 = score(WithoutTop(returns, 0.01));
        double no5 = score(WithoutTop(returns, 0.05));

        // Guard the denominator: a non-positive base score makes the ratio meaningless, so report
        // a full drop rather than a spurious number.
        double Drop(double reduced) => Math.Abs(full) < 1e-12 ? 1.0 : (full - reduced) / Math.Abs(full);

        return new Outlier(full, no1, no5, Drop(no1), Drop(no5));
    }

    // ── 2. Perturbed fitness ─────────────────────────────────────────────────────────────────
    //
    // A genotype whose neighbours in parameter space all score similarly sits on a plateau; a lone
    // peak is noise. Overfitting produces sharp optima precisely because it is fitting individual
    // trades, so the shape of the neighbourhood is diagnostic in a way the peak value is not.
    //
    // Rank finalists by the PERTURBED score rather than the peak, and read WorstRatio as well as
    // Ratio — a genotype whose median neighbour is healthy but whose nearby minimum collapses is
    // still sitting on a cliff edge, and the median alone hides that.
    public readonly record struct Robustness(
        double BaseFitness,
        double MedianPerturbed,
        double WorstPerturbed,
        double Ratio,        // median(perturbed) / base — ~1 is a plateau, near 0 is a spike
        double WorstRatio,
        int    Samples);

    // perturb: (genotype, rng, relativeMagnitude) -> a neighbour. For this repo's genotypes the
    // natural operator is the existing Mutate(rng, rate), which already respects gene bounds.
    public static Robustness PerturbedFitness<TG>(
        TG best,
        Func<TG, double> fitness,
        Func<TG, Random, double, TG> perturb,
        double[]? magnitudes = null,
        int samplesPerMagnitude = 12,
        int seed = 20260924)
    {
        magnitudes ??= new[] { 0.10, 0.20 };
        double baseFit = fitness(best);

        var rng = new Random(seed);
        var scores = new List<double>(magnitudes.Length * samplesPerMagnitude);
        foreach (double m in magnitudes)
            for (int i = 0; i < samplesPerMagnitude; i++)
                scores.Add(fitness(perturb(best, rng, m)));

        if (scores.Count == 0) return new Robustness(baseFit, baseFit, baseFit, 1.0, 1.0, 0);

        scores.Sort();
        double median = scores[scores.Count / 2];
        double worst  = scores[0];

        // Same denominator guard as above: a non-positive base makes the ratio uninterpretable.
        double R(double v) => Math.Abs(baseFit) < 1e-12 ? 0.0 : v / Math.Abs(baseFit);

        return new Robustness(baseFit, median, worst, R(median), R(worst), scores.Count);
    }

    // One line each, so a GA can print the screen without every caller reinventing the format.
    public static string Format(Robustness r) =>
        $"robustness: base={r.BaseFitness:F3} medianNeighbour={r.MedianPerturbed:F3} " +
        $"({r.Ratio:P0}) worstNeighbour={r.WorstPerturbed:F3} ({r.WorstRatio:P0}) n={r.Samples}" +
        (r.Ratio < 0.5 ? "   ⚠ SHARP PEAK — likely fitted to individual trades" : "");

    public static string Format(Outlier o) =>
        $"outlier sensitivity: full={o.ScoreFull:F3} noTop1%={o.ScoreNoTop1Pct:F3} ({o.DropTop1Pct:P0} lost) " +
        $"noTop5%={o.ScoreNoTop5Pct:F3} ({o.DropTop5Pct:P0} lost)" +
        (o.DropTop1Pct > 0.5 ? "   ⚠ EDGE CONCENTRATED IN A FEW TRADES" : "");
}
