namespace TradingGA;

// Which strategies route in which regime, decided on a TRAILING window of realised trades and
// re-decided every refresh period. Replaces the single static snapshot in
// genotypes/strategy_family_gate.json.
//
// WHY THIS AND NOT THE STATIC GATE. StrategyFamilyGating.Build fits (strategy, regime) profit
// factors over the whole training trade set, and RegimeRouterSession then gates those same trades
// on the result — with FamilyConfidence = in-sample PF / 3 feeding position size directly. Every
// number downstream of that is in-sample by construction: the gate already knows which regimes the
// strategy turned out to be profitable in. No OOS claim can survive it, whatever the backtest says.
//
// Here the decision that applies at time t is fitted only on trades that CLOSED before the refresh
// period containing t began, so a trade is never gated on knowledge of itself or of anything after
// it. Same shape as SymbolCrowdingCap's anchored rolling estimate, and for the same reason.
//
// AND IT IS THE DECAY DETECTOR. A strategy whose edge dies drops below MinPf in the trailing
// window and routes itself off, with no retrain and nobody watching; it routes back on if the
// edge returns. That is one mechanism, not a gate plus a separate monitor.
//
// ponytail: per-strategy buckets, no SIMFAM clustering. The committed gate clustered eight
// strategies into five families of exactly one member each at the 0.7 correlation threshold, so
// the clustering was a no-op that only added a way to pool an honest bucket with a dishonest one.
// Upgrade path: if a strategy is genuinely too thin to clear MinTrades on its own window, pool it
// with its correlation family rather than lowering MinTrades.
public sealed class RollingStrategyGate
{
    public const int    DefaultWindowDays  = 180;
    public const int    DefaultRefreshDays = 30;
    public const int    DefaultMinTrades   = 20;
    public const double DefaultMinPf       = 1.0;

    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Bucket is whatever dimension the gate conditions on: the BTC regime for the routing gate,
    // the symbol for the per-coin screen. One class, because the statistic and the lookahead
    // guard are identical either way — only the key changes.
    private readonly record struct Key(string Strategy, string Bucket);
    // Vol: standard deviation of the trade returns in the trailing window, in percent. Carried
    // alongside the gate decision because risk-parity sizing needs the SAME trailing, already-closed
    // window the gate uses — sizing a strategy down because we observed its drawdown on the window
    // being sized is the in-sample error the static family gate made.
    public readonly record struct Verdict(bool Active, double Confidence, double Pf, int Trades, double Vol);

    // Trades bucketed by (strategy, regime at ENTRY), each bucket sorted by EXIT time.
    private readonly Dictionary<Key, List<(DateTime Time, double Return)>> _buckets = new();
    private readonly Dictionary<(long Period, Key K), Verdict> _snaps = new();
    private readonly int _windowDays, _refreshDays, _minTrades;
    private readonly double _minPf;

    // trades: Bucket is the dimension conditioned on; ExitTime decides WHEN the evidence becomes
    // available, because a trade's outcome is not known until it closes. Using the entry time for
    // both would let a still-open trade inform the decision that admits it.
    public RollingStrategyGate(
        IEnumerable<(string Strategy, string Bucket, DateTime ExitTime, double Return)> trades,
        int windowDays  = DefaultWindowDays,
        int refreshDays = DefaultRefreshDays,
        int minTrades   = DefaultMinTrades,
        double minPf    = DefaultMinPf)
    {
        _windowDays = windowDays; _refreshDays = refreshDays;
        _minTrades  = minTrades;  _minPf = minPf;

        foreach (var (strategy, bucket, exit, ret) in trades)
        {
            var key = new Key(strategy, bucket);
            if (!_buckets.TryGetValue(key, out var list)) _buckets[key] = list = new();
            list.Add((exit, ret));          // keyed on when the outcome became known
        }
        foreach (var list in _buckets.Values) list.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    // Routing gate: bucket = the BTC regime at the trade's ENTRY, which is the regime the decision
    // is conditioned on. RegimeBarLookup.TagRegimes does the binary search.
    public static RollingStrategyGate ByRegime(
        IReadOnlyList<(string Strategy, DateTime EntryTime, DateTime ExitTime, double Return)> trades,
        RegimeBar[] regimeSeries,
        int windowDays  = DefaultWindowDays,
        int refreshDays = DefaultRefreshDays,
        int minTrades   = DefaultMinTrades,
        double minPf    = DefaultMinPf)
    {
        var regimes = RegimeBarLookup.TagRegimes(regimeSeries, trades.Select(t => t.EntryTime).ToList());
        return new RollingStrategyGate(
            trades.Select((t, i) => (t.Strategy, regimes[i].ToString(), t.ExitTime, t.Return)),
            windowDays, refreshDays, minTrades, minPf);
    }

    // Per-coin screen: bucket = the symbol. This is the honest form of the per-coin filter
    // oosbacktest applies (PF >= 1.2 and Sortino >= 0.3 on the coin's OWN leading slice, then
    // reported on the rest) — same idea, fitted on a trailing window instead of in-sample.
    public static RollingStrategyGate BySymbol(
        IEnumerable<(string Strategy, string Symbol, DateTime ExitTime, double Return)> trades,
        int windowDays  = 365,
        int refreshDays = DefaultRefreshDays,
        int minTrades   = DefaultMinTrades,
        double minPf    = DefaultMinPf)
        => new(trades, windowDays, refreshDays, minTrades, minPf);

    public bool   IsActive(string strategy, MarketRegime regime, DateTime at) => At(strategy, regime.ToString(), at).Active;
    public double Confidence(string strategy, MarketRegime regime, DateTime at) => At(strategy, regime.ToString(), at).Confidence;
    public bool   IsActive(string strategy, string bucket, DateTime at) => At(strategy, bucket, at).Active;

    public Verdict At(string strategy, MarketRegime regime, DateTime at) => At(strategy, regime.ToString(), at);

    public Verdict At(string strategy, string bucket, DateTime at)
    {
        var key = new Key(strategy, bucket);
        long period = (long)Math.Floor((at - Epoch).TotalDays / _refreshDays);
        if (_snaps.TryGetValue((period, key), out var cached)) return cached;

        var verdict = Fit(key, Epoch.AddDays(period * _refreshDays));
        _snaps[(period, key)] = verdict;
        return verdict;
    }

    // Profit factor over trades that closed inside [periodStart - windowDays, periodStart). The
    // half-open upper bound is the lookahead guard: a trade in the period being gated is excluded
    // from the evidence that gates it.
    private Verdict Fit(Key key, DateTime periodStart)
    {
        if (!_buckets.TryGetValue(key, out var list)) return default;

        DateTime windowStart = periodStart.AddDays(-_windowDays);
        double wins = 0, losses = 0, sum = 0, sumSq = 0;
        int n = 0;
        foreach (var (time, ret) in list)
        {
            if (time >= periodStart) break;          // sorted, so nothing later qualifies either
            if (time < windowStart) continue;
            if (ret > 0) wins += ret; else losses += Math.Abs(ret);
            sum += ret; sumSq += ret * ret;
            n++;
        }

        if (n < _minTrades) return new Verdict(false, 0.0, 0.0, n, 0.0);

        double mean = sum / n;
        double vol  = Math.Sqrt(Math.Max(0.0, sumSq / n - mean * mean));

        double pf = losses > 1e-12 ? wins / losses : (wins > 0 ? 99.0 : 0.0);
        if (pf < _minPf) return new Verdict(false, 0.0, pf, n, vol);

        // Bounded in [0,1) and 0 at breakeven. Deliberately NOT pf/3: that is unbounded below the
        // clamp and sizes hardest on the thin buckets whose PF is least trustworthy.
        double conf = Math.Clamp((pf - 1.0) / (pf + 1.0), 0.0, 1.0);
        return new Verdict(true, conf, pf, n, vol);
    }
}
