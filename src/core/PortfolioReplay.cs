namespace TradingGA;

// Filters combined trade list by per-strategy concurrent position count. Apply before exposure cap.
public static class PortfolioReplay
{
    // Default caps per strategy. WARNING: unknown label → falls back to int.MaxValue,
    // counted against both directional caps.
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
        ["accumgrid"]  = 12,
    };

    private static readonly HashSet<string> LongStrategies = new(StringComparer.OrdinalIgnoreCase)
        { "diplong", "fadelong", "swing_long", "grid", "accumgrid" };

    private static readonly HashSet<string> ShortStrategies = new(StringComparer.OrdinalIgnoreCase)
        { "swing", "fade_short", "ripshort", "gridshort" };

    // Direction of a strategy. Null = unknown label → callers should be conservative.
    public static bool? IsLong(string strategy) =>
        LongStrategies.Contains(strategy)  ? true
      : ShortStrategies.Contains(strategy) ? false
      : null;

    // Symbol enables per-coin cap. Optional, but PerSymbolCap warns if trades carry no Symbol.
    public record Trade(
        string   Strategy,
        DateTime EntryTime,
        TimeSpan HoldDuration,
        double   Return,
        double   Conf,
        string   Symbol = "");


    public static List<Trade> FilterByConcurrentCap(
        IEnumerable<Trade> trades,
        Dictionary<string, int>? caps = null,
        int directionalCap = int.MaxValue,
        int perSymbolCap   = int.MaxValue)
    {
        caps ??= DefaultCaps;
        var sorted  = trades.OrderBy(t => t.EntryTime).ToList();
        var result  = new List<Trade>(sorted.Count);
        var openClose = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        var longCloses  = new List<DateTime>();
        var shortCloses = new List<DateTime>();
        var warnedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnedNoCap = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Per-coin open positions, keyed strategy|symbol. Only consulted when a cap is asked for.
        var symbolCloses = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        bool warnedNoSymbol = false;

        foreach (var t in sorted)
        {

            if (!caps.TryGetValue(t.Strategy, out int cap))
            {
                cap = int.MaxValue;
                if (warnedNoCap.Add(t.Strategy))
                {
                    Console.WriteLine($"  !! PortfolioReplay: strategy label '{t.Strategy}' not in caps — falls back to int.MaxValue");
                }
            }

            if (!openClose.TryGetValue(t.Strategy, out var closes))
            {
                closes = new List<DateTime>();
                openClose[t.Strategy] = closes;
            }

            closes.RemoveAll(ct => ct <= t.EntryTime);
            longCloses.RemoveAll(ct => ct <= t.EntryTime);
            shortCloses.RemoveAll(ct => ct <= t.EntryTime);

            if (closes.Count >= cap) continue;

            // Per-coin cap.
            List<DateTime>? symCloses = null;
            if (perSymbolCap != int.MaxValue)
            {
                if (string.IsNullOrEmpty(t.Symbol))
                {
                    if (!warnedNoSymbol)
                    {
                        warnedNoSymbol = true;
                        Console.WriteLine("  !! PortfolioReplay: perSymbolCap requested but trades carry no Symbol — " +
                                          "per-coin concentration is NOT being capped");
                    }
                }
                else
                {
                    string key = t.Strategy + "|" + t.Symbol;
                    if (!symbolCloses.TryGetValue(key, out symCloses))
                        symbolCloses[key] = symCloses = new List<DateTime>();
                    symCloses.RemoveAll(ct => ct <= t.EntryTime);
                    if (symCloses.Count >= perSymbolCap) continue;
                }
            }

            // Unknown label → counts against both directional caps.
            bool knownLong  = LongStrategies.Contains(t.Strategy);
            bool knownShort = ShortStrategies.Contains(t.Strategy);
            bool unknownDir = !knownLong && !knownShort;

            if (unknownDir && warnedLabels.Add(t.Strategy))
                Console.WriteLine($"  !! PortfolioReplay: strategy label '{t.Strategy}' has no known direction — counted against both directional caps");

            bool countsLong  = knownLong  || unknownDir;
            bool countsShort = knownShort || unknownDir;

            if (countsLong  && longCloses.Count  >= directionalCap) continue;
            if (countsShort && shortCloses.Count >= directionalCap) continue;

            var closeTime = t.EntryTime + t.HoldDuration;
            closes.Add(closeTime);
            symCloses?.Add(closeTime);
            if (countsLong)  longCloses.Add(closeTime);
            if (countsShort) shortCloses.Add(closeTime);
            result.Add(t);
        }

        return result;
    }
}
