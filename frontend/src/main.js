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
const selected = {}; // param -> explicit user pick (number); undefined = "any"
let renderSeq = 0;

async function load() {
  const [s, p] = await Promise.all([
    fetch(`${API}/schema`).then(r => r.json()),
    fetch(`${API}/params/values`).then(r => r.json())
  ]);
  schema = s;
  combos = p.combinations;
  buildControls();
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
  varSel.addEventListener('change', () => sync());
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

function availableValues(p) {
  const others = paramOrder.filter(o => o !== p);
  const filtered = combos.filter(c => others.every(o => selected[o] === undefined || c[o] === selected[o]));
  return [...new Set(filtered.map(c => c[p]))].sort((a, b) => a - b);
}

function matching() {
  return combos.filter(c => paramOrder.every(p => selected[p] === undefined || c[p] === selected[p]));
}

// Rebuild every dropdown, auto-fill any dropdown whose value is uniquely determined
// by the current selections, and render the plot only when exactly one case matches.
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
    if (selected[p] !== undefined) sel.value = String(selected[p]);  // explicit pick
    else if (vals.length === 1) sel.value = String(vals[0]);         // uniquely determined → auto-fill (display only)
    else sel.value = '';                                             // any
  }

  if (m.length === 1) {
    render(m[0]);
  } else if (m.length === 0) {
    clearPlot('No case matches the selected parameters.');
  } else {
    clearPlot(`Select condition parameters to pin a unique case (currently ${m.length} matches).`);
  }
}

async function render(combo) {
  const seq = ++renderSeq;
  const variable = document.getElementById('variable').value;
  const qs = new URLSearchParams();
  qs.set('variable', variable);
  for (const p of paramOrder) qs.set(p, String(combo[p]));
  const res = await fetch(`${API}/field?${qs}`);
  if (seq !== renderSeq) return; // a newer render or clear superseded this one
  if (!res.ok) { setStatus(`Load failed (${res.status})`); return; }
  const d = await res.json();
  drawContour(d);
  setStatus(`${d.variable}${d.unit ? ' (' + d.unit + ')' : ''} · min ${fmt(d.min)} · max ${fmt(d.max)} · case: ${comboText(combo)}`);
}

function drawContour(d) {
  const z2d = [];
  for (let j = 0; j < d.ny; j++) z2d.push(d.z.slice(j * d.nx, (j + 1) * d.nx));
  const trace = {
    type: 'contour',
    x: d.x, y: d.y, z: z2d,
    colorscale: 'Turbo',
    zmin: d.min, zmax: d.max,
    colorbar: { title: { text: d.variable + (d.unit ? ' (' + d.unit + ')' : '') } },
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

function clearPlot(msg) {
  renderSeq++; // invalidate any in-flight render
  const el = document.getElementById('plot');
  try { Plotly.purge(el); } catch (e) { /* no plot to purge */ }
  el.innerHTML = `<div class="placeholder">${msg}</div>`;
  setStatus(msg);
}

function comboText(combo) {
  if (!combo) return '';
  return paramOrder.map(p => `${paramShort[p]}=${fmt(combo[p])}`).join(', ');
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
