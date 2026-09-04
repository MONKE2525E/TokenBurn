// Deterministic interaction tests for the popup UI state machine.
//
// dist/app.js is a hand-rolled single-file UI with a real state machine: popup open/close
// (poc-opened / poc-closing / visibilitychange), the breakdown drill-down, chart hover, the
// "Others" aggregation tooltip, settings pages, and async refreshes that can complete at any
// moment relative to the user's interaction.
//
// This file runs the real app.js inside a Node VM with a scriptable mini-DOM, a controllable
// clock (timers, rAF, Date.now all advance together), and stubbed Tauri IPC/fetch so every
// response can be delayed or reordered deterministically. No sleeps, no browser, no OS-level
// desktop interaction: each test drives the exact same handlers the real UI installs.
//
// Run: node UsageMonitor.TauriPoc/interaction-tests.mjs

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const here = dirname(fileURLToPath(import.meta.url));
const appSource = readFileSync(join(here, 'dist', 'app.js'), 'utf8');
const markupSource = readFileSync(join(here, 'dist', 'index.html'), 'utf8');

// ---------------------------------------------------------------------------
// Fixture helpers. The VM clock is anchored to BASE_TIME so dayKey/spend period
// math is deterministic in both host and VM.
// ---------------------------------------------------------------------------

const BASE_TIME = new Date('2026-08-10T12:00:00');
function hostDayKey(offset = 0) {
  const date = new Date(BASE_TIME);
  date.setHours(0, 0, 0, 0);
  date.setDate(date.getDate() - offset);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

// History points carry a provider's period total on a single day so the 30-day aggregation
// equals the amount the test names, instead of multiplying it by the number of days.
const USAGE_POINTS = Array.from({ length: 30 }, (_, index) => ({
  date: hostDayKey(29 - index),
  costUsd: 0,
  tokens: 0,
}));

function snapshot(id, { costUsd = 0, tokens = 0, extraLines = [] } = {}) {
  const displayName = id === 'claude-code' ? 'Claude Code' : id[0].toUpperCase() + id.slice(1);
  return {
    providerId: id,
    displayName,
    plan: 'Pro',
    fetchedAt: '2099-01-01T00:00:00Z',
    lines: [
      { type: 'progress', label: 'Weekly', used: 10, limit: 100, resetsAt: '2099-01-01T00:00:00Z' },
      { type: 'chart', label: 'Trend', points: Array.from({ length: 30 }, (_, i) => ({ value: 10 + i })) },
      ...extraLines,
    ],
    usageHistory: { points: USAGE_POINTS.map((point, index) => index === 0 ? { ...point, costUsd, tokens } : point) },
  };
}

// The daily totals land on the same day as the ring/legend math.
function dailyCost(costUsd) {
  return USAGE_POINTS.map(point => ({ ...point, costUsd }));
}

// Recording stub for every 2D canvas the popup draws to.
function makeCanvasContext() {
  const noop = () => {};
  return {
    setTransform: noop, clearRect: noop, beginPath: noop, arc: noop, stroke: noop, fill: noop,
    moveTo: noop, lineTo: noop, quadraticCurveTo: noop, bezierCurveTo: noop, closePath: noop, fillText: noop,
    save: noop, restore: noop, scale: noop, drawImage: noop, strokeRect: noop, fillRect: noop,
    createLinearGradient: () => ({ addColorStop: noop }),
    measureText: text => ({ width: Math.max(8, String(text).length * 6), actualBoundingBoxAscent: 10, actualBoundingBoxDescent: 3 }),
    getImageData: () => ({ data: new Uint8ClampedArray(16) }),
    roundRect: noop,
    font: '', fillStyle: '', strokeStyle: '', lineWidth: 1, globalAlpha: 1,
    textAlign: '', textBaseline: '', lineCap: '', lineJoin: '',
  };
}

// ---------------------------------------------------------------------------
// Mini DOM
// ---------------------------------------------------------------------------

const VOID_TAGS = new Set(['input', 'img', 'br', 'meta', 'link']);
const ENTITIES = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'" };
function decodeEntities(value) {
  return String(value).replace(/&(amp|lt|gt|quot|#39);/g, match => ENTITIES[match]);
}
function toCamel(attribute) {
  return attribute.replace(/-([a-z])/g, (_, char) => char.toUpperCase());
}

class El {
  constructor(tag = 'div') {
    this.tagName = tag.toUpperCase();
    this.children = [];
    this.parentNode = null;
    this.attributes = {};
    this.dataset = {};
    this.style = {};
    this.style.setProperty = (key, value) => { this.style[key] = value; };
    this._classes = new Set();
    this._listeners = {};
    this._text = '';
    this._value = '';
    this._checked = false;
    this._disabled = false;
    this._tabIndex = 0;
    this.hidden = false;
    this.id = '';
    this._rect = null;
    this.tabIndex = 0;
    this.scrollTop = 0;
    this.scrollLeft = 0;
    this.clientWidth = 0;
    this.clientHeight = 0;
    this.offsetWidth = 0;
    this.offsetHeight = 0;
  }

  get classList() {
    const classes = this._classes;
    return {
      add: (...names) => names.forEach(name => classes.add(name)),
      remove: (...names) => names.forEach(name => classes.delete(name)),
      toggle: (name, force) => {
        const on = force === undefined ? !classes.has(name) : Boolean(force);
        if (on) classes.add(name); else classes.delete(name);
        return on;
      },
      contains: name => classes.has(name),
    };
  }

  get className() { return [...this._classes].join(' '); }

  set className(value) {
    this._classes = new Set(String(value).split(/\s+/).filter(Boolean));
    this.attributes.class = String(value);
  }

  setAttribute(name, value) {
    this.attributes[name] = String(value);
    if (name === 'class') this._classes = new Set(String(value).split(/\s+/).filter(Boolean));
    else if (name === 'id') this.id = String(value);
    else if (name === 'hidden') this.hidden = true;
    else if (name === 'checked') this._checked = true;
    else if (name === 'disabled') this._disabled = true;
    else if (name === 'tabindex') this.tabIndex = Number(value) || 0;
    else if (name.startsWith('data-')) this.dataset[toCamel(name.slice(5))] = String(value);
  }

  getAttribute(name) {
    return this.attributes[name] ?? null;
  }

  hasAttribute(name) {
    return Object.prototype.hasOwnProperty.call(this.attributes, name);
  }

  removeAttribute(name) {
    delete this.attributes[name];
    if (name === 'hidden') this.hidden = false;
    else if (name === 'checked') this._checked = false;
    else if (name === 'disabled') this._disabled = false;
    else if (name.startsWith('data-')) delete this.dataset[toCamel(name.slice(5))];
  }

  get textContent() {
    if (this.children.length) return this.children.map(child => child.textContent).join('');
    return this._text;
  }

  set textContent(value) {
    this._text = String(value ?? '');
    this.children = [];
  }

  get innerHTML() {
    return this.children.map(child => (child.tagName === '#text' ? child._text : `<${child.tagName.toLowerCase()}>`)).join('');
  }

  set innerHTML(html) {
    const parsed = parseHTML(html);
    this.children = parsed;
    parsed.forEach(child => { child.parentNode = this; });
  }

  get value() {
    if (this.tagName === 'SELECT') {
      const options = this.options;
      const selected = options[this.selectedIndex || 0];
      return selected ? selected.value : '';
    }
    // Like a real <option>, the value falls back to the element text when no value attribute set.
    return this._value || this.textContent;
  }

  set value(value) {
    this._value = String(value ?? '');
    if (this.tagName === 'SELECT') {
      const options = this.options;
      const index = options.findIndex(option => option.value === String(value));
      if (index >= 0) this.selectedIndex = index;
    }
  }

  get options() {
    return this.children.filter(child => child.tagName === 'OPTION');
  }

  get checked() { return this._checked; }
  set checked(value) { this._checked = Boolean(value); }

  get type() { return this.attributes.type || (this.tagName === 'INPUT' ? 'text' : undefined); }

  get disabled() { return this._disabled; }
  set disabled(value) { this._disabled = Boolean(value); }

  appendChild(child) {
    if (child.parentNode) child.parentNode.removeChild(child);
    child.parentNode = this;
    this.children.push(child);
    return child;
  }

  append(...children) {
    for (const child of children) this.appendChild(child);
    return this;
  }

  insertBefore(child, reference) {
    if (child.parentNode) child.parentNode.removeChild(child);
    const index = reference ? this.children.indexOf(reference) : -1;
    if (index >= 0) this.children.splice(index, 0, child);
    else this.children.push(child);
    child.parentNode = this;
    return child;
  }

  removeChild(child) {
    const index = this.children.indexOf(child);
    if (index >= 0) this.children.splice(index, 1);
    child.parentNode = null;
    return child;
  }

  remove() {
    this.parentNode?.removeChild(this);
  }

  addEventListener(type, listener) {
    (this._listeners[type] ||= []).push(listener);
  }

  removeEventListener(type, listener) {
    const listeners = this._listeners[type];
    if (!listeners) return;
    const index = listeners.indexOf(listener);
    if (index >= 0) listeners.splice(index, 1);
  }

  dispatchEvent(event) {
    let path = [];
    for (let node = this; node; node = node.parentNode) path.push(node);
    event.target = this;
    for (const node of path) {
      if (event._stopped) break;
      event.currentTarget = node;
      for (const listener of [...(node._listeners[event.type] || [])]) listener.call(node, event);
    }
    if (!event._stopped && globalDoc) {
      globalDoc._dispatch(event, this);
      if (!event._stopped && globalWindow) globalWindow._dispatch(event, this);
    }
    return true;
  }

  getBoundingClientRect() {
    return this._rect || { left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 };
  }

  focus() {
    if (globalDoc) globalDoc.activeElement = this;
  }

  blur() {
    if (globalDoc && globalDoc.activeElement === this) globalDoc.activeElement = null;
  }

  scrollIntoView() {}
  click() {
    fire(this, 'click');
  }

  contains(node) {
    for (let current = node; current; current = current.parentNode) {
      if (current === this) return true;
    }
    return false;
  }

  // Canvas elements — including ones created after initial load through innerHTML — get the
  // recording 2D-context stub automatically.
  getContext() {
    if (!this._ctx) this._ctx = makeCanvasContext();
    return this._ctx;
  }

  toBlob(callback) {
    callback(null);
  }

  closest(selector) {
    return closestMatching(this, selector);
  }

  querySelector(selector) {
    return queryAll(this, selector)[0] || null;
  }

  querySelectorAll(selector) {
    return queryAll(this, selector);
  }

  matches(selector) {
    return matchesAny(this, selector);
  }
}

// --- Tiny HTML parser for the subset of markup app.js generates ------------

function parseHTML(html) {
  const nodes = [];
  const stack = [{ children: nodes }];
  const tokenPattern = /<!--[\s\S]*?-->|<\/?([a-zA-Z][a-zA-Z0-9]*)((?:\s+[^\s"'>/=]+(?:\s*=\s*"[^"]*"|\s*=\s*'[^']*')?)*)\s*\/?>|([\s\S]*?)(?=<!--|<\/?[a-zA-Z])/g;
  let position = 0;
  while (position < html.length) {
    tokenPattern.lastIndex = position;
    const match = tokenPattern.exec(html);
    if (!match) {
      if (position < html.length) pushText(stack, html.slice(position));
      break;
    }
    if (match[0].startsWith('<!--')) {
      position = tokenPattern.lastIndex;
      continue;
    }
    if (match[1]) {
      const tag = match[1].toLowerCase();
      if (match[0].startsWith('</')) {
        for (let index = stack.length - 1; index > 0; index--) {
          if (stack[index].el.tagName === tag.toUpperCase()) {
            stack.splice(index, 1);
            break;
          }
        }
      } else {
        const element = new El(tag);
        const attributes = match[2] || '';
        const attributePattern = /([^\s"'>/=]+)(?:\s*=\s*"([^"]*)"|\s*=\s*'([^']*)')?/g;
        let attrMatch;
        while ((attrMatch = attributePattern.exec(attributes))) {
          if (!attrMatch[0]) continue;
          element.setAttribute(attrMatch[1], attrMatch[2] ?? attrMatch[3] ?? '');
        }
        stack[stack.length - 1].children.push(element);
        element.parentNode = stack[stack.length - 1].el || null;
        if (!VOID_TAGS.has(tag) && !match[0].trimEnd().endsWith('/>')) {
          stack.push({ el: element, children: element.children });
        }
      }
    } else if (match[3] !== undefined && match[3].length) {
      pushText(stack, match[3]);
    }
    position = tokenPattern.lastIndex;
  }
  return nodes;
}

function pushText(stack, text) {
  if (!text.length) return;
  const node = new El('#text');
  node._text = decodeEntities(text);
  node.parentNode = stack[stack.length - 1].el || null;
  stack[stack.length - 1].children.push(node);
}

// --- Selector engine (ids, classes, tags, [attr], [attr="v"], compound, descendants, commas) ---

function parseSegment(part) {
  const segment = { tag: null, id: null, classes: [], attrs: [], not: null };
  let rest = part;
  const tagMatch = /^[a-zA-Z][a-zA-Z0-9]*/.exec(rest);
  if (tagMatch) {
    segment.tag = tagMatch[0].toLowerCase();
    rest = rest.slice(tagMatch[0].length);
  }
  const itemPattern = /([#.][\w-]+|\[[^\]]+\]|:not\([^)]+\))/g;
  let match;
  while ((match = itemPattern.exec(rest))) {
    const item = match[1];
    if (item.startsWith('#')) segment.id = item.slice(1);
    else if (item.startsWith('.')) segment.classes.push(item.slice(1));
    else if (item.startsWith(':not(')) {
      const inner = item.slice(5, -1).trim();
      segment.not = splitSelectors(inner)[0];
    } else {
      const inner = item.slice(1, -1);
      const equals = /^([\w-]+)\s*=\s*"([^"]*)"$/.exec(inner) || /^([\w-]+)\s*=\s*'([^']*)'$/.exec(inner);
      if (equals) segment.attrs.push({ name: equals[1], value: decodeEntities(equals[2]) });
      else segment.attrs.push({ name: inner, value: null });
    }
  }
  return segment;
}

function splitSelectors(selector) {
  return String(selector).split(',').map(part => part.trim()).filter(Boolean)
    .map(part => part.split(/\s+/).filter(Boolean).map(parseSegment));
}

function matchesSegment(element, segment) {
  if (element.tagName === '#text') return false;
  if (segment.tag && element.tagName.toLowerCase() !== segment.tag) return false;
  if (segment.id && element.id !== segment.id) return false;
  for (const name of segment.classes) if (!element._classes.has(name)) return false;
  for (const attr of segment.attrs) {
    const actual = element.getAttribute(attr.name);
    if (attr.value !== null ? actual !== attr.value : actual === null) return false;
  }
  if (segment.not && matchesChain(element, segment.not)) return false;
  return true;
}

function matchesChain(element, segments) {
  if (!segments.length) return false;
  if (!matchesSegment(element, segments[segments.length - 1])) return false;
  let ancestor = element.parentNode;
  for (let index = segments.length - 2; index >= 0; index--) {
    while (ancestor && ancestor.tagName !== '#text' && !matchesSegment(ancestor, segments[index])) ancestor = ancestor.parentNode;
    if (!ancestor) return false;
    ancestor = ancestor.parentNode;
  }
  return true;
}

function matchesAny(element, selector) {
  return splitSelectors(selector).some(segments => matchesChain(element, segments));
}

function closestMatching(element, selector) {
  for (let node = element; node; node = node.parentNode) {
    if (node.tagName !== '#text' && matchesAny(node, selector)) return node;
  }
  return null;
}

function descendants(root) {
  const result = [];
  const walk = node => {
    for (const child of node.children) {
      if (child.tagName === '#text') continue;
      result.push(child);
      walk(child);
    }
  };
  walk(root);
  return result;
}

function queryAll(root, selector) {
  const matches = [];
  for (const segments of splitSelectors(selector)) {
    // The root itself counts as a candidate (e.g. querying for the <html> element).
    if (matchesChain(root, segments)) matches.push(root);
    for (const candidate of descendants(root)) {
      if (matchesChain(candidate, segments)) matches.push(candidate);
    }
  }
  return matches;
}

// --- Document -----------------------------------------------------------------

let globalDoc = null;
let globalWindow = null;

class FakeDocument {
  constructor() {
    this._listeners = {};
    this.body = null;
    this.documentElement = null;
    this.activeElement = null;
    this.visibilityState = 'visible';
    this.title = 'TokenBurn';
  }

  getElementById(id) {
    if (!this.body) return null;
    return queryAll(this.body, `#${id}`)[0] || null;
  }

  querySelector(selector) {
    return this.body ? queryAll(this.body, selector)[0] || null : null;
  }

  querySelectorAll(selector) {
    return this.body ? queryAll(this.body, selector) : [];
  }

  createElement(tag) {
    return new El(tag);
  }

  addEventListener(type, listener) {
    (this._listeners[type] ||= []).push(listener);
  }

  removeEventListener(type, listener) {
    const listeners = this._listeners[type];
    if (!listeners) return;
    const index = listeners.indexOf(listener);
    if (index >= 0) listeners.splice(index, 1);
  }

  _dispatch(event, source) {
    if (source) event.target = source;
    event.currentTarget = this;
    for (const listener of [...(this._listeners[event.type] || [])]) listener.call(this, event);
  }

  dispatchEvent(event) {
    if (this._listeners[event.type]?.length) this._dispatch(event, event.target);
    return true;
  }

  execCommand() { return true; }
}

// ---------------------------------------------------------------------------
// Harness: DOM + clock + IPC + canvas stubs, then run app.js in a fresh VM
// ---------------------------------------------------------------------------

// Async rejections inside any harness land in a shared sink (one process-level listener for
// every harness instance, not one per instance).
const harnessErrors = [];
let rejectionListenerAttached = false;

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}

function createHarness(options = {}) {
  const clock = { now: 0 };
  const timers = new Map();
  let nextTimerId = 1;
  const rafQueue = [];

  const setTimer = (fn, ms, kind) => {
    const id = nextTimerId++;
    timers.set(id, { id, fn, kind, at: clock.now + Math.max(0, Number(ms) || 0) });
    return id;
  };
  const setTimeoutFn = (fn, ms) => setTimer(fn, ms, 'timeout');
  const setIntervalFn = (fn, ms) => setTimer(fn, ms, 'interval');
  const clearTimer = id => timers.delete(id);
  const requestAnimationFrame = fn => {
    const id = nextTimerId++;
    rafQueue.push({ id, fn });
    return id;
  };
  const cancelAnimationFrame = id => {
    const index = rafQueue.findIndex(entry => entry.id === id);
    if (index >= 0) rafQueue.splice(index, 1);
  };

  const flushRaf = () => {
    const batch = rafQueue.splice(0);
    batch.forEach(entry => entry.fn(clock.now));
    return batch.length;
  };
  // Runs queued frames, advancing the clock past tween durations so the ring/hover
  // animations settle instead of rescheduling forever.
  const settleFrames = (iterations = 20) => {
    for (let index = 0; index < iterations; index++) {
      if (!rafQueue.length) break;
      clock.now += 340;
      flushRaf();
    }
    flushRaf();
  };

  const tick = ms => {
    clock.now += ms;
    const ran = new Set();
    while (true) {
      const next = [...timers.values()].filter(timer => timer.at <= clock.now && !ran.has(timer.id)).sort((a, b) => a.at - b.at)[0];
      if (!next) break;
      ran.add(next.id);
      try {
        next.fn();
      } catch (error) {
        errors.push(error);
      }
      // A one-shot timer must not fire again on a later tick.
      if (next.kind === 'interval') next.at += Math.max(1, Math.round(ms));
      else timers.delete(next.id);
    }
    return ran.size;
  };

  const errors = [];
  const flushAsync = async (rounds = 8) => {
    for (let index = 0; index < rounds; index++) await new Promise(resolve => setImmediate(resolve));
    if (errors.length) throw errors[0];
    if (harnessErrors.length) throw harnessErrors[0];
  };

  // --- IPC and HTTP stubs ------------------------------------------------------
  const invokeCalls = [];
  const tauriEvents = {};
  const defaults = {
    fetch_usage: () => options.snapshots ?? [],
    fetch_enabled_providers: () => ['claude-code', 'codex', 'antigravity', 'cursor'],
    fetch_refresh_status: () => ({ loading: false, nextRefreshAt: null }),
    get_settings_data: () => options.settingsData ?? {
      settings: {
        usageDisplay: 'Used',
        resetTimeDisplay: 'Countdown',
        taskbarPositionLocked: true,
        motionPreference: options.motionPreference ?? 'system',
        notificationsEnabled: true,
        notificationProviderIds: [],
        disabledProviders: [],
        starredMetrics: [],
        spendMetric: 'cost',
      },
      providers: [
        { id: 'claude-code', displayName: 'Claude Code', available: true },
        { id: 'codex', displayName: 'Codex', available: true },
        { id: 'antigravity', displayName: 'Antigravity', available: true },
        { id: 'cursor', displayName: 'Cursor', available: true },
      ],
      metricNames: ['claude-code:session', 'codex:weekly'],
      providerStatuses: [],
    },
    set_breakdown_mode: () => options.breakdownWidth ?? 900,
    set_spend_metric: () => null,
    set_popup_motion_reduced: () => null,
    apply_settings_data: () => null,
    set_screen_share_privacy: () => null,
    hide_popup: () => null,
    copy_share: () => null,
    request_desktop_refresh: () => null,
    open_claude_login: () => null,
    open_antigravity_login: () => null,
    get_diagnostics_bundle: () => '',
  };

  const invoke = (command, args) => {
    invokeCalls.push({ command, args });
    const handler = defaults[command];
    if (!handler) return Promise.resolve(null);
    return Promise.resolve(handler(args));
  };
  const httpResponses = [];
  const fetchStub = (url, init) => {
    httpResponses.push({ url, init });
    const suffix = String(url).includes('force=true');
    const handler = options.http ?? (() => ({ ok: true, json: async () => options.snapshots ?? [] }));
    return Promise.resolve(handler(suffix));
  };

  // --- Window / document / globals --------------------------------------------
  const matchMedia = () => ({
    matches: Boolean(options.reducedMotion),
    addEventListener: () => {},
    removeEventListener: () => {},
  });

  const fakeDate = class FakeDate extends Date {
    constructor(...args) {
      if (args.length === 0) super(BASE_TIME.getTime() + clock.now);
      else super(...args);
    }
    static now() { return BASE_TIME.getTime() + clock.now; }
  };

  const body = new El('body');
  const html = new El('html');
  html.appendChild(body);
  globalDoc = new FakeDocument();
  globalDoc.body = body;
  globalDoc.documentElement = html;
  const markupBody = markupSource.slice(markupSource.indexOf('<body>') + 6, markupSource.indexOf('</body>'))
    .replace(/<script[^>]*><\/script>/, '');
  body.innerHTML = markupBody;

  const windowObj = {
    _listeners: {},
    addEventListener(type, listener) { (this._listeners[type] ||= []).push(listener); },
    removeEventListener(type, listener) {
      const listeners = this._listeners[type];
      if (!listeners) return;
      const index = listeners.indexOf(listener);
      if (index >= 0) listeners.splice(index, 1);
    },
    _dispatch(event, source) {
      event.target = source ?? event.target;
      event.currentTarget = this;
      for (const listener of [...(this._listeners[event.type] || [])]) listener.call(this, event);
    },
    dispatchEvent(event) {
      if (this._listeners[event.type]?.length) this._dispatch(event, null);
      return true;
    },
    innerWidth: options.innerWidth ?? 800,
    innerHeight: options.innerHeight ?? 600,
    devicePixelRatio: 1,
    setTimeout: setTimeoutFn,
    clearTimeout: clearTimer,
    setInterval: setIntervalFn,
    clearInterval: clearTimer,
    requestAnimationFrame,
    cancelAnimationFrame,
    localStorage: (() => {
      const store = new Map();
      return {
        getItem: key => store.get(key) ?? null,
        setItem: (key, value) => store.set(key, String(value)),
        removeItem: key => store.delete(key),
      };
    })(),
    matchMedia,
    __TAURI__: {
      core: { invoke },
      window: { getCurrentWindow: () => ({ hide: async () => null }) },
      event: {
        listen: (channel, callback) => { tauriEvents[channel] = callback; return Promise.resolve(() => {}); },
      },
    },
  };
  globalWindow = windowObj;

  const document = globalDoc;
  const context = {
    window: windowObj,
    document,
    navigator: { clipboard: { writeText: async () => true, write: async () => true } },
    performance: { now: () => clock.now },
    Date: fakeDate,
    Math, JSON, Number, String, Array, Object, Set, Map, Boolean, Error, Promise,
    isNaN, parseInt, parseFloat, btoa: value => Buffer.from(value, 'binary').toString('base64'),
    setTimeout: setTimeoutFn,
    clearTimeout: clearTimer,
    setInterval: setIntervalFn,
    clearInterval: clearTimer,
    requestAnimationFrame,
    cancelAnimationFrame,
    fetch: fetchStub,
    Path2D: class { moveTo() {} lineTo() {} quadraticCurveTo() {} closePath() {} arc() {} },
    Event: class EventStub {
      constructor(type, options = {}) { this.type = type; this.bubbles = options.bubbles ?? false; }
    },
    console,
    addEventListener: () => {},
    removeEventListener: () => {},
  };
  context.window = windowObj;
  context.globalThis = context;

    // Canvas stubs are provided lazily by El.getContext, including canvases created through
  // innerHTML after initial load.

    const reducedMotionQuery = { matches: Boolean(options.reducedMotion), addEventListener: () => {} };

  // Module-level `const`/`let` declarations do not become properties of the VM context object,
  // so the host cannot read `state` or the interaction helpers through the context. Append an
  // export line inside the same script instead — exactly the pattern selfcheck.mjs uses.
  const exportNames = [
    'state', 'showStatus', 'statusQueue', 'revealPopover', 'beginPopoverClose',
    'setSpendHover', 'spendProviderAtPoint', 'groupSmallSpendRows', 'renderSpend',
    'renderBreakdown', 'setBreakdownView', 'closeSpendOtherTooltip', 'paintSpendOtherTooltip',
    'setOverlayOpen', 'progressValues', 'dayKey', 'compactNumber', 'formatCost',
    'visibleLines', 'breakdownRows', 'breakdownSeries', 'closeHeaderPopovers',
    'periodRangeText', 'compactShareText', 'legendLabel', 'providersStructureKey',
  ];
  // lastSpendRootRows is reassigned by every render, so it needs a live accessor.
  const exportLine = `\n;globalThis.__selfCheck = { ${exportNames.join(', ')}, get lastSpendRootRows() { return lastSpendRootRows; }, setLastSpendRootRows(rows) { lastSpendRootRows = rows; } };`;
  context.globalThis.__selfCheck = {};
  if (!rejectionListenerAttached) {
    rejectionListenerAttached = true;
    process.on('unhandledRejection', error => { harnessErrors.push(error); });
  }
  vm.createContext(context);
  try {
    vm.runInContext(appSource + exportLine, context, { filename: 'app.js' });
  } catch (error) {
    throw new Error(`app.js failed to load in the harness: ${error.message}\n${error.stack?.split('\n').slice(0, 8).join('\n')}`);
  }
  // The export line replaced context.__selfCheck with a fresh object whose accessors stay live;
  // expose THAT object (never a copy), or the lastSpendRootRows getter would snapshot at copy time.
  const exposed = context.__selfCheck;

  // --- Interaction helpers ------------------------------------------------------
  const fire = (target, type, props = {}) => {
    const event = {
      type,
      target,
      ...props,
      _stopped: false,
      preventDefault() { this.defaultPrevented = true; },
      stopPropagation() { this._stopped = true; },
    };
    if (target instanceof El) target.dispatchEvent(event);
    else target.dispatchEvent?.(event);
    return event;
  };

  const emitNative = (channel, payload) => {
    const callback = tauriEvents[channel];
    if (callback) {
      if (typeof payload === 'function') payload(callback);
      else callback(payload);
    }
  };

  const byId = id => document.getElementById(id);
  const popover = () => query('.popover')[0] || null;
  const query = selector => queryAll(html, selector);

  return {
    context, document, windowObj, body, clock, timers,
    tick, flushRaf, settleFrames, flushAsync, errors,
    invokeCalls, httpResponses, defaults, deferred,
    fire, emitNative, byId, query, popover,
    get state() { return exposed.state; },
    exposed,
  };
}

// ---------------------------------------------------------------------------
// Test runner
// ---------------------------------------------------------------------------

const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

async function run() {
  let passed = 0;
  const failures = [];
  for (const { name, fn } of tests) {
    try {
      await fn();
      passed++;
      console.log(`  ok  ${name}`);
    } catch (error) {
      failures.push({ name, error });
      console.log(`FAIL  ${name}\n      ${error?.stack?.split('\n').slice(0, 4).join('\n      ')}`);
    }
  }
  console.log(`\n${passed}/${tests.length} interaction tests passed`);
  if (failures.length) process.exitCode = 1;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

test('open -> drill -> refresh data -> Back -> close -> reopen stays consistent', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 50 }),
      snapshot('codex', { costUsd: 30 }),
    ],
  });
  const { fire, emitNative, byId, query, tick, flushAsync, settleFrames, defaults } = harness;
  await flushAsync();
  tick(250); // backstop reveal
  emitNative('poc-opened');
  tick(90);
  await flushAsync();

  const popover = query('.popover')[0];
  assert.ok(popover.classList.contains('shown'), 'popup is shown after open');

  // Drill down.
  const breakdownAction = query('[data-metric="breakdown"]')[0];
  fire(breakdownAction, 'click');
  tick(72); // entrance wait
  await flushAsync();
  settleFrames();
  assert.equal(harness.state.view, 'breakdown', 'Full breakdown action enters the drill-down');
  assert.equal(byId('breakdown').hidden, false, 'breakdown section is visible');
  assert.equal(byId('breakdown-back').hidden, false, 'Back control is visible');

  const initialTotal = query('.breakdown-summary strong').map(node => node.textContent);
  assert.ok(initialTotal.includes('$80.00'), `breakdown summary shows $80.00 before refresh, got ${initialTotal.join(', ')}`);

  // Data changes while the drill-down is open.
  defaults.fetch_usage = () => [
    snapshot('claude-code', { costUsd: 70 }),
    snapshot('codex', { costUsd: 5 }),
  ];
  emitNative('poc-refresh', true);
  await flushAsync();
  const refreshedTotal = query('.breakdown-summary strong').map(node => node.textContent);
  assert.ok(refreshedTotal.includes('$75.00'), `drill-down table follows the refresh (now ${refreshedTotal.join(', ')})`);
  assert.ok(!refreshedTotal.includes('$80.00'), `stale pre-refresh total gone (now ${refreshedTotal.join(', ')})`);

  // Back.
  fire(byId('breakdown-back'), 'click');
  tick(72);
  await flushAsync();
  assert.equal(harness.state.view, 'compact', 'Back returns to compact');
  assert.equal(byId('breakdown').hidden, true, 'breakdown section hidden after Back');

  // Close and reopen.
  emitNative('poc-closing', 1);
  assert.ok(popover.classList.contains('closing'), 'closing transition on dismissal');
  emitNative('poc-opened');
  assert.equal(harness.state.view, 'compact', 'reopen resets to compact geometry');
  assert.ok(popover.classList.contains('shown'), 'reopen shows the popup again');
  assert.equal(popover.classList.contains('closing'), false, 'reopen clears the closing state');
  await flushAsync();
});

test('Others tooltip re-syncs membership on refresh and closes when the tail dissolves', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 90 }),
      snapshot('codex', { costUsd: 5 }),
      snapshot('cursor', { costUsd: 3 }),
      snapshot('copilot', { costUsd: 1 }),
      snapshot('devin', { costUsd: 1 }),
    ],
  });
  const { fire, emitNative, byId, query, tick, flushAsync, defaults, settleFrames } = harness;
  await flushAsync();
  tick(250);
  settleFrames();
  emitNative('poc-opened');
  await flushAsync();

  const rows = query('#spend-legend .legend-row');
  const othersRow = rows.find(row => row.dataset.spendProvider === 'others');
  assert.ok(othersRow, 'the small tail is grouped into Others');
  const others = harness.exposed.lastSpendRootRows.find(row => row.id === 'others');
  assert.equal(others.children.map(child => child.id).join(','), 'devin,copilot');

  // Open the tooltip.
  fire(othersRow, 'mouseover', { clientX: 200, clientY: 200 });
  assert.ok(byId('spend-other-tooltip').classList.contains('is-open'), 'Others tooltip opens on hover');
  assert.ok(byId('spend-other-list').textContent.includes('Devin'), 'tooltip lists the grouped children');

  // Refresh changes tail membership but keeps an aggregate.
  defaults.fetch_usage = () => [
    snapshot('claude-code', { costUsd: 90 }),
    snapshot('codex', { costUsd: 5 }),
    snapshot('cursor', { costUsd: 3 }),
    snapshot('copilot', { costUsd: 1 }),
    snapshot('grok', { costUsd: 2 }),
  ];
  emitNative('poc-refresh', true);
  await flushAsync();
  const regrouped = harness.exposed.lastSpendRootRows.find(row => row.id === 'others');
  assert.equal(regrouped.children.map(child => child.id).join(','), 'grok,copilot', 'tail membership follows the refresh');
  assert.ok(byId('spend-other-tooltip').classList.contains('is-open'), 'tooltip stays open while the aggregate exists');
  assert.ok(!byId('spend-other-list').textContent.includes('Devin'), 'tooltip no longer lists a provider that left the tail');
  assert.ok(byId('spend-other-list').textContent.includes('Grok'), 'tooltip lists the provider that joined the tail');

  // Refresh dissolves the tail entirely: tooltip must close, not float over nothing.
  defaults.fetch_usage = () => [
    snapshot('claude-code', { costUsd: 96.5 }),
    snapshot('codex', { costUsd: 3 }),
    snapshot('cursor', { costUsd: 0.5 }),
  ];
  emitNative('poc-refresh', true);
  await flushAsync();
  assert.equal(harness.exposed.lastSpendRootRows.some(row => row.id === 'others'), false, 'one small provider stays visible');
  assert.equal(byId('spend-other-tooltip').classList.contains('is-open'), false, 'tooltip closes when the aggregate disappears');
  assert.equal(harness.state.spendTooltipRowId, null, 'tooltip row id cleared');
});

test('ring hover is cleared when a refresh removes the hovered provider', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 97 }),
      snapshot('codex', { costUsd: 3 }),
    ],
  });
  const { fire, emitNative, byId, query, tick, flushAsync, defaults, settleFrames } = harness;
  await flushAsync();
  tick(250);
  settleFrames();
  emitNative('poc-opened');
  await flushAsync();

  const ring = byId('spend-ring');
  ring._rect = { left: 0, top: 0, width: 200, height: 200 };
  // Angle ~120 degrees puts the cursor inside the dominant Claude slice.
  const angle = Math.PI * 2 / 3;
  fire(ring, 'mousemove', { clientX: 100 + 67 * Math.cos(angle), clientY: 100 + 67 * Math.sin(angle) });
  assert.equal(harness.state.hoveredSpendProviderId, 'claude-code', 'hover lands on the Claude slice');

  defaults.fetch_usage = () => [snapshot('codex', { costUsd: 100 })];
  emitNative('poc-refresh', true);
  await flushAsync();
  assert.equal(harness.state.hoveredSpendProviderId, null, 'hover cleared when the provider disappears');
  assert.ok(!byId('spend-legend').textContent.includes('Claude'), 'legend no longer lists the removed provider');
});

test('rapid open/close cycles keep classes consistent and leak no popover listeners', async () => {
  const harness = createHarness({ snapshots: [] });
  const { emitNative, query, tick, flushAsync } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  const popover = query('.popover')[0];
  assert.ok(popover.classList.contains('shown'));

  for (let index = 0; index < 4; index++) {
    emitNative('poc-closing', index + 1);
    assert.ok(popover.classList.contains('closing'), `close ${index} enters closing`);
    emitNative('poc-opened');
    assert.ok(popover.classList.contains('shown'), `reopen ${index} shows again`);
    assert.ok(!popover.classList.contains('closing'), `reopen ${index} clears closing`);
  }
  assert.equal((popover._listeners.transitionend || []).length, 1,
    'exactly one transitionend listener survives repeated open/close cycles');
  tick(90);
  await flushAsync();
});

test('keyboard drill -> Escape collapses instead of hiding; popup stays shown', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 50 }),
      snapshot('codex', { costUsd: 30 }),
    ],
  });
  const { fire, emitNative, byId, query, tick, flushAsync, settleFrames, invokeCalls, document } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  fire(query('[data-metric="breakdown"]')[0], 'click');
  tick(72);
  await flushAsync();
  settleFrames();
  assert.equal(harness.state.view, 'breakdown', 'drilled into breakdown');

  // Switch to the 90-day period first so the hover can travel past 30 days.
  fire(query('[data-breakdown-period="90"]')[0], 'click');
  await flushAsync();

  // Keyboard hover on the chart.
  const chart = byId('breakdown-chart');
  chart._rect = { left: 0, top: 0, width: 640, height: 280 };
  chart.focus();
  fire(chart, 'keydown', { key: 'ArrowRight' });
  assert.equal(harness.state.breakdownHoverIndex, 1, 'ArrowRight moves the hover one day');
  assert.equal(byId('breakdown-chart-tooltip').hidden, false, 'tooltip shows the hovered day');

  // Escape collapses the drill-down one level.
  fire(document, 'keydown', { key: 'Escape', code: 'Escape' });
  tick(72);
  await flushAsync();
  assert.equal(harness.state.view, 'compact', 'Escape backs out of the drill-down');
  assert.equal(byId('breakdown').hidden, true, 'breakdown hidden after Escape');
  const popover = query('.popover')[0];
  assert.ok(popover.classList.contains('shown'), 'popup stays shown after Escape');
  assert.equal(invokeCalls.some(call => call.command === 'hide_popup'), false, 'Escape did not hide the popup');

  // Reopen the drill-down with the keyboard path still sane.
  fire(query('[data-metric="breakdown"]')[0], 'click');
  tick(72);
  await flushAsync();
  settleFrames();
  assert.equal(harness.state.view, 'breakdown', 'drill-down reopens');
  assert.equal(byId('breakdown-chart-tooltip').hidden, false, 'reopen re-syncs the tooltip for the kept hover');
});

test('Escape ordering: select -> notification menu -> overlays -> settings page -> popup', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
    settingsData: {
      settings: {
        usageDisplay: 'Used', resetTimeDisplay: 'Countdown', taskbarPositionLocked: true,
        motionPreference: 'system', notificationsEnabled: true, notificationProviderIds: [],
        disabledProviders: [], starredMetrics: [], spendMetric: 'cost', hideFromScreenShare: false,
      },
      providers: [
        { id: 'codex', displayName: 'Codex', available: true },
        { id: 'claude-code', displayName: 'Claude Code', available: true },
      ],
      metricNames: ['codex:weekly'],
      providerStatuses: [],
    },
  });
  const { fire, emitNative, byId, query, tick, flushAsync, invokeCalls, document } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const escape = () => fire(document, 'keydown', { key: 'Escape', code: 'Escape' });

  // Open settings, then its first enhanced select.
  fire(query('[data-options="settings"]')[0], 'click');
  await flushAsync();
  assert.ok(byId('settings-view').classList.contains('active'), 'settings page open');
  const trigger = query('#settings-view .select-trigger')[0];
  fire(trigger, 'click');
  assert.ok(query('.select-control.open').length === 1, 'select menu open');

  escape();
  assert.equal(query('.select-control.open').length, 0, 'Escape closes the select menu first');
  assert.ok(byId('settings-view').classList.contains('active'), 'settings page stays open');
  assert.equal(invokeCalls.some(call => call.command === 'hide_popup'), false, 'popup not hidden');

  escape();
  assert.equal(byId('settings-view').classList.contains('active'), false, 'Escape closes the settings page next');
  const popover = query('.popover')[0];
  assert.ok(popover.classList.contains('shown'), 'popup still shown after page-level Escape');

  // The notification picker closes before the page does.
  fire(query('[data-options="settings"]')[0], 'click');
  await flushAsync();
  fire(byId('notification-provider-trigger'), 'click');
  assert.ok(byId('notification-provider-picker').classList.contains('open'), 'picker open');
  escape();
  assert.equal(byId('notification-provider-picker').classList.contains('open'), false, 'Escape closes the picker');
  escape();
  assert.equal(byId('settings-view').classList.contains('active'), false, 'Escape closes the page after the picker');

  // Header overlay before page.
  fire(query('[data-options="customize"]')[0], 'click');
  await flushAsync();
  assert.ok(byId('customize-view').classList.contains('active'), 'customize page open');
  escape();
  assert.equal(byId('customize-view').classList.contains('active'), false, 'Escape closes the customize page');

  // Final Escape on a bare popup hides it.
  escape();
  await flushAsync();
  assert.ok(invokeCalls.some(call => call.command === 'hide_popup'), 'Escape on the bare popup hides it');
});

test('out-of-order settings requests: a slower older request cannot activate its page', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
    settingsData: {
      settings: { usageDisplay: 'Used', resetTimeDisplay: 'Countdown', taskbarPositionLocked: true, motionPreference: 'system', notificationsEnabled: true, notificationProviderIds: [], disabledProviders: [], starredMetrics: [], spendMetric: 'cost' },
      providers: [{ id: 'codex', displayName: 'Codex', available: true }],
      metricNames: ['codex:weekly'],
      providerStatuses: [],
    },
  });
  const { fire, emitNative, query, tick, flushAsync, defaults, deferred } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const settingsRequest = deferred();
  const customizeRequest = deferred();
  const settingsQueue = [settingsRequest, customizeRequest];
  defaults.get_settings_data = () => settingsQueue.shift().promise;

  fire(query('[data-options="settings"]')[0], 'click');
  await flushAsync();
  fire(query('[data-options="customize"]')[0], 'click');
  await flushAsync();

  // The older (settings) request resolves LAST — it must be ignored.
  customizeRequest.resolve(harness.defaults.get_settings_data ? {
    settings: { usageDisplay: 'Used', resetTimeDisplay: 'Countdown', taskbarPositionLocked: true, motionPreference: 'system', notificationsEnabled: true, notificationProviderIds: [], disabledProviders: [], starredMetrics: [], spendMetric: 'cost' },
    providers: [{ id: 'codex', displayName: 'Codex', available: true }],
    metricNames: ['codex:weekly'],
    providerStatuses: [],
  } : null);
  await flushAsync();
  assert.ok(query('#customize-view.active').length === 1, 'newer customize request wins');
  assert.equal(query('#settings-view.active').length, 0, 'settings not active yet');

  settingsRequest.resolve({});
  await flushAsync();
  assert.ok(query('#customize-view.active').length === 1, 'stale settings response does not switch pages');
  assert.equal(query('#settings-view.active').length, 0, 'stale response cannot activate settings');
});

test('customize statuses: blocking failures render red, recoverable ones amber', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
    settingsData: {
      settings: { usageDisplay: 'Used', resetTimeDisplay: 'Countdown', taskbarPositionLocked: true, motionPreference: 'system', notificationsEnabled: true, notificationProviderIds: [], disabledProviders: [], starredMetrics: [], spendMetric: 'cost' },
      providers: [
        { id: 'codex', displayName: 'Codex', available: true },
        { id: 'cursor', displayName: 'Cursor', available: false },
        { id: 'antigravity', displayName: 'Antigravity', available: true },
        { id: 'claude-code', displayName: 'Claude Code', available: true },
      ],
      metricNames: ['codex:weekly'],
      providerStatuses: [
        { id: 'cursor', reason: 'Cursor was not detected on this Windows account.', category: 'NotInstalled' },
        { id: 'antigravity', reason: 'Antigravity sign-in expired. Open Antigravity or Gemini CLI and sign in again.', category: 'Authentication' },
        // A status without a category is a warning-only snapshot from an older host build.
        { id: 'claude-code', reason: 'Claude usage is temporarily unavailable.' },
      ],
    },
  });
  const { fire, emitNative, byId, query, tick, flushAsync } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  fire(query('[data-options="customize"]')[0], 'click');
  await flushAsync();
  assert.ok(byId('customize-view').classList.contains('active'), 'customize page open');

  // The harness selector engine splits segments on whitespace, so match triggers through their
  // provider group rather than an attribute value containing spaces.
  const cursorTrigger = query('[data-provider-group="cursor"] [data-provider-status]')[0];
  const antigravityTrigger = query('[data-provider-group="antigravity"] [data-provider-status]')[0];
  assert.ok(cursorTrigger, 'cursor status trigger rendered');
  assert.equal(cursorTrigger.dataset.providerSeverity, 'error', 'NotInstalled classifies as blocking');
  assert.ok(cursorTrigger.querySelector('.provider-customize-logo').classList.contains('is-error'), 'blocking logo gets the red variant');
  assert.ok(cursorTrigger.getAttribute('aria-label').includes('unavailable'), 'blocking trigger says unavailable');

  const warningTriggers = query('[data-provider-severity="warning"]');
  assert.equal(warningTriggers.length, 2, 'Authentication and category-less statuses classify as recoverable');
  assert.ok(antigravityTrigger, 'antigravity status trigger rendered');
  assert.ok(!antigravityTrigger.querySelector('.provider-customize-logo').classList.contains('is-error'), 'recoverable logo keeps the amber variant');
  assert.ok(antigravityTrigger.getAttribute('aria-label').includes('needs attention'), 'recoverable trigger says needs attention');

  // Hover a blocking trigger: the tooltip anchors to it and carries the red title.
  fire(cursorTrigger, 'mousemove', { clientX: 30, clientY: 30 });
  const tooltip = byId('provider-status-tooltip');
  assert.ok(tooltip.classList.contains('is-open'), 'tooltip opens on hover');
  assert.ok(tooltip.classList.contains('is-error'), 'tooltip gets the red variant');
  assert.ok(tooltip.textContent.includes('Unavailable'), 'tooltip title says Unavailable in words');
  assert.ok(tooltip.textContent.includes('not detected'), 'tooltip shows the host reason');
  assert.ok(tooltip.style.left, 'tooltip is positioned');

  // Moving within the same trigger must not reposition or reset it; moving to a recoverable
  // trigger swaps the severity.
  const left = tooltip.style.left;
  fire(cursorTrigger, 'mousemove', { clientX: 34, clientY: 32 });
  assert.equal(tooltip.style.left, left, 'moving inside one trigger does not reposition the anchored tooltip');
  fire(antigravityTrigger, 'mousemove', { clientX: 40, clientY: 40 });
  assert.ok(!tooltip.classList.contains('is-error'), 'tooltip loses the red variant for recoverable statuses');
  assert.ok(tooltip.textContent.includes('Needs attention'), 'tooltip title says Needs attention');

  // Leaving the list hides the tooltip.
  fire(byId('customize-providers'), 'mouseleave');
  assert.ok(!tooltip.classList.contains('is-open'), 'tooltip closes on mouseleave');
});

test('refresh reentrancy: a refresh in flight swallows later requests without clobbering state', async () => {
  const harness = createHarness({ snapshots: [] });
  const { fire, emitNative, query, tick, flushAsync, defaults, deferred, invokeCalls, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const firstUsage = deferred();
  const fetchesBefore = invokeCalls.filter(call => call.command === 'fetch_usage').length;
  defaults.fetch_usage = () => firstUsage.promise;
  fire(byId('refresh-button'), 'click');
  await flushAsync();
  assert.equal(harness.state.localLoading, true, 'refresh starts loading');
  assert.equal(
    invokeCalls.filter(call => call.command === 'fetch_usage').length,
    fetchesBefore + 1,
    'the refresh button issues exactly one usage fetch');

  fire(byId('refresh-button'), 'click');
  await flushAsync();
  assert.equal(invokeCalls.filter(call => call.command === 'fetch_usage').length, fetchesBefore + 1,
    'a second refresh during an in-flight one is dropped, not queued');

  firstUsage.resolve([snapshot('codex', { costUsd: 42 })]);
  await flushAsync();
  assert.equal(harness.state.localLoading, false, 'loading clears after the response');
  const rows = harness.exposed.lastSpendRootRows;
  assert.equal(rows.length, 1, 'the single response painted');
  assert.equal(rows[0].name, 'Codex');
});

test('switch data empty -> populated -> empty leaves no stale hover or tooltip', async () => {
  const harness = createHarness({ snapshots: [] });
  const { fire, emitNative, query, tick, flushAsync, defaults, settleFrames, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  assert.equal(query('#spend-legend .legend-row').length, 0, 'empty data renders an empty legend');
  assert.equal(byId('spend-other-tooltip').classList.contains('is-open'), false, 'no tooltip on empty data');

  defaults.fetch_usage = () => [
    snapshot('claude-code', { costUsd: 70 }),
    snapshot('codex', { costUsd: 20 }),
    snapshot('cursor', { costUsd: 5 }),
    snapshot('copilot', { costUsd: 3 }),
    snapshot('devin', { costUsd: 2 }),
  ];
  emitNative('poc-refresh', true);
  await flushAsync();
  settleFrames();
  assert.ok(query('#spend-legend .legend-row').length >= 2, 'populated data renders rows');

  // Open the Others tooltip, then empty the data again.
  const othersRow = query('#spend-legend .legend-row').find(row => row.dataset.spendProvider === 'others');
  if (othersRow) fire(othersRow, 'mouseover', { clientX: 200, clientY: 200 });
  defaults.fetch_usage = () => [];
  emitNative('poc-refresh', true);
  await flushAsync();
  assert.equal(query('#spend-legend .legend-row').length, 0, 'data emptied again');
  assert.equal(byId('spend-other-tooltip').classList.contains('is-open'), false, 'tooltip closed on empty refresh');
  assert.equal(harness.state.hoveredSpendProviderId, null, 'hover cleared on empty refresh');
});

test('breakdown hover tooltip never points past the selected period', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 50 }),
      snapshot('codex', { costUsd: 30 }),
    ],
  });
  const { fire, emitNative, query, tick, flushAsync, settleFrames, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  fire(query('[data-metric="breakdown"]')[0], 'click');
  tick(72);
  await flushAsync();
  settleFrames();

  const chart = byId('breakdown-chart');
  chart._rect = { left: 0, top: 0, width: 640, height: 280 };
  chart.focus();
  // 90-day view first, then move far right (day ~60).
  fire(query('[data-breakdown-period="90"]')[0], 'click');
  await flushAsync();
  for (let index = 0; index < 60; index++) {
    fire(chart, 'keydown', { key: 'ArrowRight' });
  }
  assert.equal(harness.state.breakdownHoverIndex, 60, 'hover at day 60 of the 90-day view');
  assert.equal(byId('breakdown-chart-tooltip').hidden, false, 'tooltip open on the hovered day');

  // Switch to 7 days: the hover index no longer exists there.
  fire(query('[data-breakdown-period="7"]')[0], 'click');
  await flushAsync();
  assert.equal(harness.state.breakdownHoverIndex, null, 'out-of-range hover index cleared on period switch');
  assert.equal(byId('breakdown-chart-tooltip').hidden, true, 'stale tooltip closed after period switch');

  // A refresh while hovering re-syncs the tooltip to the new data instead of keeping old values.
  // The fixture places each provider's period total on dayKey(29), which is day 0 of the 30-day
  // range — hover that day and compare the tooltip before and after a refresh.
  fire(query('[data-breakdown-period="30"]')[0], 'click');
  await flushAsync();
  fire(chart, 'focus');
  assert.equal(harness.state.breakdownHoverIndex, 0, 'focus selects the first day');
  const tooltipBefore = byId('breakdown-chart-tooltip').textContent;
  harness.defaults.fetch_usage = () => [
    snapshot('claude-code', { costUsd: 99 }),
    snapshot('codex', { costUsd: 1 }),
  ];
  harness.emitNative('poc-refresh', true);
  await flushAsync();
  assert.equal(harness.state.breakdownHoverIndex, 0, 'hover index survives a refresh');
  assert.notEqual(byId('breakdown-chart-tooltip').textContent, tooltipBefore, 'tooltip values follow the refresh');
  assert.equal(byId('breakdown-chart-tooltip').hidden, false, 'tooltip still open after refresh');
});

test('breakdown chart listeners are wired once per canvas across repeated renders', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 50 }),
      snapshot('codex', { costUsd: 30 }),
    ],
  });
  const { fire, emitNative, query, tick, flushAsync, settleFrames, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  fire(query('[data-metric="breakdown"]')[0], 'click');
  tick(72);
  await flushAsync();
  settleFrames();

  const countListeners = () => {
    const canvas = byId('breakdown-chart');
    return {
      pointermove: (canvas._listeners.pointermove || []).length,
      pointerleave: (canvas._listeners.pointerleave || []).length,
      keydown: (canvas._listeners.keydown || []).length,
    };
  };
  const before = countListeners();

  // Several structural re-renders: period, grouping, sort, metric, provider toggle.
  for (const period of ['7', '30', '90', '30']) {
    fire(query(`[data-breakdown-period="${period}"]`)[0], 'click');
    await flushAsync();
  }
  fire(query('[data-breakdown-group="day"]')[0], 'click');
  await flushAsync();
  fire(query('[data-breakdown-group="model"]')[0], 'click');
  await flushAsync();
  fire(query('[data-breakdown-sort]')[0], 'click');
  await flushAsync();
  fire(query('[data-breakdown-chart="tokens"]')[0], 'click');
  await flushAsync();

  const after = countListeners();
  assert.deepEqual(after, before, 'no listener accumulation across re-renders');
  assert.ok(byId('breakdown-chart').dataset.chartWired === 'true', 'canvas marked wired');
});

test('trend tooltip closes when the provider list rebuilds underneath it', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
  });
  const { fire, emitNative, query, tick, flushAsync, defaults, document } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const bars = query('#providers .trend i');
  assert.ok(bars.length >= 30, 'trend bars exist');
  const bar = bars[5];
  fire(document, 'mousemove', { target: bar, clientX: 100, clientY: 100 });
  tick(80); // tooltip delay
  assert.ok(query('#trend-tooltip')[0].classList.contains('is-open'), 'trend tooltip opens over the bar');

  // A structural change (new provider card) rebuilds the list under the open tooltip.
  defaults.fetch_usage = () => [
    snapshot('codex', { costUsd: 10, extraLines: [{ type: 'text', label: 'Extra', value: 'x' }] }),
  ];
  emitNative('poc-refresh', true);
  await flushAsync();
  assert.equal(query('#trend-tooltip')[0].classList.contains('is-open'), false, 'tooltip closed after rebuild');
});

test('usage history chevron expands and collapses its provider card', async () => {
  const harness = createHarness({ snapshots: [snapshot('codex', { costUsd: 12, tokens: 3400 })] });
  const { fire, emitNative, tick, flushAsync, query } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const button = query('[data-history-disclosure]')[0];
  const details = query('.history-details')[0];
  assert.ok(button, 'history disclosure is rendered');
  assert.equal(button.getAttribute('aria-expanded'), 'false', 'history starts collapsed');
  assert.equal(details.classList.contains('is-open'), false, 'details start collapsed');

  fire(button, 'click');
  assert.equal(button.getAttribute('aria-expanded'), 'true', 'button reports expanded state');
  assert.ok(button.classList.contains('is-open'), 'chevron rotates when expanded');
  assert.ok(details.classList.contains('is-open'), 'details expand in place');

  fire(button, 'click');
  assert.equal(button.getAttribute('aria-expanded'), 'false', 'button reports collapsed state');
  assert.equal(details.classList.contains('is-open'), false, 'details collapse in place');
});

test('reduced motion: no entrance animation, no geometry wait, native animator told', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
    reducedMotion: true,
  });
  const { fire, emitNative, query, tick, flushAsync, invokeCalls, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const popover = query('.popover')[0];
  assert.ok(popover.classList.contains('shown'), 'popup shown');
  assert.equal(popover.classList.contains('opening'), false, 'no entrance animation under reduced motion');
  assert.ok(query('html')[0].classList.contains('motion-reduced-effective'), 'effective motion class applied');

  // The native animator receives the reduced flag.
  assert.ok(invokeCalls.some(call => call.command === 'set_popup_motion_reduced' && call.args.reduced === true),
    'native popup animator told about reduced motion');

  // Drill-down completes without the 72ms animation wait.
  fire(query('[data-metric="breakdown"]')[0], 'click');
  await flushAsync();
  assert.equal(harness.state.view, 'breakdown', 'drill-down completes immediately under reduced motion');
  assert.ok(invokeCalls.some(call => call.command === 'set_breakdown_mode' && call.args.reducedMotion === true),
    'native resize told about reduced motion');

  // Dismissal skips the closing transition class.
  emitNative('poc-closing', 1);
  assert.equal(popover.classList.contains('closing'), false, 'no closing animation under reduced motion');
  assert.equal(popover.classList.contains('shown'), false, 'popup hidden under reduced motion');
});

test('explicit full-motion preference overrides Windows reduced motion', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
    reducedMotion: true,
    motionPreference: 'full',
  });
  const { emitNative, query, tick, flushAsync, invokeCalls } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const popover = query('.popover')[0];
  assert.ok(popover.classList.contains('opening'), 'full motion replays the entrance despite OS reduced motion');
  assert.ok(!query('html')[0].classList.contains('motion-reduced-effective'), 'no reduced-motion CSS class');
  assert.ok(invokeCalls.some(call => call.command === 'set_popup_motion_reduced' && call.args.reduced === false),
    'native animator told full motion');
});

test('visibilitychange while hidden resets the drill-down and hides the entrance state', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 50 }),
      snapshot('codex', { costUsd: 30 }),
    ],
  });
  const { fire, emitNative, query, tick, flushAsync } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  fire(query('[data-metric="breakdown"]')[0], 'click');
  tick(72);
  await flushAsync();
  assert.equal(harness.state.view, 'breakdown', 'in drill-down');

  harness.document.visibilityState = 'hidden';
  fire(harness.document, 'visibilitychange');
  await flushAsync();
  const popover = query('.popover')[0];
  assert.equal(harness.state.view, 'compact', 'hidden popup collapses the drill-down');
  assert.equal(popover.classList.contains('shown'), false, 'hidden popup drops its shown state');
  assert.equal(popover.classList.contains('opening'), false, 'hidden popup drops entrance state');
});

test('window focus reasserts visibility without replaying a completed entrance', async () => {
  const harness = createHarness({ snapshots: [] });
  const { emitNative, query, tick, flushAsync, windowObj } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  const popover = query('.popover')[0];
  assert.ok(popover.classList.contains('shown'));

  emitNative('poc-closing', 1);
  assert.ok(popover.classList.contains('closing'), 'closing entered');

  // A focus event (e.g. WebView regaining focus) re-asserts visibility.
  harness.fire(windowObj, 'focus');
  tick(90);
  await flushAsync();
  assert.ok(popover.classList.contains('shown'), 'focus restores shown state');
  assert.equal(popover.classList.contains('closing'), false, 'focus clears closing state');
  assert.equal(popover.classList.contains('opening'), false, 'focus does not replay the entrance');
});

test('status queue serializes messages with a minimum visible floor', async () => {
  const harness = createHarness({ snapshots: [] });
  const { tick, flushAsync, byId, exposed } = harness;
  await flushAsync();

  exposed.showStatus('First message', 2000);
  exposed.showStatus('Second message', 2000);
  const status = byId('status');
  assert.equal(status.textContent, 'First message', 'first status shows immediately');
  assert.ok(status.classList.contains('visible'), 'status visible');

  tick(1000);
  assert.equal(status.textContent, 'First message', 'first message stays its full duration');
  tick(1000);
  assert.equal(status.textContent, 'Second message', 'queue hands over to the second message');
  assert.ok(status.classList.contains('visible'), 'no flash between queued messages');
  tick(1000);
  assert.equal(status.textContent, 'Second message', 'second message stays its full duration');
  tick(1000);
  assert.equal(status.classList.contains('visible'), false, 'status hides when the queue empties');
});

test('rapid settings changes serialize through the apply queue without dropping the last value', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
    settingsData: {
      settings: { usageDisplay: 'Used', resetTimeDisplay: 'Countdown', taskbarPositionLocked: true, motionPreference: 'system', notificationsEnabled: true, notificationProviderIds: [], disabledProviders: [], starredMetrics: [], spendMetric: 'cost', hideFromScreenShare: false },
      providers: [{ id: 'codex', displayName: 'Codex', available: true }],
      metricNames: ['codex:weekly'],
      providerStatuses: [],
    },
  });
  const { fire, emitNative, query, tick, flushAsync, defaults, deferred, invokeCalls, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  fire(query('[data-options="settings"]')[0], 'click');
  await flushAsync();

  const apply = deferred();
  defaults.apply_settings_data = () => apply.promise;

  const usageSelect = query('#settings-view select[data-field="usageDisplay"]')[0];
  const toggle = byId('settings-view');
  const checkbox = query('#settings-view input[data-field="notificationsEnabled"]')[0];

  // Change 1 starts an apply that is held open by the deferred IPC.
  usageSelect.value = 'Remaining';
  fire(usageSelect, 'change');
  tick(160);
  await flushAsync();
  assert.equal(invokeCalls.filter(call => call.command === 'apply_settings_data').length, 1, 'first apply in flight');

  // Change 2 while the first is in flight gets queued, not lost.
  checkbox.checked = false;
  fire(checkbox, 'change');
  tick(160);
  await flushAsync();
  assert.equal(invokeCalls.filter(call => call.command === 'apply_settings_data').length, 1,
    'second change is queued behind the in-flight apply');
  assert.equal(harness.state.settings.usageDisplay, 'Used', 'in-flight value not applied yet');

  // Completing the first apply drains the queue and applies the second.
  apply.resolve(null);
  await flushAsync();
  tick(160);
  await flushAsync();
  assert.equal(invokeCalls.filter(call => call.command === 'apply_settings_data').length, 2,
    'queued apply runs after the first completes');
  assert.equal(harness.state.settings.usageDisplay, 'Remaining', 'queued value reaches state');
  assert.equal(harness.state.settings.notificationsEnabled, false, 'later queued value also reaches state');
});

test('tiny slice and one-provider datasets render without inventing or dropping providers', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 100 })],
  });
  const { emitNative, query, tick, flushAsync, settleFrames, exposed, defaults, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();
  settleFrames();

  const rows = exposed.lastSpendRootRows;
  assert.equal(rows.length, 1, 'one provider renders alone');
  assert.equal(rows[0].id, 'codex');

  const ring = byId('spend-ring');
  ring._rect = { left: 0, top: 0, width: 200, height: 200 };
  // A tiny slice must stay discoverable in the ring geometry, not vanish into a gap. The
  // 0.5% slice ends just before the top of the sweep, so probe a point there.
  defaults.fetch_usage = () => [
    snapshot('codex', { costUsd: 99.5 }),
    snapshot('cursor', { costUsd: 0.5 }),
  ];
  harness.emitNative('poc-refresh', true);
  await flushAsync();
  settleFrames();
  assert.equal(harness.exposed.lastSpendRootRows.some(row => row.id === 'others'), false,
    'one small provider stays visible, not grouped');
  const probe = { x: 100 + 67 * Math.cos(Math.PI * 1.5 - 0.011), y: 100 + 67 * Math.sin(Math.PI * 1.5 - 0.011) };
  const provider = exposed.spendProviderAtPoint(probe.x, probe.y);
  assert.equal(provider, 'cursor', `tiny slice remains hittable on the ring (probe ${probe.x.toFixed(1)},${probe.y.toFixed(1)})`);
});

test('share text stays consistent with the last rendered rows after refreshes', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 12.34 }),
      snapshot('codex', { costUsd: 4.56 }),
    ],
  });
  const { emitNative, query, tick, flushAsync, exposed, defaults } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const text = exposed.compactShareText();
  assert.match(text, /Claude \$12\.34/, 'share text mirrors the current rows');
  assert.match(text, /Total \$16\.90/, 'share text totals the current rows');

  // Refresh changes data; the next share reflects it, never a stale snapshot.
  defaults.fetch_usage = () => [
    snapshot('claude-code', { costUsd: 1.00 }),
    snapshot('codex', { costUsd: 1.00 }),
  ];
  harness.emitNative('poc-refresh', true);
  await flushAsync();
  const refreshed = exposed.compactShareText();
  assert.match(refreshed, /Total \$2\.00/, 'share text follows the refresh');
});

test('hovered Others tooltip positions clamp inside the viewport', async () => {  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 90 }),
      snapshot('codex', { costUsd: 5 }),
      snapshot('cursor', { costUsd: 3 }),
      snapshot('copilot', { costUsd: 1 }),
      snapshot('devin', { costUsd: 1 }),
    ],
    innerWidth: 320,
    innerHeight: 200,
  });
  const { fire, emitNative, query, tick, flushAsync, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const othersRow = query('#spend-legend .legend-row').find(row => row.dataset.spendProvider === 'others');
  byId('spend-other-tooltip')._rect = { left: 0, top: 0, width: 228, height: 120 };
  fire(othersRow, 'mouseover', { clientX: 310, clientY: 190 });
  const tooltip = byId('spend-other-tooltip');
  const left = Number.parseFloat(tooltip.style.left);
  const top = Number.parseFloat(tooltip.style.top);
  assert.ok(left >= 8 && left + 228 <= 320 + 1, `tooltip left edge stays in viewport (${left})`);
  assert.ok(top >= 8 && top <= 200 - 8, `tooltip top stays in viewport (${top})`);
  assert.ok(tooltip.classList.contains('is-open'), 'tooltip open');
});

test('reopen never leaves a tooltip from the previous session floating', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 90 }),
      snapshot('codex', { costUsd: 5 }),
      snapshot('cursor', { costUsd: 3 }),
      snapshot('copilot', { costUsd: 1 }),
      snapshot('devin', { costUsd: 1 }),
    ],
  });
  const { fire, emitNative, query, tick, flushAsync, byId, document } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const othersRow = query('#spend-legend .legend-row').find(row => row.dataset.spendProvider === 'others');
  fire(othersRow, 'mouseover', { clientX: 200, clientY: 200 });
  assert.ok(byId('spend-other-tooltip').classList.contains('is-open'), 'Others tooltip open');

  // A trend bar tooltip is also open when the popup is dismissed.
  fire(document, 'mousemove', { target: query('#providers .trend i')[5], clientX: 100, clientY: 100 });
  tick(80);
  assert.ok(query('#trend-tooltip')[0].classList.contains('is-open'), 'trend tooltip open');

  emitNative('poc-closing', 1);
  emitNative('poc-opened');
  await flushAsync();
  assert.equal(byId('spend-other-tooltip').classList.contains('is-open'), false,
    'Others tooltip from the previous session is gone on reopen');
  assert.equal(query('#trend-tooltip')[0].classList.contains('is-open'), false,
    'trend tooltip from the previous session is gone on reopen');
  assert.equal(harness.state.spendTooltipRowId, null, 'tooltip row state cleared');
});

test('poc-refresh while the popup is hidden refreshes data without disturbing the closed state', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
  });
  const { emitNative, query, tick, flushAsync, defaults } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();
  emitNative('poc-closing', 1);
  const popover = query('.popover')[0];
  assert.ok(popover.classList.contains('closing'), 'popup dismissed');

  defaults.fetch_usage = () => [snapshot('codex', { costUsd: 42 })];
  emitNative('poc-refresh', true);
  await flushAsync();
  assert.equal(harness.exposed.lastSpendRootRows[0].cost, 42, 'hidden popup still refreshed its data');
  assert.ok(popover.classList.contains('closing'), 'refresh never touches the closing state');
  assert.equal(popover.classList.contains('opening'), false, 'refresh never replays the entrance on a hidden popup');
});

test('tray "open-page" event opens the requested settings page', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
    settingsData: {
      settings: { usageDisplay: 'Used', resetTimeDisplay: 'Countdown', taskbarPositionLocked: true, motionPreference: 'system', notificationsEnabled: true, notificationProviderIds: [], disabledProviders: [], starredMetrics: [], spendMetric: 'cost' },
      providers: [{ id: 'codex', displayName: 'Codex', available: true }],
      metricNames: ['codex:weekly'],
      providerStatuses: [],
    },
  });
  const { emitNative, query, tick, flushAsync, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  // The Rust side emits this for the tray menu's "Settings" item.
  harness.fire(harness.windowObj, 'usage-monitor-open-page', { detail: 'customize' });
  await flushAsync();
  assert.ok(byId('customize-view').classList.contains('active'), 'tray open-page activates Customize');
  assert.equal(query('.page-view.active').length, 1, 'exactly one page active');
});

test('a hidden popup stops polling the host and resumes on reopen', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
  });
  const { emitNative, tick, flushAsync, invokeCalls } = harness;
  await flushAsync();
  const statusCalls = () => invokeCalls.filter(call => call.command === 'fetch_refresh_status').length;
  const beforeHidden = statusCalls();

  // The popup starts hidden; the 1s poll must not touch the host.
  tick(1000);
  tick(1000);
  await flushAsync();
  assert.equal(statusCalls(), beforeHidden, 'no status polling while the popup is hidden');

  // Reopen: the poll resumes on the next tick.
  emitNative('poc-opened');
  tick(1000);
  await flushAsync();
  const afterReopen = statusCalls();
  assert.ok(afterReopen > beforeHidden, 'polling resumes after reopen');

  // Dismiss: polling stops again, and poc-closing alone must not keep it alive.
  emitNative('poc-closing', 1);
  tick(1000);
  tick(1000);
  await flushAsync();
  assert.equal(statusCalls(), afterReopen, 'no status polling while the popup is closed again');
});

test('a valid empty provider list is real data, not a failed load', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
  });
  const { emitNative, tick, flushAsync, invokeCalls, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  // The next refresh returns a valid-but-empty provider list.
  harness.defaults.fetch_usage = () => [];
  harness.defaults.fetch_enabled_providers = () => [];
  emitNative('poc-refresh', true);
  await flushAsync();

  assert.ok(harness.state.lastGood !== null, 'a valid empty load counts as a successful load');
  assert.ok(byId('providers').textContent.includes('No providers are enabled'),
    'the compact view shows the empty state instead of a blank list');

  const usageCalls = () => invokeCalls.filter(call => call.command === 'fetch_usage').length;
  const afterEmpty = usageCalls();
  // Poll for several seconds: the empty list must NOT be retried as a failed load.
  tick(2100);
  await flushAsync();
  tick(2100);
  await flushAsync();
  tick(2100);
  await flushAsync();
  assert.equal(usageCalls(), afterEmpty, 'an empty provider list is never retried as a failure');
});

test('repeated settings opens do not duplicate the instant-apply change listeners', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
  });
  const { fire, emitNative, query, tick, flushAsync, invokeCalls, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const options = query('[data-options="settings"]')[0];
  const back = () => byId('settings-view').querySelector('[data-page-back]');
  const openSettings = async () => {
    fire(options, 'click');
    await flushAsync();
  };
  await openSettings();
  // Close and reopen the page three times total.
  for (let index = 1; index < 3; index++) {
    fire(back(), 'click');
    await openSettings();
  }

  // One change must produce exactly one apply, not one per open.
  const usageSelect = query('#settings-view select[data-field="usageDisplay"]')[0];
  usageSelect.value = 'Remaining';
  fire(usageSelect, 'change');
  tick(160);
  await flushAsync();
  assert.equal(
    invokeCalls.filter(call => call.command === 'apply_settings_data').length,
    1,
    'a single change applies exactly once after repeated page opens'
  );
});

test('status polling never stacks overlapping requests', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
  });
  const { emitNative, tick, flushAsync, deferred, invokeCalls } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const statusCalls = () => invokeCalls.filter(call => call.command === 'fetch_refresh_status').length;
  const slowStatus = deferred();
  harness.defaults.fetch_refresh_status = () => slowStatus.promise;
  const before = statusCalls();

  // Tick the 1s poll three times while the first status request is still pending.
  tick(1000);
  tick(1000);
  tick(1000);
  await flushAsync();
  const during = statusCalls();
  assert.ok(during <= before + 1,
    `at most one status request in flight, got ${during - before} new calls`);

  slowStatus.resolve({ loading: false, nextRefreshAt: null });
  await flushAsync();
  tick(1000);
  await flushAsync();
  const after = statusCalls();
  assert.ok(after > during, 'the poll resumes once the in-flight request settles');
});

test('a lost native status bridge cannot strand the popup in refreshing state', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
  });
  const { emitNative, tick, flushAsync, defaults, state, invokeCalls } = harness;
  await flushAsync();
  emitNative('poc-opened');
  await flushAsync();

  defaults.fetch_refresh_status = () => Promise.reject(new Error('native host restarted'));
  state.hostLoading = true;
  const statusCallsBefore = invokeCalls.filter(call => call.command === 'fetch_refresh_status').length;
  tick(1000);
  await flushAsync();

  assert.ok(
    invokeCalls.filter(call => call.command === 'fetch_refresh_status').length > statusCallsBefore,
    'a status poll was attempted after the host disappeared'
  );
  assert.equal(state.hostLoading, false, 'the stale native loading flag is cleared');
});

test('a background startup refresh does not block a usable popup', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
  });
  const { emitNative, flushAsync, defaults, state, byId } = harness;
  defaults.fetch_refresh_status = () => ({ loading: true, nextRefreshAt: null });
  await flushAsync();
  emitNative('poc-opened');
  await flushAsync();

  assert.equal(state.hostLoading, true, 'the native host can still report a startup refresh');
  assert.equal(state.localLoading, false, 'the popup request has completed');
  assert.equal(byId('updated').textContent, 'Waiting for update schedule',
    'host loading does not replace the usable popup countdown');
  assert.equal(byId('refresh-button').disabled, false,
    'host loading does not disable the popup refresh action');
});

test('Escape with the notification picker menu focused closes only the picker, keeping the settings page open', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
    settingsData: {
      settings: {
        usageDisplay: 'Used', resetTimeDisplay: 'Countdown', taskbarPositionLocked: true,
        motionPreference: 'system', notificationsEnabled: true, notificationProviderIds: [],
        disabledProviders: [], starredMetrics: [], spendMetric: 'cost', hideFromScreenShare: false,
      },
      providers: [
        { id: 'codex', displayName: 'Codex', available: true },
        { id: 'claude-code', displayName: 'Claude Code', available: true },
      ],
      metricNames: ['codex:weekly'],
      providerStatuses: [],
    },
  });
  const { fire, emitNative, query, tick, flushAsync, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  fire(query('[data-options="settings"]')[0], 'click');
  await flushAsync();
  fire(byId('notification-provider-trigger'), 'click');
  assert.ok(byId('notification-provider-picker').classList.contains('open'), 'picker open');

  // The menu's own keydown handler (target phase) used to close the picker and then let the
  // event bubble to the document handler, which closed the entire settings page in the same
  // keypress — skipping the level the ordered Escape chain is meant to preserve.
  const option = query('#notification-provider-menu [role="option"]')[0];
  option.focus();
  fire(option, 'keydown', { key: 'Escape', code: 'Escape' });
  assert.equal(byId('notification-provider-picker').classList.contains('open'), false, 'picker closed');
  assert.ok(byId('settings-view').classList.contains('active'), 'settings page still open after the first Escape');

  // The next Escape closes only the page; the popup stays shown.
  fire(harness.document, 'keydown', { key: 'Escape', code: 'Escape' });
  assert.equal(byId('settings-view').classList.contains('active'), false, 'second Escape closes the page');
  assert.ok(query('.popover')[0].classList.contains('shown'), 'popup still shown');
});

test('ring hover does not survive close and reopen', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 97 }),
      snapshot('codex', { costUsd: 3 }),
    ],
  });
  const { fire, emitNative, query, tick, flushAsync, settleFrames, byId } = harness;
  await flushAsync();
  tick(250);
  settleFrames();
  emitNative('poc-opened');
  await flushAsync();

  const ring = byId('spend-ring');
  ring._rect = { left: 0, top: 0, width: 200, height: 200 };
  const angle = Math.PI * 2 / 3;
  fire(ring, 'mousemove', { clientX: 100 + 67 * Math.cos(angle), clientY: 100 + 67 * Math.sin(angle) });
  assert.equal(harness.state.hoveredSpendProviderId, 'claude-code', 'hover lands on the Claude slice');

  // Dismiss while the phantom cursor is still over the ring (no mouseleave ever fires on a
  // hidden window), then reopen: the ring must not repaint as if that cursor were still hovering.
  emitNative('poc-closing', 1);
  emitNative('poc-opened');
  await flushAsync();
  assert.equal(harness.state.hoveredSpendProviderId, null, 'hover cleared on reopen');
  assert.equal(query('#spend-legend .legend-row.is-highlighted').length, 0, 'no legend row highlighted after reopen');
});

test('double-click share runs exactly one encode and one copy_share', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
  });
  const { fire, emitNative, query, tick, flushAsync, defaults, deferred, invokeCalls, byId } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  const share = deferred();
  defaults.copy_share = () => share.promise;
  const count = () => invokeCalls.filter(call => call.command === 'copy_share').length;
  const before = count();

  fire(byId('share-button'), 'click');
  await flushAsync();
  fire(byId('share-button'), 'click');
  await flushAsync();
  assert.equal(count(), before + 1, 'only one copy_share runs while the first is in flight');

  share.resolve(null);
  await flushAsync();
  assert.equal(count(), before + 1, 'no second copy_share after the first settles');
});

test('closing during a breakdown expansion aborts the widen and cleans the transition state', async () => {
  const harness = createHarness({
    snapshots: [
      snapshot('claude-code', { costUsd: 50 }),
      snapshot('codex', { costUsd: 30 }),
    ],
  });
  const { fire, emitNative, query, tick, flushAsync, invokeCalls } = harness;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  fire(query('[data-metric="breakdown"]')[0], 'click');
  // Dismiss inside the 72ms entrance wait, before any native widen round-trip starts.
  emitNative('poc-closing', 1);
  tick(72);
  await flushAsync();

  assert.equal(
    invokeCalls.filter(call => call.command === 'set_breakdown_mode' && call.args.expanded === true).length,
    0,
    'no native widen after a close during the entrance wait'
  );
  assert.equal(harness.state.view, 'compact', 'view stays compact');
  const popover = query('.popover')[0];
  assert.equal(popover.classList.contains('view-transitioning'), false, 'transition classes cleaned after the abort');

  emitNative('poc-opened');
  await flushAsync();
  assert.equal(harness.state.view, 'compact', 'reopen lands on compact');
  assert.ok(popover.classList.contains('shown'), 'reopen shows the popup');
});

test('a stale startup settings response cannot revert a user metric pick', async () => {
  const harness = createHarness({
    snapshots: [snapshot('codex', { costUsd: 10 })],
    settingsData: {
      settings: {
        usageDisplay: 'Used', resetTimeDisplay: 'Countdown', taskbarPositionLocked: true,
        motionPreference: 'system', notificationsEnabled: true, notificationProviderIds: [],
        disabledProviders: [], starredMetrics: [], spendMetric: 'cost', hideFromScreenShare: false,
      },
      providers: [{ id: 'codex', displayName: 'Codex', available: true }],
      metricNames: ['codex:weekly'],
      providerStatuses: [],
    },
  });
  const { fire, emitNative, tick, flushAsync, defaults, deferred, invokeCalls, query } = harness;
  const settingsRequest = deferred();
  defaults.get_settings_data = () => settingsRequest.promise;
  await flushAsync();
  tick(250);
  emitNative('poc-opened');
  await flushAsync();

  // The user picks the tokens metric while the startup settings request is still pending.
  fire(query('[data-metric="tokens"]')[0], 'click');
  await flushAsync();
  assert.equal(harness.state.metric, 'tokens', 'user metric pick applied');

  settingsRequest.resolve({
    settings: {
      usageDisplay: 'Used', resetTimeDisplay: 'Countdown', taskbarPositionLocked: true,
      motionPreference: 'system', notificationsEnabled: true, notificationProviderIds: [],
      disabledProviders: [], starredMetrics: [], spendMetric: 'cost', hideFromScreenShare: false,
    },
    providers: [{ id: 'codex', displayName: 'Codex', available: true }],
    metricNames: ['codex:weekly'],
    providerStatuses: [],
  });
  await flushAsync();
  assert.equal(harness.state.metric, 'tokens', 'stale startup response cannot revert the metric');
  assert.ok(invokeCalls.some(call => call.command === 'set_spend_metric' && call.args.metric === 'tokens'),
    'the user pick reached the host');
});

// Only run the suite when this file is the entry point; importing it for harness reuse must not
// start the tests or disturb the process-wide DOM/timer state the suite uses.
if (process.argv[1] && fileURLToPath(import.meta.url).replace(/\\/g, '/') === process.argv[1].replace(/\\/g, '/')) {
  run();
}

export { createHarness, snapshot, dailyCost };
