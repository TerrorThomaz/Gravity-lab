namespace TradingGA;

// A uniform trade record across every strategy.
//
// RegimeBarsActive is carried here with a default rather than pushed into a per-strategy shape:
// DipLongGA and FadeLongGA consume it for regime-sustained filtering inside fitness, and it is
// meaningless for the grid family. One optional field beats two record types.
public readonly record struct SimTrade(
    DateTime Time,            // EXIT bar — every simulator in this repo records the exit here
    double   Return,          // percent, net of fees, slippage and funding
    string   Kind,
    DateTime EntryTime,
    double   EntryPrice,
    int      RegimeBarsActive = 0);

// The single point of contact for running a strategy.
//
// The value of this interface is not polymorphic dispatch — the backtests still call the
// concrete simulators directly for performance, and the strategies are genuinely different
// shapes (dual-timeframe signal strategies, h1-only grid ladders, a multi-level accumulator).
// The value is that it makes an INVARIANT TESTABLE:
//
//     for every registered simulator, every ExecContext field must be capable of changing
//     its output.
//
// Nothing in the suite today fails when a mechanism goes unwired. That is how eight of them
// accumulated — SizeMult, tradeGate, ExitOverrideMode, the rotator, the funding crowding gates,
// the *HighVolActive flags, HolmBonferroni and RunBlockBootstrap were all implemented, all
// compiled, all green, and none of them ran. A conformance test over this interface fails the
// moment a simulator ignores a field it is handed.
public interface IStrategySimulator
{
    // Portfolio label — must match PortfolioReplay.DefaultCaps and its direction sets, or the
    // concurrency caps silently fall back to int.MaxValue for this strategy.
    string Label { get; }

    // True = long, false = short. Direction is not cosmetic: it flips the funding sign, the
    // rotator's regime term, and every stop comparison.
    bool IsLong { get; }

    // Does this strategy read 15m execution candles, or is it h1-only? The grid family is h1-only
    // (GridSimulator takes no m15 span at all), so a conformance test must not hand it m15 data
    // and then assert the output changed.
    bool UsesM15 { get; }

    // Which context fields this simulator is expected to honour. A simulator that does not use
    // MaxLegs (single-position by construction) declares so, and the conformance test skips it
    // rather than reporting a false failure. Declaring a field here is a CLAIM the test verifies.
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
