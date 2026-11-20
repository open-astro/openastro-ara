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
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1589 — Dec running away under correction: the Dec error keeps growing while the Dec pulses sit at
/// their maximum (<see cref="GuideAxisRunaway"/>, the rule the polar alignment residual uses). A wrong Dec
/// direction after a meridian flip does this: each pulse pushes the star further away. The watch stops
/// guiding and publishes a guider fault (<c>tracking_lost</c>, details <c>dec_runaway</c>), so the shared
/// reaction runs on_guider_lost (an adoptable pause by default) and notifies. Guiding that is started
/// again and settles recovers the fault, which resumes the runs the reaction paused.
/// </summary>
/// <remarks>
/// Unlike the polar alignment residual, settle frames count: after a flip guiding restarts with a settle
/// (40 s by default), and a normal settle shrinks the error, so it never forms a growing streak. Dec only:
/// RA running away is the mount not tracking, which the mount's tracking-lost fault covers. One trip per
/// guided run: after it, nothing is checked until the next guiding start.
/// </remarks>
public sealed partial class GuiderService {

    private readonly GuideAxisRunaway _decRunaway = new(); // under _gate
    private bool _decRunawayTripped;                         // this guided run tripped: frames ignored until guiding starts again
    private bool _decRunawayPaused;                          // the guider is paused: its frames are not guiding
    private bool _decRunawayRestarted;                       // guiding started again since the fault was published
    private EquipmentFaultEvent? _decRunawayFault;           // the open fault, recovered once guiding has settled again

    private void FeedDecRunawayStep(PHD2Guider guider, IGuideStep step) {
        bool newFault;
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
            if (_decRunawayTripped || _decRunawayPaused || !_decRunaway.Step(step.DECDistanceRaw, step.DECDuration)) {
                return;
            }
            _decRunawayTripped = true;
            _decRunawayRestarted = false;
            (startPx, lastPx, pulseMs) = (_decRunaway.StartDistance, Math.Abs(_decRunaway.LastDistance), _decRunaway.MaxPulse);
            // A fault still open (guiding was started again on the same calibration and ran away before it
            // settled) holds the guider's one episode: guiding is stopped again under it, with no new fault.
            newFault = _decRunawayFault is null;
            _decRunawayFault ??= PublishFaultLocked(EquipmentFaultKind.TrackingLost, "dec_runaway");
        }
        LogDecRunaway(startPx, lastPx, GuideAxisRunaway.Steps, pulseMs, newFault);
        _ = Task.Run(() => StopGuidingForDecRunawayAsync(guider), CancellationToken.None);
    }

    // Off the guider's listener thread: the stop waits for replies that the listener delivers.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Fire-and-forget off the guider's listener thread: a failed stop is logged; the fault is already published.")]
    private async Task StopGuidingForDecRunawayAsync(PHD2Guider guider) {
        try {
            if (!await guider.StopGuiding(CancellationToken.None).ConfigureAwait(false)) {
                LogDecRunawayStopFailed(null);
            }
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

    [LoggerMessage(EventId = 4251, Level = LogLevel.Warning, Message = "#1589 Dec error grew from {StartPx:F1} to {LastPx:F1} px over {Frames} guide frames with the Dec pulse at {PulseMs:F0} ms: stopping guiding (new fault: {NewFault})")]
    private partial void LogDecRunaway(double startPx, double lastPx, int frames, double pulseMs, bool newFault);

    [LoggerMessage(EventId = 4252, Level = LogLevel.Warning, Message = "#1589 the guider did not stop guiding after the Dec runaway")]
    private partial void LogDecRunawayStopFailed(Exception? ex);

    [LoggerMessage(EventId = 4253, Level = LogLevel.Information, Message = "#1589 guiding settled again after the Dec runaway: the fault is recovered")]
    private partial void LogDecRunawayRecovered();
}
