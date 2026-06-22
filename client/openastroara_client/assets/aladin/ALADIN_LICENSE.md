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

## Written offer for the corresponding source (GPL v3 §6)

Because `aladin.js` is a **minified** build shipped *inside* the ARA desktop
binary (not fetched at runtime), GPL v3 §6 requires that the complete
corresponding source accompany it. The complete, non-minified corresponding
source for this exact build is the **`v3.6.1` tag** of the upstream repository:

> https://github.com/cds-astro/aladin-lite/releases/tag/v3.6.1
> (clone + `git checkout v3.6.1`)

That source is available there at no charge for as long as this build is
distributed. Anyone who receives the ARA binary may obtain it from that
designated network location. Should that location become unavailable, the Open
Astro project will, on request and for no more than the cost of physically
performing the transfer, provide the complete corresponding source for the
vendored version — this written offer is valid for at least three years from the
date ARA distributes this build.

When updating: replace `aladin.js` with the same-versioned build from the
upstream `api/v3/<version>/` path, bump the version pin in
`lib/widgets/sky_atlas/aladin_view.dart`, and update the tag in the offer above
so the corresponding source stays unambiguous.

The CDS logo + attribution that Aladin Lite renders bottom-right of the view
must remain visible, and the credit line in the repo-root `NOTICE.md` must be
retained.
