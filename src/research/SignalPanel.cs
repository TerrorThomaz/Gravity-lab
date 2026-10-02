namespace TradingGA;

// Pooled (coin × h1 bar) panel of signal features and forward labels. Column-major so the per-
// feature statistics stream one array at a time. Memory is ~4 bytes × (19 features + 6 labels) per
// row; sample with `stride` (keep every stride-th bar) — adjacent bars share nearly all of their
// forward window anyway, so a stride of 2-4 loses almost no independent information.
public sealed class SignalPanel
{
    public readonly List<float>[] X = Enumerable.Range(0, SignalFeatures.Count).Select(_ => new List<float>()).ToArray();
    public readonly List<float>[] Fwd = SignalFeatures.Horizons.Select(_ => new List<float>()).ToArray();
    public readonly List<sbyte> TbLabel = new();
    public readonly List<float> TbRet   = new();
    public readonly List<float> CostAtr = new();   // round-trip cost in ATR units at this row
    public readonly List<int>   Day     = new();   // days since epoch — the time cluster
    public readonly List<int>   Bar     = new();   // bar index within its coin (non-overlap sampling)
    public readonly List<byte>  Regime  = new();   // BTC MarketRegime at t (byte)RegimeUnknown if unaligned
    public readonly List<short> Coin    = new();
    public readonly List<string> CoinNames = new();

    public const byte RegimeUnknown = 255;
    public int Rows => Day.Count;

    public readonly VarianceRatioAccumulator VarianceRatios = new();

    public static int DayOf(DateTime t) => (int)(t.Ticks / TimeSpan.TicksPerDay);

    // Adds one coin. Returns the full (unstrided) feature matrix so a caller can look up arbitrary
    // bars (strategy attribution) without recomputing.
    public float[][] AddCoin(string sym, Candle[] h1, FundingRateSession? funding,
                             SignalFeatures.BtcContext? btc, int stride = 1)
    {
        var F = SignalFeatures.Compute(h1, funding, btc);
        int n = h1.Length;
        if (n <= SignalFeatures.Warmup + SignalFeatures.Horizons.Max() + 1) return F;

        var c   = CandleExt.Closes(h1);
        var atr = Volatility.Atr(CandleExt.Highs(h1), CandleExt.Lows(h1), c, 14);
        short coinIdx = (short)CoinNames.Count;
        CoinNames.Add(sym);

        // Regime per bar from the BTC context (variance ratios and the panel share it).
        var regime = new byte[n];
        for (int t = 0; t < n; t++)
            regime[t] = btc != null && btc.TryGet(h1[t].Time, out var b) ? (byte)b.Regime : RegimeUnknown;
        VarianceRatios.AddCoin(c, regime, SignalFeatures.Warmup);

        int last = n - 1 - SignalFeatures.Horizons.Max();   // every row has every label
        for (int t = SignalFeatures.Warmup; t <= last; t += Math.Max(1, stride))
        {
            for (int k = 0; k < SignalFeatures.Count; k++) X[k].Add(F[k][t]);
            for (int h = 0; h < SignalFeatures.Horizons.Length; h++)
                Fwd[h].Add((float)SignalFeatures.ForwardReturn(h1, atr, t, SignalFeatures.Horizons[h]));
            var (lab, tr) = SignalFeatures.TripleBarrier(h1, atr, t);
            TbLabel.Add(lab); TbRet.Add((float)tr);
            double atrPct = TradeCosts.AtrPct(atr[t], c[t]);
            CostAtr.Add((float)(atrPct > 1e-9 ? TradeCosts.RoundTripPct(atrPct, false, 0.0) / atrPct : 0.0));
            Day.Add(DayOf(h1[t].Time)); Bar.Add(t); Regime.Add(regime[t]); Coin.Add(coinIdx);
        }
        return F;
    }

    // Row mask helper: all rows, or rows in one BTC regime.
    public bool[] Mask(byte? regime = null)
    {
        var m = new bool[Rows];
        for (int i = 0; i < Rows; i++) m[i] = regime is null || Regime[i] == regime;
        return m;
    }
}

// Lo-MacKinlay variance ratio VR(q) = Var(q-bar log return) / (q · Var(1-bar log return)).
// VR < 1: returns REVERSE at that horizon (what fades and grids are paid by).
// VR > 1: returns CONTINUE (what pullback-continuation strategies are paid by).
// Conditioned on the BTC regime at the START of the q-bar window. Pooled across coins and also per
// coin (median, % of coins below 1) — the pooled figure is dominated by long, volatile coins and its
// naive z treats correlated coins as independent, so the per-coin spread is the honest companion.
public sealed class VarianceRatioAccumulator
{
    public static readonly int[] Qs = [4, 24, 72];
    public const int MinStartsPerCoin = 200;

    // key: (regime byte, q) → pooled sums; regime 254 = all regimes
    public const byte AllRegimes = 254;
    private readonly Dictionary<(byte, int), (double SumQ, long NQ, double Sum1, long N1)> _pooled = new();
    private readonly Dictionary<(byte, int), List<double>> _perCoin = new();

    public void AddCoin(double[] closes, byte[] regime, int start)
    {
        int n = closes.Length;
        if (n - start < 500) return;
        var r = new double[n];
        for (int t = start + 1; t < n; t++) r[t] = Math.Log(closes[t] / closes[t - 1]);
        double mu = 0; int cnt = 0;
        for (int t = start + 1; t < n; t++) { mu += r[t]; cnt++; }
        mu /= Math.Max(1, cnt);

        foreach (byte reg in regime.Distinct().Where(x => x != SignalPanel.RegimeUnknown).Append(AllRegimes))
        foreach (int q in Qs)
        {
            double sq = 0, s1 = 0; long nq = 0, n1 = 0;
            // window (t, t+q]: starts at the close of bar t, whose regime is known at that close
            for (int t = start; t + q < n; t++)
            {
                if (reg != AllRegimes && regime[t] != reg) continue;
                double rq = Math.Log(closes[t + q] / closes[t]) - q * mu;
                sq += rq * rq; nq++;
                double r1 = r[t + 1] - mu;
                s1 += r1 * r1; n1++;
            }
            if (nq == 0) continue;
            var key = (reg, q);
            var p = _pooled.GetValueOrDefault(key);
            _pooled[key] = (p.SumQ + sq, p.NQ + nq, p.Sum1 + s1, p.N1 + n1);
            if (nq >= MinStartsPerCoin && s1 > 0)
            {
                if (!_perCoin.TryGetValue(key, out var l)) _perCoin[key] = l = new();
                l.Add((sq / nq) / (q * s1 / n1));
            }
        }
    }

    public record Result(byte Regime, int Q, double PooledVr, long Starts, double MedianCoinVr, double FracCoinsBelow1, int Coins);

    public IEnumerable<Result> Results()
    {
        foreach (var ((reg, q), p) in _pooled.OrderBy(k => k.Key.Item1).ThenBy(k => k.Key.Item2))
        {
            if (p.NQ == 0 || p.Sum1 <= 0) continue;
            double vr = (p.SumQ / p.NQ) / (q * p.Sum1 / p.N1);
            var coins = _perCoin.GetValueOrDefault((reg, q)) ?? new List<double>();
            double med = coins.Count > 0 ? coins.OrderBy(x => x).ElementAt(coins.Count / 2) : double.NaN;
            double below = coins.Count > 0 ? coins.Count(x => x < 1.0) / (double)coins.Count : double.NaN;
            yield return new Result(reg, q, vr, p.NQ, med, below, coins.Count);
        }
    }
}
