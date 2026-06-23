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

```sh
gem install xcodeproj   # once
# from client/openastroara_client:
ruby packages/webview_cef/macos/webview_cef/helper/add_helper_target.rb \
  macos/Runner.xcodeproj openastroara ../packages/webview_cef/macos/webview_cef
```

The script regenerates target UUIDs each run (commit the result; don't put it in
a `git diff --exit-code` CI check) and it **re-points the Helper target at the
plugin's non-sandboxed `helper.entitlements`** — after re-running, set all three
`Helper` configs' `CODE_SIGN_ENTITLEMENTS` back to `Runner/Helper.entitlements`.

## Distribution note

`disable-library-validation` (required to load the separately-signed CEF
framework) + the App Sandbox is **incompatible with Mac App Store** distribution
— fine for Developer-ID / direct distribution, which is how CEF apps ship.
