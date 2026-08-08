const TAURI = window.__TAURI__;
const invoke = (command, args) => TAURI?.core?.invoke(command, args);
const currentWindow = () => TAURI?.window?.getCurrentWindow?.();

const state = {
  snapshots: [],
  period: '30',
  metric: 'cost',
  localLoading: false,
  hostLoading: false,
  refreshStatusError: '',
  lastGood: null,
  nextRefreshAt: null,
  enabledProviders: ['claude-code', 'codex', 'antigravity'],
  settings: { usageDisplay: 'Used', resetTimeDisplay: 'Countdown', taskbarPositionLocked: true },
  hoveredSpendProviderId: null,
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

// Providers group their local history by UTC calendar day, so the window here must compare
// against UTC "today" too. Comparing a UTC-stamped date string against a local midnight let the
// evening hours (once UTC has already rolled to tomorrow) silently drop every point for "today",
// which is why an otherwise healthy provider (e.g. Codex) could vanish from the donut entirely.
function periodPoints(snapshot) {
  const points = snapshot.usageHistory?.points || [];
  const now = new Date();
  const day = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate());
  return points.filter(point => {
    const date = new Date(`${point.date}T00:00:00Z`).getTime();
    if (state.period === 'today') return date === day;
    if (state.period === 'yesterday') return date === day - 86400000;
    return date >= day - 29 * 86400000 && date <= day;
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
let statusTimer;

function showStatus(message, duration = 2400) {
  const status = $('#status');
  status.textContent = message;
  status.classList.add('visible');
  clearTimeout(statusTimer);
  statusTimer = setTimeout(() => status.classList.remove('visible'), duration);
}

function closeHeaderPopovers() {
  $('#info-popover').hidden = true;
  $('#metric-popover').hidden = true;
}

function normalizeSpendMetric(value) {
  return value === 'tokens' || value === 'cost-mtok' ? value : 'cost';
}

function updateMetricMenu() {
  document.querySelectorAll('[data-metric]').forEach(item =>
    item.classList.toggle('selected', item.dataset.metric === state.metric));
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
  const total = values.reduce((sum, item) => sum + item.value, 0);
  cancelAnimationFrame(ringAnimationFrame);
  const started = performance.now();
  const paint = (now) => {
    const progress = renderChanged ? Math.min(1, (now - started) / 450) : 1;
    ctx.clearRect(0, 0, size, size);
    let cursor = -Math.PI / 2;
    const microMarkers = [];
    values.forEach(item => {
      if (!item.value || total <= 0) return;
      const angle = item.value / total * Math.PI * 2;
      const sweep = angle * progress;
      if (sweep > 0) {
        const focused = state.hoveredSpendProviderId === item.id;
        const dimmed = state.hoveredSpendProviderId && !focused;
        ctx.globalAlpha = dimmed ? .42 : 1;
        const width = focused ? stroke + 4 : stroke;
        ctx.strokeStyle = item.color;
        const path = donutSegmentPath({
          cx: center,
          cy: center,
          startBoundary: cursor,
          endBoundary: cursor + sweep,
          radius,
          width,
          gap: Math.min(3.5, size * .022),
          corner: Math.min(2.75, size * .016),
        });
        if (path) {
          ctx.fillStyle = item.color;
          ctx.fill(path);
        }
        // A provider can legitimately account for only a few pixels of the total. Keep the
        // proportional arc as the source of truth, then add a tiny rounded locator at its actual
        // angular position so the provider is still discoverable in the chart and legend.
        if (angle < 0.065 && progress >= 1) {
          microMarkers.push({
            item,
            center: cursor + angle / 2,
            sweep: Math.max(0.018, angle),
          });
        }
        ctx.globalAlpha = 1;
      }
      cursor += angle;
    });
    microMarkers.forEach(({ item, center: markerCenter, sweep: markerSweep }) => {
      const focused = state.hoveredSpendProviderId === item.id;
      const dimmed = state.hoveredSpendProviderId && !focused;
      ctx.globalAlpha = dimmed ? .42 : 1;
      const markerPath = donutSegmentPath({
        cx: center,
        cy: center,
        startBoundary: markerCenter - markerSweep / 2,
        endBoundary: markerCenter + markerSweep / 2,
        radius,
        width: focused ? stroke + 4 : stroke,
        gap: Math.min(1.1, size * .008),
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
    let primarySize = Math.min(21, size * 0.128);
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
    if (progress < 1) ringAnimationFrame = requestAnimationFrame(paint);
  };
  if (renderChanged) ringAnimationFrame = requestAnimationFrame(paint);
  else paint(performance.now());
}

// Final renderer uses the real provider SVG marks from the upstream OpenUsage asset pack.
function renderProvidersFinal() {
  const catalog = state.providerCatalog;
  const snapshotsById = new Map(state.snapshots.map(snapshot => [snapshot.providerId, snapshot]));
  const snapshots = state.enabledProviders.map(providerId => {
    const descriptor = catalog.find(provider => provider.id === providerId) || { id: providerId, displayName: providerId };
    return snapshotsById.get(providerId) || {
      providerId,
      displayName: descriptor.displayName,
      lines: [{ type: 'badge', text: 'Provider is not configured.' }],
    };
  });
  $('#providers').innerHTML = snapshots.map(snapshot => {
    const warning = snapshot.warning || snapshot.error;
    const compactLines = visibleLines(snapshot).map(line => displayLine(snapshot, line));
    const lines = compactLines.map(renderMetric).join('');
    const localHistory = renderLocalHistory(snapshot);
    const errorText = [warning, ...compactLines.map(line => line.text || line.value || '')].filter(Boolean).join(' ');
    const canReauth = snapshot.providerId === 'claude-code' && !lines.includes('class="meter"') && /(auth|login|expired|not configured)/i.test(errorText);
    const displayName = snapshot.providerId === 'claude-code' ? 'Claude Code' : snapshot.displayName;
    return `<article class="provider"><div class="provider-heading"><span class="provider-mark">${providerLogo(snapshot.providerId)}</span><div><span class="provider-name">${esc(displayName)}</span><span class="provider-plan">${esc(snapshot.plan || '')}</span></div>${warning ? '<span class="provider-warning" aria-label="Provider warning">!</span>' : ''}</div><div class="provider-card">${warning ? `<div class="error-line" title="${esc(warning)}">${esc(warning)}</div>` : ''}${canReauth ? '<button class="provider-action" data-provider-action="claude-login">Open Claude sign-in</button>' : ''}${lines || (!localHistory ? '<div class="error-line">No live limits returned.</div>' : '')}${localHistory}</div></article>`;
  }).join('');
}

function renderMetric(line) {
  if (progressLine(line)) {
    const used = Number(line.used || 0);
    const limit = Number(line.limit || 0);
    const remaining = Math.max(0, limit - used);
    const shown = state.settings.usageDisplay === 'Remaining' ? remaining : used;
    const fraction = Math.max(0, Math.min(1, used / limit));
    const stateClass = fraction >= .95 ? 'danger' : fraction >= .75 ? 'warn' : '';
    const suffix = state.settings.usageDisplay === 'Remaining' ? 'remaining' : 'used';
    const precision = shown % 1 ? 1 : 0;
    return `<div class="metric"><div class="metric-top"><span class="metric-label">${esc(line.label)}</span></div><div class="meter"><div class="fill ${stateClass}" style="width:${fraction * 100}%"></div></div><div class="metric-bottom"><span class="metric-value">${shown.toFixed(precision)}% ${suffix}</span><span>${esc(formatReset(line.resetsAt))}</span></div></div>`;
  }
  if (line.type === 'chart') {
    const points = (line.points || []).map(point => `<i style="height:${Math.max(3, Math.min(100, Number(point.value || 0) * 100))}%"></i>`).join('');
    return `<div class="metric"><div class="metric-top"><span class="metric-label">${esc(line.label)}</span></div><div class="trend">${points || '<i style="height:3px"></i>'.repeat(16)}</div></div>`;
  }
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
  const bars = points.slice(-30).map(point => {
    const value = Math.max(0, Number(point.tokens || point.costUsd || 0));
    const peak = Math.max(1, ...points.slice(-30).map(item => Number(item.tokens || item.costUsd || 0)));
    return `<i style="height:${Math.max(3, Math.min(100, value / peak * 100))}%"></i>`;
  }).join('');
  return `<div class="metric history-trend"><div class="metric-top"><span class="metric-label">Usage Trend</span></div><div class="trend">${bars || '<i style="height:3px"></i>'.repeat(16)}</div></div><div class="history-lines"><div class="text-line"><span>Today</span><span>${formatHistoryValue(todayTotals)}</span></div><div class="text-line"><span>Yesterday</span><span>${formatHistoryValue(yesterdayTotals)}</span></div><div class="text-line"><span>Last 30 Days</span><span>${formatHistoryValue(monthTotals)}</span></div></div>`;
}

function render() {
  const loading = state.localLoading || state.hostLoading;
  document.body.classList.toggle('refreshing', loading);
  $('#metric-title').textContent = state.metric === 'tokens' ? 'Tokens' : state.metric === 'cost-mtok' ? 'Cost/MTok' : 'Cost';
  renderSpend();
  renderProvidersFinal();
  $('#updated').textContent = loading
    ? 'Refreshing...'
    : state.refreshStatusError || formatRefreshCountdown(state.nextRefreshAt);
}

async function syncRefreshStatus() {
  try {
    const status = await invoke('fetch_refresh_status');
    if (!status) return;
    state.refreshStatusError = '';
    state.nextRefreshAt = status.nextRefreshAt || null;
    if (typeof status.loading === 'boolean') state.hostLoading = status.loading;
    render();
  } catch (_) {
    // The popup can still render cached provider data while the WPF host is starting.
    state.refreshStatusError = 'Refresh service unavailable';
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
      try {
        await invoke('request_desktop_refresh');
      } catch (_) {
        // A tray click during desktop startup can race the bridge. Preserve the old direct API
        // path as a safe fallback, then let the status poll converge once the host is ready.
        await invoke('fetch_usage', { force: true });
      }
    }
    const [snapshots, enabledProviders] = await Promise.all([
      invoke('fetch_usage', { force: false }),
      invoke('fetch_enabled_providers').catch(() => state.enabledProviders)
    ]);
    if (!Array.isArray(snapshots)) throw new Error('The local API returned no provider list.');
    state.snapshots = snapshots;
    if (Array.isArray(enabledProviders)) state.enabledProviders = enabledProviders;
    state.lastGood = Date.now();
    const errors = snapshots.filter(snapshot => snapshot.error).length;
    if (force && errors) showStatus(`${errors} provider update${errors === 1 ? '' : 's'} need attention.`);
  } catch (error) {
    state.refreshStatusError = 'Provider update unavailable';
    showStatus(error?.toString?.() || 'The local provider service is unavailable.', 3600);
  } finally {
    state.localLoading = false;
    await syncRefreshStatus();
    render();
  }
}

async function hidePopup() {
  const window = currentWindow();
  if (window) {
    try { await window.hide(); } catch (_) { /* use the Rust command below */ }
  }
  try { await invoke('hide_popup'); } catch (_) { /* startup can race WebView2 readiness */ }
}

document.addEventListener('keydown', event => {
  if (event.key === 'Escape' || event.code === 'Escape') {
    event.preventDefault();
    hidePopup();
  }
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
$('#share-button').addEventListener('click', async () => {
  closeHeaderPopovers();
  try {
    await navigator.clipboard?.writeText(`Usage Monitor: ${$('#spend-legend').innerText}`);
    showStatus('Copied spend summary');
  } catch (_) {
    showStatus('Clipboard access was blocked by Windows.', 3200);
  }
});
$('#info-button').addEventListener('click', () => {
  const next = $('#info-popover').hidden;
  closeHeaderPopovers();
  $('#info-popover').hidden = !next;
});
$('#metric-menu').addEventListener('click', () => {
  const next = $('#metric-popover').hidden;
  closeHeaderPopovers();
  $('#metric-popover').hidden = !next;
});
document.querySelectorAll('[data-metric]').forEach(button => button.addEventListener('click', async () => {
  state.metric = normalizeSpendMetric(button.dataset.metric);
  state.settings.spendMetric = state.metric;
  updateMetricMenu();
  $('#metric-popover').hidden = true;
  render();
  try {
    await invoke('set_spend_metric', { metric: state.metric });
  } catch (_) {
    showStatus('Metric preference could not be saved.', 3200);
  }
}));
document.querySelectorAll('[data-period]').forEach(button => button.addEventListener('click', () => { state.period = button.dataset.period; document.querySelectorAll('[data-period]').forEach(item => item.classList.toggle('selected', item === button)); render(); }));
// Settings/Customize render as pages inside this same popup window (see .page-view in
// styles.css) instead of spawning a second native window. Two windows coordinating focus,
// ownership, and position across Explorer's taskbar was the actual source of the
// "settings won't open" / "opens off-screen" / "opens behind the dashboard" bugs; a page
// inside the popup's own window has none of that.
let currentSettingsData = null;
let settingsApplyTimer = null;
let settingsApplyInFlight = false;
let settingsApplyQueuedName = null;

function populateSettingsForm(settings) {
  document.querySelectorAll('#settings-view [data-field]').forEach(input => {
    const value = settings[input.dataset.field];
    if (input.type === 'checkbox') input.checked = Boolean(value);
    else if (value !== undefined && value !== null) input.value = value;
  });
}

function collectSettingsForm(base) {
  const result = { ...base };
  document.querySelectorAll('#settings-view [data-field]').forEach(input => {
    result[input.dataset.field] = input.type === 'checkbox' ? input.checked : input.value;
  });
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
  $('#customize-providers').innerHTML = data.providers.map(p => {
    const checked = !disabled.has(p.id.toLowerCase());
    const metricNames = metricsByProvider.get(p.id.toLowerCase()) || [];
    const selectedCount = metricNames.filter(name => starredList.some(entry => entry.toLowerCase() === name.toLowerCase())).length;
    const panelId = `metric-options-${p.id}`;
    const isExpanded = expanded.has(p.id);
    const metricRows = metricNames.map(name => {
      const [, ...parts] = name.split(':');
      const label = parts.join(':').replace(/-/g, ' ').replace(/\b\w/g, ch => ch.toUpperCase());
      const metricChecked = starredList.some(entry => entry.toLowerCase() === name.toLowerCase());
      return `<label class="toggle-row"><input class="toggle-input" type="checkbox" data-metric-name="${esc(name)}" ${metricChecked ? 'checked' : ''}><span class="toggle-track" aria-hidden="true"><span class="toggle-thumb"></span></span><span class="toggle-label">${esc(label)}</span></label>`;
    }).join('');
    const metricWord = metricNames.length === 1 ? 'metric' : 'metrics';
    const disclosureLabel = isExpanded ? 'Hide' : 'Show';
    return `<div class="provider-customize-group" data-provider-group="${esc(p.id)}"><div class="provider-customize-row"><label class="toggle-row provider-toggle"><input class="toggle-input" type="checkbox" data-provider="${esc(p.id)}" ${checked ? 'checked' : ''}><span class="toggle-track" aria-hidden="true"><span class="toggle-thumb"></span></span><span class="toggle-label">${providerLogo(p.id)}${esc(p.displayName)}</span></label><button type="button" class="metric-disclosure" data-metric-disclosure data-provider-id="${esc(p.id)}" aria-expanded="${isExpanded ? 'true' : 'false'}" aria-controls="${panelId}" aria-label="${disclosureLabel} ${esc(p.displayName)} metrics"><span class="metric-counts"><span class="metric-count">Exposes ${metricNames.length} ${metricWord}</span><span class="metric-selected">${selectedCount} visible</span></span><svg viewBox="0 0 16 16" aria-hidden="true"><path d="m3 6 5 5 5-5"></path></svg></button></div><div id="${panelId}" class="provider-metric-options" ${isExpanded ? '' : 'hidden'}>${metricRows || '<div class="field-note">No catalog metrics yet.</div>'}</div></div>`;
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
  const updated = name === 'settings'
    ? collectSettingsForm(currentSettingsData.settings)
    : collectCustomizeForm(currentSettingsData.settings);
  settingsApplyInFlight = true;
  try {
    await invoke('apply_settings_data', { settings: updated });
    currentSettingsData.settings = updated;
    state.settings = updated;
    if (name === 'customize') {
      state.enabledProviders = (currentSettingsData.providers || []).map(p => p.id).filter(id => !(updated.disabledProviders || []).includes(id));
      renderCustomizeForm(currentSettingsData);
      wireInstantSettings(name);
    }
    try { await invoke('set_screen_share_privacy', { hidden: Boolean(updated.hideFromScreenShare) }); } catch (_) { /* older native host */ }
    render();
    refresh(true);
  } catch (error) {
    showStatus(error?.toString?.() || 'Could not apply settings.', 3600);
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

async function openSettingsPage(name) {
  closeHeaderPopovers();
  try {
    const data = await invoke('get_settings_data');
    currentSettingsData = data;
    state.settings = data.settings || state.settings;
    state.providerCatalog = data.providers || state.providerCatalog;
    if (name === 'settings') populateSettingsForm(data.settings);
    else renderCustomizeForm(data);
    $(`#${name}-view`).classList.add('active');
    wireInstantSettings(name);
    try { await invoke('set_screen_share_privacy', { hidden: Boolean(state.settings.hideFromScreenShare) }); } catch (_) { /* older native host */ }
  } catch (error) {
    showStatus(error?.toString?.() || 'Settings are unavailable while the desktop host is starting.', 3600);
  }
}

document.querySelectorAll('[data-options]').forEach(button => button.addEventListener('click', () => {
  openSettingsPage(button.dataset.options);
}));

document.querySelectorAll('[data-page-back]').forEach(button => button.addEventListener('click', () => {
  button.closest('.page-view').classList.remove('active');
}));

$('#customize-providers').addEventListener('click', event => {
  const button = event.target.closest('[data-metric-disclosure]');
  if (!button) return;
  const panel = document.getElementById(button.getAttribute('aria-controls'));
  if (!panel) return;
  const expanded = button.getAttribute('aria-expanded') === 'true';
  button.setAttribute('aria-expanded', expanded ? 'false' : 'true');
  const provider = state.providerCatalog.find(item => item.id === button.dataset.providerId);
  button.setAttribute('aria-label', `${expanded ? 'Show' : 'Hide'} ${provider?.displayName || button.dataset.providerId} metrics`);
  panel.hidden = expanded;
});

$('#providers').addEventListener('click', async event => {
  const button = event.target.closest('[data-provider-action="claude-login"]');
  if (!button) return;
  button.disabled = true;
  try {
    await invoke('open_claude_login');
    showStatus('Claude sign-in opened in a new terminal.');
  } catch (error) {
    showStatus(error?.toString?.() || 'Could not start Claude sign-in.', 3600);
  } finally {
    button.disabled = false;
  }
});
$('#spend-ring').addEventListener('mousemove', event => setSpendHover(spendProviderAtPoint(event.clientX, event.clientY)));
$('#spend-ring').addEventListener('mouseleave', () => setSpendHover(null));
window.addEventListener('resize', renderSpend);
window.__TAURI__?.event?.listen?.('poc-refresh', () => refresh(true));
window.__TAURI__?.event?.listen?.('poc-opened', () => {
  closeHeaderPopovers();
  refresh(false);
});
// Lets the tray's right-click menu open straight to Settings/Customize (see openSettingsPage)
// instead of a second native window.
window.__TAURI__?.event?.listen?.('open-page', event => openSettingsPage(event.payload));
Promise.resolve(invoke('get_settings_data')).then(data => {
  if (!data?.settings) return;
  state.settings = data.settings;
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
}, 1000);

// Compact OpenUsage-style presentation. Provider contracts stay untouched at this boundary.
function spendProviderColor(id) {
  return {
    'claude-code': '#da7756',
    codex: '#3d82f6',
    antigravity: '#34a853',
    opencode: '#2dd4bf',
    cursor: '#6c7bff',
    copilot: '#8957e5',
    devin: '#ffb454',
    grok: '#c9ced6',
  }[id] || '#8d7dff';
}

function spendValue(cost, tokens) {
  if (state.metric === 'tokens') return tokens;
  if (state.metric === 'cost') return cost;
  return tokens > 0 ? cost / tokens * 1e6 : 0;
}

function renderSpend() {
  const providerOrder = new Map(state.providerCatalog.map((provider, index) => [provider.id, index]));
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
    };
  }).filter(row => row.value > 0)
    .sort((a, b) => (providerOrder.get(a.id) ?? Number.MAX_SAFE_INTEGER) - (providerOrder.get(b.id) ?? Number.MAX_SAFE_INTEGER));
  if (state.hoveredSpendProviderId && !rows.some(row => row.id === state.hoveredSpendProviderId))
    state.hoveredSpendProviderId = null;
  const totalCost = rows.reduce((sum, row) => sum + row.cost, 0);
  const totalTokens = rows.reduce((sum, row) => sum + row.tokens, 0);
  const centerValue = state.metric === 'tokens' ? compactNumber(totalTokens)
    : state.metric === 'cost' ? `$${compactNumber(totalCost)}`
    : `$${(totalTokens > 0 ? totalCost / totalTokens * 1e6 : 0).toFixed(2)}`;
  const centerUnit = state.metric === 'tokens' ? 'tokens' : state.metric === 'cost' ? '' : 'per MTok';
  drawRing(rows, centerValue, centerUnit);
  $('#spend-legend').innerHTML = `${rows.map(row => {
    const label = state.metric === 'tokens' ? compactNumber(row.tokens)
      : state.metric === 'cost' ? `$${row.cost.toFixed(2)}`
      : `$${(row.tokens > 0 ? row.cost / row.tokens * 1e6 : 0).toFixed(2)}/MTok`;
    const focused = state.hoveredSpendProviderId === row.id ? ' is-highlighted' : '';
    return `<div class="legend-row${focused}" data-spend-provider="${esc(row.id)}"><span class="dot" style="background:${row.color}"></span><span class="name">${esc(row.name)}</span><span class="value">${label}</span></div>`;
  }).join('')}`;
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
  if (/^Claude Weekly$/i.test(line.label)) return { ...line, label: '3P Claude Weekly' };
  if (/^Claude$/i.test(line.label)) return { ...line, label: '3P Claude' };
  return line;
}
