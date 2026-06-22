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
using OpenAstroAra.Server.Contracts;
using System.Collections.Generic;
using System.Text.Json;

namespace OpenAstroAra.Test {

    // §37 wizard optics-pull: the telescope caps now carry focal length + aperture
    // (mm) so the wizard can auto-fill the optics screen from a connected mount.
    // These pin the wire contract (snake_case names) the client consumes.
    [TestFixture]
    public class TelescopeOpticsCapsTest {

        private static TelescopeCapabilitiesDto Caps(double? focalMm, double? apertureMm) =>
            new(CanSlew: true, CanSync: false, CanPark: false, CanUnpark: false,
                CanSetTracking: true, CanPulseGuide: false, CanFindHome: false,
                SupportedSiderealRates: new List<string>(),
                FocalLengthMm: focalMm, ApertureDiameterMm: apertureMm);

        [Test]
        public void Optics_serialize_as_snake_case_mm_and_round_trip() {
            var json = JsonSerializer.Serialize(
                Caps(714, 102), AraJsonSerializerContext.Default.TelescopeCapabilitiesDto);
            Assert.That(json, Does.Contain("focal_length_mm"));
            Assert.That(json, Does.Contain("aperture_diameter_mm"));

            var back = JsonSerializer.Deserialize(
                json, AraJsonSerializerContext.Default.TelescopeCapabilitiesDto)!;
            Assert.That(back.FocalLengthMm, Is.EqualTo(714));
            Assert.That(back.ApertureDiameterMm, Is.EqualTo(102));
        }

        [Test]
        public void Optics_are_null_when_the_mount_does_not_report_them() {
            // Most mounts NotImplement FocalLength/ApertureDiameter — they ride the
            // wire as null (default), and the wizard then leaves the manual fields.
            var json = JsonSerializer.Serialize(
                Caps(null, null), AraJsonSerializerContext.Default.TelescopeCapabilitiesDto);
            var back = JsonSerializer.Deserialize(
                json, AraJsonSerializerContext.Default.TelescopeCapabilitiesDto)!;
            Assert.That(back.FocalLengthMm, Is.Null);
            Assert.That(back.ApertureDiameterMm, Is.Null);
        }
    }
}
