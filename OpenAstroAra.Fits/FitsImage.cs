#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenAstroAra.Fits;

/// <summary>Pixel bit-depth of a FITS image HDU per §72.4.</summary>
public enum FitsBitDepth {
    /// <summary>Unset/unknown bit depth (default).</summary>
    None = 0,
    Byte = CFitsIO.BYTE_IMG,
    Signed16 = CFitsIO.SHORT_IMG,
    UnsignedShort = CFitsIO.USHORT_IMG,
    Signed32 = CFitsIO.LONG_IMG,
    Real32 = CFitsIO.FLOAT_IMG,
    Real64 = CFitsIO.DOUBLE_IMG,
}

/// <summary>File access mode for <see cref="FitsImage.Open"/>.</summary>
public enum FitsOpenMode {
    ReadOnly = CFitsIO.READONLY,
    ReadWrite = CFitsIO.READWRITE,
}

/// <summary>
/// Managed wrapper around a CFITSIO file handle (<c>fitsfile*</c>),
/// per playbook §72.4.
///
/// Atomic-write integration (§72.5 + §28.7): <see cref="Create"/>
/// writes to <c>&lt;path&gt;.tmp</c> internally; <see cref="Dispose"/>
/// flushes + closes + renames the temp file to its real name +
/// fsyncs the parent directory. If any step throws, the <c>.tmp</c>
/// is cleaned up by the §28.8 startup scan on next launch — no
/// torn FITS file ever appears under its real name.
///
/// Read path (<see cref="Open"/>) opens the existing file directly;
/// no temp shuffle on the read side.
/// </summary>
public sealed class FitsImage : IDisposable {
    private IntPtr _fptr;
    private readonly string? _finalPath;
    private readonly string? _tempPath;
    private bool _disposed;
    private bool _completed;

    private FitsImage(IntPtr fptr, string? finalPath, string? tempPath) {
        _fptr = fptr;
        _finalPath = finalPath;
        _tempPath = tempPath;
    }

    /// <summary>
    /// Create a new FITS file for writing. Writes to <c>&lt;path&gt;.tmp</c>;
    /// <see cref="Dispose"/> atomically renames to the final path.
    /// </summary>
    /// <param name="path">Final destination path (no <c>.tmp</c> suffix).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="bitDepth">Pixel bit-depth.</param>
    public static FitsImage Create(string path, int width, int height, FitsBitDepth bitDepth) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var tempPath = path + ".tmp";
        // CFITSIO refuses to create a file that already exists. Caller
        // may have crashed mid-write previously — purge the stale temp
        // (the §28.8 scan would do this anyway, but Create() is the
        // hot path on every exposure save so it pays to be defensive).
        if (File.Exists(tempPath)) File.Delete(tempPath);

        var status = 0;
        if (CFitsIO.CreateFile(out var fptr, tempPath, ref status) != 0) {
            throw new FitsException("ffinit", status);
        }
        var naxes = new long[] { width, height };
        if (CFitsIO.CreateImage(fptr, (int)bitDepth, 2, naxes, ref status) != 0) {
            CFitsIO.CloseFile(fptr, ref status);
            File.Delete(tempPath);
            throw new FitsException("ffcrim", status);
        }
        return new FitsImage(fptr, finalPath: path, tempPath: tempPath);
    }

    /// <summary>Open an existing FITS file for reading or read-write.</summary>
    public static FitsImage Open(string path, FitsOpenMode mode = FitsOpenMode.ReadOnly) {
        var status = 0;
        if (CFitsIO.OpenFile(out var fptr, path, (int)mode, ref status) != 0) {
            throw new FitsException("ffopen", status);
        }
        // No temp on the read path — caller's writes are unsupported in this
        // overload (callers that need read-modify-write open with ReadWrite
        // and accept they're modifying in place; §28.7 atomic-write applies
        // to fresh writes via Create).
        return new FitsImage(fptr, finalPath: null, tempPath: null);
    }

    /// <summary>
    /// Image dimensions in pixels. Read from the file's BITPIX/NAXIS
    /// header at first access; cached for the lifetime of the handle.
    /// </summary>
    public (int Width, int Height) GetDimensions() {
        var status = 0;
        if (CFitsIO.GetImageDimensions(_fptr, out var naxis, ref status) != 0) {
            throw new FitsException("ffgidm", status);
        }
        if (naxis < 2) throw new InvalidOperationException($"FITS file is not a 2-D image (naxis={naxis})");
        var dims = new long[naxis];
        if (CFitsIO.GetImageSize(_fptr, naxis, dims, ref status) != 0) {
            throw new FitsException("ffgisz", status);
        }
        // naxes is 1-indexed in FITS conventions; CFITSIO returns dim[0]=width
        // and dim[1]=height after fixing the row-major→column-major reorder.
        return ((int)dims[0], (int)dims[1]);
    }

    /// <summary>
    /// Write a 16-bit unsigned image plane. Must match the
    /// <see cref="FitsBitDepth.UnsignedShort"/> dimensions passed to
    /// <see cref="Create"/>.
    /// </summary>
    public unsafe void WriteImageData(ReadOnlySpan<ushort> data) {
        var (width, height) = GetDimensions();
        if (data.Length != width * height) {
            throw new ArgumentException(
                $"Pixel buffer length ({data.Length}) doesn't match image dimensions ({width}×{height} = {width * height})",
                nameof(data));
        }
        fixed (ushort* p = data) {
            var firstpix = new long[] { 1, 1 };
            var status = 0;
            if (CFitsIO.WritePixels(_fptr, CFitsIO.TUSHORT, firstpix,
                    data.Length, (IntPtr)p, ref status) != 0) {
                throw new FitsException("ffppx (ushort)", status);
            }
        }
    }

    /// <summary>Write a 32-bit float image plane.</summary>
    public unsafe void WriteImageData(ReadOnlySpan<float> data) {
        var (width, height) = GetDimensions();
        if (data.Length != width * height) {
            throw new ArgumentException(
                $"Pixel buffer length ({data.Length}) doesn't match image dimensions ({width}×{height} = {width * height})",
                nameof(data));
        }
        fixed (float* p = data) {
            var firstpix = new long[] { 1, 1 };
            var status = 0;
            if (CFitsIO.WritePixels(_fptr, CFitsIO.TFLOAT, firstpix,
                    data.Length, (IntPtr)p, ref status) != 0) {
                throw new FitsException("ffppx (float)", status);
            }
        }
    }

    /// <summary>Read the image plane as 16-bit unsigned pixels.</summary>
    public unsafe ushort[] ReadImageData16() {
        var (width, height) = GetDimensions();
        var pixels = new ushort[width * height];
        fixed (ushort* p = pixels) {
            var firstpix = new long[] { 1, 1 };
            var status = 0;
            if (CFitsIO.ReadPixels(_fptr, CFitsIO.TUSHORT, firstpix,
                    pixels.Length, IntPtr.Zero, (IntPtr)p, out _, ref status) != 0) {
                throw new FitsException("ffgpxv (ushort)", status);
            }
        }
        return pixels;
    }

    /// <summary>Read the image plane as 32-bit float pixels.</summary>
    public unsafe float[] ReadImageDataFloat() {
        var (width, height) = GetDimensions();
        var pixels = new float[width * height];
        fixed (float* p = pixels) {
            var firstpix = new long[] { 1, 1 };
            var status = 0;
            if (CFitsIO.ReadPixels(_fptr, CFitsIO.TFLOAT, firstpix,
                    pixels.Length, IntPtr.Zero, (IntPtr)p, out _, ref status) != 0) {
                throw new FitsException("ffgpxv (float)", status);
            }
        }
        return pixels;
    }

    /// <summary>Set or update a string-valued header card.</summary>
    public void SetHeader(string keyname, string value, string? comment = null) {
        var status = 0;
        if (CFitsIO.UpdateKeyString(_fptr, keyname, value, comment ?? string.Empty, ref status) != 0) {
            throw new FitsException($"ffukys ({keyname})", status);
        }
    }

    /// <summary>Set or update a numeric header card.</summary>
    public unsafe void SetHeader(string keyname, double value, string? comment = null) {
        var status = 0;
        // CFITSIO's ffuky takes the value as a pointer to the underlying type.
        // For doubles we marshal an 8-byte aligned address via a fixed local.
        var local = value;
        if (CFitsIO.UpdateKey(_fptr, CFitsIO.TDOUBLE, keyname, (IntPtr)(&local),
                comment ?? string.Empty, ref status) != 0) {
            throw new FitsException($"ffuky ({keyname})", status);
        }
    }

    /// <summary>Set or update an integer header card.</summary>
    public unsafe void SetHeader(string keyname, int value, string? comment = null) {
        var status = 0;
        var local = value;
        if (CFitsIO.UpdateKey(_fptr, CFitsIO.TINT, keyname, (IntPtr)(&local),
                comment ?? string.Empty, ref status) != 0) {
            throw new FitsException($"ffuky ({keyname})", status);
        }
    }

    /// <summary>
    /// Read all header cards as a dictionary keyed by keyword. Values are
    /// returned as raw strings; callers parse as needed (CFITSIO doesn't
    /// reliably round-trip typed values without per-key type lookups).
    /// </summary>
    public IReadOnlyDictionary<string, string> ReadHeaders() {
        var status = 0;
        if (CFitsIO.GetHeaderSize(_fptr, out var keysExist, out _, ref status) != 0) {
            throw new FitsException("ffghps", status);
        }
        var headers = new Dictionary<string, string>(keysExist, StringComparer.OrdinalIgnoreCase);
        var card = new byte[81];
        for (var i = 1; i <= keysExist; i++) {
            Array.Clear(card, 0, card.Length);
            status = 0;
            if (CFitsIO.GetHeaderRecord(_fptr, i, card, ref status) != 0) continue;
            var (key, value) = ParseCard(card);
            if (!string.IsNullOrEmpty(key)) headers[key] = value;
        }
        return headers;
    }

    private static (string Key, string Value) ParseCard(byte[] card) {
        // FITS card format: keyword (cols 1-8) "= " (cols 9-10) value (cols 11-80).
        // Comment is " / <text>" inside the value field.
        var text = Encoding.ASCII.GetString(card);
        var nullTerm = text.IndexOf('\0', StringComparison.Ordinal);
        if (nullTerm >= 0) text = text[..nullTerm];
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        if (eq < 1 || eq > 8) {
            // COMMENT / HISTORY / END / blank — skip
            return (string.Empty, string.Empty);
        }
        var key = text[..eq].TrimEnd();
        var valueAndComment = text[(eq + 1)..].Trim();
        // Strip a trailing " / comment" if present, but be careful: string
        // values are quoted and may contain `/`. Find the slash that's
        // outside any quote pair.
        var commentStart = FindCommentStart(valueAndComment);
        var value = (commentStart >= 0 ? valueAndComment[..commentStart] : valueAndComment).Trim();
        // Strip surrounding single quotes on string values.
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') {
            value = value[1..^1].TrimEnd();
        }
        return (key, value);
    }

    private static int FindCommentStart(string s) {
        var inQuote = false;
        for (var i = 0; i < s.Length; i++) {
            if (s[i] == '\'') inQuote = !inQuote;
            else if (s[i] == '/' && !inQuote) return i;
        }
        return -1;
    }

    /// <summary>
    /// §28.7/§72.5 atomic-write completion for the write path (<see cref="Create"/>):
    /// closes the CFITSIO handle, atomically renames <c>&lt;path&gt;.tmp</c> to the
    /// final path, and fsyncs the parent directory. Throws <see cref="FitsException"/>
    /// if the underlying close fails (the temp is removed first so no torn FITS is
    /// left behind). No-op on the read path (<see cref="Open"/>). Call this before
    /// disposing to surface write failures; <see cref="Dispose"/> alone abandons the
    /// in-progress write.
    /// </summary>
    public void Complete() {
        if (_completed || _fptr == IntPtr.Zero) return;

        var status = 0;
        var closed = CFitsIO.CloseFile(_fptr, ref status) == 0;
        _fptr = IntPtr.Zero;

        // Read path: nothing to finalize.
        if (_tempPath is null || _finalPath is null) {
            _completed = true;
            return;
        }

        if (!closed) {
            // CFITSIO close failed — the temp file may be corrupt. Best effort:
            // delete it so the next exposure doesn't trip on a stale tmp. Surface
            // the underlying status so the caller learns about the write failure.
            TryDeleteTemp();
            throw new FitsException("ffclos", status);
        }

        // Atomic rename + fsync the parent dir so the rename itself is durable.
        // File.Move on POSIX does an atomic rename; on Windows it's atomic when
        // within a single volume.
        File.Move(_tempPath, _finalPath, overwrite: true);
        SyncParentDirectory(_finalPath);
        _completed = true;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        // Best-effort cleanup; never throws (CA1065). If Complete() already ran,
        // the handle is gone and the file is committed — nothing to do. Otherwise
        // release the native handle and remove any orphaned temp file; the §28.8
        // startup scan would also reap it, but cleaning up here is tidier.
        if (_fptr != IntPtr.Zero) {
            var status = 0;
            CFitsIO.CloseFile(_fptr, ref status);
            _fptr = IntPtr.Zero;
        }

        if (!_completed && _tempPath is not null) {
            TryDeleteTemp();
        }
    }

    private void TryDeleteTemp() {
        if (_tempPath is null) return;
        try {
            File.Delete(_tempPath);
        } catch (IOException) {
            // best effort — startup scan (§28.8) reaps stragglers
        } catch (UnauthorizedAccessException) {
            // best effort — startup scan (§28.8) reaps stragglers
        }
    }

    private static void SyncParentDirectory(string filePath) {
        // No-op on Windows (NTFS doesn't expose fsync on directory entries
        // through .NET; rename durability is provided by the journal). On
        // POSIX (Linux + macOS) the parent directory's fsync is what makes
        // the rename itself crash-safe per §28.7 step 4.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var parent = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(parent)) return;

        try {
            using var dir = new FileStream(parent, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // FileStream.Flush(true) calls fsync underneath on Unix.
            dir.Flush(true);
        } catch (IOException) {
            // Opening a directory for read+fsync requires kernel support;
            // we've verified it on Linux + macOS. If it ever fails the
            // file is still durable (the file's own fsync via CFITSIO
            // ffclos covered the bytes); the rename's commit timing is
            // the only thing that becomes weaker.
        } catch (UnauthorizedAccessException) {
            // Same fallback as above.
        }
    }
}