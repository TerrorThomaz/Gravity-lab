using TradingGA;

namespace GravityGen2.Strategies.HybridGrid;

// HybridGrid GA. Same fitness plumbing as GridGA (GridShape config, per-coin folds, CVaR/mean
// aggregation, path-aware MAE term) so the two are scored by the same ruler. The side mode is fixed
// per run — train LongOnly and Both separately to see what the short side is actually worth.
public class HybridGridGA
{
    public record CoinData(ReadOnlyMemory<Candle> TrainCandles, ReadOnlyMemory<Candle> ValCandles,
                           FundingRateSession? Funding = null);

    private readonly int             _populationSize;
    private readonly int             _generations;
    private readonly int             _eliteCount;
    private readonly bool            _verbose;
    private readonly int             _tournamentK;
    private readonly int             _cataclysmStagnantGens;
    private readonly int             _seed;
    private readonly bool            _seedSupplied;
    private readonly Random          _rng;
    private readonly FitnessConfig   _cfg;
    private readonly HybridGridSides _sides;

    private const int    MinTradesPerFold = 10;
    private const double FitPosFrac       = 0.03;
    private const int    D                = HybridGridGenotype.GeneCount;

    public HybridGridGA(
        HybridGridSides sides           = HybridGridSides.Both,
        int             populationSize  = 60,
        int             generations     = 100,
        int             eliteCount      = 15,
        bool            verbose         = true,
        FitnessConfig?  cfg             = null,
        int             tournamentK           = GaSearch.DefaultTournamentK,
        int             cataclysmStagnantGens = GaSearch.DefaultCataclysmStagnantGens,
        int?            seed                  = null)
    {
        _sides                 = sides;
        _populationSize        = populationSize;
        _generations           = generations;
        _eliteCount            = eliteCount;
        _verbose               = verbose;
        _cfg                   = cfg ?? new FitnessConfig();
        _tournamentK           = tournamentK;
        _cataclysmStagnantGens = cataclysmStagnantGens;
        (_rng, _seed, _seedSupplied) = GaSearch.CreateRng(seed);
    }

    private static double FoldScore(List<double> returns, FitnessConfig cfg, IReadOnlyList<double> mae)
        => FoldScoreHelper.Canonical(
            returns, FitPosFrac, MinTradesPerFold,
            FoldScoreHelper.GridShape(cfg), volWeight: 1.0, statBonusCeiling: 1.0, maePct: mae);

    private void Collect(HybridGridGenotype ind, ReadOnlySpan<Candle> span, FundingRateSession? funding,
                         List<double> rets, List<double> maes)
    {
        var mae    = new List<double>();
        var trades = HybridGridSimulator.GetHybridSessionReturns(ind, span, _sides, funding, mae);
        for (int i = 0; i < trades.Count; i++)
        {
            rets.Add(trades[i].Return);
            maes.Add(i < mae.Count ? mae[i] : 0.0);
        }
    }

    public double Fitness(HybridGridGenotype ind, IReadOnlyList<CoinData> coins, bool useValidation, int folds = 5)
    {
        GaTrialCounter.Shared.Record("hybrid_grid");
        var validCoins = coins
            .Select(c => (c, arr: useValidation ? c.ValCandles : c.TrainCandles))
            .Where(x => x.arr.Length >= 100)
            .ToList();
        if (validCoins.Count == 0) return 0;

        if (useValidation || folds <= 1)
        {
            var all = new List<double>(); var allMae = new List<double>();
            foreach (var x in validCoins) Collect(ind, x.arr.Span, x.c.Funding, all, allMae);
            return FoldScore(all, _cfg, allMae);
        }

        var foldScores = new List<double>(folds);
        var foldCounts = new List<int>(folds);
        int attemptedFolds = 0;   // includes thin folds — they enter at ThinFoldScore, never vanish
        for (int f = 0; f < folds; f++)
        {
            attemptedFolds++;
            var rets = new List<double>(); var maes = new List<double>();
            foreach (var (coin, arr) in validCoins)
            {
                var (start, end) = FoldScoreHelper.PerCoinFoldRange(arr.Length, folds, f, _cfg.EmbargoPct);
                if (end - start < 40) continue;
                Collect(ind, arr.Slice(start, end - start).Span, coin.Funding, rets, maes);
            }
            if (rets.Count < MinTradesPerFold) continue;
            foldScores.Add(FoldScore(rets, _cfg, maes));
            foldCounts.Add(rets.Count);
        }

        return FoldScoreHelper.AggregateFoldScores(foldScores, foldCounts, D, attemptedFolds);
    }

    private HybridGridGenotype RandomGenotype()
    {
        var b = HybridGridGenotype.Bounds;
        return HybridGridGenotype.FromVector(b.Select(x => x.Lo + _rng.NextDouble() * (x.Hi - x.Lo)).ToArray());
    }

    // Gaussian step of 10% of each gene's range on ~30% of genes (at least one).
    private HybridGridGenotype Mutate(HybridGridGenotype g)
    {
        var v = g.ToVector();
        var b = HybridGridGenotype.Bounds;
        int forced = _rng.Next(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            if (i != forced && _rng.NextDouble() >= 0.3) continue;
            double u1 = 1.0 - _rng.NextDouble(), u2 = _rng.NextDouble();
            double z  = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            v[i] += z * 0.1 * (b[i].Hi - b[i].Lo);
        }
        return HybridGridGenotype.FromVector(v);
    }

    private HybridGridGenotype Crossover(HybridGridGenotype a, HybridGridGenotype b)
    {
        var va = a.ToVector(); var vb = b.ToVector();
        for (int i = 0; i < va.Length; i++) if (_rng.NextDouble() < 0.5) va[i] = vb[i];
        return HybridGridGenotype.FromVector(va);
    }

    public (HybridGridGenotype Best, double Fitness) Train(IReadOnlyList<CoinData> coins)
    {
        GaSearch.AnnounceSeed($"HybridGridGA.Train({_sides})", _seed, _seedSupplied);

        var population = Enumerable.Range(0, _populationSize).Select(_ => RandomGenotype()).ToList();
        population[0] = new HybridGridGenotype();   // defaults as one known-sane starting point
        var scored = Score(population, coins);

        double bestSeen = scored[0].f;
        int    stagnant = 0;

        for (int gen = 0; gen < _generations; gen++)
        {
            if (GaSearch.ShouldCataclysm(stagnant, _cataclysmStagnantGens))
            {
                population = GaSearch.Cataclysm(scored.Select(s => s.g).ToList(),
                                                _populationSize, _eliteCount, RandomGenotype, ref stagnant);
                if (_verbose) Console.WriteLine($"  Gen {gen,3}: CATACLYSM");
            }
            else
            {
                var next = new List<HybridGridGenotype>();
                for (int i = 0; i < Math.Min(_eliteCount, scored.Count); i++) next.Add(scored[i].g);
                while (next.Count < _populationSize)
                {
                    var p1 = GaSearch.Tournament(scored, _tournamentK, _rng, x => x.f).g;
                    var p2 = GaSearch.Tournament(scored, _tournamentK, _rng, x => x.f).g;
                    var child = _rng.NextDouble() < 0.7 ? Crossover(p1, p2) : p1;
                    next.Add(_rng.NextDouble() < 0.3 ? Mutate(child) : child);
                }
                population = next;
            }

            scored = Score(population, coins);
            if (scored[0].f > bestSeen + 1e-6) { bestSeen = scored[0].f; stagnant = 0; }
            else stagnant++;

            if (_verbose && gen % 10 == 0)
                Console.WriteLine($"  Gen {gen,3}: best={scored[0].f:F3}  {scored[0].g}");
        }
        return (scored[0].g, scored[0].f);
    }

    // Fitness evaluations are independent and the simulator is pure, so score in parallel; the
    // result is ordered deterministically (stable sort on index for ties) so a seed still replays.
    private List<(HybridGridGenotype g, double f)> Score(List<HybridGridGenotype> pop, IReadOnlyList<CoinData> coins)
    {
        var f = new double[pop.Count];
        Parallel.For(0, pop.Count, i => f[i] = Fitness(pop[i], coins, useValidation: false));
        return pop.Select((g, i) => (g, f: f[i], i))
                  .OrderByDescending(x => x.f).ThenBy(x => x.i)
                  .Select(x => (x.g, x.f)).ToList();
    }
}
