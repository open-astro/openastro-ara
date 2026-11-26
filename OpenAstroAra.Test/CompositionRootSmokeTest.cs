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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;
using System.Linq;
using System.Threading;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Server.Services.Equipment;
using System;
using System.IO;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1128 — builds the daemon's REAL composition root (<see cref="Program.BuildApp"/>,
    /// everything <c>Main</c> does before <c>app.Run()</c>) against a throw-away profile directory
    /// and resolves the sequencer-facing mediator aliases from it. #1089 found
    /// <c>IGuiderMediator</c> had silently reverted to the headless stub since #346 because no test
    /// ever built the Program.cs graph; this one fails the moment an alias points anywhere but the
    /// live service singleton. The two headless-on-purpose mediators are pinned as such.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    // Sets the process-wide profile-dir environment variable and replaces the global Serilog
    // logger (BuildApp installs the §29.9 file sink, as Main does); any future fixture that also
    // calls BuildApp needs the same isolation. Building is network-free today — Kestrel binds in
    // Run, and no singleton the startup reconciliation or boot probes resolve opens a socket or
    // starts Alpaca discovery in its constructor — but it does run the real startup side effects
    // against the temp dir (SQLite catalog, the capture scan, the orphan sweeps).
    [NonParallelizable]
    public class CompositionRootSmokeTest {

        private string profileDir = null!;
        private string? previousProfileDir;
        private WebApplication app = null!;

        private string exchangeDir = null!;
        private string? previousExchangeDir;

        [OneTimeSetUp]
        public void BuildTheDaemon() {
            profileDir = Path.Combine(Path.GetTempPath(), $"oara-composition-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDir);
            previousProfileDir = Environment.GetEnvironmentVariable("OPENASTROARA_PROFILE_DIR");
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", profileDir);
            // #1248 — a stale storage result planted BEFORE the build: BuildApp's eager resolve of
            // IStorageDeviceService runs the boot sweep, and the test below asserts it is gone
            // without ever resolving the service itself.
            exchangeDir = Path.Combine(profileDir, "storage-exchange");
            Directory.CreateDirectory(exchangeDir);
            var stale = Path.Combine(exchangeDir, "cancelled.result");
            File.WriteAllText(stale, "0\n");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - StorageDeviceService.SweepMinimumAge - TimeSpan.FromMinutes(1));
            previousExchangeDir = Environment.GetEnvironmentVariable(StorageDeviceService.ExchangeDirEnvVar);
            Environment.SetEnvironmentVariable(StorageDeviceService.ExchangeDirEnvVar, exchangeDir);
            app = Program.BuildApp([], services => {
                // What Program.cs itself registers, read before the test seams replace it.
                productionSupervisor = services.Last(d => d.ServiceType == typeof(IGuiderProcessSupervisor)).ImplementationType;
                TestHostSeams.Replace(services);
                // #2071 — the time sync as Program.cs builds it, but with a clock setter that never touches
                // this machine's clock and a kernel probe that reports no NTP sync (a CI runner usually has
                // one), so the mount-clock test below starts with no trusted time on every host.
                services.Replace(ServiceDescriptor.Singleton<TimeSyncService>(sp => new TimeSyncService(
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TimeSyncService>>(),
                    clockSetter: new NoClockSetter(),
                    profiles: sp.GetService<IProfileStore>(),
                    kernelClock: new NoKernelSync())));
            });
        }

        private Type? productionSupervisor;

        private sealed class NoClockSetter : ISystemClockSetter {
            public bool TrySet(DateTimeOffset utc) => false;
        }

        private sealed class NoKernelSync : IKernelClockSyncProbe {
            public bool IsSynchronized() => false;
        }

        /// <summary>A mount whose sidereal time follows its own clock, at the profile's site.</summary>
        private sealed class WiredMountClock(double lat, double lng) : TelescopeService.IMountClock {
            private readonly Lock gate = new();
            private DateTime clock = DateTime.UtcNow - TimeSpan.FromHours(6.8); // RC51: hours slow
            public int Writes;
            public DateTime UtcDate { get { lock (gate) { return clock; } } }
            public void SetUtcDate(DateTime utc) { lock (gate) { clock = utc; Writes++; } }
            public double SiderealTime => SiteAstrometry.LocalSiderealTimeDeg(new DateTimeOffset(UtcDate), lng) / 15.0;
            public double Latitude => lat;
            public double Longitude => lng;
            public bool Slewing => false;
        }

        [Test]
        public async Task A_trusted_time_sync_sets_the_mount_clock_and_opens_the_GoTo_and_polar_align_gate() {
            // #2071 — every other mount-clock test builds the services by hand. This one pins Program.cs: drop
            // the time-sync resolver from the TelescopeService factory and the gate is off (GoTo on a stale
            // mount clock again, the RC51 failure); drop FollowClock(...) and a sync never writes the mount's
            // clock; drop mountClock: from the PolarAlignService factory and polar alignment is not gated.
            var mount = app.Services.GetRequiredService<TelescopeService>();
            Assert.That(app.Services.GetRequiredService<IMountClockGate>(), Is.SameAs(mount));
            var polar = app.Services.GetRequiredService<IPolarAlignService>();
            var polarGate = typeof(PolarAlignService)
                .GetField("_mountClock", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(polar);
            Assert.That(polarGate, Is.SameAs(mount), "polar alignment asks the same mount-clock gate as GoTo");

            var site = app.Services.GetRequiredService<IProfileStore>().GetSiteSettings();
            var fake = new WiredMountClock(site.LatitudeDeg, site.LongitudeDeg);
            mount.MountClockSource = () => fake;
            try {
                Assert.That(mount.MountClockMotionRefusal(), Does.StartWith("Mount clock not verified"),
                    "with no trusted time and no check the gate is closed, which it only is when Program.cs wires the time sync");
                await app.Services.GetRequiredService<ITimeSyncService>()
                    .PushAsync(new TimeSyncPushRequestDto("client", DateTimeOffset.UtcNow), CancellationToken.None);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while ((Volatile.Read(ref fake.Writes) == 0 || mount.MountClockMotionRefusal() is not null) && DateTime.UtcNow < deadline) {
                    await Task.Delay(50);
                }
                Assert.Multiple(() => {
                    Assert.That(Volatile.Read(ref fake.Writes), Is.GreaterThanOrEqualTo(1), "a client sync writes the mount's UTCDate");
                    Assert.That(mount.MountClockMotionRefusal(), Is.Null, "the written and checked mount clock opens the gate");
                });
            } finally {
                mount.MountClockSource = null;
            }
        }

        // #2141 — Rig Occupancy as Program.cs composes it: a running Polar Align Routine holds the mount and the
        // main camera, so the composed Bahtinov busy reason names it; a held centering gate is a Center under way.
        // Drop either registration, or put the Bahtinov reason back on the run registry, and this fails.
        [Test]
        public async Task The_composed_rig_occupancy_names_polar_alignment_and_centering_and_the_bahtinov_readout_refuses() {
            const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var occupancy = app.Services.GetRequiredService<RigOccupancy>();
            Assert.That(occupancy.Holder(), Is.Null, "an idle rig is free");

            var polar = app.Services.GetRequiredService<IPolarAlignService>();
            var active = typeof(PolarAlignService).GetField("_active", Private)!;
            active.SetValue(polar, true);
            try {
                Assert.That(occupancy.Holder(RigHolding.Mount), Is.EqualTo("polar alignment"));
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => app.Services.GetRequiredService<IBahtinovFocusService>()
                    .StartAsync(new BahtinovFocusStartRequestDto(), CancellationToken.None));
                Assert.That(refused!.Message, Does.Contain("polar alignment is running"));
            } finally {
                active.SetValue(polar, false);
            }

            var gate = (SemaphoreSlim)typeof(CenteringService).GetField("_centeringGate", Private)!
                .GetValue(app.Services.GetRequiredService<ICenteringService>())!;
            await gate.WaitAsync();
            try {
                Assert.That(occupancy.Holder(RigHolding.MainCamera), Is.EqualTo("centering"));
            } finally {
                gate.Release();
            }
            Assert.That(occupancy.Holder(), Is.Null);
        }

        private static readonly string[] ComposedRoutines = {
            RigOccupancy.Session, "polar alignment", "centering", RigOccupancy.EndNight, "filter-offset calibration", "autofocus",
            "the Bahtinov focus readout", "guide-camera focus", "the rotation readout", "Live View", "a disk-to-disk copy",
        };

        // #2143 — every routine is registered once, in this order: the Session first (it names a refusal during a run),
        // and filter-offset calibration before autofocus (a stop ends the job before it starts its next run).
        [Test]
        public void The_composed_rig_occupancy_registers_every_routine_in_order() {
            var routines = (System.Array)typeof(RigOccupancy)
                .GetField("_routines", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(app.Services.GetRequiredService<RigOccupancy>())!;
            var names = routines.Cast<object>().Select(r => (string)r.GetType().GetProperty("Name")!.GetValue(r)!).ToArray();

            Assert.That(names, Is.EqualTo(ComposedRoutines));
        }

        // #2142 — every composed stop ladder ends routines through the one register: Emergency Stop, the safety and fault
        // parks, End Night's ladder and the unattended shutdown's own ladder. A ladder composed without it stops no routine.
        [Test]
        public async Task Every_composed_stop_ladder_reads_the_one_rig_occupancy() {
            const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            static object? Field(object owner, string name) => owner.GetType().GetField(name, Private)!.GetValue(owner);
            var occupancy = app.Services.GetRequiredService<RigOccupancy>();
            var ladders = new Dictionary<string, object> {
                ["emergency stop"] = app.Services.GetRequiredService<EmergencyStopService>(),
                ["safety park"] = app.Services.GetRequiredService<SafetyReactionService>(),
                ["fault park"] = app.Services.GetRequiredService<FaultReactionService>(),
                ["end night"] = app.Services.GetRequiredService<ShutdownLadder>(),
                ["unattended shutdown"] = Field(app.Services.GetRequiredService<UnattendedShutdownService>(), "_ladder")!,
            };
            Assert.Multiple(() => {
                foreach (var (name, ladder) in ladders) {
                    Assert.That(Field(ladder, "_occupancy"), Is.SameAs(occupancy), name);
                }
                // #2143 — End Night's ladder runs for End Night, so its own step leaves End Night alone.
                Assert.That(Field(ladders["end night"], "_routine"), Is.EqualTo(RigOccupancy.EndNight), "end night's routine");
            });

            // The composed Center has a stop: a ladder that asks for the mount reaches it (held gate: it never ends here).
            var gate = (SemaphoreSlim)Field(app.Services.GetRequiredService<ICenteringService>(), "_centeringGate")!;
            await gate.WaitAsync();
            try {
                var stops = await occupancy.StopAsync(RigHolding.Mount, MountStopReasons.EmergencyStop, TimeSpan.FromMilliseconds(100));
                Assert.That(stops.Single().Name, Is.EqualTo("centering"));
            } finally {
                gate.Release();
            }
        }

        [Test]
        public void The_test_host_resolves_the_fake_guider_supervisor_not_systemctl() {
            // The real supervisor runs `systemctl start|restart openastro-guider`; on a Linux desktop with a
            // polkit agent every test run popped a password dialog. Program.cs keeps the real one, and every
            // test host must swap it. A registration the override cannot reach fails this test: a second
            // registration after it, or an inline `new` in the GuiderService / recovery factories (the two
            // consumers' supervisor fields are read back below).
            var consumers = new object[] {
                app.Services.GetRequiredService<GuiderService>(),
                app.Services.GetRequiredService<GuiderRecoveryCoordinator>(),
            };
            var held = consumers.SelectMany(c => c.GetType()
                    .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    .Where(f => f.FieldType == typeof(IGuiderProcessSupervisor))
                    .Select(f => f.GetValue(c)?.GetType()))
                .ToList();
            Assert.Multiple(() => {
                Assert.That(held, Has.Count.EqualTo(2), "GuiderService and GuiderRecoveryCoordinator each hold one supervisor");
                Assert.That(held, Is.All.EqualTo(typeof(TestHostSeams.FakeGuiderProcessSupervisor)));
                Assert.That(productionSupervisor, Is.EqualTo(typeof(SystemctlGuiderProcessSupervisor)),
                    "Program.cs registers the systemctl supervisor for the rig");
                Assert.That(app.Services.GetServices<IGuiderProcessSupervisor>().Select(s => s.GetType()),
                    Is.EqualTo(new[] { typeof(TestHostSeams.FakeGuiderProcessSupervisor) }));
                Assert.That(app.Services.GetRequiredService<IHostPoweroffStarter>(), Is.TypeOf<TestHostSeams.FakeHostPoweroffStarter>());
            });
        }

        [Test]
        public async Task A_device_layer_toast_lands_in_the_notification_inbox() {
            // #1174 — the boot wiring under test: BuildApp installs the Notifier → inbox bridge, so a
            // static Notifier.ShowWarning from the ported device code becomes a §46 Equipment/Warning
            // entry. Delete the Notifier.Use(...) line in Program.cs and this is the test that fails.
            Assert.That(OpenAstroAra.Core.Utility.Notification.Notifier.Current, Is.TypeOf<NotifierInboxBridge>(),
                "BuildApp must install the inbox bridge as the Notifier sink");
            var message = $"composition smoke toast {Guid.NewGuid():N}";
            OpenAstroAra.Core.Utility.Notification.Notifier.ShowWarning(message);

            var inbox = app.Services.GetRequiredService<INotificationService>();
            NotificationDto? posted = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (posted is null && DateTime.UtcNow < deadline) {
                var page = await inbox.ListAsync(50, null, null, CancellationToken.None);
                // §58.10 may append "(severity raised — unattended hours)" to the text at night.
                posted = page.Items.FirstOrDefault(n => n.Message.StartsWith(message, StringComparison.Ordinal));
                if (posted is null) {
                    await Task.Delay(50);
                }
            }
            if (posted is null) {
                var page = await inbox.ListAsync(50, null, null, CancellationToken.None);
                await TestContext.Out.WriteLineAsync("inbox holds: " + string.Join(" | ", page.Items.Select(n => $"{n.Severity}/{n.Category}: {n.Message}")));
            }
            Assert.That(posted, Is.Not.Null, "the toast never reached the inbox");
            // A Warning, or Error when §58.10 raised it for unattended hours (the site is in darkness).
            Assert.That(posted!.Severity, Is.EqualTo(NotificationSeverity.Warning).Or.EqualTo(NotificationSeverity.Error));
            Assert.That(posted.Category, Is.EqualTo(NotificationCategory.Equipment));
        }

        [Test]
        public void Storage_service_is_built_eagerly_so_the_boot_sweep_runs() {
            // #1248 — the sweep is a constructor side effect; a lazily resolved singleton would
            // leave a cancelled request's leftover in tmpfs for the whole uptime (#1135). The file
            // planted in OneTimeSetUp must already be gone, with the service never resolved here.
            Assert.That(File.Exists(Path.Combine(exchangeDir, "cancelled.result")), Is.False,
                "BuildApp must resolve IStorageDeviceService itself (Program.cs #1135 eager resolve)");
        }

        [OneTimeTearDown]
        public async Task TearDownTheDaemon() {
            // BuildApp installed the process-global Notifier → inbox sink pointing at this root (#1174).
            OpenAstroAra.Core.Utility.Notification.Notifier.Use(null);
            await app.DisposeAsync();
            // BuildApp installs the §29.9 rolling file sink under the profile dir as Main does;
            // release the file so the directory can go (Main's finally does the same after Run).
            await Serilog.Log.CloseAndFlushAsync();
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", previousProfileDir);
            Environment.SetEnvironmentVariable(StorageDeviceService.ExchangeDirEnvVar, previousExchangeDir);
            // The pooled SQLite handle would keep the catalog file open past DisposeAsync on
            // Windows and leave the temp dir behind.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try {
                Directory.Delete(profileDir, recursive: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        [Test]
        public void Emergency_Stop_resolves_with_all_four_routine_services_registered() {
            // #1738 — Program.cs resolves these with GetService, so a missing registration would silently
            // drop that step from the stop.
            Assert.Multiple(() => {
                Assert.That(app.Services.GetRequiredService<EmergencyStopService>(), Is.Not.Null);
                Assert.That(app.Services.GetService<IGuideFocusService>(), Is.Not.Null);
                Assert.That(app.Services.GetService<IBahtinovFocusService>(), Is.Not.Null);
                Assert.That(app.Services.GetService<AutofocusRunTracker>(), Is.Not.Null);
                Assert.That(app.Services.GetService<IPolarAlignService>(), Is.Not.Null);
            });
        }

        [Test]
        public void Offset_calibration_resolves_from_the_composition_root() {
            // #1675 — POST /equipment/focuser/autofocus takes it as a parameter, so a missing dependency would break autofocus too.
            Assert.That(app.Services.GetRequiredService<FilterOffsetCalibrationService>(), Is.Not.Null);
        }

        private void AssertLive<TMediator, TService>() where TMediator : class where TService : class {
            object mediator = app.Services.GetRequiredService<TMediator>();
            object service = app.Services.GetRequiredService<TService>();
            Assert.That(mediator, Is.SameAs(service),
                $"{typeof(TMediator).Name} must be the live {typeof(TService).Name} singleton, not a headless stub — "
                + "the sequencer would otherwise report success while nothing drives the device (#1089)");
        }

        private void AssertSame<TA, TB>() where TA : class where TB : class {
            object a = app.Services.GetRequiredService<TA>();
            object b = app.Services.GetRequiredService<TB>();
            Assert.That(a, Is.SameAs(b), $"{typeof(TA).Name} and {typeof(TB).Name} must resolve to one singleton");
        }

        // #1194 — the production capture roots include the profile-dir frames fallback that
        // CameraService.ResolveFramesDir writes to when the save directory is unavailable. Only the
        // real composition root adds it (the constructor default has the save directory and the
        // store mount), so it is asserted here, against the app Program.cs builds.
        [Test]
        public void Frame_repository_serves_the_profile_dir_frames_fallback() {
            var repo = app.Services.GetRequiredService<IFrameRepository>() as SqliteFrameRepository;
            Assert.That(repo, Is.Not.Null, "the production IFrameRepository is the SQLite repository");
            var fallback = Path.Combine(profileDir, "frames", "manual");
            Directory.CreateDirectory(fallback);
            var fits = Path.Combine(fallback, "a.fits");
            File.WriteAllText(fits, "x");
            Assert.That(repo!.ConfinedExistingPath(fits), Is.EqualTo(Path.GetFullPath(fits)), "a frame written to the fallback root stays servable");
            var elsewhere = Path.Combine(profileDir, "not-frames", "b.fits");
            Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
            File.WriteAllText(elsewhere, "x");
            Assert.That(repo.ConfinedExistingPath(elsewhere), Is.Null, "the profile dir itself is not a root, only its frames/ child");
        }

        [Test] public void SafetyMonitor_mediator_is_the_live_service() => AssertLive<ISafetyMonitorMediator, SafetyMonitorService>();
        [Test] public void Telescope_mediator_is_the_live_service() => AssertLive<ITelescopeMediator, TelescopeService>();
        [Test] public void Guider_mediator_is_the_live_service() => AssertLive<IGuiderMediator, GuiderService>();

        [Test]
        public void The_guider_hands_the_camera_over_through_the_one_custody_module() {
            // 2026-10-16 — a run with an abandoned Polar Align step paused at "Guiding did not start". The
            // hand-over is no longer wired by functions set in Program.cs: the guider takes the one
            // registered custody module, which Polar Align, guide focus and the live view reach through it.
            var guider = app.Services.GetRequiredService<GuiderService>();
            Assert.That(guider.Custody, Is.SameAs(app.Services.GetRequiredService<GuideCameraCustody>()));
            Assert.That(guider.Custody.Holder, Is.Null, "nothing holds the camera in a fresh daemon");
        }
        [Test] public void Focuser_mediator_is_the_live_service() => AssertLive<IFocuserMediator, FocuserService>();
        [Test] public void Camera_mediator_is_the_live_service() => AssertLive<ICameraMediator, CameraService>();
        [Test] public void Imaging_mediator_is_the_live_camera_service() => AssertLive<IImagingMediator, CameraService>();
        // #1298 — the filter wheel mediator is the router in front of the live Alpaca wheel and the
        // manual wheel, and it is the SAME singleton the REST endpoints use.
        [Test] public void FilterWheel_mediator_is_the_live_service() => AssertLive<IFilterWheelMediator, FilterWheelRouter>();

        [Test]
        public void FilterWheel_rest_service_is_the_same_router() {
            Assert.That(app.Services.GetRequiredService<IFilterWheelService>(),
                Is.SameAs(app.Services.GetRequiredService<FilterWheelRouter>()));
            Assert.That(app.Services.GetRequiredService<IEquipmentDiscoveryService>(), Is.TypeOf<ManualDeviceDiscoveryService>());
        }
        [Test] public void Rotator_mediator_is_the_live_service() => AssertLive<IRotatorMediator, RotatorService>();
        [Test] public void Switch_mediator_is_the_live_service() => AssertLive<ISwitchMediator, SwitchService>();
        [Test] public void Dome_mediator_is_the_live_service() => AssertLive<IDomeMediator, DomeService>();

        [Test]
        public void The_two_headless_mediators_are_headless_on_purpose() {
            // No flat-device / weather sequence instruction consumes these yet (Program.cs §38k-19/20);
            // when one does, swap the stub here for the live service like the ten above.
            Assert.That(app.Services.GetRequiredService<IFlatDeviceMediator>(), Is.TypeOf<HeadlessFlatDeviceMediator>());
            Assert.That(app.Services.GetRequiredService<IWeatherDataMediator>(), Is.TypeOf<HeadlessWeatherDataMediator>());
        }

        [Test]
        public void The_REST_services_and_their_mediator_aliases_are_one_singleton() {
            // Playbook §8.1: one singleton backs both the REST service and the mediator, so a REST
            // connect is what the sequencer sees. A second registration path would split them.
            AssertSame<ISafetyMonitorService, ISafetyMonitorMediator>();
            AssertSame<ITelescopeService, ITelescopeMediator>();
            AssertSame<IGuiderService, IGuiderMediator>();
            AssertSame<IFocuserService, IFocuserMediator>();
            AssertSame<ICameraService, ICameraMediator>();
            AssertSame<ICameraService, IImagingMediator>();
            AssertSame<IFilterWheelService, IFilterWheelMediator>();
            AssertSame<IRotatorService, IRotatorMediator>();
            AssertSame<ISwitchService, ISwitchMediator>();
            AssertSame<IDomeService, IDomeMediator>();
        }
    }
}
