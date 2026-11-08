#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §45 — the polar-align start endpoint's error-to-status mapping. The DI swap to the real
    /// <see cref="PolarAlignService"/> made a not-connected Start a live throwing path, so — like the sibling
    /// guider endpoints — a not-connected guider maps to 409 (typed) and a daemon-rejected lease to 422,
    /// rather than a raw 500. The service is mocked so the mapping is covered without a host or live guider.
    /// </summary>
    [TestFixture]
    public class EquipmentEndpointsPolarAlignTest {

        [Test]
        public async Task Start_returns_202_accepted_on_success() {
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "polar-align.start", DateTimeOffset.UtcNow, "idem-1");
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.StartAsync(null, "idem-1", It.IsAny<CancellationToken>())).ReturnsAsync(accepted);

            var result = await EquipmentEndpoints.PolarAlignStartAsync(svc.Object, null, "idem-1", CancellationToken.None);

            var typed = result as Accepted<OperationAcceptedDto>;
            Assert.That(typed, Is.Not.Null);
            Assert.That(typed!.Value, Is.SameAs(accepted));
        }

        [Test]
        public async Task Start_maps_not_connected_InvalidOperation_to_409_typed() {
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.StartAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("guider is not connected"));

            var result = await EquipmentEndpoints.PolarAlignStartAsync(svc.Object, null, null, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(ProblemTypeOf(result), Is.EqualTo(EquipmentEndpoints.GuiderNotConnectedProblemType));
        }

        [Test]
        public async Task Start_maps_daemon_rejection_GuiderRpcException_to_422() {
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.StartAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new GuiderRpcException("set_pa_session", 1, "starting rejected while guiding"));

            var result = await EquipmentEndpoints.PolarAlignStartAsync(svc.Object, null, null, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status422UnprocessableEntity));
        }

        [TestCase("corse")]
        [TestCase("")]
        [TestCase("Fine")]
        public async Task Start_with_an_unknown_stage_is_400_and_never_reaches_the_service(string stage) {
            // A typo in the stage is the caller's mistake, not a busy routine: 400 before the service is
            // asked, so its ArgumentException never becomes a 500.
            var svc = new Mock<IPolarAlignService>(MockBehavior.Strict);

            var result = await EquipmentEndpoints.PolarAlignStartAsync(svc.Object, stage, null, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status400BadRequest));
            svc.Verify(s => s.StartAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestCase(PolarAlignStages.Coarse)]
        [TestCase(PolarAlignStages.Fine)]
        [TestCase(PolarAlignStages.Check)]
        public async Task Start_passes_a_known_stage_to_the_service(string stage) {
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "polar-align.start", DateTimeOffset.UtcNow, null);
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.StartAsync(stage, null, It.IsAny<CancellationToken>())).ReturnsAsync(accepted);

            var result = await EquipmentEndpoints.PolarAlignStartAsync(svc.Object, stage, null, CancellationToken.None);

            Assert.That((result as Accepted<OperationAcceptedDto>)?.Value, Is.SameAs(accepted));
        }

        [Test]
        public async Task Next_returns_202_accepted_on_success() {
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "polar-align.next", DateTimeOffset.UtcNow, "idem-2");
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.NextAsync("idem-2", It.IsAny<CancellationToken>())).ReturnsAsync(accepted);

            var result = await EquipmentEndpoints.PolarAlignNextAsync(svc.Object, "idem-2", CancellationToken.None);

            Assert.That((result as Accepted<OperationAcceptedDto>)?.Value, Is.SameAs(accepted));
        }

        [Test]
        public async Task Next_with_nothing_to_advance_maps_to_409() {
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.NextAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("polar alignment has nothing to advance — it is not waiting on you"));

            var result = await EquipmentEndpoints.PolarAlignNextAsync(svc.Object, null, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
        }

        [Test]
        public async Task Capture_outside_the_adjust_phase_maps_to_409() {
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.RequestCaptureAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("polar alignment is not in the adjust phase"));

            var result = await EquipmentEndpoints.PolarAlignCaptureAsync(svc.Object, null, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
        }

        private static int? ProblemStatusOf(IResult result) => (result as ProblemHttpResult)?.StatusCode;

        private static string? ProblemTypeOf(IResult result) => (result as ProblemHttpResult)?.ProblemDetails.Type;
    }
}
