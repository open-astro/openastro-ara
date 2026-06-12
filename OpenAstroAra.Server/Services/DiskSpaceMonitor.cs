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
    /// frames fit) and come from the profile's §29 storage settings (<c>MinFreeDiskWarnGb</c> /
    /// <c>MinFreeDiskCriticalGb</c>, read live each tick), falling back to 10 GiB (Low) / 2 GiB (Critical) when
    /// the stored pair is non-positive or inverted.
    /// </summary>
    public sealed partial class DiskSpaceMonitor : BackgroundService {

        public const long DefaultLowBytes = 10L * 1024 * 1024 * 1024;      // 10 GiB
        public const long DefaultCriticalBytes = 2L * 1024 * 1024 * 1024;  //  2 GiB
        public const string EventType = "storage.disk_space";

        private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);

        private readonly IProfileStore _profileStore;
        private readonly IDiagnosticsService _diagnostics;
        private readonly INotificationService _notifications;
        private readonly ISequencerService _sequencer;
        private readonly ILogger<DiskSpaceMonitor> _logger;
        private readonly TimeSpan _interval;
        private DiskSpaceLevel _last = DiskSpaceLevel.Ok;

        public DiskSpaceMonitor(
            IProfileStore profileStore,
            IDiagnosticsService diagnostics,
            INotificationService notifications,
            ISequencerService sequencer,
            ILogger<DiskSpaceMonitor> logger,
            TimeSpan? interval = null) {
            _profileStore = profileStore;
            _diagnostics = diagnostics;
            _notifications = notifications;
            _sequencer = sequencer;
            _logger = logger;
            _interval = interval ?? DefaultInterval;
        }

        /// <summary>Map free bytes to a level. Boundaries are inclusive on the low side (≤ threshold trips it).</summary>
        public static DiskSpaceLevel Evaluate(long freeBytes, long lowBytes, long criticalBytes)
            => freeBytes <= criticalBytes ? DiskSpaceLevel.Critical
             : freeBytes <= lowBytes ? DiskSpaceLevel.Low
             : DiskSpaceLevel.Ok;

        /// <summary>
        /// Convert the profile's whole-GiB warn/critical free-space thresholds to bytes. A non-positive or
        /// inverted pair (critical ≥ warn — which <see cref="Evaluate"/> would read as "Critical for almost any
        /// value", since it tests the critical arm first) is rejected in favour of the built-in 10/2 GiB
        /// defaults, so a mis-set profile degrades to sane behaviour instead of crying Critical constantly.
        /// </summary>
        public static (long LowBytes, long CriticalBytes) ResolveThresholdBytes(int warnGb, int criticalGb) {
            // Both must be ≥ 1 GiB and warn strictly above critical; otherwise fall back to the defaults.
            if (warnGb < 1 || criticalGb < 1 || warnGb <= criticalGb) {
                return (DefaultLowBytes, DefaultCriticalBytes);
            }
            const long gib = 1024L * 1024 * 1024;
            return (warnGb * gib, criticalGb * gib);
        }

        /// <summary>
        /// Whether a transition warrants a <em>new</em> low-disk notification: only when it got strictly worse
        /// (Ok→Low, Low→Critical). Recovery and a Critical→Low improvement don't notify (the user was already
        /// alerted at the worse level), so an oscillating disk doesn't pile up inbox entries. The diagnostic,
        /// by contrast, always updates to reflect the current level.
        /// </summary>
        public static bool ShouldNotify(DiskSpaceLevel previous, DiskSpaceLevel current)
            => current != DiskSpaceLevel.Ok && current > previous;

        /// <summary>
        /// Whether the §29 <c>OnDiskSpaceCritical</c> safety policy says to halt the running sequence (the only
        /// non-warn action: "abort"). Anything else — including the "warn" default, an unknown value, or null —
        /// means warn-only, so a mis-set policy degrades to the safe behaviour.
        /// </summary>
        public static bool ShouldAbortSequence(string? onDiskSpaceCritical)
            => string.Equals(onDiskSpaceCritical?.Trim(), "abort", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Of the mounted volume roots, the one that actually contains <paramref name="fullPath"/> — the longest
        /// root that is a prefix of the path (so a dedicated <c>/media/openastroara</c> mount wins over the
        /// <c>/</c> root). Returns null when none match (e.g. the save mount isn't present yet). Pure, so the
        /// cross-platform path→volume choice is unit-testable without real drives.
        /// </summary>
        public static string? LongestPrefixRoot(
            string fullPath, IEnumerable<string> driveRoots, StringComparison comparison = StringComparison.Ordinal) {
            string? best = null;
            var bestLen = -1;
            foreach (var root in driveRoots) {
                if (root.Length > bestLen && IsUnderRoot(fullPath, root, comparison)) {
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
        // pure helper is deterministic across hosts (it's tested with Windows-style roots on a Unix CI box). The
        // caller picks the comparison: case-insensitive on Windows (DriveInfo uppercases "D:\" but a user's save
        // dir may be "d:\..."), case-sensitive elsewhere (POSIX paths are case-sensitive).
        private static bool IsUnderRoot(string fullPath, string root, StringComparison comparison) {
            if (root.Length == 0 || !fullPath.StartsWith(root, comparison)) {
                return false;
            }
            return fullPath.Length == root.Length
                || IsSeparator(root[^1])
                || IsSeparator(fullPath[root.Length]);
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Best-effort monitor loop: a profile read, DriveInfo probe, or diagnostics/notification write can throw arbitrary IO/driver exceptions — each tick is logged and skipped so a transient failure never tears down the daemon's hosted services.")]
        protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
            // Startup reconciliation: clear any disk-space event orphaned by a prior run. We've emitted nothing
            // yet (_last == Ok), so this only touches a stale event — if the disk is still low, the first tick's
            // Ok→Low transition re-opens it; if it recovered while we were down, it stays cleared.
            try {
                await _diagnostics.ClearOpenEventsByTypeAsync(EventType, DateTimeOffset.UtcNow, stoppingToken);
            } catch (OperationCanceledException) {
                return;
            } catch (Exception ex) {
                LogCheckFailed(ex);
            }

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
            var storage = _profileStore.GetStorageSettings();
            var saveDir = storage.SaveDirectory;
            if (string.IsNullOrWhiteSpace(saveDir)) {
                return;
            }
            var freeBytes = TryGetFreeBytes(saveDir);
            if (freeBytes is null) {
                return; // volume not resolvable yet (e.g. the save mount isn't attached) — don't false-alarm
            }

            // Thresholds come from the profile (live), so a settings change takes effect on the next tick.
            var (lowBytes, criticalBytes) = ResolveThresholdBytes(storage.MinFreeDiskWarnGb, storage.MinFreeDiskCriticalGb);
            var level = Evaluate(freeBytes.Value, lowBytes, criticalBytes);
            if (level == _last) {
                return; // only act on transitions, so a sustained low disk doesn't re-fire every tick
            }
            await EmitTransitionAsync(_last, level, freeBytes.Value, saveDir, ct);
            // Commit the new level only after a successful emit: if EmitTransitionAsync throws partway (e.g. the
            // clear succeeds but CreateEventAsync hits a transient DB error), _last stays put so the next tick
            // re-detects the transition and retries instead of silently swallowing it.
            _last = level;
        }

        private static long? TryGetFreeBytes(string saveDir) {
            // Windows drive letters are case-insensitive (DriveInfo uppercases "D:\" but the save dir may be
            // "d:\..."); POSIX paths are case-sensitive. Match both the prefix test and the drive lookup the same way.
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var full = Path.GetFullPath(saveDir);
            var drives = DriveInfo.GetDrives().Where(IsReady).ToList();
            var root = LongestPrefixRoot(full, drives.Select(d => d.RootDirectory.FullName), comparison);
            if (root is null) {
                return null;
            }
            try {
                return drives.First(d => string.Equals(d.RootDirectory.FullName, root, comparison)).AvailableFreeSpace;
            } catch (IOException) {
                return null; // the volume went away between the IsReady probe and the read — honor the Try* contract
            }

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

            // The §54 inbox entry is gated on the user's OnDiskSpaceLow trigger toggle + a strictly-worse
            // transition (see ShouldNotify) so an oscillating disk doesn't accumulate entries; the diagnostic
            // above is not gated either way (it always reflects the current level).
            if (ShouldNotify(previous, level) && _profileStore.GetNotificationsSettings().OnDiskSpaceLow) {
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

            // §29 hard-stop policy: on entering Critical, if OnDiskSpaceCritical=abort, halt any running
            // sequence so it doesn't keep capturing into a near-full disk. Warn-only otherwise (the default).
            if (level == DiskSpaceLevel.Critical
                && ShouldAbortSequence(_profileStore.GetSafetyPolicies().OnDiskSpaceCritical)) {
                var aborted = await _sequencer.AbortActiveRunsAsync(ct);
                if (aborted > 0) {
                    LogAbortedSequences(aborted, freeGb, saveDir);
                    await _notifications.CreateAsync(
                        new NotificationDto(
                            Id: Guid.NewGuid(),
                            PostedUtc: now,
                            Severity: NotificationSeverity.Critical,
                            Category: NotificationCategory.Storage,
                            Title: "Sequence halted — disk critically low",
                            Message: $"Aborted {aborted} running sequence(s): {freeGb:F1} GB free on {saveDir} (your OnDiskSpaceCritical policy is set to abort).",
                            Read: false,
                            Dismissed: false,
                            DismissedUtc: null,
                            Payload: null,
                            RelatedEntityType: null,
                            RelatedEntityId: null),
                        ct);
                }
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "Disk-space check failed; will retry next tick.")]
        private partial void LogCheckFailed(Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Disk space recovered: {FreeGb} GB free on {SaveDir}.")]
        private partial void LogRecovered(double freeGb, string saveDir);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Disk space {Words} ({Previous}->{Level}): {FreeGb} GB free on {SaveDir}.")]
        private partial void LogLowSpace(string words, DiskSpaceLevel previous, DiskSpaceLevel level, double freeGb, string saveDir);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Disk critically low ({FreeGb} GB on {SaveDir}); OnDiskSpaceCritical=abort halted {Count} running sequence(s).")]
        private partial void LogAbortedSequences(int count, double freeGb, string saveDir);
    }
}
