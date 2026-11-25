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
using OpenAstroAra.Server.Contracts.Refusals;
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
        public async Task Start_lets_a_declared_refusal_reach_the_refusal_handler() {
            // #2070 — RefusalException is an InvalidOperationException: the not-connected catch must not turn
            // the unverified-mount refusal into a "guider not connected" 409 with no reason.
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.StartAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(TelescopeService.PositionUnverifiedRefusal(MountPoseTrust.AtHomeOnConnect));

            var thrown = await Assert.ThrowsAsync<RefusalException>(
                () => EquipmentEndpoints.PolarAlignStartAsync(svc.Object, null, null, CancellationToken.None));

            Assert.That(thrown!.Cause, Is.EqualTo(RefusalCatalog.MountPositionUnverified));
        }

        [Test]
        public async Task Start_maps_daemon_rejection_GuiderRpcException_to_422() {
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.StartAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new GuiderRpcException("set_pa_session", 1, "starting rejected while guiding"));

            var result = await EquipmentEndpoints.PolarAlignStartAsync(svc.Object, null, null, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status422UnprocessableEntity));
        }

        [TestCase("coarse")]
        [TestCase("")]
        [TestCase("Fine")]
        public async Task Start_with_an_unknown_stage_is_400_and_never_reaches_the_service(string stage) {
            // A typo in the stage is the caller's mistake, not a busy routine: 400 before the service is
            // asked, so its ArgumentException never becomes a 500.
            var svc = new Mock<IPolarAlignService>(MockBehavior.Strict);

            var result = await EquipmentEndpoints.PolarAlignStartAsync(svc.Object, new PolarAlignStartRequestDto(stage), null, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status400BadRequest));
            svc.Verify(s => s.StartAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [TestCase(PolarAlignStages.Fine)]
        [TestCase(PolarAlignStages.Check)]
        public async Task Start_passes_a_known_stage_to_the_service(string stage) {
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "polar-align.start", DateTimeOffset.UtcNow, null);
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.StartAsync(stage, null, It.IsAny<CancellationToken>())).ReturnsAsync(accepted);

            var result = await EquipmentEndpoints.PolarAlignStartAsync(svc.Object, new PolarAlignStartRequestDto(stage), null, CancellationToken.None);

            Assert.That((result as Accepted<OperationAcceptedDto>)?.Value, Is.SameAs(accepted));
        }

        // ADR 0022 — a start's refusals (parked, not at Home) leave the endpoint for the one Refusal handler, which answers
        // with their reason; the endpoint's own 409 catch would drop it.
        [Test]
        public async Task A_start_Refusal_is_left_to_the_Refusal_handler() {
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.StartAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RefusalException(RefusalCatalog.PolarAlignMountParked, PolarAlignService.MountParkedMessage));

            var refusal = await Assert.ThrowsAsync<RefusalException>(() => EquipmentEndpoints.PolarAlignStartAsync(svc.Object, null, null, CancellationToken.None));

            Assert.That(refusal!.Cause, Is.EqualTo(RefusalCatalog.PolarAlignMountParked));
        }

        [Test]
        public async Task Capture_outside_the_adjust_phase_maps_to_409() {
            var svc = new Mock<IPolarAlignService>();
            svc.Setup(s => s.RequestCaptureAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("polar alignment is not in the adjust phase"));

            var result = await EquipmentEndpoints.PolarAlignCaptureAsync(svc.Object, null, CancellationToken.None);

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
        }

        [Test]
        public async Task An_aligner_command_returns_202_accepted_on_success() {
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "polar-align.aligner-stop", DateTimeOffset.UtcNow, null);

            var result = await EquipmentEndpoints.PolarAlignCorrectionAsync(() => Task.FromResult(accepted));

            Assert.That((result as Accepted<OperationAcceptedDto>)?.Value, Is.SameAs(accepted));
        }

        [Test]
        public async Task An_aligner_command_with_nothing_to_stop_or_resume_maps_to_409() {
            var result = await EquipmentEndpoints.PolarAlignCorrectionAsync(
                () => Task.FromException<OperationAcceptedDto>(new InvalidOperationException("the Polar Aligner is not stopped")));

            Assert.That(ProblemStatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
        }

        private static int? ProblemStatusOf(IResult result) => (result as ProblemHttpResult)?.StatusCode;

        private static string? ProblemTypeOf(IResult result) => (result as ProblemHttpResult)?.ProblemDetails.Type;
    }
}
