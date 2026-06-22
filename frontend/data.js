/* Gravity-gen2 — mock dashboard data.
   Shapes mirror the real repo artifacts:
   - backtest_baseline.json  { sharpe, trades, win_rate, avg_ret }
   - livetrain_state.json    { CycleCount, Population[].Fitness, ... }
   - StrategyStats.cs        t-test / bootstrap / Kelly / deflated Sharpe / dist
   - CrashAnalyser           COVID / LUNA / FTX windows
   - RegimeClassifier        Bull / Bear / Ranging / HighVol + confidence
   All numbers are illustrative but internally consistent. */
(function () {
  // deterministic pseudo-random walk for sample series
  function walk(seed, n, drift, vol) {
    let s = seed, v = 100, out = [v];
    for (let i = 1; i < n; i++) {
      s = (s * 9301 + 49297) % 233280;
      const r = s / 233280 - 0.5;
      v = v * (1 + drift + r * vol);
      out.push(v);
    }
    return out;
  }

  // equity curve (portfolio NAV, indexed 100), ~3.2yr of daily-ish points
  const equity = (function () {
    const raw = walk(7, 180, 0.0042, 0.028);
    // normalize so final ≈ 3.71x
    const target = 371, scale = target / raw[raw.length - 1] * 100;
    return raw.map(v => +(v * scale / 100).toFixed(2));
  })();

  // benchmark (BTC buy & hold) for the same window
  const benchmark = (function () {
    const raw = walk(31, 180, 0.0026, 0.045);
    const target = 214, scale = target / raw[raw.length - 1] * 100;
    return raw.map(v => +(v * scale / 100).toFixed(2));
  })();

  // monthly return distribution (for histogram), in %
  const monthlyReturns = [
    -4.1, 2.3, 6.8, 1.1, -2.2, 9.4, 3.7, 5.1, -1.4, 7.9, 2.0, -3.6,
    4.4, 8.2, 1.7, -0.9, 5.5, 3.1, -2.8, 6.0, 2.6, 4.9, -1.1, 7.2,
    3.3, 1.9, -3.0, 5.8, 2.4, 4.1, 6.7, -1.6, 3.8, 2.1, 5.0, -0.4,
    4.6, 3.2, 1.3, 8.8
  ];

  const strategies = [
    { key: "FadeShort", regime: "Always-on", dir: "Short", trades: 612, win: 58.4, avgRet: 0.74, sharpe: 2.41, pf: 1.62, ret: 118.3, maxDD: -9.2, kelly: 0.214, halfKelly: 0.107, dsr: 0.97, spark: walk(2, 60, 0.004, 0.03) },
    { key: "Grid",      regime: "Ranging",    dir: "Long",  trades: 498, win: 64.1, avgRet: 0.41, sharpe: 1.88, pf: 1.51, ret: 71.6,  maxDD: -6.8, kelly: 0.176, halfKelly: 0.088, dsr: 0.91, spark: walk(5, 60, 0.003, 0.022) },
    { key: "SwingLong", regime: "Bull",       dir: "Long",  trades: 341, win: 52.9, avgRet: 1.18, sharpe: 2.06, pf: 1.74, ret: 96.4,  maxDD: -12.1, kelly: 0.231, halfKelly: 0.116, dsr: 0.88, spark: walk(11, 60, 0.005, 0.04) },
    { key: "DipLong",   regime: "Bull",       dir: "Long",  trades: 387, win: 55.3, avgRet: 0.92, sharpe: 1.97, pf: 1.66, ret: 83.1,  maxDD: -10.4, kelly: 0.198, halfKelly: 0.099, dsr: 0.90, spark: walk(17, 60, 0.0045, 0.035) },
    { key: "FadeLong",  regime: "Bear",       dir: "Long",  trades: 224, win: 49.6, avgRet: 1.34, sharpe: 1.71, pf: 1.58, ret: 58.7,  maxDD: -13.6, kelly: 0.182, halfKelly: 0.091, dsr: 0.83, spark: walk(23, 60, 0.004, 0.05) }
  ];

  const backtest = {
    timestamp: "2026-05-31T13:51:28Z",
    sharpe: 8.63,           // portfolio, dual-TF (matches backtest_baseline.json)
    trades: 2062,
    winRate: 57.8,
    avgRet: 0.81,
    netReturn: 271.0,       // % over full window
    cagr: 56.4,
    maxDD: -14.7,
    calmar: 3.84,
    profitFactor: 1.63,
    expectancy: 0.81,
    valSplit: 20,
    coins: 93,
    window: "≈ 3.2 yr · 113 batches"
  };

  // OOS / never-seen-coins test
  const oos = {
    coins: 41,
    trades: 734,
    sharpe: 5.97,
    winRate: 55.1,
    avgRet: 0.69,
    netReturn: 142.0,
    maxDD: -16.2,
    degradation: -30.8     // % sharpe degradation vs in-sample
  };

  // Statistical validation (portfolio-level), from StrategyStats.cs
  const stats = {
    tTest:   { mean: 0.81, se: 0.094, t: 8.62, p: 0.0001, sig: "***" },
    boot:    { mean: 0.81, lo95: 0.63, hi95: 0.99, probPos: 1.0 },
    dist:    { skew: 0.42, exKurt: 3.71, cvar5: -3.84, tailRatio: 1.46 },
    kelly:   { full: 0.227, half: 0.114, cap: 0.05 },
    dsr:     0.96,                 // deflated Sharpe
    nTrials: 1000,
    vc:      { d: 15, n: 2062, ratio: 137.5, verdict: "good" },
    hoeffding: { bound: 0.71, minN05: 318, minN10: 80 }
  };

  // Crash / rally stress windows (CrashAnalyser historic scenarios)
  const stress = {
    crashes: [
      { label: "COVID", date: "Mar 2020", drop: -50, dur: "48h",  atr: 8, openPos: 6, exposure: 31.4, w: 1, l: 3, avgRet: -2.18, portHit: -11.4, clean: -9.4, slip: -75.2 },
      { label: "LUNA",  date: "May 2022", drop: -40, dur: "7d",   atr: 5, openPos: 5, exposure: 26.8, w: 2, l: 4, avgRet: -1.62, portHit: -8.7,  clean: -7.1, slip: -35.5 },
      { label: "FTX",   date: "Nov 2022", drop: -30, dur: "5d",   atr: 3, openPos: 4, exposure: 19.2, w: 2, l: 3, avgRet: -0.94, portHit: -4.3,  clean: -3.8, slip: -11.4 }
    ],
    rallies: [
      { label: "ETF run",   date: "Jan 2024", rise: 38, dur: "9d",  openPos: 7, exposure: 34.1, w: 6, l: 1, avgRet: 2.41, portHit: 12.8 },
      { label: "Halving",   date: "Apr 2024", rise: 22, dur: "6d",  openPos: 5, exposure: 24.6, w: 4, l: 1, avgRet: 1.86, portHit: 7.9 },
      { label: "Q4 melt-up", date: "Nov 2024", rise: 44, dur: "12d", openPos: 8, exposure: 38.7, w: 7, l: 1, avgRet: 2.74, portHit: 16.2 }
    ],
    worstCase: { peakTime: "2024-03-14 08:00 UTC", peakExposure: 38.7, gridStop: 1.5, swingStop: 4.9, cleanHit: -8.2, expandedHit: -24.6 }
  };

  // GA training progress (livetrain_state.json)
  const training = {
    cycleCount: 111,
    savedAt: "2026-05-26T11:56:09Z",
    popSize: 20,
    folds: 5,
    bestFitness: 1.84,
    // fitness over generations (best & mean)
    best: walk(3, 60, 0.006, 0.012).map((v, i) => +(0.6 + (v - 100) / 100 * 0.4 + i * 0.012).toFixed(3)),
    mean: walk(9, 60, 0.005, 0.02).map((v, i) => +(0.3 + (v - 100) / 100 * 0.3 + i * 0.010).toFixed(3)),
    // current population fitness spread (sorted)
    population: [1.84, 1.79, 1.71, 1.66, 1.62, 1.58, 1.51, 1.44, 1.39, 1.31, 1.27, 1.19, 1.08, 0.97, 0.91, 0.84, 0.71, 0.58, 0.41, -0.6],
    bayesOpt: { iters: 60, lift: 0.083 }
  };

  // Live papertrade state
  const live = {
    cycle: 111,
    lastRefresh: "2026-06-22T09:00:00Z",
    nextRefresh: "10:00 UTC",
    equityEur: 1247.83,
    equityStart: 1000,
    dayPnl: 1.42,
    openRisk: 28.6,          // % half-Kelly exposure
    regime: { state: "Bull", confidence: 0.74, duration: 38, btc: "Bull 0.74", eth: "Bull 0.61" },
    router: { FadeShort: true, Grid: false, SwingLong: true, DipLong: true, FadeLong: false, sizeMult: 0.92 },
    positions: [
      { sym: "SOLUSDT",  strat: "SwingLong", dir: "Long",  entry: 168.42, mark: 174.10, pnl: 3.37, age: "14h", size: 4.2, stop: 159.8 },
      { sym: "INJUSDT",  strat: "DipLong",   dir: "Long",  entry: 21.84,  mark: 22.61,  pnl: 3.53, age: "8h",  size: 3.6, stop: 20.4 },
      { sym: "ADAUSDT",  strat: "SwingLong", dir: "Long",  entry: 0.612,  mark: 0.598,  pnl: -2.29, age: "21h", size: 3.9, stop: 0.571 },
      { sym: "DOGEUSDT", strat: "FadeShort", dir: "Short", entry: 0.1842, mark: 0.1798, pnl: 2.39, age: "5h",  size: 2.8, stop: 0.1931 },
      { sym: "LINKUSDT", strat: "DipLong",   dir: "Long",  entry: 17.92,  mark: 18.04,  pnl: 0.67, age: "3h",  size: 3.1, stop: 16.8 }
    ],
    signals: [
      { time: "09:00", sym: "AVAXUSDT", strat: "SwingLong", action: "OPEN",  note: "RSI bull div + bullish BoS 15m" },
      { time: "09:00", sym: "SUIUSDT",  strat: "DipLong",   action: "OPEN",  note: "RSI dip 47 in uptrend + BoS" },
      { time: "08:00", sym: "OPUSDT",   strat: "SwingLong", action: "CLOSE", note: "trail stop · +4.8%" },
      { time: "07:00", sym: "WIFUSDT",  strat: "FadeShort", action: "SKIP",  note: "router gated · Bull regime" },
      { time: "06:00", sym: "TIAUSDT",  strat: "DipLong",   action: "CLOSE", note: "ATR target · +2.1%" }
    ]
  };

  // ── Distribution data for statistical visualisations ──────────────────────
  const normalPDF = (x, mu, sigma) =>
    Math.exp(-0.5 * ((x - mu) / sigma) ** 2) / (sigma * Math.sqrt(2 * Math.PI));

  // Bootstrap CI histogram — distribution of 5000 resampled portfolio means
  // Bootstrap dist of means ~ N(0.81, 0.094²); SE = 0.094 from t-test above
  const bootBins = (() => {
    const mu = 0.81, sigma = 0.094, nbins = 30;
    const lo = mu - 4.2 * sigma, hi = mu + 4.2 * sigma, bw = (hi - lo) / nbins;
    return Array.from({ length: nbins }, (_, i) => {
      const x = lo + (i + 0.5) * bw;
      return { x: +x.toFixed(3), h: +(normalPDF(x, mu, sigma) * bw * 5000).toFixed(0) };
    });
  })();

  // Standard normal PDF points x=[-5,5] — null distribution for t-test viz
  const tDistPts = Array.from({ length: 101 }, (_, i) => {
    const x = -5 + i * 0.1;
    return { x: +x.toFixed(2), y: +normalPDF(x, 0, 1).toFixed(5) };
  });

  // Walk-forward validation — 5-fold fitness scores per strategy
  // Consistency across folds = real edge, not GA overfitting to one window
  const wfvFolds = {
    FadeShort: [2.14, 1.89, 2.31, 1.72, 2.06],
    Grid:      [1.61, 1.84, 1.45, 1.98, 1.73],
    SwingLong: [1.82, 2.19, 1.63, 2.07, 1.88],
    DipLong:   [1.71, 1.92, 2.01, 1.53, 1.84],
    FadeLong:  [1.52, 1.63, 1.71, 1.44, 1.81]
  };

  // Per-strategy trade return distributions — histogram bins shaped by each
  // strategy's mean/vol profile, with mild right skew (wins > losses at tail)
  const stratDists = (() => {
    function hist(mu, sigma, n, nbins, skew = 0.28) {
      const lo = mu - 3.8 * sigma, hi = mu + 4.6 * sigma, bw = (hi - lo) / nbins;
      const cvar5 = +(mu - 1.645 * sigma).toFixed(2);
      const bins = Array.from({ length: nbins }, (_, i) => {
        const x = lo + (i + 0.5) * bw;
        const sk = x > mu ? 1 + skew * (x - mu) / sigma : Math.max(0.55, 1 - 0.18 * (mu - x) / sigma);
        const h = Math.max(0, normalPDF(x, mu, sigma) * sk * bw * n);
        return { x: +x.toFixed(2), h: +h.toFixed(0), neg: x < 0 };
      });
      return { lo, hi, bw, cvar5, bins };
    }
    return {
      FadeShort: hist(0.74, 2.10, 612, 18, 0.22),
      Grid:      hist(0.41, 1.40, 498, 16, 0.16),
      SwingLong: hist(1.18, 3.20, 341, 18, 0.34),
      DipLong:   hist(0.92, 2.60, 387, 18, 0.28),
      FadeLong:  hist(1.34, 3.80, 224, 18, 0.38)
    };
  })();

  window.GRAV = {
    equity, benchmark, monthlyReturns, strategies,
    backtest, oos, stats, stress, training, live,
    bootBins, tDistPts, wfvFolds, stratDists
  };
})();
