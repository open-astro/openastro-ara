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
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>§55.1 WILMA settings sync: the file-backed opaque-blob store round-trips, validates, and degrades.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class ClientSettingsServiceTest {

        private string _dir = null!;
        private ClientSettingsService _svc = null!;
        private readonly List<(string Type, JsonElement Payload)> _published = new();

        private sealed class RecordingWs : IWsBroadcaster {
            private readonly List<(string Type, JsonElement Payload)> _sink;
            public RecordingWs(List<(string Type, JsonElement Payload)> sink) => _sink = sink;
            public long CurrentSequence { get; private set; }
            public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
                CurrentSequence++;
                _sink.Add((eventType, payload.Clone()));
                return Task.CompletedTask;
            }
        }

        [SetUp]
        public void SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), "ara-cs-" + Path.GetRandomFileName());
            Directory.CreateDirectory(_dir);
            _published.Clear();
            _svc = new ClientSettingsService(_dir, NullLogger<ClientSettingsService>.Instance, new RecordingWs(_published));
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
        public async Task Merge_replaces_only_the_given_keys() {
            await _svc.ReplaceAsync(Obj("""{"a":1,"b":{"x":1}}"""), CancellationToken.None);
            var merged = await _svc.MergeAsync(Obj("""{"b":{"y":2},"c":true}"""), CancellationToken.None);

            Assert.That(merged.Settings.GetProperty("a").GetInt32(), Is.EqualTo(1), "untouched key stays");
            Assert.That(merged.Settings.GetProperty("b").TryGetProperty("x", out _), Is.False, "key replaced, not deep-merged");
            Assert.That(merged.Settings.GetProperty("b").GetProperty("y").GetInt32(), Is.EqualTo(2));
            Assert.That(merged.Settings.GetProperty("c").GetBoolean(), Is.True);
            var got = await _svc.GetAsync(CancellationToken.None);
            Assert.That(got.Settings.GetRawText(), Is.EqualTo(merged.Settings.GetRawText()));
            Assert.That(got.UpdatedUtc, Is.EqualTo(merged.UpdatedUtc), "returned stamp is the stored one");
        }

        [Test]
        public async Task Merge_with_null_removes_the_key() {
            await _svc.ReplaceAsync(Obj("""{"a":1,"b":2}"""), CancellationToken.None);
            var merged = await _svc.MergeAsync(Obj("""{"a":null}"""), CancellationToken.None);
            Assert.That(merged.Settings.TryGetProperty("a", out _), Is.False);
            Assert.That(merged.Settings.GetProperty("b").GetInt32(), Is.EqualTo(2));
        }

        [Test]
        public async Task Merge_onto_nothing_stored_creates_the_blob() {
            var merged = await _svc.MergeAsync(Obj("""{"a":1}"""), CancellationToken.None);
            Assert.That(merged.Settings.GetProperty("a").GetInt32(), Is.EqualTo(1));
            Assert.That(merged.UpdatedUtc, Is.Not.Null);
        }

        [Test]
        public async Task Merge_rejects_a_non_object() {
            await Assert.ThrowsAsync<ArgumentException>(() => _svc.MergeAsync(Obj("[1]"), CancellationToken.None));
        }

        [Test]
        public async Task Merge_rejects_a_result_over_the_cap_and_keeps_the_stored_blob() {
            await _svc.ReplaceAsync(Obj("{\"big\":\"" + new string('x', ClientSettingsService.MaxBytes - 100) + "\"}"), CancellationToken.None);
            _published.Clear();
            // Each patch alone is tiny; the merged result is what goes over.
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _svc.MergeAsync(Obj("{\"more\":\"" + new string('y', 200) + "\"}"), CancellationToken.None));
            var got = await _svc.GetAsync(CancellationToken.None);
            Assert.That(got.Settings.TryGetProperty("more", out _), Is.False);
            Assert.That(_published, Is.Empty, "a rejected write emits nothing");
        }

        [Test]
        public async Task Concurrent_merges_of_different_keys_keep_both() {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
                _svc.MergeAsync(Obj("{\"k" + i + "\":" + i + "}"), CancellationToken.None)));
            var got = await _svc.GetAsync(CancellationToken.None);
            for (var i = 0; i < 20; i++) {
                Assert.That(got.Settings.GetProperty("k" + i).GetInt32(), Is.EqualTo(i));
            }
        }

        [Test]
        public async Task Merge_publishes_client_settings_updated_with_the_patched_keys_and_stamp() {
            var merged = await _svc.MergeAsync(Obj("""{"a":1,"b":null}"""), CancellationToken.None);
            Assert.That(_published, Has.Count.EqualTo(1));
            Assert.That(_published[0].Type, Is.EqualTo("client_settings.updated"));
            var keys = _published[0].Payload.GetProperty("keys").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.That(keys, Is.EquivalentTo(new List<string> { "a", "b" }));
            Assert.That(_published[0].Payload.GetProperty("updated_utc").GetDateTimeOffset(), Is.EqualTo(merged.UpdatedUtc));
        }

        [Test]
        public async Task Replace_publishes_client_settings_updated() {
            var put = await _svc.ReplaceAsync(Obj("""{"a":1,"b":2}"""), CancellationToken.None);
            Assert.That(_published, Has.Count.EqualTo(1));
            Assert.That(_published[0].Type, Is.EqualTo("client_settings.updated"));
            Assert.That(_published[0].Payload.GetProperty("keys").GetArrayLength(), Is.EqualTo(2));
            Assert.That(_published[0].Payload.GetProperty("updated_utc").GetDateTimeOffset(), Is.EqualTo(put.UpdatedUtc));
        }

        [Test]
        public async Task Rejected_replace_publishes_nothing() {
            await Assert.ThrowsAsync<ArgumentException>(() => _svc.ReplaceAsync(Obj("[1]"), CancellationToken.None));
            Assert.That(_published, Is.Empty);
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

        // §55.1 orphan-temp boot sweep.

        private string TempPath(string suffix) => Path.Combine(_dir, ClientSettingsService.FileName + ".tmp-" + suffix);

        [Test]
        public void SweepOrphans_removes_temp_files_left_by_a_killed_write() {
            File.WriteAllText(TempPath("deadbeef"), "partial");
            File.WriteAllText(TempPath("cafef00d"), "partial");

            var removed = ClientSettingsService.SweepOrphans(_dir);

            Assert.That(removed, Is.EqualTo(2));
            Assert.That(Directory.GetFiles(_dir, ClientSettingsService.FileName + ".tmp-*"), Is.Empty);
        }

        [Test]
        public void SweepOrphans_keeps_the_real_settings_file() {
            File.WriteAllText(Path.Combine(_dir, ClientSettingsService.FileName), "{\"theme\":\"dark\"}");
            File.WriteAllText(TempPath("deadbeef"), "partial");

            var removed = ClientSettingsService.SweepOrphans(_dir);

            Assert.That(removed, Is.EqualTo(1));
            Assert.That(File.Exists(Path.Combine(_dir, ClientSettingsService.FileName)), Is.True,
                "the live settings file is not a temp orphan");
        }

        [Test]
        public void SweepOrphans_on_a_missing_dir_is_a_no_op() =>
            Assert.That(ClientSettingsService.SweepOrphans(Path.Combine(_dir, "nope")), Is.EqualTo(0));

        [Test]
        public async Task SweepOrphans_after_a_real_write_leaves_no_temp() {
            await _svc.ReplaceAsync(Obj("{\"theme\":\"dark\"}"), CancellationToken.None);

            var removed = ClientSettingsService.SweepOrphans(_dir);

            Assert.That(removed, Is.EqualTo(0), "a completed write renames its temp away — nothing to reclaim");
        }

        [Test]
        public void SweepOrphans_logs_a_warning_only_when_it_reclaims_something() {
            var clean = new RecordingLogger();
            ClientSettingsService.SweepOrphans(_dir, clean);
            Assert.That(clean.Warnings, Is.Empty);

            File.WriteAllText(TempPath("deadbeef"), "partial");
            var swept = new RecordingLogger();
            ClientSettingsService.SweepOrphans(_dir, swept);
            Assert.That(swept.Warnings, Has.Count.EqualTo(1));
            Assert.That(swept.Warnings[0], Does.Contain(".tmp-deadbeef"), "the warning names the reclaimed file");
        }

        private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger {
            public List<string> Warnings { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter) {
                if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) {
                    Warnings.Add(formatter(state, exception));
                }
            }
        }
    }
}