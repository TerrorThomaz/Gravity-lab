namespace TradingGA;

class FadeShortGenotypeDto
{
    public int    EmaPeriod      { get; set; }
    public int    RsiPeriod      { get; set; }
    public int    AdxPeriod      { get; set; }
    public double AdxThreshold   { get; set; }
    public int    LookbackCandles  { get; set; }
    public double RsiOverbought    { get; set; }
    public double RsiDivThreshold  { get; set; }
    public double MinRallyAtrMult  { get; set; }
    public double StopLossAtrMult           { get; set; }
    public double MaeAtrMult                { get; set; }
    public double TakeProfitAtrMult         { get; set; }
    public double TrailingActivationAtrMult { get; set; }
    public double TrailingStopAtrMult       { get; set; }
    public int    MaxHoldCandles            { get; set; }
    public double PositionSizePct           { get; set; }
    // Nullable: a file written before this gene existed must restore 0 (disabled), and a plain
    // int cannot tell "absent" from an explicit 0. Same trap BailOutAtrMult fell into — it was
    // dropped on save and reloaded as 0, so every GridShort genotype written before that fix
    // carried a disabled bail-out its GA had optimised to use.
    public int?   RegimeSustainBars         { get; set; }
    public double Fitness                   { get; set; }
    public double AtrLow                    { get; init; } = 0.0;
    public double AtrHigh                   { get; init; } = 9999.0;

    public static FadeShortGenotypeDto From(FadeShortGenotype g, FitnessConfig? cfg = null) => new()
    {
        EmaPeriod      = g.EmaPeriod,
        RsiPeriod      = 7,
        AdxPeriod      = 7,
        AdxThreshold   = g.AdxThreshold,
        LookbackCandles  = g.LookbackCandles,
        RsiOverbought    = g.RsiOverbought,
        RsiDivThreshold  = g.RsiDivThreshold,
        MinRallyAtrMult  = g.MinRallyAtrMult,
        StopLossAtrMult           = g.StopLossAtrMult,
        MaeAtrMult                = g.MaeAtrMult,
        TakeProfitAtrMult         = g.TakeProfitAtrMult,
        TrailingActivationAtrMult = g.TrailingActivationAtrMult,
        TrailingStopAtrMult       = g.TrailingStopAtrMult,
        MaxHoldCandles            = g.MaxHoldCandles,
        PositionSizePct           = g.PositionSizePct,
        RegimeSustainBars         = g.RegimeSustainBars,
        Fitness                   = g.Fitness,
        AtrLow                    = cfg?.AtrLow  ?? 0.0,
        AtrHigh                   = cfg?.AtrHigh ?? 9999.0,
    };

    public FadeShortGenotype ToGenotype() => new FadeShortGenotype
    {
        EmaPeriod        = EmaPeriod,
        AdxThreshold     = AdxThreshold,
        LookbackCandles  = LookbackCandles  > 0 ? LookbackCandles  : 15,
        RsiOverbought    = RsiOverbought    > 0 ? RsiOverbought    : 68.0,
        RsiDivThreshold  = RsiDivThreshold  > 0 ? RsiDivThreshold  : 8.0,
        MinRallyAtrMult  = MinRallyAtrMult  > 0 ? MinRallyAtrMult  : 5.0,
        StopLossAtrMult           = StopLossAtrMult           > 0 ? Math.Min(StopLossAtrMult, 2.0) : 0.8,
        MaeAtrMult                = MaeAtrMult                > 0 ? MaeAtrMult                : 2.5,
        TakeProfitAtrMult         = TakeProfitAtrMult         > 0 ? TakeProfitAtrMult         : 4.0,
        TrailingActivationAtrMult = TrailingActivationAtrMult > 0 ? TrailingActivationAtrMult : 3.0,
        TrailingStopAtrMult       = TrailingStopAtrMult       > 0 ? TrailingStopAtrMult       : 1.5,
        MaxHoldCandles            = MaxHoldCandles            > 0 ? MaxHoldCandles            : 42,
        PositionSizePct           = PositionSizePct           > 0 ? PositionSizePct           : 0.03,
        RegimeSustainBars         = RegimeSustainBars ?? 0,
        Fitness                   = Fitness,
    }.ClampToBounds();
}


class FadeLongGenotypeDto
{
    public int    RegimePeriod             { get; set; }
    public int    EmaPeriod                { get; set; }
    public double AdxThreshold             { get; set; }
    public int    LookbackCandles          { get; set; }
    public double RsiOversold              { get; set; }
    public double RsiDivThreshold          { get; set; }
    public double MinDropAtrMult           { get; set; }
    public double StopLossAtrMult          { get; set; }
    public double MaeAtrMult               { get; set; }
    public double TakeProfitAtrMult        { get; set; }
    public double TrailingActivationAtrMult { get; set; }
    public double TrailingStopAtrMult      { get; set; }
    public int    MaxHoldCandles           { get; set; }
    public double PositionSizePct          { get; set; }
    public int    RegimeSustainedBars      { get; set; }
    public double Fitness                  { get; set; }
    public double AtrLow                   { get; init; } = 0.0;
    public double AtrHigh                  { get; init; } = 9999.0;

    public static FadeLongGenotypeDto From(FadeLongGenotype g, FitnessConfig? cfg = null) => new()
    {
        RegimePeriod              = g.RegimePeriod,
        EmaPeriod                 = g.EmaPeriod,
        AdxThreshold              = g.AdxThreshold,
        LookbackCandles           = g.LookbackCandles,
        RsiOversold               = g.RsiOversold,
        RsiDivThreshold           = g.RsiDivThreshold,
        MinDropAtrMult            = g.MinDropAtrMult,
        StopLossAtrMult           = g.StopLossAtrMult,
        MaeAtrMult                = g.MaeAtrMult,
        TakeProfitAtrMult         = g.TakeProfitAtrMult,
        TrailingActivationAtrMult = g.TrailingActivationAtrMult,
        TrailingStopAtrMult       = g.TrailingStopAtrMult,
        MaxHoldCandles            = g.MaxHoldCandles,
        PositionSizePct           = g.PositionSizePct,
        RegimeSustainedBars       = g.RegimeSustainedBars,
        Fitness                   = g.Fitness,
        AtrLow                    = cfg?.AtrLow  ?? 0.0,
        AtrHigh                   = cfg?.AtrHigh ?? 9999.0,
    };

    public FadeLongGenotype ToGenotype() => new FadeLongGenotype
    {
        RegimePeriod              = RegimePeriod     > 0 ? RegimePeriod     : 200,
        EmaPeriod                 = EmaPeriod        > 0 ? EmaPeriod        : 50,
        AdxThreshold              = AdxThreshold     > 0 ? AdxThreshold     : 27.0,
        LookbackCandles           = LookbackCandles  > 0 ? LookbackCandles  : 48,
        RsiOversold               = RsiOversold      > 0 ? RsiOversold      : 30.0,
        RsiDivThreshold           = RsiDivThreshold  > 0 ? RsiDivThreshold  : 8.0,
        MinDropAtrMult            = MinDropAtrMult   > 0 ? MinDropAtrMult   : 7.0,
        StopLossAtrMult           = StopLossAtrMult           > 0 ? StopLossAtrMult           : 0.8,
        MaeAtrMult                = MaeAtrMult                > 0 ? MaeAtrMult                : 2.5,
        TakeProfitAtrMult         = TakeProfitAtrMult         > 0 ? TakeProfitAtrMult         : 5.0,
        TrailingActivationAtrMult = TrailingActivationAtrMult > 0 ? TrailingActivationAtrMult : 2.0,
        TrailingStopAtrMult       = TrailingStopAtrMult       > 0 ? TrailingStopAtrMult       : 2.0,
        MaxHoldCandles            = MaxHoldCandles   > 0 ? MaxHoldCandles   : 42,
        PositionSizePct           = PositionSizePct  > 0 ? PositionSizePct  : 0.03,
        RegimeSustainedBars       = RegimeSustainedBars > 0 ? RegimeSustainedBars : 30,
        Fitness                   = Fitness,
    }.ClampToBounds();
}

class DipLongGenotypeDto
{
    public int    RegimeLongEmaPeriod         { get; set; }
    public int    RegimeSlopeLookback         { get; set; }
    public int    EmaPeriod                   { get; set; }
    public double AdxThreshold                { get; set; }
    public double RsiDipThreshold             { get; set; }
    public double StopLossAtrMult             { get; set; }
    public double TakeProfitAtrMult           { get; set; }
    public double TrailingActivationAtrMult   { get; set; }
    public double TrailingStopAtrMult         { get; set; }
    public int    MaxHoldCandles              { get; set; }
    public double PositionSizePct             { get; set; }
    public int    TimeStopBars                { get; set; }  // 0 = not in old JSON → use 999 (disabled)
    public double TimeStopLossPct             { get; set; }  // 0 = not in old JSON → use 0.99 (disabled)
    public int    RegimeSustainedBars         { get; set; }
    public double Fitness                     { get; set; }
    public double AtrLow                      { get; init; } = 0.0;
    public double AtrHigh                     { get; init; } = 9999.0;

    public static DipLongGenotypeDto From(DipLongGenotype g, FitnessConfig? cfg = null) => new()
    {
        RegimeLongEmaPeriod       = g.RegimeLongEmaPeriod,
        RegimeSlopeLookback       = g.RegimeSlopeLookback,
        EmaPeriod                 = g.EmaPeriod,
        AdxThreshold              = g.AdxThreshold,
        RsiDipThreshold           = g.RsiDipThreshold,
        StopLossAtrMult           = g.StopLossAtrMult,
        TakeProfitAtrMult         = g.TakeProfitAtrMult,
        TrailingActivationAtrMult = g.TrailingActivationAtrMult,
        TrailingStopAtrMult       = g.TrailingStopAtrMult,
        MaxHoldCandles            = g.MaxHoldCandles,
        PositionSizePct           = g.PositionSizePct,
        TimeStopBars              = g.TimeStopBars,
        TimeStopLossPct           = g.TimeStopLossPct,
        RegimeSustainedBars       = g.RegimeSustainedBars,
        Fitness                   = g.Fitness,
        AtrLow                    = cfg?.AtrLow  ?? 0.0,
        AtrHigh                   = cfg?.AtrHigh ?? 9999.0,
    };

    public DipLongGenotype ToGenotype() => new DipLongGenotype
    {
        RegimeLongEmaPeriod       = RegimeLongEmaPeriod > 0 ? RegimeLongEmaPeriod : 200,
        RegimeSlopeLookback       = RegimeSlopeLookback > 0 ? RegimeSlopeLookback : 30,
        EmaPeriod                 = EmaPeriod           > 0 ? EmaPeriod           : 50,
        AdxThreshold              = AdxThreshold        > 0 ? AdxThreshold        : 25.0,
        RsiDipThreshold           = RsiDipThreshold     > 0 ? RsiDipThreshold     : 48.0,
        StopLossAtrMult           = StopLossAtrMult           > 0 ? StopLossAtrMult           : 0.8,
        TakeProfitAtrMult         = TakeProfitAtrMult         > 0 ? TakeProfitAtrMult         : 5.0,
        TrailingActivationAtrMult = TrailingActivationAtrMult > 0 ? TrailingActivationAtrMult : 2.5,
        TrailingStopAtrMult       = TrailingStopAtrMult       > 0 ? TrailingStopAtrMult       : 2.0,
        MaxHoldCandles            = MaxHoldCandles      > 0 ? MaxHoldCandles      : 30,
        PositionSizePct           = PositionSizePct     > 0 ? PositionSizePct     : 0.03,
        TimeStopBars              = TimeStopBars        > 0 ? TimeStopBars        : 999,  // 0 in old JSON → disabled
        TimeStopLossPct           = TimeStopLossPct     > 0 ? TimeStopLossPct     : 0.99,
        RegimeSustainedBars       = RegimeSustainedBars > 0 ? RegimeSustainedBars : 30,
        Fitness                   = Fitness,
    }.ClampToBounds();
}

class RipShortGenotypeDto
{
    public int    RegimeLongEmaPeriod         { get; set; }
    public int    RegimeSlopeLookback         { get; set; }
    public int    EmaPeriod                   { get; set; }
    public double AdxThreshold                { get; set; }
    public double RsiRallyThreshold           { get; set; }
    public double StopLossAtrMult             { get; set; }
    public double TakeProfitAtrMult           { get; set; }
    public double TrailingActivationAtrMult   { get; set; }
    public double TrailingStopAtrMult         { get; set; }
    public int    MaxHoldCandles              { get; set; }
    public double PositionSizePct             { get; set; }
    public int    TimeStopBars                { get; set; }  // 0 = not in old JSON → use 999 (disabled)
    public double TimeStopLossPct             { get; set; }  // 0 = not in old JSON → use 0.99 (disabled)
    public int    RegimeSustainedBars         { get; set; }
    public double Fitness                     { get; set; }
    public double AtrLow                      { get; init; } = 0.0;
    public double AtrHigh                     { get; init; } = 9999.0;

    public static RipShortGenotypeDto From(RipShortGenotype g, FitnessConfig? cfg = null) => new()
    {
        RegimeLongEmaPeriod       = g.RegimeLongEmaPeriod,
        RegimeSlopeLookback       = g.RegimeSlopeLookback,
        EmaPeriod                 = g.EmaPeriod,
        AdxThreshold              = g.AdxThreshold,
        RsiRallyThreshold         = g.RsiRallyThreshold,
        StopLossAtrMult           = g.StopLossAtrMult,
        TakeProfitAtrMult         = g.TakeProfitAtrMult,
        TrailingActivationAtrMult = g.TrailingActivationAtrMult,
        TrailingStopAtrMult       = g.TrailingStopAtrMult,
        MaxHoldCandles            = g.MaxHoldCandles,
        PositionSizePct           = g.PositionSizePct,
        TimeStopBars              = g.TimeStopBars,
        TimeStopLossPct           = g.TimeStopLossPct,
        RegimeSustainedBars       = g.RegimeSustainedBars,
        Fitness                   = g.Fitness,
        AtrLow                    = cfg?.AtrLow  ?? 0.0,
        AtrHigh                   = cfg?.AtrHigh ?? 9999.0,
    };

    public RipShortGenotype ToGenotype() => new RipShortGenotype
    {
        RegimeLongEmaPeriod       = RegimeLongEmaPeriod > 0 ? RegimeLongEmaPeriod : 200,
        RegimeSlopeLookback       = RegimeSlopeLookback > 0 ? RegimeSlopeLookback : 30,
        EmaPeriod                 = EmaPeriod           > 0 ? EmaPeriod           : 50,
        AdxThreshold              = AdxThreshold        > 0 ? AdxThreshold        : 25.0,
        RsiRallyThreshold         = RsiRallyThreshold   > 0 ? RsiRallyThreshold   : 52.0,
        StopLossAtrMult           = StopLossAtrMult           > 0 ? StopLossAtrMult           : 0.8,
        TakeProfitAtrMult         = TakeProfitAtrMult         > 0 ? TakeProfitAtrMult         : 7.0,
        TrailingActivationAtrMult = TrailingActivationAtrMult > 0 ? TrailingActivationAtrMult : 2.5,
        TrailingStopAtrMult       = TrailingStopAtrMult       > 0 ? TrailingStopAtrMult       : 2.0,
        MaxHoldCandles            = MaxHoldCandles      > 0 ? MaxHoldCandles      : 30,
        PositionSizePct           = PositionSizePct     > 0 ? PositionSizePct     : 0.03,
        TimeStopBars              = TimeStopBars        > 0 ? TimeStopBars        : 999,  // 0 in old JSON → disabled
        TimeStopLossPct           = TimeStopLossPct     > 0 ? TimeStopLossPct     : 0.99,
        RegimeSustainedBars       = RegimeSustainedBars > 0 ? RegimeSustainedBars : 30,
        Fitness                   = Fitness,
    }.ClampToBounds();
}

class SwingLongGenotypeDto
{
    public int    EmaPeriod                 { get; set; }
    public double AdxThreshold              { get; set; }
    public int    LookbackCandles           { get; set; }
    public double RsiOversold               { get; set; }
    public double RsiDivThreshold           { get; set; }
    public double MinDeclineAtrMult         { get; set; }
    public double StopLossAtrMult           { get; set; }
    public double TakeProfitAtrMult         { get; set; }
    public double TrailingActivationAtrMult { get; set; }
    public double TrailingStopAtrMult       { get; set; }
    public int    MaxHoldCandles            { get; set; }
    public double PositionSizePct           { get; set; }
    public int    TimeStopBars              { get; set; }
    public double TimeStopLossPct           { get; set; }
    public double Fitness                   { get; set; }
    public double AtrLow                    { get; init; } = 0.0;
    public double AtrHigh                   { get; init; } = 9999.0;

    public static SwingLongGenotypeDto From(SwingLongGenotype g, FitnessConfig? cfg = null) => new()
    {
        EmaPeriod                 = g.EmaPeriod,
        AdxThreshold              = g.AdxThreshold,
        LookbackCandles           = g.LookbackCandles,
        RsiOversold               = g.RsiOversold,
        RsiDivThreshold           = g.RsiDivThreshold,
        MinDeclineAtrMult         = g.MinDeclineAtrMult,
        StopLossAtrMult           = g.StopLossAtrMult,
        TakeProfitAtrMult         = g.TakeProfitAtrMult,
        TrailingActivationAtrMult = g.TrailingActivationAtrMult,
        TrailingStopAtrMult       = g.TrailingStopAtrMult,
        MaxHoldCandles            = g.MaxHoldCandles,
        PositionSizePct           = g.PositionSizePct,
        TimeStopBars              = g.TimeStopBars,
        TimeStopLossPct           = g.TimeStopLossPct,
        Fitness                   = g.Fitness,
        AtrLow                    = cfg?.AtrLow  ?? 0.0,
        AtrHigh                   = cfg?.AtrHigh ?? 9999.0,
    };

    public SwingLongGenotype ToGenotype() => new SwingLongGenotype
    {
        EmaPeriod                 = EmaPeriod                 > 0 ? EmaPeriod                 : 50,
        AdxThreshold              = AdxThreshold              > 0 ? AdxThreshold              : 25.0,
        LookbackCandles           = LookbackCandles           > 0 ? LookbackCandles           : 48,
        RsiOversold               = RsiOversold               > 0 ? RsiOversold               : 30.0,
        RsiDivThreshold           = RsiDivThreshold           > 0 ? RsiDivThreshold           : 8.0,
        MinDeclineAtrMult         = MinDeclineAtrMult         > 0 ? MinDeclineAtrMult         : 5.0,
        StopLossAtrMult           = StopLossAtrMult           > 0 ? StopLossAtrMult           : 0.8,
        TakeProfitAtrMult         = TakeProfitAtrMult         > 0 ? TakeProfitAtrMult         : 5.0,
        TrailingActivationAtrMult = TrailingActivationAtrMult > 0 ? TrailingActivationAtrMult : 2.0,
        TrailingStopAtrMult       = TrailingStopAtrMult       > 0 ? TrailingStopAtrMult       : 2.0,
        MaxHoldCandles            = MaxHoldCandles            > 0 ? MaxHoldCandles            : 42,
        PositionSizePct           = PositionSizePct           > 0 ? PositionSizePct           : 0.03,
        TimeStopBars              = TimeStopBars              > 0 ? TimeStopBars              : 999,  // 0 in old JSON → disabled
        TimeStopLossPct           = TimeStopLossPct           > 0 ? TimeStopLossPct           : 0.99,
        Fitness                   = Fitness,
    }.ClampToBounds();
}

class GridGenotypeDto
{
    public double AdxThreshold      { get; set; }
    public int    BbPeriod          { get; set; }
    public double BbWidthMaxPct     { get; set; }
    public int    EmaPeriod         { get; set; }
    public double GridStepAtrMult   { get; set; }
    public int    GridLevels        { get; set; }
    public double TakeProfitAtrMult { get; set; }
    public double HardStopAtrMult   { get; set; }
    public int    MaxHoldCandles    { get; set; }
    // BailOutAtrMult was MISSING from this DTO while being searched by the GA and consumed by the
    // simulator, so it was discarded on every save and reloaded as 0 — a gene the GA optimised and
    // the saved genotype never carried. Eleventh instance of the built-but-not-connected pattern.
    public double BailOutAtrMult    { get; set; }
    public double RungSellFrac      { get; set; }
    public double ReanchorAlpha     { get; set; }
    // Nullable on purpose: a missing JSON key must restore the LEGACY hardcoded -0.005, and a
    // plain double cannot tell "absent" from an explicit 0.0. Defaulting to 0 silently made every
    // pre-existing genotype STRICTER than it was (0 rejects any flat/falling bar, -0.005 tolerates
    // a 0.5% decline), which changed Grid's behaviour on load and broke a funding test.
    public double? SlopeThreshold   { get; set; }
    public int    SlopeLookback     { get; set; }
    public double Fitness           { get; set; }
    public double AtrLow            { get; init; } = 0.0;
    public double AtrHigh           { get; init; } = 9999.0;

    public static GridGenotypeDto From(GridGenotype g, FitnessConfig? cfg = null) => new()
    {
        AdxThreshold      = g.AdxThreshold,
        BbPeriod          = g.BbPeriod,
        BbWidthMaxPct     = g.BbWidthMaxPct,
        EmaPeriod         = g.EmaPeriod,
        GridStepAtrMult   = g.GridStepAtrMult,
        GridLevels        = g.GridLevels,
        TakeProfitAtrMult = g.TakeProfitAtrMult,
        HardStopAtrMult   = g.HardStopAtrMult,
        MaxHoldCandles    = g.MaxHoldCandles,
        BailOutAtrMult    = g.BailOutAtrMult,
        RungSellFrac      = g.RungSellFrac,
        ReanchorAlpha     = g.ReanchorAlpha,
        SlopeThreshold    = g.SlopeThreshold,
        SlopeLookback     = g.SlopeLookback,
        Fitness           = g.Fitness,
        AtrLow            = cfg?.AtrLow  ?? 0.0,
        AtrHigh           = cfg?.AtrHigh ?? 9999.0,
    };

    public GridGenotype ToGenotype() => new GridGenotype
    {
        AdxThreshold      = AdxThreshold      > 0 ? AdxThreshold      : 16.0,
        BbPeriod          = BbPeriod          > 0 ? BbPeriod          : 20,
        BbWidthMaxPct     = BbWidthMaxPct     > 0 ? BbWidthMaxPct     : 1.8,
        EmaPeriod         = EmaPeriod         > 0 ? EmaPeriod         : 50,
        GridStepAtrMult   = GridStepAtrMult   > 0 ? GridStepAtrMult   : 0.8,
        GridLevels        = GridLevels        > 0 ? GridLevels        : 2,
        TakeProfitAtrMult = TakeProfitAtrMult > 0 ? TakeProfitAtrMult : 1.5,
        HardStopAtrMult   = HardStopAtrMult   > 0 ? HardStopAtrMult   : 2.2,
        MaxHoldCandles    = MaxHoldCandles    > 0 ? MaxHoldCandles    : 96,
        // 2.0 mirrors the old default for genotypes saved before BailOutAtrMult was persisted.
        BailOutAtrMult    = BailOutAtrMult    > 0 ? BailOutAtrMult    : 2.0,
        // 0 = legacy behaviour for all three new mechanics, so old files load unchanged.
        RungSellFrac      = RungSellFrac,
        ReanchorAlpha     = ReanchorAlpha,
        SlopeThreshold    = SlopeThreshold ?? -0.005,
        SlopeLookback     = SlopeLookback     > 0 ? SlopeLookback     : 20,
        Fitness           = Fitness,
    }.ClampToBounds();
}
