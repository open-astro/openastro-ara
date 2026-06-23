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
            _svc = new DataManagerService(_root, new UnusedFetcher(), new NullBroadcaster(), NullLogger<DataManagerService>.Instance);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_root)) {
                Directory.Delete(_root, recursive: true);
            }
        }

        // Install a package by writing a file of `bytes` length into {root}/{id}/ plus the .installed sentinel that
        // marks a completed install (the inventory layer reads the sentinel, not a bare dir).
        private long InstallPackage(string id, int bytes) {
            var dir = Path.Combine(_root, id);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "data.bin"), new byte[bytes]);
            File.WriteAllText(Path.Combine(dir, SkyDataInstaller.InstalledMarkerFileName), "stamp");
            return bytes;
        }

        [Test]
        public void Every_catalog_entry_advertises_a_positive_size_and_an_https_source() {
            // Build gate: a 0-size entry would fall back to the generous unknown-size extraction ceiling, and a
            // non-https source would be refused at fetch — catch either as soon as a catalog entry is added.
            foreach (var pkg in DataManagerService.Catalog) {
                Assert.That(pkg.SizeBytes, Is.GreaterThan(0), $"catalog entry '{pkg.Id}' must advertise a positive size");
                Assert.That(pkg.SourceUrl, Is.Not.Null, $"catalog entry '{pkg.Id}' must have a source URL");
                Assert.That(pkg.SourceUrl!.Scheme, Is.EqualTo("https"), $"catalog entry '{pkg.Id}' source must be https");
            }
        }

        [Test]
        public void Every_catalog_entry_has_a_supported_download_format() {
            // Build gate: a curator who adds an entry with an unhandled extension (.zip/.parquet/…) would otherwise
            // have it silently written as raw bytes to catalog.csv at runtime — catch it here instead.
            foreach (var pkg in DataManagerService.Catalog) {
                Assert.That(DataManagerService.IsSupportedDownloadFormat(pkg.SourceUrl!), Is.True,
                    $"catalog entry '{pkg.Id}' has an unsupported source format: {pkg.SourceUrl!.AbsolutePath}");
            }
        }

        [Test]
        public async Task Lists_the_catalog_with_install_state_read_from_disk() {
            var installedBytes = InstallPackage("hyg-stars", 2048);

            var packages = await _svc.ListPackagesAsync(CancellationToken.None);

            Assert.That(packages.Select(p => p.Id), Is.EquivalentTo(DataManagerService.Catalog.Select(c => c.Id)),
                "every catalog package is listed");
            var stars = packages.Single(p => p.Id == "hyg-stars");
            var dso = packages.Single(p => p.Id == "openngc-dso");
            Assert.Multiple(() => {
                Assert.That(stars.IsInstalled, Is.True);
                Assert.That(stars.SizeBytes, Is.EqualTo(installedBytes), "installed size is measured from disk, not the catalog");
                Assert.That(stars.InstalledUtc, Is.Not.Null);
                Assert.That(dso.IsInstalled, Is.False, "an absent package is not installed");
                Assert.That(dso.InstalledUtc, Is.Null);
            });
        }

        [Test]
        public async Task A_dir_without_the_sentinel_reads_as_not_installed() {
            // A torn/interrupted install (files present, no .installed sentinel) must NOT count as installed.
            var dir = Path.Combine(_root, "hyg-stars");
            Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(Path.Combine(dir, "data.bin"), new byte[1024]);

            var listed = await _svc.ListPackagesAsync(CancellationToken.None);
            Assert.That(listed.Single(p => p.Id == "hyg-stars").IsInstalled, Is.False, "no sentinel → not installed");

            var state = await _svc.GetStateAsync(CancellationToken.None);
            Assert.That(state.InstalledPackageCount, Is.EqualTo(0), "a torn install isn't counted");
            Assert.That(state.TotalInstalledBytes, Is.EqualTo(0));
        }

        [Test]
        public async Task InstalledUtc_and_size_come_from_the_sentinel_not_the_dir() {
            InstallPackage("hyg-stars", 2048);
            var sentinel = Path.Combine(_root, "hyg-stars", SkyDataInstaller.InstalledMarkerFileName);
            var stamp = new System.DateTime(2025, 1, 2, 3, 4, 5, System.DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(sentinel, stamp);

            var stars = (await _svc.ListPackagesAsync(CancellationToken.None)).Single(p => p.Id == "hyg-stars");

            Assert.That(stars.IsInstalled, Is.True);
            Assert.That(stars.InstalledUtc!.Value.UtcDateTime, Is.EqualTo(stamp), "InstalledUtc is the sentinel's write time");
            Assert.That(stars.SizeBytes, Is.EqualTo(2048), "the sentinel file itself is excluded from the measured size");
        }

        [Test]
        public async Task State_reflects_installed_count_and_total_size() {
            InstallPackage("hyg-stars", 2048);
            InstallPackage("openngc-dso", 512);

            var state = await _svc.GetStateAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(state.InstalledPackageCount, Is.EqualTo(2));
                Assert.That(state.TotalInstalledBytes, Is.EqualTo(2048 + 512));
                Assert.That(state.ActiveDownloads, Is.Empty);
            });
        }

        [Test]
        public async Task Delete_removes_an_installed_package() {
            InstallPackage("hyg-stars", 1024);

            var deleted = await _svc.DeleteAsync("hyg-stars", CancellationToken.None);

            Assert.That(deleted, Is.True);
            Assert.That(Directory.Exists(Path.Combine(_root, "hyg-stars")), Is.False);
            var after = await _svc.ListPackagesAsync(CancellationToken.None);
            Assert.That(after.Single(p => p.Id == "hyg-stars").IsInstalled, Is.False);
        }

        [Test]
        public async Task Delete_returns_false_for_an_uninstalled_or_unknown_package() {
            Assert.That(await _svc.DeleteAsync("openngc-dso", CancellationToken.None), Is.False,
                "a catalog package that isn't installed has nothing to delete");
            Assert.That(await _svc.DeleteAsync("not-a-real-package", CancellationToken.None), Is.False,
                "an unknown id deletes nothing");
        }

        [Test]
        public async Task Download_accepts_a_known_catalog_package() {
            var accepted = await _svc.DownloadAsync(
                new DownloadRequestDto(PackageId: "hyg-stars", ForceReinstall: false), "idem-1", CancellationToken.None);

            Assert.That(accepted.OperationType, Is.EqualTo("data-manager.download"));
            Assert.That(accepted.IdempotencyKey, Is.EqualTo("idem-1"));
        }

        [Test]
        public void Download_rejects_an_unknown_package_id() {
            Assert.That(
                async () => await _svc.DownloadAsync(
                    new DownloadRequestDto(PackageId: "../etc/passwd", ForceReinstall: false), null, CancellationToken.None),
                Throws.InstanceOf<PackageNotFoundException>(),
                "a non-catalog id (incl. a traversal attempt) must be rejected, not silently accepted");
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

        [Test]
        public async Task ReadCatalogAsync_is_null_until_installed_then_parses_catalog_csv() {
            // Not installed → null; an unknown / non-catalog id → null (never touches the disk).
            Assert.That(await _svc.ReadCatalogAsync("hyg-stars", null, null, CancellationToken.None), Is.Null);
            Assert.That(await _svc.ReadCatalogAsync("not-a-catalog", null, null, CancellationToken.None), Is.Null);

            // Install hyg-stars with a catalog.csv + the completed-install sentinel.
            var dir = Path.Combine(_root, "hyg-stars");
            Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(Path.Combine(dir, DataManagerService.CatalogFileName),
                "id,proper,ra,dec,mag\n0,Sol,0.0,0.0,4.85\n");
            await File.WriteAllTextAsync(Path.Combine(dir, SkyDataInstaller.InstalledMarkerFileName), "stamp");

            var rows = await _svc.ReadCatalogAsync("hyg-stars", null, null, CancellationToken.None);
            Assert.That(rows, Is.Not.Null);
            Assert.That(rows!.Single().Name, Is.EqualTo("Sol"));

            // Sentinel present but no catalog.csv (a torn/partial state, or removed mid-read) → null, not a throw/500.
            var bare = Path.Combine(_root, "openngc-dso");
            Directory.CreateDirectory(bare);
            await File.WriteAllTextAsync(Path.Combine(bare, SkyDataInstaller.InstalledMarkerFileName), "stamp");
            Assert.That(await _svc.ReadCatalogAsync("openngc-dso", null, null, CancellationToken.None), Is.Null);
        }
    }
}
