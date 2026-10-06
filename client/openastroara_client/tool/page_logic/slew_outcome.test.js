// GoTo outcome logic (#1198 / #1277 / #1278): slewMount + waitForSlewEnd run
// against a scripted daemon. The daemon answers 202 the moment a slew is
// QUEUED and SlewInBackground is fire-and-forget, so the page must earn its
// "Slewed ✓" from runtime.state, never from the HTTP status.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

// A daemon whose GET /equipment/telescope walks [states] (last value repeats)
// and whose POSTs answer [post]. Timers fire on the microtask queue so a
// 120-tick bound runs in milliseconds.
function daemon({ states, post = { ok: true, status: 202 } }) {
  let i = 0;
  const calls = [];
  const sandbox = loadPageFunctions(['slewMount', 'waitForSlewEnd'], {
    API: 'http://daemon',
    JSON,
    Math,
    setTimeout: (f) => Promise.resolve().then(f),
    fetch: (url, init) => {
      calls.push((init && init.method) || 'GET');
      if (init && init.method === 'POST') {
        if (post instanceof Error) return Promise.reject(post);
        return Promise.resolve(post);
      }
      const state = states[Math.min(i++, states.length - 1)];
      return Promise.resolve({ ok: true, json: () => Promise.resolve({ runtime: { state } }) });
    },
  });
  return { sandbox, calls, polls: () => i };
}

// Drive slewMount the way the GoTo button does; resolve with every label it
// reported, in order (interim ones included).
function goTo(d) {
  return new Promise((resolve) => {
    const labels = [];
    d.sandbox.slewMount(5.5, -5.4, (text, done) => {
      labels.push(text);
      if (done) resolve(labels);
    });
  });
}

test('a slew that is reported and then stops is ✓', async () => {
  const d = daemon({ states: ['slewing', 'slewing', 'tracking'] });
  assert.deepEqual(await goTo(d), ['Slewing…', 'Slewed ✓']);
  assert.deepEqual(d.calls.slice(0, 2), ['POST', 'POST'], 'unpark, then slew');
});

test('a slew the driver rejected never shows slewing and ends on the neutral label', async () => {
  // SlewInBackground logs the driver exception; runtime.state stays tracking.
  const d = daemon({ states: ['tracking'] });
  assert.deepEqual(await goTo(d), ['Slewing…', 'Slew sent']);
  assert.ok(d.polls() >= 4, 'gives the 2 s telescope cache time to catch up first');
});

test('a mount that stays parked (unpark failed) says so, after the same grace', async () => {
  const d = daemon({ states: ['parked'] });
  assert.deepEqual(await goTo(d), ['Slewing…', 'Mount parked']);
  assert.ok(d.polls() >= 4);
});

test('a stale parked read before the real slew does not end the wait early', async () => {
  const d = daemon({ states: ['parked', 'parked', 'slewing', 'tracking'] });
  assert.deepEqual(await goTo(d), ['Slewing…', 'Slewed ✓']);
});

test('a slew that ends parked after moving is a failure', async () => {
  const d = daemon({ states: ['slewing', 'parked'] });
  assert.deepEqual(await goTo(d), ['Slewing…', 'Slew failed']);
});

test('GoTo while an earlier slew is running: the retargeted slew ends as one ✓', async () => {
  // The daemon accepts a slew while slewing (TelescopeService.SlewAsync has no
  // busy check) and the ASCOM driver retargets, so runtime.state stays slewing
  // until the NEW target is reached: the end of slewing is this GoTo's end.
  const d = daemon({ states: ['slewing', 'slewing', 'slewing', 'tracking'] });
  assert.deepEqual(await goTo(d), ['Slewing…', 'Slewed ✓']);
});

test('the poll is bounded: 120 ticks of slewing end on the neutral label', async () => {
  const d = daemon({ states: ['slewing'] });
  assert.deepEqual(await goTo(d), ['Slewing…', 'Slew sent']);
  assert.ok(d.polls() <= 121);
});

test('a failed telescope poll never invents a result', async () => {
  const d = daemon({ states: ['slewing'] });
  d.sandbox.fetch = (url, init) =>
    init && init.method === 'POST' ? Promise.resolve({ ok: true, status: 202 }) : Promise.reject(new Error('offline'));
  assert.deepEqual(await goTo(d), ['Slewing…', 'Slew sent']);
});

test('HTTP outcomes: 500 is No mount, other errors are Slew failed, a network error is No mount', async () => {
  assert.deepEqual(await goTo(daemon({ states: ['idle'], post: { ok: false, status: 500 } })), ['No mount']);
  assert.deepEqual(await goTo(daemon({ states: ['idle'], post: { ok: false, status: 409 } })), ['Slew failed']);
  assert.deepEqual(await goTo(daemon({ states: ['idle'], post: new Error('ECONNREFUSED') })), ['No mount']);
});
