using System.Text;

namespace TradingGA;

// The signal study's analysis, separated from data fetching so it can be run on synthetic panels
// (SignalStudyTests: random-walk null, planted signal, causality). It answers four questions:
//
//   1. Which signals carry information about forward returns, at which horizon, in which BTC
//      regime?  → daily-clustered Spearman IC table with Benjamini-Hochberg q-values.
//   2. Do returns reverse or continue at each horizon and regime?  → variance ratios.
//   3. How many independent signals are there really?  → correlation matrix + PCA.
//   4. Does a single, small, FIXED model built from all of them forecast out of sample, after
//      costs?  → walk-forward ridge (forward return) and logistic (triple barrier).
// and, given strategy trades, 5. what each strategy selects on and what its returns load on.
//
// Nothing here is tuned: the ridge penalty, barrier width, thresholds and fold count are constants.
// That is deliberate — a study that searched over them would be one more GA with a smaller genome.
public static class SignalStudy
{
    public const int    WalkForwardBlocks = 6;
    public const double TargetClipAtr     = 10.0;   // winsorise forward returns for the ridge fit
    public const double LogitThreshold    = 0.05;   // trade when |P(up) − 0.5| > this
    public const int    LogitMaxTrainRows = 100_000;   // 19 coefficients: ample, and placebos stay affordable
    public const int    TrailingCentreDays = 30;

    public static readonly (string Name, byte? Code)[] Regimes =
    [
        ("All", null),
        ("Bull", (byte)MarketRegime.Bull), ("Bear", (byte)MarketRegime.Bear),
        ("Ranging", (byte)MarketRegime.Ranging), ("HighVol", (byte)MarketRegime.HighVol),
    ];

    public record IcRow(string Feature, int Horizon, string Regime, SignalStats.IcResult Ic, double Q);
    public record Pnl(int Trades, double MeanAtr, double HitRate, double T);
    // Placebo summary: the actual statistic's place in the shifted-target null distribution.
    public record PlaceboStat(int N, double Mean, double Sd, double P);
    public record RidgeResult(int Horizon, double[] FullB, double[] FullSe, double OosIc, double OosIcT,
                              int OosDays, double OosR2, Pnl Pnl,
                              List<(int DayLo, int DayHi, SignalStats.RidgeModel Model)> FoldModels,
                              PlaceboStat CorrPlacebo, PlaceboStat PnlPlacebo);
    public record LogitResult(double Auc, double BrierSkill, int TestRows, Pnl Pnl,
                              PlaceboStat AucPlacebo, PlaceboStat PnlPlacebo);
    public record PcaResult(double[] Eigen, double[] Vectors, double EffectiveN, double[] Corr);
    public record AttributionTrade(string Strategy, int Day, float[] X, double RetAtr, int Dir);
    public record AttributionResult(string Strategy, int N, double MeanRetAtr, double[] ProfileZ,
                                    double[] Ic, double[] IcT, double[] IcQ,
                                    int Covered, double AgreeFrac, double MeanAgree, double MeanDisagree);

    public sealed class Result
    {
        public List<IcRow> Ic = new();
        public List<VarianceRatioAccumulator.Result> VarianceRatios = new();
        public PcaResult? Pca;
        public List<RidgeResult> Ridge = new();
        public LogitResult? Logit;
        public List<AttributionResult> Attribution = new();
        public int Rows, Days, Coins, Placebos;
    }

    public static Result Analyze(SignalPanel panel, int stride, IReadOnlyList<AttributionTrade>? strategyTrades = null,
                                 bool verbose = false, int placebos = 0)
    {
        var res = new Result { Rows = panel.Rows, Coins = panel.CoinNames.Count };
        int p = SignalFeatures.Count, H = SignalFeatures.Horizons.Length;
        var allGroups = SignalStats.DayGroups(panel.Day);
        res.Days = allGroups.Length;
        void Log(string s) { if (verbose) Console.WriteLine(s); }

        // 1. IC table ───────────────────────────────────────────────────────────────────────
        Log("  [1/5] IC table…");
        // Dense day index per row, shared by every bootstrap.
        var sortedDaysAll = allGroups.Select(g => g.Day).ToArray();
        var denseDay = new Dictionary<int, int>(); for (int d = 0; d < sortedDaysAll.Length; d++) denseDay[sortedDaysAll[d]] = d;
        var rowDay = panel.Day.Select(d => denseDay[d]).ToArray();
        int D = sortedDaysAll.Length;

        var jobs = (from k in Enumerable.Range(0, p) from h in Enumerable.Range(0, H) from r in Regimes select (k, h, r)).ToArray();
        var ics = new SignalStats.IcResult[jobs.Length];
        foreach (var reg in Regimes)
        {
            var mask = panel.Mask(reg.Code);
            var xr = new double[p][]; var yr = new double[H][];
            Parallel.For(0, p, k => xr[k] = SignalStats.MaskedRanks(panel.X[k], mask));
            Parallel.For(0, H, h => yr[h] = SignalStats.MaskedRanks(panel.Fwd[h], mask));
            Parallel.For(0, jobs.Length, j =>
            {
                var (k, h, r) = jobs[j]; if (r.Name != reg.Name) return;
                ics[j] = SignalStats.PooledIc(xr[k], yr[h], rowDay, D, SignalStats.LagForHorizon(SignalFeatures.Horizons[h]),
                                              seed: 20261002 + j);
            });
        }
        var q = SignalStats.BenjaminiHochberg(ics.Select(x => x.P).ToArray());
        for (int j = 0; j < jobs.Length; j++)
            res.Ic.Add(new IcRow(SignalFeatures.Names[jobs[j].k], SignalFeatures.Horizons[jobs[j].h], jobs[j].r.Name, ics[j], q[j]));

        // 2. variance ratios ────────────────────────────────────────────────────────────────
        res.VarianceRatios = panel.VarianceRatios.Results().ToList();

        // Coins occupy contiguous row ranges in bar order (AddCoin appends a whole coin at once).
        var coinRanges = CoinRanges(panel);
        var identity = Enumerable.Range(0, panel.Rows).ToArray();
        var dayAggs = BuildDayAggs(panel, identity, denseDay, D);

        // 3. correlation + PCA ──────────────────────────────────────────────────────────────
        Log("  [2/5] correlation / PCA…");
        {
            var all = new SignalStats.Agg(p);
            foreach (var a in dayAggs[0]) all.AddAgg(a);
            var corr = new double[p * p];
            var mean = new double[p]; var sd = new double[p];
            for (int a = 0; a < p; a++) { mean[a] = all.Sx[a] / all.N; sd[a] = Math.Sqrt(Math.Max(0, all.Sxx[a * p + a] / all.N - mean[a] * mean[a])); }
            for (int a = 0; a < p; a++)
                for (int b = 0; b < p; b++)
                {
                    int lo = Math.Min(a, b), hi = Math.Max(a, b);
                    corr[a * p + b] = sd[a] > 1e-12 && sd[b] > 1e-12
                        ? (all.Sxx[lo * p + hi] / all.N - mean[a] * mean[b]) / (sd[a] * sd[b]) : (a == b ? 1 : 0);
                }
            var eig = CovarianceMatrix.EigenDecompose(corr, p);
            if (eig is { } e)
            {
                double s1 = e.Values.Sum(), s2 = e.Values.Sum(v => v * v);
                res.Pca = new PcaResult(e.Values, e.Vectors, s2 > 0 ? s1 * s1 / s2 : 0, corr);
            }
        }

        // 4. walk-forward models + placebo null ─────────────────────────────────────────────
        int purge = SignalStats.LagForHorizon(SignalFeatures.Horizons.Max()) + 1;
        var folds = SignalStats.WalkForwardFolds(sortedDaysAll, WalkForwardBlocks, purge);
        var ctx = new WfContext(panel, stride, folds, allGroups, denseDay, D, coinRanges);

        Log("  [3/5] ridge (full + walk-forward)…");
        var actualRidge = new RidgeWf[H];
        for (int h = 0; h < H; h++) actualRidge[h] = RunRidge(ctx, h, dayAggs[h], identity);
        Log("  [4/5] triple-barrier logistic (walk-forward)…");
        var actualLogit = RunLogit(ctx, identity);

        // PLACEBO NULL ON THE REAL PANEL. Every coin's targets are shifted by the SAME time offset
        // (circularly within the coin), which destroys the signal→outcome link while keeping each
        // series' autocorrelation AND the cross-coin co-movement — so the placebo has the real
        // panel's effective sample size, which no asymptotic t can be trusted to know. On the
        // synthetic null the analytic OOS t had sd 1.3-1.5 at h=24/72 instead of 1.0; the placebo
        // distribution is the calibration. One-sided p = (1 + #placebo ≥ actual) / (1 + N).
        var plRidge = Enumerable.Range(0, H).Select(_ => new List<RidgeWf>()).ToArray();
        var plLogit = new List<LogitWf>();
        if (placebos > 0) Log($"  placebo null: {placebos} shifted-target replays…");
        var prng = new Random(20261002);
        for (int b = 0; b < placebos; b++)
        {
            int shiftDays = prng.Next(Math.Min(45, D / 4), Math.Max(Math.Min(45, D / 4) + 1, D - Math.Min(45, D / 4)));
            var tgt = PlaceboMap(coinRanges, panel.Rows, shiftDays * 24 / Math.Max(1, stride));
            var pAggs = BuildDayAggs(panel, tgt, denseDay, D);
            for (int h = 0; h < H; h++) plRidge[h].Add(RunRidge(ctx, h, pAggs[h], tgt));
            plLogit.Add(RunLogit(ctx, tgt));
        }

        for (int h = 0; h < H; h++)
        {
            var full = new SignalStats.Agg(p); foreach (var a in dayAggs[h]) full.AddAgg(a);
            var fm = SignalStats.FitRidge(full);
            var se = SignalStats.BootstrapSe(dayAggs[h]);
            var w = actualRidge[h];
            res.Ridge.Add(new RidgeResult(SignalFeatures.Horizons[h], fm?.B ?? new double[p], se,
                w.Corr.Ic, w.Corr.T, w.Corr.Days, w.OosR2, w.Pnl, w.FoldModels,
                Placebo(w.Corr.Ic, plRidge[h].Select(z => z.Corr.Ic)),
                Placebo(w.Pnl.MeanAtr, plRidge[h].Select(z => z.Pnl.MeanAtr))));
        }
        res.Logit = new LogitResult(actualLogit.Auc, actualLogit.BrierSkill, actualLogit.TestRows, actualLogit.Pnl,
                                    Placebo(actualLogit.Auc, plLogit.Select(z => z.Auc)),
                                    Placebo(actualLogit.Pnl.MeanAtr, plLogit.Select(z => z.Pnl.MeanAtr)));
        res.Placebos = placebos;

        // 5. strategy attribution ──────────────────────────────────────────────────────────
        if (strategyTrades is { Count: > 0 })
        {
            Log("  [5/5] strategy attribution…");
            var all = new SignalStats.Agg(p); foreach (var a in dayAggs[0]) all.AddAgg(a);
            var pm = new double[p]; var ps = new double[p];
            for (int a = 0; a < p; a++) { pm[a] = all.Sx[a] / all.N; ps[a] = Math.Sqrt(Math.Max(0, all.Sxx[a * p + a] / all.N - pm[a] * pm[a])); }
            int h24 = Array.IndexOf(SignalFeatures.Horizons, 24);
            var foldModels24 = h24 >= 0 ? res.Ridge[h24].FoldModels : new();
            foreach (var grp in strategyTrades.GroupBy(t => t.Strategy).OrderBy(g => g.Key))
                res.Attribution.Add(Attribute(grp.Key, grp.ToList(), pm, ps, foldModels24));
        }
        return res;
    }

    // ── walk-forward machinery ───────────────────────────────────────────────────────────────
    private sealed record WfContext(SignalPanel Panel, int Stride, List<(int[] TrainDays, int[] TestDays)> Folds,
                                    (int Day, int[] Rows)[] AllGroups, Dictionary<int, int> DenseDay, int D,
                                    (int Start, int End)[] CoinRanges);
    private sealed record RidgeWf(SignalStats.IcResult Corr, double OosR2, Pnl Pnl,
                                  List<(int DayLo, int DayHi, SignalStats.RidgeModel Model)> FoldModels);
    private sealed record LogitWf(double Auc, double BrierSkill, int TestRows, Pnl Pnl);

    private static (int Start, int End)[] CoinRanges(SignalPanel panel)
    {
        var r = new List<(int, int)>(); int st = 0;
        for (int i = 1; i <= panel.Rows; i++)
            if (i == panel.Rows || panel.Coin[i] != panel.Coin[i - 1]) { r.Add((st, i)); st = i; }
        return r.ToArray();
    }

    // tgt[i] = the row whose labels stand in for row i's. Same ROW offset for every coin = the same
    // TIME offset, since every coin is sampled at the same stride.
    private static int[] PlaceboMap((int Start, int End)[] ranges, int rows, int shiftRows)
    {
        var t = new int[rows];
        foreach (var (st, en) in ranges)
        {
            int L = en - st; if (L == 0) continue;
            int k = ((shiftRows % L) + L) % L;
            for (int i = st; i < en; i++) t[i] = st + ((i - st + k) % L);
        }
        return t;
    }

    private static SignalStats.Agg[][] BuildDayAggs(SignalPanel panel, int[] tgt, Dictionary<int, int> denseDay, int D)
    {
        int p = SignalFeatures.Count, H = SignalFeatures.Horizons.Length;
        var aggs = new SignalStats.Agg[H][];
        for (int h = 0; h < H; h++) { aggs[h] = new SignalStats.Agg[D]; for (int d = 0; d < D; d++) aggs[h][d] = new SignalStats.Agg(p); }
        Parallel.For(0, H, h =>
        {
            var xx = new double[p];
            for (int i = 0; i < panel.Rows; i++)
            {
                double y = panel.Fwd[h][tgt[i]]; if (double.IsNaN(y)) continue;
                for (int k = 0; k < p; k++) xx[k] = panel.X[k][i];
                aggs[h][denseDay[panel.Day[i]]].Add(xx, Math.Clamp(y, -TargetClipAtr, TargetClipAtr));
            }
        });
        return aggs;
    }

    private static RidgeWf RunRidge(WfContext c, int h, SignalStats.Agg[] dayAggsH, int[] tgt)
    {
        var panel = c.Panel; int p = SignalFeatures.Count, hb = SignalFeatures.Horizons[h];
        var foldOfDay = new Dictionary<int, SignalStats.RidgeModel>();
        var foldModels = new List<(int, int, SignalStats.RidgeModel)>();
        foreach (var (trainDays, testDays) in c.Folds)
        {
            var tr = new SignalStats.Agg(p); foreach (int d in trainDays) tr.AddAgg(dayAggsH[c.DenseDay[d]]);
            var m = SignalStats.FitRidge(tr); if (m is null) continue;
            foldModels.Add((testDays[0], testDays[^1], m));
            foreach (int d in testDays) foldOfDay[d] = m;
        }

        // OOS correlation: prediction centred on its own TRAILING mean per coin (past predictions
        // only — no lookahead), target centred on the train mean. The trailing centre removes the
        // level offset a persistent feature carries into a test fold, which otherwise multiplies
        // that fold's drift into one slow term no day-bootstrap can see.
        var A = new List<double>(); var B = new List<double>(); var Dd = new List<int>();
        double sse = 0, sst = 0;
        var pnlByDay = new SortedDictionary<int, double>(); int trades = 0, wins = 0; double pnlSum = 0;
        int W = Math.Max(2, TrailingCentreDays * 24 / Math.Max(1, c.Stride));
        foreach (var (st, en) in c.CoinRanges)
        {
            var q = new Queue<double>(); double qs = 0;
            for (int i = st; i < en; i++)
            {
                if (!foldOfDay.TryGetValue(panel.Day[i], out var m)) { q.Clear(); qs = 0; continue; }
                double y = panel.Fwd[h][tgt[i]]; if (double.IsNaN(y)) continue;
                double pr = m.Predict(k => panel.X[k][i]);
                double yc = Math.Clamp(y, -TargetClipAtr, TargetClipAtr);
                sse += (yc - pr) * (yc - pr); sst += (yc - m.YMean) * (yc - m.YMean);
                double a = pr - m.YMean;
                if (q.Count >= W / 2) { A.Add(a - qs / q.Count); B.Add(yc - m.YMean); Dd.Add(c.DenseDay[panel.Day[i]]); }
                q.Enqueue(a); qs += a; if (q.Count > W) qs -= q.Dequeue();

                if (SignalStats.NonOverlapping(panel.Bar[i], hb, c.Stride) && Math.Abs(pr) > panel.CostAtr[i])
                {
                    double pnl = Math.Sign(pr) * y - panel.CostAtr[i];
                    pnlByDay[panel.Day[i]] = pnlByDay.GetValueOrDefault(panel.Day[i]) + pnl;
                    trades++; pnlSum += pnl; if (pnl > 0) wins++;
                }
            }
        }
        var corr = SignalStats.TrainCentredCorr(A, B, Dd, c.D, SignalStats.LagForHorizon(hb));
        var (_, pnlT) = SignalStats.NeweyWestT(pnlByDay.Values.ToList(), SignalStats.LagForHorizon(hb));
        return new RidgeWf(corr, sst > 0 ? 1 - sse / sst : double.NaN,
                           new Pnl(trades, trades > 0 ? pnlSum / trades : double.NaN, trades > 0 ? wins / (double)trades : double.NaN, pnlT),
                           foldModels);
    }

    private static LogitWf RunLogit(WfContext c, int[] tgt)
    {
        var panel = c.Panel; int p = SignalFeatures.Count;
        var scores = new List<double>(); var labels = new List<int>(); double brier = 0, brierBase = 0;
        var pnlByDay = new SortedDictionary<int, double>(); int trades = 0, wins = 0; double pnlSum = 0;
        foreach (var (trainDays, testDays) in c.Folds)
        {
            var trainSet = trainDays.ToHashSet(); var testSet = testDays.ToHashSet();
            var trainIdx = new List<int>();
            for (int i = 0; i < panel.Rows; i++)
                if (trainSet.Contains(panel.Day[i]) && panel.TbLabel[tgt[i]] is 1 or -1) trainIdx.Add(i);
            int every = Math.Max(1, trainIdx.Count / LogitMaxTrainRows);
            var Xtr = new List<double[]>(); var ytr = new List<int>();
            for (int j = 0; j < trainIdx.Count; j += every)
            {
                int i = trainIdx[j];
                var row = new double[p]; for (int k = 0; k < p; k++) row[k] = panel.X[k][i];
                Xtr.Add(row); ytr.Add(panel.TbLabel[tgt[i]] == 1 ? 1 : 0);
            }
            var m = SignalStats.FitLogit(Xtr, ytr); if (m is null) continue;
            double baseRate = ytr.Average();
            for (int i = 0; i < panel.Rows; i++)
            {
                if (!testSet.Contains(panel.Day[i])) continue;
                int lab = panel.TbLabel[tgt[i]];
                double pu = m.Prob(k => panel.X[k][i]);
                if (lab is 1 or -1)
                {
                    int yb = lab == 1 ? 1 : 0;
                    scores.Add(pu); labels.Add(yb);
                    brier += (pu - yb) * (pu - yb); brierBase += (baseRate - yb) * (baseRate - yb);
                }
                if (!SignalStats.NonOverlapping(panel.Bar[i], SignalFeatures.BarrierHorizon, c.Stride)) continue;
                int side = pu > 0.5 + LogitThreshold ? 1 : pu < 0.5 - LogitThreshold ? -1 : 0;
                if (side == 0) continue;
                double g = lab == side ? SignalFeatures.BarrierAtr
                         : lab == -side || lab == 2 ? -SignalFeatures.BarrierAtr   // tie = loss, always
                         : side * panel.TbRet[tgt[i]];
                double pnl = g - panel.CostAtr[i];
                pnlByDay[panel.Day[i]] = pnlByDay.GetValueOrDefault(panel.Day[i]) + pnl;
                trades++; pnlSum += pnl; if (pnl > 0) wins++;
            }
        }
        var (_, pnlT) = SignalStats.NeweyWestT(pnlByDay.Values.ToList(), SignalStats.LagForHorizon(SignalFeatures.BarrierHorizon));
        return new LogitWf(SignalStats.Auc(scores, labels), brierBase > 0 ? 1 - brier / brierBase : double.NaN, labels.Count,
                           new Pnl(trades, trades > 0 ? pnlSum / trades : double.NaN, trades > 0 ? wins / (double)trades : double.NaN, pnlT));
    }

    private static PlaceboStat Placebo(double actual, IEnumerable<double> nullDraws)
    {
        var v = nullDraws.Where(d => !double.IsNaN(d)).ToList();
        if (v.Count == 0 || double.IsNaN(actual)) return new PlaceboStat(v.Count, double.NaN, double.NaN, double.NaN);
        double m = v.Average(), sd = v.Count > 1 ? Math.Sqrt(v.Sum(z => (z - m) * (z - m)) / (v.Count - 1)) : double.NaN;
        return new PlaceboStat(v.Count, m, sd, (1.0 + v.Count(z => z >= actual)) / (1.0 + v.Count));
    }

    private static AttributionResult Attribute(string name, List<AttributionTrade> tr, double[] pm, double[] ps,
                                               List<(int DayLo, int DayHi, SignalStats.RidgeModel Model)> foldModels)
    {
        int p = SignalFeatures.Count, n = tr.Count;
        var prof = new double[p]; var ic = new double[p]; var icT = new double[p]; var pv = new double[p];
        var rets = tr.Select(t => t.RetAtr).ToList();
        // Day-block bootstrap of each feature's pooled Spearman with the trade return.
        var byDay = tr.Select((t, i) => (t.Day, i)).GroupBy(z => z.Day).OrderBy(g => g.Key).Select(g => g.Select(z => z.i).ToArray()).ToArray();
        var rng = new Random(20261002);
        for (int k = 0; k < p; k++)
        {
            int kk = k;
            prof[k] = ps[k] > 1e-12 ? tr.Average(t => (t.X[kk] - pm[kk]) / ps[kk]) : 0;
            var xs = tr.Select(t => (double)t.X[kk]).ToList();
            ic[k] = SignalStats.Spearman(xs, rets);
            var draws = new List<double>();
            const int block = 5;
            if (byDay.Length >= 2 * block && !double.IsNaN(ic[k]))
                for (int r = 0; r < 200; r++)
                {
                    var bx = new List<double>(); var by = new List<double>();
                    for (int filled = 0; filled < byDay.Length; filled += block)
                    {
                        int s = rng.Next(0, byDay.Length - block + 1);
                        for (int b = 0; b < block; b++) foreach (int i in byDay[s + b]) { bx.Add(xs[i]); by.Add(rets[i]); }
                    }
                    double v = SignalStats.Spearman(bx, by); if (!double.IsNaN(v)) draws.Add(v);
                }
            double se = draws.Count > 10 ? Math.Sqrt(draws.Sum(d => (d - draws.Average()) * (d - draws.Average())) / (draws.Count - 1)) : double.NaN;
            icT[k] = se > 1e-12 ? ic[k] / se : double.NaN;
            pv[k] = SignalStats.TwoSidedP(icT[k]);
        }
        var q = SignalStats.BenjaminiHochberg(pv);

        // Agreement with the OUT-OF-SAMPLE panel model (the fold whose test block holds the day).
        int covered = 0, agree = 0; double sa = 0, sd = 0; int na = 0, nd = 0;
        foreach (var t in tr)
        {
            var fmod = foldModels.FirstOrDefault(f => t.Day >= f.DayLo && t.Day <= f.DayHi);
            if (fmod.Model is null) continue;
            covered++;
            double pr = fmod.Model.Predict(k => t.X[k]);
            if (Math.Sign(pr) == t.Dir) { agree++; sa += t.RetAtr; na++; } else { sd += t.RetAtr; nd++; }
        }
        return new AttributionResult(name, n, rets.Average(), prof, ic, icT, q, covered,
                                     covered > 0 ? agree / (double)covered : double.NaN,
                                     na > 0 ? sa / na : double.NaN, nd > 0 ? sd / nd : double.NaN);
    }

    // ── report ───────────────────────────────────────────────────────────────────────────────
    public static string Format(Result r)
    {
        var sb = new StringBuilder(); var F = SignalFeatures.Names; int p = F.Length;
        void L(string s = "") => sb.AppendLine(s);
        string Star(double qv) => qv < 0.01 ? "**" : qv < 0.05 ? "* " : "  ";

        L($"Panel: {r.Rows:N0} rows · {r.Coins} coins · {r.Days} days. Forward returns in ATR(14) units from the t+1 open.");
        L("Inference is clustered by DAY (moving-block bootstrap over days); q = Benjamini-Hochberg over the whole IC table.");
        L();
        L("── 1. INFORMATION COEFFICIENTS, all regimes (pooled Spearman ×100, day-block bootstrap t, * q<.05 ** q<.01) ──");
        L($"  {"feature",-15}" + string.Concat(SignalFeatures.Horizons.Select(h => $"{"h=" + h,22}")));
        foreach (var f in F)
            L($"  {f,-15}" + string.Concat(SignalFeatures.Horizons.Select(h =>
            {
                var row = r.Ic.First(x => x.Feature == f && x.Horizon == h && x.Regime == "All");
                return $"{row.Ic.Ic * 100,9:F2} t={row.Ic.T,6:F2} {Star(row.Q)}";
            })));
        L();
        L("── 1b. IC BY BTC REGIME, h=24 (a signal whose sign FLIPS across regimes is a regime bet, not a signal) ──");
        L($"  {"feature",-15}" + string.Concat(Regimes.Skip(1).Select(g => $"{g.Name,20}")));
        foreach (var f in F)
            L($"  {f,-15}" + string.Concat(Regimes.Skip(1).Select(g =>
            {
                var row = r.Ic.FirstOrDefault(x => x.Feature == f && x.Horizon == 24 && x.Regime == g.Name);
                return row is null || double.IsNaN(row.Ic.T) ? $"{"— thin",20}" : $"{row.Ic.Ic * 100,8:F2} t={row.Ic.T,5:F1} {Star(row.Q)}";
            })));
        L($"  significant at q<.05: {r.Ic.Count(x => x.Q < 0.05)} of {r.Ic.Count} tests " +
          $"(\"— thin\" = fewer than {SignalStats.MinBlocks} day-blocks in that regime: no inference attempted)");
        L();
        L("── 2. VARIANCE RATIOS (VR<1 reversal → fades/grids paid · VR>1 continuation → pullback/trend paid) ──");
        L($"  {"regime",-9}{"q",4}{"pooled VR",11}{"median coin",13}{"% coins<1",11}{"coins",7}");
        foreach (var v in r.VarianceRatios)
        {
            string rn = v.Regime == VarianceRatioAccumulator.AllRegimes ? "All" : ((MarketRegime)v.Regime).ToString();
            L($"  {rn,-9}{v.Q,4}{v.PooledVr,11:F3}{v.MedianCoinVr,13:F3}{v.FracCoinsBelow1 * 100,10:F0}%{v.Coins,7}");
        }
        L();
        if (r.Pca is { } pca)
        {
            L($"── 3. SIGNAL REDUNDANCY — {p} signals, effective number {pca.EffectiveN:F1} ──");
            double tot = pca.Eigen.Sum();
            for (int c = 0; c < Math.Min(5, p); c++)
            {
                var top = Enumerable.Range(0, p).OrderByDescending(i => Math.Abs(pca.Vectors[i * p + c])).Take(5)
                                    .Select(i => $"{F[i]}{(pca.Vectors[i * p + c] >= 0 ? "+" : "−")}");
                L($"  PC{c + 1}: {pca.Eigen[c] / tot * 100,5:F1}% var · {string.Join(" ", top)}");
            }
            L("  most correlated pairs:");
            var pairs = (from a in Enumerable.Range(0, p) from b in Enumerable.Range(a + 1, p - a - 1)
                         select (a, b, rho: pca.Corr[a * p + b])).OrderByDescending(z => Math.Abs(z.rho)).Take(8);
            foreach (var (a, b, rho) in pairs) L($"    {F[a],-15} {F[b],-15} ρ={rho,6:F2}");
            L();
        }
        L("── 4. ONE FIXED RIDGE MODEL ON ALL SIGNALS (standardised β = ATRs per 1σ; t from day-block bootstrap) ──");
        string Pl(SignalStudy.PlaceboStat ps, string fmt) =>
            r.Placebos == 0 ? "no placebo run (--placebos 0)"
          : ps.N == 0 ? $"none of {r.Placebos} placebo replays produced a value (e.g. never cleared costs)"
            : $"placebo {ps.Mean.ToString(fmt)}±{ps.Sd.ToString(fmt)}, p={ps.P:F3} (N={ps.N})";
        foreach (var m in r.Ridge)
        {
            L($"  h={m.Horizon}: walk-forward OOS corr {m.OosIc * 100:F2}% (t={m.OosIcT:F2}, {m.OosDays} days; {Pl(m.CorrPlacebo, "P2")}) · OOS R² {m.OosR2 * 100:F3}%");
            L($"        trade |pred|>cost: n={m.Pnl.Trades} net {m.Pnl.MeanAtr:F3} ATR/trade, hit {m.Pnl.HitRate * 100:F1}%, t={m.Pnl.T:F2}; {Pl(m.PnlPlacebo, "F3")}");
            var top = Enumerable.Range(0, p).OrderByDescending(k => Math.Abs(m.FullB[k] / (m.FullSe[k] is > 0 ? m.FullSe[k] : 1e9))).Take(6);
            L("     " + string.Join("  ", top.Select(k => $"{F[k]} {m.FullB[k]:+0.000;−0.000}(t={m.FullB[k] / m.FullSe[k]:F1})")));
        }
        if (r.Logit is { } lg)
        {
            L($"  triple barrier ±{SignalFeatures.BarrierAtr}ATR/{SignalFeatures.BarrierHorizon}h logistic, OOS: AUC {lg.Auc:F3} ({Pl(lg.AucPlacebo, "F3")}) · Brier skill {lg.BrierSkill * 100:F2}% · n={lg.TestRows}");
            L($"        trade |P−½|>{LogitThreshold}: n={lg.Pnl.Trades} net {lg.Pnl.MeanAtr:F3} ATR/trade, hit {lg.Pnl.HitRate * 100:F1}%, t={lg.Pnl.T:F2}; {Pl(lg.PnlPlacebo, "F3")}");
        }
        L("  READ THE PLACEBO p, NOT THE t. Placebo = every coin's targets shifted by one common time offset;");
        L("  it keeps the panel's autocorrelation and co-movement, so it knows the real effective sample size.");
        L("  (OOS R² is against the TRAIN-fold mean; ~0.5-2% is realistic for hourly crypto and can still pay.)");
        L();
        if (r.Attribution.Count > 0)
        {
            L("── 5. STRATEGY ATTRIBUTION (OOS coins; entry features = last CLOSED h1 bar before entry) ──");
            foreach (var a in r.Attribution)
            {
                L($"  {a.Strategy}: n={a.N}  mean {a.MeanRetAtr:F3} ATR/trade");
                var prof = Enumerable.Range(0, p).OrderByDescending(k => Math.Abs(a.ProfileZ[k])).Take(6);
                L("     selects on (mean z at entry): " + string.Join("  ", prof.Select(k => $"{F[k]} {a.ProfileZ[k]:+0.00;−0.00}")));
                var ret = Enumerable.Range(0, p).Where(k => !double.IsNaN(a.IcT[k])).OrderByDescending(k => Math.Abs(a.IcT[k])).Take(5);
                L("     returns load on (IC, t, q):   " + string.Join("  ", ret.Select(k => $"{F[k]} {a.Ic[k]:+0.000;−0.000}(t={a.IcT[k]:F1}{(a.IcQ[k] < 0.05 ? "*" : "")})")));
                L($"     OOS panel model (h=24) agrees with trade direction on {a.AgreeFrac * 100:F0}% of {a.Covered} covered trades · " +
                  $"mean ret when agree {a.MeanAgree:F3} vs disagree {a.MeanDisagree:F3} ATR");
            }
        }
        return sb.ToString();
    }

    public static string IcCsv(Result r)
    {
        var sb = new StringBuilder("feature,horizon,regime,mean_ic,t,p,q,days,rows\n");
        foreach (var x in r.Ic)
            sb.AppendLine(string.Join(",", x.Feature, x.Horizon, x.Regime,
                x.Ic.Ic.ToString("R"), x.Ic.T.ToString("R"), x.Ic.P.ToString("R"), x.Q.ToString("R"), x.Ic.Days, x.Ic.Rows));
        return sb.ToString();
    }
}
