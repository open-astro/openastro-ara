# Planetarium page logic tests

`node --test tool/page_logic/*.test.js` runs the pure logic of
`assets/stellarium/index.html` (GoTo outcome polling, coordinate parsing)
without a browser: `load_page.js` cuts named top-level functions out of the
page and evaluates them in a `node:vm` sandbox with the globals each test
supplies. CI runs it in the `Client (analyze + test)` job on all three OSes.
Needs Node 20+; nothing to install. Pass the files, not the directory:
Node 22/24 treat the argument as a pattern and reject a bare directory.

`tool/stellarium_bridge_test/` is the other half — the real engine in headless
Chrome via Docker, for the parts that need WebGL.
