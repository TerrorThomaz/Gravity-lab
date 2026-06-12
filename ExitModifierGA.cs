using System.Text.Json;

namespace TradingGA;

// GA that optimises ExitModifierGenotype by maximising combined val+OOS Calmar ratio.
// Fitness = (valCalmar + oosCalmar) / 2 where Calmar = portfolioReturn / max(maxDD, 1.0).
// ApplyModifier scales each trade's CoinConf by ComputeMult — no trades removed.
public class ExitModifierGA
{
    private readonly int  _populationSize;
    private readonly int  _generations;
    private readonly bool _verbose;
    private readonly Random _rng = new();

    public ExitModifierGA(int populationSize = 40, int generations = 60, bool verbose = true)
    {
        _populationSize = populationSize;
        _generations    = generations;
        _verbose        = verbose;
    }

    public ExitModifierGenotype Run(
        List<TradeEnricher.EnrichedTrade> valEnriched,
        List<TradeEnricher.EnrichedTrade> oosEnriched)
    {
        int nDim   = 8;
        int elites = Math.Max(2, _populationSize / 5);

        var pop = new List<ExitModifierGenotype>(_populationSize);
        for (int i = 0; i < _populationSize; i++)
        {
            var g = ExitModifierGenotype.FromGenes(RandomGenes(nDim));
            pop.Add(g with { Fitness = Evaluate(g, valEnriched, oosEnriched) });
        }
        pop = [.. pop.OrderByDescending(g => g.Fitness)];

        for (int gen = 1; gen <= _generations; gen++)
        {
            var next = pop.Take(elites).ToList();
            int topN = Math.Max(2, pop.Count / 2);

            while (next.Count < _populationSize)
            {
                var parent = pop[_rng.Next(topN)];
                var genes  = parent.ToGenes();
                MutateGenes(genes, nDim, 0.12);
                var child = ExitModifierGenotype.FromGenes(genes);
                next.Add(child with { Fitness = Evaluate(child, valEnriched, oosEnriched) });
            }

            pop = [.. next.OrderByDescending(g => g.Fitness)];
            if (_verbose && gen % 10 == 0)
                Console.WriteLine($"  ExitModifierGA Gen {gen,3} — elite: {pop[0]}");
        }

        // Bayesian refinement
        Console.WriteLine("\n─── Bayesian refinement (30 TPE iterations) ───");
        var seedObs = pop.Take(10).Select(g => (g.ToGenes(), g.Fitness));
        var history = BayesianOptimizer.Refine(
            seedObs, ExitModifierGenotype.Bounds,
            genes => Evaluate(ExitModifierGenotype.FromGenes(genes), valEnriched, oosEnriched),
            iterations: 30, rng: _rng);
        var best  = history.OrderByDescending(h => h.Fitness).First();
        var bestG = ExitModifierGenotype.FromGenes(best.Params, best.Fitness);
        Console.WriteLine($"  TPE best: {bestG}");
        return bestG;
    }

    private double Evaluate(
        ExitModifierGenotype g,
        List<TradeEnricher.EnrichedTrade> val,
        List<TradeEnricher.EnrichedTrade> oos)
    {
        var valMod = ApplyModifier(g, val);
        var oosMod = ApplyModifier(g, oos);
        if (valMod.Count < 5 || oosMod.Count < 5) return -1.0;

        var valPort = Simulator.SimulatePortfolioExposureCapped(valMod, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var oosPort = Simulator.SimulatePortfolioExposureCapped(oosMod, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

        double valRet = valPort.EndBalance - 100.0;
        double oosRet = oosPort.EndBalance - 100.0;
        if (valRet <= 0 && oosRet <= 0) return (valRet + oosRet) / 200.0 - 0.5;

        double valCalmar = valRet / Math.Max(valPort.MaxDrawdownPct, 1.0);
        double oosCalmar = oosRet / Math.Max(oosPort.MaxDrawdownPct, 1.0);
        return (valCalmar + oosCalmar) / 2.0;
    }

    public static List<(DateTime EntryTime, double Return, double CoinConf, TimeSpan HoldDuration)> ApplyModifier(
        ExitModifierGenotype g, List<TradeEnricher.EnrichedTrade> trades)
    {
        return trades
            .Select(t => (t.EntryTime, t.Return, t.CoinConf * g.ComputeMult(t), t.HoldDuration))
            .OrderBy(t => t.EntryTime)
            .ToList();
    }

    private double[] RandomGenes(int nDim)
    {
        var g = new double[nDim];
        for (int d = 0; d < nDim; d++)
            g[d] = ExitModifierGenotype.Bounds[d, 0]
                 + _rng.NextDouble() * (ExitModifierGenotype.Bounds[d, 1] - ExitModifierGenotype.Bounds[d, 0]);
        return g;
    }

    private void MutateGenes(double[] genes, int nDim, double strength)
    {
        for (int d = 0; d < nDim; d++)
        {
            if (_rng.NextDouble() < 0.7)
            {
                double range = ExitModifierGenotype.Bounds[d, 1] - ExitModifierGenotype.Bounds[d, 0];
                genes[d] = Math.Clamp(
                    genes[d] + _rng.NextGaussian() * range * strength,
                    ExitModifierGenotype.Bounds[d, 0], ExitModifierGenotype.Bounds[d, 1]);
            }
        }
    }
}
