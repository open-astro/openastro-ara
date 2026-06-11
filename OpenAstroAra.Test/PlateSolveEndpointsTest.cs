#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Astrometry;
using OpenAstroAra.PlateSolving;
using OpenAstroAra.Server.Endpoints;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §18.I — the solver-result → wire-DTO mapping used by the solve endpoint (the endpoint's load/solve glue
    /// is thin; this covers the mapping logic without an ASP.NET host).
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
    }
}
