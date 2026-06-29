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
    }
}
