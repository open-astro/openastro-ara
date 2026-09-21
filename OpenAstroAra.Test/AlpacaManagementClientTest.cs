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
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // §63.20 / #1067 — the wizard's Alpaca device-name lookup moved from the Flutter client
    // (direct to the Alpaca host) to the daemon. These pin the parse rule, the edge validation,
    // the best-effort transport contract and the endpoint's status mapping.
    [TestFixture]
    public class AlpacaManagementClientTest {

        private sealed class CannedHandler : HttpMessageHandler {
            public HttpStatusCode Status = HttpStatusCode.OK;
            public string Body = "{}";
            public Exception? Throw;
            public Uri? LastUri;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
                LastUri = request.RequestUri;
                if (Throw is not null) {
                    throw Throw;
                }
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
            }
        }

        private const string Sample = """
            {"Value":[
              {"DeviceName":"ZWO ASI290MM Mini","DeviceType":"Camera","DeviceNumber":1,"UniqueID":"a"},
              {"DeviceName":"AM5N","DeviceType":"Telescope","DeviceNumber":0,"UniqueID":"b"},
              {"DeviceName":"","DeviceType":"Rotator","DeviceNumber":0,"UniqueID":"c"},
              {"DeviceType":"Focuser","DeviceNumber":2},
              "junk"
            ],"ClientTransactionID":0,"ServerTransactionID":7}
            """;

        [Test]
        public void Parse_keys_by_lowercased_type_and_number_and_skips_unusable_entries() {
            var names = AlpacaManagementClient.ParseConfiguredDeviceNames(Sample);
            Assert.That(names, Has.Count.EqualTo(2));
            Assert.That(names["camera/1"], Is.EqualTo("ZWO ASI290MM Mini"));
            Assert.That(names["telescope/0"], Is.EqualTo("AM5N"));
        }

        [Test]
        public void Parse_of_malformed_or_unexpected_json_is_empty() {
            Assert.That(AlpacaManagementClient.ParseConfiguredDeviceNames("not json"), Is.Empty);
            Assert.That(AlpacaManagementClient.ParseConfiguredDeviceNames("[]"), Is.Empty);
            Assert.That(AlpacaManagementClient.ParseConfiguredDeviceNames("{\"Value\":{}}"), Is.Empty);
        }

        [Test]
        public void ManagementUri_validates_at_the_edge() {
            Assert.That(AlpacaManagementClient.ManagementUri("192.168.1.118", 6800).ToString(),
                Is.EqualTo("http://192.168.1.118:6800/management/v1/configureddevices"));
            Assert.That(AlpacaManagementClient.ManagementUri("fe80::1", 11111).Host, Is.EqualTo("[fe80::1]"));
            Assert.Throws<ArgumentException>(() => AlpacaManagementClient.ManagementUri(" ", 6800));
            Assert.Throws<ArgumentOutOfRangeException>(() => AlpacaManagementClient.ManagementUri("rig", 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => AlpacaManagementClient.ManagementUri("rig", 70000));
            // Only a DNS name or IP literal: anything that would retarget the GET is refused.
            Assert.Throws<ArgumentException>(() => AlpacaManagementClient.ManagementUri("10.0.0.5/admin/reboot?x=", 6800));
            Assert.Throws<ArgumentException>(() => AlpacaManagementClient.ManagementUri("user@10.0.0.5", 6800));
            Assert.Throws<ArgumentException>(() => AlpacaManagementClient.ManagementUri("10.0.0.5:80", 6800));
        }

        [Test]
        public async Task Lookup_hits_the_management_api_and_maps_the_names() {
            using var handler = new CannedHandler { Body = Sample };
            using var client = new AlpacaManagementClient(handler: handler);
            var names = await client.GetConfiguredDeviceNamesAsync("rig.local", 6800, CancellationToken.None);
            Assert.That(handler.LastUri!.ToString(), Is.EqualTo("http://rig.local:6800/management/v1/configureddevices"));
            Assert.That(names["camera/1"], Is.EqualTo("ZWO ASI290MM Mini"));
        }

        [Test]
        public async Task Lookup_is_best_effort_on_transport_and_status_failures() {
            using var refused = new CannedHandler { Throw = new HttpRequestException("refused") };
            using var down = new AlpacaManagementClient(handler: refused);
            Assert.That(await down.GetConfiguredDeviceNamesAsync("rig", 6800, CancellationToken.None), Is.Empty);
            using var missing = new CannedHandler { Status = HttpStatusCode.NotFound };
            using var notFound = new AlpacaManagementClient(handler: missing);
            Assert.That(await notFound.GetConfiguredDeviceNamesAsync("rig", 6800, CancellationToken.None), Is.Empty);
        }

        [Test]
        public async Task Endpoint_maps_bad_input_to_400_and_answers_200_with_the_map() {
            using var handler = new CannedHandler { Body = Sample };
            using var client = new AlpacaManagementClient(handler: handler);
            var bad = await EquipmentEndpoints.GetAlpacaDeviceNamesAsync(null, 6800, client, CancellationToken.None);
            Assert.That(((ProblemHttpResult)bad).StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            var badPort = await EquipmentEndpoints.GetAlpacaDeviceNamesAsync("rig", 0, client, CancellationToken.None);
            Assert.That(((ProblemHttpResult)badPort).StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            var ok = await EquipmentEndpoints.GetAlpacaDeviceNamesAsync("rig", 6800, client, CancellationToken.None);
            var body = ((Ok<AlpacaDeviceNamesResponseDto>)ok).Value!;
            Assert.That(body.Names["telescope/0"], Is.EqualTo("AM5N"));
        }
    }
}
