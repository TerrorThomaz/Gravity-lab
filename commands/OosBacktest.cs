using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class OosBacktest
{

    // ── OOS position sizing: leading-slice Kelly (look-ahead guard) ────────────
    //
    // Simulator.ComputeConfidence returns a half-Kelly fraction that is used DIRECTLY as
    // the fraction of equity per position — "derived from training statistics, applied to
    // val trades" (Simulator.cs:19). CombinedBacktest.cs:290-303 honours that: conf comes
    // from the screen/train window, and rides into a DISJOINT set of val trades.
    //
    // This command has no separate train window by design — OOS coins are run over their
    // full history — so the split is made inside each coin's own trade series instead:
    // the first SizingFraction of that coin's trades (in timestamp order) are used ONLY to
    // derive conf and are then DISCARDED. Every number reported and every trade fed into
    // the portfolio simulation comes from the remaining trades, so a position is never
    // sized by the return it is about to book.
    //
    // KNOWN LIMITATION — the ordering key is the trade's EXIT bar, not its entry bar.
    // Every simulator in src/strategies/ records a single DateTime per trade and that
    // DateTime is the bar the position was closed on (e.g. SwingSimulator.cs:406
    // `result.Add((m15[im15].Time, ret, "fade_short"))`). Entry time is not carried in the
    // return tuples, so SizeThenScore sorts and slices on exit time. Consequence: the
    // first scored trade may have been ENTERED before the last sizing-slice trade closed,
    // so its size can rest on a return that had not yet been booked at its own entry. The
    // exposure is bounded by one hold period of overlap at the seam (48-72h depending on
    // strategy), affecting at most the handful of trades open across that boundary — not
    // the whole scored window. Plumbing entry time through would mean widening the return
    // tuple of all six simulators and every consumer of them; until that happens this
    // comment, not the code, is the accurate statement of what the split guarantees.
    //
    // When the leading slice is too thin to inform sizing at all (< MinSizingTrades) the
    // fixed FallbackConf is used. That constant depends on no trade's outcome, so nothing
    // needs to be withheld to keep it honest — the whole series is scored in that case and
    // SizingCount is reported as 0. Discarding trades there would shrink the sample of the
    // thinnest coins while buying no protection.
    internal const double SizingFraction  = 0.30;
    internal const int    MinSizingTrades = 5;     // ComputeConfidence returns 0 below this
    internal const double FallbackConf    = 0.02;  // 2% of equity — fixed and conservative

    internal static int SizingSliceCount(int totalTrades) =>
        totalTrades <= 0 ? 0 : (int)Math.Floor(totalTrades * SizingFraction);

    // Confidence from the leading slice ONLY. Deliberately never reads returns beyond
    // sizingCount — that is the property OosSizingSplitTests pins.
    // A slice too thin for ComputeConfidence falls back to the fixed FallbackConf (never
    // to the in-sample value); usedFallback is surfaced so callers can flag it in output,
    // and sizingCount is reported as 0 because a data-independent constant needs no
    // withheld slice to justify it.
    // A slice that is thick enough but shows no edge legitimately yields conf 0 (Kelly:
    // no edge, no bet) — that is a decision made on past trades only, not a leak.
    internal static double LeadingSliceConfidence(List<double> returns, out int sizingCount, out bool usedFallback)
    {
        sizingCount = SizingSliceCount(returns.Count);
        if (sizingCount < MinSizingTrades) { usedFallback = true; sizingCount = 0; return FallbackConf; }
        usedFallback = false;
        return Simulator.ComputeConfidence(returns.GetRange(0, sizingCount));
    }

    internal readonly record struct SizedSplit<T>(double Conf, int SizingCount, bool UsedFallback, List<T> Scored);

    // Orders a coin's trades by the supplied timestamp (in practice the trade's EXIT bar —
    // see the KNOWN LIMITATION note above), derives conf from the leading slice, and hands
    // back only the trades that may be scored.
    internal static SizedSplit<T> SizeThenScore<T>(List<T> trades, Func<T, DateTime> timeOf, Func<T, double> returnOf)
    {
        var ordered = trades.OrderBy(timeOf).ToList();
        var rets    = ordered.Select(returnOf).ToList();
        double conf = LeadingSliceConfidence(rets, out int sizingCount, out bool usedFallback);
        var scored  = ordered.GetRange(sizingCount, ordered.Count - sizingCount);
        return new SizedSplit<T>(conf, sizingCount, usedFallback, scored);
    }

    // ── Defect 1 guard: FadeShort's BacktestCoins screen window ────────────────
    //
    // In RunAllCoinsBacktest the BacktestCoins cohort is scored on a held-out val window
    // (trailing 20%), so both the coin-inclusion screen and the Kelly sizing must read the
    // train window and nothing else. This used to fall back to the FULL series whenever
    // h1Train was shorter than MinFadeShortTrainH1Bars, which let the val window select
    // which coins entered the portfolio AND set their position size. A coin that cannot be
    // screened on train data alone is skipped instead.
    internal const int MinFadeShortTrainH1Bars = 4380;

    // Returns the (h1, m15) window FadeShort's inclusion screen and Kelly sizing may read,
    // or null when the coin's train history is too short to screen without touching val.
    // Never returns a series that extends into the scored window.
    internal static (Candle[] H1, Candle[] M15)? FadeShortScreenWindow(Candle[] h1Train, Candle[] m15Train) =>
        h1Train.Length >= MinFadeShortTrainH1Bars ? (h1Train, m15Train) : null;

    // Sharpe/Sortino scale with sqrt(candleCount), so the scored window must be measured
    // over its own span — charging it the full history's candle count would inflate both.
    internal static int ScoredCandleCount(Candle[] h1, DateTime firstScoredTime)
    {
        int bars = 0;
        for (int i = 0; i < h1.Length; i++) if (h1[i].Time >= firstScoredTime) bars++;
        return Math.Max(bars, 1) * 12;
    }

    static void PrintCoinHeader(bool withGate) =>
        Console.WriteLine($"{"Coin",-18} {"Kelly%",6}   {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Sizing",6}  {"Scored",6}  {"WR",5}  {"AvgRet%",7}{(withGate ? $"  {"Gated",6}" : "")}");

    static void PrintCoinLine(string sym, double conf, bool usedFallback, double sh, double sort,
                              double pf, int sizingN, int scoredN, double wr, double avg, int? gatedOut = null) =>
        Console.WriteLine($"  {sym,-16} {conf,6:P1}{(usedFallback ? "*" : " ")}  {sh,7:F2}  {sort,7:F2}  {pf,5:F2}  {sizingN,6}  {scoredN,6}  {wr,5:P0}  {avg,+7:F2}%{(gatedOut.HasValue ? $"  -{gatedOut.Value,4}" : "")}");

    static void PrintSizingFooter(int coins, int fallbacks, int sizingTrades, int scoredTrades) =>
        Console.WriteLine($"  → {coins} coin(s): {sizingTrades} trades consumed for sizing, {scoredTrades} scored"
                        + $"{(fallbacks > 0 ? $"  ·  {fallbacks} coin(s) marked * used the {FallbackConf:P1} fallback (sizing slice < {MinSizingTrades} trades)" : "")}");

    public static async Task RunOosBacktest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | OOS BACKTEST ({Config.OosCoins.Length} never-seen coins · full history · all strategies, router-gated) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype — run 'train' first.");    return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine("Missing grid genotype — run 'gridtrain' first.");     return; }

        // Load variant arrays (currently one entry each; infrastructure ready for multi-variant)
        var fsVariants   = StrategyPipeline.LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var gridVariants = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_best", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var flVariants   = StrategyPipeline.LoadVariants<FadeLongGenotypeDto, FadeLongGenotype>(
            "fade_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var dlVariants   = StrategyPipeline.LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var slVariants   = StrategyPipeline.LoadVariants<SwingLongGenotypeDto, SwingLongGenotype>(
            "swing_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var rsVariants   = StrategyPipeline.LoadVariants<RipShortGenotypeDto, RipShortGenotype>(
            "rip_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

        // Representative single genotypes (for logging and hold-time calcs)
        var swingG = fsVariants.Length   > 0 ? fsVariants[0].Genotype!   : null;
        var gridG  = gridVariants.Length > 0 ? gridVariants[0].Genotype! : null;
        if (swingG == null) { Console.WriteLine("Missing FadeShort genotype — run 'train' first."); return; }
        if (gridG  == null) { Console.WriteLine("Missing grid genotype — run 'gridtrain' first.");  return; }

        FadeLongGenotype?     flG     = flVariants.Length > 0 ? flVariants[0].Genotype : null;
        DipLongGenotype?      dlG     = dlVariants.Length > 0 ? dlVariants[0].Genotype : null;
        SwingLongGenotype?    slG     = slVariants.Length > 0 ? slVariants[0].Genotype : null;
        RipShortGenotype?     rsG     = rsVariants.Length > 0 ? rsVariants[0].Genotype : null;
        RegimeRouterGenotype? routerG = File.Exists(Config.RouterGenoFile)    ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype() : null;

        Console.WriteLine($"FadeShort: {swingG}  [{fsVariants.Length} variant(s)]");
        Console.WriteLine($"Grid:      {gridG}  [{gridVariants.Length} variant(s)]");
        if (flG     != null) Console.WriteLine($"FadeLong:  {flG}  [{flVariants.Length} variant(s)]");
        else                 Console.WriteLine("FadeLong:  not found — skipping");
        if (dlG     != null) Console.WriteLine($"DipLong:   {dlG}  [{dlVariants.Length} variant(s)]");
        else                 Console.WriteLine("DipLong:   not found — skipping");
        if (slG     != null) Console.WriteLine($"SwingLong: {slG}  [{slVariants.Length} variant(s)]");
        else                 Console.WriteLine("SwingLong: not found — skipping");
        if (rsG     != null) Console.WriteLine($"RipShort:  {rsG}  [{rsVariants.Length} variant(s)]");
        else                 Console.WriteLine("RipShort:  not found — skipping");
        if (routerG != null) Console.WriteLine($"Router:    {routerG}");
        else                 Console.WriteLine("Router:    not found — running ungated");
        Console.WriteLine();

        var allSyms = Config.OosCoins.Concat(new[] { "BTCUSDT", "ETHUSDT" }).Distinct().ToArray();
        Console.WriteLine($"  Fetching {Config.OosCoins.Length} OOS coins + BTC/ETH for router (15m → 1h, ~3yr)...");
        var fetched = await StrategyPipeline.FetchFifteenMinAsync(client, allSyms, batches: 113);
        Console.WriteLine($"  Done.\n");

        // ── Funding rate sessions, ONE PER SYMBOL ────────────────────────────────────────
        Console.WriteLine("  Fetching per-symbol funding rate history...");
        var funding = await StrategyPipeline.FetchFundingAsync(client, fetched);
        Console.WriteLine();

        // ── DynamicGuard session ─────────────────────────────────────────────────────────
        var btcH1ForGuard = fetched.FirstOrDefault(f => f.sym == "BTCUSDT").h1;
        var guardCtx      = GuardedPortfolio.TryLoad(btcH1ForGuard);
        if (guardCtx != null) Console.WriteLine($"  Guard: {guardCtx.Genotype}\n");

        // Hoisted out of the router build so the central evaluator can bucket the final book by
        // regime. It is the same classification the router gates on — recomputing it at the
        // report site would risk grading against a different series than the one that gated.
        var router = StrategyPipeline.BuildRouterSession(routerG, fetched, btcH1ForGuard);
        RegimeRouterSession? session  = router.Session;
        RegimeBar[]?         btcRegimeSeries = router.BtcRegimeSeries;

        if (session != null && RegimeRouter.HmmEnabled && routerG is { IsHmmGenotype: true } && btcRegimeSeries is { Length: > 0 })
        {
            var lastBar = btcRegimeSeries[^1];
            if (lastBar.HmmProbs != null)
            {
                Console.WriteLine("  HMM current-bar weights:");
                foreach (var (kind, label) in new[] {
                    (RegimeRouterGA.StrategyKind.FadeShort, "FadeShort"), (RegimeRouterGA.StrategyKind.Grid, "Grid"),
                    (RegimeRouterGA.StrategyKind.GridShort, "GridShort"), (RegimeRouterGA.StrategyKind.DipLong, "DipLong"),
                    (RegimeRouterGA.StrategyKind.FadeLong, "FadeLong"), (RegimeRouterGA.StrategyKind.RipShort, "RipShort"),
                    (RegimeRouterGA.StrategyKind.SwingLong, "SwingLong"), (RegimeRouterGA.StrategyKind.AccumulationGrid, "AccumGrid") })
                    Console.WriteLine($"    {label,-14} {session.Weight(kind, lastBar.Time):F2}");
                Console.WriteLine();
            }
        }

        var oosFetched = fetched.Where(f => Config.OosCoins.Contains(f.sym)).ToArray();

        const double oosMinVol = 0.05;

        var swingTrades = new List<(DateTime Time, double Return, double Conf)>();
        var gridTrades  = new List<(DateTime Time, double Return, double Conf)>();
        var flTrades    = new List<(DateTime Time, double Return, double Conf)>();
        var dlTrades    = new List<(DateTime Time, double Return, double Conf)>();
        var slTrades    = new List<(DateTime Time, double Return, double Conf)>();
        var rsTrades    = new List<(DateTime Time, double Return, double Conf)>();
        // Entry/Sym carried so the concurrency cap models the window a position was ACTUALLY open.
        // It previously received the EXIT bar as EntryTime plus a hardcoded 48/72h HoldDuration,
        // so occupancy was modelled entirely AFTER the trade closed. Same bug as CombinedBacktest.
        var allTrades   = new List<(DateTime Time, double Return, double Conf, string Strategy, DateTime Entry, string Sym)>();

        var highVolTrades   = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var lowVolTrades    = new List<(DateTime Time, double Return, double Conf, string Strategy)>();
        var highVolCoinRets = new Dictionary<string, List<double>>();
        var lowVolCoinRets  = new Dictionary<string, List<double>>();
        // Sizing actually applied per coin (leading-slice Kelly). Reported instead of a
        // Kelly recomputed on the scored returns, which would be the same in-sample stat
        // this command is fixing.
        var highVolCoinConf = new Dictionary<string, List<double>>();
        var lowVolCoinConf  = new Dictionary<string, List<double>>();
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

        void RecordAppliedConf(string label, string sym, double conf)
        {
            var target = label == "highvol" ? highVolCoinConf : label == "lowvol" ? lowVolCoinConf : null;
            if (target == null) return;
            if (!target.ContainsKey(sym)) target[sym] = new List<double>();
            target[sym].Add(conf);
        }

        var swingCoinStats = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var gridCoinStats  = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var flCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var dlCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var slCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        var rsCoinStats    = new List<(string Coin, double Sharpe, double Sortino, double PF, int Trades, double WR, double AvgRet, double Kelly)>();
        int totalVCC = 0;

        // ── SWING ─────────────────────────────────────────────────────────────────
        Console.WriteLine($"══ SWING (full OOS history, no seed screen) ═════════════════════════════════");
        Console.WriteLine($"  Sizing: half-Kelly from each coin's leading {SizingFraction:P0} of trades; only the remaining");
        Console.WriteLine($"  {1 - SizingFraction:P0} is scored. * = sizing slice < {MinSizingTrades} trades → fixed {FallbackConf:P1} fallback conf.");
        PrintCoinHeader(withGate: false);
        Console.WriteLine(new string('-', 96));

        int fsCoins = 0, fsFallbacks = 0, fsSizingTrades = 0, fsScoredTrades = 0;
        // ── Accumulator acquisition quality on NEVER-TRAINED coins (diagnostic only) ──
        // The accumulator is deliberately excluded from every portfolio ("capital accumulator,
        // not a profit strategy"), so profit factor was the only number it ever got — and PF is
        // the wrong yardstick for it. Its real objective is acquiring inventory BELOW the
        // market's own average over the period. It scores +2.54% below VWAP on the val window;
        // this measures the same thing on coins it has never seen, which is the test that
        // decides whether that is an edge or a fit.
        GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype? oosAgBull = null;
        GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype? oosAgBear = null;
        if (File.Exists("genotypes/accumulation_grid_genotype.json"))
        {
            var agJson = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText("genotypes/accumulation_grid_genotype.json"));
            oosAgBull = JsonSerializer.Deserialize<GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype>(agJson.GetProperty("Bull").GetRawText());
            oosAgBear = JsonSerializer.Deserialize<GravityGen2.Strategies.AccumulationGrid.AccumulationGridGenotype>(agJson.GetProperty("Bear").GetRawText());
        }
        // Same GRAVITY_RATCHET toggle as combinedbacktest. Without this the two commands run
        // DIFFERENT mechanism sets, so any val-vs-OOS gap partly measures configuration drift
        // rather than generalisation — which is exactly what we are trying to isolate here.
        var oosRatchet = ExitRatchet.FromEnvironment();
        double oosLockMult = oosRatchet.LockAtrMult;
        if (oosRatchet.Enabled)
            Console.WriteLine($"  [RATCHET] active on OOS path (lock {oosLockMult}xATR, trailFloor={oosRatchet.FloorsTrailOnly})");

        // ONE context, same shape combinedbacktest builds. This is the whole point of step 2:
        // the two commands can no longer run different mechanism sets by accident.
        var ctx = new ExecContext(Ratchet: oosRatchet);

        // GRAVITY_DROP=fadeshort,grid — exclude named strategies from the PORTFOLIO (their own
        // sections still print). Not a feature: a measurement. 50% of OOS trades come from the two
        // strategies whose OOS profit factor is below 1.3 (FadeShort 39% @ 1.11, Grid 11% @ 0.94),
        // so the portfolio's 1.77 may be dead weight diluting four strategies that each generalise
        // at 2.79-4.09. This isolates that.
        var dropped = (Environment.GetEnvironmentVariable("GRAVITY_DROP") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (dropped.Count > 0)
            Console.WriteLine($"  [DROP] excluded from portfolio: {string.Join(", ", dropped)}");

        var oosAcqDiscount = new List<double>();
        var oosAcqDiscountEma = new List<double>();
        int oosAcqFills = 0;

        foreach (var (sym, m15, h1) in oosFetched)
        {
            if (h1.Length < 300) { Console.WriteLine($"  {sym,-16}  skip (only {h1.Length} h1 bars)"); continue; }

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < oosMinVol) { Console.WriteLine($"  {sym,-16}  skip (vol=${medVol:F2}M/h)"); continue; }

            if (oosAgBull != null && oosAgBear != null && h1.Length >= 300)
            {
                var agRaw = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator
                        .GetAccumulationReturns(oosAgBull, h1, MarketRegime.Bull, funding.For(sym))
                    .Concat(GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator
                        .GetAccumulationReturns(oosAgBear, h1, MarketRegime.Bear, funding.For(sym)))
                    .ToList();
                if (agRaw.Count > 0)
                {
                    var pxL = agRaw.Select(t => t.EntryPrice).ToList(); // EntryTime, not Time: an accumulator's execution quality is its FILL priced against
                    // the market around that fill. Time is the EXIT bar, and a profitable grid exits
                    // ABOVE its entries, so benchmarking against the exit window flatters every fill
                    // by roughly the trade's own profit — measuring P&L, not execution.
                    var tmL = agRaw.Select(t => t.EntryTime).ToList();
                    double d  = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator
                        .AcquisitionDiscountPct(h1, pxL, tmL);
                    double de = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator
                        .AcquisitionDiscountEmaPct(h1, pxL, tmL);
                    if (!double.IsNaN(de)) oosAcqDiscountEma.Add(de);
                    if (!double.IsNaN(d)) { oosAcqDiscount.Add(d); oosAcqFills += agRaw.Count; }
                }
            }

            var (coinFsG, fsVarLabel) = StrategyPipeline.SelectVariantLabeled(fsVariants, m15);
            coinFsG ??= swingG;
            var rawFs  = FadeShortSimulator.GetFadeShortReturns(coinFsG, h1, m15, ctx.With(funding.For(sym)));
            // Router-gated, like every other strategy on this path. FadeShort was the ONLY one
            // ungated here — the same omission that existed in CombinedBacktest, fixed there and
            // not here, so the two commands were gating different strategy sets and any val-vs-OOS
            // comparison partly measured that difference rather than generalisation.
            var trades = session != null
                ? rawFs.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeShort, t.Time)).ToList()
                : rawFs;
            var split  = SizeThenScore(trades, t => t.Time, t => t.Return);
            var scored = split.Scored;
            var vRet   = scored.Select(t => t.Return).ToList();
            if (vRet.Count < 5)
            {
                Console.WriteLine($"  {sym,-16}  skip ({trades.Count} trades → {split.SizingCount} sizing / {vRet.Count} scored)");
                continue;
            }

            int    vCC  = ScoredCandleCount(h1, scored[0].Time);
            totalVCC    = Math.Max(totalVCC, vCC);
            double conf = split.Conf;
            double sh   = Simulator.SharpeRatio(vRet, vCC);
            double sort = Simulator.SortinoRatio(vRet, vCC);
            double pf   = Simulator.ProfitFactor(vRet);
            double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
            double avg  = vRet.Average();

            PrintCoinLine(sym, conf, split.UsedFallback, sh, sort, pf, split.SizingCount, vRet.Count, wr, avg);
            fsCoins++; fsSizingTrades += split.SizingCount; fsScoredTrades += vRet.Count;
            if (split.UsedFallback) fsFallbacks++;
            swingCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
            foreach (var (t, ret, _, et, _) in scored)
            {
                double sgFs = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.FadeShort, t) : 1.0;
                double cFs = conf * sgFs;
                swingTrades.Add((t, ret, cFs));
                allTrades.Add((t, ret, cFs, "swing", et, sym));
                RouteVolVariant(fsVarLabel, sym, t, ret, cFs, "swing", vCC);
            }
            RecordAppliedConf(fsVarLabel, sym, conf);
        }
        PrintSizingFooter(fsCoins, fsFallbacks, fsSizingTrades, fsScoredTrades);

        if (oosAcqDiscount.Count > 0)
        {
            double mean = oosAcqDiscount.Average();
            int better  = oosAcqDiscount.Count(d => d > 0);
            Console.WriteLine($"\n── Accumulator acquisition quality · OOS (never-trained coins) ──");
            Console.WriteLine($"  Avg entry vs period VWAP: {mean:+0.00;-0.00}%  " +
                              $"({(mean > 0 ? "below VWAP — accumulating well" : "ABOVE VWAP — paying up")})");
            Console.WriteLine($"  Coins below VWAP: {better}/{oosAcqDiscount.Count} ({(double)better / oosAcqDiscount.Count:P0})  ·  {oosAcqFills} fills");
            if (oosAcqDiscountEma.Count > 0)
                Console.WriteLine($"  vs trailing EMA (CAUSAL — no future bars): {oosAcqDiscountEma.Average():+0.00;-0.00}%  " +
                                  $"({oosAcqDiscountEma.Count(d => d > 0)}/{oosAcqDiscountEma.Count} coins)");
            Console.WriteLine($"  Diagnostic only — the accumulator contributes no trades to any portfolio here.");
        }

        // ── GRID ──────────────────────────────────────────────────────────────────
        Console.WriteLine($"\n══ GRID (full OOS history, router-gated) ════════════════════════════════════");
        PrintCoinHeader(withGate: false);
        Console.WriteLine(new string('-', 96));

        int gdCoins = 0, gdFallbacks = 0, gdSizingTrades = 0, gdScoredTrades = 0;
        foreach (var (sym, m15Grid, h1) in oosFetched)
        {
            if (h1.Length < 300) continue;

            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < oosMinVol) continue;

            var (coinGridG, gridVarLabel) = StrategyPipeline.SelectVariantLabeled(gridVariants, m15Grid);
            coinGridG ??= gridG;
            var raw   = GridSimulator.GetGridReturns(coinGridG, h1, funding.For(sym));
            var gated = session != null
                ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList()
                : raw;
            var split  = SizeThenScore(gated, t => t.Time, t => t.Return);
            var scored = split.Scored;
            var vRet   = scored.Select(t => t.Return).ToList();
            if (vRet.Count < 5)
            {
                Console.WriteLine($"  {sym,-16}  skip ({gated.Count} trades after gate → {split.SizingCount} sizing / {vRet.Count} scored)");
                continue;
            }

            int    vCC  = ScoredCandleCount(h1, scored[0].Time);
            double conf = split.Conf;
            double sh   = Simulator.SharpeRatio(vRet, vCC);
            double sort = Simulator.SortinoRatio(vRet, vCC);
            double pf   = Simulator.ProfitFactor(vRet);
            double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
            double avg  = vRet.Average();

            PrintCoinLine(sym, conf, split.UsedFallback, sh, sort, pf, split.SizingCount, vRet.Count, wr, avg);
            gdCoins++; gdSizingTrades += split.SizingCount; gdScoredTrades += vRet.Count;
            if (split.UsedFallback) gdFallbacks++;
            gridCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
            foreach (var t in scored)
            {
                double sgGr = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.Grid, t.Time) : 1.0;
                double cGr = conf * sgGr;
                gridTrades.Add((t.Time, t.Return, cGr));
                allTrades.Add((t.Time, t.Return, cGr, "grid", t.EntryTime, sym));
                RouteVolVariant(gridVarLabel, sym, t.Time, t.Return, cGr, "grid", vCC);
            }
            RecordAppliedConf(gridVarLabel, sym, conf);
        }
        PrintSizingFooter(gdCoins, gdFallbacks, gdSizingTrades, gdScoredTrades);

        // ── FADELONG ──────────────────────────────────────────────────────────────
        if (flG != null)
        {
            Console.WriteLine($"\n══ FADELONG (full OOS history, router-gated) ════════════════════════════════");
            PrintCoinHeader(withGate: true);
            Console.WriteLine(new string('-', 104));

            int flCoins = 0, flFallbacks = 0, flSizingTrades = 0, flScoredTrades = 0;
            foreach (var (sym, m15, h1) in oosFetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < oosMinVol) continue;

                var (coinFlG, flVarLabel) = StrategyPipeline.SelectVariantLabeled(flVariants, m15);
                coinFlG ??= flG;
                var raw   = FadeLongSimulator.GetFadeLongReturns(coinFlG, h1, m15, ctx.With(funding.For(sym)));
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList()
                    : raw;
                var split  = SizeThenScore(gated, t => t.Time, t => t.Return);
                var scored = split.Scored;
                var vRet   = scored.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip ({gated.Count} trades after gate, {raw.Count} raw → {split.SizingCount} sizing / 0 scored)");
                    continue;
                }

                int    vCC  = ScoredCandleCount(h1, scored[0].Time);
                double conf = split.Conf;
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                PrintCoinLine(sym, conf, split.UsedFallback, sh, sort, pf, split.SizingCount, vRet.Count, wr, avg, gatedOut);
                flCoins++; flSizingTrades += split.SizingCount; flScoredTrades += vRet.Count;
                if (split.UsedFallback) flFallbacks++;
                flCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                foreach (var t in scored)
                {
                    double sgFl = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.FadeLong, t.Time) : 1.0;
                    double cFl = conf * sgFl;
                    flTrades.Add((t.Time, t.Return, cFl));
                    allTrades.Add((t.Time, t.Return, cFl, "fadelong", t.EntryTime, sym));
                    RouteVolVariant(flVarLabel, sym, t.Time, t.Return, cFl, "fadelong", vCC);
                }
                RecordAppliedConf(flVarLabel, sym, conf);
            }
            PrintSizingFooter(flCoins, flFallbacks, flSizingTrades, flScoredTrades);
        }

        // ── DIPLONG ───────────────────────────────────────────────────────────────
        if (dlG != null)
        {
            Console.WriteLine($"\n══ DIPLONG (full OOS history, router-gated) ═════════════════════════════════");
            PrintCoinHeader(withGate: true);
            Console.WriteLine(new string('-', 104));

            int dlCoins = 0, dlFallbacks = 0, dlSizingTrades = 0, dlScoredTrades = 0;
            foreach (var (sym, m15, h1) in oosFetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < oosMinVol) continue;

                var (coinDlG, dlVarLabel) = StrategyPipeline.SelectVariantLabeled(dlVariants, m15);
                coinDlG ??= dlG;
                var raw   = DipLongSimulator.GetDipLongReturns(coinDlG, h1, m15, ctx.With(funding.For(sym)));
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                var split  = SizeThenScore(gated, t => t.Time, t => t.Return);
                var scored = split.Scored;
                var vRet   = scored.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip ({gated.Count} trades after gate, {raw.Count} raw → {split.SizingCount} sizing / 0 scored)");
                    continue;
                }

                int    vCC  = ScoredCandleCount(h1, scored[0].Time);
                double conf = split.Conf;
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                PrintCoinLine(sym, conf, split.UsedFallback, sh, sort, pf, split.SizingCount, vRet.Count, wr, avg, gatedOut);
                dlCoins++; dlSizingTrades += split.SizingCount; dlScoredTrades += vRet.Count;
                if (split.UsedFallback) dlFallbacks++;
                dlCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                foreach (var t in scored)
                {
                    double sgDl = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.DipLong, t.Time) : 1.0;
                    double cDl = conf * sgDl;
                    dlTrades.Add((t.Time, t.Return, cDl));
                    allTrades.Add((t.Time, t.Return, cDl, "diplong", t.EntryTime, sym));
                    RouteVolVariant(dlVarLabel, sym, t.Time, t.Return, cDl, "diplong", vCC);
                }
                RecordAppliedConf(dlVarLabel, sym, conf);
            }
            PrintSizingFooter(dlCoins, dlFallbacks, dlSizingTrades, dlScoredTrades);
        }

        // ── SWINGLONG ─────────────────────────────────────────────────────────────────
        if (slG != null)
        {
            Console.WriteLine($"\n══ SWINGLONG (full OOS history, router-gated) ═══════════════════════════════");
            PrintCoinHeader(withGate: true);
            Console.WriteLine(new string('-', 104));

            int slCoins = 0, slFallbacks = 0, slSizingTrades = 0, slScoredTrades = 0;
            foreach (var (sym, m15, h1) in oosFetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < oosMinVol) continue;

                var (coinSlG, slVarLabel) = StrategyPipeline.SelectVariantLabeled(slVariants, m15);
                coinSlG ??= slG;
                var raw   = SwingLongSimulator.GetSwingLongReturns(coinSlG, h1, m15, ctx.With(funding.For(sym)));
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                var split  = SizeThenScore(gated, t => t.Time, t => t.Return);
                var scored = split.Scored;
                var vRet   = scored.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip ({gated.Count} trades after gate, {raw.Count} raw → {split.SizingCount} sizing / 0 scored)");
                    continue;
                }

                int    vCC  = ScoredCandleCount(h1, scored[0].Time);
                double conf = split.Conf;
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                PrintCoinLine(sym, conf, split.UsedFallback, sh, sort, pf, split.SizingCount, vRet.Count, wr, avg, gatedOut);
                slCoins++; slSizingTrades += split.SizingCount; slScoredTrades += vRet.Count;
                if (split.UsedFallback) slFallbacks++;
                slCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                foreach (var t in scored)
                {
                    double sgSl = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.DipLong, t.Time) : 1.0;
                    double cSl = conf * sgSl;
                    slTrades.Add((t.Time, t.Return, cSl));
                    allTrades.Add((t.Time, t.Return, cSl, "swing_long", t.EntryTime, sym));
                    RouteVolVariant(slVarLabel, sym, t.Time, t.Return, cSl, "swing_long", vCC);
                }
                RecordAppliedConf(slVarLabel, sym, conf);
            }
            PrintSizingFooter(slCoins, slFallbacks, slSizingTrades, slScoredTrades);
        }

        // ── RIPSHORT ──────────────────────────────────────────────────────────────
        if (rsG != null)
        {
            Console.WriteLine($"\n══ RIPSHORT (full OOS history, router-gated) ════════════════════════════════");
            PrintCoinHeader(withGate: true);
            Console.WriteLine(new string('-', 104));

            int rsCoins = 0, rsFallbacks = 0, rsSizingTrades = 0, rsScoredTrades = 0;
            foreach (var (sym, m15, h1) in oosFetched)
            {
                if (h1.Length < 300 || m15.Length < 1200) continue;

                var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
                double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
                if (medVol < oosMinVol) continue;

                var (coinRsG, rsVarLabel) = StrategyPipeline.SelectVariantLabeled(rsVariants, m15);
                coinRsG ??= rsG;
                var raw   = RipShortSimulator.GetRipShortReturns(coinRsG, h1, m15, funding.For(sym), StrategyPipeline.ProdRipCap(),
                                ExitRatchet.ForStrategy("ripshort"));
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time)).ToList()
                    : raw;
                var split  = SizeThenScore(gated, t => t.Time, t => t.Return);
                var scored = split.Scored;
                var vRet   = scored.Select(t => t.Return).ToList();

                int gatedOut = raw.Count - gated.Count;
                if (vRet.Count == 0)
                {
                    Console.WriteLine($"  {sym,-16}  skip ({gated.Count} trades after gate, {raw.Count} raw → {split.SizingCount} sizing / 0 scored)");
                    continue;
                }

                int    vCC  = ScoredCandleCount(h1, scored[0].Time);
                double conf = split.Conf;
                double sh   = Simulator.SharpeRatio(vRet, vCC);
                double sort = Simulator.SortinoRatio(vRet, vCC);
                double pf   = Simulator.ProfitFactor(vRet);
                double wr   = (double)vRet.Count(r => r > 0) / vRet.Count;
                double avg  = vRet.Average();

                PrintCoinLine(sym, conf, split.UsedFallback, sh, sort, pf, split.SizingCount, vRet.Count, wr, avg, gatedOut);
                rsCoins++; rsSizingTrades += split.SizingCount; rsScoredTrades += vRet.Count;
                if (split.UsedFallback) rsFallbacks++;
                rsCoinStats.Add((sym, sh, sort, pf, vRet.Count, wr, avg, conf));
                foreach (var t in scored)
                {
                    double sgRs = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.RipShort, t.Time) : 1.0;
                    double cRs = conf * sgRs;
                    rsTrades.Add((t.Time, t.Return, cRs));
                    allTrades.Add((t.Time, t.Return, cRs, "ripshort", t.EntryTime, sym));
                    RouteVolVariant(rsVarLabel, sym, t.Time, t.Return, cRs, "ripshort", vCC);
                }
                RecordAppliedConf(rsVarLabel, sym, conf);
            }
            PrintSizingFooter(rsCoins, rsFallbacks, rsSizingTrades, rsScoredTrades);
        }

        if (allTrades.Count == 0) { Console.WriteLine("\nNo OOS trades generated."); return; }

        allTrades.Sort((a, b)   => a.Time.CompareTo(b.Time));
        swingTrades.Sort((a, b) => a.Time.CompareTo(b.Time));
        gridTrades.Sort((a, b)  => a.Time.CompareTo(b.Time));
        flTrades.Sort((a, b)    => a.Time.CompareTo(b.Time));
        dlTrades.Sort((a, b)    => a.Time.CompareTo(b.Time));
        slTrades.Sort((a, b)    => a.Time.CompareTo(b.Time));
        rsTrades.Sort((a, b)    => a.Time.CompareTo(b.Time));

        // Per-strategy concurrent cap
        if (allTrades.Count > 0)
        {
            if (dropped.Count > 0)
            {
                int before = allTrades.Count;
                allTrades = allTrades.Where(t => !dropped.Contains(t.Strategy)).ToList();
                Console.WriteLine($"  [DROP] {before - allTrades.Count} trades removed from the portfolio");
            }
            var capInput = allTrades.Select(t => new PortfolioReplay.Trade(
                t.Strategy,
                t.Entry == default ? t.Time : t.Entry,
                t.Entry == default ? TimeSpan.FromHours(48) : (t.Time - t.Entry),
                t.Return,
                t.Conf,
                t.Sym)).ToList();
            int noEntry = allTrades.Count(t => t.Entry == default);
            if (noEntry > 0)
                Console.WriteLine($"  !! {noEntry} trades carry no entry time — concurrency modelled with the legacy constant");
            // Correlation-aware directional cap. OFF unless GRAVITY_CROWDING is set; one-sided when
            // on, and estimated strictly before the book's first trade. See SymbolCrowdingCap.
            var crowding = SymbolCrowdingCap.BuildForBook(fetched, capInput, "OOS BOOK");
            var filtered = PortfolioReplay.FilterByConcurrentCap(capInput, directionalCap: Config.MaxDirectionalConcurrent, perSymbolCap: Config.MaxPerSymbolConcurrent, crowding: crowding);
            Console.WriteLine($"  Concurrent cap: {allTrades.Count} → {filtered.Count} trades ({allTrades.Count - filtered.Count} removed)");
            allTrades = filtered.Select(t => (t.EntryTime + t.HoldDuration, t.Return, t.Conf, t.Strategy, t.EntryTime, t.Symbol)).ToList();

            // Central grading — identical code to combinedbacktest and fulltest. This is the OOS
            // book on never-trained coins, so it is the one grade in the suite with no selection
            // pressure behind it.
            StrategyEvaluation.Report("OOS BOOK", filtered, btcRegimeSeries,
                                      csvPath: "reports/oos_trades.csv");
        }

        var swingRet = swingTrades.Select(t => t.Return).ToList();
        var gridRet  = gridTrades.Select(t => t.Return).ToList();
        var flRet    = flTrades.Select(t => t.Return).ToList();
        var dlRet    = dlTrades.Select(t => t.Return).ToList();
        var slRet    = slTrades.Select(t => t.Return).ToList();
        var rsRet    = rsTrades.Select(t => t.Return).ToList();
        var allRet   = allTrades.Select(t => t.Return).ToList();

        string Pct(List<double> r) => r.Count > 0 ? $"WR={(double)r.Count(x => x > 0)/r.Count:P0}  Avg={r.Average():+0.00}%" : "no trades";

        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  OOS COMBINED SUMMARY  (full history · {allRet.Count} trades · {Config.OosCoins.Length} coins)");
        Console.WriteLine($"{new string('═', 70)}");
        Console.WriteLine($"  Scored window only — each coin's leading {SizingFraction:P0} of trades was consumed to");
        Console.WriteLine($"  derive its position size and is excluded from every figure below.");
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
        Console.WriteLine($"  Total:     {allRet.Count,4} trades  PF={Simulator.ProfitFactor(allRet):F2}  {Pct(allRet)}");
        Console.WriteLine();
        Console.WriteLine($"  Sharpe:  {Simulator.SharpeRatio(allRet, totalVCC):F2}");
        Console.WriteLine($"  Sortino: {Simulator.SortinoRatio(allRet, totalVCC):F2}");
        Console.WriteLine($"  Calmar:  {Simulator.CalmarRatio(allRet):F2}");

        static TimeSpan OosStrategyHold(string strat, FadeShortGenotype swG, GridGenotype grG, FadeLongGenotype? flG, DipLongGenotype? dlG, SwingLongGenotype? slG, RipShortGenotype? rsG) => strat switch
        {
            "swing"      => TimeSpan.FromHours(swG.MaxHoldCandles),
            "swing_long" => TimeSpan.FromHours(slG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "fadelong"   => TimeSpan.FromHours(flG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "diplong"    => TimeSpan.FromHours(dlG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            "ripshort"   => TimeSpan.FromHours(rsG?.MaxHoldCandles ?? swG.MaxHoldCandles),
            _            => TimeSpan.FromHours(grG.MaxHoldCandles),
        };

        var allTradesForExposure = allTrades
            .Select(t => (t.Time, t.Return, t.Conf, OosStrategyHold(t.Strategy, swingG, gridG, flG, dlG, slG, rsG)))
            .ToList();

        // Same trades, strategy label retained, for the guarded/unguarded comparison below.
        var allTradesGuardInput = allTrades
            .Select(t => new GuardedPortfolio.Trade(
                t.Time, t.Return, t.Conf,
                OosStrategyHold(t.Strategy, swingG, gridG, flG, dlG, slG, rsG), t.Strategy))
            .ToList();

        var port5cap  = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var portKelly = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct);

        void PrintPort(string label, Simulator.PortfolioResult p)
        {
            double ret = (p.EndBalance - p.StartBalance) / p.StartBalance * 100;
            Console.WriteLine($"\n  ── {label} ──");
            Console.WriteLine($"    End balance:   €{p.EndBalance:F2}  ({ret:+0.0;-0.0}%)");
            Console.WriteLine($"    Avg position:  €{p.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {p.MaxDrawdownPct:F1}%");
        }

        PrintPort($"5% per position · {Config.MaxTotalExposurePct:P0} total cap  [concurrent-aware]", port5cap);
        PrintPort($"half-Kelly · {Config.MaxTotalExposurePct:P0} total cap  [concurrent-aware]", portKelly);

        // Both blocks above are UNGUARDED, as they have always been. The guarded column is new
        // information printed alongside them, never in place of them.
        GuardedPortfolio.PrintComparison(
            "OOS portfolio (never-seen coins, full history)",
            allTradesGuardInput, guardCtx, Config.MaxTotalExposurePct);

        if (allTrades.Count >= 2)
        {
            double days      = (allTrades[^1].Time - allTrades[0].Time).TotalDays;
            double annFactor = days > 0 ? 365.0 / days : 1.0;
            void AnnLine(string tag, Simulator.PortfolioResult p)
            {
                double r   = (p.EndBalance - 100) / 100 * 100;
                double ann = (Math.Pow(1 + r / 100.0, annFactor) - 1) * 100;
                Console.WriteLine($"  {tag}: {days:F0}d  ann {ann:+0.0;-0.0}%  DD {p.MaxDrawdownPct:F1}%");
            }
            Console.WriteLine();
            AnnLine("5% cap ", port5cap);
            AnnLine("Kelly  ", portKelly);
        }

        Console.WriteLine($"\n  Per-coin breakdown (FadeShort · sorted by Sharpe):");
        Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Scored",6}  {"WR",5}  {"AvgRet%",7}");
        Console.WriteLine($"  {new string('-', 75)}");
        foreach (var r in swingCoinStats.OrderByDescending(c => c.Sharpe))
            Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");

        if (gridCoinStats.Count > 0)
        {
            Console.WriteLine($"\n  Per-coin breakdown (Grid · sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Scored",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in gridCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (flCoinStats.Count > 0)
        {
            Console.WriteLine($"\n  Per-coin breakdown (FadeLong · sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Scored",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in flCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (dlCoinStats.Count > 0)
        {
            Console.WriteLine($"\n  Per-coin breakdown (DipLong · sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Scored",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in dlCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (slCoinStats.Count > 0)
        {
            Console.WriteLine($"\n  Per-coin breakdown (SwingLong · sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Scored",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in slCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        if (rsCoinStats.Count > 0)
        {
            Console.WriteLine($"\n  Per-coin breakdown (RipShort · sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Scored",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in rsCoinStats.OrderByDescending(c => c.Sharpe))
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
                    // The sizing that was actually applied (leading-slice Kelly), NOT a Kelly
                    // recomputed on these same scored returns — that would be the in-sample
                    // figure this command exists to avoid.
                    double kelly = highVolCoinConf.TryGetValue(kv.Key, out var cs) && cs.Count > 0 ? cs.Average() : 0;
                    return (Coin: kv.Key, Sharpe: sh, Sortino: sort, PF: pf, Trades: rets.Count, WR: wr, AvgRet: avg, Kelly: kelly);
                })
                .ToList();

            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  OOS HIGH-VOL VARIANTS SUMMARY  ({highVolCoinRets.Count} coins, {highVolRet.Count} trades, ATR≥1.5×)");
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
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            Console.WriteLine($"  Portfolio:    €{hvPort.EndBalance:F2}  ({(hvPort.EndBalance - 100) / 100 * 100:+0.0;-0.0}%)  DD={hvPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Scored",6}  {"WR",5}  {"AvgRet%",7}");
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
                    // Applied sizing, not an in-sample Kelly — see the high-vol block above.
                    double kelly = lowVolCoinConf.TryGetValue(kv.Key, out var cs) && cs.Count > 0 ? cs.Average() : 0;
                    return (Coin: kv.Key, Sharpe: sh, Sortino: sort, PF: pf, Trades: rets.Count, WR: wr, AvgRet: avg, Kelly: kelly);
                })
                .ToList();

            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine($"  OOS LOW-VOL VARIANTS SUMMARY  ({lowVolCoinRets.Count} coins, {lowVolRet.Count} trades, ATR≤0.8×)");
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
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            Console.WriteLine($"  Portfolio:    €{lvPort.EndBalance:F2}  ({(lvPort.EndBalance - 100) / 100 * 100:+0.0;-0.0}%)  DD={lvPort.MaxDrawdownPct:F1}%");
            Console.WriteLine();
            Console.WriteLine($"  Per-coin (sorted by Sharpe):");
            Console.WriteLine($"  {"Coin",-18} {"Kelly%",6}  {"Sharpe",7}  {"Sortino",7}  {"PF",5}  {"Scored",6}  {"WR",5}  {"AvgRet%",7}");
            Console.WriteLine($"  {new string('-', 75)}");
            foreach (var r in lvCoinStats.OrderByDescending(c => c.Sharpe))
                Console.WriteLine($"  {r.Coin,-18} {r.Kelly,6:P1}  {r.Sharpe,7:F2}  {r.Sortino,7:F2}  {r.PF,5:F2}  {r.Trades,6}  {r.WR,5:P0}  {r.AvgRet,+7:F2}%");
        }

        // Merge OOS section into backtest_results.json
        static double OosComputeMaxDD(List<double> r)
        {
            if (r.Count == 0) return 0;
            double eq = 100.0, peak = 100.0, maxDd = 0;
            foreach (var ret in r) { eq += ret; if (eq > peak) peak = eq; double dd = (peak - eq) / peak * 100; if (dd > maxDd) maxDd = dd; }
            return maxDd;
        }

        static object OosStratStats(List<double> r, int vcc) => r.Count == 0
            ? new { trades = 0, winRate = 0.0, sharpe = 0.0, pf = 0.0, maxDD = 0.0, ret = 0.0 }
            : new
            {
                trades  = r.Count,
                winRate = Math.Round((double)r.Count(x => x > 0) / r.Count * 100, 2),
                sharpe  = Math.Round(Simulator.SharpeRatio(r, vcc), 4),
                pf      = Math.Round(Simulator.ProfitFactor(r), 4),
                maxDD   = Math.Round(-OosComputeMaxDD(r), 2),
                ret     = Math.Round(r.Sum(), 2),
            };

        var oosSection = new Dictionary<string, object>
        {
            ["FadeShort"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(swingRet, totalVCC) },
            ["Grid"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(gridRet, totalVCC) },
            ["FadeLong"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(flRet, totalVCC) },
            ["DipLong"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(dlRet, totalVCC) },
            ["SwingLong"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(slRet, totalVCC) },
            ["RipShort"] = new Dictionary<string, object>
                { ["default"] = OosStratStats(rsRet, totalVCC) },
        };

        string backtestPath = "backtest_results.json";
        var existing = File.Exists(backtestPath)
            ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(backtestPath))
              ?? new Dictionary<string, object>()
            : new Dictionary<string, object>();
        existing["oos"]       = oosSection;
        if (highVolRet.Count > 0)
            existing["oos_highvol_summary"] = new
            {
                trades = highVolRet.Count,
                winRate = Math.Round((double)highVolRet.Count(x => x > 0) / highVolRet.Count * 100, 2),
                sharpe = Math.Round(Simulator.SharpeRatio(highVolRet, highVolTotalVCC), 4),
                sortino = Math.Round(Simulator.SortinoRatio(highVolRet, highVolTotalVCC), 4),
                pf = Math.Round(Simulator.ProfitFactor(highVolRet), 4),
                calmar = Math.Round(Simulator.CalmarRatio(highVolRet), 4),
                avgRet = Math.Round(highVolRet.Average(), 4),
                coins = highVolCoinRets.Count,
            };
        if (lowVolRet.Count > 0)
            existing["oos_lowvol_summary"] = new
            {
                trades = lowVolRet.Count,
                winRate = Math.Round((double)lowVolRet.Count(x => x > 0) / lowVolRet.Count * 100, 2),
                sharpe = Math.Round(Simulator.SharpeRatio(lowVolRet, lowVolTotalVCC), 4),
                sortino = Math.Round(Simulator.SortinoRatio(lowVolRet, lowVolTotalVCC), 4),
                pf = Math.Round(Simulator.ProfitFactor(lowVolRet), 4),
                calmar = Math.Round(Simulator.CalmarRatio(lowVolRet), 4),
                avgRet = Math.Round(lowVolRet.Average(), 4),
                coins = lowVolCoinRets.Count,
            };
        existing["timestamp"] = DateTime.UtcNow.ToString("O");
        File.WriteAllText(backtestPath, System.Text.Json.JsonSerializer.Serialize(existing,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("backtest_results.json updated with OOS results.");
    }

    public static async Task RunAllCoinsBacktest(BybitRestClient client)
    {
        int totalCoins = Config.BacktestCoins.Length + Config.OosCoins.Length;
        Console.WriteLine($"=== Gravity-gen2 | ALL-COINS PORTFOLIO SIM ({totalCoins} coins · BacktestCoins {DataSplit.ValLabel} + OOS full history) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype."); return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine("Missing grid genotype.");      return; }

        // Variant arrays (reuse the class-level LoadVariants helper)
        var fsVariantsAC   = StrategyPipeline.LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var gridVariantsAC = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_best", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var flVariantsAC   = StrategyPipeline.LoadVariants<FadeLongGenotypeDto, FadeLongGenotype>(
            "fade_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var dlVariantsAC   = StrategyPipeline.LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var rsVariantsAC   = StrategyPipeline.LoadVariants<RipShortGenotypeDto, RipShortGenotype>(
            "rip_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

        var swingG = fsVariantsAC.Length   > 0 ? fsVariantsAC[0].Genotype!   : null;
        var gridG  = gridVariantsAC.Length > 0 ? gridVariantsAC[0].Genotype! : null;
        if (swingG == null) { Console.WriteLine("Missing FadeShort genotype."); return; }
        if (gridG  == null) { Console.WriteLine("Missing grid genotype.");      return; }

        FadeLongGenotype?     flG     = flVariantsAC.Length > 0 ? flVariantsAC[0].Genotype : null;
        DipLongGenotype?      dlG     = dlVariantsAC.Length > 0 ? dlVariantsAC[0].Genotype : null;
        RipShortGenotype?     rsG     = rsVariantsAC.Length > 0 ? rsVariantsAC[0].Genotype : null;
        RegimeRouterGenotype? routerG = File.Exists(Config.RouterGenoFile)   ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype() : null;

        Console.WriteLine($"FadeShort: {swingG}\nGrid:      {gridG}");
        if (flG != null)     Console.WriteLine($"FadeLong:  {flG}");
        if (dlG != null)     Console.WriteLine($"DipLong:   {dlG}");
        if (rsG != null)     Console.WriteLine($"RipShort:  {rsG}");
        if (routerG != null) Console.WriteLine($"Router:    {routerG}");
        Console.WriteLine();

        var allSyms = Config.BacktestCoins.Concat(Config.OosCoins).Concat(new[] { "BTCUSDT", "ETHUSDT" }).Distinct().ToArray();
        Console.WriteLine($"  Fetching {allSyms.Length} symbols (15m → 1h, ~3yr)...");
        var fetchedAll = await StrategyPipeline.FetchFifteenMinAsync(client, allSyms, batches: 113);
        Console.WriteLine($"  Done.\n");

        // Per-symbol funding, same rationale as RunOosBacktest above: this command previously
        // priced every position at the flat interest-rate floor.
        Console.WriteLine("  Fetching per-symbol funding rate history...");
        var funding = await StrategyPipeline.FetchFundingAsync(client, fetchedAll);
        Console.WriteLine();

        // Hoisted for the central evaluator, as in RunOosBacktest.
        var router = StrategyPipeline.BuildRouterSession(routerG, fetchedAll, null);
        RegimeRouterSession? session  = router.Session;
        RegimeBar[]?         btcRegimeSeries = router.BtcRegimeSeries;

        var allTrades = new List<(DateTime Time, double Return, double Conf, string Strategy, DateTime Entry, string Sym)>();

        static TimeSpan AcHold(string s, FadeShortGenotype sw, GridGenotype gr, FadeLongGenotype? fl, DipLongGenotype? dl, RipShortGenotype? rs) => s switch
        {
            "swing"    => TimeSpan.FromHours(sw.MaxHoldCandles),
            "fadelong" => TimeSpan.FromHours(fl?.MaxHoldCandles ?? sw.MaxHoldCandles),
            "diplong"  => TimeSpan.FromHours(dl?.MaxHoldCandles ?? sw.MaxHoldCandles),
            "ripshort" => TimeSpan.FromHours(rs?.MaxHoldCandles ?? sw.MaxHoldCandles),
            _          => TimeSpan.FromHours(gr.MaxHoldCandles),
        };

        // ── BacktestCoins — validation slice, seed-screened ─────────────────────────────
        var btFetched = fetchedAll.Where(f => Config.BacktestCoins.Contains(f.sym)).ToArray();
        int btSwing = 0, btGrid = 0, btFL = 0, btDL = 0, btRS = 0;
        int btSwingT = 0, btGridT = 0, btFLT = 0, btDLT = 0, btRST = 0;
        int btFLSize = 0, btDLSize = 0, btRSSize = 0;
        int btFLFb = 0, btDLFb = 0, btRSFb = 0;
        int btSwingSkippedShortTrain = 0;

        // Sharpe/Sortino are scaled by sqrt(candleCount); allRet below holds ONLY scored
        // trades, so the count fed to those ratios has to be the widest scored span, not
        // the widest full history. Tracked per coin-strategy as each block contributes.
        int acTotalVCC = 0;
        void NoteScoredSpan(Candle[] h1, DateTime firstScoredTime) =>
            acTotalVCC = Math.Max(acTotalVCC, ScoredCandleCount(h1, firstScoredTime));

        Console.WriteLine($"── BacktestCoins ({DataSplit.ValLabel}, seed-screened) ──────────────────────────────────");
        foreach (var (sym, m15, h1) in btFetched)
        {
            if (h1.Length < 300) continue;
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < Config.MinMedianVolUsdM) continue;

            int h1Split  = DataSplit.Split(h1).Train.Length;
            int m15Split = h1Split * 4;
            var h1Train  = h1[..h1Split];
            var h1Val    = h1[h1Split..];
            var m15Train = m15[..m15Split];
            var m15Val   = m15[m15Split..];

            // FadeShort — screen and sizing read the train window ONLY. A coin whose train
            // window is too short to screen is skipped: the old fallback to the full series
            // let the val window pick its own coins and set their position size.
            {
                var screenWin = FadeShortScreenWindow(h1Train, m15Train);
                if (screenWin == null)
                {
                    Console.WriteLine($"  {sym,-16}  skip FadeShort (train window {h1Train.Length} h1 bars < {MinFadeShortTrainH1Bars} — no leak-free screen)");
                    btSwingSkippedShortTrain++;
                }
                else
                {
                    var (screenH1, screenM15) = screenWin.Value;
                    var coinFsGAC = StrategyPipeline.SelectVariant(fsVariantsAC, screenM15) ?? swingG;
                    var tRet = FadeShortSimulator.GetFadeShortReturns(coinFsGAC, screenH1, screenM15, funding.For(sym)).Select(t => t.Return).ToList();
                    if (tRet.Count >= 5 && tRet.Average() > 0 && Simulator.ProfitFactor(tRet) >= 1.2 && Simulator.SortinoRatio(tRet, screenH1.Length * 12) >= 0.3)
                    {
                        double conf  = Simulator.ComputeConfidence(tRet);
                        var    vRet  = FadeShortSimulator.GetFadeShortReturns(coinFsGAC, h1Val, m15Val, funding.For(sym));
                        btSwing++; btSwingT += vRet.Count;
                        if (vRet.Count > 0) NoteScoredSpan(h1, h1Val[0].Time);
                        foreach (var (t, ret, _, et, _) in vRet) allTrades.Add((t, ret, conf, "swing", et, sym));
                    }
                }
            }

            // Grid — screen already reads h1Train only; variant selection now does too.
            {
                var coinGridGAC = StrategyPipeline.SelectVariant(gridVariantsAC, m15Train) ?? gridG;
                var tRet = GridSimulator.GetGridReturns(coinGridGAC, h1Train, funding.For(sym)).Select(t => t.Return).ToList();
                if (tRet.Count >= 5 && tRet.Average() > 0 && Simulator.ProfitFactor(tRet) >= 1.2 && Simulator.SortinoRatio(tRet, h1Train.Length) >= 0.3)
                {
                    double conf = Simulator.ComputeConfidence(tRet);
                    var    vRet = GridSimulator.GetGridReturns(coinGridGAC, h1Val, funding.For(sym));
                    int    kept = vRet.Count(t => session == null || session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time));
                    btGrid++; btGridT += kept;
                    if (kept > 0) NoteScoredSpan(h1, h1Val[0].Time);
                    foreach (var (t, ret, _, et, _) in vRet)
                    {
                        if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.Grid, t)) continue;
                        double sgAc = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.Grid, t) : 1.0;
                        allTrades.Add((t, ret, conf * sgAc, "grid", et, sym));
                    }
                }
            }

            // FadeLong — no train-window screen exists for the regime-gated strategies here,
            // so size from the leading slice of the coin's own val trades and score the rest.
            if (flG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var coinFlGAC = StrategyPipeline.SelectVariant(flVariantsAC, m15Train) ?? flG;
                var raw   = FadeLongSimulator.GetFadeLongReturns(coinFlGAC, h1Val, m15Val, funding.For(sym));
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList() : raw;
                var split = SizeThenScore(gated, t => t.Time, t => t.Return);
                if (split.Scored.Count > 0)
                {
                    btFL++; btFLT += split.Scored.Count; btFLSize += split.SizingCount;
                    if (split.UsedFallback) btFLFb++;
                    NoteScoredSpan(h1, split.Scored[0].Time);
                    foreach (var t in split.Scored)
                    {
                        double sgFlBt = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.FadeLong, t.Time) : 1.0;
                        allTrades.Add((t.Time, t.Return, split.Conf * sgFlBt, "fadelong", t.EntryTime, sym));
                    }
                }
            }

            // DipLong
            if (dlG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var coinDlGAC = StrategyPipeline.SelectVariant(dlVariantsAC, m15Train) ?? dlG;
                var raw   = DipLongSimulator.GetDipLongReturns(coinDlGAC, h1Val, m15Val, funding.For(sym));
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                var split = SizeThenScore(gated, t => t.Time, t => t.Return);
                if (split.Scored.Count > 0)
                {
                    btDL++; btDLT += split.Scored.Count; btDLSize += split.SizingCount;
                    if (split.UsedFallback) btDLFb++;
                    NoteScoredSpan(h1, split.Scored[0].Time);
                    foreach (var t in split.Scored)
                    {
                        double sgDlBt = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.DipLong, t.Time) : 1.0;
                        allTrades.Add((t.Time, t.Return, split.Conf * sgDlBt, "diplong", t.EntryTime, sym));
                    }
                }
            }

            // RipShort
            if (rsG != null && h1Val.Length >= 100 && m15Val.Length >= 400)
            {
                var coinRsGAC = StrategyPipeline.SelectVariant(rsVariantsAC, m15Train) ?? rsG;
                var raw   = RipShortSimulator.GetRipShortReturns(coinRsGAC, h1Val, m15Val, funding.For(sym), StrategyPipeline.ProdRipCap(),
                                ExitRatchet.ForStrategy("ripshort"));
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time)).ToList() : raw;
                var split = SizeThenScore(gated, t => t.Time, t => t.Return);
                if (split.Scored.Count > 0)
                {
                    btRS++; btRST += split.Scored.Count; btRSSize += split.SizingCount;
                    if (split.UsedFallback) btRSFb++;
                    NoteScoredSpan(h1, split.Scored[0].Time);
                    foreach (var t in split.Scored)
                    {
                        double sgRsBt = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.RipShort, t.Time) : 1.0;
                        allTrades.Add((t.Time, t.Return, split.Conf * sgRsBt, "ripshort", t.EntryTime, sym));
                    }
                }
            }
        }
        Console.WriteLine($"  FadeShort: {btSwing,3} coins → {btSwingT,4} trades  (screened + sized from train window)"
                        + (btSwingSkippedShortTrain > 0 ? $"  ·  {btSwingSkippedShortTrain} coin(s) skipped: train window < {MinFadeShortTrainH1Bars} h1 bars" : ""));
        Console.WriteLine($"  Grid:      {btGrid,3} coins → {btGridT,4} trades  (screened + sized from train window)");
        if (flG != null) Console.WriteLine($"  FadeLong:  {btFL,3} coins → {btFLT,4} scored, {btFLSize,4} used for sizing{(btFLFb > 0 ? $"  ({btFLFb} coin(s) on {FallbackConf:P1} fallback)" : "")}");
        if (dlG != null) Console.WriteLine($"  DipLong:   {btDL,3} coins → {btDLT,4} scored, {btDLSize,4} used for sizing{(btDLFb > 0 ? $"  ({btDLFb} coin(s) on {FallbackConf:P1} fallback)" : "")}");
        if (rsG != null) Console.WriteLine($"  RipShort:  {btRS,3} coins → {btRST,4} scored, {btRSSize,4} used for sizing{(btRSFb > 0 ? $"  ({btRSFb} coin(s) on {FallbackConf:P1} fallback)" : "")}");

        // ── OosCoins — full history, no seed screen ───────────────────────────
        const double oosMinVol2 = 0.05;
        var oosFetched2 = fetchedAll.Where(f => Config.OosCoins.Contains(f.sym)).ToArray();
        int oSwing = 0, oGrid = 0, oFL = 0, oDL = 0, oRS = 0;
        int oSwingT = 0, oGridT = 0, oFLT = 0, oDLT = 0, oRST = 0;
        int oSwingS = 0, oGridS = 0, oFLS = 0, oDLS = 0, oRSS = 0;   // trades consumed for sizing
        int oSwingFb = 0, oGridFb = 0, oFLFb = 0, oDLFb = 0, oRSFb = 0;

        Console.WriteLine($"\n── OosCoins (full history, vol≥${oosMinVol2:F2}M, no screen) ─────────────────────");
        Console.WriteLine($"   Sizing from each coin's leading {SizingFraction:P0} of trades; remainder scored.");
        // NOTE (unfixed, documented): SelectVariant below reads the ATR of the LAST bar of
        // the full series to pick a volatility variant, so the variant choice does see the
        // scored window. Unlike the BacktestCoins cohort above there is no train window
        // here to select on, and RunOosBacktest has the same property — changing it would
        // need a decision on what "as of" bar to use, so it is left consistent and flagged
        // rather than silently altered.
        foreach (var (sym, m15, h1) in oosFetched2)
        {
            if (h1.Length < 300) continue;
            var volUsd = h1.Select(c => c.Close * c.Volume / 1_000_000.0).OrderBy(v => v).ToList();
            double medVol = volUsd.Count > 0 ? volUsd[volUsd.Count / 2] : 0;
            if (medVol < oosMinVol2) continue;

            // FadeShort
            {
                var coinFsGOos = StrategyPipeline.SelectVariant(fsVariantsAC, m15) ?? swingG;
                var trades = FadeShortSimulator.GetFadeShortReturns(coinFsGOos, h1, m15, funding.For(sym));
                var split  = SizeThenScore(trades, t => t.Time, t => t.Return);
                if (split.Scored.Count >= 5)
                {
                    oSwing++; oSwingT += split.Scored.Count; oSwingS += split.SizingCount;
                    if (split.UsedFallback) oSwingFb++;
                    NoteScoredSpan(h1, split.Scored[0].Time);
                    foreach (var (t, ret, _, et, _) in split.Scored) allTrades.Add((t, ret, split.Conf, "swing", et, sym));
                }
            }

            // Grid
            {
                var coinGridGOos = StrategyPipeline.SelectVariant(gridVariantsAC, m15) ?? gridG;
                var raw   = GridSimulator.GetGridReturns(coinGridGOos, h1, funding.For(sym));
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.Grid, t.Time)).ToList() : raw;
                var split = SizeThenScore(gated, t => t.Time, t => t.Return);
                if (split.Scored.Count >= 5)
                {
                    oGrid++; oGridT += split.Scored.Count; oGridS += split.SizingCount;
                    if (split.UsedFallback) oGridFb++;
                    NoteScoredSpan(h1, split.Scored[0].Time);
                    foreach (var t in split.Scored)
                    {
                        double sgGrO = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.Grid, t.Time) : 1.0;
                        allTrades.Add((t.Time, t.Return, split.Conf * sgGrO, "grid", t.EntryTime, sym));
                    }
                }
            }

            // FadeLong
            if (flG != null && m15.Length >= 1200)
            {
                var coinFlGOos = StrategyPipeline.SelectVariant(flVariantsAC, m15) ?? flG;
                var raw   = FadeLongSimulator.GetFadeLongReturns(coinFlGOos, h1, m15, funding.For(sym));
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList() : raw;
                var split = SizeThenScore(gated, t => t.Time, t => t.Return);
                if (split.Scored.Count > 0)
                {
                    oFL++; oFLT += split.Scored.Count; oFLS += split.SizingCount;
                    if (split.UsedFallback) oFLFb++;
                    NoteScoredSpan(h1, split.Scored[0].Time);
                    foreach (var t in split.Scored)
                    {
                        double sgFlO = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.FadeLong, t.Time) : 1.0;
                        allTrades.Add((t.Time, t.Return, split.Conf * sgFlO, "fadelong", t.EntryTime, sym));
                    }
                }
            }

            // DipLong
            if (dlG != null && m15.Length >= 1200)
            {
                var coinDlGOos = StrategyPipeline.SelectVariant(dlVariantsAC, m15) ?? dlG;
                var raw   = DipLongSimulator.GetDipLongReturns(coinDlGOos, h1, m15, funding.For(sym));
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList() : raw;
                var split = SizeThenScore(gated, t => t.Time, t => t.Return);
                if (split.Scored.Count > 0)
                {
                    oDL++; oDLT += split.Scored.Count; oDLS += split.SizingCount;
                    if (split.UsedFallback) oDLFb++;
                    NoteScoredSpan(h1, split.Scored[0].Time);
                    foreach (var t in split.Scored)
                    {
                        double sgDlO = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.DipLong, t.Time) : 1.0;
                        allTrades.Add((t.Time, t.Return, split.Conf * sgDlO, "diplong", t.EntryTime, sym));
                    }
                }
            }

            // RipShort
            if (rsG != null && m15.Length >= 1200)
            {
                var coinRsGOos = StrategyPipeline.SelectVariant(rsVariantsAC, m15) ?? rsG;
                var raw   = RipShortSimulator.GetRipShortReturns(coinRsGOos, h1, m15, funding.For(sym), StrategyPipeline.ProdRipCap());
                var gated = session != null ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time)).ToList() : raw;
                var split = SizeThenScore(gated, t => t.Time, t => t.Return);
                if (split.Scored.Count > 0)
                {
                    oRS++; oRST += split.Scored.Count; oRSS += split.SizingCount;
                    if (split.UsedFallback) oRSFb++;
                    NoteScoredSpan(h1, split.Scored[0].Time);
                    foreach (var t in split.Scored)
                    {
                        double sgRsO = session != null ? session.SizeGate(RegimeRouterGA.StrategyKind.RipShort, t.Time) : 1.0;
                        allTrades.Add((t.Time, t.Return, split.Conf * sgRsO, "ripshort", t.EntryTime, sym));
                    }
                }
            }
        }
        string Fb(int n) => n > 0 ? $"  ({n} coin(s) on {FallbackConf:P1} fallback)" : "";
        Console.WriteLine($"  FadeShort: {oSwing,3} coins → {oSwingT,4} scored, {oSwingS,4} used for sizing{Fb(oSwingFb)}");
        Console.WriteLine($"  Grid:      {oGrid,3} coins → {oGridT,4} scored, {oGridS,4} used for sizing{Fb(oGridFb)}");
        if (flG != null) Console.WriteLine($"  FadeLong:  {oFL,3} coins → {oFLT,4} scored, {oFLS,4} used for sizing{Fb(oFLFb)}");
        if (dlG != null) Console.WriteLine($"  DipLong:   {oDL,3} coins → {oDLT,4} scored, {oDLS,4} used for sizing{Fb(oDLFb)}");
        if (rsG != null) Console.WriteLine($"  RipShort:  {oRS,3} coins → {oRST,4} scored, {oRSS,4} used for sizing{Fb(oRSFb)}");

        if (allTrades.Count == 0) { Console.WriteLine("\nNo trades generated."); return; }
        allTrades.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Per-strategy concurrent cap
        if (allTrades.Count > 0)
        {
            var capInput = allTrades.Select(t => new PortfolioReplay.Trade(
                t.Strategy,
                t.Entry == default ? t.Time : t.Entry,
                t.Entry == default ? TimeSpan.FromHours(48) : (t.Time - t.Entry),
                t.Return,
                t.Conf,
                t.Sym)).ToList();
            int noEntry = allTrades.Count(t => t.Entry == default);
            if (noEntry > 0)
                Console.WriteLine($"  !! {noEntry} trades carry no entry time — concurrency modelled with the legacy constant");
            // Correlation-aware directional cap. OFF unless GRAVITY_CROWDING is set; one-sided when
            // on, and estimated strictly before the book's first trade. See SymbolCrowdingCap.
            var crowding = SymbolCrowdingCap.BuildForBook(fetchedAll, capInput, "ALL-COINS BOOK");
            var filtered = PortfolioReplay.FilterByConcurrentCap(capInput, directionalCap: Config.MaxDirectionalConcurrent, perSymbolCap: Config.MaxPerSymbolConcurrent, crowding: crowding);
            Console.WriteLine($"  Concurrent cap: {allTrades.Count} → {filtered.Count} trades ({allTrades.Count - filtered.Count} removed)");
            allTrades = filtered.Select(t => (t.EntryTime + t.HoldDuration, t.Return, t.Conf, t.Strategy, t.EntryTime, t.Symbol)).ToList();

            StrategyEvaluation.Report("ALL-COINS BOOK", filtered, btcRegimeSeries,
                                      csvPath: "reports/allcoins_trades.csv");
        }

        var allRet   = allTrades.Select(t => t.Return).ToList();
        var swingRet = allTrades.Where(t => t.Strategy == "swing").Select(t => t.Return).ToList();
        var gridRet  = allTrades.Where(t => t.Strategy == "grid").Select(t => t.Return).ToList();
        var flRet    = allTrades.Where(t => t.Strategy == "fadelong").Select(t => t.Return).ToList();
        var dlRet    = allTrades.Where(t => t.Strategy == "diplong").Select(t => t.Return).ToList();
        var rsRet    = allTrades.Where(t => t.Strategy == "ripshort").Select(t => t.Return).ToList();
        // allRet holds SCORED trades only (val window for BacktestCoins, post-sizing-slice
        // for OosCoins), so the candle count handed to Sharpe/Sortino must be the widest
        // SCORED span — not the widest full history, which would inflate both by
        // sqrt(fullBars / scoredBars). Same convention as RunOosBacktest (ScoredCandleCount).
        int totalVCC = Math.Max(acTotalVCC, 1);

        string Pct(List<double> r) => r.Count > 0 ? $"WR={(double)r.Count(x => x > 0)/r.Count:P0}  Avg={r.Average():+0.00}%" : "no trades";

        Console.WriteLine($"\n{new string('═', 70)}");
        Console.WriteLine($"  COMBINED  ({allRet.Count} trades · {btSwing+btGrid+btFL+btDL+btRS+oSwing+oGrid+oFL+oDL+oRS} coin-strategy pairs)");
        Console.WriteLine($"{new string('═', 70)}");
        Console.WriteLine($"  FadeShort: {swingRet.Count,4} trades  PF={Simulator.ProfitFactor(swingRet):F2}  {Pct(swingRet)}");
        Console.WriteLine($"  Grid:      {gridRet.Count,4} trades  PF={Simulator.ProfitFactor(gridRet):F2}  {Pct(gridRet)}");
        if (flRet.Count > 0) Console.WriteLine($"  FadeLong:  {flRet.Count,4} trades  PF={Simulator.ProfitFactor(flRet):F2}  {Pct(flRet)}");
        if (dlRet.Count > 0) Console.WriteLine($"  DipLong:   {dlRet.Count,4} trades  PF={Simulator.ProfitFactor(dlRet):F2}  {Pct(dlRet)}");
        if (rsRet.Count > 0) Console.WriteLine($"  RipShort:  {rsRet.Count,4} trades  PF={Simulator.ProfitFactor(rsRet):F2}  {Pct(rsRet)}");
        Console.WriteLine($"  Total:     {allRet.Count,4} trades  PF={Simulator.ProfitFactor(allRet):F2}  {Pct(allRet)}");
        Console.WriteLine($"  Sharpe: {Simulator.SharpeRatio(allRet, totalVCC):F2}  Sortino: {Simulator.SortinoRatio(allRet, totalVCC):F2}  Calmar: {Simulator.CalmarRatio(allRet):F2}");

        // ── Concurrency analysis ──────────────────────────────────────────────
        var openEvents = new List<(DateTime Time, int Delta)>();
        foreach (var t in allTrades)
        {
            var hold = AcHold(t.Strategy, swingG, gridG, flG, dlG, rsG);
            openEvents.Add((t.Time, +1));
            openEvents.Add((t.Time + hold, -1));
        }
        openEvents.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : b.Delta.CompareTo(a.Delta));
        int peakConc = 0, curConc = 0;
        long totalConcTicks = 0; DateTime prevTime = DateTime.MinValue; long concAcc = 0;
        foreach (var (evtTime, delta) in openEvents)
        {
            if (prevTime != DateTime.MinValue) concAcc += curConc * (evtTime - prevTime).Ticks;
            curConc += delta; if (curConc > peakConc) peakConc = curConc;
            prevTime = evtTime; totalConcTicks += (evtTime - openEvents[0].Time).Ticks > 0 ? 0 : 0;
        }
        double totalSpanTicks = allTrades.Count > 1
            ? (allTrades[^1].Time + AcHold(allTrades[^1].Strategy, swingG, gridG, flG, dlG, rsG) - allTrades[0].Time).Ticks
            : 1;
        double avgConc = totalSpanTicks > 0 ? concAcc / totalSpanTicks : 0;

        Console.WriteLine($"\n── Concurrency analysis (hold-duration aware) ───────────────────────────────");
        Console.WriteLine($"  Peak simultaneous open positions: {peakConc}");
        Console.WriteLine($"  Max exposure at 5%/trade cap:     {peakConc * 5}%  ({peakConc} × 5%)");
        Console.WriteLine($"  Avg concurrent open (time-weighted): {avgConc:F1}");
        Console.WriteLine($"  Avg exposure at 5%/trade cap:     {avgConc * 5:F1}%");

        // ── Portfolio simulations ──────────────────────────────────────────────
        var allTradesForExposure = allTrades
            .Select(t => (t.Time, t.Return, t.Conf, AcHold(t.Strategy, swingG, gridG, flG, dlG, rsG)))
            .ToList();

        var port5cap = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
        var portKel  = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct);

        void PrintSim(string label, Simulator.PortfolioResult p)
        {
            double ret = (p.EndBalance - p.StartBalance) / p.StartBalance * 100;
            Console.WriteLine($"\n  ── {label} ──");
            Console.WriteLine($"    End balance:   €{p.EndBalance:F2}  ({ret:+0.0;-0.0}%)");
            Console.WriteLine($"    Avg position:  €{p.AvgPositionEur:F2}");
            Console.WriteLine($"    Max drawdown:  {p.MaxDrawdownPct:F1}%");
            if (p.TradesToTenPct > 0) Console.WriteLine($"    Trades to +10%:{p.TradesToTenPct}");
        }

        Console.WriteLine($"\n── Portfolio simulations (€100 start · {allTrades.Count} trades · concurrent-aware) ──────");
        PrintSim($"5% per position · {Config.MaxTotalExposurePct:P0} total cap", port5cap);
        PrintSim($"half-Kelly · {Config.MaxTotalExposurePct:P0} total cap", portKel);

        if (allTrades.Count >= 2)
        {
            double days      = (allTrades[^1].Time - allTrades[0].Time).TotalDays;
            double annFactor = days > 0 ? 365.0 / days : 1.0;
            void AnnLine(string tag, Simulator.PortfolioResult p)
            {
                double r   = (p.EndBalance - 100) / 100 * 100;
                double ann = (Math.Pow(1 + r / 100.0, annFactor) - 1) * 100;
                Console.WriteLine($"  {tag}: {days:F0}d  ann {ann:+0.0;-0.0}%  DD {p.MaxDrawdownPct:F1}%");
            }
            Console.WriteLine();
            AnnLine("5% cap ", port5cap);
            AnnLine("Kelly  ", portKel);
        }

        Console.WriteLine($"\n── Concurrency → 30% total cap explanation ──────────────────────────────────");
        Console.WriteLine($"  Peak {peakConc} simultaneous positions. Without a total cap,");
        Console.WriteLine($"  5% × {peakConc} = {peakConc * 5}% deployed at peak — far beyond safe leverage.");
        Console.WriteLine($"  The 30% total cap means each new entry gets min(sizing, remaining headroom).");
        Console.WriteLine($"  When 6 positions are open at 5% each, the 7th gets €0 until one closes.");
    }
}
