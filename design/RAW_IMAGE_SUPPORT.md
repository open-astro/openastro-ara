# Camera RAW image support

## Scope

OpenAstro Ara decodes camera RAW files for display and analysis while preserving the
original source bytes for storage and download. RAW conversion is not a post-processing
workflow: no operation writes demosaiced pixels back over the source file.

Supported camera families follow the installed LibRaw 0.21 runtime. The source factory
recognizes common TIFF/DNG, Canon CR3, Fuji RAF, Olympus ORF, Panasonic RW2, and camera-RAW
extension signatures. LibRaw remains the final format authority.

## Architecture

`IRawImageDecoder` is the shared boundary for library files and in-memory camera exposures.
`LibRawDecoder` uses only LibRaw's public C API. It deliberately does not marshal
`libraw_data_t`; that large native structure changes between releases. The managed boundary
uses exported geometry/multiplier accessors, the documented fixed make/model/filter prefix of
`libraw_iparams_t`, and the documented 16-byte `libraw_processed_image_t` header.

Decode policy:

1. Bound source bytes before native work.
2. Identify the file through LibRaw.
3. Read and bound dimensions before unpacking pixels.
4. Install a progress callback backed by the request `CancellationToken`.
5. Unpack with camera white balance, linear gamma, no automatic brightening, 16-bit output,
   sRGB primaries, and AHD demosaicing.
6. Validate processed type, channels, dimensions, bit depth, and exact buffer length before
   reading native memory directly into managed color planes; no duplicate packed buffer is allocated.
7. Produce borrowed immutable-by-contract red, green, and blue planes plus a weighted
   luminance analysis plane.
8. Release processed memory and the LibRaw context on every success, failure, or cancellation.

The preview renderer treats LibRaw output as already demosaiced. RGB, luminance, red, green,
and blue modes use the decoded planes directly. Responses report `libraw_ahd` for Bayer,
`libraw_xtrans` for X-Trans, or `libraw_native_color` for already-color sources even if a client
sends `debayer=false`; the source mosaic cannot be reconstructed from processed color planes.
Library thumbnails use the same RGB planes and luminance-derived automatic stretch as full previews.

Camera exposure conversion preserves original bytes and normalized RAW extension in
`ImageArray.RAWData`/`RAWType`. Its in-memory analysis representation is linear 16-bit
luminance. Library downloads continue serving the untouched on-disk source.

## Native deployment

- Debian/Ubuntu development and CI: `libraw-dev`.
- Debian Trixie `.deb`: hard dependency on `libraw23t64`.
- Docker: Ubuntu Noble runtime with `libraw23t64` and `libcfitsio10t64` installed.
- macOS: Homebrew `libraw`; build targets copy `libraw_r.dylib` beside the daemon.
- Windows daemon: community-supported; provide `libraw.dll` beside the server binary.

The loader prefers the re-entrant LibRaw shared library and requires ABI 0.21 or newer.
Missing or old runtimes produce a typed, actionable `RawDecoderUnavailableException`.

## Verification

Tests generate a deterministic uncompressed DNG fixture in memory. This avoids shipping
third-party camera files while exercising real LibRaw file and buffer entry points. Coverage
includes signature-over-extension selection, RGB fidelity, preview channels, malformed data,
dimension and byte limits, cancellation, capture conversion, original-byte preservation, and
native runtime availability.
