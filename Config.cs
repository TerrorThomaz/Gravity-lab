namespace TradingGA;

static class Config
{
    public const string FadeShortGenoFile = "fade_short_genotype.json";
    public const string GridGenoFile      = "grid_best_genotype.json";
    public const string MomLongGenoFile   = "mom_long_genotype.json";
    public const string FadeLongGenoFile  = "fade_long_genotype.json";
    public const string DipLongGenoFile   = "dip_long_genotype.json";
    public const string RouterGenoFile    = "regime_router_genotype.json";
    public const string SwingLongGenoFile = "swing_long_genotype.json";
    public const string ExitModifierGenoFile   = "exit_modifier_genotype.json";
    public const string DrawdownGuardGenoFile  = "drawdown_guard_genotype.json";
    public const string DynamicGuardGenoFile   = "dynamic_guard_genotype.json";

    public const double MaxTotalExposurePct = 0.30;
    public const double MinMedianVolUsdM    = 0.5;

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

    // Out-of-sample coins — never used in any strategy training or BacktestCoins.
    // Volume threshold is relaxed to $0.05M/h (vs $0.5M for production) since
    // OOS testing validates strategy edge, not execution feasibility.
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
