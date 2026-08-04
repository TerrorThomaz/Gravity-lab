using TradingGA;

namespace GravityGen2.Strategies.AccumulationGrid;

public class AccumulationGridGA
{
    public record CoinData(ReadOnlyMemory<Candle> TrainCandles, ReadOnlyMemory<Candle> ValCandles, double Weight = 1.0);

    private readonly int _populationSize;
    private readonly int _generations;
    private readonly int _eliteCount;
    private readonly bool _verbose;
    private readonly Random _rng = new();
    private readonly FitnessConfig _cfg;
    private readonly MarketRegime _targetRegime;

    private const int MinTradesPerFold = 10;
    private const double FitPosFrac = 0.03;

    public AccumulationGridGA(
        MarketRegime targetRegime,
        int populationSize = 60,
        int generations = 100,
        int eliteCount = 15,
        bool verbose = true,
        FitnessConfig? cfg = null)
    {
        _targetRegime = targetRegime;
        _populationSize = populationSize;
        _generations = generations;
        _eliteCount = eliteCount;
        _verbose = verbose;
        _cfg = cfg ?? new FitnessConfig();
    }

    private static double FoldScore(List<double> returns, FitnessConfig cfg)
    {
        if (returns.Count < MinTradesPerFold) return -1.0;

        double wr = (double)returns.Count(r => r > 0) / returns.Count;
        double grossWin = returns.Where(r => r > 0).DefaultIfEmpty(0).Sum();
        double grossLoss = Math.Abs(returns.Where(r => r <= 0).DefaultIfEmpty(0).Sum());
        double pf = grossLoss > 1e-10 ? grossWin / grossLoss : (grossWin > 0 ? 5.0 : 0.0);

        if (pf < 1.0) return pf - 2.0;

        double balance = 1.0, peak = 1.0, maxDd = 0.0;
        foreach (var r in returns)
        {
            balance += r / 100.0 * FitPosFrac;
            if (balance > peak) peak = balance;
            double dd = (peak - balance) / peak;
            if (dd > maxDd) maxDd = dd;
        }

        double gain = balance - 1.0;
        if (gain <= 0) return gain * 100 - 0.5;

        double wrMult = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 0.5;
        double ddDiv = 1.0 + maxDd * 20.0;

        double baseScore = gain * 100.0 * wrMult / ddDiv;
        int n = returns.Count;
        double sharpe = Simulator.SharpeRatio(returns, n);
        double calmar = Simulator.CalmarRatio(returns);
        double pfStat = Simulator.ProfitFactor(returns);
        double sortino = Simulator.SortinoRatio(returns, n);
        return baseScore
            * (1.0 + cfg.SharpeW * Math.Max(0, Math.Min(sharpe / 3.0, 1.0)))
            * (1.0 + cfg.CalmarW * Math.Max(0, Math.Min(calmar / 2.0, 1.0)))
            * (1.0 + cfg.PfW * Math.Max(0, Math.Min(pfStat - 1.0, 1.0)))
            * (1.0 + cfg.SortinoW * Math.Max(0, Math.Min(sortino / 4.0, 1.0)));
    }

    private double Fitness(AccumulationGridGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        var validCoins = coins
            .Select(c => (c, arr: useValidation ? c.ValCandles : c.TrainCandles))
            .Where(x => x.arr.Length >= 100)
            .ToList();
        if (validCoins.Count == 0) return 0;

        if (useValidation || folds <= 1)
        {
            var all = validCoins
                .SelectMany(x => AccumulationGridSimulator.GetAccumulationReturns(ind, x.arr.Span, _targetRegime).Select(t => t.Return))
                .ToList();
            return FoldScore(all, _cfg);
        }

        var foldScores = new List<double>();
        for (int f = 0; f < folds; f++)
        {
            var foldReturns = new List<double>();
            foreach (var (coin, _) in validCoins)
            {
                int len = coin.TrainCandles.Length;
                int foldSize = len / folds;
                int start = f * foldSize;
                int end = (f == folds - 1) ? len : start + foldSize;
                var slice = coin.TrainCandles.Slice(start, end - start);
                foldReturns.AddRange(AccumulationGridSimulator.GetAccumulationReturns(ind, slice.Span, _targetRegime).Select(t => t.Return));
            }
            foldScores.Add(FoldScore(foldReturns, _cfg));
        }

        double mean = foldScores.Average();
        double std = Math.Sqrt(foldScores.Average(s => (s - mean) * (s - mean)));
        return mean - 0.75 * std;
    }

    private AccumulationGridGenotype RandomGenotype()
    {
        return new AccumulationGridGenotype
        {
            EmaPeriod = _rng.Next(20, 100),
            GridStepAtrMult = 0.5 + _rng.NextDouble() * 2.5,
            MaxLevels = _rng.Next(3, 8),
            TakeProfitAtrMult = 1.5 + _rng.NextDouble() * 4.0,
            StopLossAtrMult = 1.0 + _rng.NextDouble() * 3.0,
            MaxHoldBars = _rng.Next(24, 200),
            PositionSizePct = 0.03 + _rng.NextDouble() * 0.04,
            RegimeSustainBars = _rng.Next(12, 72)
        };
    }

    private AccumulationGridGenotype Mutate(AccumulationGridGenotype g)
    {
        double MutateDouble(double val, double scale) => val * (1.0 + (_rng.NextDouble() * 2 - 1) * scale);
        int MutateInt(int val, int range) => Math.Max(1, val + _rng.Next(-range, range + 1));

        return new AccumulationGridGenotype
        {
            EmaPeriod = Math.Clamp(MutateInt((int)g.EmaPeriod, 10), 20, 100),
            GridStepAtrMult = Math.Clamp(MutateDouble(g.GridStepAtrMult, 0.2), 0.5, 3.0),
            MaxLevels = Math.Clamp(MutateInt(g.MaxLevels, 1), 3, 8),
            TakeProfitAtrMult = Math.Clamp(MutateDouble(g.TakeProfitAtrMult, 0.2), 1.5, 6.0),
            StopLossAtrMult = Math.Clamp(MutateDouble(g.StopLossAtrMult, 0.2), 1.0, 4.0),
            MaxHoldBars = Math.Clamp(MutateInt(g.MaxHoldBars, 20), 24, 200),
            PositionSizePct = Math.Clamp(MutateDouble(g.PositionSizePct, 0.1), 0.03, 0.07),
            RegimeSustainBars = Math.Clamp(MutateInt(g.RegimeSustainBars, 10), 12, 72)
        };
    }

    private AccumulationGridGenotype Crossover(AccumulationGridGenotype a, AccumulationGridGenotype b)
    {
        return new AccumulationGridGenotype
        {
            EmaPeriod = _rng.NextDouble() < 0.5 ? a.EmaPeriod : b.EmaPeriod,
            GridStepAtrMult = _rng.NextDouble() < 0.5 ? a.GridStepAtrMult : b.GridStepAtrMult,
            MaxLevels = _rng.NextDouble() < 0.5 ? a.MaxLevels : b.MaxLevels,
            TakeProfitAtrMult = _rng.NextDouble() < 0.5 ? a.TakeProfitAtrMult : b.TakeProfitAtrMult,
            StopLossAtrMult = _rng.NextDouble() < 0.5 ? a.StopLossAtrMult : b.StopLossAtrMult,
            MaxHoldBars = _rng.NextDouble() < 0.5 ? a.MaxHoldBars : b.MaxHoldBars,
            PositionSizePct = _rng.NextDouble() < 0.5 ? a.PositionSizePct : b.PositionSizePct,
            RegimeSustainBars = _rng.NextDouble() < 0.5 ? a.RegimeSustainBars : b.RegimeSustainBars
        };
    }

    private AccumulationGridGenotype Tournament(IReadOnlyList<(AccumulationGridGenotype g, double f)> pop, int k = 3)
    {
        var best = pop[_rng.Next(pop.Count)];
        for (int i = 1; i < k; i++)
        {
            var c = pop[_rng.Next(pop.Count)];
            if (c.f > best.f) best = c;
        }
        return best.g;
    }

    public (AccumulationGridGenotype Best, double Fitness) Train(IReadOnlyList<CoinData> coins)
    {
        var population = Enumerable.Range(0, _populationSize).Select(_ => RandomGenotype()).ToList();
        var scored = population.Select(g => (g, f: Fitness(g, coins, useValidation: false))).ToList();
        scored.Sort((a, b) => b.f.CompareTo(a.f));

        for (int gen = 0; gen < _generations; gen++)
        {
            var next = new List<AccumulationGridGenotype>();
            for (int i = 0; i < _eliteCount; i++) next.Add(scored[i].g);

            while (next.Count < _populationSize)
            {
                var p1 = Tournament(scored);
                var p2 = Tournament(scored);
                var child = _rng.NextDouble() < 0.7 ? Crossover(p1, p2) : p1;
                next.Add(_rng.NextDouble() < 0.3 ? Mutate(child) : child);
            }

            population = next;
            scored = population.Select(g => (g, f: Fitness(g, coins, useValidation: false))).ToList();
            scored.Sort((a, b) => b.f.CompareTo(a.f));

            if (_verbose && gen % 10 == 0)
                Console.WriteLine($"  Gen {gen,3}: best={scored[0].f:F3}  {scored[0].g}");
        }

        return (scored[0].g, scored[0].f);
    }
}
