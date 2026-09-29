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
using OpenAstroAra.PlateSolving.Solvers;
using System;
using System.IO;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1121 — which ASTAP star databases sit in the <c>-d</c> directory, and which one the daemon
    /// names with <c>-D</c> when there is more than one (so ASTAP does not pick on its own).
    /// </summary>
    [TestFixture]
    public class AstapStarDatabaseTest {

        private static readonly string[] D80AndW08 = { "d80", "w08" };

        private string dir = null!;

        [SetUp]
        public void SetUp() {
            dir = Path.Combine(Path.GetTempPath(), "ara-astap-db-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(dir, recursive: true);

        private static string? Pick(double fovDeg, params string[] installed) => AstapStarDatabase.Select(installed, fovDeg);

        private void Touch(string name) => File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 1 });

        [Test]
        public void Databases_are_read_from_the_file_name_prefixes() {
            Touch("d80_0101.1476");
            Touch("d80_0102.1476");
            Touch("W08_0101.001");
            Touch("README.txt");
            Touch("notes_0101.txt");

            Assert.That(AstapStarDatabase.Databases(dir), Is.EqualTo(D80AndW08));
            Assert.That(AstapStarDatabase.CountFiles(dir), Is.EqualTo(5));
        }

        [Test]
        public void Missing_or_unset_directory_has_no_databases() {
            Assert.That(AstapStarDatabase.Databases("/nonexistent/ara-" + Guid.NewGuid().ToString("N")), Is.Empty);
            Assert.That(AstapStarDatabase.Databases(null), Is.Empty);
            Assert.That(AstapStarDatabase.CountFiles(null), Is.Zero);
        }

        [Test]
        public void Effective_location_needs_files() {
            Assert.That(AstapStarDatabase.EffectiveLocation(dir), Is.Null);
            Touch("d80_0101.1476");
            Assert.That(AstapStarDatabase.EffectiveLocation(" " + dir + " "), Is.EqualTo(dir));
            Assert.That(AstapStarDatabase.EffectiveLocation(""), Is.Null);
        }

        [Test]
        public void One_database_is_never_named() {
            Assert.That(Pick(2.0, "d80"), Is.Null);
            Assert.That(Pick(2.0), Is.Null);
        }

        [TestCase(2.0, "d80")]        // D80's range (DEPLOY.md: roughly 0.25° to 30°)
        [TestCase(0.25, "d80")]
        [TestCase(30.0, "d80")]
        [TestCase(0.1, "h18")]        // narrower than D80: the deepest database
        [TestCase(45.0, "w08")]       // wider than D80: the shallowest database
        [TestCase(double.NaN, "d80")] // unknown field: the general-purpose pick
        [TestCase(0.0, "d80")]
        public void Field_of_view_picks_among_several(double fovDeg, string expected) {
            Assert.That(Pick(fovDeg, "w08", "h18", "d80", "g05"), Is.EqualTo(expected));
        }

        [Test]
        public void Each_band_falls_back_down_its_own_order() {
            Assert.That(Pick(2.0, "d20", "d50"), Is.EqualTo("d50"));
            Assert.That(Pick(2.0, "w08", "g05"), Is.EqualTo("g05"));
            Assert.That(Pick(45.0, "w08", "d05"), Is.EqualTo("w08"));
            Assert.That(Pick(45.0, "d05", "d80"), Is.EqualTo("d05"));
            Assert.That(Pick(0.1, "d50", "d80"), Is.EqualTo("d80"));
        }

        [Test]
        public void Unknown_abbreviations_rank_after_known_ones_alphabetically() {
            Assert.That(Pick(2.0, "z99", "w08"), Is.EqualTo("w08"));
            Assert.That(Pick(2.0, "z99", "x12"), Is.EqualTo("x12"));
        }
    }
}
