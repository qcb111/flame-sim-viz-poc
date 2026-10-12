const API = '/api';

const paramOrder = [
  'airFlowSLM', 'o2FlowSLM', 'c2h4FlowSCCM', 'outerVelocityCmS',
  'innerVelocityCmS', 'o2VolumeFraction', 'o2MassFraction', 'n2MassFraction'
];
const paramLabels = {
  airFlowSLM: 'Air flow (SLM)',
  o2FlowSLM: 'O₂ flow (SLM)',
  c2h4FlowSCCM: 'C₂H₄ flow (SCCM)',
  outerVelocityCmS: 'Outer velocity (cm/s)',
  innerVelocityCmS: 'Inner velocity (cm/s)',
  o2VolumeFraction: 'O₂ volume fraction',
  o2MassFraction: 'O₂ mass fraction',
  n2MassFraction: 'N₂ mass fraction'
};
const paramShort = {
  airFlowSLM: 'air', o2FlowSLM: 'O₂', c2h4FlowSCCM: 'C₂H₄', outerVelocityCmS: 'outer v',
  innerVelocityCmS: 'inner v', o2VolumeFraction: 'O₂ vol', o2MassFraction: 'O₂ mass', n2MassFraction: 'N₂ mass'
};

let schema = null;
let combos = [];
const selected = {};   // param -> explicit user pick (number); undefined = "any"
let currentCombo = null; // the pinned unique case, or null
let route = 'contour';
let profileJ = 44;
let renderSeq = 0;

async function load() {
  const [s, p] = await Promise.all([
    fetch(`${API}/schema`).then(r => r.json()),
    fetch(`${API}/params/values`).then(r => r.json())
  ]);
  schema = s;
  combos = p.combinations;
  buildControls();
  initSlider();
  setupRouter();
  sync();
}

function buildControls() {
  const controls = document.getElementById('controls');
  controls.innerHTML = '';

  const varSel = document.createElement('select');
  varSel.id = 'variable';
  for (const v of schema.variables) {
    const o = document.createElement('option');
    o.value = v.name;
    o.textContent = v.unit ? `${v.name} (${v.unit})` : v.name;
    if (v.name === 'Temperature') o.selected = true;
    varSel.appendChild(o);
  }
  varSel.addEventListener('change', () => { if (currentCombo) renderActive(); });
  controls.appendChild(wrap('Field', varSel));

  for (const p of paramOrder) {
    const sel = document.createElement('select');
    sel.id = 'param-' + p;
    sel.addEventListener('change', () => {
      const v = sel.value;
      selected[p] = v === '' ? undefined : Number(v);
      sync();
    });
    controls.appendChild(wrap(paramLabels[p], sel));
  }

  const resetBtn = document.createElement('button');
  resetBtn.id = 'reset';
  resetBtn.textContent = 'Reset';
  resetBtn.addEventListener('click', resetAll);
  controls.appendChild(resetBtn);
}

function resetAll() {
  for (const p of paramOrder) selected[p] = undefined;
  document.getElementById('variable').value = 'Temperature';
  sync();
}

function wrap(label, el) {
  const d = document.createElement('div');
  d.className = 'control';
  const l = document.createElement('label');
  l.textContent = label;
  d.appendChild(l);
  d.appendChild(el);
  return d;
}

function initSlider() {
  const slider = document.getElementById('y-slider');
  slider.max = String(schema.ny - 1);
  slider.value = String(profileJ);
  slider.addEventListener('input', () => {
    profileJ = Number(slider.value);
    updateYLabel();
    if (currentCombo && route === 'profile') renderProfile(currentCombo, profileJ);
  });
  updateYLabel();
}

function updateYLabel() {
  document.getElementById('y-value').textContent =
    schema ? `y = ${fmt(schema.y[profileJ])} m` : 'y = — m';
}

function setupRouter() {
  const apply = () => {
    route = location.hash.startsWith('#/profile') ? 'profile' : 'contour';
    document.getElementById('view-contour').classList.toggle('active', route === 'contour');
    document.getElementById('view-profile').classList.toggle('active', route === 'profile');
    document.getElementById('tab-contour').classList.toggle('active', route === 'contour');
    document.getElementById('tab-profile').classList.toggle('active', route === 'profile');
    renderActive();
  };
  window.addEventListener('hashchange', apply);
  apply();
}

function availableValues(p) {
  const others = paramOrder.filter(o => o !== p);
  const filtered = combos.filter(c => others.every(o => selected[o] === undefined || c[o] === selected[o]));
  return [...new Set(filtered.map(c => c[p]))].sort((a, b) => a - b);
}

function matching() {
  return combos.filter(c => paramOrder.every(p => selected[p] === undefined || c[p] === selected[p]));
}

function sync() {
  const m = matching();

  for (const p of paramOrder) {
    const sel = document.getElementById('param-' + p);
    const vals = availableValues(p);
    sel.innerHTML = '';
    const none = document.createElement('option');
    none.value = '';
    none.textContent = '— (any)';
    sel.appendChild(none);
    for (const v of vals) {
      const o = document.createElement('option');
      o.value = v;
      o.textContent = v;
      sel.appendChild(o);
    }
    if (selected[p] !== undefined) sel.value = String(selected[p]);
    else if (vals.length === 1) sel.value = String(vals[0]);
    else sel.value = '';
  }

  const slider = document.getElementById('y-slider');
  if (m.length === 1) {
    currentCombo = m[0];
    slider.disabled = false;
    renderActive();
  } else {
    currentCombo = null;
    slider.disabled = true;
    if (m.length === 0) clearAll('No case matches the selected parameters.');
    else clearAll(`Select condition parameters to pin a unique case (currently ${m.length} matches).`);
  }
}

function renderActive() {
  if (!currentCombo) { clearAll('Select condition parameters to pin a unique case.'); return; }
  if (route === 'profile') renderProfile(currentCombo, profileJ);
  else renderContour(currentCombo);
}

async function renderContour(combo) {
  const seq = ++renderSeq;
  const qs = buildQuery(combo, null);
  const res = await fetch(`${API}/field?${qs}`);
  if (seq !== renderSeq) return;
  if (!res.ok) { setStatus(`Load failed (${res.status})`); return; }
  const d = await res.json();
  drawContour(d);
  setStatus(`${d.variable}${unit(d)} · min ${fmt(d.min)} · max ${fmt(d.max)} · case: ${comboText(combo)}`);
}

async function renderProfile(combo, j) {
  const seq = ++renderSeq;
  const qs = buildQuery(combo, schema.y[j]);
  const res = await fetch(`${API}/field/profile?${qs}`);
  if (seq !== renderSeq) return;
  if (!res.ok) { setStatus(`Load failed (${res.status})`); return; }
  const d = await res.json();
  drawProfile(d);
  setStatus(`${d.variable}${unit(d)} · y=${fmt(d.y)} m · min ${fmt(d.min)} · max ${fmt(d.max)} · case: ${comboText(combo)}`);
}

function buildQuery(combo, y) {
  const qs = new URLSearchParams();
  qs.set('variable', document.getElementById('variable').value);
  for (const p of paramOrder) qs.set(p, String(combo[p]));
  if (y !== null && y !== undefined) qs.set('y', String(y));
  return qs;
}

function drawContour(d) {
  const z2d = [];
  for (let j = 0; j < d.ny; j++) z2d.push(d.z.slice(j * d.nx, (j + 1) * d.nx));
  const trace = {
    type: 'contour',
    x: d.x, y: d.y, z: z2d,
    colorscale: 'Turbo',
    zmin: d.min, zmax: d.max,
    colorbar: { title: { text: d.variable + unit(d) } },
    contours: { coloring: 'heatmap' }
  };
  const layout = {
    title: d.variable,
    xaxis: { title: 'x (m)' },
    yaxis: { title: 'y (m)' },
    margin: { l: 70, r: 80, t: 40, b: 50 }
  };
  Plotly.react('plot', [trace], layout, { responsive: true, displaylogo: false });
}

function drawProfile(d) {
  const trace = {
    type: 'scatter',
    mode: 'lines',
    x: d.x, y: d.v,
    line: { width: 2 }
  };
  const layout = {
    title: `${d.variable} vs. x  (y = ${fmt(d.y)} m)`,
    xaxis: { title: 'x (m)' },
    yaxis: { title: d.variable + unit(d), range: [d.min, d.max] },
    margin: { l: 70, r: 40, t: 40, b: 50 }
  };
  Plotly.react('profile-plot', [trace], layout, { responsive: true, displaylogo: false });
}

function clearAll(msg) {
  renderSeq++;
  for (const id of ['plot', 'profile-plot']) {
    const el = document.getElementById(id);
    try { Plotly.purge(el); } catch (e) { /* no plot to purge */ }
    el.innerHTML = `<div class="placeholder">${msg}</div>`;
  }
  setStatus(msg);
}

function comboText(combo) {
  if (!combo) return '';
  return paramOrder.map(p => `${paramShort[p]}=${fmt(combo[p])}`).join(', ');
}

function unit(d) {
  return d.unit ? ' (' + d.unit + ')' : '';
}

function fmt(x) {
  if (x === 0) return '0';
  const a = Math.abs(x);
  if (a >= 1000 || a < 0.001) return x.toExponential(2);
  return x.toPrecision(4);
}

function setStatus(msg) {
  document.getElementById('status').textContent = msg;
}

load();
