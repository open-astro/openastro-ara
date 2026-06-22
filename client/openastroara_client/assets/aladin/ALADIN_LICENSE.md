# Vendored: Aladin Lite v3

`aladin.js` in this directory is the **Aladin Lite v3** sky-atlas renderer
(version **3.6.1**), bundled so the §36 Sky Atlas works without a runtime
dependency on the CDS content-delivery network (per playbook §36.1).

- **Upstream:** https://github.com/cds-astro/aladin-lite
- **Vendored build:** https://aladin.cds.unistra.fr/AladinLite/api/v3/3.6.1/aladin.js
- **Version:** 3.6.1 (pinned; not `/latest/`)
- **License:** GPL v3 — see https://github.com/cds-astro/aladin-lite/blob/master/LICENSE
- **CSS + WebAssembly core** are embedded in this single bundle (no separate
  asset to fetch).

## License compatibility (per §36.11)

ARA ships under MPL 2.0. Aladin Lite (GPL v3) is loaded inside a CEF/Chromium
**WebView process** and communicates with the Dart host only over the
`executeJavaScript` / `postMessage` boundary — it is **not** statically or
dynamically linked into the ARA binary. The GPL FAQ explicitly permits this
separate-process pattern, so the two licenses do not conflict.

The complete corresponding source for this build is the tagged `3.6.1` release
in the upstream repository above. To update, replace `aladin.js` with the
same-versioned build from the upstream `api/v3/<version>/` path and bump the
version pin in `lib/widgets/sky_atlas/aladin_view.dart`.

The CDS logo + attribution that Aladin Lite renders bottom-right of the view
must remain visible, and the credit line in the repo-root `NOTICE.md` must be
retained.
