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
using System.IO;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §37.4/§46.2 filter-wheel slot labels (the 12h.2b round-trip's store half):
    /// defaults, persistence across a daemon restart, and — the upgrade case — an
    /// older profile.json written before the section existed back-fills the default
    /// instead of surfacing a null section.
    /// </summary>
    [TestFixture]
    public class FilterWheelLabelsStoreTest {

        // CA1861: the expected reference-8 default, shared by the two assertions.
        private static readonly string[] ReferenceDefault =
            ["L", "R", "G", "B", "Hα", "OIII", "SII", ""];

        private static readonly string[] NarrowbandSet = ["Ha", "OIII", "SII", ""];

        [Test]
        public void Defaults_to_the_reference_wheel_set() {
            var store = new InMemoryProfileStore();
            var labels = store.GetFilterWheelLabels();
            Assert.That(labels.Labels, Is.EqualTo(ReferenceDefault),
                "matches the client's pre-round-trip in-memory default, so first hydration changes nothing visually");
        }

        [Test]
        public void Persists_across_a_store_reload() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-wheel-labels-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                store.PutFilterWheelLabels(new FilterWheelLabelsDto(NarrowbandSet));

                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetFilterWheelLabels().Labels,
                    Is.EqualTo(NarrowbandSet), "survives a daemon restart");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void An_older_profile_json_without_the_section_backfills_the_default() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-wheel-labels-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                // Write a modern full profile, then surgically remove the new section —
                // exactly the file a pre-section daemon version left behind.
                var store = new FileProfileStore(dir);
                store.PutImagingDefaults(store.GetImagingDefaults() with { Gain = 42 });
                var path = Path.Combine(dir, "profile.json");
                var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                Assert.That(node.Remove("filter_wheel_labels"), Is.True,
                    "the modern file must actually carry the section for this test to mean anything");
                File.WriteAllText(path, node.ToJsonString());

                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetFilterWheelLabels().Labels,
                    Is.EqualTo(ReferenceDefault),
                    "missing section → the default, never a null body");
                Assert.That(reopened.GetImagingDefaults().Gain, Is.EqualTo(42),
                    "the surviving sections still load");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
