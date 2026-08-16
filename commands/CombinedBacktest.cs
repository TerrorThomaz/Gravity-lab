using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

static class CombinedBacktest
{
    // ── Multiple-testing family construction ──────────────────────────────────
    // A strategy needs trades on at least this many coins before any of the coin-level
    // statistics (DSR / PBO / WRC) or the Monte Carlo bootstrap mean anything. Shared by
    // the StatisticalTests.PrintReport calls and the Holm family so the two lists are
    // built from the same rule rather than two hand-copied literals that drifted apart.
    internal const int MinFamilyCoins = 2;

    // MonteCarloTest.Run hard-returns p = 1.0 below this many observations — it cannot form
    // a null distribution. Including such a strategy at p = 1.0 would inflate m (weakening
    // every other strategy's threshold) while contributing a verdict that is structurally
    // incapable of being a rejection, so it is excluded and the exclusion is reported.
    internal const int MinFamilyTrades = 10;

    /// <summary>
    /// Builds the Monte Carlo p-value vector for the Holm-Bonferroni family, plus the list of
    /// strategies that could not be tested and why.
    ///
    /// Each strategy is seeded from its OWN name (MonteCarloTest.SeedForStrategy). The previous
    /// version threaded a single Random(42) through the six sequential Run calls, so a strategy's
    /// p-value depended on how many draws the strategies ahead of it had consumed: skip one
    /// (fewer than two coins, fewer than ten trades, a genotype that failed to load) and every
    /// subsequent p-value shifted, even though none of those trade sets had changed. Per-name
    /// seeding makes each p-value a pure function of that strategy's own returns, which is what
    /// "reproducible for a given trade set" has to mean when family membership is variable.
    ///
    /// Extracted from the reporting flow purely so this property is directly testable.
    /// </summary>
    internal static (List<(string Name, double PValue)> Family,
                     List<(string Name, string Reason)> Excluded)
        BuildMonteCarloFamily(
            IReadOnlyList<(string Name, List<(string Label, List<double> Returns)> PerCoin, int GaTrials)> inputs)
    {
        var family   = new List<(string Name, double PValue)>();
        var excluded = new List<(string Name, string Reason)>();

        foreach (var (name, perCoin, _) in inputs)
        {
            if (perCoin.Count < MinFamilyCoins)
            {
                excluded.Add((name, $"traded on {perCoin.Count} coin(s); needs ≥ {MinFamilyCoins} (not reported above either)"));
                continue;
            }
            var rets = perCoin.SelectMany(c => c.Returns).ToList();
            if (rets.Count < MinFamilyTrades)
            {
                excluded.Add((name, $"{rets.Count} trades; the bootstrap needs ≥ {MinFamilyTrades} to form a null"));
                continue;
            }
            // BLOCK bootstrap, not iid. MonteCarloTest.Run resamples individual trades
            // independently, which assumes an independence these trades do not have: entries are
            // regime-gated so they arrive in bursts, and an iid null understates the spread and
            // returns p-values that are too optimistic. RunBlockBootstrap was written for exactly
            // this and had ZERO callers until now — the ninth-and-tenth instance of a mechanism
            // built and never connected.
            //
            // Block length 24 ~ one day of h1 bars, i.e. the timescale trades actually cluster on.
            // blockSize=1 degenerates to Run(), so the pair is directly comparable.
            var mcIid   = MonteCarloTest.Run(
                rets,
                permutations: MonteCarloTest.FamilyWiseResamples,
                rng: new Random(MonteCarloTest.SeedForStrategy(name)));
            var mcBlock = MonteCarloTest.RunBlockBootstrap(
                rets, blockSize: 24,
                permutations: MonteCarloTest.FamilyWiseResamples,
                rng: new Random(MonteCarloTest.SeedForStrategy(name)));
            Console.WriteLine($"  {name,-12} bootstrap p: iid {mcIid.PValue:F4}  |  block(24) {mcBlock.PValue:F4}" +
                              (mcBlock.PValue > mcIid.PValue ? "   ← dependence widens it" : ""));
            // The FAMILY carries the block p-value: it is the honest one when trades cluster.
            family.Add((name, mcBlock.PValue));
        }
        return (family, excluded);
    }

    // Variant loading/selection, the label→StrategyKind map and ProdRipCap live in
    // StrategyPipeline (the reproducer-of-record); this command calls them directly.


    // ── GATED-EXPOSURE BASELINE ("no-signal" strategy) ────────────────────────────────
    // Motivated directly by the random-entry control: random entries inside the router gate
    // BEAT every strategy's real entry signal on return per trade, while losing on profit
    // factor because they carry no stop. Conclusion: the entries were not adding return, the
    // EXITS were adding risk shaping. This tests the obvious consequence — keep the gate and
    // the exit ladder, throw the entry signal away entirely.
    //
    // Enter at the next bar's open whenever flat and the gate is on; exit on hard stop, take
    // profit, trailing stop or max hold. Direction comes from the gate alone (DipLong's gate =>
    // long, RipShort's gate => short). There is no RSI, no divergence, no BoS, no ADX.
    //
    // The whole grid is printed, never a best cell. Picking the winner after seeing results is
    // how a 4-parameter baseline becomes as overfit as the 14-gene strategies it is auditing.
    static void GatedExposureBaseline(
        List<(string Sym, Candle[] H1Val)> series, RegimeRouterSession session)
    {
        Console.WriteLine($"\n── Gated-exposure baseline: router gate + ATR exits, NO entry signal ──");
        Console.WriteLine($"  {"SL",5} {"TP",5} {"Trail",6} {"MaxH",5}  {"Dir",5} {"PF",6} {"WR",6} {"Avg%",7} {"N",6}");
        Console.WriteLine($"  {new string('-', 62)}");

        foreach (var (sl, tp, tr, mh) in new[] {
            (1.0, 4.0, 2.0, 48), (1.5, 5.0, 2.5, 60), (2.0, 6.0, 3.0, 72), (1.5, 8.0, 2.0, 96) })
        foreach (var (kind, isLong, label) in new[] {
            (RegimeRouterGA.StrategyKind.DipLong,  true,  "long"),
            (RegimeRouterGA.StrategyKind.RipShort, false, "short") })
        {
            var rets = new List<double>(4096);
            foreach (var (sym, h1) in series)
            {
                if (h1.Length < 260) continue;
                var closes = h1.Select(c => c.Close).ToArray();
                var highs  = h1.Select(c => c.High).ToArray();
                var lows   = h1.Select(c => c.Low).ToArray();
                var atr    = Volatility.Atr(highs, lows, closes, 14);

                int i = 220;
                while (i < h1.Length - 2)
                {
                    if (!session.IsActive(kind, h1[i].Time) || atr[i] <= 1e-9) { i++; continue; }
                    double entry = h1[i + 1].Open, a = atr[i];
                    double stop  = isLong ? entry - sl * a : entry + sl * a;
                    double targ  = isLong ? entry + tp * a : entry - tp * a;
                    double peak  = entry;
                    double ret   = double.NaN;
                    int j = i + 1;
                    for (; j < Math.Min(h1.Length, i + 1 + mh); j++)
                    {
                        double hi = h1[j].High, lo = h1[j].Low;
                        if (isLong  && lo <= stop) { ret = (stop - entry) / entry * 100.0; break; }
                        if (!isLong && hi >= stop) { ret = (entry - stop) / entry * 100.0; break; }
                        if (isLong  && hi >= targ) { ret = (targ - entry) / entry * 100.0; break; }
                        if (!isLong && lo <= targ) { ret = (entry - targ) / entry * 100.0; break; }
                        // Trailing stop, armed once price has moved tr x ATR in favour.
                        if (isLong)  { if (hi > peak) peak = hi;
                                       if (peak - entry >= tr * a) stop = Math.Max(stop, peak - tr * a); }
                        else         { if (lo < peak || peak == entry) peak = lo;
                                       if (entry - peak >= tr * a) stop = Math.Min(stop, peak + tr * a); }
                    }
                    if (double.IsNaN(ret))
                    {
                        int k = Math.Min(j, h1.Length - 1);
                        ret = isLong ? (closes[k] - entry) / entry * 100.0
                                     : (entry - closes[k]) / entry * 100.0;
                    }
                    rets.Add(ret - TradeCosts.FeeRoundTripPct);
                    i = Math.Min(j + 1, h1.Length - 1);   // flat again only after the exit
                }
            }
            if (rets.Count < 30) continue;
            Console.WriteLine($"  {sl,5:F1} {tp,5:F1} {tr,6:F1} {mh,5}  {label,5} " +
                              $"{Simulator.ProfitFactor(rets),6:F2} " +
                              $"{(double)rets.Count(r => r > 0) / rets.Count,6:P0} " +
                              $"{rets.Average(),+6:F2}% {rets.Count,6}");
        }
        Console.WriteLine("  Same router gate and same fee as the live strategies. No entry signal at all.");
    }

    public static async Task RunCombinedBacktest(BybitRestClient client)
    {
        Console.WriteLine($"=== Gravity-gen2 | COMBINED BACKTEST (all strategies, router-gated, {Config.BacktestCoins.Length} coins, {DataSplit.ValLabel}) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine($"Missing FadeShort genotype — run 'train' first.");     return; }
        if (!File.Exists(Config.GridGenoFile))      { Console.WriteLine($"Missing grid genotype — run 'gridtrain' first."); return; }

        // Load variant arrays (currently one entry each; infrastructure ready for multi-variant)
        var fsVariants = StrategyPipeline.LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var gridVariants = StrategyPipeline.LoadVariants<GridGenotypeDto, GridGenotype>(
            "grid_best", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var flVariants = StrategyPipeline.LoadVariants<FadeLongGenotypeDto, FadeLongGenotype>(
            "fade_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var dlVariants = StrategyPipeline.LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var slVariants = StrategyPipeline.LoadVariants<SwingLongGenotypeDto, SwingLongGenotype>(
            "swing_long", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var rsVariants = StrategyPipeline.LoadVariants<RipShortGenotypeDto, RipShortGenotype>(
            "rip_short", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

        // Load high-vol variants (ATR ratio > 1.5)
        var fsHvVariants = StrategyPipeline.LoadVariants<FadeShortGenotypeDto, FadeShortGenotype>(
            "fade_short_highvol", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var dlHvVariants = StrategyPipeline.LoadVariants<DipLongGenotypeDto, DipLongGenotype>(
            "dip_long_highvol", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var slHvVariants = StrategyPipeline.LoadVariants<SwingLongGenotypeDto, SwingLongGenotype>(
            "swing_long_highvol", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));
        var rsHvVariants = StrategyPipeline.LoadVariants<RipShortGenotypeDto, RipShortGenotype>(
            "rip_short_highvol", dto => dto.ToGenotype(), dto => (dto.AtrLow, dto.AtrHigh));

        // Representative single genotypes (for portfolio hold-time calcs and logging)
        var swingG = fsVariants.Length > 0 ? fsVariants[0].Genotype! : null;
        var gridG  = gridVariants.Length > 0 ? gridVariants[0].Genotype! : null;
        if (swingG == null) { Console.WriteLine("Missing FadeShort genotype — run 'train' first."); return; }
        if (gridG  == null) { Console.WriteLine("Missing grid genotype — run 'gridtrain' first.");  return; }

        // FadeLong was hardcoded off on the strength of a COMMENT ("PF=0.06 OOS, net drag"), not a
        // live measurement — and that figure predates the monotone fold aggregator, the inverted
        // funding sign on longs, trade-level slippage and the concurrency-accounting fix. On the
        // OOS path, which never had this line, has always run it. Whether it is a drag is a question
        // for the report this command prints, not for this comment. GRAVITY_NOFADELONG=1 disables it.
        FadeLongGenotype?     flG     = Environment.GetEnvironmentVariable("GRAVITY_NOFADELONG") == "1"
                                        ? null
                                        : (flVariants.Length > 0 ? flVariants[0].Genotype : null);
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
        if (flG     != null) Console.WriteLine($"FadeLong:  {flG}  [{flVariants.Length} variant(s)]");
        else                 Console.WriteLine("FadeLong:  disabled (GRAVITY_NOFADELONG=1)");
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
        var fetched = await StrategyPipeline.FetchFifteenMinAsync(client, Config.BacktestCoins, batches: 113);
        Console.WriteLine($"  Done.\n");

        // ── Funding rate sessions, ONE PER SYMBOL ────────────────────────────────────────
        // Until now this command constructed no FundingRateSession at all (`grep -c -i funding
        // commands/CombinedBacktest.cs` returned 0), so every simulator ran the `funding == null`
        // branch and priced funding at the flat interest-rate floor. Real rates are a real cost
        // this backtest has never paid, and they are anti-correlated with what the long
        // strategies trade: funding prints positive >90% of the time and hardest in bull markets,
        // which is exactly when DipLong/SwingLong/AccumGrid are active and holding. EXPECT THE
        // NUMBERS BELOW TO GET WORSE. That is the correction, not a regression.
        Console.WriteLine("  Fetching per-symbol funding rate history...");
        var funding = await StrategyPipeline.FetchFundingAsync(client, fetched);
        Console.WriteLine();

        // ── DynamicGuard session ─────────────────────────────────────────────────────────
        var btcH1ForGuard = fetched.FirstOrDefault(f => f.sym == "BTCUSDT").h1;
        var guardCtx      = GuardedPortfolio.TryLoad(btcH1ForGuard);
        if (guardCtx != null) Console.WriteLine($"  Guard: {guardCtx.Genotype}\n");

        var router = StrategyPipeline.BuildRouterSession(routerG, fetched, btcH1ForGuard, withBtcBars: true);
        RegimeRouterSession? session  = router.Session;
        RegimeBar[]?         btcRegimeSeries = router.BtcRegimeSeries;

        var swingTrades       = new List<(DateTime Time, double Return, double Conf)>();
        var gridTrades        = new List<(DateTime Time, double Return, double Conf)>();
        var flTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var dlTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var slTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var rsTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var agTrades          = new List<(DateTime Time, double Return, double Conf)>();
        var agAcqDiscount     = new List<double>();   // per-coin % below VWAP (negative = paid above)
        var agAcqDiscountEma  = new List<double>();   // causal: vs trailing EMA at fill time
        var rsWaitRet         = new List<double>();   // RipShort under ExitOverrideMode.WaitForBreakeven
        var rsCapRet          = new List<double>();   // ... plus an absolute 6% loss cap
        var rsLockRet         = new List<double>();   // ... plus a minimum-profit ratchet
        var rsAtrCapRet       = new List<double>();   // ATR-scaled cap, absolutely ceilinged
        var rsHybRet          = new List<double>();   // ATR-scaled cap + ATR-scaled ratchet
        // Does MaxHold need to exist at all, once an ATR-scaled cap and the trail are in place?
        // Anatomy says ~every trade closes on time, so this is the dominant exit being tested.
        var rsMaxHoldSweep    = new Dictionary<int, List<double>>();
        var maxHoldProbe      = new[] { 53, 160, 100_000 };   // production · new ceiling · effectively off
        // Profit-ratchet grid, printed in full. Tuning these to a maximum after seeing the table
        // is how a 2-parameter safety feature becomes as overfit as the strategies it protects.
        var rsRatchetGrid     = new Dictionary<(double Trig, double Lock), List<double>>();
        // Trade anatomy: what actually separates the big wins from the big losses. Only possible
        // now that EntryTime/EntryPrice come out of the simulators — before, `Time` was the exit
        // bar and hold duration and entry conditions were simply unavailable.
        var rsAnatomy         = new List<(double Ret, double AtrPctEntry, double HoldH, double GuardMult, double BtcAtrRatio)>();
        var ratchetTriggers   = new[] { 2.0, 4.0, 6.0 };
        var ratchetLocks      = new[] { 0.5, 1.5, 3.0 };
        var agAcqFills        = new List<int>();
        // Entry/Symbol carried so the concurrency cap can model the window a position was ACTUALLY
        // open. It previously received `t.Time` (the EXIT bar) as EntryTime plus a hardcoded
        // HoldDuration, so occupancy was modelled as [exit, exit + const] — a window entirely AFTER
        // the trade had closed. The caps are the portfolio's concentration control; they were
        // filtering on the wrong interval.
        var allTrades         = new List<(DateTime Time, double Return, double Conf, string Strategy, DateTime Entry, string Sym)>();
        // Minimum-profit floor, applied to EVERY strategy. ON by default; GRAVITY_RATCHET=0 turns
        // it off, GRAVITY_RATCHET=<lockAtrMult> overrides the lock distance.
        //
        // The lock is ATR-scaled by design: entering at high ATR carries more risk but also has
        // more profit worth protecting once breakeven is cleared, so a 4%-ATR coin locks 4x what a
        // 1%-ATR coin does. Percentage values act as ceilings so a violent coin cannot push the arm
        // point out indefinitely.
        //
        // It defaulted OFF because an earlier A/B had it losing to the baseline. That test is stale
        // — it predates the router gate on FadeShort, the widened FadeShort box and the current
        // genotypes. Re-run under those, it wins on return AND drawdown simultaneously, on both the
        // 5%-cap and Kelly sizings. Any claim about which is better belongs in a report generated
        // from a run, not in this comment: flip the toggle and compare.
        var globalRatchet = ExitRatchet.FromEnvironment();
        double lockMult = globalRatchet.LockAtrMult;
        // GRAVITY_MAXLEGS: extra DipLong legs, only added once EVERY open leg is locked in
        // profit. Requires the ratchet — without it no leg ever arms, so nothing is ever added.
        int maxLegs = int.TryParse(Environment.GetEnvironmentVariable("GRAVITY_MAXLEGS"), out var ml) && ml > 0 ? ml : 1;

        // GRAVITY_CROWD=1 — funding-rate crowding gate. Funding is the one microstructure signal
        // already in the data we fetch: a positive rate means longs are PAYING shorts, i.e. the
        // long side is crowded, and vice versa. Entering with the crowd is entering into the
        // positions most likely to be squeezed. Thresholds are calibrated constants, not genes
        // (+0.08%/8h long, -0.05%/8h short), so this adds no overfit surface.
        //
        // Already implemented and used in FullTest ONLY — absent from combinedbacktest,
        // oosbacktest and papertrade. Wiring it here so it can be measured on the main path.
        bool crowdGate = Environment.GetEnvironmentVariable("GRAVITY_CROWD") == "1";
        if (crowdGate) Console.WriteLine("  [CROWD] funding-crowding gate active (skip entries with the crowd)");
        if (maxLegs > 1 && !globalRatchet.Enabled)
            Console.WriteLine("  [MAXLEGS] ignored — legs are only added after a leg LOCKS, which needs GRAVITY_RATCHET");
        else if (maxLegs > 1)
            Console.WriteLine($"  [MAXLEGS] DipLong may hold up to {maxLegs} legs (added only when all open legs are locked)");
        if (globalRatchet.Enabled)
            Console.WriteLine($"  [RATCHET] floor active on all strategies (arm 1.5xATR cap 8%, lock {lockMult}xATR cap 3%)");

        // ONE context for the whole command. Every simulator call takes ctx.With(funding.For(sym)),
        // so this command cannot run a different mechanism set from oosbacktest or papertrade by
        // accident -- which it previously did (oosbacktest had no ratchet support at all).
        var ctx = new ExecContext(Ratchet: globalRatchet, MaxLegs: maxLegs, CrowdingGate: crowdGate);
        bool gridUngated = Environment.GetEnvironmentVariable("GRAVITY_GRIDUNGATED") == "1";
        if (gridUngated) Console.WriteLine("  [GRID] router gate bypassed for Grid");
        // Val-window price series per coin, collected once for the random-entry control below.
        var controlSeries     = new List<(string Sym, Candle[] H1Val)>();
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

            int h1Split  = DataSplit.Split(h1).Train.Length;
            int m15Split = h1Split * 4;
            var h1Train  = h1[..h1Split];
            var h1Val    = h1[h1Split..];
            var m15Train = m15[..m15Split];
            var m15Val   = m15[m15Split..];

            var screenH1  = h1Train.Length >= 4380 ? h1Train : h1;
            var screenM15 = screenH1.Length == h1.Length ? m15 : m15Train;
            var (coinFsG, fsVarLabel) = StrategyPipeline.SelectVariantLabeled(fsVariants, m15);
            coinFsG ??= swingG;
            var tRet  = FadeShortSimulator.GetFadeShortReturns(coinFsG, screenH1, screenM15, funding.For(sym)).Select(t => t.Return).ToList();
            double tExp  = tRet.Count >= 20 ? tRet.Average() : double.NegativeInfinity;
            double tSort = tRet.Count >= 20 ? Simulator.SortinoRatio(tRet, screenH1.Length * 12) : double.NegativeInfinity;
            double tPF   = tRet.Count >= 20 ? Simulator.ProfitFactor(tRet) : 0;
            if (tExp <= 0 || tSort < 0.3 || tPF < 1.2)
            {
                Console.WriteLine($"  {sym,-16}  skip (exp={tExp:+0.00;-0.00}% sort={tSort:F2} pf={tPF:F2})");
                continue;
            }

            double conf   = Simulator.ComputeConfidence(tRet);
            var    vSwing = FadeShortSimulator.GetFadeShortReturns(coinFsG, h1Val, m15Val, ctx.With(funding.For(sym)));
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
            controlSeries.Add((sym, h1Val.ToArray()));
            // FadeShort is router-gated like every other strategy. It previously was NOT: this loop
            // pushed every trade into both allTrades and allTradesNoRouter unconditionally, so the
            // router-impact table read "510 -> 510, no trades removed" while every other strategy
            // showed heavy filtering. The gate existed (StrategyKind.FadeShort, mapped at the top of
            // this file) and nothing on this path ever called it.
            //
            // It matters because FadeShort's edge is regime-split, not weak: on the embargoed
            // held-out it is strongly profitable in Bear and strongly LOSS-making in Bull, and the
            // blended near-1.0 PF is those two cancelling. Running it ungated means taking the Bull
            // side deliberately.
            foreach (var (t, ret, _, et, _) in vSwing)
            {
                allTradesNoRouter.Add((t, ret, conf, "swing"));
                if (session != null && !session.IsActive(RegimeRouterGA.StrategyKind.FadeShort, t)) continue;
                swingTrades.Add((t, ret, conf));
                allTrades.Add((t, ret, conf, "swing", et, sym));
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

            int split   = DataSplit.Split(h1).Train.Length;
            var h1Train = h1[..split];
            var h1Val   = h1[split..];

            var (coinGridG, gridVarLabel) = StrategyPipeline.SelectVariantLabeled(gridVariants, m15Grid);
            coinGridG ??= gridG;
            // Grid books funding too. Historically it did NOT — GridSimulator carried no funding
            // term at all, so the grid family held positions up to MaxHoldCandles (77 h1 bars in
            // the trained genotype) free of carry while the other six strategies paid. Any
            // Grid-vs-other comparison in older output is biased in Grid's favour by that amount.
            var tRet  = GridSimulator.GetGridReturns(coinGridG, h1Train, funding.For(sym)).Select(t => t.Return).ToList();
            double tExp  = tRet.Count >= 20 ? tRet.Average()               : double.NegativeInfinity;
            double tPF   = tRet.Count >= 20 ? Simulator.ProfitFactor(tRet)  : 0;
            double tSort = tRet.Count >= 20 ? Simulator.SortinoRatio(tRet, h1Train.Length)  : double.NegativeInfinity;
            if (tExp <= 0 || tSort < 0.3 || tPF < 1.2)
            {
                Console.WriteLine($"  {sym,-16}  skip (exp={tExp:+0.00;-0.00}% pf={tPF:F2} sort={tSort:F2})");
                continue;
            }

            double conf  = Simulator.ComputeConfidence(tRet);
            var    vGrid = GridSimulator.GetGridReturns(coinGridG, h1Val, funding.For(sym));
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
            foreach (var (t, ret, _, et, _) in vGrid)
            {
                allTradesNoRouter.Add((t, ret, conf, "grid"));
                // GRAVITY_GRIDUNGATED=1 — the router removes 84% of Grid's trades, and the Ranging
                // gate admits ~508 windows averaging 5.5 bars. A grid ladder cannot fill and unwind
                // in five hours, so what survives may be fragments that pay fees and never complete.
                if (!gridUngated && session != null && !session.IsActive(RegimeRouterGA.StrategyKind.Grid, t)) continue;
                gridTrades.Add((t, ret, conf));
                allTrades.Add((t, ret, conf, "grid", et, sym));
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

                int h1Split  = DataSplit.Split(h1).Train.Length;
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                flTotalVCC += vCC;

                var (coinFlG, flVarLabel) = StrategyPipeline.SelectVariantLabeled(flVariants, m15);
                coinFlG ??= flG;
                var raw    = FadeLongSimulator.GetFadeLongReturns(coinFlG, h1Val, m15Val, ctx.With(funding.For(sym)));
                var gated  = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.FadeLong, t.Time)).ToList()
                    : raw;
                if (crowdGate && funding.For(sym) is FundingRateSession fsG_)
                    gated = gated.Where(t => !fsG_.IsCrowdedLong(t.Time)).ToList();
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
                    allTrades.Add((t.Time, t.Return, conf, "fadelong", t.EntryTime, sym));
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

                int h1Split  = DataSplit.Split(h1).Train.Length;
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                dlTotalVCC += vCC;

                var (coinDlG, dlVarLabel) = StrategyPipeline.SelectVariantLabeled(dlVariants, m15);
                coinDlG ??= dlG;
                var raw   = DipLongSimulator.GetDipLongReturns(coinDlG, h1Val, m15Val, ctx.With(funding.For(sym)));
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                if (crowdGate && funding.For(sym) is FundingRateSession fsG_)
                    gated = gated.Where(t => !fsG_.IsCrowdedLong(t.Time)).ToList();
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
                    allTrades.Add((t.Time, t.Return, conf, "diplong", t.EntryTime, sym));
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

                int h1Split  = DataSplit.Split(h1).Train.Length;
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                slTotalVCC += vCC;

                var (coinSlG, slVarLabel) = StrategyPipeline.SelectVariantLabeled(slVariants, m15);
                coinSlG ??= slG;
                var raw   = SwingLongSimulator.GetSwingLongReturns(coinSlG, h1Val, m15Val, ctx.With(funding.For(sym)));
                // NOTE: SwingLong is gated on StrategyKind.DipLong, not SwingLong. Both are
                // bull-regime longs so it is defensible, but it means the router's SwingLong flag
                // is never consulted here — flagged rather than changed.
                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.DipLong, t.Time)).ToList()
                    : raw;
                if (crowdGate && funding.For(sym) is FundingRateSession fsSl_)
                    gated = gated.Where(t => !fsSl_.IsCrowdedLong(t.Time)).ToList();
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
                    allTrades.Add((t.Time, t.Return, conf, "swing_long", t.EntryTime, sym));
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

                int h1Split  = DataSplit.Split(h1).Train.Length;
                int m15Split = h1Split * 4;
                var h1Val    = h1[h1Split..];
                var m15Val   = m15[Math.Min(m15Split, m15.Length)..];
                if (h1Val.Length < 100 || m15Val.Length < 400) continue;

                int    vCC = h1Val.Length * 12;
                rsTotalVCC += vCC;

                var (coinRsG, rsVarLabel) = StrategyPipeline.SelectVariantLabeled(rsVariants, m15);
                coinRsG ??= rsG;
                var raw    = RipShortSimulator.GetRipShortReturns(coinRsG, h1Val, m15Val, funding.For(sym), StrategyPipeline.ProdRipCap(),
                                 ExitRatchet.ForStrategy("ripshort"));
                // WaitForBreakeven: suppress the MaxHoldCandles time exit while the position is
                // underwater AND local vol is calm, capped at MaxExtraHoldCandles. Size stays 1x,
                // so per-trade accounting stays honest — unlike DcaAndWait, which the simulator
                // itself blocks for broken exposure accounting. Dormant since it was written
                // (null at every production call site); this is the first time it is measured.
                var rawWait = RipShortSimulator.GetRipShortReturns(coinRsG, h1Val, m15Val, funding.For(sym),
                    new RipShortSimulator.ExitOverrideConfig(RipShortSimulator.ExitOverrideMode.WaitForBreakeven));
                foreach (var t in (session != null
                        ? rawWait.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time))
                        : rawWait))
                    rsWaitRet.Add(t.Return);
                // Absolute loss cap: the only mechanism that can reach the tail, because the tail
                // trades never arm the trailing stop (arming needs a favourable excursion).
                // Ratchet: 6% loss cap + arm at 2% profit, lock 0.5% minimum profit.
                var rawLock = RipShortSimulator.GetRipShortReturns(coinRsG, h1Val, m15Val, funding.For(sym),
                    new RipShortSimulator.ExitOverrideConfig(RipShortSimulator.ExitOverrideMode.WaitForBreakeven,
                        MaxLossPct: 6.0, LockTriggerPct: 2.0, LockProfitPct: 0.5));
                foreach (var t in (session != null
                        ? rawLock.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time))
                        : rawLock))
                    rsLockRet.Add(t.Return);
                // ATR-aware cap: 2.5x the coin's own ATR, floored at 3% and ceilinged at 10%.
                // Scales with volatility so it sits outside the noise, but a short's unbounded
                // downside still meets an absolute line.
                var rawAtrCap = RipShortSimulator.GetRipShortReturns(coinRsG, h1Val, m15Val, funding.For(sym),
                    new RipShortSimulator.ExitOverrideConfig(RipShortSimulator.ExitOverrideMode.WaitForBreakeven,
                        MaxLossPct: 10.0, MaxLossAtrMult: 2.5, MaxLossPctFloor: 3.0));
                foreach (var t in (session != null
                        ? rawAtrCap.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time))
                        : rawAtrCap))
                    rsAtrCapRet.Add(t.Return);
                {
                    var cl = h1Val.Select(c => c.Close).ToArray();
                    var hi = h1Val.Select(c => c.High).ToArray();
                    var lo = h1Val.Select(c => c.Low).ToArray();
                    var atrArr = Volatility.Atr(hi, lo, cl, 14);
                    var tms = h1Val.Select(c => c.Time).ToArray();
                    foreach (var t in (session != null
                            ? raw.Where(x => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, x.Time))
                            : raw))
                    {
                        int ix = Array.BinarySearch(tms, t.EntryTime);
                        if (ix < 0) ix = ~ix;
                        ix = Math.Clamp(ix, 0, atrArr.Length - 1);
                        if (t.EntryPrice <= 1e-9 || atrArr[ix] <= 1e-9) continue;
                        // What were the risk mechanisms doing at THIS entry? The guard cuts size as
                        // BTC 4H ATR expands and the rotator rotates to safety on the same signal —
                        // so if the big winners entered at LOW guard mult, both are systematically
                        // down-sizing the trades that pay best.
                        double gm  = guardCtx?.Session.GetMult(t.EntryTime, "ripshort") ?? 1.0;
                        double bar = guardCtx?.Session.GetAtrRatio(t.EntryTime) ?? 1.0;
                        rsAnatomy.Add((t.Return, atrArr[ix] / t.EntryPrice * 100.0,
                                       (t.Time - t.EntryTime).TotalHours, gm, bar));
                    }
                }

                foreach (var trig in ratchetTriggers)
                foreach (var lockPct in ratchetLocks)
                {
                    if (lockPct >= trig) continue;   // a floor at/above its own trigger is nonsense
                    var rg = RipShortSimulator.GetRipShortReturns(coinRsG, h1Val, m15Val, funding.For(sym),
                        new RipShortSimulator.ExitOverrideConfig(RipShortSimulator.ExitOverrideMode.WaitForBreakeven,
                            MaxLossPct: 10.0, MaxLossAtrMult: 2.5, MaxLossPctFloor: 3.0,
                            LockTriggerPct: trig, LockProfitPct: lockPct));
                    if (!rsRatchetGrid.TryGetValue((trig, lockPct), out var lst))
                        rsRatchetGrid[(trig, lockPct)] = lst = new List<double>();
                    foreach (var t in (session != null
                            ? rg.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time))
                            : rg))
                        lst.Add(t.Return);
                }
                // Two ratchet settings per MaxHold. The question being tested: with a profit lock
                // armed, a trade held very long should exit AT OR ABOVE the lock by construction —
                // so MaxHold ought to be redundant. It is redundant only for trades that ARM; a
                // trade that never reaches the trigger can drift indefinitely, and that residual
                // population is the only thing a time exit is still catching.
                foreach (var mh in maxHoldProbe)
                foreach (var withRatchet in new[] { false, true })
                {
                    var gMh = coinRsG.ClampToBounds();
                    gMh.MaxHoldCandles = mh;      // deliberately past the clamp: 100k = no time exit
                    var cfgMh = withRatchet
                        ? new RipShortSimulator.ExitOverrideConfig(RipShortSimulator.ExitOverrideMode.None,
                              MaxLossPct: 10.0, MaxLossAtrMult: 2.5, MaxLossPctFloor: 3.0,
                              LockTriggerPct: 8.0, LockProfitPct: 3.0,
                              LockTriggerAtrMult: 1.5, LockProfitAtrMult: 0.5)
                        : new RipShortSimulator.ExitOverrideConfig(RipShortSimulator.ExitOverrideMode.None,
                              MaxLossPct: 10.0, MaxLossAtrMult: 2.5, MaxLossPctFloor: 3.0);
                    var rmh = RipShortSimulator.GetRipShortReturns(gMh, h1Val, m15Val, funding.For(sym), cfgMh);
                    int key = mh * (withRatchet ? -1 : 1);
                    if (!rsMaxHoldSweep.TryGetValue(key, out var lst))
                        rsMaxHoldSweep[key] = lst = new List<double>();
                    foreach (var t in (session != null
                            ? rmh.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time))
                            : rmh))
                        lst.Add(t.Return);
                }

                // Hybrid: ATR-scaled cap AND ATR-scaled ratchet. Arms at 1.5x the coin's own ATR
                // (ceiling 8%), locks 0.5x ATR (ceiling 3%) — so it arms late on volatile coins
                // instead of on their noise, which is what the flat-percentage grid punished.
                var rawHyb = RipShortSimulator.GetRipShortReturns(coinRsG, h1Val, m15Val, funding.For(sym),
                    new RipShortSimulator.ExitOverrideConfig(RipShortSimulator.ExitOverrideMode.WaitForBreakeven,
                        MaxLossPct: 10.0, MaxLossAtrMult: 2.5, MaxLossPctFloor: 3.0,
                        LockTriggerPct: 8.0, LockProfitPct: 3.0,
                        LockTriggerAtrMult: 1.5, LockProfitAtrMult: 0.5));
                foreach (var t in (session != null
                        ? rawHyb.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time))
                        : rawHyb))
                    rsHybRet.Add(t.Return);
                var rawCap = RipShortSimulator.GetRipShortReturns(coinRsG, h1Val, m15Val, funding.For(sym),
                    new RipShortSimulator.ExitOverrideConfig(RipShortSimulator.ExitOverrideMode.WaitForBreakeven,
                                                             MaxLossPct: 6.0));
                foreach (var t in (session != null
                        ? rawCap.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time))
                        : rawCap))
                    rsCapRet.Add(t.Return);
                var gated  = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.RipShort, t.Time)).ToList()
                    : raw;
                if (crowdGate && funding.For(sym) is FundingRateSession fsG_)
                    gated = gated.Where(t => !fsG_.IsCrowdedShort(t.Time)).ToList();
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
                    allTrades.Add((t.Time, t.Return, conf, "ripshort", t.EntryTime, sym));
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

                int h1Split = DataSplit.Split(h1).Train.Length;
                var h1Val = h1[h1Split..];
                if (h1Val.Length < 100) continue;

                int vCC = h1Val.Length;
                agTotalVCC += vCC;

                var bullTrades = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator.GetAccumulationReturns(agBullG, h1Val, MarketRegime.Bull, funding.For(sym));
                var bearTrades = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator.GetAccumulationReturns(agBearG, h1Val, MarketRegime.Bear, funding.For(sym));
                var raw = bullTrades.Concat(bearTrades).OrderBy(t => t.Time).ToList();

                var gated = session != null
                    ? raw.Where(t => session.IsActive(RegimeRouterGA.StrategyKind.AccumulationGrid, t.Time)).ToList()
                    : raw;
                var vRet = gated.Select(t => t.Return).ToList();

                // ── Acquisition quality: what an accumulator is actually FOR ──────────────
                // Profit factor scores a trading edge; an accumulator's job is to acquire
                // inventory below the market's own average over the same window. Reported as
                // discount vs the period VWAP — negative means it paid ABOVE the average and
                // is accumulating badly no matter what its PF says.
                if (gated.Count > 0)
                {
                    var pxL = gated.Select(t => t.EntryPrice).ToList(); // EntryTime, not Time: an accumulator's execution quality is its FILL priced against
                    // the market around that fill. Time is the EXIT bar, and a profitable grid exits
                    // ABOVE its entries, so benchmarking against the exit window flatters every fill
                    // by roughly the trade's own profit — measuring P&L, not execution.
                    var tmL = gated.Select(t => t.EntryTime).ToList();
                    double d  = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator
                        .AcquisitionDiscountPct(h1Val, pxL, tmL);
                    double de = GravityGen2.Strategies.AccumulationGrid.AccumulationGridSimulator
                        .AcquisitionDiscountEmaPct(h1Val, pxL, tmL);
                    if (!double.IsNaN(de)) agAcqDiscountEma.Add(de);
                    if (!double.IsNaN(d)) { agAcqDiscount.Add(d); agAcqFills.Add(gated.Count); }
                }

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
        Console.WriteLine($"  SWING SUMMARY  ({DataSplit.ValLabel}, {swingCoinStats.Count} coins, {swingRet.Count} trades)");
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
        Console.WriteLine($"  GRID SUMMARY  ({DataSplit.ValLabel}, {gridCoinStats.Count} coins, {gridRet.Count} trades)");
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
            Console.WriteLine($"  FADELONG SUMMARY  ({DataSplit.ValLabel}, {flCoinStats.Count} coins, {flRet.Count} trades, router-gated)");
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
            Console.WriteLine($"  DIPLONG SUMMARY  ({DataSplit.ValLabel}, {dlCoinStats.Count} coins, {dlRet.Count} trades, router-gated)");
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
            Console.WriteLine($"  SWINGLONG SUMMARY  ({DataSplit.ValLabel}, {slCoinStats.Count} coins, {slRet.Count} trades, router-gated)");
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
            Console.WriteLine($"  RIPSHORT SUMMARY  ({DataSplit.ValLabel}, {rsCoinStats.Count} coins, {rsRet.Count} trades, router-gated)");
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
            Console.WriteLine($"  ACCUMGRID SUMMARY  ({DataSplit.ValLabel}, {agCoinStats.Count} coins, {agRet.Count} trades, router-gated)");
            Console.WriteLine($"  NOTE: Disabled from combined portfolio — capital accumulator, not profit generator");
            Console.WriteLine($"{new string('═', 70)}");
            int aw = agRet.Count(r => r > 0);
            Console.WriteLine($"  Win rate:     {(double)aw / agRet.Count:P1}  ({aw}W / {agRet.Count - aw}L)");
            Console.WriteLine($"  Avg return:   {agRet.Average():+0.00}%");
            Console.WriteLine($"  Sharpe:       {Simulator.SharpeRatio(agRet, agTotalVCC):F2}");
            Console.WriteLine($"  Sortino:      {Simulator.SortinoRatio(agRet, agTotalVCC):F2}");
            Console.WriteLine($"  Profit factor:{Simulator.ProfitFactor(agRet):F2}");
            if (agAcqDiscount.Count > 0)
            {
                double mean = agAcqDiscount.Average();
                int better  = agAcqDiscount.Count(d => d > 0);
                Console.WriteLine($"  ── Acquisition quality (the accumulator's real objective) ──");
                Console.WriteLine($"  Avg entry vs period VWAP: {mean:+0.00;-0.00}%  " +
                                  $"({(mean > 0 ? "below VWAP — accumulating well" : "ABOVE VWAP — paying up")})");
                Console.WriteLine($"  Coins acquiring below VWAP: {better}/{agAcqDiscount.Count}  " +
                                  $"({(double)better / agAcqDiscount.Count:P0})  ·  {agAcqFills.Sum()} fills");
                if (agAcqDiscountEma.Count > 0)
                    Console.WriteLine($"  vs trailing EMA (CAUSAL — no future bars): {agAcqDiscountEma.Average():+0.00;-0.00}%  " +
                                      $"({agAcqDiscountEma.Count(d => d > 0)}/{agAcqDiscountEma.Count} coins)");
            }
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
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
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
                Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
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
            // Real entry time and ACTUAL hold, not the exit bar and a constant. Falls back to the
            // old constant only when a trade carries no entry (Entry == default), so the change is
            // visible rather than silent.
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
            var capFiltered = PortfolioReplay.FilterByConcurrentCap(capInput, directionalCap: Config.MaxDirectionalConcurrent,
                                                                    perSymbolCap: Config.MaxPerSymbolConcurrent);
            int skipped = allTrades.Count - capFiltered.Count;
            if (skipped > 0)
                Console.WriteLine($"  Concurrent cap removed {skipped} trades");
            // Restore the EXIT bar as Time — downstream portfolio sims key off it — while keeping
            // entry and symbol attached.
            allTrades = capFiltered.Select(t => (t.EntryTime + t.HoldDuration, t.Return, t.Conf, t.Strategy, t.EntryTime, t.Symbol)).ToList();

            // Central grading of the final book. Same code path as oosbacktest and fulltest, so a
            // difference between their headline numbers is now a difference in the TRADES, not in
            // three private implementations of profit factor.
            StrategyEvaluation.Report("VALIDATION BOOK", capFiltered, btcRegimeSeries,
                                      csvPath: "reports/combined_val_trades.csv");
        }

        // Strategy label RETAINED. It used to be projected away here, which silently forced the
        // 4-tuple overload of SimulatePortfolioExposureCapped — the one that cannot see which
        // strategy a trade belongs to. That made every per-strategy portfolio mechanism
        // (DdGatedLongs, ProtectableLongs, covariance weights) unreachable on the main path while
        // looking wired at the call site.
        var allTradesForExposure = allTrades
            .Select(t => (t.Time, t.Return, t.Conf, StrategyHold(t.Strategy, swingG, gridG, flG, dlG, slG, rsG, agBullG), t.Strategy))
            .ToList();

        // Same trades, strategy label retained, for the guarded/unguarded comparison further down.
        var allTradesGuardInput = allTrades
            .Select(t => new GuardedPortfolio.Trade(
                t.Time, t.Return, t.Conf,
                StrategyHold(t.Strategy, swingG, gridG, flG, dlG, slG, rsG, agBullG), t.Strategy))
            .ToList();

        // GRAVITY_DYNCAP=1 sizes the book from a risk BUDGET instead of a constant: the cap is
        // solved from "a correlated shock may cost at most N% of equity", with the planned shock
        // scaled by BTC's current ATR ratio. Tightens into stress, releases in calm — a constant
        // does neither, and is loosest exactly when the tail is fattest.
        Func<DateTime, double>? dynCap = null;
        if (Environment.GetEnvironmentVariable("GRAVITY_DYNCAP") == "1" && guardCtx?.Session != null)
        {
            var dec = DynamicExposureCap.FromGuard(guardCtx.Session);
            dynCap = dec.CapAt;
            Console.WriteLine($"\n  [DYNCAP] risk-budgeted exposure cap active "
                            + $"(calm {dec.CapForRatio(0.7):P0} · normal {dec.CapForRatio(1.0):P0} "
                            + $"· elevated {dec.CapForRatio(1.3):P0} · stressed {dec.CapForRatio(2.0):P0})");
        }

        // GRAVITY_COVSIZE=1 — inverse-variance weights with a correlation haircut. Sizing today is
        // per-trade Kelly plus COUNT-based caps, none of which know that two positions may be the
        // same bet. CorrelatedShock already showed correlation is the dominant risk here, so the
        // flat cap is a crude proxy for a covariance constraint.
        Func<string, double>? strategySizeWeight = null;   // one sizing hook: cov-size OR vol-target OR risk-parity OR VaR/utility, never both
        // Sizing philosophy selector. Mutually exclusive; GRAVITY_SIZING wins when set, else cov-size
        // (the default). Each reads the same `allTrades` and feeds the strategyWeight hook, so a run is
        // never a silent blend of two different sizing methods. Available: voltarget | riskparity |
        // var | es | crra | covsize.
        string sizingRule = Environment.GetEnvironmentVariable("GRAVITY_SIZING")?.ToLowerInvariant()
                            ?? (Environment.GetEnvironmentVariable("GRAVITY_VOLTARGET") == "1" ? "voltarget" : "covsize");

        // ── Covariance matrix once, reused by risk parity and VaR/utility below. ──
        // Daily-aligned grids from CovarianceSizing (already the reproducible common grid).
        double[]? jointCov = null;
        string[]? stratNames = null;
        if (sizingRule is "riskparity" or "blend" or "covsize")
        {
            var grids = allTrades.GroupBy(t => t.Strategy).ToDictionary(
                g => g.Key, g => CovarianceSizing.ToGrid(g.Select(t => (t.Time, t.Return)).ToList(),
                                                         allTrades.Min(t => t.Time), allTrades.Max(t => t.Time), TimeSpan.FromDays(1)));
            stratNames = grids.Keys.OrderBy(x => x).ToArray();
            var series = stratNames.Select(n => grids[n]).ToArray();
            var sample = CovarianceMatrix.Sample(series);
            jointCov = CovarianceMatrix.Shrink(sample, stratNames.Length, lambda: 0.3);
        }

        switch (sizingRule)
        {
            case "voltarget":
                strategySizeWeight = SizingMethods.VolTargetStrategyWeights(
                    allTrades.Select(t => (t.Strategy, t.Return)).ToList(),
                    targetVolPct: 1.5, volFloorPct: 0.5, volCapPct: 8.0, meanNormalise: false);
                if (strategySizeWeight != null)
                    Console.WriteLine("  [SIZING=voltarget] volatility-targeted sizing active (target 1.5% per-strategy vol)");
                break;

            case "riskparity":
                if (jointCov != null && stratNames is { Length: > 0 })
                {
                    var rp = RiskParity.EqualRiskContribution(jointCov, stratNames.Length, normalizeToMeanOne: true);
                    var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    for (int a = 0; a < stratNames.Length; a++) map[stratNames[a]] = rp.Weights[a];
                    strategySizeWeight = s => map.TryGetValue(s, out var w) ? w : 1.0;
                    Console.WriteLine($"  [SIZING=riskparity] equal-risk-contribution sizing (portfolio vol {rp.PortfolioVol:F3}, {CovarianceMatrix.EffectiveBets(jointCov, stratNames.Length):F1} effective bets)");
                }
                break;

            case "var":
            case "es":
            case "crra":
                strategySizeWeight = VaRSizing.BuildWeights(allTrades.Select(t => (t.Strategy, t.Return)).ToList(), sizingRule, gamma: 4.0, budgetPct: 2.0, refFrac: 0.05);
                if (strategySizeWeight != null)
                    Console.WriteLine($"  [SIZING={sizingRule}] tail-risk/utility sizing active (budget {2.0:F0}pp equity tail, gamma={4.0})");
                break;

            case "covsize":
            default:
                if (CovarianceSizing.Enabled && allTrades.Count > 0)
                {
                    DateTime t0 = allTrades.Min(t => t.Time), t1 = allTrades.Max(t => t.Time);
                    var grids = allTrades.GroupBy(t => t.Strategy).ToDictionary(
                        g => g.Key,
                        g => CovarianceSizing.ToGrid(g.Select(t => (t.Time, t.Return)).ToList(), t0, t1, TimeSpan.FromDays(1)));
                    var raws = allTrades.GroupBy(t => t.Strategy)
                                        .ToDictionary(g => g.Key, g => g.Select(t => t.Return).ToArray());
                    var w = CovarianceSizing.Compute(grids, raws);
                    CovarianceSizing.Print(w);
                    strategySizeWeight = w.For;
                }
                break;

            // BLEND: geometric mix of a return-shape weight and a risk-cap weight, swept over alpha.
            // Solves the ES/vol-halves-DD-but-loses-return problem by keeping the return shape where
            // the edge is while capping the tails that add drawdown. alpha → 1 = pure return-shape,
            // alpha → 0 = pure risk-cap. We pick the alpha maximising return-per-unit-drawdown and
            // report the whole curve so the tradeoff is visible.
            case "blend":
            {
                // Return-shape: risk-parity (covariance-aware) is the best pure return source in the
                // earlier comparison; fall back to vol-target if covariance is unavailable.
                Func<string, double> retShape;
                string shapeName;
                if (jointCov is { } cov && stratNames is { Length: > 0 })
                {
                    var rp = RiskParity.EqualRiskContribution(cov, stratNames.Length, normalizeToMeanOne: true);
                    var mp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    for (int a = 0; a < stratNames.Length; a++) mp[stratNames[a]] = rp.Weights[a];
                    retShape = s => mp.TryGetValue(s, out var wv) ? wv : 1.0;
                    shapeName = "riskparity";
                }
                else
                {
                    var vt = SizingMethods.VolTargetStrategyWeights(
                        allTrades.Select(t => (t.Strategy, t.Return)).ToList(),
                        targetVolPct: 1.5, volFloorPct: 0.5, volCapPct: 8.0, meanNormalise: false) ?? (s => 1.0);
                    retShape = vt;
                    shapeName = "voltarget";
                }

                // Risk-cap: ES (expected shortfall) is the strongest risk reducer; fall back to vol-target.
                Func<string, double> riskCap;
                string riskName;
                var esW = VaRSizing.BuildWeights(allTrades.Select(t => (t.Strategy, t.Return)).ToList(), "es", gamma: 4.0, budgetPct: 2.0, refFrac: 0.05);
                if (esW != null) { riskCap = esW; riskName = "es"; }
                else
                {
                    var vt = SizingMethods.VolTargetStrategyWeights(
                        allTrades.Select(t => (t.Strategy, t.Return)).ToList(),
                        targetVolPct: 1.5, volFloorPct: 0.5, volCapPct: 8.0, meanNormalise: false) ?? (s => 1.0);
                    riskCap = vt; riskName = "voltarget";
                }

                var allStrats = allTrades.Select(t => t.Strategy).Distinct().ToList();
                string ebTag = jointCov is { } jc && stratNames is { Length: > 0 } ? $"EB={CovarianceMatrix.EffectiveBets(jc, stratNames.Length):F1}" : "n/a";
                Console.WriteLine($"  [SIZING=blend] geometric mix: return-shape=<{shapeName}> × risk-cap=<{riskName}>  (covariance {ebTag})");
                Console.WriteLine($"  {"alpha",6}  {"ret%",9}  {"maxDD%",8}  {"ret/DD",8}");

                // Sweep alpha, score each blend on the ACTUAL portfolio outcome. The user goal is "high return,
                // low drawdown". With the exposure cap binding, DD stays in a tight band across α while
                // return varies meaningfully — so the right pick is the MOST RETURN among blends whose
                // DD is within a small budget of the safest point (DdBudgetPts = 0.20pp over min DD).
                // This is the Pareto-knee on the (return, DD) frontier, not just "lowest DD".
                double bestAlpha = 0, bestRet = 0, bestDD = 0;
                double minDd = double.PositiveInfinity;
                var points = new List<(double alpha, double ret, double dd)>();
                foreach (var alpha in new double[] { 0.0, 0.2, 0.35, 0.5, 0.65, 0.8, 1.0 })
                {
                    Func<string, double>? blended = SizingMethods.BlendWeights(retShape, riskCap, allStrats, alpha, meanNormalise: true);
                    var (retPct, ddPct, _) = SizingMethods.BacktestWithWeights(
                        allTradesForExposure, blended, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
                    double score = ddPct > 1e-9 ? retPct / Math.Pow(ddPct, 1.5) : retPct;
                    Console.WriteLine($"  {alpha,6:F2}  {retPct,+9:F1}  {ddPct,8:F2}  {score,8:F2}");
                    points.Add((alpha, retPct, ddPct));
                    minDd = Math.Min(minDd, ddPct);
                }
                const double ddBudgetPts = 0.20;   // allowed DD headroom over the safest point
                double ddCeiling = minDd + ddBudgetPts;
                foreach (var p in points)
                    if (p.dd <= ddCeiling && p.ret > bestRet)
                    { bestRet = p.ret; bestDD = p.dd; bestAlpha = p.alpha; }
                strategySizeWeight = SizingMethods.BlendWeights(retShape, riskCap, allStrats, bestAlpha, meanNormalise: true);
                Console.WriteLine($"  → best blend (max return with DD ≤ {minDd:F2}%+{ddBudgetPts:F2}pp): alpha {bestAlpha:F2} → {bestRet:F1}% return, {bestDD:F2}% DD");
                break;
            }
        }

        var port5cap   = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct, maxPositionFrac: 0.05, dynamicCap: dynCap, strategyWeight: strategySizeWeight);
        var portKelly  = Simulator.SimulatePortfolioExposureCapped(allTradesForExposure, Config.MaxTotalExposurePct, dynamicCap: dynCap, strategyWeight: strategySizeWeight);

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

        // ── Alpha / beta vs BTC ───────────────────────────────────────────────────────────
        // Every PF above is a RAW return, so a strategy that is simply long-biased through a
        // rising market is indistinguishable from one with edge. This decomposes each strategy's
        // trades against the BTC move over that trade's OWN holding period — the market exposure
        // it actually carried — and reports what is left over.
        //
        // The reason this matters here specifically: OOS annualises to +86-96%/yr against
        // validation's +19.5%/yr. OOS spans the 2023-24 bull run, validation is the recent
        // trailing slice. A 4x gap in THAT direction is what levered beta looks like; genuine
        // edge degrades out of sample rather than quadrupling.
        if (btcH1ForGuard is { Length: > 1 })
        {
            var byStrategy = allTrades
                .GroupBy(t => t.Strategy)
                .ToDictionary(g => g.Key,
                              g => (IReadOnlyList<(DateTime, DateTime, double)>)
                                   g.Select(t => (t.Entry, t.Time, t.Return)).ToList());

            BetaDecomposition.PrintHeader();
            foreach (var kv in byStrategy.OrderBy(k => k.Key))
                BetaDecomposition.Print(kv.Key, BetaDecomposition.RegressTrades(kv.Value, btcH1ForGuard));

            var all = allTrades.Select(t => (t.Entry, t.Time, t.Return)).ToList();
            BetaDecomposition.Print("PORTFOLIO", BetaDecomposition.RegressTrades(all, btcH1ForGuard));
        }

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

        // ── DynamicGuard: applied vs not ─────────────────────────────────────────────────
        // The two portfolio blocks printed immediately above are UNGUARDED, exactly as they have
        // always been — the guard was trained, saved and reported but never applied here. Rather
        // than swap the headline (which would be indistinguishable from a P&L regression in a
        // diff), both columns are printed side by side and the delta is made explicit.
        GuardedPortfolio.PrintComparison(
            "Combined portfolio (val window, router-gated)",
            allTradesGuardInput, guardCtx, Config.MaxTotalExposurePct);


        // ── RANDOM-ENTRY CONTROL ──────────────────────────────────────────────────────
        // The load-bearing test of this whole system. Measured earlier: DipLong's ungated
        // held-out PF is 0.64 while its router-gated val PF is 2.44, and the router discards
        // 54-57% of every strategy's trades. That means the edge demonstrably lives in the
        // GATING, not obviously in the entry signals — so the question is whether the entry
        // signals contribute anything at all beyond "be long in Bull, short in Bear".
        //
        // Control: replace each strategy's entry SIGNAL with a random entry, holding rate,
        // direction, hold length and router gate all fixed to the real values. Returns are the
        // coin's ACTUAL subsequent price move over that hold, minus the same round-trip cost.
        //
        // Read it like this: if the random-entry column lands near the real one, the entry
        // signals are decorative and this is a regime-timing model wearing six strategy hats.
        // A real edge should beat its own random control by a wide margin.
        if (session != null && controlSeries.Count > 0 && allTrades.Count > 0)
        {
            var rng = new Random(20260810);   // fixed: this repo requires reproducible runs
            Console.WriteLine($"\n── Random-entry control (same rate · same hold · same router gate) ──");
            Console.WriteLine($"  {"Strategy",-12}  {"Real PF",8}  {"Random PF",10}  {"Real avg%",10}  {"Rand avg%",10}  {"N",6}");
            Console.WriteLine($"  {new string('-', 64)}");

            foreach (var grp in allTrades.GroupBy(t => t.Strategy).OrderBy(g => g.Key))
            {
                var kind = StrategyPipeline.StrategyKindOf(grp.Key);
                if (kind is null) continue;
                bool isLong  = PortfolioReplay.IsLong(grp.Key) ?? true;
                int  nReal   = grp.Count();
                int  holdBars = Math.Max(1, (int)StrategyHold(grp.Key, swingG, gridG, flG, dlG, slG, rsG, agBullG).TotalHours);

                var randRet = new List<double>(nReal);
                // Spread the same number of entries across the same coins, so breadth matches too.
                int perCoin = Math.Max(1, nReal / Math.Max(1, controlSeries.Count));
                foreach (var (sym, series) in controlSeries)
                {
                    if (series.Length <= holdBars + 2) continue;
                    for (int k = 0; k < perCoin; k++)
                    {
                        int i = rng.Next(0, series.Length - holdBars - 1);
                        // Same router gate, evaluated at the EXIT bar — the field CombinedBacktest
                        // gates the real trades on, so the comparison is like-for-like.
                        var exitTime = series[i + holdBars].Time;
                        if (!session.IsActive(kind.Value, exitTime)) continue;
                        double px0 = series[i].Close, px1 = series[i + holdBars].Close;
                        if (px0 <= 1e-9) continue;
                        double gross = (px1 - px0) / px0 * 100.0;
                        if (!isLong) gross = -gross;
                        randRet.Add(gross - TradeCosts.FeeRoundTripPct);
                    }
                }

                if (randRet.Count < 20) continue;
                var realRet = grp.Select(t => t.Return).ToList();
                Console.WriteLine($"  {grp.Key,-12}  {Simulator.ProfitFactor(realRet),8:F2}  " +
                                  $"{Simulator.ProfitFactor(randRet),10:F2}  {realRet.Average(),+9:F2}%  " +
                                  $"{randRet.Average(),+9:F2}%  {randRet.Count,6}");
            }
            Console.WriteLine($"  Random entries are priced on real subsequent price moves and charged the same");
            Console.WriteLine($"  round-trip fee; they carry NO stop, target or trailing logic.");

            if (rsWaitRet.Count >= 20 && rsRet.Count >= 20)
            {
                Console.WriteLine($"\n── RipShort exit override: WaitForBreakeven vs production ──");
                Console.WriteLine($"  {"Variant",-22} {"PF",6} {"WR",6} {"Avg%",8} {"N",6}");
                Console.WriteLine($"  {"production (hard MaxHold)",-22} {Simulator.ProfitFactor(rsRet),6:F2} " +
                                  $"{(double)rsRet.Count(r => r > 0) / rsRet.Count,6:P0} {rsRet.Average(),+7:F2}% {rsRet.Count,6}");
                Console.WriteLine($"  {"WaitForBreakeven",-22} {Simulator.ProfitFactor(rsWaitRet),6:F2} " +
                                  $"{(double)rsWaitRet.Count(r => r > 0) / rsWaitRet.Count,6:P0} {rsWaitRet.Average(),+7:F2}% {rsWaitRet.Count,6}");
                // The tail is the whole question. "Wait for breakeven" improves PF and WR by
                // converting small realised losses into eventual wins — right up until one does
                // not recover. Averages cannot show that; the worst trade and the 5% CVaR can.
                static (double Worst, double Cvar5) Tail(List<double> r)
                {
                    var srt = r.OrderBy(x => x).ToList();
                    int m = Math.Max(1, (int)Math.Ceiling(srt.Count * 0.05));
                    return (srt[0], srt.Take(m).Average());
                }
                var tProd = Tail(rsRet); var tWait = Tail(rsWaitRet);
                if (rsHybRet.Count >= 20)
                {
                    var tH = Tail(rsHybRet);
                    Console.WriteLine($"  {"ATR cap + ATR ratchet",-22} {Simulator.ProfitFactor(rsHybRet),6:F2} " +
                                      $"{(double)rsHybRet.Count(r => r > 0) / rsHybRet.Count,6:P0} {rsHybRet.Average(),+7:F2}% {rsHybRet.Count,6}" +
                                      $"   worst {tH.Worst,6:F2}%  CVaR5 {tH.Cvar5,6:F2}%");
                }
                if (rsAtrCapRet.Count >= 20)
                {
                    var tA = Tail(rsAtrCapRet);
                    Console.WriteLine($"  {"ATR cap 2.5A [3,10]%",-22} {Simulator.ProfitFactor(rsAtrCapRet),6:F2} " +
                                      $"{(double)rsAtrCapRet.Count(r => r > 0) / rsAtrCapRet.Count,6:P0} {rsAtrCapRet.Average(),+7:F2}% {rsAtrCapRet.Count,6}" +
                                      $"   worst {tA.Worst,6:F2}%  CVaR5 {tA.Cvar5,6:F2}%");
                }
                if (rsLockRet.Count >= 20)
                {
                    var tLock = Tail(rsLockRet);
                    Console.WriteLine($"  {"Cap + profit ratchet",-22} {Simulator.ProfitFactor(rsLockRet),6:F2} " +
                                      $"{(double)rsLockRet.Count(r => r > 0) / rsLockRet.Count,6:P0} {rsLockRet.Average(),+7:F2}% {rsLockRet.Count,6}" +
                                      $"   worst {tLock.Worst,6:F2}%  CVaR5 {tLock.Cvar5,6:F2}%");
                }
                if (rsCapRet.Count >= 20)
                {
                    var tCap = Tail(rsCapRet);
                    Console.WriteLine($"  {"Wait + 6% loss cap",-22} {Simulator.ProfitFactor(rsCapRet),6:F2} " +
                                      $"{(double)rsCapRet.Count(r => r > 0) / rsCapRet.Count,6:P0} {rsCapRet.Average(),+7:F2}% {rsCapRet.Count,6}" +
                                      $"   worst {tCap.Worst,6:F2}%  CVaR5 {tCap.Cvar5,6:F2}%");
                }
                Console.WriteLine($"  {"worst / CVaR5 prod",-22} {tProd.Worst,6:F2}% {tProd.Cvar5,13:F2}%");
                Console.WriteLine($"  {"worst / CVaR5 wait",-22} {tWait.Worst,6:F2}% {tWait.Cvar5,13:F2}%");
                if (rsMaxHoldSweep.Count > 0)
                {
                    Console.WriteLine($"\n  ── MaxHold sweep (ATR cap on, no wait/ratchet) ──");
                    Console.WriteLine($"  {"MaxHold",8} {"ratchet",8} {"PF",6} {"WR",6} {"Avg%",8} {"N",6} {"worst",8} {"CVaR5",8}");
                    foreach (var kv in rsMaxHoldSweep.OrderBy(k => Math.Abs(k.Key)).ThenBy(k => k.Key))
                    {
                        var r = kv.Value; if (r.Count < 20) continue;
                        var t3 = Tail(r);
                        int mhAbs = Math.Abs(kv.Key);
                        Console.WriteLine($"  {(mhAbs >= 100_000 ? "OFF" : mhAbs.ToString()),8} {(kv.Key < 0 ? "on" : "off"),8} {Simulator.ProfitFactor(r),6:F2} " +
                                          $"{(double)r.Count(x => x > 0) / r.Count,6:P0} {r.Average(),+7:F2}% {r.Count,6} " +
                                          $"{t3.Worst,7:F2}% {t3.Cvar5,7:F2}%");
                    }
                    Console.WriteLine($"  If OFF holds up, the time exit is redundant once the cap and trail carry the risk.");
                }

                if (rsAnatomy.Count >= 40)
                {
                    var srt = rsAnatomy.OrderBy(a => a.Ret).ToList();
                    int d = Math.Max(5, srt.Count / 10);
                    var worst = srt.Take(d).ToList();
                    var best  = srt.Skip(srt.Count - d).ToList();
                    static double Med(IEnumerable<double> xs)
                    { var l = xs.OrderBy(x => x).ToList(); return l.Count == 0 ? 0 : l[l.Count / 2]; }

                    Console.WriteLine($"\n  ── Trade anatomy: bottom vs top decile (N={srt.Count}, decile={d}) ──");
                    Console.WriteLine($"  {"",-14} {"median ret",11} {"ATR% @entry",12} {"hold h",8} {"guardMult",10} {"BTC atrRatio",13}");
                    Console.WriteLine($"  {"big LOSSES",-14} {Med(worst.Select(a => a.Ret)),10:F2}% " +
                                      $"{Med(worst.Select(a => a.AtrPctEntry)),11:F2}% {Med(worst.Select(a => a.HoldH)),7:F0} " +
                                      $"{Med(worst.Select(a => a.GuardMult)),10:F3} {Med(worst.Select(a => a.BtcAtrRatio)),13:F3}");
                    Console.WriteLine($"  {"big WINS",-14} {Med(best.Select(a => a.Ret)),10:F2}% " +
                                      $"{Med(best.Select(a => a.AtrPctEntry)),11:F2}% {Med(best.Select(a => a.HoldH)),7:F0} " +
                                      $"{Med(best.Select(a => a.GuardMult)),10:F3} {Med(best.Select(a => a.BtcAtrRatio)),13:F3}");
                    Console.WriteLine($"  {"all",-14} {Med(srt.Select(a => a.Ret)),10:F2}% " +
                                      $"{Med(srt.Select(a => a.AtrPctEntry)),11:F2}% {Med(srt.Select(a => a.HoldH)),7:F0} " +
                                      $"{Med(srt.Select(a => a.GuardMult)),10:F3} {Med(srt.Select(a => a.BtcAtrRatio)),13:F3}");
                    Console.WriteLine($"  Entry ATR is HIGHER for BOTH tails than the median trade — it is a variance");
                    Console.WriteLine($"  amplifier, not a loss predictor, so filtering it would cut the 3:1 upside too.");
                    Console.WriteLine($"  If guardMult is LOWER on the wins, guard+rotator are down-sizing the best trades.");
                }

                if (rsRatchetGrid.Count > 0)
                {
                    Console.WriteLine($"\n  ── Profit-ratchet grid (on top of the ATR cap 2.5A [3,10]%) ──");
                    Console.WriteLine($"  {"Trig%",6} {"Lock%",6} {"PF",6} {"WR",6} {"Avg%",8} {"N",6} {"worst",8} {"CVaR5",8}");
                    foreach (var kv in rsRatchetGrid.OrderBy(k => k.Key.Trig).ThenBy(k => k.Key.Lock))
                    {
                        var r = kv.Value;
                        if (r.Count < 20) continue;
                        var t2 = Tail(r);
                        Console.WriteLine($"  {kv.Key.Trig,6:F1} {kv.Key.Lock,6:F1} {Simulator.ProfitFactor(r),6:F2} " +
                                          $"{(double)r.Count(x => x > 0) / r.Count,6:P0} {r.Average(),+7:F2}% {r.Count,6} " +
                                          $"{t2.Worst,7:F2}% {t2.Cvar5,7:F2}%");
                    }
                    Console.WriteLine($"  Whole grid shown — pick on risk appetite, not on the maximum cell.");
                }

                Console.WriteLine($"  Holding a losing SHORT longer is the risky direction — bear-rally squeezes are");
                Console.WriteLine($"  RipShort's documented dominant tail. If CVaR5 worsens, PF is buying that with tail risk.");
            }

            GatedExposureBaseline(controlSeries, session);
        }

        // ── Graded router sizing vs the boolean gate ──────────────────────────────────
        // Headline stays boolean. This measures whether scaling size by regime confidence beats
        // the on/off gate before anything depends on it — the same discipline applied to the
        // guard and the rotator.
        if (session != null && allTradesGuardInput.Count >= 2)
        {
            var graded = new List<(DateTime, double, double, TimeSpan, string)>(allTradesGuardInput.Count);
            var mults  = new List<double>(allTradesGuardInput.Count);
            foreach (var t in allTradesGuardInput)
            {
                var kind = StrategyPipeline.StrategyKindOf(t.Strategy);
                // A label the router does not gate (e.g. accumgrid) keeps full size rather than
                // being silently zeroed by a kind it was never routed by.
                double m = kind is null ? 1.0 : session.SizeMult(kind.Value, t.Time);
                mults.Add(m);
                graded.Add((t.Time, t.Return, t.Conf * m, t.Hold, t.Strategy));
            }

            var gp5  = Simulator.SimulatePortfolioExposureCapped(
                GuardedPortfolio.Passthrough(allTradesGuardInput), Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var gp5g = Simulator.SimulatePortfolioExposureCapped(graded, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

            Console.WriteLine($"\n── Graded router sizing (val window): size scaled by regime confidence ──");
            Console.WriteLine($"  Curve: conf {Config.GradedConfStart:F2} → floor {Config.GradedSizeFloor:P0} funding, " +
                              $"conf {Config.GradedConfFull:F2} → 100%");
            Console.WriteLine($"  Size mult over {mults.Count} trades: min={mults.Min():F3}  mean={mults.Average():F3}  max={mults.Max():F3}");
            Console.WriteLine($"  {"Sizing",-18}  {"Boolean",10}  {"Graded",10}  {"Δ",9}  {"DD bool",8}  {"DD graded",9}");
            Console.WriteLine($"  {"5% per position",-18}  {gp5.EndBalance - 100,+9:F1}%  {gp5g.EndBalance - 100,+9:F1}%  " +
                              $"{gp5g.EndBalance - gp5.EndBalance,+8:F1}pp  {gp5.MaxDrawdownPct,7:F1}%  {gp5g.MaxDrawdownPct,8:F1}%");
        }

        // ── Rotator validation ────────────────────────────────────────────────────────
        // The rotator had never appeared in ANY backtest: its only consumer was papertrade,
        // where ComputeSafetyScore feeds a console line and a JSON field and sizes nothing.
        // Its weights were therefore trained and saved without ever being scored against P&L.
        // Same treatment as the guard — side by side, headline untouched.
        //
        // ponytail: capital rotated OUT of alts is modelled as FLAT, not as BTC/ETH exposure.
        // That makes this a "de-risk to cash" LOWER BOUND rather than a true rotation test —
        // crediting the BTC/ETH leg needs benchmark series threaded through here. It still
        // answers the load-bearing question: does throttling alt exposure on stress help?
        if (guardCtx != null && File.Exists(Config.RotatorGenoFile) && allTradesGuardInput.Count >= 2)
        {
            var rotGeno = JsonSerializer.Deserialize<VolatilityWeightedRotatorGenotypeDto>(
                File.ReadAllText(Config.RotatorGenoFile))!.ToGenotype();
            var rot = new VolatilityWeightedRotator(rotGeno);
            var gs  = guardCtx.Session;

            var rTimes   = allTradesGuardInput.Select(t => t.Time).ToList();
            var rRegimes = btcRegimeSeries is { Length: > 0 }
                ? RegimeBarLookup.TagRegimes(btcRegimeSeries, rTimes)
                : Enumerable.Repeat(MarketRegime.Ranging, rTimes.Count).ToArray();

            var scores  = new double[rTimes.Count];
            var rotated = new List<(DateTime, double, double, TimeSpan, string)>(rTimes.Count);
            // Same rotation cost the GA is now charged (a round trip on the fraction moved), so
            // training and reporting price churn identically instead of one seeing it free.
            var rotTrades = allTrades.Select(t => (t.Time, t.Return, t.Strategy)).ToList();
            double prevAlt = 1.0, rotationCostPct = 0.0;
            double rotBenchPct = 0.0;   // return earned by capital rotated into BTC/ETH
            var safeShareAt = new List<(DateTime Time, double SafeShare)>();
            for (int i = 0; i < allTradesGuardInput.Count; i++)
            {
                var t = allTradesGuardInput[i];
                // Unknown label ⇒ treat as long, matching PortfolioReplay's convention of taking
                // the conservative reading rather than silently exempting it from the haircut.
                bool isLong = PortfolioReplay.IsLong(t.Strategy) ?? true;
                // Same 24h BTC move the GA fitness uses — train and serve must see one signal.
                double s = rot.ComputeSafetyScore(gs.GetMult(t.Time), gs.GetAtrRatio(t.Time), rRegimes[i], isLong,
                                                  BtcMovePct(btcH1ForGuard, t.Time));
                scores[i] = s;
                var (altShare, btcShare, ethShare) = rot.ComputeAllocation(s);
                // Same deadband the GA fitness applies — train and serve must rotate identically.
                double stepped = rot.StepAltShare(prevAlt, altShare);
                rotationCostPct += Math.Abs(stepped - prevAlt) * TradeCosts.FeeRoundTripPct;
                prevAlt = stepped; altShare = stepped;
                btcShare = (1.0 - altShare) * rot.BtcShareOfSafe;
                ethShare = (1.0 - altShare) - btcShare;
                rotated.Add((t.Time, t.Return, t.Conf * altShare, t.Hold, t.Strategy));

                // Record the target safe-share at this instant. The benchmark sleeve is computed
                // ONCE below on a time grid, NOT summed per trade — rotated-out capital is a
                // continuously-held position, not a fresh one per signal. Summing per trade
                // over 2800 overlapping trades over-counts it by roughly the average concurrency
                // and produced a nonsense -149pp on the first attempt.
                safeShareAt.Add((t.Time, btcShare + ethShare));
            }

            // ── Benchmark sleeve, time-weighted ────────────────────────────────────────────
            // Walk the BTC series once. Between consecutive signal instants the rotated-out
            // fraction is held constant and earns BTC's return over that interval, compounded.
            // This is a TIME-weighted holding, which is what a rotation actually is.
            if (btcH1ForGuard is { Length: > 1 } && safeShareAt.Count > 1)
            {
                safeShareAt.Sort((x, y) => x.Time.CompareTo(y.Time));
                double sleeve = 1.0;
                for (int i = 1; i < safeShareAt.Count; i++)
                {
                    double share = safeShareAt[i - 1].SafeShare;
                    if (share <= 1e-9) continue;
                    double seg = BenchReturnPct(btcH1ForGuard, safeShareAt[i - 1].Time,
                                                safeShareAt[i].Time - safeShareAt[i - 1].Time);
                    sleeve *= 1.0 + share * seg / 100.0;
                }
                rotBenchPct = (sleeve - 1.0) * 100.0;
            }

            var flatIn = GuardedPortfolio.Passthrough(allTradesGuardInput);
            var rp5    = Simulator.SimulatePortfolioExposureCapped(flatIn,  Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var rp5r   = Simulator.SimulatePortfolioExposureCapped(rotated, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);

            Console.WriteLine($"\n── Rotator (val window): alt exposure scaled by (1 − safety score) ──");
            Console.WriteLine($"  Genotype: guardW={rotGeno.GuardWeight:E2}  atrW={rotGeno.AtrWeight:E2}  " +
                              $"regimeW={rotGeno.RegimeWeight:F3}  btcShare={rotGeno.BtcShare:F2}  speed={rotGeno.RotationSpeed:F2}");
            // Read the mix FROM the rotator rather than recomputing it here. The local version
            // summed only guard+atr+regime and so reported regime=99.98% on a genotype whose
            // dominant gene was BtcStressWeight — a display that silently omits a gene is how a
            // stale report becomes a wrong conclusion.
            var mix = rot.SignalMix();
            Console.WriteLine($"  Signal mix: guard={mix.Guard:P2}  atr={mix.Atr:P2}  " +
                              $"regime={mix.Regime:P2}  btcStress={mix.BtcStress:P2}");
            Console.WriteLine($"  Rotation shape: minAlt={rotGeno.MinAltShare:P0}  " +
                              $"deadband={rotGeno.RotationDeadband:F3}  speed={rotGeno.RotationSpeed:F2}");
            int mults0 = 0;
            static double BtcMovePct(Candle[]? bars, DateTime t, int lookbackBars = 24)
            {
                if (bars is not { Length: > 1 }) return 0.0;
                int i = IdxAt(bars, t);
                int i0 = Math.Max(0, i - lookbackBars);
                double p0 = bars[i0].Close;
                return p0 > 1e-12 ? (bars[i].Close - p0) / p0 * 100.0 : 0.0;
            }
            // Helper: benchmark return over a trade's holding window, on the BTC clock.
            static double BenchReturnPct(Candle[]? bars, DateTime t0, TimeSpan hold)
            {
                if (bars is not { Length: > 1 }) return 0.0;
                int i0 = IdxAt(bars, t0), i1 = IdxAt(bars, t0 + hold);
                if (i0 < 0 || i1 <= i0) return 0.0;
                double p0 = bars[i0].Close;
                return p0 > 1e-12 ? (bars[i1].Close - p0) / p0 * 100.0 : 0.0;
            }
            static int IdxAt(Candle[] bars, DateTime t)
            {
                int lo = 0, hi = bars.Length - 1;
                if (t <= bars[0].Time) return 0;
                if (t >= bars[^1].Time) return bars.Length - 1;
                while (lo < hi) { int m = (lo + hi + 1) / 2; if (bars[m].Time <= t) lo = m; else hi = m - 1; }
                return lo;
            }
            for (int i = 1; i < scores.Length; i++) if (Math.Abs(scores[i] - scores[i - 1]) > 1e-9) mults0++;
            Console.WriteLine($"  Safety score: min={scores.Min():F3}  mean={scores.Average():F3}  max={scores.Max():F3}   " +
                              $"→ mean alt share retained {1.0 - scores.Average():P1}");
            // Net = alt sleeve + benchmark sleeve - turnover.
            double rotNet = rp5r.EndBalance - 100 - rotationCostPct + rotBenchPct;
            Console.WriteLine($"  Rotation cost: {rotationCostPct:F2}pp over {mults0} allocation changes " +
                              $"(round trip on the fraction moved, at {TradeCosts.FeeRoundTripPct:F2}% each)");
            Console.WriteLine($"  {"Sizing",-18}  {"Rot off",10}  {"Rot on",10}  {"Δ net",9}  {"DD off",7}  {"DD on",7}");
            Console.WriteLine($"  {"5% per position",-18}  {rp5.EndBalance - 100,+9:F1}%  {rotNet,+9:F1}%  " +
                              $"{rotNet - (rp5.EndBalance - 100),+8:F1}pp  {rp5.MaxDrawdownPct,6:F1}%  {rp5r.MaxDrawdownPct,6:F1}%");
            Console.WriteLine($"  Benchmark sleeve (rotated-out capital held in BTC/ETH): {rotBenchPct:+0.0;-0.0}pp");

            // ── WHY does rotating cost? Two candidate explanations, and they imply opposite fixes:
            //   (a) it moves BIG losses to SMALL ones  -> trades during stress are net NEGATIVE,
            //       rotating is right, and the loss is timing/turnover.
            //   (b) it just deploys LESS capital       -> trades during stress are net POSITIVE,
            //       so shrinking them destroys value no matter how well timed.
            // The rotator scales t.Conf * altShare, i.e. it shrinks WINNERS and LOSERS alike.
            {
                var byStress = new List<(double Stress, double Ret, bool IsLong)>();
                for (int i = 0; i < rotTrades.Count; i++)
                {
                    var t = rotTrades[i];
                    byStress.Add((VolatilityWeightedRotator.BtcStress(BtcMovePct(btcH1ForGuard, t.Time)),
                                  t.Return, PortfolioReplay.IsLong(t.Strategy) ?? true));
                }
                foreach (var (lo, hi, label) in new[] { (0.0, 0.01, "calm      "), (0.01, 0.5, "mild      "),
                                                        (0.5, 0.99, "stressed  "), (0.99, 9.9, "max stress") })
                {
                    var b = byStress.Where(x => x.Stress >= lo && x.Stress < hi).ToList();
                    if (b.Count < 20) continue;
                    var lng = b.Where(x => x.IsLong).ToList();
                    var sht = b.Where(x => !x.IsLong).ToList();
                    Console.WriteLine($"  [{label}] n={b.Count,5}  avg={b.Average(x => x.Ret),+6:F2}%   "
                                    + $"long n={lng.Count,5} avg={(lng.Count > 0 ? lng.Average(x => x.Ret) : 0),+6:F2}%   "
                                    + $"short n={sht.Count,4} avg={(sht.Count > 0 ? sht.Average(x => x.Ret) : 0),+6:F2}%");
                }
            }
            Console.WriteLine($"  Rationale: alts run 1.463 down-beta to BTC vs 1.202 up-beta (83 coins, 75/83");
            Console.WriteLine($"  positive); on BTC down days the median alt underperforms BTC by 0.864%/day.");
        }

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

            var nrPort5cap  = Simulator.SimulatePortfolioExposureCapped(nrExposure, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var nrPortKelly = Simulator.SimulatePortfolioExposureCapped(nrExposure, Config.MaxTotalExposurePct);

            int nrSwing     = allTradesNoRouter.Count(t => t.Strategy == "swing");
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
            Console.WriteLine($"  {"FadeShort",-12}  {swingRet.Count,11}  {nrSwing,9}  {nrSwing - swingRet.Count,+7}");
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
                var    p5cap      = Simulator.SimulatePortfolioExposureCapped(slice90, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
                var    pKelly     = Simulator.SimulatePortfolioExposureCapped(slice90, Config.MaxTotalExposurePct);

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
            var coinFsGFull = StrategyPipeline.SelectVariant(fsVariants, m15f) ?? swingG;
            var coinRet = new List<double>();
            foreach (var (t, ret, _, _, _) in FadeShortSimulator.GetFadeShortReturns(coinFsGFull, h1f, m15f, funding.For(sym)))
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
            foreach (var (t, ret, _, _, _) in GridSimulator.GetGridReturns(gridG, h1f, funding.For(sym)))
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
                var coinFlGFull = StrategyPipeline.SelectVariant(flVariants, m15f) ?? flG;
                var coinRet = new List<double>();
                foreach (var t in FadeLongSimulator.GetFadeLongReturns(coinFlGFull, h1f, m15f, funding.For(sym)))
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
                var coinDlGFull = StrategyPipeline.SelectVariant(dlVariants, m15f) ?? dlG;
                var coinRet = new List<double>();
                foreach (var t in DipLongSimulator.GetDipLongReturns(coinDlGFull, h1f, m15f, funding.For(sym)))
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
                var coinSlGFull = StrategyPipeline.SelectVariant(slVariants, m15f) ?? slG;
                var coinRet = new List<double>();
                foreach (var t in SwingLongSimulator.GetSwingLongReturns(coinSlGFull, h1f, m15f, funding.For(sym)))
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
                var coinRsGFull = StrategyPipeline.SelectVariant(rsVariants, m15f) ?? rsG;
                var coinRet = new List<double>();
                foreach (var t in RipShortSimulator.GetRipShortReturns(coinRsGFull, h1f, m15f, funding.For(sym)))
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
            var fhPort5cap = Simulator.SimulatePortfolioExposureCapped(fullHistTrades, Config.MaxTotalExposurePct, maxPositionFrac: 0.05);
            var fhPortKel  = Simulator.SimulatePortfolioExposureCapped(fullHistTrades, Config.MaxTotalExposurePct);
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
        // ONE canonical strategy list drives both the per-strategy DSR/PBO/WRC reports below
        // and the Holm family built after them, so the two can no longer disagree about which
        // strategies exist. Order is fixed so printed tables are stable across runs.
        var mcInputs = new (string Name, List<(string Label, List<double> Returns)> PerCoin, int GaTrials)[]
        {
            ("FadeShort", swingFullCoinRet, 10_000),
            ("Grid",      gridFullCoinRet,  10_000),
            ("FadeLong",  flFullCoinRet,    12_000),
            ("DipLong",   dlFullCoinRet,    12_000),
            ("SwingLong", slFullCoinRet,    10_000),
            ("RipShort",  rsFullCoinRet,    12_000),
        };

        var statRng = new Random(42);
        foreach (var (name, perCoin, gaTrials) in mcInputs)
            if (perCoin.Count >= MinFamilyCoins)
                StatisticalTests.PrintReport(perCoin, name, gaTrials: gaTrials, rng: statRng);

        // ── Holm-Bonferroni Family-Wise Error Rate Correction ────────────────────────
        var (strategyPValues, familyExclusions) = BuildMonteCarloFamily(mcInputs);

        if (strategyPValues.Count > 0 || familyExclusions.Count > 0)
        {
            Console.WriteLine($"\n{new string('═', 70)}");
            Console.WriteLine("  HOLM-BONFERRONI FAMILY-WISE ERROR RATE CORRECTION");
            Console.WriteLine($"{new string('═', 70)}");
            Console.WriteLine($"  Monte Carlo: {MonteCarloTest.FamilyWiseResamples:N0} mean-centred bootstrap resamples per strategy,");
            Console.WriteLine($"  each seeded from its own name — a strategy's p-value does not depend on which");
            Console.WriteLine($"  other strategies were included.");
        }

        // The family criteria are a SUPERSET of the DSR/WRC print criterion above (same coin
        // minimum, plus a trade minimum the bootstrap cannot work below), so a strategy can be
        // printed above and still miss the family. That shrinks m and makes the correction
        // WEAKER for everyone else while the excluded strategy still carries an uncorrected
        // verdict, so the exclusions are printed rather than dropped silently.
        if (familyExclusions.Count > 0)
        {
            Console.WriteLine($"\n  Excluded from the family (m = {strategyPValues.Count}, not {mcInputs.Length}):");
            foreach (var (name, reason) in familyExclusions)
                Console.WriteLine($"    {name,-12} {reason}");
            Console.WriteLine("    Any DSR / WRC verdict printed above for these is UNCORRECTED for multiplicity.");
        }

        // Apply Holm-Bonferroni correction and report
        if (strategyPValues.Count > 0)
        {
            double uncorrectedFWER = 1.0 - Math.Pow(0.95, strategyPValues.Count);
            Console.WriteLine();
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
            // Effective sample size, not the raw trade count. Trades are regime-gated and arrive
            // in bursts, so DSR's sqrt(n-1) against n=|trades| divides by an inflated sample and
            // reads "significant" almost regardless. Both columns are printed: the naive one for
            // continuity with previously recorded numbers, the effective one because it is the
            // honest verdict.
            int effN = StatisticalTests.EffectiveSampleSize(allTrades.Select(t => t.Entry).ToList());
            Console.WriteLine($"  n = {allFullRet.Count:N0} trades, but only ~{effN:N0} independent " +
                              $"blocks (>24h apart) — DSR below is shown on BOTH.");
            foreach (int T in new[] { 10_000, 50_000, 500_000 })
            {
                var (dsr, psr0, eMaxSr, srHat) = StatisticalTests.DeflatedSharpeRatio(allFullRet, T);
                var (dsrE, _, _, _)            = StatisticalTests.DeflatedSharpeRatio(allFullRet, T, effN);
                string v  = dsr  >= 0.95 ? "✓ significant" : dsr  >= 0.80 ? "⚠ borderline" : "✗ not significant";
                string vE = dsrE >= 0.95 ? "✓ significant" : dsrE >= 0.80 ? "⚠ borderline" : "✗ not significant";
                Console.WriteLine($"  {T,12:N0}  {srHat,+7:F4}  {eMaxSr,+9:F4}  {psr0,6:F3}  {dsr,6:F3}  {v}");
                Console.WriteLine($"  {"  └ effective",12}  {"",7}  {"",9}  {"",6}  {dsrE,6:F3}  {vE}");
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
