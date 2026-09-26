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
        public void Blank_index_path_keeps_the_legacy_value() {
            var store = new InMemoryProfileStore();
            store.PutPlateSolveSettings(store.GetPlateSolveSettings() with { IndexDownloadPath = "  " });
            var legacy = new HeadlessProfileService();
            legacy.ActiveProfile.PlateSolveSettings.ASTAPDatabaseLocation = "/opt/astap";

            LegacyProfileBridge.SyncPlateSolve(legacy, store);

            Assert.That(legacy.ActiveProfile.PlateSolveSettings.ASTAPDatabaseLocation, Is.EqualTo("/opt/astap"));
        }

        [Test]
        public void Fresh_defaults_point_at_debians_astap_cli() {
            var store = new InMemoryProfileStore();
            Assert.That(store.GetPlateSolveSettings().PathOrEndpoint, Is.EqualTo("/usr/bin/astap_cli"));
            Assert.That(ProfileSnapshotNormalizer.Defaults.PlateSolve.PathOrEndpoint, Is.EqualTo("/usr/bin/astap_cli"));
        }
    }
}
