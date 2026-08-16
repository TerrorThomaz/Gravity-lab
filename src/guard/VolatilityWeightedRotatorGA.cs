using System.Text.Json;
using Bybit.Net.Clients;

namespace TradingGA;

// Trains VolatilityWeightedRotator genotype. Fitness: return × drawdown penalty.
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

    // Same trade-list objective as DynamicGuardGA.
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
                double fitness = Evaluate(geno, gs, btcSeries, btcH1, trades);
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

    // Scores against the portfolio's own trade list through the exposure simulator.
    private double Evaluate(
        VolatilityWeightedRotatorGenotype geno,
        DynamicGuardSession gs,
RegimeBar[] btcSeries,
        Candle[] btcH1,   // raw candles: RegimeBar carries no price, and BtcStress is a price signal
        List<(DateTime Time, double Return, double Conf, TimeSpan Hold, string Strategy)> trades)
    {
        if (trades.Count < 50) return -1000;
        var rotator = new VolatilityWeightedRotator(geno);

        var times   = trades.Select(t => t.Time).ToList();
        var regimes = RegimeBarLookup.TagRegimes(btcSeries, times);

        var sized = new List<(DateTime, double, double, TimeSpan, string)>(trades.Count);
        // Rotation costs a round-trip fee on the fraction moved.
        double prevAlt = 1.0;      // start fully in alts
        double rotationCostPct = 0.0;
        var safeShareAt = new List<(DateTime Time, double Share)>(trades.Count);
        // BTC trailing 24h move — must match the signal the backtest serves.
        static double BtcMove(Candle[] bars, DateTime t, int lookbackBars = 24)
        {
            if (bars is not { Length: > 1 }) return 0.0;
            int lo = 0, hi = bars.Length - 1;
            if (t <= bars[0].Time) return 0.0;
            if (t >= bars[^1].Time) hi = bars.Length - 1;
            while (lo < hi) { int m = (lo + hi + 1) / 2; if (bars[m].Time <= t) lo = m; else hi = m - 1; }
            int i0 = Math.Max(0, lo - lookbackBars);
            double p0 = bars[i0].Close;
            return p0 > 1e-12 ? (bars[lo].Close - p0) / p0 * 100.0 : 0.0;
        }

        for (int i = 0; i < trades.Count; i++)
        {
            var t = trades[i];
            // Unknown label => treat as long.
            bool isLong = PortfolioReplay.IsLong(t.Strategy) ?? true;
            double safety = rotator.ComputeSafetyScore(gs.GetMult(t.Time), gs.GetAtrRatio(t.Time),
                                                       regimes[i], isLong,
                                                       BtcMove(btcH1, t.Time));
            var (altShare, btcShare, ethShare) = rotator.ComputeAllocation(safety);

            double stepped = rotator.StepAltShare(prevAlt, altShare);
            rotationCostPct += Math.Abs(stepped - prevAlt) * TradeCosts.FeeRoundTripPct;
            prevAlt = stepped; altShare = stepped;
            btcShare = (1.0 - altShare) * rotator.BtcShareOfSafe;
            ethShare = (1.0 - altShare) - btcShare;
            sized.Add((t.Time, t.Return, t.Conf * altShare, t.Hold, t.Strategy));
            safeShareAt.Add((t.Time, btcShare + ethShare));
        }

        // Credit the destination sleeve (time-weighted, not per-trade summed).
        double benchPct = 0.0;
        if (btcH1 is { Length: > 1 } && safeShareAt.Count > 1)
        {
            safeShareAt.Sort((x, y) => x.Time.CompareTo(y.Time));
            double sleeve = 1.0;
            for (int i = 1; i < safeShareAt.Count; i++)
            {
                double share = safeShareAt[i - 1].Share;
                if (share <= 1e-9) continue;
                double seg = SegReturn(btcH1, safeShareAt[i - 1].Time, safeShareAt[i].Time);
                sleeve *= 1.0 + share * seg / 100.0;
            }
            benchPct = (sleeve - 1.0) * 100.0;
        }

        static double SegReturn(Candle[] bars, DateTime a, DateTime b)
        {
            int ia = Idx(bars, a), ib = Idx(bars, b);
            if (ib <= ia) return 0.0;
            double p0 = bars[ia].Close;
            return p0 > 1e-12 ? (bars[ib].Close - p0) / p0 * 100.0 : 0.0;
        }
        static int Idx(Candle[] bars, DateTime t)
        {
            int lo = 0, hi = bars.Length - 1;
            if (t <= bars[0].Time) return 0;
            if (t >= bars[^1].Time) return bars.Length - 1;
            while (lo < hi) { int m = (lo + hi + 1) / 2; if (bars[m].Time <= t) lo = m; else hi = m - 1; }
            return lo;
        }

        var p = Simulator.SimulatePortfolioExposureCapped(sized, Config.MaxTotalExposurePct,
                                                          maxPositionFrac: 0.05);

        double ret = (p.EndBalance - p.StartBalance) / p.StartBalance - rotationCostPct / 100.0 + benchPct / 100.0;
        // Return × drawdown penalty.
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
            BtcStressWeight: _rng.NextDouble() * (RegimeWeightMax - RegimeWeightMin) + RegimeWeightMin,
            InvertRotation: _rng.NextDouble(),   // GA decides the DIRECTION of rotation
            MinAltShare: _rng.NextDouble() * 0.6,          // [0, 0.6] alt capital always retained
            RotationDeadband: _rng.NextDouble() * 0.3,     // [0, 0.3] target move needed to act
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
            BtcStressWeight: p1.BtcStressWeight * alpha + p2.BtcStressWeight * (1 - alpha),
            InvertRotation: _rng.NextDouble() < 0.5 ? p1.InvertRotation : p2.InvertRotation,
            MinAltShare: p1.MinAltShare * alpha + p2.MinAltShare * (1 - alpha),
            RotationDeadband: p1.RotationDeadband * alpha + p2.RotationDeadband * (1 - alpha),
            BtcShare: p1.BtcShare * alpha + p2.BtcShare * (1 - alpha),
            RotationSpeed: p1.RotationSpeed * alpha + p2.RotationSpeed * (1 - alpha)
        );


        if (_rng.NextDouble() < 0.1)
            child = child with { GuardWeight = Clamp(child.GuardWeight + (_rng.NextDouble() - 0.5) * 0.2, GuardWeightMin, GuardWeightMax) };
        if (_rng.NextDouble() < 0.1)
            child = child with { AtrWeight = Clamp(child.AtrWeight + (_rng.NextDouble() - 0.5) * 0.2, AtrWeightMin, AtrWeightMax) };
        if (_rng.NextDouble() < 0.1)
            child = child with { RegimeWeight = Clamp(child.RegimeWeight + (_rng.NextDouble() - 0.5) * 0.2, RegimeWeightMin, RegimeWeightMax) };
        if (_rng.NextDouble() < 0.1)
            child = child with { BtcStressWeight = Clamp(child.BtcStressWeight + (_rng.NextDouble() - 0.5) * 0.2, RegimeWeightMin, RegimeWeightMax) };
        if (_rng.NextDouble() < 0.1)
            child = child with { MinAltShare = Clamp(child.MinAltShare + (_rng.NextDouble() - 0.5) * 0.15, 0.0, 0.6) };
        if (_rng.NextDouble() < 0.1)
            child = child with { InvertRotation = _rng.NextDouble() };
        if (_rng.NextDouble() < 0.1)
            child = child with { RotationDeadband = Clamp(child.RotationDeadband + (_rng.NextDouble() - 0.5) * 0.1, 0.0, 0.3) };
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

        // Build the portfolio trade list the rotator sizes.
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
        foreach (var (sym, (fullH1, fullM15)) in coinData)
        {
            // TRAIN SLICE ONLY.
            var h1s  = DataSplit.Split(fullH1);
            var m15s = DataSplit.SplitAligned(fullM15, h1s);
            if (!h1s.IsUsable) continue;
            var h1 = h1s.Train; var m15 = m15s.Train;
            if (h1.Length < 300) continue;
            if (swingG != null)
                foreach (var t in FadeShortSimulator.GetFadeShortReturns(swingG, h1, m15))
                    trades.Add((t.Time, t.Return, swingG.PositionSizePct, TimeSpan.FromHours(swingG.MaxHoldCandles), "fade_short"));
            if (gridG != null)
                foreach (var t in GridSimulator.GetGridSessionReturns(gridG, h1))

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
            BtcStressWeight = best.BtcStressWeight,
            InvertRotation = best.InvertRotation,
            MinAltShare = best.MinAltShare,
            RotationDeadband = best.RotationDeadband,
            BtcShare = best.BtcShare,
            RotationSpeed = best.RotationSpeed
        };

        File.WriteAllText(Config.RotatorGenoFile,
            JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"\nSaved → {Config.RotatorGenoFile}");
    }
}
