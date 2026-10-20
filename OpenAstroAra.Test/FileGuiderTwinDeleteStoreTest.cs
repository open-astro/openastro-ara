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
using System.Linq;

namespace OpenAstroAra.Test {

    /// <summary>§63.4 — the queued twin deletes live in <c>{profileDir}/guider-twin-deletes.json</c>
    /// and must survive a daemon restart: a SECOND store on the same directory reads what the first
    /// wrote, flags included.</summary>
    [TestFixture]
    [Category("IO")]
    public class FileGuiderTwinDeleteStoreTest {
        private string dir = null!;

        [SetUp]
        public void MakeDir() {
            dir = Path.Combine(Path.GetTempPath(), "ara-twin-store-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
        }

        [TearDown]
        public void DropDir() {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }

        private static readonly string[] TwoNames = { "Rig", "ara-rig-deadbeef" };

        [Test]
        public void A_queued_delete_survives_a_restart_with_its_flags() {
            var id = Guid.NewGuid();
            var queued = new DateTimeOffset(2026, 10, 20, 21, 0, 0, TimeSpan.Zero);
            var first = new FileGuiderTwinDeleteStore(dir);
            first.Add(new PendingGuiderTwinDelete(id, "Rig", TwoNames, DeleteDarkFiles: false, queued, Shared: true));
            Assert.That(File.Exists(Path.Combine(dir, FileGuiderTwinDeleteStore.FileName)), Is.True, "written through at once");

            var second = new FileGuiderTwinDeleteStore(dir); // the daemon came back
            var entry = second.List().Single();

            Assert.That(entry.AraProfileId, Is.EqualTo(id));
            Assert.That(entry.AraProfileName, Is.EqualTo("Rig"));
            Assert.That(entry.TwinNames, Is.EqualTo(TwoNames));
            Assert.That(entry.DeleteDarkFiles, Is.False);
            Assert.That(entry.Shared, Is.True);
            Assert.That(entry.QueuedUtc, Is.EqualTo(queued));
            Assert.That(second.PendingCount, Is.EqualTo(1));
        }

        [Test]
        public void A_second_add_for_the_same_profile_replaces_and_the_last_remove_drops_the_file() {
            var id = Guid.NewGuid();
            var store = new FileGuiderTwinDeleteStore(dir);
            store.Add(new PendingGuiderTwinDelete(id, "Rig", TwoNames, true, DateTimeOffset.UtcNow));
            store.Add(new PendingGuiderTwinDelete(id, "Rig", TwoNames, false, DateTimeOffset.UtcNow));
            Assert.That(new FileGuiderTwinDeleteStore(dir).List().Single().DeleteDarkFiles, Is.False);

            store.Remove(id);

            Assert.That(store.PendingCount, Is.Zero);
            Assert.That(File.Exists(Path.Combine(dir, FileGuiderTwinDeleteStore.FileName)), Is.False, "an empty queue leaves no file behind");
            Assert.That(new FileGuiderTwinDeleteStore(dir).List(), Is.Empty);
        }

        [Test]
        public void An_unreadable_file_loads_as_an_empty_queue_and_is_overwritten_by_the_next_add() {
            File.WriteAllText(Path.Combine(dir, FileGuiderTwinDeleteStore.FileName), "{ not json");
            var store = new FileGuiderTwinDeleteStore(dir);
            Assert.That(store.List(), Is.Empty);
            Assert.That(store.PendingCount, Is.Zero);

            store.Add(new PendingGuiderTwinDelete(Guid.NewGuid(), "Rig", TwoNames, true, DateTimeOffset.UtcNow));

            Assert.That(new FileGuiderTwinDeleteStore(dir).List(), Has.Count.EqualTo(1));
        }
    }
}
