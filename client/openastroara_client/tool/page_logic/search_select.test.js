// A search in Simple Mode's sky view (#1713): a goto alone only centres the
// object, so the page must select it for its card to open.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { loadPageFunctions } = require('./load_page.js');

function page(known) {
  const selected = [];
  const sandbox = loadPageFunctions(['selectForGoto'], {
    resolveQuery: (q) => (known.includes(q) ? { q } : null),
    selectObject: (o) => selected.push(o.q),
  });
  return { sandbox, selected };
}

test('a search goto selects the object by its designation', () => {
  const p = page(['M31']);
  assert.equal(p.sandbox.selectForGoto({ select: true, id: 'M31', name: 'Andromeda Galaxy' }), true);
  assert.deepEqual(p.selected, ['M31']);
});

test('it falls back to the name when the designation is unknown', () => {
  const p = page(['Andromeda Galaxy']);
  assert.equal(p.sandbox.selectForGoto({ select: true, id: 'X', name: 'Andromeda Galaxy' }), true);
  assert.deepEqual(p.selected, ['Andromeda Galaxy']);
});

test('a goto that does not ask to select only centres', () => {
  const p = page(['M31']);
  assert.equal(p.sandbox.selectForGoto({ id: 'M31', name: 'M31' }), false);
  assert.deepEqual(p.selected, []);
});

test('an object the sky engine does not know falls through to centring', () => {
  const p = page([]);
  assert.equal(p.sandbox.selectForGoto({ select: true, id: 'Arp 1', name: 'Arp 1' }), false);
});
