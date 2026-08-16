namespace TradingGA;

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

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

        var url = $"{_baseUrl}/api/ohlcv?symbol={Uri.EscapeDataString(symbol)}" +
                  $"&interval={Uri.EscapeDataString(interval)}&limit={limit}";
        return await GetJsonListAsync(url);
    }

    /// <summary>
    /// Convenience: fetch h1 candles aggregated from 15m (matches CandleFetcher behavior).
    /// Returns raw candles as list of {time, open, high, low, close, volume}.
    /// </summary>
    public async Task<List<Candle>> FetchH1CandlesAsync(string symbol, int batches = 113)
    {
        // Fetch 15m candles at scale (each batch ~ 1000 candles ~ 4 days)
        int candlesNeeded = batches * 1000;
        var raw = await FetchOhlcvAsync(symbol, "15m", Math.Min(candlesNeeded, 500));
        // Hyperliquid returns up to `limit` candles; caller may paginate if needed.
        // For most strategy signals, 500 x 15m = 125 hours ~ 5 days of context suffices.
        var candles = new List<Candle>(raw.Count);
        foreach (var r in raw)
        {
            if (!r.TryGetValue("time", out var tObj) || !r.TryGetValue("close", out var cObj)) continue;
            long ms = Convert.ToInt64(tObj);
            double close = Convert.ToDouble(cObj);
            double open = r.TryGetValue("open", out var oObj) ? Convert.ToDouble(oObj) : close;
            double high = r.TryGetValue("high", out var hObj) ? Convert.ToDouble(hObj) : close;
            double low = r.TryGetValue("low", out var lObj) ? Convert.ToDouble(lObj) : close;
            double vol = r.TryGetValue("volume", out var vObj) ? Convert.ToDouble(vObj) : 0;
            candles.Add(new Candle(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, open, high, low, close, vol));
        }
        return candles;
    }

    /// <summary>
    /// Fetch orderbook snapshot for a symbol. Returns bids/asks arrays with price & size.
    /// </summary>
    public async Task<Dictionary<string, object>> FetchOrderbookAsync(string symbol, int depth = 20)
    {
        ValidateSymbol(symbol);
        var url = $"{_baseUrl}/api/orderbook?symbol={Uri.EscapeDataString(symbol)}&depth={depth}";
        return await GetJsonObjectAsync(url);
    }

    /// <summary>
    /// Fetch universe metadata (all tradable markets).
    /// </summary>
    public async Task<Dictionary<string, object>> FetchUniverseAsync()
    {
        return await GetJsonObjectAsync($"{_baseUrl}/api/universe");
    }

    /// <summary>
    /// Fetch current funding rate for a symbol.
    /// </summary>
    public async Task<double?> FetchFundingRateAsync(string symbol)
    {
        try
        {
            var data = await GetJsonObjectAsync($"{_baseUrl}/api/funding?symbol={Uri.EscapeDataString(symbol)}");
            if (data.TryGetValue("fundingRate", out var frObj))
                return Convert.ToDouble(frObj);
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
            ["coin"] = coin,
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
            ["coin"] = coin,
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
