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
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>Outcome of a §29.1.4 helper invocation.</summary>
    public sealed record StorageConfigureResult(bool Success, string Code, string? Detail, string? MountPoint);

    public interface IStorageDeviceService {
        /// <summary>§29.1.1 — connected block devices that could hold ARA data,
        /// with the running system's own disk excluded.</summary>
        Task<IReadOnlyList<StorageDeviceDto>> ListAsync(CancellationToken ct);

        /// <summary>Mount (and optionally reformat as ext4) the device with this
        /// UUID at /media/openastroara via the sudoers-scoped helper.</summary>
        Task<StorageConfigureResult> ConfigureAsync(string uuid, bool format, string? expectedLabel, CancellationToken ct);
    }

    /// <summary>
    /// §29.1 storage configuration. Enumeration is a plain <c>lsblk -J</c> read
    /// (no privilege); every mutating operation goes through
    /// <c>/opt/openastroara/scripts/configure-storage.sh</c> under sudo — the
    /// single narrow privilege the daemon holds for storage (§29.1.4/§34.3), so
    /// the script's own validation (system-disk refusal, ext4 check, label
    /// confirmation) can never be bypassed by the API.
    /// </summary>
    public sealed partial class StorageDeviceService : IStorageDeviceService {
        internal const string HelperPath = "/opt/openastroara/scripts/configure-storage.sh";
        internal const string MountPoint = "/media/openastroara";

        private readonly ILogger logger;

        public StorageDeviceService(ILogger<StorageDeviceService> logger) {
            this.logger = logger;
        }

        public async Task<IReadOnlyList<StorageDeviceDto>> ListAsync(CancellationToken ct) {
            if (!OperatingSystem.IsLinux()) {
                return [];
            }
            var json = await RunCaptureAsync("lsblk",
                "-J -b -o NAME,PATH,UUID,SIZE,MOUNTPOINT,LABEL,FSTYPE,TYPE,RM,TRAN,PKNAME,MODEL", ct)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json)) {
                return [];
            }
            var systemDisks = await SystemDisksAsync(ct).ConfigureAwait(false);
            var devices = new List<StorageDeviceDto>();
            try {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("blockdevices", out var roots)) {
                    return [];
                }
                foreach (var disk in roots.EnumerateArray()) {
                    Collect(disk, parentModel: Text(disk, "model"), systemDisks, devices);
                }
            } catch (JsonException ex) {
                LogEnumerateFailed(logger, ex);
                return [];
            }
            return devices;
        }

        private static void Collect(JsonElement node, string? parentModel,
                IReadOnlySet<string> systemDisks, List<StorageDeviceDto> into) {
            var type = Text(node, "type");
            var path = Text(node, "path");
            var name = Text(node, "name");

            if (node.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array) {
                foreach (var child in children.EnumerateArray()) {
                    Collect(child, parentModel ?? Text(node, "model"), systemDisks, into);
                }
                // A disk with partitions is never itself a candidate — the
                // partitions are what get mounted.
                if (type == "disk") {
                    return;
                }
            }
            if (type is not ("part" or "disk") || string.IsNullOrEmpty(path)) {
                return;
            }
            // Noise the user must never be offered: loop/zram pseudo-devices,
            // eMMC hardware boot partitions, and empty card-reader slots (a
            // reader with no card enumerates as a real 0-byte disk).
            if (name.StartsWith("loop", StringComparison.Ordinal)
                || name.StartsWith("zram", StringComparison.Ordinal)
                || name.Contains("boot0", StringComparison.Ordinal)
                || name.Contains("boot1", StringComparison.Ordinal)) {
                return;
            }
            var size = Number(node, "size");
            // 1 GB floor: below that it is a firmware/boot artifact, not a store.
            if (size is null or < 1_000_000_000) {
                return;
            }
            var parent = Text(node, "pkname");
            var isSystem = systemDisks.Contains(path)
                || (!string.IsNullOrEmpty(parent) && systemDisks.Contains("/dev/" + parent));

            into.Add(new StorageDeviceDto(
                Path: path,
                Uuid: NullIfEmpty(Text(node, "uuid")),
                Label: NullIfEmpty(Text(node, "label")),
                Model: NullIfEmpty(parentModel ?? Text(node, "model")),
                FileSystem: NullIfEmpty(Text(node, "fstype")),
                SizeBytes: size,
                MountPoint: NullIfEmpty(Text(node, "mountpoint")),
                Removable: Bool(node, "rm"),
                Transport: NullIfEmpty(Text(node, "tran")),
                IsSystemDisk: isSystem,
                IsAraStore: string.Equals(Text(node, "mountpoint"), MountPoint, StringComparison.Ordinal)));
        }

        /// <summary>Devices carrying / or /boot* — never offered as candidates.</summary>
        private static async Task<IReadOnlySet<string>> SystemDisksAsync(CancellationToken ct) {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in new[] { "/", "/boot", "/boot/firmware" }) {
                var source = (await RunCaptureAsync("findmnt", $"-no SOURCE {target}", ct).ConfigureAwait(false))?.Trim();
                if (string.IsNullOrEmpty(source)) {
                    continue;
                }
                set.Add(source);
                var parent = (await RunCaptureAsync("lsblk", $"-no PKNAME {source}", ct).ConfigureAwait(false))?.Trim();
                if (!string.IsNullOrEmpty(parent)) {
                    set.Add("/dev/" + parent);
                }
            }
            return set;
        }

        public async Task<StorageConfigureResult> ConfigureAsync(string uuid, bool format, string? expectedLabel, CancellationToken ct) {
            ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
            if (!OperatingSystem.IsLinux()) {
                return new StorageConfigureResult(false, "unsupported_platform", "Storage configuration is Linux-only.", null);
            }
            if (!File.Exists(HelperPath)) {
                return new StorageConfigureResult(false, "helper_missing",
                    $"{HelperPath} is not installed — reinstall the openastroara-server package.", null);
            }
            if (format && string.IsNullOrWhiteSpace(expectedLabel)) {
                return new StorageConfigureResult(false, "label_required",
                    "Reformatting requires the drive's current label as confirmation.", null);
            }
            // Arguments are argv-passed (no shell), and the helper re-validates
            // everything it is told — the API cannot talk it past its own checks.
            var args = format
                ? $"-n {HelperPath} --format {uuid} {expectedLabel}"
                : $"-n {HelperPath} {uuid}";
            var (exitCode, output) = await RunAsync("sudo", args, ct).ConfigureAwait(false);
            var text = output.Trim();
            if (exitCode == 0) {
                LogConfigured(logger, uuid, format);
                return new StorageConfigureResult(true, "ok", text, MountPoint);
            }
            // "ERROR: <code> [detail]" — surface the code verbatim so the client
            // can branch (not_ext4 → offer the reformat path, etc.).
            var parts = text.StartsWith("ERROR:", StringComparison.Ordinal)
                ? text["ERROR:".Length..].Trim().Split(' ', 2)
                : [exitCode == 9 ? "usage" : "helper_failed", text];
            var code = parts[0];
            var detail = parts.Length > 1 ? parts[1] : null;
            LogConfigureFailed(logger, uuid, code, detail ?? string.Empty);
            return new StorageConfigureResult(false, code, detail, null);
        }

        private static string Text(JsonElement node, string property) =>
            node.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;

        private static long? Number(JsonElement node, string property) =>
            node.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
                ? n
                : null;

        private static bool Bool(JsonElement node, string property) =>
            node.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

        private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static async Task<string?> RunCaptureAsync(string file, string args, CancellationToken ct) {
            var (exitCode, output) = await RunAsync(file, args, ct).ConfigureAwait(false);
            return exitCode == 0 ? output : null;
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Probing external tools is best-effort: a missing/failing lsblk|findmnt|sudo must degrade to 'no devices' or a typed failure result, never crash the request. Log-and-recover boundary.")]
        private static async Task<(int ExitCode, string Output)> RunAsync(string file, string args, CancellationToken ct) {
            try {
                using var process = Process.Start(new ProcessStartInfo(file, args) {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                });
                if (process is null) {
                    return (-1, string.Empty);
                }
                // Read before waiting: a full pipe would otherwise deadlock the wait.
                var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                return (process.ExitCode, stdout);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception) {
                return (-1, string.Empty);
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Enumerating storage devices failed.")]
        private static partial void LogEnumerateFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Storage configured for UUID {Uuid} (format={Format}).")]
        private static partial void LogConfigured(ILogger logger, string uuid, bool format);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Storage configure for UUID {Uuid} failed: {Code} {Detail}.")]
        private static partial void LogConfigureFailed(ILogger logger, string uuid, string code, string detail);
    }
}
