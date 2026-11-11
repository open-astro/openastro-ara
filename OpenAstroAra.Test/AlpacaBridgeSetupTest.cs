#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // #1580 — the daemon adds devices to a fresh AlpacaBridge for the setup wizard. A canned AlpacaBridge
    // HTTP handler stands in for the bridge (made-up host 192.0.2.10, never the rig's).
    [TestFixture]
    public class AlpacaBridgeSetupTest {

        private const string Host = "192.0.2.10";
        private static readonly string[] CameraThenTelescope = { "camera", "telescope" };
        private static readonly string[] OnlyZwo = { "zwo" };

        private sealed class Bridge : HttpMessageHandler {
            public Func<HttpRequestMessage, string?, HttpResponseMessage> Reply =
                (_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
            public Exception? Throw;
            public readonly List<(HttpMethod Method, Uri Uri, string? Body)> Seen = new();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
                Seen.Add((request.Method, request.RequestUri!, body));
                if (Throw is not null) {
                    throw Throw;
                }
                return Reply(request, body);
            }
        }

        private sealed class ListLogger : ILogger<AlpacaManagementClient> {
            public readonly List<string> Lines = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                Lines.Add(formatter(state, exception) + " " + exception);
        }

        private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();

        // Shaped like AlpacaBridge's devicecatalog: the ZWO camera, a driver left out of the build, a mount.
        private const string Catalog = """
            {"Value":[
              {"vendor":"zwo","deviceType":"camera","displayName":"ZWO ASI Camera","vendorLabel":"ZWO","available":true,"buildOption":"ALPACACORE_ENABLE_ZWO",
               "fields":[
                 {"key":"cameraIndex","type":"int","default":-1,"required":false,"secret":false,"role":"EnumerationIndex"},
                 {"key":"serialNumber","type":"string","default":"","required":false,"secret":false,"role":"Plain"},
                 {"key":"usbBandwidth","type":"int","default":40,"required":false,"secret":false,"role":"Plain","range":{"min":0,"max":100}}]},
              {"vendor":"qhy","deviceType":"camera","displayName":"QHY Camera","available":false,"buildOption":"ALPACACORE_ENABLE_QHY","fields":[]},
              {"vendor":"onstep","deviceType":"telescope","displayName":"OnStep","available":true,"buildOption":"x",
               "fields":[{"key":"password","type":"string","required":false,"secret":true,"role":"Secret"},
                         {"key":"host","type":"string","required":true,"secret":false,"role":"Host","appliesWhen":{"key":"connectionType","value":"network"}}]}
            ],"ErrorNumber":0,"ErrorMessage":""}
            """;

        [Test]
        public async Task Catalog_drops_unavailable_drivers_groups_by_type_and_passes_field_schemas_through() {
            using var bridge = new Bridge { Reply = (_, _) => Json(Catalog) };
            using var client = new AlpacaManagementClient(handler: bridge);

            var result = await AlpacaBridgeSetupEndpoints.GetCatalogAsync(Host, 6800, client, CancellationToken.None);

            var dto = ((Ok<AlpacaBridgeCatalogResponseDto>)result).Value!;
            Assert.That(dto.DeviceTypes.Select(t => t.DeviceType), Is.EqualTo(CameraThenTelescope));
            var cameras = dto.DeviceTypes[0].Drivers;
            Assert.That(cameras.Select(d => d.GetProperty("vendor").GetString()), Is.EqualTo(OnlyZwo), "qhy is not built in");
            var usb = cameras[0].GetProperty("fields")[2];
            Assert.That(usb.GetProperty("range").GetProperty("max").GetInt32(), Is.EqualTo(100), "schema passed through as sent");
            var host = dto.DeviceTypes[1].Drivers[0].GetProperty("fields")[1];
            Assert.That(host.GetProperty("appliesWhen").GetProperty("value").GetString(), Is.EqualTo("network"));
            Assert.That(bridge.Seen.Single().Uri.AbsoluteUri, Is.EqualTo($"http://{Host}:6800/management/v1/devicecatalog"));
        }

        [Test]
        public async Task A_server_without_the_catalog_answers_an_empty_list() {
            using var bridge = new Bridge { Reply = (_, _) => Json("{}", HttpStatusCode.NotFound) };
            using var client = new AlpacaManagementClient(handler: bridge);

            var result = await AlpacaBridgeSetupEndpoints.GetCatalogAsync(Host, 6800, client, CancellationToken.None);

            Assert.That(((Ok<AlpacaBridgeCatalogResponseDto>)result).Value!.DeviceTypes, Is.Empty);
        }

        [Test]
        public async Task Attached_cameras_are_listed_and_a_vendor_with_no_lister_is_empty_not_an_error() {
            using var bridge = new Bridge {
                Reply = (req, _) => req.RequestUri!.Query.Contains("vendor=zwo", StringComparison.Ordinal)
                    ? Json("""{"Value":[{"Index":0,"Name":"ZWO ASI294MC Pro","Serial":"1A2B"},{"Index":1,"Name":"ZWO ASI120MM","Serial":"3C4D"}],"ErrorNumber":0}""")
                    : Json("""{"Value":null,"ErrorNumber":1025,"ErrorMessage":"No camera listing for vendor 'onstep' in this build"}"""),
            };
            using var client = new AlpacaManagementClient(handler: bridge);

            var zwo = ((Ok<AlpacaBridgeCamerasResponseDto>)await AlpacaBridgeSetupEndpoints.GetCamerasAsync(Host, 6800, "zwo", client, CancellationToken.None)).Value!;
            var none = ((Ok<AlpacaBridgeCamerasResponseDto>)await AlpacaBridgeSetupEndpoints.GetCamerasAsync(Host, 6800, "onstep", client, CancellationToken.None)).Value!;

            Assert.That(zwo.Cameras.Select(c => (c.Index, c.Name, c.Serial)),
                Is.EqualTo(new[] { (0, "ZWO ASI294MC Pro", "1A2B"), (1, "ZWO ASI120MM", "3C4D") }), "both cameras, told apart by serial");
            Assert.That(none.Cameras, Is.Empty);
        }

        [Test]
        public async Task Add_forwards_the_exact_body_to_configuredevice() {
            using var bridge = new Bridge { Reply = (_, _) => Json("""{"Value":"Device registered successfully","ErrorNumber":0,"ErrorMessage":""}""") };
            using var client = new AlpacaManagementClient(handler: bridge);
            const string raw = """{"vendor":"zwo","deviceType":"camera","serialNumber":"1A2B","cameraName":"ZWO ASI294MC Pro","usbBandwidth":60}""";

            var result = await AlpacaBridgeSetupEndpoints.AddDeviceAsync(Host, 6800, Body(raw), client, CancellationToken.None);

            Assert.That(result, Is.TypeOf<Ok<AlpacaBridgeAddDeviceResponseDto>>());
            var (method, uri, body) = bridge.Seen.Single();
            Assert.That(method, Is.EqualTo(HttpMethod.Post));
            Assert.That(uri.AbsoluteUri, Is.EqualTo($"http://{Host}:6800/management/v1/configuredevice"));
            Assert.That(body, Is.EqualTo(raw));
        }

        [Test]
        public async Task A_zero_error_number_is_success_and_carries_the_warning() {
            using var bridge = new Bridge {
                Reply = (_, _) => Json("""{"Value":"Device registered successfully. Warning: no camera matched index 3","ErrorNumber":0,"ErrorMessage":""}"""),
            };
            using var client = new AlpacaManagementClient(handler: bridge);

            var result = await AlpacaBridgeSetupEndpoints.AddDeviceAsync(Host, 6800,
                Body("""{"vendor":"zwo","deviceType":"camera"}"""), client, CancellationToken.None);

            var dto = ((Ok<AlpacaBridgeAddDeviceResponseDto>)result).Value!;
            Assert.That(dto.Warning, Is.EqualTo("no camera matched index 3"));
            Assert.That(dto.Message, Does.StartWith("Device registered successfully"));
        }

        [Test]
        public async Task A_non_zero_error_number_is_a_problem_with_the_bridges_message() {
            using var bridge = new Bridge {
                Reply = (_, _) => Json("""{"Value":null,"ErrorNumber":1025,"ErrorMessage":"gpioChip '/dev/gpiochip9' is not in the board allowlist"}""", HttpStatusCode.BadRequest),
            };
            using var client = new AlpacaManagementClient(handler: bridge);

            var result = await AlpacaBridgeSetupEndpoints.AddDeviceAsync(Host, 6800,
                Body("""{"vendor":"zwo","deviceType":"switch","gpioChip":"/dev/gpiochip9"}"""), client, CancellationToken.None);

            var problem = (ProblemHttpResult)result;
            Assert.That(problem.StatusCode, Is.EqualTo(StatusCodes.Status422UnprocessableEntity));
            Assert.That(problem.ProblemDetails.Detail, Is.EqualTo("gpioChip '/dev/gpiochip9' is not in the board allowlist"));
        }

        [Test]
        public async Task A_refused_connection_and_a_timeout_say_the_device_may_or_may_not_have_been_added() {
            foreach (Exception failure in new Exception[] { new HttpRequestException("refused"), new TaskCanceledException("timeout") }) {
                using var bridge = new Bridge { Throw = failure };
                using var client = new AlpacaManagementClient(handler: bridge);

                var result = await AlpacaBridgeSetupEndpoints.AddDeviceAsync(Host, 6800,
                    Body("""{"vendor":"zwo","deviceType":"camera"}"""), client, CancellationToken.None);

                var problem = (ProblemHttpResult)result;
                Assert.That(problem.StatusCode, Is.EqualTo(StatusCodes.Status502BadGateway), failure.GetType().Name);
                Assert.That(problem.ProblemDetails.Type, Is.EqualTo("https://openastro.net/errors/alpacabridge-unconfirmed"));
                Assert.That(problem.ProblemDetails.Detail, Does.Contain("may or may not have been added"));
            }
        }

        [Test]
        public async Task A_bad_host_or_port_is_a_400_before_anything_is_dialled() {
            using var bridge = new Bridge();
            using var client = new AlpacaManagementClient(handler: bridge);
            var body = Body("""{"vendor":"zwo","deviceType":"camera"}""");

            var results = new[] {
                await AlpacaBridgeSetupEndpoints.GetCatalogAsync("", 6800, client, CancellationToken.None),
                await AlpacaBridgeSetupEndpoints.GetCamerasAsync(Host, 0, "zwo", client, CancellationToken.None),
                await AlpacaBridgeSetupEndpoints.GetCamerasAsync(Host, 6800, "zwo&x=1", client, CancellationToken.None),
                await AlpacaBridgeSetupEndpoints.AddDeviceAsync("rig/evil", 6800, body, client, CancellationToken.None),
                await AlpacaBridgeSetupEndpoints.AddDeviceAsync(Host, 70000, body, client, CancellationToken.None),
                await AlpacaBridgeSetupEndpoints.AddDeviceAsync(Host, 6800, Body("""{"deviceType":"camera"}"""), client, CancellationToken.None),
            };

            Assert.That(results.Select(r => ((ProblemHttpResult)r).StatusCode), Is.All.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(bridge.Seen, Is.Empty);
        }

        [Test]
        public async Task A_secret_field_value_appears_in_no_log_and_no_problem() {
            const string secret = "hunter2-not-a-real-key";
            var log = new ListLogger();
            using var echo = new Bridge {
                // A bridge that quotes the value back in its refusal.
                Reply = (_, body) => Json($$"""{"Value":null,"ErrorNumber":1025,"ErrorMessage":"bad password {{secret}} for host"}""", HttpStatusCode.BadRequest),
            };
            using var refusedClient = new AlpacaManagementClient(log, echo);
            var request = Body($$"""{"vendor":"onstep","deviceType":"telescope","password":"{{secret}}"}""");

            var refused = (ProblemHttpResult)await AlpacaBridgeSetupEndpoints.AddDeviceAsync(Host, 6800, request, refusedClient, CancellationToken.None);

            using var down = new Bridge { Throw = new HttpRequestException($"connect failed, body was {secret}") };
            using var downClient = new AlpacaManagementClient(log, down);
            var unreachable = (ProblemHttpResult)await AlpacaBridgeSetupEndpoints.AddDeviceAsync(Host, 6800, request, downClient, CancellationToken.None);

            var everything = JsonSerializer.Serialize(refused.ProblemDetails) + JsonSerializer.Serialize(unreachable.ProblemDetails)
                + string.Join("\n", log.Lines);
            Assert.That(everything, Does.Not.Contain(secret));
            Assert.That(refused.ProblemDetails.Detail, Does.Contain("bad password"), "the rest of the bridge's message still shows");
        }

        [Test]
        public void A_stale_session_is_refused_on_the_add_route() {
            Assert.That(SessionGuard.ShouldReject("POST", AlpacaBridgeSetupEndpoints.AddPath,
                Guid.NewGuid().ToString(), _ => true), Is.True);
        }

        [Test]
        public void Add_waits_longer_than_a_read() {
            Assert.That(AlpacaManagementClient.AddTimeout, Is.GreaterThan(TimeSpan.FromSeconds(3)));
        }
    }
}