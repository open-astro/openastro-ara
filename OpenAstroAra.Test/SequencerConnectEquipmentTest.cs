#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Sequencer.Connections;
using OpenAstroAra.Sequencer.SequenceItem.Connect;
using OpenAstroAra.Sequencer.Trigger.Connect;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using OpenAstroAra.TestHarness.Guider;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Bug class 1 (#1545): the Sequencer's Connect instructions found a device by matching the mediator's
    /// Rescan against <c>ActiveProfile.&lt;Role&gt;Settings.Id</c>, and connected and disconnected it through
    /// the mediator. In production nothing writes that id (the store-backed profile leaves it at
    /// <c>No_Device</c>) and eight services stub Rescan, Connect and Disconnect, so Connect Equipment always
    /// said "not found", Connect All Equipment silently connected nothing, and Disconnect Equipment failed.
    /// The device a role connects is the one the rig remembers (the selection store, or the device its
    /// service retains, #1127): the same answer the Server's reconnector gives auto-connect and the REST
    /// reconnect (§52.1). These tests build the instructions over the production profile source and the
    /// real services over fake devices (ADR 0011 rule 4), and never write a profile device id. Only the
    /// <c>*Item</c> construction helpers change when the instructions move off the mediators. The guider is
    /// not Alpaca and not in the selection store: Connect All dials it when the profile names a guider type
    /// (the production default is PHD2), and recovery has no user-disconnect gate for it (a stated gap).
    /// </summary>
    [TestFixture]
    public class SequencerConnectEquipmentTest {

        private readonly List<IDisposable> _owned = new();

        // Released before the services are disposed, so a connect held open by a test never outlives it.
        private readonly ManualResetEventSlim _releaseConnect = new(false);

        private T Track<T>(T disposable) where T : IDisposable {
            _owned.Add(disposable);
            return disposable;
        }

        // One fixture instance serves every test: a release set by an earlier test's teardown must not leak into the next.
        [SetUp]
        public void HoldConnects() => _releaseConnect.Reset();

        [TearDown]
        public void DisposeOwned() {
            _releaseConnect.Set();
            foreach (var d in _owned) {
                d.Dispose();
            }
            _owned.Clear();
        }

        [OneTimeTearDown]
        public void DisposeGate() => _releaseConnect.Dispose();

        private static DiscoveredDeviceDto Dto(DeviceType type, string id) =>
            new(id, "Fake " + type, type, "192.0.2.10", "192.0.2.10", 11111, 0, false);

        // The selection store with a fixed remembered set: the production identity source of a role's device.
        private sealed class RememberedDevices : IEquipmentSelectionStore {
            private readonly List<DiscoveredDeviceDto> _devices;
            public RememberedDevices(params DiscoveredDeviceDto[] devices) => _devices = new(devices);
            public Task RememberAsync(DiscoveredDeviceDto device, CancellationToken ct) => Task.CompletedTask;
            public Task<IReadOnlyList<DiscoveredDeviceDto>> GetAllAsync(CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<DiscoveredDeviceDto>>(_devices);
            public Task<int> ForgetAsync(DeviceType type, CancellationToken ct) => Task.FromResult(0);
            public Task<int> ForgetSwitchAsync(string uniqueId, CancellationToken ct) => Task.FromResult(0);
            public Task RelocateAsync(DiscoveredDeviceDto from, DiscoveredDeviceDto moved, CancellationToken ct) => Task.CompletedTask;
        }

        /// <summary>The daemon as the instructions see it: the store-backed profile (nothing hand-set), the
        /// selection store, the device services and the reconnector over them.</summary>
        private sealed class Rig {
            public required StoreBackedProfileService Profile { get; init; }
            public required SafetyMonitorService SafetyMonitor { get; init; }
            public required RotatorService Rotator { get; init; }
            public GuiderService? Guider { get; init; }
            public required EquipmentReconnector Reconnector { get; init; }

            public IGuiderMediator GuiderOrNone => (IGuiderMediator?)Guider ?? new HeadlessGuiderMediator();
        }

        // A rig with no guider: Connect All dials the guider the profile names (PHD2 by default), so these
        // lifecycle-device tests name none. Hand-set on purpose: it excludes a role, it is no identity source.
        private Rig BuildRig(SafetyMonitorService safetyMonitor, RotatorService rotator, params DiscoveredDeviceDto[] remembered) {
            var profile = new StoreBackedProfileService(new InMemoryProfileStore());
            profile.ActiveProfile.GuiderSettings.GuiderName = "No_Guider";
            return BuildRig(safetyMonitor, rotator, profile, null, remembered);
        }

        private Rig BuildRig(SafetyMonitorService safetyMonitor, RotatorService rotator, StoreBackedProfileService profile,
                GuiderService? guider, params DiscoveredDeviceDto[] remembered) {
            var services = new Mock<IServiceProvider>();
            services.Setup(s => s.GetService(typeof(ISafetyMonitorService))).Returns(safetyMonitor);
            services.Setup(s => s.GetService(typeof(IRotatorService))).Returns(rotator);
            // Disconnect All reaches the Switch role, which resolves its service before it asks what is known.
            services.Setup(s => s.GetService(typeof(ISwitchService))).Returns(Mock.Of<ISwitchService>(
                m => m.GetAllAsync(It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<SwitchDto>>(Array.Empty<SwitchDto>())));
            // Connect All asks every role whether it is connected, so each role resolves its service; these hold no device.
            services.Setup(s => s.GetService(typeof(ICameraService))).Returns(Mock.Of<ICameraService>());
            services.Setup(s => s.GetService(typeof(ITelescopeService))).Returns(Mock.Of<ITelescopeService>());
            services.Setup(s => s.GetService(typeof(IFocuserService))).Returns(Mock.Of<IFocuserService>());
            services.Setup(s => s.GetService(typeof(IFilterWheelService))).Returns(Mock.Of<IFilterWheelService>());
            services.Setup(s => s.GetService(typeof(IDomeService))).Returns(Mock.Of<IDomeService>());
            services.Setup(s => s.GetService(typeof(IObservingConditionsService))).Returns(Mock.Of<IObservingConditionsService>());
            services.Setup(s => s.GetService(typeof(IFlatDeviceService))).Returns(Mock.Of<IFlatDeviceService>());
            if (guider is not null) {
                services.Setup(s => s.GetService(typeof(IGuiderService))).Returns(guider);
            }
            return new Rig {
                Profile = Track(profile),
                SafetyMonitor = safetyMonitor,
                Rotator = rotator,
                Guider = guider,
                Reconnector = new EquipmentReconnector(services.Object, new RememberedDevices(remembered),
                    NullLogger<EquipmentReconnector>.Instance),
            };
        }

        // The production profile over a store whose PHD2 target is the fake guider (the §63 settings the
        // daemon hydrates; the guider type stays the profile default, PHD2).
        private static StoreBackedProfileService ProfileWithPhd2At(FakeGuider fake) {
            var store = new InMemoryProfileStore();
            store.UpdatePhd2Settings(p => p with { Host = "127.0.0.1", Port = fake.Port });
            return new StoreBackedProfileService(store);
        }

        // The real guider service over the profile. Not a systemd host: no recovery pass reconnects it.
        private GuiderService GuiderOver(StoreBackedProfileService profile) {
            var supervisor = new Mock<IGuiderProcessSupervisor>();
            supervisor.Setup(s => s.QueryStatusAsync(It.IsAny<CancellationToken>())).ReturnsAsync(GuiderProcessStatus.Unknown);
            var recovery = new GuiderRecoveryCoordinator(supervisor.Object, Mock.Of<INotificationService>(),
                Mock.Of<IDiagnosticsService>(), NullLogger<GuiderRecoveryCoordinator>.Instance,
                new[] { TimeSpan.Zero }, static (_, _) => Task.CompletedTask, TimeSpan.FromSeconds(1));
            return Track(new GuiderService(profile, recovery, NullLogger<GuiderService>.Instance, supervisor.Object,
                ws: null, profileStore: null, sequencerResolver: () => Mock.Of<ISequencerService>(),
                notifications: Mock.Of<INotificationService>()) {
                ReconnectAttemptInterval = TimeSpan.FromMilliseconds(200),
                ReconnectGraceFromSeconds = _ => TimeSpan.FromSeconds(10),
            });
        }

        private static FakeGuider StartFakeGuider() {
            var fake = FakeGuider.Start();
            fake.SetOnConnectEvents(PhdEvents.Version(subver: "openastroara-fake"), PhdEvents.AppState("Stopped"));
            return fake;
        }

        private SafetyMonitorService SafetyMonitorOver(FakeSafetyMonitorDevice fake) =>
            Track(new SafetyMonitorService { DeviceFactory = _ => fake });

        private RotatorService RotatorOver(FakeRotatorDevice fake) =>
            Track(new RotatorService { DeviceFactory = _ => fake });

        // The construction points of the instructions: Connect Equipment and the Reconnect trigger take the
        // connections port (the reconnector); Disconnect Equipment and Connect All take it too (Connect All also reads the profile);
        // Switch Profile takes only the profile.
        private static ConnectEquipment ConnectItem(string device, Rig rig) =>
            new(rig.Reconnector) { SelectedDevice = device };

        private static ConnectAllEquipment ConnectAllItem(Rig rig) =>
            new(rig.Reconnector, rig.Profile);

        private static DisconnectEquipment DisconnectItem(string device, Rig rig) =>
            new(rig.Reconnector) { SelectedDevice = device };

        private static ReconnectTrigger ReconnectTriggerFor(string device, Rig rig) =>
            new(rig.Reconnector) { SelectedDevice = device };

        private static SwitchProfile SwitchProfileItem(Rig rig) =>
            new(rig.Profile) { SelectedProfileId = Guid.Parse("cccccccc-0000-0000-0000-000000000000"), Reconnect = true };

        private static async Task WaitForAsync(Func<bool> condition, string what) {
            for (var i = 0; i < 100 && !condition(); i++) {
                await Task.Delay(50).ConfigureAwait(false);
            }
            Assert.That(condition(), Is.True, what);
        }

        private static async Task ConnectedAsync(RotatorService svc, string id) {
            await svc.ConnectAsync(new ConnectRequestDto(Dto(DeviceType.Rotator, id)), null, CancellationToken.None).ConfigureAwait(false);
            await WaitForAsync(() => svc.GetInfo().Connected, "the rotator never connected").ConfigureAwait(false);
        }

        private static async Task ConnectedAsync(SafetyMonitorService svc, string id) {
            await svc.ConnectAsync(new ConnectRequestDto(Dto(DeviceType.SafetyMonitor, id)), null, CancellationToken.None).ConfigureAwait(false);
            await WaitForAsync(() => svc.GetInfo().Connected, "the safety monitor never connected").ConfigureAwait(false);
        }

        // ── Connect Equipment finds the device the rig remembers (B1: not a profile id nobody writes) ──

        [Test]
        public async Task ConnectEquipment_connects_the_safety_monitor_the_rig_remembers() {
            var fake = Track(new FakeSafetyMonitorDevice());
            var rig = BuildRig(SafetyMonitorOver(fake), RotatorOver(Track(new FakeRotatorDevice())),
                Dto(DeviceType.SafetyMonitor, "rig-safety"));
            var item = ConnectItem("Safety Monitor", rig);

            Assert.That(item.Validate(), Is.True, string.Join("; ", item.Issues));
            await item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(rig.SafetyMonitor.GetInfo().Connected, Is.True, "the instruction reported success but the monitor is not connected");
            Assert.That(fake.Connects, Is.EqualTo(1), "the connect must reach the device");
        }

        [Test]
        public async Task ConnectEquipment_connects_the_rotator_the_rig_remembers() {
            var fake = Track(new FakeRotatorDevice());
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), RotatorOver(fake),
                Dto(DeviceType.Rotator, "rig-rotator"));
            var item = ConnectItem("Rotator", rig);

            await item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(rig.Rotator.GetInfo().Connected, Is.True, "the instruction reported success but the rotator is not connected");
            Assert.That(fake.Connects, Is.EqualTo(1), "the connect must reach the device");
        }

        // Guard (passes on master): the connect path never invents a device to connect.
        [Test]
        public async Task ConnectEquipment_with_nothing_remembered_or_retained_fails_and_connects_nothing() {
            var fake = Track(new FakeRotatorDevice());
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), RotatorOver(fake));
            var item = ConnectItem("Rotator", rig);

            await Assert.CatchAsync<SequenceEntityFailedException>(() => item.Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
            Assert.That(rig.Rotator.GetInfo().Connected, Is.False);
            Assert.That(fake.Connects, Is.EqualTo(0), "nothing is remembered or retained, so nothing may be dialled");
        }

        // B3d: a Stop/Abort during a connect that has not settled is a cancellation, never a failure.
        [Test]
        public async Task ConnectEquipment_cancelled_while_the_device_connects_throws_cancellation() {
            var fake = Track(new FakeRotatorDevice());
            var rotator = Track(new RotatorService {
                DeviceFactory = _ => {
                    _releaseConnect.Wait(TimeSpan.FromSeconds(10));
                    return fake;
                },
            });
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), rotator, Dto(DeviceType.Rotator, "rig-rotator"));
            var item = ConnectItem("Rotator", rig);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.CatchAsync<OperationCanceledException>(() => item.Execute(new Progress<ApplicationStatus>(), stop.Token),
                "a stopped connect throws OperationCanceledException, not SequenceEntityFailedException");
        }

        // ── Connect All Equipment (B1 again, and B3b: the Safety Monitor goes first) ──

        [Test]
        public async Task ConnectAllEquipment_connects_every_device_the_rig_remembers() {
            var monitor = Track(new FakeSafetyMonitorDevice());
            var rotator = Track(new FakeRotatorDevice());
            var rig = BuildRig(SafetyMonitorOver(monitor), RotatorOver(rotator),
                Dto(DeviceType.SafetyMonitor, "rig-safety"), Dto(DeviceType.Rotator, "rig-rotator"));

            await ConnectAllItem(rig).Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(rig.SafetyMonitor.GetInfo().Connected, Is.True, "Connect All reported success but the monitor is not connected");
            Assert.That(rig.Rotator.GetInfo().Connected, Is.True, "Connect All reported success but the rotator is not connected");
        }

        [Test]
        public async Task ConnectAllEquipment_stops_before_any_other_device_when_the_safety_monitor_fails_to_connect() {
            var rotator = Track(new FakeRotatorDevice());
            var safetyMonitor = Track(new SafetyMonitorService {
                DeviceFactory = _ => throw new InvalidOperationException("the monitor's Alpaca server is down"),
            });
            var rig = BuildRig(safetyMonitor, RotatorOver(rotator),
                Dto(DeviceType.SafetyMonitor, "rig-safety"), Dto(DeviceType.Rotator, "rig-rotator"));

            var failure = await Assert.CatchAsync<Exception>(() => ConnectAllItem(rig).Execute(new Progress<ApplicationStatus>(), CancellationToken.None),
                "a rig whose safety is unknown must fail Connect All");
            Assert.That(failure!.ToString(), Does.Contain("Safety Monitor"), "the failure names the device that stopped the connect");
            await Task.Delay(200);
            Assert.That(rotator.Connects, Is.EqualTo(0), "no device connects before the Safety Monitor has");
        }

        // ADR 0023 rule 5: a refusal reaches the Sequencer as a refusal, with its cause, never re-wrapped as a plain failure.
        [Test]
        public async Task ConnectAllEquipment_keeps_a_refusal_as_a_refusal() {
            var port = new Mock<IEquipmentConnections>();
            port.Setup(p => p.ConnectAsync(EquipmentRole.SafetyMonitor, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new EquipmentRefusedException(EquipmentRefusalCause.NotSupported, "the monitor cannot connect here"));

            var refusal = await Assert.ThrowsAsync<EquipmentRefusedException>(() =>
                new ConnectAllEquipment(port.Object, Mock.Of<IProfileService>()).Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
            Assert.That(refusal!.Cause, Is.EqualTo(EquipmentRefusalCause.NotSupported));
        }

        // ── Disconnect Equipment (B3c: a Sequencer disconnect is a user disconnect, #1539) ──

        [Test]
        public async Task DisconnectEquipment_disconnects_a_connected_safety_monitor() {
            var fake = Track(new FakeSafetyMonitorDevice());
            var rig = BuildRig(SafetyMonitorOver(fake), RotatorOver(Track(new FakeRotatorDevice())));
            await ConnectedAsync(rig.SafetyMonitor, "rig-safety");

            await DisconnectItem("Safety Monitor", rig).Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(rig.SafetyMonitor.GetInfo().Connected, Is.False, "the instruction must disconnect the device it reports on");
            // The lifecycle reads Disconnected at once and tears the client down on a background task.
            await WaitForAsync(() => fake.Disconnects == 1, "the teardown never reached the device");
        }

        [Test]
        public async Task DisconnectEquipment_disconnects_a_connected_rotator() {
            var fake = Track(new FakeRotatorDevice());
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), RotatorOver(fake));
            await ConnectedAsync(rig.Rotator, "rig-rotator");

            await DisconnectItem("Rotator", rig).Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(rig.Rotator.GetInfo().Connected, Is.False, "the instruction must disconnect the device it reports on");
            // The lifecycle reads Disconnected at once and tears the client down on a background task.
            await WaitForAsync(() => fake.Disconnects == 1, "the teardown never reached the device");
        }

        [Test]
        public async Task DisconnectEquipment_is_a_user_disconnect_that_recovery_leaves_alone() {
            var fake = Track(new FakeRotatorDevice());
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), RotatorOver(fake));
            await ConnectedAsync(rig.Rotator, "rig-rotator");

            await DisconnectItem("Rotator", rig).Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
            var outcome = await rig.Reconnector.RecoverAsync(DeviceType.Rotator, CancellationToken.None);

            Assert.That(rig.Rotator.DesiredConnection, Is.EqualTo(DesiredConnection.DisconnectedByUser));
            Assert.That(outcome.Dispatched, Is.EqualTo(0), "the §42.3 ladder must not bring back a device the sequence disconnected");
            Assert.That(fake.Connects, Is.EqualTo(1));
        }

        // A device whose link tripped is in Error, not Connected: the disconnect must still record the user's intent.
        [Test]
        public async Task DisconnectEquipment_on_a_tripped_device_is_a_user_disconnect() {
            var fake = Track(new FakeRotatorDevice());
            var rotator = Track(new RotatorService { DeviceFactory = _ => fake, RefreshPeriod = TimeSpan.FromMilliseconds(50) });
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), rotator);
            await ConnectedAsync(rig.Rotator, "rig-rotator");
            fake.Answers = false;
            await WaitForAsync(() => !rig.Rotator.GetInfo().Connected, "the rotator never tripped");

            await DisconnectItem("Rotator", rig).Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
            var outcome = await rig.Reconnector.RecoverAsync(DeviceType.Rotator, CancellationToken.None);

            Assert.That(rig.Rotator.DesiredConnection, Is.EqualTo(DesiredConnection.DisconnectedByUser));
            Assert.That(outcome.Dispatched, Is.EqualTo(0));
        }

        [Test]
        public async Task DisconnectAllEquipment_on_a_tripped_device_is_a_user_disconnect() {
            var fake = Track(new FakeRotatorDevice());
            var rotator = Track(new RotatorService { DeviceFactory = _ => fake, RefreshPeriod = TimeSpan.FromMilliseconds(50) });
            // Disconnect All reaches every role, the guider included (it records no desired state, so it is never skipped).
            var profile = new StoreBackedProfileService(new InMemoryProfileStore());
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), rotator, profile, GuiderOver(profile));
            await ConnectedAsync(rig.Rotator, "rig-rotator");
            fake.Answers = false;
            await WaitForAsync(() => !rig.Rotator.GetInfo().Connected, "the rotator never tripped");

            await new DisconnectAllEquipment(rig.Reconnector).Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(rig.Rotator.DesiredConnection, Is.EqualTo(DesiredConnection.DisconnectedByUser));
        }

        // Guard (passes on master): the Reconnect trigger recovers; it does not override a user disconnect.
        [Test]
        public async Task ReconnectTrigger_does_not_reconnect_a_device_the_user_disconnected() {
            var fake = Track(new FakeRotatorDevice());
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), RotatorOver(fake),
                Dto(DeviceType.Rotator, "rig-rotator"));
            await ConnectedAsync(rig.Rotator, "rig-rotator");
            await rig.Rotator.DisconnectAsync(null, CancellationToken.None);
            await WaitForAsync(() => !rig.Rotator.GetInfo().Connected, "the rotator never disconnected");

            await Assert.CatchAsync<SequenceEntityFailedException>(() =>
                ReconnectTriggerFor("Rotator", rig).Execute(null!, new Progress<ApplicationStatus>(), CancellationToken.None));
            Assert.That(rig.Rotator.GetInfo().Connected, Is.False);
            Assert.That(fake.Connects, Is.EqualTo(1), "the trigger must not dial a device the user disconnected");
        }

        // A user disconnect is deliberate: the trigger does not fire (and fail) before every item for it.
        [Test]
        public async Task ReconnectTrigger_does_not_fire_for_a_device_the_user_disconnected() {
            var fake = Track(new FakeRotatorDevice());
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), RotatorOver(fake),
                Dto(DeviceType.Rotator, "rig-rotator"));
            await ConnectedAsync(rig.Rotator, "rig-rotator");
            await rig.Rotator.DisconnectAsync(null, CancellationToken.None);
            await WaitForAsync(() => !rig.Rotator.GetInfo().Connected, "the rotator never disconnected");

            Assert.That(ReconnectTriggerFor("Rotator", rig).ShouldTrigger(null, null), Is.False,
                "a device the user disconnected is not a dropped device: the trigger stays quiet");
        }

        // ── The guider (PHD2, not on the lifecycle module and not in the selection store) ──

        [Test]
        [Category("IO")] // loopback TCP to the bench fake guider
        [Category("bench")]
        public async Task ConnectAllEquipment_dials_the_guider_the_profile_names() {
            await using var phd2 = StartFakeGuider();
            var profile = ProfileWithPhd2At(phd2);
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), RotatorOver(Track(new FakeRotatorDevice())),
                profile, GuiderOver(profile));

            await ConnectAllItem(rig).Execute(new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(rig.Guider!.GetInfo().Connected, Is.True, "the profile names PHD2, so Connect All connects the guider");
            Assert.That(phd2.ConnectionCount, Is.GreaterThanOrEqualTo(1), "the connect must reach PHD2");
        }

        // Guard (passes on master). No production code writes No_Guider (only NINA's DummyGuider, which Ara
        // never builds), so this sets it by hand: it pins the skip, not an identity source.
        [Test]
        [Category("IO")] // loopback TCP to the bench fake guider
        [Category("bench")]
        public async Task ConnectAllEquipment_does_not_dial_the_guider_when_the_profile_has_none() {
            await using var phd2 = StartFakeGuider();
            var profile = ProfileWithPhd2At(phd2);
            profile.ActiveProfile.GuiderSettings.GuiderName = "No_Guider";
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), RotatorOver(Track(new FakeRotatorDevice())),
                profile, GuiderOver(profile));

            await ConnectAllItem(rig).Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
            await Task.Delay(300);

            Assert.That(rig.Guider!.GetInfo().Connected, Is.False);
            Assert.That(phd2.ConnectionCount, Is.EqualTo(0), "No_Guider means no guider: PHD2 is never dialled");
        }

        // Guard that pins a known gap (passes on master): the guider has no user-disconnect gate, so a
        // Reconnect trigger redials a guider the sequence disconnected. Gating it changes the §42.3 ladder
        // for every caller; when that lands, this test flips on purpose.
        [Test]
        [Category("IO")] // loopback TCP to the bench fake guider
        [Category("bench")]
        public async Task ReconnectTrigger_redials_a_guider_the_sequence_disconnected_known_gap() {
            await using var phd2 = StartFakeGuider();
            var profile = ProfileWithPhd2At(phd2);
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), RotatorOver(Track(new FakeRotatorDevice())),
                profile, GuiderOver(profile));
            await rig.Guider!.ConnectAsync(new GuiderConnectRequestDto(), null, CancellationToken.None);
            await WaitForAsync(() => rig.Guider.GetInfo().Connected, "the guider never connected");

            await DisconnectItem("Guider", rig).Execute(new Progress<ApplicationStatus>(), CancellationToken.None);
            await WaitForAsync(() => phd2.ConnectionCount == 0, "the guider never dropped its PHD2 socket");
            var trigger = ReconnectTriggerFor("Guider", rig);
            Assert.That(trigger.ShouldTrigger(null, null), Is.True);
            await trigger.Execute(null!, new Progress<ApplicationStatus>(), CancellationToken.None);

            Assert.That(rig.Guider.GetInfo().Connected, Is.True, "known gap: recovery redials a guider the sequence disconnected");
            Assert.That(phd2.ConnectionCount, Is.GreaterThanOrEqualTo(1));
        }


        // ── Switch Profile (B3a: never disconnect the rig and then fail) ──

        [Test]
        public async Task SwitchProfile_that_cannot_switch_refuses_before_it_disconnects_anything() {
            var fake = Track(new FakeRotatorDevice());
            var rig = BuildRig(SafetyMonitorOver(Track(new FakeSafetyMonitorDevice())), RotatorOver(fake));
            await ConnectedAsync(rig.Rotator, "rig-rotator");

            await Assert.CatchAsync<SequenceEntityFailedException>(() =>
                SwitchProfileItem(rig).Execute(new Progress<ApplicationStatus>(), CancellationToken.None));
            Assert.That(rig.Rotator.GetInfo().Connected, Is.True, "the rig stays connected when the switch cannot happen");
            Assert.That(fake.Disconnects, Is.EqualTo(0));
        }

        [Test]
        public void SwitchProfile_fails_validation_with_the_refusal() {
            var item = new SwitchProfile(Track(new StoreBackedProfileService(new InMemoryProfileStore())));

            Assert.That(item.Validate(), Is.False);
            Assert.That(item.Issues, Has.Count.EqualTo(1));
            Assert.That(item.Issues[0], Is.EqualTo("The daemon runs one profile; switch it from the Profiles page."));
        }
    }
}