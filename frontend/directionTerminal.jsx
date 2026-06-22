/* directionTerminal.jsx — Direction A: "TERMINAL"
   Classic Bloomberg-style dense multi-panel grid. Status bar + ticker +
   bordered panels packed on a 12-col grid. */
(function () {
  const G = window.GRAV;
  const { LineChart, Sparkline, Histogram, BarRow, CIBar } = window;

  const sgn = v => (v > 0 ? "pos" : v < 0 ? "neg" : "");
  const pct = (v, d = 1) => (v > 0 ? "+" : "") + v.toFixed(d) + "%";
  const num = (v, d = 2) => v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d });

  function Panel({ title, meta, span, children, pad = true, sect }) {
    return (
      <section className="tm-panel" style={{ gridColumn: `span ${span}` }} data-section={sect}>
        <header className="tm-ph">
          <span className="tm-pt">{title}</span>
          {meta && <span className="tm-pm">{meta}</span>}
        </header>
        <div className={pad ? "tm-pb" : "tm-pb tm-pb--flush"}>{children}</div>
      </section>
    );
  }

  function TermItem({ k, v, cls }) {
    return <span className="tm-ti"><i>{k}</i><b className={cls}>{v}</b></span>;
  }

  function DirectionTerminal() {
    const L = G.live, B = G.backtest, S = G.stats;
    return (
      <div className="dir dir-terminal">
        {/* top bar */}
        <div className="tm-top">
          <div className="tm-brand"><span className="tm-dot" /> GRAVITY-GEN2 <i>// PORTFOLIO TERMINAL</i></div>
          <div className="tm-top-mid">2026-06-22 · 09:04:18 UTC · cycle {L.cycle} · next refresh {L.nextRefresh}</div>
          <div className="tm-top-right">
            <span className={"tm-regime tm-regime--bull"}>● {L.regime.state.toUpperCase()} · conf {L.regime.confidence.toFixed(2)}</span>
            <span className="tm-nav">€{num(L.equityEur)} <b className={sgn(L.dayPnl)}>{pct(L.dayPnl)}</b></span>
          </div>
        </div>

        {/* ticker */}
        <div className="tm-ticker">
          <TermItem k="NAV" v={"€" + num(L.equityEur)} />
          <TermItem k="NET" v={pct(B.netReturn, 0)} cls="pos" />
          <TermItem k="SHARPE" v={B.sharpe.toFixed(2)} />
          <TermItem k="WIN" v={B.winRate.toFixed(1) + "%"} />
          <TermItem k="PF" v={B.profitFactor.toFixed(2)} />
          <TermItem k="MAXDD" v={pct(B.maxDD, 1)} cls="neg" />
          <TermItem k="CALMAR" v={B.calmar.toFixed(2)} />
          <TermItem k="DSR" v={S.dsr.toFixed(2)} />
          <TermItem k="OPEN RISK" v={L.openRisk.toFixed(1) + "%"} />
          <TermItem k="TRADES" v={B.trades.toLocaleString()} />
          <TermItem k="COINS" v={B.coins} />
        </div>

        {/* grid */}
        <div className="tm-grid">
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

          {/* regime + router */}
          <Panel title="REGIME ROUTER" meta={"BTC-anchored · ETH confirm"} span={5} sect="live">
            <div className="tm-regime-row">
              <div className="tm-reg-big">
                <div className="tm-reg-state">{L.regime.state.toUpperCase()}</div>
                <div className="tm-reg-conf">confidence {L.regime.confidence.toFixed(2)} · {L.regime.duration} bars</div>
                <div className="tm-reg-src">BTC {L.regime.btc} · ETH {L.regime.eth} · size× {L.router.sizeMult.toFixed(2)}</div>
              </div>
            </div>
            <div className="tm-router">
              {Object.entries(L.router).filter(([k]) => k !== "sizeMult").map(([k, on]) => (
                <span key={k} className={"tm-gate " + (on ? "on" : "off")}>{on ? "●" : "○"} {k}</span>
              ))}
            </div>
          </Panel>

          {/* equity curve + backtest KPIs */}
          <Panel title="PORTFOLIO EQUITY" meta={B.window + " · val " + B.valSplit + "%"} span={7} sect="backtest">
            <div className="tm-kpis">
              <div className="tm-kpi"><i>NET RETURN</i><b className="pos">{pct(B.netReturn, 0)}</b></div>
              <div className="tm-kpi"><i>CAGR</i><b>{B.cagr.toFixed(1)}%</b></div>
              <div className="tm-kpi"><i>SHARPE</i><b>{B.sharpe.toFixed(2)}</b></div>
              <div className="tm-kpi"><i>MAX DD</i><b className="neg">{pct(B.maxDD, 1)}</b></div>
              <div className="tm-kpi"><i>CALMAR</i><b>{B.calmar.toFixed(2)}</b></div>
              <div className="tm-kpi"><i>PROFIT F.</i><b>{B.profitFactor.toFixed(2)}</b></div>
            </div>
            <div className="tm-chart" style={{ color: "var(--pos)" }}>
              <LineChart data={G.equity} benchmark={G.benchmark} height={150} fill />
            </div>
            <div className="tm-legend"><span className="tm-lg"><i className="sw sw-pos" /> portfolio</span><span className="tm-lg"><i className="sw sw-bench" /> BTC buy&hold</span></div>
          </Panel>

          {/* latest signals */}
          <Panel title="LATEST SIGNALS" meta="1h refresh · router-gated" span={5} sect="live">
            <table className="tm-tbl tm-tbl--sig">
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

          {/* per-strategy */}
          <Panel title="STRATEGY BREAKDOWN" meta="5 strategies · shared capital · router-gated" span={12} sect="strategies">
            <table className="tm-tbl tm-tbl--strat">
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

          {/* statistical validation */}
          <Panel title="STATISTICAL VALIDATION" meta="portfolio · n=2062" span={5} sect="stats">
            <table className="tm-stat">
              <tbody>
                <tr><td>t-test (vs 0)</td><td className="r mono">μ {pct(S.tTest.mean, 2)} · t {S.tTest.t.toFixed(2)} · p {S.tTest.p.toFixed(4)} <b className="pos">{S.tTest.sig}</b></td></tr>
                <tr><td>Bootstrap P(μ&gt;0)</td><td className="r mono pos">{(S.boot.probPos * 100).toFixed(0)}%</td></tr>
                <tr><td>Deflated Sharpe</td><td className="r mono">{S.dsr.toFixed(2)} <b className="pos">✓ strong</b></td></tr>
                <tr><td>Kelly · ½-Kelly</td><td className="r mono">{(S.kelly.full * 100).toFixed(1)}% · {(S.kelly.half * 100).toFixed(1)}% <span className="dim">(5% cap)</span></td></tr>
                <tr><td>Skew · ExKurt</td><td className="r mono">{S.dist.skew.toFixed(2)} · {S.dist.exKurt.toFixed(2)}</td></tr>
                <tr><td>CVaR(5%) · Tail</td><td className="r mono">{S.dist.cvar5.toFixed(2)}% · {S.dist.tailRatio.toFixed(2)}</td></tr>
                <tr><td>VC · N/d</td><td className="r mono">d={S.vc.d} · {S.vc.ratio.toFixed(0)} <b className="pos">{S.vc.verdict}</b></td></tr>
              </tbody>
            </table>
            <div className="tm-ci">
              <div className="tm-ci-lab">95% bootstrap CI · mean trade return</div>
              <CIBar lo={S.boot.lo95} hi={S.boot.hi95} mean={S.boot.mean} domainLo={-0.2} domainHi={1.2} color="var(--accent)" />
              <div className="tm-ci-ax"><span>-0.2%</span><span>0</span><span>+1.2%</span></div>
            </div>
          </Panel>

          {/* return distribution */}
          <Panel title="RETURN DISTRIBUTION" meta="monthly · n=40" span={4} sect="stats">
            <div className="tm-chart" style={{ color: "var(--pos)" }}>
              <Histogram data={G.monthlyReturns} bins={13} height={130} pos="var(--pos)" neg="var(--neg)" />
            </div>
            <div className="tm-dist-ax"><span className="neg">{Math.min(...G.monthlyReturns).toFixed(1)}%</span><span className="dim">0</span><span className="pos">+{Math.max(...G.monthlyReturns).toFixed(1)}%</span></div>
            <div className="tm-mini">skew {G.stats.dist.skew.toFixed(2)} · right-tailed · fat tails (kurt {G.stats.dist.exKurt.toFixed(1)})</div>
          </Panel>

          {/* GA training */}
          <Panel title="GA TRAINING" meta={"cycle " + G.training.cycleCount + " · WFV " + G.training.folds + "-fold"} span={3} sect="training">
            <div className="tm-chart" style={{ color: "var(--accent)" }}>
              <LineChart data={G.training.best} height={88} strokeWidth={1.6} stroke="var(--accent)" benchmark={G.training.mean} benchStroke="rgba(255,255,255,0.28)" />
            </div>
            <div className="tm-train">
              <div><i>BEST FIT</i><b>{G.training.bestFitness.toFixed(2)}</b></div>
              <div><i>POP</i><b>{G.training.popSize}</b></div>
              <div><i>TPE LIFT</i><b className="pos">+{(G.training.bayesOpt.lift * 100).toFixed(1)}%</b></div>
            </div>
          </Panel>

          {/* crash stress */}
          <Panel title="CRASH STRESS TEST" meta="BTC ≥15% drawdown windows" span={7} sect="stress">
            <table className="tm-tbl tm-tbl--stress">
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

          {/* rally stress */}
          <Panel title="RALLY STRESS TEST" meta="BTC ≥15% rise from 7d trough" span={5} sect="stress">
            <table className="tm-tbl tm-tbl--stress">
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
        </div>
      </div>
    );
  }

  window.DirectionTerminal = DirectionTerminal;
})();
