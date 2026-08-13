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

    // Compute safety score from current market signals.
    // Returns 0 (all alts) to 1 (all BTC/ETH).
    // isLong: which side of the book this exposure is on. "Safety" means risk to THIS book,
    // so the regime term has to invert — a Bear regime is danger for a long position and
    // opportunity for a short one.
    //
    // Measured cost of getting this wrong: the direction-blind version scored -35.7pp of
    // return in combinedbacktest AND pushed drawdown UP from 2.5% to 4.0%. It cut only ~13.8%
    // of exposure on average, but the val window is 42.6% Bear, so it spent that budget
    // throttling FadeShort and RipShort — the two strategies that exist to earn in Bear —
    // and left the long book unhedged. A hedge that costs return and raises DD is strictly
    // worse than no hedge, which is what made this a bug rather than a tuning choice.
    // BTC drawdown intensity as a 0-1 signal, for callers that want the rotator to react to BTC
    // itself rather than only to the regime label.
    //
    // Measured justification (83 coins, daily): alts run a 1.463 beta to BTC on DOWN days against
    // 1.202 on UP days — a +0.258 asymmetry, positive in 75/83 coins. On the 212 BTC down days in
    // cache, the median alt UNDERPERFORMED BTC by 0.864%/day. So a BTC decline is not merely
    // "unsafe" in the abstract: it is the specific condition under which alt exposure is worth
    // less than the same capital held in BTC, and by a margin ~8x the round-trip rotation cost.
    //
    // Expressed as a magnitude rather than a boolean so the rotation is proportional to how hard
    // BTC is falling, matching the proportional structure of the beta asymmetry itself.
    public static double BtcStress(double btcReturnPct, double fullStressPct = 3.0)
        => btcReturnPct >= 0 ? 0.0 : Math.Clamp(-btcReturnPct / Math.Max(0.1, fullStressPct), 0.0, 1.0);

    // btcReturnPct: BTC's recent move. Defaults to 0 (no stress) so existing call sites are
    // unchanged and the gene is simply inert until a caller supplies it — the same "reachable but
    // opt-in" shape used elsewhere, so this cannot silently alter current behaviour.
    public double ComputeSafetyScore(double guardMult, double atrRatio, MarketRegime regime,
                                     bool isLong = true, double btcReturnPct = 0.0)
    {
        // Guard signal: lower mult = more stress = higher safety
        double guardSignal = 1.0 - guardMult;

        // ATR signal: higher ratio = more vol = higher safety
        // Normalize: atrRatio typically 0.5-3.0, map to 0-1
        double atrSignal = Math.Clamp((atrRatio - 0.5) / 2.5, 0.0, 1.0);

        // Regime signal: Bear/HighVol = higher safety
        // HighVol stays 1.0 for both sides: a volatility blow-up is direction-neutral danger.
        double regimeSignal = regime switch
        {
            MarketRegime.Bull => isLong ? 0.0 : 1.0,
            MarketRegime.Bear => isLong ? 1.0 : 0.0,
            MarketRegime.Ranging => 0.5,
            MarketRegime.HighVol => 1.0,
            _ => 0.5
        };

        // Weighted blend
        // BTC-drawdown signal. This is the one that fires on the days the alt/BTC asymmetry
        // actually pays: the regime label says "Bear" long after BTC has moved and says nothing
        // at all about a -5% day inside a Bull regime, which is precisely when alts run their
        // 1.463 down-beta. Direction-aware — a SHORT book wants MORE alt exposure when BTC is
        // falling, not less, so the signal inverts.
        double btcSignal = isLong ? BtcStress(btcReturnPct) : 0.0;

        double totalWeight = _guardWeight + _atrWeight + _regimeWeight + _btcStressWeight;
        if (totalWeight < 1e-9) return 0.0;

        return (_guardWeight * guardSignal +
                _atrWeight * atrSignal +
                _regimeWeight * regimeSignal +
                _btcStressWeight * btcSignal) / totalWeight;
    }

    // Compute target allocation for a coin given safety score.
    // Returns (altShare, btcShare, ethShare) where altShare + btcShare + ethShare = 1.
    // MinAltShare is a FLOOR, not a target. Rotating 100% of alt capital into BTC would starve the
    // strategies that need coins to trade at all — and the short strategies specifically WANT alt
    // exposure during a BTC drawdown, since that is when alts run their 1.463 down-beta. So the
    // rotation takes a PORTION and leaves working capital behind.
    // ── INVERTED MODE ────────────────────────────────────────────────────────────────────
    // The classic reading — "stress means danger, rotate to safety" — is measurably wrong for this
    // book, because the router has ALREADY removed the exposure that stress hurts. Measured on the
    // validation window, per-trade return by BTC-stress bucket:
    //
    //     calm       n=1956  all +2.08%   longs +2.16% (1861)   shorts +0.39% (  95)
    //     mild       n= 708  all +0.92%   longs +0.59% ( 608)   shorts +2.87% ( 100)
    //     stressed   n= 387  all +1.36%   longs -0.29% (  92)   shorts +1.88% ( 295)
    //     max stress n= 185  all +5.80%   longs -4.45% (  20)   shorts +7.04% ( 165)
    //
    // Max stress is the book's BEST regime (+5.80%/trade), and 89% of those trades are shorts
    // harvesting the 1.463 alt down-beta. Only 20 long trades survive the router's gate into that
    // bucket, so there are no big losses left for a rotation to shrink — the standard mode was
    // simply shrinking the most profitable trades in the dataset.
    //
    // Inverted: deploy INTO alts as stress rises, and park in BTC when calm. Calm is where longs
    // are numerous and self-sustaining (+2.16% on 1861 trades), so idle capital there is better
    // held in an appreciating asset than in more marginal alt exposure.
    //
    // A GENE, not a hardcoded flip: the standard direction may still be right on other windows or
    // after a retrain, and this repo has been wrong about the rotator three times already.
    public (double AltShare, double BtcShare, double EthShare) ComputeAllocation(double safetyScore)
    {
        double raw = _invert ? safetyScore : 1.0 - safetyScore;
        double altShare = Math.Max(_minAltShare, raw);
        double safeShare = 1.0 - altShare;
        double btcShare = safeShare * _btcShare;
        double ethShare = safeShare * (1.0 - _btcShare);
        return (altShare, btcShare, ethShare);
    }

    // Should we act on this target at all?
    //
    // Without a deadband the rotator tracks the signal tick-by-tick: measured at 2065 allocation
    // changes costing 22.2pp against a 6.1pp benchmark sleeve. The asymmetry it is capturing pays
    // over a HELD position (breakeven ~0.1 days), not over continuous re-tracking, so paying the
    // round trip on every wobble forfeits the edge to turnover. RotationSpeed damps the SIZE of
    // each move; this damps the FREQUENCY, which is the term that was actually binding.
    public double BtcShareOfSafe => _btcShare;

    public bool ShouldReallocate(double currentAltShare, double targetAltShare)
        => Math.Abs(targetAltShare - currentAltShare) > _rotationDeadband;

    // One step of the actual rotation: deadband first, then ease toward the target at
    // RotationSpeed. Returns the new alt share.
    //
    // ORDER MATTERS AND WAS WRONG. The deadband was being tested against the RAW target while
    // the allocation then jumped straight to it, because Rebalance() — the method that eases
    // toward a target — was never called from anywhere. RotationSpeed was a trained gene feeding
    // dead code, so every rotation was bang-bang between the extremes of a bimodal signal, and a
    // deadband cannot suppress a move that is always full-range. Easing first makes the deadband
    // meaningful: small target changes produce small steps, which the deadband then absorbs.
    public double StepAltShare(double currentAltShare, double targetAltShare)
    {
        if (!ShouldReallocate(currentAltShare, targetAltShare)) return currentAltShare;
        return currentAltShare + _rotationSpeed * (targetAltShare - currentAltShare);
    }

    // Signal weights, for reporting. Kept here so a display cannot drift out of sync with the
    // genotype — the old "Signal mix" line summed only guard+atr+regime and reported regime=99.98%
    // on a genotype whose dominant gene was BtcStressWeight=0.917.
    public (double Guard, double Atr, double Regime, double BtcStress) SignalMix()
    {
        double t = _guardWeight + _atrWeight + _regimeWeight + _btcStressWeight;
        return t < 1e-9 ? (0, 0, 0, 0)
                        : (_guardWeight / t, _atrWeight / t, _regimeWeight / t, _btcStressWeight / t);
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
    // Nullable: a rotator genotype saved before this gene existed must load as 0 (signal off),
    // which reproduces the previous behaviour exactly rather than injecting an untrained weight.
    public double? BtcStressWeight { get; set; }
    // Nullable with legacy-reproducing defaults: 0 alt floor = old all-or-nothing rotation,
    // 0 deadband = old tick-by-tick retracking. A pre-existing genotype loads unchanged.
    public double? InvertRotation { get; set; }
    public double? MinAltShare { get; set; }
    public double? RotationDeadband { get; set; }
    public double BtcShare { get; set; }
    public double RotationSpeed { get; set; }

    public VolatilityWeightedRotatorGenotype ToGenotype() =>
        new(GuardWeight, AtrWeight, RegimeWeight, BtcStressWeight ?? 0.0,
            InvertRotation ?? 0.0, MinAltShare ?? 0.0, RotationDeadband ?? 0.0, BtcShare, RotationSpeed);
}
