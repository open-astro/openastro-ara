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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.2 "Guider (PHD2): loses guide star" — a star that stays lost for longer than the grace period
/// publishes a star-lost fault, so the shared reaction runs the on_guider_lost plan (pause by default), and the first settled stretch of guiding
/// afterwards recovers the fault, which resumes the runs the shared reaction paused (#1891: a guide step far from the lock is not guiding). Before this the sequencer never heard about a lost star: on
/// 2026-10-16 the guider chased a lost star for nine minutes after a Home while Ara's run carried on.
/// Unlike a link or camera drop this is not one-shot per connect: clouds pass more than once a night.
/// </summary>
public sealed partial class GuiderService : IGuiderEpisodeListener {

    internal static readonly TimeSpan DefaultStarLostGrace = TimeSpan.FromSeconds(30);

    /// <summary>How long the star may stay lost before the reaction runs (PHD2 usually re-acquires a
    /// star within a few frames; a passing cloud takes longer). Internal so tests can shorten it.</summary>
    internal TimeSpan StarLostGrace { get; set; } = DefaultStarLostGrace;

    private Timer? _starLostTimer;                 // armed on star_lost, disarmed by a settled guide step (under _gate)
    private DateTimeOffset? _settledSinceUtc;      // the current run of guide steps inside the settle distance (under _gate)
    private bool _guidingSeen;                     // a guide step or guiding_started since the last guiding_stopped
    private bool _starLostReacted;                 // the reaction ran for the current lost-star episode
    private EquipmentFaultEvent? _starLostFault;   // the published star-lost fault, recovered when the star is back
    private IReadOnlyList<Guid> _starLostPausedRuns = []; // the runs the shared reaction paused for that fault
    // IGuiderMediator.GuidingStoppedUtc — the Dither's long-gap read. Deliberately NOT cleared by
    // ResetStarLostWatchLocked: guiding stays off across a guider disconnect and reconnect, so a
    // restart after one measures the gap from the real stop, not from the reconnect.
    private DateTimeOffset? _guidingStoppedUtc;

    private void NoteStarLostMarker(string? kind) {
        lock (_gate) {
            switch (kind) {
                case "guiding_started":
                    _guidingSeen = true;
                    _guidingStoppedUtc = null;
                    break;
                case "star_lost":
                case "lock_position_lost":
                    // PHD2 also reports StarLost while merely looping with a star selected; only a star
                    // lost from a guiding session concerns the run.
                    _settledSinceUtc = null;
                    if (_guidingSeen) {
                        ArmStarLostWatchLocked();
                    }
                    break;
                case "guiding_stopped":
                    // The user (or a park) stopped guiding: nothing to wait for. A pause that is already
                    // outstanding stays until guide steps return, a later daemon pause adopts it, or the
                    // user resumes.
                    _guidingSeen = false;
                    _settledSinceUtc = null;
                    _guidingStoppedUtc ??= DateTimeOffset.UtcNow;
                    DisarmStarLostWatchLocked();
                    if (_recoveryStopPending) {
                        // The stop the recovery itself issued before its re-center: consumed once, the
                        // episode stays alive. Any later stop is the user's and ends it below.
                        _recoveryStopPending = false;
                    } else {
                        // ...and nothing to recover: the user owns the pause now.
                        DisarmStarLostRecoveryLocked();
                    }
                    break;
            }
        }
    }

    private void ArmStarLostWatchLocked() {
        if (_disposed || _starLostTimer is not null || _starLostReacted || _state != EquipmentConnectionState.Connected) {
            return;
        }
        _starLostTimer = new Timer(OnStarLostGraceElapsed, state: null, StarLostGrace, Timeout.InfiniteTimeSpan);
    }

    private void DisarmStarLostWatchLocked() {
        _starLostTimer?.Dispose();
        _starLostTimer = null;
    }

    // A new guider connection watches afresh. An open episode (its fault, paused runs and recovery state:
    // a give-up, the failure count and its one warning, our own stop) stays across a reconnect. Settled
    // guiding, the probe or the runs leaving the daemon's pause end it; a user disconnect or the service's
    // dispose drops it (DropOpenFaultsLocked). Its probe stops with the old guider; the next Connected re-arms it.
    private void ResetStarLostWatchLocked() {
        DisarmStarLostWatchLocked();
        DisarmStarLostRecoveryLocked();
        _guidingSeen = false;
        _settledSinceUtc = null;
    }

    // Caller holds _gate. The episode is over: the hub hears Recovered and the shared reaction resumes
    // exactly the runs it paused.
    private void EndStarLostEpisodeLocked() {
        _starLostReacted = false;
        _starLostPausedRuns = [];
        RecoverFaultLocked(ref _starLostFault);
    }

    // The shared reaction paused these runs for the star-lost fault: the long-outage recovery probes and
    // re-centres for them only. Nothing paused (no run, or another episode holds them) arms nothing.
    void IGuiderEpisodeListener.Paused(EquipmentFaultEvent fault, IReadOnlyList<Guid> runIds) {
        lock (_gate) {
            if (NoteDecRunawayPausedLocked(fault, runIds)) {
                return; // #1589 — a Dec runaway's pause: no star-lost recovery
            }
            if (!_starLostReacted || !ReferenceEquals(fault, _starLostFault)) {
                return; // a link or camera fault, or the star is back already
            }
            _starLostPausedRuns = runIds;
            // The long-outage half: after StarLostRecoverAfter the guider's own frames are asked
            // whether the clouds have gone.
            ArmStarLostRecoveryLocked(StarLostRecoverAfter);
        }
    }

    internal void OnStarLostGraceElapsed(object? _) {
        lock (_gate) {
            DisarmStarLostWatchLocked();
            if (_disposed || _guider is null || _state != EquipmentConnectionState.Connected || _starLostReacted) {
                return;
            }
            if (_settledSinceUtc is not null) {
                return; // a late callback: Dispose does not stop one already queued, and a settled step has ended the loss
            }
            // Only a settled guide step disarms the timer, so a star found far from the lock (Guiding,
            // not settled) still counts as lost; a stopped or looping guider is not this watch's.
            if (_guider.AppState?.State is not ("LostLock" or "Guiding")) {
                return; // guiding ended before the grace ran out
            }
            _starLostReacted = true;
            // A new episode starts clean: a give-up, the failure count, the one-warning latch and the
            // own-stop flags all belong to the previous cloud (review round 3).
            ResetStarLostRecoveryLocked();
            _starLostPausedRuns = [];
            // The shared reaction pauses (adoptably), skips or aborts per on_guider_lost; a pause tells
            // this service which runs it paused (IGuiderEpisodeListener.Paused).
            _starLostFault = PublishFaultLocked(EquipmentFaultKind.TrackingLost, "star_lost");
        }
        LogStarLostGraceElapsed((int)StarLostGrace.TotalSeconds);
    }

    // The profile's settle rule, PHD2's own: the total error (RA and Dec together, pixels) inside
    // SettlePixels for SettleTime seconds. Caller holds _gate.
    private void TrackSettleLocked(IGuideStep step) {
        var settings = _profileService.ActiveProfile.GuiderSettings;
        if (Math.Sqrt((step.RADistanceRaw * step.RADistanceRaw) + (step.DECDistanceRaw * step.DECDistanceRaw)) <= settings.SettlePixels) {
            _settledSinceUtc ??= DateTimeOffset.UtcNow;
        } else {
            _settledSinceUtc = null;
        }
    }

    // Caller holds _gate.
    private bool GuidingSettledLocked() =>
        _settledSinceUtc is { } since
        && DateTimeOffset.UtcNow - since >= TimeSpan.FromSeconds(Math.Max(0, _profileService.ActiveProfile.GuiderSettings.SettleTime));

    // Caller holds _gate.
    private void NoteGuideStepLocked(IGuideStep step) {
        _guidingSeen = true;
        _guidingStoppedUtc = null;
        TrackSettleLocked(step);
        if (_settledSinceUtc is not null) {
            DisarmStarLostWatchLocked(); // settled guiding ends the loss; a star found far from lock does not
        }
        if (!_starLostReacted) {
            return;
        }
        if (_recoveryInFlight && _recoveryStoppedGuiding) {
            // The recovery restarted guiding itself: it re-centered first and resumes the run once
            // guiding has settled, with the notification that says so. Not a relock.
            return;
        }
        if (!GuidingSettledLocked()) {
            return; // found again, not yet guiding: the run stays paused
        }
        DisarmStarLostRecoveryLocked();
        EndStarLostEpisodeLocked();
        LogStarRecovered();
    }

    [LoggerMessage(EventId = 4228, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§42.2 guide star lost for more than {GraceSec} s — publishing the star-lost fault")]
    private partial void LogStarLostGraceElapsed(int graceSec);

    [LoggerMessage(EventId = 4247, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "§42.2 guide star recovered (guiding settled) — the star-lost fault is recovered")]
    private partial void LogStarRecovered();
}