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
using System.Collections.Generic;

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

        [TestCase("Main")]
        [TestCase("both")]
        [TestCase("")]
        [TestCase(null)]
        public void Unknown_camera_is_400_and_not_stored(string? camera) {
            // #2082 — the measurement's camera is guide or main: a typo must not become the guide camera silently.
            var store = new Mock<IProfileStore>();

            var result = ProfileEndpoints.PutPolarAlignSettings(
                new PolarAlignSettingsDto(Camera: camera!), store.Object);

            Assert.That((result as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            store.Verify(s => s.PutPolarAlignSettings(It.IsAny<PolarAlignSettingsDto>()), Times.Never);
        }

        [TestCase(0, 1)]
        [TestCase(-3, 1)]
        [TestCase(1, 1)]
        [TestCase(3, 3)]
        [TestCase(5, 5)]
        [TestCase(9, 5)]
        public void Frames_per_point_are_held_to_1_to_5_on_PUT_and_GET(int framesPerPoint, int expected) {
            // #2082 — clamped like the turns, never a 400: an older or hand-edited profile.json may hold any number.
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetPolarAlignSettings()).Returns(new PolarAlignSettingsDto(FramesPerPoint: framesPerPoint, Camera: "sideways"));

            var put = (ProfileEndpoints.PutPolarAlignSettings(
                new PolarAlignSettingsDto(FramesPerPoint: framesPerPoint, Camera: PolarAlignCameras.Main), store.Object) as Ok<PolarAlignSettingsDto>)?.Value;
            var view = (ProfileEndpoints.GetPolarAlignSettings(store.Object) as Ok<PolarAlignSettingsDto>)?.Value;

            Assert.That(put?.FramesPerPoint, Is.EqualTo(expected));
            Assert.That(put?.Camera, Is.EqualTo(PolarAlignCameras.Main));
            store.Verify(s => s.PutPolarAlignSettings(It.Is<PolarAlignSettingsDto>(d => d.FramesPerPoint == expected)), Times.Once);
            Assert.That(view?.FramesPerPoint, Is.EqualTo(expected));
            Assert.That(view?.Camera, Is.EqualTo(PolarAlignCameras.Guide), "a stored camera the daemon does not know reads as the guide camera");
        }

        // §45.12 — the turn and rough-exposure knobs have no control in the app yet (#1736), so an
        // out-of-range or non-finite value is clamped, never a 400 that would fail the panel's own edits.
        [TestCase(200.0, 0.0, 30.0, 3.0)]
        [TestCase(1.0, -2.0, 5.0, 3.0)]
        [TestCase(double.NaN, double.NaN, 10.0, 3.0)]
        [TestCase(double.PositiveInfinity, 600.0, 10.0, 60.0)]
        [TestCase(25.0, 2.5, 25.0, 2.5)]
        public void Turn_and_rough_exposure_knobs_are_clamped_on_PUT_and_the_clamped_values_are_stored(
                double seedRotationDeg, double coarseExposureSeconds, double expectedSeed, double expectedExposure) {
            var store = new Mock<IProfileStore>();
            var body = new PolarAlignSettingsDto(SeedRotationDeg: seedRotationDeg, CoarseExposureSeconds: coarseExposureSeconds);

            var result = ProfileEndpoints.PutPolarAlignSettings(body, store.Object);

            var echoed = (result as Ok<PolarAlignSettingsDto>)?.Value;
            Assert.That(echoed, Is.Not.Null);
            Assert.That(echoed!.SeedRotationDeg, Is.EqualTo(expectedSeed));
            Assert.That(echoed.CoarseExposureSeconds, Is.EqualTo(expectedExposure));
            store.Verify(s => s.PutPolarAlignSettings(It.Is<PolarAlignSettingsDto>(d =>
                d.SeedRotationDeg == expectedSeed && d.CoarseExposureSeconds == expectedExposure)), Times.Once);
            store.Verify(s => s.PutPolarAlignSettings(It.IsAny<PolarAlignSettingsDto>()), Times.Once);
        }

        [Test]
        public void GET_returns_the_stored_knobs_clamped_to_what_the_daemon_uses() {
            // An older build stored any value; the view is what the routine will run with.
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetPolarAlignSettings()).Returns(new PolarAlignSettingsDto(
                ExposureSeconds: 2.0, SeedRotationDeg: 200.0, CoarseExposureSeconds: 0.0));

            var result = ProfileEndpoints.GetPolarAlignSettings(store.Object);

            var view = (result as Ok<PolarAlignSettingsDto>)?.Value;
            Assert.That(view, Is.Not.Null);
            Assert.That(view!.ExposureSeconds, Is.EqualTo(2.0), "the clamp leaves the other fields alone");
            Assert.That(view.SeedRotationDeg, Is.EqualTo(PolarAlignSettingsClamp.MaxSeedRotationDeg));
            Assert.That(view.CoarseExposureSeconds, Is.EqualTo(PolarAlignSettingsClamp.DefaultCoarseExposureSeconds));
            store.Verify(s => s.PutPolarAlignSettings(It.IsAny<PolarAlignSettingsDto>()), Times.Never, "a read writes nothing back");
        }

        // An older profile or app still carries the per-mount clearance: the key is read like any unknown member (ignored).
        [Test]
        public void A_polar_align_section_that_still_carries_mount_clearance_is_read_without_it() {
            const string json = """
                {"exposure_seconds": 2.5, "mount_clearance": {"some-mount": {"confirmed_reach_deg": 20.5}}}
                """;

            var read = System.Text.Json.JsonSerializer.Deserialize<PolarAlignSettingsDto>(json, OpenAstroAra.Server.AraJsonSerializerContext.Default.Options);

            Assert.That(read, Is.EqualTo(new PolarAlignSettingsDto(ExposureSeconds: 2.5)));
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
