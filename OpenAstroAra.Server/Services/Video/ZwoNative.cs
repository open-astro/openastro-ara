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
using System.Runtime.InteropServices;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>
    /// P/Invoke bindings for the §77.1 video-mode subset of ZWO's ASI camera SDK.
    /// The library is `libASICamera2.so`, installed system-wide by the AlpacaBridge deb
    /// at /usr/lib/alpacabridge and registered with ldconfig — the documented companion
    /// contract (§77's placement amendment; SmartGuider consumes it the same way). This
    /// is the one §52 exception: ~10 functions, used only while the camera is detached
    /// from the Alpaca surface per §77.2. Names mirror the C API deliberately.
    /// </summary>
    internal static partial class ZwoNative {
        private const string Dll = "ASICamera2";

        internal enum AsiErrorCode {
            Success = 0,
            InvalidIndex = 1,
            InvalidId = 2,
            InvalidControlType = 3,
            CameraClosed = 4,
            CameraRemoved = 5,
            InvalidPath = 6,
            InvalidFileFormat = 7,
            InvalidSize = 8,
            InvalidImgType = 9,
            OutOfBoundary = 10,
            Timeout = 11,
            InvalidSequence = 12,
            BufferTooSmall = 13,
            VideoModeActive = 14,
            ExposureInProgress = 15,
            GeneralError = 16,
            InvalidMode = 17
        }

        internal enum AsiImgType {
            Raw8 = 0,
            Rgb24 = 1,
            Raw16 = 2,
            Y8 = 3
        }

        internal enum AsiControlType {
            Gain = 0,
            Exposure = 1
        }

        [LibraryImport(Dll, EntryPoint = "ASIGetNumOfConnectedCameras")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial int GetNumOfConnectedCameras();

        // ASI_CAMERA_INFO is a large struct; the video path only needs CameraID, so the
        // binding takes a raw buffer instead of mirroring the full layout. Sizes verified
        // empirically on linux-arm64 against the shipped SDK header (rc91 spike,
        // SDK 1.41): sizeof(ASI_CAMERA_INFO) = 248, offsetof(CameraID) = 64 — the
        // 512-byte buffer is a 2x safety margin over the measured struct.
        [LibraryImport(Dll, EntryPoint = "ASIGetCameraProperty")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode GetCameraProperty(ref byte info, int cameraIndex);

        internal static int? GetCameraIdAtIndex(int index) {
            Span<byte> info = stackalloc byte[512];
            if (GetCameraProperty(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(info), index) != AsiErrorCode.Success) {
                return null;
            }
            return System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(info.Slice(64, 4));
        }

        [LibraryImport(Dll, EntryPoint = "ASIOpenCamera")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode OpenCamera(int cameraId);

        [LibraryImport(Dll, EntryPoint = "ASIInitCamera")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode InitCamera(int cameraId);

        [LibraryImport(Dll, EntryPoint = "ASICloseCamera")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode CloseCamera(int cameraId);

        [LibraryImport(Dll, EntryPoint = "ASISetROIFormat")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode SetRoiFormat(int cameraId, int width, int height, int bin, AsiImgType imgType);

        [LibraryImport(Dll, EntryPoint = "ASISetStartPos")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode SetStartPos(int cameraId, int startX, int startY);

        [LibraryImport(Dll, EntryPoint = "ASISetControlValue")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode SetControlValue(int cameraId, AsiControlType controlType, long value, int isAuto);

        [LibraryImport(Dll, EntryPoint = "ASIStartVideoCapture")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode StartVideoCapture(int cameraId);

        [LibraryImport(Dll, EntryPoint = "ASIStopVideoCapture")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode StopVideoCapture(int cameraId);

        [LibraryImport(Dll, EntryPoint = "ASIGetVideoData")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode GetVideoData(int cameraId, ref byte buffer, long bufferSize, int waitMs);

        [LibraryImport(Dll, EntryPoint = "ASIGetDroppedFrames")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static partial AsiErrorCode GetDroppedFrames(int cameraId, out int droppedFrames);

        internal static void ThrowOnError(AsiErrorCode code, string context) {
            if (code != AsiErrorCode.Success) {
                throw new VideoCaptureException($"{context}: ASI_ERROR_{code}");
            }
        }
    }
}
