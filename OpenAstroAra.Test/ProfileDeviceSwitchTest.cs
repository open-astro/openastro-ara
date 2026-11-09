#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1710 — a profile owns its devices: POST /profiles/{id}/select, against real profile and
    /// selection stores and fake device services, checking what would happen to the rig.
    /// </summary>
    [TestFixture]
    [Category("IO")]
    public class ProfileDeviceSwitchTest {
        private WebApplication app = null!;
        private HttpClient http = null!;
        private string dir = null!;
        private FileProfileStore store = null!;
        private FileProfileRepository repo = null!;
        private EquipmentSelectionStore selections = null!;
        private ActiveRunSessionRegistry runs = null!;
        private FakeRig rig = null!;
        private readonly Mock<IGuiderService> guider = new();
        private Guid sim;
        private Guid real;

        private static readonly string[] SafetyThenMountThenConnect = ["disconnect:safety", "disconnect:mount", "connect:real-mount"];
        private static readonly string[] SwitchReleased = ["disconnect:sim-switch", "forget:sim-switch"];
        private static readonly string[] OnlySimSafety = ["sim-safety"];
        private static readonly string[] ThreeFailed = ["real-mount", "shared-camera", "dead-focuser"];
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        private static DiscoveredDeviceDto Device(DeviceType type, string id, int number = 0) =>
            new(UniqueId: id, Name: id, Type: type, HostName: id, IpAddress: "10.0.0.1", IpPort: 11111, AlpacaDeviceNumber: number, UseHttps: false);

        private static readonly DiscoveredDeviceDto SimSafety = Device(DeviceType.SafetyMonitor, "sim-safety");
        private static readonly DiscoveredDeviceDto SimMount = Device(DeviceType.Telescope, "sim-mount");
        private static readonly DiscoveredDeviceDto RealMount = Device(DeviceType.Telescope, "real-mount");
        private static readonly DiscoveredDeviceDto SharedCamera = Device(DeviceType.Camera, "shared-camera");
        private static readonly DiscoveredDeviceDto SimSwitch = Device(DeviceType.Switch, "sim-switch");

        /// <summary>Fake device services and reconnector: one call log, one state per device type.</summary>
        private sealed class FakeRig : IEquipmentReconnector {
            public readonly List<string> Log = new();
            public readonly Dictionary<DeviceType, EquipmentConnectionState> States = new();
            public readonly Dictionary<string, EquipmentConnectionState> ConnectEndsIn = new();
            public readonly HashSet<string> ConnectThrows = new();
            public readonly List<SwitchDto> Switches = new();

            public Func<Task>? ResolveConnect(DeviceType type, DiscoveredDeviceDto device, CancellationToken ct) => () => {
                Log.Add("connect:" + device.UniqueId);
                if (ConnectThrows.Contains(device.UniqueId)) throw new InvalidOperationException("bridge refused");
                States[type.Canonical()] = ConnectEndsIn.GetValueOrDefault(device.UniqueId, EquipmentConnectionState.Connected);
                return Task.CompletedTask;
            };

            public Task<ReconnectOutcome> ReconnectAsync(DeviceType type, CancellationToken ct) => throw new NotSupportedException();

            public Task<EquipmentConnectionState?> GetConnectionStateAsync(DeviceType type, CancellationToken ct) =>
                Task.FromResult<EquipmentConnectionState?>(States.TryGetValue(type.Canonical(), out var s) ? s : null);

            public static void Retain<T>(Mock<T> mock, DiscoveredDeviceDto? device) where T : class =>
                mock.As<IRetainedDeviceSource>().SetupGet(s => s.RetainedDevice).Returns(device);
        }

        private static OperationAcceptedDto Accepted() => new(Guid.Empty, "op", DateTimeOffset.UnixEpoch, null);

        [SetUp]
        public async Task Start() {
            guider.Reset();
            dir = Path.Combine(Path.GetTempPath(), "ara-profile-devices-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            store = new FileProfileStore(dir);
            repo = new FileProfileRepository(dir, store);
            sim = repo.Create("Simulators", settings: null, makeActive: true).Id;
            real = repo.Create("Real rig", settings: null, makeActive: false).Id;
            selections = new EquipmentSelectionStore(dir, NullLogger<EquipmentSelectionStore>.Instance, _ => false, () => repo.ActiveId);
            runs = new ActiveRunSessionRegistry();
            rig = new FakeRig();

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton<IProfileRepository>(repo);
            builder.Services.AddSingleton(guider.Object);
            builder.Services.AddSingleton(runs);
            AddDevices(builder.Services);
            builder.Services.AddSingleton<IEquipmentSelectionStore>(selections);
            builder.Services.AddSingleton<IEquipmentReconnector>(rig);
            builder.Services.AddSingleton<IProfileStore>(store);
            builder.Services.AddSingleton(sp => new ProfileDeviceSwitch(sp, selections, rig, store, NullLogger<ProfileDeviceSwitch>.Instance) {
                SettleTimeout = TimeSpan.FromMilliseconds(400),
            });
            builder.Services.AddProblemDetails();
            builder.Services.ConfigureHttpJsonOptions(opts => {
                opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
                opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            });
            app = builder.Build();
            app.MapProfilesEndpoints();
            await app.StartAsync();
            http = new HttpClient { BaseAddress = new Uri(app.Urls.First()), Timeout = TimeSpan.FromMinutes(2) };
        }

        [TearDown]
        public async Task Stop() {
            http.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            selections.Dispose();
            repo.Dispose();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        private Mock<ICameraService> camera = null!;
        private Mock<ITelescopeService> mount = null!;
        private Mock<ISafetyMonitorService> safety = null!;
        private Mock<ISwitchService> switches = null!;

        private void AddDevices(IServiceCollection services) {
            camera = new Mock<ICameraService>();
            camera.Setup(s => s.DisconnectAsync(null, It.IsAny<CancellationToken>())).Callback(() => Went("camera")).ReturnsAsync(Accepted());
            camera.Setup(s => s.ForgetAsync(It.IsAny<CancellationToken>())).Callback(() => rig.Log.Add("forget:camera")).ReturnsAsync(true);
            mount = new Mock<ITelescopeService>();
            mount.Setup(s => s.DisconnectAsync(null, It.IsAny<CancellationToken>())).Callback(() => Went("mount")).ReturnsAsync(Accepted());
            mount.Setup(s => s.ForgetAsync(It.IsAny<CancellationToken>())).Callback(() => rig.Log.Add("forget:mount")).ReturnsAsync(true);
            safety = new Mock<ISafetyMonitorService>();
            safety.Setup(s => s.DisconnectAsync(null, It.IsAny<CancellationToken>())).Callback(() => Went("safety")).ReturnsAsync(Accepted());
            safety.Setup(s => s.ForgetAsync(It.IsAny<CancellationToken>())).Callback(() => rig.Log.Add("forget:safety")).ReturnsAsync(true);
            switches = new Mock<ISwitchService>();
            switches.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => rig.Switches);
            switches.Setup(s => s.DisconnectAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()))
                .Callback<string, string?, CancellationToken>((id, _, _) => {
                    rig.Log.Add("disconnect:" + id);
                    var at = rig.Switches.FindIndex(d => d.DeviceId == id);
                    rig.Switches[at] = rig.Switches[at] with { State = EquipmentConnectionState.Disconnected };
                }).ReturnsAsync(Accepted());
            switches.Setup(s => s.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<string, CancellationToken>((id, _) => rig.Log.Add("forget:" + id)).ReturnsAsync(true);
            FakeRig.Retain(camera, null);
            FakeRig.Retain(mount, null);
            FakeRig.Retain(safety, null);
            services.AddSingleton(camera.Object);
            services.AddSingleton(mount.Object);
            services.AddSingleton(safety.Object);
            services.AddSingleton(switches.Object);
        }

        private void Went(string name) {
            rig.Log.Add("disconnect:" + name);
            rig.States[name switch {
                "camera" => DeviceType.Camera,
                "mount" => DeviceType.Telescope,
                _ => DeviceType.SafetyMonitor,
            }] = EquipmentConnectionState.Disconnected;
        }

        // The simulator profile is active with a connected sim mount and sim Safety Monitor.
        private async Task SimRigConnectedAsync() {
            await selections.RememberAsync(SimSafety, CancellationToken.None);
            await selections.RememberAsync(SimMount, CancellationToken.None);
            FakeRig.Retain(safety, SimSafety);
            FakeRig.Retain(mount, SimMount);
            rig.States[DeviceType.SafetyMonitor] = EquipmentConnectionState.Connected;
            rig.States[DeviceType.Telescope] = EquipmentConnectionState.Connected;
        }

        private async Task RememberForAsync(Guid profile, params DiscoveredDeviceDto[] devices) {
            var was = repo.ActiveId!.Value;
            repo.SelectProfile(profile);
            foreach (var d in devices) {
                await selections.RememberAsync(d, CancellationToken.None);
            }
            repo.SelectProfile(was);
        }

        private async Task<(HttpStatusCode Status, ProfileSelectResultDto? Body)> SelectAsync(Guid id) {
            var resp = await http.PostAsync(new Uri($"/api/v1/profiles/{id}/select", UriKind.Relative), null);
            return (resp.StatusCode, resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<ProfileSelectResultDto>(Json) : null);
        }

        [Test]
        public async Task Selecting_a_profile_turns_the_Safety_Monitor_off_first_then_unused_devices_then_connects_its_own() {
            await SimRigConnectedAsync();
            await RememberForAsync(real, RealMount);

            var (status, body) = await SelectAsync(real);

            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body!.ActiveId, Is.EqualTo(real));
            Assert.That(body.DeviceMessages, Is.Empty);
            Assert.That(rig.Log.Where(l => !l.StartsWith("forget:", StringComparison.Ordinal)),
                Is.EqualTo(SafetyThenMountThenConnect));
        }

        [Test]
        public async Task A_device_the_new_profile_does_not_remember_is_dropped_so_its_card_and_reconnect_stop_naming_it() {
            await SimRigConnectedAsync();
            await RememberForAsync(real, RealMount);

            await SelectAsync(real);

            Assert.That(rig.Log, Does.Contain("forget:safety").And.Contain("forget:mount"));
            Assert.That(rig.Log.IndexOf("forget:safety"), Is.GreaterThan(rig.Log.IndexOf("disconnect:safety")));
        }

        [Test]
        public async Task A_device_both_profiles_remember_stays_connected_untouched() {
            await selections.RememberAsync(SharedCamera, CancellationToken.None);
            FakeRig.Retain(camera, SharedCamera);
            rig.States[DeviceType.Camera] = EquipmentConnectionState.Connected;
            await RememberForAsync(real, SharedCamera);

            var (status, _) = await SelectAsync(real);

            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(rig.Log, Is.Empty, "a shared device is neither dropped nor re-dialled");
        }

        [Test]
        public async Task A_switch_the_new_profile_does_not_remember_is_disconnected() {
            await selections.RememberAsync(SimSwitch, CancellationToken.None);
            rig.Switches.Add(new SwitchDto("sim-switch", 0, "sim-switch", EquipmentConnectionState.Connected, Array.Empty<SwitchPortDto>()));
            switches.Setup(s => s.GetDevice("sim-switch")).Returns(SimSwitch);

            await SelectAsync(real);

            Assert.That(rig.Log, Is.EqualTo(SwitchReleased));
        }

        [Test]
        public async Task A_device_whose_connect_on_boot_toggle_is_off_is_not_connected() {
            await RememberForAsync(real, RealMount);
            repo.SelectProfile(real);
            store.PutEquipmentConnection(store.GetEquipmentConnection() with { Mount = false });
            repo.SelectProfile(sim);

            await SelectAsync(real);

            Assert.That(rig.Log, Does.Not.Contain("connect:real-mount"));
        }

        [Test]
        public async Task A_remembered_Safety_Monitor_left_off_by_its_toggle_is_reported() {
            // Disconnected first by the rule, and this profile's toggle will not bring it back.
            await SimRigConnectedAsync();
            await RememberForAsync(real, SimSafety);
            repo.SelectProfile(real);
            store.PutEquipmentConnection(store.GetEquipmentConnection() with { SafetyMonitor = false });
            repo.SelectProfile(sim);

            var (_, body) = await SelectAsync(real);

            Assert.That(body!.DeviceMessages.Select(m => m.DeviceName), Is.EqualTo(OnlySimSafety));
            Assert.That(rig.Log, Does.Not.Contain("forget:safety"), "the profile still remembers it");
        }

        [Test]
        public async Task A_run_refuses_the_switch_and_leaves_the_rig_alone() {
            await SimRigConnectedAsync();
            runs.Enter(Guid.NewGuid());

            var (status, _) = await SelectAsync(real);

            Assert.That(status, Is.EqualTo(HttpStatusCode.Conflict));
            Assert.That(repo.ActiveId, Is.EqualTo(sim));
            Assert.That(rig.Log, Is.Empty);
        }

        private async Task<HttpStatusCode> CreateAsync(string query = "") {
            using var body = new StringContent("{\"name\":\"Next\"}", System.Text.Encoding.UTF8, "application/json");
            return (await http.PostAsync(new Uri("/api/v1/profiles" + query, UriKind.Relative), body)).StatusCode;
        }

        private async Task<HttpStatusCode> DeleteAsync(Guid id) =>
            (await http.DeleteAsync(new Uri($"/api/v1/profiles/{id}", UriKind.Relative))).StatusCode;

        // Records whether the profile had already changed when the hold was taken, and when it ended.
        private void WatchSiteHold(Action<Guid?> taken) {
            var hold = new Mock<IDisposable>();
            hold.Setup(h => h.Dispose()).Callback(() => rig.Log.Add("site-hold-released"));
            mount.Setup(m => m.HoldSiteFollow()).Returns(() => { taken(repo.ActiveId); return hold.Object; });
        }

        [Test]
        public async Task Selecting_a_profile_holds_the_mounts_site_until_the_devices_are_done() {
            await SimRigConnectedAsync();
            await RememberForAsync(real, RealMount);
            Guid? activeAtHold = null;
            WatchSiteHold(id => activeAtHold = id);

            await SelectAsync(real);

            Assert.That(activeAtHold, Is.EqualTo(sim), "held before the profile changes");
            Assert.That(rig.Log[^1], Is.EqualTo("site-hold-released"), "released after every device");
        }

        [Test]
        public async Task Creating_an_active_profile_holds_the_mounts_site() {
            await SimRigConnectedAsync();
            Guid? activeAtHold = null;
            WatchSiteHold(id => activeAtHold = id);

            await CreateAsync();

            Assert.That(activeAtHold, Is.EqualTo(sim));
            Assert.That(rig.Log[^1], Is.EqualTo("site-hold-released"));
        }

        [Test]
        public async Task Deleting_the_active_profile_holds_the_mounts_site() {
            await SimRigConnectedAsync();
            Guid? activeAtHold = null;
            WatchSiteHold(id => activeAtHold = id);

            await DeleteAsync(sim);

            Assert.That(activeAtHold, Is.EqualTo(sim));
            Assert.That(rig.Log[^1], Is.EqualTo("site-hold-released"));
        }

        [Test]
        public async Task Creating_an_active_profile_drops_the_devices_of_the_one_it_replaces() {
            await SimRigConnectedAsync();

            Assert.That(await CreateAsync(), Is.EqualTo(HttpStatusCode.Created));

            Assert.That(rig.Log, Does.Contain("disconnect:safety"));
            Assert.That(rig.Log, Does.Contain("disconnect:mount"));
            Assert.That(rig.Log.IndexOf("disconnect:safety"), Is.LessThan(rig.Log.IndexOf("disconnect:mount")), "the Safety Monitor goes first");
        }

        [Test]
        public async Task Creating_a_profile_without_activating_it_touches_no_device() {
            await SimRigConnectedAsync();

            Assert.That(await CreateAsync("?activate=false"), Is.EqualTo(HttpStatusCode.Created));

            Assert.That(rig.Log, Is.Empty);
        }

        [Test]
        public async Task A_run_refuses_creating_an_active_profile_and_leaves_the_rig_alone() {
            await SimRigConnectedAsync();
            runs.Enter(Guid.NewGuid());

            Assert.That(await CreateAsync(), Is.EqualTo(HttpStatusCode.Conflict));

            Assert.That(repo.ActiveId, Is.EqualTo(sim));
            Assert.That(rig.Log, Is.Empty);
        }

        [Test]
        public async Task Deleting_the_active_profile_moves_the_devices_to_the_profile_that_takes_over() {
            await SimRigConnectedAsync();
            await RememberForAsync(real, RealMount);

            Assert.That(await DeleteAsync(sim), Is.EqualTo(HttpStatusCode.OK));

            Assert.That(repo.ActiveId, Is.EqualTo(real));
            Assert.That(rig.Log.Where(l => !l.StartsWith("forget:", StringComparison.Ordinal)), Is.EqualTo(SafetyThenMountThenConnect));
        }

        [Test]
        public async Task Deleting_the_active_profile_moves_the_guider_to_the_one_that_takes_over() {
            await SimRigConnectedAsync();

            await DeleteAsync(sim);

            guider.Verify(g => g.PushGuiderProfileAsync(null, It.IsAny<CancellationToken>(), false), Times.Once);
        }

        [Test]
        public async Task Deleting_another_profile_leaves_the_guider_alone() {
            await SimRigConnectedAsync();

            await DeleteAsync(real);

            guider.Verify(g => g.PushGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
        }

        [Test]
        public async Task Deleting_another_profile_touches_no_device() {
            await SimRigConnectedAsync();

            Assert.That(await DeleteAsync(real), Is.EqualTo(HttpStatusCode.OK));

            Assert.That(rig.Log, Is.Empty);
        }

        [Test]
        public async Task A_run_refuses_deleting_the_active_profile_and_leaves_the_rig_alone() {
            await SimRigConnectedAsync();
            runs.Enter(Guid.NewGuid());

            Assert.That(await DeleteAsync(sim), Is.EqualTo(HttpStatusCode.Conflict));

            Assert.That(repo.ActiveId, Is.EqualTo(sim));
            Assert.That(rig.Log, Is.Empty);
        }

        [Test]
        public async Task Deleting_the_last_profile_releases_its_devices() {
            await SimRigConnectedAsync();
            await DeleteAsync(real);

            Assert.That(await DeleteAsync(sim), Is.EqualTo(HttpStatusCode.OK));

            Assert.That(repo.ActiveId, Is.Null);
            Assert.That(rig.Log, Does.Contain("disconnect:safety"));
            Assert.That(rig.Log, Does.Contain("disconnect:mount"));
        }

        [Test]
        public async Task Selecting_the_active_profile_touches_no_device() {
            await SimRigConnectedAsync();

            var (status, _) = await SelectAsync(sim);

            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(rig.Log, Is.Empty);
        }

        [Test]
        public async Task An_unknown_profile_is_404_and_touches_no_device() {
            await SimRigConnectedAsync();

            var (status, _) = await SelectAsync(Guid.NewGuid());

            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(rig.Log, Is.Empty);
        }

        [Test]
        public async Task A_device_that_fails_to_connect_does_not_fail_the_switch_and_is_named() {
            var focuser = Device(DeviceType.Focuser, "dead-focuser");
            await RememberForAsync(real, RealMount, SharedCamera, focuser);
            rig.ConnectThrows.Add("real-mount");
            rig.ConnectEndsIn["shared-camera"] = EquipmentConnectionState.Error;
            rig.ConnectEndsIn["dead-focuser"] = EquipmentConnectionState.Connecting;

            var (status, body) = await SelectAsync(real);

            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(repo.ActiveId, Is.EqualTo(real));
            Assert.That(rig.Log, Does.Contain("connect:real-mount").And.Contain("connect:shared-camera").And.Contain("connect:dead-focuser"));
            var messages = body!.DeviceMessages.ToDictionary(m => m.DeviceName, m => m.Message);
            Assert.That(messages.Keys, Is.EquivalentTo(ThreeFailed));
            Assert.That(messages["real-mount"], Does.Contain("bridge refused"));
            Assert.That(messages["dead-focuser"], Does.Contain("still connecting"));
        }

        [Test]
        public async Task An_unreadable_device_list_still_drops_the_old_devices_and_says_so() {
            await SimRigConnectedAsync();
            var broken = new Mock<IEquipmentSelectionStore>();
            broken.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("disk gone"));
            var services = new ServiceCollection();
            services.AddSingleton(safety.Object);
            services.AddSingleton(mount.Object);
            using var device = new ProfileDeviceSwitch(services.BuildServiceProvider(), broken.Object, rig, store, NullLogger<ProfileDeviceSwitch>.Instance);

            var messages = await device.ApplyAsync(CancellationToken.None);

            Assert.That(rig.Log, Does.Contain("disconnect:safety").And.Contain("disconnect:mount"));
            Assert.That(messages, Has.Count.EqualTo(1));
        }
    }
}
