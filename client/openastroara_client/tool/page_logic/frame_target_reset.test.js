// Simple Mode (?bare=1) framing (#1899, decision B): picking another Target sets
// Framing Off and the angle to 0 degrees; the same Target keeps both. The Full
// UI keeps the box and angle across targets.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

function page(chart) {
  const calls = [];
  const p = loadPageFunctions(['setFrameTarget'], {
    CHART: chart, frameOn: true, frameRotDeg: 135, frameRaDeg: 10, frameDecDeg: 41, frameTargetName: 'M 31',
    toggleFrame(on) { calls.push(['toggleFrame', on]); p.frameOn = on; },
    updateFrame() { calls.push(['updateFrame']); },
    renderFramePanel() { calls.push(['renderFramePanel']); },
  });
  return { p, calls };
}

test('Simple Mode: a new Target sets Framing off and the angle to 0', () => {
  const { p, calls } = page(true);
  p.setFrameTarget(83.8, -5.4, 'M 42');
  assert.equal(p.frameOn, false);
  assert.equal(p.frameRotDeg, 0);
  assert.equal(p.frameTargetName, 'M 42');
  assert.equal(p.frameRaDeg, 83.8);
  assert.deepEqual(calls[0], ['toggleFrame', false]);
});

test('Simple Mode: a new Target resets an angle left dialled while Framing is off', () => {
  const { p } = page(true);
  p.frameOn = false;
  p.setFrameTarget(83.8, -5.4, 'M 42');
  assert.equal(p.frameRotDeg, 0);
  assert.equal(p.frameOn, false);
});

test('Simple Mode: the same Target keeps Framing on and the angle', () => {
  const { p } = page(true);
  p.setFrameTarget(10.7, 41.3, 'M 31');
  assert.equal(p.frameOn, true);
  assert.equal(p.frameRotDeg, 135);
  assert.equal(p.frameRaDeg, 10.7);
});

test('Full UI: a new Target keeps Framing on and the angle', () => {
  const { p } = page(false);
  p.setFrameTarget(83.8, -5.4, 'M 42');
  assert.equal(p.frameOn, true);
  assert.equal(p.frameRotDeg, 135);
  assert.equal(p.frameTargetName, 'M 42');
});
