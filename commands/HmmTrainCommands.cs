using System.Text.Json;
using Bybit.Net.Clients;

namespace TradingGA;

static class HmmTrainCommands
{
    // Per-hour BTC regime series for outside analysis: the legacy classifier's label and the
    // HMM's causal forward-filter probabilities. Bar `time` is the bar's OPEN; both are computed
    // from that bar's close, so a consumer may use them from time + 1h onward.
    public static async Task Dump(BybitRestClient client)
    {
        if (!File.Exists(Config.HmmGenoFile)) { Console.WriteLine($"  {Config.HmmGenoFile} missing."); return; }
        var geno = JsonSerializer.Deserialize<HmmGenotypeDto>(File.ReadAllText(Config.HmmGenoFile))!.ToGenotype();
        var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, "BTCUSDT", batches: 113);
        var h1 = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
        var hmm = HmmAnnotator.Annotate(h1, geno);
        var legacy = RegimeClassifier.ClassifySeriesWithDuration(h1);
        int k = geno.StatesN;
        var lines = new List<string>
        {
            "time,legacy,legacy_conf,hmm_label,hmm_conf," + string.Join(",", Enumerable.Range(0, k).Select(s => $"p{s}"))
        };
        for (int i = 0; i < h1.Length; i++)
        {
            if (hmm[i].HmmProbs is not { } p) continue;
            lines.Add(FormattableString.Invariant(
                $"{h1[i].Time:yyyy-MM-dd HH:mm:ss},{legacy[i].Regime},{legacy[i].Confidence:F4},{hmm[i].Regime},{hmm[i].Confidence:F4},")
                + string.Join(",", p.Select(v => v.ToString("F5", System.Globalization.CultureInfo.InvariantCulture))));
        }
        Directory.CreateDirectory("reports");
        File.WriteAllLines("reports/btc_regime_series.csv", lines);
        Console.WriteLine($"  wrote reports/btc_regime_series.csv: {lines.Count - 1} bars, {k} HMM states " +
                          $"(labels: {string.Join(",", geno.StateLabels)})");
    }

    public static async Task Run(BybitRestClient client, string[]? args = null)
    {
        int? forcedStates = null;
        if (args != null)
        {
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i].StartsWith("--states=") && int.TryParse(args[i].AsSpan("--states=".Length), out var s))
                    forcedStates = Math.Clamp(s, 3, 6);
            }
        }

        Console.WriteLine("=== Gravity-gen2 | HMMTRAIN (Gaussian HMM on BTC regime features) ===");
        Console.WriteLine(forcedStates is int fs ? $"States={fs} (forced)" : "States=BIC-selected over [3,6]");
        Console.WriteLine();

        Console.WriteLine("  Fetching BTCUSDT 15m candles (113 batches, ~3yr)...");
        var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, "BTCUSDT", batches: 113);
        if (m15 is not { Count: > 500 })
        {
            Console.WriteLine("  BTCUSDT data insufficient.");
            return;
        }

        var h1 = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
        Console.WriteLine($"  BTC h1 series: {h1.Length} bars ({h1[0].Time:yyyy-MM-dd} -> {h1[^1].Time:yyyy-MM-dd})");
        Console.WriteLine();

        int warmup = RegimeClassifier.Warmup;
        if (h1.Length <= warmup)
        {
            Console.WriteLine("  Not enough bars after warmup.");
            return;
        }

        var closes = CandleExt.Closes(h1);
        var highs  = CandleExt.Highs(h1);
        var lows   = CandleExt.Lows(h1);

        var ema20  = Trend.Ema(closes, 20);
        var ema50  = Trend.Ema(closes, 50);
        var ema200 = Trend.Ema(closes, 200);
        var atr14  = Volatility.Atr(highs, lows, closes, 14);
        var atr100 = Volatility.Atr(highs, lows, closes, 100);
        var adx    = Trend.Adx(highs, lows, closes, 14);

        int activeN = h1.Length - warmup;
        var features = new double[activeN][];
        for (int i = 0; i < activeN; i++)
        {
            int idx = i + warmup;
            double price = closes[idx];
            double e20 = ema20[idx], e50 = ema50[idx], e200 = ema200[idx];
            double atrRatio = atr100[idx] > 1e-10 ? atr14[idx] / atr100[idx] : 1.0;
            double e50Prev = ema50[Math.Max(0, idx - 20)];
            double slope50 = e50Prev > 1e-10 ? (e50 - e50Prev) / e50Prev : 0;
            double pPrev20 = closes[Math.Max(0, idx - 20)];
            double mom20 = pPrev20 > 1e-10 ? (price - pPrev20) / pPrev20 : 0;

            features[i] =
            [
                Math.Clamp(e20  > 1e-10 ? (price - e20)  / e20  : 0, -0.5, 0.5),
                Math.Clamp(e50  > 1e-10 ? (price - e50)  / e50  : 0, -0.5, 0.5),
                Math.Clamp(e200 > 1e-10 ? (price - e200) / e200 : 0, -0.5, 0.5),
                Math.Clamp(slope50, -0.10, 0.10),
                Math.Clamp(atrRatio, 0.0, 5.0) / 5.0,
                Math.Clamp(adx[idx], 0.0, 60.0) / 60.0,
                Math.Clamp(mom20, -0.5, 0.5),
                Math.Clamp(e50  > 1e-10 ? (e20 - e50)  / e50  : 0, -0.3, 0.3),
                Math.Clamp(e200 > 1e-10 ? (e50 - e200) / e200 : 0, -0.5, 0.5),
            ];
        }

        Console.WriteLine($"  Features: {activeN} bars x {features[0].Length} dims (after warmup={warmup})");

        // Fit on the TRAIN prefix only. The forward filter applied at serve/annotation time is
        // causal, so val/test bars get filtered with train-learned parameters — no leak. Fitting
        // on the full series (as before) let the transition matrix + emission means/vars absorb
        // the eval window, which is exactly the "train on validation" leak.
        int trainEndBar    = DataSplit.Bounds(h1.Length).TrainEnd;
        int trainFeatureEnd = Math.Clamp(trainEndBar - warmup, 0, activeN);
        var trainFeatures  = features[..trainFeatureEnd];
        Console.WriteLine($"  Train-only fit: {trainFeatureEnd}/{activeN} feature bars " +
                          $"(cut at h1 bar {trainEndBar}, aligned with DataSplit train/val boundary)");
        Console.WriteLine();

        const int restarts = 3, maxIter = 200;
        HiddenMarkovModel hmm;

        if (forcedStates is int forcedK)
        {
            Console.WriteLine($"  Training HMM (states={forcedK}, {restarts} restarts, max {maxIter} iter)...");
            hmm = HiddenMarkovModel.Train(trainFeatures, forcedK, new Random(42), restarts, maxIter);
        }
        else
        {
            // BIC model-order selection. Free params for a diagonal-covariance Gaussian HMM:
            //   Pi: N-1   A: N(N-1)   means: N*D   vars: N*D.
            Console.WriteLine("  BIC state-count selection over k=[3,6]:");
            int    bestK   = 3;
            double bestBic = double.PositiveInfinity;
            HiddenMarkovModel? bestModel = null;
            for (int k = 3; k <= 6; k++)
            {
                var cand = HiddenMarkovModel.Train(trainFeatures, k, new Random(42), restarts, maxIter);
                int    p   = (k - 1) + k * (k - 1) + 2 * k * cand.D;
                double bic = -2.0 * cand.LogLikelihood + p * Math.Log(trainFeatureEnd);
                Console.WriteLine($"    k={k}: logL={cand.LogLikelihood:F1}  params={p}  BIC={bic:F1}");
                if (bic < bestBic) { bestBic = bic; bestK = k; bestModel = cand; }
            }
            Console.WriteLine($"  → selected k={bestK} (BIC={bestBic:F1})");
            Console.WriteLine();
            hmm = bestModel!;
        }

        var labels = HmmGenotype.LabelStates(hmm.Means);
        var geno = new HmmGenotype
        {
            StatesN = hmm.N,
            Pi = (double[])hmm.Pi.Clone(),
            A = hmm.A.Select(r => (double[])r.Clone()).ToArray(),
            Means = hmm.Means.Select(r => (double[])r.Clone()).ToArray(),
            Vars = hmm.Vars.Select(r => (double[])r.Clone()).ToArray(),
            StateLabels = labels,
            LogLikelihood = hmm.LogLikelihood,
        };

        Console.WriteLine($"  Train log-likelihood: {hmm.LogLikelihood:F2}");
        Console.WriteLine();

        // State summary. Occupancy runs the causal forward filter over the FULL series with the
        // train-fit parameters — descriptive only, no parameter touches the val/test window.
        Console.WriteLine("  State summary (occupancy over full series, train-fit params):");
        Console.WriteLine($"  {"State",-6} {"Label",-10} {"Occ%",-7} {"Pi",-8}  Mean features (key dims)");
        Console.WriteLine($"  {"-----",-6} {"-----",-10} {"----",-7} {"--",-8}  ------------------------");

        var fwdProbs = hmm.ForwardFilter(features);
        for (int s = 0; s < hmm.N; s++)
        {
            double occ = 0;
            for (int t = 0; t < fwdProbs.Length; t++) occ += fwdProbs[t][s];
            occ /= fwdProbs.Length;

            double atrR = hmm.Means[s][4] * 5.0;
            double slope = hmm.Means[s][3];
            double pve50 = hmm.Means[s][1];
            double mom = hmm.Means[s][6];
            double adxM = hmm.Means[s][5] * 60.0;

            Console.WriteLine($"  {s,-6} {labels[s],-10} {occ * 100,6:F1}% {geno.Pi[s],8:F4}  " +
                $"atrR={atrR:F2} slope={slope:+0.0000;-0.0000} pvsE50={pve50:+0.000;-0.000} " +
                $"mom={mom:+0.000;-0.000} adx={adxM:F1}");
        }
        Console.WriteLine();

        Console.WriteLine("  Transition matrix A[i->j]:");
        Console.Write("         ");
        for (int j = 0; j < hmm.N; j++) Console.Write($"  s{j}     ");
        Console.WriteLine();
        for (int i = 0; i < hmm.N; i++)
        {
            Console.Write($"  s{i}  ");
            for (int j = 0; j < hmm.N; j++)
                Console.Write($"  {geno.A[i][j]:F4}");
            Console.WriteLine();
        }
        Console.WriteLine();

        File.WriteAllText(Config.HmmGenoFile,
            JsonSerializer.Serialize(HmmGenotypeDto.From(geno),
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  Saved -> {Config.HmmGenoFile}");
    }
}
