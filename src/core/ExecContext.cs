namespace TradingGA;

// Everything a simulator needs beyond price data. Record with defaults — adding a field
// cannot change existing results. Prevents mechanism drift between backtest commands.
public readonly record struct ExecContext(
    // Real per-symbol funding, else interest-rate floor. WARNING: null does NOT mean "no funding".
    FundingRateSession? Funding = null,

    // Minimum-profit ratchet. Disabled by default.
    RatchetConfig Ratchet = default,

    // Max concurrent legs per coin. Raising requires Trade.Symbol populated.
    int MaxLegs = 1,

    // Router gating weight [0,1] for GA fitness. Keyed on exit bar. null = ungated.
    Func<DateTime, double>? TradeGate = null,

    // Skip entries on the crowded side of funding.
    bool CrowdingGate = false)
{
    public static readonly ExecContext Default = new();


    public static ExecContext WithFunding(FundingRateSession? f) => new(Funding: f);

    public ExecContext With(FundingRateSession? f) => this with { Funding = f };
}
