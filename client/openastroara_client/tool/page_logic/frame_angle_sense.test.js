// #2150: the framing dial is the camera's up axis in degrees east of north,
// the daemon's meaning (Center and Rotate frame at it). The box drawn on the
// sky, the solved-field box and the run's panel centres must all turn that
// way, or the frame the rig takes is the mirror of the box on the page.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

const D2R = Math.PI / 180;
const LANDSCAPE = { ok: true, fovWDeg: 2.0, fovHDeg: 1.3 };
const PORTRAIT = { ok: true, fovWDeg: 1.3, fovHDeg: 2.0 };
const M31 = { ra: 10.685, dec: 41.269 };
const CENTRE = [M31.ra, M31.dec];

// Bearing (position angle, degrees east of north, 0 to 359) from a to b,
// both [raDeg, decDeg].
function bearing(a, b) {
  const [ra1, de1, ra2, de2] = [...a, ...b].map((x) => x * D2R);
  const da = ra2 - ra1;
  const pa = Math.atan2(Math.sin(da) * Math.cos(de2),
    Math.cos(de1) * Math.sin(de2) - Math.sin(de1) * Math.cos(de2) * Math.cos(da)) / D2R;
  return ((pa % 360) + 360) % 360;
}

function off(pa, want, period) {
  const d = (((pa - want) % period) + period) % period;
  return Math.min(d, period - d);
}

function mid(a, b) {
  return [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
}

// The page's planned box for one 1×1 frame at [dial].
function drawnRing(optics, dial) {
  const p = loadPageFunctions(['mosaicData', 'upAxisTurn', 'tanToRaDec'], {
    D2R, optics, frameRotDeg: dial, frameCols: 1, frameRows: 1, frameOverlap: 10,
    frameRaDeg: M31.ra, frameDecDeg: M31.dec,
  });
  return p.mosaicData().features[0].geometry.coordinates[0][0];
}

// Dial 0 is left out: the mirror of an unturned box is the same box.
for (const dial of [30, 125, 300]) {
  test(`the drawn box's up axis lies at the dial's position angle (dial ${dial})`, () => {
    for (const optics of [LANDSCAPE, PORTRAIT]) {
      const r = drawnRing(optics, dial);
      // Corners run bottom-left, bottom-right, top-right, top-left: the
      // centre to the top edge's middle is the camera's up, the centre to the
      // right edge's middle its width axis.
      const up = bearing(CENTRE, mid(r[2], r[3]));
      assert.ok(off(up, dial, 360) <= 0.5, `up axis at ${up.toFixed(2)}, dial ${dial}`);
      // The long side: the height on a portrait sensor, the width (dial + 90) on a landscape one.
      const portrait = optics.fovHDeg > optics.fovWDeg;
      const along = portrait ? up : bearing(CENTRE, mid(r[1], r[2]));
      const want = portrait ? dial : dial + 90;
      assert.ok(off(along, want, 180) <= 0.5, `long side at ${along.toFixed(2)}, want ${want}`);
    }
  });
}

function scopeData(paDeg) {
  const p = loadPageFunctions(['scopeBoxData', 'upAxisTurn', 'tanToRaDec'], {
    D2R, scopeBox: { ra: M31.ra, dec: M31.dec, paDeg, fovWDeg: 2.0, fovHDeg: 1.3 },
  });
  return p.scopeBoxData().features[0].geometry.coordinates;
}

test('the solved-field box uses the same angle as the planned box', () => {
  const ring = scopeData(30)[0][0];
  const up = bearing(CENTRE, mid(ring[2], ring[3]));
  assert.ok(off(up, 30, 360) <= 0.5, `scope up axis at ${up.toFixed(2)}`);
  // At the dial the camera reports, the two boxes coincide.
  const planned = drawnRing(LANDSCAPE, 30);
  for (let i = 0; i < 5; i++) {
    assert.ok(Math.abs(ring[i][0] - planned[i][0]) < 1e-9 && Math.abs(ring[i][1] - planned[i][1]) < 1e-9,
      `corner ${i} differs`);
  }
});

test("the solved-field box's up tick is on its top edge", () => {
  const [, [tick]] = scopeData(30);
  const towards = bearing(CENTRE, mid(tick[0], tick[2]));
  assert.ok(off(towards, 30, 360) <= 1, `tick lies at ${towards.toFixed(2)} from the centre, up is 30`);
});

test('run panel centres lie along the drawn width axis and match the drawn tiles', async () => {
  let body = null;
  const btn = { disabled: false, textContent: '' };
  const p = loadPageFunctions(['addFrameToSequence', 'mosaicData', 'upAxisTurn', 'tanToRaDec'], {
    D2R, CHART: false, API: 'http://daemon', optics: LANDSCAPE,
    frameRotDeg: 30, frameCols: 2, frameRows: 1, frameOverlap: 10,
    frameRaDeg: M31.ra, frameDecDeg: M31.dec, frameTargetName: 'M 31',
    document: { getElementById: () => btn },
    cleanName: (s) => s, araHeaders: (h) => h, renderFramePanel() {}, setTimeout() {},
    fetch: (url, init) => { body = JSON.parse(init.body); return new Promise(() => {}); },
  });
  p.addFrameToSequence();
  const [a, b] = body.panels.map((c) => [c.raDeg, c.decDeg]);
  const pa = bearing(CENTRE, b);
  assert.ok(off(pa, 30 + 90, 180) <= 0.5, `panels run along ${pa.toFixed(2)}, the width axis is 120`);
  // Each run panel is the centre of the tile drawn for it.
  const tiles = p.mosaicData().features[0].geometry.coordinates;
  [a, b].forEach((c, i) => {
    const r = tiles[i][0];
    const centre = mid(r[0], r[2]);
    assert.ok(Math.abs(centre[0] - c[0]) < 0.02 && Math.abs(centre[1] - c[1]) < 0.02, `panel ${i} off its tile`);
  });
  assert.equal(body.rotationDeg, 30, 'the daemon still gets the dial unchanged');
});
