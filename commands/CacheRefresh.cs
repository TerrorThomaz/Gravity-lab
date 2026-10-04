using Bybit.Net.Clients;

namespace TradingGA;

// cacherefresh: top up the 15m candle and funding caches for every configured symbol, then exit.
//
// Exists for the FORWARD TEST (docs/FORWARD_TEST_GRID_2026-10.md). The forward window is evaluated
// later by edgetest from the cache, so the cache must be kept current WHILE the window runs: Bybit
// serves no history for delisted symbols, and a coin that dies mid-window would otherwise vanish from
// the evidence, which is survivorship bias in the forward test itself. Run daily (systemd user timer).
public static class CacheRefresh
{
    public static async Task Run(BybitRestClient client)
    {
        if (CandleFetcher.Offline) { Console.WriteLine("cacherefresh: GRAVITY_OFFLINE=1 set — nothing to do"); return; }
        var symbols = Config.BacktestCoins.Concat(Config.OosCoins).Append("BTCUSDT").Append("ETHUSDT").Distinct().ToArray();
        var fetched = await StrategyPipeline.FetchFifteenMinAsync(client, symbols, batches: 113);
        await CandleFetcher.FetchFundingSessionsAsync(client, symbols);
        var now = DateTime.UtcNow;
        var live = fetched.Where(f => f.m15.Length > 0).ToArray();
        var stale = live.Where(f => now - f.m15[^1].Time > TimeSpan.FromHours(2)).Select(f => f.sym).ToArray();
        Console.WriteLine($"cacherefresh {now:yyyy-MM-dd HH:mm} UTC: {live.Length}/{symbols.Length} symbols with candles; " +
                          $"{stale.Length} stale (> 2h behind){(stale.Length > 0 ? ": " + string.Join(",", stale.Take(20)) : "")}");
    }
}
