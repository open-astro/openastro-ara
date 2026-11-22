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
using Moq;
using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.Refusals;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PierSide = OpenAstroAra.Core.Enums.PierSide;

namespace OpenAstroAra.Test {

    /// <summary>#2173 — clear an unverified mount position with a plate solve. The real telescope service against a fake mount
    /// that woke at its false zero, a fake camera and solver: a match trusts it for every reason, a mismatch offers one Sync,
    /// the pier side is checked two ways, and the check itself never sends the mount any motion.</summary>
    [TestFixture]
    public class PoseSolveCheckServiceTest {

        private static readonly DiscoveredDeviceDto Mount = new(
            UniqueId: "mount-1", Name: "Bench Mount", Type: DeviceType.Telescope,
            HostName: "127.0.0.1", IpAddress: "127.0.0.1", IpPort: 11111, AlpacaDeviceNumber: 0, UseHttps: false);

        private const double LongitudeDeg = -80.0;

        private sealed class MemoryPoseStore : IMountPoseStore {
            public readonly Dictionary<string, string> Unverified = new();
            public string? UnverifiedReason(string deviceId) { lock (Unverified) { return Unverified.GetValueOrDefault(deviceId); } }
            public void Store(string deviceId, string? reason) {
                lock (Unverified) {
                    if (reason is null) { Unverified.Remove(deviceId); } else { Unverified[deviceId] = reason; }
                }
            }
        }

        /// <summary>A camera and solver: the sky it answers is set by the test (null = the frame does not solve), and a hook
        /// runs during each exposure.</summary>
        private sealed class FakeCamera : IPoseSolveCamera {
            public PoseSolveCameraChoice Choice = new(PoseSolveCameras.Main, "main:cam-1:rotator none", null);
            public Queue<(double RaHours, double DecDeg, double? Pa)?> Skies { get; } = new();
            public (double RaHours, double DecDeg, double? Pa)? Sky;
            public Action? DuringExposure;
            public int Captures;

            public Task<PoseSolveCameraChoice> ChooseAsync(CancellationToken ct) => Task.FromResult(Choice);

            public Task<PoseSolveFrame> CaptureAndSolveAsync(PoseSolveCameraChoice camera, Coordinates? hint, CancellationToken ct) {
                Captures++;
                DuringExposure?.Invoke();
                var sky = Skies.Count > 0 ? Skies.Dequeue() : Sky;
                return Task.FromResult(sky is { } s
                    ? new PoseSolveFrame(true, new Coordinates(Angle.ByHours(s.RaHours), Angle.ByDegree(s.DecDeg), Epoch.JNOW), s.Pa, 2.0)
                    : new PoseSolveFrame(false, null, null, null, "no stars"));
            }
        }

        private FakeTelescopeDevice _fake = null!;
        private MemoryPoseStore _store = null!;
        private FakeCamera _camera = null!;
        private MemoryPoseSolvePaStore _angles = null!;

        [SetUp]
        public void SetUp() {
            _store = new MemoryPoseStore();
            _camera = new FakeCamera();
            _angles = new MemoryPoseSolvePaStore();
        }

        private static double LstHours => SiteAstrometry.LocalSiderealTimeDeg(DateTimeOffset.UtcNow, LongitudeDeg) / 15.0;

        // East of the meridian by 3 h: the expected pier side is West (ASCOM ThroughThePole); west of it, East (Normal).
        private static double RaWestOfMeridian => Wrap24(LstHours - 3.0);

        private static double Wrap24(double h) => ((h % 24.0) + 24.0) % 24.0;

        private static IProfileStore Site() {
            var profiles = new Mock<IProfileStore>();
            profiles.Setup(p => p.GetSiteSettings()).Returns(new SiteSettingsDto("bench", 35.0, LongitudeDeg, 0, "UTC", false, 0, 4, 2.0, "astronomical"));
            return profiles.Object;
        }

        private static async Task WaitForAsync(Func<bool> condition, string what) {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!condition()) {
                if (DateTime.UtcNow > deadline) {
                    Assert.Fail($"timed out waiting for {what}");
                }
                await Task.Delay(10);
            }
        }

        private static TelescopeStateDto? Runtime(TelescopeService s) => s.GetAsync(CancellationToken.None).Result?.Runtime;

        // The RC51 case: the mount woke at its false zero (AtHome, Dec 90) and Ara flagged it at_home_on_connect, a reason
        // a Home alone may not clear. Then the user pads it away (the flag stays): it reads RA ra, Dec dec, pier side East.
        private async Task<(TelescopeService Mount, PoseSolveCheckService Check)> UnverifiedAtAsync(double ra, double dec) {
            var mount = new TelescopeService(poseStore: _store) {
                RefreshPeriod = TimeSpan.FromMilliseconds(20),
                StopGuidingBeforeMoveTimeout = TimeSpan.FromMilliseconds(50),
                DeviceFactory = _ => {
                    _fake = new FakeTelescopeDevice { ReportsAtHome = true, Dec = 90 };
                    return _fake;
                },
            };
            await mount.ConnectAsync(new ConnectRequestDto(Mount), null, CancellationToken.None);
            await WaitForAsync(() => mount.IsCleanlyConnected && Runtime(mount)?.PositionUnverified == true, "the unverified position");
            Assert.That(Runtime(mount)!.PositionUnverifiedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect), "precondition");
            _fake.ReportsAtHome = false;
            _fake.Ra = ra;
            _fake.Dec = dec;
            return (mount, new PoseSolveCheckService(mount, _camera, Site(), _angles));
        }

        private static async Task<RefusalException> RefusedAsync(Func<Task> call) {
            try {
                await call();
            } catch (RefusalException refusal) {
                return refusal;
            }
            Assert.Fail("the call was not refused");
            return null!;
        }

        private void AssertNoMotion() {
            Assert.Multiple(() => {
                Assert.That(_fake.Slews, Is.Zero, "no GoTo");
                Assert.That(_fake.MoveAxisStarts, Is.Zero, "no MoveAxis");
                Assert.That(_fake.Homes, Is.Zero, "no FindHome");
                Assert.That(_fake.Parks, Is.Zero, "no Park");
                Assert.That(_fake.PierSideWrites, Is.Zero, "no pier-side write");
            });
        }

        [Test]
        public async Task A_matching_solve_trusts_the_position_for_every_reason_and_sends_no_motion() {
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _camera.Sky = (ra + 0.01, 30.2, 12.0);

            var result = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("trusted"), result.Detail);
            Assert.That(result.ClearedReason, Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
            Assert.That(result.Camera, Is.EqualTo(PoseSolveCameras.Main));
            Assert.That(await mount.PositionUnverifiedReasonAsync(CancellationToken.None), Is.Null);
            Assert.That(_store.UnverifiedReason(Mount.UniqueId), Is.Null, "persisted like other trust changes");
            await WaitForAsync(() => Runtime(mount)?.PositionUnverified == false, "the pushed row");
            Assert.That(_fake.Syncs, Is.Zero, "a match syncs nothing");
            AssertNoMotion();
            Assert.That(_angles.Lookup($"{Mount.UniqueId}|main:cam-1:rotator none"), Is.EqualTo((12.0, PierSide.pierEast)),
                "the trusted solve's position angle is kept for the next check's pier-side test");
        }

        [Test]
        public async Task A_mismatch_shows_both_positions_offers_one_Sync_and_trusts_nothing() {
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _camera.Sky = (ra + 0.5, 50.0, null);

            var result = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("mismatch"), result.Detail);
            Assert.That(result.CheckId, Is.Not.Null.And.Not.Empty);
            Assert.That(result.MountDecDegrees, Is.EqualTo(30.0).Within(1e-9));
            Assert.That(result.SolvedDecDegrees, Is.EqualTo(50.0).Within(1e-9));
            Assert.That(result.SeparationDegrees, Is.GreaterThan(20.0));
            Assert.That(await mount.PositionUnverifiedReasonAsync(CancellationToken.None), Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
            Assert.That(_fake.Syncs, Is.Zero, "the Sync is never automatic");
            AssertNoMotion();
        }

        [Test]
        public async Task A_solve_that_fails_leaves_the_position_unverified_with_the_reason() {
            var (mount, check) = await UnverifiedAtAsync(RaWestOfMeridian, 30.0);
            using var _ = mount;
            _camera.Sky = null;

            var result = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("solve_failed"));
            Assert.That(result.Detail, Does.Contain("no stars"));
            Assert.That(await mount.PositionUnverifiedReasonAsync(CancellationToken.None), Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
            AssertNoMotion();
        }

        [Test]
        public async Task A_mount_that_moves_during_the_exposure_is_not_trusted() {
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _camera.Sky = (ra, 30.0, null);
            _camera.DuringExposure = () => _fake.Dec = 35.0;

            var result = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("mount_moved"));
            Assert.That(await mount.PositionUnverifiedReasonAsync(CancellationToken.None), Is.Not.Null);
        }

        [Test]
        public async Task A_Dec_past_the_pole_is_folded_before_it_is_compared() {
            // The Dec axis over the pole reads 90.7° at RA r: the same pointing as Dec 89.3° at RA r + 12 h. Near the pole the
            // hour angle tells no pier side, so a stored position angle from an earlier trusted solve tells it.
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 90.7);
            using var _ = mount;
            _angles.Store($"{Mount.UniqueId}|main:cam-1:rotator none", 40.0, PierSide.pierEast);
            _camera.Sky = (Wrap24(ra + 12.0), 89.3, 41.0);

            var result = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("trusted"), result.Detail);
            Assert.That(result.MountDecDegrees, Is.EqualTo(89.3).Within(1e-9));
            Assert.That(result.SeparationDegrees, Is.LessThan(0.01));
            Assert.That(result.PositionAngleCheck, Is.EqualTo("agrees"));
        }

        [Test]
        public void FoldPose_turns_a_Dec_past_the_pole_into_the_near_side_and_RA_by_12_hours() {
            Assert.That(PoseSolveCheckService.FoldPose(5.0, 90.7), Is.EqualTo((17.0, 89.3)).Using<(double, double)>((a, b) =>
                Math.Abs(a.Item1 - b.Item1) < 1e-9 && Math.Abs(a.Item2 - b.Item2) < 1e-9));
            Assert.That(PoseSolveCheckService.FoldPose(20.0, -91.0)!.Value.RaHours, Is.EqualTo(8.0).Within(1e-9));
            Assert.That(PoseSolveCheckService.FoldPose(5.0, 45.0), Is.EqualTo((5.0, 45.0)));
        }

        [Test]
        public async Task A_SideOfPier_the_solved_hour_angle_disagrees_with_is_not_trusted_and_offers_no_Sync() {
            // East of the meridian the side is West; the mount reports East (Normal): its model may be a flip off.
            var ra = Wrap24(LstHours + 3.0);
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _camera.Sky = (ra, 30.0, null);

            var result = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("pier_side_disagrees"), result.Detail);
            Assert.That(result.SideOfPier, Is.EqualTo("east"));
            Assert.That(result.ExpectedSideOfPier, Is.EqualTo("west"));
            Assert.That(result.CheckId, Is.Null, "no Sync offer");
            Assert.That(await mount.PositionUnverifiedReasonAsync(CancellationToken.None), Is.Not.Null);
        }

        [Test]
        public async Task A_position_angle_flipped_from_the_last_trusted_solve_is_not_trusted() {
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _angles.Store($"{Mount.UniqueId}|main:cam-1:rotator none", 10.0, PierSide.pierEast);
            _camera.Sky = (ra, 30.0, 191.0); // the same sky, the camera turned 181°: the other pier side

            var result = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("pier_side_disagrees"), result.Detail);
            Assert.That(result.PositionAngleCheck, Is.EqualTo("flipped"));
            Assert.That(result.CheckId, Is.Null);
            Assert.That(await mount.PositionUnverifiedReasonAsync(CancellationToken.None), Is.Not.Null);
        }

        [Test]
        public async Task A_German_mount_whose_pier_side_no_check_can_tell_is_not_trusted() {
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _fake.Side = PointingState.Unknown;
            _camera.Sky = (ra, 30.0, null);

            var result = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("pier_side_unknown"), result.Detail);
            Assert.That(await mount.PositionUnverifiedReasonAsync(CancellationToken.None), Is.Not.Null);
        }

        [Test]
        public async Task Sync_after_a_mismatch_then_a_second_solve_that_agrees_trusts_the_position() {
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _fake.SyncMovesReading = true;
            _camera.Sky = (Wrap24(ra + 0.3), 45.0, null);
            var offer = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);
            Assert.That(offer.Outcome, Is.EqualTo("mismatch"), "precondition");

            var result = await check.VerifyPoseBySolveAsync(offer.CheckId, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("trusted"), result.Detail);
            Assert.That(_fake.Syncs, Is.EqualTo(1), "the one Sync the user tapped");
            Assert.That(_camera.Captures, Is.EqualTo(3), "the offer's solve, the solve the Sync goes to, and the second solve");
            Assert.That(await mount.PositionUnverifiedReasonAsync(CancellationToken.None), Is.Null);
            AssertNoMotion();
        }

        [Test]
        public async Task Sync_after_a_mismatch_whose_second_solve_disagrees_stays_unverified() {
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _fake.SyncMovesReading = true;
            _camera.Sky = (Wrap24(ra + 0.3), 45.0, null);
            var offer = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);
            // The Sync lands, but the next frame shows the sky somewhere else again (the Sync offset did not hold).
            _camera.Skies.Enqueue((Wrap24(ra + 0.3), 45.0, null));
            _camera.Skies.Enqueue((Wrap24(ra + 0.3), 52.0, null));

            var result = await check.VerifyPoseBySolveAsync(offer.CheckId, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("sync_not_taken"), result.Detail);
            Assert.That(_fake.Syncs, Is.EqualTo(1));
            Assert.That(await mount.PositionUnverifiedReasonAsync(CancellationToken.None), Is.EqualTo(MountPoseTrust.AtHomeOnConnect));
        }

        [Test]
        public async Task A_Sync_the_mount_ignores_stays_unverified() {
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _fake.SyncMovesReading = false; // the mount answers the Sync and keeps reading where it was
            _camera.Sky = (Wrap24(ra + 0.3), 45.0, null);
            var offer = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            var result = await check.VerifyPoseBySolveAsync(offer.CheckId, CancellationToken.None);

            Assert.That(result.Outcome, Is.EqualTo("sync_not_taken"), result.Detail);
            Assert.That(_fake.Syncs, Is.EqualTo(1));
            Assert.That(_camera.Captures, Is.EqualTo(2), "no second solve after a reading that did not change");
            Assert.That(await mount.PositionUnverifiedReasonAsync(CancellationToken.None), Is.Not.Null);
        }

        [Test]
        public async Task A_Sync_with_no_current_offer_is_refused_and_sends_nothing() {
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _camera.Sky = (Wrap24(ra + 0.3), 45.0, null);
            var offer = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            var wrong = await RefusedAsync(() => check.VerifyPoseBySolveAsync("not-the-offer", CancellationToken.None));
            var used = await RefusedAsync(() => check.VerifyPoseBySolveAsync(offer.CheckId, CancellationToken.None));

            Assert.That(wrong.Cause, Is.EqualTo(RefusalCatalog.PoseSyncNotOffered));
            Assert.That(used.Cause, Is.EqualTo(RefusalCatalog.PoseSyncNotOffered), "a wrong id uses up the offer too");
            Assert.That(_fake.Syncs, Is.Zero);
        }

        [Test]
        public async Task The_result_names_the_guide_camera_when_the_check_used_it() {
            var ra = RaWestOfMeridian;
            var (mount, check) = await UnverifiedAtAsync(ra, 30.0);
            using var _ = mount;
            _camera.Choice = new(PoseSolveCameras.Guide, PoseSolveCameras.Guide, null);
            _camera.Sky = (ra, 30.0, null);

            var result = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);

            Assert.That(result.Camera, Is.EqualTo(PoseSolveCameras.Guide));
            Assert.That(result.Outcome, Is.EqualTo("trusted"), result.Detail);
        }

        [Test]
        public async Task A_trusted_mount_is_not_checked() {
            var (mount, check) = await UnverifiedAtAsync(RaWestOfMeridian, 30.0);
            using var _ = mount;
            _camera.Sky = (RaWestOfMeridian, 30.0, null);
            var first = await check.VerifyPoseBySolveAsync(null, CancellationToken.None);
            Assume.That(first.Outcome, Is.EqualTo("trusted"));

            var refusal = await RefusedAsync(() => check.VerifyPoseBySolveAsync(null, CancellationToken.None));
            var availability = await check.AvailabilityAsync(CancellationToken.None);

            Assert.That(refusal.Cause, Is.EqualTo(RefusalCatalog.PoseCheckNotNeeded));
            Assert.That(availability, Is.EqualTo(new PoseSolveAvailabilityDto(false, null, "not_needed")));
        }
    }

    /// <summary>#2173 — the Sync route: #2123's no-sync-during-Polar-Align refusal stands, and a body with no check_id
    /// is refused, both before the check runs.</summary>
    [TestFixture]
    public class PoseSolveSyncEndpointTest {

        [Test]
        public async Task The_Sync_is_refused_while_Polar_Align_holds_the_mount_and_without_a_check_id() {
            var check = new Mock<IPoseSolveCheckService>();
            var telescope = new Mock<ITelescopeService>();
            var moving = new Mock<IPolarAlignService>();
            moving.Setup(p => p.IsMovingMount).Returns(true);
            var idle = new Mock<IPolarAlignService>();

            var duringPa = await Assert.ThrowsAsync<RefusalException>(() => OpenAstroAra.Server.Endpoints.EquipmentEndpoints.SyncPositionBySolveAsync(
                new PoseSolveSyncRequestDto("offer-1"), check.Object, telescope.Object, moving.Object, CancellationToken.None));
            var noId = await Assert.ThrowsAsync<RefusalException>(() => OpenAstroAra.Server.Endpoints.EquipmentEndpoints.SyncPositionBySolveAsync(
                new PoseSolveSyncRequestDto(null), check.Object, telescope.Object, idle.Object, CancellationToken.None));

            Assert.That(duringPa!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMovingMount));
            Assert.That(noId!.Cause, Is.EqualTo(RefusalCatalog.PoseSyncNotOffered));
            check.Verify(c => c.VerifyPoseBySolveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    /// <summary>#2173 — the real camera chooser: the main camera whenever one is connected, the guide camera only without
    /// one, and nothing while ASTAP is missing or the camera is busy.</summary>
    [TestFixture]
    public class PoseSolveCameraTest {

        private sealed class FakeGuider : IGuideCapture {
            public bool Connected = true;
            public string? State = "Stopped";
            public CameraUse? Holder;
            public IGuideCaptureLink ConnectedLink() => Connected ? new Link() : throw new InvalidOperationException("guider is not connected");
            public string? OutOfStepReason => null;
            public void ThrowIfOutOfStep(string operation) { }
            public Task<GuideCameraCustody.Claim> TakeCameraAsync(CameraUse use, Func<CameraUse, Task> stop, CancellationToken ct) => throw new NotSupportedException();
            public string? AppState => State;
            public CameraUse? CameraHolder => Holder;
        }

        private sealed class Link : IGuideCaptureLink {
            public string? Host => "127.0.0.1";
            public int RpcPort => 4400;
            public Task WaitForCaptureIdleAsync(CancellationToken ct) => Task.CompletedTask;
            public Task<bool> StopLoopForCaptureAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
            public Task<(int Width, int Height)?> GetCameraFrameSizeAsync(CancellationToken ct) => Task.FromResult<(int, int)?>(null);
            public Task CaptureFrameAsync(int exposureMs, int? binning, CancellationToken ct) => throw new NotSupportedException();
            public IDisposable OnFrameComplete(Action<OpenAstroAra.Equipment.Equipment.MyGuider.PHD2.SingleFrameCompleteEventArgs> handler) =>
                throw new NotSupportedException();
        }

        private static PoseSolveCamera Build(EquipmentConnectionState? mainCamera, FakeGuider guider, bool solverReady = true, bool mainFree = true) {
            var profile = new Mock<OpenAstroAra.Profile.Interfaces.IProfileService>();
            var main = new MainCameraPointingSolver(profile.Object, new Mock<OpenAstroAra.PlateSolving.Interfaces.IPlateSolverFactory>().Object,
                new Mock<OpenAstroAra.Equipment.Interfaces.Mediator.IImagingMediator>().Object,
                new Mock<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>().Object,
                new Mock<OpenAstroAra.Equipment.Interfaces.Mediator.IFilterWheelMediator>().Object);
            var camera = new Mock<ICameraService>();
            camera.Setup(c => c.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(mainCamera is { } state
                ? new CameraDto("cam-1", "Main", state, null, null!)
                : null);
            camera.Setup(c => c.IsFreeToCapture(It.IsAny<object>())).Returns(mainFree);
            return new PoseSolveCamera(main, camera.Object, guider, new Mock<IPolarAlignFrameSolver>().Object,
                new Mock<IPolarAlignFrameFetcher>().Object, new Mock<IProfileStore>().Object, () => null) {
                SolverReady = () => solverReady,
            };
        }

        [Test]
        public async Task The_guide_camera_is_used_only_when_there_is_no_main_camera() {
            var guider = new FakeGuider();

            var withMain = await Build(EquipmentConnectionState.Connected, guider).ChooseAsync(CancellationToken.None);
            var withoutMain = await Build(null, guider).ChooseAsync(CancellationToken.None);
            var mainDisconnected = await Build(EquipmentConnectionState.Disconnected, guider).ChooseAsync(CancellationToken.None);

            Assert.That(withMain, Is.EqualTo(new PoseSolveCameraChoice(PoseSolveCameras.Main, "main:cam-1:rotator none", null)));
            Assert.That(withoutMain.Camera, Is.EqualTo(PoseSolveCameras.Guide));
            Assert.That(withoutMain.UnavailableReason, Is.Null);
            Assert.That(mainDisconnected.Camera, Is.EqualTo(PoseSolveCameras.Guide));
        }

        [Test]
        public async Task A_busy_main_camera_is_busy_and_never_falls_back_to_the_guide_camera() {
            var choice = await Build(EquipmentConnectionState.Connected, new FakeGuider(), mainFree: false).ChooseAsync(CancellationToken.None);

            Assert.That(choice, Is.EqualTo(new PoseSolveCameraChoice(PoseSolveCameras.Main, null, "camera_busy")));
        }

        [Test]
        public async Task No_camera_a_busy_guider_or_no_ASTAP_is_unavailable() {
            var none = await Build(null, new FakeGuider { Connected = false }).ChooseAsync(CancellationToken.None);
            var guiding = await Build(null, new FakeGuider { State = "Guiding" }).ChooseAsync(CancellationToken.None);
            var held = await Build(null, new FakeGuider { Holder = CameraUse.PolarAlign }).ChooseAsync(CancellationToken.None);
            var noSolver = await Build(EquipmentConnectionState.Connected, new FakeGuider(), solverReady: false).ChooseAsync(CancellationToken.None);

            Assert.That(none.UnavailableReason, Is.EqualTo("no_camera"));
            Assert.That(guiding.UnavailableReason, Is.EqualTo("camera_busy"));
            Assert.That(held.UnavailableReason, Is.EqualTo("camera_busy"));
            Assert.That(noSolver.UnavailableReason, Is.EqualTo("solver_not_set"));
        }
    }
}
