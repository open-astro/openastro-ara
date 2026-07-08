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
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §18.I — the solver-result → wire-DTO mapping used by the solve endpoint (the endpoint's load/solve glue
    /// is thin; this covers the mapping logic without an ASP.NET host), plus the §18.I hinted-solve coordinate
    /// resolution (body hint → frame OBJCTRA/OBJCTDEC headers → blind) and the endpoint's load/404 + hint glue.
    /// </summary>
    [TestFixture]
    public class PlateSolveEndpointsTest {

        [Test]
        public void ToDto_maps_a_successful_solution() {
            var result = new PlateSolveResult {
                Success = true,
                Coordinates = new Coordinates(Angle.ByHours(3.5), Angle.ByDegree(12.0), Epoch.J2000),
                PositionAngle = 47.0,
                Pixscale = 1.83,
                Radius = 2.5,
            };

            var dto = PlateSolveEndpoints.ToDto(result);

            Assert.That(dto.Success, Is.True);
            Assert.That(dto.Ra, Is.EqualTo(3.5).Within(1e-6));
            Assert.That(dto.Dec, Is.EqualTo(12.0).Within(1e-6));
            Assert.That(dto.Orientation, Is.EqualTo(47.0));
            Assert.That(dto.PixelScale, Is.EqualTo(1.83));
            Assert.That(dto.SearchRadius, Is.EqualTo(2.5));
        }

        [Test]
        public void ToDto_treats_success_without_coordinates_as_a_failure() {
            // A solver that sets Success=true but leaves Coordinates null is a contract violation — don't
            // hand the client a contradictory {success:true, ra:null}; report it as failed.
            var dto = PlateSolveEndpoints.ToDto(new PlateSolveResult { Success = true, Coordinates = null });
            Assert.That(dto.Success, Is.False);
            Assert.That(dto.Ra, Is.Null);
        }

        [Test]
        public void ToDto_nulls_every_field_on_a_failed_solve() {
            // Even though an unsolved PlateSolveResult has 0-valued Orientation/Pixscale/Radius, the DTO
            // reports them null so a failed solve can't be mistaken for a real (0,0,0) solution.
            var dto = PlateSolveEndpoints.ToDto(new PlateSolveResult { Success = false });
            Assert.That(dto.Success, Is.False);
            Assert.That(dto.Ra, Is.Null);
            Assert.That(dto.Dec, Is.Null);
            Assert.That(dto.Orientation, Is.Null);
            Assert.That(dto.PixelScale, Is.Null);
            Assert.That(dto.SearchRadius, Is.Null);
        }

        // ── §18.I hinted-solve coordinate resolution ─────────────────────────

        private static Mock<IFrameRepository> FramesWithHeaderCoords((double RaDegrees, double DecDegrees)? headers) {
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.TryReadTargetCoordinatesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(headers);
            return frames;
        }

        [Test]
        public async Task ResolveApprox_prefers_an_explicit_body_hint_over_the_frame_headers() {
            var frames = FramesWithHeaderCoords((RaDegrees: 10.0, DecDegrees: -20.0)); // must be ignored
            var request = new PlateSolveRequestDto(ApproxRaHours: 3.5, ApproxDecDegrees: 12.0);

            var coords = await PlateSolveEndpoints.ResolveApproxCoordinatesAsync(
                Guid.NewGuid(), request, frames.Object, CancellationToken.None);

            Assert.That(coords, Is.Not.Null);
            Assert.That(coords!.RA, Is.EqualTo(3.5).Within(1e-6), "body RA is hours");
            Assert.That(coords.Dec, Is.EqualTo(12.0).Within(1e-6));
            frames.Verify(f => f.TryReadTargetCoordinatesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never,
                "an explicit hint short-circuits the header read");
        }

        [Test]
        public async Task ResolveApprox_falls_back_to_the_frame_OBJCT_headers_when_no_body_hint() {
            // Header RA is degrees; Coordinates exposes RA in hours, so 52.5° → 3.5h.
            var frames = FramesWithHeaderCoords((RaDegrees: 52.5, DecDegrees: 12.0));

            var coords = await PlateSolveEndpoints.ResolveApproxCoordinatesAsync(
                Guid.NewGuid(), request: null, frames.Object, CancellationToken.None);

            Assert.That(coords, Is.Not.Null);
            Assert.That(coords!.RA, Is.EqualTo(3.5).Within(1e-6), "header RA degrees convert to hours");
            Assert.That(coords.Dec, Is.EqualTo(12.0).Within(1e-6));
        }

        [Test]
        public async Task ResolveApprox_is_blind_when_neither_a_body_hint_nor_frame_headers_exist() {
            var frames = FramesWithHeaderCoords(null);

            var coords = await PlateSolveEndpoints.ResolveApproxCoordinatesAsync(
                Guid.NewGuid(), request: null, frames.Object, CancellationToken.None);

            Assert.That(coords, Is.Null);
        }

        [Test]
        public async Task ResolveApprox_treats_a_lone_body_RA_or_Dec_as_no_hint() {
            var frames = FramesWithHeaderCoords((RaDegrees: 52.5, DecDegrees: 12.0));
            var loneRa = new PlateSolveRequestDto(ApproxRaHours: 3.5, ApproxDecDegrees: null);

            var coords = await PlateSolveEndpoints.ResolveApproxCoordinatesAsync(
                Guid.NewGuid(), loneRa, frames.Object, CancellationToken.None);

            // An incomplete pair can't center a search → falls through to the header hint (3.5h here).
            Assert.That(coords, Is.Not.Null);
            Assert.That(coords!.RA, Is.EqualTo(3.5).Within(1e-6));
            frames.Verify(f => f.TryReadTargetCoordinatesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task SolveFrame_returns_404_when_the_frame_or_its_file_is_missing() {
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.LoadImageDataAsync(It.IsAny<Guid>(), It.IsAny<OpenAstroAra.Profile.Interfaces.IProfileService>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IImageData?)null);
            var solver = new Mock<IPlateSolveService>();

            var result = await PlateSolveEndpoints.SolveFrameAsync(
                Guid.NewGuid(), request: null, frames.Object, solver.Object,
                new Mock<OpenAstroAra.Profile.Interfaces.IProfileService>().Object, CancellationToken.None);

            Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.NotFound>());
            solver.Verify(s => s.SolveImage(It.IsAny<IImageData>(), It.IsAny<Coordinates?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task SolveFrame_passes_the_resolved_hint_through_to_the_solver() {
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.LoadImageDataAsync(It.IsAny<Guid>(), It.IsAny<OpenAstroAra.Profile.Interfaces.IProfileService>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Mock<IImageData>().Object);
            frames.Setup(f => f.TryReadTargetCoordinatesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(((double, double)?)null);
            Coordinates? seen = null;
            var solver = new Mock<IPlateSolveService>();
            solver.Setup(s => s.SolveImage(It.IsAny<IImageData>(), It.IsAny<Coordinates?>(), It.IsAny<IProgress<ApplicationStatus>?>(), It.IsAny<CancellationToken>()))
                .Callback((IImageData _, Coordinates? c, IProgress<ApplicationStatus>? _, CancellationToken _) => seen = c)
                .ReturnsAsync(new PlateSolveResult { Success = false });

            var result = await PlateSolveEndpoints.SolveFrameAsync(
                Guid.NewGuid(), new PlateSolveRequestDto(ApproxRaHours: 6.0, ApproxDecDegrees: 30.0),
                frames.Object, solver.Object,
                new Mock<OpenAstroAra.Profile.Interfaces.IProfileService>().Object, CancellationToken.None);

            Assert.That(result, Is.InstanceOf<Microsoft.AspNetCore.Http.HttpResults.Ok<PlateSolveResultDto>>());
            Assert.That(seen, Is.Not.Null, "the body hint must reach the solver");
            Assert.That(seen!.RA, Is.EqualTo(6.0).Within(1e-6));
            Assert.That(seen.Dec, Is.EqualTo(30.0).Within(1e-6));
        }
    }
}
