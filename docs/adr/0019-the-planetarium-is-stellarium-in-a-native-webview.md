# The planetarium is the Stellarium Web Engine in the platform's native webview

Status: accepted

## Context

Planning runs in the client ([0004](0004-planning-runs-in-the-client.md)), and its main surface is a sky the user can pan, search and frame a target on, with no internet at a dark site. Flutter has no planetarium renderer. The first build embedded Aladin Lite in a Chromium (CEF) browser rendered to a Flutter texture, so one engine covered every desktop. That path froze on macOS under sustained offscreen rendering, and on Linux the CEF start-up broke Flutter's GL context so the app drew nothing.

## Decision

We decided during the port (retired `design/PORT_DECISIONS.md`, entries of 2026-06-26, 2026-06-27 and 2026-06-28, last present at commit `51cba5c40`) on three things.

- **The engine is the Stellarium Web Engine**, a WebGL and WASM planetarium, bundled in the client with its offline sky data under `assets/stellarium/`. Ara leans on what the engine does natively (deep-sky display, search by designation). The page's catalog overlays are answered from catalogs bundled in the client, through the same loopback server. The bundled build carries a small source patch; its complete source is public at the `open-astro/stellarium-web-engine` fork, as the engine's AGPL licence requires.
- **The renderer is the platform's own webview**, through `webview_all`: WKWebView on macOS and iOS, WebView2 on Windows, WebKitGTK on Linux, the system WebView on Android. CEF is gone. On Linux the webview is a native GTK overlay composited over the Flutter view, because a platform view there blanks the whole app.
- **The page is served and driven over loopback HTTP.** `StellariumServer` in the client serves the bridge page, the engine and the sky data from the asset bundle on an ephemeral loopback port. The client sends commands that the page polls (`GET /aracmd`), the page posts events back (`POST /araevent`) and asks for catalogs (`/aracat`), all with a per-run token. Engine state never crosses a webview JavaScript bridge, so any webview that can load a URL works.

## Rules

1. The planetarium is drawn by the bundled Stellarium Web Engine; no second sky renderer is added in Dart. Scope: `client/openastroara_client/lib/widgets/sky_atlas/stellarium_view.dart`, `client/openastroara_client/assets/stellarium/index.html`. Checked by: review (a new chart renderer, or Dart code that repeats a display or search the engine already does).
2. The planetarium loads only through the platform's native webview; no bundled browser engine is added to the client. Scope: `client/openastroara_client/pubspec.yaml`, `client/openastroara_client/lib/widgets/sky_atlas/stellarium_view.dart`, `client/openastroara_client/linux/runner/planetarium_overlay.cc`. Checked by: review (a CEF, Chromium or other browser-engine dependency in `pubspec.yaml`).
3. A command to the engine, an event from the page and a catalog request travel through `StellariumServer`'s loopback endpoints with the per-run token; a webview JavaScript call never reads or changes engine state. Scope: `client/openastroara_client/lib/services/stellarium_server.dart`, `client/openastroara_client/lib/widgets/sky_atlas/stellarium_view.dart`, `client/openastroara_client/lib/services/planetarium_overlay.dart`. Checked by: review (an `addJavaScriptChannel` or `runJavaScript` call that touches the engine or returns a value from the page).
4. The engine, its page and the vector sky data are read from the asset bundle and work with no internet. Scope: `client/openastroara_client/lib/services/stellarium_server.dart`, `client/openastroara_client/pubspec.yaml`. Checked by: `client/openastroara_client/test/services/stellarium_server_test.dart`, and review (a new engine resource fetched from a remote host with no bundled copy).
5. A change to the bundled engine build is published at the public fork before the artifacts change, and `assets/stellarium/README.md` names the commit. Scope: `client/openastroara_client/assets/stellarium/README.md`, `client/openastroara_client/assets/stellarium/stellarium-web-engine.js`, `client/openastroara_client/assets/stellarium/stellarium-web-engine.wasm`. Checked by: review (the README's fork tag and commit match the new artifacts).

## Alternatives rejected

- **Aladin Lite**: the first engine. The decisions log has no entry that says why Stellarium replaced it; the entry of 2026-06-26 lists what Stellarium does natively that Ara had been rebuilding around the atlas (a full deep-sky catalog with hints, search by any designation).
- **A bundled Chromium (CEF)**: one browser on every desktop, rendered to a Flutter texture so Flutter widgets can float over it. It froze on macOS (the same page held 60 fps in Safari), broke Flutter's GL context on Linux, needs a Chromium download and five macOS helper targets, and cannot run on phones.
- **`flutter_inappwebview`**: mature, but has no Linux support.
- **`atomic_webview`**: opens a separate window instead of embedding in the tab.
- **A webview JavaScript bridge for commands and events**: fewer moving parts than a loopback server, but the CEF webview had no working one, some WKWebView setters on macOS abort the controller, and the Linux overlay is not a `webview_all` controller at all.

## Consequences

- One engine and one page on every platform, phones included, with no Chromium in the installer.
- Flutter widgets cannot reliably float over a platform view, so the planetarium's own controls are HTML in the page.
- Linux needs its own native overlay code, which is part of why the Linux client is Wayland only ([0006](0006-linux-client-is-wayland-only.md)).
- The night tint is the one webview JavaScript call: it adds a CSS overlay to the page and touches no engine state. Linux does the same through the overlay channel.
- The client ships an AGPL engine. The client's own AGPL licence is consistent with that; a change to the engine build carries a duty to publish its source.
- Android's WebView starts with JavaScript off, so the controller must turn it on before the load ([#1097](https://github.com/open-astro/openastro-ara/issues/1097)).
- A webview without WebGL2 cannot run the engine.

## How to reverse

Replacing the engine means a new bridge page, new assets and a new command and event set behind `StellariumServer`'s endpoints; the Dart side above the server changes little if the endpoints keep their shape. Going back to a bundled browser means a CEF dependency on every desktop platform, the macOS helper targets and entitlements, and no planetarium on phones. A Flutter-drawn sky is a new renderer and catalog pipeline.

## Links

- Retired `design/PORT_DECISIONS.md` at commit `51cba5c40`: "lean on the Stellarium Web engine's native capabilities" (2026-06-26), "CEF OSR stalls on macOS" (2026-06-27), "CEF fully removed" (2026-06-28)
- `client/openastroara_client/assets/stellarium/README.md` (licence, fork, build recipe)
- [#1550](https://github.com/open-astro/openastro-ara/issues/1550) (the playbook still describes Aladin Lite)
- [Planning runs in the client; the rig runs the night](0004-planning-runs-in-the-client.md), [The Linux desktop client is x86-64 and Wayland only](0006-linux-client-is-wayland-only.md)
