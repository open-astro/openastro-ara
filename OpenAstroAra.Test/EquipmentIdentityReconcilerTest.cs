#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1362 — every device's Alpaca UniqueID is re-read on connect and before a sequence starts; a
    /// first sighting is recorded quietly, a changed id posts the gear-change notice and clears the calibrations
    /// measured through the old device.</summary>
    [TestFixture]
    public class EquipmentIdentityReconcilerTest {

        private static readonly DateTimeOffset Now = new(2026, 10, 18, 2, 0, 0, TimeSpan.Zero);

        private static DeviceIdentityDto Stored(string id, string name = "Old") => new(id, name, Now.AddDays(-1));

        // ---- pure decision ----

        [Test]
        public void Decide_records_a_first_sighting_and_ignores_a_blank_live_id() {
            Assert.That(EquipmentIdentityReconcile.Decide(null, new("ZWO EAF", "EAF_1")), Is.EqualTo(EquipmentIdentityOutcome.FirstSeen));
            Assert.That(EquipmentIdentityReconcile.Decide(Stored(""), new("ZWO EAF", "EAF_1")), Is.EqualTo(EquipmentIdentityOutcome.FirstSeen));
            Assert.That(EquipmentIdentityReconcile.Decide(Stored("EAF_1"), new("ZWO EAF", "")), Is.EqualTo(EquipmentIdentityOutcome.Unchanged));
            Assert.That(EquipmentIdentityReconcile.Decide(null, null), Is.EqualTo(EquipmentIdentityOutcome.Unchanged));
        }

        [Test]
        public void Decide_compares_ids_ordinally_after_trimming() {
            Assert.That(EquipmentIdentityReconcile.Decide(Stored("EAF_1"), new("ZWO EAF", " EAF_1 ")), Is.EqualTo(EquipmentIdentityOutcome.Unchanged));
            Assert.That(EquipmentIdentityReconcile.Decide(Stored("EAF_1"), new("ZWO EAF", "eaf_1")), Is.EqualTo(EquipmentIdentityOutcome.Changed));
            Assert.That(EquipmentIdentityReconcile.Decide(Stored("EAF_1"), new("Pegasus FocusCube", "PEG_FC3_77")), Is.EqualTo(EquipmentIdentityOutcome.Changed));
        }

        [Test]
        public void Slots_cover_every_alpaca_device_type_and_skip_the_guider() {
            foreach (var type in Enum.GetValues<DeviceType>()) {
                var slot = EquipmentIdentityReconcile.SlotOf(type);
                if (type == DeviceType.Guider) {
                    Assert.That(slot, Is.Null, "the guider is a daemon, not an Alpaca device");
                } else {
                    Assert.That(slot, Is.Not.Null, type.ToString());
                }
            }
            Assert.That(EquipmentIdentityReconcile.SlotOf(DeviceType.FlatDevice), Is.EqualTo(GearSlot.CoverCalibrator));
            Assert.That(EquipmentIdentityReconcile.Key(GearSlot.FilterWheel), Is.EqualTo("filterwheel"));
            Assert.That(EquipmentIdentityReconcile.Key(GearSlot.GuideCamera), Is.EqualTo("guidecamera"));
            Assert.That(EquipmentIdentityReconcile.DeviceKey(DeviceType.FlatDevice, 2), Is.EqualTo("covercalibrator/2"));
        }

        // ---- the service ----

        private sealed class Rig : IDisposable {
            public InMemoryProfileStore Store = new();
            public Mock<IAlpacaManagementClient> Alpaca = new();
            public Mock<INotificationService> Notifications = new();
            public Mock<IGuiderLiveService> GuiderLive = new();
            public EquipmentEventPublisher Publisher = new(Mock.Of<IWsBroadcaster>());
            public Dictionary<DeviceType, DiscoveredDeviceDto?> Retained = new();
            public List<NotificationDto> Posted = new();
            public int GuideCameraRuns;
            public EquipmentIdentityReconciler Svc = null!;

            public static Rig Start(bool withGuider = true) {
                var rig = new Rig();
                rig.Notifications.Setup(n => n.CreateAsync(It.IsAny<NotificationDto>(), It.IsAny<CancellationToken>()))
                    .Callback<NotificationDto, CancellationToken>((n, _) => { lock (rig.Posted) { rig.Posted.Add(n); } })
                    .Returns(Task.CompletedTask);
                rig.GuiderLive.Setup(g => g.ClearCalibrationAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
                rig.Svc = new EquipmentIdentityReconciler(rig.Store, rig.Alpaca.Object,
                    type => rig.Retained.TryGetValue(type, out var d) ? d : null,
                    rig.Notifications.Object, withGuider ? () => rig.GuiderLive.Object : null,
                    ct => { Interlocked.Increment(ref rig.GuideCameraRuns); return Task.CompletedTask; },
                    rig.Publisher, clock: () => Now);
                return rig;
            }

            public void Offer(string host, int port, Dictionary<string, AlpacaConfiguredDevice> devices) =>
                Alpaca.Setup(a => a.GetConfiguredDevicesAsync(host, port, It.IsAny<CancellationToken>())).ReturnsAsync(devices);

            public DiscoveredDeviceDto Hold(DeviceType type, int number, string host = "192.0.2.10", int port = 11111) {
                var d = new DiscoveredDeviceDto("stale-at-boot", "remembered", type, "openastro", host, port, number, false);
                Retained[type] = d;
                return d;
            }

            public async Task ConnectAsync(DeviceType type) {
                var before = Svc.Starts;
                Publisher.StateChanged(type, "x", "x", EquipmentConnectionState.Connected);
                Assert.That(Svc.Starts, Is.EqualTo(before + 1));
                await Svc.LastReconcile.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }

            public DeviceIdentityDto? Identity(GearSlot slot) =>
                Store.GetEquipmentIdentity().Devices.TryGetValue(EquipmentIdentityReconcile.Key(slot), out var d) ? d : null;

            public void Dispose() => Svc.Dispose();
        }

        private static readonly DeviceType[] FocuserOnly = { DeviceType.Focuser };
        private static readonly string[] ReconcileThenStart = { "reconcile", "start" };

        private static FocusCalibrationDto Focus() => new([], Now, null, null, CurveHalfWidthSteps: 21);
        private static TrackingCalibrationDto Tracking() => new(120, Now, null, 1.2, null, null, "measured", []);

        [Test]
        public async Task A_first_connect_records_the_identity_quietly() {
            using var rig = Rig.Start();
            rig.Hold(DeviceType.Focuser, 0);
            rig.Offer("192.0.2.10", 11111, new() { ["focuser/0"] = new("ZWO EAF", "EAF_1") });
            rig.Store.PutFocusCalibration(Focus());
            await rig.ConnectAsync(DeviceType.Focuser).ConfigureAwait(false);
            var id = rig.Identity(GearSlot.Focuser);
            Assert.That(id, Is.Not.Null);
            Assert.That(id!.UniqueId, Is.EqualTo("EAF_1"));
            Assert.That(id.Name, Is.EqualTo("ZWO EAF"));
            Assert.That(id.SeenUtc, Is.EqualTo(Now));
            Assert.That(rig.Posted, Is.Empty, "a first sighting is not a change");
            Assert.That(rig.Store.GetFocusCalibration(), Is.Not.Null, "nothing is invalidated on a first sighting");
        }

        [Test]
        public async Task The_same_device_again_changes_nothing() {
            using var rig = Rig.Start();
            rig.Hold(DeviceType.Focuser, 0);
            rig.Offer("192.0.2.10", 11111, new() { ["focuser/0"] = new("ZWO EAF", "EAF_1") });
            await rig.ConnectAsync(DeviceType.Focuser).ConfigureAwait(false);
            var first = rig.Identity(GearSlot.Focuser);
            var writes = 0;
            rig.Store.Changed += (_, _) => writes++;
            await rig.ConnectAsync(DeviceType.Focuser).ConfigureAwait(false);
            Assert.That(rig.Identity(GearSlot.Focuser), Is.SameAs(first));
            Assert.That(writes, Is.Zero, "no profile write on an unchanged identity");
            Assert.That(rig.Posted, Is.Empty);
        }

        [Test]
        public async Task A_swapped_focuser_clears_the_focus_calibration_and_tells_the_user() {
            using var rig = Rig.Start();
            rig.Hold(DeviceType.Focuser, 0);
            rig.Offer("192.0.2.10", 11111, new() { ["focuser/0"] = new("ZWO EAF", "EAF_1") });
            await rig.ConnectAsync(DeviceType.Focuser).ConfigureAwait(false);
            rig.Store.PutFocusCalibration(Focus());
            rig.Store.PutTrackingCalibration(Tracking());

            rig.Offer("192.0.2.10", 11111, new() { ["focuser/0"] = new("Pegasus FocusCube3", "PEG_FC3_77") });
            await rig.ConnectAsync(DeviceType.Focuser).ConfigureAwait(false);

            Assert.That(rig.Identity(GearSlot.Focuser)!.UniqueId, Is.EqualTo("PEG_FC3_77"));
            Assert.That(rig.Store.GetFocusCalibration(), Is.Null, "measured through the old focuser");
            Assert.That(rig.Store.GetTrackingCalibration(), Is.Not.Null, "the mount did not change");
            Assert.That(rig.Posted, Has.Count.EqualTo(1));
            var n = rig.Posted[0];
            Assert.That(n.Title, Is.EqualTo("Focuser changed: ZWO EAF → Pegasus FocusCube3"));
            Assert.That(n.Severity, Is.EqualTo(NotificationSeverity.Warning));
            Assert.That(n.Category, Is.EqualTo(NotificationCategory.Equipment));
            Assert.That(n.Message, Does.Contain("Smart Focus calibration"));
            Assert.That(n.RelatedEntityId, Is.EqualTo("focuser"));
            rig.GuiderLive.Verify(g => g.ClearCalibrationAsync(It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task A_swapped_camera_clears_focus_tracking_and_sky_model() {
            using var rig = Rig.Start();
            rig.Hold(DeviceType.Camera, 0);
            rig.Offer("192.0.2.10", 11111, new() { ["camera/0"] = new("ZWO ASI533MC Pro", "ZWO_SN_1") });
            await rig.ConnectAsync(DeviceType.Camera).ConfigureAwait(false);
            rig.Store.PutFocusCalibration(Focus());
            rig.Store.PutTrackingCalibration(Tracking());
            rig.Store.PutSkyModelCalibration(new SkyModelCalibrationDto(Now, 1, []));

            rig.Offer("192.0.2.10", 11111, new() { ["camera/0"] = new("ZWO ASI2600MM Pro", "ZWO_SN_2") });
            await rig.ConnectAsync(DeviceType.Camera).ConfigureAwait(false);

            Assert.That(rig.Store.GetFocusCalibration(), Is.Null);
            Assert.That(rig.Store.GetTrackingCalibration(), Is.Null);
            Assert.That(rig.Store.GetSkyModelCalibration(), Is.Null);
            Assert.That(rig.Posted.Single().Title, Is.EqualTo("Camera changed: ZWO ASI533MC Pro → ZWO ASI2600MM Pro"));
            Assert.That(rig.Posted.Single().Message, Does.Contain("Settings → Optics"));
        }

        [Test]
        public async Task A_swapped_mount_clears_tracking_and_the_guider_calibration() {
            using var rig = Rig.Start();
            rig.Hold(DeviceType.Telescope, 0);
            rig.Offer("192.0.2.10", 11111, new() { ["telescope/0"] = new("EQMOD", "EQMOD_1") });
            await rig.ConnectAsync(DeviceType.Telescope).ConfigureAwait(false);
            rig.Store.PutTrackingCalibration(Tracking());
            rig.Store.PutFocusCalibration(Focus());

            rig.Offer("192.0.2.10", 11111, new() { ["telescope/0"] = new("OnStep", "ONSTEP_9") });
            await rig.ConnectAsync(DeviceType.Telescope).ConfigureAwait(false);

            Assert.That(rig.Store.GetTrackingCalibration(), Is.Null);
            Assert.That(rig.Store.GetFocusCalibration(), Is.Not.Null);
            rig.GuiderLive.Verify(g => g.ClearCalibrationAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(rig.Posted.Single().Title, Is.EqualTo("Mount changed: EQMOD → OnStep"));
        }

        [Test]
        public async Task A_swapped_guide_camera_invalidates_darks_and_the_guider_calibration() {
            using var rig = Rig.Start();
            rig.Store.PutCalibrationState(new CalibrationStateDto(
                new GuiderCalibrationEntryDto(true, Now.AddDays(-3)), new GuiderCalibrationEntryDto(true, Now.AddDays(-2))));
            var first = await rig.Svc.ObserveAsync(GearSlot.GuideCamera, new("ZWO ASI120MM Mini", "ZWO_MINI_1"), CancellationToken.None).ConfigureAwait(false);
            Assert.That(first, Is.EqualTo(EquipmentIdentityOutcome.FirstSeen));
            Assert.That(rig.Store.GetCalibrationState().DarkLibrary.Valid, Is.True);

            var swap = await rig.Svc.ObserveAsync(GearSlot.GuideCamera, new("QHY5III585M", "QHY_585_42"), CancellationToken.None).ConfigureAwait(false);
            Assert.That(swap, Is.EqualTo(EquipmentIdentityOutcome.Changed));
            var state = rig.Store.GetCalibrationState();
            Assert.That(state.DarkLibrary.Valid, Is.False);
            Assert.That(state.DarkLibrary.LastBuiltAt, Is.EqualTo(Now.AddDays(-3)), "the build time is history, kept");
            Assert.That(state.DefectMap.Valid, Is.False);
            rig.GuiderLive.Verify(g => g.ClearCalibrationAsync(It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(rig.Posted.Single().Title, Is.EqualTo("Guide camera changed: ZWO ASI120MM Mini → QHY5III585M"));
            Assert.That(rig.Posted.Single().Message, Does.Contain("guide focal length").And.Contain("dark library"));
        }

        [Test]
        public async Task A_guider_that_refuses_to_clear_does_not_block_the_notice() {
            using var rig = Rig.Start();
            rig.GuiderLive.Setup(g => g.ClearCalibrationAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("guider is not connected"));
            await rig.Svc.ObserveAsync(GearSlot.Telescope, new("EQMOD", "EQMOD_1"), CancellationToken.None).ConfigureAwait(false);
            await rig.Svc.ObserveAsync(GearSlot.Telescope, new("OnStep", "ONSTEP_9"), CancellationToken.None).ConfigureAwait(false);
            Assert.That(rig.Posted, Has.Count.EqualTo(1));
            Assert.That(rig.Identity(GearSlot.Telescope)!.UniqueId, Is.EqualTo("ONSTEP_9"));
        }

        [Test]
        public async Task An_unreachable_management_api_leaves_the_profile_alone() {
            using var rig = Rig.Start();
            rig.Hold(DeviceType.Rotator, 0);
            rig.Alpaca.Setup(a => a.GetConfiguredDevicesAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("refused"));
            await rig.ConnectAsync(DeviceType.Rotator).ConfigureAwait(false);
            Assert.That(rig.Identity(GearSlot.Rotator), Is.Null);
            Assert.That(rig.Posted, Is.Empty);

            // ...and so does a server that lists no such device, or one that reports a blank id.
            rig.Offer("192.0.2.10", 11111, new() { ["rotator/3"] = new("Elsewhere", "ROT_3") });
            await rig.ConnectAsync(DeviceType.Rotator).ConfigureAwait(false);
            Assert.That(rig.Identity(GearSlot.Rotator), Is.Null);
            rig.Offer("192.0.2.10", 11111, new() { ["rotator/0"] = new("Nameless bridge", "") });
            await rig.ConnectAsync(DeviceType.Rotator).ConfigureAwait(false);
            Assert.That(rig.Identity(GearSlot.Rotator), Is.Null);
        }

        [Test]
        public async Task The_reconcile_uses_the_retained_devices_address_not_its_boot_time_id() {
            // Auto-connect replays the remembered DTO, whose UniqueId can be stale: the live management API decides.
            using var rig = Rig.Start();
            rig.Hold(DeviceType.FilterWheel, 1, host: "10.0.0.5", port: 6800);
            rig.Offer("10.0.0.5", 6800, new() { ["filterwheel/1"] = new("ZWO EFW", "EFW_7") });
            await rig.ConnectAsync(DeviceType.FilterWheel).ConfigureAwait(false);
            Assert.That(rig.Identity(GearSlot.FilterWheel)!.UniqueId, Is.EqualTo("EFW_7"));
            rig.Alpaca.Verify(a => a.GetConfiguredDevicesAsync("10.0.0.5", 6800, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Disconnect_and_error_drop_the_device_from_the_connected_set() {
            using var rig = Rig.Start();
            rig.Hold(DeviceType.Focuser, 0);
            rig.Offer("192.0.2.10", 11111, new() { ["focuser/0"] = new("ZWO EAF", "EAF_1") });
            await rig.ConnectAsync(DeviceType.Focuser).ConfigureAwait(false);
            Assert.That(rig.Svc.Connected, Is.EquivalentTo(FocuserOnly));
            rig.Publisher.StateChanged(DeviceType.Focuser, "x", "x", EquipmentConnectionState.Disconnected);
            Assert.That(rig.Svc.Connected, Is.Empty);
            rig.Publisher.StateChanged(DeviceType.Guider, "x", "x", EquipmentConnectionState.Connected);
            Assert.That(rig.Svc.Connected, Is.Empty, "the guider is not an Alpaca slot");
            Assert.That(rig.Svc.Starts, Is.EqualTo(1));
        }

        [Test]
        public async Task A_sequence_start_re_reads_every_connected_device_and_the_guide_camera() {
            using var rig = Rig.Start();
            rig.Hold(DeviceType.Focuser, 0);
            rig.Hold(DeviceType.Telescope, 0);
            rig.Offer("192.0.2.10", 11111, new() {
                ["focuser/0"] = new("ZWO EAF", "EAF_1"),
                ["telescope/0"] = new("EQMOD", "EQMOD_1"),
            });
            await rig.ConnectAsync(DeviceType.Focuser).ConfigureAwait(false);
            await rig.ConnectAsync(DeviceType.Telescope).ConfigureAwait(false);
            Assert.That(rig.GuideCameraRuns, Is.Zero);

            // A swap since the connects, found before the first frame.
            rig.Offer("192.0.2.10", 11111, new() {
                ["focuser/0"] = new("Pegasus FocusCube3", "PEG_FC3_77"),
                ["telescope/0"] = new("EQMOD", "EQMOD_1"),
            });
            rig.Store.PutFocusCalibration(Focus());
            await rig.Svc.ReconcileConnectedAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.That(rig.GuideCameraRuns, Is.EqualTo(1));
            Assert.That(rig.Identity(GearSlot.Focuser)!.UniqueId, Is.EqualTo("PEG_FC3_77"));
            Assert.That(rig.Identity(GearSlot.Telescope)!.UniqueId, Is.EqualTo("EQMOD_1"));
            Assert.That(rig.Store.GetFocusCalibration(), Is.Null);
            Assert.That(rig.Posted.Single().Title, Does.StartWith("Focuser changed"));
        }

        [Test]
        public async Task A_sequence_start_survives_a_guider_reconcile_that_throws() {
            using var rig = Rig.Start();
            using var svc = new EquipmentIdentityReconciler(rig.Store, rig.Alpaca.Object, _ => null,
                guideCameraReconcile: _ => throw new InvalidOperationException("boom"));
            await svc.ReconcileConnectedAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.Pass();
        }

        [Test]
        public async Task Start_sequence_awaits_the_reconcile_before_starting() {
            var order = new List<string>();
            var identity = new Mock<IEquipmentIdentityReconciler>();
            identity.Setup(i => i.ReconcileConnectedAsync(It.IsAny<CancellationToken>()))
                .Returns(() => { order.Add("reconcile"); return Task.CompletedTask; });
            var id = Guid.NewGuid();
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "sequence.start", Now, null);
            var sequencer = new Mock<ISequencerService>();
            sequencer.Setup(s => s.StartAsync(id, It.IsAny<SequenceStartRequestDto>(), null, It.IsAny<CancellationToken>()))
                .Returns(() => { order.Add("start"); return Task.FromResult(accepted); });
            var bahtinov = new Mock<IBahtinovFocusService>();
            bahtinov.SetupGet(b => b.IsActive).Returns(false);
            var result = await OpenAstroAra.Server.Endpoints.SequenceEndpoints.StartSequenceAsync(id,
                new SequenceStartRequestDto(DryRun: false, StartFromInstructionIndex: null, ContinueOnRecoverableErrors: false), null,
                sequencer.Object, bahtinov.Object, new Mock<ICameraService>().Object, CancellationToken.None, identity.Object).ConfigureAwait(false);
            Assert.That((result as Microsoft.AspNetCore.Http.HttpResults.Accepted<OperationAcceptedDto>)?.Value, Is.SameAs(accepted));
            Assert.That(order, Is.EqualTo(ReconcileThenStart));
        }
    }

    /// <summary>#1362 — the equipment-identity profile section: round-trip through profile.json, the normalizer's
    /// back-fill for a pre-#1362 file, the profile-select Capture/Apply path and the share-export strip.</summary>
    [TestFixture]
    [Category("IO")]
    public class EquipmentIdentityStoreTest {

        private static readonly DateTimeOffset Seen = new(2026, 10, 15, 22, 0, 0, TimeSpan.Zero);
        private static readonly string[] CameraAndGuideCamera = { "camera", "guidecamera" };
        private static readonly string[] FocuserOnlyKey = { "focuser" };

        private static EquipmentIdentityDto Two() => new(new Dictionary<string, DeviceIdentityDto> {
            ["camera"] = new("ZWO_SN_1", "ZWO ASI533MC Pro", Seen),
            ["guidecamera"] = new("QHY_585_42", "QHY5III585M", Seen),
        });

        private static string TempDir() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-equip-identity-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Test]
        public void A_fresh_store_has_seen_nothing() {
            Assert.That(new InMemoryProfileStore().GetEquipmentIdentity().Devices, Is.Empty);
        }

        [Test]
        public void Identities_round_trip_through_profile_json_in_snake_case() {
            var dir = TempDir();
            try {
                new FileProfileStore(dir).PutEquipmentIdentity(Two());
                var json = File.ReadAllText(Path.Combine(dir, "profile.json"));
                Assert.That(json, Does.Contain("\"equipment_identity\"").And.Contain("\"unique_id\": \"QHY_585_42\"").And.Contain("\"seen_utc\""));
                var back = new FileProfileStore(dir).GetEquipmentIdentity();
                Assert.That(back.Devices.Keys, Is.EquivalentTo(CameraAndGuideCamera));
                Assert.That(back.Devices["guidecamera"], Is.EqualTo(Two().Devices["guidecamera"]));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void A_pre_1362_file_reads_as_nothing_seen() {
            var dir = TempDir();
            try {
                File.WriteAllText(Path.Combine(dir, "profile.json"), "{}");
                Assert.That(new FileProfileStore(dir).GetEquipmentIdentity().Devices, Is.Empty);
                // A hand-edited file with a null map, a null entry and a blank-field entry: the normalizer back-fills.
                File.WriteAllText(Path.Combine(dir, "profile.json"),
                    "{\"equipment_identity\": {\"devices\": {\"camera\": null, \"focuser\": {\"unique_id\": \"EAF_1\"}}}}");
                var identity = new FileProfileStore(dir).GetEquipmentIdentity();
                Assert.That(identity.Devices.Keys, Is.EquivalentTo(FocuserOnlyKey));
                Assert.That(identity.Devices["focuser"].Name, Is.EqualTo(""));
                File.WriteAllText(Path.Combine(dir, "profile.json"), "{\"equipment_identity\": {\"devices\": null}}");
                Assert.That(new FileProfileStore(dir).GetEquipmentIdentity().Devices, Is.Empty);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Profile_select_carries_the_identities_and_a_blank_profile_clears_them() {
            var a = new InMemoryProfileStore();
            a.PutEquipmentIdentity(Two());
            var snap = ProfileStoreSnapshot.Capture(a);
            Assert.That(snap.EquipmentIdentity!.Devices, Has.Count.EqualTo(2));
            var b = new InMemoryProfileStore();
            ProfileStoreSnapshot.Apply(b, snap);
            Assert.That(b.GetEquipmentIdentity().Devices.Keys, Is.EquivalentTo(CameraAndGuideCamera));
            ProfileStoreSnapshot.Apply(b, snap with { EquipmentIdentity = null });
            Assert.That(b.GetEquipmentIdentity().Devices, Is.Empty, "a never-connected profile must not inherit the previous rig's serials");
        }
    }
}
