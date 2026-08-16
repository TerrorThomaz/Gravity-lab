using Bybit.Net.Clients;
using Bybit.Net.Enums;

namespace TradingGA;

static class CandleFetcher
{
    // 4h candles from Bybit. batches=7 ≈ 3.2yr.
    public static async Task<List<Candle>> FetchSwingCandles(BybitRestClient client, string symbol, int batches = 7)
    {
        var all = new List<Candle>();
        DateTime? endTime = null;
        for (int batch = 0; batch < batches; batch++)
        {
            bool success = false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0) await Task.Delay(1500 * attempt);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var result = await client.V5Api.ExchangeData
                    .GetKlinesAsync(Category.Linear, symbol, KlineInterval.FourHours,
                        endTime: endTime, limit: 1000, ct: cts.Token);

                if (!result.Success || result.Data?.List == null)
                {
                    bool rateLimit = result.Error?.ToString().Contains("rate", StringComparison.OrdinalIgnoreCase) == true
                                  || result.Error?.ToString().Contains("429") == true;
                    if (rateLimit && attempt < 3) { Console.Write("↺"); continue; }
                    Console.WriteLine($"\n  [{symbol}] 4h batch {batch + 1} failed: {result.Error}");
                    break;
                }

                var bc = result.Data.List
                    .Select(k => new Candle(k.StartTime, (double)k.OpenPrice, (double)k.HighPrice,
                                            (double)k.LowPrice, (double)k.ClosePrice, (double)k.Volume))
                    .ToList();
                if (!bc.Any()) { success = true; break; }
                all.AddRange(bc);
                endTime = bc.Min(c => c.Time).AddHours(-4);
                success = true;
                break;
            }
            if (!success) break;
            await Task.Delay(300);
        }
        return all.GroupBy(c => c.Time).Select(g => g.First()).OrderBy(c => c.Time).ToList();
    }

    // 15m candles with disk cache (candle_cache/{symbol}_15m.csv). batches=113 ≈ 3.2yr. Incremental.
    public static async Task<List<Candle>> FetchFifteenMinCandlesCached(BybitRestClient client, string symbol, int batches = 113)
    {
        const string CacheDir = "candle_cache";
        Directory.CreateDirectory(CacheDir);
        string cacheFile = Path.Combine(CacheDir, $"{symbol}_15m.csv");

        var cached = new SortedDictionary<DateTime, Candle>();
        if (File.Exists(cacheFile))
        {
            using var fs = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var p = line.Split(',');
                if (p.Length < 6) continue;
                if (!long.TryParse(p[0], out long ms)) continue;
                var dt = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                cached[dt] = new Candle(dt,
                    double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(p[5], System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        bool dirty = false;

        async Task<bool> FetchBatch15m(DateTime? endTime)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0) await Task.Delay(1500 * attempt);
                using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var result = await client.V5Api.ExchangeData
                    .GetKlinesAsync(Category.Linear, symbol, KlineInterval.FifteenMinutes,
                        endTime: endTime, limit: 1000, ct: cts2.Token);
                if (!result.Success || result.Data?.List == null)
                {
                    if ((result.Error?.ToString().Contains("rate", StringComparison.OrdinalIgnoreCase) == true
                         || result.Error?.ToString().Contains("429") == true) && attempt < 3) continue;
                    return false;
                }
                foreach (var k in result.Data.List)
                {
                    var dt = k.StartTime;
                    var newCandle = new Candle(dt, (double)k.OpenPrice, (double)k.HighPrice,
                                               (double)k.LowPrice, (double)k.ClosePrice, (double)k.Volume);

                    if (!cached.TryGetValue(dt, out var existing) || !existing.Equals(newCandle))
                    {
                        cached[dt] = newCandle;
                        dirty = true;
                    }
                }
                await Task.Delay(500);
                return true;
            }
            return false;
        }

        // Forward fill from "now" back to last cached entry.
        DateTime now = DateTime.UtcNow;
        DateTime lastClosedStart = new DateTime(now.Ticks - now.Ticks % TimeSpan.FromMinutes(15).Ticks, DateTimeKind.Utc) - TimeSpan.FromMinutes(15);
        bool cacheIsFresh = cached.Count > 0 && cached.Keys.Max() >= lastClosedStart;
        if (!cacheIsFresh)
        {
            DateTime stopAt = cached.Count > 0 ? cached.Keys.Max() : DateTime.MinValue;
            DateTime? endTime = null;
            for (int i = 0; i < 5; i++)
            {
                int beforeCount = cached.Count;
                await FetchBatch15m(endTime);
                if (cached.Count == beforeCount) break;
                var fetched = cached.Keys.Min();
                if (fetched >= stopAt) break;
                endTime = fetched.AddMinutes(-15);
            }
        }

        // Backward fill to reach target count.
        int needed = batches * 1000;
        if (cached.Count < needed)
        {
            DateTime? endTime = cached.Count > 0 ? cached.Keys.Min().AddMinutes(-15) : null;
            int maxOldBatches = (needed - cached.Count) / 800 + 10;
            for (int i = 0; i < maxOldBatches && cached.Count < needed; i++)
            {
                int beforeCount = cached.Count;
                bool ok = await FetchBatch15m(endTime);
                if (!ok || cached.Count == beforeCount) break;
                endTime = cached.Keys.Min().AddMinutes(-15);
            }
        }

        if (dirty)
        {
            // Unique-per-process tmp name: combinedbacktest runs the same backtest under different
            // GRAVITY_SIZING envs in parallel, and a shared `{symbol}.tmp` path makes those processes
            // clobber each other's in-flight File.Move (FileNotFound / in-use). A per-process suffix
            // keeps writes atomic last-writer-wins — both writers persist complete, valid data.
            var tmpFile = cacheFile + ".tmp." + System.Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs))
                {
                    foreach (var c in cached.Values)
                    {
                        long ms = new DateTimeOffset(c.Time, TimeSpan.Zero).ToUnixTimeMilliseconds();
                        sw.WriteLine(FormattableString.Invariant(
                            $"{ms},{c.Open},{c.High},{c.Low},{c.Close},{c.Volume}"));
                    }
                }
                File.Move(tmpFile, cacheFile, overwrite: true);
            }
            catch
            {
                if (File.Exists(tmpFile)) File.Delete(tmpFile);
                throw;
            }
        }

        // Exclude in-progress bar.
        DateTime nowFinal = DateTime.UtcNow;
        DateTime currentBarStart = new DateTime(nowFinal.Ticks - nowFinal.Ticks % TimeSpan.FromMinutes(15).Ticks, DateTimeKind.Utc);
        var series = cached.Where(kv => kv.Key < currentBarStart).Select(kv => kv.Value).ToList();
        VerifyCacheIntegrity(symbol, series);
        return series;
    }

    // Coin filter: median ATR% and USD volume.
    public static (bool Passes, double AtrPct, double VolUsdM) CheckSwingCriteria(
        IReadOnlyList<Candle> candles, double minAtrPct = 1.5, double minVolUsdM = 1.0)
    {
        if (candles.Count < 50) return (false, 0, 0);

        var recent = candles.ToArray();

        var trPcts = new List<double>(recent.Length);
        for (int i = 1; i < recent.Length; i++)
        {
            double tr = Math.Max(recent[i].High - recent[i].Low,
                        Math.Max(Math.Abs(recent[i].High - recent[i - 1].Close),
                                 Math.Abs(recent[i].Low  - recent[i - 1].Close)));
            if (recent[i].Close > 0) trPcts.Add(tr / recent[i].Close * 100.0);
        }
        trPcts.Sort();
        double medAtrPct = trPcts.Count > 0 ? trPcts[trPcts.Count / 2] : 0;

        var volUsd = recent.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(x => x).ToList();
        double medVolM = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;

        return (medAtrPct >= minAtrPct && medVolM >= minVolUsdM, medAtrPct, medVolM);
    }

    // Scan h1 backward for last contiguous confirmed-regime block of ≥ minBars.
    public static (int Start, int End) FindLastRegimeBlock(
        Candle[] h1, bool wantBull, int emaPeriod = 200, int slopeLookback = 50, int minBars = 500)
    {
        int warmup = emaPeriod + slopeLookback;
        if (h1.Length < warmup + minBars) return (-1, -1);
        var closes = h1.Select(c => c.Close).ToArray();
        var ema    = Trend.Ema(closes, emaPeriod);
        bool IsReg(int j) => wantBull
            ? closes[j] > ema[j] && ema[j] > ema[j - slopeLookback]
            : closes[j] < ema[j] && ema[j] < ema[j - slopeLookback];
        int i = h1.Length - 1;
        while (i >= warmup)
        {
            if (!IsReg(i)) { i--; continue; }
            int blockEnd = i;
            while (i >= warmup && IsReg(i)) i--;
            int blockStart = i + 1;
            if (blockEnd - blockStart + 1 >= minBars) return (blockStart, blockEnd);
        }
        return (-1, -1);
    }

    // Funding rate cache: candle_cache/{symbol}_funding.csv.
    // WARNING: not all symbols settle at 8h — FundingSeriesInfo.MatchesModelGrid detects the mismatch.
    // Target expressed in hours (not records) because records-per-hour is per-symbol.
    public const double TargetFundingHistoryHours = 28032.0;

    // Hard ceiling so a 1h-settling symbol cannot spin 28k records. Sub-8h symbols hit this
    // before TargetFundingHistoryHours — shortfall reported via FundingSeriesInfo.
    public const int MaxFundingRecords        = 5000;
    public const int MaxFundingBackfillCalls  = 25;

    /// Observed funding series properties. MatchesModelGrid=false means the 8h model misprices it.
    public readonly record struct FundingSeriesInfo(
        string Symbol,
        int Records,
        double MedianIntervalHours,
        DateTime First,
        DateTime Last)
    {
        public bool MatchesModelGrid =>
            Records < 2 || Math.Abs(MedianIntervalHours - FundingRateSession.FundingIntervalHours) < 1e-6;

        // How many real settlements per 8h model tick. 2.0 on a 4h symbol = half the cost booked.
        public double CostUndercountFactor =>
            MedianIntervalHours > 1e-9 ? FundingRateSession.FundingIntervalHours / MedianIntervalHours : 1.0;

        public double SpanDays => Records < 2 ? 0.0 : (Last - First).TotalDays;

        public override string ToString() =>
            Records == 0
                ? $"{Symbol}: no funding data"
                : $"{Symbol}: {Records} records · {MedianIntervalHours:0.##}h spacing · " +
                  $"{First:yyyy-MM-dd}→{Last:yyyy-MM-dd} ({SpanDays / 365.25:0.0}yr)" +
                  (MatchesModelGrid ? "" : $"  ⚠ model assumes {FundingRateSession.FundingIntervalHours:0.#}h → " +
                                           $"funding cost undercounted ×{CostUndercountFactor:0.##}");
    }

    /// Median spacing in hours between funding prints. Median so gaps don't drag the estimate.
    public static double MedianFundingIntervalHours(IReadOnlyList<DateTime> times)
    {
        if (times.Count < 2) return 0.0;
        var gaps = new List<double>(times.Count - 1);
        for (int i = 1; i < times.Count; i++)
        {
            double h = (times[i] - times[i - 1]).TotalHours;
            if (h > 0) gaps.Add(h);
        }
        if (gaps.Count == 0) return 0.0;
        gaps.Sort();
        int mid = gaps.Count / 2;
        return gaps.Count % 2 == 1 ? gaps[mid] : (gaps[mid - 1] + gaps[mid]) / 2.0;
    }

    /// Records needed for targetHours at observed spacing. Clamped to MaxFundingRecords.
    public static int RecordsNeededFor(double medianIntervalHours, double targetHours)
    {
        double interval = medianIntervalHours > 1e-9
            ? medianIntervalHours
            : FundingRateSession.FundingIntervalHours;
        int needed = (int)Math.Ceiling(targetHours / interval);
        return Math.Clamp(needed, 1, MaxFundingRecords);
    }

    public static FundingSeriesInfo DescribeFundingSeries(string symbol, IReadOnlyList<FundingBar> bars)
    {
        if (bars.Count == 0) return new FundingSeriesInfo(symbol, 0, 0.0, default, default);
        var times = bars.Select(b => b.Time).OrderBy(t => t).ToList();
        return new FundingSeriesInfo(symbol, bars.Count, MedianFundingIntervalHours(times), times[0], times[^1]);
    }

    public static async Task<FundingBar[]> FetchFundingRateCachedAsync(BybitRestClient client, string symbol)
    {
        const string CacheDir = "candle_cache";
        Directory.CreateDirectory(CacheDir);
        string cacheFile = Path.Combine(CacheDir, $"{symbol}_funding.csv");

        var cached = new SortedDictionary<DateTime, double>();
        if (File.Exists(cacheFile))
        {
            using var fs = new FileStream(cacheFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                var p = line.Split(',');
                if (p.Length < 2) continue;
                if (!long.TryParse(p[0], out long ms)) continue;
                cached[DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime] =
                    double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        bool dirty = false;

        // Observed spacing, refreshed as cache grows. Used instead of hardcoded 8h.
        double Spacing() => cached.Count >= 2
            ? MedianFundingIntervalHours(cached.Keys.ToList())
            : FundingRateSession.FundingIntervalHours;

        // Forward fill. Staleness judged against symbol's own spacing.
        bool isFresh = cached.Count > 0 && (DateTime.UtcNow - cached.Keys.Max()).TotalHours < Spacing();
        if (!isFresh)
        {
            DateTime stopAt = cached.Count > 0 ? cached.Keys.Max() : DateTime.MinValue;
            DateTime? endTime = null;
            for (int i = 0; i < 5; i++)
            {
                int before = cached.Count;
                if (!await FetchFundingBatch(cached, client, symbol, endTime)) break;
                if (cached.Count > before) dirty = true;
                if (cached.Keys.Min() >= stopAt) break;
                endTime = cached.Keys.Min().AddHours(-Spacing());
            }
        }

        // Backward fill to TargetFundingHistoryHours of wall-clock history.
        {
            DateTime? endTime = cached.Count > 0 ? cached.Keys.Min().AddHours(-Spacing()) : null;
            for (int i = 0; i < MaxFundingBackfillCalls; i++)
            {
                if (cached.Count >= RecordsNeededFor(Spacing(), TargetFundingHistoryHours)) break;
                int before = cached.Count;
                if (!await FetchFundingBatch(cached, client, symbol, endTime)) break;
                if (cached.Count == before) break;
                dirty = true;
                endTime = cached.Keys.Min().AddHours(-Spacing());
            }
        }

        if (dirty)
        {
            var tmpFile = cacheFile + ".tmp." + System.Guid.NewGuid().ToString("N");
            try
            {
                using (var fs2 = new FileStream(tmpFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                using (var sw = new StreamWriter(fs2))
                {
                    foreach (var (dt, rate) in cached)
                    {
                        long ms = new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeMilliseconds();
                        sw.WriteLine(FormattableString.Invariant($"{ms},{rate:G17}"));
                    }
                }
                File.Move(tmpFile, cacheFile, overwrite: true);
            }
            catch
            {
                if (File.Exists(tmpFile)) File.Delete(tmpFile);
                throw;
            }
        }

        return [.. cached.Select(kv => new FundingBar(kv.Key, kv.Value))];
    }

    // Per-symbol funding sessions. Unknown symbols → null (floor fallback), not another symbol's rates.
    public sealed record FundingSessions(
        IReadOnlyDictionary<string, FundingRateSession> Sessions,
        IReadOnlyList<FundingSeriesInfo> Info)
    {

        public FundingRateSession? For(string symbol) =>
            Sessions.TryGetValue(symbol, out var s) ? s : null;

        public IEnumerable<FundingSeriesInfo> OffGridSymbols => Info.Where(i => i.Records >= 2 && !i.MatchesModelGrid);

        public void PrintSummary()
        {
            var withData = Info.Where(i => i.Records > 0).ToList();
            Console.WriteLine($"  Funding: {withData.Count}/{Info.Count} symbols have rate history " +
                              $"(per-symbol sessions; symbols without history fall back to the " +
                              $"{FundingRateSession.FallbackIntervalPct:0.##}%/interval floor).");
            if (withData.Count > 0)
            {
                double medSpan = withData.Select(i => i.SpanDays / 365.25).OrderBy(x => x).ElementAt(withData.Count / 2);
                Console.WriteLine($"    Median history depth: {medSpan:0.0}yr");
            }

            var off = OffGridSymbols.ToList();
            if (off.Count == 0)
            {
                Console.WriteLine($"    All series settle on the {FundingRateSession.FundingIntervalHours:0.#}h grid the cost model assumes.");
                return;
            }
            Console.WriteLine($"    ⚠ {off.Count} symbol(s) do NOT settle on the {FundingRateSession.FundingIntervalHours:0.#}h grid " +
                              "FundingRateSession hardcodes — their funding cost is UNDERCOUNTED:");
            foreach (var i in off.OrderByDescending(x => x.CostUndercountFactor).Take(15))
                Console.WriteLine($"      {i}");
            if (off.Count > 15) Console.WriteLine($"      … and {off.Count - 15} more");
            Console.WriteLine("      Fixing this needs a per-symbol settlement grid inside FundingRateSession, " +
                              "which hardcodes FundingIntervalHours = 8 for every symbol.");
        }
    }

    public static async Task<FundingSessions> FetchFundingSessionsAsync(
        BybitRestClient client, IEnumerable<string> symbols, int maxConcurrency = 4)
    {
        var unique = symbols.Distinct().ToArray();
        var sem    = new SemaphoreSlim(Math.Max(1, maxConcurrency));

        var results = await Task.WhenAll(unique.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var bars = await FetchFundingRateCachedAsync(client, sym);
                return (sym, bars);
            }
            // Single-symbol failure → floor fallback, not a backtest abort.
            catch (Exception ex)
            {
                Console.WriteLine($"    Funding fetch failed for {sym}: {ex.GetType().Name} — falling back to the interest-rate floor.");
                return (sym, bars: Array.Empty<FundingBar>());
            }
            finally { sem.Release(); }
        }));

        var sessions = new Dictionary<string, FundingRateSession>();
        var info     = new List<FundingSeriesInfo>();
        foreach (var (sym, bars) in results)
        {
            info.Add(DescribeFundingSeries(sym, bars));
            if (bars.Length > 0) sessions[sym] = new FundingRateSession(bars);
        }
        return new FundingSessions(sessions, info);
    }

    private static async Task<bool> FetchFundingBatch(
        SortedDictionary<DateTime, double> cached, BybitRestClient client, string symbol, DateTime? endTime)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0) await Task.Delay(1500 * attempt);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await client.V5Api.ExchangeData
                .GetFundingRateHistoryAsync(Category.Linear, symbol,
                    endTime: endTime, limit: 200, ct: cts.Token);
            if (!result.Success || result.Data?.List == null)
            {
                bool rateLimit = result.Error?.ToString().Contains("rate", StringComparison.OrdinalIgnoreCase) == true
                              || result.Error?.ToString().Contains("429") == true;
                if (rateLimit && attempt < 3) continue;
                return false;
            }
            foreach (var f in result.Data.List)
                cached.TryAdd(f.Timestamp, (double)f.FundingRate);
            await Task.Delay(300);
            return true;
        }
        return false;
    }

    public record ValidationResult(int BadOhlc, int Gaps, int Spikes, int ZeroPrices, bool IsValid);

    public static ValidationResult ValidateCandles(IReadOnlyList<Candle> candles, TimeSpan expectedInterval)
    {
        int badOhlc = 0, gaps = 0, spikes = 0, zeroPrices = 0;
        double intervalMs = expectedInterval.TotalMilliseconds;

        for (int i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            if (c.High < Math.Max(c.Open, c.Close) - 1e-10 || c.Low > Math.Min(c.Open, c.Close) + 1e-10)
                badOhlc++;
            if (c.Open <= 0 || c.High <= 0 || c.Low <= 0 || c.Close <= 0)
                zeroPrices++;
            if (i > 0)
            {
                double gap = (c.Time - candles[i - 1].Time).TotalMilliseconds;
                if (gap > intervalMs * 2.5)
                    gaps++;
                double pctChange = Math.Abs(c.Close - candles[i - 1].Close) / candles[i - 1].Close;
                if (pctChange > 0.50)
                    spikes++;
            }
        }

        if (badOhlc > 0 || zeroPrices > 0)
            Console.WriteLine($"  [ValidateCandles] badOHLC={badOhlc} zeroPrices={zeroPrices} gaps={gaps} spikes={spikes}");

        return new ValidationResult(badOhlc, gaps, spikes, zeroPrices, badOhlc == 0 && zeroPrices == 0);
    }

    public static void PrintSplitStats(string label, List<double> r, int candleCount)
    {
        if (r.Count == 0) { Console.WriteLine($"  {label,-12} (no trades)"); return; }
        double sh   = Simulator.SharpeRatio(r, candleCount);
        double sort = Simulator.SortinoRatio(r, candleCount);
        double pf   = Simulator.ProfitFactor(r);
        double wr   = (double)r.Count(x => x > 0) / r.Count;
        double avg  = r.Average();
        Console.WriteLine($"  {label,-12} Sh={sh:F2}  Sort={sort:F2}  PF={pf:F2}  WR={wr:P0}  Tr={r.Count}  Avg={avg:+0.00;-0.00}%");
    }

    // Cache integrity check. Diagnostic only — never throws. Detects stale restated bars.
    public static void VerifyCacheIntegrity(string symbol, IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 3) return;

        var gaps = new List<long>(candles.Count - 1);
        int nonMonotonic = 0, duplicates = 0;
        DateTime firstBadTime = default, firstDupTime = default;

        for (int i = 1; i < candles.Count; i++)
        {
            long d = candles[i].Time.Ticks - candles[i - 1].Time.Ticks;
            if (d == 0)     { if (duplicates++   == 0) firstDupTime = candles[i].Time; }
            else if (d < 0) { if (nonMonotonic++ == 0) firstBadTime = candles[i].Time; }
            else gaps.Add(d);
        }

        if (gaps.Count == 0) return;
        var sorted = gaps.ToArray();
        Array.Sort(sorted);
        long median = sorted[sorted.Length / 2];

        int bigGaps = 0; DateTime firstGapTime = default;
        if (median > 0)
            for (int i = 1; i < candles.Count; i++)
            {
                long d = candles[i].Time.Ticks - candles[i - 1].Time.Ticks;
                if (d > 2 * median) { if (bigGaps++ == 0) firstGapTime = candles[i].Time; }
            }

        if (nonMonotonic == 0 && duplicates == 0 && bigGaps == 0) return;

        if (nonMonotonic > 0)
            Console.WriteLine($"  [cache] {symbol}: {nonMonotonic} NON-MONOTONIC timestamp(s), first at {firstBadTime:yyyy-MM-dd HH:mm}");
        if (duplicates > 0)
            Console.WriteLine($"  [cache] {symbol}: {duplicates} DUPLICATE timestamp(s), first at {firstDupTime:yyyy-MM-dd HH:mm}");
        if (bigGaps > 0)
            Console.WriteLine($"  [cache] {symbol}: {bigGaps} gap(s) > 2x median spacing " +
                              $"({TimeSpan.FromTicks(median).TotalMinutes:F0}m), first at {firstGapTime:yyyy-MM-dd HH:mm}");
    }

}
