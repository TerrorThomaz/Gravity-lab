namespace TradingGA;

// Symbol-level co-movement: the pieces needed to turn ~190 per-coin candle arrays into one aligned
// return matrix that CovarianceMatrix can consume, plus deterministic family clustering on the
// resulting correlations.
//
// WHY THIS IS NOT CovarianceSizing: that class does pairwise Pearson correlations between STRATEGY
// P&L series and never forms a joint matrix. CovarianceMatrix does form one, but every call site
// passes k ≈ 8 strategies. Nothing in the repo has ever looked at how the ~190 SYMBOLS co-move,
// which is the quantity that decides how many independent bets the book actually carries — and
// therefore how much a "190 never-seen coins" result is really worth.
//
// DAILY, NOT HOURLY. Returns are bucketed to one observation per UTC day. Two reasons: hourly
// crypto returns carry enough microstructure noise to bias correlations downward, and the
// Ledoit-Wolf intensity is O(n·k²) — at n ≈ 28,000 hourly bars and k ≈ 190 that is ~10⁹ operations,
// against ~4·10⁷ for ~1,170 daily ones. CovarianceSizing already buckets to TimeSpan.FromDays(1)
// for the strategy matrix, so this also keeps the two consistent.
//
// NO LOOKAHEAD CLAIM IS MADE OR NEEDED: this is a descriptive diagnostic over a fixed historical
// window. It does not feed sizing, routing or any simulator, and nothing here is fit to forward
// returns. If any of it is later wired into a trading path, the window selection below becomes a
// lookahead surface and must be made expanding.
public static class SymbolCovariance
{
    // A symbol reduced to one close per UTC day.
    public sealed record DailySeries(string Symbol, DateTime[] Days, double[] Closes)
    {
        public DateTime First => Days[0];
        public DateTime Last  => Days[^1];
    }

    // Last close of each UTC day. h1 candles are assumed ascending in time, which every
    // CandleFetcher path guarantees; out-of-order input would merely mis-attribute a close.
    public static DailySeries? ToDaily(string symbol, IReadOnlyList<Candle> h1)
    {
        if (h1.Count == 0) return null;

        var days   = new List<DateTime>();
        var closes = new List<double>();
        DateTime cur = default;
        double   last = 0;
        bool     open = false;

        foreach (var c in h1)
        {
            var day = c.Time.Date;
            if (!open) { cur = day; open = true; }
            else if (day != cur)
            {
                days.Add(cur); closes.Add(last);
                cur = day;
            }
            last = c.Close;
        }
        if (open) { days.Add(cur); closes.Add(last); }

        return days.Count == 0 ? null : new DailySeries(symbol, days.ToArray(), closes.ToArray());
    }

    // The window every kept symbol must span, and the symbols that span it.
    public sealed record Window(DateTime Start, DateTime End, string[] Kept, string[] Dropped)
    {
        public int Days => (int)(End - Start).TotalDays + 1;
    }

    // Choosing the window is a rectangle problem: an early start keeps more history but drops every
    // symbol listed after it, and a late start keeps more symbols over less history. Sample()
    // requires equal-length series, so one of the two has to give.
    //
    // Rule: drop symbols whose data ends more than staleDays before the newest end (a delisted or
    // stalled symbol must not drag the whole window backwards), then pick the start that maximises
    // keptSymbols × windowDays subject to a floor on windowDays. Deterministic, and the report
    // prints what it discarded so the choice is auditable rather than implicit.
    public static Window? SelectWindow(
        IReadOnlyList<DailySeries> series, int minWindowDays = 365, int staleDays = 30)
    {
        if (series.Count == 0) return null;

        DateTime newestEnd = series.Max(s => s.Last);
        var fresh   = series.Where(s => (newestEnd - s.Last).TotalDays <= staleDays).ToList();
        var stale   = series.Where(s => (newestEnd - s.Last).TotalDays >  staleDays)
                            .Select(s => s.Symbol).ToList();
        if (fresh.Count == 0) return null;

        DateTime end = fresh.Min(s => s.Last);
        var byStart  = fresh.OrderBy(s => s.First).ToList();

        int bestIdx = -1;
        long bestArea = 0;
        for (int i = 0; i < byStart.Count; i++)
        {
            DateTime start = byStart[i].First;
            int windowDays = (int)(end - start).TotalDays + 1;
            if (windowDays < minWindowDays) break;      // later starts are only shorter
            long area = (long)(i + 1) * windowDays;
            if (area > bestArea) { bestArea = area; bestIdx = i; }
        }
        if (bestIdx < 0) return null;

        DateTime chosen = byStart[bestIdx].First;
        var kept    = byStart.Take(bestIdx + 1).Select(s => s.Symbol).ToArray();
        var dropped = byStart.Skip(bestIdx + 1).Select(s => s.Symbol).Concat(stale).ToArray();
        return new Window(chosen, end, kept, dropped);
    }

    public sealed record ReturnMatrix(string[] Symbols, double[][] Returns, DateTime[] Days, int FilledGaps);

    // Aligned simple daily returns over the window, one row per symbol. A missing day is
    // forward-filled from the previous close, which books a 0% return for the gap rather than
    // inventing a move; the count is reported so a matrix built mostly out of fill can be spotted.
    public static ReturnMatrix? BuildReturns(IReadOnlyList<DailySeries> series, Window window)
    {
        var keep = new HashSet<string>(window.Kept);
        var used = series.Where(s => keep.Contains(s.Symbol)).OrderBy(s => s.Symbol).ToList();
        if (used.Count == 0) return null;

        int nDays = window.Days;
        if (nDays < 2) return null;

        var days = new DateTime[nDays];
        for (int d = 0; d < nDays; d++) days[d] = window.Start.AddDays(d);

        var rows = new List<double[]>();
        var syms = new List<string>();
        int gaps = 0;

        foreach (var s in used)
        {
            var byDay = new Dictionary<DateTime, double>(s.Days.Length);
            for (int i = 0; i < s.Days.Length; i++) byDay[s.Days[i]] = s.Closes[i];

            var closes = new double[nDays];
            double prev = double.NaN;
            bool usable = true;
            for (int d = 0; d < nDays; d++)
            {
                if (byDay.TryGetValue(days[d], out double c) && c > 0) prev = c;
                else if (!double.IsNaN(prev)) gaps++;
                if (double.IsNaN(prev)) { usable = false; break; }   // no close at or before window start
                closes[d] = prev;
            }
            if (!usable) continue;

            var r = new double[nDays - 1];
            for (int d = 1; d < nDays; d++)
                r[d - 1] = closes[d - 1] > 1e-12 ? closes[d] / closes[d - 1] - 1.0 : 0.0;

            rows.Add(r);
            syms.Add(s.Symbol);
        }

        return rows.Count == 0 ? null : new ReturnMatrix(syms.ToArray(), rows.ToArray(), days, gaps);
    }

    // Mean of the off-diagonal correlations. This is the headline statistic for the stress split:
    // it is a pairwise quantity, so each entry is estimated from n observations independently and
    // it stays meaningful when n < k, where the full-matrix statistics do not.
    public static double MeanOffDiagonal(double[] corr, int k)
    {
        if (k < 2) return 0.0;
        double sum = 0; int cnt = 0;
        for (int a = 0; a < k; a++)
            for (int b = a + 1; b < k; b++) { sum += corr[a * k + b]; cnt++; }
        return cnt > 0 ? sum / cnt : 0.0;
    }

    // Average-linkage agglomerative clustering on distance d = 1 − ρ, merging while the closest pair
    // of clusters is nearer than maxDistance. Deterministic: no seeds, no random restarts, ties
    // broken by index — a k-means on eigenvector loadings would be none of those, and this repo has
    // reproducibility tests it would break.
    // Returns a label per symbol, labels renumbered 0..m−1 in order of first appearance.
    public static int[] Families(double[] corr, int k, double maxDistance = 0.40)
    {
        var label = new int[k];
        for (int i = 0; i < k; i++) label[i] = i;
        if (k < 2) return label;

        // members[c] = indices currently in cluster c; null once merged away.
        var members = new List<int>?[k];
        for (int i = 0; i < k; i++) members[i] = new List<int> { i };

        while (true)
        {
            double best = double.MaxValue;
            int bestA = -1, bestB = -1;

            for (int a = 0; a < k; a++)
            {
                if (members[a] is not { } ma) continue;
                for (int b = a + 1; b < k; b++)
                {
                    if (members[b] is not { } mb) continue;
                    double sum = 0;
                    foreach (int i in ma)
                        foreach (int j in mb) sum += 1.0 - corr[i * k + j];
                    double d = sum / (ma.Count * mb.Count);
                    if (d < best) { best = d; bestA = a; bestB = b; }
                }
            }

            if (bestA < 0 || best > maxDistance) break;
            members[bestA]!.AddRange(members[bestB]!);
            members[bestB] = null;
        }

        int next = 0;
        var renumber = new Dictionary<int, int>();
        for (int c = 0; c < k; c++)
        {
            if (members[c] is not { } m) continue;
            foreach (int i in m)
            {
                if (!renumber.TryGetValue(c, out int lbl)) { lbl = next++; renumber[c] = lbl; }
                label[i] = lbl;
            }
        }
        return label;
    }
}
