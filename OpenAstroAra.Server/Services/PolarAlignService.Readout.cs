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
using OpenAstroAra.Server.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// #2072 — what the live readout rests on, so a reading can be trusted or not. The §45.8 reverse projection reads each
    /// solve's alt/az shift from the seed as the user's knob move. That holds only while the camera stands still in alt/az:
    /// tracking off. A mount that keeps tracking (it refused the write, or something turned tracking back on) circles the
    /// camera about the axis at about 1.3′ a minute 5° from it, and the readout takes that for knob motion: the user
    /// chases the number to zero while the real error grows. So the routine confirms tracking is off before the first
    /// live frame, checks it on every frame, and ends the run (<see cref="TrackingNotOff"/>) when it is on. It also logs
    /// the inputs a disagreement with the guiding residual needs: each live solve's RA/Dec, the seed pointing the readout
    /// is anchored to, and the tracking state. And it warns when the camera sits where the readout reads the altitude
    /// knob poorly (|cos ψ| under <see cref="WeakReadoutCosPsi"/>).
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The <c>polar_align.error</c> reason when the mount reads as tracking in a live loop.</summary>
        internal const string TrackingNotOff = "tracking_not_off";

        private const string TrackingStayedOnMessage =
            "the mount kept tracking after Polar Align turned tracking off, so the live readout cannot tell a knob turn " +
            "from the mount's own motion — check that the mount accepts tracking off, then start again";

        private const string TrackingCameOnMessage =
            "the mount started tracking during the live readout, so the readout no longer shows only your knob turns — " +
            "something turned tracking on (the hand pad, another app, the mount's firmware); start again";

        /// <summary>Under this |cos ψ| the readout's altitude reading amplifies solve noise five times or more, and under
        /// <see cref="PolarAlignGeometry.ReadoutCosPsiFloor"/> it under-reads the altitude knob: the run warns
        /// (<see cref="PolarAlignReadoutCautions.AltitudeWeak"/>).</summary>
        internal const double WeakReadoutCosPsi = 0.2;

        // The live readout's caution for the current seed (PolarAlignReadoutCautions); null when the readout is sound.
        // Guarded by _gate; cleared per run (StartAsync) and set again by each seed.
        private string? _readoutCaution;

        /// <summary>Turn tracking off for a live loop and wait until the mount reports it off. A refused write counts the
        /// same as one the mount ignores: the run fails <see cref="TrackingNotOff"/> once the confirm window ends with
        /// tracking still on. A thrown write fails the run as before (<c>internal_error</c>). A superseded run writes
        /// nothing.</summary>
        private async Task TurnTrackingOffForLiveLoopAsync(int gen, CancellationToken ct) {
            if (!TryMarkTrackingTouched(gen)) {
                throw new OperationCanceledException("the run was superseded before it turned tracking off");
            }
            try {
                _mount.SetTrackingEnabled(false);
            } finally {
                _ownTrackingWrites = _mount.TrackingWriteCount; // a refused or thrown write is still ours
            }
            var deadline = DateTimeOffset.UtcNow + TrackingConfirmTimeout;
            while (_mount.GetInfo().TrackingEnabled) {
                if (DateTimeOffset.UtcNow >= deadline) {
                    LogTrackingStayedOn(TrackingConfirmTimeout.TotalSeconds);
                    throw new RoutineFailedException(TrackingNotOff, TrackingStayedOnMessage);
                }
                await Task.Delay(TrackingConfirmPoll, ct).ConfigureAwait(false);
            }
            LogTrackingOffConfirmed();
        }

        /// <summary>Every live frame: the mount must still read tracking off. Returns the state read (false) for the log.</summary>
        private bool RequireTrackingOff(string frameId) {
            if (_mount.GetInfo().TrackingEnabled) {
                LogTrackingCameOn(frameId);
                throw new RoutineFailedException(TrackingNotOff, TrackingCameOnMessage);
            }
            return false;
        }

        /// <summary>Log the seed pointing the live readout is anchored to, and set or clear the readout's caution for it.</summary>
        internal void NoteReadoutReference(int gen, (double AltDeg, double AzDeg) seedPointing, double latitudeDeg) {
            var cosPsi = PolarAlignGeometry.ReadoutCosPsi(seedPointing.AzDeg, latitudeDeg);
            var weak = Math.Abs(cosPsi) < WeakReadoutCosPsi;
            LogReadoutReference(seedPointing.AltDeg, seedPointing.AzDeg, cosPsi);
            if (weak) {
                LogReadoutWeak(cosPsi, WeakReadoutCosPsi);
            }
            lock (_gate) {
                if (_generation == gen) {
                    _readoutCaution = weak ? PolarAlignReadoutCautions.AltitudeWeak : null;
                }
            }
        }

        [LoggerMessage(EventId = 4590, Level = LogLevel.Information, Message = "§45 polar-align: tracking off confirmed for the live readout")]
        private partial void LogTrackingOffConfirmed();

        [LoggerMessage(EventId = 4591, Level = LogLevel.Warning, Message = "§45 polar-align: the mount still reads tracking on {Seconds:F0} s after tracking off was sent — the run ends (tracking_not_off)")]
        private partial void LogTrackingStayedOn(double seconds);

        [LoggerMessage(EventId = 4592, Level = LogLevel.Warning, Message = "§45 polar-align live frame {FrameId}: the mount reads tracking on — the run ends (tracking_not_off)")]
        private partial void LogTrackingCameOn(string frameId);

        [LoggerMessage(EventId = 4594, Level = LogLevel.Information, Message = "§45 polar-align live readout reference: seed pointing alt {AltDeg:F4}° az {AzDeg:F4}°, cos ψ {CosPsi:F3}")]
        private partial void LogReadoutReference(double altDeg, double azDeg, double cosPsi);

        [LoggerMessage(EventId = 4595, Level = LogLevel.Warning, Message = "§45 polar-align: the camera sits where the live readout reads the altitude knob poorly (cos ψ {CosPsi:F3}, under {Limit:F2}) — trust the Check, or start nearer the pole")]
        private partial void LogReadoutWeak(double cosPsi, double limit);
    }
}
