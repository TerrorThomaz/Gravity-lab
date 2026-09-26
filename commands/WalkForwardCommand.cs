using Bybit.Net.Clients;
using System.Text.Json;

namespace TradingGA;

// Standalone walk-forward validation of the COMMITTED genotypes (the files in genotypes/), on
// never-trained coins. This complements ExpandingWindowValidation, which runs INSIDE each strategy's
// GA train loop and re-trains a fresh GA per window — that measures "does a freshly-trained model
// generalise forward?", not "does the genotype we would actually trade generalise forward?".
//
// The whole point of the committed-genotype variant is that it can run WITHOUT retraining: load the
// saved JSON, slice each OOS coin's own history into expanding train/test stages, run the committed
// simulators on both sides, and report IS/OOS trade-Sharpe efficiency per stage. Fast enough to run
// nightly on already-cached data.
//
// Caveat echoed from ExpandingWindowValidation: efficiency uses per-trade Sharpe (no time
// normalisation), so a perfectly-generalising formula measures 1.0; < 0.5 flags overfit. The IS and
// OOS windows have different trade counts, and per-trade Sharpe is count-scale-free, so the ratio is
// comparable (unlike the frequency-normalised SharpeRatio, whose sqrt(candleCount/288) telescopes).
//
// ── RAW vs ROUTED ────────────────────────────────────────────────────────────────────────────────
// The four regime-gated strategies (DipLong, SwingLong, FadeLong, RipShort) are not meant to be
// judged on their raw simulator output: CombinedBacktest gates every trade through
// RegimeRouterSession.IsActive, and the router discards 54–57% of what the strategy GAs scored
// (DipLong 1221→531). DipLong's ungated held-out PF is 0.64 against a router-gated val PF of 2.44 —
// so a raw-only walk-forward would report OVERFIT on a strategy whose edge lives entirely in the
// gating, and a routed-only one would hide a strategy formula that had stopped working.
//
// Both are therefore reported side by side, RAW as the headline (unchanged from the pre-existing
// FadeShort/Grid behaviour, and the same convention GuardedPortfolio uses for guarded/unguarded).
// Gating uses each trade's t.Time, which every simulator records as the EXIT bar — the same field
// and the same entry-vs-exit approximation CombinedBacktest gates on, so this agrees with the
// backtest by construction rather than by coincidence.
//
// What the ROUTED column is NOT: an out-of-sample measure of the ROUTER. The committed router
// genotype was trained on windows that overlap these coins' calendar range, so the routed column
// measures "does this strategy+router pair hold up on coins neither has seen", exactly as the raw
// column measures the strategy alone. Coin-generalisation, not time-generalisation, on both sides.
//
// Known inherited hazard: RegimeRouterSession.Lookup falls back to the LAST BTC bar when a trade
// time misses its index by more than 4 hours, which would read future regime state. Every symbol
// here is fetched at the same batches:113 depth and BTC has the longest history of the set, so OOS
// coins start at or after BTC's first bar in practice — but a coin that ever predated BTC's series
// would silently take that fallback.
public static class WalkForwardCommand
{
    private const int StageCount   = 4;
    private const int MinStageBars = 60;
    private const double OverfitEffThreshold = 0.5;

    // Dual-timeframe simulators need enough 15m bars to resolve an entry; mirrors the
    // TrainM15.Length < 400 guard ExpandingWindowValidation applies per coin.
    private const int MinStageM15Bars = 400;

    // One strategy's per-stage IS/OOS return buckets, in both variants.
    private sealed class Track(string name, bool routable)
    {
        public readonly string Name = name;
        // False for strategies this command does not gate (none today) or when no router loaded.
        public readonly bool Routable = routable;
        public readonly List<double>[] RawIs   = Make();
        public readonly List<double>[] RawOos  = Make();
        public readonly List<double>[] GateIs  = Make();
        public readonly List<double>[] GateOos = Make();

        private static List<double>[] Make()
        {
            var a = new List<double>[StageCount];
            for (int i = 0; i < StageCount; i++) a[i] = new();
            return a;
        }
    }

    public static async Task Run(BybitRestClient client)
    {
        Console.WriteLine("=== Gravity-gen2 | WALK-FORWARD on COMMITTED genotypes (never-trained coins) ===\n");

        if (!File.Exists(Config.FadeShortGenoFile)) { Console.WriteLine("Missing FadeShort genotype."); return; }
        var fsG = JsonSerializer.Deserialize<FadeShortGenotypeDto>(File.ReadAllText(Config.FadeShortGenoFile))!.ToGenotype();
        GridGenotype? gridG = File.Exists(Config.GridGenoFile)
            ? JsonSerializer.Deserialize<GridGenotypeDto>(File.ReadAllText(Config.GridGenoFile))!.ToGenotype()
            : null;

        // The four regime-gated strategies. Each is optional: a missing file drops that row rather
        // than failing the run, so this stays usable on a partially-trained genotypes/ directory.
        DipLongGenotype? dlG = File.Exists(Config.DipLongGenoFile)
            ? JsonSerializer.Deserialize<DipLongGenotypeDto>(File.ReadAllText(Config.DipLongGenoFile))!.ToGenotype()
            : null;
        SwingLongGenotype? slG = File.Exists(Config.SwingLongGenoFile)
            ? JsonSerializer.Deserialize<SwingLongGenotypeDto>(File.ReadAllText(Config.SwingLongGenoFile))!.ToGenotype()
            : null;
        FadeLongGenotype? flG = File.Exists(Config.FadeLongGenoFile)
            ? JsonSerializer.Deserialize<FadeLongGenotypeDto>(File.ReadAllText(Config.FadeLongGenoFile))!.ToGenotype()
            : null;
        RipShortGenotype? rsG = File.Exists(Config.RipShortGenoFile)
            ? JsonSerializer.Deserialize<RipShortGenotypeDto>(File.ReadAllText(Config.RipShortGenoFile))!.ToGenotype()
            : null;
        RegimeRouterGenotype? routerG = File.Exists(Config.RouterGenoFile)
            ? JsonSerializer.Deserialize<RegimeRouterGenotypeDto>(File.ReadAllText(Config.RouterGenoFile))!.ToGenotype()
            : null;

        Console.WriteLine($"  FadeShort: {fsG}");
        if (gridG != null) Console.WriteLine($"  Grid:      {gridG}");
        if (dlG   != null) Console.WriteLine($"  DipLong:   {dlG}");
        if (slG   != null) Console.WriteLine($"  SwingLong: {slG}");
        if (flG   != null) Console.WriteLine($"  FadeLong:  {flG}");
        if (rsG   != null) Console.WriteLine($"  RipShort:  {rsG}");
        Console.WriteLine();
        Console.WriteLine($"  {Config.OosCoins.Length} never-seen OOS coins · {StageCount} expanding stages each · no retraining\n");

        // ETHUSDT joins BTCUSDT because the router blends it (EthBlendWeight) — fetching only BTC
        // would silently evaluate a different router than the one the backtests run.
        var allSyms = Config.OosCoins.Concat(new[] { "BTCUSDT", "ETHUSDT" }).Distinct().ToArray();
        var fetched = await StrategyPipeline.FetchFifteenMinAsync(client, allSyms, batches: 113);

        // withBtcBars stays false, matching OosBacktest rather than CombinedBacktest: without the BTC
        // bar series StressAt returns 0, so the BtcStress short override never fires. That is the
        // routing an OOS-style report should show, but it does mean FadeShort/GridShort/RipShort are
        // gated here without the shock override CombinedBacktest applies.
        var (session, btcSeries) = StrategyPipeline.BuildRouterSession(routerG, fetched, btcH1ForGuard: null);
        if (session == null)
            Console.WriteLine("  ⚠ No router session — ROUTED columns will be empty; RAW columns are unaffected.\n");

        var fadeShort = new Track("FadeShort", session != null);
        var grid      = new Track("Grid",      session != null);
        var dipLong   = new Track("DipLong",   session != null);
        var swingLong = new Track("SwingLong", session != null);
        var fadeLong  = new Track("FadeLong",  session != null);
        var ripShort  = new Track("RipShort",  session != null);

        // SwingLong reads its BTC-alignment gate (BtcAlignWeight, committed at 0.633) through a
        // STATIC probe that only SwingLongGA sets. Left null the simulator scores btcScore = 1.0,
        // which clears every possible `required`, so the trained gate silently does nothing. Setting
        // it here is what makes this walk-forward evaluate the genotype as trained — see the note
        // printed below, because combinedbacktest/oosbacktest do NOT currently set it.
        bool probeSet = false;
        try
        {
            if (btcSeries is { Length: > 0 })
            {
                SwingLongSimulator.BtcRegimeProbe = MakeBtcProbe(btcSeries);
                probeSet = true;
            }

            // BTCUSDT/ETHUSDT are fetched for the router only. They are not OOS coins, so their own
            // trades must not enter the never-trained aggregate.
            var oos = new HashSet<string>(Config.OosCoins);

            int lstages = 0;
            foreach (var f in fetched)
            {
                if (!oos.Contains(f.sym)) continue;
                var m15 = f.m15;
                if (m15.Length < (StageCount + 1) * MinStageBars * 4) continue;
                var h1 = FadeShortSimulator.AggregateCandles(m15, 4);

                for (int s = 0; s < StageCount; s++)
                {
                    // Stage s: train = [0, trainEnd), test = [trainEnd, trainEnd+step), on THIS coin's array.
                    int step = h1.Length / (StageCount + 1);
                    int trainEnd = (s + 1) * step;                 // increasingly long train
                    int testEnd  = Math.Min(trainEnd + step, h1.Length);
                    if (trainEnd < MinStageBars || testEnd - trainEnd < MinStageBars) continue;

                    lstages++;
                    var trainSpan = new ReadOnlySpan<Candle>(h1, 0, trainEnd);
                    var testSpan  = new ReadOnlySpan<Candle>(h1, trainEnd, testEnd - trainEnd);

                    // AggregateCandles(m15, 4) emits one h1 bar per NON-overlapping group of four
                    // 15m bars, so h1 index i maps to m15 index 4i exactly. This is the ×4 case of
                    // ExpandingWindowValidation.M15RangeByFactor and is valid for all four dual-TF
                    // strategies here: unlike the GA's per-coin bear-block extraction, nothing has
                    // filtered one timeframe independently of the other.
                    var trainM15 = M15Slice(m15, 0, trainEnd);
                    var testM15  = M15Slice(m15, trainEnd, testEnd);

                    // FadeShort (run on h1; the committed path uses h1 for regime/setup + m15 for fill,
                    // but the single-timeframe GetFadeShortReturns(h1) is the portable OOS signal used by
                    // the backtests — keep this consistent with the backtest's FadeShort evaluation).
                    Collect(fadeShort, s, FadeShortSimulator.GetFadeShortReturns(fsG, trainSpan).Select(t => (t.Time, t.Return)),
                            FadeShortSimulator.GetFadeShortReturns(fsG, testSpan).Select(t => (t.Time, t.Return)),
                            RegimeRouterGA.StrategyKind.FadeShort, session);

                    if (gridG != null)
                        Collect(grid, s, GridSimulator.GetGridReturns(gridG, trainSpan).Select(t => (t.Time, t.Return)),
                                GridSimulator.GetGridReturns(gridG, testSpan).Select(t => (t.Time, t.Return)),
                                RegimeRouterGA.StrategyKind.Grid, session);

                    bool dualTfUsable = trainM15.Length >= MinStageM15Bars && testM15.Length >= MinStageM15Bars;
                    if (!dualTfUsable) continue;

                    if (dlG != null)
                        Collect(dipLong, s, DipLongSimulator.GetDipLongReturns(dlG, trainSpan, trainM15).Select(t => (t.Time, t.Return)),
                                DipLongSimulator.GetDipLongReturns(dlG, testSpan, testM15).Select(t => (t.Time, t.Return)),
                                RegimeRouterGA.StrategyKind.DipLong, session);

                    if (slG != null)
                        Collect(swingLong, s, SwingLongSimulator.GetSwingLongReturns(slG, trainSpan, trainM15).Select(t => (t.Time, t.Return)),
                                SwingLongSimulator.GetSwingLongReturns(slG, testSpan, testM15).Select(t => (t.Time, t.Return)),
                                RegimeRouterGA.StrategyKind.SwingLong, session);

                    if (flG != null)
                        Collect(fadeLong, s, FadeLongSimulator.GetFadeLongReturns(flG, trainSpan, trainM15).Select(t => (t.Time, t.Return)),
                                FadeLongSimulator.GetFadeLongReturns(flG, testSpan, testM15).Select(t => (t.Time, t.Return)),
                                RegimeRouterGA.StrategyKind.FadeLong, session);

                    if (rsG != null)
                        Collect(ripShort, s, RipShortSimulator.GetRipShortReturns(rsG, trainSpan, trainM15).Select(t => (t.Time, t.Return)),
                                RipShortSimulator.GetRipShortReturns(rsG, testSpan, testM15).Select(t => (t.Time, t.Return)),
                                RegimeRouterGA.StrategyKind.RipShort, session);
                }
            }

            Console.WriteLine($"\n  {lstages} coin-stage evaluations across {Config.OosCoins.Length} coins\n");

            var tracks = new List<Track> { fadeShort };
            if (gridG != null) tracks.Add(grid);
            if (dlG   != null) tracks.Add(dipLong);
            if (slG   != null) tracks.Add(swingLong);
            if (flG   != null) tracks.Add(fadeLong);
            if (rsG   != null) tracks.Add(ripShort);

            foreach (var t in tracks) Print(t);

            // Overall verdict.
            Console.WriteLine($"\n  OVERALL");
            foreach (var t in tracks)
            {
                double raw  = Eff(t.RawIs,  t.RawOos);
                double gate = Eff(t.GateIs, t.GateOos);
                Console.WriteLine(
                    $"    {t.Name,-10} raw {Verdict(raw),-22}   routed {(t.Routable ? Verdict(gate) : "— (no router)")}");
            }
            Console.WriteLine("\n    (efficiency = mean(OOS Sharpe)/mean(IS Sharpe) across stages; < 0.5 flags overfit on never-seen coins)");
            Console.WriteLine("    (raw = simulator output; routed = trades RegimeRouterSession.IsActive would have allowed,");
            Console.WriteLine("     gated on each trade's EXIT bar, the same field CombinedBacktest gates on)");
            if (probeSet)
                Console.WriteLine("    (SwingLong ran WITH its BtcRegimeProbe set — combinedbacktest/oosbacktest leave that\n" +
                                  "     static null, where btcScore defaults to 1.0 and the trained BtcAlignWeight gate is inert)");
        }
        finally
        {
            // Static, process-wide: a leak would silently gate every later SwingLong run in this process.
            if (probeSet) SwingLongSimulator.BtcRegimeProbe = null;
        }
    }

    // Signed BTC alignment score (+conf in Bull, −conf in Bear, 0 otherwise), O(log n) per lookup.
    // Mirrors SwingLongGA's probe exactly — the gate must see the same function it was trained against.
    private static Func<DateTime, double> MakeBtcProbe(RegimeBar[] bars)
        => t =>
        {
            long ticks = t.Ticks;
            if (ticks <= bars[0].Time.Ticks)  return Signed(bars[0]);
            if (ticks >= bars[^1].Time.Ticks) return Signed(bars[^1]);
            int lo = 0, hi = bars.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (bars[mid].Time.Ticks <= ticks) lo = mid; else hi = mid - 1;
            }
            return Signed(bars[lo]);

            static double Signed(RegimeBar b) => b.Regime switch
            {
                MarketRegime.Bull => +b.Confidence,
                MarketRegime.Bear => -b.Confidence,
                _                 => 0.0,
            };
        };

    // h1 [start, end) → the m15 index range those h1 bars were aggregated from, clamped to the array.
    // Internal + separated from the span construction so the arithmetic is unit-testable: a silent
    // off-by-one here misaligns the two timeframes, which no figure in the report would reveal.
    internal static (int Start, int Len) M15Range(int m15Len, int h1Start, int h1End)
    {
        int start = Math.Clamp(h1Start * 4, 0, m15Len);
        int len   = Math.Max(0, Math.Min((h1End - h1Start) * 4, m15Len - start));
        return (start, len);
    }

    private static ReadOnlySpan<Candle> M15Slice(Candle[] m15, int h1Start, int h1End)
    {
        var (start, len) = M15Range(m15.Length, h1Start, h1End);
        return new ReadOnlySpan<Candle>(m15, start, len);
    }

    // IsActive's atrRatio argument is left at its 1.0 default: inside ComputeActivation it feeds only
    // the *LowVol / *HighVol outputs, and every kind queried here is a base kind. Passing a real
    // per-coin ratio would change nothing and would imply a variant selection this report does not make.
    private static void Collect(
        Track track, int stage,
        IEnumerable<(DateTime Time, double Return)> isTrades,
        IEnumerable<(DateTime Time, double Return)> oosTrades,
        RegimeRouterGA.StrategyKind kind,
        RegimeRouterSession? session)
    {
        foreach (var t in isTrades)
        {
            track.RawIs[stage].Add(t.Return);
            if (session != null && session.IsActive(kind, t.Time)) track.GateIs[stage].Add(t.Return);
        }
        foreach (var t in oosTrades)
        {
            track.RawOos[stage].Add(t.Return);
            if (session != null && session.IsActive(kind, t.Time)) track.GateOos[stage].Add(t.Return);
        }
    }

    private static string Verdict(double eff)
        => double.IsNaN(eff)
            ? "— (no usable stage)"
            : $"{eff,6:F3}  {(eff > OverfitEffThreshold ? "no overfit" : "OVERFIT")}";

    private static void Print(Track track)
    {
        Console.WriteLine($"── {track.Name} (committed genotype, never-trained coins) ──");
        Console.WriteLine($"  {"Stage",6}  {"RAW n(is/oos)",15}  {"RAW eff",9}  {"ROUTED n(is/oos)",18}  {"ROUTED eff",11}");
        for (int s = 0; s < StageCount; s++)
        {
            string rawN  = $"{track.RawIs[s].Count}/{track.RawOos[s].Count}";
            string gateN = track.Routable ? $"{track.GateIs[s].Count}/{track.GateOos[s].Count}" : "—";
            Console.WriteLine($"  {s,6}  {rawN,15}  {StageEff(track.RawIs[s], track.RawOos[s]),9}  " +
                              $"{gateN,18}  {(track.Routable ? StageEff(track.GateIs[s], track.GateOos[s]) : "—"),11}");
        }
        double rawEff  = Eff(track.RawIs,  track.RawOos);
        double gateEff = Eff(track.GateIs, track.GateOos);
        Console.WriteLine($"  → raw    mean efficiency {Verdict(rawEff)}");
        if (track.Routable)
            Console.WriteLine($"  → routed mean efficiency {Verdict(gateEff)}");
        Console.WriteLine();
    }

    // Per-stage efficiency as a display string. Mirrors Eff's usability rules so the per-stage
    // column and the mean below it can never disagree about which stages counted.
    private static string StageEff(List<double> isRet, List<double> oosRet)
    {
        if (isRet.Count < 10 || oosRet.Count < 10) return "— (thin)";
        double isS = FoldScoreHelper.PerTradeSharpe(isRet);
        if (isS <= 0.05) return "— (IS <0.05)";
        return Math.Clamp(FoldScoreHelper.PerTradeSharpe(oosRet) / isS, -1.0, 2.0).ToString("F3");
    }

    private static double Eff(List<double>[] isRet, List<double>[] oosRet)
    {
        var ratios = new List<double>();
        for (int s = 0; s < StageCount; s++)
        {
            if (isRet[s].Count < 10 || oosRet[s].Count < 10) continue;
            double isS = FoldScoreHelper.PerTradeSharpe(isRet[s]);
            if (isS <= 0.05) continue;
            double e = Math.Clamp(FoldScoreHelper.PerTradeSharpe(oosRet[s]) / isS, -1.0, 2.0);
            ratios.Add(e);
        }
        return ratios.Count > 0 ? ratios.Average() : double.NaN;
    }
}
