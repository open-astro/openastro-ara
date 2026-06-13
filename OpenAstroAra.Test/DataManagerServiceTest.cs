#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §36-1 Data Manager inventory layer: install-state is read from the data root on disk,
    /// delete frees the package directory, and a caller-supplied id can't escape the root.
    /// </summary>
    [TestFixture]
    public class DataManagerServiceTest {

        private string _root = null!;
        private DataManagerService _svc = null!;

        [SetUp]
        public void SetUp() {
            _root = Path.Combine(Path.GetTempPath(), "ara-datamgr-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
            _svc = new DataManagerService(_root, NullLogger<DataManagerService>.Instance);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_root)) {
                Directory.Delete(_root, recursive: true);
            }
        }

        // Install a package by writing a file of `bytes` length into {root}/{id}/.
        private long InstallPackage(string id, int bytes) {
            var dir = Path.Combine(_root, id);
            Directory.CreateDirectory(dir);
            var payload = new byte[bytes];
            File.WriteAllBytes(Path.Combine(dir, "data.bin"), payload);
            return bytes;
        }

        [Test]
        public async Task Lists_the_catalog_with_install_state_read_from_disk() {
            var installedBytes = InstallPackage("tycho-2", 2048);

            var packages = await _svc.ListPackagesAsync(CancellationToken.None);

            Assert.That(packages.Select(p => p.Id), Is.EquivalentTo(DataManagerService.Catalog.Select(c => c.Id)),
                "every catalog package is listed");
            var tycho = packages.Single(p => p.Id == "tycho-2");
            var gaia = packages.Single(p => p.Id == "gaia-edr3-bright");
            Assert.Multiple(() => {
                Assert.That(tycho.IsInstalled, Is.True);
                Assert.That(tycho.SizeBytes, Is.EqualTo(installedBytes), "installed size is measured from disk, not the catalog");
                Assert.That(tycho.InstalledUtc, Is.Not.Null);
                Assert.That(gaia.IsInstalled, Is.False, "an absent package is not installed");
                Assert.That(gaia.InstalledUtc, Is.Null);
            });
        }

        [Test]
        public async Task State_reflects_installed_count_and_total_size() {
            InstallPackage("tycho-2", 2048);
            InstallPackage("horizon-default", 512);

            var state = await _svc.GetStateAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(state.InstalledPackageCount, Is.EqualTo(2));
                Assert.That(state.TotalInstalledBytes, Is.EqualTo(2048 + 512));
                Assert.That(state.ActiveDownloads, Is.Empty);
            });
        }

        [Test]
        public async Task Delete_removes_an_installed_package() {
            InstallPackage("tycho-2", 1024);

            var deleted = await _svc.DeleteAsync("tycho-2", CancellationToken.None);

            Assert.That(deleted, Is.True);
            Assert.That(Directory.Exists(Path.Combine(_root, "tycho-2")), Is.False);
            var after = await _svc.ListPackagesAsync(CancellationToken.None);
            Assert.That(after.Single(p => p.Id == "tycho-2").IsInstalled, Is.False);
        }

        [Test]
        public async Task Delete_returns_false_for_an_uninstalled_or_unknown_package() {
            Assert.That(await _svc.DeleteAsync("gaia-edr3-bright", CancellationToken.None), Is.False,
                "a catalog package that isn't installed has nothing to delete");
            Assert.That(await _svc.DeleteAsync("not-a-real-package", CancellationToken.None), Is.False,
                "an unknown id deletes nothing");
        }

        [Test]
        public async Task Delete_of_a_path_traversal_id_touches_nothing_outside_the_root() {
            // A sibling dir next to the data root that a traversal id would target.
            var outside = Path.Combine(Path.GetDirectoryName(_root)!, "ara-victim-" + Path.GetRandomFileName());
            Directory.CreateDirectory(outside);
            try {
                var deleted = await _svc.DeleteAsync("../" + Path.GetFileName(outside), CancellationToken.None);
                Assert.That(deleted, Is.False, "a non-catalog (traversal) id must not map to any directory");
                Assert.That(Directory.Exists(outside), Is.True, "nothing outside the data root may be deleted");
            } finally {
                if (Directory.Exists(outside)) {
                    Directory.Delete(outside, recursive: true);
                }
            }
        }
    }
}
