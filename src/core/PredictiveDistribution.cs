namespace TradingGA;

// Conditioned estimate of the next-h1 forward-return distribution, built purely from a symbol's
// own history (no new data source). This is a research/backtest tool designed to answer one question:
//
//   "Does the shape of the predicted distribution separate later winners from losers, and convert
//    into better edge / risk / return than the unconditional baseline?"
//
// It is deliberately NOT wired into any live path or simulator. It exists to be tested against
// realised forward returns (see the `disttest` command and the unit tests in this repo).
//
// NO LOOKAHEAD GUARANTEE: prediction at bar q uses only bars [0..q-1] as candidates, so every
// matched forward return belongs to a bar strictly before q. Bar q's own forward return is the
// held-out reality used for calibration.
public static class PredictiveDistribution
{
    // Regime classification needs RegimeClassifier's warmup before signals are meaningful.
    public const int Warmup = RegimeClassifier.Warmup;

    // Matching parameters.
    public const double AtrBand       = 0.15;  // |atrRatio[cand] - atrRatio[query]| <= this
    public const int    MaxSample     = 250;   // cap on nearest retained matches (cost + focus)
    public const int    MinSamples    = 20;    // below this, "no context" (CVaR unreliable)
    public const int    LookbackBars  = 1500;  // candidate search window (cost control)

    // One bar's prediction + the realised forward return it was checked against.
    public readonly record struct BarPrediction(
        DateTime Time,
        MarketRegime Regime,
        double Confidence,
        double MeanPct,
        double StdPct,
        double ProbPositive,
        double CVaR5Pct,
        double RealizedPct);

    public readonly record struct Distribution(
        double MeanPct,
        double StdPct,
        double Skewness,
        double ExKurtosis,
        double ProbPositive,
        double ProbUp1Pct,
        double ProbDown1Pct,
        double CVaR5Pct,
        double KellyFrac,
        int SampleSize)
    {
        public static readonly Distribution Neutral = new(0, 0, 0, 0, 0.5, 0, 0, 0, 0, 0);
        public bool IsNeutral => SampleSize == 0;
    }

    // Single last-bar query. Returns Neutral when the query bar is in warmup or context is thin.
    public static Distribution Predict(Candle[] h1) => PredictLast(h1);

    // Single-bar query at an arbitrary historical time: the bar at-or-before `entryTime`. Used by the
    // trade-overlay path, which needs a prediction at each trade's ENTRY bar and would waste the whole
    // per-bar pass (≈1.5M bars across a 42-symbol universe) applying it where it is not needed.
    public static Distribution PredictAt(Candle[] h1, DateTime entryTime)
    {
        var h = Prepare(h1);
        return h.Query(entryTime);
    }

    // A prebuilt per-symbol context. Build once (O(n)), query many times (O(lookback) each). This is
    // what the trade overlay needs: ~2000 trades across 42 symbols with a single context per symbol
    // instead of rebuilding the indicator stack on every call.
    public sealed class Prepared
    {
        private readonly Candle[] _candles;
        private readonly Context _ctx;
        internal Prepared(Candle[] candles, Context ctx) { _candles = candles; _ctx = ctx; }

        public bool Usable => _candles.Length >= Warmup + MinSamples + 1;

        // Distribution at the bar at-or-before `at`. Neutral when before warmup or context is thin.
        public Distribution Query(DateTime at)
        {
            if (!Usable) return Distribution.Neutral;
            int q = -1;
            for (int i = _candles.Length - 1; i >= 0; i--)
                if (_candles[i].Time <= at) { q = i; break; }
            if (q < 0 || _ctx.RegimeSeries[q] < 0) return Distribution.Neutral;
            return EstimateAt(_ctx, _ctx.Features, _ctx.RegimeSeries, _ctx.ForwardPct, q);
        }

        // Multi-horizon predictions from the same query bar. Returns term structure of predicted
        // next-h, next-4h, next-8h, next-24h returns (h1 bars × horizon). Enables optimal entry/exit
        // timing by comparing predicted horizons — e.g., enter now if 1h pred > 0 but 4h pred < 0.
        public record HorizonPredictions(
            DateTime Time,
            MarketRegime Regime,
            double Confidence,
            Distribution H1,
            Distribution H4,
            Distribution H8,
            Distribution H24);

        public HorizonPredictions QueryHorizons(DateTime at)
        {
            if (!Usable) return new(at, MarketRegime.Ranging, 0, Distribution.Neutral, Distribution.Neutral, Distribution.Neutral, Distribution.Neutral);

            int q = -1;
            for (int i = _candles.Length - 1; i >= 0; i--)
                if (_candles[i].Time <= at) { q = i; break; }
            if (q < 0 || _ctx.RegimeSeries[q] < 0)
                return new(at, MarketRegime.Ranging, 0, Distribution.Neutral, Distribution.Neutral, Distribution.Neutral, Distribution.Neutral);

            var distH1 = EstimateAt(_ctx, _ctx.Features, _ctx.RegimeSeries, _ctx.ForwardPct, q);
            var distH4 = EstimateAtHorizon(_ctx, _candles, q, lookbackBars: 4 * LookbackBars);
            var distH8 = EstimateAtHorizon(_ctx, _candles, q, lookbackBars: 8 * LookbackBars);
            var distH24 = EstimateAtHorizon(_ctx, _candles, q, lookbackBars: 24 * LookbackBars);

            return new(at, (MarketRegime)_ctx.RegimeSeries[q], _ctx.Confidence[q], distH1, distH4, distH8, distH24);
        }
    }

    // Build the shared context for a symbol once.
    public static Prepared Prepare(Candle[] h1)
        => new(h1, BuildContext(h1));

    // Per-bar validated estimation: for every eligible bar q, predict next-bar forward return using
    // only data up to q, and record the realised forward return alongside. This is the dataset the
    // `disttest` command consumes for calibration + separation.
    public static List<BarPrediction> ComputeValidated(Candle[] h1)
    {
        var result = new List<BarPrediction>();
        if (h1.Length < Warmup + MinSamples + 1) return result;

        var ctx = BuildContext(h1);
        var feats = ctx.Features;          // [n][4] normalised
        int[] regime = ctx.RegimeSeries;    // -1 for warmup bars
        double[] fwd = ctx.ForwardPct;

        for (int q = Warmup; q < h1.Length - 1; q++)
        {
            if (regime[q] < 0) continue;
            double realized = (h1[q + 1].Close - h1[q].Close) / h1[q].Close * 100.0;

            var dist = EstimateAt(ctx, feats, regime, fwd, q);
            if (dist.IsNeutral) continue;

            result.Add(new BarPrediction(
                h1[q].Time,
                (MarketRegime)regime[q],
                ctx.Confidence[q],
                dist.MeanPct, dist.StdPct, dist.ProbPositive, dist.CVaR5Pct,
                realized));
        }
        return result;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────

    internal sealed class Context
    {
        public double[][]  Features = [];        // [n][4] normalised feature rows
        public int[]       RegimeSeries = [];    // (int)MarketRegime, or -1 for warmup bars
        public double[]    Confidence = [];
        public double[]    ForwardPct = [];      // fwd[q] valid for q < n-1
        public double[]    AtrRatio = [];        // raw ATR14/ATR100 per bar
    }

    private static Context BuildContext(Candle[] h1)
    {
        int n = h1.Length;
        var closes = CandleExt.Closes(h1);
        var highs  = CandleExt.Highs(h1);
        var lows   = CandleExt.Lows(h1);

        var ema50  = Trend.Ema(closes, 50);
        var atr14  = Volatility.Atr(highs, lows, closes, 14);
        var atr100 = Volatility.Atr(highs, lows, closes, 100);

        // Regime classifier emits per-bar regime + confidence the same way the router consumes it.
        // Use the O(n) ClassifySeriesWithDuration, NOT the O(n²) ClassifySeries — the latter re-slices
        // and re-classifies each prefix, which is catastrophically slow on the multi-symbol backtest
        // universe (42 coins × ~55k bars) that the trade overlay runs over.
        var regimeArr = RegimeClassifier.ClassifySeriesWithDuration(h1);
        var cols = new double[4][];
        for (int f = 0; f < 4; f++) cols[f] = new double[n];
        var feats = new double[n][];
        var atrRatioArr = new double[n];
        var regime = new int[n];
        var conf = new double[n];

        for (int i = 0; i < n; i++)
        {
            feats[i] = new double[4];
            int r = (int)regimeArr[i].Regime;
            regime[i] = i < Warmup ? -1 : r;
            conf[i] = regimeArr[i].Confidence;

            if (i >= Warmup)
            {
                double price   = closes[i];
                double e50     = ema50[i];
                double atrRatio = atr100[i] > 1e-10 ? atr14[i] / atr100[i] : 1.0;
                atrRatioArr[i]  = atrRatio;
                double e50Prev  = ema50[Math.Max(0, i - 20)];
                double slope50  = e50Prev > 1e-10 ? (e50 - e50Prev) / e50Prev : 0;
                double pPrev20  = closes[Math.Max(0, i - 20)];
                double mom20    = pPrev20 > 1e-10 ? (price - pPrev20) / pPrev20 : 0;
                double pv50     = e50 > 1e-10 ? (price - e50) / e50 : 0;

                cols[0][i] = pv50;
                cols[1][i] = slope50;
                cols[2][i] = atrRatio;
                cols[3][i] = mom20;
            }
        }

        var fwd = new double[n];
        for (int i = 0; i < n - 1; i++)
            fwd[i] = closes[i] > 1e-12 ? (closes[i + 1] - closes[i]) / closes[i] * 100.0 : 0.0;

        return new Context { Features = feats, RegimeSeries = regime,
                             Confidence = conf, ForwardPct = fwd, AtrRatio = atrRatioArr };
    }

    // Estimate the distribution at query bar q, using only data up to q-1 as candidate context.
    private static Distribution EstimateAt(Context ctx, double[][] feats, int[] regime, double[] fwd, int q)
    {
        int n = feats.Length;
        int candidateStart = Math.Max(Warmup, q - LookbackBars);
        double qAtr = ctx.AtrRatio[q];

        // First pass: bars in the same regime, within the ATR band, before q => valid candidates.
        // First pass: bars in the same regime, within the ATR band, before q => valid candidates.
        // Retain only the nearest MaxSample via a small running-selection. The affinity search is
        // dominated by the per-bar scan (FeatureDist over up to LookbackBars candidates); keeping the
        // retained set small bounds the finalised sort without changing which bars are selected.
        var keep  = new (double Dist, double Ret)[MaxSample];
        int nKept = 0;
        double worst = double.MinValue;   // FARTHEST distance among the retained set

        void Offer(double d, double ret)
        {
            if (nKept < MaxSample)
            {
                keep[nKept++] = (d, ret);
                if (d > worst) worst = d;
            }
            else if (d < worst)
            {
                // Replace the current farthest retained bar.
                int wi = 0;
                for (int z = 1; z < MaxSample; z++) if (keep[z].Dist > keep[wi].Dist) wi = z;
                keep[wi] = (d, ret);
                worst = double.MinValue;
                for (int z = 0; z < nKept; z++) if (keep[z].Dist > worst) worst = keep[z].Dist;
            }
        }

        for (int j = candidateStart; j < q; j++)
        {
            if (regime[j] < 0 || regime[j] != regime[q]) continue;
            if (Math.Abs(ctx.AtrRatio[j] - qAtr) > AtrBand) continue;
            Offer(FeatureDist(feats[q], feats[j]), fwd[j]);
        }

        if (nKept < MinSamples)
        {
            // Thin context: relax the ATR band (but keep regime) to recover a usable sample.
            nKept = 0; worst = double.MinValue;
            for (int j = candidateStart; j < q; j++)
            {
                if (regime[j] < 0 || regime[j] != regime[q]) continue;
                Offer(FeatureDist(feats[q], feats[j]), fwd[j]);
            }
        }

        if (nKept < MinSamples) return Distribution.Neutral;

        // Retain the nearest MaxSample by feature distance (most similar contexts win, not recency).
        System.Array.Sort(keep, 0, nKept, System.Collections.Generic.Comparer<(double, double)>.Default);
        var rets = new double[nKept];
        for (int k = 0; k < nKept; k++) rets[k] = keep[k].Ret;

        return FitMatch(rets);
    }

    // Multi-horizon variant: estimate next-h bars forward return (h = 4, 8, 24). Computes
    // returns over longer windows by aggregating consecutive h1-bar returns. Uses extended
    // candidate window proportional to horizon so there are enough historical matches.
    private static Distribution EstimateAtHorizon(Context ctx, Candle[] candles, int q, int lookbackBars)
    {
        int n = candles.Length;
        int candidateStart = Math.Max(Warmup, q - lookbackBars);
        double qAtr = ctx.AtrRatio[q];

        // Compute multi-bar-forward returns from each bar.
        var fwdH = new List<double>(n - q);
        for (int i = 0; i <= n - lookbackBars - 1 && i + lookbackBars < n; i++)
        {
            if (i >= q) break;
            int end = Math.Min(i + lookbackBars, n - 1);
            double startP = candles[i].Close;
            double endP = candles[end].Close;
            double ret = startP > 1e-12 ? (endP - startP) / startP * 100.0 : 0.0;
            fwdH.Add(ret);
        }

        var keep  = new (double Dist, double Ret)[MaxSample];
        int nKept = 0;
        double worst = double.MinValue;

        void Offer(double d, double ret)
        {
            if (nKept < MaxSample)
            {
                keep[nKept++] = (d, ret);
                if (d > worst) worst = d;
            }
            else if (d < worst)
            {
                int wi = 0;
                for (int z = 1; z < MaxSample; z++) if (keep[z].Dist > keep[wi].Dist) wi = z;
                keep[wi] = (d, ret);
                worst = double.MinValue;
                for (int z = 0; z < nKept; z++) if (keep[z].Dist > worst) worst = keep[z].Dist;
            }
        }

        for (int j = candidateStart; j < q; j++)
        {
            if (ctx.RegimeSeries[j] < 0 || ctx.RegimeSeries[j] != ctx.RegimeSeries[q]) continue;
            if (Math.Abs(ctx.AtrRatio[j] - qAtr) > AtrBand) continue;
            int idx = j;
            if (idx >= 0 && idx < fwdH.Count)
                Offer(FeatureDist(ctx.Features[q], ctx.Features[j]), fwdH[idx]);
        }

        if (nKept < MinSamples)
        {
            nKept = 0; worst = double.MinValue;
            for (int j = candidateStart; j < q; j++)
            {
                if (ctx.RegimeSeries[j] < 0 || ctx.RegimeSeries[j] != ctx.RegimeSeries[q]) continue;
                int idx = j;
                if (idx >= 0 && idx < fwdH.Count)
                    Offer(FeatureDist(ctx.Features[q], ctx.Features[j]), fwdH[idx]);
            }
        }

        if (nKept < MinSamples) return Distribution.Neutral;

        System.Array.Sort(keep, 0, nKept, System.Collections.Generic.Comparer<(double, double)>.Default);
        var rets = new double[nKept];
        for (int k = 0; k < nKept; k++) rets[k] = keep[k].Ret;

        return FitMatch(rets);
    }

    private static double FeatureDist(double[] a, double[] b)
    {
        double s = 0;
        for (int f = 0; f < a.Length; f++) { double d = a[f] - b[f]; s += d * d; }
        return Math.Sqrt(s);
    }

    private static Distribution FitMatch(double[] r)
    {
        if (r.Length < MinSamples) return Distribution.Neutral;
        int n = r.Length;
        double mean = r.Average();
        double m2 = 0, m3 = 0, m4 = 0;
        for (int i = 0; i < n; i++)
        {
            double d = r[i] - mean;
            m2 += d * d; m3 += d * d * d; m4 += d * d * d * d;
        }
        m2 /= n; m3 /= n; m4 /= n;
        double std  = Math.Sqrt(m2);
        double skew = std > 1e-10 ? m3 / (std * std * std) : 0;
        double exKurt = m2 > 1e-12 ? m4 / (m2 * m2) - 3.0 : 0;

        double pPos     = (double)r.Count(x => x > 0) / n;
        double probUp1  = (double)r.Count(x => x > 1.0) / n;
        double probDown1 = (double)r.Count(x => x < -1.0) / n;

        var sorted = r.OrderBy(x => x).ToArray();
        int tailN = Math.Max(1, (int)(n * 0.05));
        double cvar5 = sorted.Take(tailN).Average();

        double kelly = 0;
        var wins   = r.Where(x => x > 0).ToArray();
        var losses = r.Where(x => x <= 0).ToArray();
        if (wins.Length > 0 && losses.Length > 0)
        {
            double b = wins.Average() / Math.Abs(losses.Average());
            if (b > 1e-9) kelly = Math.Max(0.0, (pPos * b - (1 - pPos)) / b);
        }

        return new Distribution(mean, std, skew, exKurt, pPos, probUp1, probDown1, cvar5, kelly, n);
    }

    // Rebuild the single-bar distribution for the passed prediction (used by Predict).
    // Fresh context per call is fine: Predict() is a rarely-invoked last-bar query.
    private static Distribution PredictLast(Candle[] h1)
    {
        if (h1.Length < Warmup + MinSamples + 1) return Distribution.Neutral;
        var ctx = BuildContext(h1);
        int q = h1.Length - 1;
        if (ctx.RegimeSeries[q] < 0) return Distribution.Neutral;
        return EstimateAt(ctx, ctx.Features, ctx.RegimeSeries, ctx.ForwardPct, q);
    }
}