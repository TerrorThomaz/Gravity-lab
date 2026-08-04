using System.Text.Json;
using Bybit.Net.Clients;

namespace TradingGA;

// GA for VolatilityWeightedRotator: trains weights for guard/ATR/regime signals,
// BTC/ETH allocation split, and rotation speed.
//
// Fitness: portfolio Sharpe × drawdown penalty × frequency bonus
// Co-evolves with DynamicGuard (shares fitness function, complementary signals).
public class VolatilityWeightedRotatorGA
{
    private readonly int _populationSize;
    private readonly int _generations;
    private readonly int _eliteCount;
    private readonly Random _rng = new();

    private const double GuardWeightMin = 0.0, GuardWeightMax = 1.0;
    private const double AtrWeightMin = 0.0, AtrWeightMax = 1.0;
    private const double RegimeWeightMin = 0.0, RegimeWeightMax = 1.0;
    private const double BtcShareMin = 0.4, BtcShareMax = 0.8;  // 40-80% BTC
    private const double RotationSpeedMin = 0.1, RotationSpeedMax = 1.0;

    public VolatilityWeightedRotatorGA(
        int populationSize = 40,
        int generations = 60,
        int eliteCount = 10)
    {
        _populationSize = populationSize;
        _generations = generations;
        _eliteCount = eliteCount;
    }

    public VolatilityWeightedRotatorGenotype Run(
        Dictionary<string, (Candle[] h1, Candle[] m15)> coinData,
        DynamicGuardGenotype guardGeno,
        string[] coins)
    {
        Console.WriteLine($"Training VolatilityWeightedRotator | pop={_populationSize} gen={_generations}\n");

        var population = new List<VolatilityWeightedRotatorGenotype>();
        for (int i = 0; i < _populationSize; i++)
            population.Add(RandomGenotype());

        VolatilityWeightedRotatorGenotype? best = null;
        double bestFitness = double.NegativeInfinity;

        for (int gen = 0; gen < _generations; gen++)
        {
            var scored = new List<(VolatilityWeightedRotatorGenotype Geno, double Fitness)>();

            foreach (var geno in population)
            {
                double fitness = Evaluate(geno, guardGeno, coinData, coins);
                scored.Add((geno, fitness));
            }

            scored.Sort((a, b) => b.Fitness.CompareTo(a.Fitness));

            if (scored[0].Fitness > bestFitness)
            {
                bestFitness = scored[0].Fitness;
                best = scored[0].Geno;
            }

            if (gen % 10 == 0 || gen == _generations - 1)
                Console.WriteLine($"  Gen {gen,3}: best={bestFitness:F3}  {best}");

            var next = new List<VolatilityWeightedRotatorGenotype>();
            for (int i = 0; i < _eliteCount; i++)
                next.Add(scored[i].Geno);

            while (next.Count < _populationSize)
            {
                var p1 = Tournament(scored);
                var p2 = Tournament(scored);
                next.Add(Crossover(p1, p2));
            }

            population = next;
        }

        Console.WriteLine($"\nBest: {best}  fitness={bestFitness:F3}");
        return best!;
    }

    private double Evaluate(
        VolatilityWeightedRotatorGenotype geno,
        DynamicGuardGenotype guardGeno,
        Dictionary<string, (Candle[] h1, Candle[] m15)> coinData,
        string[] coins)
    {
        var rotator = new VolatilityWeightedRotator(geno);
        var returns = new List<double>();

        foreach (var coin in coins)
        {
            if (!coinData.TryGetValue(coin, out var data)) continue;
            if (data.h1.Length < 500) continue;

            var guardSession = new DynamicGuardSession(data.h1, guardGeno);
            var regimeSeries = RegimeClassifier.ClassifySeriesWithDuration(data.h1);

            double altWeight = 1.0, btcWeight = 0.0, ethWeight = 0.0;
            double equity = 1.0;

            for (int i = 200; i < data.h1.Length; i += 4)  // sample every 4h
            {
                var time = data.h1[i].Time;
                double guardMult = guardSession.GetMult(time);
                double atrRatio = guardSession.GetAtrRatio(time);
                var regime = regimeSeries[i].Regime;

                double safety = rotator.ComputeSafetyScore(guardMult, atrRatio, regime);
                var (targetAlt, targetBtc, targetEth) = rotator.ComputeAllocation(safety);
                var (newAlt, newBtc, newEth) = rotator.Rebalance(
                    altWeight, btcWeight, ethWeight, targetAlt, targetBtc, targetEth);

                altWeight = newAlt;
                btcWeight = newBtc;
                ethWeight = newEth;

                // Simulate returns: alts have higher vol, BTC/ETH lower vol
                double altRet = (data.h1[i].Close - data.h1[i - 4].Close) / data.h1[i - 4].Close;
                double btcRet = altRet * 0.6;  // BTC moves ~60% of alt
                double ethRet = altRet * 0.75; // ETH moves ~75% of alt

                double portfolioRet = altWeight * altRet + btcWeight * btcRet + ethWeight * ethRet;
                equity *= (1.0 + portfolioRet);
                returns.Add(portfolioRet);
            }
        }

        if (returns.Count < 50) return -1000;

        double mean = returns.Average();
        double std = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Average());
        double sharpe = std > 1e-9 ? mean / std * Math.Sqrt(252 * 6) : 0;  // annualized (4h bars)

        double maxDd = ComputeMaxDrawdown(returns);
        double ddPenalty = Math.Max(0.1, 1.0 - maxDd);

        return sharpe * ddPenalty;
    }

    private double ComputeMaxDrawdown(List<double> returns)
    {
        double equity = 1.0, peak = 1.0, maxDd = 0;
        foreach (var r in returns)
        {
            equity *= (1.0 + r);
            if (equity > peak) peak = equity;
            double dd = (peak - equity) / peak;
            if (dd > maxDd) maxDd = dd;
        }
        return maxDd;
    }

    private VolatilityWeightedRotatorGenotype RandomGenotype() =>
        new(
            GuardWeight: _rng.NextDouble() * (GuardWeightMax - GuardWeightMin) + GuardWeightMin,
            AtrWeight: _rng.NextDouble() * (AtrWeightMax - AtrWeightMin) + AtrWeightMin,
            RegimeWeight: _rng.NextDouble() * (RegimeWeightMax - RegimeWeightMin) + RegimeWeightMin,
            BtcShare: _rng.NextDouble() * (BtcShareMax - BtcShareMin) + BtcShareMin,
            RotationSpeed: _rng.NextDouble() * (RotationSpeedMax - RotationSpeedMin) + RotationSpeedMin
        );

    private VolatilityWeightedRotatorGenotype Tournament(
        List<(VolatilityWeightedRotatorGenotype Geno, double Fitness)> scored)
    {
        int i1 = _rng.Next(scored.Count);
        int i2 = _rng.Next(scored.Count);
        return scored[i1].Fitness > scored[i2].Fitness ? scored[i1].Geno : scored[i2].Geno;
    }

    private VolatilityWeightedRotatorGenotype Crossover(
        VolatilityWeightedRotatorGenotype p1, VolatilityWeightedRotatorGenotype p2)
    {
        double alpha = _rng.NextDouble();
        var child = new VolatilityWeightedRotatorGenotype(
            GuardWeight: p1.GuardWeight * alpha + p2.GuardWeight * (1 - alpha),
            AtrWeight: p1.AtrWeight * alpha + p2.AtrWeight * (1 - alpha),
            RegimeWeight: p1.RegimeWeight * alpha + p2.RegimeWeight * (1 - alpha),
            BtcShare: p1.BtcShare * alpha + p2.BtcShare * (1 - alpha),
            RotationSpeed: p1.RotationSpeed * alpha + p2.RotationSpeed * (1 - alpha)
        );

        // Mutation
        if (_rng.NextDouble() < 0.1)
            child = child with { GuardWeight = Clamp(child.GuardWeight + (_rng.NextDouble() - 0.5) * 0.2, GuardWeightMin, GuardWeightMax) };
        if (_rng.NextDouble() < 0.1)
            child = child with { AtrWeight = Clamp(child.AtrWeight + (_rng.NextDouble() - 0.5) * 0.2, AtrWeightMin, AtrWeightMax) };
        if (_rng.NextDouble() < 0.1)
            child = child with { RegimeWeight = Clamp(child.RegimeWeight + (_rng.NextDouble() - 0.5) * 0.2, RegimeWeightMin, RegimeWeightMax) };
        if (_rng.NextDouble() < 0.1)
            child = child with { BtcShare = Clamp(child.BtcShare + (_rng.NextDouble() - 0.5) * 0.1, BtcShareMin, BtcShareMax) };
        if (_rng.NextDouble() < 0.1)
            child = child with { RotationSpeed = Clamp(child.RotationSpeed + (_rng.NextDouble() - 0.5) * 0.2, RotationSpeedMin, RotationSpeedMax) };

        return child;
    }

    private static double Clamp(double v, double min, double max) => Math.Max(min, Math.Min(max, v));

    public static void Train()
    {
        using var client = new BybitRestClient();
        var coins = Config.BacktestCoins.Take(20).ToArray();  // subset for speed

        Console.WriteLine($"Fetching data for {coins.Length} coins...\n");

        var coinData = new Dictionary<string, (Candle[] h1, Candle[] m15)>();
        foreach (var coin in coins)
        {
            var m15 = CandleFetcher.FetchFifteenMinCandlesCached(client, coin, batches: 50).Result;
            var h1 = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
            coinData[coin] = (h1, m15.ToArray());
            Console.Write(".");
        }
        Console.WriteLine();

        DynamicGuardGenotype? guardGeno = null;
        if (File.Exists(Config.DynamicGuardGenoFile))
        {
            guardGeno = JsonSerializer.Deserialize<DynamicGuardGenotypeDto>(
                File.ReadAllText(Config.DynamicGuardGenoFile))!.ToGenotype();
        }
        else
        {
            Console.WriteLine("No DynamicGuard genotype found. Run 'dynamicguardtrain' first.");
            return;
        }

        var ga = new VolatilityWeightedRotatorGA();
        var best = ga.Run(coinData, guardGeno, coins);

        var dto = new VolatilityWeightedRotatorGenotypeDto
        {
            GuardWeight = best.GuardWeight,
            AtrWeight = best.AtrWeight,
            RegimeWeight = best.RegimeWeight,
            BtcShare = best.BtcShare,
            RotationSpeed = best.RotationSpeed
        };

        File.WriteAllText(Config.RotatorGenoFile,
            JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nSaved → {Config.RotatorGenoFile}");
    }
}
