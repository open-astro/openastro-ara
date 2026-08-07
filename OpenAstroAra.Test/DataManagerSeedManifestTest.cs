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
    /// §36 offline-first invariant: packaging/seed-manifest.tsv (what the .deb
    /// bundles as seed copies) must stay in lockstep with
    /// <see cref="DataManagerService.Catalog"/> — every curated package with a
    /// source URL is seeded, with the exact pinned URL and expected SHA-256.
    /// A drift here ships a .deb whose seeds fail verification (or omit a
    /// catalog), silently breaking remote-site installs.
    /// </summary>
    [TestFixture]
    public class DataManagerSeedManifestTest {

        private static string FindManifest() {
            var dir = TestContext.CurrentContext.TestDirectory;
            for (var probe = new DirectoryInfo(dir); probe is not null; probe = probe.Parent) {
                var candidate = Path.Combine(probe.FullName, "packaging", "seed-manifest.tsv");
                if (File.Exists(candidate)) return candidate;
            }
            Assert.Fail("packaging/seed-manifest.tsv not found above the test directory");
            return null!; // unreachable
        }

        private static Dictionary<string, (string Url, string Sha)> ReadManifest() {
            var rows = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(FindManifest())) {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var parts = line.Split('\t');
                Assert.That(parts, Has.Length.EqualTo(3), $"malformed manifest row: {line}");
                rows[parts[0]] = (parts[1], parts[2]);
            }
            return rows;
        }

        [Test]
        public void Every_curated_package_is_seeded_with_pinned_url_and_sha() {
            var manifest = ReadManifest();
            foreach (var pkg in DataManagerService.Catalog) {
                if (pkg.SourceUrl is null) continue;
                Assert.That(manifest, Contains.Key(pkg.Id),
                    $"package '{pkg.Id}' missing from seed-manifest.tsv — the .deb would ship without its offline seed");
                var (url, sha) = manifest[pkg.Id];
                Assert.That(url, Is.EqualTo(pkg.SourceUrl.ToString()),
                    $"seed URL for '{pkg.Id}' drifted from the curated SourceUrl");
                Assert.That(DataManagerService.CatalogSha256, Contains.Key(pkg.Id),
                    $"package '{pkg.Id}' has no expected SHA-256 — seeds and downloads both need one");
                Assert.That(sha, Is.EqualTo(DataManagerService.CatalogSha256[pkg.Id]),
                    $"seed SHA for '{pkg.Id}' drifted from CatalogSha256");
            }
        }

        [Test]
        public void Manifest_has_no_orphan_rows() {
            var curatedIds = DataManagerService.Catalog.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in ReadManifest().Keys) {
                Assert.That(curatedIds, Contains.Item(id),
                    $"seed-manifest row '{id}' has no curated package — stale after a removal?");
            }
        }
    }
}
