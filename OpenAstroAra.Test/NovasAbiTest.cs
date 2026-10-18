#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Runtime.InteropServices;
using NUnit.Framework;
using OpenAstroAra.Astrometry;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1386 — the NOVAS.cs marshaling structs are passed by reference into SuperNOVAS, which
    /// reads and writes them by its own layout. A field of the wrong width is silent memory
    /// corruption, not an exception (the NOVAS 3.1 layout had <c>short</c> type/number and no
    /// orbit block, and its <c>int</c> StarNumber was already 4 bytes short of C's LP64 long).
    /// The expected sizes are sizeof() from SuperNOVAS v1.7.2's novas.h on an LP64 host, which
    /// is every host the daemon runs on. A Dependabot bump that changes a struct fails here, not
    /// at a user's first altitude condition.
    /// </summary>
    [TestFixture]
    public class NovasAbiTest {

        [Test]
        public void Marshaling_structs_match_the_SuperNOVAS_layout() {
            Assert.Multiple(() => {
                Assert.That(Marshal.SizeOf<NOVAS.CatalogueEntry>(), Is.EqualTo(112), "cat_entry");
                Assert.That(Marshal.SizeOf<NOVAS.OrbitalSystem>(), Is.EqualTo(32), "novas_orbital_system");
                Assert.That(Marshal.SizeOf<NOVAS.OrbitalElements>(), Is.EqualTo(112), "novas_orbital");
                Assert.That(Marshal.SizeOf<NOVAS.CelestialObject>(), Is.EqualTo(296), "object");
                Assert.That(Marshal.SizeOf<NOVAS.OnSurface>(), Is.EqualTo(48), "on_surface");
                Assert.That(Marshal.SizeOf<NOVAS.InSpace>(), Is.EqualTo(48), "in_space");
                Assert.That(Marshal.SizeOf<NOVAS.Observer>(), Is.EqualTo(104), "observer");
                Assert.That(Marshal.SizeOf<SkyPosition>(), Is.EqualTo(56), "sky_pos");
            });
        }

        [Test]
        public void Object_fields_sit_at_the_SuperNOVAS_offsets() {
            // enum type at 0, long number at 8, name at 16, cat_entry star at 72, orbit at 184.
            Assert.Multiple(() => {
                Assert.That(Marshal.OffsetOf<NOVAS.CelestialObject>(nameof(NOVAS.CelestialObject.Number)).ToInt32(), Is.EqualTo(8));
                Assert.That(Marshal.OffsetOf<NOVAS.CelestialObject>(nameof(NOVAS.CelestialObject.Name)).ToInt32(), Is.EqualTo(16));
                Assert.That(Marshal.OffsetOf<NOVAS.CelestialObject>(nameof(NOVAS.CelestialObject.Star)).ToInt32(), Is.EqualTo(72));
                Assert.That(Marshal.OffsetOf<NOVAS.CelestialObject>(nameof(NOVAS.CelestialObject.Orbit)).ToInt32(), Is.EqualTo(184));
            });
        }
    }
}
