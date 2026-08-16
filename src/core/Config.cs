namespace TradingGA;

static class Config
{
    public const string FadeShortGenoFile     = "genotypes/fade_short_genotype.json";
    public const string GridGenoFile          = "genotypes/grid_best_genotype.json";
    public const string GridShortGenoFile     = "genotypes/grid_short_genotype.json";
    public const string FadeLongGenoFile      = "genotypes/fade_long_genotype.json";
    public const string DipLongGenoFile       = "genotypes/dip_long_genotype.json";
    public const string RipShortGenoFile      = "genotypes/rip_short_genotype.json";
    public const string RouterGenoFile        = "genotypes/regime_router_genotype.json";
    public const string SwingLongGenoFile     = "genotypes/swing_long_genotype.json";
    public const string ExitModifierGenoFile  = "genotypes/exit_modifier_genotype.json";
    public const string DrawdownGuardGenoFile = "genotypes/drawdown_guard_genotype.json";
    public const string DynamicGuardGenoFile  = "genotypes/dynamic_guard_genotype.json";
    public const string RotatorGenoFile       = "genotypes/vol_rotator_genotype.json";

    // Max gross notional as fraction of equity. 0.30 keeps liquidation model irrelevant.
    // Simulator throws above 1.0. Binds on ~70% of entries. Justified on tail loss (CorrelatedShock).
    // NOT const: GRAVITY_MAXEXP sweeps it for validation.
    public const double MaxTotalExposurePct = 0.30;
    public const int MaxDirectionalConcurrent = 20;
    public const double MinMedianVolUsdM    = 0.5;

    // Single slippage authority: 10 bps round-trip at 3% ATR. Charged once per trade via TradeCosts.
    // Changing this changes the GA objective — genotypes trained under a different value must retrain.
    public const double SlippageBps = 10.0;

    // Participation impact. OFF by default (GRAVITY_IMPACT=1 enables). The only size-aware cost.
    // Turning on invalidates all genotypes — deliberate retrain, not free improvement.
    public static readonly bool ChargeParticipationImpact =
        Environment.GetEnvironmentVariable("GRAVITY_IMPACT") == "1";

    // Calibration anchor for participation charge. Not a magnitude knob.
    public const double ReferenceEquityUsd = 100_000.0;

    // Max concurrent per strategy per coin. Raise only with multi-leg simulator + Trade.Symbol populated.
    public const int MaxPerSymbolConcurrent = 1;

    // Low-vol ATR band. WARNING: train/serve mismatch — lowvoltrain applies no ATR filter.
    // VariantRouter picks narrowest band containing current ratio.
    public const double LowVolAtrLow  = 0.0;
    public const double LowVolAtrHigh = 0.8;

    // Graded router sizing: ramps from floor to full across [GradedConfStart, GradedConfFull].
    // Not new genes — rides on existing confidence + thresholds. Promote to genes only if proven.
    public const double GradedConfStart = 0.20;   // at/below this → floor funding
    public const double GradedConfFull  = 0.80;   // at/above this → full funding
    public const double GradedSizeFloor = 0.30;   // never fund an active strategy below this

    public static readonly string[] BacktestCoins =
    [
        // large caps
        "BTCUSDT",   "ETHUSDT",   "SOLUSDT",   "BNBUSDT",   "XRPUSDT",
        "DOGEUSDT",  "ADAUSDT",   "AVAXUSDT",  "TRXUSDT",   "XLMUSDT",
        "DOTUSDT",   "LINKUSDT",  "ATOMUSDT",  "NEARUSDT",  "LTCUSDT",
        "BCHUSDT",   "ETCUSDT",   "VETUSDT",   "HBARUSDT",  "ALGOUSDT",
        "ICPUSDT",   "FILUSDT",   "STXUSDT",

        // L2s / newer L1s
        "MATICUSDT", "OPUSDT",    "ARBUSDT",   "APTUSDT",   "SUIUSDT",
        "INJUSDT",   "SEIUSDT",   "TIAUSDT",   "TONUSDT",   "KASUSDT",
        "FTMUSDT",   "CFXUSDT",   "ROSEUSDT",  "KSMUSDT",   "STRKUSDT",

        // DeFi blue chips
        "UNIUSDT",   "AAVEUSDT",  "MKRUSDT",   "SNXUSDT",   "CRVUSDT",
        "COMPUSDT",  "GMXUSDT",   "DYDXUSDT",  "RUNEUSDT",  "LDOUSDT",
        "SUSHIUSDT", "1INCHUSDT", "PENDLEUSDT","JUPUSDT",   "ENAUSDT",

        // AI / infra / data
        "TAOUSDT",   "RNDRUSDT",  "FETUSDT",   "WLDUSDT",   "PYTHUSDT",
        "EIGENUSDT", "ONDOUSDT",  "ARUSDT",    "OCEANUSDT",

        // NFT / gaming / meta
        "SANDUSDT",  "MANAUSDT",  "AXSUSDT",   "IMXUSDT",   "GALAUSDT",
        "APEUSDT",   "CHZUSDT",   "AUDIOUSDT", "ENJUSDT",

        // mid-caps
        "ZILUSDT",   "ANKRUSDT",  "LRCUSDT",   "SKLUSDT",   "BANDUSDT",
        "CTSIUSDT",  "HNTUSDT",   "LPTUSDT",   "STORJUSDT", "BATUSDT",
        "CELRUSDT",  "QNTUSDT",

        // memes / high-beta
        "WIFUSDT",       "MEMEUSDT",     "1000BONKUSDT", "1000PEPEUSDT",
        "1000FLOKIUSDT", "BOMEUSDT",     "1000SHIBUSDT", "NOTUSDT",
        "ORDIUSDT",      "TURBOUSDT",

        // new additions — high-volume or sector-diversifying
        "HYPEUSDT",  "MNTUSDT",   "POLUSDT",   "GRTUSDT",
        "GMTUSDT",   "STGUSDT",   "JTOUSDT",
    ];

    // OOS coins — never used in training. Volume threshold relaxed to $0.05M/h.
    public static readonly string[] OosCoins =
    [
        // L1s / mid-caps not in universe
        "CAKEUSDT",   "XTZUSDT",   "NEOUSDT",   "FLOWUSDT",  "KAVAUSDT",
        "DASHUSDT",   "THETAUSDT", "IOSTUSDT",  "ONTUSDT",   "SXPUSDT",

        // DeFi not in universe
        "YFIUSDT",    "BNTUSDT",   "KNCUSDT",   "WOOUSDT",   "ZRXUSDT",
        "COTIUSDT",   "CKBUSDT",   "CHRUSDT",

        // infra / data / oracle not in universe
        "TRBUSDT",    "SAFEUSDT",  "IDUSDT",    "NKNUSDT",   "SFPUSDT",

        // newer / gaming / NFT not in universe
        "RONINUSDT",  "BLURUSDT",  "ARKMUSDT",  "LOOKSUSDT", "BICOUSDT",

        // alt-L1s / older chains
        "EGLDUSDT",   "IOTAUSDT",  "ZECUSDT",   "WAVESUSDT", "QTUMUSDT",
        "KLAYUSDT",   "GLMRUSDT",  "ASTRUSDT",  "METISUSDT", "CELOUSDT",
        "NULSUSDT",   "LSKUSDT",

        // DeFi — AMMs, lending, yield (not in BacktestCoins)
        "BALUSDT",    "CVXUSDT",   "SPELLUSDT", "MASKUSDT",  "FXSUSDT",
        "RDNTUSDT",   "JOEUSDT",   "ALPACAUSDT","DODOUSDT",  "PERPUSDT",
        "LQTYUSDT",   "SSVUSDT",   "XVSUSDT",   "SYNUSDT",   "QIUSDT",
        "RSRUSDT",    "ALPHAUSDT", "FLMUSDT",

        // gaming / social / NFT
        "ILVUSDT",    "SLPUSDT",   "ALICEUSDT", "YGGUSDT",   "WEMIXUSDT",
        "PEOPLEUSDT", "GALUSDT",   "RAREUSDT",

        // AI / infra / oracle
        "AGIXUSDT",   "NMRUSDT",   "GTCUSDT",   "PHAUSDT",   "ARPAUSDT",
        "FRONTUSDT",  "POLYXUSDT", "DIAUSDT",   "DUSKUSDT",  "STPTUSDT",

        // utility / misc mid-caps
        "BTTUSDT",    "SUNUSDT",   "WINUSDT",   "CVCUSDT",   "TWTUSDT",
        "ACHUSDT",    "OMUSDT",    "HIGHUSDT",  "REQUSDT",   "POWRUSDT",
        "DENTUSDT",   "RADUSDT",   "UTKUSDT",   "MDTUSDT",   "SUPERUSDT",
        "BONDUSDT",   "IOTXUSDT",  "FORTHUSDT", "ADXUSDT",   "BAKEUSDT",
        "LITUSDT",    "MOVRUSDT",  "MINAUSDT",  "XPRTUSDT",
    ];
}
