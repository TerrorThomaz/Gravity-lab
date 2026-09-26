namespace TradingGA;

// Correlation-aware replacement for the directional headcount in PortfolioReplay.
//
// Config.MaxDirectionalConcurrent = 20 counts heads: twenty open longs are twenty open longs
// whether they sit in one co-movement family or five. They are not the same risk. n equally-sized
// positions with mean pairwise correlation rho carry portfolio variance proportional to
// n·(1 + (n−1)·rho) instead of n, so a crowded book occupies more of the risk budget than its
// headcount suggests. This charges for that.
//
// ONE-SIDED BY CONSTRUCTION. EffectiveSlots(n, rho, strength) >= n for every strength >= 0 and
// every rho, because the inflation factor is floored at 1 (negative correlation is not allowed to
// buy extra slots — see the stress note below). So this cap can only ever REJECT a trade the
// headcount would have allowed; it can never ADMIT one the headcount rejected. That asymmetry is
// deliberate and load-bearing, not conservatism for its own sake: crypto correlations converge
// toward 1 in exactly the drawdowns this is meant to survive, so an estimate made on average
// conditions understates crowding when it matters and must never be trusted in the loosening
// direction.
//
// EXACT NO-OP AT STRENGTH 0. Math.Pow(x, 0) is 1.0 for every finite x, so EffectiveSlots returns
// n unchanged and the cap is bit-for-bit the pre-existing headcount. Strength defaults to 0, i.e.
// OFF, and is opted into with GRAVITY_CROWDING. Without that, "the setting did nothing" cannot be
// told apart from "the setting was never on" — the ambiguity the BtcAlignWeight pilot was designed
// around.
//
// WHY MEAN PAIRWISE CORRELATION AND NOT EFFECTIVE BETS. For an equicorrelation block the two agree
// exactly: n/EffectiveBets == 1 + (n−1)·rho. Away from it they differ, but a book holds ~20 open
// positions against ~1,170 daily observations, and a 20x20 sub-matrix eigendecomposition per trade
// is both over-parameterised and O(n³) in the hot path. The pairwise mean is the robust statistic
// at that sample size and it is O(n²) — the same argument symbolcov makes for quoting mean
// off-diagonal in its stress block.
//
// ANCHORED, ROLLING ESTIMATE. Correlation is not read from a sample matrix. Each symbol is
// regressed on the two anchors the router already trusts — BTC and ETH daily log returns — and
// pairwise correlation is implied through them:
//
//     rho_ij = b_i' · Sigma_F · b_j / (sigma_i · sigma_j)
//
// This replaced a single sample matrix estimated once, strictly before the book's first trade.
// That was lookahead-safe but needed every symbol to span one common window, so on the OOS book
// (first trade 2022-05) only 3 symbols qualified, every other symbol was charged at correlation
// 1.0, and the cap collapsed ~97 coins into one and cut the book by 40-58%. An anchored model
// needs only each symbol's OWN overlap with BTC/ETH, so a coin enters as soon as it has minObs
// days of history. Loadings are re-estimated every refreshDays on the trailing windowDays of data
// strictly BEFORE the period starts, so a trade never sees a correlation fitted on its own future.
//
// ponytail: two-factor model assumes residuals are uncorrelated, so it misses co-movement that
// BTC/ETH don't explain (meme or sector clusters) and understates crowding there. Upgrade path:
// add residual pairwise correlation (shrunk) on top of the factor term, or sector factors.
public sealed class SymbolCrowdingCap
{
    // GRAVITY_CROWDING=<double>. 0 or unset → OFF. 1.0 charges the full variance inflation.
    // Values above 1 are allowed and simply charge harder; negative values are clamped to 0 so the
    // environment cannot flip this into a loosening device.
    public static double ConfiguredStrength =>
        double.TryParse(Environment.GetEnvironmentVariable("GRAVITY_CROWDING"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double v)
            ? Math.Max(0.0, v)
            : 0.0;

    public const string BtcAnchor = "BTCUSDT";
    public const string EthAnchor = "ETHUSDT";
    private static readonly DateTime Epoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // Daily log returns keyed by the day of the later close. A gap in the series yields no return
    // for that day rather than a multi-day move booked as one.
    private readonly Dictionary<string, Dictionary<DateTime, double>> _ret;
    private readonly int _windowDays, _minObs, _refreshDays;
    private readonly Dictionary<long, Snapshot> _snaps = new();

    private sealed record Loading(double B1, double B2, double Sigma);
    private sealed record Snapshot(Dictionary<string, Loading> L, double Fbb, double Fbe, double Fee);

    public double Strength   { get; }
    public int    Symbols    => _ret.Count;
    public int    WindowDays => _windowDays;
    public int    RefreshDays => _refreshDays;

    // Candidate trades whose symbol had fewer than minObs days of anchored history in the window
    // that applied to them. Each is charged at correlation 1.0, so missing history cannot dilute
    // the measured crowding of the book.
    public int UnknownSymbolHits { get; private set; }

    private SymbolCrowdingCap(Dictionary<string, Dictionary<DateTime, double>> ret, double strength,
                              int windowDays, int minObs, int refreshDays)
    {
        _ret = ret; Strength = strength;
        _windowDays = windowDays; _minObs = minObs; _refreshDays = refreshDays;
    }

    // How many directional slots n positions with mean pairwise correlation rho actually occupy.
    //   strength 0 → n                        (identity; the headcount rule, unchanged)
    //   strength 1 → n·(1 + (n−1)·rho)        (the full variance inflation)
    // Monotone non-decreasing in rho, n and strength, and never below n.
    internal static double EffectiveSlots(int n, double rho, double strength)
    {
        if (n <= 1 || strength <= 0.0) return n;
        double inflation = 1.0 + (n - 1) * Math.Max(0.0, rho);   // floored at 1: see the one-sided note
        return n * Math.Pow(inflation, strength);
    }

    // Returns null — cap inactive, and the caller says so — when strength is 0 or either anchor is
    // missing from `daily`. Estimation is lazy, per refresh period, on first use.
    public static SymbolCrowdingCap? Build(
        IReadOnlyList<SymbolCovariance.DailySeries> daily,
        double strength,
        int windowDays  = 180,
        int minObs      = 60,
        int refreshDays = 30)
    {
        if (strength <= 0.0) return null;

        var ret = new Dictionary<string, Dictionary<DateTime, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in daily)
        {
            var r = new Dictionary<DateTime, double>(s.Days.Length);
            for (int d = 1; d < s.Days.Length; d++)
                if (s.Days[d] - s.Days[d - 1] == TimeSpan.FromDays(1) && s.Closes[d - 1] > 0 && s.Closes[d] > 0)
                    r[s.Days[d]] = Math.Log(s.Closes[d] / s.Closes[d - 1]);
            ret[s.Symbol] = r;
        }
        if (!ret.ContainsKey(BtcAnchor) || !ret.ContainsKey(EthAnchor)) return null;

        return new SymbolCrowdingCap(ret, strength, windowDays, minObs, refreshDays);
    }

    // What every backtest call site needs, in one place: read the configured strength, build from
    // the fetched candles, and say plainly whether the cap ended up active. Returns null — and
    // prints nothing — when the feature is off, so an unset environment leaves the report
    // byte-identical to what it was before this existed.
    public static SymbolCrowdingCap? BuildForBook(
        IReadOnlyList<StrategyPipeline.FetchedCandles> fetched,
        IReadOnlyList<PortfolioReplay.Trade> book,
        string label)
    {
        double strength = ConfiguredStrength;
        if (strength <= 0.0 || book.Count == 0) return null;

        var daily = new List<SymbolCovariance.DailySeries>(fetched.Count);
        foreach (var f in fetched)
            if (SymbolCovariance.ToDaily(f.sym, f.h1) is { } d) daily.Add(d);

        var cap = Build(daily, strength);
        Console.WriteLine(cap != null
            ? $"  [{label}] crowding cap ON (GRAVITY_CROWDING={strength:F2}), {cap.Symbols} symbols, " +
              $"BTC+ETH-anchored correlation, trailing {cap.WindowDays}d re-estimated every {cap.RefreshDays}d"
            : $"  [{label}] crowding cap requested (GRAVITY_CROWDING={strength:F2}) but {BtcAnchor}/{EthAnchor} " +
              "are not in the fetched set — INACTIVE");
        return cap;
    }

    private Snapshot SnapshotAt(DateTime at)
    {
        long key = (long)Math.Floor((at - Epoch).TotalDays / _refreshDays);
        if (_snaps.TryGetValue(key, out var snap)) return snap;

        DateTime periodStart = Epoch.AddDays(key * _refreshDays);
        DateTime windowStart = periodStart.AddDays(-_windowDays);
        var btc = _ret[BtcAnchor];
        var eth = _ret[EthAnchor];

        // Factor covariance over the days both anchors traded, strictly before periodStart.
        var anchorDays = btc.Keys.Where(d => d >= windowStart && d < periodStart && eth.ContainsKey(d)).ToList();
        var (fbb, fbe, fee) = Cov2(anchorDays, btc, eth);

        var loadings = new Dictionary<string, Loading>(StringComparer.OrdinalIgnoreCase);
        if (anchorDays.Count >= _minObs)
        {
            foreach (var (sym, r) in _ret)
            {
                var days = anchorDays.Where(r.ContainsKey).ToList();
                if (days.Count < _minObs) continue;
                if (Regress(days, r, btc, eth) is { } l) loadings[sym] = l;
            }
        }

        snap = new Snapshot(loadings, fbb, fbe, fee);
        _snaps[key] = snap;
        return snap;
    }

    private static (double bb, double be, double ee) Cov2(
        List<DateTime> days, Dictionary<DateTime, double> x, Dictionary<DateTime, double> y)
    {
        int n = days.Count;
        if (n < 2) return (0, 0, 0);
        double mx = days.Average(d => x[d]), my = days.Average(d => y[d]);
        double sxx = 0, sxy = 0, syy = 0;
        foreach (var d in days)
        {
            double dx = x[d] - mx, dy = y[d] - my;
            sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
        }
        return (sxx / (n - 1), sxy / (n - 1), syy / (n - 1));
    }

    // OLS of r on [btc, eth] with intercept. Falls back to BTC alone when the anchors are
    // collinear over the window (a near-singular normal matrix would blow the loadings up).
    private static Loading? Regress(
        List<DateTime> days, Dictionary<DateTime, double> r,
        Dictionary<DateTime, double> btc, Dictionary<DateTime, double> eth)
    {
        var (sbb, sbe, see) = Cov2(days, btc, eth);
        var (_,   sby, syy) = Cov2(days, btc, r);
        var (_,   sey, _)   = Cov2(days, eth, r);
        if (syy <= 0 || sbb <= 0) return null;

        double det = sbb * see - sbe * sbe;
        double b1, b2;
        if (det > 1e-9 * sbb * see)
        {
            b1 = ( see * sby - sbe * sey) / det;
            b2 = (-sbe * sby + sbb * sey) / det;
        }
        else { b1 = sby / sbb; b2 = 0.0; }
        return new Loading(b1, b2, Math.Sqrt(syy));
    }

    // Implied correlation between two symbols at time `at`; 1.0 (worst case) whenever either has
    // too little history in the window that applies.
    private double Pair(Snapshot s, string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 1.0;
        if (!s.L.TryGetValue(a, out var la) || !s.L.TryGetValue(b, out var lb)) return 1.0;
        double cov = la.B1 * (s.Fbb * lb.B1 + s.Fbe * lb.B2) + la.B2 * (s.Fbe * lb.B1 + s.Fee * lb.B2);
        return Math.Clamp(cov / (la.Sigma * lb.Sigma), -1.0, 1.0);
    }

    // Mean pairwise correlation across the open set plus the candidate, as estimated for `at`.
    internal double MeanPairwise(IReadOnlyList<string> open, string candidate, DateTime at)
    {
        int n = open.Count + 1;
        if (n < 2) return 0.0;

        var s = SnapshotAt(at);
        double sum = 0; int cnt = 0;
        for (int a = 0; a < open.Count; a++)
        {
            for (int b = a + 1; b < open.Count; b++) { sum += Pair(s, open[a], open[b]); cnt++; }
            sum += Pair(s, open[a], candidate); cnt++;
        }
        return cnt > 0 ? sum / cnt : 0.0;
    }

    // True when admitting `candidate` at `at` alongside `open` would exceed the directional budget
    // once crowding is charged for. Never true when the plain headcount would already have passed
    // at strength 0 — see EffectiveSlots.
    public bool Exceeds(IReadOnlyList<string> open, string candidate, int cap, DateTime at)
    {
        if (Strength <= 0.0 || cap == int.MaxValue) return false;

        if (!string.IsNullOrEmpty(candidate) && !SnapshotAt(at).L.ContainsKey(candidate)) UnknownSymbolHits++;

        int n = open.Count + 1;
        if (n <= 1) return false;
        return EffectiveSlots(n, MeanPairwise(open, candidate, at), Strength) > cap;
    }
}
