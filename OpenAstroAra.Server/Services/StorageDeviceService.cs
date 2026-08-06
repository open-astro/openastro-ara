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

        /// <summary>Mount (and optionally reformat — exFAT by default, ext4 on
        /// request) the device with this UUID at /media/openastroara via the
        /// sudoers-scoped helper.</summary>
        Task<StorageConfigureResult> ConfigureAsync(string uuid, bool format, string? expectedLabel, string? filesystem, CancellationToken ct);

        /// <summary>§29 user-triggered disk check: unmount → matching fsck
        /// (fsck.exfat / e2fsck) → remount. exFAT has no journal, so this is
        /// its recovery story after an unclean power cut.</summary>
        Task<StorageConfigureResult> CheckAsync(string uuid, CancellationToken ct);

        /// <summary>§29 safe removal: flush + unmount so the drive can be
        /// pulled without losing cached writes. The fstab entry stays — a
        /// replug automounts.</summary>
        Task<StorageConfigureResult> EjectAsync(string uuid, CancellationToken ct);
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
                ["-J", "-b", "-o", "NAME,PATH,UUID,SIZE,MOUNTPOINT,LABEL,FSTYPE,TYPE,RM,TRAN,PKNAME,MODEL"], ct)
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

        /// <summary>
        /// Devices carrying / or /boot* — never offered as candidates.
        /// Assumes a plainly partitioned root (the Pi image default): one
        /// PKNAME hop from findmnt's source. Root on LVM/dm-crypt would need
        /// the full parent chain walked — every ancestor lands in the set, so
        /// root on md/LVM/dm-crypt still resolves to its physical holder.
        /// Keep in lock-step with the helper's refuse_if_system_disk: a miss
        /// here offers the system disk for format.
        /// </summary>
        private static async Task<IReadOnlySet<string>> SystemDisksAsync(CancellationToken ct) {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in new[] { "/", "/boot", "/boot/firmware" }) {
                var source = (await RunCaptureAsync("findmnt", ["-no", "SOURCE", target], ct).ConfigureAwait(false))?.Trim();
                if (string.IsNullOrEmpty(source)) {
                    continue;
                }
                set.Add(source);
                // Walk partition → md/LVM/dm-crypt → disk; bounded in case a
                // cyclic PKNAME answer ever appears.
                var node = source;
                for (var hop = 0; hop < 8; hop++) {
                    var parent = (await RunCaptureAsync("lsblk", ["-no", "PKNAME", node], ct).ConfigureAwait(false))
                        ?.Trim().Split('\n')[0].Trim();
                    if (string.IsNullOrEmpty(parent)) {
                        break;
                    }
                    node = "/dev/" + parent;
                    if (!set.Add(node)) {
                        break;
                    }
                }
            }
            return set;
        }

        public async Task<StorageConfigureResult> ConfigureAsync(string uuid, bool format, string? expectedLabel, string? filesystem, CancellationToken ct) {
            ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
            if (!OperatingSystem.IsLinux()) {
                return new StorageConfigureResult(false, "unsupported_platform", "Storage configuration is Linux-only.", null);
            }
            if (!File.Exists(HelperPath)) {
                return new StorageConfigureResult(false, "helper_missing",
                    $"{HelperPath} is not installed — reinstall the openastroara-server package.", null);
            }
            // exFAT is the take-the-drive-home default; ext4 the rig-resident
            // option. Anything else never reaches a command line.
            var fs = filesystem ?? "exfat";
            if (fs is not ("exfat" or "ext4")) {
                return new StorageConfigureResult(false, "bad_filesystem",
                    "Filesystem must be exfat or ext4.", null);
            }
            // An empty confirm label is legal only for the format path of a
            // drive with no label to retype — the helper still refuses unless
            // the drive's actual label is equally empty, so the retype gate
            // stays real for every labeled drive.
            if (format && expectedLabel is null) {
                return new StorageConfigureResult(false, "label_required",
                    "Reformatting requires the drive's current label as confirmation.", null);
            }
            // The identifier is a filesystem UUID (strictly hex-and-dashes)
            // or, for a brand-new blank disk that has no filesystem yet, a
            // /dev/ node path. Anything else never matches a device — reject
            // it before it reaches a command line.
            if (!UuidShape().IsMatch(uuid) && !DevPathShape().IsMatch(uuid)) {
                return new StorageConfigureResult(false, "bad_uuid",
                    "That does not look like a filesystem UUID or device path.", null);
            }
            // Arguments are argv-passed one element each (no shell, no
            // re-splitting), and the helper re-validates everything it is
            // told — the API cannot talk it past its own checks.
            string[] args = format
                ? ["-n", HelperPath, "--format", "--fs", fs, uuid, expectedLabel!]
                : ["-n", HelperPath, uuid];
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

        public async Task<StorageConfigureResult> CheckAsync(string uuid, CancellationToken ct) {
            ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
            if (!OperatingSystem.IsLinux()) {
                return new StorageConfigureResult(false, "unsupported_platform", "Storage checks are Linux-only.", null);
            }
            if (!File.Exists(HelperPath)) {
                return new StorageConfigureResult(false, "helper_missing",
                    $"{HelperPath} is not installed — reinstall the openastroara-server package.", null);
            }
            if (!UuidShape().IsMatch(uuid)) {
                return new StorageConfigureResult(false, "bad_uuid",
                    "That does not look like a filesystem UUID.", null);
            }
            var (exitCode, output) = await RunAsync("sudo", ["-n", HelperPath, "--check", uuid], ct).ConfigureAwait(false);
            var text = output.Trim();
            if (exitCode == 0) {
                LogChecked(logger, uuid, text);
                // "OK <mount> checked clean|repaired" — the last word tells the
                // client whether fsck fixed anything.
                var repaired = text.EndsWith("repaired", StringComparison.Ordinal);
                return new StorageConfigureResult(true, repaired ? "repaired" : "clean", text, MountPoint);
            }
            var parts = text.StartsWith("ERROR:", StringComparison.Ordinal)
                ? text["ERROR:".Length..].Trim().Split(' ', 2)
                : [exitCode == 9 ? "usage" : "helper_failed", text];
            LogCheckFailed(logger, uuid, parts[0], parts.Length > 1 ? parts[1] : string.Empty);
            return new StorageConfigureResult(false, parts[0], parts.Length > 1 ? parts[1] : null, null);
        }

        public async Task<StorageConfigureResult> EjectAsync(string uuid, CancellationToken ct) {
            ArgumentException.ThrowIfNullOrWhiteSpace(uuid);
            if (!OperatingSystem.IsLinux()) {
                return new StorageConfigureResult(false, "unsupported_platform", "Storage eject is Linux-only.", null);
            }
            if (!File.Exists(HelperPath)) {
                return new StorageConfigureResult(false, "helper_missing",
                    $"{HelperPath} is not installed — reinstall the openastroara-server package.", null);
            }
            if (!UuidShape().IsMatch(uuid)) {
                return new StorageConfigureResult(false, "bad_uuid",
                    "That does not look like a filesystem UUID.", null);
            }
            var (exitCode, output) = await RunAsync("sudo", ["-n", HelperPath, "--eject", uuid], ct).ConfigureAwait(false);
            var text = output.Trim();
            if (exitCode == 0) {
                LogEjected(logger, uuid);
                return new StorageConfigureResult(true, "ejected", text, null);
            }
            var parts = text.StartsWith("ERROR:", StringComparison.Ordinal)
                ? text["ERROR:".Length..].Trim().Split(' ', 2)
                : [exitCode == 9 ? "usage" : "helper_failed", text];
            LogCheckFailed(logger, uuid, parts[0], parts.Length > 1 ? parts[1] : string.Empty);
            return new StorageConfigureResult(false, parts[0], parts.Length > 1 ? parts[1] : null, null);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Storage drive {Uuid} ejected (safe to remove).")]
        private static partial void LogEjected(ILogger logger, string uuid);

        [LoggerMessage(Level = LogLevel.Information, Message = "Storage check for UUID {Uuid}: {Outcome}.")]
        private static partial void LogChecked(ILogger logger, string uuid, string outcome);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Storage check for UUID {Uuid} failed: {Code} {Detail}.")]
        private static partial void LogCheckFailed(ILogger logger, string uuid, string code, string detail);

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

        private static async Task<string?> RunCaptureAsync(string file, string[] args, CancellationToken ct) {
            var (exitCode, output) = await RunAsync(file, args, ct).ConfigureAwait(false);
            return exitCode == 0 ? output : null;
        }

        [System.Text.RegularExpressions.GeneratedRegex("^[0-9A-Fa-f-]{1,64}$")]
        private static partial System.Text.RegularExpressions.Regex UuidShape();

        [System.Text.RegularExpressions.GeneratedRegex("^/dev/[A-Za-z0-9]{1,32}$")]
        private static partial System.Text.RegularExpressions.Regex DevPathShape();

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Probing external tools is best-effort: a missing/failing lsblk|findmnt|sudo must degrade to 'no devices' or a typed failure result, never crash the request. Log-and-recover boundary.")]
        private static async Task<(int ExitCode, string Output)> RunAsync(string file, string[] args, CancellationToken ct) {
            try {
                var info = new ProcessStartInfo(file) {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in args) {
                    info.ArgumentList.Add(a);
                }
                using var process = Process.Start(info);
                if (process is null) {
                    return (-1, string.Empty);
                }
                // Read both pipes before waiting (and concurrently with each
                // other): a full pipe would otherwise deadlock the wait.
                var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                var stderrTask = process.StandardError.ReadToEndAsync(ct);
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                // The helper reports its own failures on stdout ("ERROR: …").
                // Anything failing BEFORE that handling (missing binary,
                // sudoers misconfiguration) speaks only on stderr — surface
                // it rather than collapsing to a bare helper_failed.
                var output = stdout;
                if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout)) {
                    output = stderr;
                }
                return (process.ExitCode, output);
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
