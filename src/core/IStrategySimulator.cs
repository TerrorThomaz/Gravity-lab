namespace TradingGA;

// Uniform trade record. RegimeBarsActive: consumed by DipLong/FadeLong fitness, meaningless for grid.
public readonly record struct SimTrade(
    DateTime Time,            // EXIT bar — every simulator in this repo records the exit here
    double   Return,          // percent, net of fees, slippage and funding
    string   Kind,
    DateTime EntryTime,
    double   EntryPrice,
    int      RegimeBarsActive = 0);

// Conformance test interface. Invariant: every ExecContext field must change output for simulators
// that declare they honour it. Backtests call concrete simulators directly for performance.
// Lesson: SizeMult, tradeGate, rotator, etc. were all implemented and compiled but never wired.
public interface IStrategySimulator
{
    // Must match PortfolioReplay.DefaultCaps or cap falls back to int.MaxValue.
    string Label { get; }

    // Direction flips funding sign, rotator regime term, and stop comparisons.
    bool IsLong { get; }

    // Grid family is h1-only (takes no m15 span).
    bool UsesM15 { get; }

    // Which ExecContext fields this simulator honours. Declaring a field is a claim the test verifies.
    ExecFields Honours { get; }

    IReadOnlyList<SimTrade> Run(ReadOnlySpan<Candle> h1, ReadOnlySpan<Candle> m15, in ExecContext ctx);
}

[Flags]
public enum ExecFields
{
    None    = 0,
    Funding = 1 << 0,
    Ratchet = 1 << 1,
    MaxLegs = 1 << 2,
    All     = Funding | Ratchet | MaxLegs,
}
