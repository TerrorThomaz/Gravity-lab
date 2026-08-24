namespace TradingGA;

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Globalization;

/// <summary>
/// Async HTTP client for Gravity-gen2 paper trading via Hyperliquid's Python bridge.
/// 
/// Architecture:
///   C# strategy engine => HyperliquidClient (HTTP/JSON) => bot/hyperliquid_server.py
///   => hyperliquid-python-sdk => paper.hyperliquid.xyz
///
/// The Python bridge handles all cryptographic signing (EIP-712), order construction,
/// and WebSocket subscriptions - avoiding fragile reimplementation in C#.
/// All operations are async-first using HttpClient with Polly-style retries.
/// </summary>
public sealed class HyperliquidClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private bool _disposed;

    // Default: local FastAPI bridge running on port 8765. Override with HYPERLIQUID_API_URL env var.
    public static string DefaultBaseUrl =>
        Environment.GetEnvironmentVariable("HYPERLIQUID_API_URL") ?? "http://localhost:8765";

    public HyperliquidClient(HttpClient? http = null)
    {
        _baseUrl = DefaultBaseUrl;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ── Health ───────────────────────────────────────────────────────────────
    public async Task<bool> IsReadyAsync()
    {
        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/health");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Market Data ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch OHLCV candles from Hyperliquid. Returns raw candle dicts matching the SDK output.
    /// interval in {1m, 5m, 15m, 1h, 4h, 1d}. limit in [1, 500].
    /// </summary>
    public async Task<List<Dictionary<string, object>>> FetchOhlcvAsync(
        string symbol, string interval = "1h", int limit = 100)
    {
        ValidateSymbol(symbol);
        if (!IntervalMap.ContainsKey(interval))
            throw new ArgumentException($"Invalid interval: {interval}");
        if (limit < 1 || limit > 500)
            throw new ArgumentException("limit must be 1-500");

        var url = $"{_baseUrl}/api/ohlcv?symbol={Uri.EscapeDataString(NormalizeToHyperliquidCoin(symbol))}" +
                  $"&interval={Uri.EscapeDataString(interval)}&limit={limit}";
        return await GetJsonListAsync(url);
    }

    /// <summary>
    /// Fetch OHLCV candles with pagination. Handles large requests by chunking into
    /// paginated API calls (max 5000 candles per request). Uses startTime/endTime
    /// to retrieve historical data beyond the default recent window.
    /// </summary>
    public async Task<List<Dictionary<string, object>>> FetchOhlcvPaginatedAsync(
        string symbol, string interval = "1h", int totalCandles = 10000)
    {
        ValidateSymbol(symbol);
        if (!IntervalMap.ContainsKey(interval))
            throw new ArgumentException($"Invalid interval: {interval}");
        if (totalCandles < 1)
            throw new ArgumentException("totalCandles must be >= 1");

        // HL candleSnapshot endpoint supports pagination via startTime/endTime.
        // Each request returns up to 5000 candles. We chunk backwards from now.
        var allCandles = new List<Dictionary<string, object>>();
        long endTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int batchSize = 5000;
        // Retry budget for transient bridge/exchange failures. 4 retries at 400ms base doubles to
        // ~6s worst case per page — acceptable against a 900s refresh, and far cheaper than the
        // silent data loss it replaces.
        const int CandleFetchRetries = 4;
        const int CandleRetryBaseMs  = 400;

        while (allCandles.Count < totalCandles)
        {
            try
            {
                // IntervalMap values are minutes-per-candle (e.g. "15" for "15m"); the window must
                // scan back batchSize candles' worth of *this* interval, not a fixed 1m assumption —
                // using 1m unconditionally made the 15m page span ~3.5 days instead of ~52, so pages
                // came back under batchSize and pagination stopped after one page (~330 candles).
                int intervalMinutes = int.Parse(IntervalMap[interval]);
                long startTime = endTime - (batchSize * intervalMinutes * 60_000L);
                
                // POST body matches HL's candleSnapshot API format
                var payload = new Dictionary<string, object>
                {
                    ["symbol"] = NormalizeToHyperliquidCoin(symbol),
                    ["interval"] = interval,
                    ["startTime"] = startTime,
                    ["endTime"] = endTime,
                };
                
                // Hyperliquid rate-limits hard and the bridge surfaces 429 as a 500. Before this
                // retry existed a SINGLE transient failure broke pagination and returned a short
                // list, which the papertrade loop then dropped without a word (`< 200 candles` →
                // coin skipped, filtered out by `Where(h1 is not null)`). Measured 2026-08-24:
                // 708 of 1274 candleSnapshot calls failing (56%), 32,415 429s in the bridge log,
                // and the visible symptom was only "no signals" — live_journal.json went 7 days
                // without a single event while live_state.json kept reporting a healthy 0 open.
                List<Dictionary<string, object>>? batch = null;
                for (int attempt = 0; ; attempt++)
                {
                    try { batch = await PostJsonListAsync("/api/candleSnapshot", payload); break; }
                    catch when (attempt < CandleFetchRetries)
                    {
                        // Exponential backoff with jitter — a fixed delay would re-synchronise the
                        // parallel fetchers into the same burst that caused the 429.
                        int delayMs = (int)(CandleRetryBaseMs * Math.Pow(2, attempt)
                                            * (0.75 + Random.Shared.NextDouble() * 0.5));
                        await Task.Delay(delayMs);
                    }
                }
                if (batch == null || batch.Count == 0) break;
                
                allCandles.AddRange(batch);
                if (batch.Count < batchSize) break; // reached end of history
                
                // Advance end time past last candle's close
                endTime = AsLong(batch[^1]["T"]) - 1;
                
                // Safety: prevent infinite loop
                if (allCandles.Count > totalCandles * 2) break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Warning: Candle fetch failed for {symbol}/{interval}: {ex.Message}");
                break;
            }
        }

        // Return most recent candles (sorted ascending by timestamp). Pagination accumulates in
        // whole batchSize=5000 chunks that rarely divide evenly into totalCandles, so the pool
        // almost always overshoots — Take() from the ascending-sorted front kept the OLDEST
        // candles and silently discarded the most recent ones. Every live signal, regime call,
        // and order price was therefore computed against stale history, not current market state.
        allCandles.Sort((a, b) => AsLong(a["T"]).CompareTo(AsLong(b["T"])));
        return allCandles.TakeLast(totalCandles).ToList();
    }
    
    /// <summary>
    /// POST JSON payload and return list response. Used for HL endpoints that require POST bodies.
    /// </summary>
    private async Task<List<Dictionary<string, object>>> PostJsonListAsync(string path, Dictionary<string, object> payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, 
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" });
        
        var url = $"{_baseUrl}{path}";
        var resp = await _http.PostAsync(url, content);
        
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"API error ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
            
        return await resp.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>()
            ?? new List<Dictionary<string, object>>();
    }

    /// <summary>
    /// Convenience: fetch h1 candles aggregated from 15m (matches CandleFetcher behavior).
    /// Supports arbitrary depth via pagination — no artificial cap.
    /// </summary>
    // ── Incremental 15m disk cache ────────────────────────────────────────────────────────────────
    //
    // This path had NO caching of any kind, unlike CandleFetcher.FetchFifteenMinCandlesCached which
    // has done incremental fetching for the Bybit/backtest path all along. The live loop therefore
    // re-downloaded ~125 days of history for every coin every 15 minutes to obtain ONE new candle:
    // 64 coins x 3 pages = 192 requests and 768,000 candles per cycle, ~18,400 requests/day, a
    // 12,000x waste factor. That is what drove the rate limiting that starved the loop of data
    // (45-56% of fetches failing) and left live_journal.json unwritten for a week.
    //
    // Cache is keyed by the normalised Hyperliquid coin name so kBONK/kPEPE/kSHIB do not collide
    // with their 1000-prefixed exchange-agnostic aliases.
    // Hyperliquid retains ~5000 15m candles per symbol. Requesting more than it has means the
    // cache can never reach its target, so the backward-extension re-fires every cycle forever and
    // each coin costs 2 requests instead of 1. 5000 x 15m = 1250 h1 bars — 4.9x the
    // warmup(200) + BullMinBars(53) the regime duration gate actually needs.
    public const int LiveBatches = 5;

    private const string HlCacheDir = "candle_cache";

    private static string HlCachePath(string symbol) =>
        Path.Combine(HlCacheDir, $"hl_{NormalizeToHyperliquidCoin(symbol)}_15m.csv");

    private static SortedDictionary<long, Candle> ReadHlCache(string symbol)
    {
        var res = new SortedDictionary<long, Candle>();
        string path = HlCachePath(symbol);
        if (!File.Exists(path)) return res;
        try
        {
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var f = line.Split(',');
                if (f.Length < 6) continue;
                if (!long.TryParse(f[0], out long ms)) continue;
                res[ms] = new Candle(
                    DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime,
                    double.Parse(f[1], CultureInfo.InvariantCulture),
                    double.Parse(f[2], CultureInfo.InvariantCulture),
                    double.Parse(f[3], CultureInfo.InvariantCulture),
                    double.Parse(f[4], CultureInfo.InvariantCulture),
                    double.Parse(f[5], CultureInfo.InvariantCulture));
            }
        }
        catch { return new SortedDictionary<long, Candle>(); }   // corrupt cache → refetch, never throw
        return res;
    }

    private static void WriteHlCache(string symbol, SortedDictionary<long, Candle> candles)
    {
        try
        {
            Directory.CreateDirectory(HlCacheDir);
            string tmp = HlCachePath(symbol) + ".tmp";
            using (var w = new StreamWriter(tmp, false))
            {
                w.WriteLine("T,o,h,l,c,v");
                foreach (var (ms, c) in candles)
                    w.WriteLine($"{ms},{c.Open.ToString(CultureInfo.InvariantCulture)}," +
                                $"{c.High.ToString(CultureInfo.InvariantCulture)},{c.Low.ToString(CultureInfo.InvariantCulture)}," +
                                $"{c.Close.ToString(CultureInfo.InvariantCulture)},{c.Volume.ToString(CultureInfo.InvariantCulture)}");
            }
            // Atomic replace so a crash mid-write cannot leave a truncated cache behind.
            File.Move(tmp, HlCachePath(symbol), overwrite: true);
        }
        catch { /* cache is an optimisation — never fail the fetch over it */ }
    }

    public async Task<List<Candle>> FetchH1CandlesAsync(string symbol, int batches = 113)
    {
        int wantCandles = batches * 1000;
        var cache = ReadHlCache(symbol);

        if (cache.Count > 0)
        {
            // Refetch from one candle BEFORE the last cached bar: the most recent cached candle may
            // have been captured mid-formation, so its OHLC is provisional and must be overwritten
            // rather than trusted. Keyed by timestamp, so the re-fetch simply replaces it.
            long lastMs = cache.Keys.Last();
            long fromMs = lastMs - 15 * 60_000L;
            var fresh = await FetchOhlcvRangeAsync(symbol, "15m", fromMs);
            foreach (var r in fresh)
                if (TryParseCandle(r, out long ms, out var c)) cache[ms] = c;

            // Trim to the requested depth so the cache cannot grow without bound.
            while (cache.Count > wantCandles) cache.Remove(cache.Keys.First());

            // A short cache must GROW, not trigger a full re-fetch. The cold path costs 3 pages and
            // is exactly what the rate limiter rejects, so a coin that fails its first fetch would
            // otherwise retry the expensive path every cycle forever and never bootstrap — measured
            // as only 37 of 64 coins ever building a cache. Extending backwards one page per cycle
            // fills the history in a few cycles at 1/3 the per-cycle cost, and cheaply enough that
            // it succeeds while the burst does not.
            if (cache.Count < wantCandles)
            {
                long oldestMs = cache.Keys.First();
                long backFrom = oldestMs - 5000L * 15 * 60_000L;
                var older = await FetchOhlcvRangeAsync(symbol, "15m", backFrom, oldestMs - 1);
                foreach (var r in older)
                    if (TryParseCandle(r, out long ms, out var c) && ms < oldestMs) cache[ms] = c;
            }

            while (cache.Count > wantCandles) cache.Remove(cache.Keys.First());
            WriteHlCache(symbol, cache);

            // Serve whatever we have. The caller already screens on length (`< 200 candles` → skip),
            // so a still-growing cache degrades to "this coin sits out a cycle" rather than to a
            // full re-fetch storm.
            return cache.Values.ToList();
        }

        // Cold path: no usable cache, fetch the full window.
        int candlesNeeded = wantCandles;
        var raw = await FetchOhlcvPaginatedAsync(symbol, "15m", candlesNeeded);
        foreach (var r in raw)
            if (TryParseCandle(r, out long ms, out var c)) cache[ms] = c;
        if (cache.Count > 0) WriteHlCache(symbol, cache);

        return cache.Values.ToList();
    }

    // /api/candleSnapshot returns raw HL keys (T,o,h,l,c,v,n), not a normalised shape.
    private static bool TryParseCandle(Dictionary<string, object> r, out long ms, out Candle candle)
    {
        ms = 0; candle = default;
        if (!r.TryGetValue("T", out var tObj) || !r.TryGetValue("c", out var cObj)) return false;
        ms = AsLong(tObj);
        double close = AsDouble(cObj);
        double open  = r.TryGetValue("o", out var oObj) ? AsDouble(oObj) : close;
        double high  = r.TryGetValue("h", out var hObj) ? AsDouble(hObj) : close;
        double low   = r.TryGetValue("l", out var lObj) ? AsDouble(lObj) : close;
        double vol   = r.TryGetValue("v", out var vObj) ? AsDouble(vObj) : 0;
        candle = new Candle(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, open, high, low, close, vol);
        return true;
    }

    // Single-page fetch of everything from `fromMs` to now. The incremental path needs at most a
    // handful of candles, so this deliberately does NOT paginate backwards like the cold path.
    // toMs defaults to "now". It MUST be passed explicitly when extending backwards: HL caps the
    // response and returns the most RECENT candles in the requested window, so a backward fetch left
    // open-ended returns data already held, every one of which the `ms < oldest` filter then drops —
    // a wasted request per coin per cycle and a cache that never grows (measured: 0 of 53 coins
    // reached full depth over 5 runs).
    private async Task<List<Dictionary<string, object>>> FetchOhlcvRangeAsync(
        string symbol, string interval, long fromMs, long? toMs = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["symbol"]    = NormalizeToHyperliquidCoin(symbol),
            ["interval"]  = interval,
            ["startTime"] = fromMs,
            ["endTime"]   = toMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        for (int attempt = 0; ; attempt++)
        {
            try { return await PostJsonListAsync("/api/candleSnapshot", payload) ?? []; }
            catch when (attempt < 4)
            {
                await Task.Delay((int)(400 * Math.Pow(2, attempt) * (0.75 + Random.Shared.NextDouble() * 0.5)));
            }
            catch { return []; }   // exhausted: serve stale cache rather than drop the coin entirely
        }
    }

    /// <summary>
    /// Fetch orderbook snapshot for a symbol. Returns bids/asks arrays with price & size.
    /// </summary>
    public async Task<Dictionary<string, object>> FetchOrderbookAsync(string symbol, int depth = 20)
    {
        ValidateSymbol(symbol);
        var url = $"{_baseUrl}/api/orderbook?symbol={Uri.EscapeDataString(NormalizeToHyperliquidCoin(symbol))}&depth={depth}";
        return await GetJsonObjectAsync(url);
    }

    /// <summary>
    /// Fetch universe metadata (all tradable markets).
    /// </summary>
    public async Task<Dictionary<string, object>> FetchUniverseAsync()
    {
        // Same rate limiter as candleSnapshot, and this is the FIRST call every command makes —
        // so a 429 here aborted `hyperliquid-lookback` outright before it touched a single candle
        // (observed twice while validating the cache, and it masqueraded as "the cache made it fast"
        // because a crashed run issues almost no requests). Retry it for the same reason.
        for (int attempt = 0; ; attempt++)
        {
            try { return await GetJsonObjectAsync($"{_baseUrl}/api/universe"); }
            catch when (attempt < 4)
            {
                await Task.Delay((int)(400 * Math.Pow(2, attempt) * (0.75 + Random.Shared.NextDouble() * 0.5)));
            }
        }
    }

    /// <summary>
    /// Fetch current funding rate for a symbol.
    /// </summary>
    public async Task<double?> FetchFundingRateAsync(string symbol)
    {
        try
        {
            var data = await GetJsonObjectAsync($"{_baseUrl}/api/funding?symbol={Uri.EscapeDataString(NormalizeToHyperliquidCoin(symbol))}");
            if (data.TryGetValue("current_rate", out var frObj))
                return AsDouble(frObj);
        }
        catch { /* silent fail - funding is optional */ }
        return null;
    }

    // ── User Account ─────────────────────────────────────────────────────────

    /// <summary>
    /// Fetch account state: wallet value, open positions, open orders.
    /// Requires HYPERLIQUID_PRIVATE_KEY set on the bridge server.
    /// </summary>
    public async Task<Dictionary<string, object>> FetchUserStateAsync()
    {
        return await GetJsonObjectAsync($"{_baseUrl}/api/user/info");
    }

    /// <summary>
    /// Place a limit or market order. Side: "B" (buy) or "A" (ask).
    /// isLimit=true -> limit order at `price`; isLimit=false -> market order (price ignored).
    /// reduceOnly=true -> closes position instead of opening.
    /// tif: Gtc (good-till-cancelled), Alo (good-until-open), IoC (immediate-or-cancel).
    /// slippage: max acceptable slippage for market orders (default 0.1%).
    /// </summary>
    public async Task<Dictionary<string, object>> PlaceOrderAsync(
        string coin, string side, double size, double price,
        bool isLimit = true, bool reduceOnly = false, string tif = "Gtc", double slippage = 0.1)
    {
        var payload = new Dictionary<string, object>
        {
            // The bridge's order endpoint does no normalization of its own — it expects the exact
            // Hyperliquid coin name (e.g. "BTC"), not "BTCUSDT". Every prior call site sent the raw
            // symbol here, so every order this pipeline ever placed would have been rejected.
            ["coin"] = NormalizeToHyperliquidCoin(coin),
            ["side"] = side,
            ["size"] = size,
            ["price"] = price,
            ["isLimit"] = isLimit,
            ["reduceOnly"] = reduceOnly,
            ["tif"] = tif,
            ["slippage"] = slippage,
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" });
        var resp = await _http.PostAsync($"{_baseUrl}/api/user/order", content);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Order failed ({resp.StatusCode}): {errBody}");
        }

        return await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>()
            ?? throw new InvalidOperationException("Empty response from place-order");
    }

    /// <summary>
    /// Cancel a specific order.
    /// </summary>
    public async Task<Dictionary<string, object>> CancelOrderAsync(
        string coin, string side, string orderType, double limitPx)
    {
        var payload = new Dictionary<string, object>
        {
            ["coin"] = NormalizeToHyperliquidCoin(coin),
            ["side"] = side,
            ["orderType"] = orderType,
            ["limitPx"] = limitPx,
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" });
        var resp = await _http.PostAsync($"{_baseUrl}/api/user/cancel", content);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"Cancel failed ({resp.StatusCode}): {errBody}");
        }

        return await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>()
            ?? throw new InvalidOperationException("Empty response from cancel-order");
    }

    /// <summary>
    /// Cancel all open orders for the account.
    /// </summary>
    public async Task<Dictionary<string, object>> CancelAllOrdersAsync()
    {
        var resp = await _http.PostAsync($"{_baseUrl}/api/user/cancel_all", null);

        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw new HttpRequestException($"CancelAll failed ({resp.StatusCode}): {errBody}");
        }

        return await resp.Content.ReadFromJsonAsync<Dictionary<string, object>>()
            ?? throw new InvalidOperationException("Empty response from cancel-all");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> IntervalMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "1m", "1" }, { "5m", "5" }, { "15m", "15" },
        { "1h", "60" }, { "4h", "240" }, { "1d", "1440" },
    };

    // System.Text.Json deserializes Dictionary<string, object> values as JsonElement, which
    // isn't IConvertible — Convert.ToInt64/ToDouble throw InvalidCastException on them.
    // Hyperliquid also encodes OHLC prices as JSON strings (precision), not numbers.
    private static long AsLong(object v) => v switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetInt64(),
        JsonElement { ValueKind: JsonValueKind.String } je => long.Parse(je.GetString()!),
        _ => Convert.ToInt64(v),
    };

    private static double AsDouble(object v) => v switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.String } je => double.Parse(je.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
        _ => Convert.ToDouble(v),
    };

    private static void ValidateSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol required");
        // Hyperliquid symbols are uppercase without trailing USDT (e.g., "BTC", "ETH").
        // Accept both formats and normalize.
        var normalized = symbol.ToUpperInvariant().Replace("USDT", "").Replace("USD", "");
        if (normalized.Length < 1)
            throw new ArgumentException($"Invalid symbol: {symbol}");
    }

    // Binance-style tickers Hyperliquid lists under a different native name — its 1000x meme-coin
    // convention uses a "k" prefix instead of a "1000" prefix (e.g. "1000BONK" -> "kBONK").
    private static readonly Dictionary<string, string> HlTickerOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1000BONK"] = "kBONK",
        ["1000PEPE"] = "kPEPE",
        ["1000SHIB"] = "kSHIB",
    };

    // Converts an exchange-agnostic symbol (e.g. "BTCUSDT", "1000BONKUSDT") to Hyperliquid's own
    // coin name (e.g. "BTC", "kBONK"). Every outbound call must go through this — the bridge's
    // order endpoint in particular does zero normalization of its own and expects the exact name.
    public static string NormalizeToHyperliquidCoin(string symbol)
    {
        string stripped = symbol.ToUpperInvariant().Replace("USDT", "").Replace("USD", "");
        return HlTickerOverrides.TryGetValue(stripped, out var mapped) ? mapped : stripped;
    }

    private async Task<List<Dictionary<string, object>>> GetJsonListAsync(string url)
    {
        var resp = await _http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"API error ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<List<Dictionary<string, object>>>(json)
            ?? new List<Dictionary<string, object>>();
    }

    private async Task<Dictionary<string, object>> GetJsonObjectAsync(string url)
    {
        var resp = await _http.GetAsync(url);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"API error ({resp.StatusCode}): {await resp.Content.ReadAsStringAsync()}");
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<Dictionary<string, object>>(json)
            ?? new Dictionary<string, object>();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _http.Dispose();
            _disposed = true;
        }
    }
}
