# NOTICE

OpenAstro Ara (ARA) is a derivative work of [N.I.N.A. (Nighttime Imaging 'N' Astronomy)](https://github.com/isbeorn/nina) by Stefan Berg and contributors, used under the Mozilla Public License 2.0. Original copyrights are preserved in every source file. ARA is not affiliated with or endorsed by the N.I.N.A. project.

## License

This repository carries two licenses, split by directory (decided 2026-07-01 —
see `design/PORT_DECISIONS.md`):

- **Server/daemon and everything not listed below — Mozilla Public License,
  v. 2.0.** See `LICENSE.txt` for the full terms. This covers the NINA-derived
  .NET code, whose MPL-2.0 lineage is preserved unchanged.
- **WILMA client (`client/openastroara_client/`) — GNU Affero General Public
  License, v. 3.0 or later.** See `client/openastroara_client/LICENSE`. The
  client is wholly original work written for OpenAstro Ara (no NINA-derived
  files); AGPL's network-interaction clause keeps derived clients open even
  when they are served from a device rather than shipped.

The client and the daemon communicate over REST/WebSocket as separate
programs — neither links the other, so the two licenses apply independently.

## Inherited source

Source files inherited from N.I.N.A. preserve the original `Copyright (c) Stefan Berg <isbeorn@hotmail.com> and the N.I.N.A. Contributors` header. Files modified during the OpenAstro Ara port have a second copyright line appended:

```
Copyright (c) 2026 - present Open Astro contributors
```

Wholly new source files written for OpenAstro Ara carry the Open Astro copyright only.

## Third-party dependencies

See `3rd-party-licenses.txt` (added at first release) for the bundled-dependency license inventory. Notable third-party components used at runtime:

- **CFITSIO** ([heasarc.gsfc.nasa.gov/fitsio](https://heasarc.gsfc.nasa.gov/fitsio)) — FITS file I/O, ISC-style license. Linked via P/Invoke in `OpenAstroAra.Fits`.
- **SkiaSharp** (Mono, MIT) — JPEG encoding for §65 stretch previews.
- **Microsoft.Data.Sqlite** (Microsoft, MIT) — §28 catalog DB.
- **ASCOM.Alpaca.Components / Device / Tools** (ASCOM Initiative, ASCOM license) — equipment control.
- **Zeroconf** (MIT) — mDNS service announcement.
- **Serilog** (Apache-2.0) — structured logging.
- **Accord.NET** (Accord.Math / Accord.Statistics, **LGPL-2.1**) — numerical/imaging algorithms, inherited from N.I.N.A. Distributed as separate, dynamically-linked .NET assemblies (satisfying the LGPL relinking allowance); ARA does not modify Accord.
- **Aladin Lite v3** ([CDS, Strasbourg](https://aladin.cds.unistra.fr/), **GPL v3**) — §36 Sky Atlas rendering. Sky Atlas rendering powered by [Aladin Lite](https://aladin.cds.unistra.fr/) (CDS, Strasbourg) under GPL v3. The engine is bundled with the WILMA desktop client (`client/.../assets/aladin/aladin.js`, v3.6.1) and runs inside a CEF/Chromium WebView process — a separate process boundary communicating with the Dart host only over `executeJavaScript`/`postMessage`, not statically or dynamically linked — which the GPL FAQ permits alongside ARA's MPL 2.0. The CDS logo + attribution Aladin renders in-view is retained. See `client/.../assets/aladin/ALADIN_LICENSE.md` for provenance + source.

## Downloadable sky-data catalogs

The §36 Data Manager can download optional star/deep-sky catalogs for the Sky Atlas overlay. These are **not** bundled with ARA binaries — they are fetched on demand at the user's request — but since 2026-07-01 the org **redistributes unmodified snapshots** of them (from [`open-astro/sky-data`](https://github.com/open-astro/sky-data), commit-pinned, so the downloads survive upstream repo deletion; that repo's README carries the full attribution + provenance pins). Credited here per their share-alike terms:

- **HYG star database** ([Astronomy Nexus / David Nash](https://www.astronexus.com/hyg), **CC BY-SA**) — the named-star catalog (Hipparcos + Yale Bright Star + Gliese). Snapshot byte-identical to the archived upstream GitHub repository's `hygdata_v40.csv.gz`.
- **OpenNGC** ([Mattia Verga](https://github.com/mattiaverga/OpenNGC), **CC BY-SA 4.0**) — the NGC/IC deep-sky object catalog. Snapshot byte-identical to the upstream repository's commit-pinned `NGC.csv`.

## Trademarks

"N.I.N.A." and the N.I.N.A. logo are property of their respective owners. The OpenAstro Ara project does not use the N.I.N.A. wordmark or logo in any way that would imply endorsement.

"Raspberry Pi" is a trademark of Raspberry Pi Ltd.
