// Kozmo Demo UI — vanilla JS, pure API client.
// All numbers, fingerprints, bands, and stances are rendered exactly as served.
// No scoring, no fusion, no fingerprint computation in this file.

'use strict';

// ── API ───────────────────────────────────────────────────────────────────────

const API = {
  vendors:        ()     => fetch('/vendors').then(assertOk),
  trail:          (id)   => fetch(`/vendors/${id}/trail`).then(assertOk),
  trajectory:     (id)   => fetch(`/vendors/${id}/trajectory`).then(assertOk),
  reset:          ()     => fetch('/demo/reset', { method: 'POST' }).then(assertOk),
  replay:         ()     => fetch('/demo/replay', { method: 'POST' }),
  liveSignal:     (body) => fetch('/demo/live-signal', {
    method:  'POST',
    headers: { 'Content-Type': 'application/json' },
    body:    JSON.stringify({ body }),
  }),
  uploadContract: (form) => fetch('/vendors/vendor-file/upload-contract', {
    method: 'POST',
    body:   form,
  }),
  vendorFileMd: (id) => fetch(`/vendors/${id}/vendor-file/markdown`),
};

async function assertOk(res) {
  if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  return res.json();
}

// ── State ─────────────────────────────────────────────────────────────────────

const state = {
  vendors:         [],
  selectedId:      null,
  trail:           null,
  trajectory:      [],
  lastFingerprint: null,
  replayEs:        null,
};

// ── Boot ──────────────────────────────────────────────────────────────────────

async function init() {
  state.vendors = await API.vendors();
  renderVendorList();
  document.getElementById('btn-reset').addEventListener('click', onReset);
  document.getElementById('btn-replay').addEventListener('click', onReplay);
  document.getElementById('btn-live-send').addEventListener('click', onLiveSend);
  const uploadBtn = document.getElementById('btn-upload');
  if (uploadBtn) {
    uploadBtn.addEventListener('click', () => {
      const vendorName = (document.getElementById('upload-vendor-name')?.value ?? '').trim();
      onUploadContract('upload-file', 'upload-status', 'btn-upload', vendorName);
    });
  }
  initLiveSseHandler();
}

// ── Vendor list ───────────────────────────────────────────────────────────────

function renderVendorList() {
  const el = document.getElementById('vendor-list');
  el.innerHTML = state.vendors.map(v => `
    <div class="vendor-card band-${bandClass(v.band)}${v.entityId === state.selectedId ? ' selected' : ''}"
         data-id="${v.entityId}">
      <div class="vendor-name">${esc(v.name)}</div>
      <div class="vendor-meta">
        <span class="badge band-badge band-${bandClass(v.band)}">${esc(v.band)}</span>
        <span class="badge stance-badge">${esc(v.stance)}</span>
      </div>
      <div class="vendor-confidence">conf ${pct(v.confidence)}</div>
    </div>`).join('');

  el.querySelectorAll('.vendor-card').forEach(card =>
    card.addEventListener('click', () => selectVendor(card.dataset.id)));
}

async function selectVendor(id) {
  state.selectedId = id;
  renderVendorList();

  const detail = document.getElementById('detail-view');
  document.getElementById('no-selection').classList.add('hidden');
  detail.classList.remove('hidden');
  detail.innerHTML = '<div class="loading">Loading…</div>';

  const [trail, trajectory] = await Promise.all([
    API.trail(id),
    API.trajectory(id),
  ]);
  state.trail      = trail;
  state.trajectory = trajectory;
  state.lastFingerprint = trail.index.fingerprint;

  renderDetailView(false);
}

// ── Detail view ───────────────────────────────────────────────────────────────

function renderDetailView(fingerprintMatch) {
  const trail = state.trail;
  const v     = state.vendors.find(x => x.entityId === state.selectedId);
  const el    = document.getElementById('detail-view');
  const fp    = trail.index.fingerprint;
  const bc    = bandClass(trail.band.band);

  el.innerHTML = `
    <div class="detail-header band-${bc}">
      <div class="detail-header-left">
        <h2>${esc(v ? v.name : 'Vendor')}</h2>
        <div class="detail-badges">
          <span class="badge band-badge band-${bc}">${esc(trail.band.band)}</span>
          <span class="badge stance-badge">${esc(trail.posture.stance)}</span>
        </div>
      </div>
      <div class="fingerprint-box${fingerprintMatch ? ' fingerprint-match' : ''}">
        ${fingerprintMatch ? '<div class="fingerprint-match-banner">Same answer</div>' : ''}
        <div class="fingerprint-label">Fingerprint</div>
        <div class="fingerprint-value">${esc(fp.substring(0, 16))}&hellip;</div>
        <div class="fingerprint-full">${esc(fp)}</div>
      </div>
    </div>
    <div class="detail-upload">
      <span class="detail-upload-label">Upload Document</span>
      <div class="detail-upload-row">
        <input type="file" id="duf-file" accept=".pdf" class="detail-upload-file">
        <button id="duf-btn" type="button" class="btn-upload">Process →</button>
      </div>
      <span id="duf-status" class="detail-upload-status"></span>
    </div>
    ${renderMetaCaveats(trail.posture)}
    <div class="tabs">
      <button class="tab-btn active" data-tab="overview"    type="button">Overview</button>
      <button class="tab-btn"        data-tab="file"        type="button">File</button>
      <button class="tab-btn"        data-tab="reality"     type="button">Reality</button>
      <button class="tab-btn"        data-tab="trajectory"  type="button">Trajectory</button>
    </div>
    <div id="tab-overview"    class="tab-panel">
      <iframe id="vf-frame" src="about:blank" style="width:100%;border:none;display:block;min-height:1200px;"></iframe>
    </div>
    <div id="tab-file"        class="tab-panel hidden">
      <pre id="vf-md-pre" class="vf-md-pre">Loading…</pre>
    </div>
    <div id="tab-reality"     class="tab-panel hidden">${renderTrailPanel(trail)}</div>
    <div id="tab-trajectory"  class="tab-panel hidden">${renderTrajectoryPanel(state.trajectory, trail.band.thresholds)}</div>`;

  el.querySelectorAll('.tab-btn').forEach(btn =>
    btn.addEventListener('click', () => switchTab(btn.dataset.tab)));

  el.querySelectorAll('.dim-header').forEach(header =>
    header.addEventListener('click', () => header.parentElement.classList.toggle('expanded')));

  const dufBtn = document.getElementById('duf-btn');
  if (dufBtn) {
    const vendorName = (v && v.name) ? v.name : '';
    dufBtn.addEventListener('click', () =>
      onUploadContract('duf-file', 'duf-status', 'duf-btn', vendorName));
  }

  loadOverviewTab();
}

function switchTab(tab) {
  document.querySelectorAll('.tab-btn').forEach(b =>
    b.classList.toggle('active', b.dataset.tab === tab));
  document.querySelectorAll('.tab-panel').forEach(p => {
    const match = p.id === `tab-${tab}`;
    p.classList.toggle('hidden', !match);
  });
  if (tab === 'overview') loadOverviewTab();
  if (tab === 'file')     loadFileTab();
}

// ── Overview / File tab loaders ───────────────────────────────────────────────

function loadOverviewTab() {
  const frame = document.getElementById('vf-frame');
  if (!frame || frame.dataset.loaded === '1') return;
  frame.src = `/vendor-file/${state.selectedId}`;
  frame.dataset.loaded = '1';
  frame.addEventListener('load', () => {
    try {
      const h = Math.max(
        frame.contentDocument.body.scrollHeight,
        frame.contentDocument.documentElement.scrollHeight);
      if (h > 200) frame.style.height = h + 32 + 'px';
    } catch {}
  }, { once: true });
}

async function loadFileTab() {
  const pre = document.getElementById('vf-md-pre');
  if (!pre || pre.dataset.loaded === '1') return;
  pre.textContent = 'Loading…';
  try {
    const res = await API.vendorFileMd(state.selectedId);
    if (!res.ok) { pre.textContent = 'No vendor file available yet.'; return; }
    pre.textContent = await res.text();
    pre.dataset.loaded = '1';
  } catch (err) {
    pre.textContent = 'Error: ' + err.message;
  }
}

// ── Trail panel ───────────────────────────────────────────────────────────────

function renderMetaCaveats(posture) {
  const parts = [];
  if (posture.cautions && posture.cautions.length)
    parts.push(`<div class="caveats cautions">
      <strong>Cautions (${posture.cautions.length})</strong>
      <ul>${posture.cautions.map(c => `<li>${esc(c)}</li>`).join('')}</ul>
    </div>`);
  if (posture.evidenceGaps && posture.evidenceGaps.length)
    parts.push(`<div class="caveats gaps">
      <strong>Evidence Gaps (${posture.evidenceGaps.length})</strong>
      <ul>${posture.evidenceGaps.map(g => `<li>${esc(g)}</li>`).join('')}</ul>
    </div>`);
  return parts.join('');
}

function renderTrailPanel(trail) {
  const t = trail.band.thresholds;
  return `
    <div class="trail-section">
      <h3>Posture</h3>
      <div class="info-grid">
        ${row('Stance',           `<span class="badge stance-badge">${esc(trail.posture.stance)}</span>`)}
        ${row('Confidence',       pct(trail.posture.confidence))}
        ${row('Rationale',        `<span class="value prose">${esc(trail.posture.rationale)}</span>`)}
        ${trail.posture.renewal ? rowRenewal(trail.posture.renewal) : ''}
      </div>
    </div>
    <div class="trail-section">
      <h3>Band</h3>
      <div class="info-grid">
        ${row('Band',       `<span class="badge band-badge band-${bandClass(trail.band.band)}">${esc(trail.band.band)}</span>`)}
        ${row('Driven By',  trail.band.drivenBy)}
        ${row('Thresholds', `Critical &lt; ${t.atRisk.toFixed(2)} | AtRisk &lt; ${t.healthy.toFixed(2)} | Healthy \u2265 ${t.healthy.toFixed(2)}`)}
      </div>
    </div>
    <div class="trail-section">
      <h3>Index</h3>
      <div class="info-grid">
        ${row('Composite',        trail.index.composite.toFixed(4))}
        ${row('Confidence Floor', trail.index.confidenceFloor.toFixed(4))}
        ${row('Band Driven By',   trail.index.bandDrivenBy)}
        ${trail.index.worstDimension
          ? row('Worst Dimension', `${esc(trail.index.worstDimension.dimension)} (${trail.index.worstDimension.score.toFixed(3)})`)
          : ''}
        ${row('Config Version',   trail.index.configVersion)}
      </div>
    </div>
    <div class="trail-section">
      <h3>Dimensions &amp; Beliefs</h3>
      <div class="dimensions">${trail.dimensions.map(renderDimension).join('')}</div>
    </div>`;
}

function row(label, valueHtml) {
  return `<div class="info-row">
    <span>${esc(label)}</span>
    <span class="value">${valueHtml}</span>
  </div>`;
}

function rowRenewal(renewal) {
  const days = renewal.daysToRenewal;
  const text = days >= 0 ? `In ${days} days` : `${Math.abs(days)} days overdue`;
  const cls  = renewal.windowActive ? ' renewal-active' : '';
  return row('Renewal', `<span class="${cls}">${text}${renewal.windowActive ? ' — window active' : ''}</span>`);
}

function renderDimension(dim) {
  return `
    <div class="dimension${dim.beliefs.length === 0 ? ' no-beliefs' : ''}">
      <div class="dim-header">
        <span class="dim-name">${esc(dim.dimension)}</span>
        <span class="dim-score">score ${dim.score.toFixed(3)}</span>
        <span class="dim-conf">conf ${pct(dim.confidence)}</span>
        <span class="dim-weight">w=${dim.weight.toFixed(2)}</span>
        <span class="dim-expand">&#9654;</span>
      </div>
      <div class="dim-beliefs">
        ${dim.beliefs.length === 0
          ? '<div class="no-evidence">No current evidence</div>'
          : dim.beliefs.map(renderBelief).join('')}
      </div>
    </div>`;
}

function renderBelief(b) {
  const tierCls = `tier-${b.sourceTier ? b.sourceTier.toLowerCase() : 'unknown'}`;

  const anchorHtml = b.anchorRawConfidence != null ? `
    <div class="anchor-info">
      <span class="anchor-label">Anchored</span>
      <span>raw ${pct(b.anchorRawConfidence)} \u2192 effective ${pct(b.confidence)}</span>
      ${b.anchorPredecessorTier
        ? `<span class="anchor-pred">floor from ${esc(b.anchorPredecessorTier)} predecessor</span>`
        : ''}
    </div>` : '';

  const signalHtml = b.signal ? `
    <div class="signal-ref">
      <span class="signal-label">Signal</span>
      <span class="signal-type">${esc(b.signal.type)}</span>
      <span class="signal-source">${esc(b.signal.source)}</span>
      ${b.signal.summary ? `<span class="signal-summary">${esc(b.signal.summary)}</span>` : ''}
      <span class="signal-ts">${fmtTs(b.signal.timestamp)}</span>
    </div>` : '';

  return `
    <div class="belief">
      <div class="belief-header">
        <span class="belief-criterion">${esc(b.criterion)}</span>
        <span class="belief-value">value ${b.value.toFixed(3)}</span>
        <span class="belief-conf">conf ${pct(b.confidence)}</span>
        <span class="belief-fresh">fresh ${b.freshness.toFixed(3)}</span>
        <span class="${tierCls}">${esc(b.sourceTier)}</span>
      </div>
      ${b.reasoningSummary ? `<div class="reasoning">${esc(b.reasoningSummary)}</div>` : ''}
      ${anchorHtml}
      ${signalHtml}
    </div>`;
}

// ── Trajectory chart (hand-rolled SVG) ───────────────────────────────────────

function renderTrajectoryPanel(points, thresholds) {
  if (!points || points.length === 0)
    return '<div class="no-data">No trajectory data available</div>';
  return `
    <div class="chart-container">
      <h4>Composite Score Trajectory</h4>
      ${buildSvgChart(points, thresholds)}
    </div>
    <div class="chart-container" style="margin-top:24px;">
      <h4>Signal-by-Signal Journey &nbsp;<span style="font-size:11px;color:#64748b;font-weight:400;">each step = one signal processed</span></h4>
      ${buildJourneyChart(points, thresholds)}
    </div>`;
}

function buildSvgChart(points, thresholds) {
  const W = 600, H = 260;
  const PAD = { top: 20, right: 24, bottom: 50, left: 50 };
  const iW = W - PAD.left - PAD.right;
  const iH = H - PAD.top  - PAD.bottom;

  const healthy = (thresholds && thresholds.healthy != null) ? thresholds.healthy : 0.70;
  const atRisk  = (thresholds && thresholds.atRisk  != null) ? thresholds.atRisk  : 0.40;

  const times = points.map(p => +new Date(p.timestamp));
  const tMin = Math.min(...times), tMax = Math.max(...times);
  const tRange = tMax > tMin ? tMax - tMin : 1;

  const xOf = ts => PAD.left + ((+new Date(ts) - tMin) / tRange) * iW;
  const yOf = v  => PAD.top  + (1 - v) * iH;

  const yH  = yOf(healthy);
  const yAR = yOf(atRisk);
  const yT  = PAD.top;
  const yB  = PAD.top + iH;
  const xL  = PAD.left;
  const xR  = PAD.left + iW;

  // Band colour regions
  const regions = `
    <rect x="${xL}" y="${yT}"  width="${iW}" height="${yH  - yT}"  fill="#dcfce7" opacity="0.55"/>
    <rect x="${xL}" y="${yH}"  width="${iW}" height="${yAR - yH}"  fill="#fef9c3" opacity="0.55"/>
    <rect x="${xL}" y="${yAR}" width="${iW}" height="${yB  - yAR}" fill="#fee2e2" opacity="0.55"/>`;

  // Threshold dashed lines
  const thresh = `
    <line x1="${xL}" y1="${yH}"  x2="${xR}" y2="${yH}"  stroke="#86efac" stroke-dasharray="5,3" stroke-width="1"/>
    <line x1="${xL}" y1="${yAR}" x2="${xR}" y2="${yAR}" stroke="#fca5a5" stroke-dasharray="5,3" stroke-width="1"/>`;

  // Axes
  const axes = `
    <line x1="${xL}" y1="${yT}" x2="${xL}" y2="${yB}" stroke="#94a3b8" stroke-width="1.5"/>
    <line x1="${xL}" y1="${yB}" x2="${xR}" y2="${yB}" stroke="#94a3b8" stroke-width="1.5"/>`;

  // Y axis labels
  const yLabels = [0.0, 0.2, 0.4, 0.6, 0.8, 1.0].map(v =>
    `<text x="${xL - 6}" y="${yOf(v) + 4}" text-anchor="end" class="cl">${v.toFixed(1)}</text>` +
    `<line x1="${xL - 3}" y1="${yOf(v)}" x2="${xL}" y2="${yOf(v)}" stroke="#94a3b8" stroke-width="1"/>`
  ).join('');

  // X axis labels (sample every few points, always include last)
  const step = Math.max(1, Math.floor(points.length / 6));
  const xSample = points.filter((_, i) => i % step === 0 || i === points.length - 1);
  const xLabels = xSample.map(p =>
    `<text x="${xOf(p.timestamp).toFixed(1)}" y="${yB + 16}" text-anchor="middle" class="cl">${fmtDate(p.timestamp)}</text>`
  ).join('');

  // Line path
  const pathD = points.map((p, i) =>
    `${i === 0 ? 'M' : 'L'}${xOf(p.timestamp).toFixed(1)},${yOf(p.composite).toFixed(1)}`
  ).join(' ');

  // Data points coloured by band
  const dots = points.map(p => {
    const fill = bandFill(p.band);
    const cx   = xOf(p.timestamp).toFixed(1);
    const cy   = yOf(p.composite).toFixed(1);
    return `<circle cx="${cx}" cy="${cy}" r="4" fill="${fill}" stroke="white" stroke-width="1.5">
      <title>${esc(p.band)} | ${p.composite.toFixed(3)} | ${esc(p.stance)} | ${fmtTs(p.timestamp)}</title>
    </circle>`;
  }).join('');

  // Band labels on right edge
  const bandLabels = `
    <text x="${xR + 4}" y="${yOf((1.0 + healthy) / 2) + 4}"  class="bl">Healthy</text>
    <text x="${xR + 4}" y="${yOf((healthy + atRisk) / 2) + 4}" class="bl">At-Risk</text>
    <text x="${xR + 4}" y="${yOf(atRisk / 2) + 4}"            class="bl">Critical</text>`;

  return `<svg viewBox="0 0 ${W} ${H}" class="trajectory-chart">
    <style>
      .cl { font-size: 9px; fill: #64748b; font-family: monospace; }
      .bl { font-size: 9px; fill: #94a3b8; font-family: monospace; }
    </style>
    ${regions}${thresh}${axes}${yLabels}${xLabels}
    <path d="${pathD}" fill="none" stroke="#3b82f6" stroke-width="2" stroke-linejoin="round"/>
    ${dots}
    ${bandLabels}
  </svg>`;
}

// ── Journey chart — equally spaced steps, no date axis ───────────────────────

function buildJourneyChart(points, thresholds) {
  const W = 600, H = 280;
  const PAD = { top: 20, right: 80, bottom: 64, left: 50 };
  const iW = W - PAD.left - PAD.right;
  const iH = H - PAD.top  - PAD.bottom;

  const healthy = (thresholds && thresholds.healthy != null) ? thresholds.healthy : 0.70;
  const atRisk  = (thresholds && thresholds.atRisk  != null) ? thresholds.atRisk  : 0.40;

  const n    = points.length;
  const step = n > 1 ? iW / (n - 1) : iW;

  const xOf = i => PAD.left + i * (n > 1 ? iW / (n - 1) : 0);
  const yOf = v => PAD.top  + (1 - v) * iH;

  const yH  = yOf(healthy);
  const yAR = yOf(atRisk);
  const yT  = PAD.top;
  const yB  = PAD.top + iH;
  const xL  = PAD.left;
  const xR  = PAD.left + iW;

  // Band colour regions
  const regions = `
    <rect x="${xL}" y="${yT}"  width="${iW}" height="${yH  - yT}"  fill="#dcfce7" opacity="0.45"/>
    <rect x="${xL}" y="${yH}"  width="${iW}" height="${yAR - yH}"  fill="#fef9c3" opacity="0.45"/>
    <rect x="${xL}" y="${yAR}" width="${iW}" height="${yB  - yAR}" fill="#fee2e2" opacity="0.45"/>`;

  // Threshold dashed lines
  const thresh = `
    <line x1="${xL}" y1="${yH}"  x2="${xR}" y2="${yH}"  stroke="#86efac" stroke-dasharray="5,3" stroke-width="1"/>
    <line x1="${xL}" y1="${yAR}" x2="${xR}" y2="${yAR}" stroke="#fca5a5" stroke-dasharray="5,3" stroke-width="1"/>`;

  // Axes
  const axes = `
    <line x1="${xL}" y1="${yT}" x2="${xL}" y2="${yB}" stroke="#94a3b8" stroke-width="1.5"/>
    <line x1="${xL}" y1="${yB}" x2="${xR}" y2="${yB}" stroke="#94a3b8" stroke-width="1.5"/>`;

  // Y axis labels
  const yLabels = [0.0, 0.2, 0.4, 0.6, 0.8, 1.0].map(v =>
    `<text x="${xL - 6}" y="${yOf(v) + 4}" text-anchor="end" class="cl">${v.toFixed(1)}</text>` +
    `<line x1="${xL - 3}" y1="${yOf(v)}" x2="${xL}" y2="${yOf(v)}" stroke="#94a3b8" stroke-width="1"/>`
  ).join('');

  // Line path — segments coloured by band transition
  const segments = points.slice(0, -1).map((p, i) => {
    const x1 = xOf(i).toFixed(1),   y1 = yOf(p.composite).toFixed(1);
    const x2 = xOf(i+1).toFixed(1), y2 = yOf(points[i+1].composite).toFixed(1);
    const col = bandFill(points[i+1].band);
    return `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="${col}" stroke-width="2.5" stroke-linejoin="round"/>`;
  }).join('');

  // Up/down delta arrows between consecutive points
  const arrows = points.slice(1).map((p, i) => {
    const prev  = points[i].composite;
    const curr  = p.composite;
    const delta = curr - prev;
    if (Math.abs(delta) < 0.005) return '';
    const mx  = ((xOf(i) + xOf(i+1)) / 2).toFixed(1);
    const my  = ((yOf(prev) + yOf(curr)) / 2 - 10).toFixed(1);
    const sign = delta > 0 ? '▲' : '▼';
    const col  = delta > 0 ? '#22c55e' : '#ef4444';
    return `<text x="${mx}" y="${my}" text-anchor="middle" style="font-size:8px;fill:${col};font-weight:700;">${sign}${Math.abs(delta).toFixed(2)}</text>`;
  }).join('');

  // Dots with stance label below x-axis
  const dots = points.map((p, i) => {
    const cx   = xOf(i).toFixed(1);
    const cy   = yOf(p.composite).toFixed(1);
    const fill = bandFill(p.band);
    const stanceShort = stanceAbbr(p.stance);
    const stanceCol   = stanceColor(p.stance);
    // Step label below axis
    const labelY = yB + 14;
    const stanceY = yB + 26;
    const stepLbl = `<text x="${cx}" y="${labelY}" text-anchor="middle" class="cl">S${i+1}</text>`;
    const stanceLbl = `<text x="${cx}" y="${stanceY}" text-anchor="middle" style="font-size:8px;fill:${stanceCol};font-weight:700;">${esc(stanceShort)}</text>`;
    // Value label above dot (only if not crowded)
    const valLbl = n <= 12 ? `<text x="${cx}" y="${(+cy - 8).toFixed(1)}" text-anchor="middle" class="cl">${p.composite.toFixed(2)}</text>` : '';
    return `${stepLbl}${stanceLbl}${valLbl}
    <circle cx="${cx}" cy="${cy}" r="5" fill="${fill}" stroke="white" stroke-width="2">
      <title>Step ${i+1} | ${esc(p.band)} | ${p.composite.toFixed(3)} | ${esc(p.stance)}</title>
    </circle>`;
  }).join('');

  // Band labels on right
  const bandLabels = `
    <text x="${xR + 4}" y="${yOf((1.0 + healthy) / 2) + 4}"  class="bl">Healthy</text>
    <text x="${xR + 4}" y="${yOf((healthy + atRisk) / 2) + 4}" class="bl">At-Risk</text>
    <text x="${xR + 4}" y="${yOf(atRisk / 2) + 4}"            class="bl">Critical</text>`;

  // X axis title
  const xTitle = `<text x="${xL + iW/2}" y="${yB + 50}" text-anchor="middle" class="cl" style="fill:#94a3b8;">← each point is one signal processed →</text>`;

  return `<svg viewBox="0 0 ${W} ${H}" class="trajectory-chart">
    <style>
      .cl { font-size: 9px; fill: #64748b; font-family: monospace; }
      .bl { font-size: 9px; fill: #94a3b8; font-family: monospace; }
    </style>
    ${regions}${thresh}${axes}${yLabels}
    ${segments}${arrows}${dots}
    ${bandLabels}${xTitle}
  </svg>`;
}

function stanceAbbr(stance) {
  if (!stance) return '—';
  const s = stance.toLowerCase();
  if (s === 'maintain')    return 'Maintain';
  if (s === 'monitor')     return 'Monitor';
  if (s === 'renegotiate') return 'Reneg.';
  if (s === 'escalate')    return 'Escalate';
  if (s === 'remediate')   return 'Remediate';
  return stance;
}

function stanceColor(stance) {
  if (!stance) return '#94a3b8';
  const s = stance.toLowerCase();
  if (s === 'maintain')    return '#22c55e';
  if (s === 'monitor')     return '#3b82f6';
  if (s === 'renegotiate') return '#f59e0b';
  if (s === 'escalate')    return '#ef4444';
  if (s === 'remediate')   return '#f97316';
  return '#94a3b8';
}

function bandFill(band) {
  if (!band) return '#64748b';
  const b = band.toLowerCase();
  if (b === 'healthy')  return '#16a34a';
  if (b === 'atrisk')   return '#ca8a04';
  if (b === 'critical') return '#dc2626';
  return '#64748b';
}

// ── Reset ─────────────────────────────────────────────────────────────────────

async function onReset() {
  const btn = document.getElementById('btn-reset');
  btn.disabled = true;
  btn.textContent = 'Resetting…';
  try {
    const result   = await API.reset();
    state.vendors  = result.vendors;
    renderVendorList();

    if (state.selectedId) {
      const prevFp = state.lastFingerprint;
      const [trail, trajectory] = await Promise.all([
        API.trail(state.selectedId),
        API.trajectory(state.selectedId),
      ]);
      state.trail           = trail;
      state.trajectory      = trajectory;
      state.lastFingerprint = trail.index.fingerprint;
      renderDetailView(prevFp !== null && prevFp === trail.index.fingerprint);
    }
  } catch (err) {
    alert(`Reset failed: ${err.message}`);
  } finally {
    btn.disabled    = false;
    btn.textContent = 'Reset';
  }
}

// ── Replay ────────────────────────────────────────────────────────────────────

async function onReplay() {
  if (state.replayEs) {
    state.replayEs.close();
    state.replayEs = null;
  }

  const btn      = document.getElementById('btn-replay');
  const statusEl = document.getElementById('replay-status');
  btn.disabled    = true;
  btn.textContent = 'Replaying…';
  statusEl.classList.remove('hidden');
  statusEl.textContent = 'Connecting…';

  const es = new EventSource('/events');
  state.replayEs = es;

  es.onmessage = async (evt) => {
    let msg;
    try { msg = JSON.parse(evt.data); } catch { return; }

    if (msg.type === 'replay-step') {
      const d = msg.data;
      statusEl.textContent = `${d.entityId.substring(0, 8)}… | ${d.index.band} | ${d.stance} | composite ${d.index.composite.toFixed(3)}`;
      // Refresh vendor list entry for this entity
      const vendor = state.vendors.find(v => v.entityId === d.entityId);
      if (vendor) {
        vendor.band        = d.index.band;
        vendor.stance      = d.stance;
        vendor.fingerprint = d.fingerprint;
        renderVendorList();
      }
    }

    if (msg.type === 'replay-complete') {
      state.vendors = msg.data.vendors;
      renderVendorList();
      statusEl.textContent = 'Replay complete.';
      btn.disabled    = false;
      btn.textContent = 'Replay';
      es.close();
      state.replayEs = null;

      if (state.selectedId) {
        const [trail, trajectory] = await Promise.all([
          API.trail(state.selectedId),
          API.trajectory(state.selectedId),
        ]);
        state.trail           = trail;
        state.trajectory      = trajectory;
        state.lastFingerprint = trail.index.fingerprint;
        renderDetailView(false);
      }
    }
  };

  es.onerror = () => {
    statusEl.textContent = 'SSE connection error.';
    btn.disabled    = false;
    btn.textContent = 'Replay';
    es.close();
    state.replayEs = null;
  };

  await API.replay();
}

// ── Helpers ───────────────────────────────────────────────────────────────────

function bandClass(band) {
  if (!band) return 'unknown';
  return band.toLowerCase().replace(/[^a-z]/g, '');
}

function pct(v) {
  return (v * 100).toFixed(1) + '%';
}

function esc(str) {
  if (str == null) return '';
  return String(str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

function fmtTs(ts) {
  try { return new Date(ts).toLocaleString(); } catch { return String(ts); }
}

function fmtDate(ts) {
  try {
    return new Date(ts).toLocaleDateString('en-GB', { month: 'short', day: 'numeric' });
  } catch { return ''; }
}

// ── Live signal injection ─────────────────────────────────────────────────────

function initLiveSseHandler() {
  // Attach to the existing SSE stream once; the onReplay handler creates its own EventSource.
  // We piggyback on the shared /events endpoint for live-classifying / live-update events.
  // A persistent background EventSource handles live events outside of replay.
  const es = new EventSource('/events');
  es.onmessage = (evt) => {
    let msg;
    try { msg = JSON.parse(evt.data); } catch { return; }
    if (msg.type === 'live-classifying') onLiveClassifying(msg.data);
    if (msg.type === 'live-update')      onLiveUpdateSse(msg.data);
    if (msg.type === 'live-error')       onLiveErrorSse(msg.data);
  };
}

async function onLiveSend() {
  const btn      = document.getElementById('btn-live-send');
  const statusEl = document.getElementById('live-status');
  const resultEl = document.getElementById('live-result');
  const body     = document.getElementById('live-body').value.trim();

  if (!body) {
    setLiveStatus('Enter a note before sending.', 'live-error');
    return;
  }

  btn.disabled = true;
  resultEl.classList.add('hidden');
  setLiveStatus('Submitting…', '');

  try {
    const res = await API.liveSignal(body);

    if (res.status === 503) {
      setLiveStatus('OPENAI_API_KEY not configured — live classification unavailable.', 'live-error');
      return;
    }
    if (res.status === 422) {
      const err = await res.json().catch(() => ({ error: res.statusText }));
      setLiveStatus(err.error ?? 'Vendor not identified.', 'live-error');
      return;
    }
    if (!res.ok) {
      const err = await res.json().catch(() => ({ error: res.statusText }));
      setLiveStatus(`Error ${res.status}: ${err.error ?? res.statusText}`, 'live-error');
      return;
    }

    const data       = await res.json();
    const updatedId  = data.vendor.entityId;
    renderLiveResult(data, resultEl);
    setLiveStatus('Classification complete.', 'live-success');

    // Refresh vendor list and detail view if the detected vendor is currently selected
    state.vendors = await API.vendors();
    renderVendorList();

    if (state.selectedId === updatedId) {
      const [trail, trajectory] = await Promise.all([
        API.trail(updatedId),
        API.trajectory(updatedId),
      ]);
      state.trail           = trail;
      state.trajectory      = trajectory;
      state.lastFingerprint = trail.index.fingerprint;
      renderDetailView(false);
    }
  } catch (err) {
    setLiveStatus(`Failed: ${err.message}`, 'live-error');
  } finally {
    btn.disabled = false;
  }
}

function onLiveClassifying(data) {
  setLiveStatus(`Classifying with the model…  (${esc(data.vendorName)})`, 'live-classifying');
}

function onLiveUpdateSse(data) {
  // SSE live-update: update vendor card in the list without waiting for the HTTP response
  const v = state.vendors.find(x => x.entityId === data.vendorId);
  if (v && data.vendor) {
    v.band   = data.vendor.band;
    v.stance = data.vendor.stance;
    renderVendorList();
  }
}

function onLiveErrorSse(data) {
  if (data.error) setLiveStatus(`Error: ${esc(data.error)}`, 'live-error');
}

function renderLiveResult(data, el) {
  const cl = data.classification;
  el.innerHTML = `
    <div class="live-result-row">
      <span class="live-result-label">Vendor</span>
      <span class="live-result-value">${esc(data.vendor.name)}</span>
    </div>
    <div class="live-result-row">
      <span class="live-result-label">Dimension</span>
      <span class="live-result-value">${esc(cl.dimension)}</span>
    </div>
    <div class="live-result-row">
      <span class="live-result-label">Criterion</span>
      <span class="live-result-value">${esc(cl.criterion)}</span>
    </div>
    <div class="live-result-row">
      <span class="live-result-label">Value</span>
      <span class="live-result-value">${cl.value.toFixed(3)}</span>
    </div>
    <div class="live-result-row">
      <span class="live-result-label">Confidence</span>
      <span class="live-result-value">${pct(cl.methodConfidence)}</span>
    </div>
    <div class="live-result-row">
      <span class="live-result-label">Tier</span>
      <span class="live-result-value">${esc(cl.sourceTier)}</span>
    </div>
    <div class="live-result-row">
      <span class="live-result-label">Band → </span>
      <span class="live-result-value">${esc(data.index.band)}</span>
    </div>
    ${cl.reasoningSummary ? `<div class="live-reasoning">"${esc(cl.reasoningSummary)}"</div>` : ''}`;
  el.classList.remove('hidden');
}

function setLiveStatus(msg, cls) {
  const el  = document.getElementById('live-status');
  el.textContent = msg;
  el.className   = 'live-status' + (cls ? ' ' + cls : '');
}

// ── Upload contract ───────────────────────────────────────────────────────────

async function onUploadContract(fileInputId, statusId, btnId, vendorName) {
  const fileInput = document.getElementById(fileInputId);
  const statusEl  = document.getElementById(statusId);
  const btn       = document.getElementById(btnId);
  const file      = fileInput && fileInput.files && fileInput.files[0];

  if (!file) {
    setUploadStatus(statusEl, 'Select a PDF first.', 'upload-err');
    return;
  }
  if (!vendorName) {
    setUploadStatus(statusEl, 'Enter a vendor name.', 'upload-err');
    return;
  }

  if (btn) btn.disabled = true;
  setUploadStatus(statusEl, 'Extracting… (may take a moment)', 'upload-busy');

  const form = new FormData();
  form.append('file', file);
  form.append('vendorName', vendorName);

  try {
    const res = await API.uploadContract(form);
    if (res.status === 503) {
      setUploadStatus(statusEl, 'OPENAI_API_KEY not configured — live extraction unavailable.', 'upload-err');
      return;
    }
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      setUploadStatus(statusEl, `Error ${res.status}: ${err.error ?? res.statusText}`, 'upload-err');
      return;
    }
    const data = await res.json();
    setUploadStatus(statusEl, 'Done — opening Overview…', 'upload-ok');
    try { state.vendors = await API.vendors(); } catch {}
    renderVendorList();
    await selectVendor(data.vendorId);
    switchTab('overview');
  } catch (err) {
    setUploadStatus(statusEl, `Failed: ${err.message}`, 'upload-err');
  } finally {
    if (btn) btn.disabled = false;
  }
}

function setUploadStatus(el, msg, cls) {
  if (!el) return;
  el.textContent = msg;
  el.className   = (el.classList.contains('detail-upload-status') ? 'detail-upload-status' : 'upload-status') +
                   (cls ? ' ' + cls : '');
}

// ── Init ──────────────────────────────────────────────────────────────────────

init().catch(err => {
  const app = document.getElementById('app');
  if (app) app.innerHTML = `<div class="error">Failed to load: ${esc(String(err))}</div>`;
  console.error(err);
});
