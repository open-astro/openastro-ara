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
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§55.1 WILMA settings sync: the file-backed opaque-blob store round-trips, validates, and degrades.</summary>
    [TestFixture]
    public class ClientSettingsServiceTest {

        private string _dir = null!;
        private ClientSettingsService _svc = null!;

        [SetUp]
        public void SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), "ara-cs-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
            _svc = new ClientSettingsService(_dir, NullLogger<ClientSettingsService>.Instance);
        }

        [TearDown]
        public void TearDown() {
            _svc.Dispose();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static JsonElement Obj(string json) {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        [Test]
        public async Task Get_on_empty_returns_an_empty_object_and_null_timestamp() {
            var dto = await _svc.GetAsync(CancellationToken.None);
            Assert.That(dto.Settings.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(dto.Settings.EnumerateObject().GetEnumerator().MoveNext(), Is.False, "no properties");
            Assert.That(dto.UpdatedUtc, Is.Null);
        }

        [Test]
        public async Task Replace_then_get_round_trips_the_blob() {
            var put = await _svc.ReplaceAsync(Obj("""{"theme":"dark","collapsed":["targets"],"zoom":1.5}"""), CancellationToken.None);
            Assert.That(put.UpdatedUtc, Is.Not.Null);

            var got = await _svc.GetAsync(CancellationToken.None);
            Assert.That(got.Settings.GetProperty("theme").GetString(), Is.EqualTo("dark"));
            Assert.That(got.Settings.GetProperty("zoom").GetDouble(), Is.EqualTo(1.5));
            Assert.That(got.Settings.GetProperty("collapsed")[0].GetString(), Is.EqualTo("targets"));
            Assert.That(got.UpdatedUtc, Is.Not.Null);
        }

        [Test]
        public async Task Replace_is_last_write_wins() {
            await _svc.ReplaceAsync(Obj("""{"v":1}"""), CancellationToken.None);
            await _svc.ReplaceAsync(Obj("""{"v":2}"""), CancellationToken.None);
            var got = await _svc.GetAsync(CancellationToken.None);
            Assert.That(got.Settings.GetProperty("v").GetInt32(), Is.EqualTo(2));
        }

        [Test]
        public async Task Concurrent_writes_serialize_to_one_winner_without_tearing() {
            // Fire many overlapping replaces; the semaphore serializes them, last-write-wins, and the file is never
            // torn — the final state is exactly one of the written values and re-reads as a valid object.
            var writes = new List<Task<OpenAstroAra.Server.Contracts.ClientSettingsDto>>();
            for (var i = 0; i < 20; i++) {
                writes.Add(_svc.ReplaceAsync(Obj($"{{\"v\":{i}}}"), CancellationToken.None));
            }
            await Task.WhenAll(writes);

            var got = await _svc.GetAsync(CancellationToken.None);
            Assert.That(got.Settings.ValueKind, Is.EqualTo(JsonValueKind.Object), "the file is valid (not torn)");
            Assert.That(got.Settings.GetProperty("v").GetInt32(), Is.InRange(0, 19),
                "the final state is one of the concurrent writes");
        }

        [Test]
        public void Replace_rejects_a_non_object() {
            Assert.That(async () => await _svc.ReplaceAsync(Obj("[1,2,3]"), CancellationToken.None),
                Throws.InstanceOf<ArgumentException>());
            Assert.That(async () => await _svc.ReplaceAsync(Obj("\"just a string\""), CancellationToken.None),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void Replace_rejects_an_oversized_blob() {
            // The value alone is MaxBytes chars, so the full JSON (key + quotes + braces) is deliberately over the cap.
            var big = "{\"blob\":\"" + new string('x', ClientSettingsService.MaxBytes) + "\"}";
            Assert.That(async () => await _svc.ReplaceAsync(Obj(big), CancellationToken.None),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public async Task A_valid_but_non_object_file_serves_an_empty_object() {
            // A hand-edited file that's valid JSON but not an object must still degrade to {} (the PUT path can't
            // produce this, but a manual edit can).
            await File.WriteAllTextAsync(Path.Combine(_dir, ClientSettingsService.FileName), "[1,2,3]", Encoding.UTF8);
            var dto = await _svc.GetAsync(CancellationToken.None);
            Assert.That(dto.Settings.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(dto.Settings.EnumerateObject().GetEnumerator().MoveNext(), Is.False);
            Assert.That(dto.UpdatedUtc, Is.Null);
        }

        [Test]
        public async Task A_corrupt_file_serves_an_empty_object() {
            await File.WriteAllTextAsync(Path.Combine(_dir, ClientSettingsService.FileName), "{ not valid json", Encoding.UTF8);
            var dto = await _svc.GetAsync(CancellationToken.None);
            Assert.That(dto.Settings.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(dto.Settings.EnumerateObject().GetEnumerator().MoveNext(), Is.False, "corrupt file → empty object");
        }
    }
}
