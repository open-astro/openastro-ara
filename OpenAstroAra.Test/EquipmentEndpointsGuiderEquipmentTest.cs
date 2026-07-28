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
using Moq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.17 (PR 1) — the daemon-side Alpaca discovery endpoint's error-to-status mapping: a successful sweep
    /// is 200 with the servers, a bad range is 400, a disconnected guider is 409 (typed), and a daemon
    /// rejection (e.g. no Alpaca support in the build) is 422. The service is mocked so the mapping is covered
    /// without an ASP.NET host or a live guider.
    /// </summary>
    [TestFixture]
    public class EquipmentEndpointsGuiderEquipmentTest {

        private static readonly DiscoverAlpacaServersRequestDto Request = new();

        [Test]
        public async Task Discover_returns_200_with_servers_on_success() {
            var discovered = new GuiderAlpacaDiscoveryDto(["192.168.1.154:6800"]);
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.DiscoverAlpacaServersAsync(Request, It.IsAny<CancellationToken>()))
                .ReturnsAsync(discovered);

            var result = await EquipmentEndpoints.DiscoverAlpacaServersAsync(Request, svc.Object, CancellationToken.None);

            var typed = result as Ok<GuiderAlpacaDiscoveryDto>;
            Assert.That(typed, Is.Not.Null);
            Assert.That(typed!.Value, Is.SameAs(discovered));
        }

        [Test]
        public async Task Discover_maps_validation_ArgumentException_to_400() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.DiscoverAlpacaServersAsync(It.IsAny<DiscoverAlpacaServersRequestDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentOutOfRangeException("numQueries", 0, "num_queries must be 1..20."));

            var result = await EquipmentEndpoints.DiscoverAlpacaServersAsync(Request, svc.Object, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status400BadRequest));
        }

        [Test]
        public async Task Discover_maps_not_connected_InvalidOperation_to_typed_409() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.DiscoverAlpacaServersAsync(It.IsAny<DiscoverAlpacaServersRequestDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("guider is not connected"));

            var result = await EquipmentEndpoints.DiscoverAlpacaServersAsync(Request, svc.Object, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
                Assert.That(ProblemTypeOf(result), Is.EqualTo(EquipmentEndpoints.GuiderNotConnectedProblemType));
            });
        }

        [Test]
        public async Task Discover_maps_daemon_GuiderRpcException_to_422() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.DiscoverAlpacaServersAsync(It.IsAny<DiscoverAlpacaServersRequestDto>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new GuiderRpcException("discover_alpaca_servers", 1, "alpaca support not available"));

            var result = await EquipmentEndpoints.DiscoverAlpacaServersAsync(Request, svc.Object, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status422UnprocessableEntity));
        }

        // ── §63.17 PR 2: on-demand profile push ──

        [Test]
        public async Task ProfilePush_returns_202_accepted_on_success() {
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "guider.profile.push", DateTimeOffset.UtcNow, "idem-2");
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.PushGuiderProfileAsync("idem-2", It.IsAny<CancellationToken>()))
                .ReturnsAsync(accepted);

            var result = await EquipmentEndpoints.PushGuiderProfileAsync("idem-2", svc.Object, CancellationToken.None);

            var typed = result as Accepted<OperationAcceptedDto>;
            Assert.That(typed, Is.Not.Null);
            Assert.That(typed!.Value, Is.SameAs(accepted));
        }

        [Test]
        public async Task ProfilePush_maps_not_connected_InvalidOperation_to_typed_409() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.PushGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("guider is not connected"));

            var result = await EquipmentEndpoints.PushGuiderProfileAsync(null, svc.Object, CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
                Assert.That(ProblemTypeOf(result), Is.EqualTo(EquipmentEndpoints.GuiderNotConnectedProblemType));
            });
        }

        [Test]
        public async Task ProfilePush_maps_reconnect_failure_GuiderRpcException_to_422() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.PushGuiderProfileAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new GuiderRpcException("set_connected", -1, "profile pushed, but the daemon could not reconnect its equipment"));

            var result = await EquipmentEndpoints.PushGuiderProfileAsync(null, svc.Object, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status422UnprocessableEntity));
        }

        private static int? ProblemStatusOf(IResult result) => (result as ProblemHttpResult)?.StatusCode;

        private static string? ProblemTypeOf(IResult result) => (result as ProblemHttpResult)?.ProblemDetails.Type;
    }
}
