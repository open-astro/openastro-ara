// parseRaDec: the planetarium search box's coordinate forms (#1278).
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

const page = loadPageFunctions(['parseRaDec'], { Math });
// Results are built inside the vm realm; spread them into this realm so
// deepEqual compares values, not Object prototypes.
const parseRaDec = (q) => { const r = page.parseRaDec(q); return r && { ...r }; };

test('sexagesimal with colons, spaces or h/m/d marks', () => {
  assert.deepEqual(parseRaDec('05:35:17 -05:23:28'), { raDeg: (5 + 35 / 60 + 17 / 3600) * 15, decDeg: -(5 + 23 / 60 + 28 / 3600) });
  assert.deepEqual(parseRaDec('05h35m17 +05d23'), { raDeg: (5 + 35 / 60 + 17 / 3600) * 15, decDeg: 5 + 23 / 60 });
  assert.deepEqual(parseRaDec('20 59 +44 31'), { raDeg: (20 + 59 / 60) * 15, decDeg: 44 + 31 / 60 });
});

test('a negative declination between -1° and 0° keeps its sign', () => {
  assert.equal(parseRaDec('12:00:00 -00:30:00').decDeg, -0.5);
});

test('decimal degrees, and non-coordinates are null', () => {
  assert.deepEqual(parseRaDec('83.82, -5.39'), { raDeg: 83.82, decDeg: -5.39 });
  assert.deepEqual(parseRaDec('83.82 -5.39'), { raDeg: 83.82, decDeg: -5.39 });
  assert.equal(parseRaDec('M 42'), null);
  assert.equal(parseRaDec(''), null);
});
