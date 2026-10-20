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
using OpenAstroAra.PlateSolving;
using OpenAstroAra.PlateSolving.Solvers;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// #1136 — the ASTAP star databases as Data Manager packages: they install under the profile's solver
    /// index path (one subdirectory each), the solver finds them there, a redirect to GitHub's asset store
    /// is followed and nothing else is, the Recommended flag follows the field of view, and a package that
    /// cannot fit on the volume is refused before any byte is downloaded.
    /// </summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class AstapDatabasePackagesTest {

        private string _root = null!;
        private string _solverRoot = null!;

        [SetUp]
        public void SetUp() {
            _root = Path.Combine(Path.GetTempPath(), "ara-astap-pkg-" + Path.GetRandomFileName());
            _solverRoot = Path.Combine(_root, "astap");
            Directory.CreateDirectory(_solverRoot);
            // No InvalidateCache here: the scan cache is keyed by directory and every test uses a fresh one, and
            // clearing the process-wide cache races AstapStarDatabaseTest's reference-equality check when the
            // fixtures run in parallel.
        }

        [TearDown]
        public void TearDown() {
            for (var attempt = 0; ; attempt++) {
                try {
                    if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
                    return;
                } catch (IOException) when (attempt < 20) {
                    Thread.Sleep(25);
                }
            }
        }

        private static readonly string[] D80 = { "d80" };
        private static readonly string[] D80AndG05 = { "d80", "g05" };
        private static readonly string[] D80AndH18 = { "d80", "h18" };
        private static readonly string[] D80G05W08 = { "d80", "g05", "w08" };
        private static readonly string[] ThreeIds = { "astap-d80", "astap-g05", "astap-w08" };

        private static void Touch(string dir, string name) {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name), new byte[] { 1 });
        }

        // ── the scan sees one level of package subdirectories ──

        [Test]
        public void Databases_in_package_subdirectories_are_found_with_their_directory() {
            var d80 = Path.Combine(_solverRoot, "astap-d80");
            var g05 = Path.Combine(_solverRoot, "astap-g05");
            Touch(d80, "d80_0101.1476");
            Touch(d80, "d80_0102.1476");
            Touch(g05, "g05_0101.290");
            Touch(g05, "g05_0201.290");
            Touch(Path.Combine(_solverRoot, ".staging-abc"), "w08_0101.001"); // an install in progress: never read
            Touch(d80, SkyDataInstaller.InstalledMarkerFileName);

            var scan = AstapStarDatabase.Scan(_solverRoot);
            Assert.Multiple(() => {
                Assert.That(scan.Databases, Is.EqualTo(D80AndG05));
                Assert.That(AstapStarDatabase.DirectoryFor(_solverRoot, "d80"), Is.EqualTo(d80));
                Assert.That(AstapStarDatabase.DirectoryFor(_solverRoot, "G05"), Is.EqualTo(g05), "case-insensitive lookup");
                Assert.That(AstapStarDatabase.DirectoryFor(_solverRoot, "w08"), Is.Null, "a staging dir is not an install");
                Assert.That(AstapStarDatabase.EffectiveLocation(_solverRoot), Is.EqualTo(_solverRoot), "files in subdirectories make the path effective");
                Assert.That(scan.FileCount, Is.EqualTo(5));
            });
        }

        [Test]
        public void A_manual_install_in_the_path_itself_wins_over_a_package_copy() {
            Touch(_solverRoot, "d80_0101.1476");
            Touch(_solverRoot, "d80_0102.1476");
            Touch(Path.Combine(_solverRoot, "astap-d80"), "d80_0101.1476");
            Touch(Path.Combine(_solverRoot, "astap-d80"), "d80_0102.1476");

            Assert.That(AstapStarDatabase.DirectoryFor(_solverRoot, "d80"), Is.EqualTo(_solverRoot));
            Assert.That(AstapStarDatabase.PartialDatabases(_solverRoot), Is.Empty);
        }

        [Test]
        public void A_partial_copy_is_not_reported_when_a_complete_one_exists_elsewhere() {
            Touch(_solverRoot, "d80_0101.1476"); // one orphan tile in the root
            var pkg = Path.Combine(_solverRoot, "astap-d80");
            Touch(pkg, "d80_0101.1476");
            Touch(pkg, "d80_0102.1476");

            Assert.That(AstapStarDatabase.Databases(_solverRoot), Is.EqualTo(D80));
            Assert.That(AstapStarDatabase.PartialDatabases(_solverRoot), Is.Empty);
            Assert.That(AstapStarDatabase.DirectoryFor(_solverRoot, "d80"), Is.EqualTo(pkg));
        }

        [Test]
        public void A_tile_written_into_a_subdirectory_invalidates_the_cached_scan() {
            var pkg = Path.Combine(_solverRoot, "astap-d80");
            Touch(pkg, "d80_0101.1476");
            var first = AstapStarDatabase.Scan(_solverRoot);
            Assert.That(first.Partial, Is.EqualTo(D80));

            Touch(pkg, "d80_0102.1476");
            Directory.SetLastWriteTimeUtc(pkg, DateTime.UtcNow.AddSeconds(5)); // coarse-mtime filesystems
            var second = AstapStarDatabase.Scan(_solverRoot);
            Assert.That(ReferenceEquals(second, first), Is.False);
            Assert.That(second.Databases, Is.EqualTo(D80));
        }

        // ── the solver points -d at the chosen database's directory ──

        private static string Args(ASTAPSolver solver) {
            var parameter = new PlateSolveParameter { FocalLength = 400, PixelSize = 3.75, DownSampleFactor = 2, MaxObjects = 500, SearchRadius = 30 };
            var image = new OpenAstroAra.Image.ImageData.BaseImageData(new ushort[64 * 48], 64, 48, bitDepth: 16, isBayered: false,
                new OpenAstroAra.Image.ImageData.ImageMetaData(), new OpenAstroAra.Server.Services.HeadlessProfileService(), null!, null!);
            return solver.ArgumentsFor("/tmp/x.fits", "/tmp/x.ini", parameter, PlateSolveImageProperties.Create(parameter, image));
        }

        [Test]
        public void The_only_database_in_a_package_subdirectory_is_passed_as_d_without_D() {
            var pkg = Path.Combine(_solverRoot, "astap-d80");
            Touch(pkg, "d80_0101.1476");
            Touch(pkg, "d80_0102.1476");

            var args = Args(new ASTAPSolver("/usr/bin/astap_cli", _solverRoot));
            Assert.That(args, Does.Contain($"-d \"{pkg}\""));
            Assert.That(args, Does.Not.Contain("-D "));
        }

        [Test]
        public void Several_packages_name_one_and_point_d_at_its_directory() {
            // The test frame is ~0.03° tall: narrower than D80's range, so H18 wins over D80.
            var d80 = Path.Combine(_solverRoot, "astap-d80");
            var h18 = Path.Combine(_solverRoot, "astap-h18");
            foreach (var t in new[] { "d80_0101.1476", "d80_0102.1476" }) Touch(d80, t);
            foreach (var t in new[] { "h18_0101.1476", "h18_0102.1476" }) Touch(h18, t);

            Assert.That(Args(new ASTAPSolver("/usr/bin/astap_cli", _solverRoot)), Does.Contain($"-d \"{h18}\" -D h18"));
        }

        // ── field-of-view bands follow ASTAP's own auto-selection ──

        [TestCase(2.0, "d80")]
        [TestCase(6.0, "d80")]
        [TestCase(6.5, "g05")]   // above 6°: the 500 stars/deg² database
        [TestCase(15.0, "g05")]  // a 135 mm lens on a full-frame sensor
        [TestCase(20.0, "g05")]
        [TestCase(25.0, "w08")]  // above 20°: all-sky
        public void Wide_fields_pick_g05_then_w08(double fovDeg, string expected) {
            Assert.That(AstapStarDatabase.Select(D80G05W08, fovDeg), Is.EqualTo(expected));
        }

        [Test]
        public void A_wide_field_without_g05_still_solves_through_d80() {
            Assert.That(AstapStarDatabase.Select(D80AndH18, 15.0), Is.EqualTo("d80"));
        }

        // ── redirects: GitHub's asset store only ──

        private static readonly Uri Source = new("https://github.com/open-astro/sky-data/releases/download/astap-2026.10/astap-d80.tar.gz");

        [TestCase("https://objects.githubusercontent.com/github-production-release-asset/abc?x=1", true)]
        [TestCase("https://release-assets.githubusercontent.com/x/y", true)]
        [TestCase("https://github.com/open-astro/sky-data/releases/download/x/y.tar.gz", true)]
        [TestCase("http://objects.githubusercontent.com/x", false)]           // downgrade
        [TestCase("https://evil.example/x.tar.gz", false)]                    // another host
        [TestCase("https://githubusercontent.com.evil.example/x", false)]     // suffix trick
        [TestCase("https://raw.githubusercontent.com/someone/repo/main/x", false)]  // user-hostable, not an asset store
        [TestCase("https://gist.githubusercontent.com/someone/x", false)]
        [TestCase("https://notgithub.com/x", false)]
        public void Only_https_redirects_to_github_hosts_are_trusted(string location, bool trusted) {
            Assert.That(HttpSkyDataFetcher.IsTrustedRedirect(Source, new Uri(location)), Is.EqualTo(trusted));
        }

        [Test]
        public void A_missing_or_relative_location_is_not_trusted() {
            Assert.That(HttpSkyDataFetcher.IsTrustedRedirect(Source, null), Is.False);
            Assert.That(HttpSkyDataFetcher.IsTrustedRedirect(Source, new Uri("/x", UriKind.Relative)), Is.False);
        }

        // ── Data Manager: install root, Recommended, disk space ──

        private static readonly IReadOnlyList<DataPackageDto> TestCatalog = new[] {
            new DataPackageDto("astap-d80", "D80", "", DataManagerService.SolverCategory, 1 << 20, "1", false, null,
                new Uri("https://example.test/astap-d80.tar.gz")),
            new DataPackageDto("astap-g05", "G05", "", DataManagerService.SolverCategory, 1 << 20, "1", false, null,
                new Uri("https://example.test/astap-g05.tar.gz")),
            new DataPackageDto("astap-w08", "W08", "", DataManagerService.SolverCategory, 1 << 20, "1", false, null,
                new Uri("https://example.test/astap-w08.tar.gz")),
            new DataPackageDto("hyg-stars", "HYG", "", "catalog", 1 << 20, "1", false, null,
                new Uri("https://example.test/hyg.csv"), Recommended: true),
        };

        private static readonly IReadOnlyDictionary<string, string> NoDigests = new Dictionary<string, string>();

        private DataManagerService NewService(ISkyDataFetcher fetcher, IWsBroadcaster ws, double? fov = null, Func<string, long?>? freeBytes = null) =>
            new(_root, fetcher, ws, NullLogger<DataManagerService>.Instance,
                catalog: TestCatalog, expectedDigests: NoDigests,
                solverDataRoot: () => _solverRoot, fieldOfViewDeg: () => fov, freeBytes: freeBytes ?? (_ => null));

        private static byte[] TarGz(params (string name, byte[] data)[] entries) {
            var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
            using (var tar = new System.Formats.Tar.TarWriter(gz, leaveOpen: true)) {
                foreach (var (name, data) in entries) {
                    tar.WriteEntry(new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
                }
            }
            return ms.ToArray();
        }

        private static async Task<bool> Eventually(Func<bool> predicate, int timeoutMs = 5000) {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline) {
                if (predicate()) return true;
                await Task.Delay(15);
            }
            return predicate();
        }

        [Test]
        public async Task A_solver_package_installs_under_the_solver_index_path_and_the_solver_finds_it() {
            var archive = TarGz(("d80_0101.1476", Encoding.UTF8.GetBytes("a")), ("d80_0102.1476", Encoding.UTF8.GetBytes("b")));
            var ws = new CapturingBroadcaster();
            var svc = NewService(new FakeSkyDataFetcher(archive), ws);

            await svc.DownloadAsync(new DownloadRequestDto("astap-d80", ForceReinstall: false), null, CancellationToken.None);
            Assert.That(await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete)), Is.True);

            var pkg = Path.Combine(_solverRoot, "astap-d80");
            Assert.That(File.Exists(Path.Combine(pkg, "d80_0101.1476")), Is.True, "tiles land in {index path}/{package id}");
            Assert.That(Directory.Exists(Path.Combine(_root, "astap-d80")), Is.False, "nothing under the sky-data root");
            Assert.That(AstapStarDatabase.DirectoryFor(_solverRoot, "d80"), Is.EqualTo(pkg), "the solver's scan sees the install");

            var listed = await svc.ListPackagesAsync(CancellationToken.None);
            Assert.That(listed.Single(p => p.Id == "astap-d80").IsInstalled, Is.True);

            Assert.That(await svc.DeleteAsync("astap-d80", CancellationToken.None), Is.EqualTo(PackageDeleteResult.Deleted));
            Assert.That(Directory.Exists(pkg), Is.False);
            Assert.That(AstapStarDatabase.Databases(_solverRoot), Is.Empty, "a delete is seen by the next scan");
        }

        [Test]
        public void A_blank_solver_index_path_means_the_deb_default_where_the_solver_looks() {
            // Review round 3: a blank path (a shared profile strips it; a user can clear the field) used to
            // install under the sky-data root, which no solve ever reads. Both sides now agree on the default.
            var svc = new DataManagerService(_root, new UnusedFetcher(), new NullBroadcaster(), NullLogger<DataManagerService>.Instance,
                catalog: TestCatalog, expectedDigests: NoDigests, solverDataRoot: () => "  ");
            Assert.That(svc.InstallRootFor("astap-d80"), Is.EqualTo(SolverPathMigration.DefaultDatabasePath));
            Assert.That(svc.InstallRootFor("hyg-stars"), Is.EqualTo(_root), "catalogs stay under the sky-data root");
            Assert.That(SolverPathMigration.EffectiveIndexPath("  "), Is.EqualTo(SolverPathMigration.DefaultDatabasePath));
            Assert.That(SolverPathMigration.EffectiveIndexPath(null), Is.EqualTo(SolverPathMigration.DefaultDatabasePath));
            Assert.That(SolverPathMigration.EffectiveIndexPath(" /srv/astap "), Is.EqualTo("/srv/astap"));
        }

        [Test]
        public void The_unknown_size_ceiling_follows_the_largest_catalog_entry() {
            // Pinned on purpose: with D80 in the catalog an unsized entry would be allowed ~1.66 GB of extraction.
            // Every curated entry carries a size (the catalog test above), so the fallback is never used today.
            var largest = DataManagerService.Catalog.Max(p => p.SizeBytes);
            Assert.That(DataManagerService.UnknownSizeCeiling, Is.EqualTo(largest + largest / 4));
            Assert.That(DataManagerService.UnknownSizeCeiling, Is.GreaterThan(1_600_000_000L));
        }

        [TestCase(null, true, false, false)]   // optics unknown: D80 only
        [TestCase(2.0, true, false, false)]
        [TestCase(15.0, true, true, false)]    // a 15° field: G05 too
        [TestCase(45.0, true, true, true)]     // all-sky: everything
        public async Task Recommended_follows_the_field_of_view(double? fov, bool d80, bool g05, bool w08) {
            var svc = NewService(new UnusedFetcher(), new NullBroadcaster(), fov);
            var listed = await svc.ListPackagesAsync(CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(listed.Single(p => p.Id == "astap-d80").Recommended, Is.EqualTo(d80));
                Assert.That(listed.Single(p => p.Id == "astap-g05").Recommended, Is.EqualTo(g05));
                Assert.That(listed.Single(p => p.Id == "astap-w08").Recommended, Is.EqualTo(w08));
                Assert.That(listed.Single(p => p.Id == "hyg-stars").Recommended, Is.True, "a catalog keeps its curated flag");
            });
        }

        [Test]
        public void Field_of_view_height_comes_from_the_optics() {
            // RedCat 51 (250 mm) + IMX571 (3.76 µm, 4176 px tall): 3.10"/px × 4176 = 3.6°.
            var redcat = new OpticsSettingsDto(FocalLengthMm: 250, ReducerFactor: 1.0, SensorWidthPx: 6248, SensorHeightPx: 4176, PixelSizeUm: 3.76);
            Assert.That(DataManagerService.FieldOfViewHeightDeg(redcat), Is.EqualTo(3.6).Within(0.05));
            // A 0.8× reducer widens the field.
            Assert.That(DataManagerService.FieldOfViewHeightDeg(redcat with { ReducerFactor = 0.8 }), Is.EqualTo(4.5).Within(0.05));
            // A 135 mm lens on the same sensor: 6.7° → G05 territory.
            Assert.That(DataManagerService.FieldOfViewHeightDeg(redcat with { FocalLengthMm = 135 }), Is.GreaterThan(AstapStarDatabase.MediumWideFieldAboveDeg));
            Assert.That(DataManagerService.FieldOfViewHeightDeg(redcat with { FocalLengthMm = 0 }), Is.Null, "unset optics → unknown");
            Assert.That(DataManagerService.FieldOfViewHeightDeg(null), Is.Null);
        }

        [Test]
        public async Task A_package_that_cannot_fit_is_refused_before_any_download() {
            var svc = NewService(new UnusedFetcher(), new NullBroadcaster(), freeBytes: _ => 1024);
            InsufficientDiskSpaceException? ex = null;
            try {
                await svc.DownloadAsync(new DownloadRequestDto("astap-d80", ForceReinstall: false), null, CancellationToken.None);
            } catch (InsufficientDiskSpaceException e) {
                ex = e;
            }
            Assert.That(ex, Is.Not.Null, "the download is refused");
            Assert.That(ex!.FreeBytes, Is.EqualTo(1024));
            Assert.That(ex.NeededBytes, Is.GreaterThanOrEqualTo(1 << 20));
            Assert.That(ex.Message, Does.Contain("astap-d80"));
        }

        [Test]
        public async Task A_repeat_request_while_streaming_joins_the_download_even_when_free_space_reads_low() {
            // The in-flight staging dir eats into free space; a second POST for the same package must get the
            // running download's id, not a 507 off that usage. Free space reads fine for the first call only.
            var gate = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var fetcher = new FakeSkyDataFetcher(async ct => await gate.Task.WaitAsync(ct));
            var calls = 0;
            var svc = NewService(fetcher, new CapturingBroadcaster(), freeBytes: _ => Interlocked.Increment(ref calls) == 1 ? long.MaxValue : 0);

            var first = await svc.DownloadAsync(new DownloadRequestDto("astap-d80", ForceReinstall: false), null, CancellationToken.None);
            var second = await svc.DownloadAsync(new DownloadRequestDto("astap-d80", ForceReinstall: false), null, CancellationToken.None);

            Assert.That(second.OperationId, Is.EqualTo(first.OperationId), "the repeat joins the running download");
            gate.SetResult(TarGz(("d80_0101.1476", new byte[] { 1 }), ("d80_0102.1476", new byte[] { 2 })));
            Assert.That(await Eventually(() => svc.GetStateAsync(CancellationToken.None).Result.ActiveDownloads.Count == 0), Is.True);
        }

        [Test]
        public async Task Unreadable_free_space_does_not_block_a_download() {
            var archive = TarGz(("w08_0101.001", Encoding.UTF8.GetBytes("w")));
            var ws = new CapturingBroadcaster();
            var svc = NewService(new FakeSkyDataFetcher(archive), ws, freeBytes: _ => null);
            await svc.DownloadAsync(new DownloadRequestDto("astap-w08", ForceReinstall: false), null, CancellationToken.None);
            Assert.That(await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadComplete)), Is.True);
        }

        [Test]
        public async Task An_untrusted_redirect_is_reported_as_a_failed_download_with_its_reason() {
            // The fetcher throws InvalidOperationException for a redirect it will not follow; the worker's
            // catch-all turns that into download.failed carrying the message, same as any other fetch error.
            var ws = new CapturingBroadcaster();
            var fetcher = new FakeSkyDataFetcher(_ => throw new InvalidOperationException("Sky-data source redirects to 'http://evil', refusing to follow it."));
            var svc = NewService(fetcher, ws);

            await svc.DownloadAsync(new DownloadRequestDto("astap-w08", ForceReinstall: false), null, CancellationToken.None);

            Assert.That(await Eventually(() => ws.Events.Any(e => e.EventType == WsEventCatalog.DataManagerDownloadFailed)), Is.True);
            var failed = ws.Events.First(e => e.EventType == WsEventCatalog.DataManagerDownloadFailed).Payload;
            Assert.That(failed.GetProperty("error").GetString(), Does.Contain("refusing to follow"));
            Assert.That(Directory.Exists(Path.Combine(_solverRoot, "astap-w08")), Is.False, "nothing installed");
        }

        [Test]
        public async Task Boot_sweeps_crash_leftovers_under_the_solver_root_and_leaves_installs_alone() {
            // A daemon hard-killed mid-extract leaves .staging-*/.backup-* under the index path (up to a 1.3 GB
            // partial D80). StartAsync reclaims them there as Program.cs does for the sky-data root; a package dir
            // and a manual install are untouched.
            var stale = Path.Combine(_solverRoot, ".staging-abc");
            var backup = Path.Combine(_solverRoot, ".backup-def");
            var pkg = Path.Combine(_solverRoot, "astap-d80");
            Touch(stale, "d80_0101.1476");
            Touch(backup, "d80_0101.1476");
            Touch(pkg, "d80_0101.1476");
            Touch(_solverRoot, "w08_0101.001");
            var svc = NewService(new UnusedFetcher(), new NullBroadcaster());

            await ((Microsoft.Extensions.Hosting.IHostedService)svc).StartAsync(CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(Directory.Exists(stale), Is.False, ".staging-* is reclaimed");
                Assert.That(Directory.Exists(backup), Is.False, ".backup-* is reclaimed");
                Assert.That(File.Exists(Path.Combine(pkg, "d80_0101.1476")), Is.True, "a package dir is left alone");
                Assert.That(File.Exists(Path.Combine(_solverRoot, "w08_0101.001")), Is.True, "a manual install is left alone");
            });
            await ((Microsoft.Extensions.Hosting.IHostedService)svc).StopAsync(CancellationToken.None);
        }

        [Test]
        public void Free_space_is_read_from_the_nearest_existing_ancestor() {
            var missing = Path.Combine(_root, "not", "yet", "there");
            Assert.That(DataManagerService.FreeBytesOnDisk(missing), Is.GreaterThan(0));
        }

        // ── the production catalog ──

        [Test]
        public void The_production_catalog_has_the_three_databases_as_release_assets_with_digests() {
            var solver = DataManagerService.Catalog.Where(DataManagerService.IsSolverPackage).ToList();
            Assert.That(solver.Select(p => p.Id), Is.EquivalentTo(ThreeIds));
            foreach (var pkg in solver) {
                Assert.That(pkg.SourceUrl!.Host, Is.EqualTo("github.com"), $"{pkg.Id} is a sky-data release asset");
                Assert.That(DataManagerService.IsArchiveFormat(pkg.SourceUrl!), Is.True, $"{pkg.Id} is a tar.gz");
                Assert.That(DataManagerService.CatalogSha256, Contains.Key(pkg.Id), $"{pkg.Id} has a pinned digest");
            }
            Assert.That(solver.Single(p => p.Id == "astap-d80").SizeBytes, Is.GreaterThan(1_000_000_000L), "D80 advertises its real size so the ceiling fits it");
        }
    }
}
