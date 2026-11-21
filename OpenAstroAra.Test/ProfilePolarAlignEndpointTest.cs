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
        [TestCase(200.0, 90.0, 0.0, 30.0, 30.0, 3.0)]
        [TestCase(1.0, 1.0, -2.0, 5.0, 5.0, 3.0)]
        [TestCase(double.NaN, double.NaN, double.NaN, 10.0, 10.0, 3.0)]
        [TestCase(double.PositiveInfinity, double.NegativeInfinity, 600.0, 10.0, 10.0, 60.0)]
        [TestCase(25.0, 20.0, 2.5, 25.0, 20.0, 2.5)]
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

        // #2068 review — an app from before mount_clearance PUTs the section without it: the stored confirmations stay.
        // An empty map (the app's own clear) still clears them.
        [Test]
        public void A_PUT_without_mount_clearance_keeps_every_stored_confirmation() {
            var stored = new Dictionary<string, PolarAlignMountClearanceDto> {
                ["mount-a"] = new(MaxCwAngleDeg: 45.0, ClearanceConfirmed: true, ConfirmedMaxCwAngleDeg: 45.0),
            };
            var store = new Mock<IProfileStore>();
            store.Setup(s => s.GetPolarAlignSettings()).Returns(new PolarAlignSettingsDto(MountClearance: stored));

            var kept = (ProfileEndpoints.PutPolarAlignSettings(new PolarAlignSettingsDto(ExposureSeconds: 4.0), store.Object)
                as Ok<PolarAlignSettingsDto>)?.Value;
            var cleared = (ProfileEndpoints.PutPolarAlignSettings(
                new PolarAlignSettingsDto(MountClearance: new Dictionary<string, PolarAlignMountClearanceDto>()), store.Object)
                as Ok<PolarAlignSettingsDto>)?.Value;

            Assert.That(kept?.MountClearance?["mount-a"], Is.EqualTo(new PolarAlignMountClearanceDto(45.0, true, 45.0)));
            Assert.That(kept!.ExposureSeconds, Is.EqualTo(4.0));
            Assert.That(cleared?.MountClearance, Is.Empty, "an empty map clears them");
        }

        // #2068 review — a confirmation is for the limit the user checked: a confirmed limit out of range is refused, not
        // raised to 10° or lowered to 75°. An unconfirmed one is clamped like the other knobs.
        [TestCase(5.0)]
        [TestCase(80.0)]
        public void A_confirmed_clearance_outside_10_to_75_degrees_is_400_and_not_stored(double limitDeg) {
            var store = new Mock<IProfileStore>();
            var entries = new Dictionary<string, PolarAlignMountClearanceDto> { ["m"] = new(limitDeg, ClearanceConfirmed: true) };

            var refused = ProfileEndpoints.PutPolarAlignSettings(new PolarAlignSettingsDto(MountClearance: entries), store.Object);
            Assert.That((refused as ProblemHttpResult)?.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            store.Verify(s => s.PutPolarAlignSettings(It.IsAny<PolarAlignSettingsDto>()), Times.Never);

            var unconfirmed = new Dictionary<string, PolarAlignMountClearanceDto> { ["m"] = new(limitDeg, ClearanceConfirmed: false) };
            var clamped = (ProfileEndpoints.PutPolarAlignSettings(new PolarAlignSettingsDto(MountClearance: unconfirmed), store.Object)
                as Ok<PolarAlignSettingsDto>)?.Value;
            Assert.That(clamped?.MountClearance?["m"].MaxCwAngleDeg, Is.EqualTo(PolarAlignSettingsClamp.ClampMaxCwAngleDeg(limitDeg)));
        }

        // Joey, 2026-11-21: a confirmation is for one limit. A PUT that changes the limit and carries the old confirmation
        // along (an app that copied the entry) stores the mount not confirmed; the same PUT confirming the new limit
        // keeps it confirmed; taking it back (clearance_confirmed false) stores it not confirmed.
        [Test]
        public void A_limit_changed_without_a_new_confirmation_is_stored_not_confirmed() {
            var store = new Mock<IProfileStore>();
            PolarAlignMountClearanceDto? Put(PolarAlignMountClearanceDto entry) =>
                (ProfileEndpoints.PutPolarAlignSettings(new PolarAlignSettingsDto(
                    MountClearance: new Dictionary<string, PolarAlignMountClearanceDto> { ["m"] = entry }), store.Object)
                    as Ok<PolarAlignSettingsDto>)?.Value?.MountClearance?["m"];

            var copied = Put(new(MaxCwAngleDeg: 30.0, ClearanceConfirmed: true, ConfirmedMaxCwAngleDeg: 45.0));
            var unnamed = Put(new(MaxCwAngleDeg: 30.0, ClearanceConfirmed: true));
            var confirmedAgain = Put(new(MaxCwAngleDeg: 30.0, ClearanceConfirmed: true, ConfirmedMaxCwAngleDeg: 30.0));
            var takenBack = Put(new(MaxCwAngleDeg: 30.0, ClearanceConfirmed: false, ConfirmedMaxCwAngleDeg: 30.0));

            Assert.Multiple(() => {
                Assert.That(copied, Is.EqualTo(new PolarAlignMountClearanceDto(30.0, false)), "the old confirmation does not carry to a new limit");
                Assert.That(unnamed, Is.EqualTo(new PolarAlignMountClearanceDto(30.0, false)), "a confirmation names the limit it is for");
                Assert.That(confirmedAgain, Is.EqualTo(new PolarAlignMountClearanceDto(30.0, true, 30.0)), "the same request confirms the new limit");
                Assert.That(takenBack, Is.EqualTo(new PolarAlignMountClearanceDto(30.0, false)), "taken back");
            });
            store.Verify(s => s.PutPolarAlignSettings(It.Is<PolarAlignSettingsDto>(v => v.MountClearance!["m"].ClearanceConfirmed)), Times.Once,
                "only the request that confirms the new limit stores it confirmed");
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
