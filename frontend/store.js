/* store.js — Gravity-gen2 shared data store.
   Fetches real data from API + JSON files, falls back to window.GRAV mock data.
   Defines the multi-variant genotype registry. Provides simple pub/sub.

   All pages load this before their own scripts. */
(function () {

  // ── Multi-variant genotype registry ───────────────────────────────────────
  const VARIANTS = {
    FadeShort: [
      { id:'default', label:'Default',  file:'../genotypes/fade_short_genotype.json',
        active:true,  tags:['always-on'], atrRange:[0, 9999],
        notes:'Primary trained genotype — 93 coins, 5-fold WFV.' },
      { id:'hival',   label:'High Vol', file:null, active:false,
        tags:['high-vol','draft'], atrRange:[1.5, 9999],
        notes:'For ATR expansion regimes. Tighter stop, faster RSI. Not yet trained.' },
      { id:'loval',   label:'Low Vol',  file:null, active:false,
        tags:['low-vol','draft'],  atrRange:[0, 0.8],
        notes:'Ranging / compressed markets. Wider parameters, lower size.' },
    ],
    Grid: [
      { id:'default', label:'Default',    file:'../genotypes/grid_best_genotype.json',
        active:true,  tags:['ranging'], atrRange:[0, 9999], notes:'ADX-gated ranging genotype.' },
      { id:'tight',   label:'Tight Grid', file:null, active:false,
        tags:['low-atr','draft'], atrRange:[0, 0.8], notes:'Smaller step size for low-ATR compression periods.' },
    ],
    SwingLong: [
      { id:'default', label:'Default', file:'../genotypes/swing_long_genotype.json',
        active:true,  tags:['bull'], atrRange:[0, 9999], notes:'Bull-regime RSI divergence + BoS. 93 coins.' },
      { id:'liquid',  label:'Liquid',  file:'../genotypes/swing_best_genotype_liquid.json',
        active:false, tags:['bull','liquid'], atrRange:[0, 9999], notes:'Large-cap only.' },
      { id:'mid',     label:'Mid Cap', file:'../genotypes/swing_best_genotype_mid.json',
        active:false, tags:['bull','mid-cap'], atrRange:[0, 9999], notes:'Mid-cap universe.' },
      { id:'bull_run',label:'Bull Run',file:null, active:false,
        tags:['bull','draft'], atrRange:[0, 9999], notes:'Aggressive params for confirmed bull breakout.' },
    ],
    DipLong: [
      { id:'default', label:'Default',  file:'../genotypes/dip_long_genotype.json',
        active:true,  tags:['bull'], atrRange:[0, 9999], notes:'RSI dip 40–55 in established uptrend + BoS.' },
      { id:'deep',    label:'Deep Dip', file:null, active:false,
        tags:['bull','draft'], atrRange:[0, 9999], notes:'Deeper RSI retrace (30–45).' },
    ],
    FadeLong: [
      { id:'default', label:'Default',      file:'../genotypes/fade_long_genotype.json',
        active:true,  tags:['bear'], atrRange:[0, 9999], notes:'Bear-regime oversold bounce.' },
      { id:'cascade', label:'Bear Cascade', file:null, active:false,
        tags:['bear','draft'], atrRange:[1.3, 9999], notes:'Tuned for multi-wave bear cascades.' },
    ],
  };

  const STRATEGY_META = {
    FadeShort: { regime: 'Always-on', dir: 'Short' },
    Grid:      { regime: 'Ranging',   dir: 'Long'  },
    SwingLong: { regime: 'Bull',      dir: 'Long'  },
    DipLong:   { regime: 'Bull',      dir: 'Long'  },
    FadeLong:  { regime: 'Bear',      dir: 'Long'  },
  };

  // ── State ──────────────────────────────────────────────────────────────────
  const state = {
    loading: true,
    error:   null,
    baseline:  {},
    liveState: {},
    journal:   [],
    backtestResults: null,
    router:    null,
    guard:     null,
    genotypes: {},
    variants:  VARIANTS,
  };

  const subscribers = [];
  function notify() { subscribers.forEach(fn => { try { fn(state); } catch(e){} }); }

  // ── JSON fetch with timeout & graceful fallback ────────────────────────────
  async function fetchJSON(path, fallback = null) {
    try {
      const ctrl = new AbortController();
      const tid = setTimeout(() => ctrl.abort(), 8000);
      const r = await fetch(path, { signal: ctrl.signal });
      clearTimeout(tid);
      if (!r.ok) throw new Error(r.status);
      return await r.json();
    } catch {
      return fallback;
    }
  }

  // ── Merge fulltest data into window.GRAV ──────────────────────────────────
  function mergeFulltest(ft) {
    const G = window.GRAV;
    if (!ft || !G) return;

    const bt = ft.backtest || {};

    // Backtest summary
    if (bt.sharpe !== undefined) {
      G.backtest.sharpe       = bt.sharpe;
      G.backtest.trades       = bt.trades       ?? G.backtest.trades;
      G.backtest.winRate      = bt.winRate      ?? G.backtest.winRate;
      G.backtest.maxDD        = bt.maxDD        ?? G.backtest.maxDD;
      G.backtest.cagr         = bt.cagr         ?? G.backtest.cagr;
      G.backtest.netReturn    = bt.netReturn    ?? G.backtest.netReturn;
      G.backtest.avgRet       = bt.avgRet       ?? G.backtest.avgRet;
      G.backtest.profitFactor = bt.profitFactor ?? G.backtest.profitFactor;
      G.backtest.calmar       = bt.calmar       ?? G.backtest.calmar;
      G.backtest.coins        = bt.coins        ?? G.backtest.coins;
      G.backtest.valSplit     = bt.valSplit     ?? G.backtest.valSplit;
      G.backtest.window       = bt.window       ?? G.backtest.window;
      G.backtest.timestamp    = ft.timestamp    || G.backtest.timestamp;
    }

    // Equity curve
    if (ft.equityCurve && ft.equityCurve.length > 0) {
      G.equity = ft.equityCurve.map(p => p.v);
    }
    if (ft.benchmarkCurve && ft.benchmarkCurve.length > 0) {
      G.benchmark = ft.benchmarkCurve.map(p => p.v);
    }

    // Monthly returns
    if (ft.monthlyReturns && ft.monthlyReturns.length > 0) {
      G.monthlyReturns = ft.monthlyReturns.map(m => m.ret);
    }

    // Per-strategy detailed stats (prefer valDetailed, fall back to val)
    const detail = ft.valDetailed || ft.val;
    if (detail) {
      const keys = ['FadeShort', 'Grid', 'SwingLong', 'DipLong', 'FadeLong'];
      G.strategies = keys.map(key => {
        const d = detail[key];
        const meta = STRATEGY_META[key] || {};
        if (!d || !d.trades) {
          const mock = G.strategies.find(s => s.key === key);
          return mock || { key, ...meta, trades: 0, win: 0, avgRet: 0, sharpe: 0, pf: 0, ret: 0, maxDD: 0, kelly: 0, halfKelly: 0, dsr: 0, spark: [] };
        }
        return {
          key,
          regime: meta.regime || '—',
          dir:    meta.dir || '—',
          trades:    d.trades,
          win:       d.winRate,
          avgRet:    d.avgRet,
          sharpe:    d.sharpe    ?? 0,
          pf:        d.pf        ?? 0,
          ret:       d.ret       ?? 0,
          maxDD:     d.maxDD     ? -Math.abs(d.maxDD) : 0,
          kelly:     d.kelly     ?? 0,
          halfKelly: d.halfKelly ?? 0,
          dsr:       d.dsr       ?? 0,
          spark:     d.sparkline || G.strategies.find(s => s.key === key)?.spark || [],
        };
      });
    }

    // OOS stats
    if (ft.oosDetailed || ft.oos) {
      const od = ft.oosDetailed || ft.oos;
      const keys = ['FadeShort', 'Grid', 'SwingLong', 'DipLong', 'FadeLong'];
      let oosTrades = 0, oosWins = 0, oosRetSum = 0;
      keys.forEach(k => {
        const d = od[k];
        if (d && d.trades) {
          oosTrades += d.trades;
          oosWins += Math.round(d.trades * (d.winRate || 0) / 100);
          oosRetSum += (d.ret || d.avgRet * d.trades || 0);
        }
      });
      if (oosTrades > 0) {
        G.oos.trades  = oosTrades;
        G.oos.winRate = Math.round(oosWins / oosTrades * 100 * 10) / 10;
        G.oos.avgRet  = Math.round(oosRetSum / oosTrades * 100) / 100;
        if (ft.portfolio?.oos5pct?.ret !== undefined) {
          G.oos.netReturn = ft.portfolio.oos5pct.ret;
          G.oos.maxDD = ft.portfolio.oos5pct.maxDD ? -Math.abs(ft.portfolio.oos5pct.maxDD) : G.oos.maxDD;
        }
        // Compute OOS sharpe: use individual trade returns
        const oosAllRets = [];
        keys.forEach(k => { const d = od[k]; if (d && d.avgRet && d.trades) for (let i = 0; i < d.trades; i++) oosAllRets.push(d.avgRet); });
        if (oosAllRets.length >= 5) {
          const mean = oosAllRets.reduce((a, b) => a + b, 0) / oosAllRets.length;
          const variance = oosAllRets.reduce((a, v) => a + (v - mean) ** 2, 0) / oosAllRets.length;
          const std = Math.sqrt(variance);
          G.oos.sharpe = std > 0 ? Math.round(mean / std * 100) / 100 : 0;
        }
        if (bt.sharpe && G.oos.sharpe) {
          G.oos.degradation = Math.round((1 - G.oos.sharpe / bt.sharpe) * -100 * 10) / 10;
        }
      }
    }

    // Statistical validation
    if (ft.stats) {
      const s = ft.stats;
      G.stats = {
        tTest: {
          mean: s.tTest?.mean ?? 0,
          se:   s.tTest?.se ?? 0,
          t:    s.tTest?.t ?? 0,
          p:    s.tTest?.p ?? 1,
          sig:  s.tTest?.sig ? (s.tTest.p < 0.01 ? '***' : '*') : '',
        },
        boot: {
          mean:    s.boot?.mean ?? 0,
          lo95:    s.boot?.lo95 ?? 0,
          hi95:    s.boot?.hi95 ?? 0,
          probPos: s.boot?.probPos ?? 0,
        },
        dist: {
          skew:      s.dist?.skew ?? 0,
          exKurt:    s.dist?.exKurt ?? 0,
          cvar5:     s.dist?.cvar5 ?? 0,
          tailRatio: s.dist?.tailRatio ?? 0,
        },
        kelly: {
          full: s.kelly?.full ?? 0,
          half: s.kelly?.half ?? 0,
          cap:  s.kelly?.cap  ?? 0.05,
        },
        dsr:       s.dsr ?? 0,
        nTrials:   s.nTrials ?? 1000,
        vc:        s.vc ?? G.stats.vc,
        hoeffding: s.hoeffding ?? G.stats.hoeffding,
      };
    }

    // Bootstrap histogram bins
    if (ft.bootBins && ft.bootBins.length > 0) {
      G.bootBins = ft.bootBins.map(b => ({ x: b.x, h: b.y }));
    }

    // WFV fold scores
    if (ft.wfvFolds) {
      G.wfvFolds = ft.wfvFolds;
    }

    // Stress / crash data
    if (ft.stress) {
      if (ft.stress.crashes && ft.stress.crashes.length > 0) {
        G.stress.crashes = ft.stress.crashes.map(c => ({
          label: c.label,
          date:  new Date(c.start).toLocaleDateString('en-US', { month: 'short', year: 'numeric' }),
          drop:  c.drawdown,
          dur:   c.duration + 'h',
          openPos:  c.openPos  ?? 0,
          exposure: c.exposure ?? 0,
          w:        c.w        ?? 0,
          l:        c.l        ?? 0,
          avgRet:   c.avgRet   ?? 0,
          clean:    c.clean    ?? 0,
          slip:     c.slip     ?? 0,
          ...c,
        }));
      }
      if (ft.stress.rallies && ft.stress.rallies.length > 0) {
        G.stress.rallies = ft.stress.rallies.map(r => ({
          label: r.label,
          date:  new Date(r.start).toLocaleDateString('en-US', { month: 'short', year: 'numeric' }),
          rise:  r.recovery,
          dur:   r.duration + 'h',
          w:       r.w       ?? 0,
          l:       r.l       ?? 0,
          avgRet:  r.avgRet  ?? 0,
          portHit: r.portHit ?? 0,
          ...r,
        }));
      }
      if (ft.stress.worstCase) {
        G.stress.worstCase = ft.stress.worstCase;
      }
    }

    // Per-strategy return distributions
    if (ft.stratDists) {
      const keys = ['FadeShort', 'Grid', 'SwingLong', 'DipLong', 'FadeLong'];
      const newDists = {};
      keys.forEach(k => {
        const sd = ft.stratDists[k];
        if (sd && sd.bins && sd.bins.length > 0) {
          const bins = sd.bins.map(b => ({ x: b.x, h: b.y, neg: b.x < 0 }));
          newDists[k] = {
            bins,
            cvar5: sd.cvar5,
            lo:    bins[0].x,
            hi:    bins[bins.length - 1].x,
            bw:    bins.length > 1 ? bins[1].x - bins[0].x : 1,
          };
        }
      });
      if (Object.keys(newDists).length > 0) {
        G.stratDists = { ...G.stratDists, ...newDists };
      }
    }
  }

  // ── Merge live state into window.GRAV ─────────────────────────────────────
  function mergeLive(live) {
    const G = window.GRAV;
    if (!live || !G) return;

    if (live.timestamp) {
      G.live.lastRefresh = live.timestamp;
    }
    G.live.nextRefresh = live.nextRefresh || G.live.nextRefresh;
    G.live.cycle = live.cycle ?? G.live.cycle;

    if (live.process) {
      G.live.process = live.process;
    }

    if (live.positions && live.positions.length >= 0) {
      G.live.positions = live.positions.map(p => ({
        sym:   p.sym,
        strat: p.strat,
        dir:   p.dir,
        entry: p.entry,
        mark:  p.mark,
        pnl:   p.pnl,
        age:   p.age,
        size:  0,
        stop:  p.stop,
        target: p.target,
        trailArmed: p.trailArmed,
      }));
    }

    if (live.signals && live.signals.length > 0) {
      G.live.signals = live.signals;
    }

    if (live.regime) {
      G.live.regime = {
        state:      live.regime.state,
        confidence: live.regime.confidence,
        duration:   live.regime.duration,
        btc:        `${live.regime.state} ${(live.regime.confidence * 100).toFixed(0)}%`,
        eth:        '',
      };
      G.live.router = {
        FadeShort: live.regime.FadeShort ?? true,
        Grid:      live.regime.Grid ?? false,
        SwingLong: live.regime.SwingLong ?? false,
        DipLong:   live.regime.DipLong ?? false,
        FadeLong:  live.regime.FadeLong ?? false,
        sizeMult:  live.regime.sizeMult ?? 1,
      };
    }

    G.live.openRisk = live.openCount ?? G.live.positions.length;
  }

  // ── Load all data ─────────────────────────────────────────────────────────
  async function load() {
    const G = window.GRAV || {};

    // Core files — parallel
    const [baseline, liveState, journal, router, guard, backtestResults, fulltest, liveData] = await Promise.all([
      fetchJSON('/api/baseline', null).then(r => r || fetchJSON('../backtest_baseline.json', G.backtest || {})),
      fetchJSON('../livetrain_state.json',       G.training    || {}),
      fetchJSON('../live_journal.json',          []),
      fetchJSON('../genotypes/regime_router_genotype.json', null),
      fetchJSON('../genotypes/dynamic_guard_genotype.json', null),
      fetchJSON('../backtest_results.json',      null),
      fetchJSON('/api/fulltest',                 null),
      fetchJSON('/api/live',                     null),
    ]);

    state.baseline        = baseline;
    state.liveState       = liveState;
    state.journal         = journal;
    state.router          = router;
    state.guard           = guard;
    state.backtestResults = backtestResults;

    // Merge real baseline into GRAV mock
    if (baseline && baseline.sharpe && window.GRAV) {
      window.GRAV.backtest.sharpe = baseline.sharpe;
      window.GRAV.backtest.trades = baseline.trades || window.GRAV.backtest.trades;
      window.GRAV.backtest.timestamp = baseline.timestamp || window.GRAV.backtest.timestamp;
    }

    // Merge fulltest visualization data into GRAV
    if (fulltest && window.GRAV) {
      mergeFulltest(fulltest);
    }

    // Merge live papertrade state
    if (liveData && window.GRAV) {
      mergeLive(liveData);
    }

    // Merge real cycle / population from liveState into GRAV mock
    if (liveState && liveState.CycleCount && window.GRAV) {
      window.GRAV.training.cycleCount = liveState.CycleCount;
      if (liveState.Population && liveState.Population.length) {
        window.GRAV.training.population = liveState.Population
          .map(p => +(p.Fitness || 0).toFixed(3))
          .sort((a, b) => b - a);
        const fitnesses = window.GRAV.training.population.filter(f => f > 0);
        if (fitnesses.length) window.GRAV.training.bestFitness = fitnesses[0];
      }
    }

    // Load each variant's genotype file
    for (const [strategy, variants] of Object.entries(VARIANTS)) {
      state.genotypes[strategy] = await Promise.all(
        variants.map(async v => {
          if (!v.file) return { ...v, params: null };
          const params = await fetchJSON(v.file.startsWith('../') ? v.file : '../' + v.file, null);
          return { ...v, params };
        })
      );
    }

    state.loading = false;
    notify();

    // Poll baseline + live data every 60s
    setInterval(async () => {
      const [fresh, freshLive] = await Promise.all([
        fetchJSON('/api/baseline', null),
        fetchJSON('/api/live', null),
      ]);
      let changed = false;
      if (fresh && fresh.sharpe !== undefined) {
        state.baseline = fresh;
        changed = true;
      }
      if (freshLive && freshLive.timestamp && window.GRAV) {
        mergeLive(freshLive);
        changed = true;
      }
      if (changed) notify();
    }, 60_000);

    // Poll fulltest every 5 min (heavier payload)
    setInterval(async () => {
      const fresh = await fetchJSON('/api/fulltest', null);
      if (fresh && fresh.timestamp && window.GRAV) {
        mergeFulltest(fresh);
        notify();
      }
    }, 300_000);
  }

  async function refreshBaseline() {
    try {
      await fetch('/api/baseline/refresh', { method: 'POST' });
    } catch {}
  }

  // ── Public API ─────────────────────────────────────────────────────────────
  function subscribe(fn) { subscribers.push(fn); }

  function setActive(strategy, variantId) {
    if (!state.genotypes[strategy]) return;
    state.genotypes[strategy] = state.genotypes[strategy].map(v => ({
      ...v, active: v.id === variantId
    }));
    VARIANTS[strategy] = VARIANTS[strategy].map(v => ({ ...v, active: v.id === variantId }));
    notify();
  }

  function addVariant(strategy, id, label, notes = '', tags = []) {
    const slot = { id, label, file: null, active: false, tags: [...tags, 'draft'], notes };
    VARIANTS[strategy] = [...(VARIANTS[strategy] || []), slot];
    state.genotypes[strategy] = [...(state.genotypes[strategy] || []), { ...slot, params: null }];
    notify();
  }

  async function train(strategy, variant, fitnessConfig) {
    const r = await fetch('/api/train', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ strategy, variant, fitnessConfig }),
    });
    return r.json();
  }

  async function stopTraining() {
    const r = await fetch('/api/stop', { method: 'POST' });
    return r.json();
  }

  window.GravStore = { state, load, subscribe, setActive, addVariant, VARIANTS, train, stopTraining, refreshBaseline };
})();
