// applyChartPreset: the Simple Mode dark chart's layers (#1647).
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

const page = loadPageFunctions(['applyChartPreset'], {});

// Every layer on, as the Full UI's defaults plus the ones the chart drops.
const core = () => ({
  atmosphere: { visible: true }, landscapes: { visible: true }, milkyway: { visible: true },
  dss: { visible: true },
  constellations: { images_visible: true, lines_visible: false, labels_visible: false },
  lines: { equatorial: { visible: false }, azimuthal: { visible: true } },
  dsos: { visible: false, hints_visible: false },
});

test('the chart drops the realistic layers and keeps lines, names, grid and deep-sky hints', () => {
  const c = core();
  page.applyChartPreset(c);
  assert.equal(c.atmosphere.visible, false);
  assert.equal(c.landscapes.visible, false);
  assert.equal(c.milkyway.visible, false);
  assert.equal(c.dss.visible, false);
  assert.equal(c.constellations.images_visible, false);
  assert.equal(c.constellations.lines_visible, true);
  assert.equal(c.constellations.labels_visible, true);
  assert.equal(c.lines.equatorial.visible, true);
  assert.equal(c.dsos.visible, true);
  assert.equal(c.dsos.hints_visible, true);
});
