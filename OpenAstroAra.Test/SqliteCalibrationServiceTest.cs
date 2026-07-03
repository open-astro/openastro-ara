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
using OpenAstroAra.Sequencer.SequenceItem.Focuser;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §39 — <see cref="SqliteCalibrationService"/> over a real temp SQLite catalog. Verifies session derivation
    /// from LIGHT frames, the per-filter summary, the flats-by-filter / darks-by-(exposure,gain) matching rules,
    /// and the generated matching-flats plan.
    /// </summary>
    [TestFixture]
    public class SqliteCalibrationServiceTest {

        private string _dir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private SqliteCalibrationService _svc = null!;
        private static readonly Guid Session = Guid.Parse("33333333-3333-3333-3333-333333333331");

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-cal-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _svc = new SqliteCalibrationService(_db);
            await InsertSessionAsync(Session);
        }

        [TearDown]
        public void TearDown() {
            // SqliteAraDatabase isn't IDisposable (each OpenConnection() returns a fresh caller-owned
            // connection), but Microsoft.Data.Sqlite pools the underlying file handle — release it so the temp
            // dir actually deletes on Windows rather than silently hitting the swallowed IOException.
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task Session_is_derived_from_light_frames_with_a_per_filter_summary() {
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");
            await InsertFrameAsync(Session, "light", "OIII", 600, 100, "M42");

            var dto = await _svc.GetSessionAsync(Session, CancellationToken.None);

            Assert.That(dto, Is.Not.Null);
            Assert.That(dto!.LightFrameCount, Is.EqualTo(3));
            Assert.That(dto.TargetName, Is.EqualTo("M42"));
            Assert.That(dto.FiltersUsed.Count, Is.EqualTo(2));
            var ha = dto.FiltersUsed.Single(f => f.FilterName == "Ha");
            Assert.That(ha.LightFrameCount, Is.EqualTo(2));
            Assert.That(ha.MeanExposureSeconds, Is.EqualTo(300));
            var oiii = dto.FiltersUsed.Single(f => f.FilterName == "OIII");
            Assert.That(oiii.LightFrameCount, Is.EqualTo(1));
        }

        [Test]
        public async Task ListSessions_paginates_over_the_cursor() {
            var s2 = Guid.Parse("33333333-3333-3333-3333-333333333332");
            var s3 = Guid.Parse("33333333-3333-3333-3333-333333333333");
            await InsertSessionAsync(s2);
            await InsertSessionAsync(s3);
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");
            await InsertFrameAsync(s2, "light", "Ha", 300, 100, "M81");
            await InsertFrameAsync(s3, "light", "Ha", 300, 100, "M101");

            var page1 = await _svc.ListSessionsAsync(2, null, CancellationToken.None);
            Assert.That(page1.Items.Count, Is.EqualTo(2));
            Assert.That(page1.HasMore, Is.True);
            Assert.That(page1.NextCursor, Is.Not.Null);

            var page2 = await _svc.ListSessionsAsync(2, page1.NextCursor, CancellationToken.None);
            Assert.That(page2.Items.Count, Is.EqualTo(1));
            Assert.That(page2.HasMore, Is.False);

            // The two pages together cover all three sessions with no overlap.
            var ids = page1.Items.Concat(page2.Items).Select(s => s.Id).ToHashSet();
            Assert.That(ids, Is.EquivalentTo(new[] { Session, s2, s3 }));
        }

        [Test]
        public async Task GenerateMatchingFlats_uses_the_default_target_adu_when_not_overridden() {
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");
            var plan = await _svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: true), CancellationToken.None);
            Assert.That(plan.Steps.Single().TargetAdu, Is.EqualTo(32000));
        }

        [Test]
        public async Task GetSession_returns_null_for_a_session_with_no_light_frames() {
            await InsertFrameAsync(Session, "dark", null, 300, 100, "M42"); // darks only — not a calibration session
            var dto = await _svc.GetSessionAsync(Session, CancellationToken.None);
            Assert.That(dto, Is.Null);
        }

        [Test]
        public async Task MatchingFlatsAvailable_is_true_only_when_every_light_filter_has_a_flat() {
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");
            await InsertFrameAsync(Session, "light", "OIII", 300, 100, "M42");
            await InsertFrameAsync(Session, "flat", "Ha", 2, 100, "FLAT");

            // Only Ha has a flat → OIII uncovered.
            var before = await _svc.GetSessionAsync(Session, CancellationToken.None);
            Assert.That(before!.MatchingFlatsAvailable, Is.False);

            await InsertFrameAsync(Session, "flat", "OIII", 2, 100, "FLAT");
            var after = await _svc.GetSessionAsync(Session, CancellationToken.None);
            Assert.That(after!.MatchingFlatsAvailable, Is.True);
        }

        [Test]
        public async Task MatchingDarksAvailable_matches_on_exposure_and_gain() {
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");

            await InsertFrameAsync(Session, "dark", null, 300, 50, "DARK"); // wrong gain
            var wrongGain = await _svc.GetSessionAsync(Session, CancellationToken.None);
            Assert.That(wrongGain!.MatchingDarksAvailable, Is.False);

            await InsertFrameAsync(Session, "dark", null, 300, 100, "DARK"); // matches exp + gain (same -10°C default)
            var matched = await _svc.GetSessionAsync(Session, CancellationToken.None);
            Assert.That(matched!.MatchingDarksAvailable, Is.True);
        }

        [Test]
        public async Task MatchingDarksAvailable_requires_temperature_match() {
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42", temperatureC: -10.0);

            // Right exposure + gain but a dark shot 15°C warmer is not a valid calibration match.
            await InsertFrameAsync(Session, "dark", null, 300, 100, "DARK", temperatureC: 5.0);
            var wrongTemp = await _svc.GetSessionAsync(Session, CancellationToken.None);
            Assert.That(wrongTemp!.MatchingDarksAvailable, Is.False);

            await InsertFrameAsync(Session, "dark", null, 300, 100, "DARK", temperatureC: -10.0);
            var matched = await _svc.GetSessionAsync(Session, CancellationToken.None);
            Assert.That(matched!.MatchingDarksAvailable, Is.True);
        }

        [Test]
        public async Task MatchingDarksAvailable_buckets_temperature_to_the_nearest_degree() {
            // A cooled camera regulating to ~-10°C records small fluctuations; lights and darks within the same
            // whole-degree bucket still match.
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42", temperatureC: -10.2);
            await InsertFrameAsync(Session, "dark", null, 300, 100, "DARK", temperatureC: -9.8);
            var matched = await _svc.GetSessionAsync(Session, CancellationToken.None);
            Assert.That(matched!.MatchingDarksAvailable, Is.True);
        }

        [Test]
        public async Task MatchingDarksAvailable_matches_uncooled_sentinel_temperature() {
            // temperature_c is NOT NULL; an uncooled camera records the 0.0 sentinel on both lights and darks,
            // so they bucket-match. A cooled light (-10°C) is not covered by that uncooled (0.0) dark.
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42", temperatureC: 0.0);
            await InsertFrameAsync(Session, "dark", null, 300, 100, "DARK", temperatureC: 0.0);
            var matched = await _svc.GetSessionAsync(Session, CancellationToken.None);
            Assert.That(matched!.MatchingDarksAvailable, Is.True);

            var cooled = Guid.Parse("33333333-3333-3333-3333-3333333339c0");
            await InsertSessionAsync(cooled);
            await InsertFrameAsync(cooled, "light", "Ha", 300, 100, "M42", temperatureC: -10.0);
            var uncoveredCooled = await _svc.GetSessionAsync(cooled, CancellationToken.None);
            Assert.That(uncoveredCooled!.MatchingDarksAvailable, Is.False);
        }

        [Test]
        public async Task GenerateMatchingFlats_produces_one_step_per_light_filter() {
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");
            await InsertFrameAsync(Session, "light", "OIII", 300, 100, "M42");

            var plan = await _svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(OverrideFrameCount: 30, OverrideTargetAdu: 25000, GenerateOnly: true),
                CancellationToken.None);

            Assert.That(plan.Steps.Count, Is.EqualTo(2));
            Assert.That(plan.Steps.All(s => s.FrameCount == 30), Is.True);
            Assert.That(plan.Steps.All(s => s.TargetAdu == 25000), Is.True);
            Assert.That(plan.TotalFlatFrames, Is.EqualTo(60));
            Assert.That(plan.SourceSessionId, Is.EqualTo(Session));
        }

        [Test]
        public async Task GenerateMatchingFlats_defaults_to_20_frames_when_not_overridden() {
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");
            var plan = await _svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: true), CancellationToken.None);
            Assert.That(plan.Steps.Single().FrameCount, Is.EqualTo(20));
        }

        // ── §39.5 persisted runnable sequence ──────────────────────────────────

        [Test]
        public async Task GenerateMatchingFlats_persists_a_runnable_sequence() {
            var store = new FileSequenceService(_dir);
            var svc = new SqliteCalibrationService(_db, store);
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42", offset: 10, focuserPosition: 5230);
            await InsertFrameAsync(Session, "light", "OIII", 300, 100, "M42");

            var plan = await svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(OverrideFrameCount: 5, OverrideTargetAdu: null, GenerateOnly: false),
                CancellationToken.None);

            Assert.That(plan.GeneratedSequenceId, Is.Not.Null, "persisting mode must return the stored sequence id");
            var stored = await store.GetAsync(plan.GeneratedSequenceId!.Value, CancellationToken.None);
            Assert.That(stored, Is.Not.Null, "the sequence must be retrievable from the §38 store");
            Assert.That(stored!.Name, Is.EqualTo("Flats — M42"));
            Assert.That(stored.TemplateOrigin, Is.EqualTo("calibration:matching-flats"));

            var (valid, reason) = SequenceSchemaValidator.Validate(stored.Body);
            Assert.That(valid, Is.True, $"generated body must pass §38.5 validation: {reason}");

            // The decisive proof: the stored body deserializes through the REAL sequencer factory
            // into typed, executable instructions. The profile carries the wheel's filter list —
            // SwitchFilter resolves its recorded name against the ACTIVE profile on deserialize
            // (by design: the body stores the name, the daemon resolves it at load time).
            var profile = new HeadlessProfileService();
            profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Add(new OpenAstroAra.Core.Model.Equipment.FilterInfo("Ha", 0, 0));
            profile.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Add(new OpenAstroAra.Core.Model.Equipment.FilterInfo("OIII", 0, 1));
            var factory = HeadlessSequencerFactory.WithDefaults(profileService: profile);
            var root = new SequenceJsonConverter(factory).Deserialize(stored.Body.GetRawText());
            Assert.That(root, Is.Not.Null);
            var blocks = root.GetItemsSnapshot().OfType<SequentialContainer>().ToList();
            Assert.That(blocks.Count, Is.EqualTo(2), "one looped container per light filter");

            var ha = blocks[0];
            var loop = ha.GetConditionsSnapshot().OfType<LoopCondition>().Single();
            Assert.That(loop.Iterations, Is.EqualTo(5));
            var haItems = ha.GetItemsSnapshot();
            var switchFilter = haItems.OfType<SwitchFilter>().Single();
            Assert.That(switchFilter.Filter?.Name, Is.EqualTo("Ha"));
            var focus = haItems.OfType<MoveFocuserAbsolute>().Single();
            Assert.That(focus.Position, Is.EqualTo(5230), "per-filter focus from the session's lights is replayed");
            var take = haItems.OfType<TakeExposure>().Single();
            Assert.That(take.ImageType, Is.EqualTo("FLAT"));
            Assert.That(take.Gain, Is.EqualTo(100), "flats replay the lights' gain");
            Assert.That(take.Offset, Is.EqualTo(10), "flats replay the lights' offset");

            // The OIII lights carried no offset/focuser → no focuser step; defaults untouched.
            var oiiiItems = blocks[1].GetItemsSnapshot();
            Assert.That(oiiiItems.OfType<MoveFocuserAbsolute>(), Is.Empty);
            Assert.That(oiiiItems.OfType<TakeExposure>().Single().Offset, Is.EqualTo(-1),
                "no recorded offset → TakeExposure keeps its camera-default sentinel");
        }

        [Test]
        public async Task GenerateOnly_returns_the_plan_without_persisting() {
            var store = new FileSequenceService(_dir);
            var svc = new SqliteCalibrationService(_db, store);
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");

            var plan = await svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: true), CancellationToken.None);

            Assert.That(plan.GeneratedSequenceId, Is.Null);
            var list = await store.ListAsync(50, null, CancellationToken.None);
            Assert.That(list.Items, Is.Empty, "GenerateOnly must not write to the sequence store");
        }

        [Test]
        public async Task A_filterless_session_generates_a_block_without_a_SwitchFilter() {
            var store = new FileSequenceService(_dir);
            var svc = new SqliteCalibrationService(_db, store);
            await InsertFrameAsync(Session, "light", null, 300, 100, "Moon");

            var plan = await svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: false), CancellationToken.None);

            var stored = await store.GetAsync(plan.GeneratedSequenceId!.Value, CancellationToken.None);
            var factory = HeadlessSequencerFactory.WithDefaults();
            var root = new SequenceJsonConverter(factory).Deserialize(stored!.Body.GetRawText());
            var block = root.GetItemsSnapshot().OfType<SequentialContainer>().Single();
            Assert.That(block.GetItemsSnapshot().OfType<SwitchFilter>(), Is.Empty,
                "no filter wheel in the session → no SwitchFilter step");
            Assert.That(block.GetItemsSnapshot().OfType<TakeExposure>().Single().ImageType, Is.EqualTo("FLAT"));
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private async Task InsertSessionAsync(Guid id) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (id, profile_id, sequence_json, started_at, ended_at,
                    recovery_needed, last_completed_instruction_id, current_target_id, frame_count)
                VALUES ($id, NULL, NULL, $t, $t, 0, NULL, NULL, 0);
                """;
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        private async Task InsertFrameAsync(Guid sessionId, string frameType, string? filter, int exposureSeconds, int gain, string target, double temperatureC = -10.0, int? offset = null, int? focuserPosition = null) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                    exposure_seconds, gain, "offset", focuser_position, temperature_c, captured_utc, file_path,
                    file_size_bytes, width, height, bit_depth)
                VALUES ($id, $sid, $target, $type, $filter, $exp, $gain, $offset, $focus, $temp, $utc,
                    $path, 1000, 16, 16, 16);
                """;
            cmd.Parameters.AddWithValue("$offset", offset is null ? DBNull.Value : offset.Value);
            cmd.Parameters.AddWithValue("$focus", focuserPosition is null ? DBNull.Value : focuserPosition.Value);
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$sid", sessionId.ToString());
            cmd.Parameters.AddWithValue("$target", target);
            cmd.Parameters.AddWithValue("$type", frameType);
            cmd.Parameters.AddWithValue("$filter", (object?)filter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$exp", exposureSeconds);
            cmd.Parameters.AddWithValue("$gain", gain);
            cmd.Parameters.AddWithValue("$temp", temperatureC);
            cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$path", $"/tmp/{Guid.NewGuid():N}.fits");
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
