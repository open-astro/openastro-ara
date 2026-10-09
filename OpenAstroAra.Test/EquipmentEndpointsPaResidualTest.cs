#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1311 — <c>GET /equipment/guider/pa-residuals</c>: the session filter goes through, and the
    /// row cap defaults to 20 and stays within 1–500.</summary>
    [TestFixture]
    public class EquipmentEndpointsPaResidualTest {

        private static readonly PaResidualDto Row = new(
            Id: Guid.NewGuid(), Status: "done", StartedUtc: DateTimeOffset.UnixEpoch, CompletedUtc: DateTimeOffset.UnixEpoch,
            SampleSeconds: 300, TargetSeconds: 300, Frames: 120, DriftArcsecPerMin: 0.2, PaErrorMinArcmin: 0.76,
            UncertaintyArcmin: 0.2, Reliable: true);

        [TestCase(null, 20)]
        [TestCase(5, 5)]
        [TestCase(0, 1)]
        [TestCase(10_000, 500)]
        public async Task The_list_passes_the_session_and_a_bounded_limit(int? limit, int expected) {
            var session = Guid.NewGuid();
            var log = new Mock<IPaResidualLog>();
            log.Setup(l => l.ListAsync(session, expected, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<PaResidualDto> { Row });

            var result = await EquipmentEndpoints.ListPaResidualsAsync(session, limit, log.Object, CancellationToken.None);

            Assert.That(result.Value, Has.Count.EqualTo(1));
            Assert.That(result.Value![0], Is.SameAs(Row));
            log.Verify(l => l.ListAsync(session, expected, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Without_a_session_every_row_is_listed() {
            var log = new Mock<IPaResidualLog>();
            log.Setup(l => l.ListAsync(null, 20, It.IsAny<CancellationToken>())).ReturnsAsync(new List<PaResidualDto>());

            var result = await EquipmentEndpoints.ListPaResidualsAsync(null, null, log.Object, CancellationToken.None);

            Assert.That(result.Value, Is.Empty);
            log.Verify(l => l.ListAsync(null, 20, It.IsAny<CancellationToken>()), Times.Once);
        }
    }
}
