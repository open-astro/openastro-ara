// Simple Mode (?bare=1) framing card on an upright phone (#1899): the shell
// shows its #1775 deck there instead of the rails, so the card placed for the
// right rail covered the box at the view centre. It docks under the status
// card instead, clear of the view centre, the deck and the bottom row.
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { pageSource } = require('./load_page.js');

// Simple Mode's numbers (lib/screens/simple/simple_shell.dart): the 16 px
// inset, the 56 px strip, the 48 px status line under it; on an upright
// 390 px phone the 192 px deck and the 56 px bottom row, each 8 px apart.
const INSET = 16;
const STATUS_BOTTOM = INSET * 2 + 56 + 48;
const DECK_AND_ROW = INSET + 192 + 8 + 56 + 8;

function mediaMatches(cond, w, h) {
  return cond.split(/\s+and\s+/).every((part) => {
    const m = part.match(/^\((max-width|min-width|max-height|orientation):\s*([a-z0-9]+?)(px)?\)$/);
    if (!m) throw new Error(`unknown media condition ${part}`);
    if (m[1] === 'orientation') return (h >= w ? 'portrait' : 'landscape') === m[2];
    const v = +m[2];
    return m[1] === 'max-width' ? w <= v : m[1] === 'min-width' ? w >= v : h <= v;
  });
}

// The declarations of `.bare #frame` that apply in a w × h view, in source
// order (a later one wins), from the page's style block and its media blocks.
function frameRule(w, h) {
  const css = pageSource().match(/<style>([\s\S]*?)<\/style>/)[1].replace(/\/\*[\s\S]*?\*\//g, '');
  const decls = {};
  (function walk(text) {
    let i = 0;
    for (;;) {
      const open = text.indexOf('{', i);
      if (open < 0) return;
      let depth = 1, close = open;
      while (depth) { close++; depth += text[close] === '{' ? 1 : text[close] === '}' ? -1 : 0; }
      const head = text.slice(i, open).trim(), body = text.slice(open + 1, close);
      if (head.startsWith('@media')) {
        if (mediaMatches(head.slice(6).trim(), w, h)) walk(body);
      } else if (head.split(',').some((s) => s.trim() === '.bare #frame')) {
        for (const d of body.split(';')) {
          const k = d.indexOf(':');
          if (k > 0) decls[d.slice(0, k).trim()] = d.slice(k + 1).trim();
        }
      }
      i = close + 1;
    }
  })(css);
  return decls;
}

// `Npx` or `calc(A% - Bpx)` of the view height, in px.
function px(value, h) {
  const calc = value.match(/^calc\((\d+(?:\.\d+)?)% - (\d+(?:\.\d+)?)px\)$/);
  if (calc) return (h * +calc[1]) / 100 - +calc[2];
  const m = value.match(/^(\d+(?:\.\d+)?)px$/);
  if (!m) throw new Error(`not a px length: ${value}`);
  return +m[1];
}

test('Simple Mode on an upright phone: the framing card docks under the status card, clear of the box', () => {
  const [w, h] = [390, 844];
  const r = frameRule(w, h);
  const bottom = px(r.top, h) + px(r['max-height'], h);
  assert.ok(bottom <= h / 2 - INSET, `the card can reach y ${bottom}, over the view centre at y ${h / 2}`);
  assert.ok(bottom <= h - DECK_AND_ROW, `the card can reach y ${bottom}, under the deck and the bottom row`);
  assert.ok(px(r.top, h) >= STATUS_BOTTOM, `the card starts at y ${r.top}, over the status card`);
  assert.deepEqual([r.left, r.right, r.width], [`${INSET}px`, `${INSET}px`, 'auto']);
});

test('Simple Mode beside the right rail: the framing card keeps its place', () => {
  for (const [w, h] of [[1280, 800], [844, 390]]) {
    const r = frameRule(w, h);
    assert.deepEqual([r.left, r.right, r.top, r.width], ['auto', '104px', '136px', '242px'], `${w}x${h}`);
  }
});
