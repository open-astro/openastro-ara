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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The daemon bundles its solver at <c>/opt/openastroara/astap_cli</c>; a profile that still names
    /// a stock binary (#1121's <c>/usr/bin/astap</c>, Debian's <c>/usr/bin/astap_cli</c>, the astap.nl
    /// <c>.deb</c>'s <c>/opt/astap/astap_cli</c>) is rewritten to it when the bundled binary is on disk,
    /// and boot warns when the configured binary is missing.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class SolverPathMigrationTest {

        private static Func<string, bool> Present(params string[] paths) => p => paths.Contains(p);

        private static PlateSolveSettingsDto WithPath(string path) =>
            ProfileSnapshotNormalizer.Defaults.PlateSolve with { PathOrEndpoint = path };

        private const string Bundled = SolverPathMigration.AstapCliPath;

        [TestCase("/usr/bin/astap")]
        [TestCase("/usr/bin/astap_cli")]
        [TestCase("/opt/astap/astap_cli")]
        public void A_stock_solver_path_moves_to_the_bundled_solver_when_it_is_installed(string stock) {
            var migrated = SolverPathMigration.Migrate(WithPath(stock), Present(stock, Bundled));
            Assert.That(migrated.PathOrEndpoint, Is.EqualTo(Bundled));
        }

        [Test]
        public void A_stock_path_stays_when_the_bundled_solver_is_missing() {
            var ps = WithPath("/usr/bin/astap_cli");
            Assert.That(SolverPathMigration.Migrate(ps, Present("/usr/bin/astap_cli")), Is.SameAs(ps));
        }

        [Test]
        public void The_bundled_path_is_left_alone() {
            var ps = WithPath(Bundled);
            Assert.That(SolverPathMigration.Migrate(ps, Present(Bundled)), Is.SameAs(ps));
        }

        [Test]
        public void Other_paths_are_never_touched() {
            var ps = WithPath("/home/astro/astap/astap_cli");
            Assert.That(SolverPathMigration.Migrate(ps, Present(Bundled)), Is.SameAs(ps));
        }

        [Test]
        public void Normalize_applies_the_migration() {
            var snap = ProfileSnapshotNormalizer.Defaults with { PlateSolve = WithPath("/usr/bin/astap") };
            var normalized = ProfileSnapshotNormalizer.Normalize(snap, Present(Bundled));
            Assert.That(normalized.PlateSolve.PathOrEndpoint, Is.EqualTo(Bundled));
        }

        [Test]
        public void Missing_solver_binary_is_reported() {
            Assert.That(SolverPathMigration.MissingSolverBinary(WithPath(Bundled), Present()),
                Is.EqualTo(Bundled));
            Assert.That(SolverPathMigration.MissingSolverBinary(WithPath(Bundled), Present(Bundled)),
                Is.Null);
            Assert.That(SolverPathMigration.MissingSolverBinary(WithPath("  "), Present()), Is.Null);
        }

        [Test]
        public void File_store_rewrites_an_old_profile_on_load_and_persists_it() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-profile-" + Guid.NewGuid().ToString("N"));
            try {
                // Seed profile.json through a store (whose defaults already carry the bundled solver),
                // then hand-edit it back to the #1094 Debian value as an upgraded Pi would have it.
                _ = new FileProfileStore(dir);
                var path = Path.Combine(dir, "profile.json");
                File.WriteAllText(path, File.ReadAllText(path).Replace(Bundled, "/usr/bin/astap_cli", StringComparison.Ordinal));
                Assert.That(File.ReadAllText(path), Does.Not.Contain(Bundled));

                var store = new FileProfileStore(dir, logger: null, fileExists: Present("/usr/bin/astap_cli", Bundled));

                Assert.That(store.GetPlateSolveSettings().PathOrEndpoint, Is.EqualTo(Bundled));
                Assert.That(File.ReadAllText(path), Does.Contain(Bundled));
            } finally {
                if (Directory.Exists(dir)) {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }
}
