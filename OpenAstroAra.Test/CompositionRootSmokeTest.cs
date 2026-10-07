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
using NUnit.Framework;
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

        [OneTimeSetUp]
        public void BuildTheDaemon() {
            profileDir = Path.Combine(Path.GetTempPath(), $"oara-composition-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDir);
            previousProfileDir = Environment.GetEnvironmentVariable("OPENASTROARA_PROFILE_DIR");
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", profileDir);
            app = Program.BuildApp([]);
        }

        [OneTimeTearDown]
        public async Task TearDownTheDaemon() {
            await app.DisposeAsync();
            // BuildApp installs the §29.9 rolling file sink under the profile dir as Main does;
            // release the file so the directory can go (Main's finally does the same after Run).
            await Serilog.Log.CloseAndFlushAsync();
            Environment.SetEnvironmentVariable("OPENASTROARA_PROFILE_DIR", previousProfileDir);
            // The pooled SQLite handle would keep the catalog file open past DisposeAsync on
            // Windows and leave the temp dir behind.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try {
                Directory.Delete(profileDir, recursive: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
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

        [Test] public void SafetyMonitor_mediator_is_the_live_service() => AssertLive<ISafetyMonitorMediator, SafetyMonitorService>();
        [Test] public void Telescope_mediator_is_the_live_service() => AssertLive<ITelescopeMediator, TelescopeService>();
        [Test] public void Guider_mediator_is_the_live_service() => AssertLive<IGuiderMediator, GuiderService>();
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
