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

    private readonly Dictionary<string, int> _index;
    private readonly double[] _corr;
    private readonly int _k;

    public double Strength { get; }
    public int    Symbols  => _k;

    // Candidate symbols that were not in the estimation window. Each is treated as correlation 1.0
    // with everything, so a symbol with no history cannot dilute the measured crowding of the book.
    public int UnknownSymbolHits { get; private set; }

    private SymbolCrowdingCap(Dictionary<string, int> index, double[] corr, int k, double strength)
    {
        _index = index; _corr = corr; _k = k; Strength = strength;
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

    // Build from daily candle history. `asOf` truncates the estimation window to days STRICTLY
    // BEFORE that instant, which is what keeps this out of the lookahead category: a cap fitted on
    // the same window it filters would be choosing which clusters to avoid with knowledge of how
    // they turned out. Callers pass the first trade's entry time. Returns null — cap inactive, and
    // the caller says so — when strength is 0 or too little prior history survives.
    public static SymbolCrowdingCap? Build(
        IReadOnlyList<SymbolCovariance.DailySeries> daily,
        double strength,
        DateTime? asOf = null,
        int minWindowDays = 365)
    {
        if (strength <= 0.0 || daily.Count < 2) return null;

        var usable = asOf is { } cut ? Truncate(daily, cut) : daily.ToList();
        if (usable.Count < 2) return null;

        var window = SymbolCovariance.SelectWindow(usable, minWindowDays);
        if (window == null) return null;

        var rm = SymbolCovariance.BuildReturns(usable, window);
        if (rm == null || rm.Symbols.Length < 2) return null;

        int k = rm.Symbols.Length;
        var shrunk = CovarianceMatrix.ShrinkAuto(rm.Returns);
        if (shrunk == null) return null;
        var corr = CovarianceMatrix.Correlations(shrunk.Value.Cov, k);
        if (corr == null) return null;

        var index = new Dictionary<string, int>(k, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < k; i++) index[rm.Symbols[i]] = i;

        return new SymbolCrowdingCap(index, corr, k, strength);
    }

    private static List<SymbolCovariance.DailySeries> Truncate(
        IReadOnlyList<SymbolCovariance.DailySeries> daily, DateTime cut)
    {
        var outp = new List<SymbolCovariance.DailySeries>(daily.Count);
        DateTime day = cut.Date;
        foreach (var s in daily)
        {
            int n = 0;
            while (n < s.Days.Length && s.Days[n] < day) n++;
            if (n < 2) continue;
            outp.Add(new SymbolCovariance.DailySeries(s.Symbol, s.Days[..n], s.Closes[..n]));
        }
        return outp;
    }

    // Correlation between two symbols; 1.0 (worst case) whenever either is unknown to the matrix.
    private double Pair(string a, string b)
    {
        if (!_index.TryGetValue(a, out int i) || !_index.TryGetValue(b, out int j)) return 1.0;
        return _corr[i * _k + j];
    }

    // Mean pairwise correlation across the open set plus the candidate.
    internal double MeanPairwise(IReadOnlyList<string> open, string candidate)
    {
        int n = open.Count + 1;
        if (n < 2) return 0.0;

        double sum = 0; int cnt = 0;
        for (int a = 0; a < open.Count; a++)
        {
            for (int b = a + 1; b < open.Count; b++) { sum += Pair(open[a], open[b]); cnt++; }
            sum += Pair(open[a], candidate); cnt++;
        }
        return cnt > 0 ? sum / cnt : 0.0;
    }

    // True when admitting `candidate` alongside `open` would exceed the directional budget once
    // crowding is charged for. Never true when the plain headcount would already have passed at
    // strength 0 — see EffectiveSlots.
    public bool Exceeds(IReadOnlyList<string> open, string candidate, int cap)
    {
        if (Strength <= 0.0 || cap == int.MaxValue) return false;

        if (!string.IsNullOrEmpty(candidate) && !_index.ContainsKey(candidate)) UnknownSymbolHits++;

        int n = open.Count + 1;
        if (n <= 1) return false;
        return EffectiveSlots(n, MeanPairwise(open, candidate), Strength) > cap;
    }
}
