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

    // #1075 — the filter-wheel policy section round-trips through the file store and an older
    // profile.json without it back-fills the default (home on).
    [TestFixture]
    public class FilterWheelPolicyStoreTest {

        [Test]
        public void Round_trips_and_defaults_to_home_on() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-wheel-policy-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                Assert.That(store.GetFilterWheelPolicy().HomeOnFirstConnect, Is.True, "default: home on first connect");
                store.PutFilterWheelPolicy(new FilterWheelPolicyDto(HomeOnFirstConnect: false));
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetFilterWheelPolicy().HomeOnFirstConnect, Is.False, "persisted");
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }

        [Test]
        public void An_older_profile_json_without_the_section_backfills_the_default() {
            var dir = Path.Combine(Path.GetTempPath(), "ara-wheel-policy-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            try {
                var store = new FileProfileStore(dir);
                store.PutFilterWheelPolicy(new FilterWheelPolicyDto(HomeOnFirstConnect: false));
                var path = Path.Combine(dir, "profile.json");
                var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                Assert.That(node.Remove("filter_wheel_policy"), Is.True, "the modern file must carry the section");
                File.WriteAllText(path, node.ToJsonString());
                var reopened = new FileProfileStore(dir);
                Assert.That(reopened.GetFilterWheelPolicy().HomeOnFirstConnect, Is.True);
            } finally {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
