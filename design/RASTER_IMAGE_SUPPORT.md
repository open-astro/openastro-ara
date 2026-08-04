# Raster image support

OpenAstro Ara recognizes image content before trusting file extensions. The source factory routes
classic TIFF, JPEG, PNG, and TIFF-based camera RAW files to separate bounded decoders. A DNG tag or
known camera-RAW extension selects LibRaw; TIFF without either marker uses the raster decoder.

## Import behavior

| Format | Accepted samples | Color | Storage meaning |
|---|---|---|---|
| TIFF | unsigned 8/16-bit; IEEE float32 | grayscale or RGB; contiguous/separate; strips/tiles | source precision retained; float data normalized by one finite image-wide range |
| PNG | standard PNG depths | grayscale/RGB; alpha ignored without premultiplication | JPEG/PNG library imports are preview-only; managed 16-bit decode is sample-exact and supports Adam7 |
| JPEG | 8-bit grayscale/RGB/CMYK accepted by SkiaSharp | display RGB | preview-only and lossy |

TIFF orientation is normalized into top-left row-major planes. JPEG and 8-bit PNG orientation
reported by SkiaSharp is normalized. Unsupported sample types, BigTIFF, animated images, malformed chunks,
truncation, invalid CRCs, and decompression-limit violations fail with a typed decode error.

All readers enforce file-size, dimension, pixel-count, header-size, and decoded-working-set limits
before large pixel allocations. Conversion loops observe cancellation. Source files are read-only;
preview processing and annotation never alter them.

## TIFF export

`TiffImageWriter.WriteGrayscale16` writes exact linear unsigned 16-bit grayscale data using no
compression, LZW, or Adobe Deflate (ZIP). Compressed output uses a horizontal predictor and bounded
strips. Image metadata is serialized as FITS-compatible cards in `ImageDescription`, allowing Ara
to restore capture metadata on reload. Cancellation or write failure removes partial output.

## Test contract

Raster tests cover signature/extension mismatch, JPEG/PNG file and buffer decode, exact 16-bit PNG
filters and Adam7, CRC/trailing-data rejection, TIFF compression round trips, RGB tiles,
separate-planar strips, orientation, float normalization, metadata, malformed/truncated inputs,
allocation limits, cancellation, preview color, endpoint error mapping, and partial-write cleanup.
