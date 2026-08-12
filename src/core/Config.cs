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
    // NOT const: GRAVITY_MAXEXP sweeps it so the number can be VALIDATED rather than asserted.
    // Measured, this cap binds on 70-90% of entries with peak gross sitting flush against it — it
    // is the most active risk control in the system and the primary determinant of deployed
    // capital, not a distant safety margin. A constant that shapes returns on four trades in five
    // deserves evidence, and until this sweep there was none: 0.30 was chosen to keep the missing
    // liquidation model irrelevant, which is a different job from sizing the book correctly.
    // SWEPT, then fixed. Raising it is not a tuning decision, it is a change to what the simulator
    // is capable of representing:
    //
    //   cap    5%cap return   DD      Kelly return   DD      peak gross    cap binds on
    //   0.30      133.0%     2.3%        172.8%     4.1%       30.1%          77.7%
    //   0.50      235.5%     2.3%        380.8%     4.1%          —              —
    //   0.80      388.0%     2.3%        775.5%     4.1%          —              —
    //   1.00      496.3%     3.1%       1267.4%     4.1%      100.3%          37.8%
    //
    // Return scales 3.7-7.3x while drawdown is FLAT. That is not diversification, it is the model
    // paying for risk it cannot see: there is no liquidation, no margin call, no forced
    // deleveraging, and MaxDrawdownPct is computed on realized equity at trade close, so N
    // simultaneously underwater positions register as nothing until they exit. At 0.30x gross those
    // omissions are arithmetically irrelevant (equity is 3.3x notional; liquidation cannot bind
    // ahead of a stop). At 1.0x they are the dominant risk and the backtest is silent about it.
    //
    // So 0.30 is not "the optimal risk level" — it is the largest number for which this simulator
    // is honest. Those are different claims and only the second one is evidenced. Raising it
    // requires the liquidation/margin model first; the sweep above is what that model has to
    // reproduce before any higher number is believable.
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
    // the portfolio entry points take no slippage parameter at all.
    //
    // Changing this number changes the objective the GAs select on. Genotypes trained under a
    // different value are not comparable and must be retrained. Results recorded before
    // 2026-08 assumed 0.0; results recorded before this unification saw a split cost model
    // (simulator-only in training, simulator + portfolio in reporting).
    public const double SlippageBps = 10.0;

    // Max concurrent positions per strategy PER COIN. 1 preserves today's behaviour exactly —
    // every signal simulator holds one position per coin via its `inTrade` flag, so this merely
    // makes that implicit property explicit and enforceable at the risk layer. Raise it only
    // alongside a multi-leg simulator, and never without PortfolioReplay.Trade.Symbol populated
    // (the cap warns and no-ops when symbols are missing rather than silently passing).
    public const int MaxPerSymbolConcurrent = 1;

    // ATR-ratio band claimed by the low-vol variant files. VariantRouter.Select picks the
    // NARROWEST band containing the current ratio, so this is what stops a lowvol genotype
    // from tying the base band [0, 9999] and shadowing the base genotype outright.
    //
    // WARNING — this band is an ASSERTION ABOUT ROUTING, not a property of the training.
    // lowvoltrain applies no ATR filter of any kind: its only screen is median volume, and
    // FadeShortGA.RunLowVol contains no ATR reference. The low-vol genotypes are fit on the
    // SAME data as the base genotypes and differ only in their parameter box (BoundsLowVol).
    // Serving them exclusively below 0.8 is therefore a train/serve mismatch. See CLAUDE.md.
    public const double LowVolAtrLow  = 0.0;
    public const double LowVolAtrHigh = 0.8;

    // Graded router sizing. IsActive answers "may this strategy trade at all"; the graded curve
    // answers "how much", so a barely-confirmed regime funds smaller than a deeply-confirmed one.
    // A boolean gate is a cliff, and cliffs are the defect shape this codebase keeps finding
    // (the n=100 tail gate, rrMult at rr=1, the PF<1.3 Sharpe gate) — the GA is rewarded for
    // sitting just past the edge rather than for being right.
    //
    // Deliberately NOT new genes: the curve rides on the confidence the classifier already emits
    // and the thresholds the router already has, so nothing is added to a parameter count whose
    // effective sample is dozens of independent regime episodes, not thousands of trades.
    // Promote to genes only if the fixed curve demonstrably beats the boolean baseline.
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
