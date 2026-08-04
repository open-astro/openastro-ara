#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Image.Interfaces;
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Image.FileFormat.RAW;

/// <summary>
/// Minimal, ABI-stable LibRaw C-API boundary. It consumes only exported accessors and
/// <c>libraw_processed_image_t</c>'s documented 16-byte header; no <c>libraw_data_t</c>
/// layout is marshalled.
/// </summary>
public sealed class LibRawDecoder : IRawImageDecoder {
    private const int MinimumLibRawVersion = 21 << 8;
    private const int LibRawSuccess = 0;
    private const int LibRawCancelledByCallback = -100010;
    private const int LibRawBitmap = 2;
    private const int ProcessedHeaderBytes = 16;
    private const int AhdDemosaic = 3;
    private const int ImageParametersMakeOffset = 4;
    private const int ImageParametersModelOffset = 68;
    private const int ImageParametersFiltersOffset = 344;
    private const uint LibRawXtransFilter = 9;
    private static readonly Lazy<NativeLoadResult> Native = new(LoadNative,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public bool IsAvailable => Native.Value.Api is not null;

    public string? Version => Native.Value.Api?.Version;

    public static bool IsKnownFileExtension(string? extension) {
        if (string.IsNullOrWhiteSpace(extension)) return false;
        return extension.ToUpperInvariant() is
            ".3FR" or ".ARI" or ".ARW" or ".BAY" or ".CAP" or ".CR2" or ".CR3" or ".CRW"
            or ".DCR" or ".DCS" or ".DNG" or ".DRF" or ".EIP" or ".ERF" or ".FFF" or ".GPR"
            or ".IIQ" or ".K25" or ".KDC" or ".MDC" or ".MEF" or ".MOS" or ".MRW" or ".NEF"
            or ".NRW" or ".OBM" or ".ORF" or ".PEF" or ".PTX" or ".PXN" or ".R3D" or ".RAF"
            or ".RAW" or ".RWL" or ".RW2" or ".RWZ" or ".SR2" or ".SRF" or ".SRW" or ".X3F";
    }

    public Task<DecodedRawImage> DecodeFileAsync(string path, ImageLoadLimits limits,
            CancellationToken cancellationToken) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        return Task.Run(() => DecodeFile(fullPath, limits, cancellationToken), cancellationToken);
    }

    public Task<DecodedRawImage> DecodeBufferAsync(ReadOnlyMemory<byte> source,
            ImageLoadLimits limits, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (source.IsEmpty || source.Length > limits.MaxFileBytes) {
            throw new InvalidDataException(
                $"RAW source size {source.Length} is outside the supported range 1-{limits.MaxFileBytes} bytes.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => DecodeBuffer(source, limits, cancellationToken), cancellationToken);
    }

    private static DecodedRawImage DecodeFile(string path, ImageLoadLimits limits,
            CancellationToken cancellationToken) {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("RAW source does not exist.", path);
        if (info.Length <= 0 || info.Length > limits.MaxFileBytes) {
            throw new InvalidDataException(
                $"RAW source size {info.Length} is outside the supported range 1-{limits.MaxFileBytes} bytes.");
        }
        var native = RequireNative();
        return Decode(native, context => native.OpenFile(context, path), limits, cancellationToken);
    }

    private static unsafe DecodedRawImage DecodeBuffer(ReadOnlyMemory<byte> source,
            ImageLoadLimits limits, CancellationToken cancellationToken) {
        var native = RequireNative();
        using var pin = source.Pin();
        return Decode(native,
            context => native.OpenBuffer(context, (IntPtr)pin.Pointer, checked((nuint)source.Length)),
            limits, cancellationToken);
    }

    private static DecodedRawImage Decode(NativeApi native, Func<IntPtr, int> open,
            ImageLoadLimits limits, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        var context = native.Init(0);
        if (context == IntPtr.Zero) {
            throw new RawImageDecodeException("LibRaw could not allocate a decoder context.");
        }

        NativeApi.ProgressCallback? progressCallback = null;
        try {
            progressCallback = (_, _, _, _) => cancellationToken.IsCancellationRequested ? 1 : 0;
            native.SetProgressHandler(context, progressCallback, IntPtr.Zero);
            Check(native, open(context), "identify", cancellationToken);
            ValidateGeometry(native.GetImageWidth(context), native.GetImageHeight(context), limits);
            var (cameraMake, cameraModel) = native.GetCameraIdentity(context);
            var originalCfaPattern = native.GetOriginalCfaPattern(context);
            var debayerMethod = originalCfaPattern switch {
                "XTRANS" => "libraw_xtrans",
                not null => "libraw_ahd",
                _ => "libraw_native_color",
            };
            Check(native, native.Unpack(context), "unpack", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var sourceMaximum = native.GetColorMaximum(context);
            var sourceBitDepth = sourceMaximum > 0
                ? 32 - BitOperations.LeadingZeroCount((uint)sourceMaximum)
                : 16;

            ConfigureLinearOutput(native, context);
            Check(native, native.Process(context), "process", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return CopyProcessedImage(native, context, limits, sourceBitDepth,
                cameraMake, cameraModel, originalCfaPattern, debayerMethod, cancellationToken);
        } finally {
            GC.KeepAlive(progressCallback);
            native.Close(context);
        }
    }

    private static void ConfigureLinearOutput(NativeApi native, IntPtr context) {
        native.SetOutputBitsPerSample(context, 16);
        native.SetOutputColor(context, 1); // sRGB primaries
        native.SetDemosaic(context, AhdDemosaic);
        native.SetNoAutoBright(context, 1);
        native.SetAdjustMaximumThreshold(context, 0);
        native.SetGamma(context, 0, 1);
        native.SetGamma(context, 1, 1);

        var cameraMultipliers = new float[4];
        var valid = true;
        for (var index = 0; index < cameraMultipliers.Length; index++) {
            cameraMultipliers[index] = native.GetCameraMultiplier(context, index);
            valid &= float.IsFinite(cameraMultipliers[index]) && cameraMultipliers[index] > 0;
        }
        if (!valid && cameraMultipliers[0] > 0 && cameraMultipliers[1] > 0
            && cameraMultipliers[2] > 0 && cameraMultipliers[3] == 0) {
            cameraMultipliers[3] = cameraMultipliers[1];
            valid = true;
        }
        if (!valid) return;
        for (var index = 0; index < cameraMultipliers.Length; index++) {
            native.SetUserMultiplier(context, index, cameraMultipliers[index]);
        }
    }

    private static unsafe DecodedRawImage CopyProcessedImage(NativeApi native, IntPtr context,
            ImageLoadLimits limits, int sourceBitDepth, string? cameraMake,
            string? cameraModel, string? originalCfaPattern, string debayerMethod,
            CancellationToken cancellationToken) {
        var image = native.MakeMemoryImage(context, out var errorCode);
        if (errorCode != LibRawSuccess && image != IntPtr.Zero) {
            native.ClearMemoryImage(image);
            image = IntPtr.Zero;
        }
        Check(native, errorCode, "materialize", cancellationToken);
        if (image == IntPtr.Zero) {
            throw new RawImageDecodeException("LibRaw returned no processed image.");
        }
        try {
            var type = Marshal.ReadInt32(image, 0);
            var height = unchecked((ushort)Marshal.ReadInt16(image, 4));
            var width = unchecked((ushort)Marshal.ReadInt16(image, 6));
            var colors = unchecked((ushort)Marshal.ReadInt16(image, 8));
            var bits = unchecked((ushort)Marshal.ReadInt16(image, 10));
            var dataSize = unchecked((uint)Marshal.ReadInt32(image, 12));
            if (type != LibRawBitmap) {
                throw new RawImageDecodeException($"LibRaw returned processed image type {type}; bitmap required.");
            }
            ValidateGeometry(width, height, limits);
            if (colors is < 3 or > 4) {
                throw new RawImageDecodeException(
                    $"LibRaw returned {colors} color channels; three or four required.");
            }
            if (bits is not (8 or 16)) {
                throw new RawImageDecodeException(
                    $"LibRaw returned {bits}-bit samples; 8-bit or 16-bit required.");
            }

            var bytesPerSample = bits / 8;
            var expectedBytes = checked((long)width * height * colors * bytesPerSample);
            if (expectedBytes > int.MaxValue || dataSize != expectedBytes) {
                throw new RawImageDecodeException(
                    $"LibRaw returned an invalid pixel buffer size {dataSize}; expected {expectedBytes} bytes.");
            }
            var pixelCount = checked(width * height);
            var red = new ushort[pixelCount];
            var green = new ushort[pixelCount];
            var blue = new ushort[pixelCount];
            var packed = new ReadOnlySpan<byte>(
                (void*)IntPtr.Add(image, ProcessedHeaderBytes), checked((int)expectedBytes));
            CopyPlanes(packed, colors, bits, red, green, blue, cancellationToken);
            return new DecodedRawImage(width, height, sourceBitDepth, red, green, blue,
                native.Version, debayerMethod, cameraMake, cameraModel, originalCfaPattern);
        } finally {
            native.ClearMemoryImage(image);
        }
    }

    private static void CopyPlanes(ReadOnlySpan<byte> packed, int colors, int bits,
            ushort[] red, ushort[] green, ushort[] blue, CancellationToken cancellationToken) {
        if (bits == 8) {
            for (var pixel = 0; pixel < red.Length; pixel++) {
                if ((pixel & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
                var offset = pixel * colors;
                red[pixel] = (ushort)(packed[offset] * 257);
                green[pixel] = (ushort)(packed[offset + 1] * 257);
                blue[pixel] = (ushort)(packed[offset + 2] * 257);
            }
            return;
        }

        for (var pixel = 0; pixel < red.Length; pixel++) {
            if ((pixel & 0x3ffff) == 0) cancellationToken.ThrowIfCancellationRequested();
            var offset = pixel * colors * 2;
            red[pixel] = ReadNativeUInt16(packed, offset);
            green[pixel] = ReadNativeUInt16(packed, offset + 2);
            blue[pixel] = ReadNativeUInt16(packed, offset + 4);
        }
    }

    private static ushort ReadNativeUInt16(ReadOnlySpan<byte> bytes, int offset) => BitConverter.IsLittleEndian
        ? (ushort)(bytes[offset] | (bytes[offset + 1] << 8))
        : (ushort)((bytes[offset] << 8) | bytes[offset + 1]);

    private static void ValidateGeometry(int width, int height, ImageLoadLimits limits) {
        if (width <= 0 || height <= 0 || width > limits.MaxDimension || height > limits.MaxDimension) {
            throw new InvalidDataException(
                $"RAW dimensions {width}x{height} exceed the configured dimension limit {limits.MaxDimension}.");
        }
        var pixels = (long)width * height;
        if (pixels > limits.MaxPixelCount || pixels > int.MaxValue) {
            throw new InvalidDataException(
                $"RAW pixel count {pixels} exceeds the configured limit {limits.MaxPixelCount}.");
        }
    }

    private static void Check(NativeApi native, int errorCode, string stage,
            CancellationToken cancellationToken) {
        if (errorCode == LibRawSuccess) return;
        if (errorCode == LibRawCancelledByCallback && cancellationToken.IsCancellationRequested) {
            cancellationToken.ThrowIfCancellationRequested();
        }
        throw new RawImageDecodeException(
            $"LibRaw failed to {stage} camera RAW data: {native.GetErrorText(errorCode)}.", errorCode);
    }

    private static NativeApi RequireNative() {
        var result = Native.Value;
        if (result.Api is not null) return result.Api;
        throw new RawDecoderUnavailableException(
            "Camera RAW decoding requires LibRaw 0.21 or later. Install the platform LibRaw runtime.",
            result.Error ?? new DllNotFoundException("LibRaw native library was not found."));
    }

    private static NativeLoadResult LoadNative() {
        try {
            var candidates = OperatingSystem.IsLinux()
                ? ["libraw_r.so.23", "libraw.so.23", "libraw_r.so", "libraw.so"]
                : OperatingSystem.IsMacOS()
                    ? ["libraw_r.23.dylib", "libraw.23.dylib", "libraw_r.dylib", "libraw.dylib",
                        "/opt/homebrew/lib/libraw_r.dylib", "/usr/local/lib/libraw_r.dylib"]
                    : OperatingSystem.IsWindows()
                        ? ["libraw.dll", "raw.dll"]
                        : Array.Empty<string>();
            foreach (var candidate in candidates) {
                if (!NativeLibrary.TryLoad(candidate, typeof(LibRawDecoder).Assembly,
                        DllImportSearchPath.SafeDirectories, out var handle)) continue;
                try {
                    var api = new NativeApi(handle);
                    if (api.VersionNumber < MinimumLibRawVersion) {
                        NativeLibrary.Free(handle);
                        return new NativeLoadResult(null, new NotSupportedException(
                            $"LibRaw {api.Version} is older than required version 0.21."));
                    }
                    return new NativeLoadResult(api, null);
                } catch {
                    NativeLibrary.Free(handle);
                    throw;
                }
            }
            return new NativeLoadResult(null, new DllNotFoundException(
                "No supported LibRaw native library name could be loaded."));
        } catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                     or BadImageFormatException or NotSupportedException) {
            return new NativeLoadResult(null, ex);
        }
    }

    private sealed record NativeLoadResult(NativeApi? Api, Exception? Error);

    private sealed class NativeApi {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int ProgressCallback(IntPtr data, int stage, int iteration, int expected);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr InitDelegate(uint flags);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OpenFileDelegate(IntPtr context,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int OpenBufferDelegate(IntPtr context, IntPtr buffer, nuint size);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ContextResultDelegate(IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ContextPointerDelegate(IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int ColorAtDelegate(IntPtr context, int row, int column);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ContextActionDelegate(IntPtr context);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetIntDelegate(IntPtr context, int value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetIndexedFloatDelegate(IntPtr context, int index, float value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetFloatDelegate(IntPtr context, float value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate float GetIndexedFloatDelegate(IntPtr context, int index);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr MakeMemoryImageDelegate(IntPtr context, out int errorCode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ClearMemoryImageDelegate(IntPtr image);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void SetProgressHandlerDelegate(IntPtr context,
            ProgressCallback callback, IntPtr data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr ErrorTextDelegate(int errorCode);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr VersionDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int VersionNumberDelegate();

        private readonly InitDelegate _init;
        private readonly OpenFileDelegate _openFile;
        private readonly OpenBufferDelegate _openBuffer;
        private readonly ContextResultDelegate _unpack;
        private readonly ContextResultDelegate _process;
        private readonly ContextActionDelegate _close;
        private readonly SetIntDelegate _setDemosaic;
        private readonly SetIntDelegate _setOutputColor;
        private readonly SetIntDelegate _setOutputBitsPerSample;
        private readonly SetIntDelegate _setNoAutoBright;
        private readonly SetIndexedFloatDelegate _setGamma;
        private readonly SetIndexedFloatDelegate _setUserMultiplier;
        private readonly SetFloatDelegate _setAdjustMaximumThreshold;
        private readonly GetIndexedFloatDelegate _getCameraMultiplier;
        private readonly ContextResultDelegate _getColorMaximum;
        private readonly ContextResultDelegate _getImageWidth;
        private readonly ContextResultDelegate _getImageHeight;
        private readonly ContextPointerDelegate _getImageParameters;
        private readonly ColorAtDelegate _colorAt;
        private readonly MakeMemoryImageDelegate _makeMemoryImage;
        private readonly ClearMemoryImageDelegate _clearMemoryImage;
        private readonly SetProgressHandlerDelegate _setProgressHandler;
        private readonly ErrorTextDelegate _errorText;

        internal NativeApi(IntPtr handle) {
            _init = Export<InitDelegate>(handle, "libraw_init");
            _openFile = Export<OpenFileDelegate>(handle, "libraw_open_file");
            _openBuffer = Export<OpenBufferDelegate>(handle, "libraw_open_buffer");
            _unpack = Export<ContextResultDelegate>(handle, "libraw_unpack");
            _process = Export<ContextResultDelegate>(handle, "libraw_dcraw_process");
            _close = Export<ContextActionDelegate>(handle, "libraw_close");
            _setDemosaic = Export<SetIntDelegate>(handle, "libraw_set_demosaic");
            _setOutputColor = Export<SetIntDelegate>(handle, "libraw_set_output_color");
            _setOutputBitsPerSample = Export<SetIntDelegate>(handle, "libraw_set_output_bps");
            _setNoAutoBright = Export<SetIntDelegate>(handle, "libraw_set_no_auto_bright");
            _setGamma = Export<SetIndexedFloatDelegate>(handle, "libraw_set_gamma");
            _setUserMultiplier = Export<SetIndexedFloatDelegate>(handle, "libraw_set_user_mul");
            _setAdjustMaximumThreshold = Export<SetFloatDelegate>(handle, "libraw_set_adjust_maximum_thr");
            _getCameraMultiplier = Export<GetIndexedFloatDelegate>(handle, "libraw_get_cam_mul");
            _getColorMaximum = Export<ContextResultDelegate>(handle, "libraw_get_color_maximum");
            _getImageWidth = Export<ContextResultDelegate>(handle, "libraw_get_iwidth");
            _getImageHeight = Export<ContextResultDelegate>(handle, "libraw_get_iheight");
            _getImageParameters = Export<ContextPointerDelegate>(handle, "libraw_get_iparams");
            _colorAt = Export<ColorAtDelegate>(handle, "libraw_COLOR");
            _makeMemoryImage = Export<MakeMemoryImageDelegate>(handle, "libraw_dcraw_make_mem_image");
            _clearMemoryImage = Export<ClearMemoryImageDelegate>(handle, "libraw_dcraw_clear_mem");
            _setProgressHandler = Export<SetProgressHandlerDelegate>(handle, "libraw_set_progress_handler");
            _errorText = Export<ErrorTextDelegate>(handle, "libraw_strerror");
            var version = Export<VersionDelegate>(handle, "libraw_version");
            var versionNumber = Export<VersionNumberDelegate>(handle, "libraw_versionNumber");
            Version = Marshal.PtrToStringUTF8(version()) ?? "unknown";
            VersionNumber = versionNumber();
        }

        internal string Version { get; }
        internal int VersionNumber { get; }
        internal IntPtr Init(uint flags) => _init(flags);
        internal int OpenFile(IntPtr context, string path) => _openFile(context, path);
        internal int OpenBuffer(IntPtr context, IntPtr buffer, nuint size) => _openBuffer(context, buffer, size);
        internal int Unpack(IntPtr context) => _unpack(context);
        internal int Process(IntPtr context) => _process(context);
        internal void Close(IntPtr context) => _close(context);
        internal void SetDemosaic(IntPtr context, int value) => _setDemosaic(context, value);
        internal void SetOutputColor(IntPtr context, int value) => _setOutputColor(context, value);
        internal void SetOutputBitsPerSample(IntPtr context, int value) =>
            _setOutputBitsPerSample(context, value);
        internal void SetNoAutoBright(IntPtr context, int value) => _setNoAutoBright(context, value);
        internal void SetGamma(IntPtr context, int index, float value) => _setGamma(context, index, value);
        internal void SetUserMultiplier(IntPtr context, int index, float value) =>
            _setUserMultiplier(context, index, value);
        internal void SetAdjustMaximumThreshold(IntPtr context, float value) =>
            _setAdjustMaximumThreshold(context, value);
        internal float GetCameraMultiplier(IntPtr context, int index) =>
            _getCameraMultiplier(context, index);
        internal int GetColorMaximum(IntPtr context) => _getColorMaximum(context);
        internal int GetImageWidth(IntPtr context) => _getImageWidth(context);
        internal int GetImageHeight(IntPtr context) => _getImageHeight(context);
        internal (string? Make, string? Model) GetCameraIdentity(IntPtr context) {
            var parameters = _getImageParameters(context);
            if (parameters == IntPtr.Zero) return (null, null);
            return (ReadFixedAscii(parameters, ImageParametersMakeOffset, 64),
                ReadFixedAscii(parameters, ImageParametersModelOffset, 64));
        }

        internal string? GetOriginalCfaPattern(IntPtr context) {
            var parameters = _getImageParameters(context);
            if (parameters == IntPtr.Zero) return null;
            var filters = unchecked((uint)Marshal.ReadInt32(parameters, ImageParametersFiltersOffset));
            if (filters == 0) return null;
            if (filters == LibRawXtransFilter) return "XTRANS";
            Span<int> grid = stackalloc int[36];
            var containsRed = false;
            var containsGreen = false;
            var containsBlue = false;
            for (var row = 0; row < 6; row++) {
                for (var column = 0; column < 6; column++) {
                    var color = _colorAt(context, row, column);
                    if (color is < 0 or > 3) return null;
                    grid[row * 6 + column] = color;
                    containsRed |= color == 0;
                    containsGreen |= color is 1 or 3;
                    containsBlue |= color == 2;
                }
            }
            if (!containsRed || !containsGreen || !containsBlue) return null;
            var repeatsTwoByTwo = true;
            for (var row = 0; row < 6 && repeatsTwoByTwo; row++) {
                for (var column = 0; column < 6; column++) {
                    if (grid[row * 6 + column] == grid[(row & 1) * 6 + (column & 1)]) continue;
                    repeatsTwoByTwo = false;
                    break;
                }
            }
            if (!repeatsTwoByTwo) return null;
            Span<char> pattern =
            [
                ColorName(grid[0]),
                ColorName(grid[1]),
                ColorName(grid[6]),
                ColorName(grid[7]),
            ];
            var value = new string(pattern);
            return value is "RGGB" or "BGGR" or "GBRG" or "GRBG" ? value : null;
        }
        internal IntPtr MakeMemoryImage(IntPtr context, out int errorCode) =>
            _makeMemoryImage(context, out errorCode);
        internal void ClearMemoryImage(IntPtr image) => _clearMemoryImage(image);
        internal void SetProgressHandler(IntPtr context, ProgressCallback callback, IntPtr data) =>
            _setProgressHandler(context, callback, data);
        internal string GetErrorText(int errorCode) =>
            Marshal.PtrToStringUTF8(_errorText(errorCode)) ?? $"native error {errorCode}";

        private static T Export<T>(IntPtr handle, string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));

        private static char ColorName(int color) => color switch {
            0 => 'R',
            2 => 'B',
            _ => 'G',
        };

        private static string? ReadFixedAscii(IntPtr source, int offset, int length) {
            var bytes = new byte[length];
            Marshal.Copy(IntPtr.Add(source, offset), bytes, 0, bytes.Length);
            var terminator = Array.IndexOf(bytes, (byte)0);
            var value = Encoding.ASCII.GetString(bytes, 0, terminator >= 0 ? terminator : bytes.Length).Trim();
            return value.Length == 0 ? null : value;
        }
    }
}