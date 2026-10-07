using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

// Does this system have an edge, measured so that the answer can be NO.
//
// Every other backtest in this repo answers a weaker question. Three things make their headline
// numbers unusable as evidence, and this command removes all three:
//
//  1. THE GATE WAS FITTED ON WHAT IT GATES. genotypes/strategy_family_gate.json holds
//     (strategy, regime) profit factors computed over the whole training trade set, and
//     RegimeRouterSession routes on them — with FamilyConfidence = in-sample PF / 3 sizing the
//     position. It already knows which regimes each strategy turned out to work in. Here the gate
//     is RollingStrategyGate: re-fitted every 30 days on the trailing 180 days of trades that had
//     already CLOSED, so no decision sees its own outcome. That is also the decay mechanism the
//     system needs in production — a dead strategy routes itself off.
//
//  2. "SHARPE" WAS NOT A SHARPE. Simulator.SharpeRatio is a per-trade mean/std scaled by
//     sqrt(candleCount / 288), which is why oosbacktest prints 10.95. A portfolio Sharpe has to
//     come from the equity curve, so everything below is computed on DAILY marked-to-market
//     returns and annualised by sqrt(365).
//
//  3. NOTHING WAS DEFLATED FOR THE SEARCH. Thousands of genotypes were evaluated to pick the ones
//     in genotypes/. The best of N random strategies has a high in-sample Sharpe by construction.
//     StatisticalTests has carried DeflatedSharpeRatio, ProbabilityOfBacktestOverfitting and
//     WhitesRealityCheck for a while with nothing calling them on a portfolio book. They are
//     called here.
//
// Read-only: loads the committed genotypes, places no orders, writes no genotype.
//
// ponytail: six strategies, not eight. FadeLong is disabled on disk
// (fade_long_genotype.json.DISABLED_2026-08-24) and AccumulationGrid takes a target regime, so it
// does not fit the uniform call. Add them when they are live; the gate is per-strategy and needs
// no change to carry more.
public static class EdgeTest
{
    private const int    Batches        = 113;
    private const double StartBalance   = 1000.0;
    private const double MaxPositionPct = 0.05;   // 5% of equity per position
    // GRAVITY_EDGE_MAXCONC: research override for the headcount-control test; default mirrors Config.MaxDirectionalConcurrent.
    private static readonly int MaxConcurrent = int.TryParse(Environment.GetEnvironmentVariable("GRAVITY_EDGE_MAXCONC"), out int mc) && mc > 0 ? mc : 20;

    // PER-STRATEGY concurrency caps, from PortfolioReplay.DefaultCaps — the production portfolio
    // layer applies these BEFORE the directional cap, and this harness did not. Without them one
    // strategy can take the whole directional budget on arrival order alone: FadeShort was holding
    // 8,919 slots and the cap was turning away 1,108 Grid and 1,248 GridShort trades, which is why
    // the acceptance gate rejected it. Measuring stricter than production made a crowding artifact
    // look like a property of the strategy.
    private static int CapFor(string strategy) =>
        PortfolioReplay.DefaultCaps.TryGetValue(ProductionLabel(strategy), out int c) ? c : int.MaxValue;

    // edgetest's display names -> the labels PortfolioReplay.DefaultCaps is keyed on. FadeShort is
    // "swing" there, a naming legacy the registry also carries (FadeShortAdapter.Label => "swing").
    private static string ProductionLabel(string strategy) => strategy switch
    {
        "FadeShort" => "swing",
        "Grid"      => "grid",
        "GridShort" => "gridshort",
        "SwingLong" => "swing_long",
        "DipLong"   => "diplong",
        "FadeLong"  => "fadelong",
        "RipShort"  => "ripshort",
        "AccumGrid" => "accumgrid",
        "GridOverlay"      => "gridoverlay",
        "GridShortOverlay" => "gridshortoverlay",
        _           => strategy,
    };

    // Candidate evaluations behind genotypes/, READ FROM THE LEDGER that the GAs now write
    // (GaTrialCounter). Falls back to a deliberately large default when no ledger exists, because
    // "we did not measure the search" must deflate the Sharpe, never inflate it. This number
    // decides the verdict: the same book scores DSR 0.971 at 1,000 trials and 0.770 at 100,000.
    private static int GaTrials => GaTrialCounter.LoadTotal();

    // symbol -> h1 (times, closes), used to mark OPEN positions at the price that actually
    // obtained, instead of accruing their final return in a straight line. Static because Evaluate
    // is a static helper called from several places in a single-run command.
    private static Dictionary<string, (DateTime[] T, double[] C)> _prices = new();
    // GRAVITY_CROWDING: correlation-aware charge on the shared budget (null = off, bit-identical).
    private static SymbolCrowdingCap? _crowding;

    private static double PriceAt(string sym, DateTime t)
    {
        if (!_prices.TryGetValue(sym, out var p) || p.T.Length == 0) return double.NaN;
        if (t <= p.T[0])  return p.C[0];
        if (t >= p.T[^1]) return p.C[^1];
        int lo = 0, hi = p.T.Length - 1;
        while (lo < hi) { int m = (lo + hi + 1) / 2; if (p.T[m] <= t) lo = m; else hi = m - 1; }
        return p.C[lo];
    }

    // HardStop and BailOut pushed far beyond any reachable ATR excursion. Not infinity: the
    // simulator computes price levels from these, and an infinite level would produce NaN rather
    // than a disabled check.
    private static readonly bool StructGrid = Environment.GetEnvironmentVariable("GRAVITY_GRID_STRUCT") == "1";

    // GRAVITY_GRID_OVERLAY=<csv from research/overlay_decisions.py>: the dynamic-grid overlay (option 1).
    // The GA Grid/GridShort run UNCHANGED; deep k=3 rungs are ADDED under their own labels whenever the
    // classifier armed one, with the GA genotype's HardStopAtrMult as an intrabar hard stop.
    private static Dictionary<(string Sym, int Side), Dictionary<DateTime, string>>? LoadOverlay()
    {
        var path = Environment.GetEnvironmentVariable("GRAVITY_GRID_OVERLAY");
        if (path is not { Length: > 0 }) return null;
        var d = new Dictionary<(string, int), Dictionary<DateTime, string>>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var p = line.Split(',');
            var key = (p[0], int.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
            if (!d.TryGetValue(key, out var m)) d[key] = m = new();
            m[DateTime.SpecifyKind(DateTime.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc)] = p[3];
        }
        Console.WriteLine($"  DYNAMIC-GRID OVERLAY ON: {d.Values.Sum(v => v.Count)} armed decisions for {d.Count} symbol-sides from {path}\n");
        return d;
    }

    private static GridGenotype CloneWithStopsRemoved(GridGenotype g) => new()
    {
        AdxThreshold = g.AdxThreshold, BbPeriod = g.BbPeriod, BbWidthMaxPct = g.BbWidthMaxPct,
        EmaPeriod = g.EmaPeriod, GridStepAtrMult = g.GridStepAtrMult, GridLevels = g.GridLevels,
        TakeProfitAtrMult = g.TakeProfitAtrMult,
        HardStopAtrMult = 1000.0, BailOutAtrMult = 1000.0,
        MaxHoldCandles = g.MaxHoldCandles,          // left in: the only thing forcing a close
        RungSellFrac = g.RungSellFrac, ReanchorAlpha = g.ReanchorAlpha,
        SlopeThreshold = g.SlopeThreshold, SlopeLookback = g.SlopeLookback,
        Fitness = g.Fitness,
    };

    private static bool IsLong(string strategy) =>
        strategy is "Grid" or "DipLong" or "SwingLong" or "FadeLong" or "AccumGrid";
    // FadeLong is evaluated again now that it has a genotype; it was excluded while disabled.

    // Unrealised return in percent at `now`, from the real price series. Null when the entry price
    // is unknown, which falls the position back to linear accrual rather than marking it at zero.
    private static Func<DateTime, double>? MarkPath(Booked t)
    {
        if (t.EntryPrice <= 0.0) return null;
        double dir = IsLong(t.Strategy) ? 1.0 : -1.0;
        string sym = t.Symbol;
        double entry = t.EntryPrice;
        return now =>
        {
            double p = PriceAt(sym, now);
            return double.IsNaN(p) ? 0.0 : dir * (p - entry) / entry * 100.0;
        };
    }

    private readonly record struct Booked(
        DateTime Entry, DateTime Exit, double Ret, double Size, string Strategy, string Symbol,
        MarketRegime Regime = MarketRegime.Ranging, double EntryPrice = 0.0, bool Stop = false);

    public static async Task Run(BybitRestClient client)
    {
        Console.WriteLine("\n═══ EDGE TEST — walk-forward gate, portfolio Sharpe, deflated ═══\n");

        var fsVariants = StrategyPipeline.LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short", d => d.ToGenotype(), d => (d.AtrLow, d.AtrHigh));
        var gridVariants = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_best", d => d.ToGenotype(), d => (d.AtrLow, d.AtrHigh));
        var gsVariants = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_short", d => d.ToGenotype(), d => (d.AtrLow, d.AtrHigh));
        var dlVariants = StrategyPipeline.LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long", d => d.ToGenotype(), d => (d.AtrLow, d.AtrHigh));
        var slVariants = StrategyPipeline.LoadVariants<SwingLongGenotypeDto, SwingLongGenotype>(
            "swing_long", d => d.ToGenotype(), d => (d.AtrLow, d.AtrHigh));
        var rsVariants = StrategyPipeline.LoadVariants<RipShortGenotypeDto, RipShortGenotype>(
            "rip_short", d => d.ToGenotype(), d => (d.AtrLow, d.AtrHigh));
        var flVariants = StrategyPipeline.LoadVariants<FadeLongGenotypeDto, FadeLongGenotype>(
            "fade_long", d => d.ToGenotype(), d => (d.AtrLow, d.AtrHigh));

        // Any strategy without a genotype is simply absent (FadeShort retired 2026-10-03); Grid is the floor.
        if (gridVariants.Length == 0)
        {
            Console.WriteLine("Missing Grid genotype — run 'gridtrain' first.");
            return;
        }

        // GRAVITY_EDGE_UNIVERSE=backtest: research switch to run on Config.BacktestCoins (default: OosCoins).
        var universe = Environment.GetEnvironmentVariable("GRAVITY_EDGE_UNIVERSE") == "backtest" ? Config.BacktestCoins : Config.OosCoins;
        var symbols = universe.Concat(new[] { "BTCUSDT", "ETHUSDT" }).Distinct().ToList();
        Console.WriteLine($"  Fetching {symbols.Count} symbols (never-trained OOS set + BTC/ETH anchors)…");
        var fetched = await StrategyPipeline.FetchFifteenMinAsync(client, symbols, Batches);

        var btc = fetched.FirstOrDefault(f => f.sym == "BTCUSDT");
        if (btc.h1 == null || btc.h1.Length < 500) { Console.WriteLine("  BTC history too short."); return; }
        var btcRegime = RegimeClassifier.ClassifySeriesWithDuration(btc.h1);
        Console.WriteLine($"  BTC regime series: {btcRegime.Length} h1 bars " +
                          $"({btcRegime[0].Time:yyyy-MM-dd} → {btcRegime[^1].Time:yyyy-MM-dd})\n");

        // Setup locations from the retired long strategies. Loaded from their *.RETRAINED_REJECTED_*
        // files on purpose: those genotypes were retrained from scratch under the corrected fitness
        // and still failed as DIRECTIONAL strategies, which is exactly why they are disabled. Using
        // them here asks a different question — is a place they liked a good place to harvest a
        // range, even though betting on the direction was not?
        var signalAnchors = new List<(string Label, Dictionary<string, Dictionary<int, double>> ByCoin)>();
        foreach (var (label, file) in new[]
                 {
                     ("DipLong",   "genotypes/dip_long_genotype.json.RETRAINED_REJECTED_2026-09-23"),
                     ("SwingLong", "genotypes/swing_long_genotype.json.RETRAINED_REJECTED_2026-09-23"),
                 })
        {
            if (!File.Exists(file)) { Console.WriteLine($"  [harvest] {label}: no genotype at {file} — skipped"); continue; }
            var byCoin = new Dictionary<string, Dictionary<int, double>>();
            foreach (var f in fetched)
            {
                if (f.sym is "BTCUSDT" or "ETHUSDT" || f.h1 == null || f.h1.Length < 300 || f.m15.Length < 400) continue;
                var setups = label == "DipLong"
                    ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(file))!.ToGenotype() is var dlg
                        ? DipLongSimulator.GetDipLongReturns(dlg, f.h1, f.m15).Select(t => (t.EntryTime, t.EntryPrice)).ToList()
                        : new List<(DateTime, double)>()
                    : JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(file))!.ToGenotype() is var slg
                        ? SwingLongSimulator.GetSwingLongReturns(slg, f.h1, f.m15).Select(t => (t.EntryTime, t.EntryPrice)).ToList()
                        : new List<(DateTime, double)>();

                // Map each setup's entry time onto the h1 bar index the grid loop walks.
                var idx = new Dictionary<long, int>(f.h1.Length);
                for (int i = 0; i < f.h1.Length; i++) idx.TryAdd(f.h1[i].Time.Ticks / TimeSpan.TicksPerHour, i);
                var byBar = new Dictionary<int, double>();
                foreach (var (t, px) in setups)
                    if (px > 0 && idx.TryGetValue(t.Ticks / TimeSpan.TicksPerHour, out int bar)) byBar[bar] = px;
                if (byBar.Count > 0) byCoin[f.sym] = byBar;
            }
            Console.WriteLine($"  [harvest] {label}: setups on {byCoin.Count} coins, {byCoin.Values.Sum(d => d.Count)} anchors");
            signalAnchors.Add((label, byCoin));

            // NULL CONTROL for the harvest claim. Same coins, same NUMBER of anchors per coin, but
            // placed at random bars. If a setup's location carried information, harvesting at its
            // anchors must beat harvesting at arbitrary ones. If the two match, the grid mechanics
            // were doing all the work and the signal only controlled HOW OFTEN the grid armed.
            var rng = new Random(20260923);
            var randomByCoin = new Dictionary<string, Dictionary<int, double>>();
            foreach (var (sym, byBar) in byCoin)
            {
                var entry = fetched.First(x => x.sym == sym);
                var shuffled = new Dictionary<int, double>();
                int n = byBar.Count, len = entry.h1.Length;
                while (shuffled.Count < n)
                {
                    int bar = rng.Next(250, len);              // past indicator warmup
                    // PREVIOUS bar's close: strictly known before bar `bar` opens. Anchoring on
                    // h1[bar].Close instead lets the grid fill at a price from earlier inside the
                    // same bar, chosen with knowledge of where that bar closed — same-bar lookahead.
                    shuffled[bar] = entry.h1[bar - 1].Close;
                }
                randomByCoin[sym] = shuffled;
            }
            signalAnchors.Add(($"{label}Rnd", randomByCoin));
        }

        // Research: GRAVITY_TRACE_RIPSHORT=<csv> records every RipShort exit's reason and levels.
        string? rsTracePath = Environment.GetEnvironmentVariable("GRAVITY_TRACE_RIPSHORT");
        if (rsTracePath is { Length: > 0 }) RipShortSimulator.Trace = new();

        // ── 1. Raw book: every strategy on every OOS coin, NO gating of any kind ──────────────
        var overlay = LoadOverlay();
        var raw = new List<Booked>();
        foreach (var f in fetched)
        {
            if (f.sym is "BTCUSDT" or "ETHUSDT") continue;
            if (f.h1 == null || f.h1.Length < 300 || f.m15.Length < 400) continue;

            void Add(string label, IEnumerable<(DateTime Time, double Return, DateTime EntryTime, double EntryPrice)> ts)
            {
                foreach (var t in ts)
                    raw.Add(new Booked(t.EntryTime, t.Time, t.Return, 1.0, label, f.sym,
                                       EntryPrice: t.EntryPrice));
            }

            if (StrategyPipeline.SelectVariant(fsVariants, f.m15) is { } fs)
                Add("FadeShort", FadeShortSimulator.GetFadeShortReturns(fs, f.h1, f.m15)
                    .Select(t => (t.Time, t.Return, t.EntryTime, t.EntryPrice)));
            // GRAVITY_GRID_STRUCT=1: the frozen structural-default rungs (StructGridSimulator) replace
            // both grid simulators under the SAME labels, so caps, direction and acceptance treat them
            // as Grid/GridShort. The genotypes still decide eligibility, exactly as for the GA grids.
            if (StrategyPipeline.SelectVariant(gridVariants, f.m15) is { } gr)
            {
                var stops = new List<bool>();
                int first = raw.Count;
                Add("Grid", (StructGrid ? StructGridSimulator.GetReturns(f.h1, isLong: true)
                                        : GridSimulator.GetGridSessionReturns(gr, f.h1, stopOut: stops))
                    .Select(t => (t.Time, t.Return, t.EntryTime, t.EntryPrice)));
                for (int k = 0; k < stops.Count; k++) raw[first + k] = raw[first + k] with { Stop = stops[k] };

            }
            if (StrategyPipeline.SelectVariant(gsVariants, f.m15) is { } gs)
                Add("GridShort", (StructGrid ? StructGridSimulator.GetReturns(f.h1, isLong: false)
                                             : GridShortSimulator.GetGridShortSessionReturns(gs, f.h1))
                    .Select(t => (t.Time, t.Return, t.EntryTime, t.EntryPrice)));
            if (overlay != null)
            {
                foreach (var (isLong, label, geno) in new[] { (true, "GridOverlay", StrategyPipeline.SelectVariant(gridVariants, f.m15)),
                                                              (false, "GridShortOverlay", StrategyPipeline.SelectVariant(gsVariants, f.m15)) })
                {
                    if (geno is null || !overlay.TryGetValue((f.sym, isLong ? 1 : -1), out var dec)) continue;
                    Add(label, StructGridSimulator.GetOverlayReturns(f.h1, isLong, t => dec.TryGetValue(t, out var e) ? e : null,
                                                                     hardStopAtr: geno.HardStopAtrMult)
                        .Select(t => (t.Time, t.Return, t.EntryTime, t.EntryPrice)));
                }
            }
            if (StrategyPipeline.SelectVariant(dlVariants, f.m15) is { } dl)
                Add("DipLong", DipLongSimulator.GetDipLongReturns(dl, f.h1, f.m15)
                    .Select(t => (t.Time, t.Return, t.EntryTime, t.EntryPrice)));
            if (StrategyPipeline.SelectVariant(slVariants, f.m15) is { } sl)
                Add("SwingLong", SwingLongSimulator.GetSwingLongReturns(sl, f.h1, f.m15)
                    .Select(t => (t.Time, t.Return, t.EntryTime, t.EntryPrice)));
            RipShortSimulator.TraceSymbol = f.sym;
            if (StrategyPipeline.SelectVariant(rsVariants, f.m15) is { } rs)
                Add("RipShort", RipShortSimulator.GetRipShortReturns(rs, f.h1, f.m15)
                    .Select(t => (t.Time, t.Return, t.EntryTime, t.EntryPrice)));
            if (StrategyPipeline.SelectVariant(flVariants, f.m15) is { } fl)
                Add("FadeLong", FadeLongSimulator.GetFadeLongReturns(fl, f.h1, f.m15)
                    .Select(t => (t.Time, t.Return, t.EntryTime, t.EntryPrice)));
        }

        if (SymbolCrowdingCap.ConfiguredStrength > 0)
        {
            var daily = fetched.Select(f => SymbolCovariance.ToDaily(f.sym, f.h1)).OfType<SymbolCovariance.DailySeries>().ToList();
            _crowding = SymbolCrowdingCap.Build(daily, SymbolCrowdingCap.ConfiguredStrength);
            Console.WriteLine(_crowding != null
                ? $"  CROWDING cap ON (GRAVITY_CROWDING={_crowding.Strength:F2}), {_crowding.Symbols} symbols — charges same-side open positions against the {MaxConcurrent}-slot budget\n"
                : "  CROWDING cap requested but BTC/ETH anchors missing — INACTIVE\n");
        }

        if (RipShortSimulator.Trace is { } rsTrace && rsTracePath is { Length: > 0 })
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            File.WriteAllLines(rsTracePath, new[] { "symbol,entry_time,exit_time,reason,entry,stop_px,target,exit_px,cost_stop,cost_other,size_mult" }
                .Concat(rsTrace.Select(t => string.Join(",", t.Symbol, t.EntryTime.ToString("yyyy-MM-dd HH:mm:ss"), t.ExitTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    t.Reason, t.Entry.ToString("R", inv), t.StopPx.ToString("R", inv), t.Target.ToString("R", inv), t.ExitPx.ToString("R", inv),
                    t.CostStop.ToString("R", inv), t.CostOther.ToString("R", inv), t.SizeMult.ToString("R", inv)))));
            Console.WriteLine($"  RipShort trace: {rsTrace.Count} exits → {rsTracePath}");
            RipShortSimulator.Trace = null;
        }

        _prices = fetched.Where(f => f.h1 is { Length: > 0 })
                         .ToDictionary(f => f.sym,
                                       f => (f.h1.Select(c => c.Time).ToArray(),
                                             f.h1.Select(c => c.Close).ToArray()));

        // GRAVITY_EDGE_FROM=yyyy-MM-dd: score only trades ENTERED on or after that date (the forward test,
        // docs/FORWARD_TEST_GRID_2026-10.md). Simulators still run over the full history so indicators and
        // sessions are warmed up exactly as in production; only the booked trades are filtered. The rolling
        // gates have no trailing trades to fit on, so read the RAW row.
        if (Environment.GetEnvironmentVariable("GRAVITY_EDGE_FROM") is { Length: > 0 } fromStr)
        {
            var from = DateTime.SpecifyKind(DateTime.Parse(fromStr, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
            int before = raw.Count;
            raw.RemoveAll(t => t.Entry < from);
            Console.WriteLine($"  FORWARD WINDOW: trades entered from {from:yyyy-MM-dd} only — {raw.Count} of {before} kept\n");
        }

        if (raw.Count == 0) { Console.WriteLine("  No trades generated."); return; }
        Console.WriteLine($"  Raw book: {raw.Count} trades, {raw.Select(t => t.Symbol).Distinct().Count()} coins, " +
                          $"{raw.Select(t => t.Strategy).Distinct().Count()} strategies\n");

        // Ungated trades from the live genotypes, in trade_log_edge.py's schema, so the
        // market-hedged view (scripts/trade_log_edge.py --hedge) runs on what edgetest measured.
        Directory.CreateDirectory("reports");
        File.WriteAllLines("reports/edgetest_raw_trades.csv",
            raw.Select(t => FormattableString.Invariant(
                    $"{t.Entry:yyyy-MM-dd HH:mm:ss},{t.Exit:yyyy-MM-dd HH:mm:ss},{t.Symbol},{t.Strategy},{t.Ret:F4},{t.EntryPrice},{(t.Stop ? 1 : 0)}"))
               .Prepend("entry_time,exit_time,symbol,strategy,return_pct,entry_price,stop"));

        // GRAVITY_EDGE_SLEEVES=<dir with carry.csv, trend.csv from research/export_sleeves.py>: the whole
        // book across sleeves, sized by SleeveSizer (research/cov_sizing.py's rule) instead of the shared
        // trade-slot replay. Grid sleeves come from THIS engine, marked to market on real prices.
        if (Environment.GetEnvironmentVariable("GRAVITY_EDGE_SLEEVES") is { Length: > 0 } sleeveDir)
        {
            RunSleeves(raw, sleeveDir);
            return;
        }

        // ── 2. The books ──────────────────────────────────────────────────────────────────────
        // Regime at entry, once, for every trade (binary search over the classified BTC series).
        var regimes = RegimeBarLookup.TagRegimes(btcRegime, raw.Select(t => t.Entry).ToList());

        for (int i = 0; i < raw.Count; i++) raw[i] = raw[i] with { Regime = regimes[i] };

        var regimeGate = RollingStrategyGate.ByRegime(
            raw.Select(t => (t.Strategy, t.Entry, t.Exit, t.Ret)).ToList(), btcRegime);

        // The honest form of oosbacktest's per-coin filter: same "does this strategy work on this
        // coin" question, asked of a trailing window instead of the coin's own in-sample slice.
        var coinScreen = RollingStrategyGate.BySymbol(
            raw.Select(t => (t.Strategy, t.Symbol, t.Exit, t.Ret)));

        var rolling = new List<Booked>();
        var coinOnly = new List<Booked>();
        var both = new List<Booked>();
        for (int i = 0; i < raw.Count; i++)
        {
            var t = raw[i];
            var r = regimeGate.At(t.Strategy, regimes[i], t.Entry);
            var c = coinScreen.At(t.Strategy, t.Symbol, t.Entry);
            if (r.Active) rolling.Add(t with { Size = r.Confidence });
            if (c.Active) coinOnly.Add(t with { Size = c.Confidence });
            // Both must agree, and the WEAKER evidence sizes it — the conservative combination.
            if (r.Active && c.Active) both.Add(t with { Size = Math.Min(r.Confidence, c.Confidence) });
        }

        // ── RISK PARITY ──────────────────────────────────────────────────────────────────────
        // Flat 5%-of-equity sizing is an implicit bet that equal notional means equal risk. It is
        // not: Grid and GridShort run at ~0.5% max drawdown and FadeShort at ~9.3%, a ~20x spread,
        // all sized identically. Inverse-volatility weights equalise RISK contribution instead.
        //
        // Fitted on the same trailing, already-closed window as the gate (bucket "all", so it is
        // per strategy across regimes). Sizing a strategy down because we observed its drawdown on
        // the window being sized would be the in-sample error the static family gate made.
        //
        // MEAN-NORMALISED across the strategies with a measurable vol at that moment, so this
        // REDISTRIBUTES risk and cannot inflate gross exposure — it can never manufacture return
        // through leverage, which matters because Simulator.GuardExposureCap refuses gross above
        // 1.0x for want of a liquidation model.
        var volGate = RollingStrategyGate.BySymbol(
            raw.Select(t => (t.Strategy, "all", t.Exit, t.Ret)));
        var strategyNames = raw.Select(t => t.Strategy).Distinct().OrderBy(x => x).ToList();
        var scaleCache = new Dictionary<(long Period, string Strategy), double>();

        double RiskScale(string strategy, DateTime at)
        {
            long period = (long)Math.Floor((at - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalDays / 30);
            if (scaleCache.TryGetValue((period, strategy), out double cached)) return cached;

            var inv = new Dictionary<string, double>();
            foreach (var n in strategyNames)
            {
                double v = volGate.At(n, "all", at).Vol;
                if (v > 1e-9) inv[n] = 1.0 / v;
            }
            double mean = inv.Count > 0 ? inv.Values.Average() : 0.0;
            foreach (var n in strategyNames)
                scaleCache[(period, n)] = mean > 1e-12 && inv.TryGetValue(n, out double iv) ? iv / mean : 1.0;

            return scaleCache.TryGetValue((period, strategy), out double r) ? r : 1.0;
        }

        var riskParity = rolling
            .Select(t => t with { Size = t.Size * RiskScale(t.Strategy, t.Entry) })
            .ToList();

        var staticGate = LoadStaticGate();
        List<Booked>? stat = null;
        if (staticGate != null)
        {
            stat = new List<Booked>();
            for (int i = 0; i < raw.Count; i++)
                if (StrategyFamilyGating.IsActive(staticGate, raw[i].Strategy, regimes[i]))
                    stat.Add(raw[i] with
                    {
                        Size = StrategyFamilyGating.FamilyConfidence(staticGate, raw[i].Strategy, regimes[i]),
                    });
        }

        // NULL CONTROL. A gate that admits the same FRACTION of trades at the same average size,
        // chosen at random. If the trained gate cannot beat this, its benefit is exposure
        // reduction — trading less — and not trade selection, and the machinery is unjustified.
        var nullRng = new Random(12345);
        double admitRate = rolling.Count / (double)raw.Count;
        double avgSize   = rolling.Count > 0 ? rolling.Average(t => t.Size) : 1.0;
        var randomGate = raw.Where(_ => nullRng.NextDouble() < admitRate)
                            .Select(t => t with { Size = avgSize }).ToList();

        var books = new List<(string Label, List<Booked> Book)> { ("RAW (no gate)", raw) };
        if (stat != null) books.Add(("STATIC gate (in-sample)", stat));
        books.Add(("ROLLING regime gate", rolling));
        books.Add(("ROLLING coin screen", coinOnly));
        books.Add(("ROLLING regime + coin", both));
        // Same admitted trades, but every position the same size. Any gap between this and the
        // conf-weighted book is the SIZING half of the gate; any gap between this and the random
        // control is the SELECTION half.
        books.Add(("  ↳ same trades, flat size", rolling.Select(t => t with { Size = avgSize }).ToList()));
        books.Add(("ROLLING gate + risk parity", riskParity));
        books.Add(($"RANDOM gate (null, {admitRate * 100:F0}%)", randomGate));

        Console.WriteLine("  PER-TRADE (what the other commands report) vs PORTFOLIO (what you actually earn)");
        Console.WriteLine("  Book                          PF   mean%       CAGR  annSharpe    maxDD   Calmar     DSR    days");
        Console.WriteLine("  ──────────────────────────────────────────────────────────────────────────────────────────────────");

        List<double>? rollingDaily = null;
        List<Booked> deflateBook = both.Count >= 30 ? both : rolling;
        foreach (var (label, book) in books)
        {
            var s = Evaluate(book);
            if (s == null) { Console.WriteLine($"  {label,-26}  — too few trades"); continue; }
            var v = s.Value;
            double gp = book.Where(t => t.Ret > 0).Sum(t => t.Ret);
            double gl = -book.Where(t => t.Ret <= 0).Sum(t => t.Ret);
            double pf = gl > 1e-9 ? gp / gl : 99.0;
            double dsrBook = StatisticalTests.DeflatedSharpeRatio(v.Daily, GaTrials, v.Daily.Count).Dsr;
            Console.WriteLine($"  {label,-26} {pf,5:F2} {book.Average(t => t.Ret),6:F3}%" +
                              $" {v.Cagr,9:F1}% {v.Sharpe,10:F2} {v.MaxDd,8:F1}% {v.Calmar,8:F2}" +
                              $" {dsrBook,7:F3} {v.Days,7}");
            if (label == "ROLLING regime + coin") rollingDaily = v.Daily;
        }
        Console.WriteLine();

        // ── 3. Deflation and overfitting, on the walk-forward book ────────────────────────────
        if (rollingDaily is { Count: >= 30 })
        {
            var (dsr, psr, eMax, srHat) = StatisticalTests.DeflatedSharpeRatio(
                rollingDaily, GaTrials, rollingDaily.Count);

            Console.WriteLine("  ── Deflated Sharpe (daily returns of ROLLING regime + coin) ──");
            Console.WriteLine($"     observed daily SR   {srHat,8:F4}   (annualised {srHat * Math.Sqrt(365),6:F2})");
            Console.WriteLine($"     E[max SR] of {GaTrials} trials {eMax,8:F4}   ← what pure search buys");
            Console.WriteLine($"     PSR vs zero         {psr,8:F4}");
            Console.WriteLine($"     DEFLATED SR         {dsr,8:F4}   {(dsr >= 0.95 ? "✓ survives the search"
                                                                      : dsr >= 0.50 ? "~ inconclusive"
                                                                      : "✗ indistinguishable from the best of random")}");
            Console.WriteLine();
        }

        // Per-strategy configs for PBO / White's RC: the trade returns each strategy contributed
        // to the walk-forward book.
        var configs = deflateBook.GroupBy(t => t.Strategy)
                             .Select(g => (Label: g.Key, Returns: g.Select(t => t.Ret).ToList()))
                             .Where(c => c.Returns.Count >= 20)
                             .ToList();
        if (configs.Count >= 2)
            StatisticalTests.PrintReport(configs, "walk-forward gated book", GaTrials);

        // ── 4. Does GA fitness rank strategies the way the portfolio does? ───────────────────
        // The GAs score PER-TRADE statistics (FoldScoreHelper.Canonical → PerTradeSharpe, PF,
        // gain/dd). A portfolio earns on an equity curve, where concurrency, overlap and
        // cross-coin correlation decide the outcome. If the two rankings disagree, the GA is
        // optimising something other than what the system earns.
        Console.WriteLine("\n  ── GA SELECTION DIAGNOSTIC: per-trade fitness vs portfolio outcome ──");
        Console.WriteLine("     strategy        GA PerTradeSharpe | portfolio annSharpe     CAGR    maxDD");
        var perStrat = new List<(string Name, double Ga, double Port, double Dd, double Cagr)>();
        foreach (var g in rolling.GroupBy(t => t.Strategy))
        {
            var rets = g.Select(t => t.Ret).ToList();
            var st   = Evaluate(g.ToList());
            if (st == null) continue;
            perStrat.Add((g.Key, FoldScoreHelper.PerTradeSharpe(rets), st.Value.Sharpe, st.Value.MaxDd, st.Value.Cagr));
        }
        foreach (var x in perStrat.OrderByDescending(x => x.Port))
            Console.WriteLine($"     {x.Name,-14} {x.Ga,16:F4} | {x.Port,18:F2} {x.Cagr,8:F1}% {x.Dd,7:F1}%");

        if (perStrat.Count >= 3)
        {
            int zeroed = perStrat.Count(x => x.Ga == 0.0);
            // A rank correlation computed over a majority of exact ties measures the tie, not the
            // agreement, so it is only quoted when most entries are actually distinguishable.
            if (zeroed * 2 < perStrat.Count)
            {
                double rho = Spearman(perStrat.Select(x => x.Ga).ToList(), perStrat.Select(x => x.Port).ToList());
                Console.WriteLine($"\n     Spearman (GA fitness vs portfolio Sharpe): {rho,+6:F2}");
                Console.WriteLine(rho > 0.7  ? "     → GA fitness tracks the portfolio."
                                : rho > 0.2  ? "     → weak agreement: GA fitness is a noisy proxy for what is earned."
                                             : "     → GA fitness does NOT rank strategies the way the portfolio does.");
            }
            else
            {
                Console.WriteLine($"\n     No rank correlation quoted: {zeroed}/{perStrat.Count} strategies are TIED at exactly 0.");
            }
            if (zeroed > 0)
                Console.WriteLine($"     {zeroed}/{perStrat.Count} score exactly 0 — those sit at or below breakeven (PF <= 1.0),\n" +
                                   "     where a zero Sharpe bonus is the correct answer, not a cliff. Before the ramp\n" +
                                   "     replaced PerTradeSharpe's hard PF<1.3 cut-off, 4 of 6 scored 0 and no rank\n" +
                                   "     correlation could be computed at all.");
        }

        // ── 5. Is the window simply adverse for some of these strategies? ────────────────────
        // A bear-regime strategy in a mostly-bull window earns a negative PF honestly: it had few
        // chances and bad ones. Splitting per regime separates "the formula is broken" from "the
        // window did not suit it". The regime mix of the window itself is quoted first, because a
        // per-regime PF on 30 bars of Bear says nothing.
        Console.WriteLine("\n  ── IS THE WINDOW ADVERSE? regime mix, and per-strategy PF within each ──");
        var mix = btcRegime.Where(b => b.Time >= raw.Min(t => t.Entry) && b.Time <= raw.Max(t => t.Exit))
                           .GroupBy(b => b.Regime)
                           .ToDictionary(g => g.Key, g => g.Count());
        int mixTotal = Math.Max(1, mix.Values.Sum());
        Console.WriteLine("     window regime mix (h1 bars): " + string.Join("  ",
            mix.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value * 100.0 / mixTotal:F0}%")));

        var regimeNames = Enum.GetValues<MarketRegime>().ToList();
        Console.WriteLine($"     {"strategy",-14} " + string.Join(" ", regimeNames.Select(r => $"{r,18}")));
        foreach (var g in rolling.GroupBy(t => t.Strategy).OrderBy(g => g.Key))
        {
            var byRegime = new List<string>();
            foreach (var reg in regimeNames)
            {
                var rr = g.Where(t => t.Regime == reg).Select(t => t.Ret).ToList();
                if (rr.Count < 20) { byRegime.Add($"{"n<20",18}"); continue; }
                double gp = rr.Where(x => x > 0).Sum(), gl = -rr.Where(x => x <= 0).Sum();
                byRegime.Add($"{(gl > 1e-9 ? gp / gl : 99.0),12:F2} n={rr.Count,-4}");
            }
            Console.WriteLine($"     {g.Key,-14} " + string.Join(" ", byRegime));
        }

        // GATE EFFECT: what the gate actually did per (strategy, regime), raw -> admitted. A gate
        // that admits nearly everything in a bucket it should be blocking is not gating that
        // bucket at all, and the per-regime PF table above cannot tell the two apart.
        Console.WriteLine("\n  ── GATE EFFECT: raw vs admitted, per strategy x regime ──");
        Console.WriteLine($"     {"strategy",-14} {"regime",-9} {"raw n",7} {"adm n",7} {"kept",6} {"rawPF",7} {"admPF",7}");
        static double Pf(IEnumerable<double> r)
        {
            double gp = r.Where(x => x > 0).Sum(), gl = -r.Where(x => x <= 0).Sum();
            return gl > 1e-9 ? gp / gl : (gp > 0 ? 99.0 : 0.0);
        }
        foreach (var sName in raw.Select(t => t.Strategy).Distinct().OrderBy(x => x))
            foreach (var reg in regimeNames)
            {
                var rawB = raw.Where(t => t.Strategy == sName && t.Regime == reg).Select(t => t.Ret).ToList();
                if (rawB.Count < 20) continue;
                var admB = rolling.Where(t => t.Strategy == sName && t.Regime == reg).Select(t => t.Ret).ToList();
                Console.WriteLine($"     {sName,-14} {reg,-9} {rawB.Count,7} {admB.Count,7} " +
                                  $"{admB.Count * 100.0 / rawB.Count,5:F0}% {Pf(rawB),7:F2} " +
                                  $"{(admB.Count >= 20 ? Pf(admB).ToString("F2") : "n<20"),7}");
            }

        // DOES THE GATE HAVE SKILL? The whole mechanism assumes trailing-window PF predicts the
        // NEXT period's PF in the same bucket. The table above says the gate barely moves PF in
        // any bucket, which would follow if that assumption is simply false. Test it directly:
        // pair each (bucket, period)'s trailing PF — exactly what the gate saw — against the PF
        // actually realised by trades ENTERED in that period, and correlate.
        Console.WriteLine("\n  ── DOES THE GATE PREDICT? trailing PF vs next-period realised PF ──");
        var pairs = new List<(double Trailing, double Forward, bool Admitted)>();
        foreach (var bucket in raw.GroupBy(t => (t.Strategy, t.Regime)))
        {
            var byPeriod = bucket.GroupBy(t => new DateTime(t.Entry.Year, t.Entry.Month, 1));
            foreach (var per in byPeriod)
            {
                var fwd = per.Select(t => t.Ret).ToList();
                if (fwd.Count < 20) continue;
                var v = regimeGate.At(bucket.Key.Strategy, bucket.Key.Regime, per.Key);
                if (v.Trades < 20) continue;             // gate had no opinion, not a prediction
                pairs.Add((v.Pf, Pf(fwd), v.Active));
            }
        }
        if (pairs.Count >= 20)
        {
            double rho = Spearman(pairs.Select(x => x.Trailing).ToList(),
                                  pairs.Select(x => x.Forward).ToList());
            var on  = pairs.Where(x => x.Admitted).Select(x => x.Forward).ToList();
            var off = pairs.Where(x => !x.Admitted).Select(x => x.Forward).ToList();
            Console.WriteLine($"     {pairs.Count} (bucket, month) predictions with >=20 trades each side");
            Console.WriteLine($"     Spearman(trailing PF, next-period PF): {rho,+6:F3}");
            if (on.Count > 0)  Console.WriteLine($"     gate said ON  ({on.Count,4}): mean-of-period PF {on.Average(),6:F3}");
            if (off.Count > 0) Console.WriteLine($"     gate said OFF ({off.Count,4}): mean-of-period PF {off.Average(),6:F3}");
            Console.WriteLine("     ^ mean OF RATIOS — biased up by periods with tiny gross losses, and not");
            Console.WriteLine("       what a portfolio earns. The trade-weighted figures below are.");

            // POOLED, TRADE-WEIGHTED. Every trade entered in a gate-ON period against every trade
            // entered in a gate-OFF period. This is the number the equity curve is made of.
            var onR = new List<double>(); var offR = new List<double>();
            foreach (var bucket in raw.GroupBy(t => (t.Strategy, t.Regime)))
                foreach (var per in bucket.GroupBy(t => new DateTime(t.Entry.Year, t.Entry.Month, 1)))
                {
                    var v = regimeGate.At(bucket.Key.Strategy, bucket.Key.Regime, per.Key);
                    if (v.Trades < 20 || per.Count() < 20) continue;
                    (v.Active ? onR : offR).AddRange(per.Select(t => t.Ret));
                }
            if (onR.Count > 0 && offR.Count > 0)
            {
                Console.WriteLine($"     POOLED  gate ON  ({onR.Count,6} trades): mean {onR.Average(),+7:F4}%  PF {Pf(onR),5:F3}");
                Console.WriteLine($"     POOLED  gate OFF ({offR.Count,6} trades): mean {offR.Average(),+7:F4}%  PF {Pf(offR),5:F3}");
                double lift = onR.Average() - offR.Average();
                // Welch t on the difference of means: is the lift bigger than the noise?
                double vOn = onR.Select(x => (x - onR.Average()) * (x - onR.Average())).Sum() / (onR.Count - 1);
                double vOff = offR.Select(x => (x - offR.Average()) * (x - offR.Average())).Sum() / (offR.Count - 1);
                double se = Math.Sqrt(vOn / onR.Count + vOff / offR.Count);
                double t = se > 1e-12 ? lift / se : 0.0;
                Console.WriteLine($"     GATE LIFT: {lift,+7:F4}% per trade   t = {t,5:F2}   " +
                                  (Math.Abs(t) >= 2.0 ? "← real selection skill" : "← within noise: the gate de-risks, it does not select"));
            }
        }

        Console.WriteLine("\n  ── LOSS DISTRIBUTION per strategy (raw book) ──");
        Console.WriteLine($"     {"strategy",-14} {"trades",7} {"loss%",7} {"meanLoss",9} {"p99 loss",9} {"worst",8} {"medHold",8}");
        foreach (var g in raw.GroupBy(t => t.Strategy).OrderBy(g => g.Key))
        {
            var rets = g.Select(t => t.Ret).ToList();
            var losses = rets.Where(x => x <= 0).OrderBy(x => x).ToList();
            var holds = g.Select(t => (t.Exit - t.Entry).TotalHours).OrderBy(x => x).ToList();
            if (losses.Count == 0) continue;
            Console.WriteLine($"     {g.Key,-14} {rets.Count,7} {losses.Count * 100.0 / rets.Count,6:F0}% " +
                              $"{losses.Average(),8:F2}% {losses[(int)(losses.Count * 0.01)],8:F2}% " +
                              $"{losses[0],7:F2}% {holds[holds.Count / 2],7:F0}h");
        }

        Console.WriteLine("\n  ── RISK PARITY multipliers (inverse trailing vol, mean-normalised) ──");
        Console.WriteLine($"     {"strategy",-14} {"median x",9} {"min",7} {"max",7}   (1.00 = unchanged)");
        // riskParity is built from rolling by Select, so index i corresponds to index i — no lookup.
        var multipliers = new Dictionary<string, List<double>>();
        for (int i = 0; i < rolling.Count; i++)
        {
            double b = rolling[i].Size;
            if (b <= 1e-12) continue;
            if (!multipliers.TryGetValue(rolling[i].Strategy, out var l))
                multipliers[rolling[i].Strategy] = l = new List<double>();
            l.Add(riskParity[i].Size / b);
        }
        foreach (var n in strategyNames)
        {
            if (!multipliers.TryGetValue(n, out var xs) || xs.Count == 0) continue;
            xs.Sort();
            Console.WriteLine($"     {n,-14} {xs[xs.Count / 2],9:F2} {xs[0],7:F2} {xs[^1],7:F2}");
        }

        // ── FILL-MODEL SENSITIVITY ───────────────────────────────────────────────────────────
        // A result that exists only under an optimistic fill convention is a result about the
        // simulator. Zhang et al. (2026, arXiv:2605.23959) measure exactly this by toggling one
        // execution convention at a time and report leakage gains of +5.41 to +21.65 Sharpe against
        // clean references of 0.44-0.68. Our own measurement of the same switch: 4.15 -> 0.67.
        //
        // Publish the whole row, never one cell. If the honest column is not positive, nothing else
        // on this page means anything.
        Console.WriteLine("\n  ── FILL-MODEL SENSITIVITY (Grid) ──");
        Console.WriteLine($"     {"fill model",-24} {"PF",6} {"mean%",9} {"perTradeSharpe",15} {"trades",8}");
        foreach (var (modelName, sameBar) in new[] { ("next-bar (honest)", false), ("same-bar (RETIRED)", true) })
        {
            var rets = new List<double>();
            foreach (var f2 in fetched)
            {
                if (f2.sym is "BTCUSDT" or "ETHUSDT" || f2.h1 is not { Length: > 300 }) continue;
                if (StrategyPipeline.SelectVariant(gridVariants, f2.m15) is not { } gg) continue;
                rets.AddRange(sameBar
                    ? GridSimulator.GetGridSessionReturnsSameBarFill(gg, f2.h1).Select(t => t.Return)
                    : GridSimulator.GetGridSessionReturns(gg, f2.h1).Select(t => t.Return));
            }
            if (rets.Count == 0) continue;
            double gp = rets.Where(x => x > 0).Sum(), gl = -rets.Where(x => x <= 0).Sum();
            double mean = rets.Average();
            double sd = Math.Sqrt(rets.Select(x => (x - mean) * (x - mean)).Average());
            Console.WriteLine($"     {modelName,-24} {(gl > 1e-9 ? gp / gl : 99.0),6:F2} {mean,8:F3}% " +
                              $"{(sd > 1e-12 ? mean / sd : 0),15:F4} {rets.Count,8}");
        }

        // ── ACCEPTANCE GATE ──────────────────────────────────────────────────────────────────
        // The defect this session actually cost the most was keeping strategies that had no
        // out-of-sample edge: DipLong, RipShort and SwingLong each DRAGGED the book, and removing
        // them moved the deflated Sharpe 0.003 -> 0.119 -> 0.790. That was found by hand, one
        // leave-one-out run at a time. This runs it for every strategy, every time.
        //
        // A strategy is REJECTED when the book is BETTER WITHOUT IT. That is the only test that
        // matters at the portfolio level, and it catches what per-strategy statistics cannot: a
        // strategy can look profitable alone and still cost the book through drawdown timing,
        // concurrency-slot consumption and correlation with what else is running.
        Console.WriteLine("\n  ══ ACCEPTANCE GATE — leave-one-out contribution to the gated book ══");
        var full = Evaluate(rolling);
        if (full is { } fullV)
        {
            double fullDsr = StatisticalTests.DeflatedSharpeRatio(fullV.Daily, GaTrials, fullV.Daily.Count).Dsr;
            Console.WriteLine($"     book with everything: Sharpe {fullV.Sharpe:F2}  DSR {fullDsr:F3}  CAGR {fullV.Cagr:F1}%");
            Console.WriteLine($"     {"strategy",-14} {"ΔSharpe",9} {"ΔDSR",8} {"ΔCAGR",8} | {"book WITHOUT it",28}   verdict");

            var rejected = new List<string>();
            foreach (var name in rolling.Select(t => t.Strategy).Distinct().OrderBy(x => x))
            {
                var without = rolling.Where(t => t.Strategy != name).ToList();
                var wo = Evaluate(without);
                if (wo is not { } woV) { Console.WriteLine($"     {name,-14}   (book too thin without it — cannot judge)"); continue; }

                double woDsr = StatisticalTests.DeflatedSharpeRatio(woV.Daily, GaTrials, woV.Daily.Count).Dsr;
                // Contribution = what the strategy ADDS. Negative means the book improves without it.
                double dSharpe = fullV.Sharpe - woV.Sharpe;
                double dDsr    = fullDsr - woDsr;
                double dCagr   = fullV.Cagr - woV.Cagr;
                // THREE-WAY, not binary. The first version rejected on any Sharpe reduction, which
                // flagged FadeShort as a defect when it actually ADDS 2.0% CAGR and costs risk-
                // adjusted return — a trade-off for a human to price, not a fault to delete. A gate
                // that cannot tell those apart gets ignored, which is how "VERDICT A edge=A" became
                // meaningless. Only a strategy that is worse on BOTH axes is strictly dominated.
                bool hurtsReturn = dCagr   < 0;
                bool hurtsRisk   = dSharpe < 0 || dDsr < 0;
                string verdict = hurtsReturn && hurtsRisk
                    ? "✗ REJECT — worse on BOTH return and risk; strictly dominated"
                    : hurtsRisk
                        ? "~ TRADE-OFF — adds return, costs risk-adjusted return; a human call"
                        : hurtsReturn
                            ? "~ TRADE-OFF — improves risk, costs return; a human call"
                            : "✓ accept — better on both";
                bool reject = hurtsReturn && hurtsRisk;
                if (reject) rejected.Add(name);
                Console.WriteLine($"     {name,-14} {dSharpe,+9:F2} {dDsr,+8:F3} {dCagr,+7:F1}% | " +
                                  $"CAGR {woV.Cagr,5:F1}%  Sh {woV.Sharpe,5:F2}  DD {woV.MaxDd,4:F1}%   {verdict}");

                // WHERE DOES IT HURT? If removing it lets the OTHER strategies into slots they were
                // being turned away from, the damage is crowding, not the strategy's own P&L.
                if (reject || hurtsRisk)
                {
                    fullV.Slots.TryGetValue(name, out var own);
                    Console.WriteLine($"        own slots: {own.Admitted} admitted, {own.Dropped} turned away by the cap");
                    foreach (var other in fullV.Slots.Keys.Where(k => k != name).OrderBy(k => k))
                    {
                        fullV.Slots.TryGetValue(other, out var a);
                        woV.Slots.TryGetValue(other, out var b);
                        Console.WriteLine($"        {other,-12} admitted {a.Admitted,6} -> {b.Admitted,6}" +
                                          $"  ({b.Admitted - a.Admitted,+6})   cap-dropped {a.Dropped,6} -> {b.Dropped,6}");
                    }
                }
            }

            if (rejected.Count > 0)
            {
                Console.WriteLine($"\n     {rejected.Count} strategy(s) STRICTLY DOMINATED: {string.Join(", ", rejected)}");
                Console.WriteLine("     Disable by renaming the genotype to *.json.DISABLED_<date> and re-run.");
                Environment.ExitCode = 1;   // so this can gate a commit or a CI step
            }
            else Console.WriteLine("\n     No strategy is strictly dominated. Any TRADE-OFF row above is a\n" +
                                   "     risk-appetite decision, not a defect — this gate does not price it for you.");
        }

        // ── 6. One combined verdict ──────────────────────────────────────────────────────────
        PrintScorecard(rolling, btcRegime, "ROLLING regime gate, flat sizing");
        PrintScorecard(riskParity, btcRegime, "ROLLING regime gate + RISK PARITY");

        Console.WriteLine("\n  WHAT THIS DOES AND DOES NOT SHOW");
        Console.WriteLine("    · Coins are never-trained, so strategy parameters are out-of-sample on price.");
        Console.WriteLine("    · The gate is out-of-sample in TIME: fitted only on already-closed trades.");
        Console.WriteLine("    · The gate is NOT out-of-sample in coin: it is fitted on these same coins'");
        Console.WriteLine("      realised trades, which is what a live system would also do.");
        Console.WriteLine("    · Costs are TradeCosts.FeeRoundTripPct + Config.SlippageBps, Bybit-calibrated.");
        Console.WriteLine("    · No liquidation model. Valid only while gross exposure stays under equity.");
        Console.WriteLine("    · MarkToMarket accrues an open position's P&L LINEARLY across its hold, so the");
        Console.WriteLine("      within-hold path is smoothed. That biases Sharpe UP for long-hold strategies");
        Console.WriteLine("      (the grid family most), and the effect grows with hold length. Compare Sharpe");
        Console.WriteLine("      across strategies of similar hold length only; CAGR and maxDD are unaffected.");
    }

    // A single combined verdict for the whole gated system. Deliberately NOT an average of
    // sub-grades: oosbacktest prints "VERDICT A edge=A robustness=A risk=A sample=A" on a book
    // whose deflated Sharpe is 0.00, because averaging lets three soft passes bury one fatal
    // failure. Here a deflation failure CAPS the grade, because "indistinguishable from the best
    // of a random search" is not something a good Calmar can compensate for.
    private static void PrintScorecard(List<Booked> book, RegimeBar[] btcRegime, string label)
    {
        var st = Evaluate(book);
        if (st == null) { Console.WriteLine("\n  ── RIGOR SCORECARD: too few trades to score ──"); return; }
        var v = st.Value;

        var (dsr, _, _, _) = StatisticalTests.DeflatedSharpeRatio(v.Daily, GaTrials, v.Daily.Count);
        var configs = book.GroupBy(t => t.Strategy)
                          .Select(g => (Label: g.Key, Returns: g.Select(t => t.Ret).ToList()))
                          .Where(c => c.Returns.Count >= 20).ToList();
        double pbo = configs.Count >= 2
            ? StatisticalTests.ProbabilityOfBacktestOverfitting(configs).Pbo : double.NaN;
        double wrc = configs.Count >= 1 ? StatisticalTests.WhitesRealityCheck(configs) : double.NaN;
        int effN = StatisticalTests.EffectiveSampleSize(book.Select(t => t.Entry).ToList());

        Console.WriteLine($"\n  ══ RIGOR SCORECARD — {label} ══");
        Console.WriteLine("     criterion                        value        bar    verdict");
        int pass = 0, total = 0;
        void Row(string name, double val, double bar, bool higherIsBetter, string fmt = "F2")
        {
            bool ok = double.IsNaN(val) ? false : higherIsBetter ? val >= bar : val <= bar;
            total++; if (ok) pass++;
            Console.WriteLine($"     {name,-28} {val.ToString(fmt),8} {(higherIsBetter ? ">=" : "<=")}{bar,7:F2}    {(ok ? "✓ pass" : "✗ FAIL")}");
        }
        Row("Deflated Sharpe",            dsr,        0.95, true,  "F3");
        Row("Prob. backtest overfitting", pbo,        0.20, false, "F3");
        Row("White's Reality Check p",    wrc,        0.05, false, "F3");
        Row("Portfolio ann. Sharpe",      v.Sharpe,   1.00, true);
        Row("Calmar",                     v.Calmar,   0.50, true);
        Row("Max drawdown %",             v.MaxDd,   25.00, false);
        Row("Effective indep. samples",   effN,     250.00, true,  "F0");

        string grade = dsr < 0.50 ? "F" : pass == total ? "A" : pass >= total - 1 ? "B"
                     : pass >= total - 2 ? "C" : "D";
        Console.WriteLine($"\n     OVERALL: {grade}   ({pass}/{total} criteria met, CAGR {v.Cagr:F1}%)");
        Console.WriteLine(dsr < 0.50
            ? "     Capped at F by deflation: the observed Sharpe is below what a search of this\n" +
              "     size is expected to produce from strategies with NO true edge. Every other\n" +
              "     criterion is conditional on that one, so they cannot lift the grade."
            : "     Binding constraint is the lowest-scoring row above.");
    }

    // Spearman rank correlation. Ties broken by order, which is fine at this sample size.
    private static double Spearman(List<double> a, List<double> b)
    {
        static double[] Ranks(List<double> v) =>
            v.Select(x => (double)v.Count(y => y < x)).ToArray();
        var ra = Ranks(a); var rb = Ranks(b);
        double ma = ra.Average(), mb = rb.Average();
        double num = 0, da = 0, db = 0;
        for (int i = 0; i < ra.Length; i++)
        {
            num += (ra[i] - ma) * (rb[i] - mb);
            da  += (ra[i] - ma) * (ra[i] - ma);
            db  += (rb[i] - mb) * (rb[i] - mb);
        }
        return da > 1e-12 && db > 1e-12 ? num / Math.Sqrt(da * db) : 0.0;
    }

    private static StrategyFamilyGating.Gate? LoadStaticGate()
    {
        if (!File.Exists(Config.FamilyGateGenoFile)) return null;
        return JsonSerializer.Deserialize<StrategyFamilyGatingDto>(
            File.ReadAllText(Config.FamilyGateGenoFile))!.ToGate();
    }

    private readonly record struct Stats(
        double Cagr, double Sharpe, double MaxDd, double Calmar, int Days, List<double> Daily,
        // Per strategy: trades that got a slot, and trades the concurrency cap turned away. A
        // strategy can hurt the book without ever losing money, by occupying slots a better one
        // would have used — which per-strategy statistics cannot show.
        Dictionary<string, (int Admitted, int Dropped)> Slots);

    // Concurrency-capped replay at 5% of running equity per position, then a daily marked-to-market
    // curve through MarkToMarket — which books each trade exactly once, at its close, and accrues
    // open positions linearly so a drawdown inside a hold is visible rather than hidden.
    // One grid strategy as a sleeve: fixed capital (no compounding inside the sleeve, so the daily
    // series is P&L per unit of sleeve capital), 5% per session, the per-strategy cap, and an exposure
    // cap on open notional (a session that does not fit is cut to the headroom). Marked to market daily.
    private static (Dictionary<DateTime, double> Pnl, Dictionary<DateTime, double> Gross) GridSleeve(
        List<Booked> raw, string strategy, double expCap)
    {
        var open = new List<(DateTime Exit, double Pos)>();
        var sized = new List<MarkToMarket.Position>();
        var gross = new Dictionary<DateTime, double>();
        foreach (var t in raw.Where(t => t.Strategy == strategy).OrderBy(t => t.Entry))
        {
            open.RemoveAll(o => o.Exit <= t.Entry);
            if (open.Count >= CapFor(t.Strategy)) continue;
            double pos = Math.Min(StartBalance * MaxPositionPct * Math.Clamp(t.Size, 0.0, 1.0),
                                  Math.Max(0.0, expCap * StartBalance - open.Sum(o => o.Pos)));
            if (pos <= 0) continue;
            open.Add((t.Exit, pos));
            sized.Add(new MarkToMarket.Position(t.Entry, t.Ret, t.Exit - t.Entry, pos, MarkPath(t)));
            for (var d = t.Entry.Date; d <= t.Exit.Date; d = d.AddDays(1))
                gross[d] = Math.Min(expCap, gross.GetValueOrDefault(d) + pos / StartBalance);   // upper bound per day
        }
        var pnl = new Dictionary<DateTime, double>();
        if (sized.Count == 0) return (pnl, gross);
        var c = MarkToMarket.Compute(sized, StartBalance, TimeSpan.FromDays(1)).Curve;
        for (int i = 1; i < c.Count; i++)
            pnl[c[i - 1].Time.Date] = pnl.GetValueOrDefault(c[i - 1].Time.Date) + (c[i].Equity - c[i - 1].Equity) / StartBalance;
        return (pnl, gross);
    }

    private static (Dictionary<DateTime, double> Pnl, Dictionary<DateTime, double> Gross) ReadSleeve(string path)
    {
        var pnl = new Dictionary<DateTime, double>();
        var gross = new Dictionary<DateTime, double>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var f = line.Split(',');
            var d = DateTime.SpecifyKind(DateTime.Parse(f[0], System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc).Date;
            pnl[d] = double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture);
            gross[d] = double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture);
        }
        return (pnl, gross);
    }

    private static void RunSleeves(List<Booked> raw, string dir)
    {
        Console.WriteLine("═══ SLEEVE BOOK — SleeveSizer across carry, Grid, GridShort, trend (gross notional ≤ 1) ═══\n");
        var ext = new[] { "carry", "trend" }.ToDictionary(n => n, n => ReadSleeve(Path.Combine(dir, n + ".csv")));
        var configs = new (string Name, SleeveSizer.Method M, bool Scale, double MaxW)[]
        {
            ("inverse vol, capital split (sum 1)",        SleeveSizer.Method.InverseVol,        false, double.PositiveInfinity),
            ("ERC, C# mean-diag shrink, scaled to gross", SleeveSizer.Method.ErcMeanDiagShrink, true,  double.PositiveInfinity),
            ("ERC, correlation shrink, scaled to gross",  SleeveSizer.Method.ErcCorrShrink,     true,  double.PositiveInfinity),
            ("ERC, correlation shrink, scaled, w ≤ 1",    SleeveSizer.Method.ErcCorrShrink,     true,  1.0),
            ("alpha Σ⁻¹μ (t-shrunk μ), scaled to gross",  SleeveSizer.Method.Alpha,             true,  double.PositiveInfinity),
            ("alpha Σ⁻¹μ (t-shrunk μ), scaled, w ≤ 1",    SleeveSizer.Method.Alpha,             true,  1.0),
        };
        foreach (double expCap in new[] { 0.60, 0.30 })
        {
            var sl = new Dictionary<string, (Dictionary<DateTime, double> Pnl, Dictionary<DateTime, double> Gross)>
            {
                ["carry"] = ext["carry"], ["grid"] = GridSleeve(raw, "Grid", expCap),
                ["gridshort"] = GridSleeve(raw, "GridShort", expCap), ["trend"] = ext["trend"],
            };
            var names = sl.Keys.ToArray();
            DateTime d0 = names.Max(n => sl[n].Pnl.Keys.Min()), d1 = names.Min(n => sl[n].Pnl.Keys.Max());
            var days = Enumerable.Range(0, (int)(d1 - d0).TotalDays + 1).Select(i => d0.AddDays(i)).ToArray();
            var pnl = names.Select(n => days.Select(d => sl[n].Pnl.GetValueOrDefault(d)).ToArray()).ToArray();
            var gross = names.Select(n => days.Select(d => sl[n].Gross.GetValueOrDefault(d)).ToArray()).ToArray();
            Console.WriteLine($"  grid exposure cap {expCap:P0} of sleeve capital; {d0:yyyy-MM-dd} → {d1:yyyy-MM-dd}");
            Console.WriteLine("    sleeves (MTM, per unit capital): " + string.Join("  ", names.Select((n, i) =>
                $"{n} Sharpe {DailySharpe(pnl[i]):F2} mean gross {gross[i].Average():F2}")));
            Console.WriteLine($"    {"book",-44} {"CAGR",7} {"vol",6} {"Sharpe",7} {"maxDD",7} {"DSR",6} {"gross μ/peak",13}  avg weights ({string.Join("/", names)})");
            foreach (var cfg in configs)
            {
                var r = SleeveSizer.Size(days, pnl, gross, cfg.M, cfg.Scale, maxWeight: cfg.MaxW);
                double eq = 1, peak = 1, mdd = 0;
                foreach (var x in r.Daily) { eq *= 1 + x; peak = Math.Max(peak, eq); mdd = Math.Max(mdd, 1 - eq / peak); }
                double yrs = days.Length / 365.0, cagr = Math.Pow(eq, 1 / yrs) - 1;
                double mu = r.Daily.Average(), sd = Math.Sqrt(r.Daily.Select(v => (v - mu) * (v - mu)).Average());
                double dsr = StatisticalTests.DeflatedSharpeRatio(r.Daily.ToList(), GaTrials, r.Daily.Length).Dsr;
                var aw = Enumerable.Range(0, names.Length).Select(i => r.Weights.Average(w => w[i]));
                Console.WriteLine(FormattableString.Invariant(
                    $"    {cfg.Name,-44} {cagr * 100,6:F1}% {sd * Math.Sqrt(365) * 100,5:F1}% {DailySharpe(r.Daily),7:F2} {-mdd * 100,6:F1}% {dsr,6:F3} {r.Gross.Average(),6:F2}/{r.Gross.Max(),4:F2}   {string.Join("/", aw.Select(v => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}"));
            }
            Console.WriteLine();
        }
        Console.WriteLine("  Carry and trend are read from research/export_sleeves.py (Python engine, authority cost constants);");
        Console.WriteLine("  Grid / GridShort are this engine's trades, marked daily on real prices. Seen data: information.");
    }

    private static double DailySharpe(double[] x)
    {
        double mu = x.Average(), sd = Math.Sqrt(x.Select(v => (v - mu) * (v - mu)).Average());
        return sd > 1e-12 ? mu / sd * Math.Sqrt(365.0) : 0.0;
    }

    private static Stats? Evaluate(List<Booked> book)
    {
        if (book.Count < 30) return null;

        var ordered = book.OrderBy(t => t.Entry).ToList();
        var sized   = new List<MarkToMarket.Position>();
        var open    = new List<(DateTime Exit, double Pnl, string Strategy, string Symbol)>();
        var openPerStrategy = new Dictionary<string, int>();
        double realized = StartBalance;
        var slots = new Dictionary<string, (int Admitted, int Dropped)>();
        void Tally(string k, bool admitted)
        {
            slots.TryGetValue(k, out var v);
            slots[k] = admitted ? (v.Admitted + 1, v.Dropped) : (v.Admitted, v.Dropped + 1);
        }

        foreach (var t in ordered)
        {
            for (int i = open.Count - 1; i >= 0; i--)
                if (open[i].Exit <= t.Entry)
                {
                    realized += open[i].Pnl;
                    openPerStrategy[open[i].Strategy] = openPerStrategy[open[i].Strategy] - 1;
                    open.RemoveAt(i);
                }

            // Per-strategy cap first, then the shared directional budget — same order as
            // PortfolioReplay.FilterByConcurrentCap followed by Config.MaxDirectionalConcurrent.
            openPerStrategy.TryGetValue(t.Strategy, out int mine);
            if (mine >= CapFor(t.Strategy) || open.Count >= MaxConcurrent)
            {
                Tally(t.Strategy, false); continue;
            }
            // Crowding: the same-side open positions, charged for their correlation (one-sided — it
            // can only reject what the headcount admitted). Unknown direction → charged against both.
            if (_crowding != null)
            {
                bool? lng = PortfolioReplay.IsLong(ProductionLabel(t.Strategy));
                var same = open.Where(o => lng is null || PortfolioReplay.IsLong(ProductionLabel(o.Strategy)) is not bool d || d == lng)
                               .Select(o => o.Symbol).ToList();
                if (_crowding.Exceeds(same, t.Symbol, MaxConcurrent, t.Entry)) { Tally(t.Strategy, false); continue; }
            }
            // Sized off REALISED equity, ignoring open P&L: a position is never sized on a gain
            // that has not been booked yet.
            double pos = Math.Max(0.0, realized * MaxPositionPct * Math.Clamp(t.Size, 0.0, 1.0));
            if (pos <= 0) continue;   // before the slot is claimed — a zero-size trade takes none

            Tally(t.Strategy, true);
            openPerStrategy[t.Strategy] = mine + 1;
            sized.Add(new MarkToMarket.Position(t.Entry, t.Ret, t.Exit - t.Entry, pos, MarkPath(t)));
            open.Add((t.Exit, t.Ret / 100.0 * pos, t.Strategy, t.Symbol));
        }
        if (sized.Count < 30) return null;

        var mtm = MarkToMarket.Compute(sized, StartBalance, TimeSpan.FromDays(1));
        if (mtm.Curve.Count < 30) return null;

        var daily = new List<double>(mtm.Curve.Count);
        for (int i = 1; i < mtm.Curve.Count; i++)
        {
            double prev = mtm.Curve[i - 1].Equity;
            if (prev > 1e-9) daily.Add((mtm.Curve[i].Equity - prev) / prev);
        }
        if (daily.Count < 30) return null;

        double mean = daily.Average();
        double std  = Math.Sqrt(daily.Select(r => (r - mean) * (r - mean)).Average());
        double sharpe = std > 1e-12 ? mean / std * Math.Sqrt(365.0) : 0.0;

        double years = (mtm.Curve[^1].Time - mtm.Curve[0].Time).TotalDays / 365.25;
        double end   = mtm.Curve[^1].Equity;
        double cagr  = years > 0.1 && end > 0
            ? (Math.Pow(end / StartBalance, 1.0 / years) - 1.0) * 100.0 : 0.0;
        double calmar = mtm.MaxDrawdownPct > 1e-9 ? cagr / mtm.MaxDrawdownPct : 0.0;

        return new Stats(cagr, sharpe, mtm.MaxDrawdownPct, calmar, mtm.Curve.Count, daily, slots);
    }
}
