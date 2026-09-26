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


    // `crowding` is the correlation-aware directional cap. It is OPTIONAL and one-sided: when null
    // — or built at strength 0 — this method behaves bit-for-bit as it did before, because
    // SymbolCrowdingCap.Exceeds returns false without evaluating anything. When active it can only
    // remove trades the headcount would have admitted, never add ones it rejected.
    //
    // It is applied to the DIRECTIONAL cap only, not the per-strategy or per-symbol caps. The
    // directional cap is the one whose stated purpose is correlated exposure clustering; the
    // per-strategy caps are about strategy concentration, which correlation between symbols does
    // not speak to.
    public static List<Trade> FilterByConcurrentCap(
        IEnumerable<Trade> trades,
        Dictionary<string, int>? caps = null,
        int directionalCap = int.MaxValue,
        int perSymbolCap   = int.MaxValue,
        SymbolCrowdingCap? crowding = null)
    {
        caps ??= DefaultCaps;
        var sorted  = trades.OrderBy(t => t.EntryTime).ToList();
        var result  = new List<Trade>(sorted.Count);
        var openClose = new Dictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        // Open positions per direction. Close time and symbol travel together so the expiry sweep
        // cannot drift them out of alignment; .Count and RemoveAll behave exactly as they did when
        // these were plain DateTime lists.
        var longOpen  = new List<Open>();
        var shortOpen = new List<Open>();
        bool crowdOn = crowding is { Strength: > 0.0 };
        int crowdSkipped = 0;
        bool warnedCrowdNoSymbol = false;
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
            longOpen.RemoveAll(o => o.Close <= t.EntryTime);
            shortOpen.RemoveAll(o => o.Close <= t.EntryTime);

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

            if (countsLong  && longOpen.Count  >= directionalCap) continue;
            if (countsShort && shortOpen.Count >= directionalCap) continue;

            // Correlation-aware directional cap. Strictly additional: every trade rejected here
            // had already passed the headcount above, which is what makes this tighten-only.
            if (crowdOn)
            {
                if (string.IsNullOrEmpty(t.Symbol))
                {
                    if (!warnedCrowdNoSymbol)
                    {
                        warnedCrowdNoSymbol = true;
                        Console.WriteLine("  !! PortfolioReplay: crowding cap requested but trades carry no Symbol — " +
                                          "correlated exposure is NOT being charged for");
                    }
                }
                else
                {
                    bool blocked =
                        (countsLong  && crowding!.Exceeds(Symbols(longOpen),  t.Symbol, directionalCap, t.EntryTime)) ||
                        (countsShort && crowding!.Exceeds(Symbols(shortOpen), t.Symbol, directionalCap, t.EntryTime));
                    if (blocked) { crowdSkipped++; continue; }
                }
            }

            var closeTime = t.EntryTime + t.HoldDuration;
            closes.Add(closeTime);
            symCloses?.Add(closeTime);
            if (countsLong)  longOpen.Add(new Open(closeTime, t.Symbol));
            if (countsShort) shortOpen.Add(new Open(closeTime, t.Symbol));
            result.Add(t);
        }

        if (crowdOn)
        {
            Console.WriteLine($"  Crowding cap (strength {crowding!.Strength:F2}, {crowding.Symbols} symbols): " +
                              $"removed {crowdSkipped} further trades beyond the headcount cap");
            if (crowding.UnknownSymbolHits > 0)
                Console.WriteLine($"     {crowding.UnknownSymbolHits} candidate trades named a symbol absent from the " +
                                  "anchored correlation window (too little history) — charged at correlation 1.0 (worst case)");
        }

        return result;
    }

    private readonly record struct Open(DateTime Close, string Symbol);

    private static List<string> Symbols(List<Open> open)
    {
        var s = new List<string>(open.Count);
        foreach (var o in open) s.Add(o.Symbol);
        return s;
    }
}
