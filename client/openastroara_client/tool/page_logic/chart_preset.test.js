// applyChartPreset: the Simple Mode dark chart's layers (#1647).
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

const page = loadPageFunctions(['applyChartPreset'], {});

// The Full UI's saved toggles at their worst for the chart: each layer the opposite of what it wants.
const core = () => ({
  atmosphere: { visible: true }, landscapes: { visible: false }, milkyway: { visible: true },
  dss: { visible: true },
  constellations: { images_visible: true, lines_visible: false, labels_visible: false },
  lines: { equatorial: { visible: false }, azimuthal: { visible: true } },
  dsos: { visible: false, hints_visible: false },
});

test('the chart drops the realistic layers and keeps the ground for the horizon, lines, names, grid and deep-sky hints', () => {
  const c = core();
  page.applyChartPreset(c);
  assert.equal(c.atmosphere.visible, false);
  assert.equal(c.landscapes.visible, true);
  assert.equal(c.milkyway.visible, false);
  assert.equal(c.dss.visible, false);
  assert.equal(c.constellations.images_visible, false);
  assert.equal(c.constellations.lines_visible, true);
  assert.equal(c.constellations.labels_visible, true);
  assert.equal(c.lines.equatorial.visible, true);
  assert.equal(c.lines.azimuthal.visible, false);
  assert.equal(c.dsos.visible, true);
  assert.equal(c.dsos.hints_visible, true);
});

test('the chart holds the star scale at the crisp-point end at a wide field', () => {
  const D2R = Math.PI / 180;
  const p = loadPageFunctions(['updateStarScale'],
    { CHART: true, D2R, lastStarScale: -1, stel: { core: { fov: 60 * D2R } } });
  p.updateStarScale();
  assert.equal(p.stel.core.star_linear_scale, 0.35);
  assert.equal(p.stel.core.star_relative_scale, 0.6);
});

test('the Full UI keeps the star-map scale at a wide field', () => {
  const D2R = Math.PI / 180;
  const p = loadPageFunctions(['updateStarScale'],
    { CHART: false, D2R, lastStarScale: -1, stel: { core: { fov: 60 * D2R } } });
  p.updateStarScale();
  assert.equal(p.stel.core.star_linear_scale, 0.8);
  assert.equal(p.stel.core.star_relative_scale, 1.1);
});
