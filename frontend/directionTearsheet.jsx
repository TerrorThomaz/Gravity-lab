/* directionTearsheet.jsx — Direction C: "TEARSHEET"
   Left rail (identity, section nav, live regime, equity spark) + a main column
   that reads like a quant research tearsheet: ruled section headers, dense stat
   blocks, distribution + convergence charts. Print-like restraint. */
(function () {
  const G = window.GRAV;
  const { LineChart, Sparkline, Histogram, BarRow, CIBar } = window;

  const sgn = v => (v > 0 ? "pos" : v < 0 ? "neg" : "");
  const pct = (v, d = 1) => (v > 0 ? "+" : "") + v.toFixed(d) + "%";
  const num = (v, d = 2) => v.toLocaleString("en-US", { minimumFractionDigits: d, maximumFractionDigits: d });

  const NAV = [
    ["live", "Live papertrade"],
    ["backtest", "Backtest"],
    ["strategies", "Strategies"],
    ["stats", "Statistical validation"],
    ["stress", "Stress tests"],
    ["training", "GA training"]
  ];

  function SH({ n, title, meta }) {
    return (
      <div className="ts-sh">
        <span className="ts-sh-n">{n}</span>
        <h2>{title}</h2>
        {meta && <span className="ts-sh-m">{meta}</span>}
        <span className="ts-sh-rule" />
      </div>
    );
  }

  function Stat({ k, v, cls, note }) {
    return (
      <div className="ts-stat">
        <dt>{k}</dt>
        <dd className={cls}>{v}{note && <i> {note}</i>}</dd>
      </div>
    );
  }

  function DirectionTearsheet() {
    const L = G.live, B = G.backtest, S = G.stats, O = G.oos, T = G.training;
    const maxRet = Math.max(...G.strategies.map(s => s.ret));
    return (
      <div className="dir dir-tearsheet">
        {/* left rail */}
        <aside className="ts-rail">
          <div className="ts-brand">
            <div className="ts-brand-mark">G2</div>
            <div className="ts-brand-txt"><b>Gravity-gen2</b><i>Strategy tearsheet</i></div>
          </div>

          <div className={"ts-regime ts-regime--bull"}>
            <div className="ts-regime-top"><span className="ts-rdot" /> {L.regime.state} regime</div>
            <div className="ts-regime-bar"><span style={{ width: (L.regime.confidence * 100) + "%" }} /></div>
            <div className="ts-regime-meta">confidence {L.regime.confidence.toFixed(2)} · {L.regime.duration} bars · size× {L.router.sizeMult.toFixed(2)}</div>
          </div>

          <nav className="ts-nav">
            {NAV.map(([id, label], i) => (
              <a key={id} className={"ts-nav-i" + (i === 0 ? " on" : "")} href={"#" + id}>
                <span className="ts-nav-dot" />{label}
              </a>
            ))}
          </nav>

          <div className="ts-rail-spark">
            <div className="ts-rail-lab">Equity · {G.equity[G.equity.length - 1].toFixed(0)} <span className="pos">{pct(B.netReturn, 0)}</span></div>
            <div style={{ color: "var(--pos)" }}><Sparkline data={G.equity} width={196} height={42} strokeWidth={1.6} /></div>
          </div>

          <div className="ts-rail-foot">
            <div><i>NAV</i><b>€{num(L.equityEur)}</b></div>
            <div><i>Today</i><b className={sgn(L.dayPnl)}>{pct(L.dayPnl)}</b></div>
            <div><i>Cycle</i><b>{L.cycle}</b></div>
          </div>
        </aside>

        {/* main column */}
        <main className="ts-main">
          <header className="ts-head">
            <h1>Portfolio performance & validation</h1>
            <p>Generated {B.timestamp.slice(0, 10)} · {B.coins} coins · {B.window} · dual-timeframe 1h / 15m · walk-forward {T.folds}-fold</p>
          </header>

          {/* headline row */}
          <div className="ts-headline">
            <div className="ts-hl"><i>Net return</i><b className="pos">{pct(B.netReturn, 0)}</b></div>
            <div className="ts-hl"><i>Sharpe</i><b>{B.sharpe.toFixed(2)}</b></div>
            <div className="ts-hl"><i>CAGR</i><b>{B.cagr.toFixed(1)}%</b></div>
            <div className="ts-hl"><i>Max DD</i><b className="neg">{pct(B.maxDD, 1)}</b></div>
            <div className="ts-hl"><i>Calmar</i><b>{B.calmar.toFixed(2)}</b></div>
            <div className="ts-hl"><i>Win rate</i><b>{B.winRate.toFixed(1)}%</b></div>
          </div>

          {/* live */}
          <section data-section="live" id="live">
            <SH n="01" title="Live papertrade" meta={"cycle " + L.cycle + " · next " + L.nextRefresh} />
            <div className="ts-live">
              <div className="ts-live-tbl">
                <table className="ts-tbl">
                  <thead><tr><th>Symbol</th><th>Strategy</th><th>Dir</th><th className="r">Entry</th><th className="r">Mark</th><th className="r">P&L</th><th className="r">Age</th></tr></thead>
                  <tbody>
                    {L.positions.map((p, i) => (
                      <tr key={i}>
                        <td className="b">{p.sym}</td>
                        <td className="dim">{p.strat}</td>
                        <td className={sgn(p.dir === "Long" ? 1 : -1)}>{p.dir}</td>
                        <td className="r mono">{p.entry}</td>
                        <td className="r mono">{p.mark}</td>
                        <td className={"r mono b " + sgn(p.pnl)}>{pct(p.pnl)}</td>
                        <td className="r dim">{p.age}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div className="ts-live-side">
                <div className="ts-router-lab">Router gates · size× {L.router.sizeMult.toFixed(2)}</div>
                <div className="ts-router">
                  {Object.entries(L.router).filter(([k]) => k !== "sizeMult").map(([k, on]) => (
                    <span key={k} className={"ts-gate " + (on ? "on" : "off")}>{on ? "●" : "○"} {k}</span>
                  ))}
                </div>
                <div className="ts-sig-lab">Latest signals</div>
                {L.signals.slice(0, 4).map((s, i) => (
                  <div className="ts-sig" key={i}>
                    <span className={"ts-act ts-act--" + s.action.toLowerCase()}>{s.action}</span>
                    <b>{s.sym}</b><i>{s.note}</i>
                  </div>
                ))}
              </div>
            </div>
          </section>

          {/* backtest */}
          <section data-section="backtest" id="backtest">
            <SH n="02" title="Backtest" meta={"validation " + B.valSplit + "% · " + B.trades.toLocaleString() + " trades"} />
            <div className="ts-chart" style={{ color: "var(--pos)" }}>
              <LineChart data={G.equity} benchmark={G.benchmark} height={190} fill strokeWidth={1.8} />
            </div>
            <div className="ts-legend"><span><i className="sw sw-pos" /> portfolio</span><span><i className="sw sw-bench" /> BTC buy & hold</span><span className="ts-legend-r">OOS Sharpe {O.sharpe.toFixed(2)} on {O.coins} unseen coins · {pct(O.degradation, 0)} degradation</span></div>
            <dl className="ts-stats">
              <Stat k="Profit factor" v={B.profitFactor.toFixed(2)} />
              <Stat k="Expectancy" v={pct(B.expectancy, 2)} cls="pos" />
              <Stat k="OOS net return" v={pct(O.netReturn, 0)} cls="pos" />
              <Stat k="OOS win rate" v={O.winRate.toFixed(1) + "%"} />
            </dl>
          </section>

          {/* strategies */}
          <section data-section="strategies" id="strategies">
            <SH n="03" title="Strategies" meta="5 · shared capital · router-gated" />
            <table className="ts-tbl ts-tbl--strat">
              <thead><tr><th>Strategy</th><th>Regime</th><th>Dir</th><th className="r">Trades</th><th className="r">Win%</th><th className="r">Sharpe</th><th className="r">PF</th><th className="r">½-Kelly</th><th className="r">DSR</th><th>Return</th></tr></thead>
              <tbody>
                {G.strategies.map((s, i) => (
                  <tr key={i}>
                    <td className="b">{s.key}</td>
                    <td className="dim">{s.regime}</td>
                    <td className={sgn(s.dir === "Long" ? 1 : -1)}>{s.dir}</td>
                    <td className="r mono">{s.trades}</td>
                    <td className="r mono">{s.win.toFixed(1)}</td>
                    <td className="r mono">{s.sharpe.toFixed(2)}</td>
                    <td className="r mono">{s.pf.toFixed(2)}</td>
                    <td className="r mono dim">{(s.halfKelly * 100).toFixed(1)}%</td>
                    <td className="r mono">{s.dsr.toFixed(2)}</td>
                    <td className="ts-bar-cell">
                      <span style={{ color: "var(--pos)" }}><BarRow value={s.ret} max={maxRet} width={110} height={9} color="var(--pos)" /></span>
                      <b className="pos">{pct(s.ret, 0)}</b>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>

          {/* statistical validation */}
          <section data-section="stats" id="stats">
            <SH n="04" title="Statistical validation" meta="StrategyStats · n=2062" />
            <div className="ts-stat-cols">
              <dl className="ts-stats ts-stats--col">
                <Stat k="t-test (vs 0)" v={"t " + S.tTest.t.toFixed(2)} note={"p " + S.tTest.p.toFixed(4) + " ***"} cls="" />
                <Stat k="Bootstrap P(μ>0)" v={(S.boot.probPos * 100).toFixed(0) + "%"} cls="pos" />
                <Stat k="Deflated Sharpe" v={S.dsr.toFixed(2)} note="✓ strong" cls="" />
                <Stat k="Kelly · ½-Kelly" v={(S.kelly.full * 100).toFixed(1) + "% · " + (S.kelly.half * 100).toFixed(1) + "%"} />
              </dl>
              <dl className="ts-stats ts-stats--col">
                <Stat k="Skewness" v={S.dist.skew.toFixed(2)} note="right-tail" />
                <Stat k="Excess kurtosis" v={S.dist.exKurt.toFixed(2)} note="fat tails" />
                <Stat k="CVaR (5%)" v={S.dist.cvar5.toFixed(2) + "%"} cls="neg" />
                <Stat k="VC · N/d" v={"d=" + S.vc.d + " · " + S.vc.ratio.toFixed(0)} note={S.vc.verdict} cls="" />
              </dl>
            </div>
            <div className="ts-twocol">
              <div className="ts-ci-block">
                <div className="ts-ci-lab">95% bootstrap CI · mean trade return</div>
                <CIBar lo={S.boot.lo95} hi={S.boot.hi95} mean={S.boot.mean} domainLo={-0.2} domainHi={1.2} color="var(--accent)" />
                <div className="ts-ci-ax"><span>-0.2%</span><span>+0.81%</span><span>+1.2%</span></div>
                <div className="ts-mini">Hoeffding ±{S.hoeffding.bound.toFixed(2)}% · certify ±0.5% needs {S.hoeffding.minN05} trades</div>
              </div>
              <div className="ts-dist-block">
                <div className="ts-ci-lab">Monthly return distribution</div>
                <div style={{ color: "var(--pos)" }}><Histogram data={G.monthlyReturns} bins={13} height={96} pos="var(--pos)" neg="var(--neg)" /></div>
                <div className="ts-dist-ax"><span className="neg">{Math.min(...G.monthlyReturns).toFixed(0)}%</span><span className="pos">+{Math.max(...G.monthlyReturns).toFixed(0)}%</span></div>
              </div>
            </div>
          </section>

          {/* stress */}
          <section data-section="stress" id="stress">
            <SH n="05" title="Stress tests" meta="historical crash & rally windows" />
            <div className="ts-twocol">
              <div>
                <div className="ts-ci-lab ts-neg-lab">Crash · BTC ≥15% drawdown</div>
                <table className="ts-tbl ts-tbl--mini">
                  <thead><tr><th>Window</th><th className="r">BTC</th><th className="r">W/L</th><th className="r">Clean</th><th className="r">+Slip</th></tr></thead>
                  <tbody>
                    {G.stress.crashes.map((c, i) => (
                      <tr key={i}><td className="b">{c.label} <span className="dim">{c.date}</span></td><td className="r mono neg">{c.drop}%</td><td className="r mono">{c.w}/{c.l}</td><td className="r mono neg">{pct(c.clean, 1)}</td><td className="r mono neg b">{pct(c.slip, 0)}</td></tr>
                    ))}
                  </tbody>
                </table>
              </div>
              <div>
                <div className="ts-ci-lab ts-pos-lab">Rally · BTC ≥15% rise</div>
                <table className="ts-tbl ts-tbl--mini">
                  <thead><tr><th>Window</th><th className="r">BTC</th><th className="r">W/L</th><th className="r">Avg</th><th className="r">Port</th></tr></thead>
                  <tbody>
                    {G.stress.rallies.map((c, i) => (
                      <tr key={i}><td className="b">{c.label} <span className="dim">{c.date}</span></td><td className="r mono pos">+{c.rise}%</td><td className="r mono">{c.w}/{c.l}</td><td className="r mono pos">{pct(c.avgRet, 2)}</td><td className="r mono pos b">{pct(c.portHit, 1)}</td></tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
            <div className="ts-mini">Synthetic worst case · all concurrent positions stop at once @ {G.stress.worstCase.peakExposure}% peak exposure → clean {pct(G.stress.worstCase.cleanHit, 1)} · with 3× ATR expansion {pct(G.stress.worstCase.expandedHit, 1)}</div>
          </section>

          {/* GA training */}
          <section data-section="training" id="training">
            <SH n="06" title="GA training" meta={"cycle " + T.cycleCount + " · pop " + T.popSize + " · " + T.folds + "-fold WFV"} />
            <div className="ts-twocol">
              <div>
                <div className="ts-ci-lab">Fitness convergence</div>
                <div style={{ color: "var(--accent)" }}><LineChart data={T.best} benchmark={T.mean} height={120} stroke="var(--accent)" strokeWidth={1.8} benchStroke="rgba(255,255,255,0.3)" fill /></div>
                <div className="ts-legend"><span><i className="sw sw-acc" /> best</span><span><i className="sw sw-bench" /> mean</span></div>
              </div>
              <div>
                <div className="ts-ci-lab">Population fitness spread</div>
                <div className="ts-pop">
                  {T.population.map((f, i) => (
                    <div className="ts-pop-row" key={i}>
                      <span className="ts-pop-i">{(i + 1).toString().padStart(2, "0")}</span>
                      <span style={{ color: f < 0 ? "var(--neg)" : "var(--accent)" }}><BarRow value={f} max={2} min={-0.8} width={150} height={6} color={f < 0 ? "var(--neg)" : "var(--accent)"} /></span>
                      <b className={"mono " + (f < 0 ? "neg" : "")}>{f.toFixed(2)}</b>
                    </div>
                  ))}
                </div>
              </div>
            </div>
            <dl className="ts-stats">
              <Stat k="Best fitness" v={T.bestFitness.toFixed(2)} cls="pos" />
              <Stat k="TPE refine lift" v={"+" + (T.bayesOpt.lift * 100).toFixed(1) + "%"} cls="pos" />
              <Stat k="Bayes iters" v={T.bayesOpt.iters} />
              <Stat k="Saved" v={T.savedAt.slice(0, 10)} />
            </dl>
          </section>
        </main>
      </div>
    );
  }

  window.DirectionTearsheet = DirectionTearsheet;
})();
