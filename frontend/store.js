/* store.js — Gravity-gen2 shared data store.
   Fetches real JSON files from the repo, falls back to window.GRAV mock data.
   Defines the multi-variant genotype registry. Provides simple pub/sub.

   All pages load this before their own scripts. */
(function () {

  // ── Multi-variant genotype registry ───────────────────────────────────────
  // Each strategy can have multiple named variants.
  // active:true = currently used for live trading.
  // file:null   = variant slot — no trained genotype yet.
  // tags        = display chips (regime conditions this variant targets).
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

  // ── State ──────────────────────────────────────────────────────────────────
  const state = {
    loading: true,
    error:   null,
    // From real JSON files (fallback to GRAV mock):
    baseline:  {},   // backtest_baseline.json
    liveState: {},   // livetrain_state.json
    journal:   [],   // live_journal.json
    // Backtest results
    backtestResults: null,
    // Router genotype
    router:    null,
    guard:     null,
    // Loaded variant params: { FadeShort: [{ ...variant, params:{} }], ... }
    genotypes: {},
    // Registry (always available):
    variants:  VARIANTS,
  };

  const subscribers = [];
  function notify() { subscribers.forEach(fn => { try { fn(state); } catch(e){} }); }

  // ── JSON fetch with timeout & graceful fallback ────────────────────────────
  async function fetchJSON(path, fallback = null) {
    try {
      const ctrl = new AbortController();
      const tid = setTimeout(() => ctrl.abort(), 3000);
      const r = await fetch(path, { signal: ctrl.signal });
      clearTimeout(tid);
      if (!r.ok) throw new Error(r.status);
      return await r.json();
    } catch {
      return fallback;
    }
  }

  // ── Load all data ─────────────────────────────────────────────────────────
  async function load() {
    const G = window.GRAV || {};

    // Core files — parallel
    const [baseline, liveState, journal, router, guard, backtestResults] = await Promise.all([
      fetchJSON('../backtest_baseline.json',     G.backtest    || {}),
      fetchJSON('../livetrain_state.json',       G.training    || {}),
      fetchJSON('../live_journal.json',          []),
      fetchJSON('../genotypes/regime_router_genotype.json', null),
      fetchJSON('../genotypes/dynamic_guard_genotype.json', null),
      fetchJSON('../backtest_results.json',      null),
    ]);

    state.baseline        = baseline;
    state.liveState       = liveState;
    state.journal         = journal;
    state.router          = router;
    state.guard           = guard;
    state.backtestResults = backtestResults;

    // Merge real Sharpe / trades from baseline into GRAV mock if present
    if (baseline && baseline.sharpe && window.GRAV) {
      window.GRAV.backtest.sharpe = baseline.sharpe;
      window.GRAV.backtest.trades = baseline.trades || window.GRAV.backtest.trades;
      window.GRAV.backtest.timestamp = baseline.timestamp || window.GRAV.backtest.timestamp;
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
  }

  // ── Public API ─────────────────────────────────────────────────────────────
  function subscribe(fn) { subscribers.push(fn); }

  // Activate a specific variant (mark as active, deactivate others in strategy)
  function setActive(strategy, variantId) {
    if (!state.genotypes[strategy]) return;
    state.genotypes[strategy] = state.genotypes[strategy].map(v => ({
      ...v, active: v.id === variantId
    }));
    VARIANTS[strategy] = VARIANTS[strategy].map(v => ({ ...v, active: v.id === variantId }));
    notify();
  }

  // Add a new blank variant slot for a strategy
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

  window.GravStore = { state, load, subscribe, setActive, addVariant, VARIANTS, train, stopTraining };
})();
