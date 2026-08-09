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

    // Maximum gross notional across all concurrent positions, as a fraction of equity.
    //
    // ┌── THIS CONSTANT IS WHAT MAKES THE MISSING LIQUIDATION MODEL DEFENSIBLE ──────────┐
    // │ Nothing in this repo models liquidation. At 0.30 the book runs at 0.30x gross     │
    // │ leverage, so maintenance margin would need roughly a 40x larger position before   │
    // │ it could bind ahead of any modelled stop — every exit is a stop, target, trail or │
    // │ timeout, and that is true of reality too. Raise this past ~1.0 and the assumption │
    // │ dies SILENTLY: backtests keep printing clean stop-outs on paths where a real      │
    // │ account would already have been liquidated, so the cap manufactures the returns   │
    // │ it was raised to chase. Simulator.SimulatePortfolioExposureCapped now throws      │
    // │ above 1.0 rather than let that happen. Add a liquidation model first.             │
    // └──────────────────────────────────────────────────────────────────────────────────┘
    public const double MaxTotalExposurePct = 0.30;
    public const int MaxDirectionalConcurrent = 20;
    public const double MinMedianVolUsdM    = 0.5;

    // ── THE single slippage authority ────────────────────────────────────────────────────
    // Round-trip slippage in basis points, quoted at TradeCosts.ReferenceAtrPct (3% ATR).
    // 10 bps = 0.10%, a pessimistic taker-side estimate covering the mid-cap alt perps in
    // the BacktestCoins universe.
    //
    // Every slippage charge in the codebase is this number times a dimensionless shape; no
    // simulator carries its own slippage constant any more. It is charged EXACTLY ONCE per
    // trade, inside the simulators via TradeCosts, so GA fitness and backtest reporting price
    // the same trade identically — see the header of src/core/Simulator.cs for why trade level
    // is the only placement that achieves that. The portfolio layer no longer charges it, and
    // its leftover `slippageBps` parameters are ignored.
    //
    // Changing this number changes the objective the GAs select on. Genotypes trained under a
    // different value are not comparable and must be retrained. Results recorded before
    // 2026-08 assumed 0.0; results recorded before this unification saw a split cost model
    // (simulator-only in training, simulator + portfolio in reporting).
    public const double SlippageBps = 10.0;

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
