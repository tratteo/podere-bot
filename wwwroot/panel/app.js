'use strict';

const CONFIG = window.PANEL_CONFIG || { mode: 'lan', version: 'locale' };
const $ = (id) => document.getElementById(id);

const MIN = 60000;
const GATE_OPEN_MS = 3000;
const GATE_DONE_MS = 2200;
const LIGHTS_MS = 5000;
const HOURS = [1, 2, 4, 8, 12, 24];

const VIEWS = {
  home: 'Casa',
  heating: 'Riscaldamento',
  gates: 'Cancelli',
  events: 'Eventi',
  users: 'Utenti',
  devices: 'Dispositivi',
  system: 'Sistema',
};
const MORE_VIEWS = ['users', 'devices', 'system'];
const AURA_VIEWS = ['home', 'heating'];

const TOPICS = {
  technical: { label: 'Tecnico', desc: 'Sensori e salute del sistema' },
  heating: { label: 'Riscaldamento', desc: 'Caldaia e temperatura' },
  gates: { label: 'Cancelli', desc: 'Aperture e sblocchi' },
};

const EVENT_TOPICS = {
  technical: { label: 'Tecnico', icon: 'activity' },
  heating: { label: 'Riscaldamento', icon: 'flame' },
  gates: { label: 'Cancelli', icon: 'gate' },
  panel: { label: 'Pannello', icon: 'shield' },
};

const ROLES = [
  { key: 'user', icon: 'user', label: 'Utente', desc: 'Apre i cancelli solo durante lo sblocco, con limite' },
  { key: 'gate', icon: 'gate', label: 'Cancelli', desc: 'Apre sempre, senza limite di aperture' },
  { key: 'admin', icon: 'shield', label: 'Admin', desc: 'Gestisce bot, utenti e programma' },
];

const DEVICE_STATES = {
  active: { label: 'Attivo', cls: 'ok' },
  pending: { label: 'In attesa', cls: 'warn' },
  expired: { label: 'Scaduto', cls: '' },
  revoked: { label: 'Revocato', cls: 'bad' },
};

const RANGES = [
  ['today', 'Oggi'],
  ['7d', '7 giorni'],
  ['30d', '30 giorni'],
  ['1y', '1 anno'],
  ['custom', 'Altro'],
];

/** Blob colour by temperature: flat cold blue up to 10 °C, then cool, mild, comfort, warm. */
const AURA_STOPS = [
  [10, [60, 110, 210]],
  [15, [78, 165, 215]],
  [18, [70, 178, 160]],
  [20, [224, 163, 67]],
  [24, [228, 118, 70]],
  [28, [214, 80, 84]],
];

/* ---------- preferences (per device) ---------- */

function readPref(key, fallback, allowed) {
  try {
    const value = localStorage.getItem('pb.' + key);
    return allowed.includes(value) ? value : fallback;
  } catch (e) {
    return fallback;
  }
}

const prefs = {
  theme: readPref('theme', 'auto', ['auto', 'dark', 'light']),
  motion: readPref('motion', 'full', ['full', 'calm']),
  idle: readPref('idle', '2', ['2', '5', '0']),
};

function savePref(key, value) {
  prefs[key] = value;
  try {
    localStorage.setItem('pb.' + key, value);
  } catch (e) {
    /* private mode: the choice lasts for this session */
  }
  if (key === 'theme') {
    // A kiosk browser may clear the storage on reload; the cookie keeps the choice.
    document.cookie = 'pb_theme=' + value + ';path=/;max-age=31536000;SameSite=Strict';
  }
}

/* ---------- state ---------- */

const state = {
  view: 'home',
  overview: null,
  skew: 0,
  history: null,
  historyRange: 'today',
  chartSeries: ['avg'],
  seriesMenuOpen: false,
  customFrom: '',
  customTo: '',
  events: [],
  eventFilter: 'all',
  offline: false,
  selectedUser: null,
  gateAnim: null,
  lightsUntil: 0,
  boilerPending: null,
  shownTemp: null,
  chartFull: false,
  historyLoading: false,
  lastInput: Date.now(),
  idle: false,
};

const darkQuery = window.matchMedia ? matchMedia('(prefers-color-scheme: dark)') : null;

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

function applyOverview(overview) {
  state.overview = overview;
  const server = new Date(overview.serverTime).getTime();
  if (!Number.isNaN(server)) state.skew = server - Date.now();
}

let refreshing = false;

async function refresh() {
  if (refreshing) return;
  refreshing = true;
  try {
    const first = !state.overview;
    applyOverview(await api('/overview'));
    const wasOffline = state.offline;
    state.offline = false;
    render(first);
    if (wasOffline) toast('Di nuovo collegato');
  } catch (error) {
    if (error.message !== 'Sessione scaduta') {
      if (!state.offline) toast('Bot non raggiungibile, riprovo', 'error');
      state.offline = true;
      render();
    }
  } finally {
    refreshing = false;
  }
}

async function loadEvents() {
  try {
    const topic = state.eventFilter === 'all' ? '' : '&topic=' + encodeURIComponent(state.eventFilter);
    const data = await api('/events?limit=50' + topic);
    state.events = data.events || [];
    if (state.view === 'events') render();
  } catch (error) {
    toast(error.message, 'error');
  }
}

async function loadHistory() {
  try {
    const query =
      state.historyRange === 'custom'
        ? '?range=custom&from=' + encodeURIComponent(state.customFrom) + '&to=' + encodeURIComponent(state.customTo)
        : '?range=' + state.historyRange;
    state.history = await api('/history' + query);
  } catch (error) {
    state.history = { error: error.message, points: [] };
  }
  state.historyLoading = false;
  if (state.view === 'heating') render();
}

async function write(path, options) {
  const result = await api(path, options);
  if (result && result.overview) {
    applyOverview(result.overview);
    render();
    if (state.view === 'events') loadEvents();
  }
  return result;
}

/**
 * Optimistic write: the UI is updated first, the request runs in the background, and a failed
 * request reconciles from the server. The toast shows a loading state until the outcome.
 */
async function optimistic({ pending, apply, request, success, failure }) {
  const before = state.overview
    ? typeof structuredClone === 'function'
      ? structuredClone(state.overview)
      : JSON.parse(JSON.stringify(state.overview))
    : null;

  if (apply && state.overview) {
    apply(state.overview);
    render();
  }
  if (pending) toast(pending, 'loading');

  try {
    const result = await request();
    if (result && result.overview) {
      applyOverview(result.overview);
      render();
      if (state.view === 'events') loadEvents();
    }
    if (success) toast(success, 'ok');
    else hideToast();
    return result;
  } catch (error) {
    if (before) {
      state.overview = before;
      render();
    }
    try {
      await refresh();
    } catch (e) {
      /* keep the snapshot when the server is unreachable too */
    }
    if (failure) failure(error);
    toast(error.message || 'Errore', 'error');
    return null;
  }
}

const wait = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/* ---------- helpers ---------- */

function icon(name, cls = '') {
  return `<svg class="icon ${cls}" aria-hidden="true"><use href="#i-${name}"></use></svg>`;
}

function fmt(n) {
  return n === null || n === undefined || Number.isNaN(Number(n)) ? '—' : Number(n).toFixed(1);
}

function esc(value) {
  return String(value === null || value === undefined ? '' : value).replace(
    /[&<>"']/g,
    (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]
  );
}

function nowMs() {
  return Date.now() + state.skew;
}

function toDate(value) {
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

function minutesToTime(m) {
  return String(Math.floor(m / 60)).padStart(2, '0') + ':' + String(m % 60).padStart(2, '0');
}

function timeToMinutes(v) {
  if (!v) return null;
  const parts = v.split(':').map(Number);
  if (parts.length !== 2 || Number.isNaN(parts[0]) || Number.isNaN(parts[1])) return null;
  if (parts[0] > 24 || parts[1] > 59 || (parts[0] === 24 && parts[1] !== 0)) return null;
  return parts[0] * 60 + parts[1];
}

function fmtTime(value) {
  const d = toDate(value);
  if (!d) return '--:--';
  return String(d.getHours()).padStart(2, '0') + ':' + String(d.getMinutes()).padStart(2, '0');
}

function fmtDate(value) {
  const d = toDate(value);
  return d ? d.toLocaleDateString('it-IT', { day: '2-digit', month: '2-digit' }) : '—';
}

function relative(value) {
  const d = toDate(value);
  if (!d) return 'mai';
  const seconds = Math.max(0, Math.round((nowMs() - d.getTime()) / 1000));
  if (seconds < 10) return 'adesso';
  if (seconds < 60) return `${seconds} s fa`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min fa`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h fa`;
  return `${Math.round(hours / 24)} g fa`;
}

/** Today shows the time, yesterday says so, older entries show the date. */
function whenLabel(value) {
  const d = toDate(value);
  if (!d) return '—';
  const today = new Date(nowMs());
  const start = new Date(today.getFullYear(), today.getMonth(), today.getDate()).getTime();
  if (d.getTime() >= start) return fmtTime(d);
  if (d.getTime() >= start - 86400000) return 'ieri ' + fmtTime(d);
  return fmtDate(d);
}

function duration(minutes) {
  if (minutes === null || minutes === undefined) return null;
  const m = Math.max(0, Math.round(minutes));
  if (m < 1) return null;
  if (m < 60) return `${m} min`;
  return `${Math.floor(m / 60)} h ${String(m % 60).padStart(2, '0')} min`;
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

function plural(n, one, many) {
  return `${n} ${n === 1 ? one : many}`;
}

function stripEmoji(value) {
  return String(value ?? '')
    .replace(/[\u{1F300}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE0F}\u{2B00}-\u{2BFF}]/gu, '')
    .replace(/\s+/g, ' ')
    .trim();
}

/** WMO weather codes from Open-Meteo, mapped to an icon and an Italian label. */
function weatherInfo(code, isDay) {
  if (code === 0) return { icon: isDay ? 'sun' : 'moon', label: 'Sereno' };
  if (code === 1 || code === 2) return { icon: isDay ? 'cloud-sun' : 'cloud', label: 'Poco nuvoloso' };
  if (code === 3) return { icon: 'cloud', label: 'Coperto' };
  if (code === 45 || code === 48) return { icon: 'fog', label: 'Nebbia' };
  if (code >= 51 && code <= 57) return { icon: 'rain', label: 'Pioggerella' };
  if ((code >= 61 && code <= 67) || (code >= 80 && code <= 82)) return { icon: 'rain', label: 'Pioggia' };
  if ((code >= 71 && code <= 77) || code === 85 || code === 86) return { icon: 'snow', label: 'Neve' };
  if (code >= 95) return { icon: 'storm', label: 'Temporale' };
  return { icon: 'cloud', label: 'Nuvoloso' };
}

/* ---------- derived ---------- */

const heating = () => state.overview?.heating || null;
const temperatures = () => state.overview?.temperatures || null;
const gates = () => state.overview?.gates || null;
const alerts = () => state.overview?.alerts || [];
const weather = () => state.overview?.weather || null;

function nowMinutes() {
  const d = new Date(nowMs());
  return d.getHours() * 60 + d.getMinutes();
}

function currentInterval() {
  const h = heating();
  if (!h) return null;
  const m = nowMinutes();
  return h.program.intervals.find((i) => m >= i.from && m < i.to) || null;
}

function nextChange() {
  const h = heating();
  if (!h) return null;
  const m = nowMinutes();
  const out = [];
  for (const i of h.program.intervals) {
    if (i.from > m) out.push({ at: i.from, temp: i.temperature, kind: 'start' });
    if (i.to > m) out.push({ at: i.to, temp: i.temperature, kind: 'end' });
  }
  out.sort((a, b) => a.at - b.at);
  return out[0] || null;
}

function antifreezeActive() {
  const h = heating();
  return !!h && h.antifreezeEnabled && h.temperature !== null && h.temperature < h.antifreezeThreshold;
}

function boilerOn() {
  const h = heating();
  return state.boilerPending ?? !!h?.boilerActive;
}

function sensorState(sensor) {
  if (sensor.status) return 'error';
  if (sensor.informational) return 'info';
  if (sensor.stale) return 'stale';
  return 'ok';
}

const LOCAL_STATUS = {
  Ok: 'ok',
  DevicesRootMissing: 'bus assente',
  BusMasterMissing: 'bus senza master',
  NoSensors: 'nessun sensore',
  CrcError: 'errore CRC',
  ReadError: 'errore di lettura',
  ParseError: 'errore di formato',
};

function localStatusLabel(status) {
  return LOCAL_STATUS[status] || status;
}

function lightsLit() {
  return state.lightsUntil > Date.now();
}

function userRole(u) {
  if (u.isAdmin) return 'admin';
  return u.entitlements.includes('gate_controller') ? 'gate' : 'user';
}

function sortedUsers() {
  return [...(state.overview?.users || [])].sort((a, b) => {
    const rank = { admin: 0, gate: 1, user: 2 };
    return rank[userRole(a)] - rank[userRole(b)] || (a.name || String(a.id)).localeCompare(b.name || String(b.id), 'it');
  });
}

function adminCount() {
  return (state.overview?.users || []).filter((u) => u.isAdmin).length;
}

/** Length of the shared window: from the server when known, otherwise the preset that fits. */
function windowInfo(g) {
  if (!g?.open || !g.expiresAt) return null;
  const until = toDate(g.expiresAt);
  const left = Math.max(0, until.getTime() - nowMs());
  const opened = toDate(g.openedAt);
  let total = opened ? until.getTime() - opened.getTime() : 0;
  if (!(total > 0)) total = (HOURS.find((h) => h * 3600000 >= left) || 24) * 3600000;
  const minutes = Math.ceil(left / MIN);
  const label = minutes < 1 ? 'meno di 1 min' : minutes < 60 ? `${minutes} min` : `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, '0')} min`;
  return {
    until,
    left,
    total,
    hours: Math.round(total / 3600000),
    fraction: Math.max(0, Math.min(1, left / total)),
    low: left < 10 * MIN,
    label,
  };
}

/* ---------- DOM morphing ---------- */

/**
 * Updates the view in place instead of replacing it, so running animations, transitions and
 * focus survive the 5 s poll. Elements marked data-keep are owned by script (the temperature).
 */
function morph(container, html) {
  const template = document.createElement('template');
  template.innerHTML = html;
  morphChildren(container, template.content);
}

function morphChildren(from, to) {
  const current = Array.from(from.childNodes);
  const next = Array.from(to.childNodes);
  next.forEach((node, i) => {
    const existing = current[i];
    if (!existing) {
      from.appendChild(node);
      return;
    }
    if (!sameNode(existing, node)) {
      from.replaceChild(node, existing);
      return;
    }
    if (existing.nodeType !== 1) {
      if (existing.nodeValue !== node.nodeValue) existing.nodeValue = node.nodeValue;
      return;
    }
    morphElement(existing, node);
  });
  for (let i = current.length - 1; i >= next.length; i -= 1) from.removeChild(current[i]);
}

function sameNode(a, b) {
  if (a.nodeType !== b.nodeType) return false;
  if (a.nodeType !== 1) return true;
  return a.tagName === b.tagName && a.getAttribute('data-key') === b.getAttribute('data-key');
}

function morphElement(el, next) {
  if (el.hasAttribute('data-keep')) return;
  for (const attr of Array.from(el.attributes)) {
    if (!next.hasAttribute(attr.name)) el.removeAttribute(attr.name);
  }
  for (const attr of Array.from(next.attributes)) {
    if (el.getAttribute(attr.name) !== attr.value) el.setAttribute(attr.name, attr.value);
  }
  morphChildren(el, next);
}

/* ---------- render ---------- */

let enterTimer = null;

function render(full = false) {
  $('app').dataset.view = state.view;
  renderNav();
  renderTop();

  const view = $('view');
  const html = state.overview ? VIEW_RENDERERS[state.view]() : skeleton();
  if (full) {
    const before = view.querySelector('[data-tnum]');
    const rect = before ? before.getBoundingClientRect() : null;
    view.innerHTML = html;
    view.classList.remove('enter');
    void view.offsetWidth;
    view.classList.add('enter');
    clearTimeout(enterTimer);
    enterTimer = setTimeout(() => view.classList.remove('enter'), 1400);
    enterTemperature(rect);
  } else {
    morph(view, html);
    syncTemperature(700);
  }

  renderAura();
  renderIdle();
  tickLive();
  placePills();
  reapplyChartHover();
}

function skeleton() {
  return `<div class="empty" style="min-height:50vh">${state.offline ? icon('alert') : '<span class="spinner"></span>'}<span>${state.offline ? 'Bot non raggiungibile. Riprovo tra pochi secondi.' : 'Carico i dati dal bot…'}</span></div>`;
}

function renderNav() {
  const more = MORE_VIEWS.includes(state.view);
  document.querySelectorAll('.nav-item, .tabs button').forEach((b) => {
    const active = b.dataset.more ? more : b.dataset.view === state.view;
    b.classList.toggle('active', active);
    if (active) b.setAttribute('aria-current', 'page');
    else b.removeAttribute('aria-current');
  });
  movePill();
}

let pillPlaced = false;

function movePill() {
  const pill = $('rail-pill');
  const item = document.querySelector(`.nav-item[data-view="${state.view}"]`);
  if (!pill || !item || !item.offsetParent) return;
  if (!pillPlaced) pill.style.transition = 'none';
  pill.style.transform = `translateY(${item.offsetTop}px)`;
  if (!pillPlaced) {
    void pill.offsetWidth;
    pill.style.transition = '';
    pill.classList.add('ready');
    pillPlaced = true;
  }
}

/* Every segmented control and the bottom tabs get the same sliding pill as the rail: the active
   background moves instead of jumping, so the eye follows the change. */
function placePills() {
  const boxes = [...document.querySelectorAll('.seg'), document.querySelector('.tabs')].filter(Boolean);
  for (const box of boxes) {
    const tabs = box.classList.contains('tabs');
    let pill = box.querySelector(tabs ? '.tab-pill' : '.seg-pill');
    if (!pill) {
      pill = document.createElement('span');
      pill.className = tabs ? 'tab-pill' : 'seg-pill';
      pill.setAttribute('aria-hidden', 'true');
      pill.dataset.fresh = '1';
      box.prepend(pill);
    }
    const active = box.querySelector(tabs ? 'button.active' : 'button.on');
    if (!active || !active.offsetWidth) {
      pill.style.opacity = '0';
      continue;
    }
    pill.style.opacity = '1';
    if (pill.dataset.fresh) {
      pill.style.transition = 'none';
      pill.dataset.fresh = '';
    }
    pill.style.transform = `translateX(${active.offsetLeft}px)`;
    pill.style.width = `${active.offsetWidth}px`;
    if (pill.style.transition === 'none') {
      void pill.offsetWidth;
      pill.style.transition = '';
    }
  }
}

function renderTop() {
  const title = $('page-title');
  if (title.textContent !== VIEWS[state.view]) title.textContent = VIEWS[state.view];

  const action =
    state.overview && state.view === 'users'
      ? `<button class="btn p s" data-action="add-user" aria-label="Aggiungi utente">${icon('plus')}<span class="lbl">Aggiungi utente</span></button>`
      : state.overview && state.view === 'devices'
        ? `<button class="btn p s" data-action="add-device" aria-label="Aggiungi dispositivo">${icon('plus')}<span class="lbl">Aggiungi dispositivo</span></button>`
        : '';
  morph($('top-action'), action);

  const chip = $('status-chip');
  const count = alerts().length;
  const tone = state.offline ? 'bad' : count ? 'warn' : '';
  chip.className = 'chip' + (tone ? ' ' + tone : '');
  const label = state.offline ? 'Non raggiungibile' : !state.overview ? 'Collegamento…' : count ? plural(count, 'avviso', 'avvisi') : 'Tutto ok';
  if ($('status-label').textContent !== label) $('status-label').textContent = label;
  chip.setAttribute('aria-label', count ? `${label}, apri l'elenco` : label);

  const themeIcon = prefs.theme === 'auto' ? 'auto' : prefs.theme === 'dark' ? 'moon' : 'sun';
  const button = $('theme-btn');
  if (button.dataset.icon !== themeIcon) {
    button.dataset.icon = themeIcon;
    button.innerHTML = icon(themeIcon, button.dataset.ready ? 'spin-in' : '');
    button.dataset.ready = '1';
  }

  const version = state.overview?.system?.version || CONFIG.version || '—';
  $('rail-version').textContent = version === 'locale' ? 'locale' : 'v' + String(version).slice(0, 7);
}

/* ---------- temperature number ---------- */

let tweenFrame = null;

function setTemperatureText(text) {
  document.querySelectorAll('[data-temp]').forEach((el) => {
    if (el.textContent !== text) el.textContent = text;
  });
}

function tween(from, to, ms) {
  cancelAnimationFrame(tweenFrame);
  if (prefs.motion === 'calm' || from === to) {
    state.shownTemp = to;
    setTemperatureText(fmt(to));
    return;
  }
  const start = performance.now();
  const step = (time) => {
    const k = Math.min(1, (time - start) / ms);
    const eased = 1 - Math.pow(1 - k, 3);
    state.shownTemp = from + (to - from) * eased;
    setTemperatureText(fmt(state.shownTemp));
    if (k < 1) tweenFrame = requestAnimationFrame(step);
    else state.shownTemp = to;
  };
  tweenFrame = requestAnimationFrame(step);
}

function syncTemperature(ms) {
  const target = heating()?.temperature;
  if (target === null || target === undefined) {
    state.shownTemp = null;
    setTemperatureText('—');
    return;
  }
  tween(state.shownTemp ?? target, target, ms);
}

/** Tab change: the number slides from where it was (250 ms) or counts up into place. */
function enterTemperature(previous) {
  const el = $('view').querySelector('[data-tnum]');
  const target = heating()?.temperature;
  if (!el) return;
  if (target === null || target === undefined) {
    syncTemperature(0);
    return;
  }
  const calm = prefs.motion === 'calm' || typeof el.animate !== 'function';
  if (previous && !calm) {
    const next = el.getBoundingClientRect();
    const dx = previous.left - next.left;
    const dy = previous.top - next.top;
    const k = previous.height / (next.height || 1);
    if (Math.abs(dx) + Math.abs(dy) > 0.5 || Math.abs(k - 1) > 0.01) {
      el.animate([{ transform: `translate(${dx}px, ${dy}px) scale(${k})` }, { transform: 'none' }], {
        duration: 250,
        easing: 'cubic-bezier(0.2, 0.9, 0.2, 1)',
      });
    }
    tween(state.shownTemp ?? target, target, 250);
  } else if (!calm) {
    el.animate([{ opacity: 0.2, transform: 'translateY(10px)' }, { opacity: 1, transform: 'none' }], {
      duration: 250,
      easing: 'cubic-bezier(0.2, 0.9, 0.2, 1)',
    });
    tween(target - 1.5, target, 250);
  } else {
    tween(target, target, 0);
  }
}

/* ---------- aura ---------- */

function auraColor(t) {
  if (t === null || t === undefined || Number.isNaN(Number(t))) return 'rgb(120,125,122)';
  const v = Math.max(AURA_STOPS[0][0], Math.min(AURA_STOPS[AURA_STOPS.length - 1][0], Number(t)));
  for (let i = 0; i < AURA_STOPS.length - 1; i += 1) {
    const [a, ca] = AURA_STOPS[i];
    const [b, cb] = AURA_STOPS[i + 1];
    if (v >= a && v <= b) {
      const k = (v - a) / (b - a || 1);
      return 'rgb(' + ca.map((c, j) => Math.round(c + (cb[j] - c) * k)).join(',') + ')';
    }
  }
  return 'rgb(' + AURA_STOPS[AURA_STOPS.length - 1][1].join(',') + ')';
}

function paintAura(el, t) {
  const core = el.querySelector('.aura-core');
  const highlight = el.querySelector('.aura-hl');
  const base = auraColor(t);
  const accent = auraColor(t === null || t === undefined ? t : Number(t) + 2.5);
  if (core.style.color !== base) core.style.color = base;
  if (highlight.style.color !== accent) highlight.style.color = accent;
}

function renderAura() {
  const aura = $('aura');
  const t = heating()?.temperature;
  aura.classList.toggle('show', !!state.overview && AURA_VIEWS.includes(state.view) && prefs.motion === 'full');
  paintAura(aura, t);
  paintAura($('idle-aura'), t);
}

/* ---------- shared fragments ---------- */

function boilerLine() {
  const h = heating();
  const on = boilerOn();
  const since = duration(h.boilerOnMinutes);
  const text = antifreezeActive() ? 'Antigelo attivo, caldaia accesa' : on ? (since ? `Caldaia accesa da ${since}` : 'Caldaia accesa') : 'Caldaia spenta';
  return `<div class="boil ${on ? 'on' : ''}">${icon('flame', 'fl')}<span>${text}</span></div>`;
}

function temperatureNumber() {
  const t = heating()?.temperature;
  const shown = t === null || t === undefined ? '—' : fmt(state.shownTemp ?? t);
  return `<div class="temp mono" data-tnum data-keep><span data-temp>${shown}</span><small>°C</small></div>`;
}

function miniTarget() {
  const h = heating();
  const interval = currentInterval();
  const value = h.program.suspended ? 'Sospeso' : interval ? fmt(interval.temperature) + '°' : h.program.intervals.length ? 'Spento' : '—';
  return `<div class="mini"><span class="sico">${icon('target')}</span><div><div class="k">Obiettivo</div><div class="mini-value"><b class="mono">${value}</b></div></div></div>`;
}

function miniOutdoor() {
  const w = weather();
  if (!w) {
    return `<div class="mini"><span class="sico b">${icon('cloud')}</span><div><div class="k">Esterno</div><div class="mini-value"><b class="mono">—</b><span>non disponibile</span></div></div></div>`;
  }
  const info = weatherInfo(w.code, w.isDay);
  const range = w.min !== null && w.min !== undefined ? `<span class="mono">${fmt(w.min)}–${fmt(w.max)}°</span>` : '';
  const humidity = w.humidity === null || w.humidity === undefined ? '' : `<span class="mono">${w.humidity}%</span>`;
  return `<div class="mini wx"><span class="sico b">${icon(info.icon)}</span><div><div class="k">Esterno · ${info.label.toLowerCase()}</div><div class="mini-value"><b class="mono">${fmt(w.temperature)}°</b>${range}${humidity}</div></div></div>`;
}

function programSummary() {
  const h = heating();
  if (!h.program.hasProgram || !h.program.intervals.length) return 'Nessun programma impostato';
  if (h.program.suspended) return 'Programma sospeso';
  const interval = currentInterval();
  if (interval) return `Programma · ${fmt(interval.temperature)}° fino alle ${minutesToTime(interval.to)}`;
  const next = nextChange();
  if (next && next.kind === 'start') return `Prossima accensione alle ${minutesToTime(next.at)} · ${fmt(next.temp)}°`;
  return 'Programma concluso per oggi';
}

function programHours(intervals) {
  const minutes = (intervals || []).reduce((sum, i) => sum + Math.max(0, i.to - i.from), 0);
  if (!minutes) return '0 h';
  const hours = minutes / 60;
  return `${Number.isInteger(hours) ? hours : hours.toFixed(1)} h`;
}

function programSub() {
  const h = heating();
  const count = h.program.intervals.length;
  if (!count) return 'Nessuna fascia: tocca Modifica per crearne una';
  const total = programHours(h.program.intervals);
  if (h.program.suspended) return `${plural(count, 'fascia', 'fasce')} · ${total} totali · sospeso`;
  const next = nextChange();
  if (!next) return `${plural(count, 'fascia', 'fasce')} · ${total} totali · concluso per oggi`;
  return next.kind === 'end'
    ? `${plural(count, 'fascia', 'fasce')} · ${total} totali · alle ${minutesToTime(next.at)} si spegne`
    : `${plural(count, 'fascia', 'fasce')} · ${total} totali · alle ${minutesToTime(next.at)} si accende a ${fmt(next.temp)}°`;
}

function heatingModeLine() {
  const h = heating();
  const interval = currentInterval();
  let text;
  if (antifreezeActive()) text = `Antigelo attivo sotto ${fmt(h.antifreezeThreshold)}°`;
  else if (h.program.suspended) text = 'Programma sospeso, controllo manuale';
  else if (h.manual) text = 'Controllo manuale';
  else if (interval) text = `Programma attivo fino alle ${minutesToTime(interval.to)}`;
  else if (h.program.intervals.length) text = 'Programma in attesa della prossima fascia';
  else text = 'Nessun programma: controllo manuale';
  return `<div class="boil"><span>${text}</span></div>`;
}

function nowPercent() {
  return ((nowMinutes() / 1440) * 100).toFixed(2);
}

function timeline(intervals, options = {}) {
  const blocks = intervals
    .map((i) => {
      const left = (i.from / 1440) * 100;
      const width = ((i.to - i.from) / 1440) * 100;
      let label = '';
      if (options.labels) {
        if (width > 16) label = `${minutesToTime(i.from)}–${minutesToTime(i.to)} · ${fmt(i.temperature)}°`;
        else if (width > 5) label = `${fmt(i.temperature)}°`;
      }
      return `<div class="blk mono" style="left:${left.toFixed(3)}%;width:${width.toFixed(3)}%" title="${minutesToTime(i.from)}–${minutesToTime(i.to)} · ${fmt(i.temperature)} °C">${label ? `<span class="bl">${label}</span>` : ''}</div>`;
    })
    .join('');
  const ticks = options.fine ? ['00', '03', '06', '09', '12', '15', '18', '21', '24'] : ['00', '06', '12', '18', '24'];
  return `<div class="tlw ${options.slim ? 'slim' : ''}"><div class="tl">${blocks}</div><div class="now" style="left:${nowPercent()}%"></div><div class="ticks">${ticks.map((t) => `<span>${t}</span>`).join('')}</div></div>`;
}

function gateBadge(g) {
  return g.open
    ? `<span class="badge ok">${icon('unlock')}Sbloccati fino alle ${fmtTime(g.expiresAt)}</span>`
    : `<span class="badge">${icon('lock')}Bloccati</span>`;
}

function gateButton(gate, cls, detail) {
  const phase = state.gateAnim && state.gateAnim.gate === gate ? state.gateAnim.phase : '';
  const idle = gate === 'automatic' ? 'Apri automatico' : 'Apri pedonale';
  const label = phase === 'opening' ? 'Apertura in corso' : phase === 'done' ? 'Aperto' : idle;
  const small = gate === 'automatic' ? 'Cancello carraio' : detail ? 'Cancello pedonale' : '';
  return `<button class="gb ${cls} ${phase}" data-action="open-gate" data-gate="${gate}" ${phase ? 'aria-busy="true"' : ''}>
    <span class="gi">${icon(gate === 'automatic' ? 'car' : 'walk')}</span>
    <span><span class="gl">${label}</span><span class="ck">${icon('check')}</span>${small && (detail || gate === 'automatic') ? `<small>${small}</small>` : ''}</span>
    <i class="bar"></i>
  </button>`;
}

function eventIcon(topic, title) {
  if (topic === 'gates') {
    if (/pedonale/i.test(title)) return 'walk';
    if (/automatico/i.test(title)) return 'car';
  }
  return (EVENT_TOPICS[topic] || { icon: 'activity' }).icon;
}

function eventRow(e) {
  const title = stripEmoji(e.title);
  const detail = stripEmoji(e.detail);
  const age = nowMs() - (toDate(e.at)?.getTime() || 0);
  const time = age < 12 * 3600000 ? relative(e.at) : whenLabel(e.at);
  return `<div class="ev"><span class="evi ${esc(e.topic)}">${icon(eventIcon(e.topic, title))}</span><div><b>${esc(title)}</b>${detail ? `<span class="ev-sub">${esc(detail)}</span>` : ''}</div><time title="${esc(whenLabel(e.at))}">${esc(time)}</time></div>`;
}

function seg(action, options, current, cls = '', extra = '') {
  return `<div class="seg ${cls}" role="group"><span class="seg-pill" aria-hidden="true"></span>${options
    .map(([value, label, ic]) => `<button class="${String(value) === String(current) ? 'on' : ''}" data-action="${action}" data-value="${value}" aria-pressed="${String(value) === String(current)}" ${extra}>${ic ? icon(ic) : ''}${label}</button>`)
    .join('')}</div>`;
}

/* ---------- views ---------- */

function sourceCount() {
  const t = temperatures();
  if (!t) return 0;
  const local = t.localStatus === 'Ok' && t.local !== null ? 1 : 0;
  const external = (t.sensors || []).filter((s) => s.fresh && !s.informational && !s.status).length;
  return local + external;
}

function hero(kind) {
  const h = heating();
  const sources = sourceCount();
  return `<section class="hero">
    <div class="k rise" style="--i:0">Temperatura in casa${sources ? `<span class="badge srcbadge">${plural(sources, 'sorgente', 'sorgenti')}</span>` : ''}</div>
    ${temperatureNumber()}
    <div class="hero-line rise" style="--i:1">${kind === 'home' ? boilerLine() : heatingModeLine()}</div>
    <div class="minis rise" style="--i:2">${miniTarget()}${miniOutdoor()}</div>
    ${kind === 'home' ? `<div class="hero-foot rise" style="--i:3"><div class="sub">${programSummary()}</div>${timeline(h.program.intervals, { slim: true })}</div>` : ''}
  </section>`;
}

function renderHome() {
  const g = gates();
  const lit = lightsLit();
  return `<div class="split">
    ${hero('home')}
    <section class="card rise" style="--i:2;gap:16px">
      <div class="head mb"><span class="title">Cancelli</span>${gateBadge(g)}</div>
      ${gateButton('automatic', 'p h-lg')}
      ${gateButton('pedestrian', 'h-md')}
      <div class="g2" style="margin-top:12px">
        <button class="btn g ${lit ? 'lit' : ''}" data-action="lights">${icon('sun')}<span>${lit ? 'Accese' : 'Luci'}</span><i class="lbar"></i></button>
        ${g.open ? `<button class="btn g" data-action="lock-gates">${icon('lock')}Blocca</button>` : `<button class="btn g" data-action="window" data-hours="2">${icon('unlock')}Sblocca 2 h</button>`}
      </div>
    </section>
  </div>`;
}

function fmtDuration(seconds) {
  const value = Math.max(0, seconds || 0);
  const minutes = Math.round(value / 60);
  if (minutes <= 0) return value > 0 ? '<1 min' : '0 min';
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;
  return hours ? `${hours} h ${String(rest).padStart(2, '0')} min` : `${rest} min`;
}

let barHover = null;
let barHoverTimer = null;

function boilerChart() {
  const data = state.history;
  if (!data || data.error) return '';
  const points = Array.isArray(data.boiler) ? data.boiler : [];
  if (!points.length) {
    return `<section class="card rise" style="--i:5">
      <div class="head"><span class="title">Riscaldamento acceso</span></div>
      <div class="empty">${icon('flame')}<span>Nessuna accensione in questo periodo.</span></div>
    </section>`;
  }
  const day = data.bucket === 'day';
  // One slot per bucket of the range, so the bars sit on the same time axis as the chart above.
  const slots = (data.points || []).length ? data.points.map((p) => new Date(p.at)) : points.map((p) => new Date(p.at));
  const byAt = new Map(points.map((p) => [new Date(p.at).getTime(), p.seconds]));
  const values = slots.map((at) => byAt.get(at.getTime()) || 0);
  if (!values.some((v) => v > 0)) {
    barHover = null;
    return `<section class="card rise" style="--i:5">
      <div class="head"><span class="title">Riscaldamento acceso</span><span class="badge">0 min</span></div>
      <div class="empty">${icon('flame')}<span>Nessuna accensione in questo periodo.</span></div>
    </section>`;
  }
  barHover = { day, points: slots.map((at, i) => ({ at, seconds: values[i] })) };
  const max = Math.max(...values, 1);
  const total = values.reduce((sum, v) => sum + v, 0);
  const peak = Math.max(...values);
  const bars = values
    .map((value, i) => `<i class="bcol" style="height:${Math.max(1.2, (value / max) * 100).toFixed(2)}%" data-i="${i}"></i>`)
    .join('');
  return `<section class="card rise" style="--i:5">
    <div class="head"><span class="title">Riscaldamento acceso</span><span class="badge">${esc(fmtDuration(total))}</span></div>
    <div class="bars-wrap">
      <div class="bars${values.length > 40 ? ' dense' : ''}" role="img" aria-label="Tempo di caldaia accesa">${bars}</div>
      <div class="bars-tip" hidden></div>
    </div>
    <div class="bars-foot"><span>Ogni barra è ${day ? 'un giorno' : "un'ora"}</span><span>Picco ${esc(fmtDuration(peak))}</span></div>
  </section>`;
}

function showBarHover(event) {
  if (!barHover) return;
  const wrap = event.target.closest('.bars-wrap');
  if (!wrap) return;
  const plot = wrap.querySelector('.bars');
  const bars = plot.querySelectorAll('.bcol');
  const rect = plot.getBoundingClientRect();
  if (!rect.width || !bars.length) return;
  const ratio = Math.min(1, Math.max(0, (event.clientX - rect.left) / rect.width));
  const index = Math.min(bars.length - 1, Math.floor(ratio * bars.length));
  const point = barHover.points[index];
  if (!point) return;
  bars.forEach((bar, i) => bar.classList.toggle('on', i === index));
  const tip = wrap.querySelector('.bars-tip');
  const label = barHover.day ? fmtDate(point.at) : fmtTime(point.at);
  tip.innerHTML = `<span>${esc(label)}</span><b>${esc(fmtDuration(point.seconds))}</b>`;
  tip.hidden = false;
  const center = ((index + 0.5) / bars.length) * rect.width;
  const width = tip.offsetWidth;
  tip.style.left = `${Math.max(0, Math.min(rect.width - width, center - width / 2))}px`;
}

function hideBarHover(delay = 0) {
  clearTimeout(barHoverTimer);
  const run = () => {
    document.querySelectorAll('.bars .bcol.on').forEach((bar) => bar.classList.remove('on'));
    document.querySelectorAll('.bars-tip').forEach((tip) => {
      tip.hidden = true;
    });
  };
  if (delay) barHoverTimer = setTimeout(run, delay);
  else run();
}

function renderHeating() {
  const h = heating();
  const on = boilerOn();
  const af = antifreezeActive();
  const interval = currentInterval();
  const locked = !!interval && !h.program.suspended;
  const afText = !h.antifreezeEnabled
    ? 'Antigelo disattivato'
    : af
      ? `Antigelo attivo sotto ${fmt(h.antifreezeThreshold)}°`
      : `Antigelo pronto · sotto ${fmt(h.antifreezeThreshold)}°`;

  return `<div class="stack wide">
    <div class="split">
      ${hero('heating')}
      <section class="card rise" style="--i:2;gap:20px">
        <div class="head"><span class="title">Caldaia</span></div>
        ${boilerLine()}
        <div class="seg full strong xl" role="group" aria-label="Caldaia"><span class="seg-pill" aria-hidden="true"></span>
          <button class="${on ? 'on' : ''}" data-action="boiler" data-on="1" aria-pressed="${on}" ${locked ? 'disabled' : ''}>Accesa</button>
          <button class="${on ? '' : 'on'}" data-action="boiler" data-on="0" aria-pressed="${!on}" ${locked ? 'disabled' : ''}>Spenta</button>
        </div>
        ${locked ? `<div class="cap">Programma in corso (${minutesToTime(interval.from)}–${minutesToTime(interval.to)}) · caldaia gestita automaticamente</div>` : ''}
        <button class="btn g" data-action="toggle-suspend" ${h.program.hasProgram ? '' : 'disabled'}>${icon(h.program.suspended ? 'play' : 'pause')}${h.program.suspended ? 'Riprendi programma' : 'Sospendi programma'}</button>
        <div class="af ${af ? 'on' : ''}">${icon('snow')}<span>${afText}</span></div>
      </section>
    </div>
    <section class="rise" style="--i:3">
      <div class="head" style="margin-bottom:28px">
        <div class="grow"><div class="title">Programma</div><div class="sub">${programSub()}</div></div>
        <button class="btn s g" data-action="edit-program">${icon('edit')}Modifica</button>
      </div>
      ${timeline(h.program.intervals, { labels: true, fine: true })}
    </section>
    <section class="card chart-card ${state.chartFull ? 'full' : ''} rise" style="--i:4">
      <div class="head" style="flex-wrap:wrap"><span class="title">Andamento</span><div class="chart-tools">${seg('chart-range', RANGES, state.historyRange, 'sm')}${seriesPicker()}<button class="btn s g square" data-action="chart-full" aria-label="${state.chartFull ? 'Chiudi lo schermo intero' : 'Grafico a schermo intero'}" title="${state.chartFull ? 'Chiudi' : 'Schermo intero'}">${icon(state.chartFull ? 'x' : 'screen')}</button></div></div>
      <div id="chart-body" class="chart-body${state.historyLoading ? ' is-loading' : ''}">${temperatureChart()}</div>
    </section>
    ${boilerChart()}
  </div>`;
}

function windowCard(g) {
  const info = windowInfo(g);
  const open = !!info;
  return `<section class="card win ${open ? 'open' : ''} rise" style="--i:1">
    <div class="head win-head">
      <div class="row grow">
        <span class="sico wico ${open ? 'g' : ''}">${icon('lock', 'l')}${icon('unlock', 'u')}</span>
        <div class="win-text">
          <div class="a"><div class="title">Bloccati</div><div class="sub">Aprono solo admin e gate controller</div></div>
          <div class="b"><div class="title">Sbloccati per tutti</div><div class="sub">Max ${g.rateMax} aperture ogni ${g.rateWindow} min per utente</div></div>
        </div>
      </div>
      <button class="btn lockbtn" data-action="lock-gates" ${open ? '' : 'tabindex="-1" aria-hidden="true"'}>${icon('lock')}Blocca ora</button>
    </div>
    <div class="slot">
      <div class="sa">Scegli per quanto tempo chiunque nella lista può aprire.</div>
      <div class="sb">
        <div class="prow"><span>Fino alle <b class="mono" data-win-until>${open ? fmtTime(info.until) : '--:--'}</b></span><span>ancora <b class="mono" data-win-left>${open ? info.label : '--'}</b></span></div>
        <div class="pbar ${open && info.low ? 'low' : ''}" data-win-bar><i style="transform:scaleX(${open ? info.fraction.toFixed(4) : 1})"></i></div>
      </div>
    </div>
    <div>
      <div class="k" style="margin-bottom:14px">Durata dello sblocco</div>
      <div class="seg full tall" role="group"><span class="seg-pill" aria-hidden="true"></span>${HOURS.map((h) => `<button class="${open && info.hours === h ? 'on' : ''}" data-action="window" data-hours="${h}" aria-pressed="${open && info.hours === h}">${h} h</button>`).join('')}</div>
    </div>
  </section>`;
}

function lightsCard() {
  const lit = lightsLit();
  const w = weather();
  const sub = w && w.sunset ? `Accensione temporizzata · tramonto alle ${fmtTime(w.sunset)}` : 'Accensione temporizzata';
  return `<section class="card inline lights ${lit ? 'lit' : ''} rise" style="--i:2">
    <span class="sico a">${icon('sun')}</span>
    <div class="grow"><div class="title">Luci esterne</div><div class="sub">${sub}</div></div>
    <button class="btn lights-btn" data-action="lights">${icon('sun')}<span>${lit ? 'Accese' : 'Accendi'}</span><i class="lbar"></i></button>
  </section>`;
}

function renderGates() {
  const g = gates();
  const recent = g.recent || [];
  return `<div class="stack">
    <div class="gate-grid rise" style="--i:0">${gateButton('automatic', 'p xl', true)}${gateButton('pedestrian', 'xl', true)}</div>
    ${windowCard(g)}
    ${lightsCard()}
    <section class="card tight rise" style="--i:3">
      <div class="head mb"><span class="title">Aperture recenti</span></div>
      ${recent.length ? `<div>${recent.map((e) => eventRow({ topic: 'gates', title: e.title, detail: e.detail, at: e.at })).join('')}</div>` : `<div class="empty">${icon('gate')}<span>Nessuna apertura registrata.</span></div>`}
    </section>
  </div>`;
}

function renderEvents() {
  const filters = [['all', 'Tutti'], ...Object.entries(EVENT_TOPICS).map(([k, v]) => [k, v.label])];
  return `<div class="filters rise" style="--i:0">${seg('filter', filters, state.eventFilter)}</div>
    <section class="card tight rise" style="--i:1">
      <div class="head mb"><span class="title">${plural(state.events.length, 'evento', 'eventi')}</span><span class="badge">ultimi 50</span></div>
      ${state.events.length ? `<div>${state.events.map(eventRow).join('')}</div>` : `<div class="empty">${icon('bell')}<span>Nessun evento in questa categoria.</span></div>`}
    </section>`;
}

function userLabel(u) {
  return u.name || 'ID ' + u.id;
}

function userRow(u) {
  const role = ROLES.find((r) => r.key === userRole(u));
  const topics = u.topics.length;
  const subscriptions = topics ? plural(topics, 'notifica', 'notifiche') : 'nessuna notifica';
  return `<button class="ul ${u.id === state.selectedUser ? 'on' : ''}" data-action="select-user" data-user="${u.id}">
    <span class="avatar">${u.name ? esc(initials(u.name)) : icon('user')}</span>
    <span class="grow"><b>${esc(userLabel(u))}</b><span>${role.label} · ${subscriptions}</span></span>
    ${u.isAdmin ? `<span class="abadge" title="Admin" aria-label="Admin">${icon('shield')}</span>` : ''}
    <span class="chev">${icon('chevron')}</span>
  </button>`;
}

function userDetail(u) {
  const role = userRole(u);
  const lastAdmin = u.isAdmin && adminCount() <= 1;
  const meta = ROLES.find((r) => r.key === role);
  return `<div class="head">
      <div class="row grow">
        <span class="avatar lg">${u.name ? esc(initials(u.name)) : icon('user')}</span>
        <div class="grow"><div class="title">${esc(userLabel(u))}${u.isAdmin ? `<span class="abadge" title="Admin" aria-label="Admin">${icon('shield')}</span>` : ''}</div><div class="sub mono" style="font-size:15px">${u.name ? 'ID ' + u.id : 'Nome Telegram non disponibile'}</div></div>
      </div>
      <div class="actions-inline">
        <button class="btn s g square" data-action="edit-name" data-user="${u.id}" aria-label="Modifica il nome di ${esc(userLabel(u))}">${icon('edit')}</button>
        <button class="btn s g square danger-g" data-action="remove-user" data-user="${u.id}" aria-label="Rimuovi ${esc(userLabel(u))}" ${lastAdmin ? 'disabled' : ''}>${icon('trash')}</button>
      </div>
    </div>
    <div class="sec">Ruolo</div>
    <div class="seg full tall roles" role="group" aria-label="Ruolo"><span class="seg-pill" aria-hidden="true"></span>${ROLES.map(
      (r) => `<button class="${role === r.key ? 'on' : ''}" data-action="set-role" data-user="${u.id}" data-role="${r.key}" aria-pressed="${role === r.key}" ${lastAdmin && r.key !== 'admin' ? 'disabled' : ''}>${icon(r.icon, 'r-' + r.key)}${r.label}</button>`
    ).join('')}</div>
    <div class="cap">${meta.desc}</div>
    ${lastAdmin ? '<div class="note" style="margin-top:8px">Unico admin: per cambiargli ruolo serve prima un altro admin.</div>' : ''}
    <div class="sec" style="margin-top:40px">Notifiche</div>
    ${Object.entries(TOPICS)
      .map(([key, t]) => {
        const on = u.topics.includes(key);
        return `<div class="setrow"><div><b>${t.label}</b><span class="d">${t.desc}</span></div><button class="switch" role="switch" aria-checked="${on}" aria-label="Notifiche ${t.label}" data-action="toggle-topic" data-user="${u.id}" data-topic="${key}"></button></div>`;
      })
      .join('')}`;
}

function renderUsers() {
  const users = sortedUsers();
  if (!users.length) {
    return `<section class="card rise"><div class="empty">${icon('users')}<span>Nessun utente. Aggiungine uno con il suo ID Telegram.</span></div></section>`;
  }
  if (!users.some((u) => u.id === state.selectedUser)) state.selectedUser = users[0].id;
  const selected = users.find((u) => u.id === state.selectedUser);
  return `<div class="users-grid">
    <section class="card ulist rise" style="--i:0">${users.map(userRow).join('')}</section>
    <section class="card udetail rise" style="--i:1" id="user-detail"><div class="fade-in" data-key="user-${selected.id}">${userDetail(selected)}</div></section>
  </div>`;
}

function deviceCard(d, i) {
  const meta = DEVICE_STATES[d.state] || { label: d.state, cls: '' };
  const current = CONFIG.mode === 'lan' && CONFIG.device && CONFIG.device === d.name;
  return `<section class="card device-card rise" style="--i:${Math.min(i + 1, 5)}">
    <div class="head">
      <div class="row grow"><span class="sico">${icon('device')}</span><div class="grow"><div class="title" style="font-size:19px">${esc(d.name)}</div><div class="sub">${current ? 'questo dispositivo' : d.createdBy ? 'creato da ' + esc(d.createdBy) : 'creato dal pannello'}</div></div></div>
      <span class="badge ${meta.cls}">${meta.label}</span>
    </div>
    <div>
      <div class="kv"><span>Ultimo accesso</span><b>${d.lastSeenAt ? relative(d.lastSeenAt) : 'mai'}</b></div>
      <div class="kv"><span>Creato</span><b>${fmtDate(d.createdAt)} ${fmtTime(d.createdAt)}</b></div>
      ${d.pairingExpiresAt && d.state === 'pending' ? `<div class="kv"><span>Link valido fino alle</span><b>${fmtTime(d.pairingExpiresAt)}</b></div>` : ''}
    </div>
    <div class="button-row">
      ${d.state === 'active' ? '' : `<button class="btn s" data-action="refresh-device" data-device="${esc(d.id)}">${icon('link')}Nuovo link</button>`}
      <button class="btn s danger-g" data-action="revoke-device" data-device="${esc(d.id)}" data-name="${esc(d.name)}">${icon('trash')}Revoca</button>
    </div>
  </section>`;
}

function renderDevices() {
  const devices = state.overview.devices || [];
  const self =
    CONFIG.mode === 'lan'
      ? `<section class="card inline rise" style="--i:0"><span class="sico">${icon('device')}</span><div class="grow"><div class="title">Questo dispositivo</div><div class="sub">${esc(CONFIG.device || 'Accesso dalla rete locale')}</div></div><button class="btn g" data-action="logout">${icon('logout')}Esci</button></section>`
      : '';
  return `<div class="stack">
    ${self}
    ${devices.length ? `<div class="device-grid">${devices.map(deviceCard).join('')}</div>` : `<section class="card rise" style="--i:1"><div class="empty">${icon('device')}<span>Nessun dispositivo. Aggiungine uno per usare il pannello da un tablet o un telefono.</span></div></section>`}
  </div>`;
}

function healthRow(ic, tone, label, value) {
  return `<div class="hrow"><span class="sico ${tone}">${icon(ic)}</span><b>${label}</b><span class="v">${value}</span></div>`;
}

function renderSystem() {
  const o = state.overview;
  const health = o.health;
  const usage = o.usage;
  const sensors = o.temperatures.sensors;
  const localOk = o.temperatures.localStatus === 'Ok';
  const stale = sensors.filter((s) => sensorState(s) === 'stale').length;
  const failed = sensors.filter((s) => sensorState(s) === 'error').length;
  const problems = (localOk ? 0 : 1) + stale + failed;
  const sensorBadge =
    !problems ? 'Tutti ok' : localOk && !failed ? `${stale} in ritardo` : `${problems} ${plural(problems, 'problema', 'problemi')}`;
  const localValue = localOk
    ? o.temperatures.local !== null
      ? `<span class="mono">${fmt(o.temperatures.local)}°</span>`
      : 'ok'
    : `${o.temperatures.local !== null ? `<span class="mono">${fmt(o.temperatures.local)}°</span> · ` : ''}${esc(localStatusLabel(o.temperatures.localStatus))}`;
  const pct = usage.weeklyBudgetUsd > 0 ? Math.min(100, (usage.costUsd / usage.weeklyBudgetUsd) * 100) : 0;
  const update = o.update;
  const w = o.weather;
  const cpu = health.cpuTemperatureCelsius;
  const updFailed = update.result === 'failed' || update.result === 'rolled-back';
  const updTone = updFailed ? 'bad' : update.result === 'available' ? 'warn' : update.result ? 'ok' : '';
  const updLabel = !update.result
    ? 'mai eseguito'
    : updFailed
      ? update.result
      : update.result === 'available'
        ? 'disponibile'
        : update.result === 'skipped'
          ? 'saltato'
          : 'aggiornato';

  return `<div class="stack">
    <section class="card rise" style="--i:0;gap:4px">
      <div class="head mb"><span class="title">Aspetto</span><span class="badge">Salvato su questo dispositivo</span></div>
      <div class="setrow tall"><div><b>Tema</b><span class="d">Automatico segue il dispositivo</span></div>${seg('pref-theme', [['auto', 'Automatico', 'auto'], ['dark', 'Scuro', 'moon'], ['light', 'Chiaro', 'sun']], prefs.theme)}</div>
      <div class="setrow tall"><div><b>Animazioni</b><span class="d">Ridotte spegne lo sfondo animato</span></div>${seg('pref-motion', [['full', 'Complete'], ['calm', 'Ridotte']], prefs.motion)}</div>
      <div class="setrow tall"><div><b>Riposo schermo</b><span class="d">Orologio e temperature dopo un periodo senza tocchi</span></div><div class="setrow-actions">${seg('pref-idle', [['2', '2 min'], ['5', '5 min'], ['0', 'Mai']], prefs.idle)}<button class="btn s g" data-action="idle-test">${icon('screen')}Prova</button></div></div>
    </section>

    <div class="grid-2">
      <section class="card tight rise" style="--i:1">
        <div class="head mb"><span class="title">Salute</span><span class="badge ${health.coreOk ? 'ok' : 'bad'}">${health.coreOk ? 'Core attivo' : 'Degradato'}</span></div>
        <div>
          ${healthRow('send', health.telegramOk ? 'g' : 'r', 'Telegram', health.telegramOk ? 'Connesso' : 'Non connesso')}
          ${healthRow('db', health.databaseOk ? 'g' : 'r', 'Database', health.databaseOk ? 'Scrivibile' : 'Non scrivibile')}
          ${healthRow('bell', health.panelStoreOk ? 'g' : 'r', 'Registro eventi', health.panelStoreOk ? 'Attivo' : 'Non disponibile')}
          ${healthRow('cloud', w ? 'b' : '', 'Meteo esterno', w ? 'Open-Meteo · ' + relative(w.updatedAt) : 'Non disponibile')}
          ${healthRow('cpu', cpu > 70 ? 'a' : '', 'CPU', cpu ? `<span class="mono">${fmt(cpu)}°</span>` : '—')}
          ${healthRow('clock', '', 'Attivo da', uptime(o.system.uptimeSeconds))}
          ${healthRow('activity', '', 'Memoria', `<span class="mono">${o.system.memoryMb} MB</span>`)}
        </div>
      </section>

      <section class="card tight rise" style="--i:2">
        <div class="head mb"><span class="title">Sensori</span><span class="badge ${problems ? 'warn' : 'ok'}">${sensorBadge}</span></div>
        <div>
          ${healthRow('heat', localOk ? 'g' : 'a', 'Sensore locale', localValue)}
          ${sensors
            .map((s) => {
              const st = sensorState(s);
              const value =
                st === 'error'
                  ? `<span class="mono">${esc(s.error || s.status)}</span>`
                  : `<span class="mono">${fmt(s.temperature)}°</span> · ${relative(s.timestamp)}`;
              return healthRow('wifi', st === 'error' ? 'r' : st === 'ok' ? 'b' : st === 'stale' ? 'a' : '', esc(s.location), value);
            })
            .join('')}
          ${healthRow('cloud', w ? 'b' : '', 'Esterno (Open-Meteo)', w ? `<span class="mono">${fmt(w.temperature)}°</span>${w.humidity !== null && w.humidity !== undefined ? ` · ${w.humidity}%` : ''} · ${relative(w.updatedAt)}` : 'Non disponibile')}
        </div>
      </section>
    </div>

    <div class="grid-2">
      <section class="card rise" style="--i:3;gap:20px">
        <div class="head"><span class="title">Assistente AI</span><span class="badge ${pct > 80 ? 'warn' : 'ok'}">${pct.toFixed(0)}% del budget</span></div>
        <div class="mono" style="font-size:32px;letter-spacing:-0.02em">$${usage.costUsd.toFixed(2)} <span style="font-size:18px;color:var(--muted)">/ $${usage.weeklyBudgetUsd} in 7 giorni</span></div>
        <div class="usage-bar ${pct > 80 ? 'high' : ''}"><i style="transform:scaleX(${(pct / 100).toFixed(3)})"></i></div>
        <div class="note">${new Intl.NumberFormat('it-IT').format(usage.calls)} chiamate · ${(usage.tokens / 1000).toFixed(1)}k token · ${(usage.cachedTokens / 1000).toFixed(1)}k da cache</div>
      </section>

      <section class="card rise" style="--i:4;gap:16px">
        <div class="head"><span class="title">Aggiornamento</span><span class="badge ${updTone}">${esc(updLabel)}</span></div>
        <div>
          <div class="kv"><span>Canale</span><b>release</b></div>
          <div class="kv"><span>Versione</span><b class="mono">${esc(o.system.version === 'locale' ? 'build locale' : String(o.system.version || 'non disponibile'))}</b></div>
          <div class="kv"><span>Aggiornato</span><b>${update.builtAtUtc ? `${fmtDate(update.builtAtUtc)} · ${fmtTime(update.builtAtUtc)}` : '—'}</b></div>
          <div class="kv"><span>Ultima verifica</span><b>${update.lastRunUtc ? `${fmtDate(update.lastRunUtc)} · ${fmtTime(update.lastRunUtc)}` : '—'}</b></div>
          ${update.reason && !/^already at/i.test(update.reason) ? `<div class="kv"><span>Dettaglio</span><b>${esc(update.reason)}</b></div>` : ''}
        </div>
        <button class="btn g" data-action="refresh">${icon('refresh')}Aggiorna dati</button>
      </section>
    </div>
  </div>`;
}

const VIEW_RENDERERS = {
  home: renderHome,
  heating: renderHeating,
  gates: renderGates,
  events: renderEvents,
  users: renderUsers,
  devices: renderDevices,
  system: renderSystem,
};

/* ---------- chart ---------- */

function availableSeries() {
  return [{ id: 'avg', label: 'Media' }, ...(state.history?.series || []).map((s) => ({ id: s.id, label: s.label }))];
}

function activeSeriesIds() {
  const available = new Set(availableSeries().map((s) => s.id));
  return state.chartSeries.filter((id) => available.has(id));
}

function seriesLabel(id) {
  return availableSeries().find((s) => s.id === id)?.label || 'Serie';
}

function seriesButtonLabel() {
  const ids = activeSeriesIds();
  if (!ids.length) return 'Nessuna serie';
  if (ids.length === 1) return seriesLabel(ids[0]);
  if (ids.includes('avg')) return `Media + ${ids.length - 1}`;
  return `${ids.length} sensori`;
}

/** The average stays solid; each sensor gets a different dash pattern, since the UI is monochrome. */
function seriesDash(id) {
  if (id === 'avg') return '';
  const index = (state.history?.series || []).findIndex((s) => s.id === id);
  const dashes = ['7 5', '2 4', '10 4 2 4', '14 5', '4 3 1 3'];
  return dashes[Math.max(0, index) % dashes.length];
}

function seriesColor(id) {
  if (id === 'avg') return 'var(--series-avg)';
  const index = (state.history?.series || []).findIndex((s) => s.id === id);
  return `var(--series-${(Math.max(0, index) % 6) + 1})`;
}

function seriesSwatch(id) {
  const dash = seriesDash(id);
  return `<span class="pick-line" style="color:${seriesColor(id)}"><svg viewBox="0 0 30 8" aria-hidden="true"><line x1="1" y1="4" x2="29" y2="4" stroke="currentColor" stroke-width="2" stroke-linecap="round"${dash ? ` stroke-dasharray="${dash}"` : ''}/></svg></span>`;
}

function seriesPicker() {
  const ids = activeSeriesIds();
  const rows = availableSeries()
    .map((s) => {
      const on = ids.includes(s.id);
      return `<button type="button" class="picker-row ${on ? 'on' : ''}" data-action="series-item" data-series="${esc(s.id)}" role="checkbox" aria-checked="${on}">${seriesSwatch(s.id)}<span class="pick-label">${esc(s.label)}</span><span class="pick-check">${icon('check')}</span></button>`;
    })
    .join('');
  return `<div class="picker" id="series-picker">
    <button type="button" class="btn s g picker-btn" id="series-toggle" data-action="series-menu" aria-haspopup="true" aria-expanded="${state.seriesMenuOpen}">${icon('activity')}<span id="series-label">${esc(seriesButtonLabel())}</span>${icon('chevron', 'pick-arrow')}</button>
    <div class="picker-menu" id="series-menu"${state.seriesMenuOpen ? '' : ' hidden'}>${rows}</div>
  </div>`;
}

function toggleSeriesMenu() {
  const menu = $('series-menu');
  const button = $('series-toggle');
  if (!menu || !button) return;
  state.seriesMenuOpen = !state.seriesMenuOpen;
  menu.hidden = !state.seriesMenuOpen;
  button.setAttribute('aria-expanded', String(state.seriesMenuOpen));
}

function closeSeriesMenu() {
  const menu = $('series-menu');
  const button = $('series-toggle');
  state.seriesMenuOpen = false;
  if (menu && button && !menu.hidden) {
    menu.hidden = true;
    button.setAttribute('aria-expanded', 'false');
  }
}

function toggleSeries(id) {
  const index = state.chartSeries.indexOf(id);
  if (index >= 0) state.chartSeries.splice(index, 1);
  else state.chartSeries.push(id);
  updateSeriesUI();
}

function updateSeriesUI() {
  const ids = activeSeriesIds();
  document.querySelectorAll('#series-menu [data-series]').forEach((row) => {
    const on = ids.includes(row.dataset.series);
    row.classList.toggle('on', on);
    row.setAttribute('aria-checked', String(on));
  });
  const label = $('series-label');
  if (label) label.textContent = seriesButtonLabel();
  const body = $('chart-body');
  if (body) {
    body.innerHTML = temperatureChart();
    reapplyChartHover();
  }
}

/** Monotone cubic interpolation: passes through every point without overshooting. */
function monotoneModel(points) {
  const pts = points.filter((p, i, all) => i === 0 || p.x > all[i - 1].x);
  const n = pts.length;
  if (n < 2) return null;

  const dx = [];
  const delta = [];
  for (let i = 0; i < n - 1; i += 1) {
    const h = pts[i + 1].x - pts[i].x;
    dx.push(h);
    delta.push((pts[i + 1].y - pts[i].y) / h);
  }

  const m = new Array(n);
  m[0] = delta[0];
  m[n - 1] = delta[n - 2];
  for (let i = 1; i < n - 1; i += 1) {
    if (delta[i - 1] * delta[i] <= 0) {
      m[i] = 0;
    } else {
      const w1 = 2 * dx[i] + dx[i - 1];
      const w2 = dx[i] + 2 * dx[i - 1];
      m[i] = (w1 + w2) / (w1 / delta[i - 1] + w2 / delta[i]);
    }
  }

  for (let i = 0; i < n - 1; i += 1) {
    if (delta[i] === 0) {
      m[i] = 0;
      m[i + 1] = 0;
      continue;
    }
    const a = m[i] / delta[i];
    const b = m[i + 1] / delta[i];
    const s = a * a + b * b;
    if (s > 9) {
      const t = 3 / Math.sqrt(s);
      m[i] = t * a * delta[i];
      m[i + 1] = t * b * delta[i];
    }
  }
  return { pts, dx, m };
}

function monotonePath(points) {
  const model = monotoneModel(points);
  if (!model) return '';
  const { pts, dx, m } = model;
  let d = `M${pts[0].x.toFixed(1)},${pts[0].y.toFixed(1)}`;
  for (let i = 0; i < pts.length - 1; i += 1) {
    const c1x = pts[i].x + dx[i] / 3;
    const c1y = pts[i].y + (m[i] * dx[i]) / 3;
    const c2x = pts[i + 1].x - dx[i] / 3;
    const c2y = pts[i + 1].y - (m[i + 1] * dx[i]) / 3;
    d += ` C${c1x.toFixed(1)},${c1y.toFixed(1)} ${c2x.toFixed(1)},${c2y.toFixed(1)} ${pts[i + 1].x.toFixed(1)},${pts[i + 1].y.toFixed(1)}`;
  }
  return d;
}

function monotoneSamples(points, perSegment = 8) {
  const model = monotoneModel(points);
  if (!model) return points;
  const { pts, dx, m } = model;
  const out = [{ x: pts[0].x, y: pts[0].y }];
  for (let i = 0; i < pts.length - 1; i += 1) {
    for (let s = 1; s <= perSegment; s += 1) {
      const t = s / perSegment;
      const t2 = t * t;
      const t3 = t2 * t;
      const h00 = 2 * t3 - 3 * t2 + 1;
      const h10 = t3 - 2 * t2 + t;
      const h01 = -2 * t3 + 3 * t2;
      const h11 = t3 - t2;
      out.push({
        x: pts[i].x + dx[i] * t,
        y: h00 * pts[i].y + h10 * dx[i] * m[i] + h01 * pts[i + 1].y + h11 * dx[i] * m[i + 1],
      });
    }
  }
  return out;
}

let chartHover = null;
let chartHoverTimer = null;
let chartHoverX = null;

function hideChartHover(force = false) {
  if (!force && (chartDrag || Date.now() < chartLingerUntil)) return;
  clearTimeout(chartHoverTimer);
  chartHoverX = null;
  document.querySelectorAll('.chart-cursor, .chart-tip').forEach((el) => {
    el.hidden = true;
  });
}

function updateChartHover(event) {
  const plot = event.target.closest('.chart-plot');
  if (!plot) {
    hideChartHover();
    return;
  }
  chartHoverX = event.clientX;
  showChartHoverAt(plot, event.clientX);
}

function reapplyChartHover() {
  if (chartHoverX == null) return;
  const plot = document.querySelector('.chart-plot');
  if (!plot || !chartHover) return;
  showChartHoverAt(plot, chartHoverX);
}

function showChartHoverAt(plot, clientX) {
  if (!chartHover) return;
  const cursor = plot.querySelector('.chart-cursor');
  const tip = plot.querySelector('.chart-tip');
  if (!cursor || !tip) return;

  const rect = plot.getBoundingClientRect();
  if (rect.width <= 0) return;
  const ratio = Math.min(1, Math.max(0, (clientX - rect.left) / rect.width));
  const targetMs = chartHover.t0 + ratio * chartHover.span;
  let nearest = chartHover.ref[0];
  for (const point of chartHover.ref) {
    if (Math.abs(point.ms - targetMs) < Math.abs(nearest.ms - targetMs)) nearest = point;
  }

  const lineLeft = ratio * rect.width;
  cursor.hidden = false;
  cursor.style.left = `${lineLeft}px`;

  const rows = chartHover.series
    .map((series) => {
      let best = null;
      for (const point of series.points) {
        if (!best || Math.abs(point.ms - nearest.ms) < Math.abs(best.ms - nearest.ms)) best = point;
      }
      if (!best) return '';
      return `<div class="tip-row"><i style="background:${series.color}"></i><span>${esc(series.label)}</span><b>${fmt(best.value)}°</b></div>`;
    })
    .join('');

  const time = chartHover.bucket === 'day' ? fmtDate(nearest.at) : fmtTime(nearest.at);
  tip.innerHTML = `<span class="tip-time">${esc(time)}</span>${rows}`;
  tip.hidden = false;
  const tipWidth = tip.offsetWidth;
  let tipLeft = lineLeft + 14;
  if (tipLeft + tipWidth > rect.width) tipLeft = lineLeft - tipWidth - 14;
  tip.style.left = `${Math.max(4, tipLeft)}px`;
  tip.style.top = '8px';
}

function toggleChartFull() {
  state.chartFull = !state.chartFull;
  document.body.classList.toggle('chart-full', state.chartFull);
  render();
}

function chartHeight() {
  return state.chartFull ? Math.max(280, Math.round(window.innerHeight - 212)) : 240;
}

function temperatureChart() {
  chartHover = null;
  const data = state.history;
  if (!data) return `<div class="chart-empty"><span class="spinner"></span><span>Carico lo storico…</span></div>`;
  if (data.error) return `<div class="chart-empty">${icon('alert')}<span>${esc(data.error)}</span></div>`;

  const avgPoints = (data.points || []).map((p) => ({ at: new Date(p.at), avg: p.avg, min: p.min, max: p.max }));
  const drawAvg = state.chartSeries.includes('avg') && avgPoints.length > 0;
  const series = (data.series || []).filter((s) => state.chartSeries.includes(s.id));
  if (!drawAvg && !series.length) return `<div class="chart-empty">${icon('activity')}<span>Seleziona almeno una serie dal menu.</span></div>`;

  const stamps = [];
  if (drawAvg) stamps.push(avgPoints[0].at.getTime(), avgPoints[avgPoints.length - 1].at.getTime());
  for (const s of series) for (const p of s.points) stamps.push(new Date(p.at).getTime());
  if (!stamps.length) return `<div class="chart-empty">${icon('activity')}<span>Nessun dato per questo periodo.</span></div>`;
  const t0 = Math.min(...stamps);
  const t1 = Math.max(...stamps);
  const span = t1 - t0 || 1;

  const values = [];
  if (drawAvg) for (const p of avgPoints) values.push(p.min, p.max);
  for (const s of series) for (const p of s.points) values.push(p.value);
  if (!values.length) return `<div class="chart-empty">${icon('activity')}<span>Nessun dato per questo periodo.</span></div>`;

  const min = Math.min(...values);
  const max = Math.max(...values);
  let yMin = Math.floor((min - 0.5) * 2) / 2;
  let yMax = Math.ceil((max + 0.5) * 2) / 2;
  if (yMax - yMin < 2) {
    yMin -= 1;
    yMax += 1;
  }

  const W = 640;
  const H = chartHeight();
  const x = (ms) => ((ms - t0) / span) * W;
  const y = (v) => H - ((v - yMin) / (yMax - yMin)) * H;

  const asPoints = (list, value) => list.map((p) => ({ x: x(new Date(p.at).getTime()), y: y(value(p)) }));
  const band = drawAvg
    ? (() => {
        const upper = monotoneSamples(asPoints(avgPoints, (p) => p.max));
        const lower = monotoneSamples(asPoints(avgPoints, (p) => p.min)).slice().reverse();
        if (upper.length < 2 || lower.length < 2) return '';
        const upperPath = upper.map((p, i) => `${i ? 'L' : 'M'}${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ');
        const lowerPath = lower.map((p) => `L${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ');
        return `${upperPath} ${lowerPath} Z`;
      })()
    : '';
  const avgLine = drawAvg ? monotonePath(asPoints(avgPoints, (p) => p.avg)) : '';
  const sensorLines = series
    .map((s) => {
      const points = asPoints(s.points, (p) => p.value);
      if (!points.length) return '';
      const color = seriesColor(s.id);
      if (points.length < 2) return '';
      const dash = seriesDash(s.id);
      return `<path d="${monotonePath(points)}" fill="none" stroke="currentColor" style="color:${color}" stroke-width="1.8" stroke-opacity=".95" stroke-linecap="round" stroke-linejoin="round"${dash ? ` stroke-dasharray="${dash}"` : ''} vector-effect="non-scaling-stroke"/>`;
    })
    .join('');
  // Samples are HTML dots, not SVG circles: the chart stretches a 640 unit viewBox across the
  // full width, so a circle in user units would render as an ellipse.
  const sampleDots = (points, color) =>
    points
      .map(
        (point) =>
          `<i class="chart-sample" aria-hidden="true" style="left:${((point.x / W) * 100).toFixed(2)}%;top:${point.y.toFixed(1)}px;background:${color}"></i>`
      )
      .join('');
  const avgDots = drawAvg ? sampleDots(asPoints(avgPoints, (p) => p.avg), 'var(--series-avg)') : '';
  const sensorDots = series
    .map((s) => sampleDots(asPoints(s.points, (p) => p.value), seriesColor(s.id)))
    .join('');

  const grid = [1, 2, 3].map((i) => `<line x1="0" y1="${(H / 4) * i}" x2="${W}" y2="${(H / 4) * i}" stroke="currentColor" stroke-opacity=".07" vector-effect="non-scaling-stroke"/>`).join('');

  const yLabels = [0, 1, 2, 3, 4].map((i) => `<span>${(yMax - ((yMax - yMin) / 4) * i).toFixed(1)}°</span>`).join('');
  const tickCount = data.bucket === 'day' ? 4 : 5;
  const xLabels = [];
  for (let i = 0; i < tickCount; i += 1) {
    const at = new Date(t0 + (span * i) / (tickCount - 1));
    xLabels.push(data.bucket === 'day' ? fmtDate(at) : fmtTime(at));
  }

  const last = drawAvg ? avgPoints[avgPoints.length - 1] : null;
  const avg = drawAvg ? avgPoints.reduce((sum, p) => sum + p.avg, 0) / avgPoints.length : null;
  const avgMin = drawAvg ? Math.min(...avgPoints.map((p) => p.min)) : null;
  const avgMax = drawAvg ? Math.max(...avgPoints.map((p) => p.max)) : null;
  const w = weather();
  const outdoor = state.historyRange === 'today' && w && w.min !== null && w.min !== undefined ? `<span>Esterno <b>${fmt(w.min)}–${fmt(w.max)}°</b></span>` : '';
  const key = `chart|${data.range}|${state.chartSeries.join(',')}`;
  const stats = drawAvg
    ? `<span>Min <b>${fmt(avgMin)}°</b></span><span>Max <b>${fmt(avgMax)}°</b></span><span>Media <b>${fmt(avg)}°</b></span><span>${avgPoints.length} ${data.bucket === 'day' ? 'giorni' : 'ore'}</span>`
    : `<span>${series.length} serie</span>`;

  const hoverSeries = [];
  if (drawAvg)
    hoverSeries.push({
      id: 'avg',
      label: 'Media',
      color: seriesColor('avg'),
      points: avgPoints.map((p) => ({ ms: p.at.getTime(), at: p.at, value: p.avg })),
    });
  for (const s of series) {
    hoverSeries.push({
      id: s.id,
      label: s.label,
      color: seriesColor(s.id),
      points: s.points.map((p) => {
        const at = new Date(p.at);
        return { ms: at.getTime(), at, value: p.value };
      }),
    });
  }
  const hoverRef = hoverSeries[0]?.points || [];
  chartHover = hoverRef.length ? { t0, span, bucket: data.bucket, ref: hoverRef, series: hoverSeries } : null;

  return `<div data-key="${esc(key)}">
    <div class="chart" style="--chart-h:${H}px">
      <div class="chart-y">${yLabels}</div>
      <div class="chart-plot">
        <svg class="reveal" viewBox="0 0 ${W} ${H}" preserveAspectRatio="none" aria-hidden="true">
          ${grid}
          ${band ? `<path d="${band}" fill="currentColor" style="color:var(--series-avg)" fill-opacity=".08"/>` : ''}
          ${sensorLines}
          ${avgLine ? `<path d="${avgLine}" fill="none" stroke="currentColor" style="color:var(--series-avg)" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round" vector-effect="non-scaling-stroke"/>` : ''}
        </svg>
        ${avgDots}${sensorDots}
        ${last ? `<span class="chart-dot" style="left:${((x(last.at.getTime()) / W) * 100).toFixed(2)}%;top:${y(last.avg).toFixed(1)}px;background:var(--series-avg)"></span>` : ''}
        <span class="chart-cursor" hidden></span>
        <div class="chart-tip" hidden></div>
      </div>
    </div>
    <div class="chart-x" style="margin-left:50px">${xLabels.map((l) => `<span>${l}</span>`).join('')}</div>
  </div>
  <div class="chart-summary">${stats}${outdoor}</div>`;
}

/* ---------- live tick ---------- */

function tickLive() {
  const now = new Date(nowMs());
  const time = fmtTime(now);
  const date = now.toLocaleDateString('it-IT', { weekday: 'short', day: 'numeric', month: 'short' });
  if ($('clock').textContent !== time) $('clock').textContent = time;
  if ($('clock-date').textContent !== date) $('clock-date').textContent = date;
  if ($('idle-clock').textContent !== time) $('idle-clock').textContent = time;
  const longDate = now.toLocaleDateString('it-IT', { weekday: 'long', day: 'numeric', month: 'long' });
  if ($('idle-date').textContent !== longDate) $('idle-date').textContent = longDate;

  const g = gates();
  const info = windowInfo(g);
  if (g?.open && !info) {
    refresh();
    return;
  }
  if (info && info.left <= 0) {
    refresh();
    return;
  }
  if (info) {
    const left = document.querySelector('[data-win-left]');
    if (left && left.textContent !== info.label) left.textContent = info.label;
    const bar = document.querySelector('[data-win-bar]');
    if (bar) {
      bar.classList.toggle('low', info.low);
      bar.firstElementChild.style.transform = `scaleX(${info.fraction.toFixed(4)})`;
    }
  }
}

/* ---------- idle screen ---------- */

function idleSensors() {
  const t = temperatures();
  const list = [];
  if (t && t.localStatus === 'Ok' && t.local !== null) list.push(['Locale', t.local]);
  for (const s of t?.sensors || []) {
    if (s.fresh && !s.informational && !s.status) list.push([s.location, s.temperature]);
  }
  return list;
}

function renderIdle() {
  const side = $('idle-side');
  if (!state.overview) return;
  const h = heating();
  const w = weather();
  const info = w ? weatherInfo(w.code, w.isDay) : { icon: 'cloud', label: 'Esterno' };
  const on = boilerOn();
  const g = gates();
  const interval = currentInterval();
  const hours = Math.round(((h?.program.intervals || []).reduce((sum, i) => sum + (i.to - i.from), 0)) / 60);
  const sensors = idleSensors();
  const open = !!(g && g.open);
  const gatesText = open && g.expiresAt ? `Aperti fino alle ${fmtTime(new Date(g.expiresAt))}` : open ? 'Aperti' : 'Bloccati';
  const heatText = interval ? 'Riscaldamento in corso' : hours ? `${hours} h di riscaldamento` : 'Nessun programma';
  const chips = `<div class="idle-chips">
      <span class="ichip ${open ? 'g' : ''}">${icon(open ? 'unlock' : 'lock')}${gatesText}</span>
      <span class="ichip ${on ? 'a' : ''}">${icon('flame')}${on ? 'Caldaia accesa' : 'Caldaia spenta'}</span>
      <span class="ichip">${icon('clock')}${heatText}</span>
    </div>`;
  const sensorRow = sensors.length
    ? `<div class="idle-sensors"><b>${plural(sensors.length, 'sensore', 'sensori')}</b>${sensors
        .map(([name, value]) => `<span><i>${esc(name)}</i><b class="mono">${fmt(value)}°</b></span>`)
        .join('')}</div>`
    : '';
  const html = `<div class="mini"><span class="sico ${on ? 'a' : ''}">${icon('flame')}</span><div><div class="k">In casa</div><div class="mono big"><span data-temp>${fmt(state.shownTemp ?? h.temperature)}</span>°</div></div></div>
    <div class="mini"><span class="sico b">${icon(info.icon)}</span><div><div class="k">Fuori${w ? ' · ' + info.label.toLowerCase() : ''}</div><div class="mono big">${w ? fmt(w.temperature) + '°' : '—'}</div></div></div>
    ${chips}
    ${sensorRow}`;
  morph(side, html);

  const badge = $('idle-alert');
  const count = alerts().length;
  badge.hidden = count === 0;
  if (count) badge.innerHTML = `${icon('bell')}<b>${plural(count, 'avviso', 'avvisi')}</b>`;
}

function showIdle() {
  if (state.idle || !state.overview) return;
  state.idle = true;
  renderIdle();
  $('idle-aura').classList.toggle('show', prefs.motion === 'full');
  $('idle').classList.add('show');
  $('idle').setAttribute('aria-hidden', 'false');
}

function hideIdle() {
  if (!state.idle) return;
  state.idle = false;
  $('idle').classList.remove('show');
  $('idle').setAttribute('aria-hidden', 'true');
  setTimeout(() => {
    if (!state.idle) $('idle-aura').classList.remove('show');
  }, 800);
}

function checkIdle() {
  if (state.idle || prefs.idle === '0' || !state.overview || isSheetOpen() || state.gateAnim) return;
  if (Date.now() - state.lastInput > Number(prefs.idle) * MIN) showIdle();
}

/* ---------- theme ---------- */

function applyTheme() {
  const dark = prefs.theme === 'auto' ? (darkQuery ? darkQuery.matches : true) : prefs.theme === 'dark';
  const root = document.documentElement;
  root.dataset.theme = dark ? 'dark' : 'light';
  root.classList.toggle('calm', prefs.motion === 'calm');
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute('content', dark ? '#0a0b0b' : '#f4f4f2');
}

function cycleTheme() {
  const order = ['auto', 'dark', 'light'];
  savePref('theme', order[(order.indexOf(prefs.theme) + 1) % order.length]);
  applyTheme();
  render();
  toast(prefs.theme === 'auto' ? 'Tema automatico' : prefs.theme === 'dark' ? 'Tema scuro' : 'Tema chiaro');
}

/* ---------- sheet and toast ---------- */

let confirmAction = null;
let sheetTimer = null;
let toastTimer = null;
let toastHide = null;
let qrTimer = null;

function toast(message, tone = 'ok') {
  const el = $('toast');
  clearTimeout(toastTimer);
  clearTimeout(toastHide);
  $('toast-text').textContent = message;
  const iconName = tone === 'error' ? 'alert' : tone === 'loading' ? 'loader' : 'check';
  $('toast-icon').innerHTML = `<use href="#i-${iconName}"></use>`;
  el.classList.toggle('error', tone === 'error');
  el.classList.toggle('loading', tone === 'loading');
  el.hidden = false;
  requestAnimationFrame(() => el.classList.add('show'));
  toastTimer = setTimeout(hideToast, tone === 'loading' ? 20000 : 2600);
}

function hideToast() {
  clearTimeout(toastTimer);
  clearTimeout(toastHide);
  const el = $('toast');
  el.classList.remove('show');
  toastHide = setTimeout(() => {
    el.hidden = true;
    el.classList.remove('loading', 'error');
  }, 400);
}

function isSheetOpen() {
  return !$('sheet').hidden;
}

function fillSheet(title, body, content, actions) {
  $('sheet-title').textContent = title;
  $('sheet-body').textContent = body || '';
  $('sheet-content').innerHTML = content || '';
  $('sheet-actions').innerHTML = actions || '';
}

function showSheet() {
  const sheet = $('sheet');
  const backdrop = $('backdrop');
  clearTimeout(sheetTimer);
  sheet.hidden = false;
  backdrop.hidden = false;
  requestAnimationFrame(() => {
    sheet.classList.add('open');
    backdrop.classList.add('open');
    placePills();
  });
  // Only fields take focus: a focused button would show its ring on a touch screen.
  const focus = sheet.querySelector('input');
  if (focus) {
    setTimeout(() => {
      if (sheet.classList.contains('open')) focus.focus({ preventScroll: true });
    }, 260);
  }
}

function closeSheet() {
  const sheet = $('sheet');
  const backdrop = $('backdrop');
  sheet.classList.remove('open');
  backdrop.classList.remove('open');
  confirmAction = null;
  clearTimeout(sheetTimer);
  clearInterval(qrTimer);
  qrTimer = null;
  sheetTimer = setTimeout(() => {
    sheet.hidden = true;
    backdrop.hidden = true;
    fillSheet('', '', '', '');
  }, 300);
}

function openConfirm({ title, body, label = 'Conferma', danger = false, ic = '', onConfirm }) {
  fillSheet(
    title,
    body,
    '',
    `<button class="btn g" data-action="close-sheet">Annulla</button><button class="btn ${danger ? 'danger' : 'p'}" data-action="confirm">${ic ? icon(ic) : ''}${label}</button>`
  );
  showSheet();
  confirmAction = onConfirm;
}

function openAlerts() {
  const list = alerts();
  if (!list.length) {
    toast(state.offline ? 'Bot non raggiungibile' : 'Nessun avviso attivo');
    return;
  }
  fillSheet(
    'Avvisi attivi',
    '',
    `<div>${list
      .map(
        (a) =>
          `<div class="alert-row"><span class="sico a">${icon('alert')}</span><div class="grow"><b>${esc(a.title)}</b><span>${esc(a.detail)}</span></div><button class="btn s g" data-action="go" data-target="${esc(a.target)}">Apri</button></div>`
      )
      .join('')}</div>`,
    `<button class="btn g" data-action="close-sheet">Chiudi</button>`
  );
  showSheet();
}

function openMore() {
  fillSheet(
    'Altre sezioni',
    '',
    `<div class="more-list">
      <button class="btn" data-nav="users">${icon('users')}Utenti</button>
      <button class="btn" data-nav="devices">${icon('device')}Dispositivi</button>
      <button class="btn" data-nav="system">${icon('gear')}Sistema</button>
    </div>`,
    `<button class="btn g" data-action="close-sheet">Chiudi</button>`
  );
  showSheet();
}

/* ---------- heating actions ---------- */

async function setBoiler(on) {
  const h = heating();
  if (!h || on === boilerOn()) return;
  const interval = currentInterval();
  if (interval && !h.program.suspended) {
    toast('Programma in corso: la caldaia è gestita dal programma', 'error');
    return;
  }
  await optimistic({
    pending: on ? 'Accendo la caldaia…' : 'Spengo la caldaia…',
    apply: () => {
      state.boilerPending = on;
    },
    request: () => write(on ? '/heating/on' : '/heating/off', { method: 'POST' }),
    success: on ? 'Caldaia accesa' : 'Caldaia spenta',
  });
  state.boilerPending = null;
  render();
}

async function toggleSuspend() {
  const h = heating();
  if (!h) return;
  const suspended = !h.program.suspended;
  await optimistic({
    pending: suspended ? 'Sospendo il programma…' : 'Riprendo il programma…',
    apply: (overview) => {
      overview.heating.program.suspended = suspended;
    },
    request: () => write('/heating/suspended', { method: 'PUT', body: { suspended } }),
    success: suspended ? 'Programma sospeso' : 'Programma ripreso',
  });
}

function confirmDeleteProgram() {
  openConfirm({
    title: 'Rimuovere il programma?',
    body: 'La caldaia smette di seguire un orario. Le fasce salvate vengono eliminate.',
    label: 'Rimuovi',
    danger: true,
    onConfirm: async () => {
      await optimistic({
        pending: 'Rimuovo il programma…',
        apply: (overview) => {
          overview.heating.program = { suspended: false, hasProgram: false, intervals: [] };
        },
        request: () => write('/heating/program', { method: 'DELETE' }),
        success: 'Programma rimosso',
      });
    },
  });
}

function editorRow(from, to, temperature) {
  return `<div class="editor-row">
    <label class="field"><span>Dalle</span><input class="from" type="time" value="${minutesToTime(from)}"></label>
    <label class="field"><span>Alle</span><input class="to" type="time" value="${minutesToTime(to)}"></label>
    <label class="field"><span>°C</span><input class="temp" type="number" inputmode="decimal" min="5" max="30" step="0.5" value="${temperature}"></label>
    <button class="btn s g square danger-g" data-action="remove-interval" aria-label="Rimuovi fascia">${icon('trash')}</button>
  </div>`;
}

function openProgramEditor() {
  const h = heating();
  fillSheet(
    'Programma',
    'Fasce orarie e temperatura. Non possono sovrapporsi.',
    `<div class="form"><div class="editor" id="editor">${h.program.intervals.map((i) => editorRow(i.from, i.to, i.temperature)).join('')}</div>
      <div class="editor-actions"><button class="btn s g" data-action="add-interval">${icon('plus')}Aggiungi fascia</button>${h.program.hasProgram ? `<button class="btn s danger-g" data-action="delete-program">${icon('trash')}Rimuovi programma</button>` : ''}</div>
      <p class="form-error" id="editor-error"></p></div>`,
    `<button class="btn g" data-action="close-sheet">Annulla</button><button class="btn p" data-action="save-program">Salva</button>`
  );
  showSheet();
}

function addIntervalRow() {
  const editor = $('editor');
  if (!editor) return;
  const rows = editor.querySelectorAll('.editor-row');
  let from = 6 * 60;
  if (rows.length) {
    const lastTo = timeToMinutes(rows[rows.length - 1].querySelector('input.to').value);
    if (lastTo !== null) from = Math.min(lastTo + 60, 22 * 60);
  }
  editor.insertAdjacentHTML('beforeend', editorRow(from, Math.min(from + 120, 1440), 20));
}

async function saveProgram() {
  const rows = Array.from(document.querySelectorAll('#editor .editor-row'));
  const error = $('editor-error');
  error.textContent = '';
  const intervals = [];

  for (const row of rows) {
    const from = timeToMinutes(row.querySelector('input.from').value);
    const to = timeToMinutes(row.querySelector('input.to').value);
    const temperature = Number(String(row.querySelector('input.temp').value).replace(',', '.'));
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
    error.textContent = 'Aggiungi almeno una fascia, oppure rimuovi il programma.';
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
    const result = await optimistic({
      pending: 'Applico il programma…',
      apply: (overview) => {
        overview.heating.program = {
          suspended: false,
          hasProgram: true,
          intervals: intervals.map((i) => ({ from: i.from, to: i.to, temperature: i.temperature })),
        };
      },
      request: () => write('/heating/program', { method: 'PUT', body: { intervals } }),
      success: 'Programma aggiornato',
      failure: (e) => {
        error.textContent = e.message;
      },
    });
    if (result) closeSheet();
  } catch (e) {
    error.textContent = e.message;
  }
}

function openCustomRange() {
  const now = new Date(nowMs());
  const from = new Date(now.getTime() - 7 * 86400000);
  const pad = (n) => String(n).padStart(2, '0');
  const toInput = (d) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  fillSheet(
    'Intervallo personalizzato',
    'Massimo un anno.',
    `<div class="form">
      <label class="field"><span>Dal</span><input id="range-from" type="datetime-local" value="${state.customFrom || toInput(from)}"></label>
      <label class="field"><span>Al</span><input id="range-to" type="datetime-local" value="${state.customTo || toInput(now)}"></label>
      <p class="form-error" id="range-error"></p>
    </div>`,
    `<button class="btn g" data-action="close-sheet">Annulla</button><button class="btn p" data-action="apply-range">Mostra</button>`
  );
  showSheet();
}

async function applyRange() {
  const from = $('range-from').value;
  const to = $('range-to').value;
  const error = $('range-error');
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
  state.historyLoading = true;
  closeSheet();
  render();
  await loadHistory();
}

async function changeRange(range) {
  if (range === 'custom') {
    openCustomRange();
    return;
  }
  if (range === state.historyRange) return;
  state.historyRange = range;
  state.historyLoading = true;
  render();
  await loadHistory();
}

/* ---------- gate actions ---------- */

function gateName(gate) {
  return gate === 'pedestrian' ? 'pedonale' : 'automatico';
}

function confirmOpenGate(gate) {
  if (state.gateAnim) return;
  const g = gates();
  const automatic = gate === 'automatic';
  // A dedicated sheet: opening a gate is the one action that moves something in the real world,
  // so it gets the name, what happens next and the state of the access window.
  fillSheet(
    'Aprire il cancello',
    '',
    `<div class="gate-confirm">
      <span class="gate-confirm-ico">${icon(automatic ? 'car' : 'walk')}</span>
      <b>${automatic ? 'Cancello carraio' : 'Cancello pedonale'}</b>
      <small>Resta aperto pochi secondi, poi si richiude da solo.</small>
      ${g && g.open ? '<span class="badge ok">Finestra aperta</span>' : '<span class="badge">Finestra chiusa</span>'}
    </div>`,
    `<button class="btn g" data-action="close-sheet">Annulla</button><button class="btn p" data-action="confirm">${icon('gate')}Apri</button>`
  );
  showSheet();
  confirmAction = () => openGate(gate);
}

async function openGate(gate) {
  state.gateAnim = { gate, phase: 'opening' };
  render();
  const started = Date.now();
  const result = await optimistic({
    pending: `Apro il cancello ${gateName(gate)}…`,
    request: () => write('/gates/open', { method: 'POST', body: { gate } }),
    success: `Cancello ${gateName(gate)} aperto`,
  });
  if (result) {
    await wait(Math.max(0, GATE_OPEN_MS - (Date.now() - started)));
    state.gateAnim = { gate, phase: 'done' };
    render();
    await wait(GATE_DONE_MS);
  }
  state.gateAnim = null;
  render();
}

async function openWindow(hours) {
  await optimistic({
    pending: `Sblocco i cancelli per ${hours} h…`,
    apply: (overview) => {
      const now = nowMs();
      overview.gates.open = true;
      overview.gates.openedAt = new Date(now).toISOString();
      overview.gates.expiresAt = new Date(now + hours * 3600000).toISOString();
    },
    request: () => write('/gates/window', { method: 'PUT', body: { hours } }),
    success: `Cancelli sbloccati per ${hours} h`,
  });
}

async function lockGates() {
  await optimistic({
    pending: 'Blocco i cancelli…',
    apply: (overview) => {
      overview.gates.open = false;
      overview.gates.openedAt = null;
      overview.gates.expiresAt = null;
    },
    request: () => write('/gates/window', { method: 'DELETE' }),
    success: 'Cancelli bloccati',
  });
}

async function triggerLights() {
  if (lightsLit()) return;
  const until = Date.now() + LIGHTS_MS;
  setTimeout(render, LIGHTS_MS + 50);
  await optimistic({
    pending: 'Accendo le luci…',
    apply: () => {
      state.lightsUntil = until;
    },
    request: () => write('/gates/lights', { method: 'POST' }),
    success: 'Luci esterne accese',
    failure: () => {
      state.lightsUntil = 0;
    },
  });
}

/* ---------- user actions ---------- */

function findUser(id) {
  return state.overview.users.find((u) => u.id === id);
}

function roleSeg(current, action, extra = '') {
  return `<div class="seg full tall roles" role="group" aria-label="Ruolo"><span class="seg-pill" aria-hidden="true"></span>${ROLES.map(
    (r) => `<button type="button" class="${current === r.key ? 'on' : ''}" data-action="${action}" data-role="${r.key}" aria-pressed="${current === r.key}" ${extra}>${icon(r.icon, 'r-' + r.key)}${r.label}</button>`
  ).join('')}</div>`;
}

function openAddUser() {
  fillSheet(
    'Aggiungi utente',
    '',
    `<div class="form">
      <label class="field"><span>ID Telegram</span><input id="new-user-id" type="text" inputmode="numeric" autocomplete="off" placeholder="Es. 123456789"></label>
      <label class="field"><span>Nome</span><input id="new-user-name" type="text" autocomplete="off" maxlength="80" placeholder="Facoltativo, altrimenti da Telegram"></label>
      <div><div class="sec first">Ruolo</div>${roleSeg('user', 'form-role')}<div class="cap" id="new-role-cap">${ROLES[0].desc}</div></div>
      <div><div class="sec first">Notifiche</div>${Object.entries(TOPICS)
        .map(([key, t]) => `<div class="setrow"><div><b>${t.label}</b><span class="d">${t.desc}</span></div><button type="button" class="switch" role="switch" aria-checked="false" aria-label="Notifiche ${t.label}" data-action="form-switch" data-topic="${key}"></button></div>`)
        .join('')}</div>
      <p class="form-error" id="user-error"></p>
    </div>`,
    `<button class="btn g" data-action="close-sheet">Annulla</button><button class="btn p" data-action="save-user">Aggiungi</button>`
  );
  showSheet();
}

function pickFormRole(button) {
  const group = button.closest('.seg');
  group.querySelectorAll('button').forEach((b) => {
    const on = b === button;
    b.classList.toggle('on', on);
    b.setAttribute('aria-pressed', String(on));
  });
  const role = ROLES.find((r) => r.key === button.dataset.role);
  const cap = $('new-role-cap');
  if (cap) cap.textContent = role.desc;
  if (role.key === 'admin') document.querySelectorAll('#sheet [data-action="form-switch"]').forEach((s) => s.setAttribute('aria-checked', 'true'));
}

function entitlementsFor(user, role) {
  const base = (user?.entitlements || []).filter((e) => e !== 'admin' && e !== 'gate_controller');
  if (role === 'admin') base.push('admin');
  if (role === 'gate') base.push('gate_controller');
  return base;
}

async function saveUser() {
  const idInput = $('new-user-id');
  if (!idInput) return;
  const error = $('user-error');
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
  const role = document.querySelector('#sheet [data-action="form-role"].on')?.dataset.role || 'user';
  const topics = Array.from(document.querySelectorAll('#sheet [data-action="form-switch"][aria-checked="true"]')).map((s) => s.dataset.topic);
  const name = $('new-user-name').value.trim();
  const entitlements = entitlementsFor(null, role);

  const result = await optimistic({
    pending: 'Aggiungo l’utente…',
    apply: (overview) => {
      overview.users.push({
        id,
        name: name || null,
        username: null,
        entitlements,
        topics,
        isAdmin: entitlements.includes('admin'),
        isGateController: entitlements.includes('admin') || entitlements.includes('gate_controller'),
      });
    },
    request: () => write('/users', { method: 'POST', body: { id, name, entitlements, topics } }),
    success: 'Utente aggiunto',
    failure: (e) => {
      error.textContent = e.message;
    },
  });

  if (result) {
    state.selectedUser = id;
    closeSheet();
    render();
  }
}

function openEditName(userId) {
  const user = findUser(userId);
  if (!user) return;
  fillSheet(
    'Nome utente',
    'Se lo lasci vuoto uso il nome di Telegram.',
    `<div class="form">
      <label class="field"><span>Nome</span><input id="name-input" data-user="${user.id}" type="text" maxlength="80" autocomplete="off" value="${esc(user.name || '')}" placeholder="Es. Matteo"></label>
      <p class="form-error" id="name-error"></p>
    </div>`,
    `<button class="btn g" data-action="close-sheet">Annulla</button><button class="btn p" data-action="save-name">Salva</button>`
  );
  showSheet();
}

async function saveName() {
  const input = $('name-input');
  const error = $('name-error');
  if (!input || !error) return;
  error.textContent = '';
  const user = findUser(Number(input.dataset.user));
  if (!user) return;
  const name = input.value.trim();
  const result = await optimistic({
    pending: 'Salvo il nome…',
    apply: (overview) => {
      const target = overview.users.find((u) => u.id === user.id);
      if (target) target.name = name || null;
    },
    request: () => write('/users/' + user.id, { method: 'PATCH', body: { name, entitlements: user.entitlements, topics: user.topics } }),
    success: 'Nome aggiornato',
    failure: (e) => {
      error.textContent = e.message;
    },
  });
  if (result) closeSheet();
}

async function patchUser(user, changes, message) {
  await optimistic({
    pending: 'Applico le modifiche…',
    apply: (overview) => {
      const target = overview.users.find((u) => u.id === user.id);
      if (!target) return;
      Object.assign(target, changes);
      target.isAdmin = target.entitlements.includes('admin');
      target.isGateController = target.isAdmin || target.entitlements.includes('gate_controller');
    },
    request: () =>
      write('/users/' + user.id, {
        method: 'PATCH',
        body: {
          name: user.name,
          entitlements: changes.entitlements || user.entitlements,
          topics: changes.topics || user.topics,
        },
      }),
    success: message,
  });
}

async function setRole(userId, role) {
  const user = findUser(userId);
  if (!user || userRole(user) === role) return;
  if (user.isAdmin && role !== 'admin' && adminCount() <= 1) {
    toast('Serve almeno un admin', 'error');
    return;
  }
  const label = ROLES.find((r) => r.key === role).label;
  await patchUser(user, { entitlements: entitlementsFor(user, role) }, `${userLabel(user)}: ruolo ${label}`);
}

async function toggleTopic(userId, topic) {
  const user = findUser(userId);
  if (!user) return;
  const has = user.topics.includes(topic);
  const topics = has ? user.topics.filter((t) => t !== topic) : [...user.topics, topic];
  await patchUser(user, { topics }, `Notifiche ${TOPICS[topic].label} ${has ? 'disattivate' : 'attivate'}`);
}

function confirmRemoveUser(userId) {
  const user = findUser(userId);
  if (!user) return;
  if (user.isAdmin && adminCount() <= 1) {
    toast('Serve almeno un admin', 'error');
    return;
  }
  openConfirm({
    title: `Rimuovere ${userLabel(user)}?`,
    body: user.isAdmin ? 'Perde i permessi di admin, le notifiche e l’accesso ai comandi.' : 'Perde le notifiche e l’accesso ai comandi.',
    label: 'Rimuovi',
    danger: true,
    onConfirm: async () => {
      await optimistic({
        pending: 'Rimuovo l’utente…',
        apply: (overview) => {
          overview.users = overview.users.filter((u) => u.id !== userId);
          if (state.selectedUser === userId) state.selectedUser = null;
        },
        request: () => write('/users/' + userId, { method: 'DELETE' }),
        success: 'Utente rimosso',
      });
    },
  });
}

/* ---------- device actions ---------- */

function openAddDevice() {
  fillSheet(
    'Aggiungi dispositivo',
    'Genero un link monouso, mostrato come QR code.',
    `<div class="form">
      <label class="field"><span>Nome del dispositivo</span><input id="device-name" type="text" maxlength="60" autocomplete="off" placeholder="Es. iPad cucina"></label>
      <p class="form-error" id="device-error"></p>
    </div>`,
    `<button class="btn g" data-action="close-sheet">Annulla</button><button class="btn p" data-action="create-device">Genera QR</button>`
  );
  showSheet();
}

async function createDevice(id) {
  const error = $('device-error');
  if (!error) return;
  error.textContent = '';
  const name = id ? null : $('device-name').value.trim();
  if (!id && !name) {
    error.textContent = 'Il nome è obbligatorio.';
    return;
  }
  const ticket = await optimistic({
    pending: 'Genero il link…',
    request: () => (id ? api('/devices/' + id + '/pair', { method: 'POST' }) : api('/devices/pair', { method: 'POST', body: { name } })),
    failure: (e) => {
      error.textContent = e.message;
    },
  });
  if (!ticket) return;
  showPairing(ticket);
  loadDevices();
}

function showPairing(ticket) {
  fillSheet(
    'Inquadra il QR code',
    `Apri la fotocamera di "${ticket.name}". Il link vale una sola volta.`,
    `<div class="qr-card">
      <div class="qr">${ticket.qrSvg}</div>
      <div class="qr-countdown" id="qr-countdown">—</div>
      <div class="qr-url"><input id="pair-url" type="text" readonly value="${esc(ticket.url)}"><button class="btn s g" data-action="copy-link">${icon('link')}Copia</button></div>
    </div>`,
    `<button class="btn g" data-action="close-sheet">Chiudi</button>`
  );
  showSheet();
  startQrCountdown(ticket.expiresAt);
}

function startQrCountdown(expiresAt) {
  clearInterval(qrTimer);
  const expires = new Date(expiresAt).getTime();
  const tick = () => {
    const el = $('qr-countdown');
    if (!el) {
      clearInterval(qrTimer);
      return;
    }
    const left = Math.max(0, Math.round((expires - Date.now()) / 1000));
    if (left === 0) {
      el.textContent = 'Link scaduto, generane uno nuovo';
      clearInterval(qrTimer);
      return;
    }
    el.textContent = `Valido ancora ${Math.floor(left / 60)}:${String(left % 60).padStart(2, '0')}`;
  };
  tick();
  qrTimer = setInterval(tick, 1000);
}

async function copyLink() {
  const input = $('pair-url');
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
  fillSheet(
    'Nuovo link di pairing',
    'Il link precedente smette di funzionare.',
    `<p class="form-error" id="device-error"></p>`,
    `<button class="btn g" data-action="close-sheet">Annulla</button><button class="btn p" data-action="confirm-create-device" data-device="${esc(id)}">Genera</button>`
  );
  showSheet();
}

function confirmRevokeDevice(id, name) {
  openConfirm({
    title: `Revocare "${name}"?`,
    body: 'Il dispositivo perde subito l’accesso e servirà un nuovo link.',
    label: 'Revoca',
    danger: true,
    onConfirm: async () => {
      await optimistic({
        pending: 'Revoco il dispositivo…',
        apply: (overview) => {
          if (overview.devices) overview.devices = overview.devices.filter((d) => d.id !== id);
        },
        request: () => api('/devices/' + id, { method: 'DELETE' }),
        success: 'Dispositivo revocato',
      });
      loadDevices();
    },
  });
}

async function loadDevices() {
  try {
    const data = await api('/devices');
    if (state.overview) state.overview.devices = data.devices || [];
    if (state.view === 'devices') render();
  } catch (error) {
    toast(error.message, 'error');
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

/* ---------- navigation ---------- */

function setView(name) {
  if (!VIEWS[name]) return;
  if (isSheetOpen()) closeSheet();
  const changed = name !== state.view;
  if (changed) state.seriesMenuOpen = false;
  if (changed && state.chartFull) {
    state.chartFull = false;
    document.body.classList.remove('chart-full');
  }
  state.view = name;
  try {
    if (location.hash !== '#' + name) history.replaceState(null, '', '#' + name);
  } catch (e) {
    /* the app still works without the hash */
  }
  $('main').scrollTop = 0;
  render(changed);
  if (name === 'heating') loadHistory();
  if (name === 'events') loadEvents();
  if (name === 'devices') loadDevices();
}

/* ---------- events ---------- */

const ACTIONS = {
  'open-gate': (b) => confirmOpenGate(b.dataset.gate),
  window: (b) => openWindow(Number(b.dataset.hours)),
  'lock-gates': () => lockGates(),
  lights: () => triggerLights(),
  boiler: (b) => setBoiler(b.dataset.on === '1'),
  'toggle-suspend': () => toggleSuspend(),
  'edit-program': () => openProgramEditor(),
  'delete-program': () => confirmDeleteProgram(),
  'add-interval': () => addIntervalRow(),
  'remove-interval': (b) => b.closest('.editor-row')?.remove(),
  'save-program': () => saveProgram(),
  'chart-range': (b) => changeRange(b.dataset.value),
  'series-menu': () => toggleSeriesMenu(),
  'series-item': (b) => toggleSeries(b.dataset.series),
  'apply-range': () => applyRange(),
  filter: (b) => {
    state.eventFilter = b.dataset.value;
    render();
    loadEvents();
  },
  'select-user': (b) => {
    state.selectedUser = Number(b.dataset.user);
    render();
    const detail = $('user-detail');
    if (detail && window.innerWidth <= 1100) detail.scrollIntoView({ behavior: prefs.motion === 'full' ? 'smooth' : 'auto', block: 'start' });
  },
  'set-role': (b) => setRole(Number(b.dataset.user), b.dataset.role),
  'toggle-topic': (b) => toggleTopic(Number(b.dataset.user), b.dataset.topic),
  'edit-name': (b) => openEditName(Number(b.dataset.user)),
  'save-name': () => saveName(),
  'remove-user': (b) => confirmRemoveUser(Number(b.dataset.user)),
  'add-user': () => openAddUser(),
  'save-user': () => saveUser(),
  'form-role': (b) => pickFormRole(b),
  'form-switch': (b) => b.setAttribute('aria-checked', String(b.getAttribute('aria-checked') !== 'true')),
  'add-device': () => openAddDevice(),
  'create-device': () => createDevice(null),
  'confirm-create-device': (b) => createDevice(b.dataset.device),
  'refresh-device': (b) => refreshDevicePairing(b.dataset.device),
  'revoke-device': (b) => confirmRevokeDevice(b.dataset.device, b.dataset.name),
  'copy-link': () => copyLink(),
  logout: () => logout(),
  'pref-theme': (b) => {
    savePref('theme', b.dataset.value);
    applyTheme();
    render();
  },
  'pref-motion': (b) => {
    savePref('motion', b.dataset.value);
    applyTheme();
    render();
  },
  'pref-idle': (b) => {
    savePref('idle', b.dataset.value);
    render();
  },
  'idle-test': () => showIdle(),
  'chart-full': () => toggleChartFull(),
  'cycle-theme': () => cycleTheme(),
  refresh: async () => {
    await refresh();
    toast('Dati aggiornati');
  },
  alerts: () => openAlerts(),
  go: (b) => {
    closeSheet();
    if (b.dataset.target) setView(b.dataset.target);
  },
  'close-sheet': () => closeSheet(),
  confirm: async () => {
    const fn = confirmAction;
    confirmAction = null;
    closeSheet();
    if (fn) await fn();
  },
};

let chartDrag = null;
let chartLingerUntil = 0;

function chartLinger(delay = 3000) {
  chartLingerUntil = Date.now() + delay;
  clearTimeout(chartHoverTimer);
  chartHoverTimer = setTimeout(() => hideChartHover(true), delay);
}

document.addEventListener('pointerdown', (event) => {
  const plot = event.target.closest('.chart-plot');
  if (!plot) return;
  clearTimeout(chartHoverTimer);
  chartLingerUntil = 0;
  if (event.pointerType !== 'mouse') {
    chartDrag = { id: event.pointerId };
    if (plot.setPointerCapture) {
      try {
        plot.setPointerCapture(event.pointerId);
      } catch (e) {
        /* the browser may refuse the capture, the drag still works */
      }
    }
  }
  updateChartHover(event);
});

document.addEventListener('pointermove', (event) => {
  if (chartDrag && event.pointerId !== chartDrag.id) return;
  const plot = event.target.closest('.chart-plot') || (chartDrag ? document.querySelector('.chart-plot') : null);
  if (plot) {
    chartLingerUntil = 0;
    chartHoverX = event.clientX;
    showChartHoverAt(plot, event.clientX);
  } else if (!chartDrag) {
    hideChartHover();
  }
});

document.addEventListener('pointerup', (event) => {
  if (!chartDrag || event.pointerId !== chartDrag.id) return;
  chartDrag = null;
  chartLinger(3000);
});

document.addEventListener('pointercancel', (event) => {
  if (!chartDrag || event.pointerId !== chartDrag.id) return;
  chartDrag = null;
  chartLinger(3000);
});

document.addEventListener('pointerout', (event) => {
  if (chartDrag || Date.now() < chartLingerUntil) return;
  if (!event.relatedTarget) hideChartHover();
});

document.addEventListener('pointermove', (event) => {
  if (event.target.closest('.bars-wrap')) showBarHover(event);
  else hideBarHover();
});

document.addEventListener('pointerdown', (event) => {
  if (event.target.closest('.bars-wrap')) {
    clearTimeout(barHoverTimer);
    showBarHover(event);
  }
});

document.addEventListener('pointerup', (event) => {
  if (event.pointerType === 'touch' && document.querySelector('.bars-tip:not([hidden])')) hideBarHover(2500);
});

document.addEventListener('click', (event) => {
  if (!event.target.closest('#series-picker')) closeSeriesMenu();

  const nav = event.target.closest('[data-nav]');  if (nav) {
    setView(nav.dataset.nav);
    return;
  }

  const viewButton = event.target.closest('.nav-item[data-view], .tabs [data-view]');
  if (viewButton) {
    setView(viewButton.dataset.view);
    return;
  }

  if (event.target.closest('[data-more]')) {
    openMore();
    return;
  }

  const button = event.target.closest('[data-action]');
  if (!button || button.disabled) return;
  const handler = ACTIONS[button.dataset.action];
  if (handler) handler(button);
});

$('backdrop').addEventListener('click', closeSheet);

document.addEventListener('keydown', (event) => {
  if (event.key !== 'Escape') return;
  if (state.chartFull) {
    toggleChartFull();
    return;
  }
  if (isSheetOpen()) closeSheet();
});

/* Any touch wakes the screen; the touch that wakes it never reaches the page below. */
['pointerdown', 'keydown', 'wheel'].forEach((type) =>
  document.addEventListener(
    type,
    (event) => {
      state.lastInput = Date.now();
      if (state.idle) {
        event.preventDefault();
        event.stopPropagation();
        hideIdle();
      }
    },
    { capture: true, passive: false }
  )
);

$('idle').addEventListener('click', (event) => {
  event.preventDefault();
  event.stopPropagation();
});

window.addEventListener('hashchange', () => {
  const name = location.hash.replace('#', '');
  if (VIEWS[name] && name !== state.view) setView(name);
});

let resizeTimer = null;
window.addEventListener('resize', () => {
  movePill();
  placePills();
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(() => {
    if (state.chartFull) render();
  }, 180);
});

if (darkQuery) {
  const onScheme = () => {
    if (prefs.theme === 'auto') applyTheme();
  };
  if (darkQuery.addEventListener) darkQuery.addEventListener('change', onScheme);
  else if (darkQuery.addListener) darkQuery.addListener(onScheme);
}

/* ---------- polling ---------- */

function canPoll() {
  if (document.hidden || isSheetOpen()) return false;
  const active = document.activeElement;
  return !(active && ['INPUT', 'TEXTAREA', 'SELECT'].includes(active.tagName));
}

setInterval(() => {
  tickLive();
  checkIdle();
}, 1000);

setInterval(() => {
  if (canPoll()) refresh();
}, 5000);

setInterval(() => {
  if (!canPoll()) return;
  if (state.view === 'events') loadEvents();
}, 15000);

setInterval(() => {
  if (canPoll() && state.view === 'heating') loadHistory();
}, 60000);

document.addEventListener('visibilitychange', () => {
  if (!document.hidden) refresh();
});

/* ---------- start ---------- */

applyTheme();
const initial = location.hash.replace('#', '');
if (VIEWS[initial]) state.view = initial;
render(true);
refresh();
if (state.view === 'heating') loadHistory();
if (state.view === 'events') loadEvents();
