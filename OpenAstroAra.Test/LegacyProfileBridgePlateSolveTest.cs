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

namespace OpenAstroAra.Test {

    /// <summary>
    /// §18.I — the ARA store's plate-solve section reaches the legacy settings the solver factory
    /// reads. The database-directory mapping is the one line that makes <c>-d</c> reach production.
    /// </summary>
    [TestFixture]
    public class LegacyProfileBridgePlateSolveTest {

        [Test]
        public void Index_path_and_binary_path_reach_the_legacy_settings() {
            var store = new InMemoryProfileStore();
            store.PutPlateSolveSettings(store.GetPlateSolveSettings() with {
                PathOrEndpoint = "/usr/bin/astap_cli",
                IndexDownloadPath = "/var/lib/astap",
            });
            var legacy = new HeadlessProfileService();

            LegacyProfileBridge.SyncPlateSolve(legacy, store);

            Assert.Multiple(() => {
                Assert.That(legacy.ActiveProfile.PlateSolveSettings.ASTAPLocation, Is.EqualTo("/usr/bin/astap_cli"));
                Assert.That(legacy.ActiveProfile.PlateSolveSettings.ASTAPDatabaseLocation, Is.EqualTo("/var/lib/astap"));
            });
        }

        [Test]
        public void Blank_index_path_means_the_deb_default_directory() {
            // #1136 (review round 3): a shared profile strips the path and a user can clear the field; the Data
            // Manager installs under the default directory in that case, so the solver must look there too
            // rather than keep whatever the legacy object held.
            var store = new InMemoryProfileStore();
            store.PutPlateSolveSettings(store.GetPlateSolveSettings() with { IndexDownloadPath = "  " });
            var legacy = new HeadlessProfileService();
            legacy.ActiveProfile.PlateSolveSettings.ASTAPDatabaseLocation = "/opt/astap";

            LegacyProfileBridge.SyncPlateSolve(legacy, store);

            Assert.That(legacy.ActiveProfile.PlateSolveSettings.ASTAPDatabaseLocation, Is.EqualTo(SolverPathMigration.DefaultDatabasePath));
        }

        [Test]
        public void Downsample_zero_and_the_timeout_reach_the_legacy_settings() {
            // #1136 — 0 = automatic from the pixel scale, so it must pass through rather than keep the
            // legacy value; the per-solve timeout was in the store but never reached the solver.
            var store = new InMemoryProfileStore();
            store.PutPlateSolveSettings(store.GetPlateSolveSettings() with { DownsampleFactor = 0, TimeoutSeconds = 45 });
            var legacy = new HeadlessProfileService();
            legacy.ActiveProfile.PlateSolveSettings.DownSampleFactor = 2;

            LegacyProfileBridge.SyncPlateSolve(legacy, store);

            Assert.Multiple(() => {
                Assert.That(legacy.ActiveProfile.PlateSolveSettings.DownSampleFactor, Is.EqualTo(0));
                Assert.That(legacy.ActiveProfile.PlateSolveSettings.SolveTimeoutSeconds, Is.EqualTo(45));
                Assert.That(PlateSolveService.TimeoutFrom(legacy.ActiveProfile.PlateSolveSettings), Is.EqualTo(System.TimeSpan.FromSeconds(45)));
            });
        }

        [Test]
        public void Fresh_defaults_solve_with_automatic_downsample() {
            Assert.That(new InMemoryProfileStore().GetPlateSolveSettings().DownsampleFactor, Is.EqualTo(0));
            Assert.That(ProfileSnapshotNormalizer.Defaults.PlateSolve.DownsampleFactor, Is.EqualTo(0));
        }

        [Test]
        public void Fresh_defaults_point_at_the_bundled_astap_cli() {
            var store = new InMemoryProfileStore();
            Assert.That(store.GetPlateSolveSettings().PathOrEndpoint, Is.EqualTo(SolverPathMigration.AstapCliPath));
            Assert.That(ProfileSnapshotNormalizer.Defaults.PlateSolve.PathOrEndpoint, Is.EqualTo(SolverPathMigration.AstapCliPath));
        }
    }
}
