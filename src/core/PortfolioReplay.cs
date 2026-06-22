namespace TradingGA;

// Filters a combined trade list by per-strategy concurrent position count.
// Trades are walked in entry-time order; a trade is skipped if its strategy
// already has maxConcurrent open positions at that entry time.
// HoldDuration is used to determine when each position closes.
//
// Apply BEFORE SimulatePortfolioExposureCapped so the EUR cap sees a realistic
// trade list that won't blow up with 93 simultaneously correlated positions.
public static class PortfolioReplay
{
    // Default caps per strategy kind (intentionally generous; reduces only catastrophic clustering)
    public static readonly Dictionary<string, int> DefaultCaps = new()
    {
        ["fade_short"] = 10,
        ["swing"]      = 10,
        ["swing_long"] = 8,
        ["diplong"]    = 8,
        ["fadelong"]   = 8,
        ["grid"]       = 12,
    };

    public record Trade(
        string   Strategy,
        DateTime EntryTime,
        TimeSpan HoldDuration,
        double   Return,
        double   Conf);

    // Filter trades by concurrent position cap. Returns a new list with capped trades removed.
    public static List<Trade> FilterByConcurrentCap(
        IEnumerable<Trade> trades,
        Dictionary<string, int>? caps = null)
    {
        caps ??= DefaultCaps;
        var sorted  = trades.OrderBy(t => t.EntryTime).ToList();
        var result  = new List<Trade>(sorted.Count);
        // Per-strategy list of close times for currently open positions
        var openClose = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in sorted)
        {
            int cap = caps.TryGetValue(t.Strategy, out int c) ? c : int.MaxValue;

            if (!openClose.TryGetValue(t.Strategy, out var closes))
            {
                closes = new List<DateTime>();
                openClose[t.Strategy] = closes;
            }

            // Remove positions that have closed by this entry time
            closes.RemoveAll(ct => ct <= t.EntryTime);

            if (closes.Count < cap)
            {
                closes.Add(t.EntryTime + t.HoldDuration);
                result.Add(t);
            }
            // else: trade skipped — cap exceeded
        }

        return result;
    }
}
