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
    // NOTE: Any strategy label absent from these sets will warn once per call and be
    // counted conservatively against both directional caps.
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

    // Direction of a strategy label, for consumers that must treat long and short exposure
    // differently — the rotator's safety score inverts its regime term by direction. Null means
    // the label is unknown; callers should pick the conservative reading rather than guess,
    // exactly as the concurrent-cap logic below counts an unknown label against BOTH caps.
    public static bool? IsLong(string strategy) =>
        LongStrategies.Contains(strategy)  ? true
      : ShortStrategies.Contains(strategy) ? false
      : null;

    // Symbol enables a PER-COIN concurrency cap. Without it this record cannot tell
    // "8 positions on 8 coins" from "8 positions on ONE coin" — the cap counts per STRATEGY
    // only, so all per-coin exposure was invisible to the risk layer. That is the same
    // accounting hole that keeps RipShort's DcaAndWait marked MUST NOT BE ENABLED, and it has
    // to close before any multi-leg mode can be trusted.
    //
    // Optional with a default so existing call sites compile, but PerSymbolCap warns loudly
    // when it is asked to enforce a limit on trades that carry no symbol — a silent no-op here
    // would be worse than the missing feature.
    public record Trade(
        string   Strategy,
        DateTime EntryTime,
        TimeSpan HoldDuration,
        double   Return,
        double   Conf,
        string   Symbol = "");

    // Filter trades by concurrent position cap. Returns a new list with capped trades removed.
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
            // Warn once if caps has no entry for this label
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

            // Per-coin cap. This is what makes a multi-leg mode safe: without it, N legs on one
            // coin look identical to N positions spread across N coins, and the concentration
            // risk is invisible.
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

            // An unrecognised label has no known direction, so it counts against BOTH
            // directional caps rather than escaping them — the conservative choice.
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
