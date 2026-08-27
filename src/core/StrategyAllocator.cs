namespace TradingGA;

// Covariance-aware RISK SCALE allocator. Produces per-strategy multipliers (mean-normalised to 1.0)
// derived from the joint covariance of strategy returns, with SIMFAM-style family clustering so
// correlated strategies don't stack. Composes multiplicatively with router weights and the drawdown
// guard: finalSize = base x routerWeight x riskScale x guardMult.
//
// Pipeline:
//   1. Bucket each strategy's returns onto a common time grid (same approach as CovarianceSizing.ToGrid).
//   2. Joint covariance -> Ledoit-Wolf shrink -> correlation matrix.
//   3. Single-linkage clustering on correlation (rho >= threshold -> same family, transitive closure).
//   4. Base risk scale = ERC weights on shrunk covariance (inverse-vol fallback if degenerate).
//   5. Family cap: if a family's total scale > familyCapMult x familySize x meanScale, scale down.
//   6. Correlation-load haircut: divide by sqrt(1 + sum of positive pairwise rho).
//   7. Final mean-normalisation to exactly 1.0 (redistributes size, no gross exposure change).
public static class StrategyAllocator
{
    public const int MinTrades = 20;

    public record Allocation(
        IReadOnlyDictionary<string, double> RiskScale,
        IReadOnlyDictionary<string, string> FamilyOf,
        IReadOnlyDictionary<string, double> Volatility,
        double EffectiveBets,
        double AvgPairwiseCorrelation);

    public static Allocation Compute(
        IReadOnlyDictionary<string, IReadOnlyList<(DateTime Time, double Return)>> tradesByStrategy,
        TimeSpan? bucket = null,
        double shrinkLambda = 0.3,
        double familyCorrThreshold = 0.7,
        double familyCapMult = 1.5)
    {
        var bucketSize = bucket ?? TimeSpan.FromHours(24);
        var allNames = tradesByStrategy.Keys.OrderBy(k => k).ToArray();

        // Strategies with too few trades: neutral 1.0, own family, excluded from covariance math.
        var measured = new List<string>();
        var unmeasured = new HashSet<string>();
        foreach (var name in allNames)
        {
            if (tradesByStrategy[name].Count >= MinTrades) measured.Add(name);
            else unmeasured.Add(name);
        }

        var riskScale = new Dictionary<string, double>();
        var familyOf = new Dictionary<string, string>();
        var volatility = new Dictionary<string, double>();

        // Unmeasured: neutral.
        int famCounter = 0;
        foreach (var name in allNames)
        {
            if (unmeasured.Contains(name))
            {
                riskScale[name] = 1.0;
                familyOf[name] = $"F{famCounter++}";
                volatility[name] = 0.0;
            }
        }

        if (measured.Count == 0)
            return new Allocation(riskScale, familyOf, volatility, 0.0, 0.0);

        // Find common time span across all measured strategies.
        DateTime? gStart = null, gEnd = null;
        foreach (var name in measured)
        {
            var trades = tradesByStrategy[name];
            if (trades.Count == 0) continue;
            var tMin = trades.Min(t => t.Time);
            var tMax = trades.Max(t => t.Time);
            if (gStart == null || tMin < gStart) gStart = tMin;
            if (gEnd == null || tMax > gEnd) gEnd = tMax;
        }
        if (gStart == null || gEnd == null || gEnd <= gStart)
        {
            // Degenerate time span: all measured get 1.0, own family.
            foreach (var name in measured)
            {
                riskScale[name] = 1.0;
                familyOf[name] = $"F{famCounter++}";
                volatility[name] = 0.0;
            }
            return new Allocation(riskScale, familyOf, volatility, 0.0, 0.0);
        }

        // Bucket each measured strategy onto the common grid.
        var start = gStart.Value;
        var end = gEnd.Value;
        int gridLen = Math.Max(1, (int)((end - start).TotalSeconds / bucketSize.TotalSeconds) + 1);
        var series = new Dictionary<string, double[]>();
        foreach (var name in measured)
        {
            var trades = tradesByStrategy[name];
            var g = new double[gridLen];
            foreach (var t in trades)
            {
                int i = (int)((t.Time - start).TotalSeconds / bucketSize.TotalSeconds);
                if ((uint)i < (uint)gridLen) g[i] += t.Return;
            }
            series[name] = g;
        }

        int k = measured.Count;
        var orderedNames = measured.ToArray();  // already sorted
        var matrix = new double[k][];
        for (int i = 0; i < k; i++) matrix[i] = series[orderedNames[i]];

        // Joint covariance -> shrink -> correlation.
        var cov = CovarianceMatrix.Sample(matrix);
        double[]? shrunk = cov != null ? CovarianceMatrix.Shrink(cov, k, shrinkLambda) : null;

        // Volatility per strategy (from shrunk diagonal).
        for (int i = 0; i < k; i++)
        {
            double v = shrunk != null ? Math.Sqrt(Math.Max(0, shrunk[i * k + i])) : 0.0;
            volatility[orderedNames[i]] = v;
        }

        // Base risk scale: ERC on shrunk covariance, inverse-vol fallback.
        double[] baseScale;
        if (shrunk != null)
        {
            try
            {
                var erc = RiskParity.EqualRiskContribution(shrunk, k, normalizeToMeanOne: false);
                if (erc.Weights.All(w => w > 1e-12) && erc.PortfolioVol > 1e-12)
                    baseScale = erc.Weights;
                else
                    baseScale = RiskParity.InverseVol(shrunk, k);
            }
            catch
            {
                baseScale = RiskParity.InverseVol(shrunk, k);
            }
        }
        else
        {
            baseScale = Enumerable.Repeat(1.0, k).ToArray();
        }

        // Correlation matrix for clustering + haircut.
        double[]? corr = shrunk != null ? CovarianceMatrix.Correlations(shrunk, k) : null;

        // Single-linkage clustering: two strategies join a family when rho >= threshold (transitive).
        // Union-Find over measured strategies.
        var parent = new int[k];
        for (int i = 0; i < k; i++) parent[i] = i;
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }

        if (corr != null)
        {
            for (int a = 0; a < k; a++)
                for (int b = a + 1; b < k; b++)
                    if (corr[a * k + b] >= familyCorrThreshold)
                        Union(a, b);
        }

        // Assign family ids by first appearance in sorted order.
        var familyMap = new Dictionary<int, string>();
        var strategyFamilyIdx = new int[k];
        for (int i = 0; i < k; i++)
        {
            int root = Find(i);
            if (!familyMap.TryGetValue(root, out var fid))
            {
                fid = $"F{famCounter++}";
                familyMap[root] = fid;
            }
            strategyFamilyIdx[i] = root;
            familyOf[orderedNames[i]] = fid;
        }

        // Family cap: for each family, if sum of scales > familyCapMult x familySize x meanScale, scale down.
        double meanScale = baseScale.Average();
        var familyGroups = new Dictionary<int, List<int>>();
        for (int i = 0; i < k; i++)
        {
            int root = strategyFamilyIdx[i];
            if (!familyGroups.ContainsKey(root)) familyGroups[root] = new List<int>();
            familyGroups[root].Add(i);
        }

        var scaled = (double[])baseScale.Clone();
        foreach (var (_, members) in familyGroups)
        {
            double familySum = members.Sum(i => scaled[i]);
            double cap = familyCapMult * members.Count * meanScale;
            if (familySum > cap && familySum > 1e-12)
            {
                double factor = cap / familySum;
                foreach (var i in members) scaled[i] *= factor;
            }
        }

        // Correlation-load haircut: divide by sqrt(1 + sum of positive pairwise rho).
        if (corr != null)
        {
            for (int a = 0; a < k; a++)
            {
                double posSum = 0;
                for (int b = 0; b < k; b++)
                {
                    if (a == b) continue;
                    double rho = corr[a * k + b];
                    if (rho > 0) posSum += rho;
                }
                scaled[a] /= Math.Sqrt(1.0 + posSum);
            }
        }

        // Final mean-normalisation to exactly 1.0.
        double mean = scaled.Average();
        if (mean > 1e-12)
        {
            for (int i = 0; i < k; i++) scaled[i] /= mean;
        }
        else
        {
            for (int i = 0; i < k; i++) scaled[i] = 1.0;
        }

        for (int i = 0; i < k; i++) riskScale[orderedNames[i]] = scaled[i];

        // Effective bets + avg pairwise correlation (over measured strategies only).
        double effBets = shrunk != null ? CovarianceMatrix.EffectiveBets(shrunk, k) : 0.0;
        double avgCorr = 0.0;
        if (corr != null && k >= 2)
        {
            double sum = 0; int n = 0;
            for (int a = 0; a < k; a++)
                for (int b = a + 1; b < k; b++)
                { sum += corr[a * k + b]; n++; }
            avgCorr = n > 0 ? sum / n : 0.0;
        }

        return new Allocation(riskScale, familyOf, volatility, effBets, avgCorr);
    }

    public static void Print(Allocation alloc)
    {
        Console.WriteLine($"\n-- Strategy risk scale (covariance-aware, SIMFAM families) ----------");
        Console.WriteLine($"  effective bets: {alloc.EffectiveBets:F2}   avg pairwise correlation: {alloc.AvgPairwiseCorrelation:F3}");
        Console.WriteLine($"  {"strategy",-14}  {"vol",8}  {"family",6}  {"riskScale",10}");
        Console.WriteLine($"  {new string('-', 44)}");
        foreach (var kv in alloc.RiskScale.OrderByDescending(k => k.Value))
        {
            var name = kv.Key;
            var vol = alloc.Volatility.TryGetValue(name, out var v) ? v : 0.0;
            var fam = alloc.FamilyOf.TryGetValue(name, out var f) ? f : "?";
            Console.WriteLine($"  {name,-14}  {vol,8:F3}  {fam,6}  {kv.Value,10:F3}");
        }
    }
}
