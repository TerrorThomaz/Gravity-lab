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

    // Signature mirrors DynamicGuardGA.Run: the rotator and the guard now optimise against the
    // same object — the portfolio's trade list — instead of the rotator scoring a private toy.
    public VolatilityWeightedRotatorGenotype Run(
        Candle[] btcH1,
        RegimeBar[] btcSeries,
        DynamicGuardGenotype guardGeno,
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> trades)
    {
        var gs = new DynamicGuardSession(btcH1, guardGeno);
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
                double fitness = Evaluate(geno, gs, btcSeries, trades);
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

    // Score the object the rotator is actually DEPLOYED against: the portfolio's own trade
    // list, sized through the rotator, run through the same exposure simulator the backtest
    // uses. Paired with the guard, whose session supplies two of the three input signals.
    //
    // The previous objective simulated BUY-AND-HOLD BETA on synthetic proxies —
    //     altRet = (close[i] - close[i-4]) / close[i-4];  btcRet = altRet * 0.6;
    // — so "rotating to safety" only ever meant "scale the same coin's move by 0.6". It
    // measured market direction, not rotation skill, and it is why two independent runs drove
    // GuardWeight and AtrWeight to exactly 0: the beta term drowned both signals. It was also
    // direction-blind, which cost -35.7pp until ComputeSafetyScore learned about isLong.
    private double Evaluate(
        VolatilityWeightedRotatorGenotype geno,
        DynamicGuardSession gs,
        RegimeBar[] btcSeries,
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> trades)
    {
        if (trades.Count < 50) return -1000;
        var rotator = new VolatilityWeightedRotator(geno);

        var times   = trades.Select(t => t.Time).ToList();
        var regimes = RegimeBarLookup.TagRegimes(btcSeries, times);

        var sized = new List<(DateTime, double, double, TimeSpan, string)>(trades.Count);
        // Rotation is not free: shifting capital between alts and BTC/ETH sells one and buys the
        // other, a round trip on the fraction moved. Without this the GA sees churn as costless,
        // which is why RotationSpeed sits pinned at its 1.0 maximum — bang-bang rebalancing —
        // in direct contradiction of the class comment promising "gradual ... to avoid whipsaw
        // and reduce slippage". Charging the move is what gives that gene a reason to be < 1.
        double prevAlt = 1.0;      // start fully in alts
        double rotationCostPct = 0.0;
        for (int i = 0; i < trades.Count; i++)
        {
            var t = trades[i];
            // Unknown label => treat as long, matching PortfolioReplay's conservative convention.
            bool isLong = PortfolioReplay.IsLong(t.Strategy) ?? true;
            double safety = rotator.ComputeSafetyScore(gs.GetMult(t.Time), gs.GetAtrRatio(t.Time),
                                                       regimes[i], isLong);
            var (altShare, _, _) = rotator.ComputeAllocation(safety);
            rotationCostPct += Math.Abs(altShare - prevAlt) * TradeCosts.FeeRoundTripPct;
            prevAlt = altShare;
            sized.Add((t.Time, t.Return, t.Conf * altShare, t.Hold, t.Strategy));
        }

        var p = Simulator.SimulatePortfolioExposureCapped(sized, Config.MaxTotalExposurePct,
                                                          maxPositionFrac: 0.05);
        // Charged against the portfolio, not per trade: the rotation moves the whole book's
        // allocation, so its cost scales with capital shifted rather than with any one position.
        double ret = (p.EndBalance - p.StartBalance) / p.StartBalance - rotationCostPct / 100.0;
        // Same shape as the guard's objective: return scaled by a drawdown penalty, so a
        // rotator that buys return with drawdown cannot win. Capital rotated out is modelled
        // as FLAT here, exactly as in the combinedbacktest comparison — a de-risk-to-cash
        // lower bound, consistent between training and reporting rather than differing.
        double ddPenalty = Math.Max(0.1, 1.0 - p.MaxDrawdownPct / 100.0);
        return ret * ddPenalty;
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
        var coins = Config.BacktestCoins.Take(20).Concat(new[] { "BTCUSDT" }).Distinct().ToArray();  // BTC needed for guard/regime

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

        // Build the portfolio trade list the rotator is deployed against. Previously Train()
        // handed the GA raw candles and the GA scored buy-and-hold beta on them; the rotator
        // never sizes a coin, it sizes STRATEGY TRADES, so that is what it must be scored on.
        var swingG = File.Exists(Config.FadeShortGenoFile)
            ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype() : null;
        var gridG  = File.Exists(Config.GridGenoFile)
            ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype() : null;
        var dlG    = File.Exists(Config.DipLongGenoFile)
            ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype() : null;
        var slG    = File.Exists(Config.SwingLongGenoFile)
            ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype() : null;
        if (swingG == null && gridG == null && dlG == null && slG == null)
        {
            Console.WriteLine("No strategy genotypes found — nothing for the rotator to size. Train strategies first.");
            return;
        }

        var trades = new List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)>();
        foreach (var (sym, (h1, m15)) in coinData)
        {
            if (h1.Length < 300) continue;
            if (swingG != null)
                foreach (var t in FadeShortSimulator.GetFadeShortReturns(swingG, h1, m15))
                    trades.Add((t.Time, t.Return, swingG.PositionSizePct, TimeSpan.FromHours(swingG.MaxHoldCandles), "fade_short"));
            if (gridG != null)
                foreach (var t in GridSimulator.GetGridSessionReturns(gridG, h1))
                    // GridGenotype has no position-size gene; GridGA scores at a fixed 0.03 (FitPosFrac).
                    trades.Add((t.Time, t.Return, 0.03, TimeSpan.FromHours(gridG.MaxHoldCandles), "grid"));
            if (dlG != null)
                foreach (var t in DipLongSimulator.GetDipLongReturns(dlG, h1, m15))
                    trades.Add((t.Time, t.Return, dlG.PositionSizePct, TimeSpan.FromHours(dlG.MaxHoldCandles), "diplong"));
            if (slG != null)
                foreach (var t in SwingLongSimulator.GetSwingLongReturns(slG, h1, m15))
                    trades.Add((t.Time, t.Return, slG.PositionSizePct, TimeSpan.FromHours(slG.MaxHoldCandles), "swing_long"));
        }
        trades.Sort((a, b) => a.Time.CompareTo(b.Time));
        Console.WriteLine($"  Rotator objective: {trades.Count} portfolio trades");

        if (!coinData.TryGetValue("BTCUSDT", out var btcEntry) || btcEntry.h1.Length < 300)
        { Console.WriteLine("BTCUSDT required for the guard/regime signals."); return; }
        var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcEntry.h1);

        var ga = new VolatilityWeightedRotatorGA();
        var best = ga.Run(btcEntry.h1, btcSeries, guardGeno, trades);

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
