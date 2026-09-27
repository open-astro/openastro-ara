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
        /// request) the device with this UUID at /media/openastroara through
        /// the root-run <c>configure-storage.sh</c> helper: on a packaged install
        /// via the <c>openastroara-storage@</c> unit, on a dev rig via sudo.</summary>
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
    /// (no privilege); every mutating operation runs
    /// <c>/opt/openastroara/scripts/configure-storage.sh</c> as root, so the
    /// script's own validation (system-disk refusal, filesystem check, label
    /// confirmation) can never be bypassed by the API. On a packaged install the
    /// daemon holds no privilege of its own: its unit runs with
    /// <c>NoNewPrivileges=true</c>, under which sudo refuses to run, so it
    /// writes a request file and asks systemd (over D-Bus, authorised by a
    /// polkit rule) to start <c>openastroara-storage@&lt;id&gt;.service</c>,
    /// which runs the helper and leaves a result file. A dev rig without the
    /// packaged unit falls back to the sudoers-scoped direct invocation.
    /// </summary>
    public sealed partial class StorageDeviceService : IStorageDeviceService {
        internal const string HelperPath = "/opt/openastroara/scripts/configure-storage.sh";
        internal const string MountPoint = "/media/openastroara";
        /// <summary>Template unit installed by the .deb; its presence selects the
        /// request-file path over direct sudo.</summary>
        internal const string HelperUnitTemplate = "/etc/systemd/system/openastroara-storage@.service";
        /// <summary>Daemon-owned exchange directory (tmpfiles.d): <c>&lt;id&gt;.request</c>
        /// in, <c>&lt;id&gt;.result</c> out.</summary>
        internal const string RequestDirectory = "/run/openastroara/storage";

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
            // The request file is newline-delimited (one helper argument per
            // line), so a label carrying a line break would split into extra
            // arguments: a trailing "\n" would pass the retype gate as the bare
            // label, and a run of them trips the wrapper's argument cap into an
            // opaque failure. Not reachable from the client's single-line field.
            if (!IsSingleLine(expectedLabel)) {
                return new StorageConfigureResult(false, "bad_label",
                    "The confirmation label cannot contain line breaks.", null);
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
                ? ["--format", "--fs", fs, uuid, expectedLabel!]
                : [uuid];
            var (exitCode, output) = await RunHelperAsync(args, ct).ConfigureAwait(false);
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
            var (exitCode, output) = await RunHelperAsync(["--check", uuid], ct).ConfigureAwait(false);
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
            var (exitCode, output) = await RunHelperAsync(["--eject", uuid], ct).ConfigureAwait(false);
            var text = output.Trim();
            if (exitCode == 0) {
                LogEjected(logger, uuid);
                return new StorageConfigureResult(true, "ejected", text, null);
            }
            var parts = text.StartsWith("ERROR:", StringComparison.Ordinal)
                ? text["ERROR:".Length..].Trim().Split(' ', 2)
                : [exitCode == 9 ? "usage" : "helper_failed", text];
            LogEjectFailed(logger, uuid, parts[0], parts.Length > 1 ? parts[1] : string.Empty);
            return new StorageConfigureResult(false, parts[0], parts.Length > 1 ? parts[1] : null, null);
        }

        [LoggerMessage(Level = LogLevel.Information, Message = "Storage drive {Uuid} ejected (safe to remove).")]
        private static partial void LogEjected(ILogger logger, string uuid);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Storage eject for UUID {Uuid} failed: {Code} {Detail}.")]
        private static partial void LogEjectFailed(ILogger logger, string uuid, string code, string detail);

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

        /// <summary>
        /// Runs <see cref="HelperPath"/> as root with <paramref name="helperArgs"/>
        /// and returns the helper's exit code and output. Packaged install: request
        /// file → <c>systemctl start openastroara-storage@&lt;id&gt;.service</c> →
        /// result file (see <see cref="ParseHelperResult"/> for its shape). The
        /// unit itself always exits 0 once it has written a result, so a missing
        /// result means the request never ran (polkit refused, unit not loaded)
        /// and systemctl's own stderr is the best diagnostic. Dev rig with no
        /// packaged unit: <c>sudo -n</c> directly, as before.
        /// </summary>
        private static async Task<(int ExitCode, string Output)> RunHelperAsync(string[] helperArgs, CancellationToken ct) {
            if (!File.Exists(HelperUnitTemplate) || !Directory.Exists(RequestDirectory)) {
                return await RunAsync("sudo", ["-n", HelperPath, .. helperArgs], ct).ConfigureAwait(false);
            }
            var id = Guid.NewGuid().ToString("N");
            var requestPath = Path.Combine(RequestDirectory, id + ".request");
            var resultPath = Path.Combine(RequestDirectory, id + ".result");
            try {
                // One argument per line; the wrapper rebuilds argv from it. An
                // empty confirm label must survive as an empty line.
                await File.WriteAllTextAsync(requestPath, string.Join('\n', helperArgs) + "\n", ct).ConfigureAwait(false);
                var (exitCode, output) = await RunAsync("systemctl", ["start", $"openastroara-storage@{id}.service"], ct).ConfigureAwait(false);
                if (!File.Exists(resultPath)) {
                    return (exitCode == 0 ? -1 : exitCode,
                        string.IsNullOrWhiteSpace(output) ? "storage helper unit produced no result" : output);
                }
                return ParseHelperResult(await File.ReadAllTextAsync(resultPath, ct).ConfigureAwait(false));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // An unwritable or unreadable exchange directory (root-owned after a
                // hand-edited tmpfiles config, tmpfs full) is a helper failure like
                // any other: typed result, not a 500 out of the request pipeline.
                return (-1, $"storage request exchange failed: {ex.Message}");
            } finally {
                TryDelete(requestPath);
                TryDelete(resultPath);
            }
        }

        /// <summary>
        /// Result file shape written by storage-request.sh: first line is the
        /// helper's exit code, the rest is its combined output. A malformed first
        /// line is reported as exit -1 with the whole text as output rather than
        /// being mistaken for success.
        /// </summary>
        internal static (int ExitCode, string Output) ParseHelperResult(string text) {
            var newline = text.IndexOf('\n', StringComparison.Ordinal);
            var first = (newline < 0 ? text : text[..newline]).Trim();
            var rest = newline < 0 ? string.Empty : text[(newline + 1)..];
            return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
                ? (code, rest)
                : (-1, text);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort cleanup of the request/result exchange files; a leftover file is harmless (unique id per request) and must never mask the helper's real outcome.")]
        private static void TryDelete(string path) {
            try {
                File.Delete(path);
            } catch (Exception) {
                // Intentionally swallowed — see justification.
            }
        }

        private static async Task<string?> RunCaptureAsync(string file, string[] args, CancellationToken ct) {
            var (exitCode, output) = await RunAsync(file, args, ct).ConfigureAwait(false);
            return exitCode == 0 ? output : null;
        }

        /// <summary>True when <paramref name="value"/> can travel as one line of
        /// the request file (no CR or LF); null is the "no label" case.</summary>
        internal static bool IsSingleLine(string? value) =>
            value is null || value.AsSpan().IndexOfAny('\n', '\r') < 0;

        // \z, not $: $ also matches before a single trailing newline, which
        // would let "ABCD-1234\n" through and split it into two request lines.
        [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9A-Fa-f-]{1,64}\z")]
        internal static partial System.Text.RegularExpressions.Regex UuidShape();

        [System.Text.RegularExpressions.GeneratedRegex(@"^/dev/[A-Za-z0-9]{1,32}\z")]
        internal static partial System.Text.RegularExpressions.Regex DevPathShape();

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
