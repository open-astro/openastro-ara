// GoTo during a sequence run (rig night 2026-10-18): the page asks the daemon
// whether a run is in progress BEFORE it unparks, and a 409 run_in_progress
// from the slew itself is reported as such so the button can arm "Slew anyway".
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

function page({ sequences, slewStatus = 202 }) {
  const posts = [];
  const sandbox = loadPageFunctions(['runInProgress', 'slewMount', 'waitForSlewEnd'], {
    API: 'http://daemon',
    JSON,
    Math,
    Date,
    setTimeout: (f) => Promise.resolve().then(f),
    fetch: (url, init) => {
      if (init && init.method === 'POST') {
        posts.push({ url, body: init.body ? JSON.parse(init.body) : null });
        if (url.endsWith('/slew')) return Promise.resolve({ ok: slewStatus < 300, status: slewStatus });
        return Promise.resolve({ ok: true, status: 202 });
      }
      if (url.indexOf('/api/v1/sequences') >= 0) {
        if (sequences instanceof Error) return Promise.reject(sequences);
        if (sequences === null) return Promise.resolve({ ok: false, status: 503 });
        return Promise.resolve({ ok: true, json: () => Promise.resolve({ items: sequences }) });
      }
      return Promise.resolve({ ok: true, json: () => Promise.resolve({ runtime: { state: 'tracking' } }) });
    },
  });
  return { sandbox, posts };
}

const running = (state) => new Promise((resolve) => page({ sequences: [{ id: 'a', current_run_state: state }] }).sandbox.runInProgress(resolve));

test('a run that is starting, running, paused or parked awaiting the user is in progress', async () => {
  for (const state of ['starting', 'running', 'paused', 'pausedawaitinguser']) {
    assert.equal(await running(state), true, state);
  }
});

test('an aborting, finished or idle run is not, and neither is no daemon answer', async () => {
  for (const state of ['aborting', 'completed', 'stopped', 'failed', 'idle', null]) {
    assert.equal(await running(state), false, String(state));
  }
  assert.equal(await new Promise((r) => page({ sequences: [] }).sandbox.runInProgress(r)), false, 'no sequences');
  assert.equal(await new Promise((r) => page({ sequences: null }).sandbox.runInProgress(r)), false, 'list refused');
  assert.equal(await new Promise((r) => page({ sequences: new Error('offline') }).sandbox.runInProgress(r)), false, 'no daemon');
});

test('the daemon\'s own 409 is reported as a run refusal, and force rides only when asked', async () => {
  const refused = page({ sequences: [], slewStatus: 409 });
  const outcome = await new Promise((resolve) => refused.sandbox.slewMount(5.5, -5.4, (text, done, forRun) => resolve({ text, done, forRun })));
  assert.deepEqual(outcome, { text: 'Run in progress', done: true, forRun: true });
  const slew = refused.posts.find((p) => p.url.endsWith('/slew'));
  assert.equal(slew.body.force, false, 'an unforced GoTo sends force:false');

  const forced = page({ sequences: [] });
  await new Promise((resolve) => forced.sandbox.slewMount(5.5, -5.4, (text, done) => { if (done) resolve(); }, true));
  assert.equal(forced.posts.find((p) => p.url.endsWith('/slew')).body.force, true, 'after "Slew anyway" the slew is forced');
});
