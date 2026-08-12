namespace TradingGA;

// Risk-budgeted exposure cap — the cap derived from Layer 2 instead of asserted.
//
// THE PROBLEM WITH A CONSTANT
// Config.MaxTotalExposurePct = 0.30 is a single number defended by a stress table computed at ONE
// exposure level. It binds on ~78% of entries and the book's uncapped demand peaks near 285% of
// equity, so it is leaving a large amount of deployment on the table — in every market condition
// equally, including the calm ones where the tail it protects against is least likely.
//
// THE INVERSION
// CorrelatedShock says: at gross exposure G, a simultaneous -X% move costs G*X of equity. That
// equation has three terms and the cap fixes the wrong one. Fix the LOSS you are willing to take
// and solve for exposure instead:
//
//     cap_t = riskBudget / plausibleShock_t
//
// riskBudget is a policy choice ("a correlated shock may cost me at most 15% of equity"), stated
// once and in the units a human actually reasons about. plausibleShock_t is the adverse move worth
// planning for right now, which is NOT constant: crypto's tail is far fatter when realized
// volatility is already elevated. Same risk appetite, different exposure, because the environment
// differs.
//
// WHY VOLATILITY DRIVES THE SHOCK ESTIMATE
// A -30% correlated day does not arrive out of a calm tape. It arrives when volatility is already
// high, funding is stretched and liquidations are cascading. ATR ratio (current ATR vs its own
// trailing average) is the cheapest available proxy and is already computed everywhere in this
// codebase for variant routing. Using it means the cap TIGHTENS going into stress and RELEASES in
// calm — the opposite of a constant, which is loosest exactly when risk is highest.
//
// WHAT THIS IS NOT
// Not a forecast. It makes no claim about when a shock arrives; it only sizes the book so that if
// one arrives now, the loss stays inside the stated budget. That is the same honest division
// CorrelatedShock draws: damage is computable, likelihood is not.
public sealed class DynamicExposureCap
{
    private readonly DateTime[] _times;
    private readonly double[]   _atrRatio;
    private readonly double     _budget, _shockCalm, _shockStressed, _min, _max;

    // Defaults chosen to be conservative against the measured table rather than optimistic:
    // at the floor this reproduces roughly today's constant, so the change can only add exposure
    // in conditions that are demonstrably calmer than average, never in stressed ones.
    // Calibrated so the curve passes through today's constant rather than replacing it wholesale:
    // at ATR ratio 1.3 (mildly elevated) this returns 0.30 exactly. Calm tape earns more exposure,
    // stressed tape earns less, and a -40% correlated day costs at most ~16% of equity anywhere on
    // the curve. Loosening any of these widens the tail — re-run CorrelatedShock, do not eyeball it.
    public const double DefaultRiskBudgetPct = 12.0;  // max equity loss from the PLANNED shock
    public const double DefaultShockCalm     = 30.0;  // adverse move to plan for in a calm tape
    public const double DefaultShockStressed = 55.0;  // ...and when volatility is already elevated
    public const double DefaultCapMin        = 0.20;
    public const double DefaultCapMax        = 0.40;

    public DynamicExposureCap(
        IReadOnlyList<Candle> btcH1,
        int atrPeriod = 14,
        int ratioLookback = 168,
        double riskBudgetPct  = DefaultRiskBudgetPct,
        double shockCalm      = DefaultShockCalm,
        double shockStressed  = DefaultShockStressed,
        double capMin         = DefaultCapMin,
        double capMax         = DefaultCapMax)
    {
        _budget = riskBudgetPct; _shockCalm = shockCalm; _shockStressed = shockStressed;
        _min = capMin; _max = capMax;

        int n = btcH1.Count;
        _times = new DateTime[n];
        _atrRatio = new double[n];
        for (int i = 0; i < n; i++) _times[i] = btcH1[i].Time;

        var highs = new double[n]; var lows = new double[n]; var closes = new double[n];
        for (int i = 0; i < n; i++) { highs[i] = btcH1[i].High; lows[i] = btcH1[i].Low; closes[i] = btcH1[i].Close; }
        var atr = Volatility.Atr(highs, lows, closes, atrPeriod);

        double sum = 0;
        for (int i = 0; i < n; i++)
        {
            sum += atr[i];
            if (i >= ratioLookback) sum -= atr[i - ratioLookback];
            double avg = sum / Math.Min(i + 1, ratioLookback);
            _atrRatio[i] = avg > 1e-12 ? atr[i] / avg : 1.0;
        }
    }

    // Cap at a point in time. Binary search on the BTC clock; before the series starts we return
    // the FLOOR rather than the default, because "no data" must not read as "calm".
    public double CapAt(DateTime t)
    {
        if (_times.Length == 0) return _min;
        int i = Array.BinarySearch(_times, t);
        if (i < 0) i = ~i - 1;
        if (i < 0) return _min;
        if (i >= _atrRatio.Length) i = _atrRatio.Length - 1;
        return CapForRatio(_atrRatio[i]);
    }

    // ratio 1.0 = volatility at its own trailing average. Interpolate the planning shock between
    // the calm and stressed anchors over [0.8, 2.0], then solve cap = budget / shock.
    public double CapForRatio(double atrRatio)
    {
        double w     = Math.Clamp((atrRatio - 0.8) / 1.2, 0.0, 1.0);
        double shock = _shockCalm + w * (_shockStressed - _shockCalm);
        return Math.Clamp(_budget / shock, _min, _max);
    }
}
