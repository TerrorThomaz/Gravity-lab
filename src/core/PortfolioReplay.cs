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
    // Default caps per strategy kind. DirectionalCap (passed separately) limits
    // total same-direction concurrent exposure across all strategies.
    public static readonly Dictionary<string, int> DefaultCaps = new()
    {
        ["fade_short"] = 10,
        ["swing"]      = 10,
        ["swing_long"] = 8,
        ["diplong"]    = 8,
        ["fadelong"]   = 8,
        ["ripshort"]   = 8,
        ["grid"]       = 12,
        ["gridshort"]  = 12,
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
        Dictionary<string, int>? caps = null,
        int directionalCap = int.MaxValue)
    {
        caps ??= DefaultCaps;
        var sorted  = trades.OrderBy(t => t.EntryTime).ToList();
        var result  = new List<Trade>(sorted.Count);
        var openClose = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        var longStrategies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "diplong", "fadelong", "swing_long", "grid" };
        var shortStrategies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "swing", "fade_short", "ripshort", "gridshort" };
        var longCloses  = new List<DateTime>();
        var shortCloses = new List<DateTime>();

        foreach (var t in sorted)
        {
            int cap = caps.TryGetValue(t.Strategy, out int c) ? c : int.MaxValue;

            if (!openClose.TryGetValue(t.Strategy, out var closes))
            {
                closes = new List<DateTime>();
                openClose[t.Strategy] = closes;
            }

            closes.RemoveAll(ct => ct <= t.EntryTime);
            longCloses.RemoveAll(ct => ct <= t.EntryTime);
            shortCloses.RemoveAll(ct => ct <= t.EntryTime);

            if (closes.Count >= cap) continue;

            bool isLong  = longStrategies.Contains(t.Strategy);
            bool isShort = shortStrategies.Contains(t.Strategy);
            if (isLong && longCloses.Count >= directionalCap) continue;
            if (isShort && shortCloses.Count >= directionalCap) continue;

            closes.Add(t.EntryTime + t.HoldDuration);
            if (isLong)  longCloses.Add(t.EntryTime + t.HoldDuration);
            if (isShort) shortCloses.Add(t.EntryTime + t.HoldDuration);
            result.Add(t);
        }

        return result;
    }
}
