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

            await SkyDataInstaller.InstallFromTarGzAsync(archive, target, CancellationToken.None);

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
                async () => await SkyDataInstaller.InstallFromTarGzAsync(archive, target, CancellationToken.None),
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
            await SkyDataInstaller.InstallFromTarGzAsync(archive, target, CancellationToken.None);

            Assert.That(File.Exists(Path.Combine(target, "stale.txt")), Is.False, "the prior install is fully replaced");
            Assert.That(File.Exists(Path.Combine(target, "fresh.txt")), Is.True);
            Assert.That(File.Exists(Path.Combine(target, SkyDataInstaller.InstalledMarkerFileName)), Is.True);
        }

        [Test]
        public async Task A_failed_install_preserves_the_prior_install() {
            var target = Path.Combine(_root, "tycho-2");
            using (var good = MakeTarGz(("catalog.dat", Bytes("original")))) {
                await SkyDataInstaller.InstallFromTarGzAsync(good, target, CancellationToken.None);
            }

            // A second install that fails (tar-slip) must not damage the install already on disk.
            using var poisoned = MakeTarGz(("../escaped.txt", Bytes("pwned")));
            Assert.That(
                async () => await SkyDataInstaller.InstallFromTarGzAsync(poisoned, target, CancellationToken.None),
                Throws.InstanceOf<InvalidDataException>());

            Assert.That(await File.ReadAllTextAsync(Path.Combine(target, "catalog.dat")), Is.EqualTo("original"),
                "the prior install is untouched when a re-install fails");
            Assert.That(File.Exists(Path.Combine(target, SkyDataInstaller.InstalledMarkerFileName)), Is.True);
            Assert.That(TempDirs(), Is.Empty, "no staging/backup dirs are leaked");
        }

        [Test]
        public void A_cancelled_install_leaves_no_target_and_no_staging_leak() {
            var target = Path.Combine(_root, "gaia-edr3-bright");
            using var archive = MakeTarGz(("catalog.dat", Bytes("data")));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.That(
                async () => await SkyDataInstaller.InstallFromTarGzAsync(archive, target, cts.Token),
                Throws.InstanceOf<OperationCanceledException>());

            Assert.That(Directory.Exists(target), Is.False, "a cancelled install produces no target dir");
            Assert.That(TempDirs(), Is.Empty, "the staging dir is cleaned up on cancellation");
        }

        // Sibling scratch dirs the installer creates under _root during a swap (".staging-*" / ".backup-*").
        private string[] TempDirs() =>
            Directory.EnumerateDirectories(_root, ".*", SearchOption.TopDirectoryOnly)
                .Where(d => Path.GetFileName(d).StartsWith(".staging-", StringComparison.Ordinal)
                         || Path.GetFileName(d).StartsWith(".backup-", StringComparison.Ordinal))
                .ToArray();
    }
}
