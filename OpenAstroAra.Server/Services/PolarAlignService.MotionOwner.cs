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
using OpenAstroAra.Server.Contracts.Refusals;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// #2066 (ARA-2), #2065 (ARA-7) — one motion owner. Polar Align holds the mount's motion owner
    /// (<see cref="IMountMotionControl.AcquireMotion"/>) for its whole run, the adjust loop included (Joey, 2026-11-20 on
    /// #2048), and gives it back once the run's exit has stopped the mount: meanwhile the REST GoTo, Center, Park and Home and
    /// every mediator GoTo (the sequencer's) are refused. Nothing that stops the mount is (Emergency Stop, Stop Mount,
    /// AbortSlew, a pad stop, tracking off, the safety, fault and shutdown parks). The run takes the owner before its start
    /// reading, and the daemon checks it in one step with each GoTo's send: a GoTo sent before shows on that reading, and one
    /// accepted before but not yet sent is dropped. A motion the daemon does not send (the hand controller, another
    /// program) is caught before each of the run's turns: two fresh readings <see cref="MountSteadyFor"/> apart must both say "not slewing" and agree within
    /// <see cref="StopStillDeg"/>, and the guider must not be guiding or calibrating, or the run fails with
    /// <see cref="MountBusy"/> before the motor starts. A start refuses the same on its one reading
    /// (<c>polar-align-mount-busy</c>).
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>The <c>polar_align.error</c> reason of a run whose mount moved, or whose guider guided, when a turn was
        /// due.</summary>
        internal const string MountBusy = "mount_busy";

        /// <summary>How far apart the two readings before each turn are taken: the mount must stand still between them.</summary>
        internal TimeSpan MountSteadyFor { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>The start's check (the plan and the start itself) on its one fresh reading: a mount that slews (or does
        /// not say whether it does), or a guider that guides or calibrates, is refused with <c>polar-align-mount-busy</c>
        /// before the camera is taken or anything moves.</summary>
        private void ThrowIfBusyAtStart(bool? slewing) {
            if (GuiderBusyState() is { } state) {
                LogMountBusy(GuiderBusyWhy(state));
                throw new RefusalException(RefusalCatalog.PolarAlignMountBusy, MountBusyMessage(GuiderBusyWhy(state)));
            }
            if (slewing != false) {
                var why = SlewingWhy(slewing);
                LogMountBusy(why);
                throw new RefusalException(RefusalCatalog.PolarAlignMountBusy, MountBusyMessage(why));
            }
        }

        /// <summary>
        /// Before each of the run's turns: the guider is not guiding or calibrating, and two fresh readings
        /// <see cref="MountSteadyFor"/> apart both say the mount is not slewing and stands still (under
        /// <see cref="StopStillDeg"/> in RA and in Dec). Otherwise the run fails with <see cref="MountBusy"/> and the motor
        /// never starts. A reading the run cannot trust fails it as every run reading does (<c>pointing_unknown</c>,
        /// <c>pose_ambiguous</c>). Returns the second reading: where the turn starts.
        /// </summary>
        private async Task<(double RaDeg, double DecDeg, PierSide Side)> EnsureMountSteadyAsync(int gen, CancellationToken ct) {
            ThrowIfGuiderBusy();
            var first = await RunReadingAsync(gen, MountReadKind.Full, ct).ConfigureAwait(false);
            ThrowIfSlewing(first.Slewing);
            await Task.Delay(MountSteadyFor, ct).ConfigureAwait(false);
            ThrowIfStoppedFromOutside(gen);
            var second = await RunReadingAsync(gen, MountReadKind.Full, ct).ConfigureAwait(false);
            ThrowIfSlewing(second.Slewing);
            var movedRa = Math.Abs(Wrap180(second.RaDeg - first.RaDeg));
            var movedDec = Math.Abs(second.DecDeg - first.DecDeg);
            if (movedRa >= StopStillDeg || movedDec >= StopStillDeg) {
                var why = string.Create(CultureInfo.InvariantCulture,
                    $"the mount moved {Math.Max(movedRa, movedDec):F2}° in {MountSteadyFor.TotalSeconds:F1} s by other means");
                LogMountBusy(why);
                throw new RoutineFailedException(MountBusy, MountBusyMessage(why));
            }
            ThrowIfGuiderBusy();
            return (second.RaDeg, second.DecDeg, second.Side);
        }

        private void ThrowIfSlewing(bool? slewing) {
            if (slewing != false) {
                var why = SlewingWhy(slewing);
                LogMountBusy(why);
                throw new RoutineFailedException(MountBusy, MountBusyMessage(why));
            }
        }

        private void ThrowIfGuiderBusy() {
            if (GuiderBusyState() is { } state) {
                var why = GuiderBusyWhy(state);
                LogMountBusy(why);
                throw new RoutineFailedException(MountBusy, MountBusyMessage(why));
            }
        }

        /// <summary>The guider's app state when it guides or calibrates (<see cref="GuideCameraCustody.IsBusy"/>), else null.</summary>
        private string? GuiderBusyState() {
            var state = _guider.AppState;
            return GuideCameraCustody.IsBusy(state) ? state : null;
        }

        private static string SlewingWhy(bool? slewing) =>
            slewing is null ? "the mount does not say whether it is slewing" : "the mount is slewing";

        private static string GuiderBusyWhy(string state) =>
            $"the guider is {state.ToLowerInvariant()}";

        internal static string MountBusyMessage(string why) =>
            $"Polar Align moves the mount only while nothing else moves it ({why}) — let the slew finish or stop guiding, then start again";

        // ── the run's hold of the motion owner ───────────────────────────────────────────────

        /// <summary>Take the mount's motion owner for a run. Another owner holding it refuses the start with
        /// <c>polar-align-mount-busy</c>.</summary>
        private IDisposable AcquireMountForRun() {
            try {
                return _motionControl.AcquireMotion(MotionOwners.PolarAlign);
            } catch (InvalidOperationException ex) when (ex is not RefusalException) {
                LogMountBusy(ex.Message);
                throw new RefusalException(RefusalCatalog.PolarAlignMountBusy, MountBusyMessage(ex.Message));
            }
        }

        [LoggerMessage(Level = LogLevel.Warning, Message = "§45 polar-align: the mount is busy ({Why}) — nothing moves (mount_busy)")]
        private partial void LogMountBusy(string why);
    }
}
