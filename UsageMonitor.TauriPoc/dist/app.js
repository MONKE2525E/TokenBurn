const TAURI = window.__TAURI__;
const invoke = (command, args) => TAURI?.core?.invoke(command, args);
const currentWindow = () => TAURI?.window?.getCurrentWindow?.();
const savedMotionPreference = (() => {
  try { return window.localStorage?.getItem?.('tokenburn-motion-preference') || 'system'; }
  catch (_) { return 'system'; }
})();

const LOCAL_USAGE_API = 'http://127.0.0.1:6736/v1/usage';
const COMMAND_TIMEOUT_MS = 3500;

function withTimeout(promise, timeoutMs, message) {
  let timer;
  return Promise.race([
    Promise.resolve(promise),
    new Promise((_, reject) => {
      timer = window.setTimeout(() => reject(new Error(message)), timeoutMs);
    }),
  ]).finally(() => window.clearTimeout(timer));
}

async function fetchUsageSnapshots(force = false) {
  // The Rust command is the normal path.  Some WebView2 startup races leave the command promise
  // unresolved even though the WPF host and its loopback API are already healthy.  Do not let that
  // freeze the whole dashboard: after a short bound, read that same local API directly.
  try {
    const snapshots = await withTimeout(
      invoke('fetch_usage', { force }),
      COMMAND_TIMEOUT_MS,
      'The popup command did not respond.'
    );
    if (Array.isArray(snapshots)) return snapshots;
  } catch (_) {
    // Fall through to the loopback endpoint below.
  }

  const suffix = force ? '?force=true' : '';
  const response = await withTimeout(
    fetch(`${LOCAL_USAGE_API}${suffix}`, { cache: 'no-store' }),
    COMMAND_TIMEOUT_MS,
    'The local usage service did not respond.'
  );
  if (!response.ok) throw new Error(`The local usage service returned HTTP ${response.status}.`);
  const snapshots = await response.json();
  if (!Array.isArray(snapshots)) throw new Error('The local usage service returned no provider list.');
  return snapshots;
}

async function requestDesktopRefresh() {
  try {
    await withTimeout(
      invoke('request_desktop_refresh'),
      COMMAND_TIMEOUT_MS,
      'The desktop refresh command did not respond.'
    );
  } catch (_) {
    // The direct API performs the requested refresh when the bridge is unavailable.
    await fetchUsageSnapshots(true);
  }
}

async function fetchEnabledProviders() {
  try {
    const providers = await withTimeout(
      invoke('fetch_enabled_providers'),
      COMMAND_TIMEOUT_MS,
      'The provider-settings command did not respond.'
    );
    return Array.isArray(providers) ? providers : state.enabledProviders;
  } catch (_) {
    // Keep the established visible-provider selection. Snapshot data remains fully live.
    return state.enabledProviders;
  }
}

const state = {
  snapshots: [],
  period: '30',
  metric: 'cost',
  view: 'compact',
  compactMetric: 'cost',
  breakdownPeriod: '30',
  breakdownMetric: 'cost',
  breakdownGrouping: 'model',
  breakdownSort: { column: 'costUsd', direction: 'desc' },
  hiddenChartProviders: new Set(),
  breakdownHoverIndex: null,
  localLoading: false,
  hostLoading: false,
  refreshStatusError: '',
  lastGood: null,
  nextRefreshAt: null,
  enabledProviders: ['claude-code', 'codex', 'antigravity'],
  settings: {
    usageDisplay: 'Used',
    resetTimeDisplay: 'Countdown',
    taskbarPositionLocked: true,
    motionPreference: savedMotionPreference,
    notificationsEnabled: true,
    notificationProviderIds: [],
  },
  hoveredSpendProviderId: null,
  spendTooltipRowId: null,
  spendTooltipTimer: 0,
  providerCatalog: [
    { id: 'claude-code', displayName: 'Claude Code' },
    { id: 'codex', displayName: 'Codex' },
    { id: 'antigravity', displayName: 'Antigravity' },
    { id: 'cursor', displayName: 'Cursor' },
    { id: 'copilot', displayName: 'Copilot' },
    { id: 'devin', displayName: 'Devin' },
    { id: 'grok', displayName: 'Grok' },
    { id: 'opencode', displayName: 'OpenCode' },
  ],
};

const $ = (selector) => document.querySelector(selector);
const esc = (value) => String(value ?? '').replace(/[&<>"']/g, ch => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&#39;' }[ch]));

function providerColor(id) {
  return id === 'claude-code' ? 'var(--orange)' : id === 'codex' ? 'var(--teal)' : id === 'antigravity' ? '#f1f1f2' : '#8d7dff';
}

function progressLine(line) {
  return line && line.type === 'progress' && Number.isFinite(line.limit) && line.limit > 0;
}

const providerLogoPaths = {
  codex: './assets/providers/codex.svg',
  'claude-code': './assets/providers/claude.svg',
  antigravity: './assets/providers/antigravity.svg',
  cursor: './assets/providers/cursor.svg',
  copilot: './assets/providers/copilot.svg',
  devin: './assets/providers/devin.svg',
  grok: './assets/providers/grok.svg',
  opencode: './assets/providers/opencode.svg',
  openrouter: './assets/providers/openrouter.svg',
  zai: './assets/providers/zai.svg',
};

function providerLogo(id) {
  const source = state.providerCatalog.find(provider => provider.id === id)?.logo || providerLogoPaths[id];
  return source
    ? `<img src="${source}" alt="" aria-hidden="true" loading="eager">`
    : '<span class="provider-fallback" aria-hidden="true"></span>';
}

function formatReset(value) {
  if (!value) return 'No reset data';
  if (state.settings.resetTimeDisplay === 'Exact time') {
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return 'No reset data';
    return `Resets ${date.toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })}`;
  }
  const ms = new Date(value).getTime() - Date.now();
  if (ms <= 0) return 'Resetting now';
  const totalMinutes = Math.ceil(ms / 60000);
  const days = Math.floor(totalMinutes / 1440);
  const hours = Math.floor((totalMinutes % 1440) / 60);
  const minutes = totalMinutes % 60;
  return `Resets in ${days ? `${days}d ` : ''}${hours ? `${hours}h ` : ''}${minutes}m`.trim();
}

function formatRefreshCountdown(value) {
  if (!value) return 'Waiting for update schedule';
  const seconds = Math.max(0, Math.ceil((new Date(value).getTime() - Date.now()) / 1000));
  if (seconds >= 60) return `Next update in ${Math.max(1, Math.ceil(seconds / 60))}m`;
  return `Next update in ${seconds}s`;
}

// Providers group their local history by the Windows local calendar day. Keep the selector local as
// well so evening responses do not move into tomorrow when their SQLite timestamps are UTC.
function periodPoints(snapshot) {
  const points = snapshot.usageHistory?.points || [];
  const now = new Date();
  const day = dayKey(0);
  const yesterday = dayKey(1);
  return points.filter(point => {
    if (state.period === 'today') return point.date === day;
    if (state.period === 'yesterday') return point.date === yesterday;
    return point.date >= dayKey(29) && point.date <= day;
  });
}

function spendFor(snapshot) {
  return periodPoints(snapshot).reduce((sum, point) => sum + Number(point.costUsd || 0), 0);
}

function tokensFor(snapshot) {
  return periodPoints(snapshot).reduce((sum, point) => sum + Number(point.tokens || 0), 0);
}

function compactNumber(number) {
  if (number >= 1e9) return `${(number / 1e9).toFixed(1)}B`;
  if (number >= 1e6) return `${(number / 1e6).toFixed(1)}M`;
  if (number >= 1e3) return `${(number / 1e3).toFixed(1)}K`;
  return number.toFixed(2);
}

let ringAnimationFrame = 0;
let lastRingRenderKey = '';
let lastRingHoverId = null;
let lastRingValues = [];
let lastRingSize = 0;
let ringHoverStart = 0;
let lastSpendRootRows = [];
let lastSpendDisplayedRows = [];
// render() runs during initial startup, before the lower spend-card helpers are evaluated.
// Keep this render state with the other top-level state to avoid a temporal-dead-zone crash that
// left the popup permanently in "Refreshing..." while the taskbar continued to receive data.
let lastLegendKey = '';

// CSS collapses its own durations under prefers-reduced-motion, but the canvas ring tween and the
// meter sweep are driven from JS and cannot see that media query.
const reducedMotionQuery = window.matchMedia?.('(prefers-reduced-motion: reduce)');
const prefersReducedMotion = () => {
  const preference = state.settings?.motionPreference || 'system';
  if (preference === 'full') return false;
  if (preference === 'reduced') return true;
  return Boolean(reducedMotionQuery?.matches);
};
const applyMotionPreference = () => {
  const reduced = prefersReducedMotion();
  document.documentElement?.classList.toggle('motion-reduced-effective', reduced);
  // CSS cannot move or hide the outer HWND. Keep the native popup motion in lockstep with the
  // same effective preference used by the canvas and DOM transitions.
  Promise.resolve(invoke('set_popup_motion_reduced', { reduced })).catch(() => {});
};
const rememberMotionPreference = () => {
  try { window.localStorage?.setItem?.('tokenburn-motion-preference', state.settings?.motionPreference || 'system'); }
  catch (_) { /* A locked-down WebView can still use the in-memory setting. */ }
};
applyMotionPreference();
reducedMotionQuery?.addEventListener?.('change', applyMotionPreference);

// Two durations only: a short confirmation and a long one for anything the user may need to act on.
const STATUS_SHORT = 2400;
const STATUS_LONG = 3600;
const STATUS_MIN_VISIBLE = 900;

// A single element with a single timer meant a second message overwrote the first mid-read. The
// queue gives every message a floor of time on screen.
const statusQueue = [];
let statusTimer;
let statusShownAt = 0;
let statusActive = false;

function showStatus(message, duration = STATUS_SHORT, detail = '') {
  statusQueue.push({ message, duration, detail });
  if (!statusActive) drainStatusQueue();
}

function drainStatusQueue() {
  const status = $('#status');
  const next = statusQueue.shift();
  if (!next) {
    statusActive = false;
    status.classList.remove('visible');
    return;
  }
  statusActive = true;
  status.textContent = next.message;
  if (next.detail) status.title = next.detail;
  else status.removeAttribute('title');
  status.classList.add('visible');
  statusShownAt = Date.now();
  clearTimeout(statusTimer);
  statusTimer = setTimeout(() => {
    if (statusQueue.length) {
      // Hand straight over to the next message rather than flashing the surface out and back in.
      drainStatusQueue();
      return;
    }
    statusActive = false;
    status.classList.remove('visible');
  }, Math.max(next.duration, STATUS_MIN_VISIBLE - (Date.now() - statusShownAt)));
}

// Overlays use a class rather than the `hidden` attribute: [hidden] is display:none, which cannot
// transition. See .overlay-surface in styles.css.
function setOverlayOpen(element, open) {
  if (element) element.classList.toggle('is-open', open);
}

function closeHeaderPopovers() {
  setOverlayOpen($('#info-popover'), false);
  setOverlayOpen($('#metric-popover'), false);
  // setShareMenu also resets shareMenuOpenedByPress, so a long-press release is never mistaken
  // for a plain click after the menu was dismissed some other way.
  setShareMenu(false);
  $('#metric-menu')?.setAttribute('aria-expanded', 'false');
}

function normalizeSpendMetric(value) {
  return value === 'tokens' || value === 'cost-mtok' ? value : 'cost';
}

function updateMetricMenu() {
  document.querySelectorAll('[data-metric]').forEach(item => {
    const selected = item.dataset.metric === state.metric && state.view === 'compact';
    item.classList.toggle('selected', selected);
    item.setAttribute('aria-checked', String(selected));
    const indicator = item.querySelector('.menu-indicator');
    indicator?.classList.toggle('is-on', selected);
  });
}

function breakdownDays() { return Number(state.breakdownPeriod || 30); }
function breakdownStart() { return dayKey(breakdownDays() - 1); }
function breakdownPoints(snapshot) {
  return (snapshot.usageHistory?.breakdown || []).filter(point => point.date >= breakdownStart() && point.date <= dayKey(0));
}
function breakdownProcessed(point) {
  return ['uncachedInputTokens', 'cachedInputTokens', 'cacheCreationTokens', 'outputTokens', 'reasoningTokens']
    .reduce((sum, key) => sum + Number(point[key] || 0), 0);
}
function breakdownRows() {
  const rows = [];
  state.snapshots.forEach(snapshot => {
    const points = breakdownPoints(snapshot);
    if (points.length) rows.push(...points.map(point => ({ ...point, providerName: snapshot.displayName || snapshot.providerId })));
    else (snapshot.usageHistory?.points || []).filter(point => point.date >= breakdownStart() && point.date <= dayKey(0)).forEach(point => rows.push({
      date: point.date, providerId: snapshot.providerId, providerName: snapshot.displayName || snapshot.providerId,
      modelId: null, costUsd: Number(point.costUsd || 0), processed: Number(point.tokens || 0), costBasis: 'CoarseEstimate', pricingBasis: 'Unknown', estimated: true,
    }));
  });
  return rows.map(row => ({ ...row, processed: row.processed ?? breakdownProcessed(row) }));
}
function breakdownSummary(rows) {
  return rows.reduce((result, row) => {
    result.cost += Number(row.costUsd || 0); result.processed += row.processed || 0;
    result.cached += Number(row.cachedInputTokens || 0); result.uncached += Number(row.uncachedInputTokens || 0);
    result.creation += Number(row.cacheCreationTokens || 0); result.output += Number(row.outputTokens || 0); result.reasoning += Number(row.reasoningTokens || 0);
    result.cacheSavings += Number(row.cacheSavingsUsd || 0);
    if (row.costBasis === 'ProviderReported') result.reportedCost += Number(row.costUsd || 0);
    if (row.costBasis === 'CatalogEstimated' || row.costBasis === 'CoarseEstimate') result.modelPricedCost += Number(row.costUsd || 0);
    if (row.costBasis === 'Unpriced') result.unpriced += row.processed || 0;
    return result;
  }, { cost:0, processed:0, cached:0, uncached:0, creation:0, output:0, reasoning:0, cacheSavings:0, reportedCost:0, modelPricedCost:0, unpriced:0 });
}

// The ledger aggregation the breakdown table renders. Shared with the share copy so pasting the
// text always mirrors exactly what the page shows for the current grouping and sort.
function breakdownGroupedRows(rows) {
  const modelRows = Object.values(rows.reduce((map,row) => { const key = `${row.providerId}|${row.modelId || ''}`; const target = map[key] ||= { ...row, processed:0, costUsd:0, costBases:new Set() }; target.processed += row.processed || 0; target.costUsd += Number(row.costUsd || 0); target.costBases.add(row.costBasis || 'Unknown'); target.costBasis = target.costBases.size === 1 ? [...target.costBases][0] : target.costBases.has('Unpriced') ? 'PartiallyPriced' : 'Mixed'; return map; }, {}));
  const dayRows = Object.values(rows.reduce((map,row) => { const target = map[row.date] ||= { date:row.date, processed:0, costUsd:0, providers:{}, costBases:new Set() }; target.processed += row.processed || 0; target.costUsd += Number(row.costUsd || 0); target.costBases.add(row.costBasis || 'Unknown'); target.providers[row.providerId] = (target.providers[row.providerId] || 0) + (state.breakdownMetric === 'cost' ? Number(row.costUsd || 0) : row.processed || 0); return map; }, {}));
  const data = state.breakdownGrouping === 'model' ? modelRows : dayRows;
  const column = state.breakdownSort.column; const factor = state.breakdownSort.direction === 'asc' ? 1 : -1;
  data.sort((a, b) => {
    const sortValue = item => {
      if (column === 'model') return item.modelId || item.providerName || '';
      if (column === 'date') return item.date;
      return Number(item[column] || 0);
    };
    const av = sortValue(a);
    const bv = sortValue(b);
    return typeof av === 'string' ? factor * av.localeCompare(bv) : factor * (av - bv);
  });
  return { data, modelRows, dayRows };
}

function breakdownBasisLabel(value) {
  return ({ ProviderReported: 'Provider reported', CatalogEstimated: 'Catalog estimated', CoarseEstimate: 'Coarse estimate', Unpriced: 'Unpriced', PartiallyPriced: 'Partially priced', Mixed: 'Mixed pricing' }[value] || 'Unknown');
}

function breakdownDayQuality(row) {
  if (row.costBases.has('Unpriced')) {
    return row.costBases.size > 1 ? 'Partially priced' : 'Unpriced';
  }
  if (row.costBases.has('ProviderReported')) {
    return row.costBases.size > 1 ? 'Mixed pricing' : 'Provider reported';
  }
  return 'Catalog estimated';
}

// The usage page copies as dense text only: the same summary stats and ledger rows the page
// renders, formatted for pasting into an assistant conversation.
function breakdownShareText() {
  const rows = breakdownRows();
  const summary = breakdownSummary(rows);
  const { data } = breakdownGroupedRows(rows);
  const input = summary.cached + summary.uncached + summary.creation;
  const cacheRate = input > 0 ? summary.cached / input : 0;
  const lines = [
    `TokenBurn · Usage · ${breakdownStart()} to ${dayKey(0)} (${breakdownDays()} days, local history only)`,
    `Raw cost: ${formatCost(summary.cost)} (reported ${formatCost(summary.reportedCost)}, estimated ${formatCost(summary.modelPricedCost)}${summary.unpriced ? `, unpriced ${shareTokenCount(summary.unpriced)} tokens` : ''})`,
    `Processed: ${shareTokenCount(summary.processed)} tokens · cached input ${shareTokenCount(summary.cached)} (${(cacheRate * 100).toFixed(1)}%) · uncached ${shareTokenCount(summary.uncached)} · cache creation ${shareTokenCount(summary.creation)} · output ${shareTokenCount(summary.output)}${summary.reasoning ? ` (${shareTokenCount(summary.reasoning)} reasoning)` : ''}`,
  ];
  if (summary.cacheSavings) lines.push(`Cache savings: ${formatCost(summary.cacheSavings)} (estimated with catalog rates)`);
  lines.push('');
  if (!rows.length) {
    lines.push('No local usage history is available for this range.');
    return lines.join('\n');
  }
  const metricLabel = state.breakdownMetric === 'cost' ? 'cost' : 'tokens';
  if (state.breakdownGrouping === 'model') {
    const shareTotal = state.breakdownMetric === 'cost' ? summary.cost : summary.processed;
    lines.push(`By model (${metricLabel})`, 'Model | Provider | Cost | Share | Tokens | $/MTok | Pricing');
    data.forEach(row => {
      const modelLabel = row.modelId || `${row.providerName} aggregate`;
      const shareValue = state.breakdownMetric === 'cost' ? row.costUsd : row.processed;
      lines.push(`${modelLabel} | ${row.providerName} | ${row.costBasis === 'Unpriced' ? 'Unavailable' : formatCost(row.costUsd)} | ${shareTotal ? `${(shareValue / shareTotal * 100).toFixed(1)}%` : '—'} | ${shareTokenCount(row.processed)} | ${row.costBasis === 'Unpriced' || !row.processed ? '—' : formatCost(row.costUsd / row.processed * 1e6)} | ${breakdownBasisLabel(row.costBasis)}`);
    });
  } else {
    const series = breakdownSeries(rows);
    lines.push(`By day (${metricLabel})`, ['Day', ...series.map(item => item.name), 'Total', 'Tokens', 'Pricing'].join(' | '));
    data.forEach(row => {
      const cells = [row.date,
        ...series.map(item => state.breakdownMetric === 'cost' ? formatCost(row.providers[item.id] || 0) : shareTokenCount(row.providers[item.id] || 0)),
        state.breakdownMetric === 'cost' ? formatCost(row.costUsd) : shareTokenCount(row.processed),
        shareTokenCount(row.processed), breakdownDayQuality(row)];
      lines.push(cells.join(' | '));
    });
  }
  return lines.join('\n');
}
function breakdownSeries(rows) {
  const days = Array.from({ length: breakdownDays() }, (_, index) => dayKey(breakdownDays() - 1 - index));
  const providerIds = [...new Set(rows.map(row => row.providerId))];
  return providerIds.map(id => ({ id, name: rows.find(row => row.providerId === id)?.providerName || id, color: spendProviderColor(id), points: days.map(date => {
    const matches = rows.filter(row => row.providerId === id && row.date === date);
    return { date, value: matches.reduce((sum, row) => sum + (state.breakdownMetric === 'cost' ? Number(row.costUsd || 0) : row.processed || 0), 0), present: matches.length > 0 };
  })}));
}
let breakdownChartGeometry = null;
function traceSmoothLine(ctx, coordinates) {
  if (!coordinates.length) return;
  ctx.moveTo(coordinates[0].x, coordinates[0].y);
  // Stop one point earlier: the tail quadratic ends ON the last point, so the loop must not
  // already have advanced past the midpoint of the last pair. Drawing that final segment twice
  // made the last section hook back toward the peak at the right edge of the chart.
  for (let index = 1; index < coordinates.length - 2; index++) {
    const current = coordinates[index];
    const next = coordinates[index + 1];
    ctx.quadraticCurveTo(current.x, current.y, (current.x + next.x) / 2, (current.y + next.y) / 2);
  }
  if (coordinates.length > 1) {
    const penultimate = coordinates[coordinates.length - 2];
    const last = coordinates[coordinates.length - 1];
    ctx.quadraticCurveTo(penultimate.x, penultimate.y, last.x, last.y);
  }
}
function drawBreakdownChart(series, hoverIndex = null) {
  const canvas = $('#breakdown-chart'); if (!canvas) return;
  const rect = canvas.getBoundingClientRect(); const dpr = window.devicePixelRatio || 1;
  canvas.width = Math.max(1, Math.floor(rect.width * dpr)); canvas.height = Math.max(1, Math.floor(rect.height * dpr));
  const ctx = canvas.getContext('2d'); ctx.scale(dpr, dpr); const width = rect.width, height = rect.height;
  const visible = series.filter(item => !state.hiddenChartProviders.has(item.id));
  const max = Math.max(1, ...visible.flatMap(item => item.points.map(point => point.value))) * 1.12;
  const plot = { left: 48, right: width - 10, top: 14, bottom: height - 25 };
  ctx.clearRect(0, 0, width, height); ctx.font = '10px Segoe UI'; ctx.fillStyle = '#9ea0a8';
  for (let step = 0; step < 4; step++) { const y = plot.top + step * (plot.bottom - plot.top) / 3; ctx.strokeStyle = 'rgba(255,255,255,.095)'; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(plot.left,y); ctx.lineTo(plot.right,y); ctx.stroke(); const value = max * (1 - step / 3); ctx.fillText(state.breakdownMetric === 'cost' ? `$${value.toFixed(value < 10 ? 2 : 0)}` : compactNumber(value), 1, y + 3); }
  const rendered = visible.map(item => ({ ...item, coordinates: item.points.map((point,index) => ({
    x: plot.left + index * (plot.right - plot.left) / Math.max(1,item.points.length - 1),
    y: plot.top + (1 - point.value / max) * (plot.bottom - plot.top),
    value: point.value,
  })) }));
  rendered.forEach(item => {
    ctx.save();
    ctx.globalAlpha = .13; ctx.fillStyle = item.color; ctx.beginPath(); traceSmoothLine(ctx, item.coordinates); ctx.lineTo(item.coordinates.at(-1).x, plot.bottom); ctx.lineTo(item.coordinates[0].x, plot.bottom); ctx.closePath(); ctx.fill();
    ctx.restore();
    ctx.strokeStyle = item.color; ctx.lineWidth = 2; ctx.lineJoin = 'round'; ctx.lineCap = 'round'; ctx.beginPath(); traceSmoothLine(ctx, item.coordinates); ctx.stroke();
  });
  if (Number.isInteger(hoverIndex) && hoverIndex >= 0 && hoverIndex < breakdownDays()) {
    const x = plot.left + hoverIndex * (plot.right - plot.left) / Math.max(1,breakdownDays() - 1);
    ctx.strokeStyle = 'rgba(241,241,242,.48)'; ctx.lineWidth = 1; ctx.beginPath(); ctx.moveTo(x, plot.top); ctx.lineTo(x, plot.bottom); ctx.stroke();
    rendered.forEach(item => { const point = item.coordinates[hoverIndex]; ctx.fillStyle = item.color; ctx.beginPath(); ctx.arc(point.x, point.y, 3.5, 0, Math.PI * 2); ctx.fill(); ctx.strokeStyle = '#17181b'; ctx.lineWidth = 1.5; ctx.stroke(); });
  }
  const labels = [0, Math.floor((breakdownDays()-1)/2), breakdownDays()-1]; ctx.fillStyle = '#9ea0a8'; labels.forEach(index => { const date = series[0]?.points[index]?.date; if (!date) return; const x = plot.left + index * (plot.right - plot.left) / Math.max(1,breakdownDays()-1); ctx.textAlign = index === 0 ? 'left' : index === breakdownDays() - 1 ? 'right' : 'center'; ctx.fillText(date.slice(5), x, height - 7); }); ctx.textAlign = 'left';
  breakdownChartGeometry = { series: rendered, plot, width, height };
}
function updateBreakdownChartTooltip(index, clientX = null) {
  const tooltip = $('#breakdown-chart-tooltip'); const geometry = breakdownChartGeometry;
  if (!tooltip || !geometry || !Number.isInteger(index)) { if (tooltip) tooltip.hidden = true; return; }
  const visible = geometry.series; const date = visible[0]?.points[index]?.date; if (!date) { tooltip.hidden = true; return; }
  const values = visible.map(item => ({ name:item.name, color:item.color, value:item.points[index].value }));
  const total = values.reduce((sum,item) => sum + item.value, 0);
  const display = value => state.breakdownMetric === 'cost' ? formatCost(value) : compactNumber(value);
  tooltip.innerHTML = `<strong>${esc(date)}</strong>${values.map(item => `<span><i style="background:${item.color}"></i>${esc(item.name)}<b>${esc(display(item.value))}</b></span>`).join('')}<span class="chart-tooltip-total">Total<b>${esc(display(total))}</b></span>`;
  const x = geometry.plot.left + index * (geometry.plot.right - geometry.plot.left) / Math.max(1,breakdownDays()-1);
  tooltip.style.left = `${Math.max(8, Math.min(geometry.width - 172, clientX ?? x + 10))}px`; tooltip.hidden = false;
}
function wireBreakdownChart(series) {
  const canvas = $('#breakdown-chart'); if (!canvas) return;
  const select = (event, keyboardDelta = null) => {
    if (keyboardDelta !== null) state.breakdownHoverIndex = Math.max(0, Math.min(breakdownDays()-1, (state.breakdownHoverIndex ?? 0) + keyboardDelta));
    else { const rect = canvas.getBoundingClientRect(); state.breakdownHoverIndex = Math.round((event.clientX - rect.left - breakdownChartGeometry.plot.left) / Math.max(1, breakdownChartGeometry.plot.right - breakdownChartGeometry.plot.left) * (breakdownDays()-1)); state.breakdownHoverIndex = Math.max(0, Math.min(breakdownDays()-1, state.breakdownHoverIndex)); }
    drawBreakdownChart(series, state.breakdownHoverIndex); updateBreakdownChartTooltip(state.breakdownHoverIndex, keyboardDelta === null ? event.clientX - canvas.getBoundingClientRect().left + 12 : null);
  };
  canvas.addEventListener('pointermove', event => select(event));
  canvas.addEventListener('pointerleave', () => { state.breakdownHoverIndex = null; drawBreakdownChart(series); updateBreakdownChartTooltip(null); });
  canvas.addEventListener('keydown', event => { if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') { event.preventDefault(); select(event, event.key === 'ArrowLeft' ? -1 : 1); } });
  canvas.addEventListener('focus', event => select(event, 0));
  canvas.addEventListener('blur', () => { state.breakdownHoverIndex = null; drawBreakdownChart(series); updateBreakdownChartTooltip(null); });
}
function formatCost(value) {
  if (!Number.isFinite(value)) return 'Unavailable';
  return `$${Number(value).toLocaleString('en-US', { minimumFractionDigits: value >= 100 ? 0 : 2, maximumFractionDigits: value >= 100 ? 0 : 2 })}`;
}
function renderBreakdown() {
  const root = $('#breakdown'); if (!root) return; const rows = breakdownRows(); const summary = breakdownSummary(rows); const series = breakdownSeries(rows);
  const input = summary.cached + summary.uncached + summary.creation; const cacheRate = input > 0 ? summary.cached / input : 0;
  const { data, modelRows, dayRows } = breakdownGroupedRows(rows);
  const head = state.breakdownGrouping === 'model'
    ? '<tr><th><button data-breakdown-sort="model">Model</button></th><th>Provider</th><th class="num"><button data-breakdown-sort="costUsd">Cost</button></th><th class="num">Share</th><th class="num"><button data-breakdown-sort="processed">Tokens</button></th><th class="num">$/MTok</th><th>Pricing</th></tr>'
    : `<tr><th><button data-breakdown-sort="date">Day</button></th>${series.map(item => `<th class="num">${esc(item.name)}</th>`).join('')}<th class="num">Total</th><th class="num">Tokens</th><th>Pricing</th></tr>`;
  const basisLabel = breakdownBasisLabel;
  const shareTotal = state.breakdownMetric === 'cost' ? summary.cost : summary.processed;
  const body = state.breakdownGrouping === 'model' ? data.map(row => { const modelLabel = row.modelId || `${row.providerName} aggregate`; const shareValue = state.breakdownMetric === 'cost' ? row.costUsd : row.processed; return `<tr><td title="${esc(modelLabel)}">${esc(modelLabel)}</td><td>${esc(row.providerName)}</td><td class="num">${row.costBasis === 'Unpriced' ? 'Unavailable' : formatCost(row.costUsd)}</td><td class="num">${shareTotal ? `${(shareValue / shareTotal * 100).toFixed(1)}%` : '—'}</td><td class="num">${compactNumber(row.processed)}</td><td class="num">${row.costBasis === 'Unpriced' ? 'Unavailable' : row.processed ? formatCost(row.costUsd / row.processed * 1e6) : '—'}</td><td class="breakdown-basis">${esc(basisLabel(row.costBasis))}</td></tr>`; }).join('') : data.map(row => { const quality = breakdownDayQuality(row); return `<tr><td>${esc(row.date)}</td>${series.map(item => `<td class="num">${state.breakdownMetric === 'cost' ? formatCost(row.providers[item.id] || 0) : compactNumber(row.providers[item.id] || 0)}</td>`).join('')}<td class="num">${state.breakdownMetric === 'cost' ? formatCost(row.costUsd) : compactNumber(row.processed)}</td><td class="num">${compactNumber(row.processed)}</td><td class="breakdown-basis">${esc(quality)}</td></tr>`; }).join('');
  root.innerHTML = `<div class="breakdown-header"><div><h2 id="breakdown-title">Usage</h2><p class="breakdown-eyebrow">${esc(breakdownStart())} to ${esc(dayKey(0))} · local history only</p></div></div><div class="breakdown-controls"><div class="breakdown-toggle" role="tablist" aria-label="Breakdown period">${['7','30','90'].map(day => `<button class="breakdown-segment ${state.breakdownPeriod===day?'selected':''}" data-breakdown-period="${day}" role="tab" aria-selected="${state.breakdownPeriod===day}">${day} days</button>`).join('')}</div><div class="breakdown-toggle" role="tablist" aria-label="Chart metric"><button class="breakdown-segment ${state.breakdownMetric==='cost'?'selected':''}" data-breakdown-chart="cost" role="tab" aria-selected="${state.breakdownMetric==='cost'}">Cost</button><button class="breakdown-segment ${state.breakdownMetric==='tokens'?'selected':''}" data-breakdown-chart="tokens" role="tab" aria-selected="${state.breakdownMetric==='tokens'}">Tokens</button></div></div>${rows.length ? `<div class="breakdown-summary"><div class="breakdown-stat"><small>Raw cost</small><strong>${formatCost(summary.cost)}</strong><span>Reported + estimated</span></div><div class="breakdown-stat"><small>Processed tokens</small><strong>${compactNumber(summary.processed)}</strong><span>Observed local history</span></div><div class="breakdown-stat"><small>Cached input</small><strong>${compactNumber(summary.cached)}</strong><span>${(cacheRate*100).toFixed(1)}% of observed input</span></div><div class="breakdown-stat"><small>Output</small><strong>${compactNumber(summary.output)}</strong><span>${compactNumber(summary.reasoning)} reasoning</span></div><div class="breakdown-stat"><small>Cache savings</small><strong>${summary.cacheSavings ? formatCost(summary.cacheSavings) : 'Unavailable'}</strong><span>Estimated with catalog rates</span></div></div><div class="breakdown-chart-wrap"><div class="breakdown-chart-top"><h3>Daily ${state.breakdownMetric === 'cost' ? 'cost' : 'processed tokens'}</h3></div><div class="breakdown-chart-shell"><canvas id="breakdown-chart" tabindex="0" aria-label="Daily provider usage chart. Use left and right arrow keys to inspect dates."></canvas><div id="breakdown-chart-tooltip" class="breakdown-chart-tooltip" role="tooltip" hidden></div></div><div class="breakdown-legend">${series.map(item => `<button data-breakdown-provider="${esc(item.id)}" class="${state.hiddenChartProviders.has(item.id)?'off':''}" aria-pressed="${!state.hiddenChartProviders.has(item.id)}"><span class="breakdown-dot" style="background:${item.color}"></span>${esc(item.name)}</button>`).join('')}</div></div><div class="breakdown-lower"><div class="ledger-panel"><div class="ledger-heading"><h3>Breakdown</h3><div class="breakdown-toggle"><button class="breakdown-segment ${state.breakdownGrouping==='model'?'selected':''}" data-breakdown-group="model">Model</button><button class="breakdown-segment ${state.breakdownGrouping==='day'?'selected':''}" data-breakdown-group="day">Day</button></div></div><div class="breakdown-table-scroll"><table class="breakdown-table"><thead>${head}</thead><tbody>${body}</tbody></table></div></div><aside class="pricing-quality"><h3>Cost quality</h3><div class="quality-row"><span>Provider reported</span><strong>${summary.cost ? `${(summary.reportedCost/summary.cost*100).toFixed(1)}%` : '—'}</strong></div><div class="quality-row"><span>Model priced</span><strong>${summary.cost ? `${(summary.modelPricedCost/summary.cost*100).toFixed(1)}%` : '—'}</strong></div><div class="quality-row"><span>Unpriced</span><strong>${summary.processed ? `${(summary.unpriced/summary.processed*100).toFixed(1)}%` : '—'}</strong></div><div class="quality-row"><span>Cache savings</span><strong>${summary.cacheSavings ? formatCost(summary.cacheSavings) : 'Unavailable'}</strong></div><div class="quality-row"><span>Data source</span><strong>Local</strong></div></aside></div>` : '<div class="breakdown-empty">No local usage history is available for this range. TokenBurn will show model detail when supported provider logs are present.</div>'}`;
  drawBreakdownChart(series);
  wireBreakdownChart(series);
}
async function setBreakdownView(expanded, immediate = false) {
  const minimumWidth = 720;
  const animate = !immediate && !prefersReducedMotion();
  const resizeNativeWindow = nextExpanded => Promise.resolve(invoke('set_breakdown_mode', {
    expanded: nextExpanded, reducedMotion: prefersReducedMotion() || immediate
  })).catch(() => null);
  const popover = $('.popover');
  const directionClass = expanded ? 'geometry-expanding' : 'geometry-collapsing';
  const clearTransition = () => {
    popover?.classList.remove('view-transitioning', 'geometry-expanding', 'geometry-collapsing');
  };
  const applyView = nextExpanded => {
    if (popover) popover.scrollLeft = 0;
    state.view = nextExpanded ? 'breakdown' : 'compact';
    const root = $('#breakdown');
    popover?.classList.toggle('breakdown-mode', nextExpanded);
    if (root) root.hidden = !nextExpanded;
    $('#providers').hidden = nextExpanded;
    $('#breakdown-back').hidden = !nextExpanded;
    if (nextExpanded) {
      $('#content').scrollTop = 0;
      $('#content').scrollLeft = 0;
      renderBreakdown();
    } else {
      state.metric = state.compactMetric || 'cost';
      updateMetricMenu();
    }
    render();
  };

  if (!expanded) {
    if (animate) {
      popover?.classList.add(directionClass, 'view-transitioning');
      await new Promise(resolve => setTimeout(resolve, 72));
    } else {
      clearTransition();
    }
    // Compact content is restored before the native shrink starts. Even if the WebView is shown
    // during a shell race, dense breakdown content can never paint into the 320 DIP viewport.
    applyView(false);
    await resizeNativeWindow(false);
    if (animate) requestAnimationFrame(() => requestAnimationFrame(() => {
      clearTransition();
      $('#metric-menu')?.focus();
    })); else {
      clearTransition();
      if (!immediate) $('#metric-menu')?.focus();
    }
    return true;
  }

  if (animate) {
    popover?.classList.add(directionClass, 'view-transitioning');
    await new Promise(resolve => setTimeout(resolve, 72));
  } else {
    clearTransition();
  }
  const targetWidth = await resizeNativeWindow(true);
  const availableWidth = Number.isFinite(Number(targetWidth)) ? Number(targetWidth) : window.innerWidth;
  if (availableWidth < minimumWidth) {
    await resizeNativeWindow(false);
    clearTransition();
    showStatus('Full breakdown needs at least 720 pixels of usable width.', STATUS_LONG);
    return false;
  }
  applyView(true);
  if (animate) requestAnimationFrame(() => requestAnimationFrame(() => {
    clearTransition();
    $('#breakdown')?.focus({ preventScroll: true });
    if (popover) popover.scrollLeft = 0;
  })); else {
    clearTransition();
    if (!immediate) $('#breakdown')?.focus({ preventScroll: true });
  }
  return true;
}

function ringRenderKey(values, centerValue, centerUnit, size) {
  return JSON.stringify({
    size,
    metric: state.metric,
    period: state.period,
    centerValue,
    centerUnit,
    values: values.map(item => [item.color, Number(item.value || 0)]),
  });
}

const TAU = Math.PI * 2;
const RING_TWEEN_MS = 320;
const RING_HOVER_MS = 120;
const easeOutCubic = (t) => 1 - Math.pow(1 - t, 3);

function minorArc(path, center, radius, from, to) {
  const start = Math.atan2(from.y - center.y, from.x - center.x);
  const end = Math.atan2(to.y - center.y, to.x - center.x);
  const clockwiseSweep = ((end - start) % TAU + TAU) % TAU;
  path.arc(center.x, center.y, radius, start, end, clockwiseSweep > Math.PI);
}

function makeFillet(center, angle, capOffset, inward, circleRadius, outer, corner) {
  const nx = Math.cos(angle);
  const ny = Math.sin(angle);
  const tx = -ny;
  const ty = nx;
  const filletRadius = outer ? circleRadius - corner : circleRadius + corner;
  const tangentOffset = capOffset + inward * corner;
  const radialOffset = Math.sqrt(Math.max(0, filletRadius * filletRadius - tangentOffset * tangentOffset));
  const filletCenter = {
    x: center.x + nx * radialOffset + tx * tangentOffset,
    y: center.y + ny * radialOffset + ty * tangentOffset,
  };
  const flatFacePoint = {
    x: center.x + nx * radialOffset + tx * capOffset,
    y: center.y + ny * radialOffset + ty * capOffset,
  };
  const circleScale = circleRadius / filletRadius;
  const ringPoint = {
    x: center.x + (filletCenter.x - center.x) * circleScale,
    y: center.y + (filletCenter.y - center.y) * circleScale,
  };
  return {
    filletCenter,
    flatFacePoint,
    ringPoint,
    angle: angle + Math.atan2(tangentOffset, radialOffset),
  };
}

function donutSegmentPath({ cx, cy, startBoundary, endBoundary, radius, width, gap = 3.5, corner = 2 }) {
  const center = { x: cx, y: cy };
  const outerRadius = radius + width / 2;
  const innerRadius = radius - width / 2;
  const startOuter = makeFillet(center, startBoundary, gap / 2, 1, outerRadius, true, corner);
  const startInner = makeFillet(center, startBoundary, gap / 2, 1, innerRadius, false, corner);
  const endOuter = makeFillet(center, endBoundary, -gap / 2, -1, outerRadius, true, corner);
  const endInner = makeFillet(center, endBoundary, -gap / 2, -1, innerRadius, false, corner);
  if (endOuter.angle <= startOuter.angle || endInner.angle <= startInner.angle) return null;

  const path = new Path2D();
  path.moveTo(startOuter.ringPoint.x, startOuter.ringPoint.y);
  path.arc(cx, cy, outerRadius, startOuter.angle, endOuter.angle, false);
  minorArc(path, endOuter.filletCenter, corner, endOuter.ringPoint, endOuter.flatFacePoint);
  path.lineTo(endInner.flatFacePoint.x, endInner.flatFacePoint.y);
  minorArc(path, endInner.filletCenter, corner, endInner.flatFacePoint, endInner.ringPoint);
  path.arc(cx, cy, innerRadius, endInner.angle, startInner.angle, true);
  minorArc(path, startInner.filletCenter, corner, startInner.ringPoint, startInner.flatFacePoint);
  path.lineTo(startOuter.flatFacePoint.x, startOuter.flatFacePoint.y);
  minorArc(path, startOuter.filletCenter, corner, startOuter.flatFacePoint, startOuter.ringPoint);
  path.closePath();
  return path;
}

function drawExactAnnularSector(ctx, { cx, cy, startBoundary, endBoundary, radius, width }) {
  const outerRadius = radius + width / 2;
  const innerRadius = Math.max(0, radius - width / 2);
  ctx.beginPath();
  ctx.arc(cx, cy, outerRadius, startBoundary, endBoundary, false);
  ctx.arc(cx, cy, innerRadius, endBoundary, startBoundary, true);
  ctx.closePath();
  ctx.fill();
}

function fillSpendSegment(ctx, params) {
  const path = donutSegmentPath(params);
  if (path) {
    ctx.fill(path);
    return;
  }
  // A real slice can be smaller than the rounded end caps plus the normal gap. Do not silently
  // drop it. A smaller gap keeps the polished shape for most near-threshold slices, and the exact
  // sector is the final fallback for slices that are genuinely too short for any fillet.
  const compactPath = donutSegmentPath({
    ...params,
    gap: Math.min(0.8, params.gap ?? 0),
    corner: Math.min(0.75, params.corner ?? 0),
  });
  if (compactPath) {
    ctx.fill(compactPath);
    return;
  }
  drawExactAnnularSector(ctx, params);
}

function drawCenteredText(ctx, text, x, centerY) {
  const metrics = ctx.measureText(text);
  const ascent = metrics.actualBoundingBoxAscent || 0;
  const descent = metrics.actualBoundingBoxDescent || 0;
  ctx.fillText(text, x, centerY + (ascent - descent) / 2);
}

function drawRing(values, centerValue, centerUnit) {
  const canvas = $('#spend-ring');
  const ctx = canvas.getContext('2d');
  const scale = window.devicePixelRatio || 1;
  const size = canvas.clientWidth || 200;
  const renderKey = ringRenderKey(values, centerValue, centerUnit, size);
  const hoverChanged = state.hoveredSpendProviderId !== lastRingHoverId;
  const renderChanged = renderKey !== lastRingRenderKey;
  const previousValues = lastRingValues;
  if (!renderChanged && !hoverChanged) return;
  lastRingRenderKey = renderKey;
  lastRingHoverId = state.hoveredSpendProviderId;
  lastRingValues = values;
  lastRingSize = size;
  if (renderChanged) {
    canvas.width = size * scale;
    canvas.height = size * scale;
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
  }
  const center = size / 2;
  const radius = size * .335;
  const stroke = size * .11;
  const previousById = new Map(previousValues.map(item => [item.id, Number(item.value || 0)]));
  const animateFromCurrent = renderChanged && previousValues.length > 0;
  cancelAnimationFrame(ringAnimationFrame);
  const started = performance.now();
  if (hoverChanged) ringHoverStart = started;
  const reduced = prefersReducedMotion();
  const paint = (now) => {
    const linear = renderChanged && !reduced ? Math.min(1, (now - started) / RING_TWEEN_MS) : 1;
    // Ease-out rather than linear: a value settling into place should decelerate, not arrive at
    // constant speed. 320ms instead of 450ms — long enough to read, short enough not to wait on.
    const progress = easeOutCubic(linear);
    // Hover is its own short tween. Previously the focused slice jumped its stroke width and the
    // others dropped alpha instantly, which undercut an otherwise carefully drawn component.
    const hoverT = reduced ? 1 : easeOutCubic(Math.min(1, (now - ringHoverStart) / RING_HOVER_MS));
    const displayedValues = values.map(item => {
      const target = Number(item.value || 0);
      if (!animateFromCurrent) return { ...item, value: target * progress };
      const start = previousById.get(item.id) ?? 0;
      return { ...item, value: start + (target - start) * progress };
    });
    paintRingFrame(ctx, size, displayedValues, centerValue, centerUnit, {
      hoveredId: state.hoveredSpendProviderId,
      hoverT,
      microMarkerProgress: progress,
    });
    if (linear < 1 || hoverT < 1) ringAnimationFrame = requestAnimationFrame(paint);
  };
  if (renderChanged && !reduced) ringAnimationFrame = requestAnimationFrame(paint);
  else paint(performance.now());
}

// The same ring geometry as drawRing, painted once at final values. The live popup ring and the
// generated share image both draw through this so the copied chart always matches the dashboard.
function paintRingFrame(ctx, size, values, centerValue, centerUnit, options = {}) {
  const hoveredId = options.hoveredId || null;
  const hoverT = options.hoverT ?? 1;
  const microMarkerProgress = options.microMarkerProgress ?? 1;
  const center = size / 2;
  const radius = size * .335;
  const stroke = size * .11;
  const displayedTotal = values.reduce((sum, item) => sum + Number(item.value || 0), 0);
  ctx.clearRect(0, 0, size, size);
  // Unfilled track. Without it a zero-spend period renders as a bare floating "$0.00" with no
  // indication the chart drew at all, and growing arcs have nothing to grow into.
  ctx.beginPath();
  ctx.arc(center, center, radius, 0, TAU);
  ctx.strokeStyle = '#34383b';
  ctx.lineWidth = stroke;
  ctx.stroke();
  let cursor = -Math.PI / 2;
  const microMarkers = [];
  values.forEach(item => {
    if (!item.value || displayedTotal <= 0) return;
    const angle = item.value / displayedTotal * Math.PI * 2;
    const sweep = angle;
    if (sweep > 0) {
      const focused = hoveredId === item.id;
      const dimmed = hoveredId && !focused;
      ctx.globalAlpha = dimmed ? 1 - .58 * hoverT : 1;
      const width = focused ? stroke + 4 * hoverT : stroke;
      ctx.strokeStyle = item.color;
      ctx.fillStyle = item.color;
      fillSpendSegment(ctx, {
        cx: center,
        cy: center,
        startBoundary: cursor,
        endBoundary: cursor + sweep,
        radius,
        width,
        gap: Math.min(3.5, size * .022),
        corner: Math.min(2.75, size * .016),
      });
      // A provider can legitimately account for only a few pixels of the total. Keep the
      // proportional arc as the source of truth, then add a tiny rounded locator at its actual
      // angular position so the provider is still discoverable in the chart and legend.
      // At small sizes the normal boundary gap can consume the entire slice. Keep the
      // proportional cursor movement, but redraw these tiny slices with the reduced-gap
      // marker below so they do not appear as a misleading empty hole.
      // Fade in over the last third of the tween. Switching these on at progress >= 1 made tiny
      // providers pop into existence at the exact moment the motion stopped.
      if (angle < 0.11 && microMarkerProgress > .66) {
        microMarkers.push({
          item,
          center: cursor + angle / 2,
          sweep: Math.max(0.018, angle),
          alpha: Math.min(1, (microMarkerProgress - .66) / .34),
        });
      }
      ctx.globalAlpha = 1;
    }
    cursor += angle;
  });
  microMarkers.forEach(({ item, center: markerCenter, sweep: markerSweep, alpha }) => {
    const focused = hoveredId === item.id;
    const dimmed = hoveredId && !focused;
    ctx.globalAlpha = (dimmed ? 1 - .58 * hoverT : 1) * alpha;
    // Keep the tiny slice as an annular sliver. A centerline stroke is wrong here because
    // its round caps overlap when the arc is shorter than the ring width and become a dot.
    const markerPath = donutSegmentPath({
      cx: center,
      cy: center,
      startBoundary: markerCenter - markerSweep / 2,
      endBoundary: markerCenter + markerSweep / 2,
      radius,
      width: focused ? stroke + 4 * hoverT : stroke,
      gap: 0,
      corner: Math.min(1.25, size * .008),
    });
    if (markerPath) {
      ctx.fillStyle = item.color;
      ctx.fill(markerPath);
    }
    ctx.globalAlpha = 1;
  });
  ctx.textAlign = 'center'; ctx.textBaseline = 'alphabetic';
  // The center label must fit inside the ring's hole, not just the canvas. A fixed 21px font
  // overflowed into the ring for longer values (e.g. "$204.14"). Shrink to fit the actual hole
  // width instead, same approach as the WPF spend ring.
  const maxTextWidth = (radius - stroke / 2) * 2 * 0.82;
  let primarySize = Math.min(19, size * 0.118);
  ctx.font = `700 ${primarySize}px -apple-system,Segoe UI,sans-serif`;
  while (ctx.measureText(centerValue).width > maxTextWidth && primarySize > 11) {
    primarySize -= 1;
    ctx.font = `700 ${primarySize}px -apple-system,Segoe UI,sans-serif`;
  }
  if (centerUnit) {
    let secondarySize = Math.min(12, size * 0.073);
    ctx.font = `600 ${secondarySize}px -apple-system,Segoe UI,sans-serif`;
    while (ctx.measureText(centerUnit).width > maxTextWidth && secondarySize > 8) {
      secondarySize -= 1;
      ctx.font = `600 ${secondarySize}px -apple-system,Segoe UI,sans-serif`;
    }
    ctx.font = `700 ${primarySize}px -apple-system,Segoe UI,sans-serif`;
    const primaryMetrics = ctx.measureText(centerValue);
    const primaryHeight = (primaryMetrics.actualBoundingBoxAscent || primarySize) + (primaryMetrics.actualBoundingBoxDescent || primarySize * .25);
    ctx.font = `600 ${secondarySize}px -apple-system,Segoe UI,sans-serif`;
    const secondaryMetrics = ctx.measureText(centerUnit);
    const secondaryHeight = (secondaryMetrics.actualBoundingBoxAscent || secondarySize) + (secondaryMetrics.actualBoundingBoxDescent || secondarySize * .25);
    const gap = Math.max(3, secondarySize * .25);
    const top = center - (primaryHeight + gap + secondaryHeight) / 2;
    ctx.fillStyle = '#f1f1f2'; ctx.font = `700 ${primarySize}px -apple-system,Segoe UI,sans-serif`; ctx.fillText(centerValue, center, top + (primaryMetrics.actualBoundingBoxAscent || primarySize));
    ctx.fillStyle = '#99999e'; ctx.font = `600 ${secondarySize}px -apple-system,Segoe UI,sans-serif`; ctx.fillText(centerUnit, center, top + primaryHeight + gap + (secondaryMetrics.actualBoundingBoxAscent || secondarySize));
  } else {
    ctx.fillStyle = '#f1f1f2'; ctx.font = `700 ${primarySize}px -apple-system,Segoe UI,sans-serif`; drawCenteredText(ctx, centerValue, center, center);
  }
}

// The provider list used to be rebuilt from a template string on every render(), and render() runs
// once a second off the refresh-status poll. That destroyed and recreated every node in the list
// every second, which silently voided the meter and legend transitions (a freshly inserted element
// has no previous computed value to animate from), wiped keyboard focus and :disabled, and left the
// trend tooltip pointing at detached nodes.
//
// Same fix the spend ring already uses: key the expensive work and skip it when nothing structural
// changed. Numeric values are deliberately excluded from the key and written in place instead, so
// the CSS transitions on .fill finally have a start value to animate from.
let lastProvidersKey = '';

function providerRows() {
  const catalog = state.providerCatalog;
  const snapshotsById = new Map(state.snapshots.map(snapshot => [snapshot.providerId, snapshot]));
  return state.enabledProviders.map(providerId => {
    const descriptor = catalog.find(provider => provider.id === providerId) || { id: providerId, displayName: providerId };
    const snapshot = snapshotsById.get(providerId) || {
      providerId,
      displayName: descriptor.displayName,
      lines: [{ type: 'empty', text: 'Provider is not configured.' }],
    };
    const warning = snapshot.warning || snapshot.error;
    const compactLines = visibleLines(snapshot).map(line => displayLine(snapshot, line));
    const errorText = [warning, ...compactLines.map(line => line.text || line.value || '')].filter(Boolean).join(' ');
    const reauthAction = reauthActionFor(snapshot, errorText);
    return {
      snapshot,
      warning,
      compactLines,
      canReauth: !!reauthAction,
      reauthAction,
      displayName: snapshot.providerId === 'claude-code' ? 'Claude Code' : snapshot.displayName,
    };
  });
}

// A provider in an authentication-failure state gets a re-sign-in action that opens its own CLI.
// Claude uses `claude auth login`; Antigravity uses the `agy` CLI the user is already signed in to.
function reauthActionFor(snapshot, errorText) {
  const authError = /(auth|login|expired|not configured|signed out|sign.?in)/i.test(errorText);
  if (!authError) return null;
  if (snapshot.providerId === 'claude-code') return { action: 'claude-login', label: 'Open Claude sign-in' };
  if (snapshot.providerId === 'antigravity') return { action: 'antigravity-login', label: 'Run agy to sign in' };
  return null;
}

// Everything that affects the markup EXCEPT the four things patchProviderValues writes: meter width,
// meter threshold class, the percent text, and the reset text. Trend bars and history totals are in
// the key rather than patched — they only move when real usage lands, not once a second.
function providersStructureKey(rows) {
  return JSON.stringify(rows.map(row => [
    row.snapshot.providerId,
    row.displayName,
    row.snapshot.plan || '',
    row.warning || '',
    row.canReauth,
    row.compactLines.map(line => [
      line.type || 'progress',
      line.label || '',
      line.text || '',
      line.value || '',
      (line.values || []).map(value => value.valueLabel || value.number),
      (line.points || []).slice(-30).map(point => point && point.value),
    ]),
    (row.snapshot.usageHistory?.points || []).map(point => [point.date, point.costUsd, point.tokens]),
  ]));
}

// Shared by the renderer and the patcher so a patched card can never disagree with a rebuilt one.
function progressValues(line, stale) {
  const used = Number(line.used || 0);
  const limit = Number(line.limit || 0);
  const fraction = Math.max(0, Math.min(1, used / limit));
  const expired = !stale && line.resetsAt && new Date(line.resetsAt).getTime() <= Date.now();
  const visibleFraction = expired ? 0 : fraction;
  const remaining = state.settings.usageDisplay === 'Remaining';
  // Always show the percent-of-quota, not the raw used/limit numbers — those are dollars or
  // token counts for some providers (e.g. OpenCode's cost-capped meters) and would otherwise
  // print misleading fractional "%" values instead of a clean whole percent.
  const shown = Math.round((remaining ? 1 - visibleFraction : visibleFraction) * 100);
  return {
    fraction: visibleFraction,
    stateClass: visibleFraction >= .95 ? 'danger' : visibleFraction >= .75 ? 'warn' : '',
    valueText: `${shown}% ${remaining ? 'remaining' : 'used'}`,
    resetText: stale ? 'Last known limits' : expired ? 'Updating now' : formatReset(line.resetsAt),
  };
}

function renderProvidersFinal() {
  const rows = providerRows();
  const key = providersStructureKey(rows);
  if (key === lastProvidersKey) {
    patchProviderValues(rows);
    return;
  }
  lastProvidersKey = key;
  $('#providers').innerHTML = rows.map(row => {
    const { snapshot, warning, compactLines, canReauth, reauthAction, displayName } = row;
    const lines = compactLines.map((line, index) => renderMetric(line,
      Boolean(warning) || isPreResetSnapshot(snapshot, line), `${snapshot.providerId}::${index}`)).join('');
    const localHistory = renderLocalHistory(snapshot);
    return `<article class="provider"><div class="provider-heading"><span class="provider-mark">${providerLogo(snapshot.providerId)}</span><div><span class="provider-name">${esc(displayName)}</span><span class="provider-plan">${esc(snapshot.plan || '')}</span></div>${warning ? '<span class="provider-warning" aria-label="Provider warning">!</span>' : ''}</div><div class="provider-card">${warning ? `<div class="error-line" title="${esc(warning)}">${esc(warning)}</div>` : ''}${canReauth && reauthAction ? `<button class="provider-action" data-provider-action="${esc(reauthAction.action)}">${esc(reauthAction.label)}</button>` : ''}${lines || (!localHistory ? '<div class="empty-line">No live limits returned.</div>' : '')}${localHistory}</div></article>`;
  }).join('');
  sweepMetersIn();
}

// Meters are emitted at width:0 on a rebuild and given their real width a frame later, so the bar
// sweeps in once instead of appearing pre-filled. Value-only updates skip this entirely and just
// transition from wherever the bar already is.
function sweepMetersIn() {
  const fills = document.querySelectorAll('#providers .fill[data-target-width]');
  if (!fills.length) return;
  const apply = () => fills.forEach(fill => {
    fill.style.width = fill.dataset.targetWidth;
    delete fill.dataset.targetWidth;
  });
  if (prefersReducedMotion()) {
    apply();
    return;
  }
  // Two frames: the first guarantees the browser has committed width:0 as the start value.
  requestAnimationFrame(() => requestAnimationFrame(apply));
}

function patchProviderValues(rows) {
  const nodes = new Map(Array.from(document.querySelectorAll('#providers [data-metric-key]'))
    .map(node => [node.dataset.metricKey, node]));
  rows.forEach(row => {
    row.compactLines.forEach((line, index) => {
      if (!progressLine(line)) return;
      const node = nodes.get(`${row.snapshot.providerId}::${index}`);
      if (!node) return;
      const values = progressValues(line, Boolean(row.warning) || isPreResetSnapshot(row.snapshot, line));
      const fill = node.querySelector('.fill');
      if (fill && !fill.dataset.targetWidth) {
        const width = `${values.fraction * 100}%`;
        if (fill.style.width !== width) fill.style.width = width;
        fill.className = `fill ${values.stateClass}`.trim();
      }
      const value = node.querySelector('.metric-value');
      if (value && value.textContent !== values.valueText) value.textContent = values.valueText;
      const reset = node.querySelector('.metric-reset');
      if (reset && reset.textContent !== values.resetText) reset.textContent = values.resetText;
    });
  });
}

function isPreResetSnapshot(snapshot, line) {
  if (!progressLine(line) || !line.resetsAt || !snapshot.fetchedAt) return false;
  const resetAt = new Date(line.resetsAt).getTime();
  const fetchedAt = new Date(snapshot.fetchedAt).getTime();
  return Number.isFinite(resetAt) && Number.isFinite(fetchedAt) &&
    resetAt <= Date.now() && fetchedAt < resetAt;
}

function renderMetric(line, stale = false, metricKey = '') {
  if (progressLine(line)) {
    const values = progressValues(line, stale);
    // data-target-width rather than an inline width: sweepMetersIn applies it a frame later so the
    // bar animates in from empty. patchProviderValues writes width directly on later updates.
    return `<div class="metric" data-metric-key="${esc(metricKey)}"><div class="metric-top"><span class="metric-label">${esc(line.label)}</span></div><div class="meter"><div class="fill ${values.stateClass}" style="width:0" data-target-width="${values.fraction * 100}%"></div></div><div class="metric-bottom"><span class="metric-value">${esc(values.valueText)}</span><span class="metric-reset">${esc(values.resetText)}</span></div></div>`;
  }
  if (line.type === 'chart') {
    const chartPoints = (line.points || []).slice(-30);
    const points = [...Array(Math.max(0, 30 - chartPoints.length)).fill(null), ...chartPoints].map(point => {
      if (!point) return '<i style="height:3px"></i>';
      const detail = point.valueLabel || point.label || point.value || 'No data';
      return `<i data-tooltip="${esc(detail)}" aria-label="${esc(detail)}" style="height:${Math.max(3, Math.min(100, Number(point.value || 0) * 100))}%"></i>`;
    }).join('');
    return `<div class="metric"><div class="metric-top"><span class="metric-label">${esc(line.label)}</span></div><div class="trend">${points || '<i style="height:3px"></i>'.repeat(30)}</div></div>`;
  }
  // 'empty' is the synthetic "not configured" placeholder — a neutral fact, not a failure. Real
  // provider badges keep the error styling.
  if (line.type === 'empty') return `<div class="empty-line">${esc(line.text || line.value || '')}</div>`;
  if (line.type === 'badge') return `<div class="error-line">${esc(line.text || line.value || '')}</div>`;
  if (line.type === 'text') return `<div class="text-line"><span>${esc(line.label)}</span><span>${esc(line.value || '')}</span></div>`;
  if (line.type === 'values') return `<div class="text-line"><span>${esc(line.label)}</span><span>${esc((line.values || []).map(value => value.valueLabel || value.number).join(' · '))}</span></div>`;
  return '';
}

function dayKey(offset = 0) {
  const date = new Date();
  date.setHours(0, 0, 0, 0);
  date.setDate(date.getDate() - offset);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

function historyTotals(snapshot, predicate) {
  const points = snapshot.usageHistory?.points || [];
  return points.filter(predicate).reduce((totals, point) => ({
    cost: totals.cost + Number(point.costUsd || 0),
    tokens: totals.tokens + Number(point.tokens || 0),
  }), { cost: 0, tokens: 0 });
}

function formatHistoryValue(totals) {
  if (!totals.cost && !totals.tokens) return 'No data';
  if (totals.cost && totals.tokens) return `$${totals.cost.toFixed(2)} · ${compactNumber(totals.tokens)} tokens`;
  if (totals.cost) return `$${totals.cost.toFixed(2)}`;
  return `${compactNumber(totals.tokens)} tokens`;
}

function renderLocalHistory(snapshot) {
  const points = snapshot.usageHistory?.points || [];
  if (!points.length) return '';
  const today = dayKey(0);
  const yesterday = dayKey(1);
  const monthStart = dayKey(29);
  const todayTotals = historyTotals(snapshot, point => point.date === today);
  const yesterdayTotals = historyTotals(snapshot, point => point.date === yesterday);
  const monthTotals = historyTotals(snapshot, point => point.date >= monthStart && point.date <= today);
  const pointsByDate = new Map(points.map(point => [point.date, point]));
  const last30 = Array.from({ length: 30 }, (_, index) => {
    const date = dayKey(29 - index);
    return pointsByDate.get(date) || { date, costUsd: 0, tokens: 0 };
  });
  const peak = Math.max(1, ...last30.map(item => Number(item.tokens || item.costUsd || 0)));
  const bars = last30.map(point => {
    const value = Math.max(0, Number(point.tokens || point.costUsd || 0));
    const totals = { cost: Number(point.costUsd || 0), tokens: Number(point.tokens || 0) };
    const detail = formatHistoryValue(totals);
    return `<i data-tooltip="${esc(point.date || 'Unknown date')} · ${esc(detail)}" aria-label="${esc(point.date || 'Unknown date')}: ${esc(detail)}" style="height:${Math.max(3, Math.min(100, value / peak * 100))}%"></i>`;
  }).join('');
  return `<div class="metric history-trend"><div class="metric-top"><span class="metric-label">Usage Trend · 30 Days</span></div><div class="trend">${bars}</div></div><div class="history-lines"><div class="text-line"><span>Today</span><span>${formatHistoryValue(todayTotals)}</span></div><div class="text-line"><span>Yesterday</span><span>${formatHistoryValue(yesterdayTotals)}</span></div><div class="text-line"><span>Last 30 Days</span><span>${formatHistoryValue(monthTotals)}</span></div></div>`;
}

function render() {
  const loading = state.localLoading || state.hostLoading;
  document.body.classList.toggle('refreshing', loading);
  // The reentrancy guard in refresh() silently drops a second click. Disabling the button is what
  // makes that no-op legible instead of looking like a dead control.
  const refreshButton = $('#refresh-button');
  if (refreshButton) {
    refreshButton.disabled = loading;
    refreshButton.setAttribute('aria-label', loading ? 'Refreshing' : 'Refresh now');
  }
  $('#metric-title').textContent = state.metric === 'tokens' ? 'Tokens' : state.metric === 'cost-mtok' ? 'Cost/MTok' : 'Cost';
  renderSpend();
  renderProvidersFinal();
  $('#updated').textContent = loading
    ? 'Refreshing...'
    : state.refreshStatusError || formatRefreshCountdown(state.nextRefreshAt);
}

// A single dropped poll should not replace the countdown with an error. Two in a row means the
// host is actually gone; one means the WPF side was busy for a second.
let refreshStatusFailures = 0;
let lastInitialDataRetryAt = 0;

async function syncRefreshStatus() {
  try {
    // This runs from refresh()'s cleanup path.  A WebView2 startup race can leave an IPC
    // promise unresolved, so this must use the same bound as the usage request.  Otherwise
    // snapshots can be loaded into state and never reach the renderer.
    const status = await withTimeout(
      invoke('fetch_refresh_status'),
      COMMAND_TIMEOUT_MS,
      'The refresh-status command did not respond.'
    );
    if (!status) return;
    refreshStatusFailures = 0;
    state.refreshStatusError = '';
    state.nextRefreshAt = status.nextRefreshAt || null;
    if (typeof status.loading === 'boolean') state.hostLoading = status.loading;
    render();
  } catch (_) {
    // The popup can still render cached provider data while the WPF host is starting.
    refreshStatusFailures += 1;
    if (refreshStatusFailures >= 2) state.refreshStatusError = 'Refresh service unavailable';
    render();
  }
}

async function refresh(force = false) {
  if (state.localLoading) return;
  state.localLoading = true; render();
  try {
    if (force) {
      // The desktop host owns the cache and refresh timestamp. Asking it to refresh first keeps
      // the taskbar strip, WPF fallback, and Tauri popup on the same generation of data.
      await requestDesktopRefresh();
    }
    const [snapshots, enabledProviders] = await Promise.all([
      fetchUsageSnapshots(false),
      fetchEnabledProviders()
    ]);
    if (!Array.isArray(snapshots)) throw new Error('The local API returned no provider list.');
    state.snapshots = snapshots;
    if (Array.isArray(enabledProviders)) state.enabledProviders = enabledProviders;
    state.lastGood = Date.now();
    // Paint the data immediately. Refresh-status is supplemental metadata and must never hide
    // an otherwise valid usage payload while the native command bridge is still settling.
    render();
    const errors = snapshots.filter(snapshot => snapshot.error).length;
    if (force && errors) showStatus(`${errors} provider update${errors === 1 ? '' : 's'} need attention.`);
  } catch (error) {
    state.refreshStatusError = 'Provider update unavailable';
    // Raw Rust/JS exception text used to reach the user verbatim. Keep the detail available for
    // diagnostics via the title attribute, but lead with something a person can act on.
    showStatus('Could not reach the local provider service.', STATUS_LONG, error?.toString?.());
  } finally {
    state.localLoading = false;
    render();
    // Status polling has its own bounded error handling. Do not make the primary data paint
    // depend on it, or a hung command can leave the spend ring at $0.00 forever.
    void syncRefreshStatus();
  }
}

async function hidePopup() {
  if (TAURI?.core?.invoke) {
    try {
      await invoke('hide_popup');
      return;
    } catch (_) { /* use the browser fallback below */ }
  }
  beginPopoverClose();
  setTimeout(() => currentWindow()?.hide?.(), 155);
}

document.addEventListener('keydown', event => {
  if (event.key !== 'Escape' && event.code !== 'Escape') return;
  event.preventDefault();
  // Escape backs out one level. Closing the whole window from inside a settings page skipped a
  // level and lost the user's place.
  const openSelect = document.querySelector('.select-control.open');
  if (openSelect) {
    closeSelect(openSelect);
    openSelect.querySelector('.select-trigger')?.focus();
    return;
  }
  if ($('#notification-provider-picker')?.classList.contains('open')) {
    closeNotificationProviderMenu();
    $('#notification-provider-trigger')?.focus();
    return;
  }
  if ($('#spend-other-tooltip')?.classList.contains('is-open')) {
    closeSpendOtherTooltip();
    return;
  }
  if (document.querySelector('.overlay-surface.is-open')) {
    closeHeaderPopovers();
    return;
  }
  const activePage = document.querySelector('.page-view.active');
  if (activePage) {
    closeSettingsPage(activePage);
    return;
  }
  hidePopup();
});
document.addEventListener('mousedown', event => {
  if (!event.target.closest('.chrome')) {
    closeHeaderPopovers();
  }
  if (event.target === document.body) hidePopup();
});
let scrollTimer;
$('#content').addEventListener('scroll', () => {
  $('#content').classList.add('scrolling');
  clearTimeout(scrollTimer);
  scrollTimer = setTimeout(() => $('#content').classList.remove('scrolling'), 650);
}, { passive: true });
$('#refresh-button').addEventListener('click', () => { closeHeaderPopovers(); refresh(true); });
const shareButton = $('#share-button');
let sharePressTimer = 0;
let shareMenuOpenedByPress = false;

function setShareMenu(open) {
  setOverlayOpen($('#share-popover'), open);
  if (!open) shareMenuOpenedByPress = false;
}

async function copyCompactSummary() {
  const text = compactShareText();
  const image = buildShareImage();
  if (await copyShareToClipboard(text, image)) {
    showStatus('Copied spend summary + chart image');
    return;
  }
  const ok = await copyTextToClipboard(text);
  showStatus(ok ? 'Copied spend summary without the chart image.' : 'Clipboard access was blocked by Windows.', ok ? STATUS_SHORT : STATUS_LONG);
}

async function copyCompactChartImage() {
  const image = buildShareImage();
  if (await copyShareImageOnly(image)) {
    showStatus('Copied chart image');
    return;
  }
  showStatus('Clipboard access was blocked by Windows.', STATUS_LONG);
}

// A plain click copies the summary with the chart. A long press opens the copy menu: chat apps
// that paste text first would otherwise drop the image, so an image-only copy matches how a
// Snipping Tool screenshot pastes everywhere.
shareButton?.addEventListener('pointerdown', () => {
  if (state.view === 'breakdown') return;
  clearTimeout(sharePressTimer);
  sharePressTimer = window.setTimeout(() => {
    shareMenuOpenedByPress = true;
    setShareMenu(true);
  }, 480);
});
shareButton?.addEventListener('pointerup', () => clearTimeout(sharePressTimer));
shareButton?.addEventListener('pointerleave', () => clearTimeout(sharePressTimer));
shareButton?.addEventListener('pointercancel', () => clearTimeout(sharePressTimer));
shareButton?.addEventListener('click', async () => {
  // A long press releases as a click. Keep the menu open instead of instantly closing it.
  if (shareMenuOpenedByPress) {
    shareMenuOpenedByPress = false;
    return;
  }
  if ($('#share-popover')?.classList.contains('is-open')) {
    setShareMenu(false);
    return;
  }
  closeHeaderPopovers();
  if (state.view === 'breakdown') {
    const ok = await copyTextToClipboard(breakdownShareText());
    showStatus(ok ? 'Copied usage breakdown' : 'Clipboard access was blocked by Windows.', ok ? STATUS_SHORT : STATUS_LONG);
    return;
  }
  await copyCompactSummary();
});
document.querySelectorAll('[data-share-copy]').forEach(item => item.addEventListener('click', async () => {
  const mode = item.dataset.shareCopy;
  setShareMenu(false);
  if (mode === 'image') {
    await copyCompactChartImage();
  } else if (mode === 'text') {
    const ok = await copyTextToClipboard(compactShareText());
    showStatus(ok ? 'Copied spend summary text' : 'Clipboard access was blocked by Windows.', ok ? STATUS_SHORT : STATUS_LONG);
  } else {
    await copyCompactSummary();
  }
}));
$('#info-button').addEventListener('click', event => {
  const popover = $('#info-popover');
  const next = !popover.classList.contains('is-open');
  closeHeaderPopovers();
  if (next) {
    // Anchor to the button that opened it. The shared rule used to hard-code the metric button's
    // position, so the info popover appeared under a different control than the one clicked, and
    // the metric title's width changes with the selected metric so a fixed offset cannot track it.
    const button = event.currentTarget.getBoundingClientRect();
    const chrome = $('.chrome').getBoundingClientRect();
    popover.style.left = `${Math.max(8, button.left - chrome.left - 6)}px`;
    popover.style.transformOrigin = 'top left';
  }
  setOverlayOpen(popover, next);
});
$('#metric-menu').addEventListener('click', () => {
  const next = !$('#metric-popover').classList.contains('is-open');
  closeHeaderPopovers();
  setOverlayOpen($('#metric-popover'), next);
  $('#metric-menu').setAttribute('aria-expanded', String(next));
});
document.querySelectorAll('[data-metric]').forEach(button => button.addEventListener('click', async () => {
  if (button.dataset.metric === 'breakdown') {
    closeHeaderPopovers();
    document.activeElement?.blur();
    await setBreakdownView(true);
    return;
  }
  if (state.view === 'breakdown') await setBreakdownView(false);
  closeSpendOtherTooltip();
  state.metric = normalizeSpendMetric(button.dataset.metric);
  state.compactMetric = state.metric;
  state.settings.spendMetric = state.metric;
  updateMetricMenu();
  closeHeaderPopovers();
  render();
  try {
    await invoke('set_spend_metric', { metric: state.metric });
  } catch (_) {
    showStatus('Metric preference could not be saved.', STATUS_LONG);
  }
}));
document.addEventListener('click', event => {
  const back = event.target.closest('[data-breakdown-back]');
  if (back) { setBreakdownView(false); return; }
  const period = event.target.closest('[data-breakdown-period]');
  if (period) { state.breakdownPeriod = period.dataset.breakdownPeriod; renderBreakdown(); return; }
  const metric = event.target.closest('[data-breakdown-chart]');
  if (metric) { state.breakdownMetric = metric.dataset.breakdownChart; renderBreakdown(); return; }
  const group = event.target.closest('[data-breakdown-group]');
  if (group) { state.breakdownGrouping = group.dataset.breakdownGroup; state.breakdownSort = { column: group.dataset.breakdownGroup === 'day' ? 'date' : 'costUsd', direction: 'desc' }; renderBreakdown(); return; }
  const provider = event.target.closest('[data-breakdown-provider]');
  if (provider) { const id = provider.dataset.breakdownProvider; state.hiddenChartProviders.has(id) ? state.hiddenChartProviders.delete(id) : state.hiddenChartProviders.add(id); renderBreakdown(); return; }
  const sort = event.target.closest('[data-breakdown-sort]');
  if (sort) { const column = sort.dataset.breakdownSort; state.breakdownSort = { column, direction: state.breakdownSort.column === column && state.breakdownSort.direction === 'desc' ? 'asc' : 'desc' }; renderBreakdown(); }
});
const periodButtons = Array.from(document.querySelectorAll('[data-period]'));
periodButtons.forEach((button, index) => button.addEventListener('click', () => {
  closeSpendOtherTooltip();
  state.period = button.dataset.period;
  periodButtons.forEach(item => {
    const selected = item === button;
    item.classList.toggle('selected', selected);
    item.setAttribute('aria-selected', String(selected));
  });
  // Drives the sliding indicator in styles.css. One thumb that travels, rather than three
  // backgrounds switching on and off, so the change reads as a movement with a direction.
  $('.periods').style.setProperty('--period-index', String(index));
  render();
}));
// Settings/Customize render as pages inside this same popup window (see .page-view in
// styles.css) instead of spawning a second native window. Two windows coordinating focus,
// ownership, and position across Explorer's taskbar was the actual source of the
// "settings won't open" / "opens off-screen" / "opens behind the dashboard" bugs; a page
// inside the popup's own window has none of that.
let currentSettingsData = null;
let settingsApplyTimer = null;
let settingsApplyInFlight = false;
let settingsApplyQueuedName = null;

const metricDescriptions = {
  'session': 'Usage in the current rolling session window.',
  'weekly': 'Usage in the provider’s weekly allowance window.',
  'claude weekly': 'Third-party Claude weekly allowance reported by Antigravity.',
  'claude': 'Third-party Claude allowance reported by Antigravity.',
  'usage': 'Current usage reported by the installed desktop client.',
  'credits': 'Remaining or consumed GitHub Copilot credits.',
  'daily': 'Usage in the current daily allowance window.',
  'monthly': 'Usage in the provider’s monthly allowance window.',
};

function metricDescription(metricName) {
  const [, ...parts] = metricName.split(':');
  const key = parts.join(':').trim().toLowerCase();
  return metricDescriptions[key] || 'Usage reported by this provider when available.';
}

function notificationProviderSummary(selectedCount, totalCount) {
  if (selectedCount === totalCount && totalCount > 0) return 'All providers';
  if (selectedCount === 0) return 'No providers';
  return `${selectedCount} provider${selectedCount === 1 ? '' : 's'}`;
}

function eligibleNotificationProviders(providers) {
  return Array.isArray(providers)
    ? providers.filter(provider => provider?.id && provider.available === true)
    : [];
}

function notificationProviderOptions() {
  return Array.from(document.querySelectorAll('#notification-provider-menu [role="option"]'));
}

function syncNotificationProviderFocus() {
  notificationProviderOptions().forEach((option, index) => {
    option.tabIndex = index === 0 ? 0 : -1;
  });
}

function renderNotificationProviderPicker(settings, providers) {
  const catalog = eligibleNotificationProviders(providers);
  const selected = new Set((settings?.notificationProviderIds || []).map(id => String(id).toLowerCase()));
  const selectedCount = catalog.filter(provider => selected.has(provider.id.toLowerCase())).length;
  const allSelected = catalog.length > 0 && selectedCount === catalog.length;
  const menu = $('#notification-provider-menu');
  const summary = $('#notification-provider-summary');
  if (!menu || !summary) return;

  summary.textContent = notificationProviderSummary(selectedCount, catalog.length);
  menu.innerHTML = [
    ...(catalog.length ? [`<button type="button" class="notification-provider-option notification-provider-select-all" data-notification-select-all role="option" aria-selected="${allSelected}" aria-label="${allSelected ? 'Clear all providers' : 'Select all providers'}"><span>${allSelected ? 'Clear all providers' : 'Select all providers'}</span></button>`] : []),
    ...catalog.map(provider => {
      const checked = selected.has(provider.id.toLowerCase());
      return `<button type="button" class="notification-provider-option" data-notification-provider="${esc(provider.id)}" role="option" aria-selected="${checked}"><span class="notification-provider-indicator" aria-hidden="true"></span><span class="notification-provider-option-name">${providerLogo(provider.id)}<span>${esc(provider.displayName)}</span></span></button>`;
    }),
  ].join('');
  if (!catalog.length) menu.innerHTML = '<div class="notification-provider-empty">No signed-in providers found.</div>';
  syncNotificationProviderFocus();
}

function setNotificationProviderMenuOpen(open) {
  const picker = $('#notification-provider-picker');
  const menu = $('#notification-provider-menu');
  const trigger = $('#notification-provider-trigger');
  if (open) {
    document.querySelectorAll('.select-control.open').forEach(closeSelect);
    closeHeaderPopovers();
  }
  picker?.classList.toggle('open', open);
  setOverlayOpen(menu, open);
  trigger?.setAttribute('aria-expanded', String(open));
  if (open) {
    const options = notificationProviderOptions();
    const target = options.find(option => option.getAttribute('aria-selected') === 'true' && !option.hasAttribute('data-notification-select-all')) || options[0];
    syncNotificationProviderFocus();
    target?.focus();
  } else {
    trigger?.focus();
  }
}

function closeNotificationProviderMenu() {
  setNotificationProviderMenuOpen(false);
}

function populateSettingsForm(settings) {
  document.querySelectorAll('#settings-view [data-field]').forEach(input => {
    const value = settings[input.dataset.field];
    if (input.type === 'checkbox') input.checked = Boolean(value);
    else if (value !== undefined && value !== null) input.value = value;
  });
  renderNotificationProviderPicker(settings, currentSettingsData?.providers);
}

function collectSettingsForm(base) {
  const result = { ...base };
  document.querySelectorAll('#settings-view [data-field]').forEach(input => {
    result[input.dataset.field] = input.type === 'checkbox' ? input.checked : input.value;
  });
  result.notificationProviderIds = Array.from(document.querySelectorAll('#notification-provider-menu [data-notification-provider]'))
    .filter(option => option.getAttribute('aria-selected') === 'true')
    .map(option => option.dataset.notificationProvider);
  return result;
}

function renderCustomizeForm(data) {
  const disabled = new Set((data.settings.disabledProviders || []).map(name => name.toLowerCase()));
  const expanded = new Set(Array.from(document.querySelectorAll('#customize-providers [data-metric-disclosure][aria-expanded="true"]')).map(button => button.dataset.providerId));
  state.providerCatalog = data.providers || state.providerCatalog;
  const metricGroups = new Map((data.metricNames || []).map(name => {
    const [providerId] = name.split(':');
    return [name, { providerId: providerId.toLowerCase(), name }];
  }));
  const metricsByProvider = new Map();
  for (const item of metricGroups.values()) {
    if (!metricsByProvider.has(item.providerId)) metricsByProvider.set(item.providerId, []);
    metricsByProvider.get(item.providerId).push(item.name);
  }
  const starredList = data.settings.starredMetrics || [];
  const statusByProvider = new Map((data.providerStatuses || [])
    .filter(status => status?.id && status.reason)
    .map(status => [status.id.toLowerCase(), status.reason]));
  $('#customize-providers').innerHTML = data.providers.map(p => {
    const checked = !disabled.has(p.id.toLowerCase());
    const metricNames = metricsByProvider.get(p.id.toLowerCase()) || [];
    const selectedCount = metricNames.filter(name => starredList.some(entry => entry.toLowerCase() === name.toLowerCase())).length;
    const panelId = `metric-options-${p.id}`;
    const isExpanded = expanded.has(p.id);
    const issue = statusByProvider.get(p.id.toLowerCase());
    const metricRows = metricNames.map(name => {
      const [, ...parts] = name.split(':');
      const label = parts.join(':').replace(/-/g, ' ').replace(/\b\w/g, ch => ch.toUpperCase());
      const metricChecked = starredList.some(entry => entry.toLowerCase() === name.toLowerCase());
      return `<label class="toggle-row metric-option"><input class="toggle-input" type="checkbox" data-metric-name="${esc(name)}" ${metricChecked ? 'checked' : ''}><span class="toggle-track" aria-hidden="true"><span class="toggle-thumb"></span></span><span class="toggle-label"><span>${esc(label)}</span><small>${esc(metricDescription(name))}</small></span></label>`;
    }).join('');
    const metricWord = metricNames.length === 1 ? 'metric' : 'metrics';
    const disclosureLabel = isExpanded ? 'Hide' : 'Show';
    const logo = issue
      ? `<button type="button" class="provider-status-trigger" data-provider-status="${esc(issue)}" aria-label="${esc(p.displayName)} needs attention" aria-describedby="provider-status-tooltip"><span class="provider-customize-logo has-issue">${providerLogo(p.id)}<span class="provider-status-badge" aria-hidden="true">!</span></span></button>`
      : `<span class="provider-customize-logo">${providerLogo(p.id)}</span>`;
    return `<div class="provider-customize-group" data-provider-group="${esc(p.id)}"><div class="provider-customize-row"><div class="provider-name-label">${logo}<span>${esc(p.displayName)}</span></div><button type="button" class="metric-disclosure" data-metric-disclosure data-provider-id="${esc(p.id)}" aria-expanded="${isExpanded ? 'true' : 'false'}" aria-controls="${panelId}" aria-label="${disclosureLabel} ${esc(p.displayName)} metrics"><span class="metric-counts"><span class="metric-count">Exposes ${metricNames.length} ${metricWord}</span><span class="metric-selected">${selectedCount} visible</span></span><svg viewBox="0 0 16 16" aria-hidden="true"><path d="m3 6 5 5 5-5"></path></svg></button><label class="provider-switch" aria-label="Enable ${esc(p.displayName)}"><input class="toggle-input" type="checkbox" data-provider="${esc(p.id)}" ${checked ? 'checked' : ''}><span class="toggle-track" aria-hidden="true"><span class="toggle-thumb"></span></span></label></div><div id="${panelId}" class="provider-metric-options${isExpanded ? ' is-open' : ''}"><div class="metric-options-inner">${metricRows || '<div class="field-note">No catalog metrics yet.</div>'}</div></div></div>`;
  }).join('');
}

function collectCustomizeForm(base) {
  const result = { ...base };
  result.disabledProviders = Array.from(document.querySelectorAll('#customize-providers [data-provider]'))
    .filter(input => !input.checked).map(input => input.dataset.provider.toLowerCase());
  result.starredMetrics = Array.from(document.querySelectorAll('#customize-providers [data-metric-name]'))
    .filter(input => input.checked).map(input => input.dataset.metricName);
  return result;
}

async function applySettingsImmediately(name) {
  if (!currentSettingsData) return;
  if (settingsApplyInFlight) {
    settingsApplyQueuedName = name;
    return;
  }
  const previous = currentSettingsData.settings;
  const updated = name === 'settings'
    ? collectSettingsForm(previous)
    : collectCustomizeForm(previous);
  // Only a change to which providers are enabled needs new data from the host. Display-only
  // preferences (usage display, reset-time format) used to trigger a full provider round-trip and
  // drop the whole UI into a loading state for seconds.
  const providersChanged = JSON.stringify((previous.disabledProviders || []).slice().sort())
    !== JSON.stringify((updated.disabledProviders || []).slice().sort());
  settingsApplyInFlight = true;
  try {
    await invoke('apply_settings_data', { settings: updated });
    currentSettingsData.settings = updated;
    state.settings = updated;
    rememberMotionPreference();
    applyMotionPreference();
    if (name === 'customize') {
      state.enabledProviders = (currentSettingsData.providers || []).map(p => p.id).filter(id => !(updated.disabledProviders || []).includes(id));
      renderCustomizeForm(currentSettingsData);
      wireInstantSettings(name);
    }
    try { await invoke('set_screen_share_privacy', { hidden: Boolean(updated.hideFromScreenShare) }); } catch (_) { /* older native host */ }
    render();
    if (providersChanged) refresh(true);
  } catch (error) {
    showStatus('Could not save that setting.', STATUS_LONG, error?.toString?.());
  } finally {
    settingsApplyInFlight = false;
    if (settingsApplyQueuedName) {
      const queuedName = settingsApplyQueuedName;
      settingsApplyQueuedName = null;
      scheduleSettingsApply(queuedName);
    }
  }
}

function scheduleSettingsApply(name) {
  clearTimeout(settingsApplyTimer);
  settingsApplyTimer = setTimeout(() => applySettingsImmediately(name), 160);
}

function wireInstantSettings(name) {
  const root = $(`#${name}-view`);
  root.querySelectorAll('input, select').forEach(input => input.addEventListener('change', () => scheduleSettingsApply(name)));
}

function updateNotificationProviderSummary() {
  const options = Array.from(document.querySelectorAll('#notification-provider-menu [data-notification-provider]'));
  const selected = options.filter(option => option.getAttribute('aria-selected') === 'true').length;
  const all = options.length > 0 && selected === options.length;
  $('#notification-provider-summary').textContent = notificationProviderSummary(selected, options.length);
  const selectAll = $('#notification-provider-menu [data-notification-select-all]');
  if (selectAll) {
    selectAll.setAttribute('aria-selected', String(all));
    selectAll.querySelector('span:last-child').textContent = all ? 'Clear all providers' : 'Select all providers';
  }
}

$('#notification-provider-trigger')?.addEventListener('click', event => {
  event.stopPropagation();
  setNotificationProviderMenuOpen(!$('#notification-provider-picker')?.classList.contains('open'));
});

$('#notification-provider-trigger')?.addEventListener('keydown', event => {
  if (!['ArrowDown', 'ArrowUp', 'Enter', ' ', 'Spacebar'].includes(event.key)) return;
  event.preventDefault();
  setNotificationProviderMenuOpen(true);
});

function toggleNotificationProviderOption(target) {
  if (!target) return;
  if (target.hasAttribute('data-notification-select-all')) {
    const providers = Array.from(document.querySelectorAll('#notification-provider-menu [data-notification-provider]'));
    const allSelected = providers.length > 0 && providers.every(option => option.getAttribute('aria-selected') === 'true');
    providers.forEach(option => option.setAttribute('aria-selected', String(!allSelected)));
  } else {
    const selected = target.getAttribute('aria-selected') === 'true';
    target.setAttribute('aria-selected', String(!selected));
  }
  updateNotificationProviderSummary();
  scheduleSettingsApply('settings');
}

$('#notification-provider-menu')?.addEventListener('click', event => {
  const target = event.target.closest('[data-notification-provider], [data-notification-select-all]');
  if (!target) return;
  toggleNotificationProviderOption(target);
});

$('#notification-provider-menu')?.addEventListener('keydown', event => {
  const options = notificationProviderOptions();
  const currentIndex = options.indexOf(event.target.closest('[role="option"]'));
  if (currentIndex < 0) return;
  if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
    event.preventDefault();
    const nextIndex = event.key === 'Home' ? 0 : event.key === 'End' ? options.length - 1 : Math.max(0, Math.min(options.length - 1, currentIndex + (event.key === 'ArrowDown' ? 1 : -1)));
    options.forEach((option, index) => { option.tabIndex = index === nextIndex ? 0 : -1; });
    options[nextIndex]?.focus();
    return;
  }
  if (event.key === 'Escape') {
    event.preventDefault();
    closeNotificationProviderMenu();
    return;
  }
  if (event.key === 'Enter' || event.key === ' ' || event.key === 'Spacebar') {
    event.preventDefault();
    toggleNotificationProviderOption(options[currentIndex]);
    return;
  }
  if (event.key.length === 1 && /\S/.test(event.key)) {
    const query = event.key.toLowerCase();
    const start = (currentIndex + 1) % options.length;
    const ordered = options.slice(start).concat(options.slice(0, start));
    const match = ordered.find(option => option.textContent.trim().toLowerCase().startsWith(query));
    if (match) {
      event.preventDefault();
      options.forEach(option => { option.tabIndex = option === match ? 0 : -1; });
      match.focus();
    }
  }
});

function closeSelect(control) {
  if (!control) return;
  control.classList.remove('open');
  setOverlayOpen(control.querySelector('.select-menu'), false);
  control.querySelector('.select-trigger')?.setAttribute('aria-expanded', 'false');
  control.querySelectorAll('.select-option.is-active').forEach(option => option.classList.remove('is-active'));
}

function enhanceSelects(root) {
  root.querySelectorAll('select:not([data-enhanced])').forEach((select, selectIndex) => {
    select.dataset.enhanced = 'true';
    select.classList.add('select-native');
    // The native element is a 1x1 invisible control. Leaving it focusable put a phantom tab stop
    // in front of every enhanced select; the custom control below now carries the keyboard model.
    select.tabIndex = -1;
    select.setAttribute('aria-hidden', 'true');
    const control = document.createElement('div');
    control.className = 'select-control';
    const menuId = `select-menu-${selectIndex}-${select.dataset.field || select.name || 'field'}`;
    const trigger = document.createElement('button');
    trigger.type = 'button';
    trigger.className = 'select-trigger';
    trigger.setAttribute('aria-haspopup', 'listbox');
    trigger.setAttribute('aria-expanded', 'false');
    trigger.setAttribute('aria-controls', menuId);
    const label = select.getAttribute('aria-label') || select.closest('label')?.querySelector('span')?.textContent;
    if (label) trigger.setAttribute('aria-label', label.trim());
    const menu = document.createElement('div');
    menu.className = 'select-menu overlay-surface';
    menu.id = menuId;
    menu.setAttribute('role', 'listbox');
    const arrow = '<svg viewBox="0 0 12 7" aria-hidden="true"><path d="m1 1.25 5 4.5 5-4.5"></path></svg>';
    const options = [];
    const sync = () => {
      const selected = select.options[select.selectedIndex];
      trigger.innerHTML = `<span>${esc(selected?.textContent || '')}</span>${arrow}`;
      options.forEach(option => {
        const isSelected = option.dataset.selectValue === select.value;
        option.classList.toggle('selected', isSelected);
        option.setAttribute('aria-selected', String(isSelected));
      });
    };
    const commit = (option) => {
      select.value = option.dataset.selectValue;
      select.dispatchEvent(new Event('change', { bubbles: true }));
      sync();
      closeSelect(control);
      trigger.focus();
    };
    const isOpen = () => control.classList.contains('open');
    const activeIndex = () => {
      const current = options.findIndex(option => option.classList.contains('is-active'));
      return current >= 0 ? current : Math.max(0, options.findIndex(option => option.dataset.selectValue === select.value));
    };
    const setActive = (index) => {
      const clamped = Math.max(0, Math.min(options.length - 1, index));
      options.forEach((option, position) => option.classList.toggle('is-active', position === clamped));
      trigger.setAttribute('aria-activedescendant', options[clamped]?.id || '');
      options[clamped]?.scrollIntoView({ block: 'nearest' });
    };
    const open = () => {
      document.querySelectorAll('.select-control.open').forEach(other => { if (other !== control) closeSelect(other); });
      control.classList.add('open');
      setOverlayOpen(menu, true);
      trigger.setAttribute('aria-expanded', 'true');
      setActive(activeIndex());
    };

    Array.from(select.options).forEach((option, index) => {
      const item = document.createElement('button');
      item.type = 'button';
      item.className = 'select-option';
      item.id = `${menuId}-option-${index}`;
      item.dataset.selectValue = option.value;
      item.setAttribute('role', 'option');
      item.tabIndex = -1;
      item.textContent = option.textContent;
      item.addEventListener('click', () => commit(item));
      options.push(item);
      menu.appendChild(item);
    });

    trigger.addEventListener('click', event => {
      event.stopPropagation();
      if (isOpen()) closeSelect(control);
      else open();
    });
    trigger.addEventListener('keydown', event => {
      const { key } = event;
      if (key === 'ArrowDown' || key === 'ArrowUp' || key === 'Home' || key === 'End') {
        event.preventDefault();
        if (!isOpen()) { open(); return; }
        if (key === 'Home') setActive(0);
        else if (key === 'End') setActive(options.length - 1);
        else setActive(activeIndex() + (key === 'ArrowDown' ? 1 : -1));
        return;
      }
      if (key === 'Enter' || key === ' ' || key === 'Spacebar') {
        event.preventDefault();
        if (!isOpen()) open();
        else commit(options[activeIndex()]);
        return;
      }
      if (key.length === 1 && /\S/.test(key)) {
        // Type-ahead, matching how a real <select> behaves.
        const start = activeIndex() + 1;
        const match = options.findIndex((option, index) =>
          (index >= start || isOpen() === false) && option.textContent.toLowerCase().startsWith(key.toLowerCase()));
        const wrapped = match >= 0 ? match : options.findIndex(option => option.textContent.toLowerCase().startsWith(key.toLowerCase()));
        if (wrapped >= 0) {
          if (isOpen()) setActive(wrapped);
          else commit(options[wrapped]);
        }
      }
    });

    sync();
    control.append(trigger, menu);
    // Inserted as a sibling of the label rather than inside it: nested inside, clicking the label
    // text focused the invisible native select and appeared to do nothing.
    const owningLabel = select.closest('label');
    if (owningLabel) {
      owningLabel.appendChild(control);
      owningLabel.addEventListener('click', event => {
        if (event.target.closest('.select-control')) return;
        event.preventDefault();
        trigger.focus();
        if (!isOpen()) open();
      });
    } else {
      select.parentNode.insertBefore(control, select);
    }
  });
}

document.addEventListener('click', event => {
  if (event.target.closest('.select-control') || event.target.closest('#notification-provider-picker')) return;
  document.querySelectorAll('.select-control.open').forEach(closeSelect);
  closeNotificationProviderMenu();
});

// Remembers which control opened the page so focus can be handed back on exit.
let pageReturnFocus = null;
let pageRequestGeneration = 0;

function closeSettingsPage(page) {
  // Invalidate any request that is still waiting on get_settings_data(). Without this, a slower
  // Settings request could finish after a newer Customize request and activate both pages.
  pageRequestGeneration += 1;
  closeNotificationProviderMenu();
  const view = page || document.querySelector('.page-view.active');
  document.querySelectorAll('.page-view.active').forEach(active => active.classList.remove('active'));
  $('#content')?.removeAttribute('inert');
  $('.chrome')?.removeAttribute('inert');
  $('.footer')?.removeAttribute('inert');
  const target = pageReturnFocus;
  pageReturnFocus = null;
  target?.focus?.();
}

async function openSettingsPage(name) {
  const requestGeneration = ++pageRequestGeneration;
  closeHeaderPopovers();
  try {
    const data = await invoke('get_settings_data');
    // A later navigation or a popup reopen superseded this async response. Never let stale work
    // alter the page stack after the user has moved somewhere else.
    if (requestGeneration !== pageRequestGeneration) return;
    currentSettingsData = data;
    state.settings = data.settings || state.settings;
    rememberMotionPreference();
    applyMotionPreference();
    state.providerCatalog = data.providers || state.providerCatalog;
    if (name === 'settings') populateSettingsForm(data.settings);
    else renderCustomizeForm(data);
    const view = $(`#${name}-view`);
    enhanceSelects(view);
    // Only one page at a time. Opening Customize from the tray while Settings was already open
    // left both marked active at the same z-index, with DOM order silently picking the winner.
    document.querySelectorAll('.page-view.active').forEach(other => other.classList.remove('active'));
    view.classList.add('active');
    // The dashboard behind the page is visually gone; take it out of the tab order too.
    $('#content')?.setAttribute('inert', '');
    $('.chrome')?.setAttribute('inert', '');
    $('.footer')?.setAttribute('inert', '');
    view.querySelector('[data-page-back]')?.focus();
    wireInstantSettings(name);
    try { await invoke('set_screen_share_privacy', { hidden: Boolean(state.settings.hideFromScreenShare) }); } catch (_) { /* older native host */ }
  } catch (error) {
    if (requestGeneration !== pageRequestGeneration) return;
    showStatus('Settings are unavailable while the desktop host is starting.', STATUS_LONG, error?.toString?.());
  }
}

document.querySelectorAll('[data-options]').forEach(button => button.addEventListener('click', () => {
  pageReturnFocus = button;
  openSettingsPage(button.dataset.options);
}));

document.querySelectorAll('[data-page-back]').forEach(button => button.addEventListener('click', () => {
  closeSettingsPage(button.closest('.page-view'));
}));

const copyLogsButton = $('#copy-logs-button');
copyLogsButton?.addEventListener('click', async () => {
  if (copyLogsButton.disabled) return;
  copyLogsButton.disabled = true;
  try {
    const bundle = await withTimeout(
      invoke('get_diagnostics_bundle'),
      COMMAND_TIMEOUT_MS,
      'The logs command did not respond.'
    );
    const text = typeof bundle === 'string' ? bundle : JSON.stringify(bundle, null, 2);
    await copyTextToClipboard(text);
    showStatus('Logs copied to clipboard.', STATUS_SHORT);
  } catch (error) {
    showStatus('Could not copy logs.', STATUS_LONG, error?.toString?.());
  } finally {
    copyLogsButton.disabled = false;
  }
});

async function copyTextToClipboard(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch (_) { /* fall through to the legacy path */ }
  const textarea = document.createElement('textarea');
  textarea.value = text;
  textarea.style.position = 'fixed';
  textarea.style.opacity = '0';
  document.body.appendChild(textarea);
  textarea.select();
  try {
    return document.execCommand('copy');
  } catch (_) {
    return false;
  } finally {
    textarea.remove();
  }
}

const providerStatusTooltip = $('#provider-status-tooltip');
function positionProviderStatusTooltip(clientX, clientY) {
  const gap = 12;
  const bounds = providerStatusTooltip.getBoundingClientRect();
  providerStatusTooltip.style.left = `${Math.min(window.innerWidth - bounds.width - 10, Math.max(10, clientX + gap))}px`;
  providerStatusTooltip.style.top = `${Math.min(window.innerHeight - bounds.height - 10, Math.max(10, clientY + gap))}px`;
}
function showProviderStatusTooltip(trigger, clientX, clientY) {
  if (!trigger?.dataset.providerStatus) return;
  providerStatusTooltip.textContent = trigger.dataset.providerStatus;
  positionProviderStatusTooltip(clientX, clientY);
  setOverlayOpen(providerStatusTooltip, true);
}
function hideProviderStatusTooltip() {
  setOverlayOpen(providerStatusTooltip, false);
}

$('#customize-providers').addEventListener('click', event => {
  if (event.target.closest('[data-provider-status]')) return;
  const button = event.target.closest('[data-metric-disclosure]');
  if (!button) return;
  const panel = document.getElementById(button.getAttribute('aria-controls'));
  if (!panel) return;
  const expanded = button.getAttribute('aria-expanded') === 'true';
  button.setAttribute('aria-expanded', expanded ? 'false' : 'true');
  const provider = state.providerCatalog.find(item => item.id === button.dataset.providerId);
  button.setAttribute('aria-label', `${expanded ? 'Show' : 'Hide'} ${provider?.displayName || button.dataset.providerId} metrics`);
  // Class, not the hidden attribute: display:none cannot animate, and the chevron above it has
  // been animating on its own since before this pass.
  panel.classList.toggle('is-open', !expanded);
});
$('#customize-providers').addEventListener('mousemove', event => {
  const trigger = event.target.closest('[data-provider-status]');
  if (trigger) showProviderStatusTooltip(trigger, event.clientX, event.clientY);
  else hideProviderStatusTooltip();
});
$('#customize-providers').addEventListener('mouseleave', hideProviderStatusTooltip);
$('#customize-providers').addEventListener('focusin', event => {
  const trigger = event.target.closest('[data-provider-status]');
  if (!trigger) return;
  const rect = trigger.getBoundingClientRect();
  showProviderStatusTooltip(trigger, rect.right, rect.top);
});
$('#customize-providers').addEventListener('focusout', event => {
  if (!event.relatedTarget?.closest?.('[data-provider-status]')) hideProviderStatusTooltip();
});

$('#providers').addEventListener('click', async event => {
  const button = event.target.closest('[data-provider-action]');
  if (!button) return;
  const kind = button.dataset.providerAction;
  if (kind !== 'claude-login' && kind !== 'antigravity-login') return;
  button.disabled = true;
  try {
    if (kind === 'claude-login') {
      await invoke('open_claude_login');
      showStatus('Claude sign-in opened in a new terminal.');
    } else {
      await invoke('open_antigravity_login');
      showStatus('Antigravity CLI opened in a new terminal. Finish sign-in there.');
    }
  } catch (error) {
    showStatus('Could not start provider sign-in.', STATUS_LONG, error?.toString?.());
  } finally {
    button.disabled = false;
  }
});
$('#spend-ring').addEventListener('mousemove', event => setSpendHover(spendProviderAtPoint(event.clientX, event.clientY)));
$('#spend-ring').addEventListener('mouseleave', () => setSpendHover(null));
const trendTooltip = $('#trend-tooltip');
let activeTrendBar = null;
let trendTooltipTimer = 0;
function positionTrendTooltip(event) {
  if (!activeTrendBar) return;
  const gap = 12;
  const bounds = trendTooltip.getBoundingClientRect();
  const left = Math.min(window.innerWidth - bounds.width - 10, Math.max(10, event.clientX + gap));
  const top = Math.min(window.innerHeight - bounds.height - 10, Math.max(10, event.clientY - bounds.height - gap));
  trendTooltip.style.left = `${left}px`;
  trendTooltip.style.top = `${top}px`;
}
document.addEventListener('mousemove', event => {
  const bar = event.target.closest?.('.trend i');
  if (!bar) {
    activeTrendBar = null;
    clearTimeout(trendTooltipTimer);
    setOverlayOpen(trendTooltip, false);
    return;
  }
  const detail = bar.dataset.tooltip;
  if (!detail) return;
  if (activeTrendBar !== bar) {
    activeTrendBar = bar;
    trendTooltip.textContent = detail;
    positionTrendTooltip(event);
    // A short delay before the first appearance. Without it, sweeping across 30 bars flashed the
    // tooltip 30 times. Once it is up it tracks the cursor immediately.
    if (!trendTooltip.classList.contains('is-open')) {
      clearTimeout(trendTooltipTimer);
      trendTooltipTimer = setTimeout(() => {
        if (activeTrendBar) setOverlayOpen(trendTooltip, true);
      }, 80);
    }
  }
  positionTrendTooltip(event);
});
// Hover works in both directions. Aggregate rows also expose the progressive breakdown without
// forcing a full-page navigation.
$('#spend-legend').addEventListener('mouseover', event => {
  const row = event.target.closest('.legend-row');
  if (!row || !event.currentTarget.contains(row)) return;
  setSpendHover(row.dataset.spendProvider);
  const aggregate = aggregateFromLegendRow(row);
  if (aggregate) {
    const rect = row.getBoundingClientRect();
    showSpendOtherTooltip(aggregate, event.clientX || rect.right, event.clientY || rect.top);
  }
});
$('#spend-legend').addEventListener('mousemove', event => {
  const row = event.target.closest('.legend-row');
  if (!row || !event.currentTarget.contains(row)) return;
  const aggregate = aggregateFromLegendRow(row);
  if (aggregate) {
    if ($('#spend-other-tooltip')?.classList.contains('is-open'))
      updateSpendOtherTooltipPosition(event.clientX, event.clientY);
    else
      showSpendOtherTooltip(aggregate, event.clientX, event.clientY);
  }
});
$('#spend-legend').addEventListener('mouseleave', () => {
  setSpendHover(null);
  closeSpendOtherTooltip();
});
$('#spend-legend').addEventListener('focusin', event => {
  const row = event.target.closest('.legend-row');
  const aggregate = aggregateFromLegendRow(row);
  if (!aggregate) return;
  setSpendHover(row.dataset.spendProvider);
  const rect = row.getBoundingClientRect();
  showSpendOtherTooltip(aggregate, rect.right, rect.top);
});
$('#spend-legend').addEventListener('focusout', event => {
  if (!event.relatedTarget?.closest?.('#spend-legend')) closeSpendOtherTooltip();
});
document.addEventListener('mousemove', event => {
  if (state.spendTooltipRowId && event.target.closest?.('#spend-legend'))
    updateSpendOtherTooltipPosition(event.clientX, event.clientY);
});
window.addEventListener('resize', () => {
  renderSpend();
  if (state.view === 'breakdown' && window.innerWidth < 720) {
    setBreakdownView(false, true);
  } else if (state.view === 'breakdown') {
    renderBreakdown();
  }
});
// The Rust side reveals the window with a raw ShowWindow and no fade, so the entrance happens in
// CSS here. revealPopover is idempotent and is called from four places on purpose — an invisible
// window is a far worse failure than a missed animation, so every path that could mean "visible"
// re-asserts it.
let revealAnimationFrame = 0;
let focusRevealTimer = 0;
function revealPopover(restart = false) {
  const popover = $('.popover');
  if (!popover) return;
  cancelAnimationFrame(revealAnimationFrame);
  popover.classList.remove('closing');
  if (!restart || prefersReducedMotion()) {
    popover.classList.remove('opening');
    popover.classList.add('shown');
    return;
  }
  popover.classList.add('opening');
  popover.classList.remove('shown');
  void popover.offsetWidth;
  const finishOpening = event => {
    if (event.target === popover && event.propertyName === 'transform') {
      popover.classList.remove('opening');
      popover.removeEventListener('transitionend', finishOpening);
    }
  };
  popover.addEventListener('transitionend', finishOpening);
  // The forced layout above commits the offset state. Starting the transition immediately keeps
  // the web surface synchronized with the native HWND rise instead of spending two frames fully
  // transparent after Windows has already shown the popup.
  popover.classList.add('shown');
}
function beginPopoverClose() {
  const popover = $('.popover');
  if (!popover) return;
  cancelAnimationFrame(revealAnimationFrame);
  popover.classList.remove('opening');
  if (prefersReducedMotion()) {
    popover.classList.remove('shown');
    return;
  }
  popover.classList.add('closing');
}
// Backstop for a browser preview or a native startup race. Never replay it over an entrance that
// already completed, which was the source of the delayed flash after opening.
setTimeout(() => {
  if (!$('.popover')?.classList.contains('shown')) revealPopover(true);
}, 250);
// Focused implies visible.
window.addEventListener('focus', () => {
  clearTimeout(focusRevealTimer);
  focusRevealTimer = setTimeout(revealPopover, 90);
});
// Reset only while genuinely hidden, so the next open animates again. If WebView2 does not report
// visibility for a hidden native window this simply never fires and we lose the entrance on
// subsequent opens — degraded, not broken.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'hidden') {
    $('.popover')?.classList.remove('shown', 'opening');
    if (state.view === 'breakdown') setBreakdownView(false, true);
  }
});

window.__TAURI__?.event?.listen?.('poc-refresh', () => refresh(true));
window.__TAURI__?.event?.listen?.('poc-closing', beginPopoverClose);
window.__TAURI__?.event?.listen?.('poc-opened', () => {
  clearTimeout(focusRevealTimer);
  closeHeaderPopovers();
  // A tray click means "show me my usage". Reopening onto whatever settings page happened to be
  // open when the popup was last dismissed is a wrong answer to that.
  document.querySelectorAll('.select-control.open').forEach(closeSelect);
  closeSettingsPage();
  // The Rust side always shows this popup at compact geometry. Reset the DOM synchronously too,
  // so reopening never briefly paints the previous wide view into a compact native window.
  if (state.view === 'breakdown') setBreakdownView(false, true);
  revealPopover(true);
  refresh(false);
});
// Lets the tray's right-click menu open straight to Settings/Customize (see openSettingsPage)
// instead of a second native window.
window.__TAURI__?.event?.listen?.('open-page', event => openSettingsPage(event.payload));
window.addEventListener('usage-monitor-open-page', event => openSettingsPage(event.detail));
withTimeout(
  invoke('get_settings_data'),
  COMMAND_TIMEOUT_MS,
  'The settings command did not respond.'
).then(data => {
  if (!data?.settings) return;
  state.settings = data.settings;
  rememberMotionPreference();
  applyMotionPreference();
  state.metric = normalizeSpendMetric(state.settings.spendMetric);
  updateMetricMenu();
  state.providerCatalog = data.providers || state.providerCatalog;
  render();
  return invoke('set_screen_share_privacy', { hidden: Boolean(state.settings.hideFromScreenShare) });
}).catch(() => { /* the native host can still be starting */ });
refresh(false);
syncRefreshStatus();
setInterval(async () => {
  const wasLoading = state.localLoading || state.hostLoading;
  await syncRefreshStatus();
  // A background WPF refresh can complete while the popup is open. Pull the new cached
  // envelope once the host leaves its loading state, without creating a second refresh clock.
  if (wasLoading && !state.localLoading && !state.hostLoading) refresh(false);
  // The desktop API and WebView start independently. A first request can legitimately arrive
  // before the loopback listener is ready; without this retry, the popup kept rendering its
  // synthetic "not configured" placeholders forever even though the live API came up seconds
  // later. Retrying only while no real snapshot exists keeps the normal five-minute refresh
  // cadence untouched.
  if (!state.localLoading && !state.hostLoading && !state.snapshots.length &&
      Date.now() - lastInitialDataRetryAt >= 2000) {
    lastInitialDataRetryAt = Date.now();
    refresh(false);
  }
}, 1000);

// Compact presentation. Provider contracts stay untouched at this boundary.
function spendProviderColor(id) {
  return {
    'claude-code': '#da7756',
    codex: '#3d82f6',
    antigravity: '#34a853',
    opencode: '#ffffff',
    cursor: '#6c7bff',
    copilot: '#8957e5',
    devin: '#ffb454',
    grok: '#c9ced6',
  }[id] || '#8d7dff';
}

function spendValue(cost, tokens) {
  if (state.metric === 'tokens') return tokens;
  if (state.metric === 'cost') return cost;
  // Cost/MTok is a rate, and rates are not additive, so it cannot size ring slices (a
  // high-rate, near-zero-usage provider would swallow the donut). Weight slices by tokens:
  // the blended rate in the center is a token-weighted average of the per-provider rates.
  return tokens;
}

function spendRowOrder() {
  return new Map(state.providerCatalog.map((provider, index) => [provider.id, index]));
}

function spendRowCompare(a, b, providerOrder) {
  return (providerOrder.get(a.id) ?? Number.MAX_SAFE_INTEGER)
    - (providerOrder.get(b.id) ?? Number.MAX_SAFE_INTEGER);
}

function groupSmallSpendRows(rows) {
  if (rows.length < 3) return rows.slice();
  const total = rows.reduce((sum, row) => sum + Number(row.value || 0), 0);
  if (total <= 0) return rows.slice();
  const providerOrder = spendRowOrder();
  const byValue = rows.slice().sort((a, b) => Number(b.value || 0) - Number(a.value || 0)
    || spendRowCompare(a, b, providerOrder));
  // Compact geometry needs a slightly wider tail than the native dashboard. At this size a 2%
  // slice is technically valid but still too narrow to target reliably, so fold a multi-provider
  // tail up to 3% into the accessible Others row.
  const threshold = total * 0.03;
  const tail = [];
  let tailValue = 0;
  for (let index = byValue.length - 1; index >= 0; index--) {
    const candidate = byValue[index];
    if (tailValue + Number(candidate.value || 0) > threshold) break;
    tail.push(candidate);
    tailValue += Number(candidate.value || 0);
  }
  if (tail.length < 2) return rows.slice().sort((a, b) => spendRowCompare(a, b, providerOrder));

  const children = tail.slice().sort((a, b) => Number(b.value || 0) - Number(a.value || 0)
    || spendRowCompare(a, b, providerOrder));
  const tailIds = new Set(children.map(row => row.id));
  const aggregate = {
    id: 'others',
    name: 'Others',
    cost: children.reduce((sum, row) => sum + Number(row.cost || 0), 0),
    tokens: children.reduce((sum, row) => sum + Number(row.tokens || 0), 0),
    value: children.reduce((sum, row) => sum + Number(row.value || 0), 0),
    color: '#77777D',
    isAggregate: true,
    children,
  };
  return rows.filter(row => !tailIds.has(row.id)).sort((a, b) => spendRowCompare(a, b, providerOrder)).concat(aggregate);
}

function renderSpend() {
  const rows = state.snapshots.map(snapshot => {
    const cost = spendFor(snapshot);
    const tokens = tokensFor(snapshot);
    const catalogEntry = state.providerCatalog.find(provider => provider.id === snapshot.providerId);
    return {
      id: snapshot.providerId,
      name: snapshot.providerId === 'claude-code' ? 'Claude' : snapshot.displayName || catalogEntry?.displayName || snapshot.providerId,
      cost,
      tokens,
      value: spendValue(cost, tokens),
      color: spendProviderColor(snapshot.providerId),
      isAggregate: false,
    };
  }).filter(row => row.value > 0)
    .sort((a, b) => spendRowCompare(a, b, spendRowOrder()));
  const rootRows = groupSmallSpendRows(rows);
  lastSpendRootRows = rootRows;
  lastSpendDisplayedRows = rootRows;
  if (state.hoveredSpendProviderId && !rootRows.some(row => row.id === state.hoveredSpendProviderId))
    state.hoveredSpendProviderId = null;
  const totalCost = rootRows.reduce((sum, row) => sum + row.cost, 0);
  const totalTokens = rootRows.reduce((sum, row) => sum + row.tokens, 0);
  const centerValue = state.metric === 'tokens' ? compactNumber(totalTokens)
    : state.metric === 'cost' ? `$${compactNumber(totalCost)}`
    : `$${(totalTokens > 0 ? totalCost / totalTokens * 1e6 : 0).toFixed(2)}`;
  const centerUnit = state.metric === 'tokens' ? 'tokens' : '';
  drawRing(rootRows, centerValue, centerUnit);
  renderLegend(rootRows);
  const spendCard = $('#spend-card');
  if (spendCard) {
    spendCard.dataset.spendView = 'root';
    spendCard.dataset.spendTotal = String(Math.round(totalCost * 1e6) / 1e6);
  }
}

// Keyed for the same reason as the provider list. Critically, the highlight is applied as a class
// on existing nodes rather than baked into the markup — re-emitting the row already carrying
// .is-highlighted is why the legend's hover transition could never run.

function legendLabel(row) {
  if (state.metric === 'tokens') return compactNumber(row.tokens);
  if (state.metric === 'cost') return `$${row.cost.toFixed(2)}`;
  return `$${(row.tokens > 0 ? row.cost / row.tokens * 1e6 : 0).toFixed(2)}`;
}

function legendStructureKey(rows) {
  // Values are deliberately excluded. They change with each refresh, whereas a new provider or
  // a renamed provider changes the actual row structure and warrants rebuilding the legend.
  return JSON.stringify(rows.map(row => [row.id, row.name, Boolean(row.isAggregate),
    row.children?.map(child => child.id) || []]));
}

function renderLegend(rows) {
  const key = legendStructureKey(rows);
  if (key !== lastLegendKey) {
    lastLegendKey = key;
    $('#spend-legend').innerHTML = rows.map(row =>
      `<div class="legend-row${row.isAggregate ? ' is-aggregate' : ''}" data-spend-provider="${esc(row.id)}" data-spend-aggregate="${row.isAggregate ? 'true' : 'false'}"${row.isAggregate ? ` tabindex="0" aria-label="${esc(row.name)}, ${esc(legendLabel(row))}. Provider details appear on hover or focus."` : ''}><span class="dot" style="background:${row.color}"></span><span class="name">${esc(row.name)}</span><span class="value">${esc(legendLabel(row))}</span></div>`
    ).join('');
  } else {
    // Keep the actual row nodes alive. Besides avoiding needless layout work, this preserves the
    // highlighted class long enough for its transition to animate instead of appearing already
    // settled on a freshly created element.
    const nodes = new Map(Array.from(document.querySelectorAll('#spend-legend .legend-row'))
      .map(node => [node.dataset.spendProvider, node]));
    rows.forEach(row => {
      const node = nodes.get(row.id);
      const value = node?.querySelector('.value');
      const label = legendLabel(row);
      if (value && value.textContent !== label) value.textContent = label;
    });
  }
  applyLegendHighlight();
}

// --- Share copy: generated chart image + text ------------------------------------------------

function formatShareDate(key) {
  const [year, month, day] = String(key).split('-').map(Number);
  const names = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  return `${names[(month || 1) - 1] || ''} ${day || ''}, ${year || ''}`.trim();
}

function periodRangeText() {
  if (state.period === 'today') return `Today · ${formatShareDate(dayKey(0))}`;
  if (state.period === 'yesterday') return `Yesterday · ${formatShareDate(dayKey(1))}`;
  return `${formatShareDate(dayKey(29))} – ${formatShareDate(dayKey(0))} · Last 30 Days`;
}

function compactShareText() {
  const rows = lastSpendRootRows;
  const totalCost = rows.reduce((sum, row) => sum + row.cost, 0);
  const totalTokens = rows.reduce((sum, row) => sum + row.tokens, 0);
  const lines = [`TokenBurn · ${periodRangeText()}`];
  lines.push(...rows.map(row => `${row.name} ${legendLabel(row)}`));
  if (state.metric === 'tokens') lines.push(`Total ${shareTokenCount(totalTokens)} tokens`);
  else if (state.metric === 'cost-mtok') lines.push(`Total $${totalCost.toFixed(2)} · ${shareTokenCount(totalTokens)} tokens`);
  else lines.push(`Total $${totalCost.toFixed(2)}`);
  return lines.join('\n');
}

// Token counts in copied text read better as whole numbers below 1K; compactNumber's two-decimal
// fallback exists for cost figures like the ring center and would print "500.00 tokens".
function shareTokenCount(number) {
  return number >= 1e3 ? compactNumber(number) : String(Math.round(number || 0));
}

const SHARE_IMAGE_WIDTH = 800;
const SHARE_IMAGE_HEIGHT = 400;

// Draws the spend card as a shareable image straight from the data the dashboard renders — the
// same rows and the same ring painter, not a screenshot. The clipboard carries the PNG-equivalent
// bitmap alongside the text so pasting into an assistant chat brings both. Sized like the compact
// card: small ring, tight legend rows, minimal chrome.
function buildShareImage() {
  const canvas = document.createElement('canvas');
  canvas.width = SHARE_IMAGE_WIDTH;
  canvas.height = SHARE_IMAGE_HEIGHT;
  const ctx = canvas.getContext('2d');
  const rows = lastSpendRootRows;
  const totalCost = rows.reduce((sum, row) => sum + row.cost, 0);
  const totalTokens = rows.reduce((sum, row) => sum + row.tokens, 0);
  let centerValue;
  if (state.metric === 'tokens') centerValue = shareTokenCount(totalTokens);
  else if (state.metric === 'cost') centerValue = `$${compactNumber(totalCost)}`;
  else centerValue = `$${(totalTokens > 0 ? totalCost / totalTokens * 1e6 : 0).toFixed(2)}`;
  const centerUnit = state.metric === 'tokens' ? 'tokens' : '';
  const margin = 32;
  const footerH = 40;
  const bodyTop = 62;
  const bodyBottom = SHARE_IMAGE_HEIGHT - footerH;

  // Card surface and hairline border, matching the popup's dark theme.
  ctx.fillStyle = '#0F1115';
  ctx.fillRect(0, 0, SHARE_IMAGE_WIDTH, SHARE_IMAGE_HEIGHT);
  ctx.strokeStyle = 'rgba(255,255,255,.08)';
  ctx.lineWidth = 1;
  if (ctx.roundRect) {
    ctx.beginPath();
    ctx.roundRect(.5, .5, SHARE_IMAGE_WIDTH - 1, SHARE_IMAGE_HEIGHT - 1, 16);
    ctx.stroke();
  } else {
    ctx.strokeRect(.5, .5, SHARE_IMAGE_WIDTH - 1, SHARE_IMAGE_HEIGHT - 1);
  }

  // Header: brand + period on the left, generated timestamp on the right, one compact row.
  ctx.textAlign = 'left';
  ctx.fillStyle = '#f1f1f2';
  ctx.font = '700 19px -apple-system,Segoe UI,sans-serif';
  ctx.fillText('TokenBurn', margin, 32);
  ctx.fillStyle = '#9ea0a8';
  ctx.font = '500 12px -apple-system,Segoe UI,sans-serif';
  ctx.fillText(periodRangeText(), margin, 50);
  const generated = `Generated ${new Date().toLocaleString([], { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })}`;
  ctx.fillStyle = '#77777D';
  ctx.font = '400 11px -apple-system,Segoe UI,sans-serif';
  ctx.fillText(generated, SHARE_IMAGE_WIDTH - margin - ctx.measureText(generated).width, 32);
  ctx.strokeStyle = 'rgba(255,255,255,.09)';
  ctx.beginPath();
  ctx.moveTo(margin, bodyTop);
  ctx.lineTo(SHARE_IMAGE_WIDTH - margin, bodyTop);
  ctx.stroke();

  // The donut, painted by the same painter the live dashboard uses at final values. Sized near
  // the compact card's proportion so the exported chart reads like the dashboard, not a poster.
  const ringSize = 240;
  const ringCanvas = document.createElement('canvas');
  ringCanvas.width = ringSize;
  ringCanvas.height = ringSize;
  paintRingFrame(ringCanvas.getContext('2d'), ringSize, rows, centerValue, centerUnit);
  const ringLeft = 48;
  const ringTop = bodyTop + (bodyBottom - bodyTop - ringSize) / 2;
  ctx.drawImage(ringCanvas, ringLeft, ringTop);

  // Legend rows mirror #spend-legend: dot, name, value right-aligned.
  const legendLeft = 300;
  const legendRight = SHARE_IMAGE_WIDTH - margin;
  const rowHeight = 28;
  const legendTop = bodyTop + Math.max(0, (bodyBottom - bodyTop - rows.length * rowHeight) / 2);
  if (!rows.length) {
    ctx.fillStyle = '#9ea0a8';
    ctx.font = '500 13px -apple-system,Segoe UI,sans-serif';
    ctx.fillText('No usage data for this period.', legendLeft + 5, legendTop + 13);
  }
  rows.forEach((row, index) => {
    const y = legendTop + index * rowHeight + rowHeight / 2;
    ctx.fillStyle = row.color;
    ctx.beginPath();
    ctx.arc(legendLeft + 5, y, 4.5, 0, TAU);
    ctx.fill();
    ctx.textAlign = 'left';
    ctx.fillStyle = '#f1f1f2';
    ctx.font = '500 13px -apple-system,Segoe UI,sans-serif';
    ctx.fillText(row.name, legendLeft + 18, y + 4.5);
    ctx.textAlign = 'right';
    ctx.font = '600 13px -apple-system,Segoe UI,sans-serif';
    ctx.fillText(legendLabel(row), legendRight, y + 4.5);
  });
  ctx.textAlign = 'left';

  // Footer: totals for the selected period.
  ctx.strokeStyle = 'rgba(255,255,255,.09)';
  ctx.beginPath();
  ctx.moveTo(margin, SHARE_IMAGE_HEIGHT - footerH);
  ctx.lineTo(SHARE_IMAGE_WIDTH - margin, SHARE_IMAGE_HEIGHT - footerH);
  ctx.stroke();
  let footer;
  if (state.metric === 'tokens') footer = `Total ${shareTokenCount(totalTokens)} tokens`;
  else if (state.metric === 'cost') footer = `Total $${totalCost.toFixed(2)}`;
  else footer = `Total $${totalCost.toFixed(2)} · ${shareTokenCount(totalTokens)} tokens`;
  ctx.fillStyle = '#9ea0a8';
  ctx.font = '500 12px -apple-system,Segoe UI,sans-serif';
  ctx.fillText(footer, margin, SHARE_IMAGE_HEIGHT - 18);
  const caption = 'Local usage history';
  ctx.fillStyle = '#77777D';
  ctx.font = '400 11px -apple-system,Segoe UI,sans-serif';
  ctx.fillText(caption, SHARE_IMAGE_WIDTH - margin - ctx.measureText(caption).width, SHARE_IMAGE_HEIGHT - 18);
  return canvas;
}

// Writes the chart bitmap to the clipboard. The native command is the primary path because it
// bypasses the WebView clipboard-permission quirks; the web ClipboardItem path covers older
// binaries that predate the command. A null text writes the image formats only.
async function copyShareToClipboard(text, canvas) {
  try {
    const pixels = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
    let binary = '';
    // apply() with a very large argument list can exhaust the call stack on some engines; 16K
    // elements per call is comfortably below the limits of every WebView2/WebKit build.
    const chunk = 0x4000;
    for (let index = 0; index < pixels.length; index += chunk) {
      binary += String.fromCharCode.apply(null, pixels.subarray(index, index + chunk));
    }
    // Chromium paste targets (ChatGPT, browser uploads) read image data from the registered PNG
    // clipboard format, so encode the same canvas as a PNG alongside the raw bitmap.
    const pngBlob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
    let pngBase64 = '';
    if (pngBlob) {
      pngBase64 = await new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result).split(',')[1] || '');
        reader.onerror = reject;
        reader.readAsDataURL(pngBlob);
      });
    }
    await withTimeout(invoke('copy_share', {
      payload: {
        text,
        width: canvas.width,
        height: canvas.height,
        rgbaBase64: btoa(binary),
        pngBase64,
      },
    }), COMMAND_TIMEOUT_MS, 'The clipboard command did not respond.');
    return true;
  } catch (_) {
    // Fall through to the web clipboard path.
  }
  try {
    if (typeof ClipboardItem === 'undefined') return false;
    const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
    if (!blob) return false;
    const payload = { 'image/png': blob };
    if (text) payload['text/plain'] = new Blob([text], { type: 'text/plain' });
    await navigator.clipboard.write([new ClipboardItem(payload)]);
    return true;
  } catch (_) {
    return false;
  }
}

async function copyShareImageOnly(canvas) {
  return copyShareToClipboard(null, canvas);
}

function drawMiniSpendRing(rows) {
  const canvas = $('#spend-other-ring');
  if (!canvas) return;
  const size = 82;
  const scale = window.devicePixelRatio || 1;
  canvas.width = size * scale;
  canvas.height = size * scale;
  canvas.style.width = `${size}px`;
  canvas.style.height = `${size}px`;
  const ctx = canvas.getContext('2d');
  ctx.setTransform(scale, 0, 0, scale, 0, 0);
  const center = size / 2;
  const radius = 27;
  const stroke = 12;
  ctx.clearRect(0, 0, size, size);
  ctx.beginPath();
  ctx.arc(center, center, radius, 0, TAU);
  ctx.strokeStyle = '#3a3a3d';
  ctx.lineWidth = stroke;
  ctx.stroke();
  const total = rows.reduce((sum, row) => sum + Number(row.value || 0), 0);
  if (total <= 0) return;
  let cursor = -Math.PI / 2;
  rows.forEach(row => {
    const angle = Number(row.value || 0) / total * TAU;
    const gap = angle > 0.12 ? 0.045 : 0;
    ctx.beginPath();
    ctx.arc(center, center, radius, cursor + gap, cursor + angle - gap, false);
    ctx.strokeStyle = row.color;
    ctx.lineWidth = stroke;
    ctx.lineCap = 'butt';
    ctx.stroke();
    cursor += angle;
  });
}

function updateSpendOtherTooltipPosition(clientX, clientY) {
  const tooltip = $('#spend-other-tooltip');
  if (!tooltip?.classList.contains('is-open')) return;
  const bounds = tooltip.getBoundingClientRect();
  const gap = 13;
  const left = clientX + gap + bounds.width <= window.innerWidth - 8
    ? clientX + gap
    : Math.max(8, clientX - bounds.width - gap);
  const top = Math.min(window.innerHeight - bounds.height - 8,
    Math.max(8, clientY - bounds.height - 10));
  tooltip.style.left = `${left}px`;
  tooltip.style.top = `${top}px`;
}

function closeSpendOtherTooltip() {
  clearTimeout(state.spendTooltipTimer);
  state.spendTooltipTimer = 0;
  state.spendTooltipRowId = null;
  setOverlayOpen($('#spend-other-tooltip'), false);
}

function showSpendOtherTooltip(row, clientX, clientY) {
  if (!row?.isAggregate || !row.children?.length) {
    closeSpendOtherTooltip();
    return;
  }
  clearTimeout(state.spendTooltipTimer);
  state.spendTooltipRowId = row.id;
  $('#spend-other-total').textContent = legendLabel(row);
  $('#spend-other-list').innerHTML = row.children.map(child =>
    `<div class="spend-other-item"><span class="dot" style="background:${child.color}"></span><span>${esc(child.name)}</span><strong>${esc(legendLabel(child))}</strong></div>`
  ).join('');
  drawMiniSpendRing(row.children);
  setOverlayOpen($('#spend-other-tooltip'), true);
  updateSpendOtherTooltipPosition(clientX, clientY);
}

function aggregateFromLegendRow(row) {
  if (!row?.dataset.spendAggregate || row.dataset.spendAggregate !== 'true') return null;
  return lastSpendRootRows.find(item => item.id === row.dataset.spendProvider && item.isAggregate) || null;
}

function applyLegendHighlight() {
  document.querySelectorAll('#spend-legend .legend-row').forEach(row =>
    row.classList.toggle('is-highlighted', row.dataset.spendProvider === state.hoveredSpendProviderId));
}

function spendProviderAtPoint(clientX, clientY) {
  const canvas = $('#spend-ring');
  const rect = canvas.getBoundingClientRect();
  const size = lastRingSize || canvas.clientWidth || 200;
  const x = (clientX - rect.left) * size / Math.max(1, rect.width);
  const y = (clientY - rect.top) * size / Math.max(1, rect.height);
  const center = size / 2;
  const radius = size * .335;
  const stroke = size * .11;
  const distance = Math.hypot(x - center, y - center);
  if (distance < radius - stroke * .7 || distance > radius + stroke * .7) return null;
  const total = lastRingValues.reduce((sum, item) => sum + Number(item.value || 0), 0);
  if (total <= 0) return null;
  let angle = Math.atan2(y - center, x - center) + Math.PI / 2;
  if (angle < 0) angle += Math.PI * 2;
  let cursor = 0;
  for (const item of lastRingValues) {
    const sweep = Number(item.value || 0) / total * Math.PI * 2;
    if (angle >= cursor && angle <= cursor + sweep) return item.id;
    cursor += sweep;
  }
  return null;
}

function setSpendHover(providerId) {
  if (state.hoveredSpendProviderId === providerId) return;
  state.hoveredSpendProviderId = providerId;
  // Toggle classes and repaint the canvas. renderSpend() no longer rebuilds the legend, so the
  // highlight transition survives.
  applyLegendHighlight();
  renderSpend();
}

function visibleLines(snapshot) {
  const lines = Array.isArray(snapshot.lines) ? snapshot.lines : [];
  const progress = lines.filter(progressLine);
  if (snapshot.providerId === 'antigravity') {
    const ordered = [...progress].sort((a, b) => {
      const rank = label => /^session$/i.test(label) ? 0 : /^weekly$/i.test(label) ? 1 : 2;
      return rank(a.label) - rank(b.label);
    });
    const extras = lines.filter(line => !progressLine(line));
    return [...ordered, ...extras];
  }
  const session = progress.find(line => /session|five.?hour|hourly/i.test(line.label));
  const weekly = progress.find(line => /^weekly$|weekly|seven.?day/i.test(line.label));
  const extras = lines.filter(line => !progressLine(line) || /extra|credit|reset|trend|today|yesterday|last 30/i.test(line.label));
  return [...new Set([session, weekly, ...extras].filter(Boolean))];
}

function displayLine(snapshot, line) {
  if (snapshot.providerId !== 'antigravity') return line;
  if (/^Claude Weekly$/i.test(line.label)) return { ...line, label: 'Claude Weekly (third-party)' };
  if (/^Claude$/i.test(line.label)) return { ...line, label: 'Claude (third-party)' };
  return line;
}
