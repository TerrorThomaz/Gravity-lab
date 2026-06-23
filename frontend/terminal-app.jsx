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

  /* ── Color-coded interpretation helpers ─────────────────────────────────── */
  function Badge({ level, label }) {
    return <span className={"tm-badge tm-badge--" + level}>{label}</span>;
  }

  function Verdict({ level, children }) {
    const cls = level === "strong" ? "tm-verdict tm-verdict--strong"
              : level === "weak"  ? "tm-verdict tm-verdict--weak"
              : "tm-verdict";
    return <div className={cls}><div className="tm-verdict-text">{children}</div></div>;
  }

  function InterpBar({ label, value, max, color }) {
    const w = Math.min(100, Math.max(2, (value / max) * 100));
    return (
      <div className="tm-interp-row">
        <i style={{ width: 60 }}>{label}</i>
        <b>{typeof value === "number" ? value.toFixed(2) : value}</b>
        <div className="tm-interp-bar">
          <div className="tm-interp-fill" style={{ width: w + "%", background: color }} />
        </div>
      </div>
    );
  }

  const sharpeTier = v => v >= 3 ? "strong" : v >= 2 ? "good" : v >= 1 ? "ok" : "weak";
  const sharpeLabel = v => v >= 3 ? "EXCELLENT" : v >= 2 ? "STRONG" : v >= 1 ? "ACCEPTABLE" : "WEAK";
  const pfTier = v => v >= 2 ? "strong" : v >= 1.5 ? "good" : v >= 1.2 ? "ok" : "weak";
  const wrTier = v => v >= 60 ? "strong" : v >= 55 ? "good" : v >= 50 ? "ok" : "weak";
  const dsrTier = v => v >= 0.95 ? "strong" : v >= 0.8 ? "good" : v >= 0.5 ? "ok" : "weak";
  const dsrLabel = v => v >= 0.95 ? "CONFIRMED" : v >= 0.8 ? "LIKELY REAL" : v >= 0.5 ? "UNCERTAIN" : "SUSPECT";
  const calmarTier = v => v >= 3 ? "strong" : v >= 2 ? "good" : v >= 1 ? "ok" : "weak";
  const ddTier = v => Math.abs(v) <= 10 ? "good" : Math.abs(v) <= 20 ? "ok" : "weak";
  const pTier = v => v < 0.001 ? "strong" : v < 0.01 ? "good" : v < 0.05 ? "ok" : "weak";
  const pLabel = v => v < 0.001 ? "REJECT H₀" : v < 0.01 ? "SIGNIFICANT" : v < 0.05 ? "MARGINAL" : "NOT SIG";
  const ciTier = (lo) => lo > 0 ? "strong" : "weak";
  const kellyTier = v => v >= 0.15 ? "strong" : v >= 0.08 ? "good" : v >= 0.03 ? "ok" : "weak";

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
    const tier = (s.sharpe || 0) >= 2 ? "strong" : (s.sharpe || 0) >= 1 ? "good" : (s.pf || 0) >= 1.2 ? "ok" : "weak";
    return (
      <div className="tm-mini-strat">
        <div className="tm-mini-head">
          <b>{s.key}</b>
          <span style={{ display: "flex", gap: 5, alignItems: "center" }}>
            <Badge level={tier} label={tier === "strong" ? "STRONG" : tier === "good" ? "GOOD" : tier === "ok" ? "MARGINAL" : "WEAK"} />
            <span className={"tm-dir tm-dir--" + s.dir.toLowerCase()}>{s.dir}</span>
          </span>
        </div>
        <AnnotHistogram bins={dist.bins} meanX={s.avgRet} cvarX={dist.cvar5} height={110} />
        <div className="tm-mini-ax">
          <span className="neg">{xlo.toFixed(1)}%</span>
          <span className="dim">0</span>
          <span className="pos">+{xhi.toFixed(1)}%</span>
        </div>
        <div className="tm-mini-foot">
          <span>μ <b className={sgn(s.avgRet)}>{pct(s.avgRet, 2)}</b></span>
          <span>CVaR <b className="neg">{safe(dist.cvar5, v => v.toFixed(1) + "%")}</b></span>
          <span>win <b className={wrTier(s.win || 0) === "strong" ? "pos" : ""}>{safe(s.win, v => v.toFixed(0) + "%")}</b></span>
          <span>SR <b className={sharpeTier(s.sharpe || 0) === "strong" ? "pos" : sharpeTier(s.sharpe || 0) === "weak" ? "neg" : ""}>{safe(s.sharpe, v => v.toFixed(2))}</b></span>
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
          <Ti k="SHARPE"    v={safe(B.sharpe, v => v.toFixed(2))} cls={B.sharpe >= 3 ? "pos" : B.sharpe < 1 ? "neg" : ""} />
          <Ti k="WIN"       v={safe(B.winRate, v => v.toFixed(1) + "%")} cls={B.winRate >= 55 ? "pos" : B.winRate < 50 ? "neg" : ""} />
          <Ti k="PF"        v={safe(B.profitFactor, v => v.toFixed(2))} cls={B.profitFactor >= 1.5 ? "pos" : B.profitFactor < 1.2 ? "neg" : ""} />
          <Ti k="MAXDD"     v={pct(B.maxDD, 1)} cls="neg" />
          <Ti k="CALMAR"    v={safe(B.calmar, v => v.toFixed(2))} cls={B.calmar >= 3 ? "pos" : B.calmar < 1 ? "neg" : ""} />
          <Ti k="DSR"       v={safe(S.dsr, v => v.toFixed(2))} cls={S.dsr >= 0.95 ? "pos" : S.dsr < 0.5 ? "neg" : ""} />
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
              <div className={"tm-reg-state " + (L.regime?.state === "Bull" ? "pos" : L.regime?.state === "Bear" ? "neg" : "")}>{L.regime?.state || "—"}</div>
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
                <div className="tm-kpi"><i>SHARPE {B.sharpe != null && <Badge level={sharpeTier(B.sharpe)} label={sharpeLabel(B.sharpe)} />}</i><b>{safe(B.sharpe, v => v.toFixed(2))}</b></div>
                <div className="tm-kpi"><i>MAX DD {B.maxDD != null && <Badge level={ddTier(B.maxDD)} label={Math.abs(B.maxDD) <= 10 ? "LOW" : Math.abs(B.maxDD) <= 20 ? "MODERATE" : "HIGH"} />}</i><b className="neg">{pct(B.maxDD, 1)}</b></div>
                <div className="tm-kpi"><i>CALMAR {B.calmar != null && <Badge level={calmarTier(B.calmar)} label={B.calmar >= 3 ? "EXCELLENT" : B.calmar >= 2 ? "GOOD" : B.calmar >= 1 ? "FAIR" : "POOR"} />}</i><b>{safe(B.calmar, v => v.toFixed(2))}</b></div>
                <div className="tm-kpi"><i>PROFIT F. {B.profitFactor != null && <Badge level={pfTier(B.profitFactor)} label={B.profitFactor >= 2 ? "STRONG" : B.profitFactor >= 1.5 ? "GOOD" : B.profitFactor >= 1.2 ? "MARGINAL" : "WEAK"} />}</i><b>{safe(B.profitFactor, v => v.toFixed(2))}</b></div>
              </div>
              <div className="tm-desc">
                <b>Sharpe</b> = risk-adjusted return (annualised mean / std); &gt;2 is strong, &gt;3 is exceptional.
                <b> Calmar</b> = CAGR / max drawdown; higher means faster recovery from losses.
                <b> Profit Factor</b> = gross wins / gross losses; &gt;1.5 is a tradeable edge.
                <b> Max DD</b> = worst peak-to-trough equity decline.
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
              <div className="tm-desc" style={{ marginBottom: 8 }}>
                Each strategy targets a different market regime. The <b>regime router</b> activates/deactivates strategies based on BTC's current state.
                <b> Win%</b> = percentage of profitable trades.
                <b> Avg Ret</b> = mean return per trade.
                <b> PF</b> = profit factor (gross profit / gross loss; &gt;1.5 is good).
                <b> ½-Kelly</b> = recommended position size (half Kelly criterion, capped at 5%).
                <b> DSR</b> = deflated Sharpe ratio — adjusts for multiple strategy testing; &gt;0.95 means the edge survives correction.
              </div>
              <table className="tm-tbl">
                <thead><tr><th>STRATEGY</th><th>REGIME</th><th>DIR</th><th className="r">TRADES</th><th className="r">WIN%</th><th className="r">AVG RET</th><th className="r">SHARPE</th><th className="r">PF</th><th className="r">RETURN</th><th className="r">MAX DD</th><th className="r">½-KELLY</th><th className="r">DSR</th><th>EQUITY</th></tr></thead>
                <tbody>
                  {G.strategies.map((s, i) => (
                    <tr key={i}>
                      <td className="b">{s.key}</td>
                      <td className="dim">{s.regime}</td>
                      <td><span className={"tm-dir tm-dir--" + s.dir.toLowerCase()}>{s.dir}</span></td>
                      <td className="r mono">{s.trades}</td>
                      <td className={"r mono " + (s.win != null ? wrTier(s.win) === "strong" ? "pos" : wrTier(s.win) === "weak" ? "neg" : "" : "")}>{safe(s.win, v => v.toFixed(1))}</td>
                      <td className="r mono pos">{pct(s.avgRet, 2)}</td>
                      <td className="r mono"><span className={sharpeTier(s.sharpe || 0) === "strong" ? "pos" : sharpeTier(s.sharpe || 0) === "weak" ? "neg" : ""}>{safe(s.sharpe, v => v.toFixed(2))}</span></td>
                      <td className="r mono"><span className={pfTier(s.pf || 0) === "strong" ? "pos" : pfTier(s.pf || 0) === "weak" ? "neg" : ""}>{safe(s.pf, v => v.toFixed(2))}</span></td>
                      <td className="r mono b pos">{pct(s.ret, 1)}</td>
                      <td className="r mono neg">{pct(s.maxDD, 1)}</td>
                      <td className="r mono dim">{safe(s.halfKelly, v => (v * 100).toFixed(1) + "%")}</td>
                      <td className="r mono"><span className={dsrTier(s.dsr || 0) === "strong" ? "pos" : dsrTier(s.dsr || 0) === "weak" ? "neg" : ""}>{safe(s.dsr, v => v.toFixed(2))}</span> {s.dsr != null && <Badge level={dsrTier(s.dsr)} label={dsrLabel(s.dsr)} />}</td>
                      <td style={{ color: "var(--accent)", width: 90 }}><Sparkline data={s.spark} width={90} height={20} /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </Panel>

            <Sep label="STATISTICAL EDGE ANALYSIS" />

            {/* Key stats summary */}
            <Panel title="EDGE QUALITY SCORECARD" meta="portfolio-level statistical validation" span={12} sect="stats">
              <div className="tm-desc" style={{ marginBottom: 10 }}>
                These tests answer: <b>"Is this a real edge, or could random trading produce these results?"</b> A genuine trading edge must pass multiple independent statistical tests — any single metric can be gamed or misleading. Green badges mean the test passed convincingly.
              </div>
              <div className="tm-stat-grid">
                <div className="tm-stat-item"><i>Deflated Sharpe (DSR)</i><b>{safe(S.dsr, v => v.toFixed(3))}</b> {S.dsr != null && <Badge level={dsrTier(S.dsr)} label={dsrLabel(S.dsr)} />}</div>
                <div className="tm-stat-item"><i>t-test p-value</i><b>{S.tTest?.p < 0.0001 ? "< 0.0001" : safe(S.tTest?.p, v => v.toFixed(4))}</b> {S.tTest?.p != null && <Badge level={pTier(S.tTest.p)} label={pLabel(S.tTest.p)} />}</div>
                <div className="tm-stat-item"><i>Bootstrap P(μ&gt;0)</i><b>{safe(S.boot?.probPos, v => (v * 100).toFixed(0) + "%")}</b> {S.boot?.probPos != null && <Badge level={S.boot.probPos >= 0.99 ? "strong" : S.boot.probPos >= 0.95 ? "good" : "weak"} label={S.boot.probPos >= 0.99 ? "CERTAIN" : S.boot.probPos >= 0.95 ? "LIKELY" : "UNCERTAIN"} />}</div>
                <div className="tm-stat-item"><i>½-Kelly size</i><b>{safe(S.kelly?.half, v => (v * 100).toFixed(1) + "%")}</b> {S.kelly?.half != null && <Badge level={kellyTier(S.kelly.half)} label={S.kelly.half >= 0.15 ? "AGGRESSIVE" : S.kelly.half >= 0.08 ? "MODERATE" : S.kelly.half >= 0.03 ? "CONSERVATIVE" : "TINY"} />}</div>
                <div className="tm-stat-item"><i>Return skew</i><b>{safe(S.dist?.skew, v => v.toFixed(2))}</b> {S.dist?.skew != null && <Badge level={S.dist.skew > 0 ? "good" : "ok"} label={S.dist.skew > 0.5 ? "RIGHT SKEW" : S.dist.skew > 0 ? "SLIGHT RIGHT" : "LEFT SKEW"} />}</div>
                <div className="tm-stat-item"><i>CVaR (5%)</i><b className="neg">{safe(S.dist?.cvar5, v => v.toFixed(2) + "%")}</b> {S.dist?.cvar5 != null && <Badge level={Math.abs(S.dist.cvar5) < 3 ? "good" : Math.abs(S.dist.cvar5) < 6 ? "ok" : "weak"} label={Math.abs(S.dist.cvar5) < 3 ? "CONTAINED" : Math.abs(S.dist.cvar5) < 6 ? "MODERATE" : "HIGH TAIL"} />}</div>
                <div className="tm-stat-item"><i>Tail ratio</i><b>{safe(S.dist?.tailRatio, v => v.toFixed(2))}</b> {S.dist?.tailRatio != null && <Badge level={S.dist.tailRatio > 1.2 ? "good" : S.dist.tailRatio > 0.8 ? "ok" : "weak"} label={S.dist.tailRatio > 1.2 ? "FAVORABLE" : S.dist.tailRatio > 0.8 ? "NEUTRAL" : "UNFAVORABLE"} />}</div>
                <div className="tm-stat-item"><i>VC ratio (N/d)</i><b>{safe(S.vc?.ratio, v => v.toFixed(0))}</b> {S.vc?.verdict && <Badge level={S.vc.verdict === "good" ? "good" : S.vc.verdict === "ok" ? "ok" : "weak"} label={S.vc.verdict.toUpperCase()} />}</div>
              </div>
              <div className="tm-desc" style={{ marginTop: 8 }}>
                <b>DSR</b> = Sharpe adjusted for multiple strategy tests — the "real" Sharpe after accounting for data-snooping.
                <b> Skew</b> &gt; 0 means winners tend to be larger than losers (desirable).
                <b> CVaR(5%)</b> = average loss in the worst 5% of trades — your tail risk.
                <b> Tail ratio</b> = right tail / left tail; &gt;1 means big winners outweigh big losers.
                <b> VC ratio</b> = trades / parameters; &gt;20 means enough data to trust the fit.
              </div>
            </Panel>

            {/* Bootstrap CI distribution */}
            <Panel title="BOOTSTRAP CI · MEAN RETURN" meta="5 000 resamples" span={5} sect="stats">
              <div className="tm-desc">Resamples all trades with replacement 5 000 times and computes the mean return of each sample. The histogram shows the distribution of possible "true" mean returns — if the entire distribution is above zero, the edge is statistically robust.</div>
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
                <span>P(μ&gt;0) <b className="pos">{safe(S.boot.probPos, v => (v * 100).toFixed(0) + "%")}</b></span>
              </div>
              <Verdict level={ciTier(S.boot?.lo95)}>
                {S.boot?.lo95 > 0
                  ? <span><Badge level="strong" label="EDGE CONFIRMED" /> The lower bound of the 95% confidence interval is <b>{pct(S.boot.lo95, 2)}</b> — above zero. Even in the worst 2.5% of bootstrap scenarios, the strategy is profitable. This is strong evidence of a real, non-random edge.</span>
                  : <span><Badge level="weak" label="UNCERTAIN" /> The confidence interval crosses zero — the observed returns could be due to chance. More trades or a tighter strategy may be needed.</span>
                }
              </Verdict>
            </Panel>

            {/* T-test null distribution */}
            <Panel title="NULL HYPOTHESIS TEST" meta={`H₀: μ = 0 · t = ${safe(S.tTest?.t, v => v.toFixed(2))} · p ${S.tTest?.p < 0.0001 ? "< 0.0001" : safe(S.tTest?.p, v => "= " + v.toFixed(4))}`} span={4} sect="stats">
              <div className="tm-desc">Tests whether the mean trade return is statistically different from zero. The <b>t-statistic</b> measures how many standard errors the mean is from zero. The <b>p-value</b> is the probability of seeing this result by pure chance. A p &lt; 0.05 means we can reject the "no edge" hypothesis.</div>
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
                  offscaleLabel={`t = ${safe(S.tTest?.t, v => v.toFixed(2))}`}
                />
              </div>
              <div className="tm-stat-grid">
                <div className="tm-stat-item"><i>Mean return</i><b className="pos">{pct(S.tTest?.mean, 3)}</b></div>
                <div className="tm-stat-item"><i>Std error</i><b>{safe(S.tTest?.se, v => v.toFixed(4) + "%")}</b></div>
                <div className="tm-stat-item"><i>t-statistic</i><b className="accent">{safe(S.tTest?.t, v => v.toFixed(2))}</b></div>
                <div className="tm-stat-item"><i>p-value</i><b>{S.tTest?.p < 0.0001 ? "< 0.0001" : safe(S.tTest?.p, v => v.toFixed(4))} {S.tTest?.p != null && <Badge level={pTier(S.tTest.p)} label={pLabel(S.tTest.p)} />}</b></div>
              </div>
              <Verdict level={S.tTest?.p < 0.01 ? "strong" : S.tTest?.p < 0.05 ? undefined : "weak"}>
                {S.tTest?.p < 0.01
                  ? <span><Badge level="strong" label={S.tTest.p < 0.001 ? "p < 0.001" : "p < 0.01"} /> The observed t-stat of <b>{safe(S.tTest?.t, v => v.toFixed(2))}</b> is far beyond the ±1.96 rejection threshold. There is less than a <b>{S.tTest.p < 0.001 ? "0.1%" : "1%"}</b> chance this edge is random noise. The null hypothesis (no edge) is decisively rejected.</span>
                  : S.tTest?.p < 0.05
                    ? <span><Badge level="ok" label="MARGINAL" /> Statistically significant at 95% but not at 99%. The edge exists but with less certainty — more trades would strengthen the conclusion.</span>
                    : <span><Badge level="weak" label="NOT SIGNIFICANT" /> Cannot reject the null hypothesis. The observed returns may be due to chance.</span>
                }
              </Verdict>
            </Panel>

            {/* WFV fold performance */}
            <Panel title="WALK-FORWARD VALIDATION" meta="per-year consistency" span={3} sect="stats">
              <div className="tm-desc">Shows returns across different time windows. Consistent bars = the edge works across different market periods, not just one lucky stretch. Large variation between bars suggests regime-dependent performance.</div>
              <div className="tm-wfv">
                {Object.entries(G.wfvFolds).map(([name, folds]) => (
                  <FoldRow key={name} name={name} folds={folds} />
                ))}
              </div>
              {(() => {
                const allFolds = Object.values(G.wfvFolds).flat().filter(v => v != null);
                if (allFolds.length < 2) return null;
                const mean = allFolds.reduce((a,b) => a+b, 0) / allFolds.length;
                const std = Math.sqrt(allFolds.reduce((a,v) => a + (v-mean)**2, 0) / allFolds.length);
                const cv = mean !== 0 ? std / Math.abs(mean) : 999;
                const allPos = allFolds.every(v => v > 0);
                const tier = allPos && cv < 0.5 ? "strong" : allPos ? "good" : cv < 1 ? "ok" : "weak";
                return (
                  <Verdict level={tier}>
                    <Badge level={tier} label={tier === "strong" ? "CONSISTENT" : tier === "good" ? "POSITIVE" : tier === "ok" ? "MIXED" : "UNSTABLE"} />{" "}
                    {allPos
                      ? <span>All time windows are profitable (CV={cv.toFixed(2)}). {cv < 0.5 ? "Low variation confirms a robust, regime-independent edge." : "Some variation between periods — edge is real but regime-sensitive."}</span>
                      : <span>Some windows show negative returns — the edge may depend on specific market conditions or be partially overfit.</span>
                    }
                  </Verdict>
                );
              })()}
            </Panel>

            <Sep label="OUTCOME DISTRIBUTIONS · PER STRATEGY" />

            {/* strategy return distributions */}
            <Panel title="TRADE RETURN DISTRIBUTIONS" meta="CVaR(5%) shaded · dashed line: mean (μ)" span={12} flush sect="stats">
              <div className="tm-desc" style={{ padding: "0 11px", marginBottom: 4 }}>
                Each histogram shows the distribution of individual trade returns. A distribution shifted right with a long right tail is ideal — it means most trades are profitable and winners occasionally run big. The <b>red shading</b> marks CVaR(5%): the average loss in the worst 5% of trades (tail risk). The <b>dashed line</b> marks the mean return (μ). <b>SR</b> = Sharpe ratio for that strategy alone.
              </div>
              <div className="tm-strat-dists">
                {G.strategies.map(s => <MiniDist key={s.key} s={s} />)}
              </div>
            </Panel>

            <Sep label="STRESS TESTS" />

            {/* crash */}
            <Panel title="CRASH STRESS TEST" meta="historical BTC ≥ 15% drawdown windows" span={7} sect="stress">
              <div className="tm-desc">How the portfolio performed during major BTC crashes. <b>Open</b> = positions open when crash started. <b>Exp</b> = total portfolio exposure. <b>Clean</b> = portfolio hit if all stops execute normally. <b>+Slip</b> = estimated hit with slippage (ATR expansion during crashes).</div>
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
                      <td className={"r mono " + (Math.abs(c.clean) < 5 ? "" : "neg")}>{pct(c.clean, 1)} {Math.abs(c.clean) < 5 && <Badge level="good" label="CONTAINED" />}</td>
                      <td className="r mono neg b">{pct(c.slip, 0)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {G.stress.worstCase && <Verdict level={Math.abs(G.stress.worstCase.cleanHit) < 10 ? "strong" : undefined}>
                <b>Synthetic worst-case:</b> All {G.stress.worstCase.peakExposure}% of concurrent positions stop out simultaneously → clean hit {pct(G.stress.worstCase.cleanHit, 1)}, with 3× ATR crash slippage {pct(G.stress.worstCase.expandedHit, 1)}.
                {Math.abs(G.stress.worstCase.cleanHit) < 10
                  ? <span> <Badge level="good" label="SURVIVABLE" /> Clean stops keep losses under 10% of portfolio — position sizing protects the account.</span>
                  : <span> <Badge level="weak" label="SIGNIFICANT RISK" /> Clean stops exceed 10% — consider reducing concurrent position limits or tightening stops.</span>
                }
              </Verdict>}
            </Panel>

            {/* rally */}
            <Panel title="RALLY STRESS TEST" meta="BTC ≥ 15% rise from 7d trough" span={5} sect="stress">
              <div className="tm-desc">Performance during strong BTC rallies. Shows whether the portfolio captures upside during bull moves. <b>Port</b> = total portfolio return during the rally window.</div>
              <table className="tm-tbl">
                <thead><tr><th>WINDOW</th><th className="r">BTC</th><th className="r">DUR</th><th className="r">W/L</th><th className="r">AVG</th><th className="r">PORT</th></tr></thead>
                <tbody>
                  {G.stress.rallies.map((c, i) => (
                    <tr key={i}>
                      <td className="b">{c.label} <span className="dim">{c.date}</span></td>
                      <td className="r mono pos">+{safe(c.rise, v => typeof v === 'number' ? v.toFixed(1) : v)}%</td>
                      <td className="r dim">{c.dur}</td>
                      <td className="r mono">{c.w}/{c.l}</td>
                      <td className="r mono pos">{pct(c.avgRet, 2)}</td>
                      <td className="r mono pos b">{pct(c.portHit, 1)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {(() => {
                const rallies = G.stress.rallies || [];
                const avgHit = rallies.length > 0 ? rallies.reduce((a, r) => a + (r.portHit || 0), 0) / rallies.length : 0;
                return rallies.length > 0 && (
                  <Verdict level={avgHit > 0 ? "strong" : "weak"}>
                    {avgHit > 0
                      ? <span><Badge level="good" label="CAPTURES UPSIDE" /> Average portfolio gain of <b>{pct(avgHit, 1)}</b> during rallies — the long strategies participate in bull moves while FadeShort stays hedged.</span>
                      : <span><Badge level="weak" label="MISSES RALLIES" /> The portfolio underperforms during rallies — bull-regime strategies may need tuning.</span>
                    }
                  </Verdict>
                );
              })()}
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
