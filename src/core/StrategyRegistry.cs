namespace TradingGA;

// Thin adapters over the concrete simulators, plus a registry the conformance test enumerates.
//
// The backtests keep calling the concrete simulators directly — these adapters exist so that a
// test can iterate every strategy uniformly and assert each one honours the ExecContext fields
// it claims to. Adding a strategy without registering it here is itself caught, because the
// registry count is asserted against the number of genotype files the commands load.
public static class StrategyRegistry
{
    private sealed class FadeShortAdapter : IStrategySimulator
    {
        private readonly FadeShortGenotype _g;
        public FadeShortAdapter(FadeShortGenotype g) => _g = g;
        public string Label => "swing";
        public bool IsLong => false;
        public bool UsesM15 => true;
        public ExecFields Honours => ExecFields.Funding | ExecFields.Ratchet;
        public IReadOnlyList<SimTrade> Run(ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, in ExecContext ctx)
            => FadeShortSimulator.GetFadeShortReturns(_g, h1, m15, ctx)
                .Select(t => new SimTrade(t.Time, t.Return, t.Kind, t.EntryTime, t.EntryPrice)).ToList();
    }

    private sealed class SwingLongAdapter : IStrategySimulator
    {
        private readonly SwingLongGenotype _g;
        public SwingLongAdapter(SwingLongGenotype g) => _g = g;
        public string Label => "swing_long";
        public bool IsLong => true;
        public bool UsesM15 => true;
        public ExecFields Honours => ExecFields.Funding | ExecFields.Ratchet;
        public IReadOnlyList<SimTrade> Run(ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, in ExecContext ctx)
            => SwingLongSimulator.GetSwingLongReturns(_g, h1, m15, ctx)
                .Select(t => new SimTrade(t.Time, t.Return, t.Kind, t.EntryTime, t.EntryPrice)).ToList();
    }

    private sealed class DipLongAdapter : IStrategySimulator
    {
        private readonly DipLongGenotype _g;
        public DipLongAdapter(DipLongGenotype g) => _g = g;
        public string Label => "diplong";
        public bool IsLong => true;
        public bool UsesM15 => true;
        // The only multi-leg strategy today; the rest are single-position by construction.
        public ExecFields Honours => ExecFields.All;
        public IReadOnlyList<SimTrade> Run(ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, in ExecContext ctx)
            => DipLongSimulator.GetDipLongReturns(_g, h1, m15, ctx)
                .Select(t => new SimTrade(t.Time, t.Return, t.Kind, t.EntryTime, t.EntryPrice, t.RegimeBarsActive)).ToList();
    }

    private sealed class FadeLongAdapter : IStrategySimulator
    {
        private readonly FadeLongGenotype _g;
        public FadeLongAdapter(FadeLongGenotype g) => _g = g;
        public string Label => "fadelong";
        public bool IsLong => true;
        public bool UsesM15 => true;
        public ExecFields Honours => ExecFields.Funding | ExecFields.Ratchet;
        public IReadOnlyList<SimTrade> Run(ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, in ExecContext ctx)
            => FadeLongSimulator.GetFadeLongReturns(_g, h1, m15, ctx)
                .Select(t => new SimTrade(t.Time, t.Return, t.Kind, t.EntryTime, t.EntryPrice, t.RegimeBarsActive)).ToList();
    }

    // Grid is h1-only — GridSimulator.GetGridSessionReturns takes no m15 span at all — and has no
    // ratchet path, so it declares Funding only. Declaring less is not a free pass: the test also
    // checks that a simulator does NOT silently ignore something it claims to honour.
    private sealed class GridAdapter : IStrategySimulator
    {
        private readonly GridGenotype _g;
        public GridAdapter(GridGenotype g) => _g = g;
        public string Label => "grid";
        public bool IsLong => true;
        public bool UsesM15 => false;
        public ExecFields Honours => ExecFields.Funding;
        public IReadOnlyList<SimTrade> Run(ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, in ExecContext ctx)
            => GridSimulator.GetGridSessionReturns(_g, h1, ctx.Funding)
                .Select(t => new SimTrade(t.Time, t.Return, t.Kind, t.EntryTime, t.EntryPrice)).ToList();
    }

    public static IReadOnlyList<IStrategySimulator> All(
        FadeShortGenotype fs, SwingLongGenotype sl, DipLongGenotype dl,
        FadeLongGenotype fl, GridGenotype grid) =>
        new IStrategySimulator[]
        {
            new FadeShortAdapter(fs),
            new SwingLongAdapter(sl),
            new DipLongAdapter(dl),
            new FadeLongAdapter(fl),
            new GridAdapter(grid),
        };
}
