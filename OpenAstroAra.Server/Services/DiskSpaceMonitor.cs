#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>Coarse free-space level for the image save volume.</summary>
    public enum DiskSpaceLevel {
        /// <summary>Plenty of room.</summary>
        Ok,
        /// <summary>Getting tight — warn (Yellow).</summary>
        Low,
        /// <summary>About to run out — critical (Red).</summary>
        Critical,
    }

    /// <summary>
    /// §29 — periodically checks free space on the configured image save volume and surfaces a §51 diagnostic
    /// (+ the §54 <c>OnDiskSpaceLow</c> notification when the user has it enabled) when it runs low, so a long
    /// unattended session doesn't silently die on a full disk. It only <em>warns</em> — it never blocks or
    /// aborts a capture, and never deletes anything. Issues are opened on a downward transition and cleared on
    /// recovery (one open issue at a time). Thresholds are absolute free space (what determines whether the next
    /// frames fit), defaulting to 10 GiB (Low) / 2 GiB (Critical); making them user-configurable is a follow-up.
    /// </summary>
    public sealed partial class DiskSpaceMonitor : BackgroundService {

        public const long DefaultLowBytes = 10L * 1024 * 1024 * 1024;      // 10 GiB
        public const long DefaultCriticalBytes = 2L * 1024 * 1024 * 1024;  //  2 GiB
        public const string EventType = "storage.disk_space";

        private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

        private readonly IProfileStore _profileStore;
        private readonly IDiagnosticsService _diagnostics;
        private readonly INotificationService _notifications;
        private readonly ILogger<DiskSpaceMonitor> _logger;
        private readonly long _lowBytes;
        private readonly long _criticalBytes;
        private readonly TimeSpan _interval;
        private DiskSpaceLevel _last = DiskSpaceLevel.Ok;

        public DiskSpaceMonitor(
            IProfileStore profileStore,
            IDiagnosticsService diagnostics,
            INotificationService notifications,
            ILogger<DiskSpaceMonitor> logger,
            long? lowBytes = null,
            long? criticalBytes = null,
            TimeSpan? interval = null) {
            _profileStore = profileStore;
            _diagnostics = diagnostics;
            _notifications = notifications;
            _logger = logger;
            _lowBytes = lowBytes ?? DefaultLowBytes;
            _criticalBytes = criticalBytes ?? DefaultCriticalBytes;
            _interval = interval ?? DefaultInterval;
        }

        /// <summary>Map free bytes to a level. Boundaries are inclusive on the low side (≤ threshold trips it).</summary>
        public static DiskSpaceLevel Evaluate(long freeBytes, long lowBytes, long criticalBytes)
            => freeBytes <= criticalBytes ? DiskSpaceLevel.Critical
             : freeBytes <= lowBytes ? DiskSpaceLevel.Low
             : DiskSpaceLevel.Ok;

        /// <summary>
        /// Of the mounted volume roots, the one that actually contains <paramref name="fullPath"/> — the longest
        /// root that is a prefix of the path (so a dedicated <c>/media/openastroara</c> mount wins over the
        /// <c>/</c> root). Returns null when none match (e.g. the save mount isn't present yet). Pure, so the
        /// cross-platform path→volume choice is unit-testable without real drives.
        /// </summary>
        public static string? LongestPrefixRoot(string fullPath, IEnumerable<string> driveRoots) {
            string? best = null;
            var bestLen = -1;
            foreach (var root in driveRoots) {
                if (root.Length > bestLen && IsUnderRoot(fullPath, root)) {
                    best = root;
                    bestLen = root.Length;
                }
            }
            return best;
        }

        private static bool IsSeparator(char c) => c == '/' || c == '\\';

        // A prefix match only counts on a path-component boundary, so a "/mnt/data" mount doesn't swallow a
        // "/mnt/data2" path. A root that already ends on a separator ("/" or "D:\") bounds on its own; otherwise
        // the char right after the matched prefix must be a separator. Both separator styles are accepted so the
        // pure helper is deterministic across hosts (it's tested with Windows-style roots on a Unix CI box).
        private static bool IsUnderRoot(string fullPath, string root) {
            if (root.Length == 0 || !fullPath.StartsWith(root, StringComparison.Ordinal)) {
                return false;
            }
            return fullPath.Length == root.Length
                || IsSeparator(root[^1])
                || IsSeparator(fullPath[root.Length]);
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort monitor loop: a profile read, DriveInfo probe, or diagnostics/notification write can throw arbitrary IO/driver exceptions — each tick is logged and skipped so a transient failure never tears down the daemon's hosted services.")]
        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            using var timer = new PeriodicTimer(_interval);
            while (!stoppingToken.IsCancellationRequested) {
                try {
                    await CheckOnceAsync(stoppingToken);
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    LogCheckFailed(ex);
                }
                try {
                    if (!await timer.WaitForNextTickAsync(stoppingToken)) {
                        break;
                    }
                } catch (OperationCanceledException) {
                    break;
                }
            }
        }

        private async Task CheckOnceAsync(CancellationToken ct) {
            var saveDir = _profileStore.GetStorageSettings().SaveDirectory;
            if (string.IsNullOrWhiteSpace(saveDir)) {
                return;
            }
            var freeBytes = TryGetFreeBytes(saveDir);
            if (freeBytes is null) {
                return; // volume not resolvable yet (e.g. the save mount isn't attached) — don't false-alarm
            }

            var level = Evaluate(freeBytes.Value, _lowBytes, _criticalBytes);
            if (level == _last) {
                return; // only act on transitions, so a sustained low disk doesn't re-fire every tick
            }
            var previous = _last;
            _last = level;
            await EmitTransitionAsync(previous, level, freeBytes.Value, saveDir, ct);
        }

        private static long? TryGetFreeBytes(string saveDir) {
            var full = Path.GetFullPath(saveDir);
            var drives = DriveInfo.GetDrives().Where(IsReady).ToList();
            var root = LongestPrefixRoot(full, drives.Select(d => d.RootDirectory.FullName));
            if (root is null) {
                return null;
            }
            return drives.First(d => d.RootDirectory.FullName == root).AvailableFreeSpace;

            static bool IsReady(DriveInfo d) {
                try {
                    return d.IsReady;
                } catch (IOException) {
                    return false;
                }
            }
        }

        private async Task EmitTransitionAsync(
            DiskSpaceLevel previous, DiskSpaceLevel level, long freeBytes, string saveDir, CancellationToken ct) {
            var now = DateTimeOffset.UtcNow;
            // Close whatever issue the previous level had opened before opening the new one — keeps a single
            // open disk-space issue and resolves it on recovery.
            await _diagnostics.ClearOpenEventsByTypeAsync(EventType, now, ct);

            var freeGb = freeBytes / (1024.0 * 1024.0 * 1024.0);
            if (level == DiskSpaceLevel.Ok) {
                LogRecovered(freeGb, saveDir);
                return;
            }

            var (health, severity, words) = level == DiskSpaceLevel.Critical
                ? (DiagnosticHealth.Red, NotificationSeverity.Critical, "critically low")
                : (DiagnosticHealth.Yellow, NotificationSeverity.Warning, "low");
            var description = $"Disk space {words}: {freeGb:F1} GB free on the image save volume ({saveDir}).";
            const string recommendedAction = "Free up space or change the save directory before capturing more frames.";
            LogLowSpace(words, previous, level, freeGb, saveDir);

            await _diagnostics.CreateEventAsync(
                new DiagnosticEventDto(
                    Id: Guid.NewGuid(),
                    EventType: EventType,
                    Severity: health,
                    Description: description,
                    DetectedUtc: now,
                    ClearedUtc: null,
                    AutoActionTaken: false,
                    AutoActionDescription: null),
                recommendedAction,
                autoCorrectible: false,
                ct);

            // The §54 inbox entry is gated on the user's OnDiskSpaceLow trigger toggle; the diagnostic above is not.
            if (_profileStore.GetNotificationsSettings().OnDiskSpaceLow) {
                await _notifications.CreateAsync(
                    new NotificationDto(
                        Id: Guid.NewGuid(),
                        PostedUtc: now,
                        Severity: severity,
                        Category: NotificationCategory.Storage,
                        Title: "Low disk space",
                        Message: description,
                        Read: false,
                        Dismissed: false,
                        DismissedUtc: null,
                        Payload: null,
                        RelatedEntityType: null,
                        RelatedEntityId: null),
                    ct);
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Disk-space check failed; will retry next tick.")]
        private partial void LogCheckFailed(Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Disk space recovered: {FreeGb} GB free on {SaveDir}.")]
        private partial void LogRecovered(double freeGb, string saveDir);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Disk space {Words} ({Previous}->{Level}): {FreeGb} GB free on {SaveDir}.")]
        private partial void LogLowSpace(string words, DiskSpaceLevel previous, DiskSpaceLevel level, double freeGb, string saveDir);
    }
}
