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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §63.4 profile-lifecycle hooks — the delete half of the playbook's lifecycle table. The
/// connect path already selects-or-creates the twin (PHD2Guider.EnsureAraGuiderProfileAsync);
/// this partial previews and removes the twin when the user asks for it on an ARA profile
/// delete — now when the guider is connected and idle, otherwise from a persisted queue on
/// the next connect or idle moment — so orphaned guider profiles and their dark libraries
/// don't accumulate on the daemon, and the user is told what happened.
/// </summary>
public sealed partial class GuiderService {

    // §63.4 — the pending-delete queue and the live ARA profile list it is checked against
    // (a queued twin name that a live profile maps to again is dropped, never deleted).
    private readonly IGuiderTwinDeleteStore? _twinDeleteQueue;
    private readonly Func<IReadOnlyList<(Guid Id, string? Name)>>? _araProfilesResolver;
    // One twin delete at a time: the REST delete and the idle/connect drains must not interleave
    // their release → delete → reconnect windows.
    private readonly SemaphoreSlim _twinDeleteLock = new(1, 1);

    /// <summary>The names an ARA profile's twin may carry on the daemon: the display name (current
    /// scheme) and the legacy <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> name (created before the rename
    /// migration, or never migrated because the guider has not reconnected since).</summary>
    internal static IReadOnlyList<string> TwinNames(string? araProfileName, Guid araProfileId) =>
        new[] { PHD2Guider.AraGuiderDisplayProfileName(araProfileName), PHD2Guider.AraGuiderProfileName(araProfileName, araProfileId) };

    /// <inheritdoc/>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A preview must never fail the dialog: any RPC/socket fault degrades to 'unknown' with the fault's text as the reason.")]
    public async Task<GuiderTwinStatus> DescribeAraGuiderTwinAsync(string? araProfileName, Guid araProfileId, CancellationToken ct) {
        var guider = ConnectedGuiderOrNull();
        if (guider is null) {
            return new GuiderTwinStatus(Exists: null, SelectedOnDaemon: false, CanDeleteNow: false,
                Reason: "No guider is connected. It will be deleted the next time the guider connects.");
        }
        try {
            var presence = await guider.DescribeGuiderProfilesAsync(TwinNames(araProfileName, araProfileId), ct).ConfigureAwait(false);
            if (!presence.Exists) {
                return new GuiderTwinStatus(Exists: false, SelectedOnDaemon: false, CanDeleteNow: false, Reason: null);
            }
            if (!PHD2Guider.IsIdleAppState(presence.AppState)) {
                return new GuiderTwinStatus(Exists: true, presence.Selected, CanDeleteNow: false, Reason: BusyReason(presence.AppState));
            }
            return new GuiderTwinStatus(Exists: true, presence.Selected, CanDeleteNow: true, Reason: null);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogGuiderTwinPreviewFailed(ex, araProfileName);
            return new GuiderTwinStatus(Exists: null, SelectedOnDaemon: false, CanDeleteNow: false, Reason: ex.Message);
        }
    }

    private static string BusyReason(string? appState) =>
        $"The guider is {DescribeAppState(appState)}. It will be deleted once guiding stops.";

    private static string DescribeAppState(string? appState) {
        // PhdAppState's tokens are static readonly, not const, so no switch pattern.
        if (string.IsNullOrEmpty(appState)) return "busy";
        if (appState == PhdAppState.GUIDING) return "guiding";
        if (appState == PhdAppState.CALIBRATING) return "calibrating";
        if (appState == PhdAppState.LOOPING) return "looping exposures";
        if (appState == PhdAppState.LOSTLOCK) return "guiding (star lost)";
        if (appState == PhdAppState.PAUSED) return "paused";
        if (appState == PhdAppState.SELECTED) return "holding a star";
        return appState;
    }

    /// <inheritdoc/>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The ARA profile is already gone; any RPC/socket/protocol fault here must degrade to a reported outcome (queued or failed), never a thrown error over the delete response.")]
    public async Task<GuiderTwinDeleteOutcome> DeleteAraGuiderProfileAsync(
            string? araProfileName, Guid araProfileId, bool deleteDarkFiles, CancellationToken ct) {
        var names = TwinNames(araProfileName, araProfileId);
        var guider = ConnectedGuiderOrNull();
        if (guider is null) {
            Enqueue(araProfileId, araProfileName, names, deleteDarkFiles);
            LogGuiderProfileDeleteQueued(names[0], "no guider connected");
            return new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Queued, "No guider is connected. It will be deleted the next time the guider connects.");
        }
        try {
            await _twinDeleteLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                var result = await guider.DeleteGuiderProfilesAsync(names, deleteDarkFiles, reconnectEquipment: true, ct).ConfigureAwait(false);
                switch (result.Kind) {
                    case GuiderProfileDeleteKind.Deleted:
                        _twinDeleteQueue?.Remove(araProfileId);
                        return new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Deleted, null);
                    case GuiderProfileDeleteKind.NotFound:
                        _twinDeleteQueue?.Remove(araProfileId);
                        return new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.NotFound, null);
                    case GuiderProfileDeleteKind.Busy:
                        Enqueue(araProfileId, araProfileName, names, deleteDarkFiles);
                        LogGuiderProfileDeleteQueuedBusy(names[0], result.Detail);
                        return new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Queued, BusyReason(result.Detail));
                    default:
                        LogGuiderProfileDeleteRefused(names[0], result.Detail ?? "(no reason given)");
                        return new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Failed, result.Detail ?? "The guider refused to delete the profile.");
                }
            } finally {
                ReleaseTwinDeleteLock();
            }
        } catch (OperationCanceledException) {
            // The caller's bound ran out (the daemon took too long) — the delete is not lost, it waits
            // for the next connect or idle drain.
            Enqueue(araProfileId, araProfileName, names, deleteDarkFiles);
            LogGuiderProfileDeleteQueued(names[0], "the guider did not answer in time");
            return new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Queued, "The guider did not answer in time. It will be deleted once the guider is free.");
        } catch (Exception ex) {
            LogGuiderProfileDeleteFailed(ex, names[0]);
            return new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Failed, ex.Message);
        }
    }

    private void ReleaseTwinDeleteLock() {
        try {
            _twinDeleteLock.Release();
        } catch (ObjectDisposedException) {
            // Dispose raced a drain's tail — nothing else will ever wait on the lock.
        }
    }

    private void Enqueue(Guid araProfileId, string? araProfileName, IReadOnlyList<string> names, bool deleteDarkFiles) {
        if (_twinDeleteQueue is null) {
            LogGuiderProfileDeleteSkipped(names[0]);
            return;
        }
        _twinDeleteQueue.Add(new PendingGuiderTwinDelete(araProfileId, araProfileName, names, deleteDarkFiles, DateTimeOffset.UtcNow));
    }

    /// <summary>True when a queued twin delete is waiting — the ping loop's cheap pre-check.</summary>
    internal bool HasPendingTwinDeletes => _twinDeleteQueue is { } q && q.List().Count > 0;

    /// <summary>
    /// §63.4 — run the queued twin deletes against <paramref name="guider"/>. Called from the connect
    /// path (after <c>get_profiles</c>, before the twin ensure; <paramref name="reconnectEquipment"/>
    /// false because the connect's own steps re-select and reconnect) and from the ping loop when the
    /// connected guider is idle. A queued name that a LIVE ARA profile maps to again (the user
    /// recreated a profile with the same name since) is dropped, not deleted. A busy guider stops the
    /// drain; the rest waits for the next pass. Never throws except on cancellation.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background drain: a transport fault ends this pass with a log line; the queue keeps the entry for the next connect or idle pass.")]
    internal async Task DrainPendingTwinDeletesAsync(PHD2Guider guider, bool reconnectEquipment, CancellationToken ct) {
        if (_twinDeleteQueue is null) {
            return;
        }
        var pending = _twinDeleteQueue.List();
        if (pending.Count == 0) {
            return;
        }
        var live = _araProfilesResolver?.Invoke() ?? Array.Empty<(Guid, string?)>();
        var liveNames = live.SelectMany(p => TwinNames(p.Name, p.Id)).ToList();
        await _twinDeleteLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            foreach (var entry in pending) {
                var names = entry.TwinNames.Where(n => !liveNames.Any(l => SameTwin(l, n))).ToList();
                if (names.Count == 0) {
                    _twinDeleteQueue.Remove(entry.AraProfileId);
                    LogGuiderProfileDeleteDroppedLive(entry.TwinNames[0]);
                    continue;
                }
                try {
                    var result = await guider.DeleteGuiderProfilesAsync(names, entry.DeleteDarkFiles, reconnectEquipment, ct).ConfigureAwait(false);
                    switch (result.Kind) {
                        case GuiderProfileDeleteKind.Deleted:
                        case GuiderProfileDeleteKind.NotFound:
                            _twinDeleteQueue.Remove(entry.AraProfileId);
                            LogGuiderProfileDeleteDrained(names[0], result.Kind);
                            break;
                        case GuiderProfileDeleteKind.Busy:
                            return; // not idle after all — the next pass retries every remaining entry
                        default:
                            // The daemon said no (not the equipment-connected refusal: that is handled
                            // by the release window). Retrying every 10 s would only repeat the refusal;
                            // the twin stays, the warning says so, and the queue lets it go.
                            _twinDeleteQueue.Remove(entry.AraProfileId);
                            LogGuiderProfileDeleteRefused(names[0], result.Detail ?? "(no reason given)");
                            break;
                    }
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    LogGuiderProfileDeleteFailed(ex, names[0]);
                    return;
                }
            }
        } finally {
            ReleaseTwinDeleteLock();
        }
    }

    /// <summary>
    /// #1156 clone hook — a duplicated ARA profile gets its twin cloned from the source
    /// profile's twin (<c>create_profile</c> + <c>copy_from</c>), so calibration survives the
    /// duplicate. Best-effort like the delete hook: no connected guider, identical twin
    /// names (identically-named ARA profiles share a twin), a missing source twin or a
    /// daemon refusal all log and return false. Never throws.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lifecycle hook: the ARA create already succeeded; any RPC/socket fault degrades to a logged skip so the fire-and-forget caller never sees an unobserved exception.")]
    public async Task<bool> TryCloneAraGuiderProfileAsync(string? sourceAraProfileName, string? newAraProfileName, CancellationToken ct) {
        var source = PHD2Guider.AraGuiderDisplayProfileName(sourceAraProfileName);
        var target = PHD2Guider.AraGuiderDisplayProfileName(newAraProfileName);
        if (SameTwin(source, target)) {
            return false; // same display name → same twin already
        }
        var guider = ConnectedGuiderOrNull();
        if (guider is null) {
            LogGuiderProfileCloneSkipped(target);
            return false;
        }
        try {
            return await guider.CloneGuiderProfileAsync(source, target, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            LogGuiderProfileCloneFailed(ex, source, target);
            return false;
        }
    }

    /// <summary>
    /// #1156 rename hook — a renamed ARA profile keeps its twin (<c>rename_profile</c>) instead
    /// of leaving the calibrated one orphaned and getting a blank one on the next connect.
    /// Tries the display-name twin first, then the legacy <c>ara-&lt;slug&gt;-&lt;id8&gt;</c> name.
    /// Best-effort like the delete hook; never throws. The caller decides whether the old
    /// name is shared with another ARA profile (then the twin must stay) — this method only
    /// talks to the daemon.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lifecycle hook: the ARA rename already succeeded; any RPC/socket fault degrades to a logged skip so the fire-and-forget caller never sees an unobserved exception.")]
    public async Task<bool> TryRenameAraGuiderProfileAsync(string? oldAraProfileName, Guid araProfileId, string? newAraProfileName, CancellationToken ct) {
        var oldName = PHD2Guider.AraGuiderDisplayProfileName(oldAraProfileName);
        var legacyName = PHD2Guider.AraGuiderProfileName(oldAraProfileName, araProfileId);
        var newName = PHD2Guider.AraGuiderDisplayProfileName(newAraProfileName);
        if (SameTwin(oldName, newName)) {
            return false; // a case-only change maps to the same twin (the daemon's names are case-insensitive)
        }
        var guider = ConnectedGuiderOrNull();
        if (guider is null) {
            LogGuiderProfileRenameSkipped(oldName);
            return false;
        }
        foreach (var candidate in new[] { oldName, legacyName }) {
            try {
                if (await guider.RenameGuiderProfileAsync(candidate, newName, ct).ConfigureAwait(false)) {
                    return true;
                }
            } catch (Exception ex) {
                LogGuiderProfileRenameFailed(ex, candidate, newName);
            }
        }
        return false;
    }

    /// <summary>Two ARA display names map to one twin when they differ only by case: the daemon's
    /// profile-name uniqueness and <c>ResolveAraProfileSelection</c> are both case-insensitive.</summary>
    internal static bool SameTwin(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal PHD2Guider? ConnectedGuiderOrNull() {
        lock (_gate) {
            return !_disposed && _state == EquipmentConnectionState.Connected ? _guider : null;
        }
    }

    [LoggerMessage(EventId = 6363, Level = LogLevel.Information, Message = "#1156 guider profile '{ProfileName}' not cloned — no guider connected (the next connect creates a blank twin)")]
    private partial void LogGuiderProfileCloneSkipped(string profileName);

    [LoggerMessage(EventId = 6364, Level = LogLevel.Warning, Message = "#1156 cloning guider profile '{Source}' → '{Target}' failed — the ARA duplicate already succeeded; the next connect creates a blank twin")]
    private partial void LogGuiderProfileCloneFailed(Exception ex, string source, string target);

    [LoggerMessage(EventId = 6365, Level = LogLevel.Information, Message = "#1156 guider profile '{ProfileName}' not renamed — no guider connected (the next connect creates a blank twin under the new name)")]
    private partial void LogGuiderProfileRenameSkipped(string profileName);

    [LoggerMessage(EventId = 6366, Level = LogLevel.Warning, Message = "#1156 renaming guider profile '{Source}' → '{Target}' failed — the ARA rename already succeeded")]
    private partial void LogGuiderProfileRenameFailed(Exception ex, string source, string target);

    /// <summary>The selected-twin guard, pure for tests: PHD2 profile names are exact
    /// identifiers, so the comparison is Ordinal; a daemon with no selected profile
    /// (null) never blocks a delete.</summary>
    internal static bool IsTwinSelectedOnDaemon(string twinName, string? daemonSelectedName) =>
        string.Equals(daemonSelectedName, twinName, StringComparison.Ordinal);

    [LoggerMessage(EventId = 6360, Level = LogLevel.Information, Message = "§63.4 guider profile '{ProfileName}' not cleaned up — no guider connected and no delete queue configured (the twin, if any, remains on the daemon)")]
    private partial void LogGuiderProfileDeleteSkipped(string profileName);

    [LoggerMessage(EventId = 6361, Level = LogLevel.Information, Message = "§63.4 guider profile '{ProfileName}' delete queued ({Why}) — it runs on the next guider connect or idle moment")]
    private partial void LogGuiderProfileDeleteQueued(string profileName, string why);

    [LoggerMessage(EventId = 6374, Level = LogLevel.Information, Message = "§63.4 guider profile '{ProfileName}' delete queued (the guider is {AppState}) — it runs on the next idle moment")]
    private partial void LogGuiderProfileDeleteQueuedBusy(string profileName, string? appState);

    [LoggerMessage(EventId = 6362, Level = LogLevel.Warning, Message = "§63.4 guider profile '{ProfileName}' cleanup failed — the ARA profile delete already succeeded; the twin remains on the daemon")]
    private partial void LogGuiderProfileDeleteFailed(Exception ex, string profileName);

    [LoggerMessage(EventId = 6367, Level = LogLevel.Warning, Message = "§63.4 guider profile '{ProfileName}' not deleted — the daemon refused: {Reason}")]
    private partial void LogGuiderProfileDeleteRefused(string profileName, string reason);

    [LoggerMessage(EventId = 6369, Level = LogLevel.Information, Message = "§63.4 queued guider profile delete '{ProfileName}' drained ({Kind})")]
    private partial void LogGuiderProfileDeleteDrained(string profileName, GuiderProfileDeleteKind kind);

    [LoggerMessage(EventId = 6370, Level = LogLevel.Information, Message = "§63.4 queued guider profile delete '{ProfileName}' dropped — a live ARA profile maps to that twin again")]
    private partial void LogGuiderProfileDeleteDroppedLive(string profileName);

    [LoggerMessage(EventId = 6371, Level = LogLevel.Warning, Message = "§63.4 guider twin preview for ARA profile '{ProfileName}' failed")]
    private partial void LogGuiderTwinPreviewFailed(Exception ex, string? profileName);
}
