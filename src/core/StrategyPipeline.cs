using Bybit.Net.Clients;
using System.Text.Json;
using TradingGA;

// ── Shared backtest/bootstrap wiring ────────────────────────────────────────
//
// The single home for the steps every fait-and-full-history backtest repeats:
// variant loading/selection, the parallel candle fetch, and the regime/funding/
// guard session builds. CombinedBacktest, OosBacktest and AllCoinsBacktest used
// to each carry a personal copy of these bodies; a change to one silently drifted
// from the others. This is the reproducer-of-record: fix it here and every
// command that touches a candle or a session picks it up.
//
// This is deliberately NOT a strategy-loop driver. The per-coin capture loops and
// their diagnostics are embedded in each command and stay there; this file owns
// only the setup wiring that is identical across them.
public static class StrategyPipeline
{
    // ── Variant loading ──────────────────────────────────────────────────────
    // Scans genotypes/ for {strategyKey}_*_genotype.json plus the plain
    // {strategyKey}_genotype.json default, one VariantSpec per file found
    // (AtrLow/AtrHigh come from the DTO). Empty when none are present.
    public static VariantSpec<TG>[] LoadVariants<TDto, TG>(
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

    // Non-labeled selection: whitelist fallback to the first variant. Used where the
    // caller does not need the volatility bucket label.
    public static TG? SelectVariant<TG>(VariantSpec<TG>[] variants, Candle[] m15)
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

    // Labeled selection: narrowest ATR band containing the current ATR ratio, plus a
    // "lowvol"/"highvol"/"base" label for reporting. ATR is the ratio of the last
    // bar's 14-period ATR to the 100-bar mean (see LabelForVariant).
    public static (TG? Genotype, string Label) SelectVariantLabeled<TG>(VariantSpec<TG>[] variants, Candle[] m15)
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

    public static string LabelForVariant<T>(VariantSpec<T> v) where T : class
    {
        if (v.AtrLow >= 1.0) return "highvol";
        if (v.AtrHigh <= 1.0) return "lowvol";
        return "base";
    }

    // GRAVITY_ATRCAP=1 puts the ATR-scaled loss cap on RipShort's production path.
    // clamp(2.5 x ATR%, 3%, 10%): scales with the coin's own volatility so it sits outside
    // the noise (a flat 6% cap raised trade count 48% and halved return by being brushed),
    // but a short's downside is unbounded so it still meets an absolute ceiling.
    public static RipShortSimulator.ExitOverrideConfig? ProdRipCap() =>
        Environment.GetEnvironmentVariable("GRAVITY_ATRCAP") == "1"
            ? new RipShortSimulator.ExitOverrideConfig(RipShortSimulator.ExitOverrideMode.None,
                  MaxLossPct: 10.0, MaxLossAtrMult: 2.5, MaxLossPctFloor: 3.0)
            : null;

    // Portfolio trade label → router StrategyKind. Null means the router does not gate
    // this label (accumgrid is routed by its own Bull/Bear sub-genotypes, not a kind).
    public static RegimeRouterGA.StrategyKind? StrategyKindOf(string strategy) => strategy switch
    {
        "swing" or "fade_short" => RegimeRouterGA.StrategyKind.FadeShort,
        "grid"                  => RegimeRouterGA.StrategyKind.Grid,
        "gridshort"             => RegimeRouterGA.StrategyKind.GridShort,
        "diplong"               => RegimeRouterGA.StrategyKind.DipLong,
        "swing_long"            => RegimeRouterGA.StrategyKind.SwingLong,
        "fadelong"              => RegimeRouterGA.StrategyKind.FadeLong,
        "ripshort"              => RegimeRouterGA.StrategyKind.RipShort,
        _                       => null,
    };

    // One coin's candles as both timeframes. Positional record so it supports both
    // field access (f.sym / f.h1) and tuple deconstruction (foreach (var (sym,m15,h1))).
    public readonly record struct FetchedCandles(string sym, Candle[] m15, Candle[] h1);

    // Parallel fetch (4-concurrent) of 15m candles aggregated to 1h, using the disk cache.
    public static async Task<FetchedCandles[]> FetchFifteenMinAsync(
        BybitRestClient client, IEnumerable<string> symbols, int batches)
    {
        var sem = new SemaphoreSlim(4);
        var tasks = symbols.Select(async sym =>
        {
            await sem.WaitAsync();
            try
            {
                var m15 = await CandleFetcher.FetchFifteenMinCandlesCached(client, sym, batches: batches);
                var h1  = FadeShortSimulator.AggregateCandles(m15.ToArray(), 4);
                return new FetchedCandles(sym, m15.ToArray(), h1);
            }
            finally { sem.Release(); }
        });
        return await Task.WhenAll(tasks);
    }

    // Per-symbol funding sessions of the fetched coins. Unknown symbols resolve to null
    // (interest-rate floor fallback) rather than borrowing another symbol's rates.
    internal static async Task<CandleFetcher.FundingSessions> FetchFundingAsync(
        BybitRestClient client, IEnumerable<FetchedCandles> fetched)
    {
        var funding = await CandleFetcher.FetchFundingSessionsAsync(client, fetched.Select(f => f.sym));
        funding.PrintSummary();
        return funding;
    }

    // The regime router + the regime series it gates on, built from BTC (primary) and ETH
    // (secondary confirmer). When withBtcBars is set the session additionally carries the
    // BTC bar series for guard/portfolio use (CombinedBacktest does; OOS does not have that
    // need). Returns (null, null) when the router is absent or BTC data is too short.
    public readonly record struct RouterBootstrap(RegimeRouterSession? Session, RegimeBar[]? BtcRegimeSeries);

    public static RouterBootstrap BuildRouterSession(
        RegimeRouterGenotype? routerG,
        IReadOnlyList<FetchedCandles> fetched,
        Candle[]? btcH1ForGuard,
        bool withBtcBars = false)
    {
        if (routerG == null) return default;
        var btcEntry = fetched.FirstOrDefault(f => f.sym == "BTCUSDT");
        if (btcEntry.h1 == null || btcEntry.h1.Length < 200)
        {
            Console.WriteLine("  ⚠ BTC data insufficient for regime session — router gate disabled\n");
            return default;
        }
        var btcSeries = RegimeClassifier.ClassifySeriesWithDuration(btcEntry.h1);
        var ethEntry  = fetched.FirstOrDefault(f => f.sym == "ETHUSDT");
        RegimeBar[]? ethSeries = ethEntry.h1 != null && ethEntry.h1.Length >= 200
            ? RegimeClassifier.ClassifySeriesWithDuration(ethEntry.h1) : null;
        var session = new RegimeRouterSession(btcSeries, ethSeries, routerG);
        if (withBtcBars) session = session.WithBtcBars(btcH1ForGuard);
        Console.WriteLine($"  Router session: BTC {btcSeries.Length} bars  ETH {(ethSeries != null ? ethSeries.Length.ToString() : "none")} bars\n");
        return new RouterBootstrap(session, btcSeries);
    }
}