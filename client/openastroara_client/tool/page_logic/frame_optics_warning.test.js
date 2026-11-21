// Simple Mode (?bare=1) framing card (#1899): with no camera optics in the
// profile there is no box, and the card says why. The Full UI's Create Run
// hints stay off the Simple card.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions, pageSource } = require('./load_page.js');

const WARNING = 'Set your camera + focal length in Options → Imaging → Optics.';

function el() {
  const cls = new Set();
  return {
    style: {}, setAttribute() {},
    classList: {
      add: (c) => cls.add(c), remove: (c) => cls.delete(c),
      toggle: (c, on) => (on ? cls.add(c) : cls.delete(c)), contains: (c) => cls.has(c),
    },
  };
}

function render(chart, optics) {
  const els = {};
  const p = loadPageFunctions(['renderFramePanel'], {
    CHART: chart, API: 'http://daemon', optics,
    frameRotDeg: 0, frameCols: 1, frameRows: 1, frameOverlap: 10, frameOn: true,
    frameRaDeg: 10.685, frameCatPaDeg: null,
    document: { getElementById: (id) => (els[id] = els[id] || el()), querySelector: () => null },
    fmtFovDeg: (x) => x.toFixed(2) + '°', snapEnabled: () => false, setFrameCoordText() {},
  });
  p.renderFramePanel();
  return els['f-msg'];
}

test('Simple Mode: the card says why no box shows, and gives no Create Run hint', () => {
  const line = pageSource().match(/<div id="f-msg"[^>]*>/)[0];
  assert.doesNotMatch(line, /full-only/);
  const noOptics = render(true, { ok: false });
  assert.equal(noOptics.textContent, WARNING);
  assert.equal(noOptics.classList.contains('warn'), true);
  assert.ok(!noOptics.hidden);
  assert.equal(render(true, { ok: true, fovWDeg: 2, fovHDeg: 1.3 }).hidden, true);
});

test('Full UI: the card keeps its Create Run hint', () => {
  const msg = render(false, { ok: true, fovWDeg: 2, fovHDeg: 1.3 });
  assert.equal(msg.textContent, 'Drag the sky to position the frame, then “Create Run”.');
  assert.ok(!msg.hidden);
});
