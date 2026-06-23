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
using OpenAstroAra.Server.Services;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §36 sky-catalog parsing: HYG (comma, decimal-hours RA) + OpenNGC (semicolon, sexagesimal RA/Dec) are normalized
    /// to {name, ra°, dec°, mag}, with bad-position rows skipped and magnitude/limit filters applied.
    /// </summary>
    [TestFixture]
    public class SkyCatalogReaderTest {

        private static MemoryStream S(string text) => new(Encoding.UTF8.GetBytes(text));

        [Test]
        public void Hyg_converts_hours_to_degrees_and_reads_name_and_mag() {
            // ra is in decimal HOURS; dec in degrees. (Minimal header — the reader indexes columns by name.)
            var csv = "id,proper,ra,dec,mag\n" +
                      "0,Sol,0.0,0.0,4.85\n" +
                      "71683,Rigil Kentaurus,14.66,-60.83,-0.01\n";
            var rows = SkyCatalogReader.Read("hyg-stars", S(csv), maxMag: null, limit: null, CancellationToken.None);

            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows[0].Name, Is.EqualTo("Sol"));
            Assert.That(rows[0].RaDeg, Is.EqualTo(0).Within(1e-6));
            Assert.That(rows[1].Name, Is.EqualTo("Rigil Kentaurus"));
            Assert.That(rows[1].RaDeg, Is.EqualTo(14.66 * 15).Within(1e-6), "ra hours × 15 → degrees");
            Assert.That(rows[1].DecDeg, Is.EqualTo(-60.83).Within(1e-6));
            Assert.That(rows[1].Magnitude, Is.EqualTo(-0.01).Within(1e-6));
        }

        [Test]
        public void Hyg_skips_unparseable_positions_and_honours_maxMag_and_limit() {
            var csv = "id,proper,ra,dec,mag\n" +
                      "1,,12.0,45.0,6.5\n" +        // dim → filtered by maxMag
                      "2,Vega,18.6156,38.78,0.03\n" + // bright → kept
                      "3,Bad,notanumber,10,3\n";    // bad ra → skipped
            var rows = SkyCatalogReader.Read("hyg-stars", S(csv), maxMag: 5.0, limit: null, CancellationToken.None);

            Assert.That(rows, Has.Count.EqualTo(1), "the dim star is dropped by maxMag, the bad-position row skipped");
            Assert.That(rows.Single().Name, Is.EqualTo("Vega"));

            const string three = "id,proper,ra,dec,mag\n1,A,1,1,1\n2,B,2,2,2\n3,C,3,3,3\n";
            Assert.That(SkyCatalogReader.Read("hyg-stars", S(three), null, limit: 2, CancellationToken.None),
                Has.Count.EqualTo(2));
            Assert.That(SkyCatalogReader.Read("hyg-stars", S(three), null, limit: 0, CancellationToken.None),
                Is.Empty, "limit 0 returns nothing, not one (the cap is checked before adding)");
        }

        [Test]
        public void Hyg_skips_out_of_range_ra_or_dec() {
            var csv = "id,proper,ra,dec,mag\n" +
                      "1,BadRa,999,10,3\n" +    // ra hours ≥ 24 → skipped
                      "2,BadDec,5,-200,3\n" +   // dec < -90 → skipped
                      "3,Ok,5,10,3\n";
            var rows = SkyCatalogReader.Read("hyg-stars", S(csv), maxMag: null, limit: null, CancellationToken.None);
            Assert.That(rows.Single().Name, Is.EqualTo("Ok"), "a corrupted ra/dec row is skipped, not wrapped");
        }

        [Test]
        public void Hyg_handles_a_quoted_field_containing_the_delimiter() {
            var rows = SkyCatalogReader.Read("hyg-stars",
                S("id,proper,ra,dec,mag\n3,\"Alpha, the first\",1.0,2.0,5.0\n"),
                maxMag: null, limit: null, CancellationToken.None);
            Assert.That(rows.Single().Name, Is.EqualTo("Alpha, the first"), "a quoted name with a comma stays intact");
        }

        [Test]
        public void OpenNgc_parses_sexagesimal_and_picks_the_first_common_name() {
            var csv = "Name;Type;RA;Dec;V-Mag;B-Mag;Common names\n" +
                      "NGC0224;G;00:42:44.33;+41:16:09.4;3.4;4.4;Andromeda Galaxy,M31\n";
            var rows = SkyCatalogReader.Read("openngc-dso", S(csv), maxMag: null, limit: null, CancellationToken.None);

            var m31 = rows.Single();
            Assert.That(m31.Name, Is.EqualTo("Andromeda Galaxy"), "the first comma-separated common name");
            Assert.That(m31.RaDeg, Is.EqualTo(10.6847).Within(1e-3), "00h42m44.33s → degrees");
            Assert.That(m31.DecDeg, Is.EqualTo(41.2693).Within(1e-3), "+41:16:09.4 → degrees");
            Assert.That(m31.Magnitude, Is.EqualTo(3.4).Within(1e-6), "V-Mag");
        }

        [Test]
        public void OpenNgc_skips_positionless_rows_falls_back_to_BMag_and_the_designation() {
            var csv = "Name;Type;RA;Dec;V-Mag;B-Mag;Common names\n" +
                      "NGC9999;G;;;;;\n" +                              // no position → skipped
                      "IC0001;**;00:08:27.05;+27:43:03.6;;13.4;\n";     // V empty → B-Mag; no common name → designation
            var rows = SkyCatalogReader.Read("openngc-dso", S(csv), maxMag: null, limit: null, CancellationToken.None);

            var only = rows.Single();
            Assert.That(only.Name, Is.EqualTo("IC0001"), "falls back to the catalog designation when no common name");
            Assert.That(only.Magnitude, Is.EqualTo(13.4).Within(1e-6), "falls back to B-Mag when V-Mag is absent");
            Assert.That(only.DecDeg, Is.EqualTo(27.7177).Within(1e-3));
        }

        [Test]
        public void OpenNgc_skips_out_of_range_sexagesimal_but_keeps_the_poles() {
            var csv = "Name;Type;RA;Dec;V-Mag;B-Mag;Common names\n" +
                      "BAD1;G;99:99:99;+10:00:00;5;;\n" +   // RA components out of range → skipped
                      "BAD2;G;01:00:00;+99:00:00;5;;\n" +   // Dec > 90 → skipped
                      "BAD3;G;24:30:00;+10:00:00;5;;\n" +   // RA hours ≥ 24 (exclusive upper bound) → skipped
                      "BAD4;G;01:00:00;+90:30:00;5;;\n" +   // Dec 90:30:00 = 90.5° (past the pole) → skipped
                      "BAD5;G;01.5:30:00;+10:00:00;5;;\n" + // non-integral hours component → skipped (not read as 2h)
                      "POLE;G;05:00:00;+90:00:00;5;;\n" +   // Dec exactly 90 (pole) → KEPT
                      "OK;G;01:00:00;+10:00:00;5;;\n";
            var rows = SkyCatalogReader.Read("openngc-dso", S(csv), maxMag: null, limit: null, CancellationToken.None);
            var names = rows.Select(r => r.Name).ToList();
            Assert.That(names, Has.Count.EqualTo(2));
            Assert.That(names, Does.Contain("POLE").And.Contain("OK"),
                "bad/out-of-range positions are skipped; the +90° pole is a valid Dec and is kept");
        }

        [Test]
        public void HasParser_only_for_known_catalog_packages() {
            Assert.That(SkyCatalogReader.HasParser("hyg-stars"), Is.True);
            Assert.That(SkyCatalogReader.HasParser("openngc-dso"), Is.True);
            Assert.That(SkyCatalogReader.HasParser("something-else"), Is.False);
        }
    }
}
