using Bybit.Net.Clients;
using Bybit.Net.Enums;

namespace TradingGA;

static class CandleFetcher
{
    // Fetch 4h candles from Bybit. batches=7 → ~3.2yr; batches=1 sufficient for papertrade warmup.
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

    // 15m candles with disk cache. batches=113 ≈ 3.2yr. Cache: candle_cache/{symbol}_15m.csv.
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
                    if (!cached.ContainsKey(dt))
                    {
                        cached[dt] = new Candle(dt, (double)k.OpenPrice, (double)k.HighPrice,
                                                (double)k.LowPrice, (double)k.ClosePrice, (double)k.Volume);
                        dirty = true;
                    }
                }
                await Task.Delay(500);
                return true;
            }
            return false;
        }

        // Forward fill: fetch new candles from "now" back to the last cached entry.
        bool cacheIsFresh = cached.Count > 0 &&
            (DateTime.UtcNow - cached.Keys.Max()).TotalHours < 1.0;
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

        // Backward fill: extend history further back if needed.
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
            using var fs = new FileStream(cacheFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs);
            foreach (var c in cached.Values)
            {
                long ms = new DateTimeOffset(c.Time, TimeSpan.Zero).ToUnixTimeMilliseconds();
                sw.WriteLine(FormattableString.Invariant(
                    $"{ms},{c.Open},{c.High},{c.Low},{c.Close},{c.Volume}"));
            }
        }

        return cached.Values.ToList();
    }

    // Coin filter: median ATR% and USD volume over the candle window.
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

    // Scan h1 backward for the last contiguous block of ≥ minBars where the fixed 200-EMA
    // regime is confirmed. Returns (blockStart, blockEnd) indices, or (-1, -1) if none found.
    public static (int Start, int End) FindLastRegimeBlock(
        Candle[] h1, bool wantBull, int emaPeriod = 200, int slopeLookback = 50, int minBars = 500)
    {
        int warmup = emaPeriod + slopeLookback;
        if (h1.Length < warmup + minBars) return (-1, -1);
        var closes = h1.Select(c => c.Close).ToArray();
        var ema    = Indicators.Ema(closes, emaPeriod);
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

    // ── Funding rate ─────────────────────────────────────────────────────────
    // Bybit perp funding settles every 8h. Cache: candle_cache/{symbol}_funding.csv.
    // 3.2yr ≈ 3504 records; ~18 API calls to build from scratch.

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

        // Forward fill: catch up to "now" from the last cached record
        bool isFresh = cached.Count > 0 && (DateTime.UtcNow - cached.Keys.Max()).TotalHours < 8.0;
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
                endTime = cached.Keys.Min().AddHours(-8);
            }
        }

        // Backward fill: extend to ~3.2yr (3504 records at 8h intervals)
        const int Needed = 3504;
        if (cached.Count < Needed)
        {
            DateTime? endTime = cached.Count > 0 ? cached.Keys.Min().AddHours(-8) : null;
            for (int i = 0; i < 25 && cached.Count < Needed; i++)
            {
                int before = cached.Count;
                if (!await FetchFundingBatch(cached, client, symbol, endTime)) break;
                if (cached.Count == before) break;
                dirty = true;
                endTime = cached.Keys.Min().AddHours(-8);
            }
        }

        if (dirty)
        {
            using var fs2 = new FileStream(cacheFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var sw = new StreamWriter(fs2);
            foreach (var (dt, rate) in cached)
            {
                long ms = new DateTimeOffset(dt, TimeSpan.Zero).ToUnixTimeMilliseconds();
                sw.WriteLine(FormattableString.Invariant($"{ms},{rate:G17}"));
            }
        }

        return [.. cached.Select(kv => new FundingBar(kv.Key, kv.Value))];
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
}
