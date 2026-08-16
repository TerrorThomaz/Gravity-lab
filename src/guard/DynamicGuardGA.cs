namespace TradingGA;

// (mu+lambda) ES: truncation selection, single parent, no crossover. Seed defaults to 42.
// No stagnation handling — a plateau spends the remaining budget creeping.
public class DynamicGuardGA
{
    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly Random _rng;
    private readonly string _seedLabel;

    // seed defaults to 42 for reproducibility.
    public DynamicGuardGA(int populationSize = 40, int generations = 60, Random? rng = null, int? seed = null)
    {
        _populationSize = populationSize;
        _generations    = generations;
        if (rng != null)
        {
            _rng       = rng;
            _seedLabel = "caller-supplied Random instance (seed not observable here)";
        }
        else
        {
            int s      = seed ?? DefaultSeed;
            _rng       = new Random(s);
            _seedLabel = seed is null ? $"{s} (default)" : $"{s} (supplied)";
        }
    }

    internal const int DefaultSeed = 42;

    // trainTrades MUST come from the train slice.
    public DynamicGuardGenotype Run(
        Candle[] btcH1,
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> trainTrades)
    {
        Console.WriteLine($"  [seed] DynamicGuardGA rng seed = {_seedLabel}");

        int nDim   = DynamicGuardGenotype.Bounds.GetLength(0);
        int elites = Math.Max(2, _populationSize / 5);

        var pop = new List<DynamicGuardGenotype>(_populationSize);
        for (int i = 0; i < _populationSize; i++)
        {
            var g = DynamicGuardGenotype.FromGenes(RandomGenes(nDim, _rng));
            pop.Add(g with { Fitness = Evaluate(g, btcH1, trainTrades) });
        }
        pop = [.. pop.OrderByDescending(g => g.Fitness)];

        for (int gen = 1; gen <= _generations; gen++)
        {
            var next = pop.Take(elites).ToList();
            while (next.Count < _populationSize)
            {
                var parent = pop[_rng.Next(Math.Min(10, pop.Count))];
                var genes  = parent.ToGenes();
                for (int d = 0; d < nDim; d++)
                    genes[d] = MutateGene(d, genes[d], _rng);
                var child = DynamicGuardGenotype.FromGenes(genes);
                next.Add(child with { Fitness = Evaluate(child, btcH1, trainTrades) });
            }
            pop = [.. next.OrderByDescending(g => g.Fitness)];
            if (gen % 10 == 0)
                Console.WriteLine($"  DynamicGuardGA Gen {gen,3} — elite: {pop[0]}");
        }

        Console.WriteLine("\n─── Bayesian refinement (30 TPE iterations) ───");
        var seedObs = pop.Take(10).Select(g => (g.ToGenes(), g.Fitness));
        var history = BayesianOptimizer.Refine(seedObs, DynamicGuardGenotype.Bounds,
            genes => Evaluate(DynamicGuardGenotype.FromGenes(genes), btcH1, trainTrades),
            30, _rng);
        var top  = history.OrderByDescending(h => h.Fitness).First();
        var best = DynamicGuardGenotype.FromGenes(top.Params, top.Fitness);
        Console.WriteLine($"  TPE best: {best}");
        return best;
    }

    // CVaR-weighted segment scoring. lambda=0.6 (bad segments carry most weight).
    // A single aggregate Calmar produced an inert guard (GA learned to switch it off).
    internal const int    FitnessSegments = 10;
    internal const double FitnessLambda   = 0.6;
    internal const double FitnessCVaRAlpha = 0.4;

    internal static double Evaluate(
        DynamicGuardGenotype g,
        Candle[] btcH1,
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> trainTrades)
    {
        var session = new DynamicGuardSession(btcH1, g);
        var capped  = ApplyCap(trainTrades).OrderBy(t => t.EntryTime).ToList();
        if (capped.Count == 0) return -1000.0;

        int per = Math.Max(1, capped.Count / FitnessSegments);
        var seg = new List<double>(FitnessSegments);

        for (int start = 0; start < capped.Count; start += per)
        {
            var slice = capped.GetRange(start, Math.Min(per, capped.Count - start));
            if (slice.Count < 10) continue;

            // DdEntryGatePct is a DD fraction — pass straight through.
            var r = Simulator.SimulatePortfolioExposureCapped(
                ApplyGuard(slice, session), Config.MaxTotalExposurePct, maxPositionFrac: 0.05,
                ddLongEntryGatePct: g.DdEntryGatePct, confLossCapMin: g.ConfLossCapMin,
                confLossCapMax: g.ConfLossCapMax, profitProtectThreshold: g.ProfitProtectThreshold,
                profitProtectDrawback: g.ProfitProtectDrawback, profitProtectFactor: g.ProfitProtectFactor);

            seg.Add((r.EndBalance - 100.0) / Math.Max(r.MaxDrawdownPct, 0.5));
        }

        if (seg.Count == 0) return -1000.0;

        seg.Sort();
        int tail = Math.Max(1, (int)Math.Ceiling(FitnessCVaRAlpha * seg.Count));
        double cvar = seg.Take(tail).Average();
        return FitnessLambda * cvar + (1.0 - FitnessLambda) * seg.Average();
    }

    // Concurrent position cap — identical to fulltest.
    internal static List<PortfolioReplay.Trade> ApplyCap(
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> trades) =>
        PortfolioReplay.FilterByConcurrentCap(
            trades.Select(t => new PortfolioReplay.Trade(t.Strategy, t.Time, t.Hold, t.Return, t.Conf)));

    // ATR entry gate + confidence scaling. Strategy preserved for simulator-level gates.
    internal static List<(DateTime, double, double, TimeSpan, string)> ApplyGuard(
        IEnumerable<PortfolioReplay.Trade> trades,
        DynamicGuardSession session) =>
        trades
        .Where(t => !session.IsEntryBlocked(t.EntryTime, t.Strategy))
        .Select(t => (
            t.EntryTime, t.Return,
            DynamicGuardSession.IsGuarded(t.Strategy) ? t.Conf * session.GetMult(t.EntryTime, t.Strategy) : t.Conf,
            t.HoldDuration, t.Strategy))
        .OrderBy(t => t.Item1).ToList();

    // Per-gene mutation. DD-gate switch gets an explicit Bernoulli toggle (binary gene).
    internal const double DdGateFlipRate = 0.15;

    internal static double MutateGene(int d, double value, Random rng)
    {
        if (d == DynamicGuardGenotype.DdGateEnableGeneIndex)
            return rng.NextDouble() < DdGateFlipRate
                ? (value >= DynamicGuardGenotype.DdGateEnableThreshold
                    ? DynamicGuardGenotype.DdGateDisabledGene
                    : DynamicGuardGenotype.DdGateEnabledGene)
                : value;

        if (rng.NextDouble() >= 0.7) return value;
        double lo = DynamicGuardGenotype.Bounds[d, 0], hi = DynamicGuardGenotype.Bounds[d, 1];
        return Math.Clamp(value + rng.NextGaussian() * (hi - lo) * 0.1, lo, hi);
    }

    // Uniform draw inside Bounds. Internal so tests assert on the real initialiser.
    internal static double[] RandomGenes(int nDim, Random rng)
    {
        var g = new double[nDim];
        for (int d = 0; d < nDim; d++)
            g[d] = DynamicGuardGenotype.Bounds[d, 0]
                 + rng.NextDouble() * (DynamicGuardGenotype.Bounds[d, 1] - DynamicGuardGenotype.Bounds[d, 0]);
        return g;
    }
}
