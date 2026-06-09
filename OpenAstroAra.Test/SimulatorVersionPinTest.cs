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
using System.IO;
using System.Text.RegularExpressions;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §14.5.1 — structural validation of the Alpaca-simulator version pin
    /// (<c>OpenAstroAra.Test/fixtures/SIMULATORS_VERSION.md</c>). Catches a
    /// malformed/half-edited pin file (e.g. from the weekly auto-bump PR) before it
    /// reaches the download script. Does not touch the network.
    /// </summary>
    [TestFixture]
    public class SimulatorVersionPinTest {

        private static string ReadPinFile() {
            var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
            while (dir is not null) {
                var candidate = Path.Combine(dir.FullName, "OpenAstroAra.Test", "fixtures", "SIMULATORS_VERSION.md");
                if (File.Exists(candidate)) {
                    return File.ReadAllText(candidate);
                }
                dir = dir.Parent;
            }
            Assert.Fail($"SIMULATORS_VERSION.md not found walking up from {TestContext.CurrentContext.TestDirectory}");
            return string.Empty;
        }

        [Test]
        public void Has_a_pinned_release_and_commit_sha() {
            var text = ReadPinFile();
            Assert.That(text, Does.Match(@"Pinned release:\s*v\d+\.\d+\.\d+"), "needs a 'Pinned release: vX.Y.Z'");
            Assert.That(text, Does.Match(@"Pinned SHA:\s*[0-9a-f]{40}"), "needs a 40-hex 'Pinned SHA:'");
        }

        [Test]
        public void Pins_the_linux_x64_ci_artifact_with_a_sha256() {
            // linux-x64 is the CI target — it must be pinned with a 64-hex sha256.
            var text = ReadPinFile();
            Assert.That(
                Regex.IsMatch(text, @"linux-x64\.tar\.xz\s+sha256:\s*[0-9a-f]{64}"),
                Is.True,
                "the linux-x64 artifact must be pinned with a 64-hex sha256");
        }

        [Test]
        public void All_listed_checksums_are_64_hex() {
            var text = ReadPinFile();
            var matches = Regex.Matches(text, @"sha256:\s*(\S+)");
            Assert.That(matches.Count, Is.GreaterThan(0), "expected at least one sha256 entry");
            foreach (Match m in matches) {
                Assert.That(m.Groups[1].Value, Does.Match("^[0-9a-f]{64}$"), $"malformed sha256: {m.Groups[1].Value}");
            }
        }
    }
}
