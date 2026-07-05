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
using System.Threading;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §36.8 catalog parse: <see cref="SkyCatalogService.GetAllDsos"/> reads the installed OpenNGC
    /// <c>catalog.csv</c> into the rich entries the Tonight's Sky planner ranks on, including the
    /// optional size / surface-brightness columns and a clean display name.
    /// </summary>
    [TestFixture]
    public class SkyCatalogServiceTest {

        private string _root = null!;

        [SetUp]
        public void SetUp() {
            _root = Path.Combine(Path.GetTempPath(), "ara-skycat-" + Path.GetRandomFileName());
            Directory.CreateDirectory(Path.Combine(_root, "openngc-dso"));
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_root)) {
                Directory.Delete(_root, recursive: true);
            }
        }

        private void WriteCatalog(string body) =>
            File.WriteAllText(Path.Combine(_root, "openngc-dso", "catalog.csv"),
                "Name;Type;RA;Dec;V-Mag;B-Mag;M;NGC;IC;Identifiers;MajAx;MinAx;PosAng;SurfBr;Common names\n" + body);

        [Test]
        public void GetAllDsos_takes_the_first_of_comma_separated_common_names() {
            // OpenNGC lists multiple common names comma-separated in one semicolon-delimited field; the
            // display name must be the first, not the whole "Andromeda Galaxy,Messier 31" string.
            WriteCatalog("NGC0224;G;00:42:44.3;+41:16:09;3.4;;031;0224;;;199.5;70.8;35;22.1;Andromeda Galaxy,Messier 31\n");
            var svc = new SkyCatalogService(_root);

            var dsos = svc.GetAllDsos(CancellationToken.None);

            Assert.That(dsos, Is.Not.Null);
            var m31 = dsos!.Single(d => d.Name == "NGC0224");
            Assert.That(m31.CommonName, Is.EqualTo("Andromeda Galaxy"));
            Assert.That(m31.MajAxArcmin, Is.EqualTo(199.5));
            Assert.That(m31.MinAxArcmin, Is.EqualTo(70.8));
            Assert.That(m31.SurfaceBrightness, Is.EqualTo(22.1));
        }

        [Test]
        public void GetAllDsos_maps_a_leading_comma_common_name_to_null_not_empty() {
            // A leading comma (",Messier 31") would make Split(',')[0] an empty string; it must become
            // null so the `CommonName ?? Name` fallback fires instead of showing a blank display name.
            WriteCatalog("NGC0224;G;00:42:44.3;+41:16:09;3.4;;031;0224;;;199.5;70.8;35;22.1;,Messier 31\n");
            var svc = new SkyCatalogService(_root);

            var o = svc.GetAllDsos(CancellationToken.None)!.Single(d => d.Name == "NGC0224");
            Assert.That(o.CommonName, Is.Null);
        }

        [Test]
        public void GetAllDsos_is_null_when_the_catalog_is_not_installed() {
            // No catalog.csv written → not installed → null (the planner falls back to its starter list).
            Assert.That(new SkyCatalogService(_root).GetAllDsos(CancellationToken.None), Is.Null);
        }

        [Test]
        public void GetAllDsos_leaves_optional_measured_fields_null_when_blank() {
            WriteCatalog("NGC7000;Neb;20:59:00.0;+44:19:00;;;;7000;;;;;;;North America Nebula\n");
            var svc = new SkyCatalogService(_root);

            var o = svc.GetAllDsos(CancellationToken.None)!.Single(d => d.Name == "NGC7000");
            Assert.That(o.CommonName, Is.EqualTo("North America Nebula"));
            Assert.That(o.MajAxArcmin, Is.Null);
            Assert.That(o.SurfaceBrightness, Is.Null);
            Assert.That(o.Magnitude, Is.Null);
        }

        // A miniature OpenNGC slice exercising the membership rules: NGC1027 carries an IC
        // CROSS-REFERENCE (IC column '1824') but is NOT an IC object; IC0434 is a real IC row
        // whose IC column is empty; IC0011 is a duplicate stub (Type Dup) of NGC0281; N0001
        // is a catalogued error (NonEx). Membership must follow the primary designation (Name),
        // not the cross-id columns, and the stubs must not appear anywhere.
        private void WriteMembershipFixture() =>
            WriteCatalog(
                "NGC0224;G;00:42:44.3;+41:16:09;3.4;;031;;;;199.5;70.8;35;22.1;Andromeda Galaxy\n" +
                "NGC1027;OCl;02:42:40.4;+61:35:42;6.7;;;;1824;;20;;;;\n" +
                "IC0434;HII;05:41:00.0;-02:27:00;;7.3;;;;;60;10;;;Horsehead region\n" +
                "IC0011;Dup;00:52:59.3;+56:37:19;;;;0281;;;;;;;\n" +
                "N0001;NonEx;00:00:00.0;+00:00:00;;;;;;;;;;;\n" +
                "NGC3603;OCl;11:15:07.2;-61:15:38;9.1;;;;;C 076;;;;;\n");

        private static readonly string[] ExpectedNgcNames = { "NGC 224", "NGC 1027", "NGC 3603" };
        private static readonly string[] ExpectedIcNames = { "IC 434" };

        [Test]
        public void GetObjects_ngc_and_ic_membership_follows_the_primary_name_not_the_cross_id_columns() {
            WriteMembershipFixture();
            var svc = new SkyCatalogService(_root);

            var ngc = svc.GetObjects("ngc", null, CancellationToken.None)!.Select(o => o.Name).ToList();
            var ic = svc.GetObjects("ic", null, CancellationToken.None)!.Select(o => o.Name).ToList();

            // NGC1027 has IC='1824' (a cross-id) — it must be in NGC only; IC0434 has an empty
            // IC column — it must still be in IC. The Dup/NonEx stubs are in neither.
            Assert.That(ngc, Is.EquivalentTo(ExpectedNgcNames));
            Assert.That(ic, Is.EquivalentTo(ExpectedIcNames));
        }

        [Test]
        public void GetObjects_labels_rows_in_the_requested_catalogs_designation_system() {
            WriteMembershipFixture();
            var svc = new SkyCatalogService(_root);

            // The same physical object (NGC0224 = M 31) is labeled per the catalog asked for.
            Assert.That(svc.GetObjects("messier", null, CancellationToken.None)!.Single().Name,
                Is.EqualTo("M 31"));
            Assert.That(svc.GetObjects("caldwell", null, CancellationToken.None)!.Single().Name,
                Is.EqualTo("C 76"));
            Assert.That(svc.GetObjects("ngc", null, CancellationToken.None)!
                .Select(o => o.Name), Has.Member("NGC 224"));
        }

        [Test]
        public void GetAllDsos_drops_duplicate_and_nonexistent_stub_rows() {
            WriteMembershipFixture();
            var svc = new SkyCatalogService(_root);

            var names = svc.GetAllDsos(CancellationToken.None)!.Select(d => d.Name).ToList();
            Assert.That(names, Has.No.Member("IC0011"));   // Type=Dup stub of NGC0281
            Assert.That(names, Has.No.Member("N0001"));    // Type=NonEx catalogued error
            Assert.That(names, Has.Member("NGC0224"));     // real rows keep their raw OpenNGC key
        }
    }
}
