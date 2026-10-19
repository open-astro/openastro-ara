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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// A manual slew while a sequence run is in progress stops guiding and parks the run (2026-10-18:
    /// a Planning GoTo mid-dither, accepted without a word). The endpoint refuses it with 409 and a
    /// sentence unless the caller forces it; a sync never moves the mount.
    /// </summary>
    [TestFixture]
    public class EquipmentEndpointsSlewTest {
        private static readonly IReadOnlyList<Guid> None = Array.Empty<Guid>();
        private static readonly IReadOnlyList<Guid> One = [Guid.NewGuid()];
        private static readonly IReadOnlyList<Guid> Two = [Guid.NewGuid(), Guid.NewGuid()];

        [Test]
        public void A_slew_with_no_run_in_progress_goes_ahead() {
            Assert.That(EquipmentEndpoints.SlewDuringRunRefusal(new SlewRequestDto(5.5, -12.25), None), Is.Null);
        }

        [Test]
        public void A_slew_during_a_run_is_refused_with_the_reason_and_the_way_out() {
            var refusal = EquipmentEndpoints.SlewDuringRunRefusal(new SlewRequestDto(5.5, -12.25), One);
            Assert.That(refusal, Does.Contain("run is in progress").And.Contain("stops guiding").And.Contain("force"));
            Assert.That(EquipmentEndpoints.SlewDuringRunRefusal(new SlewRequestDto(5.5, -12.25), Two), Does.Contain("2 sequence runs"));
        }

        [Test]
        public async Task The_endpoint_answers_409_run_in_progress_during_a_run_and_202_when_forced_or_idle() {
            // The wiring, not the helper: the handler asks the sequencer and refuses with the problem title
            // the client keys on; a forced request (and an idle sequencer) reach the telescope service.
            var telescope = new Mock<ITelescopeService>();
            telescope.Setup(t => t.SlewAsync(It.IsAny<SlewRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationAcceptedDto(Guid.NewGuid(), "telescope.slew", DateTimeOffset.UtcNow, null));
            var busy = new Mock<ISequencerService>();
            busy.Setup(s => s.ActiveRunIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(One);
            var idle = new Mock<ISequencerService>();
            idle.Setup(s => s.ActiveRunIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(None);

            var refused = await EquipmentEndpoints.SlewAsync(new SlewRequestDto(5.5, -12.25), null, telescope.Object, busy.Object, CancellationToken.None);
            Assert.That((refused as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That((refused as ProblemHttpResult)?.ProblemDetails.Title, Is.EqualTo("run_in_progress"));
            Assert.That((refused as ProblemHttpResult)?.ProblemDetails.Detail, Does.Contain("force"));
            telescope.Verify(t => t.SlewAsync(It.IsAny<SlewRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never, "a refused slew never reaches the mount");

            var forced = await EquipmentEndpoints.SlewAsync(new SlewRequestDto(5.5, -12.25, Force: true), "k1", telescope.Object, busy.Object, CancellationToken.None);
            Assert.That(forced, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
            var quiet = await EquipmentEndpoints.SlewAsync(new SlewRequestDto(5.5, -12.25), null, telescope.Object, idle.Object, CancellationToken.None);
            Assert.That(quiet, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
            telescope.Verify(t => t.SlewAsync(It.IsAny<SlewRequestDto>(), "k1", It.IsAny<CancellationToken>()), Times.Once, "the idempotency key rides through");
        }

        [Test]
        public void A_forced_slew_and_a_sync_go_ahead_during_a_run() {
            Assert.That(EquipmentEndpoints.SlewDuringRunRefusal(new SlewRequestDto(5.5, -12.25, Force: true), One), Is.Null, "the caller was told and said yes");
            Assert.That(EquipmentEndpoints.SlewDuringRunRefusal(new SlewRequestDto(5.5, -12.25, Sync: true), One), Is.Null, "a sync does not move the mount");
        }
    }
}
