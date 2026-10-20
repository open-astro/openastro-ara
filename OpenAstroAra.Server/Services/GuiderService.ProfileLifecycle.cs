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
            if (presence.AppState is null) {
                // The same rule as the delete: an unreadable state is never "now".
                return new GuiderTwinStatus(Exists: true, presence.Selected, CanDeleteNow: false,
                    Reason: "The guider did not report its state. It will be deleted once it is idle.");
            }
            if (!PHD2Guider.IsIdleAppState(presence.AppState)) {
                return new GuiderTwinStatus(Exists: true, presence.Selected, CanDeleteNow: false, Reason: BusyReason(presence.AppState));
            }
            if (GuiderHeldReason() is { } held) {
                return new GuiderTwinStatus(Exists: true, presence.Selected, CanDeleteNow: false, Reason: held);
            }
            return new GuiderTwinStatus(Exists: true, presence.Selected, CanDeleteNow: true, Reason: null);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogGuiderTwinPreviewFailed(ex, araProfileName);
            return new GuiderTwinStatus(Exists: null, SelectedOnDaemon: false, CanDeleteNow: false, Reason: ex.Message);
        }
    }

    /// <summary>Why the guider's equipment must not be released right now, or null when it may be:
    /// an imaging run owns it (a sequence stops guiding for an autofocus, a slew or a flip and the
    /// guider reads Stopped, but its next Start Guiding needs the equipment) or a polar alignment holds
    /// the guide camera. Shared by the preview, the REST delete and the idle drain.</summary>
    private string? GuiderHeldReason() {
        if (ActiveRunSession?.Invoke() is not null) {
            return "An imaging run is in progress. It will be deleted once the run ends.";
        }
        if (PolarAlignActive?.Invoke() == true) {
            return "A polar alignment is in progress. It will be deleted once it ends.";
        }
        return null;
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
        if (GuiderHeldReason() is { } held) {
            // Same rule as the idle drain: a Stopped guider under a run is still the run's.
            Enqueue(araProfileId, araProfileName, names, deleteDarkFiles);
            LogGuiderProfileDeleteQueued(names[0], "the guider's equipment is held by a run or an alignment");
            return new GuiderTwinDeleteOutcome(GuiderTwinDeleteStatus.Queued, held);
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
        // The user ticked the box knowing another ARA profile shares the twin (the dialog says so):
        // the queued delete must do what the immediate one would have, so the drain must not read
        // the survivor as "a live profile maps to it again".
        var shared = LiveTwinNames().Any(l => SameTwin(l, names[0]));
        _twinDeleteQueue.Add(new PendingGuiderTwinDelete(araProfileId, araProfileName, names, deleteDarkFiles, DateTimeOffset.UtcNow, shared));
    }

    private List<string> LiveTwinNames() =>
        (_araProfilesResolver?.Invoke() ?? Array.Empty<(Guid, string?)>()).SelectMany(p => TwinNames(p.Name, p.Id)).ToList();

    /// <summary>True when a queued twin delete is waiting — the ping loop's pre-check, answered from
    /// the store's lock-free count (never behind a queue write).</summary>
    internal bool HasPendingTwinDeletes => _twinDeleteQueue is { } q && q.PendingCount > 0;

    private int _idleDrainInFlight;

    /// <summary>
    /// §63.4 — from the ping tick: start a queued-delete drain when the guider is idle and nobody
    /// is relying on its equipment. Never under an imaging run (a sequence stops guiding for an
    /// autofocus, a slew or a flip and the guider reads Stopped — releasing the equipment then
    /// fails the run's next Start Guiding) nor under a polar alignment (it holds the guide camera).
    /// Fire-and-forget off the ping path, so the hang-detection ping never stalls behind a
    /// release → delete → reconnect cycle; one drain at a time.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget boundary: the drain logs its own faults; anything escaping would be an unobserved task exception.")]
    internal void StartIdleTwinDrainIfDue(PHD2Guider guider) {
        // Cheapest first, and nothing here takes a lock: the cached app state, the hold delegates,
        // then the store's volatile count. The ping tick must never wait behind a queue write.
        if (!PHD2Guider.IsIdleAppState(guider.AppState?.State) || GuiderHeldReason() is not null) {
            return; // busy, or the run / alignment owns the guider; the next idle tick asks again
        }
        if (!HasPendingTwinDeletes) {
            return;
        }
        if (Interlocked.CompareExchange(ref _idleDrainInFlight, 1, 0) != 0) {
            return;
        }
        _ = Task.Run(async () => {
            try {
                await DrainPendingTwinDeletesAsync(guider, reconnectEquipment: true, _shutdownCts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // shutdown
            } catch (Exception ex) {
                LogGuiderProfileDeleteFailed(ex, "(queued)");
            } finally {
                Interlocked.Exchange(ref _idleDrainInFlight, 0);
            }
        });
    }

    /// <summary>
    /// §63.4 — run the queued twin deletes against <paramref name="guider"/>. Called from the connect
    /// path (after <c>get_profiles</c>, before the twin ensure; <paramref name="reconnectEquipment"/>
    /// false because the connect's own steps re-select and reconnect) and from the ping loop when the
    /// connected guider is idle. A queued name that a LIVE ARA profile maps to again (the user
    /// recreated a profile with the same name since) is dropped, not deleted — unless the entry was
    /// queued as shared, which deletes regardless. A busy guider stops the drain; the rest waits for
    /// the next pass. Never throws except on cancellation.
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
        var liveNames = LiveTwinNames();
        await _twinDeleteLock.WaitAsync(ct).ConfigureAwait(false);
        try {
            foreach (var entry in pending) {
                if (reconnectEquipment && GuiderHeldReason() is not null) {
                    return; // a run or an alignment started since the tick looked; the next idle tick asks again
                }
                if (entry.TwinNames is not { Count: > 0 }) {
                    _twinDeleteQueue.Remove(entry.AraProfileId); // a hand-edited file: nothing to delete, never retry
                    continue;
                }
                // A twin queued as shared goes whatever the live list says (the user chose that); any
                // other queued name a live profile maps to again was recreated since, and stays.
                var names = entry.Shared
                    ? entry.TwinNames.ToList()
                    : entry.TwinNames.Where(n => !liveNames.Any(l => SameTwin(l, n))).ToList();
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
