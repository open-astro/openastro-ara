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
using System;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §36-2 install engine: safe + atomic extraction of a sky-data <c>.tar.gz</c>. Covers the happy path
    /// (files + sentinel), tar-slip rejection, replacing a prior install, and that a cancelled/failed install
    /// leaves neither a target nor a leaked staging dir.
    /// </summary>
    [TestFixture]
    public class SkyDataInstallerTest {

        private string _root = null!;

        [SetUp]
        public void SetUp() {
            _root = Path.Combine(Path.GetTempPath(), "ara-installer-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown() {
            if (Directory.Exists(_root)) {
                Directory.Delete(_root, recursive: true);
            }
        }

        // Build an in-memory .tar.gz from (entryName, bytes) pairs. A null payload makes a directory entry.
        private static MemoryStream MakeTarGz(params (string name, byte[]? data)[] entries) {
            var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
            using (var tar = new TarWriter(gz, leaveOpen: true)) {
                foreach (var (name, data) in entries) {
                    if (data is null) {
                        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, name));
                        continue;
                    }
                    tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) {
                        DataStream = new MemoryStream(data),
                    });
                }
            }
            ms.Position = 0;
            return ms;
        }

        private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

        [Test]
        public async Task Installs_files_into_the_target_with_an_install_sentinel() {
            var target = Path.Combine(_root, "tycho-2");
            using var archive = MakeTarGz(
                ("catalog.dat", Bytes("star-data")),
                ("meta/version.txt", Bytes("v2024.10")));

            await SkyDataInstaller.InstallFromTarGzAsync(archive, target, maxBytes: null, remoteLastModified: null, CancellationToken.None);

            Assert.That(File.Exists(Path.Combine(target, "catalog.dat")), Is.True);
            Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "catalog.dat")), Is.EqualTo("star-data"));
            Assert.That(File.Exists(Path.Combine(target, "meta", "version.txt")), Is.True, "nested entries are extracted");

            var sentinel = Path.Combine(target, SkyDataInstaller.InstalledMarkerFileName);
            Assert.That(File.Exists(sentinel), Is.True, "a completed install is marked with the sentinel");
            Assert.That(
                DateTimeOffset.TryParse(await File.ReadAllTextAsync(sentinel), CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out _),
                Is.True, "the sentinel holds a round-trip install timestamp");
        }

        [Test]
        public void Rejects_a_tar_slip_entry_and_writes_nothing() {
            var target = Path.Combine(_root, "evil-pkg");
            using var archive = MakeTarGz(("../escaped.txt", Bytes("pwned")));

            Assert.That(
                async () => await SkyDataInstaller.InstallFromTarGzAsync(archive, target, maxBytes: null, remoteLastModified: null, CancellationToken.None),
                Throws.InstanceOf<InvalidDataException>(), "an entry resolving outside the target is rejected");

            Assert.That(Directory.Exists(target), Is.False, "a rejected install leaves no target dir");
            Assert.That(File.Exists(Path.Combine(_root, "escaped.txt")), Is.False, "nothing is written outside the target");
            Assert.That(TempDirs(), Is.Empty, "the staging dir is cleaned up after the failure");
        }

        [Test]
        public async Task Replaces_a_prior_install() {
            var target = Path.Combine(_root, "horizon-default");
            Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "stale.txt"), "old");

            using var archive = MakeTarGz(("fresh.txt", Bytes("new")));
            await SkyDataInstaller.InstallFromTarGzAsync(archive, target, maxBytes: null, remoteLastModified: null, CancellationToken.None);

            Assert.That(File.Exists(Path.Combine(target, "stale.txt")), Is.False, "the prior install is fully replaced");
            Assert.That(File.Exists(Path.Combine(target, "fresh.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(target, SkyDataInstaller.InstalledMarkerFileName)), Is.True);
        }

        [Test]
        public async Task A_failed_install_preserves_the_prior_install() {
            var target = Path.Combine(_root, "tycho-2");
            using (var good = MakeTarGz(("catalog.dat", Bytes("original")))) {
                await SkyDataInstaller.InstallFromTarGzAsync(good, target, maxBytes: null, remoteLastModified: null, CancellationToken.None);
            }

            // A second install that fails (tar-slip) must not damage the install already on disk.
            using var poisoned = MakeTarGz(("../escaped.txt", Bytes("pwned")));
            Assert.That(
                async () => await SkyDataInstaller.InstallFromTarGzAsync(poisoned, target, maxBytes: null, remoteLastModified: null, CancellationToken.None),
                Throws.InstanceOf<InvalidDataException>());

            Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "catalog.dat")), Is.EqualTo("original"),
                "the prior install is untouched when a re-install fails");
            Assert.That(File.Exists(Path.Combine(target, SkyDataInstaller.InstalledMarkerFileName)), Is.True);
            Assert.That(TempDirs(), Is.Empty, "no staging/backup dirs are leaked");
        }

        [Test]
        public void SweepStaleScratch_removes_orphaned_scratch_dirs_but_keeps_packages() {
            // Simulate a crash aftermath: orphaned scratch dirs alongside a real installed package.
            Directory.CreateDirectory(Path.Combine(_root, ".staging-tycho-2-abc"));
            Directory.CreateDirectory(Path.Combine(_root, ".backup-tycho-2-def"));
            var pkg = Path.Combine(_root, "tycho-2");
            Directory.CreateDirectory(pkg);
            File.WriteAllText(Path.Combine(pkg, "data.bin"), "x");

            var removed = SkyDataInstaller.SweepStaleScratch(_root);

            Assert.That(removed, Is.EqualTo(2));
            Assert.That(Directory.Exists(Path.Combine(_root, ".staging-tycho-2-abc")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(_root, ".backup-tycho-2-def")), Is.False);
            Assert.That(Directory.Exists(pkg), Is.True, "a real package dir is never swept");
            Assert.That(File.Exists(Path.Combine(pkg, "data.bin")), Is.True);
        }

        [Test]
        public void SweepStaleScratch_is_a_no_op_on_a_missing_root() {
            Assert.That(SkyDataInstaller.SweepStaleScratch(Path.Combine(_root, "does-not-exist")), Is.EqualTo(0));
        }

        [Test]
        public void A_pre_cancelled_install_leaves_no_target_and_no_staging_leak() {
            var target = Path.Combine(_root, "gaia-edr3-bright");
            using var archive = MakeTarGz(("catalog.dat", Bytes("data")));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.That(
                async () => await SkyDataInstaller.InstallFromTarGzAsync(archive, target, maxBytes: null, remoteLastModified: null, cts.Token),
                Throws.InstanceOf<OperationCanceledException>());

            Assert.That(Directory.Exists(target), Is.False, "a cancelled install produces no target dir");
            Assert.That(TempDirs(), Is.Empty, "the staging dir is cleaned up on cancellation");
        }

        [Test]
        public void A_mid_extraction_cancel_cleans_up_staging() {
            var target = Path.Combine(_root, "gaia-edr3-bright");
            // Many entries so cancellation fires while the archive is being read, after extraction has begun —
            // exercising the cleanup of a partially-populated staging dir, not just a throw before the first read.
            var entries = Enumerable.Range(0, 64)
                .Select(i => ("file-" + i.ToString(CultureInfo.InvariantCulture) + ".dat", (byte[]?)new byte[4096]))
                .ToArray();
            using var cts = new CancellationTokenSource();
            // The wrapper owns the archive stream (disposes it); cts is owned by the using above. Cancel once a
            // little of the compressed stream has been consumed (mid-extraction, not before it starts).
            using var canceling = new CancelAfterBytesStream(MakeTarGz(entries), cts.Cancel, thresholdBytes: 256);

            Assert.That(
                async () => await SkyDataInstaller.InstallFromTarGzAsync(canceling, target, maxBytes: null, remoteLastModified: null, cts.Token),
                Throws.InstanceOf<OperationCanceledException>());

            Assert.That(Directory.Exists(target), Is.False, "a mid-extraction cancel produces no target dir");
            Assert.That(TempDirs(), Is.Empty, "the partially-populated staging dir is cleaned up");
        }

        [Test]
        public void Rejects_an_archive_exceeding_the_size_ceiling() {
            var target = Path.Combine(_root, "gaia-edr3-bright");
            // Two 4 KiB files = 8 KiB extracted; a 6 KiB ceiling must abort partway and leave nothing behind.
            using var archive = MakeTarGz(("a.dat", new byte[4096]), ("b.dat", new byte[4096]));

            Assert.That(
                async () => await SkyDataInstaller.InstallFromTarGzAsync(archive, target, maxBytes: 6 * 1024, remoteLastModified: null, CancellationToken.None),
                Throws.InstanceOf<InvalidDataException>(), "extraction past the byte ceiling is rejected");

            Assert.That(Directory.Exists(target), Is.False, "an over-limit install produces no target dir");
            Assert.That(TempDirs(), Is.Empty, "the staging dir is cleaned up after the limit abort");
        }

        // ── single-file install (§36 bare CSV / CSV.gz catalogs) ──────────────────────────────────────────

        // Gzip-compress bytes into an in-memory stream (the .csv.gz download shape).
        private static MemoryStream MakeGz(byte[] data) {
            var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true)) {
                gz.Write(data, 0, data.Length);
            }
            ms.Position = 0;
            return ms;
        }

        [Test]
        public async Task Installs_a_plain_file_under_the_given_name_with_a_sentinel() {
            var target = Path.Combine(_root, "openngc-dso");
            using var src = new MemoryStream(Bytes("name;ra;dec\nM31;10.68;41.27\n"));

            await SkyDataInstaller.InstallFromFileAsync(src, target, "catalog.csv", gunzip: false,
                maxBytes: null, remoteLastModified: null, CancellationToken.None);

            Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "catalog.csv")),
                Is.EqualTo("name;ra;dec\nM31;10.68;41.27\n"));
            Assert.That(File.Exists(Path.Combine(target, SkyDataInstaller.InstalledMarkerFileName)), Is.True);
        }

        [Test]
        public async Task Installs_a_gzipped_file_decompressed() {
            var target = Path.Combine(_root, "hyg-stars");
            using var src = MakeGz(Bytes("id,proper,ra,dec\n0,Sol,0,0\n"));

            await SkyDataInstaller.InstallFromFileAsync(src, target, "catalog.csv", gunzip: true,
                maxBytes: null, remoteLastModified: null, CancellationToken.None);

            Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "catalog.csv")),
                Is.EqualTo("id,proper,ra,dec\n0,Sol,0,0\n"), "the .csv.gz is decompressed on install");
        }

        [Test]
        public async Task A_file_exceeding_the_cap_is_rejected_and_the_prior_install_survives() {
            var target = Path.Combine(_root, "hyg-stars");
            using (var first = new MemoryStream(Bytes("original"))) {
                await SkyDataInstaller.InstallFromFileAsync(first, target, "catalog.csv", gunzip: false,
                    maxBytes: null, remoteLastModified: null, CancellationToken.None);
            }

            using var tooBig = new MemoryStream(new byte[8 * 1024]);
            Assert.That(
                async () => await SkyDataInstaller.InstallFromFileAsync(tooBig, target, "catalog.csv", gunzip: false,
                    maxBytes: 6 * 1024, remoteLastModified: null, CancellationToken.None),
                Throws.InstanceOf<InvalidDataException>(), "a file past the byte cap is rejected");

            Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "catalog.csv")), Is.EqualTo("original"),
                "the over-limit install left the prior install intact");
            Assert.That(TempDirs(), Is.Empty, "the staging dir is cleaned up after the cap abort");
        }

        // A dest name that isn't a single plain file — a separator, or the "."/".." relative-dir tokens that
        // GetFileName passes through unchanged and Path.Combine would resolve to the package dir or its parent.
        [TestCase("sub/catalog.csv")]
        [TestCase("sub\\catalog.csv")] // backslash isn't a Linux separator — reject it OS-independently
        [TestCase("..")]
        [TestCase(".")]
        [TestCase("../escaped.csv")]
        public void Rejects_a_dest_file_name_that_is_not_a_plain_file(string destFileName) {
            var target = Path.Combine(_root, "openngc-dso");
            using var src = new MemoryStream(Bytes("x"));
            Assert.That(
                async () => await SkyDataInstaller.InstallFromFileAsync(src, target, destFileName, gunzip: false,
                    maxBytes: null, remoteLastModified: null, CancellationToken.None),
                Throws.InstanceOf<ArgumentException>(),
                $"'{destFileName}' must be rejected — it can't be allowed to escape the package dir");
            Assert.That(Directory.Exists(target), Is.False, "a rejected install creates no target dir");
            Assert.That(TempDirs(), Is.Empty, "a rejected dest name leaks no staging dir");
        }

        // ── SHA-256 integrity (verify-before-swap) ────────────────────────────────────────────────────────

        // Convert.ToHexString returns upper-case hex; the installer compares OrdinalIgnoreCase, so case doesn't matter.
        private static string Sha256Hex(byte[] data) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));

        [Test]
        public async Task Installs_a_file_whose_digest_matches() {
            var target = Path.Combine(_root, "hyg-stars");
            var bytes = Bytes("id,proper\n0,Sol\n");
            using var src = new MemoryStream(bytes);

            await SkyDataInstaller.InstallFromFileAsync(src, target, "catalog.csv", gunzip: false, maxBytes: null,
                remoteLastModified: null, CancellationToken.None, expectedSha256: Sha256Hex(bytes));

            Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "catalog.csv")), Is.EqualTo("id,proper\n0,Sol\n"));
            Assert.That(File.Exists(Path.Combine(target, SkyDataInstaller.InstalledMarkerFileName)), Is.True);
        }

        [Test]
        public async Task Installs_a_gzipped_file_whose_digest_matches_and_decompresses_it() {
            var target = Path.Combine(_root, "hyg-stars");
            var content = Bytes("id,proper,ra,dec\n0,Sol,0,0\n");
            var gz = MakeGz(content).ToArray(); // digest is of the COMPRESSED download
            using var src = new MemoryStream(gz);

            await SkyDataInstaller.InstallFromFileAsync(src, target, "catalog.csv", gunzip: true, maxBytes: null,
                remoteLastModified: null, CancellationToken.None, expectedSha256: Sha256Hex(gz));

            Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "catalog.csv")),
                Is.EqualTo("id,proper,ra,dec\n0,Sol,0,0\n"), "the digest covers the .gz bytes; content is decompressed");
        }

        [Test]
        public async Task A_digest_mismatch_is_rejected_and_the_prior_install_survives() {
            var target = Path.Combine(_root, "hyg-stars");
            using (var first = new MemoryStream(Bytes("original"))) {
                await SkyDataInstaller.InstallFromFileAsync(first, target, "catalog.csv", gunzip: false, maxBytes: null,
                    remoteLastModified: null, CancellationToken.None);
            }

            using var tampered = new MemoryStream(Bytes("tampered"));
            Assert.That(
                async () => await SkyDataInstaller.InstallFromFileAsync(tampered, target, "catalog.csv", gunzip: false,
                    maxBytes: null, remoteLastModified: null, CancellationToken.None,
                    expectedSha256: new string('0', 64)),
                Throws.InstanceOf<InvalidDataException>(), "a download whose digest doesn't match is rejected");

            Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "catalog.csv")), Is.EqualTo("original"),
                "verify happens before the swap, so the prior install is untouched on a mismatch");
            Assert.That(TempDirs(), Is.Empty, "the staging dir is cleaned up after the rejected install");
        }

        // Sibling scratch dirs the installer creates under _root during a swap (".staging-*" / ".backup-*").
        private string[] TempDirs() =>
            Directory.EnumerateDirectories(_root, ".*", SearchOption.TopDirectoryOnly)
                .Where(d => Path.GetFileName(d).StartsWith(".staging-", StringComparison.Ordinal)
                         || Path.GetFileName(d).StartsWith(".backup-", StringComparison.Ordinal))
                .ToArray();

        // A read-through stream that fires a callback once it has yielded a byte threshold, so a consumer reading
        // through it (here: GZipStream → TarReader) can be interrupted partway rather than before the first read.
        // It takes ownership of the wrapped stream (disposes it) but NOT of any cancellation source — the threshold
        // action is supplied by the caller, who owns whatever it captures.
        private sealed class CancelAfterBytesStream : Stream {
            private readonly Stream _inner;
            private readonly Action _onThreshold;
            private readonly long _thresholdBytes;
            private long _read;
            private bool _fired;

            public CancelAfterBytesStream(Stream inner, Action onThreshold, long thresholdBytes) {
                _inner = inner;
                _onThreshold = onThreshold;
                _thresholdBytes = thresholdBytes;
            }

            private void Advance(int n) {
                _read += n;
                if (!_fired && _read >= _thresholdBytes) {
                    _fired = true;
                    _onThreshold();
                }
            }

            public override int Read(byte[] buffer, int offset, int count) {
                var n = _inner.Read(buffer, offset, count);
                Advance(n);
                return n;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
                var n = await _inner.ReadAsync(buffer, cancellationToken);
                Advance(n);
                return n;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
            public override void Flush() => _inner.Flush();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing) {
                if (disposing) {
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }
    }
}
