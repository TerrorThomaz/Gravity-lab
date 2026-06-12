namespace TradingGA;

public class DrawdownGuardGA
{
    private readonly int    _populationSize;
    private readonly int    _generations;
    private readonly Random _rng;

    public DrawdownGuardGA(int populationSize = 40, int generations = 60, Random? rng = null)
    {
        _populationSize = populationSize;
        _generations    = generations;
        _rng            = rng ?? new Random(42);
    }

    public DrawdownGuardGenotype Run(
        List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration, bool IsGuarded)> valTrades,
        List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration, bool IsGuarded)> oosTrades)
    {
        int nDim   = DrawdownGuardGenotype.Bounds.GetLength(0);
        int elites = Math.Max(2, _populationSize / 5);

        var pop = new List<DrawdownGuardGenotype>(_populationSize);
        for (int i = 0; i < _populationSize; i++)
        {
            var g = DrawdownGuardGenotype.FromGenes(RandomGenes(nDim));
            pop.Add(g with { Fitness = Evaluate(g, valTrades, oosTrades) });
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
                        double range = DrawdownGuardGenotype.Bounds[d, 1] - DrawdownGuardGenotype.Bounds[d, 0];
                        genes[d] = Math.Clamp(
                            genes[d] + _rng.NextGaussian() * range * 0.1,
                            DrawdownGuardGenotype.Bounds[d, 0], DrawdownGuardGenotype.Bounds[d, 1]);
                    }
                }
                var child = DrawdownGuardGenotype.FromGenes(genes);
                next.Add(child with { Fitness = Evaluate(child, valTrades, oosTrades) });
            }
            pop = [.. next.OrderByDescending(g => g.Fitness)];
            if (gen % 10 == 0)
                Console.WriteLine($"  DrawdownGuardGA Gen {gen,3} — elite: {pop[0]}");
        }

        Console.WriteLine("\n─── Bayesian refinement (30 TPE iterations) ───");
        var seedObs = pop.Take(10).Select(g => (g.ToGenes(), g.Fitness));
        var history = BayesianOptimizer.Refine(seedObs, DrawdownGuardGenotype.Bounds,
            genes => Evaluate(DrawdownGuardGenotype.FromGenes(genes), valTrades, oosTrades),
            30, _rng);
        var top  = history.OrderByDescending(h => h.Fitness).First();
        var best = DrawdownGuardGenotype.FromGenes(top.Params, top.Fitness);
        Console.WriteLine($"  TPE best: {best}");
        return best;
    }

    private static double Evaluate(
        DrawdownGuardGenotype g,
        List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration, bool IsGuarded)> valTrades,
        List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration, bool IsGuarded)> oosTrades)
    {
        var valR = Simulator.SimulateWithDrawdownGuard(valTrades, g, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var oosR = Simulator.SimulateWithDrawdownGuard(oosTrades, g, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        double valCalmar = (valR.EndBalance - 100.0) / Math.Max(valR.MaxDrawdownPct, 1.0);
        double oosCalmar = (oosR.EndBalance - 100.0) / Math.Max(oosR.MaxDrawdownPct, 1.0);
        return (valCalmar + oosCalmar) / 2.0;
    }

    private double[] RandomGenes(int nDim)
    {
        var g = new double[nDim];
        for (int d = 0; d < nDim; d++)
            g[d] = DrawdownGuardGenotype.Bounds[d, 0]
                 + _rng.NextDouble() * (DrawdownGuardGenotype.Bounds[d, 1] - DrawdownGuardGenotype.Bounds[d, 0]);
        return g;
    }
}
