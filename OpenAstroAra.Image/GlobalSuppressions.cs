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
