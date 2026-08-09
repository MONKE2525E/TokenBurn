// Self-check for the popup's render gating.
//
// dist/app.js no longer rebuilds the provider list on every render() — it compares a structure key
// and patches values in place when nothing structural changed. That gate is the kind of thing that
// fails silently: too coarse and the UI goes stale, too fine and we are back to rebuilding once a
// second and losing focus, hover, and every transition.
//
// Run: node UsageMonitor.TauriPoc/selfcheck.mjs

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import vm from 'node:vm';
import assert from 'node:assert/strict';

const here = dirname(fileURLToPath(import.meta.url));
const source = readFileSync(join(here, 'dist', 'app.js'), 'utf8');
const markup = readFileSync(join(here, 'dist', 'index.html'), 'utf8');
const styles = readFileSync(join(here, 'dist', 'styles.css'), 'utf8');

// Enough of a DOM for app.js's module-level wiring to run. Nothing here is asserted against; the
// assertions below only exercise the pure key/derivation logic.
const noop = () => {};
const makeElement = () => new Proxy({}, {
  get(target, property) {
    if (property === 'classList') return { toggle: noop, add: noop, remove: noop, contains: () => false };
    if (property === 'dataset' || property === 'style') return {};
    if (property === 'getBoundingClientRect') return () => ({ left: 0, top: 0, width: 0, height: 0 });
    if (property === 'getContext') return () => ({
      setTransform: noop, clearRect: noop, beginPath: noop, arc: noop, stroke: noop, fill: noop,
      moveTo: noop, lineTo: noop, quadraticCurveTo: noop, closePath: noop, fillText: noop, save: noop, restore: noop,
      createLinearGradient: () => ({ addColorStop: noop }),
    });
    if (property in target) return target[property];
    if (property === 'options' || property === 'children') return [];
    if (property === 'textContent' || property === 'innerHTML' || property === 'id') return '';
    return noop;
  },
  set: () => true,
});

const timers = [];
const context = {
  console,
  performance,
  Date,
  Math,
  JSON,
  Number,
  String,
  Array,
  Object,
  Set,
  Map,
  Boolean,
  Error,
  Promise,
  isNaN,
  parseInt,
  parseFloat,
  setTimeout: () => 0,
  clearTimeout: noop,
  setInterval: (...args) => { timers.push(args); return 0; },
  requestAnimationFrame: () => 0,
  cancelAnimationFrame: noop,
  document: {
    querySelector: () => makeElement(),
    querySelectorAll: () => [],
    addEventListener: noop,
    getElementById: () => makeElement(),
    body: makeElement(),
    visibilityState: 'visible',
  },
};
context.window = context;
context.globalThis = context;
context.window.matchMedia = () => ({ matches: false, addEventListener: noop });
context.window.addEventListener = noop;
context.__TAURI__ = undefined;

vm.createContext(context);
vm.runInContext(`${source}\nglobalThis.__selfCheck = { providersStructureKey, providerRows, progressValues, groupSmallSpendRows, eligibleNotificationProviders, state };`, context, { filename: 'app.js' });

const { providersStructureKey, providerRows, progressValues, groupSmallSpendRows, eligibleNotificationProviders, state } = context.__selfCheck;
assert.ok(typeof providersStructureKey === 'function', 'providersStructureKey should be defined');
assert.ok(typeof providerRows === 'function', 'providerRows should be defined');

const snapshot = (overrides = {}) => ({
  providerId: 'codex',
  displayName: 'Codex',
  plan: 'Pro',
  lines: [{ type: 'progress', label: 'Session', used: 10, limit: 100, resetsAt: '2099-01-01T00:00:00Z' }],
  ...overrides,
});

const keyFor = (snapshots) => {
  state.snapshots = snapshots;
  state.enabledProviders = ['codex'];
  return providersStructureKey(providerRows());
};

const healthy = keyFor([snapshot()]);

// 1. A pure value change must NOT change the key — this is what keeps the list from being torn
//    down and rebuilt once a second, and what lets .fill animate at all.
assert.equal(
  keyFor([snapshot({ lines: [{ type: 'progress', label: 'Session', used: 42, limit: 100, resetsAt: '2099-01-01T00:00:00Z' }] })]),
  healthy,
  'a changed usage number must be patched in place, not trigger a rebuild');

// 2. The regression the plan called out: healthy -> error -> healthy must rebuild both ways. A key
//    that ignored the warning would leave the error card on screen after recovery.
const errored = keyFor([snapshot({ warning: 'Rate limited' })]);
assert.notEqual(errored, healthy, 'an error appearing must rebuild the card');
assert.equal(keyFor([snapshot()]), healthy, 'recovering must rebuild back to the healthy card');

// 3. Structural changes must rebuild.
assert.notEqual(keyFor([snapshot({ plan: 'Team' })]), healthy, 'a plan change must rebuild');
assert.notEqual(
  keyFor([snapshot({ lines: [{ type: 'progress', label: 'Weekly', used: 10, limit: 100 }] })]),
  healthy,
  'a different metric label must rebuild');
assert.notEqual(
  keyFor([snapshot({ usageHistory: { points: [{ date: '2026-01-01', costUsd: 1, tokens: 2 }] } })]),
  healthy,
  'new history must rebuild, since trend bars are not patched');

// 4. Display preferences are deliberately NOT in the key: they only affect text that
//    patchProviderValues writes, so toggling them must not re-sweep every meter from zero.
state.settings = { ...state.settings, usageDisplay: 'Remaining' };
assert.equal(keyFor([snapshot()]), healthy, 'a display preference must not trigger a rebuild');

// 5. progressValues is shared by the renderer and the patcher, so they cannot disagree.
state.settings = { ...state.settings, usageDisplay: 'Used' };
const line = { type: 'progress', label: 'Session', used: 80, limit: 100, resetsAt: '2099-01-01T00:00:00Z' };
assert.equal(progressValues(line, false).stateClass, 'warn', '80% should be the warn threshold');
assert.equal(progressValues(line, false).valueText, '80% used');
assert.equal(progressValues({ ...line, used: 96 }, false).stateClass, 'danger');
assert.equal(progressValues(line, true).resetText, 'Last known limits', 'stale limits must say so');
// An expired window collapses to 0 and says it is updating, rather than showing a stale full bar.
const expired = { ...line, resetsAt: '2000-01-01T00:00:00Z' };
assert.equal(progressValues(expired, false).fraction, 0);
assert.equal(progressValues(expired, false).resetText, 'Updating now');

// 6. Compact spend rows group a multi-provider tail through the accessible 3% threshold.
const spendRow = (id, value) => ({ id, name: id, value, cost: value, tokens: value, color: '#77777D', isAggregate: false });
const exactBoundary = groupSmallSpendRows([
  spendRow('claude-code', 97),
  spendRow('cursor', 1.5),
  spendRow('copilot', 1.5),
]);
const exactOthers = exactBoundary.find(row => row.id === 'others');
assert.ok(exactOthers, 'a tail equal to exactly 3% should become Others');
assert.equal(exactOthers.value, 3, 'Others should preserve the exact cumulative value');
assert.equal(exactOthers.children.map(row => row.id).join(','), 'cursor,copilot');

const oneSmallProvider = groupSmallSpendRows([
  spendRow('claude-code', 96.5),
  spendRow('codex', 3),
  spendRow('cursor', 0.5),
]);
assert.equal(oneSmallProvider.some(row => row.id === 'others'), false, 'one small provider must stay visible');
assert.ok(oneSmallProvider.some(row => row.id === 'cursor'), 'the ungrouped small provider must remain visible');

const screenshotTail = groupSmallSpendRows([
  spendRow('claude-code', 89.51),
  spendRow('codex', 46.78),
  spendRow('antigravity', 0.62),
  spendRow('opencode', 3),
]);
const screenshotOthers = screenshotTail.find(row => row.id === 'others');
assert.ok(screenshotOthers, 'the narrow Antigravity and OpenCode slices should become Others');
assert.equal(screenshotOthers.value, 3.62, 'Others should preserve the screenshot tail total');
assert.equal(screenshotOthers.children.map(row => row.id).join(','), 'opencode,antigravity');

assert.doesNotMatch(source, /spendDrilldownId|enterSpendDrilldown|leaveSpendDrilldown/, 'Others must not switch the popup into a drill-down view');
assert.doesNotMatch(markup, /id="spend-back"/, 'the compact popup must not render an Others back button');

// 7. The status channel is supplementary. A hung Tauri status command must not block a valid
// usage response from painting into the compact popup.
assert.match(source, /withTimeout\(\s*invoke\('fetch_refresh_status'\),\s*COMMAND_TIMEOUT_MS/, 'refresh-status IPC must be bounded');
assert.match(source, /state\.lastGood = Date\.now\(\);\s*\/\/ Paint the data immediately[\s\S]*?render\(\);/, 'usage snapshots must render before status polling');
assert.match(source, /state\.localLoading = false;\s*render\(\);\s*\/\/ Status polling[\s\S]*?void syncRefreshStatus\(\);/, 'refresh cleanup must not await status polling');
assert.match(source, /withTimeout\(\s*invoke\('get_settings_data'\),\s*COMMAND_TIMEOUT_MS/, 'settings IPC must be bounded');

// 8. The compact metric menu must retain the original route to the separate historical Usage
// screen. This is deliberately unrelated to the compact Others tooltip and never changes the
// donut into an Others-only drill-down.
assert.match(markup, /data-metric="breakdown"/, 'the compact metric menu must expose the original full breakdown');
assert.match(markup, /Full breakdown/, 'the original full-breakdown action must remain available');
assert.match(source, /if \(button\.dataset\.metric === 'breakdown'\) \{[\s\S]*?await setBreakdownView\(true\);/, 'the full-breakdown action must open the historical Usage screen');
assert.match(source, /const minimumWidth = 720/, 'the full Usage view must retain its native minimum-width guard');

// 9. The popup entrance must replay on every native open, and reduced motion must skip the
// transition wait instead of merely hiding it with CSS.
assert.match(source, /revealPopover\(true\);/, 'native opens must restart the popup entrance');
assert.match(styles, /translate3d\(0, 22px, 0\) scale\(\.992, \.985\)/, 'the popup must rise visibly from its taskbar-facing edge');
assert.match(source, /const animate = !immediate && !prefersReducedMotion\(\);/, 'reduced motion must bypass the view-transition delay');
assert.match(source, /preference === 'full'/, 'an explicit full-motion setting must override Windows reduced motion');
assert.match(source, /preference === 'reduced'/, 'an explicit reduced-motion setting must disable transitions');
assert.match(source, /motion-reduced-effective/, 'the effective app motion policy must also drive CSS');
assert.doesNotMatch(styles, /@media \(prefers-reduced-motion: reduce\)/, 'raw media queries must not bypass the explicit full-motion override');
assert.match(markup, /data-field="motionPreference"/, 'settings must expose the TokenBurn motion preference');
assert.match(source, /geometry-expanding/, 'the compact-to-wide transition needs a directional phase');
assert.match(source, /geometry-collapsing/, 'the wide-to-compact transition needs a directional phase');
assert.match(source, /if \(!\$\('\.popover'\)\?\.classList\.contains\('shown'\)\) revealPopover\(true\);/, 'the entrance fallback must not replay over a visible popup');
assert.match(source, /poc-closing/, 'native dismissal must trigger the web close transition');
assert.match(styles, /\.popover\.shown\.closing[\s\S]*translate3d\(0, 16px, 0\)/, 'dismissal must animate toward the taskbar-facing edge');
assert.match(source, /set_popup_motion_reduced/, 'the effective motion preference must reach the native popup animator');

// 10. Reset notifications belong to their own provider filter, independent from dashboard
// visibility. These assertions protect the settings markup and collection path from regressing
// back to the old threshold-trigger dropdown.
assert.match(markup, /id="notification-provider-picker"/, 'settings need a notification provider picker');
assert.match(markup, /Notify when quotas reset/, 'settings need the reset notification toggle');
assert.doesNotMatch(markup, /data-field="notificationTrigger"/, 'threshold trigger must not remain visible');
assert.match(source, /notificationProviderIds/, 'settings must persist notification provider IDs');
assert.match(source, /data-notification-provider/, 'settings must collect individual notification providers');
assert.match(source, /data-notification-select-all/, 'notification provider picker needs select-all');
assert.doesNotMatch(source, /lastExpiredLimitRefreshAt/, 'popup must not own expired-reset refresh polling');
assert.match(markup, /class="select-menu overlay-surface notification-provider-menu"/, 'notification menu must use the animated overlay surface');
assert.match(source, /provider\.available === true/, 'notification picker must require confirmed provider availability');
assert.doesNotMatch(source, /menu-check|notification-provider-check|✓/, 'picker and metric menus must not render checkmarks');
assert.deepEqual(
  eligibleNotificationProviders([
    { id: 'codex', available: true },
    { id: 'claude-code', available: false },
    { id: 'cursor' },
  ]).map(provider => provider.id),
  ['codex'],
  'notification picker must exclude unavailable or unknown providers');
assert.match(source, /ArrowDown.*ArrowUp.*Home.*End/, 'notification picker needs keyboard navigation');
assert.match(styles, /notification-provider-menu::?-webkit-scrollbar/, 'notification picker needs custom scrollbar styling');

console.log('popup render and breakdown navigation self-check: all assertions passed');
