#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services.Video {

    /// <summary>
    /// §77.1 usbfs auto-tune: Linux defaults <c>usbcore.usbfs_memory_mb</c> to 16 MB —
    /// far too small for 100+ MB/s bulk video. The daemon sizes it from box RAM
    /// (~total/8, clamped [64, 1000] — ≈256 MB on a 2 GB iMate, 1000 MB on 8 GB; it is
    /// PINNED kernel memory, so it scales with the box) and applies it via
    /// <c>sudo /opt/openastroara/scripts/set-usbfs-memory.sh</c>, the single
    /// deb-installed privilege for this path (§34.3). Best-effort everywhere: absent
    /// script / non-Linux / failure is logged and reported, never fatal.
    /// </summary>
    public sealed partial class UsbfsTuner {
        internal const string SysfsPath = "/sys/module/usbcore/parameters/usbfs_memory_mb";
        internal const string ScriptPath = "/opt/openastroara/scripts/set-usbfs-memory.sh";

        private readonly ILogger logger;

        public UsbfsTuner(ILogger<UsbfsTuner> logger) {
            this.logger = logger;
        }

        /// <summary>RAM-scaled target: clamp(memTotalBytes / 8 in MB, 64, 1000).</summary>
        public static int TargetMb(long memTotalBytes) =>
            (int)Math.Clamp(memTotalBytes / 8 / (1024 * 1024), 64, 1000);

        /// <summary>MemTotal from /proc/meminfo, bytes; 0 when unknown.</summary>
        public static long ReadMemTotalBytes() {
            try {
                if (!OperatingSystem.IsLinux()) {
                    return 0;
                }
                foreach (var line in File.ReadLines("/proc/meminfo")) {
                    if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) {
                        var fields = line.AsSpan("MemTotal:".Length).Trim();
                        var space = fields.IndexOf(' ');
                        var digits = space >= 0 ? fields[..space] : fields;
                        return long.TryParse(digits, out var kib) ? kib * 1024 : 0;
                    }
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Best-effort probe.
            }
            return 0;
        }

        /// <summary>Current live value from sysfs; null off-Linux or unreadable.</summary>
        public static int? ReadCurrentMb() {
            try {
                if (!OperatingSystem.IsLinux() || !File.Exists(SysfsPath)) {
                    return null;
                }
                var text = File.ReadAllText(SysfsPath).Trim();
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? mb : null;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return null;
            }
        }

        /// <summary>
        /// Apply the RAM-scaled (or overridden) value via the sudoers-scoped script.
        /// Returns the applied value, or null when tuning was skipped/failed (logged).
        /// </summary>
        public async Task<int?> AutoTuneAsync(int? overrideMb, CancellationToken ct) {
            if (!OperatingSystem.IsLinux()) {
                return null;
            }
            var target = overrideMb ?? TargetMb(ReadMemTotalBytes());
            target = Math.Clamp(target, 16, 1000);
            if (ReadCurrentMb() == target) {
                return target;   // already there; no privileged call needed
            }
            if (!File.Exists(ScriptPath)) {
                LogScriptMissing(logger, ScriptPath);
                return null;
            }
            try {
                using var process = Process.Start(new ProcessStartInfo("sudo",
                    $"-n {ScriptPath} {target.ToString(CultureInfo.InvariantCulture)}") {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                if (process is null) {
                    LogTuneFailed(logger, target, "process start returned null");
                    return null;
                }
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                if (process.ExitCode != 0) {
                    LogTuneFailed(logger, target, $"exit code {process.ExitCode}");
                    return null;
                }
                LogTuned(logger, target);
                return target;
            } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                             or PlatformNotSupportedException or IOException) {
                LogTuneFailedEx(logger, target, ex);
                return null;
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "usbfs tuning script missing at {Path}; leaving usbfs_memory_mb unchanged.")]
        private static partial void LogScriptMissing(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "usbfs tune to {TargetMb} MB failed: {Reason}.")]
        private static partial void LogTuneFailed(ILogger logger, int targetMb, string reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "usbfs tune to {TargetMb} MB failed.")]
        private static partial void LogTuneFailedEx(ILogger logger, int targetMb, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "usbfs_memory_mb set to {TargetMb} MB (live + boot-persisted).")]
        private static partial void LogTuned(ILogger logger, int targetMb);
    }
}
