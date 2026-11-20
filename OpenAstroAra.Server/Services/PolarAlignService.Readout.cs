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

        private const string TrackingRefusedMessage =
            "the mount refused tracking off, so the live readout cannot tell a knob turn from the mount's own motion — " +
            "check that the mount accepts tracking off, then start again";

        private const string MountGoneMessage =
            "the mount is not connected, so Polar Align cannot confirm tracking is off for the live readout — " +
            "reconnect the mount, then start again";

        private const string MountMovingMessage =
            "the mount reads as slewing in the live readout, so the readout no longer shows only your knob turns — " +
            "something moved the mount (the hand pad, another app); start again";

        private const string TrackingCameOnMessage =
            "the mount started tracking during the live readout, so the readout no longer shows only your knob turns — " +
            "something turned tracking on (the hand pad, another app, the mount's firmware); start again";

        /// <summary>Under this |cos ψ| the readout's altitude reading amplifies solve noise five times or more, and under
        /// <see cref="PolarAlignGeometry.ReadoutCosPsiFloor"/> it under-reads the altitude knob: the run warns
        /// (<see cref="PolarAlignReadoutCautions.AltitudeWeak"/>).</summary>
        internal const double DefaultWeakReadoutCosPsi = 0.2;

        /// <summary><see cref="DefaultWeakReadoutCosPsi"/>; tests raise it to put any seed in the weak band.</summary>
        internal double WeakReadoutCosPsi { get; set; } = DefaultWeakReadoutCosPsi;

        // The live readout's caution for the current seed (PolarAlignReadoutCautions); null when the readout is sound.
        // Guarded by _gate; set by each seed once tracking is confirmed off, cleared per run (StartAsync); the status shows
        // it only while a live readout runs (adjusting or paused).
        private string? _readoutCaution;

        /// <summary>Turn tracking off for a live loop and wait until the mount reports it off and still: the run fails
        /// <see cref="TrackingNotOff"/> at once when the mount refuses the write and still reads tracking on, or is not
        /// connected (a disconnected mount reads tracking off, which confirms nothing), and once the confirm window ends
        /// when it accepts the write and keeps tracking or slewing. A thrown write fails the run as before
        /// (<c>internal_error</c>). A superseded run writes nothing.</summary>
        private async Task TurnTrackingOffForLiveLoopAsync(int gen, CancellationToken ct) {
            if (!TryMarkTrackingTouched(gen)) {
                throw new OperationCanceledException("the run was superseded before it turned tracking off");
            }
            bool accepted;
            try {
                accepted = _mount.SetTrackingEnabled(false);
            } finally {
                _ownTrackingWrites = _mount.TrackingWriteCount; // a refused or thrown write is still ours
            }
            var info = _mount.GetInfo();
            if (!accepted && (!info.Connected || info.TrackingEnabled)) {
                LogTrackingOffRefused(info.Connected);
                throw new RoutineFailedException(TrackingNotOff, info.Connected ? TrackingRefusedMessage : MountGoneMessage);
            }
            var deadline = DateTimeOffset.UtcNow + TrackingConfirmTimeout;
            while (!info.Connected || info.TrackingEnabled || info.Slewing) {
                if (DateTimeOffset.UtcNow >= deadline) {
                    LogTrackingStayedOn(TrackingConfirmTimeout.TotalSeconds, info.Connected, info.TrackingEnabled, info.Slewing);
                    throw new RoutineFailedException(TrackingNotOff,
                        !info.Connected ? MountGoneMessage : info.TrackingEnabled ? TrackingStayedOnMessage : MountMovingMessage);
                }
                await Task.Delay(TrackingConfirmPoll, ct).ConfigureAwait(false);
                info = _mount.GetInfo();
            }
            LogTrackingOffConfirmed();
        }

        /// <summary>Every live frame: the mount must still read tracking off and not slewing (an iOptron reads tracking off
        /// while it slews, so tracking alone would miss a hand-pad slew). A mount that dropped its connection mid-loop is not
        /// failed here: it cannot change its own tracking while disconnected, and the first frame after the reconnect reads
        /// it again. Returns the tracking state read (false) for the log.</summary>
        private bool RequireTrackingOff(string frameId) {
            var info = _mount.GetInfo();
            if (info.TrackingEnabled) {
                LogTrackingCameOn(frameId);
                throw new RoutineFailedException(TrackingNotOff, TrackingCameOnMessage);
            }
            if (info.Slewing) {
                LogMountMovedInReadout(frameId);
                throw new RoutineFailedException(TrackingNotOff, MountMovingMessage);
            }
            return false;
        }

        /// <summary>Log the seed pointing the live readout is anchored to, and set or clear the readout's caution for it.
        /// Returns the caution (<see cref="PolarAlignReadoutCautions"/>), or null for a sound readout.</summary>
        internal string? NoteReadoutReference(int gen, (double AltDeg, double AzDeg) seedPointing, double latitudeDeg) {
            var cosPsi = PolarAlignGeometry.ReadoutCosPsi(seedPointing.AzDeg, latitudeDeg);
            var caution = Math.Abs(cosPsi) < WeakReadoutCosPsi ? PolarAlignReadoutCautions.AltitudeWeak : null;
            LogReadoutReference(seedPointing.AltDeg, seedPointing.AzDeg, cosPsi);
            if (caution is not null) {
                LogReadoutWeak(cosPsi, WeakReadoutCosPsi);
            }
            lock (_gate) {
                if (_generation == gen) {
                    _readoutCaution = caution;
                }
            }
            return caution;
        }

        [LoggerMessage(EventId = 4590, Level = LogLevel.Information, Message = "§45 polar-align: tracking off confirmed for the live readout")]
        private partial void LogTrackingOffConfirmed();

        [LoggerMessage(EventId = 4591, Level = LogLevel.Warning, Message = "§45 polar-align: tracking off not confirmed {Seconds:F0} s after it was sent (connected {Connected}, tracking {Tracking}, slewing {Slewing}) — the run ends (tracking_not_off)")]
        private partial void LogTrackingStayedOn(double seconds, bool connected, bool tracking, bool slewing);

        [LoggerMessage(EventId = 4593, Level = LogLevel.Warning, Message = "§45 polar-align: the mount refused tracking off and reads tracking on or is not connected (connected {Connected}) — the run ends (tracking_not_off)")]
        private partial void LogTrackingOffRefused(bool connected);

        [LoggerMessage(EventId = 4592, Level = LogLevel.Warning, Message = "§45 polar-align live frame {FrameId}: the mount reads tracking on — the run ends (tracking_not_off)")]
        private partial void LogTrackingCameOn(string frameId);

        [LoggerMessage(EventId = 4596, Level = LogLevel.Warning, Message = "§45 polar-align live frame {FrameId}: the mount reads slewing — the run ends (tracking_not_off)")]
        private partial void LogMountMovedInReadout(string frameId);

        [LoggerMessage(EventId = 4594, Level = LogLevel.Information, Message = "§45 polar-align live readout reference: seed pointing alt {AltDeg:F4}° az {AzDeg:F4}°, cos ψ {CosPsi:F3}")]
        private partial void LogReadoutReference(double altDeg, double azDeg, double cosPsi);

        [LoggerMessage(EventId = 4595, Level = LogLevel.Warning, Message = "§45 polar-align: the camera sits where the live readout reads the altitude knob poorly (cos ψ {CosPsi:F3}, under {Limit:F2}) — trust Verify, or start nearer the pole")]
        private partial void LogReadoutWeak(double cosPsi, double limit);
    }
}
