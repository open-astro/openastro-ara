# macOS CEF multi-process helpers (`openastroara Helper*.app`)

The §36 Planning **Aladin** atlas renders through `webview_cef` (Chromium/CEF
149). On macOS CEF runs **multi-process**, the same model as Linux/Windows: the
renderer, GPU, and utility work each run in a nested `openastroara Helper*.app`
subprocess, not in the main app. This is the stable, default Chromium model —
single-process is unstable for long-running WebGL/font workloads (Chromium's
Rust `fontations` backend segfaults), which is what crashed the app before this
was wired in.

Chromium-on-macOS requires the **full set** of helper bundles, not one:
`openastroara Helper.app` (base) plus the typed `Helper (GPU)`, `Helper
(Renderer)`, `Helper (Plugin)`, and `Helper (Alerts)` siblings, all in
`Contents/Frameworks/`. CEF derives the typed child paths from the base helper
that `browser_subprocess_path` points at, so a missing sibling fails that child's
launch — most visibly the GPU process aborting with `gpu_process_host`
`error_code=1003` (the bug an earlier single-helper build hit).

## What's committed

- **Submodule** `packages/webview_cef` is pinned to the fork's CEF 149
  multi-process build (full helper-bundle set + the `fontations` workaround).
- **`macos/Runner.xcodeproj`** has the **five** helper targets (`Helper`,
  `Helper_gpu`, `Helper_renderer`, `Helper_plugin`, `Helper_alerts`) building
  `openastroara Helper[ (Variant)].app` + an *Embed CEF Helper* copy-files phase,
  injected by the plugin's `add_helper_target.rb`.
- **`Runner/{DebugProfile,Release}.entitlements`** carry the V8 JIT /
  framework-loading entitlements CEF needs on the host.
- **`Runner/Helper.entitlements`** (this app, not the plugin default) is the
  **non-sandboxed** helper variant: just the JIT / framework-loading entitlements,
  **no** `app-sandbox`/`inherit`. The host runs unsandboxed (see the sandbox note
  below), so there is no container for the helper to join — declaring `app-sandbox`
  on the helper with no host container left its sandbox init invalid and crash-looped
  CEF's network service. **All five** helper targets'
  `CODE_SIGN_ENTITLEMENTS` point here, not at the plugin's
  `helper/helper.entitlements`, so we keep one audited file.

## Regenerating (only if the plugin's helper tooling changes)

The wiring is committed, so a normal `flutter build macos` needs nothing extra
beyond `packages/webview_cef/macos/setup_cef.sh` (downloads the git-ignored CEF
binaries). Only re-run the injector if the plugin's helper target changes:

The injector **re-points every helper target at the plugin's own
`helper.entitlements`**. We keep them pointed at our committed, audited
`Runner/Helper.entitlements` instead, so always run the injector together with the
repoint below as a single block — never the injector alone:

```sh
gem install xcodeproj   # once
# from client/openastroara_client:
ruby packages/webview_cef/macos/webview_cef/helper/add_helper_target.rb \
  macos/Runner.xcodeproj openastroara ../packages/webview_cef/macos/webview_cef

# REQUIRED follow-up: re-point ALL five helper targets' entitlements at our
# committed file (the injector resets these to the plugin's default each run).
ruby -e 'require "xcodeproj"; p=Xcodeproj::Project.open("macos/Runner.xcodeproj"); \
  p.targets.select{|t| t.name =~ /\AHelper(_[a-z]+)?\z/}.each{|t| \
    t.build_configurations.each{|c| c.build_settings["CODE_SIGN_ENTITLEMENTS"]="Runner/Helper.entitlements"}}; \
  p.save; puts "repointed all helpers -> Runner/Helper.entitlements"'
```

The script regenerates target UUIDs each run, so commit the result and don't put
it in a `git diff --exit-code` CI check (the output isn't byte-stable).

## Distribution note

The App Sandbox is **off** and `disable-library-validation` is on (to load the
separately-signed CEF framework) — both are **incompatible with the Mac App
Store**, so distribution is **Developer-ID / direct only**, permanently. This is
how CEF/Electron desktop apps ship. Notarization (Developer-ID) still applies and
is unaffected by the sandbox being off.

## Security posture (host entitlement tradeoff)

Hosting CEF weakens the host app's Hardened Runtime, by design and unavoidably:

- **`cs.allow-jit`** — narrow: permits the V8 JIT's W+X mapping. The renderer
  helper runs V8; required there. Kept on the host too (harmless over-provision).
- **`cs.allow-unsigned-executable-memory`** — broader than `allow-jit`: permits
  *any* writable+executable mapping, not just the V8 JIT region. The **GPU helper**
  specifically needs it: this build routes WebGL through software **SwiftShader**
  (`common/webview_app.cc` appends `disable-gpu` + `enable-unsafe-swiftshader`),
  and SwiftShader's Reactor backend JITs shader programs into W+X memory at runtime,
  *outside* the V8 JIT path `allow-jit` covers — so the process running SwiftShader
  aborts on first WebGL init (i.e. as soon as Aladin renders) without this key.
  Because the same `Runner/Helper.entitlements` is shared by all five helper
  variants (and the host carries it too), every process that could JIT is covered.
  The plugin's own template
  (`packages/webview_cef/macos/webview_cef/helper/app.entitlements`) prescribes it
  for the same reason.
- **`cs.disable-library-validation`** — lets the host (and helpers) load the
  separately-signed CEF framework (see Distribution note).

### macOS App Sandbox is OFF (host + helper)

CEF 149 runs multi-process: the browser process registers a PID-suffixed **global**
Mach bootstrap name (`…MachPortRendezvousServer.<pid>`) for the helper rendezvous.
Under the App Sandbox `bootstrap_check_in` denies that name ("Permission denied
(1100)"), aborting `CefInitialize` (FATAL in `mach_port_rendezvous_mac.cc`). The
name is PID-suffixed, so a static `temporary-exception.mach-register.global-name`
can't cover it — the sandbox has to be **off** on the host, and therefore on the
helper too. The host CI test (`test/macos/cef_helper_entitlements_test.dart`) pins
`app-sandbox=false` so re-enabling it fails loudly rather than shipping a crash.

### No Chromium renderer sandbox either

`webview_cef` initializes CEF with `CefSettings.no_sandbox = true`
(`common/webview_plugin.cc`), so Chromium's **own** renderer/seatbelt sandbox is
also disabled — this is a plugin default, not changed by this PR. So the webview
runs with **neither** the macOS App Sandbox **nor** Chromium's internal sandbox.
We accept this because (a) CEF + software SwiftShader require the JIT/exec-memory
entitlements regardless, and (b) the attack surface is narrow: the atlas loads a
**fixed, bundled, offline** Aladin page (our own `file://` bootstrap, pinned engine
— no arbitrary browsing); the only network fetches are read-only CDS HiPS image
tiles. This is the same unsandboxed posture most Electron/CEF desktop apps ship
with. (Enabling Chromium's macOS sandbox would mean linking `cef_sandbox` + a
sandboxed helper variant — a separate plugin-level effort, tracked as future
hardening.)

`files.user-selected.read-write` on **both** `DebugProfile` and `Release` is
**pre-existing** (§54 save-downloads / open-save dialogs) — not added by the CEF
work. The two files are kept byte-identical so Debug and Release share one audited
entitlement set.
