using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class BacktestCommands
{
    public static async Task RunBacktest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | BACKTEST ({Config.BacktestCoins.Length} coins · last 1 year · regime-routed) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile))
        { Console.WriteLine("No FadeShort genotype — run 'dotnet run -- train' first."); return; }
        var gUniversal = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var clusterGenos = new Dictionary<CoinCluster, FadeShortGenotype>();
        foreach (CoinCluster cl in Enum.GetValues<CoinCluster>())
        {
            string clFile = CoinClusterHelper.GenoFile(cl);
            clusterGenos[cl] = File.Exists(clFile)
                ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype()
                : gUniversal;
        }
        Console.WriteLine($"  FadeShort  (overbought fade · coin-screened)  : {gUniversal}");

        GridGenotype? gridG = null;
        if (File.Exists(Config.GridGenoFile))
        {
            gridG = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
            Console.WriteLine($"  Grid       (ranging/neutral · coin-screened) : {gridG}");
        }
        else Console.WriteLine("  Grid       — no genotype (run 'dotnet run -- gridtrain')");

        DipLongGenotype? dlGeno = File.Exists(Config.DipLongGenoFile)
            ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()
            : null;
        FadeLongGenotype? flGeno = File.Exists(Config.FadeLongGenoFile)
            ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype()
            : null;

        if (dlGeno != null) Console.WriteLine($"  DipLong    (bull ≥{dlGeno.RegimeSustainedBars} h1 bars · full history)   : {dlGeno}");
        else                Console.WriteLine("  DipLong    — no genotype (run diplongtrain)");
        if (flGeno != null) Console.WriteLine($"  FadeLong   (bear ≥{flGeno.RegimeSustainedBars} h1 bars · full history)   : {flGeno}");
        else                Console.WriteLine("  FadeLong   — no genotype (run fadelongtrain)");

        RegimeRouterGenotype? routerGeno = File.Exists(Config.RouterGenoFile)
            ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype()
            : null;
        Console.WriteLine();

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m candles, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try { var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113); return (sym, m15); }
            finally { sem.Release(); }
        });
        var fetchedArr = await Task.WhenAll(fetchTasks);
        Console.WriteLine();

        var fsTrades   = new List<(string Coin, DateTime Time, double Return, double Frac)>();
        var gridTrades = new List<(string Coin, DateTime Time, double Return, double Frac)>();
        var dlTrades   = new List<(string Coin, DateTime Time, double Return, double Frac)>();
        var flTrades   = new List<(string Coin, DateTime Time, double Return, double Frac)>();

        var fsCoinRows   = new List<(string Coin, int Trades, double WR, double PF, double AvgRet, double Frac)>();
        var gridCoinRows = new List<(string Coin, int Trades, double WR, double PF, double AvgRet, double Frac)>();
        var dlCoinRows   = new List<(string Coin, int Trades, double WR, double PF, double AvgRet, double Frac)>();
        var flCoinRows   = new List<(string Coin, int Trades, double WR, double PF, double AvgRet, double Frac)>();

        // Unscreened val returns: all coins regardless of train-gate, to reveal selection bias
        var fsUnscreenedRet = new List<double>();

        int fsVCC = 0, gridVCC = 0, dlVCC = 0, flVCC = 0;

        foreach (var (sym, m15List) in fetchedArr)
        {
            if (m15List.Count < 600) continue;
            var m15 = m15List.ToArray();
            var h1  = FadeShortSimulator.AggregateCandles(m15, 4);

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            if (volUsd.Count == 0 || volUsd[volUsd.Count / 2] < Config.MinMedianVolUsdM) continue;

            int h1Split  = Math.Max(0, h1.Length - 8760);
            int m15Split = h1Split * 4;
            var h1Train  = h1[..h1Split];
            var h1Val    = h1[h1Split..];
            var m15Train = m15[..m15Split];
            var m15Val   = m15[m15Split..];

            var coinCluster = CoinClusterHelper.Classify(h1);
            var fsG        = clusterGenos[coinCluster];
            var screenH1   = h1Train.Length >= 4380 ? h1Train : h1;
            var screenM15  = screenH1.Length == h1.Length ? m15 : m15Train;
            var fsTr = FadeShortSimulator.GetFadeShortReturns(fsG, screenH1, screenM15).Select(t => t.Return).ToList();
            double fsExp   = fsTr.Count >= 20 ? fsTr.Average()                                  : double.NegativeInfinity;
            double fsPF    = fsTr.Count >= 20 ? Simulator.ProfitFactor(fsTr)                    : 0;
            double fsSort  = fsTr.Count >= 20 ? Simulator.SortinoRatio(fsTr, screenH1.Length * 12) : double.NegativeInfinity;
            // Screened path: only coins where strategy showed edge on train data contribute to
            // the main simulation.  The unscreened path (below) is the honest OOS baseline —
            // the gap between the two shows how much selection bias the train-gate adds.
            {
                var vt   = FadeShortSimulator.GetFadeShortReturns(fsG, h1Val, m15Val);
                var vRet = vt.Select(t => t.Return).ToList();
                fsVCC += h1Val.Length * 12;
                // Unscreened: include all coins in the aggregate for an unbiased lower bound
                foreach (var (t, r, _, _, _) in vt) fsUnscreenedRet.Add(r);
                if (fsExp > 0 && fsPF >= 1.2 && fsSort >= 0.3)
                {
                    double conf = Simulator.ComputeConfidence(fsTr);
                    foreach (var (t, r, _, _, _) in vt) fsTrades.Add((sym, t, r, conf));
                    double wr = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
                    if (vRet.Count > 0) fsCoinRows.Add((sym, vRet.Count, wr, Simulator.ProfitFactor(vRet), vRet.Count > 0 ? vRet.Average() : 0, conf));
                }
            }

            if (gridG != null)
            {
                var gTr   = GridSimulator.GetGridReturns(gridG, h1Train).Select(t => t.Return).ToList();
                double gExp  = gTr.Count >= 20 ? gTr.Average()                           : double.NegativeInfinity;
                double gPF   = gTr.Count >= 20 ? Simulator.ProfitFactor(gTr)             : 0;
                double gSort = gTr.Count >= 20 ? Simulator.SortinoRatio(gTr, h1Train.Length) : double.NegativeInfinity;
                if (gExp > 0 && gPF >= 1.2 && gSort >= 0.3)
                {
                    double gConf = Simulator.ComputeConfidence(gTr);
                    var    gVt   = GridSimulator.GetGridReturns(gridG, h1Val);
                    var    gVRet = gVt.Select(t => t.Return).ToList();
                    gridVCC += h1Val.Length * 12;
                    foreach (var (t, r, _, _, _) in gVt) gridTrades.Add((sym, t, r, gConf));
                    double gWr = gVRet.Count > 0 ? (double)gVRet.Count(r => r > 0) / gVRet.Count : 0;
                    if (gVRet.Count > 0) gridCoinRows.Add((sym, gVRet.Count, gWr, Simulator.ProfitFactor(gVRet), gVRet.Count > 0 ? gVRet.Average() : 0, gConf));
                }
            }

            if (dlGeno != null && h1.Length >= 200)
            {
                int dlStart  = Math.Max(0, h1.Length - 8760);
                var h1Year   = h1[dlStart..];
                var m15Year  = m15[(dlStart * 4)..];
                var raw      = DipLongSimulator.GetDipLongReturns(dlGeno, h1Year, m15Year);
                var filtered = raw.Where(t => t.RegimeBarsActive >= dlGeno.RegimeSustainedBars).ToList();
                var vRet     = filtered.Select(t => t.Return).ToList();
                dlVCC += h1Year.Length * 12;
                foreach (var t in filtered) dlTrades.Add((sym, t.Time, t.Return, dlGeno.PositionSizePct));
                double wr = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
                if (vRet.Count > 0) dlCoinRows.Add((sym, vRet.Count, wr, Simulator.ProfitFactor(vRet), vRet.Average(), dlGeno.PositionSizePct));
            }

            if (flGeno != null && h1.Length >= 200)
            {
                int flStart  = Math.Max(0, h1.Length - 8760);
                var h1Year   = h1[flStart..];
                var m15Year  = m15[(flStart * 4)..];
                var raw      = FadeLongSimulator.GetFadeLongReturns(flGeno, h1Year, m15Year);
                var filtered = raw.Where(t => t.RegimeBarsActive >= flGeno.RegimeSustainedBars).ToList();
                var vRet     = filtered.Select(t => t.Return).ToList();
                flVCC += h1Year.Length * 12;
                foreach (var t in filtered) flTrades.Add((sym, t.Time, t.Return, flGeno.PositionSizePct));
                double wr = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
                if (vRet.Count > 0) flCoinRows.Add((sym, vRet.Count, wr, Simulator.ProfitFactor(vRet), vRet.Average(), flGeno.PositionSizePct));
            }
        }

        static void PrintStrategySection(
            string label,
            List<(string Coin, DateTime Time, double Return, double Frac)> trades,
            List<(string Coin, int Trades, double WR, double PF, double AvgRet, double Frac)> coinRows,
            int candleCount)
        {
            var ret  = trades.Select(t => t.Return).ToList();
            int wins = ret.Count(r => r > 0);
            Console.WriteLine($"\n{new string('═', 72)}");
            Console.WriteLine($"  {label}");
            Console.WriteLine($"{new string('═', 72)}");
            if (ret.Count == 0) { Console.WriteLine("  (no trades)"); return; }
            Console.WriteLine($"  Trades:       {ret.Count}  ({wins}W / {ret.Count - wins}L)");
            Console.WriteLine($"  Win rate:     {(double)wins / ret.Count:P1}");
            Console.WriteLine($"  Avg return:   {ret.Average():+0.00}%");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(ret):F2}");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(ret, candleCount):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(ret, candleCount):F2}");
            Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(ret):F2}");
            var port = Simulator.SimulatePortfolio(trades.Select(t => (t.Return, t.Frac)).ToList());
            Console.WriteLine($"  Portfolio sim (€100 · 5% cap):");
            Console.WriteLine($"    End balance:  €{port.EndBalance:F2}  ({(port.EndBalance - port.StartBalance) / port.StartBalance * 100:+0.0;-0.0}%)");
            Console.WriteLine($"    Max drawdown: {port.MaxDrawdownPct:F1}%");
            if (coinRows.Count > 0)
            {
                Console.WriteLine($"\n  Per-coin ({coinRows.Sum(r => r.Trades)} trades · {coinRows.Count} coins · sorted by PF):");
                Console.WriteLine($"  {"Coin",-18} {"Trades",6}  {"WR",5}  {"PF",5}  {"AvgRet%",8}  {"Sz%",4}");
                Console.WriteLine($"  {new string('-', 58)}");
                foreach (var row in coinRows.OrderByDescending(r => r.PF))
                    Console.WriteLine($"  {row.Coin,-18} {row.Trades,6}  {row.WR,5:P0}  {row.PF,5:F2}  {row.AvgRet,+8:F2}%  {row.Frac,4:P0}");
            }
        }

        {
            var btcEntry = fetchedArr.FirstOrDefault(x => x.sym == "BTCUSDT");
            var ethEntry = fetchedArr.FirstOrDefault(x => x.sym == "ETHUSDT");
            if (btcEntry.m15 is { Count: > 200 })
            {
                var btcH1  = FadeShortSimulator.AggregateCandles(btcEntry.m15.ToArray(), 4);
                Candle[]? ethH1 = ethEntry.m15 is { Count: > 200 }
                    ? FadeShortSimulator.AggregateCandles(ethEntry.m15.ToArray(), 4) : null;

                StrategyActivation routing;
                if (routerGeno != null)
                {
                    routing = RegimeRouter.Route(btcH1, routerGeno, ethH1);
                    var btcBar = RegimeClassifier.ClassifySeriesWithDuration(btcH1)[^1];
                    Console.WriteLine($"  Ensemble regime (BTC · last bar): {routing.Regime}  " +
                        $"confidence={routing.Confidence:P0}  duration={btcBar.Duration} bars  [trained router]");
                }
                else
                {
                    routing = RegimeRouter.Route(btcH1, ethH1);
                    Console.WriteLine($"  Ensemble regime (BTC · last bar): {routing.Regime}  " +
                        $"confidence={routing.Confidence:P0}  [rule-based router — run routertrain to improve]");
                }
                Console.WriteLine($"  Strategy routing:  {RegimeRouter.Describe(routing)}\n");
            }
        }

        // Selection-bias diagnostic: unscreened vs screened val
        // The gap shows how much the train-gate inflates reported performance.
        if (fsUnscreenedRet.Count > 0 && fsTrades.Count > 0)
        {
            var screened = fsTrades.Select(t => t.Return).ToList();
            Console.WriteLine($"\n  [BIAS CHECK] FadeShort unscreened val (all {Config.BacktestCoins.Length} coins, no train gate):");
            Console.WriteLine($"    Unscreened — {fsUnscreenedRet.Count} trades  WR={fsUnscreenedRet.Count(r => r > 0) * 100.0 / fsUnscreenedRet.Count:F1}%  AvgRet={fsUnscreenedRet.Average():+0.00}%  PF={Simulator.ProfitFactor(fsUnscreenedRet):F2}");
            Console.WriteLine($"    Screened   — {screened.Count} trades  WR={screened.Count(r => r > 0) * 100.0 / screened.Count:F1}%  AvgRet={screened.Average():+0.00}%  PF={Simulator.ProfitFactor(screened):F2}");
            Console.WriteLine($"    (Screen selects {fsTrades.Select(t => t.Coin).Distinct().Count()} / {Config.BacktestCoins.Length} coins — excess PF vs unscreened = selection bias)");
        }

        PrintStrategySection(
            "FADE SHORT  (overbought fade · own regime gate · screened · last 1yr)",
            fsTrades, fsCoinRows, fsVCC);

        if (gridG != null)
            PrintStrategySection(
                "GRID  (ranging/neutral · coin-screened · last 1yr)",
                gridTrades, gridCoinRows, gridVCC);

        if (dlGeno != null)
            PrintStrategySection(
                $"DIP LONG  (bull pullback · regime ≥{dlGeno.RegimeSustainedBars} bull h1 bars · last 1yr)",
                dlTrades, dlCoinRows, dlVCC);

        if (flGeno != null)
            PrintStrategySection(
                $"FADE LONG  (bear bounce · regime ≥{flGeno.RegimeSustainedBars} bear h1 bars · last 1yr)",
                flTrades, flCoinRows, flVCC);

        bool dlInCombined = dlGeno != null && dlGeno.Fitness > 0;
        bool flInCombined = flGeno != null && flGeno.Fitness > 0;

        var combined = fsTrades
            .Concat(gridTrades)
            .Concat(dlInCombined ? dlTrades : [])
            .Concat(flInCombined ? flTrades : [])
            .OrderBy(t => t.Time)
            .Select(t => (t.Return, t.Frac))
            .ToList();

        var combParts = new List<string> { "FadeShort", "Grid" };
        if (dlInCombined) combParts.Add("DipLong [bull]");
        else if (dlGeno != null) combParts.Add($"DipLong [EXCLUDED F={dlGeno.Fitness:+0.00;-0.00}]");
        if (flInCombined) combParts.Add("FadeLong [bear]");
        else if (flGeno != null) combParts.Add($"FadeLong [EXCLUDED F={flGeno.Fitness:+0.00;-0.00}]");

        if (combined.Count > 0)
        {
            int combWins = combined.Count(t => t.Return > 0);
            var combRet  = combined.Select(t => t.Return).ToList();
            var combPort = Simulator.SimulatePortfolio(combined);
            int combVCC  = new[] { fsVCC, gridVCC, dlInCombined ? dlVCC : 0, flInCombined ? flVCC : 0 }.Max();

            Console.WriteLine($"\n{new string('═', 72)}");
            Console.WriteLine($"  COMBINED  ({string.Join(" + ", combParts)})");
            Console.WriteLine($"{new string('═', 72)}");
            Console.WriteLine($"  Trades: {combined.Count}  ({combWins}W / {combined.Count - combWins}L)" +
                              $"  FS={fsTrades.Count}  Grid={gridTrades.Count}" +
                              (dlInCombined ? $"  DL={dlTrades.Count}" : "") +
                              (flInCombined ? $"  FL={flTrades.Count}" : ""));
            Console.WriteLine($"  Win rate:     {(double)combWins / combined.Count:P1}");
            Console.WriteLine($"  Avg return:   {combRet.Average():+0.00}%");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(combRet):F2}");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(combRet, combVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(combRet, combVCC):F2}");
            Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(combRet):F2}");
            Console.WriteLine($"  Portfolio sim (€100 · fitness-gated · 5% cap per trade):");
            Console.WriteLine($"    End balance:  €{combPort.EndBalance:F2}  ({(combPort.EndBalance - combPort.StartBalance) / combPort.StartBalance * 100:+0.0;-0.0}%)");
            Console.WriteLine($"    Max drawdown: {combPort.MaxDrawdownPct:F1}%");
        }

        if (routerGeno?.Fitness > 0)
        {
            var btcEntry2 = fetchedArr.FirstOrDefault(x => x.sym == "BTCUSDT");
            var ethEntry2 = fetchedArr.FirstOrDefault(x => x.sym == "ETHUSDT");
            if (btcEntry2.m15 is { Count: > 200 })
            {
                var btcH1r  = FadeShortSimulator.AggregateCandles(btcEntry2.m15.ToArray(), 4);
                var ethH1r  = ethEntry2.m15 is { Count: > 200 }
                    ? FadeShortSimulator.AggregateCandles(ethEntry2.m15.ToArray(), 4) : null;
                var btcSerR = RegimeClassifier.ClassifySeriesWithDuration(btcH1r);
                var ethSerR = ethH1r != null ? RegimeClassifier.ClassifySeriesWithDuration(ethH1r) : null;
                var session = new RegimeRouterSession(btcSerR, ethSerR, routerGeno);

                var routed = Enumerable.Empty<(DateTime Time, double Return, double Frac)>()
                    .Concat(fsTrades  .Select(t => (t.Time, t.Return, t.Frac))
                                       .Where(_ => session.IsActive(RegimeRouterGA.StrategyKind.FadeShort, _.Time)))
                    .Concat(gridTrades.Select(t => (t.Time, t.Return, t.Frac))
                                       .Where(_ => session.IsActive(RegimeRouterGA.StrategyKind.Grid,      _.Time)))
                    .Concat((dlInCombined ? dlTrades : []).Select(t => (t.Time, t.Return, t.Frac))
                                       .Where(_ => session.IsActive(RegimeRouterGA.StrategyKind.DipLong,   _.Time)))
                    .Concat((flInCombined ? flTrades : []).Select(t => (t.Time, t.Return, t.Frac))
                                       .Where(_ => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong,  _.Time)))
                    .OrderBy(t => t.Time)
                    .Select(t => (t.Return, t.Frac))
                    .ToList();

                if (routed.Count > 20)
                {
                    int rWins   = routed.Count(t => t.Return > 0);
                    var rRet    = routed.Select(t => t.Return).ToList();
                    var rPort   = Simulator.SimulatePortfolio(routed);
                    int rVCC    = new[] { fsVCC, gridVCC, dlInCombined ? dlVCC : 0, flInCombined ? flVCC : 0 }.Max();

                    Console.WriteLine($"\n{new string('═', 72)}");
                    Console.WriteLine($"  COMBINED  (router-gated time-series · Bull≥{routerGeno.BullMinBars:F0} Bear≥{routerGeno.BearMinBars:F0} bars)");
                    Console.WriteLine($"{new string('═', 72)}");
                    Console.WriteLine($"  Trades: {routed.Count}/{combined.Count} activated  ({rWins}W / {routed.Count - rWins}L)");
                    Console.WriteLine($"  Win rate:     {(double)rWins / routed.Count:P1}");
                    Console.WriteLine($"  Avg return:   {rRet.Average():+0.00}%");
                    Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(rRet):F2}");
                    Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(rRet, rVCC):F2}");
                    Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(rRet, rVCC):F2}");
                    Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(rRet):F2}");
                    Console.WriteLine($"  Portfolio sim (€100 · router-gated · 5% cap per trade):");
                    Console.WriteLine($"    End balance:  €{rPort.EndBalance:F2}  ({(rPort.EndBalance - rPort.StartBalance) / rPort.StartBalance * 100:+0.0;-0.0}%)");
                    Console.WriteLine($"    Max drawdown: {rPort.MaxDrawdownPct:F1}%");
                }
            }
        }
    }

    public static async Task RunYearlyBreakdown(BybitRestClient client)
    {
        Console.WriteLine("=== Gravity-gen2 | YEARLY BREAKDOWN (full history, in-sample 2021-2025) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile))
        { Console.WriteLine("No FadeShort genotype — run train first."); return; }

        var gUniversal = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        var clusterGenos = new Dictionary<CoinCluster, FadeShortGenotype>();
        foreach (CoinCluster cl in Enum.GetValues<CoinCluster>())
        {
            string clFile = CoinClusterHelper.GenoFile(cl);
            clusterGenos[cl] = File.Exists(clFile)
                ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype()
                : gUniversal;
        }

        GridGenotype? gridG = File.Exists(Config.GridGenoFile)
            ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype()
            : null;

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try { var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113); return (sym, m15); }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);
        Console.WriteLine();

        const double RiskBudget = 0.004;
        const double FsHoldHours   = 48;
        const double GridHoldHours = 36;

        var allTrades  = new List<(DateTime Time, double Return, double Conf, double RiskCap)>();
        var gridTrades = new List<(DateTime Time, double Return, double Conf, double RiskCap)>();
        var coinDiag   = new List<(string Sym, int Trades, double WL, double RC, double FullKPos)>();

        foreach (var (sym, m15List) in fetched)
        {
            if (m15List.Count < 600) continue;
            var m15 = m15List.ToArray();
            var h1  = FadeShortSimulator.AggregateCandles(m15, 4);

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) continue;

            var coinCluster = CoinClusterHelper.Classify(h1);
            var g = clusterGenos[coinCluster];

            int trainEnd = (int)(h1.Length * 0.75);
            var trainRet = FadeShortSimulator.GetFadeShortReturns(g, h1[..trainEnd], m15[..(trainEnd * 4)])
                               .Select(t => t.Return).ToList();
            if (trainRet.Count < 10) continue;
            if (trainRet.Average() <= 0 || Simulator.ProfitFactor(trainRet) < 1.2) continue;
            double conf = Simulator.ComputeConfidence(trainRet);

            var fullTrades = FadeShortSimulator.GetFadeShortReturns(g, h1, m15);
            var losses = fullTrades.Where(t => t.Return < 0)
                                   .Select(t => Math.Abs(t.Return) / 100.0)
                                   .OrderBy(x => x).ToList();
            double worstLoss = losses.Count >= 5
                ? losses[Math.Min(losses.Count - 1, (int)(losses.Count * 0.99))]
                : 0.05;
            worstLoss = Math.Max(worstLoss, 0.005);
            double riskCap = RiskBudget / worstLoss;

            coinDiag.Add((sym, fullTrades.Count, worstLoss, riskCap, Math.Min(conf * 2.0, riskCap)));

            foreach (var (t, ret, _, _, _) in fullTrades)
                allTrades.Add((t, ret, conf, riskCap));

            if (gridG != null)
            {
                var gtTrain = GridSimulator.GetGridReturns(gridG, h1[..trainEnd]).Select(t => t.Return).ToList();
                if (gtTrain.Count >= 5 && gtTrain.Average() > 0 && Simulator.SortinoRatio(gtTrain, trainEnd) >= 0.3 && Simulator.ProfitFactor(gtTrain) >= 1.2)
                {
                    double gConf = Simulator.ComputeConfidence(gtTrain);
                    var gLoss = GridSimulator.GetGridReturns(gridG, h1)
                        .Where(t => t.Return < 0).Select(t => Math.Abs(t.Return) / 100.0).OrderBy(x => x).ToList();
                    double gWorstLoss = gLoss.Count >= 5
                        ? gLoss[Math.Min(gLoss.Count - 1, (int)(gLoss.Count * 0.99))] : 0.05;
                    gWorstLoss = Math.Max(gWorstLoss, 0.005);
                    double gRiskCap = RiskBudget / gWorstLoss;
                    foreach (var (t, ret, _, _, _) in GridSimulator.GetGridReturns(gridG, h1))
                        gridTrades.Add((t, ret, gConf, gRiskCap));
                }
            }
        }

        if (allTrades.Count == 0) { Console.WriteLine("No trades."); return; }

        var years     = allTrades.Select(t => t.Time.Year)
                        .Concat(gridTrades.Select(t => t.Time.Year))
                        .Distinct().OrderBy(y => y).ToList();
        var fullYears = years.Where(y => y >= 2022 && y <= 2025).ToList();
        var fsHold    = TimeSpan.FromHours(FsHoldHours);
        var gridHold  = TimeSpan.FromHours(GridHoldHours);

        Console.WriteLine($"  {"Year",-6}  {"Trades",6}  {"½-K seq ret",11}  {"½-K seq DD",10}  {"½-K live ret",15}  {"½-K live DD",14}");
        Console.WriteLine($"  {new string('-', 78)}");

        foreach (int yr in years)
        {
            var yrSeq = allTrades.Where(t => t.Time.Year == yr).Select(t => (t.Return, t.Conf))
                .Concat(gridTrades.Where(t => t.Time.Year == yr).Select(t => (t.Return, t.Conf))).ToList();

            var yrLive = allTrades.Where(t => t.Time.Year == yr)
                .Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, fsHold))
                .Concat(gridTrades.Where(t => t.Time.Year == yr)
                    .Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, gridHold)))
                .OrderBy(t => t.Time).ToList();

            var seqPort  = Simulator.SimulatePortfolio(yrSeq, startBalance: 100.0, maxPositionPct: 0.05);
            var livePort = Simulator.SimulatePortfolioExposureCapped(yrLive, startBalance: 100.0,
                maxTotalExposurePct: 0.30, drawdownBrakeAt: 0.15, kellyMultiplier: 1.0);

            int n = yrSeq.Count;
            Console.WriteLine($"  {yr,-6}  {n,6}  {seqPort.EndBalance-100,+10:F1}%  {seqPort.MaxDrawdownPct,9:F1}%  {livePort.EndBalance-100,+14:F1}%  {livePort.MaxDrawdownPct,13:F1}%");
        }

        Console.WriteLine($"  {new string('-', 78)}");

        var allLive = allTrades.Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, fsHold))
            .Concat(gridTrades.Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, gridHold)))
            .OrderBy(t => t.Time).ToList();
        var allSeq = allTrades.Select(t => (t.Return, t.Conf))
            .Concat(gridTrades.Select(t => (t.Return, t.Conf))).ToList();

        Console.WriteLine($"\n{new string('═', 72)}");
        Console.WriteLine($"  SIZING SCENARIOS  (full 2021–2026, in-sample)");
        Console.WriteLine($"  Concurrent = overlapping positions tracked; brake = size→20% floor at 15% DD");
        Console.WriteLine($"  RiskCap = position capped so worst stop-out ≤ 0.4% of portfolio");
        Console.WriteLine($"{new string('═', 72)}");
        Console.WriteLine($"  {"Scenario",-38}  {"Total",6}  {"Max DD",7}  {"Worst yr",9}  {"Best yr",8}");
        Console.WriteLine($"  {new string('-', 72)}");

        void PrintScenario(string label,
            Func<List<(double Return, double Conf)>, Simulator.PortfolioResult> seqFn,
            Func<List<(DateTime Time, double Return, double Conf, double RiskCap, TimeSpan Hold)>, Simulator.PortfolioResult>? liveFn)
        {
            Simulator.PortfolioResult full = liveFn != null ? liveFn(allLive) : seqFn(allSeq);
            var yrRets = fullYears.Select(yr =>
            {
                var ys = allTrades.Where(t => t.Time.Year == yr).Select(t => (t.Return, t.Conf))
                    .Concat(gridTrades.Where(t => t.Time.Year == yr).Select(t => (t.Return, t.Conf))).ToList();
                var yl = allTrades.Where(t => t.Time.Year == yr)
                    .Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, fsHold))
                    .Concat(gridTrades.Where(t => t.Time.Year == yr)
                        .Select(t => (t.Time, t.Return, t.Conf, t.RiskCap, gridHold)))
                    .OrderBy(t => t.Time).ToList();
                return liveFn != null ? liveFn(yl).EndBalance - 100.0 : seqFn(ys).EndBalance - 100.0;
            }).ToList();
            double worst = yrRets.Count > 0 ? yrRets.Min() : 0;
            double best  = yrRets.Count > 0 ? yrRets.Max() : 0;
            Console.WriteLine($"  {label,-38}  {full.EndBalance-100,+5:F1}%  {full.MaxDrawdownPct,6:F1}%  {worst,+8:F1}%  {best,+7:F1}%");
        }

        PrintScenario("½-K  seq  5%cap  no-brake  [current]",
            t => Simulator.SimulatePortfolio(t, maxPositionPct: 0.05), null);

        PrintScenario("½-K  concurrent  brake@15%",
            _ => default!, t => Simulator.SimulatePortfolioExposureCapped(t,
                drawdownBrakeAt: 0.15, kellyMultiplier: 1.0));

        PrintScenario("Full-K  concurrent  brake@15%  no-riskcap",
            _ => default!, t => Simulator.SimulatePortfolioExposureCapped(
                t.Select(x => (x.Time, x.Return, x.Conf, x.Hold)).ToList(),
                drawdownBrakeAt: 0.15, kellyMultiplier: 2.0));

        PrintScenario("½-K  concurrent  30%cap  brake@15%  riskcap0.4%  [live]",
            _ => default!, t => Simulator.SimulatePortfolioExposureCapped(t,
                maxTotalExposurePct: 0.30, drawdownBrakeAt: 0.15, kellyMultiplier: 1.0));

        Console.WriteLine($"\n  RiskCap per coin: worst 1% of stop-outs limits position so max loss = 0.4% of portfolio.");
        Console.WriteLine($"  Concurrent: positions overlap in time (48h avg FS hold, 36h avg grid hold).");
        Console.WriteLine($"  In-sample: 2021–2025 trained. OOS test = Oct 2025–Jun 2026.");

        Console.WriteLine($"\n  {"Coin",-10}  {"FS trades",9}  {"WorstLoss",9}  {"RiskCap",8}  {"FullK pos",9}  {"Cap binds?",10}");
        Console.WriteLine($"  {new string('-', 66)}");
        foreach (var (sym, trades, wl, rc, fkPos) in coinDiag.OrderBy(d => d.Sym))
            Console.WriteLine($"  {sym,-10}  {trades,9}  {wl*100,8:F1}%  {rc*100,7:F1}%  {fkPos*100,8:F1}%  {(rc < fkPos * 1.001 ? "YES — cap binds" : "no"),10}");
    }

    public static async Task RunTest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | STATISTICAL TEST ({Config.BacktestCoins.Length} coins · last 1 year · all strategies) ===\n");

        FadeShortGenotype? sg     = null;
        GridGenotype?      gg     = null;
        DipLongGenotype?   dlGeno = null;
        FadeLongGenotype?  flGeno = null;

        if (File.Exists(Config.FadeShortGenoFile))
            sg = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        if (File.Exists(Config.GridGenoFile))
            gg = JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype();
        if (File.Exists(Config.DipLongGenoFile))
            dlGeno = JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype();
        if (File.Exists(Config.FadeLongGenoFile))
            flGeno = JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype();

        if (sg == null && gg == null && dlGeno == null && flGeno == null)
        { Console.WriteLine("No genotypes found."); return; }

        var clusterGenos = new Dictionary<CoinCluster, FadeShortGenotype>();
        if (sg != null)
        {
            foreach (CoinCluster cl in Enum.GetValues<CoinCluster>())
            {
                string clFile = CoinClusterHelper.GenoFile(cl);
                clusterGenos[cl] = File.Exists(clFile)
                    ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(clFile))!.ToGenotype()
                    : sg;
            }
        }

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m candles, reading from cache)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try { return (sym, await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113)); }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);
        Console.WriteLine();

        var swingRet = new List<double>();
        var gridRet  = new List<double>();
        var dlRet    = new List<double>();
        var flRet    = new List<double>();
        int totalCandleCount = 0;

        var crashTrades = new List<(DateTime Open, DateTime Close, double Return, double HalfKelly, string Strategy)>();

        foreach (var (sym, m15List) in fetched)
        {
            if (m15List.Count < 600) continue;
            var m15 = m15List.ToArray();
            var h1  = FadeShortSimulator.AggregateCandles(m15, 4);

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            if (volUsd.Count == 0 || volUsd[volUsd.Count / 2] < Config.MinMedianVolUsdM) continue;

            int yearStart    = Math.Max(0, h1.Length - 8760);
            int m15YearStart = yearStart * 4;
            var h1Year   = h1[yearStart..];
            var m15Year  = m15[m15YearStart..];
            var h1Train  = h1[..yearStart];
            var m15Train = m15[..m15YearStart];

            if (h1Year.Length < 100) continue;
            totalCandleCount += h1Year.Length * 12;

            if (sg != null && h1Train.Length >= 100)
            {
                var coinCluster = CoinClusterHelper.Classify(h1);
                var fsG       = clusterGenos[coinCluster];
                var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
                var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
                var fsTr = FadeShortSimulator.GetFadeShortReturns(fsG, screenH1, screenM15).Select(t => t.Return).ToList();
                if (fsTr.Count >= 20 && fsTr.Average() > 0
                    && Simulator.ProfitFactor(fsTr) >= 1.2
                    && Simulator.SortinoRatio(fsTr, screenH1.Length * 12) >= 0.3)
                {
                    var yearTrades = FadeShortSimulator.GetFadeShortReturns(fsG, h1Year, m15Year);
                    var (_, hk)    = StrategyStats.KellyFraction(fsTr);
                    double fsHk    = Math.Min(hk, 0.05);
                    swingRet.AddRange(yearTrades.Select(t => t.Return));
                    foreach (var (t, ret, _, _, _) in yearTrades)
                        crashTrades.Add((t - TimeSpan.FromHours(sg.MaxHoldCandles), t, ret, fsHk, "FadeShort"));
                }
            }

            if (gg != null && h1Train.Length >= 100)
            {
                var gTr = GridSimulator.GetGridReturns(gg, h1Train).Select(t => t.Return).ToList();
                if (gTr.Count >= 20 && gTr.Average() > 0
                    && Simulator.ProfitFactor(gTr) >= 1.2
                    && Simulator.SortinoRatio(gTr, h1Train.Length) >= 0.3)
                {
                    var yearTrades = GridSimulator.GetGridReturns(gg, h1Year);
                    var (_, hk)    = StrategyStats.KellyFraction(gTr);
                    double gHk     = Math.Min(hk, 0.05);
                    gridRet.AddRange(yearTrades.Select(t => t.Return));
                    foreach (var (t, ret, _, _, _) in yearTrades)
                        crashTrades.Add((t - TimeSpan.FromHours(gg.MaxHoldCandles), t, ret, gHk, "Grid"));
                }
            }

            if (dlGeno != null && h1Year.Length >= 200)
            {
                var raw = DipLongSimulator.GetDipLongReturns(dlGeno, h1Year, m15Year);
                var filtered = raw.Where(t => t.RegimeBarsActive >= dlGeno.RegimeSustainedBars).ToList();
                dlRet.AddRange(filtered.Select(t => t.Return));
                double dlHk = Math.Min(dlGeno.PositionSizePct, 0.05);
                foreach (var t in filtered)
                    crashTrades.Add((t.Time - TimeSpan.FromHours(dlGeno.MaxHoldCandles), t.Time, t.Return, dlHk, "DipLong"));
            }

            if (flGeno != null && h1Year.Length >= 200)
            {
                var raw = FadeLongSimulator.GetFadeLongReturns(flGeno, h1Year, m15Year);
                var filtered = raw.Where(t => t.RegimeBarsActive >= flGeno.RegimeSustainedBars).ToList();
                flRet.AddRange(filtered.Select(t => t.Return));
                double flHk = Math.Min(flGeno.PositionSizePct, 0.05);
                foreach (var t in filtered)
                    crashTrades.Add((t.Time - TimeSpan.FromHours(flGeno.MaxHoldCandles), t.Time, t.Return, flHk, "FadeLong"));
            }
        }

        int candleCount = totalCandleCount;

        if (sg != null)
        {
            Console.WriteLine($"FadeShort genotype: {sg}");
            StrategyStats.Report("FadeShort", swingRet, candleCount);
        }
        if (gg != null)
        {
            Console.WriteLine($"\nGrid genotype: {gg}");
            StrategyStats.Report("Grid", gridRet, candleCount);
        }
        if (dlGeno != null)
        {
            Console.WriteLine($"\nDipLong genotype: {dlGeno}");
            StrategyStats.Report("DipLong", dlRet, candleCount);
        }
        if (flGeno != null)
        {
            Console.WriteLine($"\nFadeLong genotype: {flGeno}");
            StrategyStats.Report("FadeLong", flRet, candleCount);
        }

        if (sg != null && gg != null && swingRet.Count >= 5 && gridRet.Count >= 5)
            StrategyStats.Compare("FadeShort", swingRet, "Grid", gridRet, candleCount);
        if (dlGeno != null && flGeno != null && dlRet.Count >= 5 && flRet.Count >= 5)
            StrategyStats.Compare("DipLong", dlRet, "FadeLong", flRet, candleCount);
        if (sg != null && dlGeno != null && swingRet.Count >= 5 && dlRet.Count >= 5)
            StrategyStats.Compare("FadeShort", swingRet, "DipLong", dlRet, candleCount);

        Console.WriteLine($"\n  Crash/rally analysis — {crashTrades.Count} trades across all strategies (last 1yr)");

        Console.WriteLine("  Fetching BTCUSDT for crash detection...");
        var btcM15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, "BTCUSDT", batches: 113);
        var btcH1  = FadeShortSimulator.AggregateCandles(btcM15.ToArray(), 4);
        var btcYear = btcH1[Math.Max(0, btcH1.Length - 8760)..];

        var crashes = CrashAnalyser.DetectCrashes(btcYear);
        CrashAnalyser.Report(crashes, crashTrades);

        var rallies = CrashAnalyser.DetectRallies(btcYear);
        CrashAnalyser.ReportRallies(rallies, crashTrades);

        CrashAnalyser.SyntheticWorstCase(crashTrades);
    }
}
