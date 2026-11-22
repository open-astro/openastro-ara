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
    /// mount with no saved direction for its identity and driver version (#2167: <see cref="MountDriverKey"/>) first turns the RA axis
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

        /// <summary>#2081 — the active profile's id (<see cref="IProfileRepository.ActiveId"/>), read when a run is planned
        /// and again before the direction test saves its sign. Null (tests, no repository): the save is never skipped.</summary>
        internal Func<Guid?>? ActiveProfileId { get; set; }

        /// <summary>#2167 — the mount a saved motor direction stands for: <see cref="Mount"/> is its identity, the key its
        /// <c>mount_clearance</c> entry uses (<see cref="PolarAlignPreflightEvaluator.MountKey"/>: the Alpaca UniqueID, else
        /// its name, else <see cref="PolarAlignPreflightEvaluator.UnidentifiedMountKey"/>; blank only with no mount
        /// connected, a key no saved test stands for and none is saved under), <see cref="Info"/> its driver's DriverInfo
        /// and <see cref="Version"/> its DriverVersion (both read once per connect; empty when the driver reports none).
        /// The driver's name is in the key because another driver set up behind the same Alpaca device keeps its UniqueID
        /// and the bridge's version. A blank DriverInfo and DriverVersion were the whole key before: the daemon never read
        /// them, so every mount shared one saved direction.</summary>
        internal sealed record MountDriverKey(string Mount, string Info, string Version) {
            /// <summary>What a log line names: the mount, and its driver's name and version when they are reported.</summary>
            public string Identity {
                get {
                    var mount = Mount.Length == 0 ? "(no mount connected)" : Mount;
                    var driver = Info.Length == 0 ? Version : Version.Length == 0 ? Info : $"{Info} {Version}";
                    return driver.Length == 0 ? mount : $"{mount}, driver {driver}";
                }
            }
        }

        /// <summary>The key of the mount <paramref name="info"/> reports. With no mount connected the key is blank (never
        /// <see cref="PolarAlignPreflightEvaluator.UnidentifiedMountKey"/>, which a mount with no UniqueID and no name
        /// saves under): no saved test stands for it, and a test is never saved under it.</summary>
        internal static MountDriverKey MountDriverOf(TelescopeInfo? info) =>
            new(PolarAlignPreflightEvaluator.MountKey(info) ?? string.Empty,
                info?.Connected == true ? info.DriverInfo?.Trim() ?? string.Empty : string.Empty,
                info?.Connected == true ? info.DriverVersion?.Trim() ?? string.Empty : string.Empty);

        private MountDriverKey ReadMountDriver() => MountDriverOf(_mount.GetInfo());

        /// <summary>The saved RA motor sign (+1 or −1) when one stands for <paramref name="driver"/>; null otherwise (never
        /// tested, forgotten, tested on another mount or driver name or version, or no mount connected).</summary>
        private double? SavedRaMotorSign(MountDriverKey driver) =>
            _profileStore.GetPolarAlignMotorDirection() is { } saved && saved.IsFor(driver.Mount, driver.Info, driver.Version)
                ? saved.Sign
                : null;

        /// <summary><c>GET /polaralign/motor-direction</c>: the saved test, and whether it stands for the connected mount
        /// (null when none is connected). A test saved before #2167 has no mount key and stands for no mount: it is sent as
        /// nothing saved (never a null <c>mount_key</c> the client's strict reader refuses), so the next start tests again.
        /// Pure, for the endpoint and its tests.</summary>
        internal static PolarAlignMotorDirectionStateDto MotorDirectionState(PolarAlignMotorDirectionDto? saved, TelescopeInfo? mount) {
            saved = saved is { MountKey: { } key } && !string.IsNullOrWhiteSpace(key) && saved.DriverInfo is not null
                    && saved.DriverVersion is not null
                ? saved
                : null;
            if (mount?.Connected != true) {
                return new(saved, null);
            }
            var driver = MountDriverOf(mount);
            return new(saved, saved is not null && saved.IsFor(driver.Mount, driver.Info, driver.Version));
        }

        /// <summary>#2081 — when no saved RA motor direction stands for the mount's driver (<see cref="RunBand.RaMotorSign"/>
        /// null), turn the RA axis <see cref="DirectionTestDeg"/> before the measurement's first point, with the same
        /// guarded move as every turn (<see cref="MoveAxisUntilAsync"/>: steady first, the matched slow rate, the stall,
        /// the time bound, the Dec and pier-side checks, the arrival check), a rate that assumes NINA's positive = east, and
        /// progress counted either way. The reading says which way the driver turns: the run turns with that sign, and it is
        /// saved for the driver. The sweep limit is checked first for the test going either way. Nothing happens when a
        /// saved sign stands.</summary>
        private async Task TestRaMotorDirectionIfOwedAsync(RunBand band, int gen, CancellationToken ct) {
            if (band.RaMotorSign is not null) {
                return;
            }
            var driver = band.Driver ?? ReadMountDriver();
            // A pointing moved onto the axis since the confirmed start fails the run before the test moves anything.
            await StartPointingOffTheAxisAsync(gen, ct).ConfigureAwait(false);
            var toward = band.AwaySign >= 0 ? 1.0 : -1.0;
            (double RaDeg, double DecDeg)? from = null;
            LogDirectionTestStarts(DirectionTestDeg, toward > 0 ? PolarAlignDirections.East : PolarAlignDirections.West,
                driver.Identity);
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
            // A profile switch during the test swaps the store: the sign belongs to the profile the run was planned under,
            // so it is not written into another one. This run still turns with it (the same mount and driver).
            var profileNow = ActiveProfileId?.Invoke();
            if (profileNow != band.ProfileId) {
                LogDirectionTestNotSavedProfileSwitched(sign, band.ProfileId, profileNow);
                return;
            }
            if (driver.Mount.Length == 0) {
                // No mount was connected when the key was read: a blank key stands for no mount, so nothing is saved.
                return;
            }
            _profileStore.PutPolarAlignMotorDirection(new PolarAlignMotorDirectionDto(sign, driver.Mount, driver.Info, driver.Version, DateTimeOffset.UtcNow));
            LogDirectionTested(moved, sign, driver.Identity);
        }

        [LoggerMessage(EventId = 4650, Level = LogLevel.Information, Message = "§45 polar-align: no saved RA motor direction for the mount {Mount} — testing it with a {Degrees:F1}° turn toward the {Direction} (a positive rate assumed east)")]
        private partial void LogDirectionTestStarts(double degrees, string direction, string mount);

        [LoggerMessage(EventId = 4651, Level = LogLevel.Information, Message = "§45 polar-align: the direction test moved the RA reading {MovedDeg:+0.00;-0.00}°: RA motor sign {Sign:+0;-0}, saved for the mount {Mount}")]
        private partial void LogDirectionTested(double movedDeg, int sign, string mount);

        [LoggerMessage(EventId = 4652, Level = LogLevel.Warning, Message = "§45 polar-align: the direction test found RA motor sign {Sign:+0;-0}, not saved: the active profile changed during it (was {ProfileAtPlan}, now {ProfileNow}); this run turns with it")]
        private partial void LogDirectionTestNotSavedProfileSwitched(int sign, Guid? profileAtPlan, Guid? profileNow);
    }
}
