# §36 Planetarium — Stellarium Web Engine (vendored)

OpenAstro Ara's Planning planetarium embeds the **Stellarium Web Engine**, a
WebGL/WASM planetarium renderer by Stellarium Labs SRL.

- **Upstream:** https://github.com/Stellarium/stellarium-web-engine
- **Licence:** GNU AGPL v3 — see [`LICENSE-AGPL-3.0.txt`](LICENSE-AGPL-3.0.txt).
  The engine runs locally inside the app's webview; the daemon's `StellariumServer`
  serves it to that webview over a loopback HTTP server. The page (`index.html`) is
  **self-driven**: it loads with the observer site + the daemon API base in the URL
  query and drives the engine in-page (on-screen controls, object selection/GoTo) —
  there is no Dart→page JS bridge.
- **This is a MODIFIED build of the engine.** The vendored `.wasm`/`.js` carry a
  small source patch (clickable star pick-areas, a star-survey type-check relax,
  a pre-first-frame font-registration fix and a float-arg hardening) on top of
  upstream, plus four build-config flags. **The complete corresponding source is
  publicly hosted at the AGPL §13 fork:**
  **https://github.com/open-astro/stellarium-web-engine** — branch `master`
  (upstream `525aa40` + the ARA patch commits), pinned by tag
  [`ara-v2`](https://github.com/open-astro/stellarium-web-engine/releases/tag/ara-v2)
  at commit `9d48a78d`, whose ancestry carries all seven patch commits
  (oldest → newest): `cf31725` (SConstruct: both export edits —
  `EXPORTED_FUNCTIONS=['_free','_malloc']` + `withStackSave`),
  `ba905ca` (stars.c: every star selectable), `a867caf` (stars.c: type-less
  survey accepted), `6bfebb5` (stars.c: per-frame pick-area cap),
  `e310ae69` (SConstruct: `STACK_SIZE=5242880`), `c66f13ae` (render_gl.c:
  create the renderer when core_add_font runs before the first frame),
  `9d48a78d` (args.c: deterministic 0 on a bad float arg; SConstruct: make
  `mode=debug` buildable on emcc 3.1.x).
  The same patch is also reproduced in
  [§ Source modifications](#source-modifications) below — apply it to a clean
  upstream checkout and run the build recipe to reproduce the exact artifacts.
  (§13 applies because the daemon conveys the engine to users over the network;
  published 2026-07-01, updated 2026-07-03.)

## How the vendored artifacts were built

`stellarium-web-engine.js` + `stellarium-web-engine.wasm` were built from a clean
checkout of the upstream repo in the official emscripten container, after applying
the source patch in [§ Source modifications](#source-modifications) and three
adjustments to the build invocation (`SConstruct`). The simplest reproduction is
to clone the published fork at the pinned tag, which already carries the patch
and the SConstruct edits as commits:

```sh
git clone --branch ara-v2 https://github.com/open-astro/stellarium-web-engine.git
cd stellarium-web-engine
docker run --rm -v "$PWD":/src -w /src emscripten/emsdk:3.1.51 \
  bash -lc "pip3 install scons && emscons scons -j8 mode=release werror=0"
```

(Equivalently, without docker: install emsdk 3.1.51 + scons locally and run the
same `emscons scons` line — the `ara-v2` artifacts were produced that way on
macOS arm64; the wasm is fully deterministic given the emsdk version.)

Or equivalently, from a clean upstream checkout:

```sh
git clone --depth 1 https://github.com/Stellarium/stellarium-web-engine.git
cd stellarium-web-engine
# Apply the source patch from "Source modifications" below, then:
# 1. Export _malloc/_free as compiled functions (see below).
#    In SConstruct, set:  '-s', '"EXPORTED_FUNCTIONS=[\'_free\',\'_malloc\']"'
docker run --rm -v "$PWD":/src -w /src emscripten/emsdk:3.1.51 \
  bash -lc "pip3 install scons && emscons scons -j8 mode=release werror=0"
# → build/stellarium-web-engine.js, build/stellarium-web-engine.wasm
```

Four build-config deviations from upstream's default `make js`:

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
2. **`withStackSave` added to `EXTRA_EXPORTED_RUNTIME_METHODS`.** The engine's
   async tile-download glue (`request_js.c` → emscripten's `wget2` XHR) calls the
   runtime helper `withStackSave` in its **error** callback (`onerrorjs`) to
   stack-allocate the HTTP `statusText`. Under `-O3` it gets dead-code-eliminated
   unless named here, leaving it referenced-but-undefined. Every 404 tile then
   throws `ReferenceError` so the wasm `onerror` never runs, the in-flight request
   counter never decrements, and after 16 leaked 404s (DSS edge tiles, trimmed
   offline catalog Norder1 misses) **the whole tile loader deadlocks** — DSS never
   paints, constellation art drops the moment you zoom past wide-field, and
   `core.fov` reads `NaN`. Listing it keeps the helper in the build. (emscripten
   3.1.51 prints `invalid item in EXPORTED_RUNTIME_METHODS: withStackSave` — that
   warning is harmless; the symbol is still emitted into the module.)
3. **`werror=0`.** Modern emscripten/clang promotes some warnings in the
   vendored 2022-era C deps (e.g. K&R-style `zlib` prototypes) to errors.
4. **`STACK_SIZE=5242880`** (the `ara-v2` fix). emscripten 3.1.27 dropped the
   default wasm stack from 5 MB (`TOTAL_STACK`) to 64 KB and moved it FIRST in
   linear memory. The engine was written against the old default: deep render
   paths plus the re-entrant JS→wasm attribute calls its per-frame callbacks
   make (obj getters, `ccall` string marshalling via `stackAlloc`) can exceed
   64 KB, and with the stack-first layout the overflow traps as a fatal,
   state-corrupting `memory access out of bounds` mid-frame — after which every
   call into the module faults. In ARA this made the Catalogs overlay (a
   geojson layer with a few hundred octagon features) freeze the whole
   planetarium the moment NGC/IC was toggled on. Restoring the 5 MB stack the
   engine was designed for fixes it (verified: 500-object overlays × 12
   catalogs render fault-free).

## Source modifications

To satisfy AGPL §1 ("complete corresponding source"), the source patch carried by
the vendored binary is reproduced here. The pinned fork tag is authoritative;
the diffs below summarise every change. The patch touches three files:

- **`src/modules/stars.c`** — makes rendered stars selectable by click (upstream
  only registers a hit-area for the few brightest), bounded by a per-frame budget
  so deep dense fields stay responsive, plus relaxes a star-survey `type` check so
  minimal upstream survey manifests load (diff below).
- **`src/render_gl.c`** (`ara-v2`) — `core_add_font` now creates the renderer when
  called before the first frame. The JS `setFont` glue calls in as soon as its
  font fetch resolves, which on a local server beats the first `core_render`;
  `core->rend` was still NULL and nanovg scribbled through the garbage read from
  the bottom of linear memory — random boot-time heap corruption:

  ```diff
  @@ void core_add_font(renderer_t *rend, ...)
  -    rend = rend ?: (void*)core->rend;
  +    if (!rend) {
  +        if (!core->rend)
  +            core->rend = render_create();
  +        rend = (void*)core->rend;
  +    }
  ```
- **`src/args.c`** (`ara-v2`) — `args_vget`'s `TYPE_FLOAT` branch now writes a
  deterministic `0` before its `assert(false)` on non-numeric json (a JS `NaN`
  arrives as json `null`); release builds previously left the caller's output
  double unwritten, storing stack garbage into the attribute being set.

Apply with `git apply` to a clean upstream checkout before building.

```diff
@@ struct stars {
     bool            hints_visible;
+    // Per-frame budget of clickable hit-areas to register (brightest first).
+    // Bounds the cost of making faint stars selectable in deep, dense fields.
+    int             areas_budget;
 };

+// Max clickable star hit-areas registered per frame. Registering one for every
+// rendered star in a deep, dense field (mag-16 Milky Way ≈ tens of thousands)
+// stalls the offscreen renderer; brightest-first keeps the ones you'd aim at.
+#define STARS_MAX_AREAS 4000
+
@@ static int render_visitor(stars_t *stars, survey_t *survey, ...)
         bv_to_rgb(isnan(s->bv) ? 0 : s->bv, color);
+        selected = (&s->obj == core->selection);
+        // Make rendered stars selectable; bound it with a per-frame budget so deep
+        // dense fields don't register tens of thousands of areas and stall the
+        // renderer. The selected star always keeps its area.
+        bool give_area = selected || stars->areas_budget > 0;
+        if (give_area && !selected) stars->areas_budget--;
         points[n] = (point_t) {
             ...
-            // This makes very faint stars not selectable
-            .obj = (luminance > 0.5 && size > 1) ? &s->obj : NULL,
+            .obj = give_area ? &s->obj : NULL,
         };
         n++;
-        selected = (&s->obj == core->selection);
@@ static int stars_render(obj_t *obj, const painter_t *painter_)
     if (!stars->visible) return 0;
+    // Refill the clickable-area budget each frame (surveys/tiles are bright→faint
+    // ordered, so the budget is spent on the brightest stars on screen first).
+    stars->areas_budget = STARS_MAX_AREAS;
@@ static int stars_add_data_source(obj_t *obj, const char *url, const char *key)
     args_type = json_get_attr_s(args, "type");
-    if (!args_type || strcmp(args_type, "stars")) {
+    // A source added via core.stars IS a star survey, so only reject one that
+    // explicitly declares a different type (some published surveys ship a minimal
+    // properties file with no "type" key).
+    if (args_type && strcmp(args_type, "stars")) {
         LOG_W("Source is not a star survey: %s", url);
```

## Sky data (`skydata/`)

The offline sky catalogues (stars, deep-sky objects, planets/Moon/Sun, the
`horizon` landscape, satellites, milky way, and the `western` sky culture +
constellation art) are the engine's `test-skydata` set, served to the webview by
`StellariumServer` so the planetarium works with no internet. The non-`western`
sky cultures were trimmed to keep the bundle small.

The `horizon` landscape is the engine's `guereins` panorama with its tiles
dimmed + desaturated (Pillow, brightness ×0.16, colour ×0.28) so the ground
reads as a clean dark silhouette instead of a bright daytime photo — the
panorama brightness is driven by the engine's global sky-brightness model, not a
runtime tint, so it has to be baked into the images.
