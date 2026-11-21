#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using OpenAstroAra.Test.PolarAlign;
using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // #1971 — the indoor loop: the real routine, the real mount and Polar Aligner services, and the
    // PolarAlign Alpaca simulator over real HTTP. The simulator's virtual mount reports where a misaligned
    // mount really points, so its reported pointing, read from the simulator, stands in for the camera and the plate solve; nothing
    // here shares code with the simulator's own sky geometry. Run by hand with the simulator listening:
    //   dotnet test OpenAstroAra.Test --filter "FullyQualifiedName~Against_the_PolarAlign_simulator"
    public partial class PolarAlignServiceTest {

        private const string SimulatorUrl = "http://localhost:11112";

        /// <summary>A "plate solve" that asks the simulator where its mount points right now (hours and degrees
        /// of date), once any slew has ended: the daemon's own cached mount position is a poll old.</summary>
        private sealed class SimulatedSkySolver(HttpClient http) : IPolarAlignFrameSolver {
            public Task<double?> MeasureFocalLengthAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) =>
                Task.FromResult<double?>(null);
            public async Task<PolarAlignSolveOutcome> SolveAsync(string fitsPath, int binning, double? hintRa, double? hintDec, CancellationToken ct) {
                while ((await ValueAsync("slewing", ct).ConfigureAwait(false)).GetBoolean()) {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
                var raHours = (await ValueAsync("rightascension", ct).ConfigureAwait(false)).GetDouble();
                var decDeg = (await ValueAsync("declination", ct).ConfigureAwait(false)).GetDouble();
                return new PolarAlignSolveOutcome(true, raHours * 15.0, decDeg);
            }
            private async Task<JsonElement> ValueAsync(string member, CancellationToken ct) {
                var json = await http.GetStringAsync(new Uri($"{SimulatorUrl}/api/v1/telescope/0/{member}?ClientID=1&ClientTransactionID=1"), ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.GetProperty("Value").Clone();
            }
        }

        private static async Task<JsonElement> SimulatorStateAsync(HttpClient http) {
            using var doc = JsonDocument.Parse(await http.GetStringAsync(new Uri(SimulatorUrl + "/sim/state")).ConfigureAwait(false));
            return doc.RootElement.Clone();
        }

        private static async Task PutMountAsync(HttpClient http, string member, string key, string value, string? key2 = null, string? value2 = null) {
            var fields = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>> {
                new(key, value), new("ClientID", "1"), new("ClientTransactionID", "1") };
            if (key2 is not null) {
                fields.Add(new(key2, value2!));
            }
            using var form = new FormUrlEncodedContent(fields);
            using var reply = await http.PutAsync(new Uri($"{SimulatorUrl}/api/v1/telescope/0/{member}"), form).ConfigureAwait(false);
            reply.EnsureSuccessStatusCode();
        }

        private static Task SlewSimulatorAsync(HttpClient http, double raHours, double decDeg) =>
            PutMountAsync(http, "slewtocoordinatesasync", "RightAscension", raHours.ToString(CultureInfo.InvariantCulture),
                "Declination", decDeg.ToString(CultureInfo.InvariantCulture));

        // A made-up northern and a made-up southern site; the simulator's own site is put back afterwards.
        [TestCase(40.0, 25.0, -12.0, 85.0)]
        [TestCase(-40.0, 25.0, -12.0, 85.0)]
        [TestCase(-40.0, -18.0, 9.0, 85.0)]
        [TestCase(40.0, 25.0, -12.0, 60.0)]
        [TestCase(-40.0, 25.0, -12.0, 60.0)]
        [TestCase(-40.0, -18.0, 9.0, 60.0)]
        [Category("Integration")]
        [Explicit("needs the PolarAlign Alpaca simulator listening at localhost:11112")]
        public async Task Against_the_PolarAlign_simulator_the_routine_drives_the_error_inside_the_tolerance(
                double latitudeDeg, double azErrorArcmin, double altErrorArcmin, double startDecMagnitudeDeg) {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            JsonElement before;
            try {
                before = await SimulatorStateAsync(http).ConfigureAwait(false);
            } catch (HttpRequestException) {
                Assert.Ignore("the PolarAlign Alpaca simulator is not listening at " + SimulatorUrl);
                return;
            }
            var simLatitude = before.GetProperty("mount").GetProperty("latitude").GetDouble();
            var simLongitude = before.GetProperty("mount").GetProperty("longitude").GetDouble();
            const double longitudeDeg = 10.0;

            // The aligner over real Alpaca: the simulator's Switch through the daemon's own services.
            using var telescope = new TelescopeService();
            using var switches = new SwitchService();
            var alignerDevice = new DiscoveredDeviceDto("4c8a1f0e-6b1d-4f6a-9a38-0000000000aa", "Simulated aligner", DeviceType.Switch,
                "localhost", "127.0.0.1", 11112, AlpacaDeviceNumber: 0, UseHttps: false);
            // The mount as the routine sees a real one: it reports its own (encoder) coordinates, which are
            // what it was last sent to. The simulator's Telescope reports the true sky pointing instead, for a
            // camera simulator; here that true pointing is the "solve". Every slew and tracking write is
            // forwarded to the simulator.
            var south = latitudeDeg < 0;
            var mount = new Moq.Mock<ITelescopeMediator>();
            var tracking = true;
            // 5° or 30° from the pole: the live readout maps the camera's alt/az shift back to the axis's at any
            // distance from the pole (#1983).
            var encoder = new OpenAstroAra.Astrometry.Coordinates(0.0, south ? -startDecMagnitudeDeg : startDecMagnitudeDeg, OpenAstroAra.Astrometry.Epoch.JNOW,
                OpenAstroAra.Astrometry.Coordinates.RAType.Degrees);
            // #2064 — the mock's own pose: RA 0 at LST 0 h, on the meridian, the east side of the pier counterweight down.
            mount.Setup(m => m.GetInfo()).Returns(() => new OpenAstroAra.Equipment.Equipment.MyTelescope.TelescopeInfo {
                Connected = true, SiderealTime = 0.0, TrackingEnabled = tracking, SideOfPier = OpenAstroAra.Core.Enums.PierSide.pierEast });
            mount.Setup(m => m.GetCurrentPosition()).Returns(() => encoder);
            mount.Setup(m => m.SlewToCoordinatesAsync(Moq.It.IsAny<OpenAstroAra.Astrometry.Coordinates>(), Moq.It.IsAny<CancellationToken>()))
                .Returns<OpenAstroAra.Astrometry.Coordinates, CancellationToken>(async (target, _) => {
                    await SlewSimulatorAsync(http, target.RADegrees / 15.0, target.Dec).ConfigureAwait(false);
                    encoder = target;
                    return true;
                });
            // Polar alignment turns with the motors (MoveAxis), not GoTos: each command steps the encoder 0.25°, and
            // the stop sends the simulator where the encoder says, as a slew there would.
            var moving = false;
            mount.Setup(m => m.MoveAxis(Moq.It.IsAny<OpenAstroAra.Core.Enums.TelescopeAxes>(), Moq.It.IsAny<double>()))
                .Callback<OpenAstroAra.Core.Enums.TelescopeAxes, double>((axis, rate) => {
                    if (rate == 0) {
                        if (moving) {
                            moving = false;
#pragma warning disable CA2025 // the slew completes synchronously here, inside the test that owns the client
                            SlewSimulatorAsync(http, encoder.RADegrees / 15.0, encoder.Dec).GetAwaiter().GetResult();
#pragma warning restore CA2025
                        }
                        return;
                    }
                    moving = true;
                    var step = Math.Sign(rate) * 0.25;
                    encoder = axis == OpenAstroAra.Core.Enums.TelescopeAxes.Primary
                        ? new OpenAstroAra.Astrometry.Coordinates((encoder.RADegrees + step + 360.0) % 360.0, encoder.Dec,
                            OpenAstroAra.Astrometry.Epoch.JNOW, OpenAstroAra.Astrometry.Coordinates.RAType.Degrees)
                        : new OpenAstroAra.Astrometry.Coordinates(encoder.RADegrees, encoder.Dec >= 0 ? encoder.Dec - step : encoder.Dec + step,
                            OpenAstroAra.Astrometry.Epoch.JNOW, OpenAstroAra.Astrometry.Coordinates.RAType.Degrees);
                });
            mount.Setup(m => m.WaitForSlew(Moq.It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            mount.Setup(m => m.SetTrackingEnabled(Moq.It.IsAny<bool>())).Returns<bool>(on => {
                using var form = new FormUrlEncodedContent([new("Tracking", on ? "true" : "false"), new("ClientID", "1"), new("ClientTransactionID", "1")]);
                using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(SimulatorUrl + "/api/v1/telescope/0/tracking")) { Content = form };
                using var reply = http.Send(request);
                reply.EnsureSuccessStatusCode();
                tracking = on;
                return true;
            });
            try {
                await PutMountAsync(http, "connected", "Connected", "true").ConfigureAwait(false);
                await PutMountAsync(http, "sitelatitude", "SiteLatitude", latitudeDeg.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await PutMountAsync(http, "sitelongitude", "SiteLongitude", longitudeDeg.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await PutMountAsync(http, "tracking", "Tracking", "true").ConfigureAwait(false);
                await SlewSimulatorAsync(http, encoder.RADegrees / 15.0, encoder.Dec).ConfigureAwait(false);
                await switches.ConnectAsync(new ConnectRequestDto(alignerDevice), null, CancellationToken.None).ConfigureAwait(false);
                var aligners = new PolarAlignerService(switches, telescope);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (!aligners.IsPresent) {
                    Assert.That(DateTime.UtcNow, Is.LessThan(deadline), "the simulated aligner did not connect as a Polar Aligner");
                    await Task.Delay(50).ConfigureAwait(false);
                }
                using (var error = new StringContent(
                        FormattableString.Invariant($"{{\"az\":{azErrorArcmin},\"alt\":{altErrorArcmin}}}"), Encoding.UTF8, "application/json")) {
                    (await http.PostAsync(new Uri(SimulatorUrl + "/sim/error"), error).ConfigureAwait(false)).EnsureSuccessStatusCode();
                }

                var store = new InMemoryProfileStore();
                store.PutSiteSettings(store.GetSiteSettings() with { LatitudeDeg = latitudeDeg, LongitudeDeg = longitudeDeg });
                store.PutPolarAlignSettings(new PolarAlignSettingsDto(
                    ExposureSeconds: 0.05, LoopCadenceMs: 200, SettleSeconds: 0.5, SeedRotationDeg: 30.0).Confirmed());
                var ws = new WsRecorder();
                using var svc = new PolarAlignService(NewFake(), Microsoft.Extensions.Logging.Abstractions.NullLogger<PolarAlignService>.Instance,
                    new SimulatedSkySolver(http), mount.Object, MediatorMotionControl.For(mount.Object), store, ws, log: null, new FakeFrameFetcher(), aligner: aligners) {
                    AxisMovePoll = TimeSpan.FromMilliseconds(5),
                    AxisSettle = TimeSpan.Zero,
                };

                await svc.StartConfirmedAsync(PolarAlignStages.Fine, null, CancellationToken.None).ConfigureAwait(false);
                await ws.WhenAsync(events => Array.Exists(events, e => e.Type == WsEventCatalog.PolarAlignCorrection
                        && e.Payload.GetProperty("state").GetString() is PolarAlignCorrectionStates.Converged or PolarAlignCorrectionStates.Stopped)
                    || Array.Exists(events, e => e.Type == WsEventCatalog.PolarAlignError))
                    .WaitAsync(TimeSpan.FromMinutes(3)).ConfigureAwait(false);

                var status = await svc.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
                var after = (await SimulatorStateAsync(http).ConfigureAwait(false)).GetProperty("error");
                await TestContext.Out.WriteLineAsync(FormattableString.Invariant(
                    $"lat {latitudeDeg}: simulator error set az {azErrorArcmin}′ alt {altErrorArcmin}′; Ara ended {status.Correction?.State} ({status.Correction?.Cause}) after {status.Correction?.Moves} moves, reading {status.CurrentErrorArcmin:F2}′; simulator now az {after.GetProperty("az").GetDouble():F2}′ alt {after.GetProperty("alt").GetDouble():F2}′ total {after.GetProperty("total").GetDouble():F2}′")).ConfigureAwait(false);
                await svc.StopAsync(null, CancellationToken.None).ConfigureAwait(false);

                Assert.That(status.Correction?.State, Is.EqualTo(PolarAlignCorrectionStates.Converged), $"stopped: {status.Correction?.Cause} {status.Correction?.DeviceText}");
                Assert.That(status.CurrentErrorArcmin, Is.LessThanOrEqualTo(1.0 + 0.3), "Ara's own reading, the 1′ tolerance plus the simulator's noise");
                // The simulator's truth has no refraction; Ara aims at the refracted pole, about 1′ higher at 40°.
                var slack = 1.0 + PolarAlignGeometry.BennettRefractionArcmin(Math.Abs(latitudeDeg)) + 0.5;
                Assert.That(after.GetProperty("total").GetDouble(), Is.LessThanOrEqualTo(slack), "the simulator's own measure of the misalignment left");
            } finally {
                await switches.DisconnectAsync(alignerDevice.UniqueId, null, CancellationToken.None).ConfigureAwait(false);
                await PutMountAsync(http, "sitelatitude", "SiteLatitude", simLatitude.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await PutMountAsync(http, "sitelongitude", "SiteLongitude", simLongitude.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
                var was = before.GetProperty("error");
                using (var error = new StringContent(FormattableString.Invariant(
                        $"{{\"az\":{was.GetProperty("az").GetDouble()},\"alt\":{was.GetProperty("alt").GetDouble()}}}"), Encoding.UTF8, "application/json")) {
                    (await http.PostAsync(new Uri(SimulatorUrl + "/sim/error"), error).ConfigureAwait(false)).EnsureSuccessStatusCode();
                }
                await PutMountAsync(http, "connected", "Connected", "false").ConfigureAwait(false);
            }
        }
    }
}
