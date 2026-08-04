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
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §28.8 — the capture scan must walk PAST directories it can't read, not
    /// die inside them. The real-world shape: every ext4 volume has a
    /// root-owned <c>lost+found</c> at its root, and the §29 storage flow
    /// points the save directory at exactly such a mount root for every user.
    /// The original walk crash-looped the daemon on boot the first time rc91
    /// came up with the T7 as its store — a lazy <c>Directory.EnumerateFiles</c>
    /// throws mid-iteration, past the try/catch that guarded only its creation.
    /// </summary>
    [TestFixture]
    public class CaptureScanInaccessibleDirTest {

        private string _root = string.Empty;
        private string _profileDir = string.Empty;
        private string _lockedDir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private CaptureScanService _scan = null!;

        [SetUp]
        public async Task SetUp() {
            _root = Path.Combine(Path.GetTempPath(), $"oara-scan-{Guid.NewGuid():N}");
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-scanp-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_profileDir);

            _db = new SqliteAraDatabase(_profileDir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);

            var profile = new InMemoryProfileStore();
            profile.PutStorageSettings(
                profile.GetStorageSettings() with { SaveDirectory = _root });
            _scan = new CaptureScanService(profile, _db, logger: null);
        }

        [TearDown]
        public void TearDown() {
            _scan.Dispose();
            // Restore permissions first or the recursive delete fails too.
            if (_lockedDir.Length > 0 && Directory.Exists(_lockedDir)) {
                try {
#pragma warning disable CA1416 // guarded by the OS check in the test itself
                    File.SetUnixFileMode(_lockedDir,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
                } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(_profileDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task Scan_survives_an_unreadable_directory_at_the_store_root() {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
                Assert.Ignore("Unix file modes drive this scenario; the lost+found shape doesn't exist on Windows.");
            }

            // The lost+found shape: a directory at the root the daemon user
            // cannot open, next to content it can.
            _lockedDir = Path.Combine(_root, "lost+found");
            Directory.CreateDirectory(_lockedDir);
            var reachable = Path.Combine(_root, "M31");
            Directory.CreateDirectory(reachable);
            // A stale .tmp the sweep should still find PAST the locked dir.
            var stale = Path.Combine(reachable, "crashed-write.tmp");
            await File.WriteAllTextAsync(stale, "partial");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

            File.SetUnixFileMode(_lockedDir, UnixFileMode.None);
            // Root ignores file modes; under sudo/CI-as-root the directory stays
            // readable and the scenario can't be reproduced.
            if (Environment.UserName == "root") {
                Assert.Ignore("Running as root — an unreadable directory can't be simulated.");
            }

            CaptureScanResult result = null!;
            Assert.DoesNotThrowAsync(async () =>
                result = await _scan.RunAsync(CancellationToken.None),
                "an unreadable subdirectory must be walked past, never thrown out of");

            Assert.That(result.Ran, Is.True, "the store root itself is writable — the scan runs");
            Assert.That(result.TempFilesSwept, Is.EqualTo(1),
                "content beyond the locked directory is still reached");
            Assert.That(File.Exists(stale), Is.False, "the stale .tmp was actually swept");
        }
    }
}
