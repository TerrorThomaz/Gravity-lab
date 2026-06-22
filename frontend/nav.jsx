/* nav.jsx — shared top navigation bar for all Gravity-gen2 pages.
   Exports window.GravNav. Load after React, before page scripts. */
(function () {
  const TABS = [
    { id: 'terminal',  label: 'DASHBOARD',  href: 'Gravity Terminal.html' },
    { id: 'genotypes', label: 'GENOTYPES',  href: 'Gravity Genotypes.html' },
    { id: 'training',  label: 'TRAINING',   href: 'Gravity Training.html' },
  ];

  function GravNav({ active, liveState, baseline }) {
    const cycle   = liveState?.CycleCount ?? liveState?.cycleCount ?? '—';
    const sharpe  = baseline?.sharpe  ?? '—';
    const trades  = baseline?.trades  ?? '—';
    return (
      <header className="gn-bar">
        <div className="gn-brand">
          <span className="gn-dot" />
          <span className="gn-name">GRAVITY<i>-GEN2</i></span>
        </div>
        <nav className="gn-tabs">
          {TABS.map(t => (
            <a key={t.id} href={t.href}
              className={"gn-tab" + (active === t.id ? " gn-tab--active" : "")}>
              {t.label}
            </a>
          ))}
        </nav>
        <div className="gn-meta">
          <span className="gn-m"><i>CYCLE</i><b>{cycle}</b></span>
          <span className="gn-m"><i>SHARPE</i><b>{typeof sharpe === 'number' ? sharpe.toFixed(2) : sharpe}</b></span>
          <span className="gn-m"><i>TRADES</i><b>{typeof trades === 'number' ? trades.toLocaleString() : trades}</b></span>
          <span className="gn-live"><span className="gn-live-dot" />LIVE</span>
        </div>
      </header>
    );
  }

  /* ── Shared nav + page CSS (injected once) ─────────────────────────────── */
  const NAV_CSS = `
:root{
  --bg:oklch(0.16 0.006 250); --panel:oklch(0.195 0.007 250);
  --panel-2:oklch(0.225 0.008 250); --panel-3:oklch(0.255 0.009 250);
  --line:oklch(0.31 0.009 250); --line-s:oklch(0.26 0.008 250);
  --ink:oklch(0.93 0.006 250); --ink-dim:oklch(0.68 0.008 250);
  --ink-faint:oklch(0.52 0.008 250);
  --accent:oklch(0.80 0.058 78); --accent-s:oklch(0.80 0.058 78 / 0.14);
  --pos:oklch(0.76 0.085 158); --neg:oklch(0.67 0.115 28);
  --mono:"IBM Plex Mono",ui-monospace,SFMono-Regular,Menlo,monospace;
  --sans:"IBM Plex Sans",system-ui,sans-serif;
}
*{box-sizing:border-box;margin:0;padding:0;}
body{background:var(--bg);color:var(--ink);font-family:var(--sans);
  font-size:13px;-webkit-font-smoothing:antialiased;}
.pos{color:var(--pos);} .neg{color:var(--neg);} .accent{color:var(--accent);}
.dim{color:var(--ink-dim);} .mono{font-family:var(--mono);font-variant-numeric:tabular-nums;}
.b{font-weight:600;} .r{text-align:right;}
table{border-collapse:collapse;width:100%;}

/* nav bar */
.gn-bar{display:flex;align-items:center;height:48px;padding:0 18px;
  background:var(--panel-2);border-bottom:1px solid var(--line);
  position:sticky;top:0;z-index:30;gap:0;}
.gn-brand{display:flex;align-items:center;gap:10px;margin-right:28px;flex-shrink:0;}
.gn-dot{width:9px;height:9px;border-radius:50%;background:var(--accent);
  box-shadow:0 0 0 3px var(--accent-s);}
.gn-name{font-family:var(--mono);font-weight:700;font-size:14px;letter-spacing:.04em;}
.gn-name i{font-style:normal;color:var(--ink-dim);}
.gn-tabs{display:flex;gap:0;height:100%;align-items:stretch;}
.gn-tab{display:flex;align-items:center;padding:0 18px;font-family:var(--mono);
  font-size:11px;font-weight:600;letter-spacing:.10em;color:var(--ink-faint);
  text-decoration:none;border-bottom:2px solid transparent;
  transition:color .15s,border-color .15s;}
.gn-tab:hover{color:var(--ink);}
.gn-tab--active{color:var(--accent);border-bottom-color:var(--accent);}
.gn-meta{display:flex;align-items:center;gap:16px;margin-left:auto;
  font-family:var(--mono);}
.gn-m{display:flex;align-items:baseline;gap:6px;}
.gn-m i{font-style:normal;font-size:9.5px;color:var(--ink-faint);letter-spacing:.06em;}
.gn-m b{font-size:13px;font-weight:600;}
.gn-live{display:flex;align-items:center;gap:7px;font-size:11px;
  font-weight:700;letter-spacing:.08em;color:var(--pos);margin-left:8px;}
.gn-live-dot{width:7px;height:7px;border-radius:50%;background:var(--pos);
  animation:pulse 2s ease-in-out infinite;}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:.4}}

/* shared page body */
.gv-body{padding:14px 14px 40px;background:var(--bg);}
.gv-section{margin-bottom:28px;}
.gv-sh{display:flex;align-items:center;gap:12px;margin-bottom:13px;}
.gv-sh-n{font-family:var(--mono);font-size:10px;color:var(--accent);
  font-weight:700;letter-spacing:.12em;}
.gv-sh h2{font-size:15px;font-weight:600;letter-spacing:-.01em;margin:0;}
.gv-sh-m{font-family:var(--mono);font-size:10.5px;color:var(--ink-faint);}
.gv-sh-rule{flex:1;height:1px;background:var(--line-s);}

/* shared card */
.gv-card{background:var(--panel);border:1px solid var(--line);border-radius:4px;overflow:hidden;}
.gv-card-head{display:flex;align-items:center;justify-content:space-between;
  padding:8px 12px;background:var(--panel-2);border-bottom:1px solid var(--line-s);}
.gv-card-title{font-family:var(--mono);font-size:11px;font-weight:700;
  letter-spacing:.09em;color:var(--accent);}
.gv-card-meta{font-family:var(--mono);font-size:10px;color:var(--ink-faint);}
.gv-card-body{padding:12px;}

/* pill tags */
.gv-pill{display:inline-block;font-family:var(--mono);font-size:9.5px;
  font-weight:600;letter-spacing:.05em;padding:2px 7px;border-radius:2px;
  border:1px solid var(--line);color:var(--ink-faint);margin:0 3px 3px 0;}
.gv-pill--active{color:var(--pos);border-color:color-mix(in oklch,var(--pos),transparent 60%);}
.gv-pill--draft{color:var(--ink-faint);opacity:.7;}
.gv-pill--hv{color:var(--accent);border-color:var(--accent-s);}
.gv-pill--bear{color:var(--neg);border-color:color-mix(in oklch,var(--neg),transparent 65%);}

/* shared table */
.gv-tbl{font-family:var(--mono);font-size:12px;}
.gv-tbl th{text-align:left;color:var(--ink-faint);font-weight:500;font-size:10px;
  letter-spacing:.05em;padding:5px 9px;border-bottom:1px solid var(--line-s);}
.gv-tbl td{padding:7px 9px;border-bottom:1px solid var(--line-s);}
.gv-tbl tbody tr:last-child td{border-bottom:none;}
.gv-tbl tbody tr:hover{background:var(--panel-2);}

/* badge */
.gv-badge{display:inline-block;font-family:var(--mono);font-size:9.5px;
  font-weight:700;letter-spacing:.06em;padding:2px 7px;border-radius:2px;}
.gv-badge--active{background:color-mix(in oklch,var(--pos),transparent 85%);color:var(--pos);}
.gv-badge--draft{background:rgba(255,255,255,0.05);color:var(--ink-faint);}
.gv-badge--live{background:var(--accent-s);color:var(--accent);}

/* button */
.gv-btn{display:inline-flex;align-items:center;gap:7px;
  font-family:var(--mono);font-size:11px;font-weight:600;letter-spacing:.06em;
  padding:7px 14px;border-radius:3px;border:1px solid var(--line);
  background:var(--panel-2);color:var(--ink-dim);cursor:pointer;transition:all .15s;}
.gv-btn:hover{border-color:var(--accent);color:var(--accent);}
.gv-btn--primary{background:color-mix(in oklch,var(--pos),transparent 85%);
  color:var(--pos);border-color:color-mix(in oklch,var(--pos),transparent 60%);}
.gv-btn--primary:hover{background:color-mix(in oklch,var(--pos),transparent 75%);}
.gv-btn--danger{background:color-mix(in oklch,var(--neg),transparent 85%);
  color:var(--neg);border-color:color-mix(in oklch,var(--neg),transparent 60%);}
.gv-btn--sm{padding:4px 10px;font-size:10px;}
`;

  function injectNavCSS() {
    if (document.getElementById('grav-nav-css')) return;
    const s = document.createElement('style');
    s.id = 'grav-nav-css';
    s.textContent = NAV_CSS;
    document.head.appendChild(s);
  }
  injectNavCSS();

  window.GravNav = GravNav;
})();
