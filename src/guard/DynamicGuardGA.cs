namespace TradingGA;

public class DynamicGuardGA
{
    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly Random _rng;

    public DynamicGuardGA(int populationSize = 40, int generations = 60, Random? rng = null)
    {
        _populationSize = populationSize;
        _generations    = generations;
        _rng            = rng ?? new Random(42);
    }

    public DynamicGuardGenotype Run(
        Candle[] btcH1,
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> valTrades,
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> oosTrades)
    {
        int nDim   = DynamicGuardGenotype.Bounds.GetLength(0);
        int elites = Math.Max(2, _populationSize / 5);

        var pop = new List<DynamicGuardGenotype>(_populationSize);
        for (int i = 0; i < _populationSize; i++)
        {
            var g = DynamicGuardGenotype.FromGenes(RandomGenes(nDim));
            pop.Add(g with { Fitness = Evaluate(g, btcH1, valTrades, oosTrades) });
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
                {
                    if (_rng.NextDouble() < 0.7)
                    {
                        double range = DynamicGuardGenotype.Bounds[d, 1] - DynamicGuardGenotype.Bounds[d, 0];
                        genes[d] = Math.Clamp(
                            genes[d] + _rng.NextGaussian() * range * 0.1,
                            DynamicGuardGenotype.Bounds[d, 0], DynamicGuardGenotype.Bounds[d, 1]);
                    }
                }
                var child = DynamicGuardGenotype.FromGenes(genes);
                next.Add(child with { Fitness = Evaluate(child, btcH1, valTrades, oosTrades) });
            }
            pop = [.. next.OrderByDescending(g => g.Fitness)];
            if (gen % 10 == 0)
                Console.WriteLine($"  DynamicGuardGA Gen {gen,3} — elite: {pop[0]}");
        }

        Console.WriteLine("\n─── Bayesian refinement (30 TPE iterations) ───");
        var seedObs = pop.Take(10).Select(g => (g.ToGenes(), g.Fitness));
        var history = BayesianOptimizer.Refine(seedObs, DynamicGuardGenotype.Bounds,
            genes => Evaluate(DynamicGuardGenotype.FromGenes(genes), btcH1, valTrades, oosTrades),
            30, _rng);
        var top  = history.OrderByDescending(h => h.Fitness).First();
        var best = DynamicGuardGenotype.FromGenes(top.Params, top.Fitness);
        Console.WriteLine($"  TPE best: {best}");
        return best;
    }

    internal static double Evaluate(
        DynamicGuardGenotype g,
        Candle[] btcH1,
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> valTrades,
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> oosTrades)
    {
        var session   = new DynamicGuardSession(btcH1, g);
        var valCapped = ApplyCap(valTrades);
        var oosCapped = ApplyCap(oosTrades);
        var valSim    = ApplyGuard(valCapped, session);
        var oosSim    = ApplyGuard(oosCapped, session);
        // ddLongEntryGatePct takes a DD fraction; DdEntryGatePct is bounded {0.02, 0.15} so it is
        // already in fraction units — do not rescale here.
        var valR      = Simulator.SimulatePortfolioExposureCapped(valSim, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, ddLongEntryGatePct: g.DdEntryGatePct, confLossCapMin: g.ConfLossCapMin, confLossCapMax: g.ConfLossCapMax, profitProtectThreshold: g.ProfitProtectThreshold, profitProtectDrawback: g.ProfitProtectDrawback, profitProtectFactor: g.ProfitProtectFactor, slippageBps: Config.SlippageBps);
        var oosR      = Simulator.SimulatePortfolioExposureCapped(oosSim, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, ddLongEntryGatePct: g.DdEntryGatePct, confLossCapMin: g.ConfLossCapMin, confLossCapMax: g.ConfLossCapMax, profitProtectThreshold: g.ProfitProtectThreshold, profitProtectDrawback: g.ProfitProtectDrawback, profitProtectFactor: g.ProfitProtectFactor, slippageBps: Config.SlippageBps);
        double valCalmar = (valR.EndBalance - 100.0) / Math.Max(valR.MaxDrawdownPct, 0.5);
        double oosCalmar = (oosR.EndBalance - 100.0) / Math.Max(oosR.MaxDrawdownPct, 0.5);
        return (valCalmar + oosCalmar) / 2.0;
    }

    // Apply concurrent position cap — identical to fulltest ApplyCap, so GA sees same trade set.
    internal static List<PortfolioReplay.Trade> ApplyCap(
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> trades) =>
        PortfolioReplay.FilterByConcurrentCap(
            trades.Select(t => new PortfolioReplay.Trade(t.Strategy, t.Time, t.Hold, t.Return, t.Conf)));

    // Applies dynamic guard: ATR entry gate first (blocks high-ATR entries), then confidence scaling.
    // Strategy is preserved in output so the simulator can apply the portfolio-DD entry gate.
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

    private double[] RandomGenes(int nDim)
    {
        var g = new double[nDim];
        for (int d = 0; d < nDim; d++)
            g[d] = DynamicGuardGenotype.Bounds[d, 0]
                 + _rng.NextDouble() * (DynamicGuardGenotype.Bounds[d, 1] - DynamicGuardGenotype.Bounds[d, 0]);
        return g;
    }
}
