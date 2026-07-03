#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.FilterWheel;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §40.6 — ResumeTargetAsync produces a persisted runnable sequence: the session's own
    /// recorded body when present, catalog synthesis (per-filter modal LIGHT blocks with the
    /// original frame counts) otherwise, or the caller's override echoed back.
    /// </summary>
    [TestFixture]
    public class ResumeTargetTest {

        private string _dir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private FileSequenceService _store = null!;
        private SqliteSessionService _svc = null!;
        private static readonly Guid Session = Guid.Parse("66666666-6666-6666-6666-666666666661");

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-resume-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _store = new FileSequenceService(_dir);
            var frames = new SqliteFrameRepository(_db, new InMemoryProfileStore());
            _svc = new SqliteSessionService(_db, frames, new InMemoryBatchJobService(logger: null), new NullBroadcaster(), _store);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private async Task InsertSessionAsync(Guid id, string? sequenceJson = null) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (id, profile_id, sequence_json, started_at, ended_at,
                    recovery_needed, last_completed_instruction_id, current_target_id, frame_count)
                VALUES ($id, NULL, $seq, $t, $t, 0, NULL, NULL, 0);
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$seq", (object?)sequenceJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", new DateTimeOffset(2026, 6, 30, 22, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        private async Task InsertLightAsync(string? filter, double exposure, int? gain, int? offset, int? focus) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                    exposure_seconds, gain, "offset", focuser_position, temperature_c, captured_utc,
                    file_path, file_size_bytes, width, height, bit_depth)
                VALUES ($id, $sid, 'NGC 7000', 'light', $filter, $exp, $gain, $off, $focus, -10,
                    $utc, '/tmp/x.fits', 1, 16, 16, 16);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$sid", Session.ToString());
            cmd.Parameters.AddWithValue("$filter", (object?)filter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$exp", exposure);
            cmd.Parameters.AddWithValue("$gain", gain is null ? DBNull.Value : gain.Value);
            cmd.Parameters.AddWithValue("$off", offset is null ? DBNull.Value : offset.Value);
            cmd.Parameters.AddWithValue("$focus", focus is null ? DBNull.Value : focus.Value);
            cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        [Test]
        public async Task Synthesis_replays_per_filter_modal_settings_with_original_counts() {
            await InsertSessionAsync(Session);
            await InsertLightAsync("Ha", 300, 100, 10, 5230);
            await InsertLightAsync("Ha", 300, 100, 10, 5230);
            await InsertLightAsync("Ha", 180, 100, 10, 5230); // minority combo — loses the mode
            await InsertLightAsync("OIII", 180, 120, null, null);

            var result = await _svc.ResumeTargetAsync(Session,
                new ResumeTargetRequestDto(RecreateSequence: false, OverrideSequenceId: null),
                idempotencyKey: null, CancellationToken.None);

            Assert.That(result.Origin, Is.EqualTo("synthesized-from-catalog"));
            Assert.That(result.SequenceName, Does.Contain("NGC 7000"));
            var stored = await _store.GetAsync(result.SequenceId, CancellationToken.None);
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.TemplateOrigin, Is.EqualTo("session:resume-target"));
            Assert.That(SequenceSchemaValidator.Validate(stored.Body).Valid, Is.True);

            var root = new SequenceJsonConverter(HeadlessSequencerFactory.WithDefaults())
                .Deserialize(stored.Body.GetRawText());
            var blocks = root.GetItemsSnapshot().OfType<SequentialContainer>().ToList();
            Assert.That(blocks.Count, Is.EqualTo(2), "one block per filter");

            var ha = blocks.Single(b => b.GetItemsSnapshot().OfType<TakeExposure>().Single().ExposureTime == 300);
            Assert.That(ha.GetConditionsSnapshot().OfType<LoopCondition>().Single().Iterations, Is.EqualTo(3),
                "the block re-captures the filter's ORIGINAL total, not just the modal combo's count");
            var haTake = ha.GetItemsSnapshot().OfType<TakeExposure>().Single();
            Assert.That(haTake.ImageType, Is.EqualTo("LIGHT"));
            Assert.That(haTake.Gain, Is.EqualTo(100));
            Assert.That(haTake.Offset, Is.EqualTo(10));

            var oiii = blocks.Single(b => b.GetItemsSnapshot().OfType<TakeExposure>().Single().ExposureTime == 180);
            Assert.That(oiii.GetItemsSnapshot().OfType<TakeExposure>().Single().Offset, Is.EqualTo(-1),
                "no recorded offset → camera-default sentinel");
        }

        [Test]
        public async Task The_recorded_session_sequence_wins_over_synthesis() {
            var original = /*lang=json,strict*/ """
                {
                  "schemaVersion": "openastroara-sequence-v1",
                  "$type": "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer",
                  "Strategy": { "$type": "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer" },
                  "Name": "Original plan",
                  "Conditions": [], "Triggers": [],
                  "Items": [
                    { "$type": "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer", "ExposureTime": 240.0, "ImageType": "LIGHT", "ExposureCount": 0 }
                  ]
                }
                """;
            await InsertSessionAsync(Session, original);
            await InsertLightAsync("Ha", 300, 100, 10, null);

            var result = await _svc.ResumeTargetAsync(Session,
                new ResumeTargetRequestDto(RecreateSequence: false, OverrideSequenceId: null),
                idempotencyKey: null, CancellationToken.None);

            Assert.That(result.Origin, Is.EqualTo("original-sequence"));
            var stored = await _store.GetAsync(result.SequenceId, CancellationToken.None);
            Assert.That(stored!.Body.GetProperty("Items").EnumerateArray().First()
                    .GetProperty("ExposureTime").GetDouble(), Is.EqualTo(240.0),
                "the stored body IS the session's recorded sequence");
        }

        [Test]
        public async Task RecreateSequence_forces_catalog_synthesis_past_a_recorded_body() {
            await InsertSessionAsync(Session, """{ "schemaVersion": "openastroara-sequence-v1" }""");
            await InsertLightAsync("Ha", 300, 100, 10, null);

            var result = await _svc.ResumeTargetAsync(Session,
                new ResumeTargetRequestDto(RecreateSequence: true, OverrideSequenceId: null),
                idempotencyKey: null, CancellationToken.None);

            Assert.That(result.Origin, Is.EqualTo("synthesized-from-catalog"));
        }

        [Test]
        public async Task Override_echoes_an_existing_sequence_without_persisting_a_new_one() {
            await InsertSessionAsync(Session);
            var mine = await _store.CreateAsync(new SequenceCreateRequestDto(
                Name: "My NGC 7000 plan", Description: null,
                Body: JsonSerializer.SerializeToElement(new { schemaVersion = "openastroara-sequence-v1" }),
                TemplateOrigin: null), idempotencyKey: null, CancellationToken.None);

            var result = await _svc.ResumeTargetAsync(Session,
                new ResumeTargetRequestDto(RecreateSequence: false, OverrideSequenceId: mine.Id),
                idempotencyKey: null, CancellationToken.None);

            Assert.That(result.Origin, Is.EqualTo("override"));
            Assert.That(result.SequenceId, Is.EqualTo(mine.Id));
            var all = await _store.ListAsync(50, null, CancellationToken.None);
            Assert.That(all.Items.Count, Is.EqualTo(1), "no extra sequence persisted");
        }

        [Test]
        public async Task An_unknown_override_id_is_a_validation_error() {
            await InsertSessionAsync(Session);
            var ex = Assert.ThrowsAsync<ArgumentException>(() => _svc.ResumeTargetAsync(Session,
                new ResumeTargetRequestDto(RecreateSequence: false, OverrideSequenceId: Guid.NewGuid()),
                idempotencyKey: null, CancellationToken.None));
            Assert.That(ex!.ParamName, Is.EqualTo("request"),
                "the endpoint's 422 catch filters on ParamName == request; any other name becomes a 500");
        }

        [Test]
        public async Task A_session_with_no_lights_and_no_sequence_is_a_validation_error() {
            await InsertSessionAsync(Session);
            var ex = Assert.ThrowsAsync<ArgumentException>(() => _svc.ResumeTargetAsync(Session,
                new ResumeTargetRequestDto(RecreateSequence: false, OverrideSequenceId: null),
                idempotencyKey: null, CancellationToken.None));
            Assert.That(ex!.ParamName, Is.EqualTo("request"),
                "the endpoint's 422 catch filters on ParamName == request; any other name becomes a 500");
        }
    }
}
