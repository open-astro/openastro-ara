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
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Solvers;
using System;
using System.IO;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §18.I — the ASTAP star-database directory reaches <c>astap_cli</c> as <c>-d</c>. Before this,
    /// the solver relied on ASTAP's own lookup, which finds nothing on a packaged Pi.
    /// </summary>
    [TestFixture]
    public class ASTAPSolverDatabaseLocationTest {

        private static string Args(ASTAPSolver solver) {
            var parameter = new PlateSolveParameter { FocalLength = 400, PixelSize = 3.75, DownSampleFactor = 2, MaxObjects = 500, SearchRadius = 30 };
            var image = new OpenAstroAra.Image.ImageData.BaseImageData(new ushort[64 * 48], 64, 48, bitDepth: 16, isBayered: false,
                new OpenAstroAra.Image.ImageData.ImageMetaData(), new OpenAstroAra.Server.Services.HeadlessProfileService(), null!, null!);
            return solver.ArgumentsFor("/tmp/x.fits", "/tmp/x.ini", parameter, PlateSolveImageProperties.Create(parameter, image));
        }

        [Test]
        public void Existing_database_directory_is_passed_as_d() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-astap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "d80_0101.1476"), new byte[] { 1 });
            try {
                var solver = new ASTAPSolver("/usr/bin/astap_cli", dir);
                Assert.That(solver.EffectiveDatabaseLocation, Is.EqualTo(dir));
                Assert.That(Args(solver), Does.Contain($"-d \"{dir}\""));
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void Empty_database_directory_counts_as_missing() {
            // The .deb's tmpfiles entry creates /var/lib/astap before any database is downloaded into it.
            var dir = Path.Combine(Path.GetTempPath(), "ara-astap-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {
                var solver = new ASTAPSolver("/usr/bin/astap_cli", dir);
                Assert.That(solver.EffectiveDatabaseLocation, Is.Null);
                Assert.That(Args(solver), Does.Not.Contain("-d "));
            } finally {
                Directory.Delete(dir);
            }
        }

        [Test]
        public void Missing_database_directory_falls_back_to_default_lookup() {
            var solver = new ASTAPSolver("/usr/bin/astap_cli", "/nonexistent/ara-astap-" + Guid.NewGuid().ToString("N"));
            Assert.That(solver.EffectiveDatabaseLocation, Is.Null);
            Assert.That(Args(solver), Does.Not.Contain("-d "));
        }

        [Test]
        public void Unset_database_directory_passes_nothing() {
            Assert.That(Args(new ASTAPSolver("/usr/bin/astap_cli", null)), Does.Not.Contain("-d "));
            Assert.That(Args(new ASTAPSolver("/usr/bin/astap_cli", "  ")), Does.Not.Contain("-d "));
        }
    }
}
