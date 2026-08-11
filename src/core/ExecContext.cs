namespace TradingGA;

// Everything a simulator needs beyond price data, in one object.
//
// WHY THIS EXISTS
// Mechanisms in this repo have a strong track record of being built and never connected:
// SizeMult, tradeGate, ExitOverrideMode, the volatility rotator, the funding crowding gates,
// the *HighVolActive flags, HolmBonferroni and RunBlockBootstrap were all implemented, all
// compiled, all passed the suite, and none of them ran. Two distinct failure shapes produced
// that, and a context object addresses both:
//
//   1. MECHANISM DRIFT. combinedbacktest, oosbacktest and papertrade each construct their own
//      call arguments, so they run DIFFERENT models. Discovered mid-verification: oosbacktest
//      had no ratchet support at all, which meant a val-vs-OOS comparison was partly measuring
//      configuration difference rather than generalisation. With one context built per command
//      and handed to everything, the three paths cannot silently diverge.
//
//   2. WIRING COST. Adding one mechanism previously meant editing N simulators x M call sites.
//      Threading the ratchet took edits across four simulators and three command files, and
//      introduced a real bug (a missing per-trade reset) that only surfaced because it produced
//      a physically impossible 100% win rate. A field on this record is one edit.
//
// A record with defaults, deliberately: `default` reproduces current production behaviour
// exactly, so adding a field cannot change any existing result. That is what makes the
// incremental migration safe on a codebase whose test suite covers no strategy P&L.
public readonly record struct ExecContext(
    // Real per-symbol funding when the cache covers the trade, else the interest-rate floor.
    // NOTE: null does NOT mean "no funding" — FundingRateSession.PnlPct still charges the floor.
    FundingRateSession? Funding = null,

    // Minimum-profit ratchet. Disabled by default; see src/core/ExitRatchet.cs for why it is
    // applied to the hard stop rather than subordinated to the trailing stop.
    RatchetConfig Ratchet = default,

    // Max concurrent legs per coin. 1 is every signal simulator's historical behaviour (a single
    // `inTrade` flag). Raising it requires PortfolioReplay.Trade.Symbol to be populated, or
    // per-coin concentration is invisible to the risk layer.
    int MaxLegs = 1,

    // Router gating weight in [0,1] applied to a trade's contribution during GA fitness, so the
    // strategy is selected under the gating it will actually be deployed with. Keyed on the EXIT
    // bar, matching how the backtests gate. null = ungated.
    Func<DateTime, double>? TradeGate = null,

    // Skip entries that would join the crowded side of funding. Funding is the only
    // microstructure signal already present in the data this repo fetches.
    bool CrowdingGate = false)
{
    public static readonly ExecContext Default = new();

    // Convenience for the common backtest case: real funding, everything else at production
    // defaults. Keeps call sites from spelling out named arguments for a single field.
    public static ExecContext WithFunding(FundingRateSession? f) => new(Funding: f);

    public ExecContext With(FundingRateSession? f) => this with { Funding = f };
}
