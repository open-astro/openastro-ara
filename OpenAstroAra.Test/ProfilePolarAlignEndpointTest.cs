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

namespace OpenAstroAra.Test {

    /// <summary>
    /// §45.12 — <c>PUT /profile/polar-align</c> write-boundary validation. The live loop re-reads the
    /// settings before every frame, so an out-of-range exposure or an unknown loop mode is a 400 and
    /// never reaches the store; a valid body is stored and echoed back.
    /// </summary>
    [TestFixture]
    public class ProfilePolarAlignEndpointTest {

        [TestCase(0.0)]
        [TestCase(-1.0)]
        [TestCase(60.001)]
        [TestCase(61.0)]
        [TestCase(double.NaN)]
        [TestCase(double.PositiveInfinity)]
        public void Out_of_range_exposure_is_400_and_not_stored(double exposureSeconds) {
            var store = new Mock<IProfileStore>();

            var result = ProfileEndpoints.PutPolarAlignSettings(
                new PolarAlignSettingsDto(ExposureSeconds: exposureSeconds), store.Object);

            Assert.That((result as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            store.Verify(s => s.PutPolarAlignSettings(It.IsAny<PolarAlignSettingsDto>()), Times.Never);
        }

        [TestCase("burst")]
        [TestCase("Loop")]
        [TestCase("")]
        public void Unknown_loop_mode_is_400_and_not_stored(string loopMode) {
            var store = new Mock<IProfileStore>();

            var result = ProfileEndpoints.PutPolarAlignSettings(
                new PolarAlignSettingsDto(LoopMode: loopMode), store.Object);

            Assert.That((result as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            store.Verify(s => s.PutPolarAlignSettings(It.IsAny<PolarAlignSettingsDto>()), Times.Never);
        }

        [TestCase(0.001, PolarAlignLoopModes.Loop)]
        [TestCase(60.0, PolarAlignLoopModes.SingleFrame)]
        [TestCase(2.5, PolarAlignLoopModes.Loop)]
        public void Valid_settings_are_stored_and_echoed(double exposureSeconds, string loopMode) {
            var store = new Mock<IProfileStore>();
            var body = new PolarAlignSettingsDto(ExposureSeconds: exposureSeconds, LoopMode: loopMode);

            var result = ProfileEndpoints.PutPolarAlignSettings(body, store.Object);

            Assert.That((result as Ok<PolarAlignSettingsDto>)?.Value, Is.EqualTo(body));
            store.Verify(s => s.PutPolarAlignSettings(body), Times.Once);
        }
    }
}
