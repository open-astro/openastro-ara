# §36 Planetarium — Stellarium Web Engine (vendored)

OpenAstro Ara's Planning planetarium embeds the **Stellarium Web Engine**, a
WebGL/WASM planetarium renderer by Stellarium Labs SRL.

- **Upstream:** https://github.com/Stellarium/stellarium-web-engine
- **Licence:** GNU AGPL v3 — see [`LICENSE-AGPL-3.0.txt`](LICENSE-AGPL-3.0.txt).
  The engine runs locally inside the app's CEF webview (no network service). The
  page (`index.html`) is **self-driven**: it loads with the observer site + the
  daemon API base in the URL query and drives the engine in-page (on-screen
  controls, object selection/GoTo) — there is no Dart→page JS bridge. The
  complete corresponding source of the engine is the upstream repository above;
  **we make no modifications to the engine C/JS source** — only the two
  build-config flags below.

## How the vendored artifacts were built

`stellarium-web-engine.js` + `stellarium-web-engine.wasm` were built from a clean
checkout of the upstream repo in the official emscripten container, with two
adjustments to the build invocation (`SConstruct`), no source changes:

```sh
git clone --depth 1 https://github.com/Stellarium/stellarium-web-engine.git
cd stellarium-web-engine
# 1. Export _malloc/_free as compiled functions (see below).
#    In SConstruct, set:  '-s', '"EXPORTED_FUNCTIONS=[\'_free\',\'_malloc\']"'
docker run --rm -v "$PWD":/src -w /src emscripten/emsdk:3.1.51 \
  bash -lc "pip3 install scons && emscons scons -j8 mode=release werror=0"
# → build/stellarium-web-engine.js, build/stellarium-web-engine.wasm
```

Two build-config deviations from upstream's default `make js`, **both build
flags — no source files are changed:**

1. **`EXPORTED_FUNCTIONS=['_free','_malloc']`.** Upstream's `SConstruct` lists
   `_free`/`_malloc` under `EXTRA_EXPORTED_RUNTIME_METHODS` with
   `EXPORTED_FUNCTIONS=[]`. On the emscripten this is built with (3.1.51),
   `_malloc`/`_free` are **compiled functions, not runtime methods**, so that
   leaves `Module._free` undefined. The engine's JS bindings (`obj.js`) call
   `Module._free` to release every C string returned across the boundary, so the
   first attribute read (`core.observer`) throws — swallowed at the WASM init
   boundary — and `onReady` never fires (blank planetarium: no stars/DSO load,
   `atmosphere`/`landscape` defaults never get turned off). Moving them into
   `EXPORTED_FUNCTIONS` fixes it.
2. **`werror=0`.** Modern emscripten/clang promotes some warnings in the
   vendored 2022-era C deps (e.g. K&R-style `zlib` prototypes) to errors.

## Sky data (`skydata/`)

The offline sky catalogues (stars, deep-sky objects, planets/Moon/Sun, the
`guereins` horizon landscape, satellites, milky way, and the `western` sky
culture + constellation art) are the engine's `test-skydata` set, served to the
webview by `StellariumServer` so the planetarium works with no internet. The
non-`western` sky cultures were trimmed to keep the bundle small.
