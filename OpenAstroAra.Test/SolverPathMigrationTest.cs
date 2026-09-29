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
    /// #1121 — a profile from before #1094 still says <c>/usr/bin/astap</c>, which Debian's
    /// <c>astap-cli</c> package never installs. The normalizer rewrites it to <c>/usr/bin/astap_cli</c>
    /// when that is the binary actually on disk, and boot warns when the configured binary is missing.
    /// </summary>
    [TestFixture]
    public class SolverPathMigrationTest {

        private static Func<string, bool> Present(params string[] paths) => p => paths.Contains(p);

        private static PlateSolveSettingsDto WithPath(string path) =>
            ProfileSnapshotNormalizer.Defaults.PlateSolve with { PathOrEndpoint = path };

        [Test]
        public void Old_astap_path_moves_to_astap_cli_when_only_astap_cli_is_installed() {
            var migrated = SolverPathMigration.Migrate(WithPath("/usr/bin/astap"), Present("/usr/bin/astap_cli"));
            Assert.That(migrated.PathOrEndpoint, Is.EqualTo("/usr/bin/astap_cli"));
        }

        [Test]
        public void Old_astap_path_stays_when_the_gui_package_is_installed() {
            var ps = WithPath("/usr/bin/astap");
            Assert.That(SolverPathMigration.Migrate(ps, Present("/usr/bin/astap", "/usr/bin/astap_cli")), Is.SameAs(ps));
        }

        [Test]
        public void Old_astap_path_stays_when_astap_cli_is_missing_too() {
            var ps = WithPath("/usr/bin/astap");
            Assert.That(SolverPathMigration.Migrate(ps, Present()), Is.SameAs(ps));
        }

        [Test]
        public void Other_paths_are_never_touched() {
            var ps = WithPath("/opt/astap/astap");
            Assert.That(SolverPathMigration.Migrate(ps, Present("/usr/bin/astap_cli")), Is.SameAs(ps));
        }

        [Test]
        public void Normalize_applies_the_migration() {
            var snap = ProfileSnapshotNormalizer.Defaults with { PlateSolve = WithPath("/usr/bin/astap") };
            var normalized = ProfileSnapshotNormalizer.Normalize(snap, Present("/usr/bin/astap_cli"));
            Assert.That(normalized.PlateSolve.PathOrEndpoint, Is.EqualTo("/usr/bin/astap_cli"));
        }

        [Test]
        public void Missing_solver_binary_is_reported() {
            Assert.That(SolverPathMigration.MissingSolverBinary(WithPath("/usr/bin/astap_cli"), Present()),
                Is.EqualTo("/usr/bin/astap_cli"));
            Assert.That(SolverPathMigration.MissingSolverBinary(WithPath("/usr/bin/astap_cli"), Present("/usr/bin/astap_cli")),
                Is.Null);
            Assert.That(SolverPathMigration.MissingSolverBinary(WithPath("  "), Present()), Is.Null);
        }

        [Test]
        public void File_store_rewrites_an_old_profile_on_load_and_persists_it() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-profile-" + Guid.NewGuid().ToString("N"));
            try {
                // Seed profile.json through a store (whose defaults already carry astap_cli), then
                // hand-edit it back to the pre-#1094 value as an upgraded Pi would have it.
                _ = new FileProfileStore(dir);
                var path = Path.Combine(dir, "profile.json");
                File.WriteAllText(path, File.ReadAllText(path).Replace("/usr/bin/astap_cli", "/usr/bin/astap", StringComparison.Ordinal));
                Assert.That(File.ReadAllText(path), Does.Not.Contain("astap_cli"));

                var store = new FileProfileStore(dir, logger: null, fileExists: Present("/usr/bin/astap_cli"));

                Assert.That(store.GetPlateSolveSettings().PathOrEndpoint, Is.EqualTo("/usr/bin/astap_cli"));
                Assert.That(File.ReadAllText(path), Does.Contain("/usr/bin/astap_cli"));
            } finally {
                if (Directory.Exists(dir)) {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }
}
