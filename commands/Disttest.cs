using Bybit.Net.Clients;

namespace TradingGA;

// Historically the system's edge is validated only via `combinedbacktest` / `oosbacktest` on the
// realised-trade lists. Those answer "did the chosen trades win?" — they never ask the prior
// question "could a live predictive distribution have separated winners from losers BEFORE entry?"
// This command builds that distribution at every historical bar (no lookahead — candidates are
// strictly before the query bar) and checks two things:
//
//   1. CALIBRATION  — are realised next-h1 moves spread like the predicted distribution says?
//   2. SEPARATION   — if we had gated entries on predicted ProbPositive / mean, would the kept
//                     bucket have measurably better EDGE (PF/Sharpe) and RIGHT TAIL / RISK
//                     (CVaR, downside) than the always-on baseline?
//
// It is the option-A gate from the build plan: if separation is absent, wiring the distribution
// into a live entry path is pointless. Both edge AND risk are reported per bucket.
//
// Usage:  dotnet run -- disttest [numCoins] [tradesCsv]
//   numCoins: how many BacktestCoins to sample (default 12). BTC is always included.
//   tradesCsv: optional CSV of (entry_time,symbol,return_pct) — typically a combinedbacktest export.
//              If given, each trade is tagged with the predicted distribution at its entry bar, and
//              the same edge/risk bucketing runs over TRADES instead of raw bars.
static class Disttest
{
    public static async Task Run(BybitRestClient client, string[]? args = null)
    {
        // Program.cs passes the full argv (mode at index 0), so positional args begin at 1.
        const int defaultCoins = 12;
        int numCoins = defaultCoins;
        string? tradesCsv = null;
        bool localCache = false;
        if (args is { Length: > 1 } && args[1].Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            localCache = true;
            if (args.Length > 2 && int.TryParse(args[2], out int c) && c > 0) numCoins = c;
            if (args.Length > 3) tradesCsv = args[3];
        }
        else
        {
            if (args is { Length: > 1 } && int.TryParse(args[1], out int c) && c > 0) numCoins = c;
            if (args is { Length: > 2 }) tradesCsv = args[2];
        }

        Console.WriteLine($"=== Gravity-gen2 | DISTTEST (predictive distribution calibration + separation) ===\n");
        Console.WriteLine($"  sample: BTC + first {numCoins - 1} of {Config.BacktestCoins.Length} BacktestCoins  [{(localCache ? "local cache only (no API)" : "network fetch")}]");
        if (tradesCsv != null) Console.WriteLine($"  trade overlay: {tradesCsv}");

        // Coin set for Part 1/2 (raw-bar calibration): BTC + a spread of BacktestCoins.
        var coins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BTCUSDT" };
        foreach (var sym in Config.BacktestCoins.Skip(1))
        {
            if (coins.Count >= numCoins) break;
            coins.Add(sym);
        }

        // If a trade overlay is requested, its symbols MUST all be predicted too, otherwise the
        // overlay silently tags nothing and prints a misleading "skipped N". Union them in — this
        // is what makes the per-strategy answer possible instead of a raw-bar-only one.
        var overlaySymbols = new List<string>();
        if (tradesCsv != null && System.IO.File.Exists(tradesCsv))
        {
            overlaySymbols = CsvSymbols(tradesCsv);
            foreach (var s in overlaySymbols) coins.Add(s);
            Console.WriteLine($"  overlay adds {overlaySymbols.Count} symbol(s) → total {coins.Count} to {(localCache ? "read from cache" : "fetch")}");
        }

        // Overlay-only mode: skip Part 1/2 entirely and only tag the trade list, computing a single
        // prediction per trade via PredictAt — this is O(#trades × lookback), not O(nbars × lookback),
        // so a 42-symbol overlay runs in seconds instead of minutes. This is the fast path for the
        // gating question, which only ever needs predictions at entry bars.
        if (localCache && tradesCsv != null && System.IO.File.Exists(tradesCsv))
        {
            Console.WriteLine($"\n── Trade overlay ({tradesCsv}) — distribution at each trade's entry bar (overlay-only) ──\n");
            TagTradesLazy(tradesCsv);
            return;
        }

        var sem = new SemaphoreSlim(4);
        var coinTasks = coins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                // localCache: read the on-disk 15m cache only — no API. Makes re-running the overlay
                // (the iteration step for the gating question) instant once caches exist. Missing
                // cache → (null, empty) → skipped downstream.
                var m15 = localCache
                    ? LoadM15Cache(sym)
                    : await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 12);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                if (h1.Length < PredictiveDistribution.Warmup + PredictiveDistribution.MinSamples + 1) return (sym, h1, (PredictiveDistribution.BarPrediction[]?)null);
                var preds = PredictiveDistribution.ComputeValidated(h1);
                return (sym, h1, preds.ToArray());
            }
            finally { sem.Release(); }
        }).ToList();
        var coinData = (await Task.WhenAll(coinTasks)).Where(x => x.h1.Length > 0).ToList();

        if (coinData.Count == 0) { Console.WriteLine("  no coin data fetched."); return; }

        // ── 1. Bar-level calibration + separation ─────────────────────────────────
        Console.WriteLine("\n── Part 1: bar-level calibration over all sampled coins ──────────────\n");
        var bars = new List<PredictiveDistribution.BarPrediction>();
        foreach (var (_, _, preds) in coinData)
            if (preds is { Length: > 0 }) bars.AddRange(preds);
        Console.WriteLine($"  {bars.Count} predicted bars across {coinData.Count} coins");

        ReportCalibration(bars);

        // ── 2. Bucket by predicted direction probability → edge & risk ────────────
        Console.WriteLine("\n── Part 2: bucketed separation, raw bars (would gating on the distribution help?) —\n");
        ReportSeparation(bars.Select(b => new Row(b.MeanPct, b.StdPct, b.ProbPositive, b.CVaR5Pct, b.RealizedPct)).ToArray(), "bars");

        // ── 3. Optional: overlay an actual trade list ─────────────────────────────
        if (tradesCsv != null && System.IO.File.Exists(tradesCsv))
        {
            Console.WriteLine($"\n── Part 3: trade overlay ({tradesCsv}) — distribution at each trade's entry bar ──\n");
            TagTrades(tradesCsv, coinData);
        }
        else if (tradesCsv != null)
        {
            Console.WriteLine($"\n  ⚠ trades CSV not found: {tradesCsv} — skipping trade overlay.\n");
        }
    }

    // Calibration: the predicted distribution should put ~X% of realised moves within symmetric
    // bands derived from predicted StdPct, when the estimator is honest. Also prints the fraction
    // of realised returns inside [mean±1σ] and mean-track records realised mean ~ predicted.
    private static void ReportCalibration(List<PredictiveDistribution.BarPrediction> bars)
    {
        if (bars.Count == 0) { Console.WriteLine("  no predicted bars."); return; }

        foreach (var band in new[] { 0.5, 1.0, 1.5 })
        {
            // Fraction of bars whose signed distance |realized - mean| <= band*StdPct.
            int inside = 0;
            foreach (var b in bars)
            {
                if (b.StdPct <= 1e-9) continue;
                if (Math.Abs(b.RealizedPct - b.MeanPct) <= band * b.StdPct) inside++;
            }
            double frac = (double)inside / bars.Count;
            // Under a normal model, |z|<=0.67 ~ 50%, |z|<=1 ~ 68%, |z|<=1.5 ~ 87%. We don't assume
            // normality, but these are useful reference points. Honest = realised std often > predicted
            // std (fat tails), so "inside" will run lower than the normal reference.
            var (lo, hi) = NormalBand(band);
            Console.WriteLine($"  |realized − mean| ≤ {band:0.0}×σpred : {frac,7:P1}   (normal ref {lo:P0}–{hi:P0})");
        }

        // Predicted mean vs realised mean (sign-weighted mean of (realized * sign(predMean))).
        double posSum = 0, negSum = 0; int posN = 0, negN = 0;
        foreach (var b in bars)
        {
            if (b.MeanPct > 0) { posSum += b.RealizedPct; posN++; }
            else if (b.MeanPct < 0) { negSum += b.RealizedPct; negN++; }
        }
        if (posN > 0) Console.WriteLine($"  bars pred MeanPct>0 : n={posN,6}  realised avg = {posSum / posN:+0.000;-0.000}%");
        if (negN > 0) Console.WriteLine($"  bars pred MeanPct<0 : n={negN,6}  realised avg = {negSum / negN:+0.000;-0.000}%");
    }

    // One row = a predicted distribution at a point in time, paired with the realised outcome.
    private readonly record struct Row(double MeanPct, double StdPct, double ProbPos, double CVaR5, double Realized);

    // Bucket rows by predicted ProbPositive quintiles, then report per-bucket EDGE (mean, PF, Sharpe)
    // and RISK (downside, CVaR5). Baseline = all rows pooled. Quintiles BEFORE splitting so the group
    // sizes are balanced and no arbitrary threshold is imposed.
    private static void ReportSeparation(IReadOnlyList<Row> rows, string label)
    {
        if (rows.Count == 0) { Console.WriteLine($"  no {label} to bucket."); return; }
        ReportBucket(rows, "ALL (always-on baseline)");

        var sorted = rows.Select(r => r.ProbPos).OrderBy(x => x).ToArray();
        var cut = new double[4];
        for (int k = 0; k < 4; k++)
            cut[k] = sorted[Math.Clamp((int)((k + 1) * sorted.Length / 5.0), 0, sorted.Length - 1)];

        // Print Q5 (highest predicted up-probability) and Q1 (lowest) — the two extremes are where a
        // separation edge would show first.
        ReportBucket(rows.Where(r => r.ProbPos >= cut[3]).ToList(), "Q5 (highest pred P+)");
        ReportBucket(rows.Where(r => r.ProbPos <= cut[0]).ToList(), "Q1 (lowest pred P+)");

        // Continuous correlation between predicted ProbPositive and realised return.
        double rho = Spearman(rows.Select(r => r.ProbPos).ToArray(), rows.Select(r => r.Realized).ToArray());
        Console.WriteLine($"  Spearman ρ(pred P+, realised return) = {rho:+0.000;-0.000}");
    }

    private static void ReportBucket(IReadOnlyList<Row> rows, string label)
    {
        if (rows.Count < 5) { Console.WriteLine($"  {label,-36} n={rows.Count,5}  (skip <5)"); return; }
        var r = rows.Select(x => x.Realized).ToArray();
        double mean  = r.Average();
        double wins  = r.Where(x => x > 0).Sum();
        double loss  = Math.Abs(r.Where(x => x <= 0).Sum());
        double pf    = loss > 1e-9 ? wins / loss : (wins > 0 ? 99.0 : 0.0);
        double sd    = Std(r);
        double sharpe = sd > 1e-9 ? mean / sd : 0;
        double down  = Downside(r);
        double sortino = down > 1e-9 ? mean / down : 0;
        var sorted   = r.OrderBy(x => x).ToArray();
        double cvar  = sorted.Take(Math.Max(1, (int)(r.Length * 0.05))).Average();

        Console.WriteLine($"  {label,-36} n={r.Length,5}  mean={mean,7:F3}%  PF={pf,5:F2}  Sharpe={sharpe,6:F2}  " +
                          $"Sortino={sortino,6:F2}  CVaR5={cvar,7:F2}%");
    }

    // THE gating question: would filtering on predicted P+ IMPROVE the net book?
    //
    // A gate keeps only the fraction of trades whose P+ is high enough. Per-trade edge rises, but
    // volume falls. The gate is only "worth it" if keptMean × keptCount exceeds baselineMean ×
    // baselineCount (equal per-trade size is the honest first-order assumption — the book sizes
    // every entry near the same posFrac). This is the number that decides whether Part 3's
    // separation is actionable, so it is reported both as the fraction of baseline net kept, and as
    // a curve across thresholds (translation: how selective can you be before the volume loss wins).
    private static void ReportGating(IReadOnlyList<Row> rows, string label)
    {
        if (rows.Count < 20) { Console.WriteLine($"  gating: insufficient {label} (n={rows.Count})."); return; }

        double baseMean = rows.Average(r => r.Realized);
        double baseNet  = baseMean * rows.Count;

        Console.WriteLine($"  ── GATING (would keeping only high-P+ trades beat always-on?) ──");
        Console.WriteLine($"  baseline: n={rows.Count,5}  mean={baseMean:+0.000;-0.000}%  net={baseNet:+0.0;-0.0}%");
        Console.WriteLine($"  {"keep",7}  {"thresh",7}  {"n",5}  {"mean%",8}  {"PF",6}  {"Sortino",7}  {"CVaR5%",7}  {"net%",8}  {"% of base",8}");

        // Sort by P+ descending; sweep the retention headroom.
        var byP = rows.OrderByDescending(r => r.ProbPos).ToList();

        // Fraction-of-kept curve: how much of the book does a P+ top-fraction retain in net return?
        foreach (var frac in new double[] { 1.00, 0.80, 0.60, 0.50, 0.40, 0.25, 0.10 })
        {
            int keepN = Math.Max(1, (int)Math.Round(rows.Count * frac));
            double minP = byP[keepN - 1].ProbPos;
            var kept = byP.Take(keepN).ToArray();
            double km = kept.Average(k => k.Realized);
            double kn = kept.Sum(k => k.Realized);
            double net = km * keepN;
            double pf = Pf(kept.Select(k => k.Realized));
            double sort = Sortino(kept.Select(k => k.Realized).ToArray());
            double cv = CVaR5(kept.Select(k => k.Realized).ToArray());
            Console.WriteLine($"  {frac,6:P0}  {minP,7:F3}  {keepN,5}  {km,8:F3}  {pf,6:F2}  {sort,7:F2}  {cv,7:F2}  {net,8:F1}  {net / (baseNet > 1e-12 ? baseNet : 1),8:P0}");
            _ = kn;
        }

        // Best single threshold: the top-K (by P+) that maximises net return.
        double bestNet = double.MinValue; int bestK = 0; double bestPF = 0; double bestSort = 0; double bestCV = 0;
        double runningNet = 0;
        for (int k = 1; k <= byP.Count; k++)
        {
            runningNet += byP[k - 1].Realized;
            if (runningNet > bestNet) { bestNet = runningNet; bestK = k; }
        }
        var bestSet = byP.Take(bestK).ToArray();
        bestPF   = Pf(bestSet.Select(x => x.Realized));
        bestSort = Sortino(bestSet.Select(x => x.Realized).ToArray());
        bestCV   = CVaR5(bestSet.Select(x => x.Realized).ToArray());
        double bestFrac = (double)bestK / rows.Count;
        double bestMean = bestSet.Average(x => x.Realized);
        Console.WriteLine($"  BEST single gate: top {bestK} ({bestFrac:P0} of trades), P+ ≥ {byP[bestK - 1].ProbPos:F3} → " +
                          $"mean={bestMean:+0.000}%  PF={bestPF:F2}  Sortino={bestSort:F2}  CVaR5={bestCV:F2}%  net={bestNet:+0.0;-0.0}% ({bestNet / (baseNet > 1e-12 ? baseNet : 1):P0} of baseline)");
        bool improves = bestNet > baseNet * 1.05;
        Console.WriteLine($"  → gating {(improves ? "IMPROVES" : "does NOT improve")} net return vs always-on ({bestNet / (baseNet > 1e-12 ? baseNet : 1):P0} of baseline).");
    }

    private static double Pf(IEnumerable<double> r)
    {
        var a = r.ToArray();
        double w = a.Where(x => x > 0).Sum(), l = Math.Abs(a.Where(x => x <= 0).Sum());
        return l > 1e-9 ? w / l : (w > 0 ? 99.0 : 0.0);
    }

    private static double CVaR5(IReadOnlyList<double> r)
    {
        if (r.Count == 0) return 0;
        var s = r.OrderBy(x => x).ToArray();
        return s.Take(Math.Max(1, (int)(s.Length * 0.05))).Average();
    }

    private static double Sortino(double[] r)
    {
        if (r.Length < 2) return 0;
        double mean = r.Average();
        double down = Downside(r);
        return down > 1e-9 ? mean / down : 0;
    }

    // Tag a trade list with the predicted distribution at each trade's ENTRY bar, then bucket by it.
    private static void TagTrades(string path, List<(string Sym, Candle[] H1, PredictiveDistribution.BarPrediction[]? Preds)> coinData)
    {
        var lines = System.IO.File.ReadAllLines(path);
        var header = lines.FirstOrDefault()?.Split(',');
        int iTime = 0, iSym = 1, iRet = 2;
        if (header is { Length: > 0 })
        {
            for (int i = 0; i < header.Length; i++)
            {
                var h = header[i].Trim().ToLowerInvariant();
                if (h == "entry_time") iTime = i;
                else if (h == "symbol") iSym = i;
                else if (h == "return_pct" || h == "return") iRet = i;
            }
        }

        // Index of the nearest preceding predicted bar for each query time, per symbol.
        var bySym = coinData.ToDictionary(c => c.Sym.ToUpperInvariant(), c => c.Preds);
        var rows = new List<Row>();
        int tagged = 0, skipped = 0;

        foreach (var line in lines.Skip(header != null ? 1 : 0))
        {
            var p = line.Split(',');
            if (p.Length <= Math.Max(iTime, Math.Max(iSym, iRet))) { skipped++; continue; }
            if (!DateTime.TryParse(p[iTime], out DateTime t)) { skipped++; continue; }
            string sym = p[iSym].ToUpperInvariant();
            if (!double.TryParse(p[iRet], System.Globalization.CultureInfo.InvariantCulture, out double ret))
            { skipped++; continue; }

            if (!bySym.TryGetValue(sym, out var preds) || preds is not { Length: > 0 }) { skipped++; continue; }

            // Nearest prediction AT OR BEFORE entry time.
            PredictiveDistribution.BarPrediction best = default;
            bool found = false;
            foreach (var bp in preds)
            {
                if (bp.Time > t) break;
                best = bp; found = true;
            }
            if (!found) { skipped++; continue; }

            rows.Add(new Row(best.MeanPct, best.StdPct, best.ProbPositive, best.CVaR5Pct, ret));
            tagged++;
        }

        Console.WriteLine($"  tagged {tagged} trades, skipped {skipped} (no entry-bar prediction)");
        ReportSeparation(rows, "trades");
        ReportGating(rows, "trades");
    }

    // Overlay-only variant: loads each symbol's h1 from cache lazily (one aggregate per symbol) and
    // calls PredictiveDistribution.PredictAt per trade — O(#trades × lookback) instead of the full
    // per-bar pass. This is the fast iteration path for the gating question.
    private static void TagTradesLazy(string path)
    {
        var lines = System.IO.File.ReadAllLines(path);
        var header = lines.FirstOrDefault()?.Split(',');
        int iTime = 0, iSym = 1, iRet = 2;
        if (header is { Length: > 0 })
        {
            for (int i = 0; i < header.Length; i++)
            {
                var h = header[i].Trim().ToLowerInvariant();
                if (h == "entry_time") iTime = i;
                else if (h == "symbol") iSym = i;
                else if (h == "return_pct" || h == "return") iRet = i;
            }
        }

        // Lazy per-symbol prepared context, built once (O(n)) and queried O(lookback) per trade.
        var prepared = new Dictionary<string, PredictiveDistribution.Prepared>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<Row>();
        int tagged = 0, skipped = 0, noCache = 0;

        foreach (var line in lines.Skip(header != null ? 1 : 0))
        {
            var p = line.Split(',');
            if (p.Length <= Math.Max(iTime, Math.Max(iSym, iRet))) { skipped++; continue; }
            if (!DateTime.TryParse(p[iTime], out DateTime t)) { skipped++; continue; }
            string sym = p[iSym].ToUpperInvariant();
            if (!double.TryParse(p[iRet], System.Globalization.CultureInfo.InvariantCulture, out double ret))
            { skipped++; continue; }

            if (!prepared.TryGetValue(sym, out var prep))
            {
                var m15 = LoadM15Cache(sym);
                var h1 = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                prep = PredictiveDistribution.Prepare(h1);
                prepared[sym] = prep;
            }
            if (!prep.Usable) { noCache++; skipped++; continue; }

            var dist = prep.Query(t);
            if (dist.IsNeutral) { skipped++; continue; }

            rows.Add(new Row(dist.MeanPct, dist.StdPct, dist.ProbPositive, dist.CVaR5Pct, ret));
            tagged++;
        }

        Console.WriteLine($"  tagged {tagged} trades, skipped {skipped} ({noCache} with no usable cached history)");
        ReportSeparation(rows, "trades");
        ReportGating(rows, "trades");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────
    // Read the on-disk 15m candle cache without any API call. Format matches CandleFetcher's writer:
    // candles at exactly 15m, ms,open,high,low,close,volume per line. Returns empty on missing/corrupt.
    private static List<Candle> LoadM15Cache(string sym)
    {
        string file = System.IO.Path.Combine("candle_cache", $"{sym}_15m.csv");
        var list = new List<Candle>();
        if (!System.IO.File.Exists(file)) return list;
        foreach (var raw in System.IO.File.ReadLines(file))
        {
            var p = raw.Split(',');
            if (p.Length < 6) continue;
            if (!long.TryParse(p[0], out long ms)) continue;
            list.Add(new Candle(
                DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
                double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(p[5], System.Globalization.CultureInfo.InvariantCulture)));
        }
        return list;
    }

    // Distinct symbol column values from a trades CSV. Header-orientation independent: it locates
    // the column named "symbol" (case-insensitive); falls back to column 1 when no header.
    private static List<string> CsvSymbols(string path)
    {
        var syms = new List<string>();
        string? headerLine = null;
        string[]? lines;
        try { lines = System.IO.File.ReadAllLines(path); }
        catch { return syms; }
        if (lines.Length == 0) return syms;
        headerLine = lines[0];
        int symCol = 1;
        if (headerLine.Contains("symbol", StringComparison.OrdinalIgnoreCase))
        {
            var h = headerLine.Split(',');
            for (int i = 0; i < h.Length; i++)
                if (h[i].Trim().Equals("symbol", StringComparison.OrdinalIgnoreCase)) { symCol = i; break; }
        }
        for (int i = 1; i < lines.Length; i++)
        {
            var p = lines[i].Split(',');
            if (p.Length <= symCol) continue;
            string s = p[symCol].Trim();
            if (s.Length > 0) syms.Add(s);
        }
        return syms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (double, double) NormalBand(double k)
        => k switch { 0.5 => (0.33, 0.67), 1.0 => (0.60, 0.75), 1.5 => (0.82, 0.92), _ => (0.0, 1.0) };

    private static double Std(double[] x)
    {
        if (x.Length < 2) return 0;
        double m = x.Average(), s = 0;
        foreach (var v in x) { double d = v - m; s += d * d; }
        return Math.Sqrt(s / (x.Length - 1));
    }

    private static double Downside(double[] x)
    {
        double s = 0;
        foreach (var v in x) s += Math.Min(0, v) * Math.Min(0, v);
        return Math.Sqrt(s / x.Length);
    }

    // Spearman rank correlation (handles ties via average rank). Returns NaN on degenerate input.
    private static double Spearman(double[] a, double[] b)
    {
        if (a.Length != b.Length || a.Length < 3) return double.NaN;
        var ra = Rank(a); var rb = Rank(b);
        double ma = ra.Average(), mb = rb.Average();
        double num = 0, da = 0, db = 0;
        for (int i = 0; i < ra.Length; i++)
        {
            num += (ra[i] - ma) * (rb[i] - mb);
            da += (ra[i] - ma) * (ra[i] - ma);
            db += (rb[i] - mb) * (rb[i] - mb);
        }
        return da > 1e-12 && db > 1e-12 ? num / Math.Sqrt(da * db) : double.NaN;
    }

    private static double[] Rank(double[] x)
    {
        var idx = x.Select((v, i) => (v, i)).OrderBy(p => p.v).ToArray();
        var ranks = new double[x.Length];
        int i = 0;
        while (i < idx.Length)
        {
            int j = i;
            while (j < idx.Length && Math.Abs(idx[j].v - idx[i].v) < 1e-12) j++;
            double avg = (i + 1 + j) / 2.0;
            for (int k = i; k < j; k++) ranks[idx[k].i] = avg;
            i = j;
        }
        return ranks;
    }
}