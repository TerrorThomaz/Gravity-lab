namespace TradingGA;

// Shifts exposure between alts and BTC/ETH based on stress signals (guard, ATR, regime).
// Safety score 0=low stress (100% alts) to 1=high stress (BTC/ETH). Gradual per-cycle rebalance.
public class VolatilityWeightedRotator
{
    private readonly double _guardWeight;
    private readonly double _atrWeight;
    private readonly double _regimeWeight;
    private readonly double _btcStressWeight;
    private readonly bool _invert;
    private readonly double _minAltShare;
    private readonly double _rotationDeadband;
    private readonly double _btcShare;  // share of safe allocation (rest goes to ETH)
    private readonly double _rotationSpeed;  // 0-1, how fast to rebalance per cycle

    public VolatilityWeightedRotator(VolatilityWeightedRotatorGenotype g)
    {
        _guardWeight = g.GuardWeight;
        _atrWeight = g.AtrWeight;
        _regimeWeight = g.RegimeWeight;
        _btcStressWeight = g.BtcStressWeight;
        _invert = g.InvertRotation > 0;
        _minAltShare = g.MinAltShare;
        _rotationDeadband = g.RotationDeadband;
        _btcShare = g.BtcShare;
        _rotationSpeed = g.RotationSpeed;
    }

    // Safety score 0-1. isLong inverts the regime term — Bear is danger for longs, opportunity for shorts.
    public static double BtcStress(double btcReturnPct, double fullStressPct = 3.0)
        => btcReturnPct >= 0 ? 0.0 : Math.Clamp(-btcReturnPct / Math.Max(0.1, fullStressPct), 0.0, 1.0);

    // BTC drawdown intensity 0-1. Alts have 1.463 beta to BTC on down days — proportional signal.
    public double ComputeSafetyScore(double guardMult, double atrRatio, MarketRegime regime,
                                     bool isLong = true, double btcReturnPct = 0.0)
    {

        double guardSignal = 1.0 - guardMult;


        double atrSignal = Math.Clamp((atrRatio - 0.5) / 2.5, 0.0, 1.0);

        // HighVol = 1.0 for both sides (direction-neutral danger).
        double regimeSignal = regime switch
        {
            MarketRegime.Bull => isLong ? 0.0 : 1.0,
            MarketRegime.Bear => isLong ? 1.0 : 0.0,
            MarketRegime.Ranging => 0.5,
            MarketRegime.HighVol => 1.0,
            _ => 0.5
        };

        // BTC-drawdown signal. Direction-aware — short book wants MORE alt exposure when BTC falls.
        double btcSignal = isLong ? BtcStress(btcReturnPct) : 0.0;

        double totalWeight = _guardWeight + _atrWeight + _regimeWeight + _btcStressWeight;
        if (totalWeight < 1e-9) return 0.0;

        return (_guardWeight * guardSignal +
                _atrWeight * atrSignal +
                _regimeWeight * regimeSignal +
                _btcStressWeight * btcSignal) / totalWeight;
    }

    // Target allocation. MinAltShare is a floor — strategies need coins to trade.
    // Inverted mode (gene > 0): deploy INTO alts on stress, park in BTC when calm.
    public (double AltShare, double BtcShare, double EthShare) ComputeAllocation(double safetyScore)
    {
        double raw = _invert ? safetyScore : 1.0 - safetyScore;
        double altShare = Math.Max(_minAltShare, raw);
        double safeShare = 1.0 - altShare;
        double btcShare = safeShare * _btcShare;
        double ethShare = safeShare * (1.0 - _btcShare);
        return (altShare, btcShare, ethShare);
    }

    // Deadband: don't reallocate unless target moves more than this. Damps frequency, not size.
    public double BtcShareOfSafe => _btcShare;

    public bool ShouldReallocate(double currentAltShare, double targetAltShare)
        => Math.Abs(targetAltShare - currentAltShare) > _rotationDeadband;

    // Ease toward target at RotationSpeed, then check deadband. Order matters.
    public double StepAltShare(double currentAltShare, double targetAltShare)
    {
        if (!ShouldReallocate(currentAltShare, targetAltShare)) return currentAltShare;
        return currentAltShare + _rotationSpeed * (targetAltShare - currentAltShare);
    }

    // Signal weights for reporting.
    public (double Guard, double Atr, double Regime, double BtcStress) SignalMix()
    {
        double t = _guardWeight + _atrWeight + _regimeWeight + _btcStressWeight;
        return t < 1e-9 ? (0, 0, 0, 0)
                        : (_guardWeight / t, _atrWeight / t, _regimeWeight / t, _btcStressWeight / t);
    }

    // Gradual rotation toward target.
    public (double AltWeight, double BtcWeight, double EthWeight) Rebalance(
        double currentAltWeight, double currentBtcWeight, double currentEthWeight,
        double targetAltWeight, double targetBtcWeight, double targetEthWeight)
    {

        double newAlt = currentAltWeight + _rotationSpeed * (targetAltWeight - currentAltWeight);
        double newBtc = currentBtcWeight + _rotationSpeed * (targetBtcWeight - currentBtcWeight);
        double newEth = currentEthWeight + _rotationSpeed * (targetEthWeight - currentEthWeight);


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
    double BtcStressWeight,  // weight for the BTC-drawdown signal — see BtcStress()
    double InvertRotation,   // >0 = deploy INTO alts on stress and park in BTC when calm
    double MinAltShare,      // floor: never rotate ALL alt capital out — strategies still need coins
    double RotationDeadband, // don't reallocate unless the target moves more than this
    double BtcShare,         // share of safe allocation going to BTC (rest to ETH)
    double RotationSpeed     // 0-1, how fast to rebalance per cycle
);

public class VolatilityWeightedRotatorGenotypeDto
{
    public double GuardWeight { get; set; }
    public double AtrWeight { get; set; }
    public double RegimeWeight { get; set; }
    // Nullable: pre-existing genotypes load as 0 (signal off).
    public double? BtcStressWeight { get; set; }
    // Nullable with legacy defaults (0 floor, 0 deadband).
    public double? InvertRotation { get; set; }
    public double? MinAltShare { get; set; }
    public double? RotationDeadband { get; set; }
    public double BtcShare { get; set; }
    public double RotationSpeed { get; set; }

    public VolatilityWeightedRotatorGenotype ToGenotype() =>
        new(GuardWeight, AtrWeight, RegimeWeight, BtcStressWeight ?? 0.0,
            InvertRotation ?? 0.0, MinAltShare ?? 0.0, RotationDeadband ?? 0.0, BtcShare, RotationSpeed);
}
