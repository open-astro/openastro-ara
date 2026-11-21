// Simple Mode framing (#1899) runs on an iPad: the angle dial and the 1° nudges
// listen to pointer events, so a finger turns the box as a mouse does.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

function el() {
  const on = {};
  return {
    on, style: {},
    addEventListener(type, fn) { (on[type] = on[type] || []).push(fn); },
    fire(type, e) { (on[type] || []).forEach((fn) => fn({ preventDefault() {}, pointerId: 1, ...e })); },
    setPointerCapture() {},
    querySelector() { return el(); },
    // A 100 px dial with its centre at (50, 50).
    getBoundingClientRect() { return { left: 0, top: 0, width: 100, height: 100 }; },
  };
}

function wired() {
  const els = {};
  const p = loadPageFunctions(['wireFrame'], {
    document: { getElementById: (id) => (els[id] = els[id] || el()) },
    frameRotDeg: 0, frameCols: 1, frameRows: 1, frameOverlap: 10, frameOn: false,
    updateFrame() {}, toggleFrame() {}, loadOptics() {},
    addFrameToSequence() {}, openRotateCamera() {}, snapToCatalogueAngle() {},
    setTimeout() { return 1; }, clearTimeout() {}, setInterval() {},
  });
  p.wireFrame();
  return { p, els };
}

test('a finger drag on the dial turns the box and stops on lift', () => {
  const { p, els } = wired();
  const dial = els['f-dial'];
  dial.fire('pointerdown', { clientX: 100, clientY: 50 });   // east of centre
  assert.equal(p.frameRotDeg, 90);
  dial.fire('pointermove', { clientX: 50, clientY: 100 });   // south
  assert.equal(p.frameRotDeg, 180);
  dial.fire('pointerup', {});
  dial.fire('pointermove', { clientX: 0, clientY: 50 });
  assert.equal(p.frameRotDeg, 180);
});

test('a finger on the plus and minus nudges turns the box 1 degree', () => {
  const { p, els } = wired();
  els['f-rot+'].fire('pointerdown', {});
  assert.equal(p.frameRotDeg, 1);
  els['f-rot-'].fire('pointerdown', {});
  els['f-rot-'].fire('pointerdown', {});
  assert.equal(p.frameRotDeg, 359);
});
