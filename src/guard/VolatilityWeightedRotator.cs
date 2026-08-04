namespace TradingGA;

// Volatility-weighted rotation layer: gradually shifts exposure between alts and BTC/ETH
// based on market stress signals (guard mult, ATR ratio, regime state).
//
// Safety score (0-1):
//   0 = low stress → 100% alts (max alpha)
//   1 = high stress → weighted BTC/ETH (capital preservation)
//
// Signals:
//   Guard mult:   DynamicGuardSession.GetMult() — stress/panic detection
//   ATR ratio:    DynamicGuardSession.GetAtrRatio() — volatility spike detection
//   Regime score: 0=Bull, 0.5=Ranging, 1=Bear/HighVol — directional risk
//
// Rotation is gradual (per-cycle rebalance) to avoid whipsaw and reduce slippage.
public class VolatilityWeightedRotator
{
    private readonly double _guardWeight;
    private readonly double _atrWeight;
    private readonly double _regimeWeight;
    private readonly double _btcShare;  // share of safe allocation (rest goes to ETH)
    private readonly double _rotationSpeed;  // 0-1, how fast to rebalance per cycle

    public VolatilityWeightedRotator(VolatilityWeightedRotatorGenotype g)
    {
        _guardWeight = g.GuardWeight;
        _atrWeight = g.AtrWeight;
        _regimeWeight = g.RegimeWeight;
        _btcShare = g.BtcShare;
        _rotationSpeed = g.RotationSpeed;
    }

    // Compute safety score from current market signals.
    // Returns 0 (all alts) to 1 (all BTC/ETH).
    public double ComputeSafetyScore(double guardMult, double atrRatio, MarketRegime regime)
    {
        // Guard signal: lower mult = more stress = higher safety
        double guardSignal = 1.0 - guardMult;

        // ATR signal: higher ratio = more vol = higher safety
        // Normalize: atrRatio typically 0.5-3.0, map to 0-1
        double atrSignal = Math.Clamp((atrRatio - 0.5) / 2.5, 0.0, 1.0);

        // Regime signal: Bear/HighVol = higher safety
        double regimeSignal = regime switch
        {
            MarketRegime.Bull => 0.0,
            MarketRegime.Ranging => 0.5,
            MarketRegime.Bear => 1.0,
            MarketRegime.HighVol => 1.0,
            _ => 0.5
        };

        // Weighted blend
        double totalWeight = _guardWeight + _atrWeight + _regimeWeight;
        if (totalWeight < 1e-9) return 0.0;

        return (_guardWeight * guardSignal +
                _atrWeight * atrSignal +
                _regimeWeight * regimeSignal) / totalWeight;
    }

    // Compute target allocation for a coin given safety score.
    // Returns (altShare, btcShare, ethShare) where altShare + btcShare + ethShare = 1.
    public (double AltShare, double BtcShare, double EthShare) ComputeAllocation(double safetyScore)
    {
        double altShare = 1.0 - safetyScore;
        double safeShare = safetyScore;
        double btcShare = safeShare * _btcShare;
        double ethShare = safeShare * (1.0 - _btcShare);
        return (altShare, btcShare, ethShare);
    }

    // Rebalance current exposure toward target allocation.
    // Returns new exposure weights (alt, btc, eth) after gradual rotation.
    public (double AltWeight, double BtcWeight, double EthWeight) Rebalance(
        double currentAltWeight, double currentBtcWeight, double currentEthWeight,
        double targetAltWeight, double targetBtcWeight, double targetEthWeight)
    {
        // Linear interpolation toward target
        double newAlt = currentAltWeight + _rotationSpeed * (targetAltWeight - currentAltWeight);
        double newBtc = currentBtcWeight + _rotationSpeed * (targetBtcWeight - currentBtcWeight);
        double newEth = currentEthWeight + _rotationSpeed * (targetEthWeight - currentEthWeight);

        // Normalize to sum to 1
        double total = newAlt + newBtc + newEth;
        if (total > 1e-9)
        {
            newAlt /= total;
            newBtc /= total;
            newEth /= total;
        }

        return (newAlt, newBtc, newEth);
    }
}

public record VolatilityWeightedRotatorGenotype(
    double GuardWeight,      // weight for guard mult signal
    double AtrWeight,        // weight for ATR ratio signal
    double RegimeWeight,     // weight for regime signal
    double BtcShare,         // share of safe allocation going to BTC (rest to ETH)
    double RotationSpeed     // 0-1, how fast to rebalance per cycle
);

public class VolatilityWeightedRotatorGenotypeDto
{
    public double GuardWeight { get; set; }
    public double AtrWeight { get; set; }
    public double RegimeWeight { get; set; }
    public double BtcShare { get; set; }
    public double RotationSpeed { get; set; }

    public VolatilityWeightedRotatorGenotype ToGenotype() =>
        new(GuardWeight, AtrWeight, RegimeWeight, BtcShare, RotationSpeed);
}
