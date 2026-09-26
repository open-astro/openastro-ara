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
        public void GetObjects_wolf_rayet_lists_WR_rows_from_the_wr_stars_package() {
            // The package's rows spell the name "WR NN" and the type "WR*" (sky-data wr.csv);
            // both the overlay predicate and the cull bypass key on exactly that.
            WriteCatalog("NGC0224;G;00:42:44.3;+41:16:09;3.44;4.36;031;;;C 076;;;;;Andromeda Galaxy\n");
            Directory.CreateDirectory(Path.Combine(_root, "wr-stars"));
            File.WriteAllText(Path.Combine(_root, "wr-stars", "catalog.csv"),
                "Name;Type;RA;Dec;V-Mag;B-Mag;M;NGC;IC;Identifiers;MajAx;MinAx;PosAng;SurfBr;Common names\n" +
                "WR 134;WR*;20:10:14.19;+36:10:34.9;7.99;8.25;;;;HD 191765;;;;;Anon (Chu)\n" +
                "WR 136;WR*;20:12:06.53;+38:21:17.7;7.44;7.65;;;;HD 192163;;;;;NGC 6888\n" +
                "WR 3-1;WR*;01:40:32.96;+63:42:22.9;;16.04;;;;WR-C-01;;;;;\n");
            var svc = new SkyCatalogService(_root);

            var wr = svc.GetObjects("wolf-rayet", null, CancellationToken.None)!;
            Assert.That(wr.Select(o => o.Name), Is.EqualTo(WrBrightestFirst),
                "brightest first; WR 3-1 has no V but B 16.04, which the parser falls back to");
            Assert.That(svc.GetObjects("ngc", null, CancellationToken.None)!.Select(o => o.Name),
                Has.No.Member("WR 134"), "WR rows never leak into the NGC set");
            // And the planning entries carry the type the cull + the client ranker key on.
            Assert.That(svc.GetAllDsos(CancellationToken.None)!.Where(d => d.Name.StartsWith("WR ", StringComparison.Ordinal))
                .Select(d => d.Type).Distinct(), Is.EqualTo(WrTypeOnly));
        }

        private static readonly string[] WrBrightestFirst = { "WR 136", "WR 134", "WR 3-1" };
        private static readonly string[] WrTypeOnly = { "WR*" };

        // Verbatim rows from the pinned sky-data wr.csv (29 columns). WR 21 and WR 30 list
        // two spectral classifications; the first build separated them with ";" — the
        // file's own separator — so everything right of Hubble shifted and Identifiers
        // read as the common name (review #1107: WR 21 became "HD 90657").
        private const string WrRealHeader =
            "Name;Type;RA;Dec;Const;MajAx;MinAx;PosAng;B-Mag;V-Mag;J-Mag;H-Mag;K-Mag;SurfBr;Hubble;Pax;Pm-RA;Pm-Dec;RadVel;Redshift;Cstar U-Mag;Cstar B-Mag;Cstar V-Mag;M;NGC;IC;Cstar Names;Identifiers;Common names\n";
        private const string WrRealRows =
            "WR 21;WR*;10:26:31.40;-58:38:26.1;;;;;10.16;9.71;8.41;8.22;8.03;;WN5o+O4-6, WN5o+O7V;;;;;;;;;;;;;HD 90657,DR3 5255569549619300096;\n" +
            "WR 30;WR*;10:51:05.99;-62:17:01.6;;;;;;11.73;10.05;9.76;9.21;;WC6+O6-8, WC6+O7.5;;;;;;;;;;;;;HD 94305,DR3 5241922754918453760,TYC 8961 618 1;Anon (Marston)\n" +
            "WR 136;WR*;20:12:06.53;+38:21:17.7;;;;;7.65;7.44;6.13;5.90;5.56;;WN6b(h);;;;;;;;;;;;;HD 192163,DR3 2061690233159124352,V1770 Cyg;NGC 6888\n";

        [Test]
        public void Wr_rows_with_two_spectral_types_keep_their_columns_aligned() {
            WriteCatalog("NGC0224;G;00:42:44.3;+41:16:09;3.44;4.36;031;;;C 076;;;;;Andromeda Galaxy\n");
            Directory.CreateDirectory(Path.Combine(_root, "wr-stars"));
            File.WriteAllText(Path.Combine(_root, "wr-stars", "catalog.csv"), WrRealHeader + WrRealRows);
            var svc = new SkyCatalogService(_root);

            var byName = svc.GetAllDsos(CancellationToken.None)!.ToDictionary(d => d.Name);
            Assert.That(byName["WR 21"].CommonName, Is.Null, "Identifiers must not read as the common name");
            Assert.That(byName["WR 30"].CommonName, Is.EqualTo("Anon (Marston)"));
            Assert.That(byName["WR 136"].CommonName, Is.EqualTo("NGC 6888"));
            Assert.That(byName["WR 21"].Magnitude, Is.EqualTo(9.71).Within(1e-6), "V, left of Hubble, never shifted");
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
