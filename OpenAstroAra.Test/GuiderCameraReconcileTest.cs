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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.TestHarness.Guider;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Test {

    /// <summary>#1362 — the guide camera's pixel size and UniqueID are re-read when the guider connects (§63.20).
    /// A different camera in the slot is a swap: it is re-pinned and its size read, and the identity reconciler
    /// is told; a slot that is the main imaging camera keeps the #1308 pin and is never read.</summary>
    [TestFixture]
    public class GuiderCameraReconcileTest {
        private const string Selection = "Alpaca Camera [192.0.2.10:6800/1]";

        private static Dictionary<string, AlpacaConfiguredDevice> Slot(string uniqueId) =>
            new Dictionary<string, AlpacaConfiguredDevice> { ["camera/1"] = new("Test Guide Camera", uniqueId) };

        // ---- pure decision ----

        [Test]
        public void Decide_replaces_a_stale_pixel_size_and_fills_an_empty_id() {
            var d = GuiderCameraReconcile.Decide("", 3.75, 1, Slot("TEST_GUIDE_0001"), 2.9);
            Assert.That(d.Outcome, Is.EqualTo(GuiderCameraReconcileOutcome.Update));
            Assert.That(d.NewPixelSize, Is.EqualTo(2.9));
            Assert.That(d.NewUniqueId, Is.EqualTo("TEST_GUIDE_0001"));
        }

        [Test]
        public void Decide_updates_the_size_when_the_slot_matches_the_pin() {
            var d = GuiderCameraReconcile.Decide("TEST_GUIDE_0001", 3.75, 1, Slot("TEST_GUIDE_0001"), 2.9);
            Assert.That(d.Outcome, Is.EqualTo(GuiderCameraReconcileOutcome.Update));
            Assert.That(d.NewPixelSize, Is.EqualTo(2.9));
            Assert.That(d.NewUniqueId, Is.Null);
        }

        [Test]
        public void Decide_leaves_equal_values_untouched() {
            Assert.That(GuiderCameraReconcile.Decide("TEST_GUIDE_0001", 2.9, 1, Slot("TEST_GUIDE_0001"), 2.9).Outcome,
                Is.EqualTo(GuiderCameraReconcileOutcome.Unchanged));
        }

        [Test]
        public void Decide_never_overwrites_a_different_non_empty_pin() {
            var d = GuiderCameraReconcile.Decide("TEST_GUIDE_0001", 3.75, 1, Slot("TEST_GUIDE_0002"), 2.9);
            Assert.That(d, Is.EqualTo(new GuiderCameraReconcileDecision(GuiderCameraReconcileOutcome.PinMismatch, null, null)));
            var gone = GuiderCameraReconcile.Decide("TEST_GUIDE_0001", 3.75, 2, Slot("TEST_GUIDE_0002"), 2.9);
            Assert.That(gone.Outcome, Is.EqualTo(GuiderCameraReconcileOutcome.PinMismatch), "slot not offered");
        }

        [Test]
        public void Decide_treats_an_unknown_device_map_as_unreadable_when_pinned() {
            var d = GuiderCameraReconcile.Decide("TEST_GUIDE_0001", 3.75, 1, new Dictionary<string, AlpacaConfiguredDevice>(), 2.9);
            Assert.That(d.Outcome, Is.EqualTo(GuiderCameraReconcileOutcome.Unchanged));
        }

        [Test]
        public void Decide_ignores_a_missing_or_non_positive_size_and_never_fills_a_blank_id() {
            Assert.That(GuiderCameraReconcile.Decide("", 3.75, 1, Slot("TEST_GUIDE_0001"), null).NewPixelSize, Is.Null);
            Assert.That(GuiderCameraReconcile.Decide("", 3.75, 1, Slot("TEST_GUIDE_0001"), 0).NewPixelSize, Is.Null);
            Assert.That(GuiderCameraReconcile.Decide("", 3.75, 1, Slot(""), null).Outcome,
                Is.EqualTo(GuiderCameraReconcileOutcome.Unchanged));
        }

        [Test]
        public void ForLog_drops_control_characters() {
            Assert.That(GuiderCameraReconcile.ForLog("ID\r\nforged line"), Is.EqualTo("IDforged line"));
        }

        [Test]
        public void ForLog_drops_bidi_controls() {
            Assert.That(GuiderCameraReconcile.ForLog("ID\u202Eevil\u202C \u2066x\u2069 \u200Fy\u200E\u061C"), Is.EqualTo("IDevil x y"));
        }

        // ---- through the service, against the fake guider ----

        private sealed class Rig : IAsyncDisposable {
            public FakeGuider Fake = null!;
            public InMemoryProfileStore Store = new();
            public CapturingLogger Log = new();
            public GuiderService Svc = null!;
            public StoreBackedProfileService Profiles = null!;
            public Mock<IAlpacaManagementClient> Alpaca = new();
            public RecordingIdentity Identity = new();

            public static async Task<Rig> StartAsync(string pinnedId, double pixelSize, Func<JsonNode?, JsonNode?>? pixelRpc,
                    IReadOnlyDictionary<string, AlpacaConfiguredDevice>? devices,
                    DiscoveredDeviceDto? mainCamera = null, Action<FakeGuider>? setup = null) {
                var rig = new Rig { Fake = FakeGuider.Start() };
                setup?.Invoke(rig.Fake);
                rig.Fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
                rig.Fake.OnRpc("get_alpaca_camera_pixelsize", req => pixelRpc is null
                    ? throw new InvalidOperationException("simulated daemon rejection")
                    : pixelRpc(req));
                rig.Store.PutPhd2Settings(rig.Store.GetPhd2Settings() with {
                    GuiderCamera = Selection,
                    GuiderCameraAlpacaUniqueId = pinnedId,
                    GuidePixelSize = pixelSize,
                });
                rig.Alpaca.Setup(a => a.GetConfiguredDevicesAsync("192.0.2.10", 6800, It.IsAny<CancellationToken>()))
                    .Returns(devices is null
                        ? Task.FromException<IReadOnlyDictionary<string, AlpacaConfiguredDevice>>(new InvalidOperationException("unreachable"))
                        : Task.FromResult(devices));
                rig.Profiles = new StoreBackedProfileService(rig.Store);
                rig.Svc = new GuiderService(rig.Profiles, new GuiderRecoveryCoordinator(Mock.Of<IGuiderProcessSupervisor>(),
                        Mock.Of<INotificationService>(), Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance),
                    rig.Log, Mock.Of<IGuiderProcessSupervisor>(), profileStore: rig.Store, alpaca: rig.Alpaca.Object,
                    mainCameraResolver: () => mainCamera, identity: rig.Identity);
                await rig.Svc.ConnectAsync(new GuiderConnectRequestDto("localhost", rig.Fake.Port), null, CancellationToken.None).ConfigureAwait(false);
                // The connect and the reconcile run on one background task; wait for the reconcile to start
                // (by count: a synchronous one returns the cached completed task), then for it to end.
                var until = DateTime.UtcNow.AddSeconds(15);
                while (rig.Svc.ReconcileStarts == 0) {
                    if (DateTime.UtcNow >= until) {
                        Assert.Fail("the reconcile never started");
                    }
                    await Task.Delay(10).ConfigureAwait(false);
                }
                await rig.Svc.ReconcileTask.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                return rig;
            }

            public Phd2SettingsDto Stored => Store.GetPhd2Settings();
            public EquipmentConnectionState State => Svc.GetAsync(CancellationToken.None).GetAwaiter().GetResult()!.State;

            public async ValueTask DisposeAsync() {
                Svc.Dispose();
                Profiles.Dispose();
                await Fake.DisposeAsync().ConfigureAwait(false);
            }
        }

        private sealed class CapturingLogger : ILogger<GuiderService> {
            public List<(LogLevel Level, string Text)> Lines { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                lock (Lines) { Lines.Add((logLevel, formatter(state, exception))); }
            }
            public string[] Reconcile() { lock (Lines) { return Lines.Where(l => l.Text.Contains("#1362", StringComparison.Ordinal)).Select(l => l.Text).ToArray(); } }
            public string[] ReconcileAt(LogLevel level) { lock (Lines) { return Lines.Where(l => l.Level == level && l.Text.Contains("#1362", StringComparison.Ordinal)).Select(l => l.Text).ToArray(); } }
        }

        private sealed class RecordingIdentity : IEquipmentIdentityReconciler {
            public List<(GearSlot Slot, string UniqueId)> Observed { get; } = new();
            public List<string?> Previous { get; } = new();
            public Task ReconcileConnectedAsync(CancellationToken ct) => Task.CompletedTask;
            public Task<EquipmentIdentityOutcome> ObserveAsync(GearSlot slot, AlpacaConfiguredDevice live, CancellationToken ct, DeviceIdentityDto? previous = null) {
                lock (Observed) { Observed.Add((slot, live.UniqueId)); Previous.Add(previous?.UniqueId); }
                return Task.FromResult(EquipmentIdentityOutcome.FirstSeen);
            }
        }

        private static readonly string?[] OldPinOnly = { "TEST_GUIDE_0001" };

        private static JsonObject Size(double v) => new JsonObject { ["pixel_size"] = v };

        [Test]
        public async Task Connect_replaces_a_stale_pixel_size_and_fills_an_empty_id_in_the_store() {
            await using var rig = await Rig.StartAsync("", 3.75, _ => Size(2.9), Slot("TEST_GUIDE_0001")).ConfigureAwait(false);
            Assert.That(rig.Stored.GuidePixelSize, Is.EqualTo(2.9));
            Assert.That(rig.Stored.GuiderCameraAlpacaUniqueId, Is.EqualTo("TEST_GUIDE_0001"));
            var lines = rig.Log.Reconcile();
            Assert.That(lines, Has.Length.EqualTo(1));
            Assert.That(lines[0], Does.Contain("3.75 -> 2.9").And.Contain("'' -> 'TEST_GUIDE_0001'"));
            Assert.That(rig.State, Is.EqualTo(EquipmentConnectionState.Connected));
        }

        [Test]
        public async Task Connect_leaves_equal_values_untouched_and_logs_nothing() {
            await using var rig = await Rig.StartAsync("TEST_GUIDE_0001", 2.9, _ => Size(2.9), Slot("TEST_GUIDE_0001")).ConfigureAwait(false);
            Assert.That(rig.Fake.ReceivedMethods, Does.Contain("get_alpaca_camera_pixelsize"), "the camera was read");
            Assert.That(rig.Stored.GuidePixelSize, Is.EqualTo(2.9));
            Assert.That(rig.Stored.GuiderCameraAlpacaUniqueId, Is.EqualTo("TEST_GUIDE_0001"));
            Assert.That(rig.Log.Reconcile(), Is.Empty);
        }

        [Test]
        public async Task Connect_survives_a_read_failure_and_leaves_the_profile_unchanged() {
            await using var rig = await Rig.StartAsync("", 3.75, null, null).ConfigureAwait(false);
            Assert.That(rig.Fake.ReceivedMethods, Does.Contain("get_alpaca_camera_pixelsize"), "the camera read was attempted and failed");
            rig.Alpaca.Verify(a => a.GetConfiguredDevicesAsync("192.0.2.10", 6800, It.IsAny<CancellationToken>()), Times.AtLeastOnce,
                "the reconcile reads the slots; the #1665 selection check after the connect may read them too");
            Assert.That(rig.Stored.GuidePixelSize, Is.EqualTo(3.75));
            Assert.That(rig.Stored.GuiderCameraAlpacaUniqueId, Is.EqualTo(""));
            Assert.That(rig.Log.Reconcile(), Is.Empty);
            Assert.That(rig.State, Is.EqualTo(EquipmentConnectionState.Connected), "the connect still succeeds");
        }

        [Test]
        public async Task Connect_repins_a_swapped_guide_camera_and_reads_its_size() {
            // The 2026-10-15 field case: a QHY5III585M replaced the old camera at the same slot.
            await using var rig = await Rig.StartAsync("TEST_GUIDE_0001", 3.75, _ => Size(2.9), Slot("TEST_GUIDE_0002")).ConfigureAwait(false);
            Assert.That(rig.Stored.GuidePixelSize, Is.EqualTo(2.9));
            Assert.That(rig.Stored.GuiderCameraAlpacaUniqueId, Is.EqualTo("TEST_GUIDE_0002"));
            var warnings = rig.Log.ReconcileAt(LogLevel.Warning);
            Assert.That(warnings, Has.Length.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("pinned 'TEST_GUIDE_0001'").And.Contain("swapped"));
            Assert.That(rig.Log.Reconcile(), Has.Length.EqualTo(2), "the swap warning and the re-read line");
            Assert.That(rig.Fake.ReceivedMethods, Does.Contain("get_alpaca_camera_pixelsize"), "the new camera is read");
            Assert.That(rig.Identity.Observed, Is.EqualTo(new[] { (GearSlot.GuideCamera, "TEST_GUIDE_0002") }));
            Assert.That(rig.Identity.Previous, Is.EqualTo(OldPinOnly), "the old pin is handed over as the previous identity");
            Assert.That(rig.State, Is.EqualTo(EquipmentConnectionState.Connected));
        }

        [Test]
        public async Task Connect_keeps_the_pin_when_the_mismatched_slot_is_the_main_camera() {
            // A renumber put the imaging camera where the guide camera was: the pin stands, nothing is read.
            var main = new DiscoveredDeviceDto("TEST_MAIN_0001", "Test Imaging Camera", DeviceType.Camera,
                "192.0.2.10", "192.0.2.10", 6800, 1, false);
            var devices = new Dictionary<string, AlpacaConfiguredDevice> {
                ["camera/0"] = new("Test Guide Camera", "TEST_GUIDE_0001"),
                ["camera/1"] = new("Test Imaging Camera", "TEST_MAIN_0001"),
            };
            await using var rig = await Rig.StartAsync("TEST_GUIDE_0001", 3.75, _ => Size(2.9), devices, main).ConfigureAwait(false);
            Assert.That(rig.Stored.GuidePixelSize, Is.EqualTo(3.75));
            Assert.That(rig.Stored.GuiderCameraAlpacaUniqueId, Is.EqualTo("TEST_GUIDE_0001"));
            var warnings = rig.Log.ReconcileAt(LogLevel.Warning);
            Assert.That(warnings, Has.Length.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("reports UniqueID 'TEST_MAIN_0001' but the profile pins 'TEST_GUIDE_0001'"));
            Assert.That(rig.Log.Reconcile(), Has.Length.EqualTo(1));
            Assert.That(rig.Fake.ReceivedMethods, Does.Not.Contain("get_alpaca_camera_pixelsize"), "the main camera is never read");
            Assert.That(rig.Identity.Observed, Is.Empty);
            Assert.That(rig.State, Is.EqualTo(EquipmentConnectionState.Connected));
        }

        [Test]
        public async Task Connect_keeps_the_pin_when_the_guide_camera_moved_to_another_number() {
            // A bridge restart renumbered: the pinned camera is now camera/2 and a third camera sits at camera/1.
            // That is the #1308 "has moved" case (the user picks again), not a swap: nothing is re-pinned or read.
            var devices = new Dictionary<string, AlpacaConfiguredDevice> {
                ["camera/0"] = new("Test Imaging Camera", "TEST_MAIN_0001"),
                ["camera/1"] = new("Some Other Camera", "TEST_OTHER_0003"),
                ["camera/2"] = new("Test Guide Camera", "TEST_GUIDE_0001"),
            };
            await using var rig = await Rig.StartAsync("TEST_GUIDE_0001", 3.75, _ => Size(2.9), devices).ConfigureAwait(false);
            Assert.That(rig.Stored.GuidePixelSize, Is.EqualTo(3.75));
            Assert.That(rig.Stored.GuiderCameraAlpacaUniqueId, Is.EqualTo("TEST_GUIDE_0001"));
            var warnings = rig.Log.ReconcileAt(LogLevel.Warning);
            Assert.That(warnings, Has.Length.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("moved to camera/2").And.Not.Contain("swapped"));
            Assert.That(rig.Fake.ReceivedMethods, Does.Not.Contain("get_alpaca_camera_pixelsize"), "a moved pin is never read");
            Assert.That(rig.Identity.Observed, Is.Empty);
            Assert.That(rig.State, Is.EqualTo(EquipmentConnectionState.Connected));
        }

        [Test]
        public async Task Connect_learns_nothing_from_a_blank_id_under_a_non_empty_pin() {
            await using var rig = await Rig.StartAsync("TEST_GUIDE_0001", 3.75, _ => Size(2.9), Slot("")).ConfigureAwait(false);
            Assert.That(rig.Stored.GuidePixelSize, Is.EqualTo(3.75));
            Assert.That(rig.Stored.GuiderCameraAlpacaUniqueId, Is.EqualTo("TEST_GUIDE_0001"));
            Assert.That(rig.Fake.ReceivedMethods, Does.Not.Contain("get_alpaca_camera_pixelsize"));
            Assert.That(rig.Identity.Observed, Is.Empty);
        }

        [Test]
        public async Task Connect_records_the_guide_camera_identity_on_a_quiet_connect() {
            await using var rig = await Rig.StartAsync("TEST_GUIDE_0001", 2.9, _ => Size(2.9), Slot("TEST_GUIDE_0001")).ConfigureAwait(false);
            Assert.That(rig.Identity.Observed, Is.EqualTo(new[] { (GearSlot.GuideCamera, "TEST_GUIDE_0001") }));
            Assert.That(rig.Identity.Previous, Is.EqualTo(new string?[] { null }));
        }

        [Test]
        public async Task Connect_learns_nothing_when_the_slot_is_the_main_imaging_camera() {
            var main = new DiscoveredDeviceDto("TEST_MAIN_0001", "Test Imaging Camera", DeviceType.Camera,
                "192.0.2.10", "192.0.2.10", 6800, 1, false);
            await using var rig = await Rig.StartAsync("", 3.75, _ => Size(5.9), Slot("TEST_MAIN_0001"), main).ConfigureAwait(false);
            Assert.That(rig.Stored.GuidePixelSize, Is.EqualTo(3.75));
            Assert.That(rig.Stored.GuiderCameraAlpacaUniqueId, Is.EqualTo(""));
            Assert.That(rig.Log.Reconcile(), Is.Empty);
        }

        // ALP-4982: the fill and the camera update write through one locked read-modify-write, so a PHD2
        // save from another thread between their read and their write isn't lost.
        [Test]
        public void UpdatePhd2Settings_applies_a_change_under_the_lock_keeping_the_other_fields() {
            var store = new InMemoryProfileStore();
            store.PutPhd2Settings(store.GetPhd2Settings() with { GuideFocalLength = 240 });
            var result = store.UpdatePhd2Settings(cur => cur with { GuidePixelSize = 2.9 });
            Assert.That(result.GuidePixelSize, Is.EqualTo(2.9));
            Assert.That(store.GetPhd2Settings().GuideFocalLength, Is.EqualTo(240), "untouched fields kept");
        }

        [Test]
        public void UpdatePhd2Settings_returning_null_writes_nothing() {
            var store = new InMemoryProfileStore();
            var changed = false;
            store.Changed += (_, _) => changed = true;
            store.UpdatePhd2Settings(_ => null);
            Assert.That(changed, Is.False, "no write means no change event");
        }

        [Test]
        public void FileProfileStore_UpdatePhd2Settings_persists_to_disk() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-phd2-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                store.PutPhd2Settings(store.GetPhd2Settings() with { GuideFocalLength = 240 });
                store.UpdatePhd2Settings(cur => cur with { GuidePixelSize = 2.9 });
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetPhd2Settings().GuidePixelSize, Is.EqualTo(2.9), "persisted to disk");
                Assert.That(reopened.GetPhd2Settings().GuideFocalLength, Is.EqualTo(240));
                Assert.That(store.UpdatePhd2Settings(_ => null).GuidePixelSize, Is.EqualTo(2.9));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ALP-4982 (#1486 review): the guide-optics fill ran before the pin checks, so a guider holding the
        // main imaging camera (a renumber) had that camera's pixel size kept as the guide camera's.
        private static Action<FakeGuider> GuiderOpens(string selection) => f => {
            f.OnRpc("get_pixel_scale", JsonValue.Create(1.5));
            f.OnRpc("get_connected", JsonValue.Create(true));
            f.OnRpc("get_profile", _ => new JsonObject { ["id"] = 1, ["name"] = "My Equipment" });
            f.OnRpc("get_current_equipment", _ => new JsonObject {
                ["camera"] = new JsonObject { ["name"] = selection + " - Test Camera", ["connected"] = true },
            });
        };

        private static async Task ReconcileWithScaleAsync(Rig rig) {
            // The scale arrives after the connect's own reconcile may have run: run it again once it's there.
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!(rig.Svc.GetInfo().PixelScale > 0)) {
                if (DateTime.UtcNow >= until) {
                    Assert.Fail("precondition: the guider reported its pixel scale");
                }
                await Task.Delay(10).ConfigureAwait(false);
            }
            await rig.Svc.ReconcileGuideCameraIfConnectedAsync(CancellationToken.None).ConfigureAwait(false);
        }

        [Test]
        public async Task Connect_fills_no_guide_optics_from_the_main_imaging_camera() {
            var main = new DiscoveredDeviceDto("TEST_MAIN_0001", "Test Imaging Camera", DeviceType.Camera,
                "192.0.2.10", "192.0.2.10", 6800, 1, false);
            await using var rig = await Rig.StartAsync("", 0, _ => Size(5.9), Slot("TEST_MAIN_0001"), main,
                GuiderOpens(Selection)).ConfigureAwait(false);
            await ReconcileWithScaleAsync(rig).ConfigureAwait(false);
            Assert.That(rig.Stored.GuidePixelSize, Is.EqualTo(0), "the main camera's 5.9 µm is not the guide camera's");
            Assert.That(rig.Stored.GuideFocalLength, Is.EqualTo(0));
            Assert.That(rig.Fake.ReceivedMethods, Does.Not.Contain("get_current_equipment"), "the fill never asked");
        }

        [Test]
        public async Task Connect_fills_an_unset_focal_length_once_the_slot_is_the_guide_camera() {
            await using var rig = await Rig.StartAsync("TEST_GUIDE_0001", 3.75, _ => Size(3.75), Slot("TEST_GUIDE_0001"), null,
                GuiderOpens(Selection)).ConfigureAwait(false);
            await ReconcileWithScaleAsync(rig).ConfigureAwait(false);
            Assert.That(rig.Stored.GuidePixelSize, Is.EqualTo(3.75).Within(1e-9));
            Assert.That(rig.Stored.GuideFocalLength, Is.EqualTo(516), "206.265 × 3.75 / 1.5, whole mm");
        }
    }
}
