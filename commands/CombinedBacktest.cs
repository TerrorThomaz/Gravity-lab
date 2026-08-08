using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class CombinedBacktest
{
    // ── Variant loading helper ────────────────────────────────────────────────
    // Scans genotypes/ for all files matching {strategyKey}_*_genotype.json and the
    // plain {strategyKey}_genotype.json default.  Builds a VariantSpec<TG>[] array
    // with one entry per file found (AtrLow/AtrHigh come from the DTO).
    static VariantSpec<TG>[] LoadVariants<TDto, TG>(
        string strategyKey,
        Func<TDto, TG> toGenotype,
        Func<TDto, (double Low, double High)> getRange)
        where TG : class
    {
        var files = Directory.Exists("genotypes")
            ? Directory.GetFiles("genotypes", $"{strategyKey}_*_genotype.json")
            : Array.Empty<string>();
        string defaultFile = $"genotypes/{strategyKey}_genotype.json";
        if (File.Exists(defaultFile))
            files = files.Append(defaultFile).Distinct().ToArray();
        if (files.Length == 0) return Array.Empty<VariantSpec<TG>>();
        return files.Select(f =>
        {
            var dto = JsonSerializer.Deserialize<TDto>(File.ReadAllText(f))!;
            var (lo, hi) = getRange(dto);
            string variantId = Path.GetFileNameWithoutExtension(f)
                .Replace($"{strategyKey}_", "").Replace("_genotype", "");
            return new VariantSpec<TG>(variantId, lo, hi, toGenotype(dto));
        }).ToArray();
    }

    static TG? SelectVariant<TG>(VariantSpec<TG>[] variants, Candle[] m15)
        where TG : class
    {
        if (variants.Length == 0) return null;
        double[] highs  = m15.Select(c => c.High).ToArray();
        double[] lows   = m15.Select(c => c.Low).ToArray();
        double[] closes = m15.Select(c => c.Close).ToArray();
        double[] atr    = Volatility.Atr(highs, lows, closes, 14);
        int      bar    = atr.Length - 1;
        return VariantRouter.Select(atr, bar, variants) ?? variants[0].Genotype;
    }

    static (TG? Genotype, string Label) SelectVariantLabeled<TG>(VariantSpec<TG>[] variants, Candle[] m15)
        where TG : class
    {
        if (variants.Length == 0) return (null, "base");
        double[] highs  = m15.Select(c => c.High).ToArray();
        double[] lows   = m15.Select(c => c.Low).ToArray();
        double[] closes = m15.Select(c => c.Close).ToArray();
        double[] atr    = Volatility.Atr(highs, lows, closes, 14);
        int      bar    = atr.Length - 1;
        if (bar < 100 || atr.Length <= bar)
            return (variants[0].Genotype, LabelForVariant(variants[0]));
        double baseline = 0;
        for (int j = bar - 100; j < bar; j++) baseline += atr[j];
        baseline /= 100;
        if (baseline < 1e-10)
            return (variants[0].Genotype, LabelForVariant(variants[0]));
        double ratio = atr[bar] / baseline;
        VariantSpec<TG>? best = null;
        double bestWidth = double.MaxValue;
        foreach (var v in variants)
        {
            if (v.Genotype == null) continue;
            if (ratio < v.AtrLow || ratio >= v.AtrHigh) continue;
            double width = v.AtrHigh - v.AtrLow;
            if (width < bestWidth) { bestWidth = width; best = v; }
        }
        var selected = best ?? variants[0];
        return (selected.Genotype, LabelForVariant(selected));
    }

    static string LabelForVariant<T>(VariantSpec<T> v) where T : class
    {
        if (v.AtrLow >= 1.0) return "highvol";
        if (v.AtrHigh <= 1.0) return "lowvol";
        return "base";
    }

    public static async Task RunCombinedBacktest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | COMBINED BACKTEST (all strategies, router-gated, {Config.BacktestCoins.Length} coins, val 20%) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine($"Missing FadeShort genotype — run 'train' first.");     return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine($"Missing grid genotype — run 'gridtrain' first."); return; }

        // Load variant arrays (currently one entry each; infrastructure ready for multi-variant)
        var fsVariants = LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var gridVariants = LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_best", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var flVariants = LoadVariants<FadeLongGenotypeDto, FadeLongGenotype>(
            "fade_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var dlVariants = LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var slVariants = LoadVariants<SwingLongGenotypeDto, SwingLongGenotype>(
            "swing_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var rsVariants = LoadVariants<RipShortGenotypeDto, RipShortGenotype>(
            "rip_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

        // Load high-vol variants (ATR ratio > 1.5)
        var fsHvVariants = LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short_highvol", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var dlHvVariants = LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long_highvol", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var slHvVariants = LoadVariants<SwingLongGenotypeDto, SwingLongGenotype>(
            "swing_long_highvol", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var rsHvVariants = LoadVariants<RipShortGenotypeDto, RipShortGenotype>(
            "rip_short_highvol", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

        // Representative single genotypes (for portfolio hold-time calcs and logging)
        var swingG = fsVariants.Length > 0 ? fsVariants[0].Genotype! : null;
        var gridG  = gridVariants.Length > 0 ? gridVariants[0].Genotype! : null;
        if (swingG == null) { Console.WriteLine("Missing FadeShort genotype — run 'train' first."); return; }
        if (gridG  == null) { Console.WriteLine("Missing grid genotype — run 'gridtrain' first.");  return; }

        FadeLongGenotype?     flG     = null;
        DipLongGenotype?      dlG     = dlVariants.Length > 0  ? dlVariants[0].Genotype  : null;
        SwingLongGenotype?    slG     = slVariants.Length > 0  ? slVariants[0].Genotype  : null;
        RipShortGenotype?     rsG     = rsVariants.Length > 0  ? rsVariants[0].Genotype  : null;
        RegimeRouterGenotype? routerG = File.Exists(Config.RouterGenoFile)    ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype() : null;

        // High-vol genotypes
        FadeShortGenotype?  fsHvG = fsHvVariants.Length > 0 ? fsHvVariants[0].Genotype : null;
        DipLongGenotype?    dlHvG = dlHvVariants.Length > 0 ? dlHvVariants[0].Genotype : null;
        SwingLongGenotype?  slHvG = slHvVariants.Length > 0 ? slHvVariants[0].Genotype : null;
        RipShortGenotype?   rsHvG = rsHvVariants.Length > 0 ? rsHvVariants[0].Genotype : null;

        GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype? agBullG = null;
        GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype? agBearG = null;
        if (File.Exists("genotypes/accumulation_grid_genotype.json"))
        {
            var agJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText("genotypes/accumulation_grid_genotype.json"));
            agBullG = JsonSerializer.Deserialize<GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype>(agJson.GetProperty("Bull").GetRawText());
            agBearG = JsonSerializer.Deserialize<GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype>(agJson.GetProperty("Bear").GetRawText());
        }

        Console.WriteLine($"FadeShort: {swingG}  [{fsVariants.Length} variant(s)]");
        Console.WriteLine($"Grid:      {gridG}  [{gridVariants.Length} variant(s)]");
        Console.WriteLine("FadeLong:  disabled (PF=0.06 OOS, net drag — see FullTest.cs)");
        if (dlG     != null) Console.WriteLine($"DipLong:   {dlG}  [{dlVariants.Length} variant(s)]");
        else                 Console.WriteLine("DipLong:   not found — skipping");
        if (slG     != null) Console.WriteLine($"SwingLong: {slG}  [{slVariants.Length} variant(s)]");
        else                 Console.WriteLine("SwingLong: not found — skipping");
        if (rsG     != null) Console.WriteLine($"RipShort:  {rsG}{(rsG.Fitness < 0 ? " ⚠ negative fitness" : "")}  [{rsVariants.Length} variant(s)]");
        else                 Console.WriteLine("RipShort:  not found — skipping");
        if (agBullG != null && agBearG != null)
        {
            Console.WriteLine($"AccumGrid: Bull={agBullG}  Bear={agBearG}");
        }
        else                 Console.WriteLine("AccumGrid: not found — skipping");
        if (routerG != null) Console.WriteLine($"Router:    {routerG}");
        else                 Console.WriteLine("Router:    not found — strategies run without regime gate");
        if (fsHvG != null) Console.WriteLine($"FadeShort-HV: {fsHvG}  [{fsHvVariants.Length} variant(s)]");
        if (dlHvG != null) Console.WriteLine($"DipLong-HV:   {dlHvG}  [{dlHvVariants.Length} variant(s)]");
        if (slHvG != null) Console.WriteLine($"SwingLong-HV: {slHvG}  [{slHvVariants.Length} variant(s)]");
        if (rsHvG != null) Console.WriteLine($"RipShort-HV:  {rsHvG}  [{rsHvVariants.Length} variant(s)]");
        Console.WriteLine();

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} coins (15m → 1h, ~3yr)...");
        var sem = new SemaphoreSlim(4);
        var fetchTasks = Config.BacktestCoins.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: 113);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                return (sym, m15: m15.ToArray(), h1);
            }
            finally { sem.Release(); }
        });
        var fetched = await Task.WhenAll(fetchTasks);
        Console.WriteLine($"  Done.\n");

        RegimeRouterSession? session  = null;
        RegimeBar[]?         btcRegimeSeries = null;
        if (routerG != null)
        {
            var btcEntry = fetched.FirstOrDefault(f => f.sym == "BTCUSDT");
            if (btcEntry.h1 != null && btcEntry.h1.Length >= 200)
            {
                btcRegimeSeries   = RegimeClassifier.ClassifySeriesWithDuration(btcEntry.h1);
                var ethEntry      = fetched.FirstOrDefault(f => f.sym == "ETHUSDT");
                RegimeBar[]? ethSeries = ethEntry.h1 != null && ethEntry.h1.Length >= 200
                    ? RegimeClassifier.ClassifySeriesWithDuration(ethEntry.h1) : null;
                session = new RegimeRouterSession(btcRegimeSeries, ethSeries, routerG);
                Console.WriteLine($"  Router session: BTC {btcRegimeSeries.Length} bars  ETH {(ethSeries != null ? ethSeries.Length.ToString() : "none")} bars\n");
            }
            else Console.WriteLine("  ⚠ BTC data insufficient for regime session — router gate disabled\n");
        }

        var swingTrades       = new List<(DateTime Time, double Return, double Conf)>();
        var gridTrades        = new List<(DateTime Time, double Return, double Conf)>();
        var flTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var dlTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var slTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var rsTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var agTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var allTrades         = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var allTradesNoRouter = new List<(DateTime Time, double Return, double Conf, string Strategy)>();

        var swingCoinStats = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var gridCoinStats  = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var flCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var dlCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var slCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var rsCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var agCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();

        int swingTotalVCC = 0;
        int gridTotalVCC  = 0;
        int flTotalVCC    = 0;
        int dlTotalVCC    = 0;
        int slTotalVCC    = 0;
        int rsTotalVCC    = 0;
        int agTotalVCC    = 0;

        var swingFullCoins = new List<(string Sym, Candle[] H1, Candle[] M15, double Conf)>();
        var gridFullCoins  = new List<(string Sym, Candle[] H1, double Conf)>();
        var flFullCoins    = new List<(string Sym, Candle[] H1, Candle[] M15, double Conf)>();
        var dlFullCoins    = new List<(string Sym, Candle[] H1, Candle[] M15, double Conf)>();
        var slFullCoins    = new List<(string Sym, Candle[] H1, Candle[] M15, double Conf)>();
        var rsFullCoins    = new List<(string Sym, Candle[] H1, Candle[] M15, double Conf)>();

        var highVolTrades  = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var lowVolTrades   = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var highVolCoinRets = new Dictionary<string, List<double>>();
        var lowVolCoinRets  = new Dictionary<string, List<double>>();
        int highVolTotalVCC = 0;
        int lowVolTotalVCC  = 0;

        void RouteVolVariant(string label, string sym, DateTime time, double ret, double conf, string strategy, int vcc)
        {
            if (label == "highvol")
            {
                highVolTrades.Add((time, ret, conf, strategy));
                if (!highVolCoinRets.ContainsKey(sym)) highVolCoinRets[sym] = new List<double>();
                highVolCoinRets[sym].Add(ret);
                highVolTotalVCC = Math.Max(highVolTotalVCC, vcc);
            }
            else if (label == "lowvol")
            {
                lowVolTrades.Add((time, ret, conf, strategy));
                if (!lowVolCoinRets.ContainsKey(sym)) lowVolCoinRets[sym] = new List<double>();
                lowVolCoinRets[sym].Add(ret);
                lowVolTotalVCC = Math.Max(lowVolTotalVCC, vcc);
            }
        }

        // Per-coin val returns for statistical tests (router-gated where applicable).
        var swingCoinRet = new List<(string Label, List<double> Returns)>();
        var gridCoinRet  = new List<(string Label, List<double> Returns)>();
        var flCoinRet    = new List<(string Label, List<double> Returns)>();
        var dlCoinRet    = new List<(string Label, List<double> Returns)>();
        var slCoinRet    = new List<(string Label, List<double> Returns)>();
        var rsCoinRet    = new List<(string Label, List<double> Returns)>();
        var agCoinRet    = new List<(string Label, List<double> Returns)>();

        Console.WriteLine($"══ SWING (1h setup + 15m exec) ══════════════════════════════════════════════");
        Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine(new string('-', 82));

        foreach (var (sym, m15, h1) in fetched)
        {
            if (h1.Length < 300) { Console.WriteLine($"  {sym,-16}  skip (no data)"); continue; }

            {
                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM)
                {
                    Console.WriteLine($"  {sym,-16}  skip (vol=${medVol:F2}M/h < ${Config.MinMedianVolUsdM:F1}M)");
                    continue;
                }
            }

            int h1Split  = (int)(h1.Length * 0.8);
            int m15Split = h1Split * 4;
            var h1Train  = h1[..h1Split];
            var h1Val    = h1[h1Split..];
            var m15Train = m15[..m15Split];
            var m15Val   = m15[m15Split..];

            var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
            var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
            var (coinFsG, fsVarLabel) = SelectVariantLabeled(fsVariants, m15);
            coinFsG ??= swingG;
            var tRet  = FadeShortSimulator.GetFadeShortReturns(coinFsG, screenH1, screenM15).Select(t => t.Return).ToList();
            double tExp  = tRet.Count >= 20 ? tRet.Average() : double.NegativeInfinity;
            double tSort = tRet.Count >= 20 ? Simulator.SortinoRatio(tRet, screenH1.Length * 12) : double.NegativeInfinity;
            double tPF   = tRet.Count >= 20 ? Simulator.ProfitFactor(tRet) : 0;
            if (tExp <= 0 || tSort < 0.3 || tPF < 1.2)
            {
                Console.WriteLine($"  {sym,-16}  skip (exp={tExp:+0.00;-0.00}% sort={tSort:F2} pf={tPF:F2})");
                continue;
            }

            double conf   = Simulator.ComputeConfidence(tRet);
            var    vSwing = FadeShortSimulator.GetFadeShortReturns(coinFsG, h1Val, m15Val);
            var    vRet   = vSwing.Select(t => t.Return).ToList();
            int    vCC    = h1Val.Length * 12;
            swingTotalVCC += vCC;

            double sh   = Simulator.SharpeRatio(vRet, vCC);
            double sort = Simulator.SortinoRatio(vRet, vCC);
            double pf   = Simulator.ProfitFactor(vRet);
            double wr   = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
            double avg  = vRet.Count > 0 ? vRet.Average() : 0;

            Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
            swingCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
            swingCoinRet.Add((sym, vRet));
            swingFullCoins.Add((sym, h1, m15, conf));
            foreach (var (t, ret, _) in vSwing)
            {
                swingTrades.Add((t, ret, conf));
                allTrades.Add((t, ret, conf, "swing"));
                allTradesNoRouter.Add((t, ret, conf, "swing"));
                RouteVolVariant(fsVarLabel, sym, t, ret, conf, "swing", vCC);
            }
        }

        Console.WriteLine($"\n══ GRID (1h candles, ranging-market long) ════════════════════════════════════");
        Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine(new string('-', 82));

        foreach (var (sym, m15Grid, h1) in fetched)
        {
            if (h1.Length < 300) { Console.WriteLine($"  {sym,-16}  skip (no data)"); continue; }

            {
                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM)
                {
                    Console.WriteLine($"  {sym,-16}  skip (vol=${medVol:F2}M/h < ${Config.MinMedianVolUsdM:F1}M)");
                    continue;
                }
            }

            int split   = (int)(h1.Length * 0.8);
            var h1Train = h1[..split];
            var h1Val   = h1[split..];

            var (coinGridG, gridVarLabel) = SelectVariantLabeled(gridVariants, m15Grid);
            coinGridG ??= gridG;
            var tRet  = GridSimulator.GetGridReturns(coinGridG, h1Train).Select(t => t.Return).ToList();
            double tExp  = tRet.Count >= 20 ? tRet.Average()               : double.NegativeInfinity;
            double tPF   = tRet.Count >= 20 ? Simulator.ProfitFactor(tRet)  : 0;
            double tSort = tRet.Count >= 20 ? Simulator.SortinoRatio(tRet, h1Train.Length)  : double.NegativeInfinity;
            if (tExp <= 0 || tSort < 0.3 || tPF < 1.2)
            {
                Console.WriteLine($"  {sym,-16}  skip (exp={tExp:+0.00;-0.00}% pf={tPF:F2} sort={tSort:F2})");
                continue;
            }

            double conf  = Simulator.ComputeConfidence(tRet);
            var    vGrid = GridSimulator.GetGridReturns(coinGridG, h1Val);
            var    vRet  = vGrid.Select(t => t.Return).ToList();
            int    vCC   = h1Val.Length * 12;
            gridTotalVCC += vCC;

            double sh   = Simulator.SharpeRatio(vRet, vCC);
            double sort = Simulator.SortinoRatio(vRet, vCC);
            double pf   = Simulator.ProfitFactor(vRet);
            double wr   = vRet.Count > 0 ? (double)vRet.Count(r => r > 0) / vRet.Count : 0;
            double avg  = vRet.Count > 0 ? vRet.Average() : 0;

            Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
            gridCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
            gridFullCoins.Add((sym, h1, conf));
            var gridGated = new List<double>();
            foreach (var (t, ret, _) in vGrid)
            {
                allTradesNoRouter.Add((t, ret, conf, "grid"));
                if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.Grid, t)) continue;
                gridTrades.Add((t, ret, conf));
                allTrades.Add((t, ret, conf, "grid"));
                gridGated.Add(ret);
                RouteVolVariant(gridVarLabel, sym, t, ret, conf, "grid", vCC);
            }
            gridCoinRet.Add((sym, gridGated));
        }

        if (flG != null)
        {
            Console.WriteLine($"\n══ FADELONG (bear-regime bounce, router-gated) ═══════════════════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Gated",6}");
            Console.WriteLine(new string('-', 90));

            foreach (var (sym, m15, h1) in fetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) { continue; }

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM) continue;

                int h1Split  = (int)(h1.Length * 0.8);
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                flTotalVCC += vCC;

                var (coinFlG, flVarLabel) = SelectVariantLabeled(flVariants, m15);
                coinFlG ??= flG;
                var raw    = FadeLongSimulator.GetFadeLongReturns(coinFlG, h1Val, m15Val);
                var gated  = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList()
                    : raw;
                var vRet   = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)");
                    continue;
                }

                double conf = Simulator.ComputeConfidence(vRet);
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%  -{gatedOut,4}");
                flCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                flCoinRet.Add((sym, vRet));
                flFullCoins.Add((sym, h1, m15, conf));
                foreach (var t in raw)
                    allTradesNoRouter.Add((t.Time, t.Return, conf, "fadelong"));
                foreach (var t in gated)
                {
                    flTrades.Add((t.Time, t.Return, conf));
                    allTrades.Add((t.Time, t.Return, conf, "fadelong"));
                    RouteVolVariant(flVarLabel, sym, t.Time, t.Return, conf, "fadelong", vCC);
                }
            }
        }

        if (dlG != null)
        {
            Console.WriteLine($"\n══ DIPLONG (bull-regime pullback, router-gated) ══════════════════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Gated",6}");
            Console.WriteLine(new string('-', 90));

            foreach (var (sym, m15, h1) in fetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM) continue;

                int h1Split  = (int)(h1.Length * 0.8);
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                dlTotalVCC += vCC;

                var (coinDlG, dlVarLabel) = SelectVariantLabeled(dlVariants, m15);
                coinDlG ??= dlG;
                var raw   = DipLongSimulator.GetDipLongReturns(coinDlG, h1Val, m15Val);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)");
                    continue;
                }

                double conf = Simulator.ComputeConfidence(vRet);
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%  -{gatedOut,4}");
                dlCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                dlCoinRet.Add((sym, vRet));
                dlFullCoins.Add((sym, h1, m15, conf));
                foreach (var t in raw)
                    allTradesNoRouter.Add((t.Time, t.Return, conf, "diplong"));
                foreach (var t in gated)
                {
                    dlTrades.Add((t.Time, t.Return, conf));
                    allTrades.Add((t.Time, t.Return, conf, "diplong"));
                    RouteVolVariant(dlVarLabel, sym, t.Time, t.Return, conf, "diplong", vCC);
                }
            }
        }

        if (slG != null)
        {
            Console.WriteLine($"\n══ SWINGLONG (bull-regime bullish-BoS long, router-gated) ═══════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Gated",6}");
            Console.WriteLine(new string('-', 90));

            foreach (var (sym, m15, h1) in fetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM) continue;

                int h1Split  = (int)(h1.Length * 0.8);
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                slTotalVCC += vCC;

                var (coinSlG, slVarLabel) = SelectVariantLabeled(slVariants, m15);
                coinSlG ??= slG;
                var raw   = SwingLongSimulator.GetSwingLongReturns(coinSlG, h1Val, m15Val);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)");
                    continue;
                }

                double conf = Simulator.ComputeConfidence(vRet);
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%  -{gatedOut,4}");
                slCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                slCoinRet.Add((sym, vRet));
                slFullCoins.Add((sym, h1, m15, conf));
                foreach (var t in raw)
                    allTradesNoRouter.Add((t.Time, t.Return, conf, "swing_long"));
                foreach (var t in gated)
                {
                    slTrades.Add((t.Time, t.Return, conf));
                    allTrades.Add((t.Time, t.Return, conf, "swing_long"));
                    RouteVolVariant(slVarLabel, sym, t.Time, t.Return, conf, "swing_long", vCC);
                }
            }
        }

        if (rsG != null)
        {
            Console.WriteLine($"\n══ RIPSHORT (bear-regime relief-rally short, router-gated) ═══════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}  {"Gated",6}");
            Console.WriteLine(new string('-', 90));

            foreach (var (sym, m15, h1) in fetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) { continue; }

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM) continue;

                int h1Split  = (int)(h1.Length * 0.8);
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                rsTotalVCC += vCC;

                var (coinRsG, rsVarLabel) = SelectVariantLabeled(rsVariants, m15);
                coinRsG ??= rsG;
                var raw    = RipShortSimulator.GetRipShortReturns(coinRsG, h1Val, m15Val);
                var gated  = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time)).ToList()
                    : raw;
                var vRet   = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)");
                    continue;
                }

                double conf = Simulator.ComputeConfidence(vRet);
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%  -{gatedOut,4}");
                rsCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                rsCoinRet.Add((sym, vRet));
                rsFullCoins.Add((sym, h1, m15, conf));
                foreach (var t in raw)
                    allTradesNoRouter.Add((t.Time, t.Return, conf, "ripshort"));
                foreach (var t in gated)
                {
                    rsTrades.Add((t.Time, t.Return, conf));
                    allTrades.Add((t.Time, t.Return, conf, "ripshort"));
                    RouteVolVariant(rsVarLabel, sym, t.Time, t.Return, conf, "ripshort", vCC);
                }
            }
        }

        if (agBullG != null && agBearG != null)
        {
            Console.WriteLine($"\n══ ACCUMGRID (bull+bear regime accumulation, router-gated) ═══════════════════");
            Console.WriteLine($"{"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine(new string('-', 82));

            foreach (var (sym, m15, h1) in fetched)
            {
                if (h1.Length < 300) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < Config.MinMedianVolUsdM) continue;

                int h1Split = (int)(h1.Length * 0.8);
                var h1Val = h1[h1Split..];
                if (h1Val.Length < 100) continue;

                int vCC = h1Val.Length;
                agTotalVCC += vCC;

                var bullTrades = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator.GetAccumulationReturns(agBullG, h1Val, MarketRegime.Bull);
                var bearTrades = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator.GetAccumulationReturns(agBearG, h1Val, MarketRegime.Bear);
                var raw = bullTrades.Concat(bearTrades).OrderBy(t => t.Time).ToList();

                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.AccumulationGrid, t.Time)).ToList()
                    : raw;
                var vRet = gated.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip (0 trades after gate, {raw.Count} raw)");
                    continue;
                }

                double conf = Simulator.ComputeConfidence(vRet);
                double sh = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf = Simulator.ProfitFactor(vRet);
                double wr = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg = vRet.Average();

                Console.WriteLine($"  {sym,-16} {conf,6:P1}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {vRet.Count,6}  {wr,5:P0}  {avg,+7:F2}%");
                agCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                agCoinRet.Add((sym, vRet));
                foreach (var t in raw)
                    allTradesNoRouter.Add((t.Time, t.Return, conf, "accumgrid"));
                foreach (var t in gated)
                {
                    agTrades.Add((t.Time, t.Return, conf));
                    // NOTE: AccumGrid excluded from allTrades — it's a capital accumulator, not a profit strategy
                    // allTrades.Add((t.Time, t.Return, conf, "accumgrid"));
                }
            }
        }

        if (allTrades.Count == 0) { Console.WriteLine("No trades."); return; }

        swingTrades.Sort((a, b)       => a.Time.CompareTo(b.Time));
        gridTrades.Sort((a, b)        => a.Time.CompareTo(b.Time));
        flTrades.Sort((a, b)          => a.Time.CompareTo(b.Time));
        dlTrades.Sort((a, b)          => a.Time.CompareTo(b.Time));
        slTrades.Sort((a, b)          => a.Time.CompareTo(b.Time));
        rsTrades.Sort((a, b)          => a.Time.CompareTo(b.Time));
        agTrades.Sort((a, b)          => a.Time.CompareTo(b.Time));
        allTrades.Sort((a, b)         => a.Time.CompareTo(b.Time));
        allTradesNoRouter.Sort((a, b) => a.Time.CompareTo(b.Time));

        var swingRet = swingTrades.Select(t => t.Return).ToList();
        var gridRet  = gridTrades.Select(t => t.Return).ToList();
        var flRet    = flTrades.Select(t => t.Return).ToList();
        var dlRet    = dlTrades.Select(t => t.Return).ToList();
        var slRet    = slTrades.Select(t => t.Return).ToList();
        var rsRet    = rsTrades.Select(t => t.Return).ToList();
        var agRet    = agTrades.Select(t => t.Return).ToList();
        var allRet   = allTrades.Select(t => t.Return).ToList();

        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  SWING SUMMARY  (val 20%, {swingCoinStats.Count} coins, {swingRet.Count} trades)");
        Console.WriteLine($"{new string('═', 70)}");
        if (swingRet.Count > 0)
        {
            int sw = swingRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)sw / swingRet.Count:P1}  ({sw}W / {swingRet.Count - sw}L)");
            Console.WriteLine($"  Avg return:   {swingRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(swingRet, swingTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(swingRet, swingTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(swingRet):F2}");
            Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(swingRet):F2}");
            var swingPort = Simulator.SimulatePortfolio(swingTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine();
            Console.WriteLine($"  ── Portfolio sim (€100 start · half-Kelly · 5% max) ──");
            Console.WriteLine($"    End balance:   €{swingPort.EndBalance:F2}");
            Console.WriteLine($"    Return:        {(swingPort.EndBalance - swingPort.StartBalance) / swingPort.StartBalance * 100:+0.0;-0.0}%");
            Console.WriteLine($"    Avg position:  €{swingPort.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {swingPort.MaxDrawdownPct:F1}%");
        }
        Console.WriteLine();
        Console.WriteLine($"  Per-coin (sorted by Sharpe):");
        Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine($"  {new string('-', 75)}");
        foreach (var r in swingCoinStats.OrderByDescending(c => c.Sharpe))
            Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");

        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  GRID SUMMARY  (val 20%, {gridCoinStats.Count} coins, {gridRet.Count} trades)");
        Console.WriteLine($"{new string('═', 70)}");
        if (gridRet.Count > 0)
        {
            int gw = gridRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)gw / gridRet.Count:P1}  ({gw}W / {gridRet.Count - gw}L)");
            Console.WriteLine($"  Avg return:   {gridRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(gridRet, gridTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(gridRet, gridTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(gridRet):F2}");
            Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(gridRet):F2}");
            var gridPort = Simulator.SimulatePortfolio(gridTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine();
            Console.WriteLine($"  ── Portfolio sim (€100 start · half-Kelly · 5% max) ──");
            Console.WriteLine($"    End balance:   €{gridPort.EndBalance:F2}");
            Console.WriteLine($"    Return:        {(gridPort.EndBalance - gridPort.StartBalance) / gridPort.StartBalance * 100:+0.0;-0.0}%");
            Console.WriteLine($"    Avg position:  €{gridPort.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {gridPort.MaxDrawdownPct:F1}%");
        }
        Console.WriteLine();
        Console.WriteLine($"  Per-coin (sorted by Sharpe):");
        Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine($"  {new string('-', 75)}");
        foreach (var r in gridCoinStats.OrderByDescending(c => c.Sharpe))
            Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");

        if (flRet.Count > 0)
        {
            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  FADELONG SUMMARY  (val 20%, {flCoinStats.Count} coins, {flRet.Count} trades, router-gated)");
            Console.WriteLine($"{new string('═', 70)}");
            int fw = flRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)fw / flRet.Count:P1}  ({fw}W / {flRet.Count - fw}L)");
            Console.WriteLine($"  Avg return:   {flRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(flRet, flTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(flRet, flTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(flRet):F2}");
            var flPort = Simulator.SimulatePortfolio(flTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine($"  Portfolio:    €{flPort.EndBalance:F2}  ({(flPort.EndBalance - 100) / 100 * 100:+0.0;-0.0}%)  DD={flPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in flCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (dlRet.Count > 0)
        {
            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  DIPLONG SUMMARY  (val 20%, {dlCoinStats.Count} coins, {dlRet.Count} trades, router-gated)");
            Console.WriteLine($"{new string('═', 70)}");
            int dw = dlRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)dw / dlRet.Count:P1}  ({dw}W / {dlRet.Count - dw}L)");
            Console.WriteLine($"  Avg return:   {dlRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(dlRet, dlTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(dlRet, dlTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(dlRet):F2}");
            var dlPort = Simulator.SimulatePortfolio(dlTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine($"  Portfolio:    €{dlPort.EndBalance:F2}  ({(dlPort.EndBalance - 100) / 100 * 100:+0.0;-0.0}%)  DD={dlPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in dlCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (slRet.Count > 0)
        {
            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  SWINGLONG SUMMARY  (val 20%, {slCoinStats.Count} coins, {slRet.Count} trades, router-gated)");
            Console.WriteLine($"{new string('═', 70)}");
            int sw2 = slRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)sw2 / slRet.Count:P1}  ({sw2}W / {slRet.Count - sw2}L)");
            Console.WriteLine($"  Avg return:   {slRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(slRet, slTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(slRet, slTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(slRet):F2}");
            var slPort = Simulator.SimulatePortfolio(slTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine($"  Portfolio:    €{slPort.EndBalance:F2}  ({(slPort.EndBalance - 100) / 100 * 100:+0.0;-0.0}%)  DD={slPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in slCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (rsRet.Count > 0)
        {
            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  RIPSHORT SUMMARY  (val 20%, {rsCoinStats.Count} coins, {rsRet.Count} trades, router-gated)");
            Console.WriteLine($"{new string('═', 70)}");
            int rw = rsRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)rw / rsRet.Count:P1}  ({rw}W / {rsRet.Count - rw}L)");
            Console.WriteLine($"  Avg return:   {rsRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(rsRet, rsTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(rsRet, rsTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(rsRet):F2}");
            var rsPort = Simulator.SimulatePortfolio(rsTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine($"  Portfolio:    €{rsPort.EndBalance:F2}  ({(rsPort.EndBalance - 100) / 100 * 100:+0.0;-0.0}%)  DD={rsPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in rsCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (agRet.Count > 0)
        {
            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  ACCUMGRID SUMMARY  (val 20%, {agCoinStats.Count} coins, {agRet.Count} trades, router-gated)");
            Console.WriteLine($"  NOTE: Disabled from combined portfolio — capital accumulator, not profit generator");
            Console.WriteLine($"{new string('═', 70)}");
            int aw = agRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)aw / agRet.Count:P1}  ({aw}W / {agRet.Count - aw}L)");
            Console.WriteLine($"  Avg return:   {agRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(agRet, agTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(agRet, agTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(agRet):F2}");
            var agPort = Simulator.SimulatePortfolio(agTrades.Select(t => (t.Return, t.Conf)).ToList());
            Console.WriteLine($"  Portfolio:    €{agPort.EndBalance:F2}  ({(agPort.EndBalance - 100) / 100 * 100:+0.0;-0.0}%)  DD={agPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in agCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        var highVolRet = highVolTrades.Select(t => t.Return).ToList();
        var lowVolRet  = lowVolTrades.Select(t => t.Return).ToList();

        if (highVolRet.Count > 0)
        {
            var hvCoinStats = highVolCoinRets
                .Where(kv => kv.Value.Count >= 3)
                .Select(kv =>
                {
                    var rets = kv.Value;
                    int vcc = highVolTotalVCC;
                    double sh   = Simulator.SharpeRatio(rets, vcc);
                    double sort = Simulator.SortinoRatio(rets, vcc);
                    double pf   = Simulator.ProfitFactor(rets);
                    double wr   = (double)rets.Count(r => r > 0) / rets.Count;
                    double avg  = rets.Average();
                    double kelly = Simulator.ComputeConfidence(rets);
                    return (Coin: kv.Key, Sharpe: sh, Sortino: sort, PF: pf, Trades: rets.Count, WR: wr, AvgRet: avg, Kelly: kelly);
                })
                .ToList();

            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  HIGH-VOL VARIANTS SUMMARY  ({highVolCoinRets.Count} coins, {highVolRet.Count} trades, ATR≥1.5×)");
            Console.WriteLine($"{new string('═', 70)}");
            int hvw = highVolRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)hvw / highVolRet.Count:P1}  ({hvw}W / {highVolRet.Count - hvw}L)");
            Console.WriteLine($"  Avg return:   {highVolRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(highVolRet, highVolTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(highVolRet, highVolTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(highVolRet):F2}");
            Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(highVolRet):F2}");
            var hvPort = Simulator.SimulatePortfolioExposureCapped(
                highVolTrades.Select(t => (t.Time, t.Return, t.Conf, TimeSpan.FromHours(48))).ToList(),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05, slippageBps: Config.SlippageBps);
            Console.WriteLine();
            Console.WriteLine($"  ── Portfolio sim (€100 start · half-Kelly · 5% max) ──");
            Console.WriteLine($"    End balance:   €{hvPort.EndBalance:F2}");
            Console.WriteLine($"    Return:        {(hvPort.EndBalance - hvPort.StartBalance) / hvPort.StartBalance * 100:+0.0;-0.0}%");
            Console.WriteLine($"    Avg position:  €{hvPort.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {hvPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in hvCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (lowVolRet.Count > 0)
        {
            var lvCoinStats = lowVolCoinRets
                .Where(kv => kv.Value.Count >= 3)
                .Select(kv =>
                {
                    var rets = kv.Value;
                    int vcc = lowVolTotalVCC;
                    double sh   = Simulator.SharpeRatio(rets, vcc);
                    double sort = Simulator.SortinoRatio(rets, vcc);
                    double pf   = Simulator.ProfitFactor(rets);
                    double wr   = (double)rets.Count(r => r > 0) / rets.Count;
                    double avg  = rets.Average();
                    double kelly = Simulator.ComputeConfidence(rets);
                    return (Coin: kv.Key, Sharpe: sh, Sortino: sort, PF: pf, Trades: rets.Count, WR: wr, AvgRet: avg, Kelly: kelly);
                })
                .ToList();

            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  LOW-VOL VARIANTS SUMMARY  ({lowVolCoinRets.Count} coins, {lowVolRet.Count} trades, ATR≤0.8×)");
            Console.WriteLine($"{new string('═', 70)}");
            int lvw = lowVolRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)lvw / lowVolRet.Count:P1}  ({lvw}W / {lowVolRet.Count - lvw}L)");
            Console.WriteLine($"  Avg return:   {lowVolRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(lowVolRet, lowVolTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(lowVolRet, lowVolTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(lowVolRet):F2}");
            Console.WriteLine($"  Calmar:       {Simulator.CalmarRatio(lowVolRet):F2}");
            var lvPort = Simulator.SimulatePortfolioExposureCapped(
                lowVolTrades.Select(t => (t.Time, t.Return, t.Conf, TimeSpan.FromHours(48))).ToList(),
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05, slippageBps: Config.SlippageBps);
            Console.WriteLine();
            Console.WriteLine($"  ── Portfolio sim (€100 start · half-Kelly · 5% max) ──");
            Console.WriteLine($"    End balance:   €{lvPort.EndBalance:F2}");
            Console.WriteLine($"    Return:        {(lvPort.EndBalance - lvPort.StartBalance) / lvPort.StartBalance * 100:+0.0;-0.0}%");
            Console.WriteLine($"    Avg position:  €{lvPort.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {lvPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Trades",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in lvCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        int totalWins = allRet.Count(r => r > 0);
        int totalVCC  = new[] { swingTotalVCC, gridTotalVCC, flTotalVCC, dlTotalVCC, slTotalVCC, rsTotalVCC, agTotalVCC }.Max();

        static TimeSpan StrategyHold(string strat, FadeShortGenotype swG, GridGenotype grG, FadeLongGenotype? flG, DipLongGenotype? dlG, SwingLongGenotype? slG, RipShortGenotype? rsG, GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype? agG) => strat switch
        {
            "swing"      => TimeSpan.FromHours(swG.MaxHoldCandles),
            "fadelong"   => TimeSpan.FromHours(flG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "diplong"    => TimeSpan.FromHours(dlG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "swing_long" => TimeSpan.FromHours(slG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "ripshort"   => TimeSpan.FromHours(rsG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "accumgrid"  => TimeSpan.FromHours(agG?.MaxHoldBars ?? 150),
            _            => TimeSpan.FromHours(grG.MaxHoldCandles),
        };

        // Per-strategy concurrent cap — prevents catastrophic correlation clustering
        if (allTrades.Count > 0)
        {
            var capInput = allTrades.Select(t => new PortfolioReplay.Trade(
                t.Strategy,
                t.Time,
                t.Strategy switch {
                    "swing"      => TimeSpan.FromHours(48),
                    "swing_long" => TimeSpan.FromHours(48),
                    "diplong"    => TimeSpan.FromHours(48),
                    "fadelong"   => TimeSpan.FromHours(72),
                    "ripshort"   => TimeSpan.FromHours(72),
                    "grid"       => TimeSpan.FromHours(72),
                    "accumgrid"  => TimeSpan.FromHours(150),
                    _            => TimeSpan.FromHours(48),
                },
                t.Return,
                t.Conf)).ToList();
            var capFiltered = PortfolioReplay.FilterByConcurrentCap(capInput, directionalCap: Config.MaxDirectionalConcurrent);
            int skipped = allTrades.Count - capFiltered.Count;
            if (skipped > 0)
                Console.WriteLine($"  Concurrent cap removed {skipped} trades");
            allTrades = capFiltered.Select(t => (t.EntryTime, t.Return, t.Conf, t.Strategy)).ToList();
        }

        var allTradesForExposure = allTrades
            .Select(t => (t.Time, t.Return, t.Conf, StrategyHold(t.Strategy, swingG, gridG, flG, dlG, slG, rsG, agBullG)))
            .ToList();

        var port5cap   = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, slippageBps: Config.SlippageBps);
        var portKelly  = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct, slippageBps: Config.SlippageBps);

        string Pct(List<double> r) => r.Count > 0 ? $"WR={(double)r.Count(x => x > 0)/r.Count:P0}  Avg={r.Average():+0.00}%" : "no trades";
        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  COMBINED SUMMARY  (all strategies, {allRet.Count} trades total)");
        Console.WriteLine($"{new string('═', 70)}");
        Console.WriteLine($"  FadeShort: {swingRet.Count,4} trades  PF={Simulator.ProfitFactor(swingRet):F2}  {Pct(swingRet)}");
        Console.WriteLine($"  Grid:      {gridRet.Count,4} trades  PF={Simulator.ProfitFactor(gridRet):F2}  {Pct(gridRet)}");
        if (flRet.Count > 0)
            Console.WriteLine($"  FadeLong:  {flRet.Count,4} trades  PF={Simulator.ProfitFactor(flRet):F2}  {Pct(flRet)}");
        if (dlRet.Count > 0)
            Console.WriteLine($"  DipLong:   {dlRet.Count,4} trades  PF={Simulator.ProfitFactor(dlRet):F2}  {Pct(dlRet)}");
        if (slRet.Count > 0)
            Console.WriteLine($"  SwingLong: {slRet.Count,4} trades  PF={Simulator.ProfitFactor(slRet):F2}  {Pct(slRet)}");
        if (rsRet.Count > 0)
            Console.WriteLine($"  RipShort:  {rsRet.Count,4} trades  PF={Simulator.ProfitFactor(rsRet):F2}  {Pct(rsRet)}");
        if (agRet.Count > 0)
            Console.WriteLine($"  AccumGrid: {agRet.Count,4} trades  PF={Simulator.ProfitFactor(agRet):F2}  {Pct(agRet)}  (excluded from portfolio)");
        Console.WriteLine($"  Total:     {allRet.Count,4} trades  PF={Simulator.ProfitFactor(allRet):F2}  {Pct(allRet)}");
        Console.WriteLine();
        Console.WriteLine($"  Sharpe (combined):  {Simulator.SharpeRatio(allRet, totalVCC):F2}");
        Console.WriteLine($"  Sortino (combined): {Simulator.SortinoRatio(allRet, totalVCC):F2}");
        Console.WriteLine($"  Calmar (combined):  {Simulator.CalmarRatio(allRet):F2}");

        void PrintCombinedPort(string label, Simulator.PortfolioResult p)
        {
            double ret = (p.EndBalance - p.StartBalance) / p.StartBalance * 100;
            Console.WriteLine($"\n  ── {label} ──");
            Console.WriteLine($"    End balance:   €{p.EndBalance:F2}  ({ret:+0.0;-0.0}%)");
            Console.WriteLine($"    Avg position:  €{p.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {p.MaxDrawdownPct:F1}%");
            if (p.TradesToTenPct > 0) Console.WriteLine($"    Trades to +10%:{p.TradesToTenPct}");
        }

        PrintCombinedPort($"5% per position · {Config.MaxTotalExposurePct:P0} total cap  [concurrent-aware]", port5cap);
        PrintCombinedPort($"Kelly(15%) · {Config.MaxTotalExposurePct:P0} total cap  [concurrent-aware]", portKelly);

        if (allTrades.Count >= 2)
        {
            double valDays   = (allTrades[^1].Time - allTrades[0].Time).TotalDays;
            double annFactor = valDays > 0 ? 365.0 / valDays : 1.0;
            void AnnLine(string tag, Simulator.PortfolioResult p)
            {
                double r   = (p.EndBalance - p.StartBalance) / p.StartBalance * 100;
                double ann = (Math.Pow(1 + r / 100.0, annFactor) - 1) * 100;
                Console.WriteLine($"  {tag}: {valDays:F0}d  ann {ann:+0.0;-0.0}%  DD {p.MaxDrawdownPct:F1}%");
            }
            Console.WriteLine();
            AnnLine("5% cap ", port5cap);
            AnnLine("Kelly(15%)  ", portKelly);
        }

        {
            var byMonth = allTrades
                .GroupBy(t => new DateTime(t.Time.Year, t.Time.Month, 1))
                .OrderBy(g => g.Key)
                .ToList();

            bool hasGrid     = gridRet.Count > 0;
            bool hasDipLong  = dlRet.Count  > 0;
            bool hasFadeLong = flRet.Count  > 0;

            Dictionary<DateTime, (string Label, int HVPct)> monthRegime = new();
            if (btcRegimeSeries != null)
            {
                foreach (var grp2 in btcRegimeSeries.GroupBy(b => new DateTime(b.Time.Year, b.Time.Month, 1)))
                {
                    var counts = grp2.GroupBy(b => b.Regime)
                                     .ToDictionary(g => g.Key, g => g.Count());
                    int total = grp2.Count();
                    var dom   = counts.OrderByDescending(kv => kv.Value).First().Key;
                    int hvPct = counts.TryGetValue(MarketRegime.HighVol, out int hv) ? hv * 100 / total : 0;
                    monthRegime[grp2.Key] = (dom.ToString()[..4], hvPct);
                }
            }

            Console.WriteLine($"\n── Monthly trade activity (val window, router-gated) ────────────────────────");
            Console.WriteLine($"  {"Month",-9}  {"Regime",7}  {"Swing",5}  {"Grid",5}  {"DipLong",8}  {"FadeLong",9}  {"RipShort",9}  {"Total",5}");
            Console.WriteLine($"  {new string('-', 60)}");

            int gridGapMonths   = 0;
            int totalSilentMonths = 0;
            foreach (var grp in byMonth)
            {
                int sw = grp.Count(t => t.Strategy == "swing");
                int gr = grp.Count(t => t.Strategy == "grid");
                int dl = grp.Count(t => t.Strategy == "diplong");
                int fl = grp.Count(t => t.Strategy == "fadelong");
                int rs = grp.Count(t => t.Strategy == "ripshort");

                string regLabel = monthRegime.TryGetValue(grp.Key, out var rm)
                    ? (rm.HVPct >= 20 ? $"{rm.Label}+HV" : rm.Label) : "?";

                string flag = "";
                if (hasGrid && gr == 0) { flag += " ← Grid dark"; gridGapMonths++; }
                if (grp.Count() <= 5)   { flag += " ← sparse"; totalSilentMonths++; }

                Console.WriteLine($"  {grp.Key:yyyy-MM}  {regLabel,7}  {sw,5}  {gr,5}  {dl,8}  {fl,9}  {rs,9}  {grp.Count(),5}{flag}");
            }

            if (hasGrid && gridGapMonths > 0)
                Console.WriteLine($"\n  ⚠ Grid dark in {gridGapMonths}/{byMonth.Count} months — router GridMaxConf={routerG?.GridMaxConf:F2}");
            else if (hasGrid)
                Console.WriteLine($"\n  ✓ Grid active every month — router gate is not causing inactivity");

            if (totalSilentMonths > 0)
                Console.WriteLine($"  ⚠ {totalSilentMonths} month(s) sparse (≤5 trades) — potential coverage gap");

            if (btcRegimeSeries != null)
            {
                var regimeCounts = btcRegimeSeries.GroupBy(b => b.Regime)
                    .Select(g => (Regime: g.Key, Pct: g.Count() * 100.0 / btcRegimeSeries.Length))
                    .OrderByDescending(x => x.Pct).ToList();
                Console.WriteLine($"\n── BTC regime distribution (full history, {btcRegimeSeries.Length} h1 bars) ──────────────────");
                foreach (var (regime, pct) in regimeCounts)
                {
                    string gap = regime switch
                    {
                        MarketRegime.HighVol => "FadeShort only (half-size) — no long strategy active",
                        MarketRegime.Bear    => $"FadeLong needs ≥{routerG?.BearMinBars ?? 276}h sustained — {pct:F0}% of history uncovered by longs",
                        _                   => ""
                    };
                    Console.WriteLine($"  {regime,-10} {pct,5:F1}%{(gap.Length > 0 ? $"  ← {gap}" : "")}");
                }
            }
        }

        {
            var nrExposure = allTradesNoRouter
                .Select(t => (t.Time, t.Return, t.Conf, StrategyHold(t.Strategy, swingG, gridG, flG, dlG, slG, rsG, agBullG)))
                .ToList();

            var nrPort5cap  = Simulator.SimulatePortfolioExposureCapped(nrExposure, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, slippageBps: Config.SlippageBps);
            var nrPortKelly = Simulator.SimulatePortfolioExposureCapped(nrExposure, Config.MaxTotalExposurePct, slippageBps: Config.SlippageBps);

            int nrGrid      = allTradesNoRouter.Count(t => t.Strategy == "grid");
            int nrDipLong   = allTradesNoRouter.Count(t => t.Strategy == "diplong");
            int nrFadeLong  = allTradesNoRouter.Count(t => t.Strategy == "fadelong");
            int nrSwingLong = allTradesNoRouter.Count(t => t.Strategy == "swing_long");
            int nrRipShort  = allTradesNoRouter.Count(t => t.Strategy == "ripshort");

            double r5R   = (port5cap.EndBalance   - 100) / 100 * 100;
            double r5NR  = (nrPort5cap.EndBalance  - 100) / 100 * 100;
            double rKR   = (portKelly.EndBalance   - 100) / 100 * 100;
            double rKNR  = (nrPortKelly.EndBalance - 100) / 100 * 100;

            Console.WriteLine($"\n── Router impact: with vs without router (val window) ───────────────────────");
            Console.WriteLine($"  {"Strategy",-12}  {"With router",11}  {"No router",9}  {"Δ extra",7}");
            Console.WriteLine($"  {new string('-', 46)}");
            Console.WriteLine($"  {"FadeShort",-12}  {swingRet.Count,11}  {swingRet.Count,9}  {"—",7}");
            Console.WriteLine($"  {"Grid",-12}  {gridRet.Count,11}  {nrGrid,9}  {nrGrid - gridRet.Count,+7}");
            Console.WriteLine($"  {"DipLong",-12}  {dlRet.Count,11}  {nrDipLong,9}  {nrDipLong - dlRet.Count,+7}");
            Console.WriteLine($"  {"FadeLong",-12}  {flRet.Count,11}  {nrFadeLong,9}  {nrFadeLong - flRet.Count,+7}");
            if (slRet.Count > 0 || nrSwingLong > 0)
                Console.WriteLine($"  {"SwingLong",-12}  {slRet.Count,11}  {nrSwingLong,9}  {nrSwingLong - slRet.Count,+7}");
            if (rsRet.Count > 0 || nrRipShort > 0)
                Console.WriteLine($"  {"RipShort",-12}  {rsRet.Count,11}  {nrRipShort,9}  {nrRipShort - rsRet.Count,+7}");
            Console.WriteLine($"  {"Total",-12}  {allTrades.Count,11}  {allTradesNoRouter.Count,9}");
            Console.WriteLine();
            Console.WriteLine($"  {"Scenario",-30}  {"Return",8}  {"DD",6}");
            Console.WriteLine($"  {new string('-', 48)}");
            Console.WriteLine($"  {"5% cap · with router",-30}  {r5R,+7:F1}%  {port5cap.MaxDrawdownPct,5:F1}%");
            Console.WriteLine($"  {"5% cap · no router",-30}  {r5NR,+7:F1}%  {nrPort5cap.MaxDrawdownPct,5:F1}%");
            Console.WriteLine($"  {"Kelly(15%) · with router",-30}  {rKR,+7:F1}%  {portKelly.MaxDrawdownPct,5:F1}%");
            Console.WriteLine($"  {"Kelly(15%) · no router",-30}  {rKNR,+7:F1}%  {nrPortKelly.MaxDrawdownPct,5:F1}%");

            string edgeSign5 = r5R >= r5NR ? "+" : "";
            string edgeSignK = rKR >= rKNR ? "+" : "";
            Console.WriteLine($"  Router edge: 5% cap {edgeSign5}{r5R - r5NR:F1}pp  /  Kelly(15%) {edgeSignK}{rKR - rKNR:F1}pp");
        }

        {
            var lastDate = allTradesForExposure[^1].Time;
            var cutDate  = lastDate - TimeSpan.FromDays(90);
            var slice90  = allTradesForExposure.Where(t => t.Time >= cutDate).ToList();

            if (slice90.Count >= 10)
            {
                double spanDays   = (lastDate - slice90[0].Time).TotalDays;
                var    p5cap      = Simulator.SimulatePortfolioExposureCapped(slice90, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, slippageBps: Config.SlippageBps);
                var    pKelly     = Simulator.SimulatePortfolioExposureCapped(slice90, Config.MaxTotalExposurePct, slippageBps: Config.SlippageBps);

                double r5   = (p5cap.EndBalance  - 100.0) / 100.0;
                double rK   = (pKelly.EndBalance  - 100.0) / 100.0;
                double ann5 = spanDays > 0 ? (Math.Pow(1 + r5, 365.0 / spanDays) - 1) * 100 : 0;
                double annK = spanDays > 0 ? (Math.Pow(1 + rK, 365.0 / spanDays) - 1) * 100 : 0;

                Console.WriteLine($"\n── Forward projection: last 90d run-rate → 1yr ─────────────────────────────");
                Console.WriteLine($"  Slice: {slice90.Count} trades  period: {slice90[0].Time:yyyy-MM-dd} → {lastDate:yyyy-MM-dd}  ({spanDays:F0}d)");
                Console.WriteLine($"  5% cap:  {r5 * 100:+0.0;-0.0}% in {spanDays:F0}d  →  projected {ann5:+0.0;-0.0}%/yr  (DD in slice: {p5cap.MaxDrawdownPct:F1}%)");
                Console.WriteLine($"  Kelly:   {rK * 100:+0.0;-0.0}% in {spanDays:F0}d  →  projected {annK:+0.0;-0.0}%/yr  (DD in slice: {pKelly.MaxDrawdownPct:F1}%)");
                Console.WriteLine($"  Full-window ann for reference:  5% cap {(allTrades.Count >= 2 ? $"{(Math.Pow(1 + (port5cap.EndBalance - 100) / 100, 365.0 / (allTrades[^1].Time - allTrades[0].Time).TotalDays) - 1) * 100:+0.0}%" : "n/a")}  Kelly {(allTrades.Count >= 2 ? $"{(Math.Pow(1 + (portKelly.EndBalance - 100) / 100, 365.0 / (allTrades[^1].Time - allTrades[0].Time).TotalDays) - 1) * 100:+0.0}%" : "n/a")}");
                Console.WriteLine($"  ⚠ Assumes stable regime — recent bull market may not persist.");
            }
        }

        Console.WriteLine($"\n── Full 3yr history DD (train + val, router-gated) ──────────────────────────");
        var fullHistTrades = new List<(DateTime Time, double Return, double Conf, TimeSpan Hold)>();

        // Per-coin full-history returns for statistical tests (more trades → PBO meaningful for sparse strategies).
        var swingFullCoinRet = new List<(string Label, List<double> Returns)>();
        var gridFullCoinRet  = new List<(string Label, List<double> Returns)>();
        var flFullCoinRet    = new List<(string Label, List<double> Returns)>();
        var dlFullCoinRet    = new List<(string Label, List<double> Returns)>();
        var slFullCoinRet    = new List<(string Label, List<double> Returns)>();
        var rsFullCoinRet    = new List<(string Label, List<double> Returns)>();

        foreach (var (sym, h1f, m15f, conf) in swingFullCoins)
        {
            var coinFsGFull = SelectVariant(fsVariants, m15f) ?? swingG;
            var coinRet = new List<double>();
            foreach (var (t, ret, _) in FadeShortSimulator.GetFadeShortReturns(coinFsGFull, h1f, m15f))
            {
                fullHistTrades.Add((t, ret, conf, TimeSpan.FromHours(coinFsGFull.MaxHoldCandles)));
                coinRet.Add(ret);
            }
            if (coinRet.Count > 0) swingFullCoinRet.Add((sym, coinRet));
        }

        foreach (var (sym, h1f, conf) in gridFullCoins)
        {
            // gridFullCoins doesn't store m15 — use the representative gridG for hold-time
            var coinRet = new List<double>();
            foreach (var (t, ret, _) in GridSimulator.GetGridReturns(gridG, h1f))
            {
                if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.Grid, t)) continue;
                fullHistTrades.Add((t, ret, conf, TimeSpan.FromHours(gridG.MaxHoldCandles)));
                coinRet.Add(ret);
            }
            if (coinRet.Count > 0) gridFullCoinRet.Add((sym, coinRet));
        }

        if (flG != null)
            foreach (var (sym, h1f, m15f, conf) in flFullCoins)
            {
                var coinFlGFull = SelectVariant(flVariants, m15f) ?? flG;
                var coinRet = new List<double>();
                foreach (var t in FadeLongSimulator.GetFadeLongReturns(coinFlGFull, h1f, m15f))
                {
                    if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)) continue;
                    fullHistTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(coinFlGFull.MaxHoldCandles)));
                    coinRet.Add(t.Return);
                }
                if (coinRet.Count > 0) flFullCoinRet.Add((sym, coinRet));
            }

        if (dlG != null)
            foreach (var (sym, h1f, m15f, conf) in dlFullCoins)
            {
                var coinDlGFull = SelectVariant(dlVariants, m15f) ?? dlG;
                var coinRet = new List<double>();
                foreach (var t in DipLongSimulator.GetDipLongReturns(coinDlGFull, h1f, m15f))
                {
                    if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)) continue;
                    fullHistTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(coinDlGFull.MaxHoldCandles)));
                    coinRet.Add(t.Return);
                }
                if (coinRet.Count > 0) dlFullCoinRet.Add((sym, coinRet));
            }

        if (slG != null)
            foreach (var (sym, h1f, m15f, conf) in slFullCoins)
            {
                var coinSlGFull = SelectVariant(slVariants, m15f) ?? slG;
                var coinRet = new List<double>();
                foreach (var t in SwingLongSimulator.GetSwingLongReturns(coinSlGFull, h1f, m15f))
                {
                    if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)) continue;
                    fullHistTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(coinSlGFull.MaxHoldCandles)));
                    coinRet.Add(t.Return);
                }
                if (coinRet.Count > 0) slFullCoinRet.Add((sym, coinRet));
            }

        if (rsG != null)
            foreach (var (sym, h1f, m15f, conf) in rsFullCoins)
            {
                var coinRsGFull = SelectVariant(rsVariants, m15f) ?? rsG;
                var coinRet = new List<double>();
                foreach (var t in RipShortSimulator.GetRipShortReturns(coinRsGFull, h1f, m15f))
                {
                    if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time)) continue;
                    fullHistTrades.Add((t.Time, t.Return, conf, TimeSpan.FromHours(coinRsGFull.MaxHoldCandles)));
                    coinRet.Add(t.Return);
                }
                if (coinRet.Count > 0) rsFullCoinRet.Add((sym, coinRet));
            }

        fullHistTrades.Sort((a, b) => a.Time.CompareTo(b.Time));

        if (fullHistTrades.Count >= 10)
        {
            var fhPort5cap = Simulator.SimulatePortfolioExposureCapped(fullHistTrades, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, slippageBps: Config.SlippageBps);
            var fhPortKel  = Simulator.SimulatePortfolioExposureCapped(fullHistTrades, Config.MaxTotalExposurePct, slippageBps: Config.SlippageBps);
            double fhDays  = (fullHistTrades[^1].Time - fullHistTrades[0].Time).TotalDays;
            double fhAnnF  = fhDays > 0 ? 365.0 / fhDays : 1.0;
            double fhRet5  = (fhPort5cap.EndBalance - 100) / 100 * 100;
            double fhRetE  = (fhPortKel.EndBalance  - 100) / 100 * 100;
            double fhAnn5  = (Math.Pow(1 + fhRet5  / 100.0, fhAnnF) - 1) * 100;
            double fhAnnE  = (Math.Pow(1 + fhRetE  / 100.0, fhAnnF) - 1) * 100;
            Console.WriteLine($"  Trades: {fullHistTrades.Count}  Period: {fhDays:F0} days");
            Console.WriteLine($"  5% cap · {Config.MaxTotalExposurePct:P0} total:  End {fhRet5:+0.0;-0.0}%  ann {fhAnn5:+0.0;-0.0}%  DD {fhPort5cap.MaxDrawdownPct:F1}%");
            Console.WriteLine($"  Kelly  · {Config.MaxTotalExposurePct:P0} total:  End {fhRetE:+0.0;-0.0}%  ann {fhAnnE:+0.0;-0.0}%  DD {fhPortKel.MaxDrawdownPct:F1}%");
        }
        else Console.WriteLine("  Not enough trades for full-history simulation.");

        // ── Multiple-testing analysis ─────────────────────────────────────────
        // DSR corrects the Sharpe for non-normality and GA selection bias.
        // PBO asks: across CPCV splits, does the IS-best coin win OOS?
        // WRC asks: is the best coin's edge explainable by random data-snooping?
        //
        // GA trial estimates (upper bounds — correlated trials → effective T < nominal):
        //   FadeShort / Grid / SwingLong : ~50 pop × 200 gen = 10,000
        //   FadeLong / DipLong           : coevolve 8 rounds × 30 gen × 50 pop = 12,000
        //   Interpretation: use T=1,000 as optimistic, T=10,000 as nominal,
        //   T=100,000 as conservative worst-case.
        Console.WriteLine($"\n\n{new string('═', 70)}");
        Console.WriteLine("  MULTIPLE-TESTING ANALYSIS");
        Console.WriteLine("  Deflated SR · Probability of Backtest Overfitting · White's Reality Check");
        Console.WriteLine($"{new string('═', 70)}");

        // Statistical tests use full 3yr history per coin (IS + val) so regime-conditional
        // strategies (FadeLong, DipLong) have enough trades per coin for PBO to be meaningful.
        var statRng = new Random(42);
        if (swingFullCoinRet.Count >= 2) StatisticalTests.PrintReport(swingFullCoinRet, "FadeShort",  gaTrials: 10_000, rng: statRng);
        if (gridFullCoinRet.Count  >= 2) StatisticalTests.PrintReport(gridFullCoinRet,  "Grid",       gaTrials: 10_000, rng: statRng);
        if (flFullCoinRet.Count    >= 2) StatisticalTests.PrintReport(flFullCoinRet,    "FadeLong",   gaTrials: 12_000, rng: statRng);
        if (dlFullCoinRet.Count    >= 2) StatisticalTests.PrintReport(dlFullCoinRet,    "DipLong",    gaTrials: 12_000, rng: statRng);
        if (slFullCoinRet.Count    >= 2) StatisticalTests.PrintReport(slFullCoinRet,    "SwingLong",  gaTrials: 10_000, rng: statRng);
        if (rsFullCoinRet.Count    >= 2) StatisticalTests.PrintReport(rsFullCoinRet,    "RipShort",   gaTrials: 12_000, rng: statRng);

        // ── Holm-Bonferroni Family-Wise Error Rate Correction ────────────────────────
        // Collect per-strategy p-values from Monte Carlo test and apply correction.
        var strategyPValues = new List<(string Name, double PValue)>();
        var mcRng = new Random(42);

        // Strategy order is fixed so the printed table is stable across runs, and so the
        // p-value vector handed to Holm-Bonferroni is reproducible for a given trade set.
        var mcInputs = new (string Name, List<(string Label, List<double> Returns)> PerCoin)[]
        {
            ("FadeShort", swingFullCoinRet),
            ("Grid",      gridFullCoinRet),
            ("FadeLong",  flFullCoinRet),
            ("DipLong",   dlFullCoinRet),
            ("SwingLong", slFullCoinRet),
            ("RipShort",  rsFullCoinRet),
        };

        foreach (var (name, perCoin) in mcInputs)
        {
            if (perCoin.Count < 2) continue;
            var rets = perCoin.SelectMany(c => c.Returns).ToList();
            if (rets.Count < 10) continue;
            strategyPValues.Add((name, MonteCarloTest.Run(rets, permutations: 1000, rng: mcRng).PValue));
        }

        // Apply Holm-Bonferroni correction and report
        if (strategyPValues.Count > 0)
        {
            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine("  HOLM-BONFERRONI FAMILY-WISE ERROR RATE CORRECTION");
            Console.WriteLine($"{new string('═', 70)}");
            double uncorrectedFWER = 1.0 - Math.Pow(0.95, strategyPValues.Count);
            Console.WriteLine($"  Across {strategyPValues.Count} strategies tested at α=0.05:");
            Console.WriteLine($"  Uncorrected FWER ≈ {uncorrectedFWER:P1} (one in {1.0/uncorrectedFWER:F1} chance of false rejection)");
            Console.WriteLine($"  ∴ Read the ADJUSTED p-value column below (step-down Holm threshold)\n");

            double[] pVals = strategyPValues.Select(sp => sp.PValue).ToArray();
            double[] adjPVals = StatisticalTests.HolmBonferroniAdjustedPValues(pVals);
            bool[] rejected = StatisticalTests.HolmBonferroni(pVals);

            Console.WriteLine($"  {"Strategy",-15} {"Raw p-value",12} {"Adjusted p-val",14} {"Significant",12}");
            Console.WriteLine($"  {new string('-', 66)}");
            for (int i = 0; i < strategyPValues.Count; i++)
            {
                string sig = rejected[i] ? "✓ YES" : "✗ NO";
                Console.WriteLine($"  {strategyPValues[i].Name,-15} {pVals[i],12:F4} {adjPVals[i],14:F4} {sig,12}");
            }
        }

        // Combined portfolio: DSR on full-history returns + WRC across all coin×strategy configs.
        var allFullRet = swingFullCoinRet.Concat(gridFullCoinRet).Concat(flFullCoinRet)
                                         .Concat(dlFullCoinRet).Concat(slFullCoinRet).Concat(rsFullCoinRet)
                                         .SelectMany(c => c.Returns).ToList();
        if (allFullRet.Count >= 10)
        {
            Console.WriteLine($"\n── Combined portfolio  ({allFullRet.Count} trades, full history) ──────────────────────");
            Console.WriteLine("  DSR  (H₀: true SR ≤ 0 after selection from T trials across all strategies)");
            Console.WriteLine($"  {"T (trials)",12}  {"SR̂",7}  {"E[maxSR]",9}  {"PSR₀",6}  {"DSR",6}  verdict");
            foreach (int T in new[] { 10_000, 50_000, 500_000 })
            {
                var (dsr, psr0, eMaxSr, srHat) = StatisticalTests.DeflatedSharpeRatio(allFullRet, T);
                string v = dsr >= 0.95 ? "✓ significant" : dsr >= 0.80 ? "⚠ borderline" : "✗ not significant";
                Console.WriteLine($"  {T,12:N0}  {srHat,+7:F4}  {eMaxSr,+9:F4}  {psr0,6:F3}  {dsr,6:F3}  {v}");
            }
            var allCoinConfigs = swingFullCoinRet.Concat(gridFullCoinRet).Concat(flFullCoinRet)
                                                 .Concat(dlFullCoinRet).Concat(slFullCoinRet).Concat(rsFullCoinRet).ToList();
            if (allCoinConfigs.Count >= 2)
            {
                double pVal = StatisticalTests.WhitesRealityCheck(allCoinConfigs, rng: statRng);
                if (!double.IsNaN(pVal))
                {
                    string v = pVal < 0.05 ? "✓ rejects H₀ (genuine edge)"
                             : pVal < 0.20 ? "⚠ weak evidence" : "✗ H₀ not rejected";
                    Console.WriteLine($"  WRC  (all {allCoinConfigs.Count} coin×strategy configs, 1000 bootstrap):  p={pVal:F3}  [{v}]");
                }
            }
        }

        // Write structured JSON for frontend
        static double ComputeMaxDD(List<double> r)
        {
            if (r.Count == 0) return 0;
            double eq = 100.0, peak = 100.0, maxDd = 0;
            foreach (var ret in r) { eq += ret; if (eq > peak) peak = eq; double dd = (peak - eq) / peak * 100; if (dd > maxDd) maxDd = dd; }
            return maxDd;
        }

        static object StratStats(List<double> r, int vcc) => r.Count == 0
            ? new { trades = 0, winRate = 0.0, sharpe = 0.0, pf = 0.0, maxDD = 0.0, ret = 0.0 }
            : new
            {
                trades  = r.Count,
                winRate = Math.Round((double)r.Count(x => x > 0) / r.Count * 100, 2),
                sharpe  = Math.Round(Simulator.SharpeRatio(r, vcc), 4),
                pf      = Math.Round(Simulator.ProfitFactor(r), 4),
                maxDD   = Math.Round(-ComputeMaxDD(r), 2),
                ret     = Math.Round(r.Sum(), 2),
            };

        var backtestResults = new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            val = new Dictionary<string, object>
            {
                ["FadeShort"] = new Dictionary<string, object>
                    { ["default"] = StratStats(swingRet, swingTotalVCC) },
                ["Grid"] = new Dictionary<string, object>
                    { ["default"] = StratStats(gridRet, gridTotalVCC) },
                ["FadeLong"] = new Dictionary<string, object>
                    { ["default"] = StratStats(flRet, flTotalVCC) },
                ["DipLong"] = new Dictionary<string, object>
                    { ["default"] = StratStats(dlRet, dlTotalVCC) },
                ["SwingLong"] = new Dictionary<string, object>
                    { ["default"] = StratStats(slRet, slTotalVCC) },
                ["RipShort"] = new Dictionary<string, object>
                    { ["default"] = StratStats(rsRet, rsTotalVCC) },
            },
            highvol_summary = highVolRet.Count == 0 ? null : new
            {
                trades = highVolRet.Count,
                winRate = Math.Round((double)highVolRet.Count(x => x > 0) / highVolRet.Count * 100, 2),
                sharpe = Math.Round(Simulator.SharpeRatio(highVolRet, highVolTotalVCC), 4),
                sortino = Math.Round(Simulator.SortinoRatio(highVolRet, highVolTotalVCC), 4),
                pf = Math.Round(Simulator.ProfitFactor(highVolRet), 4),
                calmar = Math.Round(Simulator.CalmarRatio(highVolRet), 4),
                avgRet = Math.Round(highVolRet.Average(), 4),
                coins = highVolCoinRets.Count,
            },
            lowvol_summary = lowVolRet.Count == 0 ? null : new
            {
                trades = lowVolRet.Count,
                winRate = Math.Round((double)lowVolRet.Count(x => x > 0) / lowVolRet.Count * 100, 2),
                sharpe = Math.Round(Simulator.SharpeRatio(lowVolRet, lowVolTotalVCC), 4),
                sortino = Math.Round(Simulator.SortinoRatio(lowVolRet, lowVolTotalVCC), 4),
                pf = Math.Round(Simulator.ProfitFactor(lowVolRet), 4),
                calmar = Math.Round(Simulator.CalmarRatio(lowVolRet), 4),
                avgRet = Math.Round(lowVolRet.Average(), 4),
                coins = lowVolCoinRets.Count,
            },
        };
        File.WriteAllText("backtest_results.json",
            System.Text.Json.JsonSerializer.Serialize(backtestResults,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("backtest_results.json written.");
    }
}
