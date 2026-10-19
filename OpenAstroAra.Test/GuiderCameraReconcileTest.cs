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
                    DiscoveredDeviceDto? mainCamera = null) {
                var rig = new Rig { Fake = FakeGuider.Start() };
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
            public Task ReconcileConnectedAsync(CancellationToken ct) => Task.CompletedTask;
            public Task<EquipmentIdentityOutcome> ObserveAsync(GearSlot slot, AlpacaConfiguredDevice live, CancellationToken ct) {
                lock (Observed) { Observed.Add((slot, live.UniqueId)); }
                return Task.FromResult(EquipmentIdentityOutcome.FirstSeen);
            }
        }

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
            rig.Alpaca.Verify(a => a.GetConfiguredDevicesAsync("192.0.2.10", 6800, It.IsAny<CancellationToken>()), Times.Once);
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
        public async Task Connect_records_the_guide_camera_identity_on_a_quiet_connect() {
            await using var rig = await Rig.StartAsync("TEST_GUIDE_0001", 2.9, _ => Size(2.9), Slot("TEST_GUIDE_0001")).ConfigureAwait(false);
            Assert.That(rig.Identity.Observed, Is.EqualTo(new[] { (GearSlot.GuideCamera, "TEST_GUIDE_0001") }));
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
    }
}
