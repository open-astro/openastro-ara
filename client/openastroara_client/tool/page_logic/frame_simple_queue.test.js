// Simple Mode (?bare=1) framing reaches the queue only through the shell's
// own Add to queue (#1899): the page posts its framing to Flutter as the Full
// UI does, and the card has no Add to queue of its own (the card's button
// would build a Full UI run: ADR 0010 rule 2).
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions, pageSource } = require('./load_page.js');

function el() {
  return { style: {}, classList: { toggle() {}, add() {}, remove() {} } };
}

function page(chart) {
  const posts = [], timers = [];
  const p = loadPageFunctions(
    ['setFrameTarget', 'toggleFrame', 'updateFrame', 'postFraming', 'framingPayload', 'cleanName', 'addFrameToSequence'],
    {
      CHART: chart, API: 'http://daemon', D2R: Math.PI / 180,
      frameOn: true, frameRotDeg: 55, frameRaDeg: 10.685, frameDecDeg: 41.269, frameTargetName: 'M 31',
      frameCols: 1, frameRows: 1, frameOverlap: 10, frameCatPaDeg: 35,
      optics: { ok: false }, _frameGeo: {}, _savedTimeSpeed: null, _framingPosted: '', _framingTimer: null,
      document: { getElementById: () => el() },
      renderFramePanel() {}, mosaicData() { return {}; }, araHeaders: (h) => h,
      setTimeout(f) { timers.push(f); return timers.length; },
      fetch(url, init) { posts.push(JSON.parse(init.body)); return Promise.resolve({ ok: true }); },
    },
  );
  const flush = () => { while (timers.length) timers.shift()(); };
  return { p, posts, flush };
}

test('Simple Mode posts its framing to Flutter: on, angle and name', () => {
  const { p, posts, flush } = page(true);
  p.updateFrame();
  flush();
  assert.equal(posts.length, 1);
  assert.deepEqual(posts[0], { type: 'framing', on: true, raDeg: 10.685, decDeg: 41.269, rotationDeg: 55, name: 'M 31' });
});

test('Simple Mode: a new Target posts Framing off', () => {
  const { p, posts, flush } = page(true);
  p.setFrameTarget(83.822, -5.391, 'M 42');
  flush();
  assert.equal(posts.length, 1);
  assert.equal(posts[0].type, 'framing');
  assert.equal(posts[0].on, false);
  assert.equal(posts[0].name, 'M 42');
});

test('Simple Mode: the card has no Add to queue and posts no addToSequence', () => {
  const button = pageSource().match(/<button id="f-add"[^>]*>/)[0];
  assert.match(button, /class="[^"]*\bfull-only\b/);
  const { p, posts, flush } = page(true);
  p.addFrameToSequence();
  flush();
  assert.deepEqual(posts, []);
});

test('Full UI: Create Run still posts addToSequence', () => {
  const { p, posts } = page(false);
  p.addFrameToSequence();
  assert.equal(posts.length, 1);
  assert.equal(posts[0].type, 'addToSequence');
  assert.equal(posts[0].name, 'M 31');
});
