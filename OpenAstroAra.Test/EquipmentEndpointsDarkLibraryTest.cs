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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §63.6 (guider-e-4b-2) — the dark-library build endpoint's error-to-status mapping: a valid request is
    /// 202-Accepted, a bad request (validation) is 400, and a disconnected guider is 409. The service is mocked
    /// so the mapping is covered without an ASP.NET host or a live guider.
    /// </summary>
    [TestFixture]
    public class EquipmentEndpointsDarkLibraryTest {

        private static readonly BuildDarkLibraryRequestDto Request = new(FrameCount: 5);

        [Test]
        public async Task Build_returns_202_accepted_on_success() {
            var accepted = new OperationAcceptedDto(Guid.NewGuid(), "guider.dark_library.build", DateTimeOffset.UtcNow, "idem-1");
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.BuildDarkLibraryAsync(Request, "idem-1", It.IsAny<CancellationToken>()))
                .ReturnsAsync(accepted);

            var result = await EquipmentEndpoints.BuildDarkLibraryAsync(Request, "idem-1", svc.Object, CancellationToken.None);

            var typed = result as Accepted<OperationAcceptedDto>;
            Assert.That(typed, Is.Not.Null);
            Assert.That(typed!.Value, Is.SameAs(accepted));
        }

        [Test]
        public async Task Build_maps_validation_ArgumentException_to_400() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.BuildDarkLibraryAsync(It.IsAny<BuildDarkLibraryRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ArgumentOutOfRangeException("frameCount"));

            var result = await EquipmentEndpoints.BuildDarkLibraryAsync(Request, null, svc.Object, CancellationToken.None);

            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status400BadRequest));
        }

        [Test]
        public async Task Build_maps_not_connected_InvalidOperation_to_409() {
            var svc = new Mock<IGuiderService>();
            svc.Setup(s => s.BuildDarkLibraryAsync(It.IsAny<BuildDarkLibraryRequestDto>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("guider is not connected"));

            var result = await EquipmentEndpoints.BuildDarkLibraryAsync(Request, null, svc.Object, CancellationToken.None);

            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
        }

        private static int? StatusOf(IResult result) => (result as ProblemHttpResult)?.StatusCode;
    }
}
