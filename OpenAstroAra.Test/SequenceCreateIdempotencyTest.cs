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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>The create-replay dedup (2026-07-15 audit): a retried create
    /// carrying the same Idempotency-Key must return the FIRST sequence, not
    /// mint a second one — the endpoint always declared the header, but the
    /// key was silently ignored until now.</summary>
    public class SequenceCreateIdempotencyTest {
        private string _tempDir = null!;
        private FileSequenceService _svc = null!;

        [SetUp]
        public void SetUp() {
            _tempDir = Path.Combine(Path.GetTempPath(), $"oara-idem-{Guid.NewGuid():N}");
            _svc = new FileSequenceService(_tempDir);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { /* best effort */ }
        }

        private static SequenceCreateRequestDto Request(string name = "M 31") =>
            new(name, null, JsonDocument.Parse("{}").RootElement, null);

        [Test]
        public async Task Same_key_replays_the_first_create_instead_of_duplicating() {
            var first = await _svc.CreateAsync(Request(), "key-1", CancellationToken.None);
            var retry = await _svc.CreateAsync(Request(), "key-1", CancellationToken.None);
            Assert.That(retry.Id, Is.EqualTo(first.Id), "the retry must dedupe");
            var page = await _svc.ListAsync(50, null, CancellationToken.None);
            Assert.That(page.Items, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Different_keys_and_no_key_create_distinct_sequences() {
            var a = await _svc.CreateAsync(Request("A"), "key-a", CancellationToken.None);
            var b = await _svc.CreateAsync(Request("B"), "key-b", CancellationToken.None);
            var c = await _svc.CreateAsync(Request("C"), null, CancellationToken.None);
            var d = await _svc.CreateAsync(Request("D"), null, CancellationToken.None);
            Assert.That(new[] { a.Id, b.Id, c.Id, d.Id }, Is.Unique);
        }

        [Test]
        public async Task A_replay_is_not_honoured_after_the_sequence_was_deleted() {
            var first = await _svc.CreateAsync(Request(), "key-1", CancellationToken.None);
            await _svc.DeleteAsync(first.Id, CancellationToken.None);
            var retry = await _svc.CreateAsync(Request(), "key-1", CancellationToken.None);
            Assert.That(retry.Id, Is.Not.EqualTo(first.Id),
                "honouring the replay would resurrect a ghost id the user deleted");
        }
        [Test]
        public async Task Concurrent_same_key_requests_share_one_create() {
            // The #853 TOCTOU: a retry racing the still-processing original must
            // JOIN it, not double-create. Fire both before awaiting either.
            var a = _svc.CreateAsync(Request(), "key-race", CancellationToken.None);
            var b = _svc.CreateAsync(Request(), "key-race", CancellationToken.None);
            var results = await Task.WhenAll(a, b);
            Assert.That(results[0].Id, Is.EqualTo(results[1].Id));
            var page = await _svc.ListAsync(50, null, CancellationToken.None);
            Assert.That(page.Items, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task Replay_survives_a_daemon_restart_and_has_no_time_cliff() {
            // The offline-draft case: the stamped key may come back DAYS later,
            // possibly after a daemon restart — the persisted map (validity =
            // the sequence file still exists) must still dedupe.
            var first = await _svc.CreateAsync(Request(), "key-durable", CancellationToken.None);
            var restarted = new FileSequenceService(_tempDir); // fresh process, same library dir
            var retry = await restarted.CreateAsync(Request(), "key-durable", CancellationToken.None);
            Assert.That(retry.Id, Is.EqualTo(first.Id), "the persisted replay map must dedupe");
        }

        [Test]
        public async Task Restarted_daemon_does_not_replay_a_deleted_sequence() {
            var first = await _svc.CreateAsync(Request(), "key-gone", CancellationToken.None);
            await _svc.DeleteAsync(first.Id, CancellationToken.None);
            var restarted = new FileSequenceService(_tempDir);
            var retry = await restarted.CreateAsync(Request(), "key-gone", CancellationToken.None);
            Assert.That(retry.Id, Is.Not.EqualTo(first.Id));
        }
    }
}
