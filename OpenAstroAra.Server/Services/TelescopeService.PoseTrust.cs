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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// #2070 — the mount's position after a power loss. A mount with no absolute encoders wakes "at Home"
// wherever it points, so every move that trusts the position model (GoTo, Park, polar alignment, the
// flip, a sequence's Find Home) is refused until Home is confirmed. Sync, the pad, Stop, tracking and
// Unpark stay allowed: the user must always be able to stop the mount and back it off. #2173 — so does
// the user's Home (FindHome, the mount's home-sensor search): it is the way out, and a search that ends
// with the mount reporting Home trusts the position again by itself.
public sealed partial class TelescopeService {

    // Under _gate. Internal for tests.
    internal MountPoseTrust PoseTrust { get; } = new();

    // #2070 — keeps the unverified flag across daemon restarts. Optional: without it the flag is in memory only.
    private readonly IMountPoseStore? _poseStore;

    // Orders the store writes: the state is read under _gate and written under this lock, so two writes
    // can never land out of order. Never taken while _gate is held.
    private readonly object _posePersistLock = new();

    // Test knob: the clock of the in-session quiet window.
    internal TimeProvider PoseClock { get; init; } = TimeProvider.System;

    private static readonly TimeSpan PoseReadTimeout = TimeSpan.FromSeconds(10);

    // Caller holds the gate (ResetSessionLocked).
    private void BeginPoseSessionLocked() {
        var id = _lifecycle.Device?.UniqueId;
        PoseTrust.BeginSession(id, id is null ? null : _poseStore?.UnverifiedReason(id));
    }

    public async Task<string?> PositionUnverifiedReasonAsync(CancellationToken ct) {
        ITelescopeDevice? client;
        lock (_gate) {
            client = _lifecycle.Live;
        }
        return client is null ? null : await PoseBlockReasonAsync(client, ct).ConfigureAwait(false);
    }

    // Null when the move may go; otherwise the reason. Before the session's first tick it reads the mount
    // once (bounded), as the #1124 equatorial-system read does: a GoTo right after a reconnect must not
    // slip through the window before the first refresh.
    private async Task<string?> PoseBlockReasonAsync(ITelescopeDevice client, CancellationToken ct) {
        lock (_gate) {
            if (PoseTrust.Observed || PoseTrust.Reason is not null) {
                return PoseTrust.BlockReason;
            }
        }
        // ReadPoseBestEffort never throws, so a task abandoned on a timeout needs no observer.
        var read = Task.Run(() => ReadPoseBestEffort(client), CancellationToken.None);
        (bool AtHome, bool? Slewing, double? DecDeg) pose;
        try {
            pose = await read.WaitAsync(PoseReadTimeout, ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            pose = (false, null, null);
        }
        var flagged = false;
        string? reason;
        lock (_gate) {
            if (!_lifecycle.IsLive(client)) {
                return MountPoseTrust.NotReadYet;
            }
            if (!PoseTrust.Observed && pose.Slewing is bool slewing) {
                flagged = PoseTrust.Observe(pose.AtHome, slewing, pose.DecDeg, PoseClock.GetUtcNow());
            }
            reason = PoseTrust.BlockReason;
        }
        if (flagged) {
            AfterPoseFlagged(reason!);
        }
        return reason;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a failed Slewing or Dec read leaves the pose undecided (null), a failed AtHome reads as not at home, as ReadRuntime does.")]
    private static (bool AtHome, bool? Slewing, double? DecDeg) ReadPoseBestEffort(ITelescopeDevice c) {
        bool? slewing;
        try { slewing = c.Slewing; } catch (Exception) { slewing = null; }
        double? dec;
        try { dec = c.Declination; } catch (Exception) { dec = null; }
        bool atHome;
        try { atHome = c.AtHome; } catch (Exception) { atHome = false; }
        return (atHome, slewing, dec);
    }

    // An Ara axis move started, renewed or stopped: it explains the position changes it makes.
    private void NotePoseAxis(int axis, bool moving) {
        lock (_gate) {
            PoseTrust.NoteAxisMove(axis, moving, PoseClock.GetUtcNow());
        }
    }

    // Caller holds the gate: one refresh tick's reading. True when it made the position unverified. #2173 — an exact
    // (0, 0) is AlpacaBridge's placeholder after a failed status poll, not a position: it decides nothing, as Polar
    // Align's fresh reads treat it.
    private bool ObservePoseLocked(Contracts.TelescopeStateDto runtime, bool slewingUnknown) =>
        !slewingUnknown && PoseTrust.Observe(runtime.AtHome, runtime.State == "slewing",
            runtime is { RightAscensionHours: 0.0, DeclinationDegrees: 0.0 } ? null : runtime.DeclinationDegrees, PoseClock.GetUtcNow());

    // #2173 — an Ara GoTo, park or Home whose call or arrival wait runs: every position change until the end is its own.
    private int BeginPoseMove(double? gotoTargetDecDeg = null) {
        lock (_gate) {
            return PoseTrust.BeginCommandedMove(PoseClock.GetUtcNow(), gotoTargetDecDeg);
        }
    }

    private void EndPoseMove(int generation) {
        lock (_gate) {
            PoseTrust.EndCommandedMove(generation, PoseClock.GetUtcNow());
        }
    }

    // Test knobs: how often and how long the user's Home is watched for its end (#2173).
    internal TimeSpan HomeSearchPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan HomeSearchTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>#2173 — the user's Home (FindHome) was sent. A home-sensor search that ends with the mount still and
    /// reporting Home, on two polls in a row, trusts the position again with no eye check: if Ara drove it home, it
    /// is home. Only a search seen away from Home (<paramref name="leftHome"/>: the mount did not report Home when it was
    /// sent, or stopped reporting it while it ran) counts: AlpacaBridge's HAE29C search clears AtHome until it lands
    /// (AlpacaBridge#2420), while a mount with no sensor search sent Home at its false zero goes nowhere and reports Home
    /// throughout. Nothing is cleared when the mount reconnects, a stop lands after the Home (<paramref name="fence"/>), or
    /// the search does not end at Home within <see cref="HomeSearchTimeout"/>.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background watch boundary: a failed read is a poll that did not see Home; nothing escapes the fire-and-forget task.")]
    private async Task TrustPoseAfterHomeSearchAsync(ITelescopeDevice client, long fence, bool leftHome) {
        var deadline = DateTime.UtcNow + HomeSearchTimeout;
        var atHomePolls = 0;
        try {
            while (DateTime.UtcNow < deadline) {
                await Task.Delay(HomeSearchPollInterval, CancellationToken.None).ConfigureAwait(false);
                lock (_gate) {
                    if (!_lifecycle.IsLive(client)) {
                        return;
                    }
                }
                if (AbortedSince(fence)) {
                    LogHomeSearchNotTrusted("a stop landed after the Home");
                    return;
                }
                var pose = ReadPoseBestEffort(client);
                leftHome |= !pose.AtHome;
                atHomePolls = pose is { AtHome: true, Slewing: false, DecDeg: double } ? atHomePolls + 1 : 0;
                if (atHomePolls < 2) {
                    continue;
                }
                if (!leftHome) {
                    LogHomeSearchNotTrusted("the mount reported Home all along, so no search was seen; confirm Home by eye");
                    return;
                }
                bool cleared;
                string? device;
                lock (_gate) {
                    if (!_lifecycle.IsLive(client) || AbortedSince(fence)) {
                        return;
                    }
                    cleared = PoseTrust.Confirm(pose.DecDeg);
                    device = _lifecycle.Device?.Name;
                }
                LogHomeSearchTrusted(device ?? "?", cleared, pose.DecDeg!.Value);
                PersistPose();
                RefreshCacheOnce();
                return;
            }
            LogHomeSearchTimedOut(HomeSearchTimeout.TotalSeconds);
        } catch (Exception ex) {
            LogHomeSearchWatchFailed(ex);
        }
    }

    private void AfterPoseFlagged(string reason) {
        string? device;
        lock (_gate) {
            device = _lifecycle.Device?.Name;
        }
        LogPositionUnverified(device ?? "?", reason);
        PersistPose();
    }

    private void PersistPose() {
        if (_poseStore is null) {
            return;
        }
        lock (_posePersistLock) {
            string? id;
            string? reason;
            lock (_gate) {
                id = PoseTrust.DeviceId;
                reason = PoseTrust.Reason;
            }
            if (id is not null) {
                _poseStore.Store(id, reason);
            }
        }
    }

    /// <summary>The refusal a REST move answers while the position is unverified (409, ADR 0022).</summary>
    internal static RefusalException PositionUnverifiedRefusal(string reason) =>
        new(RefusalCatalog.MountPositionUnverified, PositionUnverifiedDetail(reason),
            new Dictionary<string, object?> { ["position_unverified_reason"] = reason });

    internal static string PositionUnverifiedDetail(string reason) {
        const string Fix = " The safe way out is Home: it runs the mount's own home-sensor search, and the position is trusted again once the mount reports Home."
            + " You can also run that search from the mount's hand controller or app, then confirm Home."
            + " GoTo, Park and polar alignment are blocked until then; Home, Stop, the direction pad and tracking still work.";
        return reason switch {
            MountPoseTrust.NotReadYet => "The mount's position has not been read since it connected; try again in a few seconds.",
            MountPoseTrust.AtHomeOnConnect => "Mount position unverified: the mount reported Home as soon as it connected, which a mount without absolute encoders also does after a power loss wherever it points." + Fix,
            MountPoseTrust.PoseJumpOnReconnect => string.Create(CultureInfo.InvariantCulture,
                $"Mount position unverified: its declination moved more than {MountPoseTrust.PoseJumpDeg:0}° while it was disconnected, as after a power loss.") + Fix,
            MountPoseTrust.AtHomeWithoutCommand => "Mount position unverified: it started reporting Home with no move commanded, as after a power blip." + Fix,
            MountPoseTrust.StopUnconfirmed => "Mount position unverified: Ara could not confirm that the mount stopped after polar alignment moved it, or the mount reconnected while polar alignment ran, so it may have moved on." + Fix,
            _ => string.Create(CultureInfo.InvariantCulture,
                $"Mount position unverified: its declination jumped more than {MountPoseTrust.PoseJumpDeg:0}° with no move commanded, as after a power blip.") + Fix,
        };
    }

    // REST moves: the ADR 0022 refusal.
    private async Task ThrowIfPoseUnverifiedAsync(ITelescopeDevice client, string op, CancellationToken ct) {
        if (await PoseBlockReasonAsync(client, ct).ConfigureAwait(false) is { } reason) {
            LogMoveRefusedUnverified(op, reason);
            throw PositionUnverifiedRefusal(reason);
        }
    }

    // Sequencer moves: a failed instruction with the same words. Never an op fault (§42.4): a refusal must
    // not count toward the abort-and-park escalation, whose park would be refused in turn.
    // stopTrackingOnRefusal: a refused park stops tracking first (best effort, bounded), so the mount is not left
    // tracking unattended when the park the plan asked for did not happen.
    private async Task ThrowIfPoseUnverifiedForSequenceAsync(ITelescopeDevice client, string op, CancellationToken ct,
            bool stopTrackingOnRefusal = false) {
        if (await PoseBlockReasonAsync(client, ct).ConfigureAwait(false) is { } reason) {
            LogMoveRefusedUnverified(op, reason);
            var detail = PositionUnverifiedDetail(reason);
            if (stopTrackingOnRefusal) {
                var stopped = false;
                try {
                    stopped = await Task.Run(() => SetTrackingEnabled(false), CancellationToken.None)
                        .WaitAsync(PoseReadTimeout, CancellationToken.None).ConfigureAwait(false);
                } catch (TimeoutException) {
                    stopped = false;
                }
                detail = (stopped ? "The mount was NOT parked; tracking was stopped instead. " : "The mount was NOT parked, and stopping tracking failed. ") + detail;
            }
            throw new SequenceEntityFailedException(detail);
        }
    }

    public void MarkPositionUnverified(string reason) {
        bool flagged;
        lock (_gate) {
            flagged = PoseTrust.Flag(reason);
        }
        if (flagged) {
            AfterPoseFlagged(reason); // logged and kept per device, as a flag the mount's own readings raise
        }
    }

    public async Task ConfirmHomeAsync(CancellationToken ct) {
        var client = RequireConnectedClient();
        (bool AtHome, bool? Slewing, double? DecDeg) pose;
        try {
            pose = await Task.Run(() => ReadPoseBestEffort(client), CancellationToken.None).WaitAsync(PoseReadTimeout, ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            throw new InvalidOperationException("the mount did not answer; Home was not confirmed");
        }
        if (!pose.AtHome || pose.Slewing != false) {
            throw new InvalidOperationException(
                "the mount does not report that it is at Home, so Home cannot be confirmed: run the mount's zero (home sensor) search from its hand controller or app, then confirm");
        }
        bool cleared;
        string? device;
        lock (_gate) {
            if (!_lifecycle.IsLive(client)) {
                throw new InvalidOperationException("telescope is not connected");
            }
            cleared = PoseTrust.Confirm(pose.DecDeg);
            device = _lifecycle.Device?.Name;
        }
        LogHomeConfirmed(device ?? "?", cleared);
        PersistPose();
        RefreshCacheOnce();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telescope '{Device}': position unverified ({Reason}); GoTo, Park and polar alignment are refused until a Home search ends at Home or Home is confirmed (#2070)")]
    private partial void LogPositionUnverified(string device, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "telescope.{Op} refused: the mount's position is unverified ({Reason}) (#2070)")]
    private partial void LogMoveRefusedUnverified(string op, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telescope '{Device}': the user confirmed the mount is at Home (cleared an unverified position: {Cleared}); its position is trusted again (#2070)")]
    private partial void LogHomeConfirmed(string device, bool cleared);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telescope '{Device}': the Home search ended with the mount at Home (Dec {DecDeg:F2}°; cleared an unverified position: {Cleared}); its position is trusted again (#2173)")]
    private partial void LogHomeSearchTrusted(string device, bool cleared, double decDeg);

    [LoggerMessage(Level = LogLevel.Information, Message = "telescope.findhome: the position was not trusted from this Home ({Why}) (#2173)")]
    private partial void LogHomeSearchNotTrusted(string why);

    [LoggerMessage(Level = LogLevel.Information, Message = "telescope.findhome: the position was not trusted from this Home (the mount did not report Home and still within {Seconds:0} s) (#2173)")]
    private partial void LogHomeSearchTimedOut(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "telescope.findhome: watching the Home search for its end failed (#2173)")]
    private partial void LogHomeSearchWatchFailed(Exception exception);
}
