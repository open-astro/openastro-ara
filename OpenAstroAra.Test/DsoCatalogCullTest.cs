#region "copyright"

/*
    Copyright © 2016 - 2025 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy and its OpenAstro Ara port.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test {

    /// <summary>The /data-manager/dso-catalog cull (<see cref="SystemEndpoints.PassesDsoCatalogCull"/>)
    /// and the package-list parity the reader must keep with the DSO merge set (review #1107).</summary>
    [TestFixture]
    public class DsoCatalogCullTest {

        private static DsoEntryDto Row(string name, string type, double? mag) =>
            new(name, null, type, 0, 0, mag, null, null, null, null);

        [Test]
        public void Bright_rows_pass_and_faint_rows_are_culled() {
            Assert.That(SystemEndpoints.PassesDsoCatalogCull(Row("NGC0224", "G", 3.4), 12), Is.True);
            Assert.That(SystemEndpoints.PassesDsoCatalogCull(Row("NGC0001", "G", 13.6), 12), Is.False);
        }

        [Test]
        public void Magnitudeless_nebulae_pass_but_magnitudeless_stars_and_stubs_do_not() {
            Assert.That(SystemEndpoints.PassesDsoCatalogCull(Row("Sh2-110", "HII", null), 12), Is.True);
            Assert.That(SystemEndpoints.PassesDsoCatalogCull(Row("LDN 1235", "DrkN", null), 12), Is.True);
            Assert.That(SystemEndpoints.PassesDsoCatalogCull(Row("NGC0017", "*", null), 12), Is.False);
            Assert.That(SystemEndpoints.PassesDsoCatalogCull(Row("NGC0018", "Other", null), 12), Is.False);
        }

        [Test]
        public void WolfRayet_stars_pass_regardless_of_magnitude() {
            // Mostly v 10–17: without the bypass the whole package would be culled and the
            // client's offline search could never resolve a WR number.
            Assert.That(SystemEndpoints.PassesDsoCatalogCull(Row("WR 134", "WR*", 7.99), 12), Is.True);
            Assert.That(SystemEndpoints.PassesDsoCatalogCull(Row("WR 3-1", "WR*", 14.96), 12), Is.True);
            Assert.That(SystemEndpoints.PassesDsoCatalogCull(Row("WR 2-1", "WR*", null), 12), Is.True);
        }

        [Test]
        public void Every_merged_dso_package_has_a_reader_parser() {
            // A package in DsoPackages without a parser answers 404 on
            // /data-manager/{id}/catalog while its siblings return rows — the drift
            // wr-stars shipped with.
            foreach (var id in SkyCatalogService.DsoPackages) {
                Assert.That(SkyCatalogReader.HasParser(id), Is.True, $"{id} has no reader parser");
            }
        }
    }
}
