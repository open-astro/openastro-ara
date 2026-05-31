# NOTICE

OpenAstro Ara (ARA) is a derivative work of [N.I.N.A. (Nighttime Imaging 'N' Astronomy)](https://github.com/isbeorn/nina) by Stefan Berg and contributors, used under the Mozilla Public License 2.0. Original copyrights are preserved in every source file. ARA is not affiliated with or endorsed by the N.I.N.A. project.

## License

This software is released under the Mozilla Public License, v. 2.0. See `LICENSE.txt` for the full terms.

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

## Trademarks

"N.I.N.A." and the N.I.N.A. logo are property of their respective owners. The OpenAstro Ara project does not use the N.I.N.A. wordmark or logo in any way that would imply endorsement.

"Raspberry Pi" is a trademark of Raspberry Pi Ltd.
