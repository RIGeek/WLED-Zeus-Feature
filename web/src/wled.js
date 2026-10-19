// SPDX-License-Identifier: GPL-2.0-or-later
// WLED panel. Plain createElement (no JSX, no bundler dependencies), built the same way and with
// the same look as the RZ1ZR amplifier and Home Assistant panels. `react` is provided by the Zeus
// host; styles are inlined by build.mjs.
import * as ReactNS from 'react';
// Works whether the host exposes React as named exports, a default export, or both.
const React = ReactNS.default ?? ReactNS;

const h = React.createElement;
const STYLES = /*__STYLES__*/"";
const ROOT = 'com-n1mjf-wled';
const POLL_MS = 400;

function ensureStyles() {
  if (document.getElementById(`${ROOT}-styles`)) return;
  const el = document.createElement('style');
  el.id = `${ROOT}-styles`;
  el.textContent = STYLES;
  document.head.appendChild(el);
}

// callBackend resolves to a fetch Response (not parsed JSON); parse it and surface HTTP errors.
async function call(api, method, path, body) {
  const res = await api.callBackend(method, path, body);
  let data = null;
  try { data = await res.json(); } catch { /* empty or non-JSON body */ }
  return { ok: !!res.ok, status: res.status, data };
}

const isNum = (v) => typeof v === 'number' && Number.isFinite(v);
const pctOf = (v, min, max) => (v == null ? 0 : Math.max(0, Math.min(100, ((v - min) / (max - min)) * 100)));
const num = (s) => (s === '' || s == null ? null : Number(s));
const str = (v) => (v == null ? '' : String(v));
const hexIn = (c) => `#${(c || '000000').toLowerCase()}`;
const hexOut = (v) => v.replace('#', '').toUpperCase();

const STYLE_NAME = { look: 'Colour', percent: 'Percent bar', leds: 'LED bar', color: 'Zone colour', band: 'Band colours' };
const STYLE_LONG = { look: 'Colour and effect', percent: 'Percent bar (WLED effect)', leds: 'LED bar (zones, peak)', color: 'Whole segment in the zone colour', band: 'Band colours' };
const GATE_NAME = { always: 'Always', tx: 'While transmitting', rx: 'While receiving' };
const STATE_PILL = { connected: ['Connected', 'ok'], connecting: ['Connecting…', 'warn'], error: ['Unreachable', 'warn'], disabled: ['Off', ''] };

function zoneOf(value, m) {
  if (value == null || !m || !isNum(m.warn) || !isNum(m.bad)) return null;
  if (m.lowIsBad) return value <= m.bad ? 'bad' : value <= m.warn ? 'warn' : 'ok';
  return value >= m.bad ? 'bad' : value >= m.warn ? 'warn' : 'ok';
}

function fmt(v, digits) {
  return isNum(v) ? v.toFixed(digits ?? 0) : '—';
}

// LED-style bar, identical in build to the amplifier's meters.
function Bar({ value, m, label }) {
  const live = isNum(value);
  const zones = isNum(m.warn) && isNum(m.bad);
  const banded = zones && live;
  const style = banded ? { '--wp': `${pctOf(m.warn, m.min, m.max)}%`, '--bp': `${pctOf(m.bad, m.min, m.max)}%` } : undefined;
  const pct = pctOf(live ? value : null, m.min, m.max);
  return h('div', {
    className: `zw-bar${live ? '' : ' stale'}${banded ? (m.lowIsBad ? ' banded-low' : ' banded') : ''}`, style,
    role: 'meter', 'aria-label': label, 'aria-valuemin': m.min, 'aria-valuemax': m.max, 'aria-valuenow': live ? value : m.min,
  },
    h('i', { style: { clipPath: `inset(0 ${100 - pct}% 0 0)` } }),
    pct > 0 ? h('s', { className: 'zw-tip', style: { left: `${pct}%` } }) : null);
}

function Gear() {
  const spokes = Array.from({ length: 8 }, (_, i) => {
    const a = (i * Math.PI) / 4;
    return h('line', { key: i, x1: 12 + 7.5 * Math.cos(a), y1: 12 + 7.5 * Math.sin(a), x2: 12 + 10 * Math.cos(a), y2: 12 + 10 * Math.sin(a) });
  });
  return h('svg', {
    width: 16, height: 16, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor',
    strokeWidth: 2.4, strokeLinecap: 'round', 'aria-hidden': 'true', focusable: 'false',
  }, h('circle', { cx: 12, cy: 12, r: 6 }), h('circle', { cx: 12, cy: 12, r: 2 }), spokes);
}

function Icon({ d }) {
  return h('svg', {
    width: 14, height: 14, viewBox: '0 0 24 24', fill: 'none', stroke: 'currentColor', strokeWidth: 2.4,
    strokeLinecap: 'round', strokeLinejoin: 'round', 'aria-hidden': 'true', focusable: 'false',
  }, h('path', { d }));
}
const ICONS = { up: 'M6 15l6-6 6 6', down: 'M6 9l6 6 6-6', edit: 'M4 20h4L19 9l-4-4L4 16v4z', remove: 'M6 6l12 12M18 6 6 18' };

// A labelled box for numbers or text, holding strings so half-typed values survive re-renders.
function Field({ label, value, onChange, type = 'text', placeholder, max, inputMode, id }) {
  return h('label', { className: 'zw-num' },
    h('span', { className: 'zw-label' }, label),
    h('input', {
      id, type, value, placeholder, maxLength: max, inputMode, step: 'any', autoComplete: 'off', spellCheck: false,
      onChange: (e) => onChange(e.target.value),
    }));
}

function ColorField({ label, value, onChange }) {
  return h('label', { className: 'zw-num' },
    h('span', { className: 'zw-label' }, label),
    h('input', { type: 'color', value: hexIn(value), onChange: (e) => onChange(hexOut(e.target.value)) }));
}

function Select({ label, value, options, onChange }) {
  return h('label', { className: 'zw-num' },
    h('span', { className: 'zw-label' }, label),
    h('select', { value, onChange: (e) => onChange(e.target.value) },
      options.map(([v, text]) => h('option', { key: v, value: v }, text))));
}

function Check({ label, checked, onChange }) {
  return h('label', { className: 'zw-check' },
    h('input', { type: 'checkbox', checked, onChange: (e) => onChange(e.target.checked) }), label);
}

// ------------------------------------------------------------------ rule drafts

function meterDraft(m) {
  return {
    min: str(m.min), max: str(m.max), warn: str(m.warn), bad: str(m.bad), lowIsBad: !!m.lowIsBad,
    okColor: m.okColor || '00FF00', warnColor: m.warnColor || 'FFA000', badColor: m.badColor || 'FF0000', offColor: m.offColor || '000000',
    brightness: str(m.brightness ?? 255), smoothing: str(m.smoothing ?? 40), peakHold: m.peakHold ?? true,
  };
}

function lookDraft(l, color) {
  return {
    color: l?.color || color, effect: l?.effect || '', brightness: str(l?.brightness ?? 255),
    speed: str(l?.speed ?? 128), intensity: str(l?.intensity ?? 128),
  };
}

function bandsDraft(given, catalog) {
  const out = {};
  for (const b of catalog.bands) out[b.band] = given?.[b.band] || b.color;
  return out;
}

function toDraft(r, catalog) {
  const sig = catalog.signals.find((s) => s.id === r.signal) || catalog.signals[0];
  return {
    id: r.id, label: r.label || '', enabled: r.enabled !== false, signal: sig.id, controller: r.controller, segment: String(r.segment ?? -1),
    style: r.style || sig.styles[0], gate: r.gate || sig.gate,
    look: lookDraft(r.look, sig.styles[0] === 'look' ? 'FF0000' : 'FFFFFF'),
    meter: meterDraft(r.meter || sig),
    bands: bandsDraft(r.bandColors, catalog),
  };
}

function newDraft(catalog, controller) {
  return { ...toDraft({ signal: 'tx', controller, segment: -1 }, catalog), id: null };
}

// Returns [payload, error].
function fromDraft(d, catalog) {
  const sig = catalog.signals.find((s) => s.id === d.signal);
  if (!d.controller) return [null, 'Choose a WLED controller'];
  const out = {
    id: d.id, label: d.label.trim() || null, enabled: d.enabled, signal: d.signal, controller: d.controller,
    segment: Number(d.segment), style: d.style, gate: d.gate, look: null, meter: null, bandColors: null,
  };
  if (d.style === 'look' || d.style === 'band') {
    out.look = {
      color: d.look.color, effect: d.look.effect || null,
      brightness: num(d.look.brightness), speed: num(d.look.speed), intensity: num(d.look.intensity),
    };
  }
  if (d.style === 'band') out.bandColors = d.bands;
  if (sig.kind === 'level') {
    const m = d.meter;
    const v = { min: num(m.min), max: num(m.max), warn: num(m.warn), bad: num(m.bad) };
    if (!isNum(v.min) || !isNum(v.max)) return [null, 'Enter the bar minimum and maximum'];
    if (!(v.min < v.max)) return [null, 'Minimum must be below maximum'];
    if ((v.warn == null) !== (v.bad == null)) return [null, 'Fill both colour thresholds, or neither'];
    if (v.warn != null && (m.lowIsBad ? v.warn < v.bad : v.warn > v.bad)) {
      return [null, m.lowIsBad ? 'With “low is bad”, yellow must not be below red' : 'Yellow must not be above red'];
    }
    out.meter = {
      ...v, lowIsBad: m.lowIsBad, okColor: m.okColor, warnColor: m.warnColor, badColor: m.badColor, offColor: m.offColor,
      brightness: num(m.brightness), smoothing: num(m.smoothing), peakHold: m.peakHold,
    };
  }
  return [out, null];
}

// Signal change: style, gate and bar defaults follow the new signal.
function withSignal(d, id, catalog) {
  const sig = catalog.signals.find((s) => s.id === id);
  return {
    ...d, signal: id, style: sig.styles.includes(d.style) ? d.style : sig.styles[0], gate: sig.gate,
    meter: meterDraft(sig), look: sig.kind === 'flag' ? lookDraft(null, 'FF0000') : d.look,
  };
}

function segmentName(ctl, seg) {
  if (seg < 0) return 'all segments';
  const s = ctl?.segments?.find((x) => x.id === seg);
  return s?.name ? `seg ${seg} ${s.name}` : `seg ${seg}`;
}

function RuleEditor({ api, d, catalog, controllers, statusById, onChange }) {
  const set = (patch) => onChange({ ...d, ...patch });
  const setLook = (patch) => set({ look: { ...d.look, ...patch } });
  const setM = (patch) => set({ meter: { ...d.meter, ...patch } });
  const sig = catalog.signals.find((s) => s.id === d.signal);
  const ctl = statusById[d.controller];
  const [effects, setEffects] = React.useState([]);
  React.useEffect(() => {
    let stop = false;
    (async () => {
      try {
        const r = await call(api, 'GET', `/effects?controller=${encodeURIComponent(d.controller)}`);
        if (!stop && r.ok) setEffects(r.data.effects || []);
      } catch { /* list stays empty */ }
    })();
    return () => { stop = true; };
  }, [api, d.controller, ctl?.effectCount]);

  const segOptions = [['-1', 'All segments'], ...(ctl?.segments || []).map((s) => [String(s.id), `${s.id}${s.name ? ` · ${s.name}` : ''} (${s.length})`])];
  if (!segOptions.some(([v]) => v === d.segment)) segOptions.push([d.segment, `${d.segment} (not on the controller)`]);
  const effectOptions = [['', 'Solid'], ...effects.filter((e) => e !== 'Solid').map((e) => [e, e])];
  if (d.look.effect && !effects.includes(d.look.effect)) effectOptions.push([d.look.effect, `${d.look.effect} (not found)`]);
  const [, err] = fromDraft(d, catalog);

  return h('div', { className: 'zw-editor' },
    h('div', { className: 'zw-fields' },
      Field({ label: 'Label', value: d.label, onChange: (v) => set({ label: v }), placeholder: sig.name, max: 40 }),
      Select({ label: 'Follows', value: d.signal, options: catalog.signals.map((s) => [s.id, s.name]), onChange: (v) => onChange(withSignal(d, v, catalog)) }),
      Select({ label: 'Controller', value: d.controller, options: controllers.map((c) => [c.id, c.name]), onChange: (v) => set({ controller: v }) }),
      Select({ label: 'Segment', value: d.segment, options: segOptions, onChange: (v) => set({ segment: v }) }),
      sig.styles.length > 1 ? Select({ label: 'Show as', value: d.style, options: sig.styles.map((s) => [s, STYLE_LONG[s]]), onChange: (v) => set({ style: v }) }) : null,
      sig.kind === 'level' ? Select({ label: 'When', value: d.gate, options: Object.entries(GATE_NAME), onChange: (v) => set({ gate: v }) }) : null),

    d.style === 'look' || d.style === 'band' ? h('div', { className: 'zw-fields' },
      d.style === 'look' ? ColorField({ label: 'Colour', value: d.look.color, onChange: (v) => setLook({ color: v }) }) : null,
      Select({ label: 'Effect', value: d.look.effect, options: effectOptions, onChange: (v) => setLook({ effect: v }) }),
      Field({ label: 'Brightness', type: 'number', inputMode: 'numeric', value: d.look.brightness, onChange: (v) => setLook({ brightness: v }) }),
      d.look.effect ? Field({ label: 'Speed', type: 'number', inputMode: 'numeric', value: d.look.speed, onChange: (v) => setLook({ speed: v }) }) : null,
      d.look.effect ? Field({ label: 'Intensity', type: 'number', inputMode: 'numeric', value: d.look.intensity, onChange: (v) => setLook({ intensity: v }) }) : null) : null,

    d.style === 'band' ? h('div', { className: 'zw-bands' },
      catalog.bands.map((b) => h(ColorField, {
        key: b.band, label: b.band, value: d.bands[b.band],
        onChange: (v) => set({ bands: { ...d.bands, [b.band]: v } }),
      }))) : null,

    sig.kind === 'level' ? h('div', { className: 'zw-range-fields' },
      Field({ label: `Min${sig.unit ? ` (${sig.unit})` : ''}`, type: 'number', inputMode: 'decimal', value: d.meter.min, onChange: (v) => setM({ min: v }) }),
      Field({ label: 'Max', type: 'number', inputMode: 'decimal', value: d.meter.max, onChange: (v) => setM({ max: v }) }),
      Field({ label: 'Yellow from', type: 'number', inputMode: 'decimal', value: d.meter.warn, onChange: (v) => setM({ warn: v }) }),
      Field({ label: 'Red from', type: 'number', inputMode: 'decimal', value: d.meter.bad, onChange: (v) => setM({ bad: v }) })) : null,
    sig.kind === 'level' ? h('div', { className: 'zw-range-fields' },
      ColorField({ label: 'Normal', value: d.meter.okColor, onChange: (v) => setM({ okColor: v }) }),
      ColorField({ label: 'Yellow', value: d.meter.warnColor, onChange: (v) => setM({ warnColor: v }) }),
      ColorField({ label: 'Red', value: d.meter.badColor, onChange: (v) => setM({ badColor: v }) }),
      d.style !== 'color' ? ColorField({ label: 'Unlit', value: d.meter.offColor, onChange: (v) => setM({ offColor: v }) }) : null) : null,
    sig.kind === 'level' ? h('div', { className: 'zw-range-fields' },
      Field({ label: 'Brightness', type: 'number', inputMode: 'numeric', value: d.meter.brightness, onChange: (v) => setM({ brightness: v }) }),
      Field({ label: 'Smoothing %', type: 'number', inputMode: 'numeric', value: d.meter.smoothing, onChange: (v) => setM({ smoothing: v }) })) : null,
    sig.kind === 'level' ? Check({ label: 'Low values are bad', checked: d.meter.lowIsBad, onChange: (v) => setM({ lowIsBad: v }) }) : null,
    sig.kind === 'level' && d.style === 'leds' ? Check({ label: 'Peak hold (white LED)', checked: d.meter.peakHold, onChange: (v) => setM({ peakHold: v }) }) : null,
    Check({ label: 'Enabled', checked: d.enabled, onChange: (v) => set({ enabled: v }) }),
    err ? h('p', { className: 'zw-msg err', role: 'alert' }, err) : null);
}

// ------------------------------------------------------------------ panel

function Panel({ api }) {
  const [status, setStatus] = React.useState(null);
  const [cfg, setCfg] = React.useState(null);          // GET /settings: controllers, sources, rules, catalog
  const [loadError, setLoadError] = React.useState(null);
  const [msg, setMsg] = React.useState({ text: '', err: false });
  const [view, setView] = React.useState('main');
  // settings drafts
  const [ctlDraft, setCtlDraft] = React.useState(null);
  const [srcDraft, setSrcDraft] = React.useState(null);
  const [ruleDraft, setRuleDraft] = React.useState(null);
  const [editing, setEditing] = React.useState(null);
  const [sectionMsg, setSectionMsg] = React.useState({});
  const gearRef = React.useRef(null);
  const firstRef = React.useRef(null);
  const wasSettings = React.useRef(false);

  const loadSettings = React.useCallback(async () => {
    try {
      const r = await call(api, 'GET', '/settings');
      if (r.ok) setCfg(r.data);
    } catch { /* retried by the poll */ }
  }, [api]);

  React.useEffect(() => {
    let stop = false;
    let timer = 0;
    const tick = async () => {
      try {
        const r = await call(api, 'GET', '/status');
        if (stop) return;
        if (!r.ok || !r.data?.signals) throw new Error(r.data?.message || `HTTP ${r.status}`);
        setStatus(r.data);
        setLoadError(null);
      } catch (e) {
        if (!stop) setLoadError(e?.message || 'Cannot reach the WLED feature');
      }
      if (!stop) timer = setTimeout(tick, POLL_MS);
    };
    tick();
    loadSettings();
    return () => { stop = true; clearTimeout(timer); };
  }, [api, loadSettings]);

  React.useEffect(() => {
    if (view === 'settings') firstRef.current?.focus();
    else if (wasSettings.current) gearRef.current?.focus();
    wasSettings.current = view === 'settings';
  }, [view]);

  React.useEffect(() => {
    if (!msg.text || msg.err) return undefined;
    const t = setTimeout(() => setMsg({ text: '', err: false }), 4000);
    return () => clearTimeout(t);
  }, [msg]);

  const post = async (path, body, section) => {
    const setter = (m) => (section ? setSectionMsg((s) => ({ ...s, [section]: m })) : setMsg(m));
    try {
      const r = await call(api, 'POST', path, body);
      const ok = r.ok && r.data?.ok !== false;
      setter({ text: r.data?.message || (ok ? 'Done' : `Request failed (HTTP ${r.status})`), err: !ok });
      return { ok, data: r.data };
    } catch (e) {
      setter({ text: e?.message || 'Request failed', err: true });
      return { ok: false, data: null };
    }
  };

  if (!status || !cfg) {
    return h('div', { className: ROOT },
      h('div', { className: 'zw-card' },
        h('h2', { className: 'zw-title' }, 'WLED'),
        h('p', { className: 'zw-empty', role: 'status' }, loadError ? `Unavailable: ${loadError}` : 'Loading…')));
  }

  const { catalog } = cfg;
  const sigInfo = Object.fromEntries(catalog.signals.map((s) => [s.id, s]));
  const statusById = Object.fromEntries(status.controllers.map((c) => [c.id, c]));
  const tx = status.radio.tx === true;
  const connected = status.controllers.filter((c) => c.state === 'connected').length;
  const total = status.controllers.length;

  // ------------------------------------------------------------------ settings view
  if (view === 'settings') {
    const ctls = ctlDraft ?? cfg.controllers.map((c) => ({ ...c, maxRate: str(c.maxRate) }));
    const src = srcDraft ?? { ...cfg.sources, tciPort: str(cfg.sources.tciPort), tciIntervalMs: str(cfg.sources.tciIntervalMs), zeusUrl: cfg.sources.zeusUrl || '' };
    const rules = ruleDraft ?? cfg.rules.map((r) => toDraft(r, catalog));
    const ruleErrors = rules.map((d) => fromDraft(d, catalog)[1]);
    const sm = (k) => sectionMsg[k] || { text: '', err: false };
    const savedControllers = cfg.controllers;

    const close = () => {
      setCtlDraft(null); setSrcDraft(null); setRuleDraft(null); setEditing(null); setSectionMsg({}); setView('main');
    };
    const changeRules = (next) => { setRuleDraft(next); setSectionMsg((s) => ({ ...s, rules: { text: '', err: false } })); };
    const moveRule = (i, by) => {
      const next = [...rules];
      const [x] = next.splice(i, 1);
      next.splice(i + by, 0, x);
      changeRules(next);
    };

    const tciLink = status.sources.tci;
    const ampLink = status.sources.amp;
    const linkText = (l) => (!l.enabled ? 'Off' : l.state === 'connected' ? 'Connected' : l.error || l.state);

    return h('div', { className: ROOT },
      h('section', { className: 'zw-card', 'aria-label': 'WLED settings' },
        h('div', { className: 'zw-head' },
          h('h2', { className: 'zw-title' }, 'Settings'),
          h('button', { type: 'button', ref: firstRef, onClick: close }, 'Done')),

        // ---- controllers
        h('div', { className: 'zw-section' },
          h('h2', { className: 'zw-label' }, `WLED controllers (up to ${catalog.maxControllers})`),
          ctls.length === 0 ? h('p', { className: 'zw-note' }, 'No controllers yet. Add one with its IP address or name.') : null,
          ctls.map((c, i) => {
            const live = c.id ? statusById[c.id] : null;
            const set = (patch) => setCtlDraft(ctls.map((x, j) => (j === i ? { ...x, ...patch } : x)));
            return h('div', { key: c.id || `new${i}`, className: 'zw-editor' },
              h('div', { className: 'zw-fields' },
                Field({ label: 'Name', value: c.name || '', onChange: (v) => set({ name: v }), placeholder: 'Desk strip', max: 32 }),
                Field({ label: 'Address', value: c.host || '', onChange: (v) => set({ host: v }), placeholder: '192.168.1.50' }),
                Field({ label: 'Updates / s', type: 'number', inputMode: 'numeric', value: c.maxRate, onChange: (v) => set({ maxRate: v }) })),
              Check({ label: 'Turn the light on when a rule needs it (and off again after)', checked: c.turnOn !== false, onChange: (v) => set({ turnOn: v }) }),
              Check({ label: 'Enabled', checked: c.enabled !== false, onChange: (v) => set({ enabled: v }) }),
              h('div', { className: 'zw-row' },
                live ? h('span', { className: `zw-pill ${(STATE_PILL[live.state] || [live.state, 'warn'])[1]}` }, (STATE_PILL[live.state] || [live.state])[0]) : null,
                live?.error ? h('span', { className: 'zw-note' }, live.error) : null,
                h('button', { type: 'button', style: { marginLeft: 'auto' }, onClick: () => setCtlDraft(ctls.filter((_, j) => j !== i)) }, 'Remove')));
          }),
          h('div', { className: 'zw-row' },
            h('button', {
              type: 'button', disabled: ctls.length >= catalog.maxControllers,
              onClick: () => setCtlDraft([...ctls, { id: null, name: '', host: '', enabled: true, maxRate: '20', turnOn: true }]),
            }, 'Add controller'),
            h('button', {
              type: 'button', disabled: !ctlDraft,
              onClick: async () => {
                const payload = ctls.map((c) => ({ id: c.id, name: c.name, host: c.host, enabled: c.enabled !== false, maxRate: num(c.maxRate), turnOn: c.turnOn !== false }));
                const r = await post('/controllers', { controllers: payload }, 'ctl');
                if (r.ok) { setCtlDraft(null); setRuleDraft(null); await loadSettings(); }
              },
            }, 'Save controllers'),
            ctlDraft ? h('button', { type: 'button', onClick: () => setCtlDraft(null) }, 'Discard') : null),
          h('div', { className: `zw-msg${sm('ctl').err ? ' err' : ''}`, role: 'status', 'aria-live': 'polite' },
            sm('ctl').text || (ctlDraft ? 'Unsaved changes. Removing a controller removes its rules.' : ''))),

        // ---- sources
        h('form', {
          className: 'zw-section',
          onSubmit: async (e) => {
            e.preventDefault();
            const r = await post('/sources', {
              tciEnabled: src.tciEnabled, tciHost: src.tciHost, tciPort: num(src.tciPort), tciIntervalMs: num(src.tciIntervalMs),
              ampEnabled: src.ampEnabled, zeusUrl: src.zeusUrl,
            }, 'src');
            if (r.ok) { setSrcDraft(null); await loadSettings(); }
          },
        },
          h('h2', { className: 'zw-label' }, 'Meters from the radio'),
          h('p', { className: 'zw-note' }, status.radio.available
            ? 'Transmit and band come straight from Zeus. S-meter, mic, power, SWR and tune come from Zeus’s TCI server (Settings → TCI in Zeus).'
            : 'This Zeus does not share radio state with features, so transmit also comes from TCI.'),
          Check({ label: 'Use TCI', checked: src.tciEnabled, onChange: (v) => setSrcDraft({ ...src, tciEnabled: v }) }),
          src.tciEnabled ? h('div', { className: 'zw-fields' },
            Field({ label: 'TCI host', value: src.tciHost, onChange: (v) => setSrcDraft({ ...src, tciHost: v }), placeholder: '127.0.0.1' }),
            Field({ label: 'Port', type: 'number', inputMode: 'numeric', value: src.tciPort, onChange: (v) => setSrcDraft({ ...src, tciPort: v }) }),
            Field({ label: 'Meter rate (ms)', type: 'number', inputMode: 'numeric', value: src.tciIntervalMs, onChange: (v) => setSrcDraft({ ...src, tciIntervalMs: v }) })) : null,
          src.tciEnabled ? h('p', { className: 'zw-note' }, `TCI: ${linkText(tciLink)}`) : null,
          Check({ label: 'Read the RZ1ZR amplifier feature (amp power and SWR)', checked: src.ampEnabled, onChange: (v) => setSrcDraft({ ...src, ampEnabled: v }) }),
          src.ampEnabled ? h('p', { className: 'zw-note' },
            `Amplifier: ${ampLink.state === 'idle' ? 'Waiting (no rule uses amp power or amp SWR yet)'
              : ampLink.state === 'waiting_for_zeus' ? 'Finding the amplifier feature…' : linkText(ampLink)}`,
            ampLink.endpoint ? h('br') : null,
            ampLink.endpoint ? h('span', { className: 'zw-item-id' }, ampLink.endpoint) : null) : null,
          src.ampEnabled ? Field({
            label: 'Zeus address (leave blank to find it automatically)', value: src.zeusUrl,
            onChange: (v) => setSrcDraft({ ...src, zeusUrl: v }), placeholder: 'Automatic',
          }) : null,
          h('div', { className: 'zw-row' },
            h('button', { type: 'submit', disabled: !srcDraft }, 'Save sources'),
            srcDraft ? h('button', { type: 'button', onClick: () => setSrcDraft(null) }, 'Discard') : null),
          h('div', { className: `zw-msg${sm('src').err ? ' err' : ''}`, role: 'status', 'aria-live': 'polite' }, sm('src').text)),

        // ---- rules
        h('div', { className: 'zw-section' },
          h('h2', { className: 'zw-label' }, 'Rules'),
          h('p', { className: 'zw-note' }, 'A rule higher in the list wins a segment over a lower one. When no rule wants a segment, it goes back to its own look.'),
          savedControllers.length === 0 ? h('p', { className: 'zw-note' }, 'Save a controller first.') : null,
          rules.length ? h('div', { className: 'zw-list' },
            rules.map((d, i) => {
              const key = d.id || `new${i}`;
              const open = editing === key;
              const sig = sigInfo[d.signal];
              const ctl = statusById[d.controller];
              return h('div', { key, className: 'zw-item' },
                h('div', { className: 'zw-item-row' },
                  h('span', { className: 'zw-kind' }, STYLE_NAME[d.style] || d.style),
                  h('span', { className: 'zw-item-text' },
                    h('span', { className: 'zw-item-name' }, d.label || sig?.name || d.signal, d.enabled ? '' : ' (off)'),
                    h('span', { className: 'zw-item-id' }, `${ctl?.name || '?'} · ${segmentName(ctl, Number(d.segment))}`)),
                  h('button', { type: 'button', className: 'zw-icon', 'aria-label': 'Move up', title: 'Move up', disabled: i === 0, onClick: () => moveRule(i, -1) }, h(Icon, { d: ICONS.up })),
                  h('button', { type: 'button', className: 'zw-icon', 'aria-label': 'Move down', title: 'Move down', disabled: i === rules.length - 1, onClick: () => moveRule(i, 1) }, h(Icon, { d: ICONS.down })),
                  h('button', { type: 'button', className: 'zw-icon', 'aria-label': 'Edit', title: 'Edit', 'aria-expanded': open, onClick: () => setEditing(open ? null : key) }, h(Icon, { d: ICONS.edit })),
                  h('button', { type: 'button', className: 'zw-icon', 'aria-label': 'Remove', title: 'Remove', onClick: () => changeRules(rules.filter((_, j) => j !== i)) }, h(Icon, { d: ICONS.remove }))),
                open ? h(RuleEditor, {
                  api, d, catalog, controllers: savedControllers, statusById,
                  onChange: (nd) => changeRules(rules.map((x, j) => (j === i ? nd : x))),
                }) : null,
                !open && ruleErrors[i] ? h('p', { className: 'zw-msg err' }, ruleErrors[i]) : null);
            })) : null,
          h('div', { className: 'zw-row' },
            h('button', {
              type: 'button', disabled: savedControllers.length === 0 || rules.length >= catalog.maxRules,
              onClick: () => { const d = newDraft(catalog, savedControllers[0].id); changeRules([...rules, d]); setEditing(`new${rules.length}`); },
            }, 'Add rule'),
            h('button', {
              type: 'button', disabled: !ruleDraft || ruleErrors.some(Boolean),
              onClick: async () => {
                const r = await post('/rules', { rules: rules.map((d) => fromDraft(d, catalog)[0]) }, 'rules');
                if (r.ok) { setRuleDraft(null); setEditing(null); await loadSettings(); }
              },
            }, 'Save rules'),
            ruleDraft ? h('button', { type: 'button', onClick: () => { setRuleDraft(null); setEditing(null); } }, 'Discard') : null),
          h('div', { className: `zw-msg${sm('rules').err ? ' err' : ''}`, role: 'status', 'aria-live': 'polite' },
            sm('rules').text || (ruleDraft ? 'Unsaved changes' : '')))));
  }

  // ------------------------------------------------------------------ main view
  const pill = total === 0 ? ['Not configured', '']
    : tx ? ['Transmitting', 'tx']
      : connected === total ? [total === 1 ? 'Connected' : `${total} lights`, 'ok'] : [`${connected} of ${total} lights`, 'warn'];

  const shownSignals = status.signals.filter((s) => s.id !== 'rx' && (
    s.valid || status.rules.some((r) => r.rule.signal === s.id) || ['tx', 'band'].includes(s.id)));

  const readout = (s) => {
    const info = sigInfo[s.id];
    let text = '—';
    let cls = '';
    if (s.valid) {
      if (s.kind === 'flag') { text = s.value > 0 ? 'On' : 'Off'; cls = s.value > 0 ? (s.id === 'tx' ? ' z-bad' : ' on') : ' off'; }
      else if (s.kind === 'text') text = s.text;
      else {
        text = fmt(s.value, s.digits);
        const z = zoneOf(s.value, info);
        if (z && z !== 'ok') cls = ` z-${z}`;
      }
    } else cls = ' na';
    return h('div', { key: s.id, className: `zw-read${cls}${s.testing ? ' testing' : ''}`, title: s.testing ? 'Test value' : undefined },
      h('span', { className: 'zw-label k' }, s.name),
      h('span', { className: 'v' }, text, s.valid && s.kind === 'level' && s.unit ? h('span', { className: 'unit' }, s.unit) : null));
  };

  const ruleRow = (ar) => {
    const r = ar.rule;
    const info = sigInfo[r.signal];
    const ctl = statusById[r.controller];
    const where = `${ctl?.name || '?'} · ${segmentName(ctl, r.segment)} · ${STYLE_NAME[r.style]}${info?.kind === 'level' && r.gate !== 'always' ? ` · ${GATE_NAME[r.gate].toLowerCase()}` : ''}`;
    const dot = ar.error ? 'err' : ar.active && ar.segments.length ? 'on' : '';
    return h('div', { key: r.id, className: 'zw-rule' },
      h('div', { className: 'zw-rule-head' },
        h('span', { className: `zw-dot ${dot}`, title: ar.error || (ar.active ? (ar.segments.length ? 'Showing' : 'Active, but a rule above has its segments') : 'Waiting') }),
        r.look && r.style === 'look' ? h('span', { className: 'zw-swatch', 'aria-hidden': 'true', style: { background: hexIn(r.look.color) } }) : null,
        h('span', { className: 'zw-rule-text' },
          h('span', { className: 'zw-rule-name' }, r.label || info?.name || r.signal, r.enabled ? '' : ' (off)'),
          h('span', { className: 'zw-rule-where' }, where)),
        h('button', {
          type: 'button', disabled: !r.enabled, title: 'Show this rule on the lights for 5 seconds without transmitting',
          onClick: () => post('/test', { rule: r.id, seconds: 5 }),
        }, 'Test')),
      r.meter && r.enabled ? h('div', { className: 'zw-bar-wrap', style: { marginLeft: 16 } }, h(Bar, { value: ar.shown, m: r.meter, label: r.label || info?.name })) : null,
      ar.error ? h('p', { className: 'zw-msg err', style: { margin: '0 0 0 16px' } }, ar.error) : null);
  };

  return h('div', { className: ROOT },
    h('div', { className: 'zw-card' },
      h('div', { className: 'zw-head' },
        h('h2', { className: 'zw-title' }, 'WLED'),
        h('div', { className: 'zw-head-end' },
          h('span', { className: `zw-pill ${pill[1]}`, role: 'status' }, pill[0]),
          h('button', {
            type: 'button', className: 'zw-icon', ref: gearRef, 'aria-label': 'Settings', title: 'Settings',
            onClick: () => { loadSettings(); setView('settings'); },
          }, h(Gear)))),
      h('div', { className: 'zw-readouts auto' }, shownSignals.map(readout)),
      status.sources.tci.enabled && status.sources.tci.state !== 'connected' && status.sources.tci.error
        ? h('p', { className: 'zw-msg err' }, `TCI: ${status.sources.tci.error}`) : null,
      status.sources.amp.enabled && status.sources.amp.state !== 'connected' && status.sources.amp.state !== 'idle' && status.sources.amp.error
        ? h('p', { className: 'zw-msg err' }, `Amplifier: ${status.sources.amp.error}`) : null,
      total === 0 ? h('p', { className: 'zw-empty' }, 'No WLED controllers yet. Open settings (the gear) to add up to four, then add rules.') : null,
      msg.text ? h('div', { className: `zw-msg${msg.err ? ' err' : ''}`, role: 'status', 'aria-live': 'polite' }, msg.text) : null),

    total ? h('section', { className: 'zw-card', 'aria-label': 'Lights' },
      h('h2', { className: 'zw-label' }, 'Lights'),
      status.controllers.map((c) => {
        const p = STATE_PILL[c.state] || [c.state, 'warn'];
        const meta = c.state === 'connected'
          ? [c.deviceName, c.version && `v${c.version}`, c.ledCount && `${c.ledCount} LEDs`, c.lightOn ? null : 'light off'].filter(Boolean).join(' · ')
          : c.error || c.host;
        return h('div', { key: c.id, className: 'zw-ctl' },
          h('div', { className: 'zw-ctl-head' },
            h('span', { className: 'zw-ctl-name' }, c.name),
            h('span', { className: 'zw-ctl-meta', title: c.host }, meta),
            h('span', { className: `zw-pill ${p[1]}` }, p[0])),
          c.segments.length ? h('div', { className: 'zw-segs', role: 'group', 'aria-label': `${c.name} segments` },
            c.segments.map((s) => h('button', {
              key: s.id, type: 'button', className: `zw-seg${c.driven.includes(s.id) ? ' driven' : ''}${s.on ? '' : ' off'}`,
              title: `Flash segment ${s.id} to find it${c.driven.includes(s.id) ? ' (a rule is showing on it)' : ''}`,
              onClick: () => post('/identify', { controller: c.id, segment: s.id }),
            }, `${s.id}${s.name ? ` ${s.name}` : ''}`))) : null);
      })) : null,

    status.rules.length ? h('section', { className: 'zw-card', 'aria-label': 'Rules' },
      h('div', { className: 'zw-head' },
        h('h2', { className: 'zw-label' }, 'Rules'),
        status.signals.some((s) => s.testing) ? h('button', { type: 'button', onClick: () => post('/test', { rule: null }) }, 'Stop test') : null),
      status.rules.map(ruleRow)) : null);
}

export default function register(api) {
  ensureStyles();
  api.registerPanel({
    id: 'wled',
    component: () => h(Panel, { api }),
  });
}
