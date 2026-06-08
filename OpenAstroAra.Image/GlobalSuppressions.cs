// Assembly-level analyzer configuration & suppressions for OpenAstroAra.Image.
//
// This assembly contains the CFITSIO native-interop binding layer (FileFormat/FITS/
// Cfitsio*). Those P/Invoke declarations intentionally mirror the cfitsio C API.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

// CA5392 — pin native library resolution to safe directories for every DllImport in the
// assembly (the cfitsio native bindings) instead of repeating the attribute on each extern.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

// CA1707 — the CfitsioNative P/Invoke layer mirrors the cfitsio C library API (fits_open_file,
// fits_read_key_long, ffgkyj-style entry points, etc.). The underscore names are the established,
// recognizable cfitsio identifiers; renaming them would obscure the mapping to the documented C
// API. CA1707 documents that interop identifiers may keep their native names.
[assembly: SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
    Justification = "CfitsioNative mirrors the cfitsio C API; the underscore identifiers match the documented native function names for recognizability.",
    Scope = "type",
    Target = "~T:OpenAstroAra.Image.FileFormat.FITS.CfitsioNative")]

// CA1838 — the cfitsio string-output P/Invokes use StringBuilder to receive fixed-size C char
// buffers (FITS card/keyword/value/comment), which is the standard managed marshaling for these
// CFITS_API signatures. The perf concern CA1838 flags does not apply to these one-shot header reads.
[assembly: SuppressMessage("Performance", "CA1838:Avoid StringBuilder parameters for P/Invokes",
    Justification = "cfitsio string-output parameters marshal fixed-size C char buffers into StringBuilder; this is the established marshaling for the cfitsio header APIs and is not on a hot path.",
    Scope = "type",
    Target = "~T:OpenAstroAra.Image.FileFormat.FITS.CfitsioNative")]

// CA1724 — the COMPRESSION enum mirrors cfitsio's compression-type constants (RICE_1, GZIP_1, …).
// The clash with the third-party K4os.Compression namespace is incidental; the enum name matches
// the cfitsio C API and is nested under CfitsioNative.
[assembly: SuppressMessage("Naming", "CA1724:Type names should not match namespaces",
    Justification = "COMPRESSION mirrors the cfitsio compression-type constant group; the incidental clash with the K4os.Compression namespace does not warrant renaming the cfitsio API mirror.",
    Scope = "type",
    Target = "~T:OpenAstroAra.Image.FileFormat.FITS.CfitsioNative.COMPRESSION")]

// CA1819 — the image pixel/data buffers (FlatArray, FlatArrayInt, RAWData, the LRGB planes,
// the rendered-image data and the XISF/FITS serialized blocks) are raw contiguous arrays that
// are indexed directly and passed to Buffer.BlockCopy, native interop and the data converters.
// Exposing them as IReadOnlyList<T> would regress performance and break that interop, so CA1819's
// collection alternative is inappropriate for these performance-critical image buffers.
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Raw image pixel buffer; direct array access/Buffer.BlockCopy/interop required.", Scope = "type", Target = "~T:OpenAstroAra.Image.Interfaces.IImageArray")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Raw image pixel buffer; direct array access/Buffer.BlockCopy/interop required.", Scope = "type", Target = "~T:OpenAstroAra.Image.ImageData.ImageArray")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Raw image pixel planes; direct array access/Buffer.BlockCopy/interop required.", Scope = "type", Target = "~T:OpenAstroAra.Image.ImageData.LRGBArrays")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Raw rendered-image data buffer; direct array access/interop required.", Scope = "type", Target = "~T:OpenAstroAra.Image.ImageData.RenderedImage")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Raw rendered-image data buffer; direct array access/interop required.", Scope = "type", Target = "~T:OpenAstroAra.Image.Interfaces.IRenderedImage")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Raw serialized XISF data block; direct array access/Buffer.BlockCopy/interop required.", Scope = "type", Target = "~T:OpenAstroAra.Image.FileFormat.XISF.XISFData")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Raw serialized FITS data block; direct array access/Buffer.BlockCopy/interop required.", Scope = "type", Target = "~T:OpenAstroAra.Image.FileFormat.FITS.FITSData")]
[assembly: SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Raw image pixel buffer; direct array access/Buffer.BlockCopy/interop required.", Scope = "type", Target = "~T:OpenAstroAra.Image.ImageData.ImageArrayInt")]

// CA1707 — the FITS BITPIX_* constants mirror the FITS standard's BITPIX keyword value names
// (BITPIX_BYTE=8, BITPIX_SHORT=16, …); the underscore names match the documented FITS convention.
[assembly: SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
    Justification = "BITPIX_* mirror the FITS standard BITPIX keyword value names.",
    Scope = "type", Target = "~T:OpenAstroAra.Image.FileFormat.FITS.FITS")]

// CA1724 — FITS and XISF are the established public types of their same-named file-format
// namespaces; renaming either the type or the namespace is a breaking change with no benefit.
[assembly: SuppressMessage("Naming", "CA1724:Type names should not match namespaces",
    Justification = "FITS is the established public file-format type of the OpenAstroAra.Image.FileFormat.FITS namespace.",
    Scope = "type", Target = "~T:OpenAstroAra.Image.FileFormat.FITS.FITS")]
[assembly: SuppressMessage("Naming", "CA1724:Type names should not match namespaces",
    Justification = "XISF is the established public file-format type of the OpenAstroAra.Image.FileFormat.XISF namespace.",
    Scope = "type", Target = "~T:OpenAstroAra.Image.FileFormat.XISF.XISF")]


[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "XISFSampleFormat members (UInt16/UInt32/Float32/...) mirror the XISF spec sampleFormat attribute values; the names match the file-format identifiers.", Scope = "type", Target = "~T:OpenAstroAra.Image.FileFormat.XISF.XISFSampleFormat")]
