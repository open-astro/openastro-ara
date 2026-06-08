#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Runtime.InteropServices;

// CA5392/CA5393: constrain native library resolution to the OS "safe"
// directories (which include the application directory) and nothing else, so
// CFITSIO can't be hijacked by DLL-planting from the current/working directory.
// SafeDirectories is the value both rules accept (AssemblyDirectory is rejected
// by CA5393 as attacker-influenceable for side-loaded assemblies).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]

namespace OpenAstroAra.Fits;

/// <summary>
/// §72.3 P/Invoke wrappers for CFITSIO. AOT-safe via
/// <c>[LibraryImport]</c> source generators per playbook §71.
///
/// Library name resolution is automatic per-platform:
/// <list type="bullet">
///   <item>Linux: <c>libcfitsio.so</c> → <c>libcfitsio.so.10</c></item>
///   <item>macOS: <c>libcfitsio.dylib</c></item>
///   <item>Windows: <c>cfitsio.dll</c></item>
/// </list>
///
/// Subset surface — only the entry points <c>FitsImage</c> actually
/// uses. Adding new ones is fine; see the CFITSIO reference at
/// <see href="https://heasarc.gsfc.nasa.gov/fitsio/c/c_user/cfitsio.html"/>.
/// </summary>
internal static partial class CFitsIO {
    private const string LibraryName = "cfitsio";

    // ── Status code → string. Used by FitsException for clear error messages. ─────
    [LibraryImport(LibraryName, EntryPoint = "ffgerr")]
    internal static partial void GetErrorStatus(int status, [Out] byte[] errText);

    // ── File lifecycle ──────────────────────────────────────────────────────────────
    [LibraryImport(LibraryName, EntryPoint = "ffinit", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int CreateFile(out IntPtr fptr, string filename, ref int status);

    [LibraryImport(LibraryName, EntryPoint = "ffopen", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int OpenFile(out IntPtr fptr, string filename, int mode, ref int status);

    [LibraryImport(LibraryName, EntryPoint = "ffclos")]
    internal static partial int CloseFile(IntPtr fptr, ref int status);

    // ── Image HDU ───────────────────────────────────────────────────────────────────
    [LibraryImport(LibraryName, EntryPoint = "ffcrim")]
    internal static partial int CreateImage(IntPtr fptr, int bitpix, int naxis,
        [In] long[] naxes, ref int status);

    [LibraryImport(LibraryName, EntryPoint = "ffgidm")]
    internal static partial int GetImageDimensions(IntPtr fptr, out int naxis, ref int status);

    [LibraryImport(LibraryName, EntryPoint = "ffgisz")]
    internal static partial int GetImageSize(IntPtr fptr, int maxdim,
        [Out] long[] naxes, ref int status);

    [LibraryImport(LibraryName, EntryPoint = "ffgidt")]
    internal static partial int GetImageType(IntPtr fptr, out int bitpix, ref int status);

    // ── Pixel I/O ───────────────────────────────────────────────────────────────────
    [LibraryImport(LibraryName, EntryPoint = "ffppx")]
    internal static partial int WritePixels(IntPtr fptr, int datatype,
        [In] long[] firstpix, long nelements, IntPtr buffer, ref int status);

    [LibraryImport(LibraryName, EntryPoint = "ffgpxv")]
    internal static partial int ReadPixels(IntPtr fptr, int datatype,
        [In] long[] firstpix, long nelements, IntPtr nullval, IntPtr buffer,
        out int anynul, ref int status);

    // ── Header cards ────────────────────────────────────────────────────────────────
    [LibraryImport(LibraryName, EntryPoint = "ffuky", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UpdateKey(IntPtr fptr, int datatype, string keyname,
        IntPtr value, string comment, ref int status);

    [LibraryImport(LibraryName, EntryPoint = "ffukys", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int UpdateKeyString(IntPtr fptr, string keyname,
        string value, string comment, ref int status);

    [LibraryImport(LibraryName, EntryPoint = "ffgnxk", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int FindNextKey(IntPtr fptr, [In] string[] inclist, int ninc,
        [In] string[] exclist, int nexc, [Out] byte[] card, ref int status);

    [LibraryImport(LibraryName, EntryPoint = "ffghps")]
    internal static partial int GetHeaderSize(IntPtr fptr, out int keysexist, out int morekeys, ref int status);

    [LibraryImport(LibraryName, EntryPoint = "ffgrec")]
    internal static partial int GetHeaderRecord(IntPtr fptr, int keyNum, [Out] byte[] card, ref int status);

    // ── Datatype constants (see fitsio.h CFITSIO_DATATYPES) ──
    internal const int TBYTE = 11;
    internal const int TSBYTE = 12;
    internal const int TLOGICAL = 14;
    internal const int TSTRING = 16;
    internal const int TUSHORT = 20;
    internal const int TSHORT = 21;
    internal const int TUINT = 30;
    internal const int TINT = 31;
    internal const int TULONG = 40;
    internal const int TLONG = 41;
    internal const int TFLOAT = 42;
    internal const int TLONGLONG = 81;
    internal const int TDOUBLE = 82;

    // ── BITPIX (image type) constants ─────────────────────
    internal const int BYTE_IMG = 8;
    internal const int SHORT_IMG = 16;
    internal const int LONG_IMG = 32;
    internal const int LONGLONG_IMG = 64;
    internal const int FLOAT_IMG = -32;
    internal const int DOUBLE_IMG = -64;
    internal const int USHORT_IMG = 20;  // CFITSIO extension
    internal const int ULONG_IMG = 40;

    // ── Open modes ────────────────────────────────────────
    internal const int READONLY = 0;
    internal const int READWRITE = 1;
}

/// <summary>
/// Thrown when a CFITSIO call returns a non-zero status. Carries
/// the operation name + the status code + the human-readable
/// error string from <c>ffgerr</c>.
/// </summary>
public sealed class FitsException : Exception {
    public string Operation { get; } = string.Empty;
    public int StatusCode { get; }

    public FitsException(string operation, int statusCode)
        : base($"{operation} failed with status {statusCode}: {GetErrorMessage(statusCode)}") {
        Operation = operation;
        StatusCode = statusCode;
    }

    private static string GetErrorMessage(int status) {
        var buffer = new byte[81];  // CFITSIO FLEN_ERRMSG
        CFitsIO.GetErrorStatus(status, buffer);
        var nullTerminator = Array.IndexOf<byte>(buffer, 0);
        var length = nullTerminator >= 0 ? nullTerminator : buffer.Length;
        return System.Text.Encoding.ASCII.GetString(buffer, 0, length);
    }

    public FitsException() {
    }

    public FitsException(string message) : base(message) {
    }

    public FitsException(string message, Exception innerException) : base(message, innerException) {
    }
}

/// <summary>
/// Confirms the CFITSIO library is loadable at startup. Calls a
/// no-op entry point (<c>ffgerr</c> on status 0); if the library
/// can't be found, .NET raises <see cref="DllNotFoundException"/>
/// which the caller surfaces as a clear "install libcfitsio"
/// message per §72.3.
/// </summary>
public static class FitsLibraryProbe {
    public static void EnsureLoadable() {
        var buffer = new byte[81];
        CFitsIO.GetErrorStatus(0, buffer);
    }
}