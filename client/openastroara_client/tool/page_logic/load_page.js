// Loads named top-level functions out of assets/stellarium/index.html so the
// page's pure logic can run under `node --test` with no browser (#1278).
//
// The page is one classic script, so its functions are plain `function name(`
// declarations. Each requested function is cut out by brace matching and
// evaluated in a vm sandbox whose globals the caller supplies (fetch,
// setTimeout, API, D2R …). Anything the function touches that the caller did
// not stub is a ReferenceError, which is the point: the test states exactly
// what the function depends on.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const PAGE = path.join(__dirname, '..', '..', 'assets', 'stellarium', 'index.html');

function pageSource() {
  // Windows runners check the page out with CRLF (core.autocrlf=true and
  // `* text=auto`), which would hide every `\n}\n` closing line below.
  return fs.readFileSync(PAGE, 'utf8').replace(/\r\n/g, '\n');
}

// Source text of `function <name>(` up to its closing brace, or throws. Page
// functions close with a line that is exactly `}`, so each such line after the
// declaration is tried in turn and the first candidate that parses as a
// complete script wins — a brace scanner would trip on the apostrophes inside
// the page's comments ("hasn't", "daemon's").
function functionSource(html, name) {
  const start = html.indexOf(`\nfunction ${name}(`);
  if (start < 0) throw new Error(`index.html defines no top-level function ${name}`);
  let end = start;
  for (;;) {
    end = html.indexOf('\n}\n', end + 1);
    if (end < 0) throw new Error(`no closing brace found for function ${name}`);
    const candidate = html.slice(start + 1, end + 2);
    try {
      new vm.Script(candidate, { filename: `index.html#${name}` });
      return candidate;
    } catch (e) {
      if (!(e instanceof SyntaxError)) throw e;
    }
  }
}

/// Evaluate [names] into a fresh sandbox built from [globals]; returns the
/// sandbox, so `sandbox.waitForSlewEnd(...)` calls the page's own code and
/// functions that call each other (slewMount → waitForSlewEnd) resolve.
function loadPageFunctions(names, globals) {
  const html = pageSource();
  const sandbox = vm.createContext({ ...globals });
  for (const n of names) vm.runInContext(functionSource(html, n), sandbox, { filename: `index.html#${n}` });
  return sandbox;
}

module.exports = { loadPageFunctions, functionSource, pageSource };
