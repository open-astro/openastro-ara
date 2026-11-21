// Simple Mode framing (#1899): "Snap to catalogue angle" sets the dial to the
// object's catalogue position angle (degrees east of north) and is disabled
// when Framing is off, when the angle already matches, or when the catalogue
// has none.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

const m31 = { jsonData: { model_data: { angle: 34.914615640785, dimx: 177.8 } } };

test('catalogueAngle reads the rounded position angle of a galaxy', () => {
  const p = loadPageFunctions(['catalogueAngle'], {});
  assert.equal(p.catalogueAngle(m31), 35);
});

test('catalogueAngle is null for a star, a missing field and a throwing object', () => {
  const p = loadPageFunctions(['catalogueAngle'], {});
  assert.equal(p.catalogueAngle({ jsonData: { model: 'star' } }), null);
  assert.equal(p.catalogueAngle({ jsonData: { model_data: { angle: 'x' } } }), null);
  assert.equal(p.catalogueAngle({ get jsonData() { throw new Error('gone'); } }), null);
});

test('catalogueAngle wraps into 0 to 359', () => {
  const p = loadPageFunctions(['catalogueAngle'], {});
  assert.equal(p.catalogueAngle({ jsonData: { model_data: { angle: -10 } } }), 350);
  assert.equal(p.catalogueAngle({ jsonData: { model_data: { angle: 360 } } }), 0);
});

test('snapToCatalogueAngle sets the dial to the catalogue angle and redraws', () => {
  let redraws = 0;
  const p = loadPageFunctions(['snapToCatalogueAngle'], {
    frameCatPaDeg: 35, frameRotDeg: 0, updateFrame() { redraws++; },
  });
  p.snapToCatalogueAngle();
  assert.equal(p.frameRotDeg, 35);
  assert.equal(redraws, 1);
});

test('snapToCatalogueAngle with no catalogue angle leaves the dial alone', () => {
  let redraws = 0;
  const p = loadPageFunctions(['snapToCatalogueAngle'], {
    frameCatPaDeg: null, frameRotDeg: 90, updateFrame() { redraws++; },
  });
  p.snapToCatalogueAngle();
  assert.equal(p.frameRotDeg, 90);
  assert.equal(redraws, 0);
});

test('snapEnabled: on, a catalogue angle that differs', () => {
  const p = loadPageFunctions(['snapEnabled'], { frameOn: true, frameCatPaDeg: 35, frameRotDeg: 0 });
  assert.equal(p.snapEnabled(), true);
});

test('snapEnabled is false when Framing is off, the angle matches, or there is none', () => {
  const p = loadPageFunctions(['snapEnabled'], { frameOn: true, frameCatPaDeg: 35, frameRotDeg: 35 });
  assert.equal(p.snapEnabled(), false);
  p.frameRotDeg = 0; p.frameOn = false;
  assert.equal(p.snapEnabled(), false);
  p.frameOn = true; p.frameCatPaDeg = null;
  assert.equal(p.snapEnabled(), false);
});

test('setFrameTarget keeps the catalogue angle of the new Target and clears it without one', () => {
  const p = loadPageFunctions(['setFrameTarget'], {
    CHART: true, frameOn: false, frameRotDeg: 0, frameRaDeg: null, frameDecDeg: null, frameTargetName: '',
    frameCatPaDeg: null, toggleFrame() {}, updateFrame() {}, renderFramePanel() {},
  });
  p.setFrameTarget(10.7, 41.3, 'M 31', 35);
  assert.equal(p.frameCatPaDeg, 35);
  p.setFrameTarget(83.8, -5.4, 'M 42');
  assert.equal(p.frameCatPaDeg, null);
});
