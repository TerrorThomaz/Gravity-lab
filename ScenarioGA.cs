namespace TradingGA;

// Adversarial GA: finds the crash scenario that maximises portfolio max-drawdown.
// Fitness = maxDrawdownPct (higher = worse for the portfolio = fitter for this GA).
// Uses a 10-coin diverse slice for speed; builds route session from original (unmorphed) BTC.
public class ScenarioGA
{
    public static readonly string[] ScenarioCoins =
    [
        "BTCUSDT", "ETHUSDT", "SOLUSDT", "LINKUSDT", "AAVEUSDT",
        "ARBUSDT", "NEARUSDT", "WIFUSDT", "1000PEPEUSDT", "ORDIUSDT"
    ];

    private readonly int  _populationSize;
    private readonly int  _generations;
    private readonly bool _verbose;

    public ScenarioGA(int populationSize = 40, int generations = 60, bool verbose = true)
    {
        _populationSize = populationSize;
        _generations    = generations;
        _verbose        = verbose;
    }

    public record CoinData(Candle[] H1, string Symbol);

    public ScenarioGenotype Run(
        IReadOnlyDictionary<string, Candle[]> h1Map,
        FadeShortGenotype     fsG,
        GridGenotype          gridG,
        FadeLongGenotype?     flG,
        DipLongGenotype?      dlG,
        SwingLongGenotype?    slG,
        RegimeRouterGenotype? routerG)
    {
        var coins = ScenarioCoins
            .Where(h1Map.ContainsKey)
            .Select(s => new CoinData(h1Map[s], s))
            .ToList();

        if (coins.Count == 0) throw new InvalidOperationException("No scenario coins found in h1Map.");

        RegimeRouterSession? session = null;
        if (routerG != null && h1Map.TryGetValue("BTCUSDT", out var btcH1r) && btcH1r.Length >= 200)
        {
            var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcH1r);
            RegimeBar[]? ethSeries = h1Map.TryGetValue("ETHUSDT", out var ethH1r) && ethH1r.Length >= 200
                ? RegimeClassifier.ClassifySeriesWithDuration(ethH1r) : null;
            session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
        }

        var rng  = new Random(42);
        int nDim = 7;

        var pop = new List<ScenarioGenotype>(_populationSize);
        for (int i = 0; i < _populationSize; i++)
        {
            var genes = RandomGenes(nDim, rng);
            var g     = ScenarioGenotype.FromGenes(genes);
            pop.Add(g with { Fitness = Evaluate(g, coins, fsG, gridG, flG, dlG, slG, session) });
        }
        pop = [.. pop.OrderByDescending(g => g.Fitness)];

        for (int gen = 1; gen <= _generations; gen++)
        {
            var next = pop.Take(2).ToList();

            while (next.Count < _populationSize)
            {
                var parent = pop[rng.Next(Math.Min(10, pop.Count))];
                var genes  = parent.ToGenes();
                for (int d = 0; d < nDim; d++)
                {
                    if (rng.NextDouble() < 0.7)
                    {
                        double range = ScenarioGenotype.Bounds[d, 1] - ScenarioGenotype.Bounds[d, 0];
                        genes[d] = Math.Clamp(
                            genes[d] + rng.NextGaussian() * range * 0.1,
                            ScenarioGenotype.Bounds[d, 0], ScenarioGenotype.Bounds[d, 1]);
                    }
                }
                var child = ScenarioGenotype.FromGenes(genes);
                next.Add(child with { Fitness = Evaluate(child, coins, fsG, gridG, flG, dlG, slG, session) });
            }

            pop = [.. next.OrderByDescending(g => g.Fitness)];
            if (_verbose && gen % 10 == 0)
                Console.WriteLine($"  ScenarioGA Gen {gen,3} — worst-case DD: {pop[0].Fitness:F1}%  "
                    + $"Depth={pop[0].CrashDepthPct:P0}  Dur={pop[0].CrashDurationHours:F0}h  "
                    + $"AltBeta={pop[0].AltBetaPct:F2}");
        }

        return pop[0];
    }

    // Public entry point: evaluate a specific scenario against the full coin set,
    // optionally with the drawdown guard active.
    public static double EvaluateScenario(
        ScenarioGenotype         scenario,
        IReadOnlyDictionary<string, Candle[]> h1Map,
        FadeShortGenotype        fsG,
        GridGenotype             gridG,
        FadeLongGenotype?        flG,
        DipLongGenotype?         dlG,
        SwingLongGenotype?       slG,
        RegimeRouterSession?     session,
        DrawdownGuardGenotype?   guard = null)
    {
        var coins = ScenarioCoins
            .Where(h1Map.ContainsKey)
            .Select(s => new CoinData(h1Map[s], s))
            .ToList();
        return Evaluate(scenario, coins, fsG, gridG, flG, dlG, slG, session, guard);
    }

    private static double Evaluate(
        ScenarioGenotype      g,
        List<CoinData>        coins,
        FadeShortGenotype     fsG,
        GridGenotype          gridG,
        FadeLongGenotype?     flG,
        DipLongGenotype?      dlG,
        SwingLongGenotype?    slG,
        RegimeRouterSession?  session,
        DrawdownGuardGenotype? guard = null)
    {
        var btcCoin = coins.FirstOrDefault(c => c.Symbol == "BTCUSDT");
        if (btcCoin == null) return 0;
        int injBar = (int)(btcCoin.H1.Length * g.InjectionOffsetFrac);
        int injUpper = btcCoin.H1.Length - (int)g.CrashDurationHours - (int)g.RecoveryHours - 10;
        if (injUpper < 50) return 0;
        injBar = Math.Clamp(injBar, 50, injUpper);

        var trades = new List<(DateTime Time, double Return, double Conf, TimeSpan Hold, bool IsGuarded)>();

        foreach (var coin in coins)
        {
            int coinInjUpper = coin.H1.Length - (int)g.CrashDurationHours - (int)g.RecoveryHours - 10;
            if (coinInjUpper < 50) continue;
            int coinInjBar = Math.Min(injBar, coinInjUpper);

            double beta = coin.Symbol == "BTCUSDT" ? 1.0 : g.AltBetaPct;
            var    mH1  = ScenarioInjector.Inject(coin.H1, g, coinInjBar, beta);
            var    mM15 = ScenarioInjector.DeaggregateToM15(mH1);
            if (mH1.Length < 200) continue;

            // FadeShort — exempt from guard
            {
                var t    = FadeShortSimulator.GetFadeShortReturns(fsG, mH1, mM15);
                double c = t.Count > 0 ? Simulator.ComputeConfidence(t.Select(x => x.Return).ToList()) : 0.03;
                foreach (var (time, ret, _) in t)
                    trades.Add((time, ret, c, TimeSpan.FromHours(fsG.MaxHoldCandles), false));
            }
            // Grid — guarded
            {
                var raw   = GridSimulator.GetGridReturns(gridG, mH1);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList() : raw;
                double c  = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(x => x.Return).ToList()) : 0.03;
                foreach (var (time, ret, _) in gated)
                    trades.Add((time, ret, c, TimeSpan.FromHours(gridG.MaxHoldCandles), true));
            }
            // FadeLong — exempt from guard
            if (flG != null && mH1.Length >= 200 && mM15.Length >= 800)
            {
                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, mH1, mM15);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList() : raw;
                double c  = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(x => x.Return).ToList()) : 0.03;
                foreach (var (time, ret, _, _) in gated)
                    trades.Add((time, ret, c, TimeSpan.FromHours(flG.MaxHoldCandles), false));
            }
            // DipLong — guarded
            if (dlG != null && mH1.Length >= 200 && mM15.Length >= 800)
            {
                var raw   = DipLongSimulator.GetDipLongReturns(dlG, mH1, mM15);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                double c  = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(x => x.Return).ToList()) : 0.03;
                foreach (var (time, ret, _, _) in gated)
                    trades.Add((time, ret, c, TimeSpan.FromHours(dlG.MaxHoldCandles), true));
            }
            // SwingLong — guarded
            if (slG != null && mH1.Length >= 200 && mM15.Length >= 800)
            {
                var raw   = SwingLongSimulator.GetSwingLongReturns(slG, mH1, mM15);
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                double c  = gated.Count > 0 ? Simulator.ComputeConfidence(gated.Select(x => x.Return).ToList()) : 0.03;
                foreach (var (time, ret, _) in gated)
                    trades.Add((time, ret, c, TimeSpan.FromHours(slG.MaxHoldCandles), true));
            }
        }

        if (trades.Count < 5) return 0;

        if (guard != null)
        {
            var simInput = trades.OrderBy(t => t.Time).ToList();
            return Simulator.SimulateWithDrawdownGuard(simInput, guard, Config.MaxTotalExposurePct, maxPositionFrac: 0.05).MaxDrawdownPct;
        }
        else
        {
            var simInput = trades.OrderBy(t => t.Time)
                .Select(t => (t.Time, t.Return, t.Conf, t.Hold)).ToList();
            return Simulator.SimulatePortfolioExposureCapped(simInput, Config.MaxTotalExposurePct, maxPositionFrac: 0.05).MaxDrawdownPct;
        }
    }

    private static double[] RandomGenes(int nDim, Random rng)
    {
        var g = new double[nDim];
        for (int d = 0; d < nDim; d++)
            g[d] = ScenarioGenotype.Bounds[d, 0]
                 + rng.NextDouble() * (ScenarioGenotype.Bounds[d, 1] - ScenarioGenotype.Bounds[d, 0]);
        return g;
    }
}
