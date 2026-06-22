/* charts.jsx — tiny dependency-free SVG chart helpers for the Gravity dashboard.
   All charts read plain number arrays and inherit color via CSS currentColor /
   explicit props. Exported to window for cross-file use. */

// ---- helpers ---------------------------------------------------------------
function _minmax(arr) {
  let lo = Infinity, hi = -Infinity;
  for (const v of arr) { if (v < lo) lo = v; if (v > hi) hi = v; }
  return [lo, hi];
}
function _path(data, w, h, pad) {
  const [lo, hi] = _minmax(data);
  const span = hi - lo || 1;
  const innerW = w - pad * 2, innerH = h - pad * 2;
  return data.map((v, i) => {
    const x = pad + (i / (data.length - 1)) * innerW;
    const y = pad + innerH - ((v - lo) / span) * innerH;
    return `${i === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(" ");
}

// ---- LineChart (optionally with a benchmark + area fill) -------------------
function LineChart({ data, benchmark, width = 600, height = 180, pad = 6, stroke = "currentColor", fill = false, strokeWidth = 1.6, benchStroke = "rgba(255,255,255,0.22)" }) {
  const [lo, hi] = _minmax(benchmark ? data.concat(benchmark) : data);
  const span = hi - lo || 1;
  const innerW = width - pad * 2, innerH = height - pad * 2;
  const pts = data.map((v, i) => {
    const x = pad + (i / (data.length - 1)) * innerW;
    const y = pad + innerH - ((v - lo) / span) * innerH;
    return [x, y];
  });
  const d = pts.map((p, i) => `${i === 0 ? "M" : "L"}${p[0].toFixed(1)},${p[1].toFixed(1)}`).join(" ");
  const area = `${d} L${pts[pts.length - 1][0].toFixed(1)},${height - pad} L${pts[0][0].toFixed(1)},${height - pad} Z`;
  const gid = "g" + Math.random().toString(36).slice(2, 8);
  return (
    <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} preserveAspectRatio="none" style={{ display: "block" }}>
      <defs>
        <linearGradient id={gid} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0%" stopColor={stroke} stopOpacity="0.22" />
          <stop offset="100%" stopColor={stroke} stopOpacity="0" />
        </linearGradient>
      </defs>
      {benchmark && <path d={_path(benchmark, width, height, pad)} fill="none" stroke={benchStroke} strokeWidth="1" strokeDasharray="3 3" vectorEffect="non-scaling-stroke" />}
      {fill && <path d={area} fill={`url(#${gid})`} stroke="none" />}
      <path d={d} fill="none" stroke={stroke} strokeWidth={strokeWidth} vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
    </svg>
  );
}

// ---- Sparkline -------------------------------------------------------------
function Sparkline({ data, width = 120, height = 28, stroke = "currentColor", strokeWidth = 1.4 }) {
  return (
    <svg viewBox={`0 0 ${width} ${height}`} width={width} height={height} preserveAspectRatio="none" style={{ display: "block" }}>
      <path d={_path(data, width, height, 2)} fill="none" stroke={stroke} strokeWidth={strokeWidth} vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
    </svg>
  );
}

// ---- Histogram (return distribution) ---------------------------------------
function Histogram({ data, bins = 13, width = 600, height = 150, pos = "currentColor", neg = "rgba(255,255,255,0.25)" }) {
  const [lo, hi] = _minmax(data);
  const span = (hi - lo) || 1;
  const counts = new Array(bins).fill(0);
  for (const v of data) {
    let b = Math.floor(((v - lo) / span) * bins);
    if (b >= bins) b = bins - 1;
    counts[b]++;
  }
  const maxC = Math.max(...counts) || 1;
  const bw = width / bins;
  const zeroBin = Math.floor(((0 - lo) / span) * bins);
  return (
    <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} preserveAspectRatio="none" style={{ display: "block" }}>
      {counts.map((c, i) => {
        const bh = (c / maxC) * (height - 4);
        return <rect key={i} x={i * bw + 1} y={height - bh} width={bw - 2} height={bh} fill={i < zeroBin ? neg : pos} opacity="0.85" />;
      })}
    </svg>
  );
}

// ---- Horizontal bar row (for strategy returns, population fitness) ---------
function BarRow({ value, max, min = 0, width = 160, height = 8, color = "currentColor", track = "rgba(255,255,255,0.06)" }) {
  const span = max - min || 1;
  const neg = value < 0;
  const zeroX = ((0 - min) / span) * width;
  const valX = ((value - min) / span) * width;
  const x = neg ? valX : zeroX;
  const w = Math.abs(valX - zeroX);
  return (
    <svg viewBox={`0 0 ${width} ${height}`} width={width} height={height} style={{ display: "block" }}>
      <rect x="0" y="0" width={width} height={height} fill={track} rx="1" />
      <rect x={x} y="0" width={Math.max(w, 1)} height={height} fill={color} rx="1" />
    </svg>
  );
}

// ---- Confidence-interval bar (bootstrap CI) --------------------------------
function CIBar({ lo, hi, mean, domainLo, domainHi, width = 240, height = 26, color = "currentColor" }) {
  const span = domainHi - domainLo || 1;
  const X = v => ((v - domainLo) / span) * width;
  const zeroX = X(0);
  const cy = height / 2;
  return (
    <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} preserveAspectRatio="none" style={{ display: "block" }}>
      <line x1={zeroX} y1="2" x2={zeroX} y2={height - 2} stroke="rgba(255,255,255,0.28)" strokeWidth="1" strokeDasharray="2 2" vectorEffect="non-scaling-stroke" />
      <line x1={X(lo)} y1={cy} x2={X(hi)} y2={cy} stroke={color} strokeWidth="2" vectorEffect="non-scaling-stroke" />
      <line x1={X(lo)} y1={cy - 5} x2={X(lo)} y2={cy + 5} stroke={color} strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
      <line x1={X(hi)} y1={cy - 5} x2={X(hi)} y2={cy + 5} stroke={color} strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
      <circle cx={X(mean)} cy={cy} r="3.2" fill={color} />
    </svg>
  );
}

// ---- Donut / gauge (regime confidence) -------------------------------------
function Gauge({ value, size = 64, stroke = 6, color = "currentColor", track = "rgba(255,255,255,0.1)" }) {
  const r = (size - stroke) / 2;
  const c = 2 * Math.PI * r;
  const off = c * (1 - value);
  return (
    <svg viewBox={`0 0 ${size} ${size}`} width={size} height={size}>
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={track} strokeWidth={stroke} />
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={color} strokeWidth={stroke} strokeLinecap="round"
        strokeDasharray={c} strokeDashoffset={off} transform={`rotate(-90 ${size / 2} ${size / 2})`} />
    </svg>
  );
}

// ---- Smooth PDF curve (normal / t-distribution) ----------------------------
// pts: [{x, y}] · shade rejection tails with shadeLo / shadeHi · vertical markers
function NormalCurve({ pts, width = 400, height = 130, pad = 8,
  stroke = "var(--ink-dim)", shadeLo = null, shadeHi = null,
  shadeColor = "rgba(200,70,50,0.22)", markers = [],
  offscaleLabel = null }) {
  if (!pts || pts.length === 0) return null;
  const xs = pts.map(p => p.x), ys = pts.map(p => p.y);
  const xlo = xs[0], xhi = xs[xs.length - 1], ymax = Math.max(...ys) || 1;
  const px = x => pad + (x - xlo) / (xhi - xlo) * (width - 2 * pad);
  const py = y => height - pad - Math.max(0, y / ymax) * (height - 2 * pad - 4);
  const d = pts.map((p, i) => `${i === 0 ? "M" : "L"}${px(p.x).toFixed(1)},${py(p.y).toFixed(1)}`).join(" ");

  function shadePath(ptsSub, xEdge, fromLeft) {
    if (!ptsSub || ptsSub.length < 2) return null;
    const sp = ptsSub.map((p, i) => `${i === 0 ? "M" : "L"}${px(p.x).toFixed(1)},${py(p.y).toFixed(1)}`).join(" ");
    const xClose = fromLeft ? xlo : xhi;
    return `${sp} L${px(xEdge).toFixed(1)},${height - pad} L${px(xClose).toFixed(1)},${height - pad} Z`;
  }

  const leftFill  = shadeLo !== null ? shadePath(pts.filter(p => p.x <= shadeLo), shadeLo, true)  : null;
  const rightFill = shadeHi !== null ? shadePath(pts.filter(p => p.x >= shadeHi), shadeHi, false) : null;

  return (
    <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} preserveAspectRatio="none" style={{ display: "block" }}>
      {leftFill  && <path d={leftFill}  fill={shadeColor} stroke="none" />}
      {rightFill && <path d={rightFill} fill={shadeColor} stroke="none" />}
      <line x1={pad} y1={height - pad} x2={width - pad} y2={height - pad} stroke="rgba(255,255,255,0.1)" strokeWidth="1" />
      <path d={d} fill="none" stroke={stroke} strokeWidth="1.8" vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
      {markers.map((m, i) => (
        <g key={i}>
          <line x1={px(m.x)} y1={pad + 2} x2={px(m.x)} y2={height - pad}
            stroke={m.color || stroke} strokeWidth="1" strokeDasharray="3 2" vectorEffect="non-scaling-stroke" />
          {m.label && <text x={px(m.x)} y={py(ymax * 0.82)} fill={m.color || stroke}
            fontSize="8" textAnchor="middle" fontFamily="IBM Plex Mono,monospace">{m.label}</text>}
        </g>
      ))}
      {offscaleLabel && (
        <g>
          <line x1={width - pad - 22} y1={height / 2} x2={width - pad - 4} y2={height / 2}
            stroke="var(--accent)" strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
          <polygon points={`${width - pad - 4},${height / 2 - 4} ${width - pad + 2},${height / 2} ${width - pad - 4},${height / 2 + 4}`}
            fill="var(--accent)" />
          <text x={width - pad - 24} y={height / 2 - 5} fill="var(--accent)"
            fontSize="8" textAnchor="end" fontFamily="IBM Plex Mono,monospace">{offscaleLabel}</text>
        </g>
      )}
    </svg>
  );
}

// ---- Annotated histogram (trade return distribution with CVaR shading) ------
// bins: [{x, h, neg}] · cvarX = x below which is CVaR region (dark red)
// meanX: dashed ink line · ciLo/ciHi: dashed accent lines
function AnnotHistogram({ bins, width = 300, height = 140, pad = 4,
  meanX = null, cvarX = null, ciLo = null, ciHi = null }) {
  if (!bins || bins.length === 0) return null;
  const n = bins.length;
  const hmax = Math.max(...bins.map(b => b.h)) || 1;
  const bwData = n > 1 ? bins[1].x - bins[0].x : 1;
  const xlo = bins[0].x - bwData / 2, xhi = bins[n - 1].x + bwData / 2;
  const px = x => pad + (x - xlo) / (xhi - xlo) * (width - 2 * pad);
  const bwPx = (width - 2 * pad) / n;

  return (
    <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} preserveAspectRatio="none" style={{ display: "block" }}>
      <line x1={pad} y1={height - pad} x2={width - pad} y2={height - pad} stroke="rgba(255,255,255,0.1)" strokeWidth="1" />
      {bins.map((b, i) => {
        const bh = (b.h / hmax) * (height - pad * 2 - 2);
        const isCvar = cvarX !== null && b.x < cvarX;
        const fill = b.neg
          ? (isCvar ? "var(--neg)" : "color-mix(in oklch, var(--neg), transparent 52%)")
          : "color-mix(in oklch, var(--pos), transparent 38%)";
        return <rect key={i} x={i * bwPx + pad + 0.5} y={height - pad - bh - 1}
          width={Math.max(bwPx - 1.5, 1)} height={Math.max(bh, 1)} fill={fill} />;
      })}
      {cvarX !== null && <line x1={px(cvarX)} y1={pad} x2={px(cvarX)} y2={height - pad}
        stroke="var(--neg)" strokeWidth="1.2" strokeDasharray="2.5 2" vectorEffect="non-scaling-stroke" />}
      {meanX !== null && <line x1={px(meanX)} y1={pad + 2} x2={px(meanX)} y2={height - pad}
        stroke="rgba(255,255,255,0.65)" strokeWidth="1.5" strokeDasharray="3 2" vectorEffect="non-scaling-stroke" />}
      {ciLo !== null && <line x1={px(ciLo)} y1={pad + 4} x2={px(ciLo)} y2={height - pad}
        stroke="var(--accent)" strokeWidth="1" strokeDasharray="2 2" vectorEffect="non-scaling-stroke" />}
      {ciHi !== null && <line x1={px(ciHi)} y1={pad + 4} x2={px(ciHi)} y2={height - pad}
        stroke="var(--accent)" strokeWidth="1" strokeDasharray="2 2" vectorEffect="non-scaling-stroke" />}
    </svg>
  );
}

Object.assign(window, { LineChart, Sparkline, Histogram, BarRow, CIBar, Gauge, NormalCurve, AnnotHistogram });
