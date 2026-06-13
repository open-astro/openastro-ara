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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §68.1 equipment-connect version gate (<see cref="EquipmentEndpoints.ConnectGatedAsync"/>): a
    /// bridge below the minimum is refused 503 <c>alpaca_bridge_outdated</c> without connecting; an
    /// acceptable / warn / missing bridge proceeds to the real connect.
    /// </summary>
    [TestFixture]
    public class EquipmentConnectGateTest {

        private static ConnectRequestDto Request(string ip = "192.168.1.50", int port = 11111, bool https = false) =>
            new(new DiscoveredDeviceDto(
                UniqueId: "cam-1", Name: "Camera", Type: DeviceType.Camera,
                HostName: "bridge.local", IpAddress: ip, IpPort: port, AlpacaDeviceNumber: 0, UseHttps: https));

        private static readonly OperationAcceptedDto Accepted =
            new(Guid.NewGuid(), "camera.connect", DateTimeOffset.UnixEpoch, "idem-1");

        [Test]
        public async Task Blocks_with_503_alpaca_bridge_outdated_when_the_bridge_is_too_old() {
            var bridge = new FakeHandshake(new AlpacaBridgeHandshake(AlpacaBridgeStatus.OutdatedBlock, "1.1.0"));
            var connectCalled = false;

            var result = await EquipmentEndpoints.ConnectGatedAsync(
                Request(), bridge,
                () => { connectCalled = true; return Task.FromResult(Accepted); },
                CancellationToken.None);

            var problem = result as ProblemHttpResult;
            Assert.Multiple(() => {
                Assert.That(problem, Is.Not.Null, "a too-old bridge must yield a Problem result");
                Assert.That(problem!.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
                Assert.That(problem.ProblemDetails.Extensions.TryGetValue("code", out var code) ? code : null,
                    Is.EqualTo("alpaca_bridge_outdated"));
                Assert.That(connectCalled, Is.False, "the device must NOT be connected through a too-old bridge");
            });
        }

        [TestCase(AlpacaBridgeStatus.Ok, "1.6.0")]
        [TestCase(AlpacaBridgeStatus.OutdatedWarn, "1.3.0")]
        [TestCase(AlpacaBridgeStatus.Missing, null)]
        public async Task Connects_when_the_bridge_is_acceptable_warn_or_missing(AlpacaBridgeStatus status, string? version) {
            var bridge = new FakeHandshake(new AlpacaBridgeHandshake(status, version));
            var connectCalled = false;

            var result = await EquipmentEndpoints.ConnectGatedAsync(
                Request(), bridge,
                () => { connectCalled = true; return Task.FromResult(Accepted); },
                CancellationToken.None);

            var accepted = result as Accepted<OperationAcceptedDto>;
            Assert.Multiple(() => {
                Assert.That(accepted, Is.Not.Null, $"status {status} should proceed to connect");
                Assert.That(accepted!.Value, Is.SameAs(Accepted));
                Assert.That(connectCalled, Is.True);
            });
        }

        [Test]
        public async Task Probes_the_bridge_uri_built_from_the_device() {
            var bridge = new FakeHandshake(new AlpacaBridgeHandshake(AlpacaBridgeStatus.Ok, "1.6.0"));

            await EquipmentEndpoints.ConnectGatedAsync(
                Request(ip: "192.168.1.50", port: 11111), bridge,
                () => Task.FromResult(Accepted), CancellationToken.None);

            Assert.That(bridge.LastUri, Is.EqualTo(new Uri("http://192.168.1.50:11111")));
        }

        [Test]
        public void BridgeUri_honors_scheme_and_brackets_ipv6() {
            var v4 = EquipmentEndpoints.BridgeUri(Request(ip: "10.0.0.4", port: 11111).Device);
            var https = EquipmentEndpoints.BridgeUri(Request(ip: "10.0.0.4", port: 443, https: true).Device);
            var v6 = EquipmentEndpoints.BridgeUri(Request(ip: "::1", port: 11111).Device);
            var v6Bracketed = EquipmentEndpoints.BridgeUri(Request(ip: "[::1]", port: 11111).Device);

            Assert.Multiple(() => {
                Assert.That(v4, Is.EqualTo(new Uri("http://10.0.0.4:11111")));
                Assert.That(https, Is.EqualTo(new Uri("https://10.0.0.4:443")));
                Assert.That(v6.Host, Is.EqualTo("[::1]"), "a bare IPv6 literal must be bracketed so the Uri parses");
                Assert.That(v6Bracketed.Host, Is.EqualTo("[::1]"), "an already-bracketed IPv6 literal must not be double-bracketed");
            });
        }

        private sealed class FakeHandshake : IAlpacaBridgeHandshakeService {
            private readonly AlpacaBridgeHandshake _result;
            public Uri? LastUri { get; private set; }

            public FakeHandshake(AlpacaBridgeHandshake result) => _result = result;

            public Task<AlpacaBridgeHandshake> HandshakeAsync(Uri bridgeBaseUri, CancellationToken ct) {
                LastUri = bridgeBaseUri;
                return Task.FromResult(_result);
            }
        }
    }
}
