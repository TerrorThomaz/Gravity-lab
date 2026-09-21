namespace TradingGA;

// SIMFAM-based strategy gating: clusters strategies into families by return correlation,
// computes empirical profitability per family per regime, and provides a simple gate
// (family active in regime iff PF > threshold with sufficient sample size).
// Replaces the GA-evolved favorability matrix with an interpretable, data-driven mapping.
//
// Pipeline:
//   1. StrategyAllocator.Compute → SIMFAM families (single-linkage on shrunk correlation).
//   2. For each trade, look up the regime at trade time (hour-level index).
//   3. Group trades by (family, regime) → compute PF, WR, avg return, count.
//   4. Gate: family is active in a regime iff PF >= minPf AND tradeCount >= minTrades.
public static class StrategyFamilyGating
{
    public const int MinTradesPerBucket = 20;

    public record RegimeProfitability(
        double ProfitFactor,
        double WinRate,
        double AvgReturn,
        int TradeCount,
        bool IsActive);

    public record FamilyStats(
        string FamilyId,
        IReadOnlyList<string> Members,
        IReadOnlyDictionary<MarketRegime, RegimeProfitability> ProfitabilityByRegime);

    public record Gate(
        IReadOnlyDictionary<string, string> FamilyOf,
        IReadOnlyList<FamilyStats> Families,
        IReadOnlyDictionary<MarketRegime, IReadOnlyList<string>> ActiveFamiliesByRegime,
        double FamilyCorrThreshold);

    public static Gate Build(
        IReadOnlyDictionary<string, IReadOnlyList<(DateTime Time, double Return)>> tradesByStrategy,
        RegimeBar[] regimeSeries,
        double familyCorrThreshold = 0.7,
        double minPfForActivation = 1.0,
        int minTradesPerBucket = MinTradesPerBucket)
    {
        // 1. Compute families via StrategyAllocator (SIMFAM clustering on shrunk correlation).
        var alloc = StrategyAllocator.Compute(tradesByStrategy, familyCorrThreshold: familyCorrThreshold);

        // 2. Build time index for regime lookup (hour-level, same as RegimeRouterGA).
        var timeIndex = BuildTimeIndex(regimeSeries);

        // 3. Group trades by (family, regime) and collect returns.
        var familyRegimeReturns = new Dictionary<string, Dictionary<MarketRegime, List<double>>>();
        foreach (var (strategy, trades) in tradesByStrategy)
        {
            if (!alloc.FamilyOf.TryGetValue(strategy, out var familyId)) continue;
            if (!familyRegimeReturns.ContainsKey(familyId))
                familyRegimeReturns[familyId] = new Dictionary<MarketRegime, List<double>>();
            var regimeReturns = familyRegimeReturns[familyId];

            foreach (var (time, ret) in trades)
            {
                var regime = LookupRegime(timeIndex, time, regimeSeries);
                if (regime == null) continue;
                if (!regimeReturns.ContainsKey(regime.Value))
                    regimeReturns[regime.Value] = new List<double>();
                regimeReturns[regime.Value].Add(ret);
            }
        }

        // 4. Compute profitability stats per (family, regime).
        var families = new List<FamilyStats>();
        var familyMembers = new Dictionary<string, List<string>>();
        foreach (var (strategy, familyId) in alloc.FamilyOf)
        {
            if (!familyMembers.ContainsKey(familyId)) familyMembers[familyId] = new List<string>();
            familyMembers[familyId].Add(strategy);
        }

        foreach (var (familyId, members) in familyMembers)
        {
            var profitabilityByRegime = new Dictionary<MarketRegime, RegimeProfitability>();
            foreach (MarketRegime regime in Enum.GetValues<MarketRegime>())
            {
                var returns = familyRegimeReturns.TryGetValue(familyId, out var rr)
                    && rr.TryGetValue(regime, out var r) ? r : new List<double>();
                profitabilityByRegime[regime] = ComputeProfitability(returns, minPfForActivation, minTradesPerBucket);
            }
            families.Add(new FamilyStats(familyId, members, profitabilityByRegime));
        }

        // 5. Build active families per regime.
        var activeByRegime = new Dictionary<MarketRegime, IReadOnlyList<string>>();
        foreach (MarketRegime regime in Enum.GetValues<MarketRegime>())
        {
            var active = families
                .Where(f => f.ProfitabilityByRegime[regime].IsActive)
                .Select(f => f.FamilyId)
                .ToList();
            activeByRegime[regime] = active;
        }

        return new Gate(alloc.FamilyOf, families, activeByRegime, familyCorrThreshold);
    }

    public static bool IsActive(Gate gate, string strategy, MarketRegime regime)
    {
        if (!gate.FamilyOf.TryGetValue(strategy, out var familyId)) return false;
        if (!gate.ActiveFamiliesByRegime.TryGetValue(regime, out var activeFamilies)) return false;
        return activeFamilies.Contains(familyId);
    }

    public static double FamilyConfidence(Gate gate, string strategy, MarketRegime regime)
    {
        if (!gate.FamilyOf.TryGetValue(strategy, out var familyId)) return 0.0;
        var family = gate.Families.FirstOrDefault(f => f.FamilyId == familyId);
        if (family == null) return 0.0;
        if (!family.ProfitabilityByRegime.TryGetValue(regime, out var prof)) return 0.0;
        if (!prof.IsActive) return 0.0;
        // Confidence = PF clamped to [0, 3], normalized to [0, 1].
        return Math.Clamp(prof.ProfitFactor / 3.0, 0.0, 1.0);
    }

    public static void Print(Gate gate)
    {
        Console.WriteLine($"\n── SIMFAM strategy families (corr threshold {gate.FamilyCorrThreshold:F2}) ──────");
        foreach (var fam in gate.Families)
        {
            Console.WriteLine($"  Family {fam.FamilyId}: {string.Join(", ", fam.Members)}");
            foreach (var (regime, prof) in fam.ProfitabilityByRegime)
            {
                string active = prof.IsActive ? "✓" : "✗";
                Console.WriteLine($"    {regime,-10} PF={prof.ProfitFactor,6:F2}  WR={prof.WinRate,5:P0}  " +
                    $"avg={prof.AvgReturn,+7:F3}%  n={prof.TradeCount,4}  {active}");
            }
        }
        Console.WriteLine($"\n  Active families by regime:");
        foreach (var (regime, active) in gate.ActiveFamiliesByRegime)
        {
            Console.WriteLine($"    {regime,-10} → {string.Join(", ", active)}");
        }
    }

    static RegimeProfitability ComputeProfitability(List<double> returns, double minPf, int minTrades)
    {
        if (returns.Count < minTrades)
            return new RegimeProfitability(0.0, 0.0, 0.0, returns.Count, false);

        double gross = 0, wins = 0, losses = 0;
        int winCount = 0;
        foreach (var r in returns)
        {
            gross += r;
            if (r > 0) { wins += r; winCount++; }
            else losses += Math.Abs(r);
        }
        double pf = losses > 1e-12 ? wins / losses : (wins > 0 ? 99.0 : 0.0);
        double wr = (double)winCount / returns.Count;
        double avg = gross / returns.Count;
        bool active = pf >= minPf && returns.Count >= minTrades;
        return new RegimeProfitability(pf, wr, avg, returns.Count, active);
    }

    static Dictionary<long, int> BuildTimeIndex(RegimeBar[] series)
    {
        var idx = new Dictionary<long, int>(series.Length);
        for (int i = 0; i < series.Length; i++)
            idx.TryAdd(series[i].Time.Ticks / TimeSpan.TicksPerHour, i);
        return idx;
    }

    static MarketRegime? LookupRegime(Dictionary<long, int> idx, DateTime t, RegimeBar[] series)
    {
        long key = t.Ticks / TimeSpan.TicksPerHour;
        if (idx.TryGetValue(key, out int bar)) return series[bar].Regime;
        for (int delta = 1; delta <= 4; delta++)
            if (idx.TryGetValue(key - delta, out bar)) return series[bar].Regime;
        return null;
    }
}

// DTO for serializing the family gate to JSON.
public record FamilyStatsDto(
    string FamilyId,
    List<string> Members,
    Dictionary<string, RegimeProfitabilityDto> ProfitabilityByRegime);

public record RegimeProfitabilityDto(
    double ProfitFactor,
    double WinRate,
    double AvgReturn,
    int TradeCount,
    bool IsActive);

public record StrategyFamilyGatingDto(
    Dictionary<string, string> FamilyOf,
    List<FamilyStatsDto> Families,
    Dictionary<string, List<string>> ActiveFamiliesByRegime,
    double FamilyCorrThreshold)
{
    public static StrategyFamilyGatingDto From(StrategyFamilyGating.Gate gate)
    {
        var families = gate.Families.Select(f => new FamilyStatsDto(
            f.FamilyId,
            f.Members.ToList(),
            f.ProfitabilityByRegime.ToDictionary(
                kv => kv.Key.ToString(),
                kv => new RegimeProfitabilityDto(
                    kv.Value.ProfitFactor,
                    kv.Value.WinRate,
                    kv.Value.AvgReturn,
                    kv.Value.TradeCount,
                    kv.Value.IsActive)))).ToList();

        var activeByRegime = gate.ActiveFamiliesByRegime.ToDictionary(
            kv => kv.Key.ToString(),
            kv => kv.Value.ToList());

        return new StrategyFamilyGatingDto(
            new Dictionary<string, string>(gate.FamilyOf),
            families,
            activeByRegime,
            gate.FamilyCorrThreshold);
    }

    public StrategyFamilyGating.Gate ToGate()
    {
        var familyOf = new Dictionary<string, string>(FamilyOf);

        var families = Families.Select(f => new StrategyFamilyGating.FamilyStats(
            f.FamilyId,
            f.Members,
            f.ProfitabilityByRegime.ToDictionary(
                kv => Enum.Parse<MarketRegime>(kv.Key),
                kv => new StrategyFamilyGating.RegimeProfitability(
                    kv.Value.ProfitFactor,
                    kv.Value.WinRate,
                    kv.Value.AvgReturn,
                    kv.Value.TradeCount,
                    kv.Value.IsActive)))).ToList();

        var activeByRegime = ActiveFamiliesByRegime.ToDictionary(
            kv => Enum.Parse<MarketRegime>(kv.Key),
            kv => (IReadOnlyList<string>)kv.Value);

        return new StrategyFamilyGating.Gate(familyOf, families, activeByRegime, FamilyCorrThreshold);
    }
}
