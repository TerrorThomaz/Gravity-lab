using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class FullTest
{
    // Variant loading/selection lives in StrategyPipeline (the reproducer-of-record).

    public static async Task RunFullTest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | FULL TEST ({DataSplit.ValLabel} + 28 OOS · all strategies · condensed) ===\n");

        // ── Genotypes ─────────────────────────────────────────────────────────────
        var swingG  = File.Exists(Config.FadeShortGenoFile)  ? JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype()   : (FadeShortGenotype?)null;
        var gridG   = File.Exists(Config.GridGenoFile)        ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype()             : (GridGenotype?)null;
        // Loaded like every other strategy. It was hardcoded to null behind a comment asserting a
        // performance number; that number was never re-measured after the fold-aggregator, funding
        // sign, slippage and concurrency fixes, and it kept FadeLong out of this report entirely.
        // A claim in a comment is not a measurement — if FadeLong is a drag, this report is the
        // thing that should say so, from data.
        var flG     = File.Exists(Config.FadeLongGenoFile)    ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype()     : (FadeLongGenotype?)null;
        var dlG     = File.Exists(Config.DipLongGenoFile)     ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()       : (DipLongGenotype?)null;
        var slG     = File.Exists(Config.SwingLongGenoFile)   ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype()   : (SwingLongGenotype?)null;
        var rsG     = File.Exists(Config.RipShortGenoFile)    ? JsonSerializer.Deserialize<RipShortGenotypeDto>(File.ReadAllText(Config.RipShortGenoFile))!.ToGenotype()      : (RipShortGenotype?)null;
        var gsG     = File.Exists(Config.GridShortGenoFile)   ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridShortGenoFile))!.ToGenotype()          : (GridGenotype?)null;
        var routerG = File.Exists(Config.RouterGenoFile)      ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype()   : (RegimeRouterGenotype?)null;

        GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype? agBullG = null;
        GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype? agBearG = null;
        if (File.Exists("genotypes/accumulation_grid_genotype.json"))
        {
            var agJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText("genotypes/accumulation_grid_genotype.json"));
            agBullG = JsonSerializer.Deserialize<GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype>(agJson.GetProperty("Bull").GetRawText());
            agBearG = JsonSerializer.Deserialize<GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype>(agJson.GetProperty("Bear").GetRawText());
        }

        if (swingG == null) { Console.WriteLine("Missing FadeShort genotype — run 'train' first."); return; }
        if (gridG  == null) { Console.WriteLine("Missing grid genotype — run 'gridtrain' first."); return; }

        var fsVariants = StrategyPipeline.LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var gridVariants = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_best", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var dlVariants = StrategyPipeline.LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var slVariants = StrategyPipeline.LoadVariants<SwingLongGenotypeDto, SwingLongGenotype>(
            "swing_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var rsVariants = StrategyPipeline.LoadVariants<RipShortGenotypeDto, RipShortGenotype>(
            "rip_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

        Console.WriteLine($"  FadeShort: {swingG}");
        Console.WriteLine($"  Grid:      {gridG}");
        if (flG     != null) Console.WriteLine($"  FadeLong:  {flG}");
        if (dlG     != null) Console.WriteLine($"  DipLong:   {dlG}");
        if (slG     != null) Console.WriteLine($"  SwingLong: {slG}");
        if (rsG     != null) Console.WriteLine($"  RipShort:  {rsG}");
        if (gsG     != null) Console.WriteLine($"  GridShort: {gsG}");
        if (agBullG != null && agBearG != null)
            Console.WriteLine($"  AccumGrid: Bull={agBullG}  Bear={agBearG}");
        if (routerG != null) Console.WriteLine($"  Router:    {routerG}");
        Console.WriteLine();

        // ── Single candle fetch: training + OOS + BTC/ETH ─────────────────────────
        var allSyms = Config.BacktestCoins
            .Concat(Config.OosCoins)
            .Concat(new[] { "BTCUSDT", "ETHUSDT" })
            .Distinct().ToArray();

        Console.WriteLine($"  Fetching {Config.BacktestCoins.Length} training + {Config.OosCoins.Length} OOS coins (15m → 1h, ~3yr)...");
        var fetched = await StrategyPipeline.FetchFifteenMinAsync(client, allSyms, batches: 113);
        var fetchedMap = fetched.ToDictionary(f => f.sym);
        Console.WriteLine("  Done.\n");

        // ── Router session ─────────────────────────────────────────────────────────
        var router = StrategyPipeline.BuildRouterSession(routerG, fetched, null);
        RegimeRouterSession? session   = router.Session;
        RegimeBar[]?         btcSeries = router.BtcRegimeSeries;

        // ── Funding rate session (BTC as market-wide proxy) ───────────────────────────
        FundingRateSession? fundingSession = null;
        if (fetchedMap.ContainsKey("BTCUSDT"))
        {
            var btcFunding = await CandleFetcher.FetchFundingRateCachedAsync(client, "BTCUSDT");
            if (btcFunding.Length > 0)
            {
                fundingSession = new FundingRateSession(btcFunding, 8.0);
                Console.WriteLine($"  BTC funding: {btcFunding.Length} 8h records · current={fundingSession.CurrentRate:+0.0000%;-0.0000%;0.0000%}/8h\n");
            }
        }

        // ── Dynamic guard session (built once, used in Section 2 and 9) ─────────────
        DynamicGuardSession? dgSession = null;
        DynamicGuardGenotype? dgGeno   = null;
        if (File.Exists(Config.DynamicGuardGenoFile) && fetchedMap.TryGetValue("BTCUSDT", out var btcDG) && btcDG.h1.Length >= 50)
        {
            dgGeno   = JsonSerializer.Deserialize<DynamicGuardGenotypeDto>(File.ReadAllText(Config.DynamicGuardGenoFile))!.ToGenotype();
            dgSession = new DynamicGuardSession(btcDG.h1, dgGeno);
        }

        var valHighVolTrades   = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var valLowVolTrades    = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var valHighVolCoinRets = new Dictionary<string, List<double>>();
        var valLowVolCoinRets  = new Dictionary<string, List<double>>();
        int valHighVolVCC = 0;
        int valLowVolVCC  = 0;
        var oosHighVolTrades   = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var oosLowVolTrades    = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var oosHighVolCoinRets = new Dictionary<string, List<double>>();
        var oosLowVolCoinRets  = new Dictionary<string, List<double>>();
        int oosHighVolVCC = 0;
        int oosLowVolVCC  = 0;

        void RouteValVol(string label, string sym, DateTime time, double ret, double conf, string strategy, int vcc)
        {
            if (label == "highvol")
            {
                valHighVolTrades.Add((time, ret, conf, strategy));
                if (!valHighVolCoinRets.ContainsKey(sym)) valHighVolCoinRets[sym] = new List<double>();
                valHighVolCoinRets[sym].Add(ret);
                valHighVolVCC = Math.Max(valHighVolVCC, vcc);
            }
            else if (label == "lowvol")
            {
                valLowVolTrades.Add((time, ret, conf, strategy));
                if (!valLowVolCoinRets.ContainsKey(sym)) valLowVolCoinRets[sym] = new List<double>();
                valLowVolCoinRets[sym].Add(ret);
                valLowVolVCC = Math.Max(valLowVolVCC, vcc);
            }
        }

        void RouteOosVol(string label, string sym, DateTime time, double ret, double conf, string strategy, int vcc)
        {
            if (label == "highvol")
            {
                oosHighVolTrades.Add((time, ret, conf, strategy));
                if (!oosHighVolCoinRets.ContainsKey(sym)) oosHighVolCoinRets[sym] = new List<double>();
                oosHighVolCoinRets[sym].Add(ret);
                oosHighVolVCC = Math.Max(oosHighVolVCC, vcc);
            }
            else if (label == "lowvol")
            {
                oosLowVolTrades.Add((time, ret, conf, strategy));
                if (!oosLowVolCoinRets.ContainsKey(sym)) oosLowVolCoinRets[sym] = new List<double>();
                oosLowVolCoinRets[sym].Add(ret);
                oosLowVolVCC = Math.Max(oosLowVolVCC, vcc);
            }
        }

        // ── Val trade collection (training coins, 80/20 time split) ───────────────
        var valSwingRets = new List<double>();
        var valGridRets  = new List<double>();
        var valGsRets    = new List<double>();
        var valFlRets    = new List<double>();
        var valDlRets    = new List<double>();
        var valSlRets    = new List<double>();
        var valRsRets    = new List<double>();
        var valAgRets    = new List<double>();
        var valAll         = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var valNoRouter    = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var crashTrades    = new List<(DateTime Open, DateTime Close, double Return, double HalfKelly, string Strategy)>();
        int valCandleCount = 0;
        var valRawForEnrich = new List<(DateTime Time, double Return, double Conf, string Strategy, string Symbol, TimeSpan HoldDuration)>();
        var oosRawForEnrich = new List<(DateTime Time, double Return, double Conf, string Strategy, string Symbol, TimeSpan HoldDuration)>();

        foreach (var sym in Config.BacktestCoins)
        {
            if (!fetchedMap.TryGetValue(sym, out var entry)) continue;
            var (_, m15, h1) = entry;
            if (h1.Length < 300) continue;

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            if (volUsd.Count == 0 || volUsd[volUsd.Count / 2] < Config.MinMedianVolUsdM) continue;

            int h1Split  = DataSplit.Split(h1).Train.Length;
            int m15Split = h1Split * 4;
            var h1Train  = h1[..h1Split];
            var h1Val    = h1[h1Split..];
            var m15Train = m15[..m15Split];
            var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
            valCandleCount += h1Val.Length * 12;

            // FadeShort — screen on train, test on val
            {
                var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
                var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
                var (coinFsG, fsVarLabel) = StrategyPipeline.SelectVariantLabeled(fsVariants, m15);
                coinFsG ??= swingG;
                var fsTr = FadeShortSimulator.GetFadeShortReturns(coinFsG, screenH1, screenM15).Select(t => t.Return).ToList();
                if (fsTr.Count >= 5 && fsTr.Average() > 0
                    && Simulator.ProfitFactor(fsTr) >= 1.3
                    && Simulator.SortinoRatio(fsTr, screenH1.Length * 12) >= 0.5)
                {
                    double conf = Simulator.ComputeConfidence(fsTr);
                    var (_, hk) = StrategyStats.KellyFraction(fsTr);
                    double fsHk = Math.Min(hk, 0.05);
                    foreach (var (t, ret, _, _, _) in FadeShortSimulator.GetFadeShortReturns(coinFsG, h1Val, m15Val))
                    {
                        if (fundingSession?.IsCrowdedShort(t) == true) continue;
                        valSwingRets.Add(ret);
                        valAll.Add((t, ret, conf, "swing"));
                        valNoRouter.Add((t, ret, conf, "swing"));
                        valRawForEnrich.Add((t, ret, conf, "swing", sym, TimeSpan.FromHours(coinFsG.MaxHoldCandles)));
                        crashTrades.Add((t - TimeSpan.FromHours(coinFsG.MaxHoldCandles), t, ret, fsHk, "FadeShort"));
                        RouteValVol(fsVarLabel, sym, t, ret, conf, "swing", valCandleCount);
                    }
                }
            }

            // Grid
            if (h1Train.Length >= 100)
            {
                var (coinGridG, gridVarLabel) = StrategyPipeline.SelectVariantLabeled(gridVariants, m15);
                coinGridG ??= gridG;
                var gTr = GridSimulator.GetGridReturns(coinGridG, h1Train).Select(t => t.Return).ToList();
                if (gTr.Count >= 5 && gTr.Average() > 0
                    && Simulator.ProfitFactor(gTr) >= 1.2
                    && Simulator.SortinoRatio(gTr, h1Train.Length) >= 0.3)
                {
                    double conf = Simulator.ComputeConfidence(gTr);
                    var raw = GridSimulator.GetGridReturns(coinGridG, h1Val);
                    var gated = session != null
                        ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList()
                        : raw;
                    valGridRets.AddRange(gated.Select(t => t.Return));
                    foreach (var t in gated) valAll.Add((t.Time, t.Return, conf, "grid"));
                    foreach (var t in raw)   valNoRouter.Add((t.Time, t.Return, conf, "grid"));
                    foreach (var t in gated)
                    {
                        valRawForEnrich.Add((t.Time, t.Return, conf, "grid", sym, TimeSpan.FromHours(coinGridG.MaxHoldCandles)));
                        RouteValVol(gridVarLabel, sym, t.Time, t.Return, conf, "grid", valCandleCount);
                    }
                }
            }

            // GridShort
            if (gsG != null && h1Train.Length >= 100)
            {
                var gsTr = GridShortSimulator.GetGridShortReturns(gsG, h1Train).Select(t => t.Return).ToList();
                if (gsTr.Count >= 5 && gsTr.Average() > 0
                    && Simulator.ProfitFactor(gsTr) >= 1.2
                    && Simulator.SortinoRatio(gsTr, h1Train.Length) >= 0.3)
                {
                    double conf = Simulator.ComputeConfidence(gsTr);
                    var raw   = GridShortSimulator.GetGridShortReturns(gsG, h1Val);
                    var gated = session != null
                        ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.GridShort, t.Time)).ToList()
                        : raw;
                    valGsRets.AddRange(gated.Select(t => t.Return));
                    foreach (var t in gated) valAll.Add((t.Time, t.Return, conf, "gridshort"));
                    foreach (var t in raw)   valNoRouter.Add((t.Time, t.Return, conf, "gridshort"));
                    foreach (var t in gated)
                        valRawForEnrich.Add((t.Time, t.Return, conf, "gridshort", sym, TimeSpan.FromHours(gsG.MaxHoldCandles)));
                }
            }

            // FadeLong
            if (flG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                double conf = Simulator.ComputeConfidence(
                    FadeLongSimulator.GetFadeLongReturns(flG, h1Val, m15Val).Select(t => t.Return).ToList());
                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, h1Val, m15Val);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList()
                    : raw;
                valFlRets.AddRange(gated.Select(t => t.Return));
                foreach (var t in gated) valAll.Add((t.Time, t.Return, conf, "fadelong"));
                foreach (var t in raw)   valNoRouter.Add((t.Time, t.Return, conf, "fadelong"));
                foreach (var t in gated)
                    valRawForEnrich.Add((t.Time, t.Return, conf, "fadelong", sym, TimeSpan.FromHours(flG!.MaxHoldCandles)));
            }

            // DipLong
            if (dlG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var (coinDlG, dlVarLabel) = StrategyPipeline.SelectVariantLabeled(dlVariants, m15);
                coinDlG ??= dlG;
                double conf = Simulator.ComputeConfidence(
                    DipLongSimulator.GetDipLongReturns(coinDlG, h1Val, m15Val).Select(t => t.Return).ToList());
                double dlHk = Math.Min(coinDlG.PositionSizePct, 0.05);
                var raw   = DipLongSimulator.GetDipLongReturns(coinDlG, h1Val, m15Val);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                if (fundingSession != null)
                    gated = gated.Where(t => !fundingSession.IsCrowdedLong(t.Time)).ToList();
                valDlRets.AddRange(gated.Select(t => t.Return));
                foreach (var t in gated)
                {
                    valAll.Add((t.Time, t.Return, conf, "diplong"));
                    crashTrades.Add((t.Time - TimeSpan.FromHours(coinDlG.MaxHoldCandles), t.Time, t.Return, dlHk, "DipLong"));
                    RouteValVol(dlVarLabel, sym, t.Time, t.Return, conf, "diplong", valCandleCount);
                }
                foreach (var t in raw) valNoRouter.Add((t.Time, t.Return, conf, "diplong"));
                foreach (var t in gated)
                    valRawForEnrich.Add((t.Time, t.Return, conf, "diplong", sym, TimeSpan.FromHours(coinDlG.MaxHoldCandles)));
            }

            // SwingLong
            if (slG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var (coinSlG, slVarLabel) = StrategyPipeline.SelectVariantLabeled(slVariants, m15);
                coinSlG ??= slG;
                double conf = Simulator.ComputeConfidence(
                    SwingLongSimulator.GetSwingLongReturns(coinSlG, h1Val, m15Val).Select(t => t.Return).ToList());
                var raw   = SwingLongSimulator.GetSwingLongReturns(coinSlG, h1Val, m15Val);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                if (fundingSession != null)
                    gated = gated.Where(t => !fundingSession.IsCrowdedLong(t.Time)).ToList();
                valSlRets.AddRange(gated.Select(t => t.Return));
                foreach (var t in gated) valAll.Add((t.Time, t.Return, conf, "swing_long"));
                foreach (var t in raw)   valNoRouter.Add((t.Time, t.Return, conf, "swing_long"));
                foreach (var t in gated)
                {
                    valRawForEnrich.Add((t.Time, t.Return, conf, "swing_long", sym, TimeSpan.FromHours(coinSlG.MaxHoldCandles)));
                    RouteValVol(slVarLabel, sym, t.Time, t.Return, conf, "swing_long", valCandleCount);
                }
            }

            // RipShort
            if (rsG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var (coinRsG, rsVarLabel) = StrategyPipeline.SelectVariantLabeled(rsVariants, m15);
                coinRsG ??= rsG;
                double conf = Simulator.ComputeConfidence(
                    RipShortSimulator.GetRipShortReturns(coinRsG, h1Val, m15Val, fundingSession).Select(t => t.Return).ToList());
                var raw   = RipShortSimulator.GetRipShortReturns(coinRsG, h1Val, m15Val, fundingSession);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time)).ToList()
                    : raw;
                valRsRets.AddRange(gated.Select(t => t.Return));
                foreach (var t in gated) valAll.Add((t.Time, t.Return, conf, "ripshort"));
                foreach (var t in raw)   valNoRouter.Add((t.Time, t.Return, conf, "ripshort"));
                foreach (var t in gated)
                {
                    valRawForEnrich.Add((t.Time, t.Return, conf, "ripshort", sym, TimeSpan.FromHours(coinRsG.MaxHoldCandles)));
                    RouteValVol(rsVarLabel, sym, t.Time, t.Return, conf, "ripshort", valCandleCount);
                }
            }

            // AccumulationGrid
            if (agBullG != null && agBearG != null && h1Val.Length >= 100)
            {
                var bullTrades = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator.GetAccumulationReturns(agBullG, h1Val, MarketRegime.Bull);
                var bearTrades = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator.GetAccumulationReturns(agBearG, h1Val, MarketRegime.Bear);
                var raw = bullTrades.Concat(bearTrades).OrderBy(t => t.Time).ToList();
                double conf = Simulator.ComputeConfidence(raw.Select(t => t.Return).ToList());
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.AccumulationGrid, t.Time)).ToList()
                    : raw;
                valAgRets.AddRange(gated.Select(t => t.Return));
                foreach (var t in gated) valAll.Add((t.Time, t.Return, conf, "accumgrid"));
                foreach (var t in raw)   valNoRouter.Add((t.Time, t.Return, conf, "accumgrid"));
                foreach (var t in gated)
                    valRawForEnrich.Add((t.Time, t.Return, conf, "accumgrid", sym, TimeSpan.FromHours(agBullG!.MaxHoldBars)));
            }
        }

        // ── OOS trade collection (28 OOS coins, full history) ─────────────────────
        var oosSwingRets = new List<double>();
        var oosGridRets  = new List<double>();
        var oosGsRets    = new List<double>();
        var oosFlRets    = new List<double>();
        var oosDlRets    = new List<double>();
        var oosSlRets    = new List<double>();
        var oosRsRets    = new List<double>();
        var oosAgRets    = new List<double>();
        var oosAll       = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var oosNoRouter  = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        int oosCandleCount = 0;
        const double oosMinVol = 0.05;

        foreach (var sym in Config.OosCoins)
        {
            if (!fetchedMap.TryGetValue(sym, out var entry)) continue;
            var (_, m15, h1) = entry;
            if (h1.Length < 300) continue;

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            if (volUsd.Count == 0 || volUsd[volUsd.Count / 2] < oosMinVol) continue;
            oosCandleCount += h1.Length * 12;

            // FadeShort — screen on first 80% of OOS coin history
            {
                int oosSplit   = DataSplit.Split(h1).Train.Length;
                int oosM15Spl  = oosSplit * 4;
                var h1Screen   = h1[..oosSplit];
                var m15Screen  = m15[..Math.Min(oosM15Spl, m15.Length)];
                var (coinFsGOos, fsOosLabel) = StrategyPipeline.SelectVariantLabeled(fsVariants, m15);
                coinFsGOos ??= swingG;
                var screenRets = FadeShortSimulator.GetFadeShortReturns(coinFsGOos, h1Screen, m15Screen)
                    .Select(t => t.Return).ToList();
                bool oosScreenPass = screenRets.Count >= 5 && screenRets.Average() > 0
                    && Simulator.ProfitFactor(screenRets) >= 1.1;
                if (oosScreenPass)
                {
                    var trades = FadeShortSimulator.GetFadeShortReturns(coinFsGOos, h1, m15)
                        .Where(t => fundingSession?.IsCrowdedShort(t.Time) != true).ToList();
                    var vRet = trades.Select(t => t.Return).ToList();
                    if (vRet.Count >= 5)
                    {
                        double conf = Simulator.ComputeConfidence(vRet);
                        oosSwingRets.AddRange(vRet);
                        foreach (var (t, ret, _, _, _) in trades)
                        {
                            oosAll.Add((t, ret, conf, "swing"));
                            oosRawForEnrich.Add((t, ret, conf, "swing", sym, TimeSpan.FromHours(coinFsGOos.MaxHoldCandles)));
                            RouteOosVol(fsOosLabel, sym, t, ret, conf, "swing", oosCandleCount);
                        }
                    }
                }
            }

            // Grid
            {
                var (coinGridGOos, gridOosLabel) = StrategyPipeline.SelectVariantLabeled(gridVariants, m15);
                coinGridGOos ??= gridG;
                var raw   = GridSimulator.GetGridReturns(coinGridGOos, h1);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();
                if (vRet.Count >= 5)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosGridRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "grid"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "grid"));
                    foreach (var t in gated)
                    {
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "grid", sym, TimeSpan.FromHours(coinGridGOos.MaxHoldCandles)));
                        RouteOosVol(gridOosLabel, sym, t.Time, t.Return, conf, "grid", oosCandleCount);
                    }
                }
            }

            // GridShort
            if (gsG != null)
            {
                var raw   = GridShortSimulator.GetGridShortReturns(gsG, h1);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.GridShort, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();
                if (vRet.Count >= 5)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosGsRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "gridshort"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "gridshort"));
                    foreach (var t in gated)
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "gridshort", sym, TimeSpan.FromHours(gsG.MaxHoldCandles)));
                }
            }

            // FadeLong
            if (flG != null && m15.Length >= 1200)
            {
                var raw   = FadeLongSimulator.GetFadeLongReturns(flG, h1, m15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();
                if (vRet.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosFlRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "fadelong"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "fadelong"));
                    foreach (var t in gated)
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "fadelong", sym, TimeSpan.FromHours(flG!.MaxHoldCandles)));
                }
            }

            // DipLong
            if (dlG != null && m15.Length >= 1200)
            {
                var (coinDlGOos, dlOosLabel) = StrategyPipeline.SelectVariantLabeled(dlVariants, m15);
                coinDlGOos ??= dlG;
                var raw   = DipLongSimulator.GetDipLongReturns(coinDlGOos, h1, m15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                if (fundingSession != null)
                    gated = gated.Where(t => !fundingSession.IsCrowdedLong(t.Time)).ToList();
                var vRet  = gated.Select(t => t.Return).ToList();
                if (vRet.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosDlRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "diplong"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "diplong"));
                    foreach (var t in gated)
                    {
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "diplong", sym, TimeSpan.FromHours(coinDlGOos.MaxHoldCandles)));
                        RouteOosVol(dlOosLabel, sym, t.Time, t.Return, conf, "diplong", oosCandleCount);
                    }
                }
            }

            // SwingLong
            if (slG != null && m15.Length >= 1200)
            {
                var (coinSlGOos, slOosLabel) = StrategyPipeline.SelectVariantLabeled(slVariants, m15);
                coinSlGOos ??= slG;
                var raw   = SwingLongSimulator.GetSwingLongReturns(coinSlGOos, h1, m15);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                if (fundingSession != null)
                    gated = gated.Where(t => !fundingSession.IsCrowdedLong(t.Time)).ToList();
                var vRet  = gated.Select(t => t.Return).ToList();
                if (vRet.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosSlRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "swing_long"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "swing_long"));
                    foreach (var t in gated)
                    {
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "swing_long", sym, TimeSpan.FromHours(coinSlGOos.MaxHoldCandles)));
                        RouteOosVol(slOosLabel, sym, t.Time, t.Return, conf, "swing_long", oosCandleCount);
                    }
                }
            }

            // RipShort
            if (rsG != null && m15.Length >= 1200)
            {
                var (coinRsGOos, rsOosLabel) = StrategyPipeline.SelectVariantLabeled(rsVariants, m15);
                coinRsGOos ??= rsG;
                var raw   = RipShortSimulator.GetRipShortReturns(coinRsGOos, h1, m15, fundingSession);
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time)).ToList()
                    : raw;
                var vRet  = gated.Select(t => t.Return).ToList();
                if (vRet.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosRsRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "ripshort"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "ripshort"));
                    foreach (var t in gated)
                    {
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "ripshort", sym, TimeSpan.FromHours(coinRsGOos.MaxHoldCandles)));
                        RouteOosVol(rsOosLabel, sym, t.Time, t.Return, conf, "ripshort", oosCandleCount);
                    }
                }
            }

            // AccumulationGrid
            if (agBullG != null && agBearG != null && h1.Length >= 100)
            {
                var bullTrades = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator.GetAccumulationReturns(agBullG, h1, MarketRegime.Bull);
                var bearTrades = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator.GetAccumulationReturns(agBearG, h1, MarketRegime.Bear);
                var raw = bullTrades.Concat(bearTrades).OrderBy(t => t.Time).ToList();
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.AccumulationGrid, t.Time)).ToList()
                    : raw;
                var vRet = gated.Select(t => t.Return).ToList();
                if (vRet.Count > 0)
                {
                    double conf = Simulator.ComputeConfidence(vRet);
                    oosAgRets.AddRange(vRet);
                    foreach (var t in gated) oosAll.Add((t.Time, t.Return, conf, "accumgrid"));
                    foreach (var t in raw)   oosNoRouter.Add((t.Time, t.Return, conf, "accumgrid"));
                    foreach (var t in gated)
                        oosRawForEnrich.Add((t.Time, t.Return, conf, "accumgrid", sym, TimeSpan.FromHours(agBullG!.MaxHoldBars)));
                }
            }
        }

        // ── Apply concurrent cap ───────────────────────────────────────────────────
        static TimeSpan StratHold(string s, FadeShortGenotype sw, GridGenotype gr,
            FadeLongGenotype? fl, DipLongGenotype? dl, SwingLongGenotype? sl, RipShortGenotype? rs, GridGenotype? gs) => s switch
        {
            "swing"      => TimeSpan.FromHours(sw.MaxHoldCandles),
            "swing_long" => TimeSpan.FromHours(sl?.MaxHoldCandles ?? sw.MaxHoldCandles),
            "fadelong"   => TimeSpan.FromHours(fl?.MaxHoldCandles ?? sw.MaxHoldCandles),
            "diplong"    => TimeSpan.FromHours(dl?.MaxHoldCandles ?? sw.MaxHoldCandles),
            "ripshort"   => TimeSpan.FromHours(rs?.MaxHoldCandles ?? sw.MaxHoldCandles),
            "gridshort"  => TimeSpan.FromHours(gs?.MaxHoldCandles ?? gr.MaxHoldCandles),
            _            => TimeSpan.FromHours(gr.MaxHoldCandles),
        };

        // No longer `static`: it closes over btcSeries so the capped book can be graded through
        // StrategyEvaluation — the same call combinedbacktest and oosbacktest make, on the same
        // trade shape. This is the point where fulltest's numbers stop being its own.
        List<(DateTime Time, double Return, double Conf, string Strategy)> ApplyCap(
            List<(DateTime Time, double Return, double Conf, string Strategy)> trades, string label)
        {
            if (trades.Count == 0) return trades;
            trades.Sort((a, b) => a.Time.CompareTo(b.Time));
            var capIn = trades.Select(t => new PortfolioReplay.Trade(t.Strategy, t.Time,
                t.Strategy switch {
                    "swing"      => TimeSpan.FromHours(48),
                    "swing_long" => TimeSpan.FromHours(48),
                    "diplong"    => TimeSpan.FromHours(48),
                    "fadelong"   => TimeSpan.FromHours(72),
                    "ripshort"   => TimeSpan.FromHours(72),
                    "gridshort"  => TimeSpan.FromHours(72),
                    _            => TimeSpan.FromHours(72),
                }, t.Return, t.Conf)).ToList();
            var capped = PortfolioReplay.FilterByConcurrentCap(capIn, directionalCap: Config.MaxDirectionalConcurrent);
            StrategyEvaluation.Report(label, capped, btcSeries,
                                      csvPath: $"reports/fulltest_{label.ToLowerInvariant().Replace(' ', '_')}_trades.csv");
            return capped.Select(t => (t.EntryTime, t.Return, t.Conf, t.Strategy)).ToList();
        }

        valAll = ApplyCap(valAll, "FULLTEST VAL BOOK");
        oosAll = ApplyCap(oosAll, "FULLTEST OOS BOOK");

        // Helper: trades → exposure sim input
        List<(DateTime, double, double, TimeSpan)> ToSim(
            List<(DateTime Time, double Return, double Conf, string Strategy)> t) =>
            t.Select(x => (x.Time, x.Return, x.Conf, StratHold(x.Strategy, swingG, gridG, flG, dlG, slG, rsG, gsG))).ToList();

        // Guarded variant: ATR entry gate first (blocks high-ATR DipLong/SwingLong),
        // then confidence scaling. Strategy preserved for the portfolio-DD gate inside the simulator.
        List<(DateTime, double, double, TimeSpan, string)> ToSimGuarded(
            List<(DateTime Time, double Return, double Conf, string Strategy)> t,
            DynamicGuardSession gs) =>
            t.Where(x => !gs.IsEntryBlocked(x.Time, x.Strategy))
             .Select(x => (x.Time, x.Return,
                DynamicGuardSession.IsGuarded(x.Strategy) ? x.Conf * gs.GetMult(x.Time, x.Strategy) : x.Conf,
                StratHold(x.Strategy, swingG, gridG, flG, dlG, slG, rsG, gsG), x.Strategy)).ToList();

        // 5-tuple version of ToSim (adds Strategy) — used for DD-gate-aware guarded simulation.
        List<(DateTime, double, double, TimeSpan, string)> ToSim5(
            List<(DateTime Time, double Return, double Conf, string Strategy)> t) =>
            t.Select(x => (x.Time, x.Return, x.Conf,
                StratHold(x.Strategy, swingG, gridG, flG, dlG, slG, rsG, gsG), x.Strategy)).ToList();

        // Strip Strategy from 5-tuple for callers that only need 4-tuple (PortMetrics, etc.)
        static List<(DateTime, double, double, TimeSpan)> Drop5(
            List<(DateTime, double, double, TimeSpan, string)> t) =>
            t.Select(x => (x.Item1, x.Item2, x.Item3, x.Item4)).ToList();

        var valSim  = ToSim(valAll);
        var oosSim  = ToSim(oosAll);
        var valSimG = dgSession != null ? ToSimGuarded(valAll,  dgSession) : ToSim5(valAll);
        var oosSimG = dgSession != null ? ToSimGuarded(oosAll, dgSession) : ToSim5(oosAll);

        // Pre-compute no-router sims (needed by Section 3 year-by-year and Section 6)
        static TimeSpan HoldFor(string s) => s switch {
            "swing" or "swing_long" or "diplong" => TimeSpan.FromHours(48),
            _ => TimeSpan.FromHours(72),
        };
        valNoRouter.Sort((a, b) => a.Time.CompareTo(b.Time));
        oosNoRouter.Sort((a, b) => a.Time.CompareTo(b.Time));
        var nrFilteredTrades    = PortfolioReplay.FilterByConcurrentCap(valNoRouter.Select(t => new PortfolioReplay.Trade(t.Strategy, t.Time, HoldFor(t.Strategy), t.Return, t.Conf)).ToList(), directionalCap: Config.MaxDirectionalConcurrent).ToList();
        var nrFiltered          = nrFilteredTrades.Select(t => (t.EntryTime, t.Return, t.Conf, StratHold(t.Strategy, swingG, gridG, flG, dlG, slG, rsG, gsG))).ToList();
        var oosNrFilteredTrades = PortfolioReplay.FilterByConcurrentCap(oosNoRouter.Select(t => new PortfolioReplay.Trade(t.Strategy, t.Time, HoldFor(t.Strategy), t.Return, t.Conf)).ToList(), directionalCap: Config.MaxDirectionalConcurrent).ToList();
        var oosNrFiltered       = oosNrFilteredTrades.Select(t => (t.EntryTime, t.Return, t.Conf, StratHold(t.Strategy, swingG, gridG, flG, dlG, slG, rsG, gsG))).ToList();

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 1: STRATEGY PERFORMANCE
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  STRATEGY PERFORMANCE");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"Strategy",-12}  {"Trades",6}  {"WR",5}  {"PF",5}  {"Avg%",8}  |  {"Trades",6}  {"WR",5}  {"PF",5}  {"Avg%",8}");
        Console.WriteLine($"  {"",12}  {$"── ({DataSplit.ValLabel}) ─────────────────",30}  |  {"── OOS (28 coins) ────────────",29}");
        Console.WriteLine($"  {new string('-', 86)}");

        static string FmtRow(List<double> r) => r.Count > 0
            ? $"{r.Count,6}  {(double)r.Count(x => x > 0)/r.Count,5:P0}  {Simulator.ProfitFactor(r),5:F2}  {r.Average(),+8:F2}%"
            : $"{"—",6}  {"—",5}  {"—",5}  {"—",8}";

        Console.WriteLine($"  {"FadeShort",-12}  {FmtRow(valSwingRets)}  |  {FmtRow(oosSwingRets)}");
        Console.WriteLine($"  {"Grid",-12}  {FmtRow(valGridRets)}  |  {FmtRow(oosGridRets)}");
        Console.WriteLine($"  {"DipLong",-12}  {FmtRow(valDlRets)}  |  {FmtRow(oosDlRets)}");
        Console.WriteLine($"  {"SwingLong",-12}  {FmtRow(valSlRets)}  |  {FmtRow(oosSlRets)}");
        Console.WriteLine($"  {"FadeLong",-12}  {FmtRow(valFlRets)}  |  {FmtRow(oosFlRets)}");
        Console.WriteLine($"  {"RipShort",-12}  {FmtRow(valRsRets)}  |  {FmtRow(oosRsRets)}");
        Console.WriteLine($"  {"GridShort",-12}  {FmtRow(valGsRets)}  |  {FmtRow(oosGsRets)}");
        Console.WriteLine($"  {"AccumGrid",-12}  {FmtRow(valAgRets)}  |  {FmtRow(oosAgRets)}");

        var valHighVolRet  = valHighVolTrades.Select(t => t.Return).ToList();
        var valLowVolRet   = valLowVolTrades.Select(t => t.Return).ToList();
        var oosHighVolRet  = oosHighVolTrades.Select(t => t.Return).ToList();
        var oosLowVolRet   = oosLowVolTrades.Select(t => t.Return).ToList();

        if (valHighVolRet.Count > 0 || oosHighVolRet.Count > 0)
        {
            Console.WriteLine($"\n  ── High-Vol Variants (ATR≥1.5×) ──");
            if (valHighVolRet.Count > 0)
            {
                int hvw = valHighVolRet.Count(x => x > 0);
                Console.WriteLine($"  Val:  {valHighVolRet.Count} trades  WR={hvw}/{valHighVolRet.Count} ({(double)hvw/valHighVolRet.Count:P0})  PF={Simulator.ProfitFactor(valHighVolRet):F2}  Avg={valHighVolRet.Average():+0.00}%");
                Console.WriteLine($"        Sharpe={Simulator.SharpeRatio(valHighVolRet, valHighVolVCC):F2}  Sortino={Simulator.SortinoRatio(valHighVolRet, valHighVolVCC):F2}  Calmar={Simulator.CalmarRatio(valHighVolRet):F2}");
            }
            if (oosHighVolRet.Count > 0)
            {
                int ohvw = oosHighVolRet.Count(x => x > 0);
                Console.WriteLine($"  OOS:  {oosHighVolRet.Count} trades  WR={ohvw}/{oosHighVolRet.Count} ({(double)ohvw/oosHighVolRet.Count:P0})  PF={Simulator.ProfitFactor(oosHighVolRet):F2}  Avg={oosHighVolRet.Average():+0.00}%");
                Console.WriteLine($"        Sharpe={Simulator.SharpeRatio(oosHighVolRet, oosHighVolVCC):F2}  Sortino={Simulator.SortinoRatio(oosHighVolRet, oosHighVolVCC):F2}  Calmar={Simulator.CalmarRatio(oosHighVolRet):F2}");
            }
        }

        if (valLowVolRet.Count > 0 || oosLowVolRet.Count > 0)
        {
            Console.WriteLine($"\n  ── Low-Vol Variants (ATR≤0.8×) ──");
            if (valLowVolRet.Count > 0)
            {
                int lvw = valLowVolRet.Count(x => x > 0);
                Console.WriteLine($"  Val:  {valLowVolRet.Count} trades  WR={lvw}/{valLowVolRet.Count} ({(double)lvw/valLowVolRet.Count:P0})  PF={Simulator.ProfitFactor(valLowVolRet):F2}  Avg={valLowVolRet.Average():+0.00}%");
                Console.WriteLine($"        Sharpe={Simulator.SharpeRatio(valLowVolRet, valLowVolVCC):F2}  Sortino={Simulator.SortinoRatio(valLowVolRet, valLowVolVCC):F2}  Calmar={Simulator.CalmarRatio(valLowVolRet):F2}");
            }
            if (oosLowVolRet.Count > 0)
            {
                int olvw = oosLowVolRet.Count(x => x > 0);
                Console.WriteLine($"  OOS:  {oosLowVolRet.Count} trades  WR={olvw}/{oosLowVolRet.Count} ({(double)olvw/oosLowVolRet.Count:P0})  PF={Simulator.ProfitFactor(oosLowVolRet):F2}  Avg={oosLowVolRet.Average():+0.00}%");
                Console.WriteLine($"        Sharpe={Simulator.SharpeRatio(oosLowVolRet, oosLowVolVCC):F2}  Sortino={Simulator.SortinoRatio(oosLowVolRet, oosLowVolVCC):F2}  Calmar={Simulator.CalmarRatio(oosLowVolRet):F2}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 2: PORTFOLIO SIMULATIONS
        // ══════════════════════════════════════════════════════════════════════════
        static (string Ret, string Ann, string DD, string Sh) PortMetrics(
            List<(DateTime Time, double Return, double Conf, TimeSpan Hold)> trades,
            Simulator.PortfolioResult p)
        {
            if (trades.Count < 2) return ("—", "—", "—", "—");
            double r    = (p.EndBalance - 100.0) / 100.0 * 100;
            double days = (trades[^1].Time - trades[0].Time).TotalDays;
            double ann  = days > 0 ? (Math.Pow(1 + r / 100.0, 365.0 / days) - 1) * 100 : 0;
            var rets    = trades.Select(t => t.Return).ToList();
            double sh   = rets.Count >= 5 ? Simulator.SharpeRatio(rets, (int)(days * 24)) : 0;
            return ($"{r:+0.0;-0.0}%", $"{ann:+0.0;-0.0}%/yr", $"{p.MaxDrawdownPct:F1}%", $"{sh:F2}");
        }

        // Fraction in [0,1] — see Simulator.SimulatePortfolioExposureCapped. Fallback = gate off.
        double ddGate      = dgGeno?.DdEntryGatePct         ?? DynamicGuardGenotype.DdGateDisabled;
        double capMin      = dgGeno?.ConfLossCapMin          ?? 1.0;
        double capMax      = dgGeno?.ConfLossCapMax          ?? 1.0;
        double ppThreshold = dgGeno?.ProfitProtectThreshold  ?? 1.0;
        double ppDrawback  = dgGeno?.ProfitProtectDrawback   ?? 0.10;
        double ppFactor    = dgGeno?.ProfitProtectFactor     ?? 1.0;

        var val5p   = valSim.Count  > 0 ? Simulator.SimulatePortfolioExposureCapped(valSim,  Config.MaxTotalExposurePct, maxPositionFrac: 0.05) : default!;
        var valKel  = valSim.Count  > 0 ? Simulator.SimulatePortfolioExposureCapped(valSim,  Config.MaxTotalExposurePct) : default!;
        var oos5p   = oosSim.Count  > 0 ? Simulator.SimulatePortfolioExposureCapped(oosSim,  Config.MaxTotalExposurePct, maxPositionFrac: 0.05) : default!;
        var oosKel  = oosSim.Count  > 0 ? Simulator.SimulatePortfolioExposureCapped(oosSim,  Config.MaxTotalExposurePct) : default!;
        var val5pG  = valSimG.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(valSimG, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, ddLongEntryGatePct: ddGate, confLossCapMin: capMin, confLossCapMax: capMax, profitProtectThreshold: ppThreshold, profitProtectDrawback: ppDrawback, profitProtectFactor: ppFactor) : default!;
        var valKelG = valSimG.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(valSimG, Config.MaxTotalExposurePct, ddLongEntryGatePct: ddGate, confLossCapMin: capMin, confLossCapMax: capMax, profitProtectThreshold: ppThreshold, profitProtectDrawback: ppDrawback, profitProtectFactor: ppFactor) : default!;
        var oos5pG  = oosSimG.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(oosSimG, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, ddLongEntryGatePct: ddGate, confLossCapMin: capMin, confLossCapMax: capMax, profitProtectThreshold: ppThreshold, profitProtectDrawback: ppDrawback, profitProtectFactor: ppFactor) : default!;
        var oosKelG = oosSimG.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(oosSimG, Config.MaxTotalExposurePct, ddLongEntryGatePct: ddGate, confLossCapMin: capMin, confLossCapMax: capMax, profitProtectThreshold: ppThreshold, profitProtectDrawback: ppDrawback, profitProtectFactor: ppFactor) : default!;

        var valSimG4 = Drop5(valSimG);
        var oosSimG4 = Drop5(oosSimG);

        var (vr5, va5, vd5, vs5)    = valSim.Count   > 0 ? PortMetrics(valSim,   val5p)   : ("—","—","—","—");
        var (vrK, vaK, vdK, vsK)    = valSim.Count   > 0 ? PortMetrics(valSim,   valKel)  : ("—","—","—","—");
        var (or5, oa5, od5, os5)    = oosSim.Count   > 0 ? PortMetrics(oosSim,   oos5p)   : ("—","—","—","—");
        var (orK, oaK, odK, osK)    = oosSim.Count   > 0 ? PortMetrics(oosSim,   oosKel)  : ("—","—","—","—");
        var (vr5G, va5G, vd5G, vs5G)  = valSimG4.Count > 0 ? PortMetrics(valSimG4, val5pG)  : ("—","—","—","—");
        var (vrKG, vaKG, vdKG, vsKG)  = valSimG4.Count > 0 ? PortMetrics(valSimG4, valKelG) : ("—","—","—","—");
        var (or5G, oa5G, od5G, os5G)  = oosSimG4.Count > 0 ? PortMetrics(oosSimG4, oos5pG)  : ("—","—","—","—");
        var (orKG, oaKG, odKG, osKG)  = oosSimG4.Count > 0 ? PortMetrics(oosSimG4, oosKelG) : ("—","—","—","—");

        bool hasGuard = dgSession != null;
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  PORTFOLIO SIMULATION (€100 start · 30% total cap)");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"",16}  {"Val 5%cap",12}  {"Val Kelly(15%)",12}  {"OOS 5%cap",12}  {"OOS Kelly(15%)",12}");
        Console.WriteLine($"  {new string('-', 72)}");
        string lbl  = "No guard";
        string lblG = "Dyn guard";
        Console.WriteLine($"  {lbl,-16}  {vr5,12}  {vrK,12}  {or5,12}  {orK,12}");
        if (hasGuard) Console.WriteLine($"  {lblG,-16}  {vr5G,12}  {vrKG,12}  {or5G,12}  {orKG,12}");
        Console.WriteLine($"  {new string('-', 72)}");
        Console.WriteLine($"  {"Annualised",16}  {va5,12}  {vaK,12}  {oa5,12}  {oaK,12}");
        if (hasGuard) Console.WriteLine($"  {"",16}  {va5G,12}  {vaKG,12}  {oa5G,12}  {oaKG,12}");
        Console.WriteLine($"  {"Max DD",16}  {vd5,12}  {vdK,12}  {od5,12}  {odK,12}");
        if (hasGuard) Console.WriteLine($"  {"",16}  {vd5G,12}  {vdKG,12}  {od5G,12}  {odKG,12}");
        Console.WriteLine($"  {"Sharpe",16}  {vs5,12}  {vsK,12}  {os5,12}  {osK,12}");
        if (hasGuard) Console.WriteLine($"  {"",16}  {vs5G,12}  {vsKG,12}  {os5G,12}  {osKG,12}");

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 3: YEAR-BY-YEAR (val window)
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  YEAR-BY-YEAR (val window)");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"Year",-6}  {"Regime",7}  {"N",4}  {"5% w/router",12}  {"5% no router",13}  {"Router Δ",9}  {"Kelly(15%) w/r",10}  {"Kelly(15%) no r",11}");
        Console.WriteLine($"  {new string('-', 84)}");

        foreach (int yr in valSim.Select(t => t.Item1.Year).Distinct().OrderBy(y => y))
        {
            var yrSim = valSim.Where(t => t.Item1.Year == yr).ToList();
            if (yrSim.Count == 0) continue;
            var yrNr  = nrFiltered.Where(t => t.Item1.Year == yr).ToList();
            var p5    = Simulator.SimulatePortfolioExposureCapped(yrSim, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var pK    = Simulator.SimulatePortfolioExposureCapped(yrSim, Config.MaxTotalExposurePct);
            var p5nr  = yrNr.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(yrNr, Config.MaxTotalExposurePct, maxPositionFrac: 0.05) : default!;
            var pKnr  = yrNr.Count > 0 ? Simulator.SimulatePortfolioExposureCapped(yrNr, Config.MaxTotalExposurePct) : default!;
            double r5   = p5.EndBalance - 100;
            double r5nr = yrNr.Count > 0 ? p5nr.EndBalance - 100 : double.NaN;
            double rK   = pK.EndBalance - 100;
            double rKnr = yrNr.Count > 0 ? pKnr.EndBalance - 100 : double.NaN;
            string reg  = btcSeries != null
                ? btcSeries.Where(b => b.Time.Year == yr).GroupBy(b => b.Regime)
                    .OrderByDescending(g => g.Count()).FirstOrDefault()?.Key.ToString()[..4] ?? "?"
                : "?";
            string nr5str  = double.IsNaN(r5nr)  ? "—" : $"{r5nr,+6:F1}%";
            string nrKstr  = double.IsNaN(rKnr)  ? "—" : $"{rKnr,+6:F1}%";
            string delta5  = double.IsNaN(r5nr)  ? "—" : $"{r5-r5nr,+6:F1}pp";
            Console.WriteLine($"  {yr,-6}  {reg,7}  {yrSim.Count,4}  {r5,+10:F1}%    {nr5str,10}    {delta5,8}  {rK,+8:F1}%    {nrKstr,9}");
        }

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 4: STATISTICAL EDGE (val window)
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  STATISTICAL EDGE (val window)");
        Console.WriteLine($"{new string('═', 88)}");
        Console.WriteLine($"  {"Strategy",-12}  {"N",5}  {"Mean%",8}  {"t-stat",7}  {"p-val",7}  {"DSR",5}  {"P(μ>0)",7}");
        Console.WriteLine($"  {new string('-', 65)}");

        void PrintEdge(string name, List<double> rets)
        {
            if (rets.Count < 10) { Console.WriteLine($"  {name,-12}  {rets.Count,5}  (insufficient trades)"); return; }
            var tt    = StrategyStats.OneSampleT(rets);
            var boot  = StrategyStats.Bootstrap(rets);
            double dsr = StrategyStats.DeflatedSharpe(rets, valCandleCount);
            string sig = tt.Sig99 ? "***" : tt.Sig95 ? "*" : "";
            Console.WriteLine($"  {name,-12}  {rets.Count,5}  {tt.Mean,+8:F3}  {tt.T,7:F2}  {tt.PValue,7:F4}  {dsr,5:F3}  {boot.ProbPositive,7:P1}  {sig}");
        }

        PrintEdge("FadeShort",  valSwingRets);
        PrintEdge("Grid",       valGridRets);
        PrintEdge("DipLong",    valDlRets);
        PrintEdge("SwingLong",  valSlRets);
        PrintEdge("FadeLong",   valFlRets);
        PrintEdge("RipShort",   valRsRets);
        PrintEdge("GridShort",  valGsRets);
        PrintEdge("AccumGrid",  valAgRets);

        Console.WriteLine($"\n  ── Strategy comparisons ──");
        if (valDlRets.Count >= 10 && valSwingRets.Count >= 10)
            StrategyStats.Compare("DipLong", valDlRets, "FadeShort", valSwingRets, valCandleCount);
        if (valSlRets.Count >= 10 && valSwingRets.Count >= 10)
            StrategyStats.Compare("SwingLong", valSlRets, "FadeShort", valSwingRets, valCandleCount);
        if (valDlRets.Count >= 10 && valSlRets.Count >= 10)
            StrategyStats.Compare("DipLong", valDlRets, "SwingLong", valSlRets, valCandleCount);

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 5: CRASH / RALLY ANALYSIS
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  CRASH / RALLY ANALYSIS  ({crashTrades.Count} trades in crash windows)");
        Console.WriteLine($"{new string('═', 88)}");

        if (fetchedMap.TryGetValue("BTCUSDT", out var btcForCrash))
        {
            // Use full BTC history — cache goes back to 2020, covers COVID/LUNA/FTX
            CrashAnalyser.Report(CrashAnalyser.DetectCrashes(btcForCrash.h1), crashTrades);
            CrashAnalyser.ReportRallies(CrashAnalyser.DetectRallies(btcForCrash.h1), crashTrades);
            CrashAnalyser.SyntheticWorstCase(crashTrades);
        }
        else Console.WriteLine("  BTC data unavailable — skipping crash/rally analysis");

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 6: ROUTER IMPACT (val + OOS)
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  ROUTER IMPACT");
        Console.WriteLine($"{new string('═', 88)}");

        // Val no-router (nrFilteredTrades / nrFiltered already computed above)
        var nrPort5 = nrFiltered.Count > 0
            ? Simulator.SimulatePortfolioExposureCapped(nrFiltered, Config.MaxTotalExposurePct, maxPositionFrac: 0.05)
            : default!;
        var nrPortK = nrFiltered.Count > 0
            ? Simulator.SimulatePortfolioExposureCapped(nrFiltered, Config.MaxTotalExposurePct)
            : default!;

        // OOS no-router (oosNrFilteredTrades / oosNrFiltered already computed above)
        var oosNrPort5 = oosNrFiltered.Count > 0
            ? Simulator.SimulatePortfolioExposureCapped(oosNrFiltered, Config.MaxTotalExposurePct, maxPositionFrac: 0.05)
            : default!;
        var oosNrPortK = oosNrFiltered.Count > 0
            ? Simulator.SimulatePortfolioExposureCapped(oosNrFiltered, Config.MaxTotalExposurePct)
            : default!;

        double r5R   = valSim.Count > 0        ? (val5p.EndBalance   - 100) / 100 * 100 : 0;
        double r5NR  = nrFiltered.Count > 0    ? (nrPort5.EndBalance  - 100) / 100 * 100 : 0;
        double rKR   = valSim.Count > 0        ? (valKel.EndBalance   - 100) / 100 * 100 : 0;
        double rKNR  = nrFiltered.Count > 0    ? (nrPortK.EndBalance  - 100) / 100 * 100 : 0;
        double or5R  = oosSim.Count > 0        ? (oos5p.EndBalance    - 100) / 100 * 100 : 0;
        double or5NR = oosNrFiltered.Count > 0 ? (oosNrPort5.EndBalance - 100) / 100 * 100 : 0;
        double orKR  = oosSim.Count > 0        ? (oosKel.EndBalance   - 100) / 100 * 100 : 0;
        double orKNR = oosNrFiltered.Count > 0 ? (oosNrPortK.EndBalance - 100) / 100 * 100 : 0;

        string Sign(double v) => v >= 0 ? "+" : "";

        Console.WriteLine($"  {"",12}  {$"── ({DataSplit.ValLabel}) ──────────────",26}  {"── OOS (28 coins) ─────────",26}");
        Console.WriteLine($"  {"Strategy",-12}  {"W/ router",9}  {"No router",9}  {"Δ",5}  |  {"W/ router",9}  {"No router",9}  {"Δ",5}");
        Console.WriteLine($"  {new string('-', 78)}");
        int cntSwing    = valAll.Count(t => t.Strategy == "swing");
        int oosCntSwing = oosAll.Count(t => t.Strategy == "swing");
        Console.WriteLine($"  {"FadeShort",-12}  {cntSwing,9}  {cntSwing,9}  {"—",5}  |  {oosCntSwing,9}  {oosCntSwing,9}  {"—",5}");
        foreach (var (strat, label) in new[] { ("grid","Grid"), ("diplong","DipLong"), ("fadelong","FadeLong"), ("swing_long","SwingLong"), ("ripshort","RipShort"), ("gridshort","GridShort") })
        {
            int vW = valAll.Count(t => t.Strategy == strat);
            int vN = nrFilteredTrades.Count(t => t.Strategy == strat);
            int oW = oosAll.Count(t => t.Strategy == strat);
            int oN = oosNrFilteredTrades.Count(t => t.Strategy == strat);
            Console.WriteLine($"  {label,-12}  {vW,9}  {vN,9}  {vN-vW,+5}  |  {oW,9}  {oN,9}  {oN-oW,+5}");
        }
        Console.WriteLine($"  {"Total",-12}  {valAll.Count,9}  {nrFiltered.Count,9}  {"",5}  |  {oosAll.Count,9}  {oosNrFiltered.Count,9}");
        Console.WriteLine();
        Console.WriteLine($"  {"Scenario",-22}  {"Val ret",8}  {"Val DD",7}  |  {"OOS ret",8}  {"OOS DD",7}");
        Console.WriteLine($"  {new string('-', 64)}");
        Console.WriteLine($"  {"5%cap · with router",-22}  {r5R,+7:F1}%  {(valSim.Count>0?val5p.MaxDrawdownPct:0),6:F1}%  |  {or5R,+7:F1}%  {(oosSim.Count>0?oos5p.MaxDrawdownPct:0),6:F1}%");
        Console.WriteLine($"  {"5%cap · no router",-22}  {r5NR,+7:F1}%  {(nrFiltered.Count>0?nrPort5.MaxDrawdownPct:0),6:F1}%  |  {or5NR,+7:F1}%  {(oosNrFiltered.Count>0?oosNrPort5.MaxDrawdownPct:0),6:F1}%");
        Console.WriteLine($"  {"Kelly(15%) · with router",-22}  {rKR,+7:F1}%  {(valSim.Count>0?valKel.MaxDrawdownPct:0),6:F1}%  |  {orKR,+7:F1}%  {(oosSim.Count>0?oosKel.MaxDrawdownPct:0),6:F1}%");
        Console.WriteLine($"  {"Kelly(15%) · no router",-22}  {rKNR,+7:F1}%  {(nrFiltered.Count>0?nrPortK.MaxDrawdownPct:0),6:F1}%  |  {orKNR,+7:F1}%  {(oosNrFiltered.Count>0?oosNrPortK.MaxDrawdownPct:0),6:F1}%");
        Console.WriteLine($"  Val  router edge: 5%cap {Sign(r5R-r5NR)}{r5R-r5NR:F1}pp  /  Kelly(15%) {Sign(rKR-rKNR)}{rKR-rKNR:F1}pp");
        Console.WriteLine($"  OOS  router edge: 5%cap {Sign(or5R-or5NR)}{or5R-or5NR:F1}pp  /  Kelly(15%) {Sign(orKR-orKNR)}{orKR-orKNR:F1}pp");

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 7: FORWARD PROJECTION
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  FORWARD PROJECTION (last 90d val run-rate → 1yr)");
        Console.WriteLine($"{new string('═', 88)}");

        if (valSim.Count >= 10)
        {
            var lastDate = valSim[^1].Item1;
            var slice90  = valSim.Where(t => t.Item1 >= lastDate - TimeSpan.FromDays(90)).ToList();
            if (slice90.Count >= 10)
            {
                double span = (lastDate - slice90[0].Item1).TotalDays;
                var sp5  = Simulator.SimulatePortfolioExposureCapped(slice90, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
                var spK  = Simulator.SimulatePortfolioExposureCapped(slice90, Config.MaxTotalExposurePct);
                double r5  = (sp5.EndBalance - 100.0) / 100.0;
                double rK  = (spK.EndBalance - 100.0) / 100.0;
                double a5  = span > 0 ? (Math.Pow(1 + r5, 365.0 / span) - 1) * 100 : 0;
                double aK  = span > 0 ? (Math.Pow(1 + rK, 365.0 / span) - 1) * 100 : 0;
                Console.WriteLine($"  Slice: {slice90.Count} trades  {slice90[0].Item1:yyyy-MM-dd} → {lastDate:yyyy-MM-dd}  ({span:F0}d)");
                Console.WriteLine($"  5%cap: {r5*100:+0.0;-0.0}% in {span:F0}d  →  projected {a5:+0.0;-0.0}%/yr  DD {sp5.MaxDrawdownPct:F1}%");
                Console.WriteLine($"  Kelly(15%): {rK*100:+0.0;-0.0}% in {span:F0}d  →  projected {aK:+0.0;-0.0}%/yr  DD {spK.MaxDrawdownPct:F1}%");
                Console.WriteLine("  Warning: Assumes stable regime — recent bull market may not persist.");
            }
            else Console.WriteLine($"  Only {slice90.Count} trades in last 90d — insufficient for projection.");
        }
        else Console.WriteLine("  Insufficient val trades for projection.");

        // ── Section 8: Dynamic Guard ───────────────────────────────────────────────
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  DYNAMIC GUARD (BTC 4H ATR/momentum · proactive · Grid/DipLong/SwingLong)");
        Console.WriteLine($"{new string('═', 88)}");

        if (dgSession == null || dgGeno == null)
        {
            Console.WriteLine("  No dynamic guard genotype found — run 'dynamicguardtrain' to train one.");
        }
        else
        {
            Console.WriteLine($"  Genotype: {dgGeno}\n");

            // Reuse the already-computed 5%cap portfolio results from the portfolio section.
            // Baseline = val5p / oos5p (no guard, cap-filtered), Guarded = val5pG / oos5pG.
            // This ensures the guard section baseline exactly matches the "No guard" portfolio row.
            string FmtDG(Simulator.PortfolioResult p) =>
                $"ret={p.EndBalance - 100:+0.0;-0.0}%  DD={p.MaxDrawdownPct:F1}%  Calmar={(p.EndBalance - 100) / Math.Max(p.MaxDrawdownPct, 1.0):F1}";

            Console.WriteLine($"  {"",12}  {$"── ({DataSplit.ValLabel}) ──────────────────────────────",43}  {"── OOS ──────────────────────────────",37}");
            Console.WriteLine($"  {"Baseline",-12}  {FmtDG(val5p),-43}  {FmtDG(oos5p),-37}");
            Console.WriteLine($"  {"Guarded",-12}  {FmtDG(val5pG),-43}  {FmtDG(oos5pG),-37}");

            static string Sgn(double v) => $"{(v >= 0 ? "+" : "")}{v:F1}";
            Console.WriteLine($"\n  Val Δ: return {Sgn(val5pG.EndBalance - val5p.EndBalance)}%  DD {Sgn(val5pG.MaxDrawdownPct - val5p.MaxDrawdownPct)}pp");
            Console.WriteLine($"  OOS Δ: return {Sgn(oos5pG.EndBalance - oos5p.EndBalance)}%  DD {Sgn(oos5pG.MaxDrawdownPct - oos5p.MaxDrawdownPct)}pp");

            // Avg multiplier per strategy (uses post-cap valAll which matches portfolio section)
            Console.WriteLine($"\n  Avg guard multiplier per strategy (val):");
            foreach (var strat in new[] { "grid", "diplong", "swing_long" })
            {
                var forStrat = valAll.Where(t => t.Strategy == strat).ToList();
                if (forStrat.Count == 0) continue;
                double avgMult = forStrat.Average(t => dgSession.GetMult(t.Time));
                Console.WriteLine($"    {strat,-12}  {avgMult:F3}×  ({forStrat.Count} trades)");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // SECTION 10: OOS KELLY DD INVESTIGATION
        // ══════════════════════════════════════════════════════════════════════════
        Console.WriteLine($"\n{new string('═', 88)}");
        Console.WriteLine($"  OOS KELLY DD INVESTIGATION");
        Console.WriteLine($"{new string('═', 88)}");

        if (oosSim.Count > 0)
        {
            // Per-year OOS Kelly breakdown
            Console.WriteLine($"\n  ── OOS Kelly by year ──");
            Console.WriteLine($"  {"Year",-6}  {"N",4}  {"Return",8}  {"DD",6}  Strategies");
            Console.WriteLine($"  {new string('-', 70)}");
            foreach (int yr in oosSim.Select(t => t.Item1.Year).Distinct().OrderBy(y => y))
            {
                var yrSim = oosSim.Where(t => t.Item1.Year == yr).ToList();
                if (yrSim.Count == 0) continue;
                var pK = Simulator.SimulatePortfolioExposureCapped(yrSim, Config.MaxTotalExposurePct);
                double rK = pK.EndBalance - 100;
                var stratCounts = oosAll
                    .Where(t => t.Time.Year == yr)
                    .GroupBy(t => t.Strategy)
                    .Select(g => $"{g.Key}:{g.Count()}")
                    .ToArray();
                Console.WriteLine($"  {yr,-6}  {yrSim.Count,4}  {rK,+7:F1}%  {pK.MaxDrawdownPct,5:F1}%  {string.Join("  ", stratCounts)}");
            }

            // Worst-DD event: peak/trough timestamps + trade decomposition
            var (_, ddEvent, _) = Simulator.SimulateExposureCappedWithCurve(oosSim, Config.MaxTotalExposurePct);

            if (ddEvent != null)
            {
                double spanDays = (ddEvent.TroughTime - ddEvent.PeakTime).TotalDays;
                Console.WriteLine($"\n  ── Worst DD event ──");
                Console.WriteLine($"  Peak    {ddEvent.PeakTime:yyyy-MM-dd HH:mm}  balance={ddEvent.PeakBalance:F3}");
                Console.WriteLine($"  Trough  {ddEvent.TroughTime:yyyy-MM-dd HH:mm}  balance={ddEvent.TroughBalance:F3}");
                Console.WriteLine($"  Drop    {ddEvent.DropPct:F2}%  over {spanDays:F0}d");

                // Trades that entered during the DD window (post-cap oosAll matches oosSim)
                var ddTrades = oosAll
                    .Where(t => t.Time >= ddEvent.PeakTime && t.Time <= ddEvent.TroughTime)
                    .OrderBy(t => t.Time)
                    .ToList();
                Console.WriteLine($"\n  Trades entering in DD window ({ddTrades.Count} total):");
                Console.WriteLine($"  {"Strategy",-12}  {"N",4}  {"WR",5}  {"Avg%",8}  {"Sum%",8}");
                Console.WriteLine($"  {new string('-', 48)}");
                foreach (var grp in ddTrades.GroupBy(t => t.Strategy).OrderBy(g => g.Key))
                {
                    var rets = grp.Select(t => t.Return).ToList();
                    double wr  = (double)rets.Count(r => r > 0) / rets.Count;
                    Console.WriteLine($"  {grp.Key,-12}  {grp.Count(),4}  {wr,5:P0}  {rets.Average(),+8:F2}%  {rets.Sum(),+8:F1}%");
                }

                // 10 worst individual trades in the window
                var worst = ddTrades.OrderBy(t => t.Return).Take(10).ToList();
                if (worst.Count > 0)
                {
                    Console.WriteLine($"\n  Worst individual trades in DD window:");
                    Console.WriteLine($"  {"Date",-18}  {"Strategy",-12}  {"Return",8}  {"Conf",6}");
                    foreach (var t in worst)
                        Console.WriteLine($"  {t.Time:yyyy-MM-dd HH:mm}  {t.Strategy,-12}  {t.Return,+8:F2}%  {t.Conf,6:F4}");
                }

                // ATR + funding context at peak and trough
                Console.WriteLine();
                if (dgSession != null)
                {
                    double atrPeak   = dgSession.GetAtrRatio(ddEvent.PeakTime);
                    double atrTrough = dgSession.GetAtrRatio(ddEvent.TroughTime);
                    string gate      = dgGeno != null ? $"  entryGate={dgGeno.EntryAtrGate:F2}" : "";
                    Console.WriteLine($"  BTC 4H ATR ratio at peak:   {atrPeak:F3}×{gate}");
                    Console.WriteLine($"  BTC 4H ATR ratio at trough: {atrTrough:F3}×");
                    bool wouldBlock = dgSession.IsEntryBlocked(ddEvent.TroughTime, "diplong");
                    Console.WriteLine($"  Entry gate would block DipLong/SwingLong at trough: {wouldBlock}");

                    // ── What were the protections actually DOING through this window? ──
                    // Peak/trough snapshots alone cannot answer "did the guard fire, and did the
                    // regime turn?" — the two questions you always ask of a drawdown. Report the
                    // guard's trajectory and the regime mix across the whole window, plus how many
                    // of the losing trades each protection would have stopped.
                    Console.WriteLine($"\n  ── Protection state through the DD window ──");

                    var ddTimes  = ddTrades.Select(t => t.Time).ToList();
                    if (ddTimes.Count > 0 && btcSeries != null && btcSeries.Length > 0)
                    {
                        var ddRegimes = RegimeBarLookup.TagRegimes(btcSeries, ddTimes);
                        var mix = ddRegimes.GroupBy(r => r).OrderByDescending(g => g.Count())
                            .Select(g => $"{g.Key}:{g.Count()} ({(double)g.Count() / ddRegimes.Length:P0})");
                        Console.WriteLine($"  BTC regime at entry, across DD trades: {string.Join("  ", mix)}");
                        Console.WriteLine($"  BTC regime at peak / trough:           " +
                            $"{RegimeBarLookup.TagRegimes(btcSeries, new[] { ddEvent.PeakTime })[0]} / " +
                            $"{RegimeBarLookup.TagRegimes(btcSeries, new[] { ddEvent.TroughTime })[0]}");
                    }

                    // Guard multiplier trajectory. 1.0 = guard idle; the floor gene is the most it
                    // can ever cut. A guard that sat at 1.0 through the whole event did nothing.
                    var ddMults = ddTimes.Select(t => dgSession.GetMult(t)).ToList();
                    if (ddMults.Count > 0)
                    {
                        Console.WriteLine($"  Guard mult over DD trades: min={ddMults.Min():F3}  " +
                            $"mean={ddMults.Average():F3}  max={ddMults.Max():F3}  " +
                            $"(1.000 = idle; SizeFloor gene = {dgGeno?.SizeFloor:F3})");
                        int idle = ddMults.Count(m => m > 0.999);
                        Console.WriteLine($"  Guard was IDLE for {idle}/{ddMults.Count} DD-window entries " +
                            $"({(double)idle / ddMults.Count:P0})");
                    }

                    // How many of these trades each protection would actually have stopped.
                    int gateBlocked = ddTrades.Count(t => dgSession.IsEntryBlocked(t.Time, t.Strategy));
                    Console.WriteLine($"  Entry-ATR gate would have blocked {gateBlocked}/{ddTrades.Count} of them");

                    // Sum% in the table above is a SUM OF PER-TRADE PERCENTAGES, not a portfolio
                    // loss — 30 trades at -2% sum to -60% while costing ~-3% of equity at 5%
                    // sizing. The portfolio number is DropPct on the line above.
                    Console.WriteLine($"  NOTE: 'Sum%' above sums per-trade returns and is NOT a portfolio loss; " +
                        $"the portfolio drop for this event is {ddEvent.DropPct:F2}%.");
                }
                if (fundingSession != null)
                {
                    double rateAtPeak   = fundingSession.GetRate(ddEvent.PeakTime);
                    double rateAtTrough = fundingSession.GetRate(ddEvent.TroughTime);
                    Console.WriteLine($"  BTC funding at peak:   {rateAtPeak:+0.0000%;-0.0000%;0.0000%}/8h  " +
                        $"(crowded long: {fundingSession.IsCrowdedLong(ddEvent.PeakTime)})");
                    Console.WriteLine($"  BTC funding at trough: {rateAtTrough:+0.0000%;-0.0000%;0.0000%}/8h  " +
                        $"(crowded long: {fundingSession.IsCrowdedLong(ddEvent.TroughTime)})");
                }
            }
        }
        else Console.WriteLine("  No OOS trades — skipping.");

        // Write fulltest_results.json
        static object FtStratStats(List<double> r) => r.Count == 0
            ? new { trades = 0, winRate = 0.0, pf = 0.0, avgRet = 0.0 }
            : new
            {
                trades  = r.Count,
                winRate = Math.Round((double)r.Count(x => x > 0) / r.Count * 100, 2),
                pf      = Math.Round(Simulator.ProfitFactor(r), 4),
                avgRet  = Math.Round(r.Average(), 4),
            };

        static object FtPortStats(Simulator.PortfolioResult p, bool hasData) => !hasData
            ? new { ret = 0.0, maxDD = 0.0 }
            : new
            {
                ret   = Math.Round(p.EndBalance - 100.0, 2),
                maxDD = Math.Round(p.MaxDrawdownPct, 2),
            };

        // Per-strategy detailed stats for the frontend
        object FtStratDetailed(string name, List<double> r, int candleCount)
        {
            if (r.Count == 0)
                return new { trades = 0, winRate = 0.0, avgRet = 0.0, sharpe = 0.0, pf = 0.0, ret = 0.0, maxDD = 0.0, kelly = 0.0, halfKelly = 0.0, dsr = 0.0, sparkline = Array.Empty<double>() };

            var tt  = StrategyStats.OneSampleT(r);
            double sh  = r.Count >= 5 ? Math.Round(Simulator.SharpeRatio(r, candleCount), 4) : 0;
            double pf  = Math.Round(Simulator.ProfitFactor(r), 4);
            double wr  = Math.Round((double)r.Count(x => x > 0) / r.Count * 100, 2);
            double totalRet = Math.Round(r.Sum(), 4);
            var (kelly, halfKelly) = StrategyStats.KellyFraction(r);
            double dsr = r.Count >= 10 ? Math.Round(StrategyStats.DeflatedSharpe(r, candleCount), 4) : 0;

            // MaxDD from cumulative equity
            double cumBal = 100.0, cumPeak = 100.0, cumMaxDD = 0;
            foreach (var ret in r)
            {
                cumBal += cumBal * ret / 100.0;
                if (cumBal > cumPeak) cumPeak = cumBal;
                double dd = (cumPeak - cumBal) / cumPeak * 100.0;
                if (dd > cumMaxDD) cumMaxDD = dd;
            }

            // Sparkline: mini equity curve (cumulative balance), sampled to ~50 points
            var eqFull = new List<double>(r.Count + 1);
            double sBal = 100.0;
            eqFull.Add(sBal);
            foreach (var ret in r) { sBal += sBal * ret / 100.0; eqFull.Add(sBal); }
            int sparkStep = Math.Max(1, eqFull.Count / 50);
            var sparkline = eqFull.Where((_, i) => i % sparkStep == 0 || i == eqFull.Count - 1)
                .Select(v => Math.Round(v, 2)).ToArray();

            return new
            {
                trades    = r.Count,
                winRate   = wr,
                avgRet    = Math.Round(r.Average(), 4),
                sharpe    = sh,
                pf        = pf,
                ret       = totalRet,
                maxDD     = Math.Round(cumMaxDD, 2),
                kelly     = Math.Round(kelly, 4),
                halfKelly = Math.Round(halfKelly, 4),
                dsr       = dsr,
                sparkline = sparkline,
            };
        }

        // Portfolio-level aggregate for the backtest object
        var allValRets = valAll.Select(t => t.Return).ToList();
        var allOosRets = oosAll.Select(t => t.Return).ToList();
        var combinedRets = allValRets.Concat(allOosRets).ToList();
        int combinedVCC = Math.Max(valCandleCount, oosCandleCount);
        double btTrades  = combinedRets.Count;
        double btWinRate = combinedRets.Count > 0 ? Math.Round((double)combinedRets.Count(r => r > 0) / combinedRets.Count * 100, 2) : 0;
        double btSharpe  = combinedRets.Count >= 5 ? Math.Round(Simulator.SharpeRatio(combinedRets, combinedVCC), 4) : 0;
        // Use val 5%cap portfolio for maxDD and CAGR (primary backtest result)
        double btMaxDD   = valSim.Count > 0 ? Math.Round(-val5p.MaxDrawdownPct, 2) : 0;
        double btCagr    = 0;
        if (valSim.Count >= 2)
        {
            double valDays = (valSim[^1].Item1 - valSim[0].Item1).TotalDays;
            double valRet  = (val5p.EndBalance - 100.0) / 100.0;
            btCagr = valDays > 0 ? Math.Round((Math.Pow(1 + valRet, 365.0 / valDays) - 1) * 100, 2) : 0;
        }

        // ── 1. Equity curve (val 5%cap) + BTC benchmark ──────────────────────
        var (ecResult, ecDD, ecCurve) = valSim.Count > 0
            ? Simulator.SimulateExposureCappedWithCurve(valSim, Config.MaxTotalExposurePct, maxPositionFrac: 0.05)
            : (default!, null, new List<(DateTime, double)>());

        // Sample equity curve to max ~500 points
        static List<object> SampleCurve(List<(DateTime Time, double Balance)> curve, int maxPts = 500)
        {
            if (curve.Count == 0) return new List<object>();
            int step = Math.Max(1, curve.Count / maxPts);
            return curve.Where((_, i) => i % step == 0 || i == curve.Count - 1)
                .Select(p => (object)new { t = p.Time.ToString("O"), v = Math.Round(p.Balance, 2) }).ToList();
        }
        var equityCurveJson = SampleCurve(ecCurve);

        // BTC buy-and-hold benchmark normalized to start at 100
        var benchmarkCurveJson = new List<object>();
        if (fetchedMap.TryGetValue("BTCUSDT", out var btcBench) && ecCurve.Count > 0)
        {
            var btcH1 = btcBench.h1;
            var ecStart = ecCurve[0].Time;
            var ecEnd   = ecCurve[^1].Time;
            var btcWindow = btcH1.Where(c => c.Time >= ecStart && c.Time <= ecEnd).ToArray();
            if (btcWindow.Length > 0)
            {
                double btcBase = btcWindow[0].Close;
                int btcStep = Math.Max(1, btcWindow.Length / 500);
                benchmarkCurveJson = btcWindow
                    .Where((_, i) => i % btcStep == 0 || i == btcWindow.Length - 1)
                    .Select(c => (object)new { t = c.Time.ToString("O"), v = Math.Round(c.Close / btcBase * 100, 2) })
                    .ToList();
            }
        }

        // ── 2. Monthly returns (val+oos combined) ────────────────────────────
        var allTrades = valAll.Concat(oosAll).OrderBy(t => t.Time).ToList();
        var monthlyReturnsJson = allTrades
            .GroupBy(t => t.Time.ToString("yyyy-MM"))
            .OrderBy(g => g.Key)
            .Select(g => (object)new { month = g.Key, ret = Math.Round(g.Sum(t => t.Return), 2) })
            .ToList();

        // ── 3. Per-strategy detailed stats ───────────────────────────────────
        var valDetailed = new
        {
            FadeShort = FtStratDetailed("FadeShort", valSwingRets, valCandleCount),
            Grid      = FtStratDetailed("Grid",      valGridRets,  valCandleCount),
            DipLong   = FtStratDetailed("DipLong",   valDlRets,    valCandleCount),
            SwingLong = FtStratDetailed("SwingLong", valSlRets,    valCandleCount),
            FadeLong  = FtStratDetailed("FadeLong",  valFlRets,    valCandleCount),
        };
        var oosDetailed = new
        {
            FadeShort = FtStratDetailed("FadeShort", oosSwingRets, oosCandleCount),
            Grid      = FtStratDetailed("Grid",      oosGridRets,  oosCandleCount),
            DipLong   = FtStratDetailed("DipLong",   oosDlRets,    oosCandleCount),
            SwingLong = FtStratDetailed("SwingLong", oosSlRets,    oosCandleCount),
            FadeLong  = FtStratDetailed("FadeLong",  oosFlRets,    oosCandleCount),
        };

        // ── 4. Statistical tests ─────────────────────────────────────────────
        object? statsJson = null;
        if (combinedRets.Count >= 10)
        {
            var stTt   = StrategyStats.OneSampleT(combinedRets);
            var stBoot = StrategyStats.Bootstrap(combinedRets);
            var stDist = StrategyStats.Distribution(combinedRets);
            var (stKelly, stHalfKelly) = StrategyStats.KellyFraction(combinedRets);
            double stDsr = StrategyStats.DeflatedSharpe(combinedRets, combinedVCC);
            int nTrials = 1000;
            var (dsrFull, _, eMaxSr, srHat) = StatisticalTests.DeflatedSharpeRatio(combinedRets, nTrials);

            // VC analysis
            int vcD = 15; // FadeShort genes as representative
            var (vcRatio, vcVerdict) = ((double)combinedRets.Count / vcD, combinedRets.Count / (double)vcD < 5 ? "severely undersampled" : combinedRets.Count / (double)vcD < 10 ? "borderline" : combinedRets.Count / (double)vcD < 20 ? "ok" : "good");
            double hoeffding = combinedRets.Count > 0
                ? (combinedRets.Max() - combinedRets.Min()) * Math.Sqrt(Math.Log(2.0 / 0.05) / (2.0 * combinedRets.Count))
                : 0;

            statsJson = new
            {
                tTest = new { mean = Math.Round(stTt.Mean, 4), se = Math.Round(stTt.StdErr, 4), t = Math.Round(stTt.T, 4), p = Math.Round(stTt.PValue, 6), sig = stTt.Sig95 },
                boot  = new { mean = Math.Round(stBoot.Mean, 4), lo95 = Math.Round(stBoot.Lo95, 4), hi95 = Math.Round(stBoot.Hi95, 4), probPos = Math.Round(stBoot.ProbPositive, 4) },
                dist  = new { skew = Math.Round(stDist.Skewness, 4), exKurt = Math.Round(stDist.ExKurtosis, 4), cvar5 = Math.Round(stDist.CVaR5, 4), tailRatio = Math.Round(stDist.TailRatio, 4) },
                kelly = new { full = Math.Round(stKelly, 4), half = Math.Round(stHalfKelly, 4), cap = 0.05 },
                dsr   = Math.Round(stDsr, 4),
                nTrials = nTrials,
                vc    = new { d = vcD, ratio = Math.Round(vcRatio, 1), verdict = vcVerdict },
                hoeffding = Math.Round(hoeffding, 4),
            };
        }

        // ── 5. Bootstrap histogram bins ──────────────────────────────────────
        List<object>? bootBinsJson = null;
        if (combinedRets.Count >= 10)
        {
            // Re-run bootstrap to get samples for histogram
            var arr = combinedRets.ToArray();
            var rng2 = new Random(42);
            int nBoot = 5000;
            var bootMeans = new double[nBoot];
            for (int i = 0; i < nBoot; i++)
            {
                double s = 0;
                for (int j = 0; j < arr.Length; j++)
                    s += arr[rng2.Next(arr.Length)];
                bootMeans[i] = s / arr.Length;
            }
            Array.Sort(bootMeans);

            // Bin into ~30 bins
            int numBins = 30;
            double bMin = bootMeans[0], bMax = bootMeans[^1];
            double binW = (bMax - bMin) / numBins;
            if (binW < 1e-12) binW = 1;
            bootBinsJson = new List<object>();
            for (int i = 0; i < numBins; i++)
            {
                double lo = bMin + i * binW;
                double hi = lo + binW;
                int cnt = bootMeans.Count(m => m >= lo && (i == numBins - 1 ? m <= hi : m < hi));
                bootBinsJson.Add(new { x = Math.Round(lo + binW / 2, 4), y = cnt });
            }
        }

        // ── 6. WFV fold scores (not available — fulltest uses time-split, not GA WFV) ──
        // GA fold scores are computed during training, not during fulltest.
        // Provide per-year breakdown as the closest equivalent.
        var wfvFoldsJson = new Dictionary<string, List<double>>();
        // Use year-by-year val returns as fold proxies
        foreach (var (stratName, stratKey) in new[]
        {
            ("FadeShort", "swing"), ("Grid", "grid"), ("DipLong", "diplong"),
            ("SwingLong", "swing_long"), ("FadeLong", "fadelong"),
        })
        {
            var stratTrades = valAll.Where(t => t.Strategy == stratKey).ToList();
            if (stratTrades.Count == 0) { wfvFoldsJson[stratName] = new List<double>(); continue; }
            var years = stratTrades.Select(t => t.Time.Year).Distinct().OrderBy(y => y).ToList();
            wfvFoldsJson[stratName] = years.Select(yr =>
                Math.Round(stratTrades.Where(t => t.Time.Year == yr).Sum(t => t.Return), 2)
            ).ToList();
        }

        // ── 7. Stress/crash data ─────────────────────────────────────────────
        List<object>? crashesJson = null;
        List<object>? ralliesJson = null;
        if (fetchedMap.TryGetValue("BTCUSDT", out var btcStress))
        {
            var crashes = CrashAnalyser.DetectCrashes(btcStress.h1);
            var rallies = CrashAnalyser.DetectRallies(btcStress.h1);

            crashesJson = crashes.Select(c => (object)new
            {
                label    = c.Label,
                drawdown = Math.Round(c.BtcDropPct, 2),
                duration = c.DurationH,
                start    = c.Start.ToString("O"),
                end      = c.End.ToString("O"),
            }).ToList();

            ralliesJson = rallies.Select(r => (object)new
            {
                label    = r.Label,
                recovery = Math.Round(r.BtcDropPct, 2),
                duration = r.DurationH,
                start    = r.Start.ToString("O"),
                end      = r.End.ToString("O"),
            }).ToList();
        }

        // ── 8. Per-strategy return distributions ─────────────────────────────
        static object StratDistBins(List<double> rets)
        {
            if (rets.Count < 5) return new { bins = Array.Empty<object>(), cvar5 = 0.0 };
            var sorted = rets.OrderBy(r => r).ToList();
            int tail5 = Math.Max(1, (int)(0.05 * rets.Count));
            double cvar = sorted.Take(tail5).Average();

            int numBins = Math.Min(30, Math.Max(5, rets.Count / 10));
            double rMin = sorted[0], rMax = sorted[^1];
            double binW = (rMax - rMin) / numBins;
            if (binW < 1e-12) binW = 1;
            var bins = new List<object>();
            for (int i = 0; i < numBins; i++)
            {
                double lo = rMin + i * binW;
                double hi = lo + binW;
                int cnt = sorted.Count(v => v >= lo && (i == numBins - 1 ? v <= hi : v < hi));
                bins.Add(new { x = Math.Round(lo + binW / 2, 4), y = cnt });
            }
            return new { bins, cvar5 = Math.Round(cvar, 4) };
        }

        var stratDistsJson = new
        {
            FadeShort = StratDistBins(valSwingRets.Concat(oosSwingRets).ToList()),
            Grid      = StratDistBins(valGridRets.Concat(oosGridRets).ToList()),
            DipLong   = StratDistBins(valDlRets.Concat(oosDlRets).ToList()),
            SwingLong = StratDistBins(valSlRets.Concat(oosSlRets).ToList()),
            FadeLong  = StratDistBins(valFlRets.Concat(oosFlRets).ToList()),
        };

        // Compute missing backtest-level fields
        double btNetReturn = valSim.Count > 0 ? Math.Round(val5p.EndBalance - 100.0, 2) : 0;
        double btAvgRet    = combinedRets.Count > 0 ? Math.Round(combinedRets.Average(), 4) : 0;
        double btPF        = combinedRets.Count > 0 ? Math.Round(Simulator.ProfitFactor(combinedRets), 4) : 0;
        double btCalmar    = (btMaxDD != 0 && btCagr != 0) ? Math.Round(btCagr / Math.Abs(btMaxDD), 2) : 0;

        // Enrich crash objects with trade analysis
        List<object>? crashesEnrichedJson = null;
        if (fetchedMap.TryGetValue("BTCUSDT", out var btcCrashEnrich))
        {
            var crashes2 = CrashAnalyser.DetectCrashes(btcCrashEnrich.h1);
            crashesEnrichedJson = crashes2.Select(c =>
            {
                var active = crashTrades.Where(t => t.Open <= c.Start && t.Close >= c.Start).ToList();
                var resolved = crashTrades.Where(t => t.Open <= c.Start && t.Close >= c.Start && t.Close <= c.End.AddHours(24)).ToList();
                double exposure = active.Sum(t => t.HalfKelly) * 100.0;
                int wins = resolved.Count(t => t.Return > 0);
                int losses = resolved.Count(t => t.Return <= 0);
                double avg = resolved.Count > 0 ? resolved.Average(t => t.Return) : 0;
                double portHit = resolved.Sum(t => t.HalfKelly * t.Return / 100.0) * 100.0;
                return (object)new
                {
                    label = c.Label, drawdown = Math.Round(c.BtcDropPct, 2), duration = c.DurationH,
                    start = c.Start.ToString("O"), end = c.End.ToString("O"),
                    openPos = active.Count, exposure = Math.Round(exposure, 1),
                    w = wins, l = losses, avgRet = Math.Round(avg, 2),
                    clean = Math.Round(portHit, 1), slip = 0.0, portHit = Math.Round(portHit, 1),
                };
            }).ToList();

            var rallies2 = CrashAnalyser.DetectRallies(btcCrashEnrich.h1);
            ralliesJson = rallies2.Select(r =>
            {
                var active = crashTrades.Where(t => t.Open <= r.Start && t.Close >= r.Start).ToList();
                var resolved = crashTrades.Where(t => t.Open <= r.Start && t.Close >= r.Start && t.Close <= r.End.AddHours(24)).ToList();
                int wins = resolved.Count(t => t.Return > 0);
                int losses = resolved.Count(t => t.Return <= 0);
                double avg = resolved.Count > 0 ? resolved.Average(t => t.Return) : 0;
                double portHit = resolved.Sum(t => t.HalfKelly * t.Return / 100.0) * 100.0;
                return (object)new
                {
                    label = r.Label, recovery = Math.Round(r.BtcDropPct, 2), duration = r.DurationH,
                    start = r.Start.ToString("O"), end = r.End.ToString("O"),
                    openPos = active.Count, w = wins, l = losses,
                    avgRet = Math.Round(avg, 2), portHit = Math.Round(portHit, 1),
                };
            }).ToList();
        }

        // Worst-case synthetic: peak concurrent exposure all-stop
        object? worstCaseJson = null;
        if (crashTrades.Count > 0)
        {
            var wcEvents = new List<(DateTime T, double D, bool IsGrid)>();
            foreach (var t in crashTrades)
            {
                bool grid = t.Strategy == "grid" || t.Strategy == "Grid";
                double capped = Math.Min(t.HalfKelly, 0.15);
                wcEvents.Add((t.Open, +capped, grid));
                wcEvents.Add((t.Close, -capped, grid));
            }
            wcEvents.Sort((a, b) => a.T.CompareTo(b.T));
            double total = 0, gridExp = 0, swingExp = 0, peakTotal2 = 0, peakGrid2 = 0, peakSwing2 = 0;
            DateTime peakTime2 = wcEvents[0].T;
            foreach (var (t, d, isGrid) in wcEvents)
            {
                total += d; if (isGrid) gridExp += d; else swingExp += d;
                if (total > peakTotal2) { peakTotal2 = total; peakGrid2 = gridExp; peakSwing2 = swingExp; peakTime2 = t; }
            }
            double cleanHit = -(peakGrid2 * 1.5 + peakSwing2 * 4.9);
            double expandedHit = cleanHit * 3;
            worstCaseJson = new
            {
                peakTime = peakTime2.ToString("O"),
                peakExposure = Math.Round(peakTotal2 * 100, 1),
                cleanHit = Math.Round(cleanHit, 1),
                expandedHit = Math.Round(expandedHit, 1),
            };
        }

        var fulltestOutput = new
        {
            timestamp = DateTime.UtcNow.ToString("O"),
            backtest = new
            {
                trades  = (int)btTrades,
                winRate = btWinRate,
                sharpe  = btSharpe,
                maxDD   = btMaxDD,
                cagr    = btCagr,
                netReturn = btNetReturn,
                avgRet    = btAvgRet,
                profitFactor = btPF,
                calmar    = btCalmar,
                coins     = Config.BacktestCoins.Length,
                valSplit  = 20,
                window    = "≈ 3.2 yr · 113 batches",
            },
            val = new
            {
                FadeShort = FtStratStats(valSwingRets),
                Grid      = FtStratStats(valGridRets),
                DipLong   = FtStratStats(valDlRets),
                SwingLong = FtStratStats(valSlRets),
                FadeLong  = FtStratStats(valFlRets),
            },
            oos = new
            {
                FadeShort = FtStratStats(oosSwingRets),
                Grid      = FtStratStats(oosGridRets),
                DipLong   = FtStratStats(oosDlRets),
                SwingLong = FtStratStats(oosSlRets),
                FadeLong  = FtStratStats(oosFlRets),
            },
            portfolio = new
            {
                val5pct   = FtPortStats(val5p,  valSim.Count > 0),
                valKelly  = FtPortStats(valKel, valSim.Count > 0),
                oos5pct   = FtPortStats(oos5p,  oosSim.Count > 0),
                oosKelly  = FtPortStats(oosKel, oosSim.Count > 0),
            },
            routerImpact = new
            {
                valEdge5pct   = Math.Round(r5R  - r5NR,  2),
                valEdgeKelly  = Math.Round(rKR  - rKNR,  2),
                oosEdge5pct   = Math.Round(or5R - or5NR, 2),
                oosEdgeKelly  = Math.Round(orKR - orKNR, 2),
            },
            // ── New fields for frontend visualization ──
            equityCurve    = equityCurveJson,
            benchmarkCurve = benchmarkCurveJson,
            monthlyReturns = monthlyReturnsJson,
            valDetailed    = valDetailed,
            oosDetailed    = oosDetailed,
            stats          = statsJson,
            bootBins       = bootBinsJson,
            wfvFolds       = wfvFoldsJson,
            stress = new
            {
                crashes   = crashesEnrichedJson ?? crashesJson,
                rallies   = ralliesJson,
                worstCase = worstCaseJson,
            },
            stratDists = stratDistsJson,
            val_highvol_summary = valHighVolRet.Count == 0 ? null : new
            {
                trades = valHighVolRet.Count,
                winRate = Math.Round((double)valHighVolRet.Count(x => x > 0) / valHighVolRet.Count * 100, 2),
                sharpe = Math.Round(Simulator.SharpeRatio(valHighVolRet, valHighVolVCC), 4),
                sortino = Math.Round(Simulator.SortinoRatio(valHighVolRet, valHighVolVCC), 4),
                pf = Math.Round(Simulator.ProfitFactor(valHighVolRet), 4),
                calmar = Math.Round(Simulator.CalmarRatio(valHighVolRet), 4),
                avgRet = Math.Round(valHighVolRet.Average(), 4),
                coins = valHighVolCoinRets.Count,
            },
            val_lowvol_summary = valLowVolRet.Count == 0 ? null : new
            {
                trades = valLowVolRet.Count,
                winRate = Math.Round((double)valLowVolRet.Count(x => x > 0) / valLowVolRet.Count * 100, 2),
                sharpe = Math.Round(Simulator.SharpeRatio(valLowVolRet, valLowVolVCC), 4),
                sortino = Math.Round(Simulator.SortinoRatio(valLowVolRet, valLowVolVCC), 4),
                pf = Math.Round(Simulator.ProfitFactor(valLowVolRet), 4),
                calmar = Math.Round(Simulator.CalmarRatio(valLowVolRet), 4),
                avgRet = Math.Round(valLowVolRet.Average(), 4),
                coins = valLowVolCoinRets.Count,
            },
            oos_highvol_summary = oosHighVolRet.Count == 0 ? null : new
            {
                trades = oosHighVolRet.Count,
                winRate = Math.Round((double)oosHighVolRet.Count(x => x > 0) / oosHighVolRet.Count * 100, 2),
                sharpe = Math.Round(Simulator.SharpeRatio(oosHighVolRet, oosHighVolVCC), 4),
                sortino = Math.Round(Simulator.SortinoRatio(oosHighVolRet, oosHighVolVCC), 4),
                pf = Math.Round(Simulator.ProfitFactor(oosHighVolRet), 4),
                calmar = Math.Round(Simulator.CalmarRatio(oosHighVolRet), 4),
                avgRet = Math.Round(oosHighVolRet.Average(), 4),
                coins = oosHighVolCoinRets.Count,
            },
            oos_lowvol_summary = oosLowVolRet.Count == 0 ? null : new
            {
                trades = oosLowVolRet.Count,
                winRate = Math.Round((double)oosLowVolRet.Count(x => x > 0) / oosLowVolRet.Count * 100, 2),
                sharpe = Math.Round(Simulator.SharpeRatio(oosLowVolRet, oosLowVolVCC), 4),
                sortino = Math.Round(Simulator.SortinoRatio(oosLowVolRet, oosLowVolVCC), 4),
                pf = Math.Round(Simulator.ProfitFactor(oosLowVolRet), 4),
                calmar = Math.Round(Simulator.CalmarRatio(oosLowVolRet), 4),
                avgRet = Math.Round(oosLowVolRet.Average(), 4),
                coins = oosLowVolCoinRets.Count,
            },
        };
        File.WriteAllText("fulltest_results.json",
            System.Text.Json.JsonSerializer.Serialize(fulltestOutput,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("fulltest_results.json written.");
    }
}
