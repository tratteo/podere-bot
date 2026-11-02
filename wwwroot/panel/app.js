'use strict';

const MIN = 60000;
const CONFIG = window.PANEL_CONFIG || { mode: 'lan', version: 'locale' };

const ROLES = {
  admin: { label: 'Admin', icon: 'shield' },
  gate_controller: { label: 'Gate controller', icon: 'gate' },
};

const TOPICS = {
  technical: { label: 'Tecnico', icon: 'activity' },
  heating: { label: 'Riscaldamento', icon: 'heat' },
  gates: { label: 'Cancelli', icon: 'gate' },
};

const EVENT_TOPICS = {
  technical: { label: 'Tecnico', icon: 'activity' },
  heating: { label: 'Riscaldamento', icon: 'heat' },
  gates: { label: 'Cancelli', icon: 'gate' },
  panel: { label: 'Pannello', icon: 'shield' },
};

const DEVICE_STATES = {
  active: { label: 'Attivo', cls: 'ok' },
  pending: { label: 'In attesa', cls: 'warn' },
  expired: { label: 'Scaduto', cls: 'muted' },
  revoked: { label: 'Revocato', cls: 'bad' },
};

const views = {
  home: { title: 'Casa', sub: 'Riepilogo' },
  heating: { title: 'Riscaldamento', sub: 'Programma, caldaia e antigelo' },
  gates: { title: 'Cancelli', sub: 'Accessi, finestra e aperture' },
  events: { title: 'Eventi', sub: 'Notifiche e attività recenti' },
  system: { title: 'Sistema', sub: 'Salute, sensori e consumo AI' },
  users: { title: 'Utenti', sub: 'Ruoli, topic e accessi' },
  devices: { title: 'Dispositivi', sub: 'Accesso al pannello da tablet e telefoni' },
};

const state = {
  view: 'home',
  now: new Date(),
  overview: null,
  history: null,
  historyRange: 'today',
  customFrom: '',
  customTo: '',
  events: [],
  eventFilter: 'all',
  loading: true,
  offline: false,
};

/* ---------- api ---------- */

async function api(path, options = {}) {
  const method = options.method || 'GET';
  const headers = {};
  if (CONFIG.mode === 'kiosk' && CONFIG.token) headers['X-Api-Token'] = CONFIG.token;
  if (method !== 'GET') {
    headers['X-Panel'] = '1';
    if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  }

  const response = await fetch('/panel/api' + path, {
    method,
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    credentials: 'same-origin',
    cache: 'no-store',
  });

  if (response.status === 401 && CONFIG.mode === 'lan') {
    location.replace('/panel/login');
    throw new Error('Sessione scaduta');
  }

  let data = null;
  try {
    data = await response.json();
  } catch (error) {
    data = null;
  }

  if (!response.ok) throw new Error(data?.error || 'Errore ' + response.status);
  return data;
}

async function refresh() {
  try {
    const overview = await api('/overview');
    state.overview = overview;
    state.now = new Date();
    state.loading = false;
    state.offline = false;
    render();
  } catch (error) {
    state.offline = true;
    if (error.message !== 'Sessione scaduta') toast(error.message);
    renderStatus();
  }
}

async function loadEvents() {
  try {
    const topic = state.eventFilter === 'all' ? '' : '&topic=' + encodeURIComponent(state.eventFilter);
    const data = await api('/events?limit=50' + topic);
    state.events = data.events || [];
    if (state.view === 'events') renderEvents();
  } catch (error) {
    toast(error.message);
  }
}

async function loadHistory() {
  try {
    const query =
      state.historyRange === 'custom'
        ? '?range=custom&from=' + encodeURIComponent(state.customFrom) + '&to=' + encodeURIComponent(state.customTo)
        : '?range=' + state.historyRange;
    state.history = await api('/history' + query);
    if (state.view === 'heating' && state.overview) renderHeating();
  } catch (error) {
    state.history = { error: error.message, points: [] };
    if (state.view === 'heating' && state.overview) renderHeating();
  }
}

async function write(path, options) {
  const result = await api(path, options);
  if (result && result.overview) {
    state.overview = result.overview;
    state.now = new Date();
    render();
    loadEvents();
  }
  return result;
}

/* ---------- helpers ---------- */

function icon(name, cls = '') {
  return `<svg class="icon ${cls}" aria-hidden="true"><use href="#i-${name}"></use></svg>`;
}

function fmt(n) {
  return n === null || n === undefined || Number.isNaN(Number(n)) ? '—' : Number(n).toFixed(1);
}

function integer(n) {
  return new Intl.NumberFormat('it-IT').format(n || 0);
}

function esc(value) {
  return String(value === null || value === undefined ? '' : value).replace(
    /[&<>"']/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])
  );
}

function minutesToTime(m) {
  const h = Math.floor(m / 60);
  const min = m % 60;
  return String(h).padStart(2, '0') + ':' + String(min).padStart(2, '0');
}

function timeToMinutes(v) {
  if (!v) return null;
  const parts = v.split(':').map(Number);
  if (parts.length !== 2 || Number.isNaN(parts[0]) || Number.isNaN(parts[1])) return null;
  if (parts[0] > 24 || parts[1] > 59) return null;
  if (parts[0] === 24 && parts[1] !== 0) return null;
  return parts[0] * 60 + parts[1];
}

function fmtTime(date) {
  if (!date) return '--:--';
  const d = date instanceof Date ? date : new Date(date);
  if (Number.isNaN(d.getTime())) return '--:--';
  return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
}

function fmtDate(date) {
  const d = date instanceof Date ? date : new Date(date);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleDateString('it-IT', { day: '2-digit', month: '2-digit' });
}

function relative(date) {
  if (!date) return 'mai';
  const d = date instanceof Date ? date : new Date(date);
  if (Number.isNaN(d.getTime())) return 'mai';
  const seconds = Math.max(0, Math.round((state.now - d) / 1000));
  if (seconds < 10) return 'adesso';
  if (seconds < 60) return `${seconds} s fa`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min fa`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h fa`;
  return `${Math.round(hours / 24)} g fa`;
}

function duration(minutes) {
  if (minutes === null || minutes === undefined) return '—';
  const m = Math.max(0, Math.round(minutes));
  if (m < 60) return `${m} minuti`;
  const hours = Math.floor(m / 60);
  return `${hours} h ${m % 60} min`;
}

function uptime(seconds) {
  const days = Math.floor(seconds / 86400);
  const hours = Math.floor((seconds % 86400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  if (days > 0) return `${days} g ${hours} h`;
  if (hours > 0) return `${hours} h ${minutes} min`;
  return `${minutes} min`;
}

function initials(name) {
  return (
    String(name || '?')
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((w) => w[0].toUpperCase())
      .join('') || '?'
  );
}

/* ---------- derived ---------- */

function heating() {
  return state.overview?.heating || null;
}

function temperatures() {
  return state.overview?.temperatures || null;
}

function gates() {
  return state.overview?.gates || null;
}

function alerts() {
  return state.overview?.alerts || [];
}

function currentInterval() {
  const h = heating();
  if (!h) return null;
  const m = state.now.getHours() * 60 + state.now.getMinutes();
  return h.program.intervals.find((i) => m >= i.from && m < i.to) || null;
}

function nextChange() {
  const h = heating();
  if (!h) return null;
  const m = state.now.getHours() * 60 + state.now.getMinutes();
  const out = [];
  for (const i of h.program.intervals) {
    if (i.from > m) out.push({ at: i.from, temp: i.temperature, kind: 'start' });
    if (i.to > m) out.push({ at: i.to, temp: i.temperature, kind: 'end' });
  }
  out.sort((a, b) => a.at - b.at);
  return out[0] || null;
}

function nextLabel(next) {
  if (!next) return 'Nessun cambio previsto';
  if (next.kind === 'end') return `Prossimo cambio: ${minutesToTime(next.at)} · spegnimento`;
  return `Prossima accensione: ${minutesToTime(next.at)} · ${next.temp} °C`;
}

function sensorState(sensor) {
  if (sensor.informational) return 'info';
  return sensor.fresh ? 'ok' : 'stale';
}

/** Hero tint: the temperature decides the shade, from cold blue to warm red. */
function temperatureShade(value) {
  if (value === null || value === undefined || Number.isNaN(Number(value))) return null;
  const stops = [
    { at: 0, rgb: [78, 122, 178] },
    { at: 12, rgb: [86, 163, 128] },
    { at: 20, rgb: [196, 146, 70] },
    { at: 30, rgb: [196, 106, 96] },
  ];
  const v = Math.max(0, Math.min(30, Number(value)));
  let from = stops[0];
  let to = stops[stops.length - 1];
  for (let i = 0; i < stops.length - 1; i += 1) {
    if (v >= stops[i].at && v <= stops[i + 1].at) {
      from = stops[i];
      to = stops[i + 1];
      break;
    }
  }
  const k = (v - from.at) / (to.at - from.at || 1);
  const tint = from.rgb.map((c, i) => c + (to.rgb[i] - c) * k);
  const base = [19, 23, 20];
  const weight = 0.08;
  return `rgb(${tint.map((c, i) => Math.round(base[i] * (1 - weight) + c * weight)).join(',')})`;
}

/* ---------- render ---------- */

function render() {
  document.querySelectorAll('.nav-item, .tabs button').forEach((b) => {
    if (b.dataset.more) {
      const active = ['system', 'users', 'devices'].includes(state.view);
      b.classList.toggle('active', active);
      if (active) b.setAttribute('aria-current', 'page');
      else b.removeAttribute('aria-current');
      return;
    }
    const active = b.dataset.view === state.view;
    b.classList.toggle('active', active);
    if (active) b.setAttribute('aria-current', 'page');
    else b.removeAttribute('aria-current');
  });
  document.querySelectorAll('.view').forEach((v) => {
    v.classList.toggle('active', v.dataset.view === state.view);
  });

  document.getElementById('page-title').textContent = views[state.view].title;
  document.getElementById('page-sub').textContent = views[state.view].sub;

  if (state.loading && !state.overview) {
    document.getElementById(state.view + '-view').innerHTML = skeleton();
  } else if (state.view === 'home') renderHome();
  else if (state.view === 'heating') renderHeating();
  else if (state.view === 'gates') renderGates();
  else if (state.view === 'events') renderEvents();
  else if (state.view === 'system') renderSystem();
  else if (state.view === 'users') renderUsers();
  else if (state.view === 'devices') renderDevices();

  renderStatus();
  updateClock();
}

function skeleton() {
  return `<div class="stack"><section class="card"><div class="empty">${icon('activity')}<span>${state.offline ? 'Bot non raggiungibile. Riprovo…' : 'Carico i dati dal bot…'}</span></div></section></div>`;
}

function renderStatus() {
  const overview = state.overview;
  const health = overview?.health;
  const tg = document.getElementById('tg-pill');
  const db = document.getElementById('db-pill');
  const cpu = document.getElementById('cpu-value');
  const version = document.getElementById('rail-version');
  const mode = document.getElementById('rail-mode');
  const logout = document.getElementById('logout-pill');

  if (tg) tg.classList.toggle('alert', !health?.telegramOk);
  if (db) db.classList.toggle('alert', !health?.databaseOk);
  if (cpu) cpu.textContent = health?.cpuTemperatureCelsius ? `${fmt(health.cpuTemperatureCelsius)} °C` : '—';
  const versionValue = overview?.system?.version || CONFIG.version || '—';
  if (version) version.textContent = versionValue === 'locale' ? 'locale' : 'v' + versionValue;
  if (mode) mode.textContent = CONFIG.mode === 'kiosk' ? 'Kiosk locale' : 'Rete locale';
  if (logout) logout.hidden = CONFIG.mode !== 'lan';

  renderAlertPill();
}

function renderAlertPill() {
  const pill = document.getElementById('alert-pill');
  const count = document.getElementById('alert-count');
  if (!pill || !count) return;
  const list = alerts();
  if (!list.length) {
    pill.hidden = true;
    return;
  }
  pill.hidden = false;
  count.textContent = `${list.length} ${list.length === 1 ? 'avviso' : 'avvisi'}`;
  pill.setAttribute('aria-label', `${list.length} avvisi attivi, apri elenco`);
}

function renderHome() {
  const h = heating();
  const t = temperatures();
  const g = gates();
  const interval = currentInterval();
  const next = nextChange();
  const sensors = t?.sensors || [];
  const activeSensors = sensors.filter((s) => sensorState(s) !== 'stale').length;
  const staleSensors = sensors.filter((s) => sensorState(s) === 'stale').length;
  const trend = t?.trend || [];
  const delta = trend.length > 1 ? trend[trend.length - 1] - trend[0] : 0;
  const trendUp = delta >= 0;

  const progBadge = h.program.suspended
    ? `<span class="badge warn">Sospeso</span>`
    : !h.program.intervals.length
      ? `<span class="badge muted">Nessuno</span>`
      : interval
        ? `<span class="badge ok">Attivo</span>`
        : `<span class="badge muted">Fuori fascia</span>`;
  const progValue = h.program.suspended
    ? 'Sospeso'
    : interval
      ? `${fmt(interval.temperature)} °C`
      : h.program.intervals.length
        ? 'Spento'
        : 'Manuale';
  const shade = temperatureShade(h.temperature);

  document.getElementById('home-view').innerHTML = `
    <div class="actions">
      <button class="tile primary" data-action="open-gate" data-gate="automatic">${icon('gate')}<span>Automatico</span></button>
      <button class="tile" data-action="open-gate" data-gate="pedestrian">${icon('walk')}<span>Pedonale</span></button>
      <button class="tile" data-action="lights">${icon('sun')}<span>Luci</span></button>
      <button class="tile" data-action="window" data-hours="2">${icon('clock')}<span>Finestra</span><small>${g.open ? 'aperta' : '2 h'}</small></button>
    </div>

    <div class="home-grid">
      <section class="card hero"${shade ? ` style="background:${shade}"` : ''}>
        <div class="card-head">
          <div class="kicker">Temperatura operativa</div>
          <span class="badge muted">12 h</span>
        </div>
        <div class="temp-row"><span class="temp">${fmt(h.temperature)}</span><span class="unit">°C</span></div>
        <div class="trend-row">
          <span class="trend ${trendUp ? 'up' : 'down'}">${icon(trendUp ? 'arrow-up' : 'arrow-down')}${trendUp ? '+' : ''}${fmt(delta)} °C</span>
          <span>nelle ultime 12 ore</span>
        </div>
        <div class="spark">${trend.length > 1 ? sparkline(trend) : '<div class="spark-empty">Storico in raccolta</div>'}</div>
        <div class="strip">
          <div><span class="label ${h.boilerActive ? 'hot' : ''}">${icon('flame')} Caldaia</span><b>${h.boilerActive ? 'accesa' : 'spenta'}</b><span class="note">${h.boilerActive ? 'da ' + duration(h.boilerOnMinutes) : 'in attesa'}</span></div>
          <div><span class="label">${icon('snow')} Antigelo</span><b>${fmt(h.antifreezeThreshold)} °C</b><span class="note">${h.antifreezeEnabled ? 'soglia attiva' : 'disattivato'}</span></div>
          <div><span class="label">${icon('wifi')} Sensori</span><b>${activeSensors} / ${sensors.length}</b><span class="note">${staleSensors ? `${staleSensors} in ritardo` : 'tutti ok'}</span></div>
        </div>
      </section>

      <section class="card">
        <div class="card-head">
          <div><div class="kicker">Eventi</div><div class="value">Oggi</div></div>
          <button class="link" data-nav="events">Vedi tutti ${icon('chevron')}</button>
        </div>
        ${state.overview.events.length ? `<ul class="event-list compact">${state.overview.events.slice(0, 3).map(eventRow).join('')}</ul>` : `<div class="empty">${icon('bell')}<span>Nessun evento registrato.</span></div>`}
      </section>

      <section class="card">
        <div class="card-head">
          <div><div class="kicker">Programma</div><div class="value">${progValue}</div></div>
          ${progBadge}
        </div>
        ${timeline(h.program.intervals, true)}
        ${h.program.intervals
          .map(
            (i) =>
              `<div class="interval-row home-ival"><span class="interval-time">${minutesToTime(i.from)}–${minutesToTime(i.to)}</span><span class="interval-temp">${fmt(i.temperature)}°</span></div>`
          )
          .join('')}
        <div class="card-foot">${h.program.suspended ? 'Programma sospeso' : nextLabel(next)}</div>
      </section>

      <section class="card">
        <div class="card-head">
          <div><div class="kicker">Sensori</div><div class="value">${activeSensors} / ${sensors.length} attivi</div></div>
          <span class="badge ${staleSensors ? 'warn' : 'ok'}">${staleSensors ? `${staleSensors} in ritardo` : 'tutti ok'}</span>
        </div>
        ${sensors.length ? `<div class="sen-list">${sensors.map(sensorLine).join('')}</div>` : `<div class="empty">${icon('wifi')}<span>Nessun sensore configurato.</span></div>`}
      </section>
    </div>`;
}

function sensorLine(s) {
  return `<div class="sen ${sensorState(s)}"><span class="sen-name">${esc(s.location)}</span><span class="sen-val">${fmt(s.temperature)}°</span><span class="sen-age">${relative(s.timestamp)}</span></div>`;
}

function renderHeating() {
  const h = heating();
  if (!h) return;
  const t = temperatures();
  const interval = currentInterval();
  const next = nextChange();
  const rows = h.program.intervals
    .map(
      (i) =>
        `<div class="interval-row"><span class="interval-time">${minutesToTime(i.from)} - ${minutesToTime(i.to)}</span><span class="interval-duration">${((i.to - i.from) / 60).toFixed(1)} h</span><span class="interval-temp">${fmt(i.temperature)} °C</span></div>`
    )
    .join('');
  const mode = h.program.suspended ? 'Sospeso' : h.manual ? 'Manuale' : interval ? 'Programma' : 'In attesa';
  const antifreezeActive = h.antifreezeEnabled && h.temperature !== null && h.temperature < h.antifreezeThreshold;
  const heatingAlerts = alerts().filter((a) => a.target === 'heating');
  const rangeLabel =
    state.historyRange === 'today'
      ? 'Oggi'
      : state.historyRange === '7d'
        ? '7 giorni'
        : state.historyRange === '30d'
          ? 'Ultimo mese'
          : state.historyRange === '1y'
            ? '1 anno'
            : 'Personalizzato';

  document.getElementById('heating-view').innerHTML = `
    <div class="stack">
      <section class="card heating-hero">
        <div class="heating-top">
          <div>
            <div class="kicker">Riscaldamento</div>
            <div class="temp-row"><span class="temp">${fmt(h.temperature)}</span><span class="unit">°C</span></div>
            <div class="trend-row"><span>Operativa · banda ${fmt(h.bandMin)}–${fmt(h.bandMax)} °C</span></div>
          </div>
          <div class="heating-state">
            <span class="state-icon ${h.boilerActive ? 'on' : ''}">${icon('flame')}</span>
            <div class="heating-text"><b>Caldaia ${h.boilerActive ? 'accesa' : 'spenta'}</b><span>${mode} · ${h.boilerActive ? duration(h.boilerOnMinutes) : '--'}</span></div>
          </div>
        </div>
        <div class="button-row">
          <button class="btn ${h.boilerActive ? '' : 'primary'}" data-action="toggle-boiler">${icon(h.boilerActive ? 'power' : 'flame', h.boilerActive ? '' : 'fill')} ${h.boilerActive ? 'Spegni' : 'Accendi'}</button>
          <button class="btn" data-action="toggle-suspend" ${h.program.hasProgram ? '' : 'disabled'}>${icon(h.program.suspended ? 'play' : 'pause', h.program.suspended ? 'fill' : '')} ${h.program.suspended ? 'Riprendi programma' : 'Sospendi programma'}</button>
          <button class="btn" data-action="edit-program">${icon('edit')} Modifica programma</button>
          <button class="btn danger-ghost" data-action="delete-program" ${h.program.hasProgram ? '' : 'disabled'}>${icon('trash')} Rimuovi programma</button>
        </div>
        ${h.program.suspended ? `<div class="notice warn">Programma sospeso. Il riscaldamento è in controllo manuale.</div>` : ''}
        ${antifreezeActive ? `<div class="notice warn">Antigelo attivo: la caldaia resta accesa a prescindere dal programma.</div>` : ''}
      </section>

      <section class="card">
        <div class="card-head">
          <div><div class="kicker">Programma</div><div class="value">${h.program.intervals.length} fasce</div></div>
          <span class="badge ${h.program.suspended ? 'warn' : h.program.intervals.length ? 'ok' : 'muted'}">${h.program.suspended ? 'Sospeso' : h.program.intervals.length ? 'Attivo' : 'Nessuno'}</span>
        </div>
        ${timeline(h.program.intervals)}
        <div class="interval-list">${rows || '<div class="empty">' + icon('clock') + '<span>Nessuna fascia impostata.</span></div>'}</div>
        <div class="card-foot">${h.program.suspended ? 'Programma sospeso' : nextLabel(next)}</div>
      </section>

      <section class="card chart-card">
        <div class="card-head">
          <div><div class="kicker">Temperatura nel tempo</div><div class="value">${rangeLabel}</div></div>
          <div class="chart-range">
            <button class="chip-btn ${state.historyRange === 'today' ? 'active' : ''}" data-action="chart-range" data-range="today">Oggi</button>
            <button class="chip-btn ${state.historyRange === '7d' ? 'active' : ''}" data-action="chart-range" data-range="7d">7 giorni</button>
            <button class="chip-btn ${state.historyRange === '30d' ? 'active' : ''}" data-action="chart-range" data-range="30d">Ultimo mese</button>
            <button class="chip-btn ${state.historyRange === '1y' ? 'active' : ''}" data-action="chart-range" data-range="1y">1 anno</button>
            <button class="chip-btn ${state.historyRange === 'custom' ? 'active' : ''}" data-action="chart-range" data-range="custom">Personalizzato</button>
          </div>
        </div>
        <div class="chart-caption">Media oraria con minimo e massimo · sensori operativi</div>
        ${temperatureChart()}
      </section>

      <div class="two-col">
        <section class="card">
          <div class="card-head">
            <div><div class="kicker">Antigelo</div><div class="value">${antifreezeActive ? 'Attivo' : h.antifreezeEnabled ? 'Pronto' : 'Disattivato'}</div></div>
            <span class="badge ${antifreezeActive ? 'warn' : 'ok'}">soglia ${fmt(h.antifreezeThreshold)} °C</span>
          </div>
          <p class="muted" style="margin:10px 0 0;font-size:12px">Sotto la soglia la caldaia resta accesa anche con programma sospeso o spento manualmente.</p>
          <div class="sys-list">
            <div class="sys-row"><span>Stato</span><b>${antifreezeActive ? 'Forzatura attiva' : 'Sopra soglia'}</b></div>
            <div class="sys-row"><span>Isteresi</span><b>+0.5 °C</b></div>
            <div class="sys-row"><span>Sensore locale</span><b>${esc(t?.localStatus || '—')}</b></div>
          </div>
        </section>

        <section class="card">
          <div class="card-head">
            <div><div class="kicker">Avvisi</div><div class="value">${heatingAlerts.length ? `${heatingAlerts.length} ${heatingAlerts.length === 1 ? 'avviso' : 'avvisi'}` : 'Nessuno'}</div></div>
            <span class="badge ${heatingAlerts.length ? 'warn' : 'ok'}">${heatingAlerts.length ? 'attenzione' : 'tutto ok'}</span>
          </div>
          ${heatingAlerts.length ? `<div class="sys-list">${heatingAlerts.map((a) => `<div class="sys-row"><span>${esc(a.title)}</span><b style="color:var(--amber)">${esc(a.detail)}</b></div>`).join('')}</div>` : `<div class="empty">${icon('check')}<span>Nessun avviso attivo.</span></div>`}
        </section>
      </div>
    </div>`;
}

function renderGates() {
  const g = gates();
  const recent = g.recent || [];
  const stateBadge = g.open
    ? `<span class="badge ok">fino alle ${fmtTime(g.expiresAt)}</span>`
    : `<span class="badge">bloccati</span>`;

  document.getElementById('gates-view').innerHTML = `
    <div class="stack">
      <section class="card">
        <div class="card-head">
          <div><div class="kicker">Apri ora</div><div class="value">Accesso immediato</div></div>
          ${stateBadge}
        </div>
        <div class="gate-open-grid">
          <button class="btn gate gate-open" data-action="open-gate" data-gate="automatic">${icon('gate')} Cancello automatico</button>
          <button class="btn gate gate-open" data-action="open-gate" data-gate="pedestrian">${icon('walk')} Cancello pedonale</button>
        </div>
        <div class="card-foot">${g.open ? 'Finestra aperta: possono aprire anche i non admin.' : 'Cancelli bloccati: il pannello apre come admin.'}</div>
      </section>

      <section class="card gates-hero">
        <div class="gate-status">
          <span class="state-icon ${g.open ? 'ok' : 'locked'}">${icon(g.open ? 'unlock' : 'lock')}</span>
          <div><div class="kicker">Finestra</div><div class="value">${g.open ? 'Sbloccati' : 'Bloccati'}</div><div class="muted">${g.open ? `Accesso aperto fino alle ${fmtTime(g.expiresAt)}` : 'Solo admin e gate_controller'}</div></div>
        </div>
        <div class="window-buttons">
          ${[1, 2, 4, 8, 12, 24].map((h) => `<button class="btn" data-action="window" data-hours="${h}">${h} h</button>`).join('')}
        </div>
        <button class="btn danger" data-action="lock-gates" ${g.open ? '' : 'disabled'}>${icon('lock')} Blocca i cancelli</button>
      </section>

      <section class="card">
        <div class="card-head">
          <div><div class="kicker">Illuminazione</div><div class="value">Luci dei cancelli</div></div>
          <span class="badge muted">impulso breve</span>
        </div>
        <p class="muted" style="margin:0;font-size:12px">Accende le luci esterne per pochi istanti, indipendente dalla finestra di accesso.</p>
        <button class="btn" data-action="lights">${icon('sun')} Attiva luci</button>
      </section>

      <section class="card">
        <div class="card-head"><div><div class="kicker">Rate limit</div><div class="value">${g.rateMax} aperture</div></div><span class="badge">ogni ${g.rateWindow} min</span></div>
        <p class="muted" style="margin:10px 0 0;font-size:12px">Admin e gate_controller non sono mai limitati. Il conteggio parte alla conferma. Il pannello apre come admin.</p>
      </section>

      <section class="card">
        <div class="card-head"><div><div class="kicker">Accessi recenti</div><div class="value">Registro</div></div><span class="badge muted">${recent.length} eventi</span></div>
        ${recent.length ? `<div class="log-list">${recent.map((e) => `<div class="log-row"><span class="event-icon">${icon('gate')}</span><div class="log-main"><b>${esc(e.title)}</b><span>${esc(e.detail || '')}</span></div><span class="log-time">${fmtTime(e.at)}</span></div>`).join('')}</div>` : `<div class="empty">${icon('gate')}<span>Nessuna apertura registrata.</span></div>`}
      </section>
    </div>`;
}

function renderEvents() {
  const filters = [
    ['all', 'Tutti'],
    ['technical', 'Tecnico'],
    ['heating', 'Riscaldamento'],
    ['gates', 'Cancelli'],
    ['panel', 'Pannello'],
  ];
  document.getElementById('events-view').innerHTML = `
    <div class="filter-row">
      ${filters.map(([k, l]) => `<button class="chip-btn ${state.eventFilter === k ? 'active' : ''}" data-action="filter" data-filter="${k}">${l}</button>`).join('')}
    </div>
    <section class="card">
      <div class="card-head"><div><div class="kicker">Attività</div><div class="value">${state.events.length} eventi</div></div><span class="badge muted">ultimi 50</span></div>
      ${state.events.length ? `<ul class="event-list">${state.events.map(eventRow).join('')}</ul>` : `<div class="empty">${icon('bell')}<span>Nessun evento in questa categoria.</span></div>`}
    </section>`;
}

function renderSystem() {
  const o = state.overview;
  const health = o.health;
  const usage = o.usage;
  const sensors = o.temperatures.sensors;
  const stale = sensors.filter((s) => sensorState(s) === 'stale').length;
  const pct = usage.weeklyBudgetUsd > 0 ? Math.min(100, (usage.costUsd / usage.weeklyBudgetUsd) * 100) : 0;
  const update = o.update;

  document.getElementById('system-view').innerHTML = `
    <div class="stack">
      <div class="health-grid">
        <div class="stat"><div class="kicker">${icon('activity')} Telegram</div><b style="color:${health.telegramOk ? 'var(--green)' : 'var(--amber)'}">${health.telegramOk ? 'Connesso' : 'Non connesso'}</b><span>${health.telegramOk ? 'heartbeat attivo' : 'il bot resta operativo in locale'}</span></div>
        <div class="stat"><div class="kicker">${icon('db')} Database</div><b style="color:${health.databaseOk ? 'var(--green)' : 'var(--red)'}">${health.databaseOk ? 'Scrivibile' : 'Non scrivibile'}</b><span>db.json · registro ${health.panelStoreOk ? 'ok' : 'non disponibile'}</span></div>
        <div class="stat"><div class="kicker">${icon('cpu')} CPU</div><b>${health.cpuTemperatureCelsius ? fmt(health.cpuTemperatureCelsius) + ' °C' : '—'}</b><span>throttled: ${esc(health.throttled || 'sconosciuto')}</span></div>
        <div class="stat"><div class="kicker">${icon('clock')} Uptime</div><b>${uptime(o.system.uptimeSeconds)}</b><span>versione ${esc(o.system.version)}</span></div>
      </div>

      <section class="card">
        <div class="card-head"><div><div class="kicker">OpenCode Go · ultimi 7 giorni</div><div class="value">$${usage.costUsd.toFixed(2)} <span class="muted" style="font-size:14px;font-weight:400">/ $${usage.weeklyBudgetUsd}</span></div></div><span class="badge ${pct > 80 ? 'warn' : 'ok'}">${pct.toFixed(0)}% budget</span></div>
        <div class="usage-bar"><i style="width:${pct}%"></i></div>
        <div class="usage-meta"><span>${integer(usage.calls)} chiamate</span><span>${(usage.tokens / 1000).toFixed(1)}k token · ${(usage.cachedTokens / 1000).toFixed(1)}k da cache</span></div>
      </section>

      <section class="card">
        <div class="card-head"><div><div class="kicker">Sensori</div><div class="value">${sensors.length} configurati</div></div><span class="badge ${stale ? 'warn' : 'ok'}">${stale ? `${stale} in ritardo` : 'tutti ok'}</span></div>
        <div class="sensor-table">
          ${sensors
            .map(
              (s) =>
                `<div class="sensor-row"><span class="name">${esc(s.location)}</span><span class="temp">${fmt(s.temperature)} °C</span><span>${s.informational ? '<span class="badge muted">informativo</span>' : s.fresh ? '<span class="badge ok">ok</span>' : '<span class="badge warn">in ritardo</span>'}</span><span class="updated">${relative(s.timestamp)}</span></div>`
            )
            .join('') || `<div class="empty">${icon('wifi')}<span>Nessun sensore configurato.</span></div>`}
        </div>
      </section>

      <div class="sys-grid">
        <section class="card">
          <div class="card-head"><div><div class="kicker">Sistema</div><div class="value">PodereBot</div></div><span class="badge ${health.coreOk ? 'ok' : 'bad'}">${health.coreOk ? 'core attivo' : 'degradato'}</span></div>
          <div class="sys-list">
            <div class="sys-row"><span>Uptime</span><b>${uptime(o.system.uptimeSeconds)}</b></div>
            <div class="sys-row"><span>Memoria</span><b>${o.system.memoryMb} MB</b></div>
            <div class="sys-row"><span>Versione</span><b>${esc(o.system.version)}</b></div>
            <div class="sys-row"><span>Ambiente</span><b>${esc(o.system.environment)}</b></div>
            <div class="sys-row"><span>Sensore locale</span><b>${esc(o.temperatures.localStatus)}</b></div>
          </div>
        </section>
        <section class="card">
          <div class="card-head"><div><div class="kicker">Aggiornamento</div><div class="value">Automatico</div></div><span class="badge ${update.result === 'updated' ? 'ok' : update.result === 'failed' || update.result === 'rolled-back' ? 'bad' : 'muted'}">${esc(update.result || 'mai eseguito')}</span></div>
          <div class="sys-list">
            <div class="sys-row"><span>Canale</span><b>release</b></div>
            <div class="sys-row"><span>Ultimo controllo</span><b>${update.lastRunUtc ? relative(update.lastRunUtc) : '—'}</b></div>
            <div class="sys-row"><span>Versione</span><b>${esc(update.toSha || o.system.version)}</b></div>
            <div class="sys-row"><span>Dettaglio</span><b>${esc(update.reason || '—')}</b></div>
          </div>
          <div class="button-row"><button class="btn ghost" data-action="refresh">${icon('refresh')} Aggiorna dati</button></div>
        </section>
      </div>
    </div>`;
}

function renderUsers() {
  const users = [...state.overview.users].sort((a, b) => {
    const aa = a.isAdmin ? 0 : 1;
    const bb = b.isAdmin ? 0 : 1;
    return aa - bb || (a.name || String(a.id)).localeCompare(b.name || String(b.id), 'it');
  });
  const admins = users.filter((u) => u.isAdmin).length;
  const gateControllers = users.filter((u) => u.isGateController).length;
  const countTopic = (topic) => users.filter((u) => u.topics.includes(topic)).length;

  document.getElementById('users-view').innerHTML = `
    <div class="stack">
      <section class="card">
        <div class="card-head user-view-head">
          <div><div class="kicker">Utenti</div><div class="value">${users.length} configurati</div></div>
          <button class="btn primary" data-action="add-user">${icon('plus')} Aggiungi utente</button>
        </div>
        <div class="user-stats">
          <div><b>${users.length}</b><span>utenti</span></div>
          <div><b>${admins}</b><span>admin</span></div>
          <div><b>${gateControllers}</b><span>gate controller</span></div>
          <div><b>${countTopic('technical')}</b><span>tecnico</span></div>
          <div><b>${countTopic('heating')}</b><span>riscaldamento</span></div>
          <div><b>${countTopic('gates')}</b><span>cancelli</span></div>
        </div>
      </section>

      <div class="user-grid">${users.map(userCard).join('')}</div>

      <section class="card">
        <div class="card-head"><div><div class="kicker">Come funziona</div><div class="value">Ruoli e topic</div></div></div>
        <div class="legend-grid">
          <div class="legend-item"><b>Admin</b><span>Gestisce bot, utenti e programma. Non è limitato.</span></div>
          <div class="legend-item"><b>Gate controller</b><span>Apre i cancelli senza rate limit. Implicito per gli admin.</span></div>
          <div class="legend-item"><b>Tecnico</b><span>Riceve sensori e salute del sistema.</span></div>
          <div class="legend-item"><b>Riscaldamento</b><span>Riceve caldaia e temperatura.</span></div>
          <div class="legend-item"><b>Cancelli</b><span>Riceve aperture e finestra di accesso.</span></div>
        </div>
      </section>
    </div>`;
}

function userCard(u) {
  const label = u.name || 'ID ' + u.id;
  const lastAdmin = u.isAdmin && state.overview.users.filter((x) => x.isAdmin).length <= 1;

  return `<section class="card user-card">
    <div class="user-head">
      <span class="avatar">${u.name ? esc(initials(u.name)) : icon('users')}</span>
      <div class="user-main"><b>${esc(label)}</b><span>${u.name ? 'ID ' + u.id : 'nome non disponibile'}</span></div>
      <button class="icon-btn" data-action="edit-name" data-user="${u.id}" aria-label="Modifica il nome di ${esc(label)}" title="Modifica nome">${icon('edit')}</button>
      <button class="icon-btn danger" data-action="remove-user" data-user="${u.id}" aria-label="Rimuovi ${esc(label)}" title="${lastAdmin ? 'Serve almeno un admin' : ''}">${icon('trash')}</button>
    </div>
    <div class="user-section">
      <div class="kicker">Ruoli</div>
      <div class="role-row">
        <button class="role-btn admin ${u.isAdmin ? 'on' : ''}" data-action="toggle-role" data-user="${u.id}" data-role="admin" aria-pressed="${u.isAdmin}" title="${lastAdmin ? 'Serve almeno un admin' : ''}">${icon(u.isAdmin ? 'check' : 'plus')} Admin</button>
        <button class="role-btn gate ${u.isGateController && !u.isAdmin ? 'on' : ''}" data-action="toggle-role" data-user="${u.id}" data-role="gate_controller" aria-pressed="${u.isGateController}" ${u.isAdmin ? 'disabled title="Implicito per gli admin"' : ''}>${icon(u.isGateController ? 'check' : 'plus')} Gate controller${u.isAdmin ? ' · implicito' : ''}</button>
      </div>
    </div>
    <div class="user-section">
      <div class="kicker">Topic</div>
      <div class="topic-row">${Object.entries(TOPICS)
        .map(
          ([key, meta]) =>
            `<button class="topic-chip ${u.topics.includes(key) ? 'on' : ''}" data-action="toggle-topic" data-user="${u.id}" data-topic="${key}" aria-pressed="${u.topics.includes(key)}">${icon(meta.icon)} ${meta.label}</button>`
        )
        .join('')}</div>
    </div>
  </section>`;
}

function renderDevices() {
  const devices = state.overview.devices || [];
  const active = devices.filter((d) => d.state === 'active').length;

  document.getElementById('devices-view').innerHTML = `
    <div class="stack">
      <section class="card">
        <div class="card-head user-view-head">
          <div><div class="kicker">Dispositivi</div><div class="value">${devices.length} ${devices.length === 1 ? 'autorizzato' : 'autorizzati'} · ${active} ${active === 1 ? 'attivo' : 'attivi'}</div></div>
          <button class="btn primary" data-action="add-device">${icon('plus')} Aggiungi dispositivo</button>
        </div>
        <p class="muted" style="margin:10px 0 0;font-size:12px">Ogni dispositivo entra con un link monouso mostrato come QR code. Il nome è un'etichetta: l'identità è la chiave consegnata al dispositivo. Gli indirizzi IP e i nomi di rete non vengono usati.</p>
      </section>

      ${devices.length ? `<div class="device-grid">${devices.map(deviceCard).join('')}</div>` : `<section class="card"><div class="empty">${icon('device')}<span>Nessun dispositivo. Aggiungine uno per provare il pannello da un tablet o un telefono.</span></div></section>`}

      <section class="card">
        <div class="card-head"><div><div class="kicker">Come funziona</div><div class="value">Pairing</div></div></div>
        <div class="legend-grid">
          <div class="legend-item"><b>Link monouso</b><span>Vale ${CONFIG.mode === 'kiosk' ? '10 minuti' : '10 minuti'} e una sola volta. Il QR viene mostrato una volta sola.</span></div>
          <div class="legend-item"><b>Sessione</b><span>Il dispositivo riceve un cookie HttpOnly. Revocandolo, il cookie smette di funzionare.</span></div>
          <div class="legend-item"><b>Host stabile</b><span>Il link usa l'indirizzo LAN del Pi. Se cambia, serve un nuovo pairing.</span></div>
        </div>
      </section>
    </div>`;
}

function deviceCard(d) {
  const meta = DEVICE_STATES[d.state] || { label: d.state, cls: 'muted' };
  const action = d.state === 'active'
    ? ''
    : `<button class="btn ghost" data-action="refresh-device" data-device="${d.id}">${icon('link')} Nuovo link</button>`;

  return `<section class="card device-card">
    <div class="user-head">
      <span class="avatar">${icon('device')}</span>
      <div class="user-main"><b>${esc(d.name)}</b><span>${d.createdBy ? 'creato da ' + esc(d.createdBy) : 'creato dal pannello'}</span></div>
      <span class="badge ${meta.cls}">${meta.label}</span>
    </div>
    <div class="sys-list">
      <div class="sys-row"><span>Ultimo accesso</span><b>${d.lastSeenAt ? relative(d.lastSeenAt) : 'mai'}</b></div>
      <div class="sys-row"><span>Creato</span><b>${fmtDate(d.createdAt)} ${fmtTime(d.createdAt)}</b></div>
      ${d.pairingExpiresAt ? `<div class="sys-row"><span>Link valido fino</span><b>${fmtTime(d.pairingExpiresAt)}</b></div>` : ''}
    </div>
    <div class="button-row">
      ${action}
      <button class="btn danger-ghost" data-action="revoke-device" data-device="${d.id}" data-name="${esc(d.name)}">${icon('trash')} Revoca</button>
    </div>
  </section>`;
}

function eventRow(e) {
  const meta = EVENT_TOPICS[e.topic] || { icon: 'activity' };
  const cls = e.topic === 'heating' ? 'heating' : e.topic === 'gates' ? 'gates' : e.topic === 'panel' ? 'panel' : 'technical';
  const strip = (value) =>
    String(value ?? '')
      .replace(/[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE0F}\u{2B00}-\u{2BFF}]/gu, '')
      .replace(/\s+/g, ' ')
      .trim();
  return `<li class="event ${cls}">
    <span class="event-icon">${icon(meta.icon, e.topic === 'heating' ? 'fill' : '')}</span>
    <span class="event-main"><b>${esc(strip(e.title))}</b><span>${esc(strip(e.detail))}</span></span>
    <span class="event-time">${relative(e.at)}</span>
  </li>`;
}

/* ---------- chart ---------- */

function sparkline(values) {
  const w = 240;
  const h = 56;
  const pad = 4;
  const min = Math.min(...values) - 0.3;
  const max = Math.max(...values) + 0.3;
  const range = max - min || 1;
  const points = values.map((v, i) => {
    const x = pad + (i / (values.length - 1)) * (w - pad * 2);
    const y = h - pad - ((v - min) / range) * (h - pad * 2);
    return [x, y];
  });
  const line = points.map((p) => p.join(',')).join(' ');
  const area = `${pad},${h - pad} ${line} ${w - pad},${h - pad}`;
  const last = points[points.length - 1];
  return `<svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="none" aria-hidden="true">
    <polygon points="${area}" fill="currentColor" fill-opacity=".08"></polygon>
    <polyline points="${line}" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" vector-effect="non-scaling-stroke"></polyline>
  </svg><span class="spark-dot" style="left:${(last[0] / w) * 100}%;top:${(last[1] / h) * 100}%"></span>`;
}

function timeline(intervals, compact = false) {
  if (!intervals.length) return `<div class="notice">Nessun programma impostato.</div>`;
  const blocks = intervals
    .map((i) => {
      const left = (i.from / 1440) * 100;
      const width = ((i.to - i.from) / 1440) * 100;
      return `<div class="tl-block" style="left:${left}%;width:${width}%" title="${minutesToTime(i.from)} - ${minutesToTime(i.to)} · ${fmt(i.temperature)} °C"></div>`;
    })
    .join('');
  const nowMin = state.now.getHours() * 60 + state.now.getMinutes();
  return `<div class="timeline ${compact ? 'compact' : ''}">
    <div class="tl-track">${blocks}<div class="tl-now" style="left:${(nowMin / 1440) * 100}%"></div></div>
    <div class="tl-ticks"><span>00</span><span>06</span><span>12</span><span>18</span><span>24</span></div>
  </div>`;
}

function temperatureChart() {
  const data = state.history;
  if (!data) return `<div class="empty">${icon('activity')}<span>Carico lo storico…</span></div>`;
  if (data.error) return `<div class="empty">${icon('alert')}<span>${esc(data.error)}</span></div>`;

  const points = (data.points || []).map((p) => ({
    at: new Date(p.at),
    avg: p.avg,
    min: p.min,
    max: p.max,
    samples: p.samples,
  }));
  if (points.length < 2) {
    return `<div class="empty">${icon('activity')}<span>Nessun dato per questo periodo.</span></div>`;
  }

  const lows = points.map((p) => p.min);
  const highs = points.map((p) => p.max);
  const min = Math.min(...lows);
  const max = Math.max(...highs);
  const avg = points.reduce((sum, p) => sum + p.avg, 0) / points.length;
  let yMin = Math.floor((min - 1) * 2) / 2;
  let yMax = Math.ceil((max + 1) * 2) / 2;
  if (yMax - yMin < 2) {
    yMin -= 1;
    yMax += 1;
  }

  const W = 600;
  const H = 200;
  const t0 = points[0].at.getTime();
  const t1 = points[points.length - 1].at.getTime();
  const span = t1 - t0 || 1;
  const x = (at) => ((at.getTime() - t0) / span) * W;
  const y = (value) => H - ((value - yMin) / (yMax - yMin)) * H;

  const upper = points.map((p) => `${x(p.at).toFixed(1)},${y(p.max).toFixed(1)}`);
  const lower = points
    .slice()
    .reverse()
    .map((p) => `${x(p.at).toFixed(1)},${y(p.min).toFixed(1)}`);
  const band = `M${upper.join(' L')} L${lower.join(' L')} Z`;
  const line = points.map((p, i) => `${i === 0 ? 'M' : 'L'}${x(p.at).toFixed(1)},${y(p.avg).toFixed(1)}`).join(' ');

  const grid = [0, 1, 2, 3]
    .map(
      (i) =>
        `<line x1="0" y1="${(H / 3) * i}" x2="${W}" y2="${(H / 3) * i}" stroke="rgba(255,255,255,.06)" stroke-width="1" vector-effect="non-scaling-stroke"/>`
    )
    .join('');

  const yTicks = [yMax, yMax - (yMax - yMin) / 3, yMax - (2 * (yMax - yMin)) / 3, yMin];
  const yLabels = yTicks.map((v) => `<span>${v.toFixed(1).replace('.0', '')}°</span>`).join('');

  const tickCount = data.bucket === 'day' ? 4 : 5;
  const xLabels = [];
  for (let i = 0; i < tickCount; i += 1) {
    const at = new Date(t0 + (span * i) / (tickCount - 1));
    xLabels.push(data.bucket === 'day' ? fmtDate(at) : fmtTime(at));
  }

  const last = points[points.length - 1];
  const summary = `<div class="chart-summary"><span>Min <b>${fmt(min)}°</b></span><span>Max <b>${fmt(max)}°</b></span><span>Media <b>${fmt(avg)}°</b></span><span>${points.length} ${data.bucket === 'day' ? 'giorni' : 'ore'}</span></div>`;

  return `<div class="chart">
    <div class="chart-y">${yLabels}</div>
    <div class="chart-plot">
      <svg viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" aria-hidden="true">
        ${grid}
        <path d="${band}" fill="currentColor" fill-opacity=".10"/>
        <path d="${line}" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" vector-effect="non-scaling-stroke"/>
      </svg>
      <span class="chart-dot" style="left:${(x(last.at) / W) * 100}%;top:${(y(last.avg) / H) * 100}%"></span>
    </div>
  </div>
  <div class="chart-x">${xLabels.map((l) => `<span>${l}</span>`).join('')}</div>
  ${summary}`;
}

/* ---------- sheet ---------- */

let confirmAction = null;
let sheetTimer = null;
let toastTimer = null;
let toastHide = null;
let qrTimer = null;

function toast(message) {
  const el = document.getElementById('toast');
  clearTimeout(toastTimer);
  clearTimeout(toastHide);
  el.textContent = message;
  el.hidden = false;
  requestAnimationFrame(() => el.classList.add('show'));
  toastTimer = setTimeout(() => {
    el.classList.remove('show');
    toastHide = setTimeout(() => {
      el.hidden = true;
    }, 180);
  }, 2600);
}

function isSheetOpen() {
  return !document.getElementById('sheet').hidden;
}

function showSheet() {
  const sheet = document.getElementById('sheet');
  const backdrop = document.getElementById('backdrop');
  clearTimeout(sheetTimer);
  sheet.hidden = false;
  backdrop.hidden = false;
  document.body.classList.add('sheet-open');
  requestAnimationFrame(() => {
    sheet.classList.add('open');
    backdrop.classList.add('open');
  });
  const focus = sheet.querySelector('.btn.primary, .btn.danger, input');
  if (focus) {
    setTimeout(() => {
      if (sheet.classList.contains('open')) focus.focus();
    }, 240);
  }
}

function closeSheet() {
  const sheet = document.getElementById('sheet');
  const backdrop = document.getElementById('backdrop');
  sheet.classList.remove('open');
  backdrop.classList.remove('open');
  document.body.classList.remove('sheet-open');
  confirmAction = null;
  clearTimeout(sheetTimer);
  clearTimeout(qrTimer);
  qrTimer = null;
  sheetTimer = setTimeout(() => {
    sheet.hidden = true;
    backdrop.hidden = true;
    document.getElementById('sheet-content').innerHTML = '';
    document.getElementById('sheet-actions').innerHTML = '';
  }, 240);
}

function openConfirm({ title, body, label = 'Conferma', danger = false, onConfirm }) {
  confirmAction = onConfirm;
  document.getElementById('sheet-title').textContent = title;
  document.getElementById('sheet-body').textContent = body;
  document.getElementById('sheet-content').innerHTML = '';
  document.getElementById('sheet-actions').innerHTML = `<button class="btn ghost" data-action="close-sheet">Annulla</button><button class="btn ${danger ? 'danger' : 'primary'}" data-action="confirm">${label}</button>`;
  showSheet();
}

function openAlerts() {
  const list = alerts();
  if (!list.length) return;
  document.getElementById('sheet-title').textContent = 'Avvisi attivi';
  document.getElementById('sheet-body').textContent = `${list.length} ${list.length === 1 ? 'condizione richiede' : 'condizioni richiedono'} attenzione.`;
  document.getElementById('sheet-content').innerHTML = `<div class="alert-list">${list
    .map(
      (a) =>
        `<div class="alert-row"><span class="alert-icon">${icon('alert')}</span><div class="alert-main"><b>${esc(a.title)}</b><span>${esc(a.detail)}</span></div><button class="btn ghost" data-action="go" data-target="${a.target}">Apri</button></div>`
    )
    .join('')}</div>`;
  document.getElementById('sheet-actions').innerHTML = `<button class="btn ghost" data-action="close-sheet">Chiudi</button>`;
  confirmAction = null;
  showSheet();
}

/* ---------- heating actions ---------- */

async function toggleBoiler() {
  const h = heating();
  try {
    await write(h.boilerActive ? '/heating/off' : '/heating/on', { method: 'POST' });
    toast(h.boilerActive ? 'Caldaia spenta' : 'Caldaia accesa');
  } catch (error) {
    toast(error.message);
  }
}

async function toggleSuspend() {
  const h = heating();
  try {
    await write('/heating/suspended', { method: 'PUT', body: { suspended: !h.program.suspended } });
    toast(h.program.suspended ? 'Programma ripreso' : 'Programma sospeso');
  } catch (error) {
    toast(error.message);
  }
}

function confirmDeleteProgram() {
  openConfirm({
    title: 'Rimuovere il programma?',
    body: 'La caldaia smetterà di seguire un orario fino a un nuovo programma. Le fasce salvate vengono eliminate.',
    label: 'Rimuovi',
    danger: true,
    onConfirm: async () => {
      try {
        await write('/heating/program', { method: 'DELETE' });
        toast('Programma rimosso');
      } catch (error) {
        toast(error.message);
      }
    },
  });
}

function editorRow(from, to, temperature) {
  return `<div class="editor-row">
    <label class="field"><span>Dalle</span><input class="from" type="time" value="${minutesToTime(from)}"></label>
    <label class="field"><span>Alle</span><input class="to" type="time" value="${minutesToTime(to)}"></label>
    <label class="field temp"><span>°C</span><input class="temp" type="number" min="5" max="30" step="0.5" value="${temperature}"></label>
    <button class="icon-btn danger" data-action="remove-interval" aria-label="Rimuovi fascia">${icon('trash')}</button>
  </div>`;
}

function openProgramEditor() {
  const h = heating();
  const rows = h.program.intervals.map((i) => editorRow(i.from, i.to, i.temperature)).join('');
  document.getElementById('sheet-title').textContent = 'Modifica programma';
  document.getElementById('sheet-body').textContent =
    'Imposta le fasce orarie e la temperatura. Le fasce non possono sovrapporsi e devono iniziare prima di finire.';
  document.getElementById('sheet-content').innerHTML = `<div class="editor" id="editor">${rows}</div><button class="btn ghost add-row" data-action="add-interval">${icon('plus')} Aggiungi fascia</button><p class="form-error" id="editor-error"></p>`;
  document.getElementById('sheet-actions').innerHTML = `<button class="btn ghost" data-action="close-sheet">Annulla</button><button class="btn primary" data-action="save-program">Salva</button>`;
  confirmAction = null;
  showSheet();
}

function addIntervalRow() {
  const editor = document.getElementById('editor');
  if (!editor) return;
  editor.insertAdjacentHTML('beforeend', editorRow(480, 600, 20));
}

async function saveProgram() {
  const rows = Array.from(document.querySelectorAll('#editor .editor-row'));
  const error = document.getElementById('editor-error');
  error.textContent = '';
  const intervals = [];

  for (const row of rows) {
    const from = timeToMinutes(row.querySelector('input.from').value);
    const to = timeToMinutes(row.querySelector('input.to').value);
    const temperature = Number(row.querySelector('input.temp').value);
    if (from == null || to == null) {
      error.textContent = 'Completa tutti gli orari.';
      return;
    }
    if (from >= to) {
      error.textContent = 'Ogni fascia deve iniziare prima di finire.';
      return;
    }
    if (!(temperature >= 5 && temperature <= 30)) {
      error.textContent = 'La temperatura deve stare tra 5 e 30 °C.';
      return;
    }
    intervals.push({ from, to, temperature });
  }

  if (!intervals.length) {
    error.textContent = 'Aggiungi almeno una fascia.';
    return;
  }

  intervals.sort((a, b) => a.from - b.from);
  for (let i = 1; i < intervals.length; i += 1) {
    if (intervals[i].from < intervals[i - 1].to) {
      error.textContent = 'Le fasce non possono sovrapporsi.';
      return;
    }
  }

  try {
    await write('/heating/program', { method: 'PUT', body: { intervals } });
    closeSheet();
    toast('Programma aggiornato');
  } catch (e) {
    error.textContent = e.message;
  }
}

/* ---------- gate actions ---------- */

function confirmOpenGate(gate) {
  const name = gate === 'pedestrian' ? 'pedonale' : 'automatico';
  const g = gates();
  openConfirm({
    title: `Aprire il cancello ${name}?`,
    body: g.open
      ? 'Il cancello si apre per pochi secondi. Confermi?'
      : 'I cancelli sono bloccati, ma il pannello opera come admin e può aprire comunque.',
    label: 'Apri',
    onConfirm: async () => {
      try {
        await write('/gates/open', { method: 'POST', body: { gate } });
        toast(`Cancello ${name} aperto`);
      } catch (error) {
        toast(error.message);
      }
    },
  });
}

function confirmWindow(hours) {
  openConfirm({
    title: `Sbloccare i cancelli per ${hours} ${hours === 1 ? 'ora' : 'ore'}?`,
    body: 'Durante la finestra anche gli utenti non admin possono aprire i cancelli, nel rispetto del rate limit.',
    label: 'Sblocca',
    onConfirm: async () => {
      try {
        await write('/gates/window', { method: 'PUT', body: { hours } });
        toast('Cancelli sbloccati');
      } catch (error) {
        toast(error.message);
      }
    },
  });
}

function confirmLockGates() {
  openConfirm({
    title: 'Bloccare i cancelli?',
    body: 'Gli utenti non admin non potranno più aprire. Admin e gate_controller restano operativi.',
    label: 'Blocca',
    danger: true,
    onConfirm: async () => {
      try {
        await write('/gates/window', { method: 'DELETE' });
        toast('Cancelli bloccati');
      } catch (error) {
        toast(error.message);
      }
    },
  });
}

async function toggleLights() {
  try {
    await write('/gates/lights', { method: 'POST' });
    toast('Luci dei cancelli attivate');
  } catch (error) {
    toast(error.message);
  }
}

/* ---------- user actions ---------- */

function openAddUser() {  document.getElementById('sheet-title').textContent = 'Aggiungi utente';
  document.getElementById('sheet-body').textContent = "Inserisci l'ID Telegram e scegli ruoli e topic.";
  document.getElementById('sheet-content').innerHTML = `<div class="user-form">
    <label class="field"><span>ID Telegram</span><input id="new-user-id" type="text" inputmode="numeric" autocomplete="off" placeholder="123456789"></label>
    <label class="field"><span>Nome</span><input id="new-user-name" type="text" autocomplete="off" placeholder="Matteo"></label>
    <div class="form-block"><span class="kicker">Ruoli</span>
      <label class="check"><input type="checkbox" id="new-admin"> Admin</label>
      <label class="check"><input type="checkbox" id="new-gate"> Gate controller</label>
    </div>
    <div class="form-block"><span class="kicker">Topic</span>
      <label class="check"><input type="checkbox" id="new-technical"> Tecnico</label>
      <label class="check"><input type="checkbox" id="new-heating"> Riscaldamento</label>
      <label class="check"><input type="checkbox" id="new-gates"> Cancelli</label>
    </div>
    <p class="form-error" id="user-error"></p>
  </div>`;
  document.getElementById('sheet-actions').innerHTML = `<button class="btn ghost" data-action="close-sheet">Annulla</button><button class="btn primary" data-action="save-user">Aggiungi</button>`;
  const admin = document.getElementById('new-admin');
  admin.addEventListener('change', () => {
    if (!admin.checked) return;
    ['new-technical', 'new-heating', 'new-gates'].forEach((id) => {
      const el = document.getElementById(id);
      if (el) el.checked = true;
    });
  });
  confirmAction = null;
  showSheet();
}

async function saveUser() {
  const idInput = document.getElementById('new-user-id');
  if (!idInput) return;
  const error = document.getElementById('user-error');
  error.textContent = '';
  const rawId = idInput.value.replace(/\D/g, '');
  const id = Number(rawId);
  if (!rawId || !Number.isSafeInteger(id) || id <= 0) {
    error.textContent = 'Inserisci un ID Telegram numerico.';
    return;
  }
  if (state.overview.users.some((u) => u.id === id)) {
    error.textContent = 'Questo ID è già nella lista.';
    return;
  }

  const name = document.getElementById('new-user-name').value.trim();
  const entitlements = [];
  if (document.getElementById('new-admin').checked) entitlements.push('admin');
  if (document.getElementById('new-gate').checked) entitlements.push('gate_controller');
  const topics = [];
  if (document.getElementById('new-technical').checked) topics.push('technical');
  if (document.getElementById('new-heating').checked) topics.push('heating');
  if (document.getElementById('new-gates').checked) topics.push('gates');

  try {
    await write('/users', { method: 'POST', body: { id, name, entitlements, topics } });
    closeSheet();
    toast('Utente aggiunto');
  } catch (e) {
    error.textContent = e.message;
  }
}

function openEditName(userId) {
  const user = state.overview.users.find((u) => u.id === userId);
  if (!user) return;

  document.getElementById('sheet-title').textContent = 'Nome utente';
  document.getElementById('sheet-body').textContent =
    "Il nome è un'etichetta del pannello. Se lo lasci vuoto provo a leggerlo da Telegram.";
  document.getElementById('sheet-content').innerHTML = `<div class="user-form">
    <label class="field"><span>Nome</span><input id="name-input" data-user="${user.id}" type="text" maxlength="80" autocomplete="off" value="${esc(user.name || '')}" placeholder="Es. Matteo"></label>
    <p class="form-error" id="name-error"></p>
  </div>`;
  document.getElementById('sheet-actions').innerHTML = `<button class="btn ghost" data-action="close-sheet">Annulla</button><button class="btn primary" data-action="save-name">Salva</button>`;
  confirmAction = null;
  showSheet();
}

async function saveName() {
  const input = document.getElementById('name-input');
  const error = document.getElementById('name-error');
  if (!input || !error) return;
  error.textContent = '';

  const id = Number(input.dataset.user);
  const user = state.overview.users.find((u) => u.id === id);
  if (!user) return;

  try {
    await write('/users/' + id, {
      method: 'PATCH',
      body: { name: input.value.trim(), entitlements: user.entitlements, topics: user.topics },
    });
    closeSheet();
    toast('Nome aggiornato');
  } catch (e) {
    error.textContent = e.message;
  }
}

async function toggleRole(userId, role) {
  const user = state.overview.users.find((u) => u.id === userId);
  if (!user) return;
  const has = user.entitlements.includes(role);
  const entitlements = has ? user.entitlements.filter((r) => r !== role) : [...user.entitlements, role];
  if (role === 'admin' && has && state.overview.users.filter((u) => u.isAdmin).length <= 1) {
    toast('Serve almeno un admin');
    return;
  }
  if (role === 'gate_controller' && user.isAdmin) {
    toast('Gate controller è implicito per gli admin');
    return;
  }

  // Optimistic: the switch flips immediately, the server confirms right after.
  const before = {
    entitlements: [...user.entitlements],
    isAdmin: user.isAdmin,
    isGateController: user.isGateController,
  };
  user.entitlements = entitlements;
  user.isAdmin = entitlements.includes('admin');
  user.isGateController = user.isAdmin || entitlements.includes('gate_controller');
  renderUsers();

  try {
    await write('/users/' + userId, { method: 'PATCH', body: { name: user.name, entitlements, topics: user.topics } });
    toast(`${ROLES[role].label} ${has ? 'revocato' : 'concesso'}`);
  } catch (error) {
    Object.assign(user, before);
    renderUsers();
    toast(error.message);
  }
}

async function toggleTopic(userId, topic) {
  const user = state.overview.users.find((u) => u.id === userId);
  if (!user) return;
  const has = user.topics.includes(topic);
  const topics = has ? user.topics.filter((t) => t !== topic) : [...user.topics, topic];

  const before = [...user.topics];
  user.topics = topics;
  renderUsers();

  try {
    await write('/users/' + userId, { method: 'PATCH', body: { name: user.name, entitlements: user.entitlements, topics } });
    toast(`Topic ${TOPICS[topic].label} ${has ? 'disattivato' : 'attivato'}`);
  } catch (error) {
    user.topics = before;
    renderUsers();
    toast(error.message);
  }
}

function confirmRemoveUser(userId) {
  const user = state.overview.users.find((u) => u.id === userId);
  if (!user) return;
  const label = user.name || 'ID ' + user.id;
  if (user.isAdmin && state.overview.users.filter((u) => u.isAdmin).length <= 1) {
    toast('Serve almeno un admin');
    return;
  }
  openConfirm({
    title: `Rimuovere ${label}?`,
    body: user.isAdmin
      ? 'Non riceverà più notifiche, non potrà usare i comandi e perderà i permessi di admin.'
      : 'Non riceverà più notifiche e non potrà usare i comandi.',
    label: 'Rimuovi',
    danger: true,
    onConfirm: async () => {
      try {
        await write('/users/' + userId, { method: 'DELETE' });
        toast('Utente rimosso');
      } catch (error) {
        toast(error.message);
      }
    },
  });
}

/* ---------- device actions ---------- */

function openAddDevice() {
  document.getElementById('sheet-title').textContent = 'Aggiungi dispositivo';
  document.getElementById('sheet-body').textContent =
    'Dai un nome al dispositivo. Il server genera un link monouso e lo mostra come QR code: vale una sola volta.';
  document.getElementById('sheet-content').innerHTML = `<div class="user-form">
    <label class="field"><span>Nome dispositivo</span><input id="device-name" type="text" maxlength="60" autocomplete="off" placeholder="iPad cucina"></label>
    <p class="form-error" id="device-error"></p>
  </div>`;
  document.getElementById('sheet-actions').innerHTML = `<button class="btn ghost" data-action="close-sheet">Annulla</button><button class="btn primary" data-action="create-device">Genera QR</button>`;
  confirmAction = null;
  showSheet();
}

async function createDevice(id) {
  const error = document.getElementById('device-error');
  if (!error) return;
  error.textContent = '';

  if (id) {
    try {
      const ticket = await api('/devices/' + id + '/pair', { method: 'POST' });
      showPairing(ticket);
      loadDevices();
    } catch (e) {
      error.textContent = e.message;
    }
    return;
  }

  const nameInput = document.getElementById('device-name');
  if (!nameInput) return;
  const name = nameInput.value.trim();
  if (!name) {
    error.textContent = 'Il nome è obbligatorio.';
    return;
  }

  try {
    const ticket = await api('/devices/pair', { method: 'POST', body: { name } });
    showPairing(ticket);
    loadDevices();
  } catch (e) {
    error.textContent = e.message;
  }
}

function showPairing(ticket) {
  document.getElementById('sheet-title').textContent = 'Inquadra il QR code';
  document.getElementById('sheet-body').textContent = `Apri la fotocamera del dispositivo "${ticket.name}" e inquadra il codice. Il link vale una sola volta.`;
  document.getElementById('sheet-content').innerHTML = `
    <div class="qr-card">
      <div class="qr">${ticket.qrSvg}</div>
      <div class="qr-countdown" id="qr-countdown">—</div>
      <div class="qr-url"><input id="pair-url" type="text" readonly value="${esc(ticket.url)}"><button class="btn ghost" data-action="copy-link">Copia link</button></div>
      <p class="muted qr-note">Se chiudi questo foglio il QR non è più recuperabile: potrai generare un nuovo link, che invalida il precedente.</p>
    </div>`;
  document.getElementById('sheet-actions').innerHTML = `<button class="btn ghost" data-action="close-sheet">Chiudi</button>`;
  confirmAction = null;
  showSheet();
  startQrCountdown(ticket.expiresAt);
}

function startQrCountdown(expiresAt) {
  clearInterval(qrTimer);
  const expires = new Date(expiresAt).getTime();
  const tick = () => {
    const el = document.getElementById('qr-countdown');
    if (!el) {
      clearInterval(qrTimer);
      return;
    }
    const left = Math.max(0, Math.round((expires - Date.now()) / 1000));
    if (left === 0) {
      el.textContent = 'Link scaduto · genera un nuovo link';
      clearInterval(qrTimer);
      return;
    }
    const minutes = Math.floor(left / 60);
    const seconds = left % 60;
    el.textContent = `Valido ancora ${minutes}:${String(seconds).padStart(2, '0')}`;
  };
  tick();
  qrTimer = setInterval(tick, 1000);
}

async function copyLink() {
  const input = document.getElementById('pair-url');
  if (!input) return;
  try {
    await navigator.clipboard.writeText(input.value);
    toast('Link copiato');
  } catch (error) {
    input.select();
    toast('Copia il link selezionato');
  }
}

function refreshDevicePairing(id) {
  document.getElementById('sheet-title').textContent = 'Nuovo link di pairing';
  document.getElementById('sheet-body').textContent = 'Il link precedente smette di funzionare.';
  document.getElementById('sheet-content').innerHTML = `<p class="muted">Genero un nuovo link monouso per questo dispositivo.</p><p class="form-error" id="device-error"></p>`;
  document.getElementById('sheet-actions').innerHTML = `<button class="btn ghost" data-action="close-sheet">Annulla</button><button class="btn primary" data-action="confirm-create-device" data-device="${id}">Genera</button>`;
  confirmAction = null;
  showSheet();
}

function confirmRevokeDevice(id, name) {
  openConfirm({
    title: `Revocare "${name}"?`,
    body: 'Il dispositivo perde subito la sessione e dovrà essere riaccoppiato con un nuovo link.',
    label: 'Revoca',
    danger: true,
    onConfirm: async () => {
      try {
        await api('/devices/' + id, { method: 'DELETE' });
        toast('Dispositivo revocato');
        loadDevices();
      } catch (error) {
        toast(error.message);
      }
    },
  });
}

async function loadDevices() {
  try {
    const data = await api('/devices');
    if (state.overview) state.overview.devices = data.devices || [];
    if (state.view === 'devices') renderDevices();
  } catch (error) {
    toast(error.message);
  }
}

async function logout() {
  try {
    await api('/logout', { method: 'POST' });
  } catch (error) {
    /* the cookie may already be gone */
  }
  location.replace('/panel/login');
}

/* ---------- custom range ---------- */

function openCustomRange() {
  const now = new Date();
  const from = new Date(now.getTime() - 7 * 86400000);
  const toInput = (d) => {
    const pad = (n) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  };

  document.getElementById('sheet-title').textContent = 'Intervallo personalizzato';
  document.getElementById('sheet-body').textContent = 'Massimo un anno. La data di fine deve essere successiva a quella di inizio.';
  document.getElementById('sheet-content').innerHTML = `<div class="user-form">
    <label class="field"><span>Dal</span><input id="range-from" type="datetime-local" value="${toInput(from)}"></label>
    <label class="field"><span>Al</span><input id="range-to" type="datetime-local" value="${toInput(now)}"></label>
    <p class="form-error" id="range-error"></p>
  </div>`;
  document.getElementById('sheet-actions').innerHTML = `<button class="btn ghost" data-action="close-sheet">Annulla</button><button class="btn primary" data-action="apply-range">Applica</button>`;
  confirmAction = null;
  showSheet();
}

async function applyRange() {
  const from = document.getElementById('range-from').value;
  const to = document.getElementById('range-to').value;
  const error = document.getElementById('range-error');
  error.textContent = '';
  if (!from || !to) {
    error.textContent = 'Indica la data di inizio e di fine.';
    return;
  }
  if (new Date(from) >= new Date(to)) {
    error.textContent = 'La data di fine deve essere successiva a quella di inizio.';
    return;
  }
  if (new Date(to).getTime() - new Date(from).getTime() > 366 * 86400000) {
    error.textContent = "L'intervallo massimo è di un anno.";
    return;
  }

  state.customFrom = from;
  state.customTo = to;
  state.historyRange = 'custom';
  closeSheet();
  await loadHistory();
}

/* ---------- navigation ---------- */

function openMore() {
  document.getElementById('sheet-title').textContent = 'Altre sezioni';
  document.getElementById('sheet-body').textContent = 'Sistema, utenti e dispositivi.';
  document.getElementById('sheet-content').innerHTML = `<div class="more-grid">
    <button class="btn" data-nav="system">${icon('gear')} Sistema</button>
    <button class="btn" data-nav="users">${icon('users')} Utenti</button>
    <button class="btn" data-nav="devices">${icon('device')} Dispositivi</button>
  </div>`;
  document.getElementById('sheet-actions').innerHTML = `<button class="btn ghost" data-action="close-sheet">Chiudi</button>`;
  confirmAction = null;
  showSheet();
}

function setView(name) {
  if (!views[name]) return;
  if (isSheetOpen()) closeSheet();
  state.view = name;
  try {
    if (location.hash !== '#' + name) history.replaceState(null, '', '#' + name);
  } catch (e) {
    /* the app still works without the hash */
  }
  render();
  if (name === 'heating') loadHistory();
  if (name === 'events') loadEvents();
  if (name === 'devices') loadDevices();
  window.scrollTo(0, 0);
}

/* ---------- events ---------- */

document.addEventListener('click', async (event) => {
  const nav = event.target.closest('[data-nav]');
  if (nav) {
    setView(nav.dataset.nav);
    return;
  }

  const viewButton = event.target.closest('[data-view]');
  if (viewButton && (viewButton.classList.contains('nav-item') || viewButton.closest('.tabs'))) {
    setView(viewButton.dataset.view);
    return;
  }

  const moreButton = event.target.closest('[data-more]');
  if (moreButton) {
    openMore();
    return;
  }

  const button = event.target.closest('[data-action]');
  if (!button) return;
  const action = button.dataset.action;

  if (action === 'toggle-boiler') await toggleBoiler();
  else if (action === 'toggle-suspend') await toggleSuspend();
  else if (action === 'edit-program') openProgramEditor();
  else if (action === 'delete-program') confirmDeleteProgram();
  else if (action === 'open-gate') confirmOpenGate(button.dataset.gate);
  else if (action === 'window') confirmWindow(Number(button.dataset.hours));
  else if (action === 'lock-gates') confirmLockGates();
  else if (action === 'lights') await toggleLights();
  else if (action === 'add-user') openAddUser();
  else if (action === 'save-user') await saveUser();
  else if (action === 'edit-name') openEditName(Number(button.dataset.user));
  else if (action === 'save-name') await saveName();
  else if (action === 'toggle-role') await toggleRole(Number(button.dataset.user), button.dataset.role);
  else if (action === 'toggle-topic') await toggleTopic(Number(button.dataset.user), button.dataset.topic);
  else if (action === 'remove-user') confirmRemoveUser(Number(button.dataset.user));
  else if (action === 'add-device') openAddDevice();
  else if (action === 'create-device') await createDevice(null);
  else if (action === 'confirm-create-device') await createDevice(button.dataset.device);
  else if (action === 'refresh-device') refreshDevicePairing(button.dataset.device);
  else if (action === 'revoke-device') confirmRevokeDevice(button.dataset.device, button.dataset.name);
  else if (action === 'copy-link') await copyLink();
  else if (action === 'logout') await logout();
  else if (action === 'chart-range') await changeRange(button.dataset.range);
  else if (action === 'filter') {
    state.eventFilter = button.dataset.filter;
    await loadEvents();
    renderEvents();
  } else if (action === 'refresh') {
    await refresh();
    await loadEvents();
    toast('Dati aggiornati');
  } else if (action === 'alerts') openAlerts();
  else if (action === 'go') {
    const target = button.dataset.target;
    closeSheet();
    if (target) setView(target);
  } else if (action === 'close-sheet') closeSheet();
  else if (action === 'confirm') {
    const fn = confirmAction;
    confirmAction = null;
    closeSheet();
    if (fn) await fn();
  } else if (action === 'add-interval') addIntervalRow();
  else if (action === 'remove-interval') {
    const row = button.closest('.editor-row');
    if (row) row.remove();
  } else if (action === 'save-program') await saveProgram();
  else if (action === 'apply-range') await applyRange();
});

async function changeRange(range) {
  if (range === 'custom') {
    openCustomRange();
    return;
  }
  state.historyRange = range;
  renderHeating();
  await loadHistory();
}

document.getElementById('backdrop').addEventListener('click', closeSheet);
document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' && isSheetOpen()) closeSheet();
});

window.addEventListener('hashchange', () => {
  const name = location.hash.replace('#', '');
  if (views[name] && name !== state.view) setView(name);
});

/* ---------- clock and polling ---------- */

function updateClock() {
  const clock = document.getElementById('clock');
  const date = document.getElementById('clock-date');
  const now = new Date();
  if (clock)
    clock.textContent = String(now.getHours()).padStart(2, '0') + ':' + String(now.getMinutes()).padStart(2, '0');
  if (date)
    date.textContent = now.toLocaleDateString('it-IT', { weekday: 'short', day: '2-digit', month: 'short' });
}

setInterval(() => {
  state.now = new Date();
  updateClock();
}, 15000);

setInterval(() => {
  if (isSheetOpen()) return;
  const active = document.activeElement;
  if (active && ['INPUT', 'TEXTAREA', 'SELECT'].includes(active.tagName)) return;
  refresh();
}, 5000);

setInterval(() => {
  if (state.view === 'heating' && !isSheetOpen()) loadHistory();
}, 60000);

/* ---------- start ---------- */

const initial = location.hash.replace('#', '');
if (views[initial]) state.view = initial;
render();
refresh();
if (state.view === 'heating') loadHistory();
if (state.view === 'events') loadEvents();
