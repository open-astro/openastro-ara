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
using OpenAstroAra.Equipment.Equipment.MyTelescope;
using OpenAstroAra.Server.Contracts;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using TelescopeAxes = OpenAstroAra.Core.Enums.TelescopeAxes;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// #2081 rows 4 and 5 — the RA turn direction the user sees, and a motor that turns the way it is shown. NINA's Three
    /// Point Polar Alignment turns <c>MoveAxis(Primary, EastDirection ? +rate : −rate)</c> and assumes a positive rate turns
    /// east. ASCOM leaves that sign to the driver (AlpacaBridge #2424), so Polar Align learns it once per mount: a run on a
    /// mount whose driver (DriverInfo and DriverVersion) has no saved direction first turns the RA axis
    /// <see cref="DirectionTestDeg"/> at the slow rate, with the guarded move of every turn (#2067), shown in the confirm
    /// step and counted toward the sweep limit, and saves which way the reading went (profile section
    /// <c>polar_align_motor_direction</c>). A later turn whose reading goes more than <see cref="WrongWayDeg"/> the other way
    /// is stopped and fails the run (<see cref="TurnWrongWay"/>); nothing ever reverses the motor mid-move. Settings shows
    /// the saved direction and "Test direction again", which forgets it so the next start tests again.
    /// </summary>
    public sealed partial class PolarAlignService {

        /// <summary>How far the direction test turns the RA axis (degrees, at the slow rate).</summary>
        internal const double DirectionTestDeg = 0.5;

        /// <summary>How far a turn's reading may go the other way from the saved motor direction before the turn fails.</summary>
        internal const double WrongWayDeg = 0.5;

        /// <summary>The <c>polar_align.error</c> reason of a turn whose reading went the other way from the saved RA motor
        /// direction (a driver upgrade that changed the sign, a wrong reading). Not the Polar Aligner's <c>wrong_direction</c>
        /// (#2069), which is about the aligner's own motors.</summary>
        internal const string TurnWrongWay = "turn_wrong_way";

        internal const string TurnWrongWayMessage =
            "the mount turned the other way from its saved RA motor direction, so Polar Align stopped it and never turned it back — check the mount, then tap Test direction again (Settings → Imaging → Polar align) and start again";

        /// <summary>The mount driver a saved motor direction stands for: its DriverInfo and DriverVersion.</summary>
        internal sealed record MountDriverKey(string Info, string Version);

        private MountDriverKey ReadMountDriver() {
            var info = _mount.GetInfo();
            return new MountDriverKey(info?.DriverInfo ?? string.Empty, info?.DriverVersion ?? string.Empty);
        }

        /// <summary>The saved RA motor sign (+1 or −1) when one stands for <paramref name="driver"/>; null otherwise (never
        /// tested, forgotten, or tested on another driver name or version).</summary>
        private double? SavedRaMotorSign(MountDriverKey driver) =>
            _profileStore.GetPolarAlignMotorDirection() is { } saved && saved.IsFor(driver.Info, driver.Version)
                ? saved.Sign
                : null;

        /// <summary><c>GET /polaralign/motor-direction</c>: the saved test, and whether it stands for the connected mount
        /// (null when none is connected). Pure, for the endpoint and its tests.</summary>
        internal static PolarAlignMotorDirectionStateDto MotorDirectionState(PolarAlignMotorDirectionDto? saved, TelescopeInfo? mount) =>
            new(saved, mount?.Connected == true ? saved is not null && saved.IsFor(mount.DriverInfo, mount.DriverVersion) : null);

        /// <summary>#2081 — when no saved RA motor direction stands for the mount's driver (<see cref="RunBand.RaMotorSign"/>
        /// null), turn the RA axis <see cref="DirectionTestDeg"/> before the measurement's first point, with the same
        /// guarded move as every turn (<see cref="MoveAxisUntilAsync"/>: steady first, the matched slow rate, the stall,
        /// the time bound, the Dec and pier-side checks, the arrival check), a rate that assumes NINA's positive = east, and
        /// progress counted either way. The reading says which way the driver turns: the run turns with that sign, and it is
        /// saved for the driver. The sweep limit is checked first for the test going either way. Nothing happens on a
        /// hand-turn run (the user turns RA) or when a saved sign stands.</summary>
        private async Task TestRaMotorDirectionIfOwedAsync(RunBand band, int gen, CancellationToken ct) {
            if (band.HandTurn || band.RaMotorSign is not null) {
                return;
            }
            var driver = band.Driver ?? ReadMountDriver();
            // A pointing moved onto the axis since the confirmed start fails the run before the test moves anything.
            await StartPointingOffTheAxisAsync(gen, ct).ConfigureAwait(false);
            var toward = band.AwaySign >= 0 ? 1.0 : -1.0;
            (double RaDeg, double DecDeg)? from = null;
            LogDirectionTestStarts(DirectionTestDeg, toward > 0 ? PolarAlignDirections.East : PolarAlignDirections.West,
                driver.Info, driver.Version);
            var after = await MoveAxisUntilAsync(TelescopeAxes.Primary, toward, DirectionTestDeg,
                (start, now) => Math.Abs(Wrap180(now.RaDeg - start.RaDeg)), checkDirection: false, gen, ct,
                beforeMove: start => {
                    from = start;
                    // A standard driver turns toward the run's turns, an inverted one away from them: either must fit.
                    ThrowIfTurnPassesSweepLimit(start.RaDeg, DirectionTestDeg);
                    ThrowIfTurnPassesSweepLimit(start.RaDeg, -DirectionTestDeg);
                },
                motorSign: 1.0).ConfigureAwait(false);
            var moved = Wrap180(after.RaDeg - from!.Value.RaDeg);
            if (Math.Abs(moved) < DirectionTestDeg / 2) {
                throw new RoutineFailedException(TurnMismatch, string.Create(CultureInfo.InvariantCulture,
                    $"the mount's RA reading moved only {Math.Abs(moved):F2}° in the {DirectionTestDeg:F1}° direction test, so its motor's direction is not known — check the mount, then start again"));
            }
            var sign = Math.Sign(moved) == Math.Sign(toward) ? 1 : -1;
            lock (_gate) {
                if (_generation != gen) {
                    // Never a superseded run's lesson: checked and written under the same lock as a new run's start.
                    throw new OperationCanceledException("the run was superseded during the direction test");
                }
                _raMotorSign = sign;
            }
            _profileStore.PutPolarAlignMotorDirection(new PolarAlignMotorDirectionDto(sign, driver.Info, driver.Version, DateTimeOffset.UtcNow));
            LogDirectionTested(moved, sign, driver.Info, driver.Version);
        }

        [LoggerMessage(EventId = 4640, Level = LogLevel.Information, Message = "§45 polar-align: no saved RA motor direction for the driver '{DriverInfo}' {DriverVersion} — testing it with a {Degrees:F1}° turn toward the {Direction} (a positive rate assumed east)")]
        private partial void LogDirectionTestStarts(double degrees, string direction, string driverInfo, string driverVersion);

        [LoggerMessage(EventId = 4641, Level = LogLevel.Information, Message = "§45 polar-align: the direction test moved the RA reading {MovedDeg:+0.00;-0.00}°: RA motor sign {Sign:+0;-0}, saved for the driver '{DriverInfo}' {DriverVersion}")]
        private partial void LogDirectionTested(double movedDeg, int sign, string driverInfo, string driverVersion);
    }
}
