namespace TradingGA;

// Gaussian HMM with diagonal covariance over the 9-element normalized feature vector
// produced by RegimeClassifier.Features. States are latent (unsupervised); post-hoc labels
// are assigned by HmmGenotype.LabelStates. Baum-Welch EM with scaled forward-backward
// to avoid underflow, multiple random restarts, deterministic given the rng.
public class HiddenMarkovModel
{
    public int N { get; }
    public int D { get; }
    public double[] Pi { get; }
    public double[][] A { get; }
    public double[][] Means { get; }
    public double[][] Vars { get; }
    public double LogLikelihood { get; set; }

    const double VarFloor = 1e-6;
    const double EmissionFloor = 1e-300;

    public HiddenMarkovModel(int states, int dims)
    {
        N = states; D = dims;
        Pi = new double[N];
        A = new double[N][];
        for (int i = 0; i < N; i++) A[i] = new double[N];
        Means = new double[N][];
        for (int i = 0; i < N; i++) Means[i] = new double[D];
        Vars = new double[N][];
        for (int i = 0; i < N; i++) Vars[i] = new double[D];
    }

    public static HiddenMarkovModel Train(double[][] features, int states, Random rng,
        int restarts = 3, int maxIter = 200, double tol = 1e-4)
    {
        int n = features.Length;
        int d = features[0].Length;
        HiddenMarkovModel? best = null;
        double bestLL = double.NegativeInfinity;

        for (int r = 0; r < restarts; r++)
        {
            var model = new HiddenMarkovModel(states, d);
            InitRandom(model, rng, features);
            double prevLL = double.NegativeInfinity;

            for (int iter = 0; iter < maxIter; iter++)
            {
                double ll = BaumWelchStep(model, features);
                if (double.IsNaN(ll) || double.IsInfinity(ll)) break;
                if (Math.Abs(ll - prevLL) < tol * Math.Abs(ll)) break;
                prevLL = ll;
            }

            if (model.LogLikelihood > bestLL)
            {
                bestLL = model.LogLikelihood;
                best = model;
            }
        }

        return best!;
    }

    static void InitRandom(HiddenMarkovModel m, Random rng, double[][] features)
    {
        double sum = 0;
        for (int i = 0; i < m.N; i++) { m.Pi[i] = 0.1 + rng.NextDouble(); sum += m.Pi[i]; }
        for (int i = 0; i < m.N; i++) m.Pi[i] /= sum;

        for (int i = 0; i < m.N; i++)
        {
            sum = 0;
            for (int j = 0; j < m.N; j++) { m.A[i][j] = (i == j ? 5.0 : 1.0) + rng.NextDouble(); sum += m.A[i][j]; }
            for (int j = 0; j < m.N; j++) m.A[i][j] /= sum;
        }

        int n = features.Length;
        for (int i = 0; i < m.N; i++)
        {
            int anchor = rng.Next(n);
            for (int d = 0; d < m.D; d++)
            {
                m.Means[i][d] = features[anchor][d] + rng.NextGaussian() * 0.05;
                m.Vars[i][d] = 0.01 + rng.NextDouble() * 0.05;
            }
        }
    }

    static double BaumWelchStep(HiddenMarkovModel m, double[][] obs)
    {
        int T = obs.Length;
        int N = m.N;
        var logA = new double[N * N];
        var logPi = new double[N];
        for (int i = 0; i < N; i++)
        {
            logPi[i] = Math.Log(Math.Max(m.Pi[i], 1e-300));
            for (int j = 0; j < N; j++)
                logA[i * N + j] = Math.Log(Math.Max(m.A[i][j], 1e-300));
        }

        var logB = new double[T * N];
        for (int t = 0; t < T; t++)
            for (int i = 0; i < N; i++)
                logB[t * N + i] = Math.Log(Math.Max(LogGaussian(obs[t], m.Means[i], m.Vars[i]), EmissionFloor));

        var alpha = new double[T * N];
        var scale = new double[T];
        double totalLL = 0;

        // Forward pass with scaling
        for (int i = 0; i < N; i++)
            alpha[0 * N + i] = Math.Exp(logPi[i] + logB[0 * N + i]);
        double s = 0;
        for (int i = 0; i < N; i++) s += alpha[0 * N + i];
        if (s < 1e-300) s = 1e-300;
        scale[0] = 1.0 / s;
        for (int i = 0; i < N; i++) alpha[0 * N + i] *= scale[0];

        for (int t = 1; t < T; t++)
        {
            for (int j = 0; j < N; j++)
            {
                double sum = 0;
                for (int i = 0; i < N; i++)
                    sum += alpha[(t - 1) * N + i] * m.A[i][j];
                alpha[t * N + j] = sum * Math.Exp(logB[t * N + j]);
            }
            s = 0;
            for (int j = 0; j < N; j++) s += alpha[t * N + j];
            if (s < 1e-300) s = 1e-300;
            scale[t] = 1.0 / s;
            for (int j = 0; j < N; j++) alpha[t * N + j] *= scale[t];
        }

        for (int t = 0; t < T; t++)
            totalLL += Math.Log(Math.Max(scale[t], 1e-300));
        totalLL = -totalLL;

        // Backward pass with same scaling
        var beta = new double[T * N];
        for (int i = 0; i < N; i++) beta[(T - 1) * N + i] = scale[T - 1];

        for (int t = T - 2; t >= 0; t--)
        {
            for (int i = 0; i < N; i++)
            {
                double sum = 0;
                for (int j = 0; j < N; j++)
                    sum += m.A[i][j] * Math.Exp(logB[(t + 1) * N + j]) * beta[(t + 1) * N + j];
                beta[t * N + i] = sum * scale[t];
            }
        }

        // Gamma and xi
        var gamma = new double[T * N];
        for (int t = 0; t < T; t++)
        {
            double s2 = 0;
            for (int i = 0; i < N; i++)
            {
                gamma[t * N + i] = alpha[t * N + i] * beta[t * N + i] / scale[t];
                if (gamma[t * N + i] < 0) gamma[t * N + i] = 0;
                s2 += gamma[t * N + i];
            }
            if (s2 > 1e-300)
                for (int i = 0; i < N; i++) gamma[t * N + i] /= s2;
        }

        // M-step
        for (int i = 0; i < N; i++)
        {
            m.Pi[i] = Math.Max(gamma[0 * N + i], 1e-10);
        }
        double piSum = 0;
        for (int i = 0; i < N; i++) piSum += m.Pi[i];
        for (int i = 0; i < N; i++) m.Pi[i] /= piSum;

        var gammaSumT = new double[N];
        for (int t = 0; t < T; t++)
            for (int i = 0; i < N; i++)
                gammaSumT[i] += gamma[t * N + i];

        for (int i = 0; i < N; i++)
        {
            for (int j = 0; j < N; j++)
            {
                double xiSum = 0;
                for (int t = 0; t < T - 1; t++)
                    xiSum += alpha[t * N + i] * m.A[i][j] * Math.Exp(logB[(t + 1) * N + j]) * beta[(t + 1) * N + j] / scale[t + 1];
                m.A[i][j] = Math.Max(xiSum, 1e-10);
            }
            double aSum = 0;
            for (int j = 0; j < N; j++) aSum += m.A[i][j];
            for (int j = 0; j < N; j++) m.A[i][j] /= aSum;
        }

        for (int i = 0; i < N; i++)
        {
            double gSum = Math.Max(gammaSumT[i], 1e-10);
            for (int d = 0; d < m.D; d++)
            {
                double mean = 0;
                for (int t = 0; t < T; t++) mean += gamma[t * N + i] * obs[t][d];
                mean /= gSum;
                m.Means[i][d] = mean;

                double var = 0;
                for (int t = 0; t < T; t++)
                {
                    double diff = obs[t][d] - mean;
                    var += gamma[t * N + i] * diff * diff;
                }
                m.Vars[i][d] = Math.Max(var / gSum, VarFloor);
            }
        }

        m.LogLikelihood = totalLL;
        return totalLL;
    }

    public double[][] ForwardFilter(double[][] features)
    {
        int T = features.Length;
        var probs = new double[T][];
        for (int t = 0; t < T; t++) probs[t] = new double[N];

        for (int i = 0; i < N; i++)
            probs[0][i] = Pi[i] * Math.Max(LogGaussian(features[0], Means[i], Vars[i]), EmissionFloor);
        Normalize(probs[0]);

        for (int t = 1; t < T; t++)
        {
            for (int j = 0; j < N; j++)
            {
                double sum = 0;
                for (int i = 0; i < N; i++)
                    sum += probs[t - 1][i] * A[i][j];
                probs[t][j] = sum * Math.Max(LogGaussian(features[t], Means[j], Vars[j]), EmissionFloor);
            }
            Normalize(probs[t]);
        }

        return probs;
    }

    public double[][] ForwardFilterSmoothed(double[][] features, double alpha = 0.75, double dwellBias = 0.02)
    {
        int T = features.Length;
        var rawProbs = ForwardFilter(features);
        var smoothed = new double[T][];
        for (int t = 0; t < T; t++) smoothed[t] = new double[N];

        if (T == 0) return smoothed;

        Array.Copy(rawProbs[0], smoothed[0], N);

        int prevState = ArgMax(rawProbs[0]);
        int dwell = 1;

        for (int t = 1; t < T; t++)
        {
            int currentState = ArgMax(rawProbs[t]);
            dwell = currentState == prevState ? dwell + 1 : 1;
            prevState = currentState;

            for (int s = 0; s < N; s++)
            {
                double dwellBonus = s == currentState ? dwellBias * dwell / (dwell + 10.0) : 0.0;
                smoothed[t][s] = alpha * rawProbs[t][s] + (1 - alpha) * smoothed[t - 1][s] + dwellBonus;
            }
            Normalize(smoothed[t]);
        }

        return smoothed;
    }

    static int ArgMax(double[] v)
    {
        int best = 0;
        for (int i = 1; i < v.Length; i++)
            if (v[i] > v[best]) best = i;
        return best;
    }

    static void Normalize(double[] v)
    {
        double s = 0;
        for (int i = 0; i < v.Length; i++) { if (v[i] < 0) v[i] = 0; s += v[i]; }
        if (s < 1e-300) { for (int i = 0; i < v.Length; i++) v[i] = 1.0 / v.Length; return; }
        for (int i = 0; i < v.Length; i++) v[i] /= s;
    }

    static double LogGaussian(double[] x, double[] mean, double[] vars)
    {
        int d = x.Length;
        double logProb = 0;
        for (int i = 0; i < d; i++)
        {
            double v = Math.Max(vars[i], VarFloor);
            double diff = x[i] - mean[i];
            logProb += -0.5 * (Math.Log(2 * Math.PI * v) + diff * diff / v);
        }
        double result = Math.Exp(logProb);
        if (double.IsNaN(result) || double.IsInfinity(result)) return EmissionFloor;
        return result;
    }
}

// Builds a RegimeBar[] series from h1 candles + a trained HMM genotype.
// Mirrors RegimeClassifier.ClassifySeriesWithDuration: computes indicators once, builds
// the same 9-element feature vector with the same clamps, runs the forward filter, and
// emits one RegimeBar per bar with the argmax state label and duration tracking.
public static class HmmAnnotator
{
    public static RegimeBar[] Annotate(Candle[] h1, HmmGenotype geno, double smoothingAlpha = 0.75)
    {
        int n = h1.Length;
        if (n == 0) return [];

        var closes = CandleExt.Closes(h1);
        var highs  = CandleExt.Highs(h1);
        var lows   = CandleExt.Lows(h1);

        var ema20  = Trend.Ema(closes, 20);
        var ema50  = Trend.Ema(closes, 50);
        var ema200 = Trend.Ema(closes, 200);
        var atr14  = Volatility.Atr(highs, lows, closes, 14);
        var atr100 = Volatility.Atr(highs, lows, closes, 100);
        var adx    = Trend.Adx(highs, lows, closes, 14);

        int warmup = RegimeClassifier.Warmup;
        int activeN = Math.Max(0, n - warmup);
        var features = new double[activeN][];
        for (int i = 0; i < activeN; i++)
            features[i] = BuildFeature(i + warmup, closes, ema20, ema50, ema200, atr14, atr100, adx);

        double[][]? probs = null;
        if (activeN > 0)
        {
            var hmm = geno.ToHmm();
            probs = hmm.ForwardFilterSmoothed(features, smoothingAlpha);
        }

        var result = new RegimeBar[n];
        var prevRegime = MarketRegime.Ranging;
        int duration = 0;

        for (int i = 0; i < n; i++)
        {
            if (i < warmup || probs == null)
            {
                result[i] = new(h1[i].Time, MarketRegime.Ranging, 0.0, 0, null);
                prevRegime = MarketRegime.Ranging;
                duration = 0;
                continue;
            }

            int fi = i - warmup;
            var p = probs[fi];
            int best = 0;
            for (int s = 1; s < p.Length; s++)
                if (p[s] > p[best]) best = s;

            var regime = geno.StateLabels[best];
            double conf = p[best];

            duration = regime == prevRegime ? duration + 1 : 1;
            prevRegime = regime;

            var pCopy = new double[p.Length];
            Array.Copy(p, pCopy, p.Length);
            result[i] = new(h1[i].Time, regime, conf, duration, pCopy);
        }

        return result;
    }

    static double[] BuildFeature(int i, double[] closes, double[] ema20, double[] ema50,
        double[] ema200, double[] atr14, double[] atr100, double[] adx)
    {
        double price = closes[i];
        double e20 = ema20[i], e50 = ema50[i], e200 = ema200[i];
        double atrRatio = atr100[i] > 1e-10 ? atr14[i] / atr100[i] : 1.0;

        return
        [
            Math.Clamp(e20  > 1e-10 ? (price - e20)  / e20  : 0, -0.5, 0.5),
            Math.Clamp(e50  > 1e-10 ? (price - e50)  / e50  : 0, -0.5, 0.5),
            Math.Clamp(e200 > 1e-10 ? (price - e200) / e200 : 0, -0.5, 0.5),
            Math.Clamp(ema50[Math.Max(0, i - 20)] > 1e-10
                ? (e50 - ema50[Math.Max(0, i - 20)]) / ema50[Math.Max(0, i - 20)] : 0, -0.10, 0.10),
            Math.Clamp(atrRatio, 0.0, 5.0) / 5.0,
            Math.Clamp(adx[i], 0.0, 60.0) / 60.0,
            Math.Clamp(closes[Math.Max(0, i - 20)] > 1e-10
                ? (price - closes[Math.Max(0, i - 20)]) / closes[Math.Max(0, i - 20)] : 0, -0.5, 0.5),
            Math.Clamp(e50 > 1e-10 ? (e20 - e50) / e50 : 0, -0.3, 0.3),
            Math.Clamp(e200 > 1e-10 ? (e50 - e200) / e200 : 0, -0.5, 0.5),
        ];
    }
}
