namespace TradingGA;

// Statistics for the signal study. Everything is clustered by CALENDAR DAY, because the effective
// sample is far smaller than the row count: alts co-move with BTC (one day's rows across 150 coins
// are closer to one observation than 150), and forward windows overlap across adjacent bars.
//   - Information coefficients are POOLED rank correlations with a day-block bootstrap SE (see
//     PooledIc for why not a mean of daily ICs).
//   - Coefficient uncertainty is a moving-block bootstrap over days.
//   - Out-of-sample = walk-forward over contiguous day blocks with a purge gap ≥ the longest label.
public static class SignalStats
{
    // ── basic statistics ─────────────────────────────────────────────────────────────────────
    public static double[] Ranks(IReadOnlyList<double> v)
    {
        int n = v.Count;
        var idx = Enumerable.Range(0, n).ToArray();
        Array.Sort(idx, (a, b) => v[a].CompareTo(v[b]));
        var r = new double[n];
        for (int i = 0; i < n;)
        {
            int j = i;
            while (j + 1 < n && v[idx[j + 1]] == v[idx[i]]) j++;
            double avg = (i + j) / 2.0 + 1.0;
            for (int k = i; k <= j; k++) r[idx[k]] = avg;
            i = j + 1;
        }
        return r;
    }

    public static double Pearson(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        int n = x.Count; if (n < 3) return double.NaN;
        double mx = 0, my = 0; for (int i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n; my /= n;
        double sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < n; i++) { double a = x[i] - mx, b = y[i] - my; sxy += a * b; sxx += a * a; syy += b * b; }
        return sxx > 1e-24 && syy > 1e-24 ? sxy / Math.Sqrt(sxx * syy) : double.NaN;
    }

    public static double Spearman(IReadOnlyList<double> x, IReadOnlyList<double> y) => Pearson(Ranks(x), Ranks(y));

    // Newey-West (Bartlett) t-statistic of the mean of a time-ordered series.
    public static (double Mean, double T) NeweyWestT(IReadOnlyList<double> s, int lag)
    {
        int n = s.Count; if (n < 3) return (n > 0 ? s.Average() : double.NaN, double.NaN);
        double m = s.Average();
        double v = 0; for (int i = 0; i < n; i++) v += (s[i] - m) * (s[i] - m);
        v /= n;
        for (int l = 1; l <= Math.Min(lag, n - 1); l++)
        {
            double g = 0; for (int i = l; i < n; i++) g += (s[i] - m) * (s[i - l] - m);
            v += 2.0 * (1.0 - l / (lag + 1.0)) * (g / n);
        }
        return (m, v > 1e-30 ? m / Math.Sqrt(v / n) : double.NaN);
    }

    public static double TwoSidedP(double t) => double.IsNaN(t) ? 1.0 : 2.0 * (1.0 - NormalCdf(Math.Abs(t)));

    public static double NormalCdf(double x)
    {
        // Abramowitz-Stegun 7.1.26 via erf; |error| < 1.5e-7, ample for p-values.
        double z = Math.Abs(x) / Math.Sqrt(2.0), t = 1.0 / (1.0 + 0.3275911 * z);
        double erf = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
        return 0.5 * (1.0 + (x >= 0 ? erf : -erf));
    }

    // Benjamini-Hochberg q-values (FDR). The study runs hundreds of IC tests; reading raw p < 0.05
    // across that table would hand out a dozen "discoveries" from noise.
    public static double[] BenjaminiHochberg(IReadOnlyList<double> p)
    {
        int m = p.Count; var q = new double[m];
        var idx = Enumerable.Range(0, m).OrderBy(i => p[i]).ToArray();
        double prev = 1.0;
        for (int r = m - 1; r >= 0; r--)
        {
            double v = Math.Min(prev, p[idx[r]] * m / (r + 1.0));
            q[idx[r]] = prev = Math.Min(1.0, v);
        }
        return q;
    }

    // NW lag for daily series given a label horizon in hours: overlap spans ceil(h/24) days, +1.
    public static int LagForHorizon(int hBars) => (int)Math.Ceiling(hBars / 24.0) + 1;

    // ── day grouping ─────────────────────────────────────────────────────────────────────────
    public static (int Day, int[] Rows)[] DayGroups(IReadOnlyList<int> day, bool[]? mask = null)
    {
        var d = new SortedDictionary<int, List<int>>();
        for (int i = 0; i < day.Count; i++)
        {
            if (mask != null && !mask[i]) continue;
            if (!d.TryGetValue(day[i], out var l)) d[day[i]] = l = new List<int>();
            l.Add(i);
        }
        return d.Select(kv => (kv.Key, kv.Value.ToArray())).ToArray();
    }

    public record IcResult(double Ic, double T, double P, int Days, int Rows);

    public const int MinBlocks = 15;   // minimum bootstrap blocks for a reported t

    // POOLED rank IC with a day-block bootstrap standard error.
    //
    // NOT a mean of per-day ICs. That was the first version and the random-walk null rejected it:
    // a per-day Spearman demeans within the day, and with PERSISTENT regressors (EMA distance, RSI,
    // range position move slowly) and OVERLAPPING forward windows, within-block demeaning
    // manufactures a negative correlation — the Stambaugh / Nickell bias. On six random-walk seeds
    // dist_ema50 came out t = −1.3, 0.0, −2.7, −2.2, −1.5, −1.2: every trend signal read as a
    // reversal signal. Pooling over the whole sample shrinks that bias with the full T instead of
    // one day's 24 bars.
    //
    // Per-day sums of the RANKS (computed once over the masked rows) are sufficient statistics, so
    // a resample of days is a sum, not a re-sort — which is what makes the bootstrap affordable.
    // xr/yr: ranks over the masked rows, NaN where unmasked. rowDay: dense day index 0..D-1.
    public static IcResult PooledIc(double[] xr, double[] yr, int[] rowDay, int D, int lag,
                                    int reps = 200, int seed = 20261002)
    {
        var n = new double[D]; var sx = new double[D]; var sy = new double[D];
        var sxx = new double[D]; var syy = new double[D]; var sxy = new double[D];
        int rows = 0;
        for (int i = 0; i < xr.Length; i++)
        {
            double a = xr[i], c = yr[i]; if (double.IsNaN(a) || double.IsNaN(c)) continue;
            int d = rowDay[i]; n[d]++; sx[d] += a; sy[d] += c; sxx[d] += a * a; syy[d] += c * c; sxy[d] += a * c; rows++;
        }
        static double Corr(double N, double Sx, double Sy, double Sxx, double Syy, double Sxy)
        {
            if (N < 3) return double.NaN;
            double vx = Sxx - Sx * Sx / N, vy = Syy - Sy * Sy / N;
            // RELATIVE threshold. A constant feature (funding = 0 on a coin with no funding data)
            // has all ranks equal; the absolute variance is then pure roundoff (~1e-3 at N=30k),
            // which produced IC ≈ 1e-17 with a bootstrap SE of ≈ 1e-18 — "t = 5" on nothing.
            return vx > 1e-9 * Sxx && vy > 1e-9 * Syy ? (Sxy - Sx * Sy / N) / Math.Sqrt(vx * vy) : double.NaN;
        }
        var live = Enumerable.Range(0, D).Where(d => n[d] > 0).ToArray();
        double ic = Corr(n.Sum(), sx.Sum(), sy.Sum(), sxx.Sum(), syy.Sum(), sxy.Sum());
        // Fewer than MinBlocks independent blocks and the bootstrap SE is itself noise: a thin
        // regime (48 Ranging days ≈ 6 blocks) produced t = 4.0 on a random walk. Report no t.
        int block = Math.Max(5, 2 * lag), L = live.Length;
        if (double.IsNaN(ic) || L < MinBlocks * block) return new IcResult(ic, double.NaN, 1.0, L, rows);

        var rng = new Random(seed); var draws = new List<double>(reps);
        for (int r = 0; r < reps; r++)
        {
            double N = 0, Sx = 0, Sy = 0, Sxx = 0, Syy = 0, Sxy = 0;
            for (int filled = 0; filled < L; filled += block)
            {
                int s0 = rng.Next(0, L - block + 1);
                for (int k = 0; k < block; k++)
                { int d = live[s0 + k]; N += n[d]; Sx += sx[d]; Sy += sy[d]; Sxx += sxx[d]; Syy += syy[d]; Sxy += sxy[d]; }
            }
            double v = Corr(N, Sx, Sy, Sxx, Syy, Sxy); if (!double.IsNaN(v)) draws.Add(v);
        }
        if (draws.Count < 10) return new IcResult(ic, double.NaN, 1.0, L, rows);
        double mu = draws.Average();
        double se = Math.Sqrt(draws.Sum(v => (v - mu) * (v - mu)) / (draws.Count - 1));
        double t = se > 1e-15 ? ic / se : double.NaN;
        return new IcResult(ic, t, TwoSidedP(t), L, rows);
    }

    // OUT-OF-SAMPLE correlation with both sides centred at TRAINING means (a = pred − ȳ_train,
    // b = y − ȳ_train), day-block bootstrap SE. Any statistic that re-centres on the TEST window
    // uses the test window's own mean — information the model never had — and with persistent
    // regressors that alone biases OOS correlation negative: a pooled rank IC averaged t ≈ −1.2
    // over ten random-walk seeds. Same reasoning as Campbell-Thompson OOS R².
    public static IcResult TrainCentredCorr(IReadOnlyList<double> a, IReadOnlyList<double> b, IReadOnlyList<int> rowDay,
                                            int D, int lag, int reps = 200, int seed = 20261002, int? blockDays = null)
    {
        var sab = new double[D]; var saa = new double[D]; var sbb = new double[D]; var n = new int[D];
        for (int i = 0; i < a.Count; i++) { int d = rowDay[i]; sab[d] += a[i] * b[i]; saa[d] += a[i] * a[i]; sbb[d] += b[i] * b[i]; n[d]++; }
        var live = Enumerable.Range(0, D).Where(d => n[d] > 0).ToArray();
        static double C(double ab, double aa, double bb) => aa > 1e-24 && bb > 1e-24 ? ab / Math.Sqrt(aa * bb) : double.NaN;
        double r0 = C(sab.Sum(), saa.Sum(), sbb.Sum());
        int block = blockDays ?? Math.Max(5, 2 * lag), L = live.Length;
        if (double.IsNaN(r0) || L < (blockDays.HasValue ? 8 : MinBlocks) * block) return new IcResult(r0, double.NaN, 1.0, L, a.Count);
        var rng = new Random(seed); var draws = new List<double>(reps);
        for (int r = 0; r < reps; r++)
        {
            double AB = 0, AA = 0, BB = 0;
            for (int filled = 0; filled < L; filled += block)
            {
                int s0 = rng.Next(0, L - block + 1);
                for (int k = 0; k < block; k++) { int d = live[s0 + k]; AB += sab[d]; AA += saa[d]; BB += sbb[d]; }
            }
            double v = C(AB, AA, BB); if (!double.IsNaN(v)) draws.Add(v);
        }
        if (draws.Count < 10) return new IcResult(r0, double.NaN, 1.0, L, a.Count);
        double mu = draws.Average(), se = Math.Sqrt(draws.Sum(v => (v - mu) * (v - mu)) / (draws.Count - 1));
        double t = se > 1e-15 ? r0 / se : double.NaN;
        return new IcResult(r0, t, TwoSidedP(t), L, a.Count);
    }

    // Ranks of v over the rows where mask is true; NaN elsewhere.
    public static double[] MaskedRanks(IReadOnlyList<float> v, bool[]? mask)
    {
        var idx = new List<int>(v.Count);
        for (int i = 0; i < v.Count; i++) if ((mask == null || mask[i]) && !float.IsNaN(v[i])) idx.Add(i);
        var r = SignalStats.Ranks(idx.Select(i => (double)v[i]).ToList());
        var outp = new double[v.Count]; Array.Fill(outp, double.NaN);
        for (int j = 0; j < idx.Count; j++) outp[idx[j]] = r[j];
        return outp;
    }

    // ── ridge from sufficient statistics ─────────────────────────────────────────────────────
    // Per-day sums. Summing these over any set of days gives that set's regression exactly, which is
    // what makes the day-block bootstrap and the walk-forward folds cheap.
    public sealed class Agg
    {
        public readonly int P; public double N, Sy, Syy; public readonly double[] Sx, Sxy, Sxx;
        public Agg(int p) { P = p; Sx = new double[p]; Sxy = new double[p]; Sxx = new double[p * p]; }
        public void Add(double[] x, double y)
        {
            N++; Sy += y; Syy += y * y;
            for (int a = 0; a < P; a++)
            {
                Sx[a] += x[a]; Sxy[a] += x[a] * y;
                for (int b = a; b < P; b++) Sxx[a * P + b] += x[a] * x[b];
            }
        }
        public void AddAgg(Agg o)
        {
            N += o.N; Sy += o.Sy; Syy += o.Syy;
            for (int a = 0; a < P; a++) { Sx[a] += o.Sx[a]; Sxy[a] += o.Sxy[a]; }
            for (int k = 0; k < Sxx.Length; k++) Sxx[k] += o.Sxx[k];
        }
    }

    // Ridge on standardised features. Returns standardised coefficients b (comparable across
    // features: "ATRs of forward move per 1σ of signal"), and the raw-space predictor.
    public sealed record RidgeModel(double[] B, double[] Mean, double[] Sd, double YMean)
    {
        public double Predict(Func<int, double> x)
        {
            double s = YMean;
            for (int k = 0; k < B.Length; k++) if (Sd[k] > 1e-12) s += B[k] * (x(k) - Mean[k]) / Sd[k];
            return s;
        }
    }

    public const double RidgeLambda = 0.01;   // on the correlation scale; fixed — not tuned, not a trial

    public static RidgeModel? FitRidge(Agg g, double lambda = RidgeLambda)
    {
        int p = g.P; if (g.N < p + 10) return null;
        var mean = new double[p]; var sd = new double[p];
        for (int a = 0; a < p; a++) mean[a] = g.Sx[a] / g.N;
        for (int a = 0; a < p; a++) sd[a] = Math.Sqrt(Math.Max(0, g.Sxx[a * p + a] / g.N - mean[a] * mean[a]));
        double my = g.Sy / g.N;
        var A = new double[p * p]; var rhs = new double[p];
        for (int a = 0; a < p; a++)
        {
            if (sd[a] < 1e-12) { A[a * p + a] = 1; continue; }   // constant feature → coefficient 0
            rhs[a] = (g.Sxy[a] / g.N - mean[a] * my) / sd[a];
            for (int b = 0; b < p; b++)
            {
                if (sd[b] < 1e-12) continue;
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                A[a * p + b] = (g.Sxx[lo * p + hi] / g.N - mean[a] * mean[b]) / (sd[a] * sd[b]);
            }
            A[a * p + a] += lambda;
        }
        var b0 = SolveSpd(A, rhs, p);
        return b0 is null ? null : new RidgeModel(b0, mean, sd, my);
    }

    // Cholesky solve of a symmetric positive-definite system; null if not SPD.
    public static double[]? SolveSpd(double[] A, double[] rhs, int p)
    {
        var L = new double[p * p];
        for (int i = 0; i < p; i++)
            for (int j = 0; j <= i; j++)
            {
                double s = A[i * p + j];
                for (int k = 0; k < j; k++) s -= L[i * p + k] * L[j * p + k];
                if (i == j) { if (s <= 1e-14) return null; L[i * p + i] = Math.Sqrt(s); }
                else L[i * p + j] = s / L[j * p + j];
            }
        var z = new double[p];
        for (int i = 0; i < p; i++) { double s = rhs[i]; for (int k = 0; k < i; k++) s -= L[i * p + k] * z[k]; z[i] = s / L[i * p + i]; }
        var x = new double[p];
        for (int i = p - 1; i >= 0; i--) { double s = z[i]; for (int k = i + 1; k < p; k++) s -= L[k * p + i] * x[k]; x[i] = s / L[i * p + i]; }
        return x;
    }

    // Moving-block bootstrap over days → standard error of each standardised coefficient.
    public static double[] BootstrapSe(IReadOnlyList<Agg> dayAggs, int reps = 200, int blockDays = 5, int seed = 20261002)
    {
        int p = dayAggs.Count > 0 ? dayAggs[0].P : 0, D = dayAggs.Count;
        var draws = new List<double[]>();
        if (D < 2 * blockDays) return Enumerable.Repeat(double.NaN, p).ToArray();
        var rng = new Random(seed);
        for (int r = 0; r < reps; r++)
        {
            var g = new Agg(p);
            for (int filled = 0; filled < D; filled += blockDays)
            {
                int s = rng.Next(0, D - blockDays + 1);
                for (int k = 0; k < blockDays; k++) g.AddAgg(dayAggs[s + k]);
            }
            if (FitRidge(g) is { } m) draws.Add(m.B);
        }
        var se = new double[p];
        for (int a = 0; a < p; a++)
        {
            if (draws.Count < 10) { se[a] = double.NaN; continue; }
            double mu = draws.Average(d => d[a]);
            se[a] = Math.Sqrt(draws.Sum(d => (d[a] - mu) * (d[a] - mu)) / (draws.Count - 1));
        }
        return se;
    }

    // ── logistic ridge (triple barrier: P(upper barrier first | a barrier was hit)) ───────────
    public sealed record LogitModel(double[] B, double B0, double[] Mean, double[] Sd)
    {
        public double Prob(Func<int, double> x)
        {
            double s = B0;
            for (int k = 0; k < B.Length; k++) if (Sd[k] > 1e-12) s += B[k] * (x(k) - Mean[k]) / Sd[k];
            return 1.0 / (1.0 + Math.Exp(-s));
        }
    }

    public static LogitModel? FitLogit(IReadOnlyList<double[]> X, IReadOnlyList<int> y, double lambda = 1.0, int iters = 25)
    {
        int n = X.Count; if (n < 50) return null;
        int p = X[0].Length;
        var mean = new double[p]; var sd = new double[p];
        for (int a = 0; a < p; a++) { double s = 0, s2 = 0; foreach (var r in X) { s += r[a]; s2 += r[a] * r[a]; } mean[a] = s / n; sd[a] = Math.Sqrt(Math.Max(0, s2 / n - mean[a] * mean[a])); }
        double Z(double[] r, int a) => sd[a] > 1e-12 ? (r[a] - mean[a]) / sd[a] : 0.0;

        int q = p + 1;                        // index p = intercept (unpenalised)
        var w = new double[q];
        for (int it = 0; it < iters; it++)
        {
            var H = new double[q * q]; var g = new double[q];
            for (int i = 0; i < n; i++)
            {
                var r = X[i];
                double s = w[p]; for (int a = 0; a < p; a++) s += w[a] * Z(r, a);
                double mu = 1.0 / (1.0 + Math.Exp(-s)), wt = Math.Max(mu * (1 - mu), 1e-9), e = y[i] - mu;
                for (int a = 0; a < q; a++)
                {
                    double za = a == p ? 1.0 : Z(r, a);
                    g[a] += e * za;
                    for (int b = a; b < q; b++) H[a * q + b] += wt * za * (b == p ? 1.0 : Z(r, b));
                }
            }
            for (int a = 0; a < q; a++) for (int b = 0; b < a; b++) H[a * q + b] = H[b * q + a];
            for (int a = 0; a < p; a++) { H[a * q + a] += lambda; g[a] -= lambda * w[a]; }
            var step = SolveSpd(H, g, q);
            if (step is null) return null;
            double mx = 0; for (int a = 0; a < q; a++) { w[a] += step[a]; mx = Math.Max(mx, Math.Abs(step[a])); }
            if (mx < 1e-7) break;
        }
        return new LogitModel(w[..p], w[p], mean, sd);
    }

    public static double Auc(IReadOnlyList<double> score, IReadOnlyList<int> y)
    {
        var r = Ranks(score); double sumPos = 0; long nPos = 0, nNeg = 0;
        for (int i = 0; i < y.Count; i++) { if (y[i] == 1) { sumPos += r[i]; nPos++; } else nNeg++; }
        return nPos == 0 || nNeg == 0 ? double.NaN : (sumPos - nPos * (nPos + 1) / 2.0) / ((double)nPos * nNeg);
    }

    // ── walk-forward ─────────────────────────────────────────────────────────────────────────
    // Days split into `blocks` contiguous blocks; fold k trains on blocks [0,k) minus the last
    // `purgeDays` before block k, and tests on block k. Expanding window, no future in any fit.
    public static List<(int[] TrainDays, int[] TestDays)> WalkForwardFolds(int[] sortedDays, int blocks, int purgeDays)
    {
        var folds = new List<(int[], int[])>();
        int D = sortedDays.Length; if (D < blocks * 5) return folds;
        for (int k = 1; k < blocks; k++)
        {
            int testLo = D * k / blocks, testHi = D * (k + 1) / blocks;
            int trainHi = Math.Max(0, testLo - purgeDays);
            if (trainHi < 30) continue;
            folds.Add((sortedDays[..trainHi], sortedDays[testLo..testHi]));
        }
        return folds;
    }

    // Non-overlapping row selector for horizon h given the panel stride: keeps rows spaced at
    // stride·ceil(h/stride) ≥ h bars, so each kept row's label window is disjoint from the next.
    public static bool NonOverlapping(int bar, int h, int stride)
    {
        int step = Math.Max(1, (int)Math.Ceiling(h / (double)Math.Max(1, stride)));
        return ((bar - SignalFeatures.Warmup) / Math.Max(1, stride)) % step == 0;
    }
}
