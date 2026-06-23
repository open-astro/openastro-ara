# macOS CEF multi-process helper (`openastroara Helper.app`)

The §36 Planning **Aladin** atlas renders through `webview_cef` (Chromium/CEF
130). On macOS CEF runs **multi-process**: the renderer/GPU/utility work happens
in a nested `openastroara Helper.app` subprocess, not in the main app. This is
the stable, default Chromium model — single-process is unstable for long-running
WebGL/font workloads (Chromium 130's Rust `fontations` backend segfaults), which
is what crashed the app before this was wired in.

## What's committed

- **Submodule** `packages/webview_cef` is pinned to the fork's multi-process
  build (helper-bundle model + `in-process-gpu` + the `fontations` workaround).
- **`macos/Runner.xcodeproj`** has a `Helper` target (`openastroara Helper`) +
  an *Embed CEF Helper* copy-files phase, injected by the plugin's
  `add_helper_target.rb`.
- **`Runner/{DebugProfile,Release}.entitlements`** carry the V8 JIT /
  framework-loading entitlements CEF needs on the host.
- **`Runner/Helper.entitlements`** (this app, not the plugin default) is the
  **sandboxed** helper variant: `app-sandbox` + `inherit` so the helper joins the
  host's sandbox container (a sandboxed host can't launch a non-sandboxed nested
  helper), plus the JIT entitlements. The `Helper` target's
  `CODE_SIGN_ENTITLEMENTS` points here, not at the plugin's non-sandboxed
  `helper/helper.entitlements`.

## Regenerating (only if the plugin's helper tooling changes)

The wiring is committed, so a normal `flutter build macos` needs nothing extra
beyond `packages/webview_cef/macos/setup_cef.sh` (downloads the git-ignored CEF
binaries). Only re-run the injector if the plugin's helper target changes:

The injector **re-points the Helper target at the plugin's non-sandboxed
`helper.entitlements`**, which would drop `app-sandbox`+`inherit` and make macOS
refuse to launch the helper from this sandboxed host. So always run it together
with the repoint below as a single block — never the injector alone:

```sh
gem install xcodeproj   # once
# from client/openastroara_client:
ruby packages/webview_cef/macos/webview_cef/helper/add_helper_target.rb \
  macos/Runner.xcodeproj openastroara ../packages/webview_cef/macos/webview_cef

# REQUIRED follow-up: re-point the sandboxed Helper entitlements (the injector
# resets these to the plugin's non-sandboxed default each run).
ruby -e 'require "xcodeproj"; p=Xcodeproj::Project.open("macos/Runner.xcodeproj"); \
  t=p.targets.find{|t|t.name=="Helper"}; \
  t.build_configurations.each{|c| c.build_settings["CODE_SIGN_ENTITLEMENTS"]="Runner/Helper.entitlements"}; \
  p.save; puts "repointed Helper -> Runner/Helper.entitlements"'
```

The script regenerates target UUIDs each run, so commit the result and don't put
it in a `git diff --exit-code` CI check (the output isn't byte-stable).

## Distribution note

`disable-library-validation` (required to load the separately-signed CEF
framework) + the App Sandbox is **incompatible with Mac App Store** distribution
— fine for Developer-ID / direct distribution, which is how CEF apps ship.

## Security posture (host entitlement tradeoff)

Hosting CEF weakens the host app's Hardened Runtime, by design and unavoidably:

- **`cs.allow-jit`** — narrow: permits JIT-compiled pages (V8). Required.
- **`cs.allow-unsigned-executable-memory`** — broader than `allow-jit`: permits
  *any* writable+executable mapping, not just JIT regions. CEF's own host
  template (`packages/webview_cef/macos/webview_cef/helper/app.entitlements`)
  prescribes it because the embedded Chromium allocates W+X memory outside the
  JIT path; dropping it aborts at runtime. It is the standard, documented cost of
  in-process CEF hosting.
- **`cs.disable-library-validation`** — lets the host load the separately-signed
  CEF framework (see Distribution note).

Net: with these three, the host's Hardened Runtime offers little exploit
mitigation. We accept this because (a) CEF requires all three to run, and (b) the
attack surface is contained — the renderer/GPU/utility work runs in the
**sandboxed** `Helper.app` subprocess (`app-sandbox` + `inherit`), not the host.
This is the same posture every Electron/CEF desktop app ships with.

`files.user-selected.read-write` on **both** `DebugProfile` and `Release` is
**pre-existing** (§54 save-downloads / open-save dialogs) — it is *not* added by
the CEF work; this PR only re-orders the keys and adds the three `cs.*`
entitlements above. The two files are kept byte-identical so Debug and Release
share one audited entitlement set.
