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
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// #2070 — the mount's position after a power loss. A mount with no absolute encoders wakes "at Home"
// wherever it points, so every move that trusts the position model (GoTo, Park, polar alignment, the
// flip, a sequence's Find Home) is refused until Home is confirmed. Sync, the pad, Stop, tracking and
// Unpark stay allowed: the user must always be able to stop the mount and back it off. #2173 — so does
// the user's Home on every mount whose Alpaca CanFindHome is true, as NINA allows Find Home at any time: it
// is the way out. A search seen to run that ends with the mount reporting Home trusts the position again by
// itself only when the reason leaves the mount's own model intact (MountPoseTrust.HomeSearchMayTrust); when the
// model may be wrong (a power loss, a reset, a jump), the user confirms Home by eye after it
// (TrustPoseAfterHomeSearchAsync). A mount whose driver says its FindHome is a real home-sensor search
// (HomeIsSensorSearchAction) is trusted after such a Home for every reason. ADR 0003 rule 2: nothing here looks at
// the brand, model or driver name; only that capability decides.
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

    // Test knob (#2173 review): how long one guard read of the mount may take before it counts as failed.
    internal TimeSpan PoseReadTimeout { get; init; } = TimeSpan.FromSeconds(10);

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
        bool waiting;
        lock (_gate) {
            waiting = PoseTrust.Observed && PoseTrust.Reason is null && PoseTrust.PendingVerdict is not null;
            if (!waiting && (PoseTrust.Observed || PoseTrust.Reason is not null)) {
                return PoseTrust.BlockReason;
            }
        }
        if (waiting) {
            return await PoseBlockReasonOnceSettledAsync(ct).ConfigureAwait(false);
        }
        // ReadPoseBestEffort never throws, so a task abandoned on a timeout needs no observer.
        var read = Task.Run(() => ReadPoseBestEffort(client), CancellationToken.None);
        PoseReading pose;
        try {
            pose = await read.WaitAsync(PoseReadTimeout, ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            pose = new(null, null, null);
        }
        var flagged = false;
        string? reason;
        lock (_gate) {
            if (!_lifecycle.IsLive(client)) {
                return MountPoseTrust.NotReadYet;
            }
            if (!PoseTrust.Observed && pose.Slewing is bool slewing) {
                // A failed AtHome reads as not at Home, as the refresh tick's ReadRuntime reads it (a driver may not
                // implement it at all); the placeholder (0, 0) is already no Dec (ReadPoseBestEffort).
                flagged = PoseTrust.Observe(pose.AtHome ?? false, slewing, pose.DecDeg, PoseClock.GetUtcNow());
            }
            reason = PoseTrust.BlockReason;
        }
        if (flagged) {
            AfterPoseFlagged(reason!);
        }
        return reason;
    }

    // #2173 review — what one best-effort read of the mount answered; null = that read failed. A failed AtHome stays
    // unknown here: the Home watch must not take it as "away from Home".
    private readonly record struct PoseReading(bool? AtHome, bool? Slewing, double? DecDeg);

    // How long a guard waits for a verdict one reading suggested to be settled by the next readings (#2173 review).
    internal TimeSpan PendingVerdictWait { get; init; } = TimeSpan.FromSeconds(6);

    // #2173 review — one reading suggested a verdict and the next has not agreed yet: a GoTo or park now would drive by a
    // model that may just have reset. Wait (bounded) for the next readings to settle it, then answer; still pending at the
    // bound answers not_read_yet. Each refresh tick settles it, so a healthy link answers within about the confirm gap.
    private async Task<string?> PoseBlockReasonOnceSettledAsync(CancellationToken ct) {
        RefreshCacheOnce();
        var deadline = DateTime.UtcNow + PendingVerdictWait;
        while (true) {
            lock (_gate) {
                if (PoseTrust.Reason is not null || PoseTrust.PendingVerdict is null || DateTime.UtcNow >= deadline) {
                    return PoseTrust.BlockReason;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a failed Slewing, Dec or AtHome read leaves that field unknown (null); the callers decide what an unknown means.")]
    private static PoseReading ReadPoseBestEffort(ITelescopeDevice c) {
        bool? slewing;
        try { slewing = c.Slewing; } catch (Exception) { slewing = null; }
        double? dec;
        try { dec = c.Declination; } catch (Exception) { dec = null; }
        bool? atHome;
        try { atHome = c.AtHome; } catch (Exception) { atHome = null; }
        // #2173 review — an exact (0, 0) is AlpacaBridge's placeholder after a failed status poll, not a position, as the
        // refresh tick's reading treats it (ObservePoseLocked): no Dec. RA is read only for a Dec of exactly 0.
        if (dec is 0.0) {
            double? ra;
            try { ra = c.RightAscension; } catch (Exception) { ra = null; }
            if (ra is 0.0) {
                dec = null;
            }
        }
        return new(atHome, slewing, dec);
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

    // #2173 review — a REST GoTo that was dropped or failed: its target explains no later Dec.
    private void ClearPoseGotoTarget() {
        lock (_gate) {
            PoseTrust.ClearGotoTarget();
        }
    }

    // arrived false: the move failed, was refused or was given up on, so a GoTo's target explains nothing after it.
    private void EndPoseMove(int generation, bool arrived) {
        lock (_gate) {
            PoseTrust.EndCommandedMove(generation, PoseClock.GetUtcNow(), arrived);
        }
    }

    // Test knob: how long a REST park or a model Home is held as Ara's own move after its call returns (#2173 review).
    internal TimeSpan MoveSettleTimeout { get; init; } = TimeSpan.FromMinutes(5);

    // Test knob (#2173 review): a not-slewing read ends the hold only after the mount was seen slewing, or once this
    // long has passed: a driver may still read Slewing false right after Park returns, before its slew starts.
    internal TimeSpan MoveSettleGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>#2173 review — many drivers return from Park (or a model Home) at once, and the mount then moves for a
    /// minute or more. Over a slow link that drops the Slewing reads, that move would outlast the quiet window and land
    /// as a jump. So the move stays Ara's own until the mount reports <paramref name="arrived"/> or answers that it is
    /// not slewing (after it was seen slewing, or once <see cref="MoveSettleGrace"/> has passed), a stop lands after it,
    /// the mount reconnects, or <see cref="MoveSettleTimeout"/> passes. The deadlines run on the wall clock, not
    /// <see cref="PoseClock"/>: a test's manual clock would otherwise hold the move forever.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background hold boundary: a failed read is a poll that did not see the end; nothing escapes the fire-and-forget task.")]
    private async Task HoldPoseMoveUntilSettledAsync(ITelescopeDevice client, long fence, Func<ITelescopeDevice, bool> arrived) {
        var started = DateTime.UtcNow;
        var deadline = started + MoveSettleTimeout;
        var sawSlewing = false;
        try {
            while (DateTime.UtcNow < deadline) {
                lock (_gate) {
                    if (!_lifecycle.IsLive(client)) {
                        return;
                    }
                }
                if (AbortedSince(fence)) {
                    return;
                }
                bool done;
                try {
                    if (arrived(client)) {
                        done = true;
                    } else {
                        var slewing = ReadPoseBestEffort(client).Slewing;
                        sawSlewing |= slewing == true;
                        done = slewing == false && (sawSlewing || DateTime.UtcNow - started >= MoveSettleGrace);
                    }
                } catch (Exception) {
                    done = false; // a read that failed: the move may still run
                }
                if (done) {
                    return;
                }
                await Task.Delay(HomeSearchPollInterval, CancellationToken.None).ConfigureAwait(false);
            }
        } catch (Exception ex) {
            LogMoveSettleWatchFailed(ex);
        }
    }

    // #2173 — the mount's Alpaca CanFindHome, read fresh for the user's Home: a mount that cannot find Home keeps the
    // #2070 refusal while unverified, with the hand controller's search as the way out. Null when the read failed: the
    // caller then uses the connect's capability read, so the decision and the refusal's words agree (#2173 review).
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: a failed CanFindHome read is unknown; the caller falls back to the connect's read.")]
    private static bool? ReadCanFindHomeBestEffort(ITelescopeDevice c) {
        try {
            return c.CanFindHome;
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>#2173 — the Alpaca action AlpacaBridge lists in <c>SupportedActions</c> (only while connected) when the
    /// driver can say whether its FindHome is a real home-sensor search (AlpacaBridge #2550). The one place the name lives.</summary>
    internal const string HomeIsSensorSearchAction = "findhomeissensorsearch";

    /// <summary>#2173 — read once per connect with the capabilities (never kept across a reconnect: the capabilities are
    /// re-read for each session). True only when the action is listed (case-insensitive) and its reply says
    /// <c>sensor_search: true</c>. Not listed, a NotConnected or any other failed call, or a reply that does not parse:
    /// false (fail safe).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A driver may NotImplement SupportedActions/Action, answer NotConnected or fail the call; not a sensor search is the fail-safe answer.")]
    internal static bool ReadHomeIsSensorSearch(ITelescopeDevice c) {
        try {
            var actions = c.SupportedActions;
            if (actions is null || !actions.Any(a => string.Equals(a, HomeIsSensorSearchAction, StringComparison.OrdinalIgnoreCase))) {
                return false;
            }
            return ParseHomeIsSensorSearchReply(c.Action(HomeIsSensorSearchAction, string.Empty));
        } catch (Exception) {
            return false;
        }
    }

    /// <summary>#2173 — the action's reply, <c>{"sensor_search": bool, "method": "...", "model": "..."}</c>: true only for a
    /// JSON object whose <c>sensor_search</c> is the JSON literal <c>true</c>. A missing field, another type or text that
    /// is not JSON is false. <c>method</c> and <c>model</c> are never read: nothing branches on them (ADR 0003 rule 2).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Malformed JSON from a third-party driver is 'not a sensor search', not a daemon fault.")]
    internal static bool ParseHomeIsSensorSearchReply(string? reply) {
        if (string.IsNullOrWhiteSpace(reply)) {
            return false;
        }
        try {
            using var doc = JsonDocument.Parse(reply);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("sensor_search", out var flag)
                && flag.ValueKind == JsonValueKind.True;
        } catch (Exception) {
            return false;
        }
    }

    /// <summary>#2173 — the connect's read of the sensor-search capability; see
    /// <see cref="IMountMotionControl.HomeIsSensorSearch"/>.</summary>
    public bool HomeIsSensorSearch => HomeIsSensorSearchKnown;

    // #2173 — the connect's read of the sensor-search capability (false until read, and for every new session until re-read).
    private bool HomeIsSensorSearchKnown {
        get {
            lock (_gate) {
                return _capabilities?.HomeIsSensorSearch == true;
            }
        }
    }

    // The connect's capability read of CanFindHome (the refusals' wording only; Home itself reads it fresh).
    private bool CanFindHomeKnown {
        get {
            lock (_gate) {
                return _capabilities?.CanFindHome == true;
            }
        }
    }

    // Test knobs: how often and how long the user's Home is watched for its end (#2173).
    internal TimeSpan HomeSearchPollInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan HomeSearchTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>#2173 — the user's Home (FindHome) was sent, on any mount whose CanFindHome is true. A search seen to run
    /// that ends with the mount still and reporting Home, on two polls in a row, trusts the position again with no eye
    /// check, but only when the reason, or the driver's sensor-search capability (<see cref="HomeIsSensorSearchAction"/>),
    /// allows it (<see cref="MountPoseTrust.HomeSearchMayTrust"/>): without that capability Ara cannot tell a
    /// sensor search from a Home that drives by the mount's own model, so after a power loss, a reset or a jump the model
    /// may be the thing that is wrong, and the user confirms Home by eye after the Home. #2173 review — "seen to run" means, since the last poll that could not be
    /// read: one poll answered AtHome false with a real Dec (not AlpacaBridge's (0, 0) placeholder), and one answered
    /// Slewing true or the Dec moved more than <see cref="MountPoseTrust.PoseJumpDeg"/>. A failed AtHome is never "away
    /// from Home", and any poll that could not be read (the mount may have lost power and woken at a false zero in that
    /// gap) wipes what was seen before it. <paramref name="atSend"/> is the read just before FindHome went out. Nothing
    /// is cleared when the mount reconnects or its session changes in any way (<paramref name="sessionEpoch"/>, read when
    /// FindHome went out), a stop lands after the Home (<paramref name="fence"/>), or the search does not end at Home
    /// within <see cref="HomeSearchTimeout"/>. Only a FindHome Ara sent in this session that returned without error is
    /// watched: a FindHome that throws is no Home, and the reason stays.
    /// <para>"Seen to run" alone does not keep a false zero untrusted: after a pad nudge off the false zero, a model Home
    /// (a <c>:MH#</c>-style "go to where the model says Home is") drives back to it, is seen to run and lands at Home.
    /// That is why the reason decides, with no knowledge of the mount's brand or driver (ADR 0003 rule 2). "Seen to run",
    /// the two still reads and the gap wipe guard the reasons that may trust (a Home that never moves clears
    /// nothing).</para></summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background watch boundary: a failed read is a poll that did not see Home; nothing escapes the fire-and-forget task.")]
    private async Task TrustPoseAfterHomeSearchAsync(ITelescopeDevice client, long fence, long sessionEpoch, PoseReading atSend) {
        var deadline = DateTime.UtcNow + HomeSearchTimeout;
        var search = new HomeSearchEvidence();
        search.Add(atSend);
        var atHomePolls = 0;
        try {
            while (DateTime.UtcNow < deadline) {
                await Task.Delay(HomeSearchPollInterval, CancellationToken.None).ConfigureAwait(false);
                lock (_gate) {
                    if (HomeWatchVoidedLocked(client, sessionEpoch)) {
                        LogHomeSearchNotTrusted("the mount reconnected or its session changed after the Home");
                        return;
                    }
                }
                if (AbortedSince(fence)) {
                    LogHomeSearchNotTrusted("a stop landed after the Home");
                    return;
                }
                var pose = ReadPoseBestEffort(client);
                search.Add(pose);
                atHomePolls = pose is { AtHome: true, Slewing: false, DecDeg: double } ? atHomePolls + 1 : 0;
                if (atHomePolls < 2) {
                    continue;
                }
                // A Home that never left Home and moved clears nothing, whatever the reason.
                if (!search.Seen) {
                    LogHomeSearchNotTrusted("no search was seen leaving Home and moving since the last poll the mount answered; confirm Home by eye");
                    return;
                }
                bool cleared;
                string? device;
                string? suspect = null;
                lock (_gate) {
                    if (HomeWatchVoidedLocked(client, sessionEpoch) || AbortedSince(fence)) {
                        return;
                    }
                    // #2173 review — the reason is read here, at the trust, so one that replaced stop_unconfirmed during
                    // the search counts. A model-suspect reason waits for the user's eye check (confirm-home), unless
                    // this session's driver says its FindHome is a real sensor search.
                    if (!MountPoseTrust.HomeSearchMayTrust(PoseTrust.Reason, _capabilities?.HomeIsSensorSearch == true)) {
                        suspect = PoseTrust.Reason;
                    }
                    cleared = suspect is null && PoseTrust.Confirm(pose.DecDeg);
                    device = _lifecycle.Device?.Name;
                }
                if (suspect is not null) {
                    LogHomeSearchNeedsEyeCheck(suspect);
                    return;
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

    // #2173 — caller holds the gate. The Home watch ends on any link-lifecycle change: the client is no longer the live one,
    // or the session was reset (a reconnect, a trip, a disconnect) since FindHome went out.
    private bool HomeWatchVoidedLocked(ITelescopeDevice client, long sessionEpoch) =>
        !_lifecycle.IsLive(client) || Interlocked.Read(ref _sessionEpoch) != sessionEpoch;

    // #2173 review — what the Home watch has seen of a search since the last poll that could not be read.
    private sealed class HomeSearchEvidence {
        private bool _leftHome;
        private bool _moved;
        private double _decLow = double.PositiveInfinity;
        private double _decHigh = double.NegativeInfinity;

        public bool Seen => _leftHome && _moved;

        public void Add(PoseReading pose) {
            if (pose.AtHome is not bool atHome || pose.Slewing is not bool slewing || pose.DecDeg is not double raw
                    || MountPoseTrust.FoldDec(raw) is not double dec) {
                // A gap: the mount may have reset in it and woken reporting Home wherever it points.
                _leftHome = false;
                _moved = false;
                _decLow = double.PositiveInfinity;
                _decHigh = double.NegativeInfinity;
                return;
            }
            _leftHome |= !atHome;
            _decLow = Math.Min(_decLow, dec);
            _decHigh = Math.Max(_decHigh, dec);
            _moved |= slewing || _decHigh - _decLow > MountPoseTrust.PoseJumpDeg;
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
    /// <param name="canFindHome">#2173 — the mount's Alpaca CanFindHome: Home in Ara is then the way out; a mount that
    /// cannot find Home is sent to its hand controller's search.</param>
    /// <param name="homeIsSensorSearch">#2173 — the driver says its FindHome is a real sensor search: a Home then clears
    /// the flag by itself for every reason.</param>
    internal static RefusalException PositionUnverifiedRefusal(string reason, bool canFindHome = false, bool homeIsSensorSearch = false) =>
        new(RefusalCatalog.MountPositionUnverified, PositionUnverifiedDetail(reason, canFindHome, homeIsSensorSearch),
            new Dictionary<string, object?> {
                ["position_unverified_reason"] = reason,
                // #2173 — always sent: the client words the way out by it (a Home then clears the flag by itself).
                ["home_is_sensor_search"] = homeIsSensorSearch,
            });

    internal static string PositionUnverifiedDetail(string reason, bool canFindHome = false, bool homeIsSensorSearch = false) {
        // #2173 review — a completed Home trusts the position by itself only when the reason leaves the mount's model intact.
        var fix = canFindHome
            ? (MountPoseTrust.HomeSearchMayTrust(reason, homeIsSensorSearch)
                ? " The way out is Home: press Home, and the position is trusted again once the mount is seen to move and then reports Home."
                    + " Or confirm the mount is physically at Home."
                : " The way out is Home: press Home, then, after Home, confirm by eye that the mount is really at Home and confirm it in Ara"
                    + " (the mount's own position may be wrong, so a Home alone does not trust it).")
                + " GoTo, Park and polar alignment are blocked until then; Home, Stop, the direction pad and tracking still work."
            : " This mount cannot find Home from Ara: run its zero (home-sensor) search from its hand controller or app, then confirm Home."
                + " GoTo, Park and polar alignment are blocked until then; Stop, the direction pad and tracking still work.";
        return reason switch {
            MountPoseTrust.NotReadYet => "The mount's position is not confirmed yet: it was just connected, or a reading is being checked against the next one; try again in a few seconds.",
            MountPoseTrust.AtHomeOnConnect => "Mount position unverified: the mount reported Home as soon as it connected, which a mount without absolute encoders also does after a power loss wherever it points." + fix,
            MountPoseTrust.PoseJumpOnReconnect => string.Create(CultureInfo.InvariantCulture,
                $"Mount position unverified: its declination moved more than {MountPoseTrust.PoseJumpDeg:0}° while it was disconnected, as after a power loss.") + fix,
            MountPoseTrust.AtHomeWithoutCommand => "Mount position unverified: it started reporting Home with no move commanded, as after a power blip." + fix,
            MountPoseTrust.StopUnconfirmed => "Mount position unverified: Ara could not confirm that the mount stopped after polar alignment moved it, or the mount reconnected while polar alignment ran, so it may have moved on." + fix,
            _ => string.Create(CultureInfo.InvariantCulture,
                $"Mount position unverified: its declination jumped more than {MountPoseTrust.PoseJumpDeg:0}° with no move commanded, as after a power blip.") + fix,
        };
    }

    // REST moves: the ADR 0022 refusal.
    // canFindHome: the answer the caller decided by (the user's Home), else the connect's capability read.
    private async Task ThrowIfPoseUnverifiedAsync(ITelescopeDevice client, string op, CancellationToken ct, bool? canFindHome = null) {
        if (await PoseBlockReasonAsync(client, ct).ConfigureAwait(false) is { } reason) {
            LogMoveRefusedUnverified(op, reason);
            throw PositionUnverifiedRefusal(reason, canFindHome ?? CanFindHomeKnown, HomeIsSensorSearchKnown);
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
            var detail = PositionUnverifiedDetail(reason, CanFindHomeKnown, HomeIsSensorSearchKnown);
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
        PoseReading pose;
        try {
            pose = await Task.Run(() => ReadPoseBestEffort(client), CancellationToken.None).WaitAsync(PoseReadTimeout, ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            throw new InvalidOperationException("the mount did not answer; Home was not confirmed");
        }
        if (pose.AtHome != true || pose.Slewing != false) {
            string? reason;
            lock (_gate) {
                reason = PoseTrust.Reason;
            }
            throw new InvalidOperationException(CanFindHomeKnown
                ? (MountPoseTrust.HomeSearchMayTrust(reason, HomeIsSensorSearchKnown)
                    ? "the mount does not report that it is at Home, so Home cannot be confirmed: press Home (the position is trusted again once the mount is seen to move and then reports Home)"
                    : "the mount does not report that it is at Home, so Home cannot be confirmed: press Home, then, after Home, confirm by eye that the mount is really at Home and confirm it here")
                : "the mount does not report that it is at Home, so Home cannot be confirmed: this mount cannot find Home from Ara, so run its zero (home sensor) search from its hand controller or app, then confirm");
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telescope '{Device}': position unverified ({Reason}); GoTo, Park and polar alignment are refused until Home is confirmed, or, for an unconfirmed stop, until a Home search ends at Home (#2070, #2173)")]
    private partial void LogPositionUnverified(string device, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "telescope.{Op} refused: the mount's position is unverified ({Reason}) (#2070)")]
    private partial void LogMoveRefusedUnverified(string op, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telescope '{Device}': the user confirmed the mount is at Home (cleared an unverified position: {Cleared}); its position is trusted again (#2070)")]
    private partial void LogHomeConfirmed(string device, bool cleared);

    [LoggerMessage(Level = LogLevel.Information, Message = "Telescope '{Device}': the Home search ended with the mount at Home (Dec {DecDeg:F2}°; cleared an unverified position: {Cleared}); its position is trusted again (#2173)")]
    private partial void LogHomeSearchTrusted(string device, bool cleared, double decDeg);

    [LoggerMessage(Level = LogLevel.Information, Message = "telescope.findhome: the position was not trusted from this Home ({Why}) (#2173)")]
    private partial void LogHomeSearchNotTrusted(string why);

    [LoggerMessage(Level = LogLevel.Information, Message = "telescope.findhome: the Home ended at Home, but the position stays unverified ({Reason}: the mount's own position model may be wrong); confirm by eye that the mount is really at Home (#2173)")]
    private partial void LogHomeSearchNeedsEyeCheck(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "telescope.findhome: the position was not trusted from this Home (the mount did not report Home and still within {Seconds:0} s) (#2173)")]
    private partial void LogHomeSearchTimedOut(double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "telescope: watching a park or Home for its end failed (#2173)")]
    private partial void LogMoveSettleWatchFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "telescope.findhome: watching the Home search for its end failed (#2173)")]
    private partial void LogHomeSearchWatchFailed(Exception exception);
}
