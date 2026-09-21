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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    // #1064 — status mapping of the manual-nudge handler, on the extracted handler with a mocked
    // ITelescopeService (the repo's endpoint-test pattern, see EquipmentEndpointsGuiderEquipmentTest).
    [TestFixture]
    public class EquipmentEndpointsMoveAxisTest {

        private static int StatusOf(IResult r) => r switch {
            ProblemHttpResult p => p.StatusCode,
            Accepted => StatusCodes.Status202Accepted,
            _ => throw new InvalidOperationException(r.GetType().Name),
        };

        [Test]
        public async Task A_pad_axis_nudge_is_forwarded_and_accepted() {
            var svc = new Mock<ITelescopeService>();
            var result = await EquipmentEndpoints.MoveAxisAsync(new MoveAxisRequestDto(1, 1.5), svc.Object, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status202Accepted));
            svc.Verify(s => s.MoveAxisAsync(1, 1.5, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Tertiary_and_out_of_range_axes_and_a_non_finite_rate_are_400_before_the_driver() {
            var svc = new Mock<ITelescopeService>();
            Assert.That(StatusOf(await EquipmentEndpoints.MoveAxisAsync(new MoveAxisRequestDto(2, 1), svc.Object, CancellationToken.None)), Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(StatusOf(await EquipmentEndpoints.MoveAxisAsync(new MoveAxisRequestDto(-1, 1), svc.Object, CancellationToken.None)), Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(StatusOf(await EquipmentEndpoints.MoveAxisAsync(new MoveAxisRequestDto(0, double.NaN), svc.Object, CancellationToken.None)), Is.EqualTo(StatusCodes.Status400BadRequest));
            svc.Verify(s => s.MoveAxisAsync(It.IsAny<int>(), It.IsAny<double>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task A_refused_nudge_maps_InvalidOperation_to_409() {
            var svc = new Mock<ITelescopeService>();
            svc.Setup(s => s.MoveAxisAsync(0, 1.0, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Mount reports no MoveAxis rate for this axis"));
            var result = await EquipmentEndpoints.MoveAxisAsync(new MoveAxisRequestDto(0, 1.0), svc.Object, CancellationToken.None);
            Assert.That(StatusOf(result), Is.EqualTo(StatusCodes.Status409Conflict));
        }
    }
}
