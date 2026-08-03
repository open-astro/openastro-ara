#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>
    /// P/Invoke bindings for the §77.1 video-mode subset of Player One's camera SDK
    /// (`libPlayerOneCamera.so`, installed system-wide by the AlpacaBridge deb — the
    /// same companion contract as the ZWO bindings). Names mirror the C API.
    /// </summary>
    internal static partial class PlayerOneNative {
        private const string Dll = "PlayerOneCamera";

        internal enum PoaErrorCode {
            Ok = 0,
            InvalidIndex = 1,
            InvalidId = 2,
            InvalidConfig = 3,
            InvalidArgument = 4,
            NotOpened = 5,
            DeviceNotFound = 6,
            OutOfLimit = 7,
            ExposureFailed = 8,
            Timeout = 9,
            SizeLess = 10,
            Exposing = 11,
            Pointer = 12
        }

        internal enum PoaImgFormat {
            Raw8 = 0,
            Raw16 = 1,
            Rgb24 = 2,
            Mono8 = 3
        }

        internal enum PoaConfig {
            Exposure = 0,           // microseconds
            Gain = 1,
            FrameLimit = 26,        // [0, 2000]; 0 = no limit
            UsbBandwidthLimit = 28  // percent
        }

        // POAConfigValue is an 8-byte union (long | double | POABool).
        [StructLayout(LayoutKind.Explicit)]
        internal struct PoaConfigValue {
            [FieldOffset(0)] public long IntValue;
        }

        [LibraryImport(Dll, EntryPoint = "POAGetCameraCount")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetCameraCount();

        // POACameraProperties: cameraModelName[256] + userCustomID[16], then cameraID.
        // Only cameraID is needed, so the binding takes a raw buffer instead of the
        // full layout. Sizes verified empirically on linux-arm64 against the shipped
        // SDK header (rc91 spike, SDK 3.10.0): sizeof(POACameraProperties) = 992,
        // offsetof(cameraID) = 272 — the 1024-byte buffer covers it with margin.
        [LibraryImport(Dll, EntryPoint = "POAGetCameraProperties")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode GetCameraProperties(int index, ref byte properties);

        [LibraryImport(Dll, EntryPoint = "POAOpenCamera")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode OpenCamera(int cameraId);

        [LibraryImport(Dll, EntryPoint = "POAInitCamera")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode InitCamera(int cameraId);

        [LibraryImport(Dll, EntryPoint = "POACloseCamera")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode CloseCamera(int cameraId);

        [LibraryImport(Dll, EntryPoint = "POASetImageStartPos")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode SetImageStartPos(int cameraId, int startX, int startY);

        [LibraryImport(Dll, EntryPoint = "POASetImageSize")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode SetImageSize(int cameraId, int width, int height);

        [LibraryImport(Dll, EntryPoint = "POASetImageBin")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode SetImageBin(int cameraId, int bin);

        [LibraryImport(Dll, EntryPoint = "POASetImageFormat")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode SetImageFormat(int cameraId, PoaImgFormat format);

        [LibraryImport(Dll, EntryPoint = "POASetConfig")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode SetConfig(int cameraId, PoaConfig config, PoaConfigValue value, int isAuto);

        [LibraryImport(Dll, EntryPoint = "POAStartExposure")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode StartExposure(int cameraId, int singleFrame);

        [LibraryImport(Dll, EntryPoint = "POAStopExposure")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode StopExposure(int cameraId);

        [LibraryImport(Dll, EntryPoint = "POAGetImageData")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode GetImageData(int cameraId, ref byte buffer, long bufferSize, int timeoutMs);

        [LibraryImport(Dll, EntryPoint = "POAGetDroppedImagesCount")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial PoaErrorCode GetDroppedImagesCount(int cameraId, out int droppedCount);

        internal static int? GetCameraIdAtIndex(int index) {
            Span<byte> properties = stackalloc byte[1024];
            if (GetCameraProperties(index, ref MemoryMarshal.GetReference(properties)) != PoaErrorCode.Ok) {
                return null;
            }
            return BinaryPrimitives.ReadInt32LittleEndian(properties.Slice(272, 4));
        }

        internal static void ThrowOnError(PoaErrorCode code, string context) {
            if (code != PoaErrorCode.Ok) {
                throw new VideoCaptureException($"{context}: POA_ERROR_{code}");
            }
        }
    }
}
