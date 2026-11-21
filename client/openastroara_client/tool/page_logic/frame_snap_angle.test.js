// Simple Mode framing (#1899): "Snap to catalogue angle" turns the box drawn
// on the sky so its long side lies along the object's catalogue position
// angle, and is disabled when Framing is off, when the dial already shows
// that turn, or when the catalogue has none.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

const D2R = Math.PI / 180;
const m31 = { jsonData: { model_data: { angle: 34.914615640785, dimx: 177.8 } } };
const LANDSCAPE = { ok: true, fovWDeg: 2.0, fovHDeg: 1.3 };
const PORTRAIT = { ok: true, fovWDeg: 1.3, fovHDeg: 2.0 };

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

// The ring of the page's own box for M 31 (catalogue angle 35) after Snap.
function snappedBox(optics) {
  const p = loadPageFunctions(['snapDialDeg', 'snapToCatalogueAngle', 'mosaicData', 'tanToRaDec'], {
    D2R, optics, frameCatPaDeg: 35, frameRotDeg: 0, frameCols: 1, frameRows: 1, frameOverlap: 10,
    frameRaDeg: 10.685, frameDecDeg: 41.269, updateFrame() {},
  });
  p.snapToCatalogueAngle();
  return p.mosaicData().features[0].geometry.coordinates[0][0];
}

// Position angle (degrees east of north, mod 180) and length of the sky arc
// between two [raDeg, decDeg] corners.
function arc(a, b) {
  const [ra1, de1, ra2, de2] = [...a, ...b].map((x) => x * D2R);
  const da = ra2 - ra1;
  const pa = Math.atan2(Math.sin(da) * Math.cos(de2),
    Math.cos(de1) * Math.sin(de2) - Math.sin(de1) * Math.cos(de2) * Math.cos(da)) / D2R;
  const len = Math.acos(Math.sin(de1) * Math.sin(de2) + Math.cos(de1) * Math.cos(de2) * Math.cos(da));
  return { pa: ((pa % 180) + 180) % 180, len };
}

function longEdgePa(ring) {
  const [a, b] = [arc(ring[0], ring[1]), arc(ring[1], ring[2])];
  return (a.len >= b.len ? a : b).pa;
}

function offAxis(pa, axis) {
  const d = Math.abs(pa - axis) % 180;
  return Math.min(d, 180 - d);
}

test('Snap lines the box up with M 31 on a landscape sensor: the long side along the catalogue angle', () => {
  const pa = longEdgePa(snappedBox(LANDSCAPE));
  assert.ok(offAxis(pa, 35) <= 1, `long edge ${pa.toFixed(2)} vs catalogue 35`);
});

test('Snap lines the box up with M 31 on a portrait sensor: the long side along the catalogue angle', () => {
  const pa = longEdgePa(snappedBox(PORTRAIT));
  assert.ok(offAxis(pa, 35) <= 1, `long edge ${pa.toFixed(2)} vs catalogue 35`);
});

test('snapToCatalogueAngle turns the dial to the angle that lines the box up, and redraws', () => {
  let redraws = 0;
  const p = loadPageFunctions(['snapDialDeg', 'snapToCatalogueAngle'], {
    optics: LANDSCAPE, frameCatPaDeg: 35, frameRotDeg: 0, updateFrame() { redraws++; },
  });
  p.snapToCatalogueAngle();
  assert.equal(p.frameRotDeg, 55);
  assert.equal(redraws, 1);
  p.optics = PORTRAIT;
  p.snapToCatalogueAngle();
  assert.equal(p.frameRotDeg, 145);
});

test('snapToCatalogueAngle with no catalogue angle leaves the dial alone', () => {
  let redraws = 0;
  const p = loadPageFunctions(['snapDialDeg', 'snapToCatalogueAngle'], {
    optics: LANDSCAPE, frameCatPaDeg: null, frameRotDeg: 90, updateFrame() { redraws++; },
  });
  p.snapToCatalogueAngle();
  assert.equal(p.frameRotDeg, 90);
  assert.equal(redraws, 0);
});

test('snapEnabled: on, a catalogue angle that differs', () => {
  const p = loadPageFunctions(['snapDialDeg', 'snapEnabled'], {
    optics: LANDSCAPE, frameOn: true, frameCatPaDeg: 35, frameRotDeg: 0,
  });
  assert.equal(p.snapEnabled(), true);
});

test('snapEnabled is false when Framing is off, the dial shows the Snap angle, or there is none', () => {
  const p = loadPageFunctions(['snapDialDeg', 'snapEnabled'], {
    optics: LANDSCAPE, frameOn: true, frameCatPaDeg: 35, frameRotDeg: 55,
  });
  assert.equal(p.snapEnabled(), false);
  p.frameRotDeg = 35;   // the raw catalogue angle does not line the box up
  assert.equal(p.snapEnabled(), true);
  p.frameRotDeg = 0; p.frameOn = false;
  assert.equal(p.snapEnabled(), false);
  p.frameOn = true; p.frameCatPaDeg = null;
  assert.equal(p.snapEnabled(), false);
});

test('the Snap label shows the angle Snap turns the dial to', () => {
  const els = {};
  const el = () => ({ style: {}, setAttribute() {}, classList: { add() {}, remove() {}, toggle() {} } });
  const p = loadPageFunctions(['snapDialDeg', 'snapEnabled', 'renderFramePanel'], {
    CHART: true, API: 'http://daemon', optics: LANDSCAPE,
    frameOn: true, frameRotDeg: 0, frameCols: 1, frameRows: 1, frameOverlap: 10,
    frameRaDeg: 10.685, frameCatPaDeg: 35,
    document: { getElementById: (id) => (els[id] = els[id] || el()), querySelector: () => null },
    fmtFovDeg: (x) => x.toFixed(2) + '°', setFrameCoordText() {},
  });
  p.renderFramePanel();
  assert.equal(els['f-snap'].textContent, 'Snap to catalogue angle (55°)');
  assert.equal(els['f-snap'].disabled, false);
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
