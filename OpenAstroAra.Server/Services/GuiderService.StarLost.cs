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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.2 "Guider (PHD2): loses guide star" — a star that stays lost for longer than the grace period
/// runs the on_guider_lost reaction (pause by default), and the first guide step afterwards resumes
/// the runs that pause parked. Before this the sequencer never heard about a lost star: on
/// 2026-10-16 the guider chased a lost star for nine minutes after a Home while Ara's run carried on.
/// Unlike a link or camera drop this is not one-shot per connect: clouds pass more than once a night.
/// </summary>
public sealed partial class GuiderService {

    internal static readonly TimeSpan DefaultStarLostGrace = TimeSpan.FromSeconds(30);

    /// <summary>How long the star may stay lost before the reaction runs (PHD2 usually re-acquires a
    /// star within a few frames; a passing cloud takes longer). Internal so tests can shorten it.</summary>
    internal TimeSpan StarLostGrace { get; set; } = DefaultStarLostGrace;

    private Timer? _starLostTimer;                 // armed on star_lost, disarmed by any guide step (under _gate)
    private bool _guidingSeen;                     // a guide step or guiding_started since the last guiding_stopped
    private bool _starLostReacted;                 // the reaction ran for the current lost-star episode
    private EquipmentFaultEvent? _starLostFault;   // its §42.5 row, resolved when the star is back
    private IReadOnlyList<Guid> _starLostPausedRuns = Array.Empty<Guid>();
    private DateTimeOffset? _guidingStoppedUtc;   // IGuiderMediator.GuidingStoppedUtc — the Dither's long-gap read

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
                    if (_guidingSeen) {
                        ArmStarLostWatchLocked();
                    }
                    break;
                case "guiding_stopped":
                    // The user (or a park) stopped guiding: nothing to wait for. A pause that is already
                    // outstanding stays until guide steps return, a later daemon pause adopts it, or the
                    // user resumes.
                    _guidingSeen = false;
                    _guidingStoppedUtc ??= DateTimeOffset.UtcNow;
                    DisarmStarLostWatchLocked();
                    if (!_recoveryStoppedGuiding) {
                        // ...and nothing to recover: the user owns the pause now. A stop the recovery
                        // itself issued (before its re-center) keeps the episode alive.
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

    private void ResetStarLostWatchLocked() {
        DisarmStarLostWatchLocked();
        ResetStarLostRecoveryLocked();
        _guidingSeen = false;
        _starLostReacted = false;
        _starLostFault = null;
        _starLostPausedRuns = Array.Empty<Guid>();
    }

    private void OnStarLostGraceElapsed(object? _) {
        EquipmentFaultEvent fault;
        lock (_gate) {
            DisarmStarLostWatchLocked();
            if (_disposed || _guider is null || _state != EquipmentConnectionState.Connected || _starLostReacted) {
                return;
            }
            if (_guider.AppState?.State != "LostLock") {
                return; // the star came back (or guiding ended) before the grace ran out
            }
            _starLostReacted = true;
            fault = new EquipmentFaultEvent(DeviceType.Guider, DeviceId: null, DeviceName: "PHD2",
                EquipmentFaultKind.TrackingLost,
                $"guide star lost for more than {(int) StarLostGrace.TotalSeconds} s — the guider is still looking for it",
                DateTimeOffset.UtcNow);
            _starLostFault = fault;
        }
        LogStarLostGraceElapsed((int) StarLostGrace.TotalSeconds);
        RecordFaultQuietly(fault);
        _ = Task.Run(() => ReactToGuidingLossAsync(GuiderFaultKind.StarLost, fault), CancellationToken.None);
    }

    // Caller holds _gate.
    private void NoteGuideStepLocked() {
        _guidingSeen = true;
        _guidingStoppedUtc = null;
        DisarmStarLostWatchLocked();
        if (!_starLostReacted) {
            return;
        }
        if (_recoveryInFlight && _recoveryStoppedGuiding) {
            // The recovery restarted guiding itself: it re-centered first and resumes the run once
            // guiding is confirmed, with the notification that says so. Not a relock.
            return;
        }
        DisarmStarLostRecoveryLocked();
        _starLostReacted = false;
        var fault = _starLostFault;
        var paused = _starLostPausedRuns;
        _starLostFault = null;
        _starLostPausedRuns = Array.Empty<Guid>();
        _ = Task.Run(() => ResumeAfterStarRecoveredAsync(fault, paused), CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background recovery boundary: a sequencer or notification fault leaves the run paused for the user; logged, never an unobserved task exception.")]
    private async Task ResumeAfterStarRecoveredAsync(EquipmentFaultEvent? fault, IReadOnlyList<Guid> paused) {
        try {
            if (fault is not null) {
                await ResolveFaultQuietlyAsync(fault, DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
            var resumed = 0;
            if (paused.Count > 0) {
                var sequencer = _sequencerResolver?.Invoke();
                if (sequencer is not null) {
                    resumed = await sequencer.ResumeRunsAsync(paused, CancellationToken.None).ConfigureAwait(false);
                }
            }
            LogStarRecovered(paused.Count, resumed);
            if (paused.Count == 0) {
                return; // nothing was parked by the star loss (skip/abort policy, or no run); the chip already shows guiding
            }
            await PublishFaultEventAsync(new JsonObject {
                ["action"] = "auto_resume",
                ["runs_affected"] = resumed,
                ["kind"] = "star_recovered",
            }).ConfigureAwait(false);
            await NotifyQuietlyAsync(NotificationSeverity.Info, "Guide star back — sequence resumed",
                resumed > 0
                    ? "The guider is guiding again, so the run paused by the lost star continues on its own."
                    : "The guider is guiding again. The run it had paused was not released by this (it had already been resumed, is stopped, or waits on you).")
                .ConfigureAwait(false);
        } catch (Exception ex) {
            LogStarRecoveryFailed(ex);
        }
    }

    [LoggerMessage(EventId = 4228, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§42.2 guide star lost for more than {GraceSec} s — running the on_guider_lost reaction")]
    private partial void LogStarLostGraceElapsed(int graceSec);

    [LoggerMessage(EventId = 4247, Level = Microsoft.Extensions.Logging.LogLevel.Information, Message = "§42.2 guide star recovered: runsPaused={Paused} runsResumed={Resumed}")]
    private partial void LogStarRecovered(int paused, int resumed);

    [LoggerMessage(EventId = 4248, Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "§42.2 star-recovered resume failed; the run stays paused for the user")]
    private partial void LogStarRecoveryFailed(Exception exception);
}
