#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Test {

    /// <summary>
    /// ADR 0023 (#1545): the Sequencer reaches equipment through narrow ports, never by reflection on a device.
    /// </summary>
    [TestFixture]
    public class SequencerEquipmentSeamTest {

        private static readonly string[] ConnectFamilyDirs = { "OpenAstroAra.Sequencer/SequenceItem/Connect", "OpenAstroAra.Sequencer/Trigger/Connect" };

        private static string FindRepoRoot() {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenAstroAra.sln"))) {
                dir = dir.Parent;
            }
            Assert.That(dir, Is.Not.Null, "the repository root (OpenAstroAra.sln) was not found above the test binaries");
            return dir!.FullName;
        }

        // INVARIANT 3: `type.GetMethod("Connect")` threw AmbiguousMatchException on any rig with a guider type set.
        [Test]
        public void ConnectFamily_does_not_use_reflection_on_devices() {
            var root = FindRepoRoot();
            var files = ConnectFamilyDirs
                .SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.cs"))
                .ToList();
            Assert.That(files, Is.Not.Empty);

            var offenders = files.Where(f => File.ReadAllText(f).Contains("GetMethod(", StringComparison.Ordinal))
                .Select(Path.GetFileName)
                .ToList();

            Assert.That(offenders, Is.Empty, "the Connect family reaches a device through the connections port, not reflection");
        }
    }
}