namespace TradingGA;

// CMA-ES (Covariance Matrix Adaptation Evolution Strategy) optimizer.
// More sample-efficient than a GA for the 12-parameter genotype space:
// adapts the full covariance of the search distribution each generation.
// Reference: Hansen (2016) "The CMA Evolution Strategy: A Tutorial".
public class CmaOptimizer
{
    private const int N = 12;

    private static readonly double[] Lbs  = [7, 68, 1, 10, 0.990, 1.0, 7, 15.0, 0.6, 0.3, 0.0, 0];
    private static readonly double[] Ubs  = [21, 80, 5, 50, 0.999, 2.5, 21, 35.0, 5.0, 8.0, 2.0, 5];
    private static readonly bool[]   IsInt = [true, false, true, true, false, false, true, false, false, false, false, true];

    private readonly bool _useAtr;
    private readonly bool _verbose;

    public CmaOptimizer(bool useAtr = true, bool verbose = true)
    {
        _useAtr  = useAtr;
        _verbose = verbose;
    }

    public Genotype Run(IReadOnlyList<GeneticAlgorithm.CoinData> coins, Genotype? seed = null, int maxEvals = 3000)
    {
        // Standard CMA-ES hyperparameters (Hansen 2016)
        int lam  = 4 + (int)Math.Floor(3 * Math.Log(N)); // = 11
        int mu   = lam / 2;                               // = 5

        var w = new double[mu];
        for (int i = 0; i < mu; i++) w[i] = Math.Log(mu + 0.5) - Math.Log(i + 1);
        double wSum  = w.Sum();
        for (int i = 0; i < mu; i++) w[i] /= wSum;
        double muEff = 1.0 / w.Select(wi => wi * wi).Sum();

        double cs    = (muEff + 2) / (N + muEff + 5);
        double ds    = 1 + 2 * Math.Max(0, Math.Sqrt((muEff - 1.0) / (N + 1)) - 1) + cs;
        double chiN  = Math.Sqrt(N) * (1 - 1.0 / (4 * N) + 1.0 / (21.0 * N * N));
        double cc    = (4 + muEff / N) / (N + 4 + 2 * muEff / N);
        double c1    = 2.0 / ((N + 1.3) * (N + 1.3) + muEff);
        double cmu   = Math.Min(1 - c1, 2 * (muEff - 2 + 1.0 / muEff) / ((N + 2.0) * (N + 2) + muEff));

        var rng      = new Random(42);
        double[] xm  = seed != null ? Encode(seed) : RandomVec(rng);
        double sigma = 0.3;
        double[] ps  = new double[N];
        double[] pc  = new double[N];
        double[,] C  = Identity();
        double[,] B  = Identity();
        double[]  D  = Enumerable.Repeat(1.0, N).ToArray();

        int eigenEvery = Math.Max(1, (int)Math.Floor(lam / (c1 + cmu) / N / 10.0));

        Genotype bestG   = Decode(xm);
        double   bestFit = double.MinValue;
        int evals = 0, gen = 0;

        if (_verbose) Console.WriteLine($"  CMA-ES  n={N}  λ={lam}  μ={mu}  maxEvals={maxEvals}  eigenEvery={eigenEvery}");

        while (evals < maxEvals)
        {
            var arx = new double[lam][];
            var arz = new double[lam][];
            for (int k = 0; k < lam; k++)
            {
                arz[k] = SampleStdNormal(rng);
                // x = xm + sigma * B * D * z
                var Dz = new double[N];
                for (int i = 0; i < N; i++) Dz[i] = D[i] * arz[k][i];
                var BDz = MatVec(B, Dz);
                arx[k] = new double[N];
                for (int i = 0; i < N; i++) arx[k][i] = xm[i] + sigma * BDz[i];
            }

            // Evaluate
            var fits = new double[lam];
            Parallel.For(0, lam, k => fits[k] = Fitness(Decode(arx[k]), coins));
            evals += lam;

            var ranked = Enumerable.Range(0, lam).OrderByDescending(k => fits[k]).ToArray();

            if (fits[ranked[0]] > bestFit)
            {
                bestFit       = fits[ranked[0]];
                bestG         = Decode(arx[ranked[0]]);
                bestG.Fitness = bestFit;
                if (_verbose) Console.WriteLine($"  Gen {gen,4}  evals={evals,5}  best={bestFit:F4}  σ={sigma:F4}  {bestG}");
            }

            // Update mean
            var xmOld = (double[])xm.Clone();
            for (int i = 0; i < N; i++) xm[i] = 0;
            for (int j = 0; j < mu; j++)
                for (int i = 0; i < N; i++)
                    xm[i] += w[j] * arx[ranked[j]][i];

            // Step-size evolution path: ps = (1-cs)*ps + sqrt(cs*(2-cs)*muEff) * C^{-1/2} * (xm-xmOld)/sigma
            var dx       = new double[N];
            for (int i = 0; i < N; i++) dx[i] = (xm[i] - xmOld[i]) / sigma;
            var invsqCdx = InvSqrtC_x(B, D, dx);
            double sqfac = Math.Sqrt(cs * (2 - cs) * muEff);
            for (int i = 0; i < N; i++)
                ps[i] = (1 - cs) * ps[i] + sqfac * invsqCdx[i];

            // hsig: stall indicator
            double psNorm = Math.Sqrt(ps.Select(p => p * p).Sum());
            double thresh  = 1.4 + 2.0 / (N + 1);
            double expected = chiN * Math.Sqrt(1 - Math.Pow(1 - cs, 2.0 * ((evals / lam) + 1)));
            double hsig    = psNorm / (expected < 1e-10 ? 1 : expected) < thresh ? 1.0 : 0.0;

            // Covariance evolution path: pc
            double pcfac = Math.Sqrt(cc * (2 - cc) * muEff);
            for (int i = 0; i < N; i++)
                pc[i] = (1 - cc) * pc[i] + hsig * pcfac * (xm[i] - xmOld[i]) / sigma;

            // Update covariance matrix
            if (gen % eigenEvery == 0)
            {
                double rankOneScale = c1 * (hsig == 0 ? (1 - cc * (2 - cc)) : 1.0);
                for (int i = 0; i < N; i++)
                for (int j = i; j < N; j++)
                {
                    C[i, j] = (1 - c1 - cmu) * C[i, j] + c1 * pc[i] * pc[j];
                    for (int k = 0; k < mu; k++)
                    {
                        double zi = (arx[ranked[k]][i] - xmOld[i]) / sigma;
                        double zj = (arx[ranked[k]][j] - xmOld[j]) / sigma;
                        C[i, j] += cmu * w[k] * zi * zj;
                    }
                    C[j, i] = C[i, j];
                }
                _ = rankOneScale; // suppress unused warning

                (B, D) = EigenDecomp(C);
                for (int i = 0; i < N; i++) D[i] = Math.Sqrt(Math.Max(D[i], 1e-20));
            }

            // Update step size
            sigma *= Math.Exp((cs / ds) * (psNorm / chiN - 1));
            sigma  = Math.Clamp(sigma, 1e-12, 2.0);
            gen++;
        }

        bestG.Fitness = bestFit;
        return bestG;
    }

    // ── Fitness: 5-fold walk-forward cross-validation (mirrors GA.Fitness) ──
    private double Fitness(Genotype g, IReadOnlyList<GeneticAlgorithm.CoinData> coins)
    {
        double total = 0; int count = 0;
        foreach (var coin in coins)
        {
            if (coin.TrainCandles.Length < 500) continue;
            int k = Math.Min(5, coin.TrainCandles.Length / 200);
            double coinScore;
            if (k < 2)
            {
                var r        = Simulator.GetUnifiedReturns(g, coin.TrainCandles, _useAtr).Select(t => t.Return).ToList();
                double sh    = Simulator.SharpeRatio(r, coin.TrainCandles.Length);
                double cal   = Math.Clamp(Simulator.CalmarRatio(r), -2.0, 3.0);
                double days  = coin.TrainCandles.Length * 5.0 / 60.0 / 24.0;
                double annR  = days > 0 ? r.Sum() * (365.25 / days) : 0;
                coinScore    = 0.40 * sh + 0.10 * cal + 0.50 * Math.Tanh(annR / 5.0);
            }
            else
            {
                int foldSize = coin.TrainCandles.Length / k;
                var scores   = new double[k];
                for (int f = 0; f < k; f++)
                {
                    int s   = f * foldSize;
                    int e   = f == k - 1 ? coin.TrainCandles.Length : s + foldSize;
                    var chunk = coin.TrainCandles[s..e];
                    var r        = Simulator.GetUnifiedReturns(g, chunk, _useAtr).Select(t => t.Return).ToList();
                    double sh    = Simulator.SharpeRatio(r, chunk.Length);
                    double cal   = Math.Clamp(Simulator.CalmarRatio(r), -2.0, 3.0);
                    double days  = chunk.Length * 5.0 / 60.0 / 24.0;
                    double annR  = days > 0 ? r.Sum() * (365.25 / days) : 0;
                    scores[f]    = 0.40 * sh + 0.10 * cal + 0.50 * Math.Tanh(annR / 5.0);
                }
                double mean = scores.Average();
                double std  = Math.Sqrt(scores.Select(sc => (sc - mean) * (sc - mean)).Average());
                coinScore   = mean - 0.75 * std;
            }
            total += coinScore; count++;
        }
        return count > 0 ? total / count : 0;
    }

    // ── Encode / decode ───────────────────────────────────────────────────────
    private static double[] Encode(Genotype g)
    {
        double[] v = [g.RsiPeriod, g.RsiOverbought, g.BosCandlesWait, g.EmaPeriod,
                      g.BosThreshold, g.VolumeMultiplier, g.RegimeAdxPeriod, g.RegimeAdxThreshold,
                      g.GridStepAtrMult, g.DcaTriggerAtrMult, g.BreakEvenAtrMult, g.MaxDcaLevels];
        var x = new double[N];
        for (int i = 0; i < N; i++) x[i] = (v[i] - Lbs[i]) / (Ubs[i] - Lbs[i]);
        return x;
    }

    private static Genotype Decode(double[] x)
    {
        var v = new double[N];
        for (int i = 0; i < N; i++)
        {
            double raw = Lbs[i] + Math.Clamp(x[i], 0, 1) * (Ubs[i] - Lbs[i]);
            v[i] = IsInt[i] ? Math.Round(raw) : raw;
        }
        return new Genotype
        {
            RsiPeriod          = (int)v[0],   RsiOverbought      = v[1],
            BosCandlesWait     = (int)v[2],   EmaPeriod          = (int)v[3],
            BosThreshold       = v[4],        VolumeMultiplier   = v[5],
            RegimeAdxPeriod    = (int)v[6],   RegimeAdxThreshold = v[7],
            GridStepAtrMult    = v[8],        DcaTriggerAtrMult  = v[9],
            BreakEvenAtrMult   = v[10],       MaxDcaLevels       = (int)v[11],
        };
    }

    // ── Linear algebra helpers ────────────────────────────────────────────────

    private static double[] RandomVec(Random rng)
    {
        var x = new double[N];
        for (int i = 0; i < N; i++) x[i] = 0.3 + rng.NextDouble() * 0.4; // start near center
        return x;
    }

    private static double[,] Identity()
    {
        var m = new double[N, N];
        for (int i = 0; i < N; i++) m[i, i] = 1.0;
        return m;
    }

    private static double[] SampleStdNormal(Random rng)
    {
        var z = new double[N];
        for (int i = 0; i < N - 1; i += 2)
        {
            double u1 = Math.Max(rng.NextDouble(), 1e-15), u2 = rng.NextDouble();
            double mag = Math.Sqrt(-2 * Math.Log(u1));
            z[i]     = mag * Math.Cos(2 * Math.PI * u2);
            z[i + 1] = mag * Math.Sin(2 * Math.PI * u2);
        }
        return z;
    }

    // Matrix-vector product: y = M * x
    private static double[] MatVec(double[,] M, double[] x)
    {
        var y = new double[N];
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
                y[i] += M[i, j] * x[j];
        return y;
    }

    // C^{-1/2} * v  using B, D where C = B * D^2 * B^T → C^{-1/2} = B * D^{-1} * B^T
    private static double[] InvSqrtC_x(double[,] B, double[] D, double[] v)
    {
        // Bt * v
        var Btv = new double[N];
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
                Btv[i] += B[j, i] * v[j];
        // D^{-1} * Bt*v
        var Dinv = new double[N];
        for (int i = 0; i < N; i++) Dinv[i] = Btv[i] / Math.Max(D[i], 1e-20);
        // B * result
        return MatVec(B, Dinv);
    }

    // Jacobi eigendecomposition for symmetric N×N matrix.
    // Returns (B, D) where B columns are eigenvectors, D are eigenvalues.
    private static (double[,] B, double[] D) EigenDecomp(double[,] Cin)
    {
        var A = (double[,])Cin.Clone();
        var B = Identity();

        for (int sweep = 0; sweep < 50; sweep++)
        {
            bool converged = true;
            for (int p = 0; p < N - 1; p++)
            for (int q = p + 1; q < N; q++)
            {
                double apq = A[p, q];
                if (Math.Abs(apq) < 1e-12) continue;
                converged = false;

                double theta = (A[q, q] - A[p, p]) / (2 * apq);
                double t = theta >= 0
                    ? 1.0 / (theta + Math.Sqrt(1 + theta * theta))
                    : 1.0 / (theta - Math.Sqrt(1 + theta * theta));
                double c   = 1.0 / Math.Sqrt(1 + t * t);
                double s   = t * c;
                double tau = s / (1 + c);

                A[p, p] -= t * apq;
                A[q, q] += t * apq;
                A[p, q] = A[q, p] = 0;

                for (int r = 0; r < N; r++)
                {
                    if (r == p || r == q) continue;
                    double g = A[p, r], h = A[q, r];
                    A[p, r] = A[r, p] = g - s * (h + g * tau);
                    A[q, r] = A[r, q] = h + s * (g - h * tau);
                }
                for (int r = 0; r < N; r++)
                {
                    double g = B[r, p], h = B[r, q];
                    B[r, p] = g - s * (h + g * tau);
                    B[r, q] = h + s * (g - h * tau);
                }
            }
            if (converged) break;
        }

        var D = new double[N];
        for (int i = 0; i < N; i++) D[i] = A[i, i];
        return (B, D);
    }
}
