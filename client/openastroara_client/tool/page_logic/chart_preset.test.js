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

// A toggle another device changed reaches the open page live (#1644).
function livePage(chart) {
  const calls = { cats: [], renders: 0 };
  const p = loadPageFunctions(['applyDisplayPrefs', 'applyChartPreset', 'setLayer'], {
    CHART: chart,
    stel: { core: core() },
    catToggle: (id, on, skipPost) => calls.cats.push([id, on, skipPost]),
    renderDisplayPanel: () => { calls.renders++; },
  });
  return { p, calls };
}

test('a toggle pushed from another device keeps the Simple Mode chart dark', () => {
  const { p, calls } = livePage(true);
  p.applyChartPreset(p.stel.core); // as the page booted
  p.applyDisplayPrefs({ atmosphere: true, landscape: true, dss: true, constellationArt: true,
    azimuthalGrid: true, dsos: false, 'cat:messier': true }, false);
  const c = p.stel.core;
  assert.equal(c.atmosphere.visible, false);
  assert.equal(c.landscapes.visible, false);
  assert.equal(c.dss.visible, false);
  assert.equal(c.constellations.images_visible, false);
  assert.equal(c.lines.azimuthal.visible, false);
  assert.equal(c.dsos.visible, true);
  assert.deepEqual(calls.cats, [['messier', true, true]]); // the rings still follow
});

test('a toggle pushed from another device reaches the Full UI sky', () => {
  const { p, calls } = livePage(false);
  p.applyDisplayPrefs({ atmosphere: false, dss: true, 'cat:messier': false }, false);
  const c = p.stel.core;
  assert.equal(c.atmosphere.visible, false);
  assert.equal(c.dss.visible, true);
  assert.deepEqual(calls.cats, [['messier', false, true]]);
  assert.equal(calls.renders, 1);
});
