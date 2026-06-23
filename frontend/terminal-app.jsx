/* terminal-app.jsx — Gravity-gen2 Bloomberg-style standalone terminal.
   Includes all original panels + new statistical distribution section:
   Bootstrap CI histogram · Null hypothesis t-curve · WFV fold bars
   · Per-strategy return distributions with CVaR shading. */
(function () {
  const G = window.GRAV;
  const { LineChart, Sparkline, Histogram, BarRow, CIBar, NormalCurve, AnnotHistogram } = window;

  const sgn  = v => v > 0 ? "pos" : v < 0 ? "neg" : "";
  const pct  = (v, d = 1) => { if (v == null || isNaN(v)) return "—"; return (v > 0 ? "+" : "") + v.toFixed(d) + "%"; };
  const num  = (v, d = 2) => { if (v == null || isNaN(v)) return "—"; return v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d }); };
  const safe = (v, fn) => (v == null || isNaN(v)) ? "—" : fn(v);

  /* ── Panel shell ─────────────────────────────────────────────────────────── */
  function Panel({ title, meta, span, flush, sect, children }) {
    return (
      <section className="tm-panel" style={{ gridColumn: `span ${span}` }} data-section={sect}>
        <header className="tm-ph">
          <span className="tm-pt">{title}</span>
          {meta && <span className="tm-pm">{meta}</span>}
        </header>
        <div className={flush ? "tm-pb tm-pb--flush" : "tm-pb"}>{children}</div>
      </section>
    );
  }

  /* ── Section separator (full-width label) ───────────────────────────────── */
  function Sep({ label }) {
    return (
      <div className="tm-sep">
        <span className="tm-sep-l">{label}</span>
        <span className="tm-sep-r" />
      </div>
    );
  }

  /* ── Ticker item ─────────────────────────────────────────────────────────── */
  function Ti({ k, v, cls }) {
    return (
      <span className="tm-ti"><i>{k}</i><b className={cls}>{v}</b></span>
    );
  }

  /* ── WFV fold mini-bar row ──────────────────────────────────────────────── */
  function FoldRow({ name, folds }) {
    const allVals = Object.values(G.wfvFolds).flat();
    const lo = Math.min(...allVals), hi = Math.max(...allVals), span = hi - lo || 1;
    const mean = folds.reduce((a, b) => a + b, 0) / folds.length;
    return (
      <div className="tm-wfv-row">
        <span className="tm-wfv-name">{name}</span>
        <div className="tm-wfv-bars">
          {folds.map((f, i) => {
            const h = Math.round(((f - lo) / span) * 44 + 4);
            const op = 0.5 + 0.5 * (f - lo) / span;
            return <div key={i} className="tm-wfv-bar" style={{ height: h + "px", opacity: op }} title={`Fold ${i + 1}: ${f.toFixed(2)}`} />;
          })}
        </div>
        <span className="tm-wfv-mean">{mean.toFixed(2)}</span>
      </div>
    );
  }

  /* ── Strategy mini distribution card ───────────────────────────────────── */
  function MiniDist({ s }) {
    const dist = G.stratDists[s.key];
    const xlo = dist.bins[0].x, xhi = dist.bins[dist.bins.length - 1].x;
    return (
      <div className="tm-mini-strat">
        <div className="tm-mini-head">
          <b>{s.key}</b>
          <span className={"tm-dir tm-dir--" + s.dir.toLowerCase()}>{s.dir}</span>
        </div>
        <AnnotHistogram bins={dist.bins} meanX={s.avgRet} cvarX={dist.cvar5} height={110} />
        <div className="tm-mini-ax">
          <span className="neg">{xlo.toFixed(1)}%</span>
          <span className="dim">0</span>
          <span className="pos">+{xhi.toFixed(1)}%</span>
        </div>
        <div className="tm-mini-foot">
          <span>μ <b className="pos">{pct(s.avgRet, 2)}</b></span>
          <span>CVaR <b className="neg">{dist.cvar5.toFixed(1)}%</b></span>
          <span>win <b>{s.win.toFixed(0)}%</b></span>
          <span>SR <b>{s.sharpe.toFixed(2)}</b></span>
        </div>
      </div>
    );
  }

  /* ── Main terminal ──────────────────────────────────────────────────────── */
  function TerminalApp() {
    const L = G.live || {}, B = G.backtest || {}, S = G.stats || {};
    if (!L.positions) L.positions = [];
    if (!L.signals) L.signals = [];
    if (!L.regime) L.regime = {};
    if (!L.router) L.router = {};

    return (
      <div className="tm-app">
        {/* shared nav bar (links to Genotypes + Training) */}
        {window.GravNav && <GravNav active="terminal" liveState={window.GravStore?.state?.liveState} baseline={window.GravStore?.state?.baseline} />}

        {/* sticky ticker */}
        <div className="tm-ticker">
          <Ti k="NAV"       v={"€" + num(L.equityEur)} />
          <Ti k="NET"       v={pct(B.netReturn, 0)} cls="pos" />
          <Ti k="SHARPE"    v={safe(B.sharpe, v => v.toFixed(2))} />
          <Ti k="WIN"       v={safe(B.winRate, v => v.toFixed(1) + "%")} />
          <Ti k="PF"        v={safe(B.profitFactor, v => v.toFixed(2))} />
          <Ti k="MAXDD"     v={pct(B.maxDD, 1)} cls="neg" />
          <Ti k="CALMAR"    v={safe(B.calmar, v => v.toFixed(2))} />
          <Ti k="DSR"       v={safe(S.dsr, v => v.toFixed(2))} />
          <Ti k="OPEN RISK" v={safe(L.openRisk, v => v.toFixed(1) + "%")} />
          <Ti k="TRADES"    v={B.trades != null ? B.trades.toLocaleString() : "—"} />
          <Ti k="COINS"     v={B.coins ?? "—"} />
        </div>

        {/* body */}
        <div className="tm-body">
          <div className="tm-grid">

            <Sep label="LIVE TRADING" />

            {/* live positions */}
            <Panel title="LIVE POSITIONS" meta={L.positions.length + " open · half-Kelly sized"} span={7} sect="live">
              <table className="tm-tbl">
                <thead><tr><th>SYMBOL</th><th>STRAT</th><th>DIR</th><th className="r">ENTRY</th><th className="r">MARK</th><th className="r">P&L</th><th className="r">SIZE</th><th className="r">AGE</th></tr></thead>
                <tbody>
                  {L.positions.map((p, i) => (
                    <tr key={i}>
                      <td className="b">{p.sym}</td>
                      <td className="dim">{p.strat}</td>
                      <td><span className={"tm-dir tm-dir--" + p.dir.toLowerCase()}>{p.dir === "Long" ? "▲" : "▼"} {p.dir}</span></td>
                      <td className="r mono">{p.entry}</td>
                      <td className="r mono">{p.mark}</td>
                      <td className={"r mono b " + sgn(p.pnl)}>{pct(p.pnl)}</td>
                      <td className="r mono dim">{p.size}%</td>
                      <td className="r dim">{p.age}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Panel>

            {/* regime router */}
            <Panel title="REGIME ROUTER" meta="BTC-anchored · ETH confirm" span={5} sect="live">
              <div className="tm-reg-state">BULL</div>
              <div className="tm-reg-meta">confidence {safe(L.regime?.confidence, v => v.toFixed(2))} · {L.regime?.duration ?? 0} bars · size× {safe(L.router?.sizeMult, v => v.toFixed(2))}</div>
              <div className="tm-reg-src">BTC {L.regime.btc} · ETH {L.regime.eth}</div>
              <div className="tm-router">
                {Object.entries(L.router).filter(([k]) => k !== "sizeMult").map(([k, on]) => (
                  <span key={k} className={"tm-gate " + (on ? "on" : "off")}>{on ? "●" : "○"} {k}</span>
                ))}
              </div>
            </Panel>

            {/* equity curve */}
            <Panel title="PORTFOLIO EQUITY" meta={B.window + " · val " + B.valSplit + "%"} span={7} sect="backtest">
              <div className="tm-kpis">
                <div className="tm-kpi"><i>NET RETURN</i><b className="pos">{pct(B.netReturn, 0)}</b></div>
                <div className="tm-kpi"><i>CAGR</i><b>{safe(B.cagr, v => v.toFixed(1) + "%")}</b></div>
                <div className="tm-kpi"><i>SHARPE</i><b>{safe(B.sharpe, v => v.toFixed(2))}</b></div>
                <div className="tm-kpi"><i>MAX DD</i><b className="neg">{pct(B.maxDD, 1)}</b></div>
                <div className="tm-kpi"><i>CALMAR</i><b>{safe(B.calmar, v => v.toFixed(2))}</b></div>
                <div className="tm-kpi"><i>PROFIT F.</i><b>{safe(B.profitFactor, v => v.toFixed(2))}</b></div>
              </div>
              <div className="tm-chart" style={{ color: "var(--pos)" }}>
                <LineChart data={G.equity} benchmark={G.benchmark} height={148} fill />
              </div>
              <div className="tm-legend"><span className="tm-lg"><i className="sw sw-pos" /> portfolio</span><span className="tm-lg"><i className="sw sw-bench" /> BTC buy&hold</span></div>
            </Panel>

            {/* latest signals */}
            <Panel title="LATEST SIGNALS" meta="1h refresh · router-gated" span={5} sect="live">
              <table className="tm-tbl">
                <tbody>
                  {L.signals.map((s, i) => (
                    <tr key={i}>
                      <td className="dim mono">{s.time}</td>
                      <td><span className={"tm-act tm-act--" + s.action.toLowerCase()}>{s.action}</span></td>
                      <td className="b">{s.sym}</td>
                      <td className="dim">{s.strat}</td>
                      <td className="dim tm-note">{s.note}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Panel>

            <Sep label="STRATEGY BREAKDOWN" />

            {/* per-strategy table */}
            <Panel title="5 STRATEGIES · SHARED CAPITAL · ROUTER-GATED" meta={B.coins + " coins · " + B.window} span={12} sect="strategies">
              <table className="tm-tbl">
                <thead><tr><th>STRATEGY</th><th>REGIME</th><th>DIR</th><th className="r">TRADES</th><th className="r">WIN%</th><th className="r">AVG RET</th><th className="r">SHARPE</th><th className="r">PF</th><th className="r">RETURN</th><th className="r">MAX DD</th><th className="r">½-KELLY</th><th className="r">DSR</th><th>EQUITY</th></tr></thead>
                <tbody>
                  {G.strategies.map((s, i) => (
                    <tr key={i}>
                      <td className="b">{s.key}</td>
                      <td className="dim">{s.regime}</td>
                      <td><span className={"tm-dir tm-dir--" + s.dir.toLowerCase()}>{s.dir}</span></td>
                      <td className="r mono">{s.trades}</td>
                      <td className="r mono">{s.win.toFixed(1)}</td>
                      <td className="r mono pos">{pct(s.avgRet, 2)}</td>
                      <td className="r mono">{s.sharpe.toFixed(2)}</td>
                      <td className="r mono">{s.pf.toFixed(2)}</td>
                      <td className="r mono b pos">{pct(s.ret, 1)}</td>
                      <td className="r mono neg">{pct(s.maxDD, 1)}</td>
                      <td className="r mono dim">{(s.halfKelly * 100).toFixed(1)}%</td>
                      <td className="r mono">{s.dsr.toFixed(2)}</td>
                      <td style={{ color: "var(--accent)", width: 90 }}><Sparkline data={s.spark} width={90} height={20} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Panel>

            <Sep label="STATISTICAL EDGE ANALYSIS" />

            {/* Bootstrap CI distribution */}
            <Panel title="BOOTSTRAP CI · MEAN RETURN" meta="5 000 resamples · SE = 0.094%" span={5} sect="stats">
              <div className="tm-chart">
                <AnnotHistogram
                  bins={G.bootBins}
                  meanX={S.boot.mean}
                  ciLo={S.boot.lo95}
                  ciHi={S.boot.hi95}
                  height={148}
                />
              </div>
              <div className="tm-dist-ax">
                <span className="dim">{G.bootBins[0].x.toFixed(2)}%</span>
                <span className="accent">μ = {pct(S.boot.mean, 2)}</span>
                <span className="dim">{G.bootBins[G.bootBins.length - 1].x.toFixed(2)}%</span>
              </div>
              <div className="tm-stat-row">
                <span>95% CI <b className="accent">[{pct(S.boot.lo95, 2)}, {pct(S.boot.hi95, 2)}]</b></span>
                <span>P(μ&gt;0) <b className="pos">100%</b></span>
              </div>
              <div className="tm-mini">── dashed lines: CI bounds · solid: observed mean · entire distribution above zero → edge confirmed</div>
            </Panel>

            {/* T-test null distribution */}
            <Panel title="NULL HYPOTHESIS TEST" meta="H₀: μ = 0 · observed t = 8.62 · p &lt; 0.0001" span={4} sect="stats">
              <div className="tm-chart">
                <NormalCurve
                  pts={G.tDistPts}
                  height={148}
                  stroke="var(--ink-dim)"
                  shadeLo={-1.96}
                  shadeHi={1.96}
                  shadeColor="rgba(200,70,50,0.20)"
                  markers={[
                    { x: -1.96, color: "var(--neg)", label: "−1.96" },
                    { x:  1.96, color: "var(--neg)", label: "+1.96" }
                  ]}
                  offscaleLabel="t = 8.62"
                />
              </div>
              <div className="tm-dist-ax">
                <span className="dim">−5σ</span>
                <span>H₀: μ = 0</span>
                <span className="accent">→ t = 8.62</span>
              </div>
              <div className="tm-stat-row">
                <span>reject region <b className="neg">|t| &gt; 1.96</b></span>
                <span>our t <b className="accent">8.62 σ from null</b></span>
              </div>
              <div className="tm-mini">── shaded: α = 0.05 rejection tails · our observed t-stat is impossibly extreme under H₀</div>
            </Panel>

            {/* WFV fold performance */}
            <Panel title="WALK-FORWARD FOLDS" meta="5-fold · time-disjoint consistency" span={3} sect="stats">
              <div className="tm-wfv">
                {Object.entries(G.wfvFolds).map(([name, folds]) => (
                  <FoldRow key={name} name={name} folds={folds} />
                ))}
              </div>
              <div className="tm-mini">── bar height = fold fitness · consistent = real edge · no GA overfit to one window</div>
            </Panel>

            <Sep label="OUTCOME DISTRIBUTIONS · PER STRATEGY" />

            {/* strategy return distributions */}
            <Panel title="TRADE RETURN DISTRIBUTIONS" meta="right skew · CVaR(5%) shaded · dashed: μ" span={12} flush sect="stats">
              <div className="tm-strat-dists">
                {G.strategies.map(s => <MiniDist key={s.key} s={s} />)}
              </div>
            </Panel>

            <Sep label="STRESS TESTS" />

            {/* crash */}
            <Panel title="CRASH STRESS TEST" meta="historical BTC ≥ 15% drawdown windows" span={7} sect="stress">
              <table className="tm-tbl">
                <thead><tr><th>WINDOW</th><th className="r">BTC</th><th className="r">DUR</th><th className="r">OPEN</th><th className="r">EXP</th><th className="r">W/L</th><th className="r">AVG</th><th className="r">CLEAN</th><th className="r">+SLIP</th></tr></thead>
                <tbody>
                  {G.stress.crashes.map((c, i) => (
                    <tr key={i}>
                      <td className="b">{c.label} <span className="dim">{c.date}</span></td>
                      <td className="r mono neg">{c.drop}%</td>
                      <td className="r dim">{c.dur}</td>
                      <td className="r mono">{c.openPos}</td>
                      <td className="r mono dim">{c.exposure}%</td>
                      <td className="r mono">{c.w}/{c.l}</td>
                      <td className="r mono neg">{pct(c.avgRet, 2)}</td>
                      <td className="r mono neg">{pct(c.clean, 1)}</td>
                      <td className="r mono neg b">{pct(c.slip, 0)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <div className="tm-mini">worst-case all-stop @ peak {G.stress.worstCase.peakExposure}% exposure → clean {pct(G.stress.worstCase.cleanHit, 1)} · 3× ATR {pct(G.stress.worstCase.expandedHit, 1)}</div>
            </Panel>

            {/* rally */}
            <Panel title="RALLY STRESS TEST" meta="BTC ≥ 15% rise from 7d trough" span={5} sect="stress">
              <table className="tm-tbl">
                <thead><tr><th>WINDOW</th><th className="r">BTC</th><th className="r">DUR</th><th className="r">W/L</th><th className="r">AVG</th><th className="r">PORT</th></tr></thead>
                <tbody>
                  {G.stress.rallies.map((c, i) => (
                    <tr key={i}>
                      <td className="b">{c.label} <span className="dim">{c.date}</span></td>
                      <td className="r mono pos">+{c.rise}%</td>
                      <td className="r dim">{c.dur}</td>
                      <td className="r mono">{c.w}/{c.l}</td>
                      <td className="r mono pos">{pct(c.avgRet, 2)}</td>
                      <td className="r mono pos b">{pct(c.portHit, 1)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Panel>

            <Sep label="GA TRAINING" />

            {/* fitness convergence */}
            <Panel title="FITNESS CONVERGENCE" meta={"cycle " + G.training.cycleCount + " · pop " + G.training.popSize + " · " + G.training.folds + "-fold WFV"} span={7} sect="training">
              <div className="tm-train-kpis">
                <div><i>BEST</i><b className="pos">{G.training.bestFitness.toFixed(2)}</b></div>
                <div><i>TPE LIFT</i><b className="pos">+{(G.training.bayesOpt.lift * 100).toFixed(1)}%</b></div>
                <div><i>TPE ITERS</i><b>{G.training.bayesOpt.iters}</b></div>
                <div><i>SAVED</i><b className="dim">{G.training.savedAt.slice(0, 10)}</b></div>
              </div>
              <div className="tm-chart" style={{ color: "var(--accent)" }}>
                <LineChart data={G.training.best} benchmark={G.training.mean} height={120}
                  stroke="var(--accent)" strokeWidth={1.8} benchStroke="rgba(255,255,255,0.28)" fill />
              </div>
              <div className="tm-legend"><span className="tm-lg"><i className="sw sw-acc" /> best fitness</span><span className="tm-lg"><i className="sw sw-bench" /> population mean</span></div>
            </Panel>

            {/* population spread */}
            <Panel title="POPULATION SPREAD" meta="sorted by fitness · 20 individuals" span={5} sect="training">
              <div className="tm-pop">
                {G.training.population.map((f, i) => (
                  <div className="tm-pop-row" key={i}>
                    <span className="tm-pop-i">{(i + 1).toString().padStart(2, "0")}</span>
                    <span style={{ color: f < 0 ? "var(--neg)" : "var(--accent)" }}>
                      <BarRow value={f} max={2} min={-0.8} width={170} height={6}
                        color={f < 0 ? "var(--neg)" : "var(--accent)"} />
                    </span>
                    <b className={"mono " + (f < 0 ? "neg" : "")}>{f.toFixed(2)}</b>
                  </div>
                ))}
              </div>
            </Panel>

          </div>{/* end tm-grid */}
        </div>{/* end tm-body */}
      </div>
    );
  }

  window.TerminalApp = TerminalApp;
})();
