using TradingGA;

namespace GravityGen2.Strategies.AccumulationGrid;

// AccumulationGrid GA: regime-gated EMA-anchored dynamic accumulation grid.
public class AccumulationGridGA
{
    public record CoinData(ReadOnlyMemory<Candle> TrainCandles, ReadOnlyMemory<Candle> ValCandles, double Weight = 1.0);

    private readonly int _populationSize;
    private readonly int _generations;
    private readonly int _eliteCount;
    private readonly bool _verbose;
    private readonly int _tournamentK;
    private readonly int _cataclysmStagnantGens;
    private readonly int _seed;
    private readonly bool _seedSupplied;
    private readonly Random _rng;
    private readonly FitnessConfig _cfg;
    private readonly MarketRegime _targetRegime;

    private const int MinTradesPerFold = 10;
    private const double FitPosFrac = 0.03;
    private const int D = 8;   // genotype parameter count — see RandomGenotype()

    // eliteCount = real elitism knob (no separate reporting slice). seed=null = random.
    public AccumulationGridGA(
        MarketRegime targetRegime,
        int populationSize = 60,
        int generations = 100,
        int eliteCount = 15,
        bool verbose = true,
        FitnessConfig? cfg = null,
        int tournamentK           = GaSearch.DefaultTournamentK,
        int cataclysmStagnantGens = GaSearch.DefaultCataclysmStagnantGens,
        int? seed                 = null)
    {
        _targetRegime = targetRegime;
        _populationSize = populationSize;
        _generations = generations;
        _eliteCount = eliteCount;
        _verbose = verbose;
        _cfg = cfg ?? new FitnessConfig();
        _tournamentK           = tournamentK;
        _cataclysmStagnantGens = cataclysmStagnantGens;
        (_rng, _seed, _seedSupplied) = GaSearch.CreateRng(seed);
    }

    private static double FoldScore(List<double> returns, FitnessConfig cfg)
        => FoldScoreHelper.Canonical(
            returns, FitPosFrac, MinTradesPerFold,
            FoldScoreHelper.GridShape(cfg), volWeight: 1.0, statBonusCeiling: 1.0);

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

        // Per-coin walk-forward folds via shared helpers.
        var foldScores = new List<double>();
        var foldCounts = new List<int>();
        int attemptedFolds = 0;  // includes thin folds
        for (int f = 0; f < folds; f++)
        {
            attemptedFolds++;
            var foldReturns = new List<double>();
            foreach (var (_, arr) in validCoins)
            {
                var (start, end) = FoldScoreHelper.PerCoinFoldRange(arr.Length, folds, f, _cfg.EmbargoPct);
                if (end - start < 40) continue;
                foldReturns.AddRange(AccumulationGridSimulator
                    .GetAccumulationReturns(ind, arr.Slice(start, end - start).Span, _targetRegime)
                    .Select(t => t.Return));
            }

            if (foldReturns.Count < MinTradesPerFold) continue;

            foldScores.Add(FoldScore(foldReturns, _cfg));
            foldCounts.Add(foldReturns.Count);
        }

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D, attemptedFolds);
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

    internal AccumulationGridGenotype Tournament(IReadOnlyList<(AccumulationGridGenotype g, double f)> pop) =>
        GaSearch.Tournament(pop, _tournamentK, _rng, x => x.f).g;

    public (AccumulationGridGenotype Best, double Fitness) Train(IReadOnlyList<CoinData> coins)
    {
        GaSearch.AnnounceSeed($"AccumulationGridGA.Train({_targetRegime})", _seed, _seedSupplied);

        var population = Enumerable.Range(0, _populationSize).Select(_ => RandomGenotype()).ToList();
        var scored = population.Select(g => (g, f: Fitness(g, coins, useValidation: false))).ToList();
        scored.Sort((a, b) => b.f.CompareTo(a.f));

        double bestFitnessSeen = scored[0].f;
        int    stagnantGens    = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            if (GaSearch.ShouldCataclysm(stagnantGens, _cataclysmStagnantGens))
            {
                population = GaSearch.Cataclysm(scored.Select(s => s.g).ToList(),
                                                _populationSize, _eliteCount,
                                                RandomGenotype, ref stagnantGens);
                if (_verbose)
                    Console.WriteLine($"  Gen {gen,3}: CATACLYSM — kept top {Math.Min(_eliteCount, _populationSize)}, " +
                                      $"reinitialised {_populationSize - Math.Min(_eliteCount, _populationSize)} at random");
            }
            else
            {
                var next = new List<AccumulationGridGenotype>();
                for (int i = 0; i < Math.Min(_eliteCount, scored.Count); i++) next.Add(scored[i].g);

                while (next.Count < _populationSize)
                {
                    var p1 = Tournament(scored);
                    var p2 = Tournament(scored);
                    var child = _rng.NextDouble() < 0.7 ? Crossover(p1, p2) : p1;
                    next.Add(_rng.NextDouble() < 0.3 ? Mutate(child) : child);
                }
                population = next;
            }

            scored = population.Select(g => (g, f: Fitness(g, coins, useValidation: false))).ToList();
            scored.Sort((a, b) => b.f.CompareTo(a.f));

            if (scored[0].f > bestFitnessSeen + 1e-6) { bestFitnessSeen = scored[0].f; stagnantGens = 0; }
            else stagnantGens++;

            if (_verbose && gen % 10 == 0)
                Console.WriteLine($"  Gen {gen,3}: best={scored[0].f:F3}  {scored[0].g}");
        }

        return (scored[0].g, scored[0].f);
    }
}
