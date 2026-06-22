/* directionCockpit.jsx — Direction B: "COCKPIT"
   Headline KPI strip, hero equity panel, then airy 2-col cards. More breathing
   room than the terminal; bigger numbers, calmer rhythm. */
(function () {
  const G = window.GRAV;
  const { LineChart, Sparkline, Histogram, BarRow, CIBar, Gauge } = window;

  const sgn = v => (v > 0 ? "pos" : v < 0 ? "neg" : "");
  const pct = (v, d = 1) => (v > 0 ? "+" : "") + v.toFixed(d) + "%";
  const num = (v, d = 2) => v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d });

  function Kpi({ label, value, sub, cls }) {
    return (
      <div className="ck-kpi">
        <div className="ck-kpi-l">{label}</div>
        <div className={"ck-kpi-v " + (cls || "")}>{value}</div>
        {sub && <div className="ck-kpi-s">{sub}</div>}
      </div>
    );
  }

  function Card({ title, meta, children, wide, sect }) {
    return (
      <section className={"ck-card" + (wide ? " ck-card--wide" : "")} data-section={sect}>
        <header className="ck-ch"><h3>{title}</h3>{meta && <span>{meta}</span>}</header>
        {children}
      </section>
    );
  }

  function DirectionCockpit() {
    const L = G.live, B = G.backtest, S = G.stats, O = G.oos;
    const maxRet = Math.max(...G.strategies.map(s => s.ret));
    return (
      <div className="dir dir-cockpit">
        {/* header */}
        <header className="ck-top">
          <div className="ck-id">
            <div className="ck-logo">◆</div>
            <div>
              <div className="ck-name">Gravity-gen2</div>
              <div className="ck-tag">portfolio monitor · cycle {L.cycle}</div>
            </div>
          </div>
          <div className="ck-top-r">
            <div className={"ck-regime ck-regime--bull"}>
              <span className="ck-reg-dot" />
              <div><b>{L.regime.state}</b><i>regime · conf {L.regime.confidence.toFixed(2)}</i></div>
            </div>
            <div className="ck-clock">09:04 UTC<i>next refresh {L.nextRefresh}</i></div>
          </div>
        </header>

        {/* KPI strip */}
        <div className="ck-kpis">
          <Kpi label="Live NAV" value={"€" + num(L.equityEur)} sub={<span className={sgn(L.dayPnl)}>{pct(L.dayPnl)} today</span>} />
          <Kpi label="Net return" value={pct(B.netReturn, 0)} cls="pos" sub={`CAGR ${B.cagr.toFixed(0)}%`} />
          <Kpi label="Sharpe" value={B.sharpe.toFixed(2)} sub={`Calmar ${B.calmar.toFixed(2)}`} />
          <Kpi label="Win rate" value={B.winRate.toFixed(1) + "%"} sub={`PF ${B.profitFactor.toFixed(2)}`} />
          <Kpi label="Max drawdown" value={pct(B.maxDD, 1)} cls="neg" sub={`${B.trades.toLocaleString()} trades`} />
          <Kpi label="Open risk" value={L.openRisk.toFixed(1) + "%"} sub={`${L.positions.length} positions`} />
        </div>

        {/* hero equity */}
        <section className="ck-hero" data-section="backtest">
          <div className="ck-hero-head">
            <div>
              <h3>Portfolio equity curve</h3>
              <span className="ck-hero-meta">{B.coins} coins · {B.window} · validation {B.valSplit}% · dual-TF 1h + 15m</span>
            </div>
            <div className="ck-hero-legend">
              <span><i className="sw sw-pos" /> Gravity portfolio</span>
              <span><i className="sw sw-bench" /> BTC buy & hold</span>
            </div>
          </div>
          <div className="ck-hero-chart" style={{ color: "var(--pos)" }}>
            <LineChart data={G.equity} benchmark={G.benchmark} height={230} fill strokeWidth={2} />
          </div>
          <div className="ck-hero-foot">
            <div><i>Start</i><b>100.0</b></div>
            <div><i>Peak</i><b>{Math.max(...G.equity).toFixed(0)}</b></div>
            <div><i>Final</i><b className="pos">{G.equity[G.equity.length - 1].toFixed(0)}</b></div>
            <div><i>Expectancy</i><b>{pct(B.expectancy, 2)}/trade</b></div>
            <div><i>OOS Sharpe</i><b>{O.sharpe.toFixed(2)} <span className="dim">({O.coins} unseen coins)</span></b></div>
          </div>
        </section>

        {/* 2-col cards */}
        <div className="ck-cols">
          {/* strategies */}
          <Card title="Strategy breakdown" meta="shared capital · router-gated" wide sect="strategies">
            <div className="ck-strat-head"><span>Strategy</span><span className="r">Trades</span><span className="r">Win</span><span className="r">Sharpe</span><span>Return</span></div>
            {G.strategies.map((s, i) => (
              <div className="ck-strat" key={i}>
                <div className="ck-strat-n"><b>{s.key}</b><i className={"ck-pill ck-pill--" + s.dir.toLowerCase()}>{s.regime} · {s.dir}</i></div>
                <span className="r mono">{s.trades}</span>
                <span className="r mono">{s.win.toFixed(0)}%</span>
                <span className="r mono">{s.sharpe.toFixed(2)}</span>
                <div className="ck-strat-bar">
                  <span style={{ color: "var(--pos)" }}><BarRow value={s.ret} max={maxRet} width={120} height={9} color="var(--pos)" /></span>
                  <b className="pos">{pct(s.ret, 0)}</b>
                </div>
              </div>
            ))}
          </Card>

          {/* live positions */}
          <Card title="Open positions" meta={L.positions.length + " live"} sect="live">
            {L.positions.map((p, i) => (
              <div className="ck-pos" key={i}>
                <div className="ck-pos-l">
                  <b>{p.sym}</b>
                  <i className={"ck-dir ck-dir--" + p.dir.toLowerCase()}>{p.dir === "Long" ? "▲" : "▼"} {p.strat}</i>
                </div>
                <div className="ck-pos-r">
                  <span className="ck-pos-mark mono">{p.mark}</span>
                  <b className={"mono " + sgn(p.pnl)}>{pct(p.pnl)}</b>
                  <i className="ck-pos-age">{p.age}</i>
                </div>
              </div>
            ))}
          </Card>

          {/* statistical validation */}
          <Card title="Statistical validation" meta="portfolio · n=2062" sect="stats">
            <div className="ck-stat-grid">
              <div className="ck-stat"><i>t-stat</i><b>{S.tTest.t.toFixed(2)}</b><span className="pos">p&lt;0.001</span></div>
              <div className="ck-stat"><i>P(μ&gt;0)</i><b className="pos">{(S.boot.probPos * 100).toFixed(0)}%</b><span>bootstrap</span></div>
              <div className="ck-stat"><i>Deflated Sharpe</i><b>{S.dsr.toFixed(2)}</b><span className="pos">✓ strong</span></div>
              <div className="ck-stat"><i>½-Kelly</i><b>{(S.kelly.half * 100).toFixed(1)}%</b><span>5% cap</span></div>
              <div className="ck-stat"><i>Skew</i><b>{S.dist.skew.toFixed(2)}</b><span>right-tail</span></div>
              <div className="ck-stat"><i>N/d</i><b>{S.vc.ratio.toFixed(0)}</b><span className="pos">{S.vc.verdict}</span></div>
            </div>
            <div className="ck-ci">
              <div className="ck-ci-lab">95% CI · mean trade return</div>
              <CIBar lo={S.boot.lo95} hi={S.boot.hi95} mean={S.boot.mean} domainLo={-0.2} domainHi={1.2} color="var(--accent)" />
              <div className="ck-ci-ax"><span>-0.2%</span><span>+0.81%</span><span>+1.2%</span></div>
            </div>
          </Card>

          {/* return distribution */}
          <Card title="Return distribution" meta="monthly" sect="stats">
            <div className="ck-dist" style={{ color: "var(--pos)" }}>
              <Histogram data={G.monthlyReturns} bins={13} height={140} pos="var(--pos)" neg="var(--neg)" />
            </div>
            <div className="ck-dist-ax"><span className="neg">{Math.min(...G.monthlyReturns).toFixed(0)}%</span><span className="dim">0</span><span className="pos">+{Math.max(...G.monthlyReturns).toFixed(0)}%</span></div>
            <div className="ck-dist-stats"><span>CVaR(5%) <b className="neg">{S.dist.cvar5.toFixed(1)}%</b></span><span>Tail ratio <b>{S.dist.tailRatio.toFixed(2)}</b></span><span>ExKurt <b>{S.dist.exKurt.toFixed(1)}</b></span></div>
          </Card>

          {/* crash stress */}
          <Card title="Crash stress test" meta="historical BTC drawdowns" sect="stress">
            {G.stress.crashes.map((c, i) => (
              <div className="ck-stress" key={i}>
                <div className="ck-stress-l"><b>{c.label}</b><i>{c.date} · {c.drop}% / {c.dur}</i></div>
                <div className="ck-stress-bars">
                  <div className="ck-stress-bar"><span style={{ width: Math.min(Math.abs(c.clean) * 3, 100) + "%" }} className="neg-bar" /><em className="neg">{pct(c.clean, 1)}</em></div>
                  <div className="ck-stress-bar"><span style={{ width: Math.min(Math.abs(c.slip), 100) + "%" }} className="neg-bar neg-bar--slip" /><em className="neg">{pct(c.slip, 0)} <i>+slip</i></em></div>
                </div>
              </div>
            ))}
            <div className="ck-mini">worst-case all-stop @ {G.stress.worstCase.peakExposure}% peak exposure</div>
          </Card>

          {/* GA training */}
          <Card title="GA training progress" meta={"cycle " + G.training.cycleCount + " · " + G.training.folds + "-fold WFV"} sect="training">
            <div className="ck-train-top">
              <div><i>Best fitness</i><b className="pos">{G.training.bestFitness.toFixed(2)}</b></div>
              <div><i>Population</i><b>{G.training.popSize}</b></div>
              <div><i>TPE refine</i><b className="pos">+{(G.training.bayesOpt.lift * 100).toFixed(1)}%</b></div>
            </div>
            <div className="ck-train-chart" style={{ color: "var(--accent)" }}>
              <LineChart data={G.training.best} benchmark={G.training.mean} height={96} stroke="var(--accent)" strokeWidth={2} benchStroke="rgba(255,255,255,0.3)" fill />
            </div>
            <div className="ck-train-legend"><span><i className="sw sw-acc" /> best</span><span><i className="sw sw-bench" /> population mean</span></div>
          </Card>
        </div>
      </div>
    );
  }

  window.DirectionCockpit = DirectionCockpit;
})();
