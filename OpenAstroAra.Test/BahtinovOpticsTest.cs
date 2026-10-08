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
using OpenAstroAra.Server;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>#1299 — the Bahtinov readout's optics come from the profile with the reducer applied, or not at
    /// all when the focal length, aperture or pixel size is missing (the zone then falls back to half a pixel).</summary>
    [TestFixture]
    public class BahtinovOpticsTest {

        private static InMemoryProfileStore Optics(double focal, double reducer, double aperture, double pixel) {
            var store = new InMemoryProfileStore();
            store.PutOpticsSettings(store.GetOpticsSettings() with {
                FocalLengthMm = focal, ReducerFactor = reducer, ApertureMm = aperture, PixelSizeUm = pixel,
            });
            return store;
        }

        [Test]
        public void Optics_give_the_working_focal_ratio_with_the_reducer() {
            var native = Program.BahtinovOpticsFor(Optics(500, 1.0, 100, 3.76));
            Assert.That(native, Is.EqualTo(new BahtinovOptics(FocalRatio: 5.0, PixelSizeUm: 3.76)));
            var reduced = Program.BahtinovOpticsFor(Optics(500, 0.8, 100, 3.76));
            Assert.That(reduced!.FocalRatio, Is.EqualTo(4.0).Within(1e-9), "a 0.8× reducer takes f/5 to f/4");
            Assert.That(Program.BahtinovOpticsFor(Optics(500, 0, 100, 3.76))!.FocalRatio, Is.EqualTo(5.0),
                "an unset reducer counts as none");
        }

        [TestCase(0, 100, 3.76, TestName = "No focal length, no optics")]
        [TestCase(500, 0, 3.76, TestName = "No aperture, no optics")]
        [TestCase(500, 100, 0, TestName = "No pixel size, no optics")]
        public void Missing_optics_give_none_so_the_zone_falls_back(double focal, double aperture, double pixel) {
            Assert.That(Program.BahtinovOpticsFor(Optics(focal, 1.0, aperture, pixel)), Is.Null);
        }
    }
}
