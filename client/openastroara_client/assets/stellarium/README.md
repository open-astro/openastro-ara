# §36 Planetarium — Stellarium Web Engine (vendored)

OpenAstro Ara's Planning planetarium embeds the **Stellarium Web Engine**, a
WebGL/WASM planetarium renderer by Stellarium Labs SRL.

- **Upstream:** https://github.com/Stellarium/stellarium-web-engine
- **Licence:** GNU AGPL v3 — see [`LICENSE-AGPL-3.0.txt`](LICENSE-AGPL-3.0.txt).
  The engine runs locally inside the app's CEF webview (no network service), and
  OpenAstro Ara communicates with it only across the process boundary via the
  documented JS API (the `window.araStel` bridge in `index.html`). The complete
  corresponding source of the engine is the upstream repository above; **we make
  no modifications to the engine source** — only the build flags below.

## How the vendored artifacts were built

`stellarium-web-engine.js` + `stellarium-web-engine.wasm` were built from a clean
checkout of the upstream repo, unmodified, in the official emscripten container:

```sh
git clone --depth 1 https://github.com/Stellarium/stellarium-web-engine.git
cd stellarium-web-engine
docker run --rm -v "$PWD":/src -w /src emscripten/emsdk:3.1.51 \
  bash -lc "pip3 install scons && emscons scons -j8 mode=release werror=0"
# → build/stellarium-web-engine.js, build/stellarium-web-engine.wasm
```

`werror=0` is the only deviation from the default `make js`: modern emscripten/
clang promotes some warnings in the vendored 2022-era C deps (e.g. K&R-style
`zlib` prototypes, link-only `-s` settings seen during compile) to errors. It is
a build-invocation flag; **no source files are changed.**

## Sky data (`skydata/`)

The offline sky catalogues (stars, deep-sky objects, planets/Moon/Sun, the
`guereins` horizon landscape, satellites, milky way, and the `western` sky
culture + constellation art) are the engine's `test-skydata` set, served to the
webview by `StellariumServer` so the planetarium works with no internet. The
non-`western` sky cultures were trimmed to keep the bundle small.
