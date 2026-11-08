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

        [TestCase(-1)]
        [TestCase(5)]
        public void Out_of_range_binning_is_400_and_not_stored(int binning) {
            // §45.4 — 0 = auto, 1–4 = override. Without this check a negative value reaches
            // PolarAlignBinning.Resolve and is taken as auto; a hand-edited profile.json above 4
            // is clamped by Resolve, but the API says no.
            var store = new Mock<IProfileStore>();

            var result = ProfileEndpoints.PutPolarAlignSettings(
                new PolarAlignSettingsDto(Binning: binning), store.Object);

            Assert.That((result as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            store.Verify(s => s.PutPolarAlignSettings(It.IsAny<PolarAlignSettingsDto>()), Times.Never);
        }

        [TestCase(0)]
        [TestCase(4)]
        public void Auto_and_the_cap_are_accepted_binning_values(int binning) {
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetPolarAlignSettings()).Returns(new PolarAlignSettingsDto(Binning: binning));

            var result = ProfileEndpoints.PutPolarAlignSettings(
                new PolarAlignSettingsDto(Binning: binning), store.Object);

            Assert.That(result, Is.Not.InstanceOf<ProblemHttpResult>());
            store.Verify(s => s.PutPolarAlignSettings(It.Is<PolarAlignSettingsDto>(d => d.Binning == binning)), Times.Once);
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

        // §45.12 — the turn and rough-exposure knobs have no control in the app yet (#1736), so an
        // out-of-range or non-finite value is clamped, never a 400 that would fail the panel's own edits.
        [TestCase(200.0, 90.0, 0.0, 120.0, 60.0, 3.0)]
        [TestCase(1.0, 1.0, -2.0, 5.0, 5.0, 3.0)]
        [TestCase(double.NaN, double.NaN, double.NaN, 60.0, 30.0, 3.0)]
        [TestCase(double.PositiveInfinity, double.NegativeInfinity, 600.0, 60.0, 30.0, 60.0)]
        [TestCase(45.0, 20.0, 2.5, 45.0, 20.0, 2.5)]
        public void Turn_and_rough_exposure_knobs_are_clamped_on_PUT_and_the_clamped_values_are_stored(
                double seedRotationDeg, double checkStepDeg, double coarseExposureSeconds,
                double expectedSeed, double expectedCheck, double expectedExposure) {
            var store = new Mock<IProfileStore>();
            var body = new PolarAlignSettingsDto(
                SeedRotationDeg: seedRotationDeg, CheckStepDeg: checkStepDeg, CoarseExposureSeconds: coarseExposureSeconds);

            var result = ProfileEndpoints.PutPolarAlignSettings(body, store.Object);

            var echoed = (result as Ok<PolarAlignSettingsDto>)?.Value;
            Assert.That(echoed, Is.Not.Null);
            Assert.That(echoed!.SeedRotationDeg, Is.EqualTo(expectedSeed));
            Assert.That(echoed.CheckStepDeg, Is.EqualTo(expectedCheck));
            Assert.That(echoed.CoarseExposureSeconds, Is.EqualTo(expectedExposure));
            store.Verify(s => s.PutPolarAlignSettings(It.Is<PolarAlignSettingsDto>(d =>
                d.SeedRotationDeg == expectedSeed && d.CheckStepDeg == expectedCheck && d.CoarseExposureSeconds == expectedExposure)), Times.Once);
            store.Verify(s => s.PutPolarAlignSettings(It.IsAny<PolarAlignSettingsDto>()), Times.Once);
        }

        [Test]
        public void GET_returns_the_stored_knobs_clamped_to_what_the_daemon_uses() {
            // An older build stored any value; the view is what the routine will run with.
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetPolarAlignSettings()).Returns(new PolarAlignSettingsDto(
                ExposureSeconds: 2.0, SeedRotationDeg: 200.0, CheckStepDeg: 90.0, CoarseExposureSeconds: 0.0));

            var result = ProfileEndpoints.GetPolarAlignSettings(store.Object);

            var view = (result as Ok<PolarAlignSettingsDto>)?.Value;
            Assert.That(view, Is.Not.Null);
            Assert.That(view!.ExposureSeconds, Is.EqualTo(2.0), "the clamp leaves the other fields alone");
            Assert.That(view.SeedRotationDeg, Is.EqualTo(PolarAlignSettingsClamp.MaxSeedRotationDeg));
            Assert.That(view.CheckStepDeg, Is.EqualTo(PolarAlignSettingsClamp.MaxCheckStepDeg));
            Assert.That(view.CoarseExposureSeconds, Is.EqualTo(PolarAlignSettingsClamp.DefaultCoarseExposureSeconds));
            store.Verify(s => s.PutPolarAlignSettings(It.IsAny<PolarAlignSettingsDto>()), Times.Never, "a read writes nothing back");
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
