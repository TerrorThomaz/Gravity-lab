namespace TradingGA;

// Rip-short genotype — regime-gated bear trend-continuation strategy.
//
// Direction-mirror of DipLong. DipLong buys RSI dips in established uptrends;
// RipShort shorts relief rallies ("rips") in established downtrends. Both are
// trend-continuation plays, opposite in direction and regime.
//
// Entry (1h setup + 15m trigger):
//   1. Bear regime  — close < RegimeLongEma AND RegimeLongEma is falling
//   2. Trend gate   — close < EmaPeriod EMA AND ADX ≥ threshold
//   3. Rally setup  — RSI(7) ≥ RsiRallyThreshold (relief rally within downtrend)
//   4. Bearish BoS  — 15m close < previous 15m low (committed sellers)
//
// Stop: recentSwingHigh + StopLossAtrMult × h4ATR (highest high in last 20 h1 bars)
//
// Fixed: RsiPeriod=7, AdxPeriod=7, SwingHighLookback=20 (consistent with siblings).
public class RipShortGenotype
{
    // ── Regime genes ──────────────────────────────────────────────────────────────
    public int    RegimeLongEmaPeriod { get; set; }   // 100–500  bear market gate EMA
    public int    RegimeSlopeLookback { get; set; }   // 10–60    bars to measure EMA slope direction
    public int    EmaPeriod           { get; set; }   // 20–100   short-term trend EMA
    public double AdxThreshold        { get; set; }   // 15–35    trend strength gate

    // ── Entry signal genes ────────────────────────────────────────────────────────
    public double RsiRallyThreshold { get; set; }   // 40–60  RSI floor for relief-rally entry (short the bounce)

    // ── Exit genes ────────────────────────────────────────────────────────────────
    public double StopLossAtrMult           { get; set; }   // 0.5–2.0   ATR buffer above recent swing high
    public double TakeProfitAtrMult         { get; set; }   // 2.0–15.0  fixed profit target below entry
    public double TrailingActivationAtrMult { get; set; }   // 1.5–5.0   arm trail after this profit
    public double TrailingStopAtrMult       { get; set; }   // 1.0–5.0   trail distance from trough
    // 10–160 h1 bars before forced exit. Widened from 10–60 on measured evidence: the trained
    // value sat at 53 against a ceiling of 60, median realised hold was 48h, and trade anatomy
    // showed hold time is IDENTICAL for the best and worst deciles (47h vs 48h) — i.e. nearly
    // every trade was closing on the TIME limit rather than on stop or target. MaxHold was the
    // dominant exit while being bounded below where the GA wanted it.
    public int    MaxHoldCandles            { get; set; }   // 10–160    h1 bars before forced exit
    public double PositionSizePct           { get; set; }   // 0.01–0.05 fraction of capital per trade

    // ── Time-decay stop genes ────────────────────────────────────────────────────
    // After TimeStopBars h1 bars, the max tolerated adverse move from entry tightens linearly
    // from TimeStopLossPct (at bar TimeStopBars) down to 0% (at bar MaxHoldCandles).
    // Prevents slow-bleeding positions that never hit the hard stop.
    public int    TimeStopBars     { get; set; }   // 5–50    h1 bars before time-decay activates
    public double TimeStopLossPct  { get; set; }   // 0.02–0.25  max adverse move allowed at TimeStopBars (ratio)

    // ── Regime-conditional fitness gene ──────────────────────────────────────────
    public int    RegimeSustainedBars  { get; set; }   // 10–100    min consecutive bear-regime h1 bars before a trade counts

    // ── Regime-adaptive parameters (PILOT) ──────────────────────────────────────────
    // Lets the strategy scale its own entry threshold and stop with how ESTABLISHED the bear
    // regime is, instead of using one fixed setting from a fresh downturn to a mature one.
    //
    // The question these exist to answer is whether the GA WANTS this. RegimeAdaptStrength = 0
    // is a bit-for-bit no-op — not "approximately neutral", exactly the old code path — so if
    // adaptation does not pay, selection drives the gene to zero and that is the answer. A gene
    // that cannot be switched off would only tell us the GA found SOME value for it.
    //
    // Signed on purpose. Positive tightens when the regime is young (stricter RSI, tighter stop);
    // negative loosens. Which direction helps is exactly what is unknown, so it is not assumed.
    public int    RegimeAdaptPivotBars { get; set; }   // 20–300    bars of bear regime counted as "mature"
    public double RegimeAdaptStrength  { get; set; }   // -0.4–0.4  0 = disabled (exact no-op)

    public double Fitness { get; set; } = double.MinValue;

    // ── Seeded initialisation ────────────────────────────────────────────────────
    // Probability that a seeded Random* draw returns a LOOSE mutant of the seed
    // rather than an independent uniform draw. Matches RegimeRouterGenotype.Random,
    // which sits on the identical GA Run skeleton; one number across the whole
    // strategy suite keeps the initial-diversity mix comparable between GAs.
    //
    // Resulting population mix at popSize 80 with a seed (the GA's Run block
    // installs the clamped seed at index 0 and tight rate-0.25 mutants at 1..16):
    //   1  exact seed
    //   16 tight  (rate 0.25) mutants  — the seed's immediate neighbourhood
    //   ~19 loose (rate 0.50) mutants  — 30% of the remaining 63 slots
    //   ~44 fully independent random genotypes
    // ≈ 45% anchored on the incumbent, ≈ 55% genuine exploration. Before this
    // change the last 63 slots were byte-identical copies of the seed, leaving
    // at most 17 distinct starting points (78.75% duplicates) — a hill-climb,
    // not a GA.
    private const double SeedMutantProbability = 0.3;

    public static RipShortGenotype Random(System.Random rng, RipShortGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBounds().Mutate(rng, 0.5);

        return new()
        {
            RegimeLongEmaPeriod = rng.Next(100, 501),
            RegimeSlopeLookback = rng.Next(10, 61),
            EmaPeriod           = rng.Next(20, 101),
            AdxThreshold        = 15.0 + rng.NextDouble() * 20.0,
            RsiRallyThreshold   = 40.0 + rng.NextDouble() * 20.0,
            StopLossAtrMult           = 0.5 + rng.NextDouble() * 1.5,
            TakeProfitAtrMult         = 2.0 + rng.NextDouble() * 13.0,
            TrailingActivationAtrMult = 1.5 + rng.NextDouble() * 3.5,
            TrailingStopAtrMult       = 1.0 + rng.NextDouble() * 4.0,
            MaxHoldCandles            = rng.Next(10, 161),
            PositionSizePct           = 0.01 + rng.NextDouble() * 0.04,
            TimeStopBars              = rng.Next(5, 51),
            TimeStopLossPct           = 0.02 + rng.NextDouble() * 0.23,
            RegimeSustainedBars  = rng.Next(10, 101),
            RegimeAdaptPivotBars = (int)(Lo(IdxAdaptPivot) + rng.NextDouble() * (Hi(IdxAdaptPivot) - Lo(IdxAdaptPivot))),
            RegimeAdaptStrength  = Lo(IdxAdaptStrength) + rng.NextDouble() * (Hi(IdxAdaptStrength) - Lo(IdxAdaptStrength)),
        };
    }

    public static RipShortGenotype Crossover(RipShortGenotype a, RipShortGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            RegimeLongEmaPeriod = Pick(a.RegimeLongEmaPeriod, b.RegimeLongEmaPeriod),
            RegimeSlopeLookback = Pick(a.RegimeSlopeLookback, b.RegimeSlopeLookback),
            EmaPeriod           = Pick(a.EmaPeriod,           b.EmaPeriod),
            AdxThreshold        = Pick(a.AdxThreshold,        b.AdxThreshold),
            RsiRallyThreshold   = Pick(a.RsiRallyThreshold,   b.RsiRallyThreshold),
            StopLossAtrMult           = Pick(a.StopLossAtrMult,           b.StopLossAtrMult),
            TakeProfitAtrMult         = Pick(a.TakeProfitAtrMult,         b.TakeProfitAtrMult),
            TrailingActivationAtrMult = Pick(a.TrailingActivationAtrMult, b.TrailingActivationAtrMult),
            TrailingStopAtrMult       = Pick(a.TrailingStopAtrMult,       b.TrailingStopAtrMult),
            MaxHoldCandles            = Pick(a.MaxHoldCandles,            b.MaxHoldCandles),
            PositionSizePct           = Pick(a.PositionSizePct,           b.PositionSizePct),
            TimeStopBars              = Pick(a.TimeStopBars,              b.TimeStopBars),
            TimeStopLossPct           = Pick(a.TimeStopLossPct,           b.TimeStopLossPct),
            RegimeSustainedBars  = Pick(a.RegimeSustainedBars,  b.RegimeSustainedBars),
            RegimeAdaptPivotBars = Pick(a.RegimeAdaptPivotBars, b.RegimeAdaptPivotBars),
            RegimeAdaptStrength  = Pick(a.RegimeAdaptStrength,  b.RegimeAdaptStrength),
        };
    }

    public RipShortGenotype Mutate(System.Random rng, double rate)
    {
        double Nudge(double val, double min, double max, double scale)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + (rng.NextDouble() - 0.5) * scale, min, max);
        }
        int NudgeInt(int val, int min, int max, int step)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + rng.Next(-step, step + 1), min, max);
        }
        return new RipShortGenotype
        {
            RegimeLongEmaPeriod = NudgeInt(RegimeLongEmaPeriod, 100, 500, 30),
            RegimeSlopeLookback = NudgeInt(RegimeSlopeLookback,  10,  60,  5),
            EmaPeriod           = NudgeInt(EmaPeriod,            20, 100, 10),
            AdxThreshold        = Nudge(AdxThreshold,           15.0, 35.0, 3.0),
            RsiRallyThreshold   = Nudge(RsiRallyThreshold,      40.0, 60.0, 3.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           0.5,  2.0, 0.3),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         2.0, 15.0, 2.0),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.5,  5.0, 0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       1.0,  5.0, 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles, 10, 160, 10),
            PositionSizePct           = Nudge(PositionSizePct,   0.01, 0.05, 0.005),
            TimeStopBars              = NudgeInt(TimeStopBars,    5, 50, 5),
            TimeStopLossPct           = Nudge(TimeStopLossPct,   0.02, 0.25, 0.03),
            RegimeSustainedBars  = NudgeInt(RegimeSustainedBars, 10, 100, 10),
            RegimeAdaptPivotBars = NudgeInt(RegimeAdaptPivotBars, (int)Lo(IdxAdaptPivot), (int)Hi(IdxAdaptPivot), 30),
            RegimeAdaptStrength  = Nudge(RegimeAdaptStrength, Lo(IdxAdaptStrength), Hi(IdxAdaptStrength), 0.15),
        };
    }

    public RipShortGenotype ClampToBounds() => new()
    {
        RegimeLongEmaPeriod = Math.Clamp(RegimeLongEmaPeriod, 100, 500),
        RegimeSlopeLookback = Math.Clamp(RegimeSlopeLookback,  10,  60),
        EmaPeriod           = Math.Clamp(EmaPeriod,            20, 100),
        AdxThreshold        = Math.Clamp(AdxThreshold,        15.0, 35.0),
        RsiRallyThreshold   = Math.Clamp(RsiRallyThreshold,   40.0, 60.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.5,  2.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         2.0, 15.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.5,  5.0),
        // The trail must stay BELOW its own activation distance, else arming it still permits a
        // LOSS: measured live at activation 2.13A / trail 3.20A, so a trade could move 2.13A in
        // favour, arm the trail, and still stop out 1.07A down. The other four strategies satisfy
        // this by accident of their trained values; RipShort — which carries the worst tail — did
        // not. Enforced here rather than left to the GA, because it is a geometric invariant, not
        // a preference: TrailingActivationAtrMult is clamped first, so this reads the clamped value.
        TrailingStopAtrMult       = Math.Min(Math.Clamp(TrailingStopAtrMult, 1.0, 5.0),
                                             Math.Clamp(TrailingActivationAtrMult, 1.5, 5.0) * 0.9),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,            10,  160),
        PositionSizePct           = Math.Clamp(PositionSizePct,           0.01, 0.05),
        TimeStopBars              = Math.Clamp(TimeStopBars,               5,   50),
        TimeStopLossPct           = Math.Clamp(TimeStopLossPct,           0.02, 0.25),
        RegimeSustainedBars  = Math.Clamp(RegimeSustainedBars,  10,  100),
        // Omitting a field from this initialiser silently RESETS it to 0. For RegimeAdaptStrength
        // that means the gene is disabled on every clamp, and the GA would look like it rejected
        // adaptation when in fact the plumbing threw it away.
        RegimeAdaptPivotBars = Math.Clamp(RegimeAdaptPivotBars, (int)Lo(IdxAdaptPivot), (int)Hi(IdxAdaptPivot)),
        RegimeAdaptStrength  = Math.Clamp(RegimeAdaptStrength, Lo(IdxAdaptStrength), Hi(IdxAdaptStrength)),
        Fitness = Fitness,
    };

    // ── Bayesian optimiser interface ──────────────────────────────────────────
    // [gene, 0]=min  [gene, 1]=max — mirrors the Clamp bounds above.
    private const int IdxAdaptPivot = 14, IdxAdaptStrength = 15;
    private static double Lo(int i) => Bounds[i, 0];
    private static double Hi(int i) => Bounds[i, 1];

    public static readonly double[,] Bounds =
    {
        { 100,  500 }, // RegimeLongEmaPeriod
        {  10,   60 }, // RegimeSlopeLookback
        {  20,  100 }, // EmaPeriod
        {  15,   35 }, // AdxThreshold
        {  40,   60 }, // RsiRallyThreshold
        { 0.5,  2.0 }, // StopLossAtrMult
        { 2.0, 15.0 }, // TakeProfitAtrMult
        { 1.5,  5.0 }, // TrailingActivationAtrMult
        { 1.0,  5.0 }, // TrailingStopAtrMult
        {  10,  160 }, // MaxHoldCandles — widened, see the field comment
        { 0.01,0.05 }, // PositionSizePct
        {   5,   50 }, // TimeStopBars
        { 0.02,0.25 }, // TimeStopLossPct
        {  10,  100 }, // RegimeSustainedBars
            {  20,  300 }, // RegimeAdaptPivotBars
        {-0.4,  0.4 }, // RegimeAdaptStrength — 0 disables
};

    public static readonly double[,] BoundsLowVol =
    {
        { 100,  500 }, // RegimeLongEmaPeriod
        {  10,   60 }, // RegimeSlopeLookback
        {  20,  100 }, // EmaPeriod
        {  10,   30 }, // AdxThreshold (lower for low-vol)
        {  40,   60 }, // RsiRallyThreshold
        { 0.5,  1.5 }, // StopLossAtrMult (tighter for low-vol)
        { 2.0,  6.0 }, // TakeProfitAtrMult (smaller for low-vol)
        { 1.5,  4.0 }, // TrailingActivationAtrMult
        { 1.0,  3.0 }, // TrailingStopAtrMult (tighter for low-vol)
        {  72, 200 }, // MaxHoldCandles (longer for low-vol)
        { 0.01,0.03 }, // PositionSizePct (smaller for low-vol)
        {  20,  80 }, // TimeStopBars (longer for low-vol)
        { 0.02,0.20 }, // TimeStopLossPct
        {  10,  100 }, // RegimeSustainedBars
            {  20,  300 }, // RegimeAdaptPivotBars
        {-0.4,  0.4 }, // RegimeAdaptStrength — 0 disables
};

    public static readonly double[,] BoundsHighVol =
    {
        { 150,  400 }, // RegimeLongEmaPeriod
        {  12,   40 }, // RegimeSlopeLookback
        {  30,   80 }, // EmaPeriod
        {  30,   50 }, // AdxThreshold
        {  45,   65 }, // RsiRallyThreshold
        { 1.5,  2.5 }, // StopLossAtrMult
        { 5.0, 15.0 }, // TakeProfitAtrMult
        { 2.5,  5.0 }, // TrailingActivationAtrMult
        { 2.0,  5.0 }, // TrailingStopAtrMult
        {  24,   72 }, // MaxHoldCandles
        { 0.03, 0.07 }, // PositionSizePct
        {  15,   45 }, // TimeStopBars
        { 0.08, 0.20 }, // TimeStopLossPct
        {  30,   80 }, // RegimeSustainedBars
            {  20,  300 }, // RegimeAdaptPivotBars
        {-0.4,  0.4 }, // RegimeAdaptStrength — 0 disables
};

    public static readonly string[] ParameterNames =
    [
        "RegimeLongEmaPeriod", "RegimeSlopeLookback", "EmaPeriod",
        "AdxThreshold", "RsiRallyThreshold",
        "StopLossAtrMult", "TakeProfitAtrMult",
        "TrailingActivationAtrMult", "TrailingStopAtrMult",
        "MaxHoldCandles", "PositionSizePct",
        "TimeStopBars", "TimeStopLossPct",
        "RegimeSustainedBars",
    ];

    // Same seeded-init contract as Random (see SeedMutantProbability), but the
    // seed-mutant branch is confined to BoundsHighVol via ClampToBoundsHighVol +
    // MutateHighVol — never the normal-regime Mutate, which would propose
    // genotypes outside the region the high-vol variant is defined on.
    public static RipShortGenotype RandomHighVol(System.Random rng, RipShortGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.MutateHighVol(rng, 0.5);

        return new()
        {
            RegimeLongEmaPeriod = rng.Next(150, 401),
            RegimeSlopeLookback = rng.Next(12, 41),
            EmaPeriod           = rng.Next(30, 81),
            AdxThreshold        = 30.0 + rng.NextDouble() * 20.0,
            RsiRallyThreshold   = 45.0 + rng.NextDouble() * 20.0,
            StopLossAtrMult           = 1.5 + rng.NextDouble() * 1.0,
            TakeProfitAtrMult         = 5.0 + rng.NextDouble() * 10.0,
            TrailingActivationAtrMult = 2.5 + rng.NextDouble() * 2.5,
            TrailingStopAtrMult       = 2.0 + rng.NextDouble() * 3.0,
            MaxHoldCandles            = rng.Next(24, 73),
            PositionSizePct           = 0.03 + rng.NextDouble() * 0.04,
            TimeStopBars              = rng.Next(15, 46),
            TimeStopLossPct           = 0.08 + rng.NextDouble() * 0.12,
            RegimeSustainedBars  = rng.Next(30, 81),
            RegimeAdaptPivotBars = (int)(Lo(IdxAdaptPivot) + rng.NextDouble() * (Hi(IdxAdaptPivot) - Lo(IdxAdaptPivot))),
            RegimeAdaptStrength  = Lo(IdxAdaptStrength) + rng.NextDouble() * (Hi(IdxAdaptStrength) - Lo(IdxAdaptStrength)),
        };
    }

    // ── High-vol variant operators ───────────────────────────────────────────────
    // The low-vol variant already had ClampToBoundsLowVol / MutateLowVol; the
    // high-vol variant had only BoundsHighVol, so seeded RandomHighVol had no
    // in-region mutation operator to call. Both are driven off the BoundsHighVol
    // table so they can never drift out of the high-vol region.
    public static RipShortGenotype FromVectorHighVol(double[] v) => new()
    {
        RegimeLongEmaPeriod       = (int)Math.Clamp(Math.Round(v[0]),  BoundsHighVol[0, 0],  BoundsHighVol[0, 1]),
        RegimeSlopeLookback       = (int)Math.Clamp(Math.Round(v[1]),  BoundsHighVol[1, 0],  BoundsHighVol[1, 1]),
        EmaPeriod                 = (int)Math.Clamp(Math.Round(v[2]),  BoundsHighVol[2, 0],  BoundsHighVol[2, 1]),
        AdxThreshold              = Math.Clamp(v[3],  BoundsHighVol[3, 0],  BoundsHighVol[3, 1]),
        RsiRallyThreshold         = Math.Clamp(v[4],  BoundsHighVol[4, 0],  BoundsHighVol[4, 1]),
        StopLossAtrMult           = Math.Clamp(v[5],  BoundsHighVol[5, 0],  BoundsHighVol[5, 1]),
        TakeProfitAtrMult         = Math.Clamp(v[6],  BoundsHighVol[6, 0],  BoundsHighVol[6, 1]),
        TrailingActivationAtrMult = Math.Clamp(v[7],  BoundsHighVol[7, 0],  BoundsHighVol[7, 1]),
        TrailingStopAtrMult       = Math.Clamp(v[8],  BoundsHighVol[8, 0],  BoundsHighVol[8, 1]),
        MaxHoldCandles            = (int)Math.Clamp(Math.Round(v[9]),  BoundsHighVol[9, 0],  BoundsHighVol[9, 1]),
        PositionSizePct           = Math.Clamp(v[10], BoundsHighVol[10, 0], BoundsHighVol[10, 1]),
        TimeStopBars              = (int)Math.Clamp(Math.Round(v[11]), BoundsHighVol[11, 0], BoundsHighVol[11, 1]),
        TimeStopLossPct           = Math.Clamp(v[12], BoundsHighVol[12, 0], BoundsHighVol[12, 1]),
        RegimeSustainedBars       = (int)Math.Clamp(Math.Round(v[13]), BoundsHighVol[13, 0], BoundsHighVol[13, 1]),
        RegimeAdaptPivotBars      = v.Length > IdxAdaptPivot    ? (int)Math.Clamp(Math.Round(v[IdxAdaptPivot]), BoundsHighVol[IdxAdaptPivot, 0], BoundsHighVol[IdxAdaptPivot, 1]) : (int)BoundsHighVol[IdxAdaptPivot, 0],
        RegimeAdaptStrength       = v.Length > IdxAdaptStrength ? Math.Clamp(v[IdxAdaptStrength], BoundsHighVol[IdxAdaptStrength, 0], BoundsHighVol[IdxAdaptStrength, 1]) : 0.0,
    };

    public RipShortGenotype ClampToBoundsHighVol()
    {
        var g = FromVectorHighVol(ToVector());
        g.Fitness = Fitness;
        return g;
    }

    public RipShortGenotype MutateHighVol(System.Random rng, double rate)
    {
        double[] v = ClampToBoundsHighVol().ToVector();
        for (int i = 0; i < v.Length; i++)
        {
            if (rng.NextDouble() > rate) continue;
            double lo = BoundsHighVol[i, 0], hi = BoundsHighVol[i, 1];
            v[i] = Math.Clamp(v[i] + (rng.NextDouble() - 0.5) * (hi - lo) * 0.2, lo, hi);
        }
        return FromVectorHighVol(v);
    }

    public double[] ToVector() =>
    [
        RegimeLongEmaPeriod, RegimeSlopeLookback, EmaPeriod,
        AdxThreshold, RsiRallyThreshold,
        StopLossAtrMult, TakeProfitAtrMult,
        TrailingActivationAtrMult, TrailingStopAtrMult,
        MaxHoldCandles, PositionSizePct,
        TimeStopBars, TimeStopLossPct,
        RegimeSustainedBars,
        RegimeAdaptPivotBars, RegimeAdaptStrength,
    ];

    public static RipShortGenotype FromVector(double[] v) => new()
    {
        RegimeLongEmaPeriod       = Math.Clamp((int)Math.Round(v[0]),  100, 500),
        RegimeSlopeLookback       = Math.Clamp((int)Math.Round(v[1]),   10,  60),
        EmaPeriod                 = Math.Clamp((int)Math.Round(v[2]),   20, 100),
        AdxThreshold              = Math.Clamp(v[3],  15.0, 35.0),
        RsiRallyThreshold         = Math.Clamp(v[4],  40.0, 60.0),
        StopLossAtrMult           = Math.Clamp(v[5],   0.5,  2.0),
        TakeProfitAtrMult         = Math.Clamp(v[6],   2.0, 15.0),
        TrailingActivationAtrMult = Math.Clamp(v[7],   1.5,  5.0),
        TrailingStopAtrMult       = Math.Clamp(v[8],   1.0,  5.0),
        MaxHoldCandles            = Math.Clamp((int)Math.Round(v[9]),   10, 160),
        PositionSizePct           = Math.Clamp(v[10], 0.01, 0.05),
        TimeStopBars              = Math.Clamp((int)Math.Round(v[11]),   5,  50),
        TimeStopLossPct           = Math.Clamp(v[12], 0.02, 0.25),
        RegimeSustainedBars       = Math.Clamp((int)Math.Round(v[13]), 10, 100),
        // Older vectors (13 genes) predate these — they read as 0, which disables adaptation.
        // Carried through the variant paths too: ClampToBoundsHighVol is FromVectorHighVol(ToVector()),
        // so a gene missing there is silently zeroed on every clamp — which is how the diversity
        // test caught a pivot of 0 sitting outside its own [20,300] bound.
        RegimeAdaptPivotBars      = v.Length > IdxAdaptPivot    ? Math.Clamp((int)Math.Round(v[IdxAdaptPivot]), (int)Lo(IdxAdaptPivot), (int)Hi(IdxAdaptPivot)) : 0,
        RegimeAdaptStrength       = v.Length > IdxAdaptStrength ? Math.Clamp(v[IdxAdaptStrength], Lo(IdxAdaptStrength), Hi(IdxAdaptStrength)) : 0.0,
    };

    public static RipShortGenotype FromVectorLowVol(double[] v) => new()
    {
        RegimeLongEmaPeriod       = Math.Clamp((int)Math.Round(v[0]),  100, 500),
        RegimeSlopeLookback       = Math.Clamp((int)Math.Round(v[1]),   10,  60),
        EmaPeriod                 = Math.Clamp((int)Math.Round(v[2]),   20, 100),
        AdxThreshold              = Math.Clamp(v[3],  10.0, 30.0),
        RsiRallyThreshold         = Math.Clamp(v[4],  40.0, 60.0),
        StopLossAtrMult           = Math.Clamp(v[5],   0.5,  1.5),
        TakeProfitAtrMult         = Math.Clamp(v[6],   2.0,  6.0),
        TrailingActivationAtrMult = Math.Clamp(v[7],   1.5,  4.0),
        TrailingStopAtrMult       = Math.Clamp(v[8],   1.0,  3.0),
        MaxHoldCandles            = Math.Clamp((int)Math.Round(v[9]),   72, 200),
        PositionSizePct           = Math.Clamp(v[10], 0.01, 0.03),
        TimeStopBars              = Math.Clamp((int)Math.Round(v[11]),  20,  80),
        TimeStopLossPct           = Math.Clamp(v[12], 0.02, 0.20),
        RegimeSustainedBars       = Math.Clamp((int)Math.Round(v[13]), 10, 100),
        // Carried through the variant paths too. ClampToBoundsHighVol is FromVectorHighVol(ToVector()),
        // so a gene missing here is silently zeroed on every clamp — which is how the diversity
        // test caught a pivot of 0 sitting outside its own [20,300] bound.
        RegimeAdaptPivotBars      = v.Length > IdxAdaptPivot    ? Math.Clamp((int)Math.Round(v[IdxAdaptPivot]), (int)Lo(IdxAdaptPivot), (int)Hi(IdxAdaptPivot)) : 0,
        RegimeAdaptStrength       = v.Length > IdxAdaptStrength ? Math.Clamp(v[IdxAdaptStrength], Lo(IdxAdaptStrength), Hi(IdxAdaptStrength)) : 0.0,
    };

    // Same seeded-init contract as Random (see SeedMutantProbability), but the
    // seed-mutant branch uses the low-vol clamp + mutate pair so it stays inside
    // BoundsLowVol.
    public static RipShortGenotype RandomLowVol(System.Random rng, RipShortGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBoundsLowVol().MutateLowVol(rng, 0.5);

        return new()
        {
            RegimeLongEmaPeriod = rng.Next(100, 501),
            RegimeSlopeLookback = rng.Next(10, 61),
            EmaPeriod           = rng.Next(20, 101),
            AdxThreshold        = 10.0 + rng.NextDouble() * 20.0,
            RsiRallyThreshold   = 40.0 + rng.NextDouble() * 20.0,
            StopLossAtrMult           = 0.5 + rng.NextDouble() * 1.0,
            TakeProfitAtrMult         = 2.0 + rng.NextDouble() * 4.0,
            TrailingActivationAtrMult = 1.5 + rng.NextDouble() * 2.5,
            TrailingStopAtrMult       = 1.0 + rng.NextDouble() * 2.0,
            MaxHoldCandles            = rng.Next(72, 201),
            PositionSizePct           = 0.01 + rng.NextDouble() * 0.02,
            TimeStopBars              = rng.Next(20, 81),
            TimeStopLossPct           = 0.02 + rng.NextDouble() * 0.18,
            RegimeSustainedBars  = rng.Next(10, 101),
            RegimeAdaptPivotBars = (int)(Lo(IdxAdaptPivot) + rng.NextDouble() * (Hi(IdxAdaptPivot) - Lo(IdxAdaptPivot))),
            RegimeAdaptStrength  = Lo(IdxAdaptStrength) + rng.NextDouble() * (Hi(IdxAdaptStrength) - Lo(IdxAdaptStrength)),
        };
    }

    public RipShortGenotype ClampToBoundsLowVol() => new()
    {
        RegimeLongEmaPeriod = Math.Clamp(RegimeLongEmaPeriod, 100, 500),
        RegimeSlopeLookback = Math.Clamp(RegimeSlopeLookback,  10,  60),
        EmaPeriod           = Math.Clamp(EmaPeriod,            20, 100),
        AdxThreshold        = Math.Clamp(AdxThreshold,        10.0, 30.0),
        RsiRallyThreshold   = Math.Clamp(RsiRallyThreshold,   40.0, 60.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.5,  1.5),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         2.0,  6.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.5,  4.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,       1.0,  3.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,            72, 200),
        PositionSizePct           = Math.Clamp(PositionSizePct,           0.01, 0.03),
        TimeStopBars              = Math.Clamp(TimeStopBars,               20,  80),
        TimeStopLossPct           = Math.Clamp(TimeStopLossPct,           0.02, 0.20),
        RegimeSustainedBars  = Math.Clamp(RegimeSustainedBars,  10,  100),
        // Carried, not dropped — see the note on ClampToBounds.
        RegimeAdaptPivotBars = Math.Clamp(RegimeAdaptPivotBars, (int)Lo(IdxAdaptPivot), (int)Hi(IdxAdaptPivot)),
        RegimeAdaptStrength  = Math.Clamp(RegimeAdaptStrength, Lo(IdxAdaptStrength), Hi(IdxAdaptStrength)),
        Fitness = Fitness,
    };

    public RipShortGenotype MutateLowVol(System.Random rng, double rate)
    {
        double Nudge(double val, double min, double max, double scale)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + (rng.NextDouble() - 0.5) * scale, min, max);
        }
        int NudgeInt(int val, int min, int max, int step)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + rng.Next(-step, step + 1), min, max);
        }
        return new RipShortGenotype
        {
            RegimeLongEmaPeriod = NudgeInt(RegimeLongEmaPeriod, 100, 500, 30),
            RegimeSlopeLookback = NudgeInt(RegimeSlopeLookback,  10,  60,  5),
            EmaPeriod           = NudgeInt(EmaPeriod,            20, 100, 10),
            AdxThreshold        = Nudge(AdxThreshold,           10.0, 30.0, 3.0),
            RsiRallyThreshold   = Nudge(RsiRallyThreshold,      40.0, 60.0, 3.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           0.5,  1.5, 0.2),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         2.0,  6.0, 1.0),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.5,  4.0, 0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       1.0,  3.0, 0.4),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles, 72, 200, 10),
            PositionSizePct           = Nudge(PositionSizePct,   0.01, 0.03, 0.004),
            TimeStopBars              = NudgeInt(TimeStopBars,    20, 80, 5),
            TimeStopLossPct           = Nudge(TimeStopLossPct,   0.02, 0.20, 0.03),
            RegimeSustainedBars  = NudgeInt(RegimeSustainedBars, 10, 100, 10),
            RegimeAdaptPivotBars = NudgeInt(RegimeAdaptPivotBars, (int)Lo(IdxAdaptPivot), (int)Hi(IdxAdaptPivot), 30),
            RegimeAdaptStrength  = Nudge(RegimeAdaptStrength, Lo(IdxAdaptStrength), Hi(IdxAdaptStrength), 0.15),
        };
    }

    public override string ToString() =>
        $"BearEMA{RegimeLongEmaPeriod}(slope{RegimeSlopeLookback}) EMA{EmaPeriod} ADX(7,≥{AdxThreshold:F0}) " +
        $"RSI(7,rally≥{RsiRallyThreshold:F0}) " +
        $"SL={StopLossAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars Pos={PositionSizePct:P0} " +
        $"TStop({TimeStopBars}bars/{TimeStopLossPct:P0}) " +
        $"RegSust={RegimeSustainedBars} " +
        $"F={Fitness:F4}";
}
