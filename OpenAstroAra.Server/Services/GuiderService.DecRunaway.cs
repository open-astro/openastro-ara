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
using OpenAstroAra.Core.Interfaces;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.PhdEvents;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1589 — Dec running away under correction: the Dec error keeps growing while the Dec pulses sit at
/// their maximum (<see cref="GuideAxisRunaway"/>, the rule the polar alignment residual uses). A wrong Dec
/// direction after a meridian flip does this: each pulse pushes the star further away, until PHD2 cuts it
/// to the Max Dec Duration (<c>DecLimited</c>, required on the tripping frame). The watch stops
/// guiding and publishes a guider fault (<c>tracking_lost</c>, details <c>dec_runaway</c>), so the shared
/// reaction runs on_guider_lost (an adoptable pause by default) and notifies. Guiding that is started
/// again and settles recovers the fault, which resumes the runs the reaction paused.
/// </summary>
/// <remarks>
/// Unlike the polar alignment residual, settle frames count: after a flip guiding restarts with a settle
/// (40 s by default), and a normal settle shrinks the error, so it never forms a growing streak. Dec only:
/// RA running away is the mount not tracking, which the mount's tracking-lost fault covers. One trip per
/// guided run: after it, nothing is checked until the next guiding start.
/// <para>A runaway on the same calibration after a restart reacts again unless the reaction is still
/// holding the open fault with the runs paused: a skip, an abort or a notify-only reaction ends at once,
/// and a paused run that the user resumed is no longer held, so the old fault is closed and a new one
/// runs on_guider_lost again (<see cref="IGuiderEpisodeListener.Released"/>).</para>
/// </remarks>
public sealed partial class GuiderService {

    private readonly GuideAxisRunaway _decRunaway = new(); // under _gate
    private bool _decRunawayTripped;                         // this guided run tripped: frames ignored until guiding starts again
    private bool _decRunawayPaused;                          // the guider is paused: its frames are not guiding
    private bool _decRunawayRestarted;                       // guiding started again since the fault was published
    private EquipmentFaultEvent? _decRunawayFault;           // the open fault, recovered once guiding has settled again
    private bool _decRunawayHeld;                            // a reaction episode still holds _decRunawayFault
    private IReadOnlyList<Guid> _decRunawayPausedRuns = [];  // the runs that episode paused
    private EquipmentFaultEvent? _decRunawayRepublishAfter;  // closed because the user resumed: publish anew once its episode lets go

    private void FeedDecRunawayStep(PHD2Guider guider, IGuideStep step) {
        bool newFault;
        EquipmentFaultEvent? heldFault = null;
        IReadOnlyList<Guid> heldRuns = [];
        double startPx, lastPx, pulseMs;
        lock (_gate) {
            if (!ReferenceEquals(guider, _guider) || _state != EquipmentConnectionState.Connected) {
                return;
            }
            if (_decRunawayFault is not null && _decRunawayRestarted && GuidingSettledLocked()) {
                _decRunawayRestarted = false;
                RecoverFaultLocked(ref _decRunawayFault);
                LogDecRunawayRecovered();
            }
            // The streak's "maximum" is the largest pulse since guiding started, so growing proportional
            // pulses (Dec backlash after a centre or flip) count too: the tripping frame must also be one
            // PHD2 cut to its Max Dec Duration, as a wrong Dec direction soon is.
            if (_decRunawayTripped || _decRunawayPaused || !_decRunaway.Step(step.DECDistanceRaw, step.DECDuration)
                    || step is not PhdEventGuideStep { DecLimited: true }) {
                return;
            }
            _decRunawayTripped = true;
            _decRunawayRestarted = false;
            (startPx, lastPx, pulseMs) = (_decRunaway.StartDistance, Math.Abs(_decRunaway.LastDistance), _decRunaway.MaxPulse);
            if (_decRunawayFault is not null && !_decRunawayHeld) {
                // Guiding was started again on the same calibration and the reaction is done with the old
                // fault (it skipped, aborted or only notified): close it, so this runaway reacts again.
                RecoverFaultLocked(ref _decRunawayFault);
            }
            newFault = _decRunawayFault is null;
            if (newFault) {
                PublishFaultLocked(EquipmentFaultKind.TrackingLost, "dec_runaway", OpenDecRunawayFaultLocked);
            } else {
                // The reaction still holds the open fault: if the runs it paused are paused still, guiding
                // only stops again; if the user resumed them, StopGuidingForDecRunawayAsync reacts anew.
                (heldFault, heldRuns) = (_decRunawayFault, _decRunawayPausedRuns);
            }
        }
        LogDecRunaway(startPx, lastPx, GuideAxisRunaway.Steps, pulseMs, newFault);
        _ = Task.Run(() => StopGuidingForDecRunawayAsync(guider, heldFault, heldRuns), CancellationToken.None);
    }

    // Caller holds _gate. Runs before the hub's subscribers see the fault, so a reaction that lets go of
    // it at once (it joined a running guider episode) finds it here.
    private void OpenDecRunawayFaultLocked(EquipmentFaultEvent fault) {
        _decRunawayFault = fault;
        _decRunawayHeld = true;
        _decRunawayPausedRuns = [];
    }

    // The shared reaction paused these runs for the open runaway fault.
    private bool NoteDecRunawayPausedLocked(EquipmentFaultEvent fault, IReadOnlyList<Guid> runIds) {
        if (!ReferenceEquals(fault, _decRunawayFault)) {
            return false;
        }
        _decRunawayPausedRuns = runIds;
        return true;
    }

    // OnFault can call this on the thread that holds _gate (PublishFaultLocked), so the lock below re-enters
    // it. That is safe only because _gate is a Monitor, which is re-entrant.
    void IGuiderEpisodeListener.Released(EquipmentFaultEvent fault) {
        lock (_gate) {
            if (ReferenceEquals(fault, _guidedLinkFault)) {
                _guidedLinkFault = null; // #2169 — no episode will restore guiding for this link loss
                return;
            }
            if (ReferenceEquals(fault, _decRunawayFault)) {
                _decRunawayHeld = false;
                _decRunawayPausedRuns = [];
                return;
            }
            if (!ReferenceEquals(fault, _decRunawayRepublishAfter)) {
                return;
            }
            _decRunawayRepublishAfter = null;
            // The user resumed the paused runs and guiding ran away again: the old episode has let go, so
            // the new fault gets an episode of its own. Not when guiding has started again since.
            if (_decRunawayTripped && _decRunawayFault is null && _guider is not null
                    && _state == EquipmentConnectionState.Connected) {
                PublishFaultLocked(EquipmentFaultKind.TrackingLost, "dec_runaway", OpenDecRunawayFaultLocked);
            }
        }
    }

    // Off the guider's listener thread: the stop waits for replies that the listener delivers.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget off the guider's listener thread: a failed stop or run lookup is logged; the fault is already published.")]
    private async Task StopGuidingForDecRunawayAsync(PHD2Guider guider, EquipmentFaultEvent? heldFault, IReadOnlyList<Guid> heldRuns) {
        try {
            if (!await guider.StopGuiding(CancellationToken.None).ConfigureAwait(false)) {
                LogDecRunawayStopFailed(null);
            }
        } catch (Exception ex) {
            LogDecRunawayStopFailed(ex);
        }
        if (heldFault is null || heldRuns.Count == 0) {
            return; // a new fault was published, or the reaction is still placing its pause
        }
        try {
            var sequencer = _sequencerResolver?.Invoke();
            if (sequencer is null || (await sequencer.DaemonPausedRunsAsync(heldRuns, CancellationToken.None).ConfigureAwait(false)).Count > 0) {
                return; // the runs are still paused for the open fault: stopping guiding again is enough
            }
            lock (_gate) {
                if (!ReferenceEquals(heldFault, _decRunawayFault) || !_decRunawayHeld) {
                    return;
                }
                // The user resumed the runs on the same calibration: close the old fault (its episode resumes
                // nothing and ends) and publish a new one when the episode lets go (Released).
                _decRunawayRepublishAfter = heldFault;
                RecoverFaultLocked(ref _decRunawayFault);
            }
            LogDecRunawayAfterUserResume();
        } catch (Exception ex) {
            LogDecRunawayStopFailed(ex);
        }
    }

    private void NoteDecRunawayMarker(string? kind) {
        lock (_gate) {
            switch (kind) {
                case "guiding_started":
                    _decRunaway.Clear();
                    _decRunawayTripped = false;
                    _decRunawayPaused = false;
                    _decRunawayRestarted = _decRunawayFault is not null;
                    break;
                case "dithered":
                case "star_lost":
                case "lock_position_lost":
                case "calibration_started":
                    _decRunaway.Clear();
                    break;
                case "paused":
                    _decRunawayPaused = true;
                    break;
                case "resumed":
                    _decRunawayPaused = false;
                    _decRunaway.Clear();
                    break;
            }
        }
    }

    // Caller holds _gate. The guider went away: a new connection watches afresh. An open fault stays
    // (a user disconnect drops it in DropOpenFaultsLocked; a link drop keeps the runs paused).
    private void ResetDecRunawayLocked() {
        _decRunaway.Clear();
        _decRunawayTripped = false;
        _decRunawayPaused = false;
        _decRunawayRestarted = false;
    }

    // Caller holds _gate. A user disconnect (or the service's dispose) drops the open fault without Recovered.
    private void DropDecRunawayFaultLocked() {
        _decRunawayFault = null;
        _decRunawayHeld = false;
        _decRunawayPausedRuns = [];
        _decRunawayRepublishAfter = null;
    }

    [LoggerMessage(EventId = 4251, Level = LogLevel.Warning, Message = "#1589 Dec error grew from {StartPx:F1} to {LastPx:F1} px over {Frames} guide frames with the Dec pulse at {PulseMs:F0} ms: stopping guiding (new fault: {NewFault})")]
    private partial void LogDecRunaway(double startPx, double lastPx, int frames, double pulseMs, bool newFault);

    [LoggerMessage(EventId = 4252, Level = LogLevel.Warning, Message = "#1589 the guider did not stop guiding after the Dec runaway")]
    private partial void LogDecRunawayStopFailed(Exception? ex);

    [LoggerMessage(EventId = 4253, Level = LogLevel.Information, Message = "#1589 guiding settled again after the Dec runaway: the fault is recovered")]
    private partial void LogDecRunawayRecovered();

    [LoggerMessage(EventId = 4254, Level = LogLevel.Warning, Message = "#1589 the runs paused for the Dec runaway were resumed by hand and Dec ran away again: a new fault follows")]
    private partial void LogDecRunawayAfterUserResume();
}
