// The Sky Map at rest (PR #1856): Simple Mode keeps the page loaded while a
// frame shows, so opening it is instant, but hidden it must draw nothing.
// Every animation frame asked for while it rests (the engine's render loop,
// the FOV guard) is held and run when it is shown or woken.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

function sky() {
  const ran = [];
  let now = 0;
  const p = loadPageFunctions(
    ['araSkyResting', 'araSkyFrame', 'araSkyRun', 'araSkyWake', 'araSkySetHidden'],
    { Math },
  );
  const s = { hidden: false, ready: false, awakeUntil: 0, held: [], raf: (cb) => { ran.push(cb); return 1; }, now: () => now };
  return { p, s, ran, at: (t) => { now = t; } };
}

test('shown, every frame runs', () => {
  const { p, s, ran } = sky();
  s.ready = true;
  p.araSkyFrame(s, 'render');
  assert.deepEqual(ran, ['render']);
});

test('hidden before the first sky has drawn, it keeps drawing so the tiles load', () => {
  const { p, s, ran } = sky();
  p.araSkySetHidden(s, true);
  p.araSkyFrame(s, 'render');
  assert.deepEqual(ran, ['render']);
});

test('hidden once drawn, frames are held, and shown again they run at once', () => {
  const { p, s, ran } = sky();
  s.ready = true;
  p.araSkySetHidden(s, true);
  p.araSkyFrame(s, 'render');
  p.araSkyFrame(s, 'guard');
  assert.deepEqual(ran, [], 'nothing drawn while hidden');
  p.araSkySetHidden(s, false);
  assert.deepEqual(ran, ['render', 'guard']);
  assert.equal(s.held.length, 0, "nothing left held");
});

test('a command while hidden wakes it for a while, so a moved view loads its tiles, then it rests', () => {
  const { p, s, ran, at } = sky();
  s.ready = true;
  p.araSkySetHidden(s, true);
  p.araSkyFrame(s, 'render');
  at(1000);
  p.araSkyWake(s, 1500);
  assert.deepEqual(ran, ['render'], 'the held frame runs on wake');
  at(2000);
  p.araSkyFrame(s, 'render2');
  assert.deepEqual(ran, ['render', 'render2'], 'awake: frames run');
  at(2500);
  p.araSkyFrame(s, 'render3');
  assert.deepEqual(ran, ['render', 'render2'], 'the wake is over: it rests');
});
