#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Services;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Sim-free unit coverage for the §14e <see cref="ITelescopeMediator"/> surface that
    /// <see cref="TelescopeService"/> serves alongside its REST surface (one singleton backs both, so
    /// the telescope instructions drive the live mount). The live ops are exercised by the
    /// <c>[Category("Integration")]</c> companion test; here we cover the not-connected / disposed
    /// contracts the Sequencer relies on never to throw, plus the pure mapping helpers.
    /// </summary>
    [TestFixture]
    public class TelescopeMediatorTest {

        private static Coordinates SampleTarget() =>
            new Coordinates(Angle.ByHours(6.0), Angle.ByDegree(45.0), Epoch.J2000);

        [Test]
        public void GetInfo_before_connect_reports_not_connected() {
            using var svc = new TelescopeService();
            var info = ((ITelescopeMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
            Assert.That(info.AtPark, Is.False);
            Assert.That(info.CanFindHome, Is.False);
            Assert.That(info.TrackingModes, Is.Empty);
        }

        [Test]
        public void GetInfo_after_Dispose_reports_not_connected_without_throwing() {
            var svc = new TelescopeService();
            svc.Dispose();
            var info = ((ITelescopeMediator)svc).GetInfo();
            Assert.That(info.Connected, Is.False);
        }

        [Test]
        public async Task SlewToCoordinatesAsync_when_not_connected_returns_false() {
            using var svc = new TelescopeService();
            Assert.That(
                await ((ITelescopeMediator)svc).SlewToCoordinatesAsync(SampleTarget(), CancellationToken.None),
                Is.False);
        }

        [Test]
        public void SlewToCoordinatesAsync_null_coords_throws() {
            using var svc = new TelescopeService();
            Assert.Throws<System.ArgumentNullException>(
                () => { _ = ((ITelescopeMediator)svc).SlewToCoordinatesAsync((Coordinates)null!, CancellationToken.None); });
        }

        [Test]
        public async Task Sync_when_not_connected_returns_false() {
            // §28 — the centering loop calls Sync; when the mount isn't connected it must report a clean
            // false (the loop offset-compensates) rather than touching astrometry or throwing.
            using var svc = new TelescopeService();
            Assert.That(
                await ((ITelescopeMediator)svc).Sync(SampleTarget()),
                Is.False);
        }

        [Test]
        public void Sync_null_coords_throws() {
            using var svc = new TelescopeService();
            Assert.ThrowsAsync<System.ArgumentNullException>(
                () => ((ITelescopeMediator)svc).Sync((Coordinates)null!));
        }

        [Test]
        public async Task ParkTelescope_when_not_connected_returns_false() {
            using var svc = new TelescopeService();
            Assert.That(
                await ((ITelescopeMediator)svc).ParkTelescope(progress: null!, CancellationToken.None),
                Is.False);
        }

        [Test]
        public async Task UnparkTelescope_when_not_connected_returns_false() {
            using var svc = new TelescopeService();
            Assert.That(
                await ((ITelescopeMediator)svc).UnparkTelescope(progress: null!, CancellationToken.None),
                Is.False);
        }

        [Test]
        public async Task FindHome_when_not_connected_returns_false() {
            using var svc = new TelescopeService();
            Assert.That(
                await ((ITelescopeMediator)svc).FindHome(progress: null!, CancellationToken.None),
                Is.False);
        }

        [Test]
        public async Task Ops_after_Dispose_return_false_without_throwing() {
            var svc = new TelescopeService();
            svc.Dispose();
            Assert.That(
                await ((ITelescopeMediator)svc).ParkTelescope(progress: null!, CancellationToken.None),
                Is.False);
            Assert.That(
                await ((ITelescopeMediator)svc).SlewToCoordinatesAsync(SampleTarget(), CancellationToken.None),
                Is.False);
            Assert.That(
                await ((ITelescopeMediator)svc).Sync(SampleTarget()),
                Is.False);
        }

        [Test]
        public void SetTrackingEnabled_when_not_connected_returns_false() {
            using var svc = new TelescopeService();
            Assert.That(((ITelescopeMediator)svc).SetTrackingEnabled(true), Is.False);
        }

        [Test]
        public void SetTrackingMode_when_not_connected_returns_false() {
            using var svc = new TelescopeService();
            Assert.That(((ITelescopeMediator)svc).SetTrackingMode(TrackingMode.Sidereal), Is.False);
        }

        [Test]
        public void SetTrackingMode_Custom_is_unsupported_headless() {
            using var svc = new TelescopeService();
            Assert.That(((ITelescopeMediator)svc).SetTrackingMode(TrackingMode.Custom), Is.False);
        }

        [Test]
        public void StopSlew_when_not_connected_does_not_throw() {
            using var svc = new TelescopeService();
            Assert.DoesNotThrow(() => ((ITelescopeMediator)svc).StopSlew());
        }

        [Test]
        public void WaitForSlew_when_not_connected_completes() {
            using var svc = new TelescopeService();
            Assert.DoesNotThrowAsync(() => ((ITelescopeMediator)svc).WaitForSlew(CancellationToken.None));
        }

        [Test]
        public void GetCurrentPosition_when_not_connected_returns_zero_J2000_sentinel() {
            using var svc = new TelescopeService();
            var position = ((ITelescopeMediator)svc).GetCurrentPosition();
            Assert.That(position.RADegrees, Is.EqualTo(0));
            Assert.That(position.Dec, Is.EqualTo(0));
            Assert.That(position.Epoch, Is.EqualTo(Epoch.J2000));
        }

        [Test]
        public void Unconsumed_surface_reports_failure_stubs() {
            using var svc = new TelescopeService();
            Assert.That(((ITelescopeMediator)svc).DestinationSideOfPier(SampleTarget()), Is.EqualTo(PierSide.pierUnknown));
            Assert.That(((ITelescopeMediator)svc).SendToSnapPort(true), Is.False);
            Assert.DoesNotThrow(() => ((ITelescopeMediator)svc).MoveAxis(TelescopeAxes.Primary, 1.0));
        }

        [Test]
        public void MapEpoch_covers_the_ascom_coordinate_systems() {
            Assert.That(TelescopeService.MapEpoch(EquatorialCoordinateType.J2000), Is.EqualTo(Epoch.J2000));
            Assert.That(TelescopeService.MapEpoch(EquatorialCoordinateType.J2050), Is.EqualTo(Epoch.J2050));
            Assert.That(TelescopeService.MapEpoch(EquatorialCoordinateType.B1950), Is.EqualTo(Epoch.B1950));
            Assert.That(TelescopeService.MapEpoch(EquatorialCoordinateType.Topocentric), Is.EqualTo(Epoch.JNOW));
            Assert.That(TelescopeService.MapEpoch(EquatorialCoordinateType.Other), Is.EqualTo(Epoch.JNOW));
        }

        [Test]
        public void MapSlewEpoch_clamps_untransformable_epochs_to_JNOW() {
            // Coordinates.Transform throws NotSupportedException for J2050/B1950 targets — the slew
            // path must never feed it one.
            Assert.That(TelescopeService.MapSlewEpoch(EquatorialCoordinateType.J2000), Is.EqualTo(Epoch.J2000));
            Assert.That(TelescopeService.MapSlewEpoch(EquatorialCoordinateType.Topocentric), Is.EqualTo(Epoch.JNOW));
            Assert.That(TelescopeService.MapSlewEpoch(EquatorialCoordinateType.J2050), Is.EqualTo(Epoch.JNOW));
            Assert.That(TelescopeService.MapSlewEpoch(EquatorialCoordinateType.B1950), Is.EqualTo(Epoch.JNOW));
        }

        [Test]
        public void TransformBestEffort_degrades_to_untransformed_target_when_natives_missing() {
            // The SOFA/NOVAS natives are not packaged for this platform yet (PORT_TODO §14e), so a
            // cross-epoch transform must fall back to the original coordinates, never throw.
            using var svc = new TelescopeService();
            var j2000 = SampleTarget();
            Coordinates? result = null;
            Assert.DoesNotThrow(() => result = svc.TransformBestEffort(j2000, Epoch.JNOW));
            Assert.That(result, Is.Not.Null);
            // Once the natives ship this returns a real JNOW transform; until then it's the input.
            Assert.That(result!.Epoch, Is.EqualTo(Epoch.JNOW).Or.EqualTo(Epoch.J2000));
        }

        [Test]
        public void MapDriveRateName_maps_known_rates_and_rejects_unknown() {
            Assert.That(TelescopeService.MapDriveRateName("Sidereal"), Is.EqualTo(TrackingMode.Sidereal));
            Assert.That(TelescopeService.MapDriveRateName("Lunar"), Is.EqualTo(TrackingMode.Lunar));
            Assert.That(TelescopeService.MapDriveRateName("Solar"), Is.EqualTo(TrackingMode.Solar));
            Assert.That(TelescopeService.MapDriveRateName("King"), Is.EqualTo(TrackingMode.King));
            Assert.That(TelescopeService.MapDriveRateName("NotARate"), Is.Null);
        }

        [Test]
        public void MapDriveRate_maps_every_settable_mode() {
            Assert.That(TelescopeService.MapDriveRate(TrackingMode.Sidereal), Is.EqualTo(DriveRate.Sidereal));
            Assert.That(TelescopeService.MapDriveRate(TrackingMode.Lunar), Is.EqualTo(DriveRate.Lunar));
            Assert.That(TelescopeService.MapDriveRate(TrackingMode.Solar), Is.EqualTo(DriveRate.Solar));
            Assert.That(TelescopeService.MapDriveRate(TrackingMode.King), Is.EqualTo(DriveRate.King));
        }
    }
}
