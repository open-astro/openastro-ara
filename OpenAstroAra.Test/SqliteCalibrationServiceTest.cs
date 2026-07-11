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
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.FilterWheel;
using OpenAstroAra.Sequencer.SequenceItem.FlatDevice;
using OpenAstroAra.Sequencer.SequenceItem.Focuser;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Serialization;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
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
        public async Task ListSessions_keyset_cursor_is_stable_when_a_newer_session_lands_mid_pagination() {
            // The pre-keyset integer-OFFSET cursor shifted the window when a new session arrived between
            // pages (duplicating the page boundary). The keyset cursor anchors on the last row's
            // (started, session_id), so a newer arrival can't disturb page 2.
            var s2 = Guid.Parse("44444444-4444-4444-4444-444444444442");
            var s3 = Guid.Parse("44444444-4444-4444-4444-444444444443");
            await InsertSessionAsync(s2);
            await InsertSessionAsync(s3);
            var t0 = new DateTimeOffset(2026, 7, 10, 1, 0, 0, TimeSpan.Zero);
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42", capturedUtc: t0);
            await InsertFrameAsync(s2, "light", "Ha", 300, 100, "M81", capturedUtc: t0.AddHours(1));
            await InsertFrameAsync(s3, "light", "Ha", 300, 100, "M101", capturedUtc: t0.AddHours(2));

            var page1 = await _svc.ListSessionsAsync(2, null, CancellationToken.None);
            Assert.That(page1.Items.Select(i => i.Id), Is.EqualTo(new[] { s3, s2 }), "newest first");

            // A brand-new session lands AFTER page 1 was served.
            var s4 = Guid.Parse("44444444-4444-4444-4444-444444444444");
            await InsertSessionAsync(s4);
            await InsertFrameAsync(s4, "light", "Ha", 300, 100, "NGC7000", capturedUtc: t0.AddHours(3));

            var page2 = await _svc.ListSessionsAsync(2, page1.NextCursor, CancellationToken.None);
            Assert.That(page2.Items.Select(i => i.Id), Is.EqualTo(new[] { Session }),
                "page 2 continues from the anchor — no duplicate of page 1, no skip, no bleed-in of the newcomer");
            Assert.That(page2.HasMore, Is.False);
        }

        [Test]
        public async Task ListSessions_does_not_duplicate_or_skip_sessions_with_identical_start_times() {
            // Equal MIN(captured_utc) across sessions — the session_id tiebreaker must keep the page
            // boundary deterministic (the pre-keyset ORDER BY had no tiebreaker at all).
            var t = new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero);
            var ids = new List<Guid> { Session };
            for (var i = 2; i <= 5; i++) {
                var sid = Guid.Parse($"55555555-5555-5555-5555-55555555555{i}");
                ids.Add(sid);
                await InsertSessionAsync(sid);
            }
            foreach (var sid in ids) {
                await InsertFrameAsync(sid, "light", "Ha", 300, 100, "M42", capturedUtc: t);
            }

            var seen = new List<Guid>();
            string? cursor = null;
            do {
                var page = await _svc.ListSessionsAsync(2, cursor, CancellationToken.None);
                seen.AddRange(page.Items.Select(i => i.Id));
                cursor = page.NextCursor;
            } while (cursor is not null);

            Assert.That(seen, Is.Unique, "no session appears on two pages despite the timestamp tie");
            Assert.That(seen, Is.EquivalentTo(ids), "every session appears exactly once");
        }

        [Test]
        public async Task ListSessions_still_accepts_a_legacy_integer_cursor() {
            // A client mid-pagination across the keyset upgrade still holds an integer cursor from the old
            // response shape — it must keep paging (via the legacy OFFSET path), not 500 or restart.
            var s2 = Guid.Parse("66666666-6666-6666-6666-666666666662");
            await InsertSessionAsync(s2);
            var t0 = new DateTimeOffset(2026, 7, 10, 3, 0, 0, TimeSpan.Zero);
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42", capturedUtc: t0);
            await InsertFrameAsync(s2, "light", "Ha", 300, 100, "M81", capturedUtc: t0.AddHours(1));

            var legacy = await _svc.ListSessionsAsync(1, "1", CancellationToken.None);
            Assert.That(legacy.Items.Select(i => i.Id), Is.EqualTo(new[] { Session }),
                "offset 1 skips the newest session, exactly the old semantics");
            // And the NEW cursor it hands out is keyset-format, migrating the client forward.
            var page1 = await _svc.ListSessionsAsync(1, null, CancellationToken.None);
            Assert.That(page1.NextCursor, Does.StartWith("k2:"));
        }

        [Test]
        public async Task ListSessions_attributes_fields_per_session_across_a_batched_page() {
            // #825 r1 — the batched page assembly is new SQL, so pin the FIELD attribution with two
            // sessions of deliberately different shape on ONE page: coverage, filters, target, and
            // profile id must each land on their own session, never leak from a page neighbor.
            var covered = Guid.Parse("77777777-7777-7777-7777-777777777771");
            var uncovered = Guid.Parse("77777777-7777-7777-7777-777777777772");
            await InsertSessionAsync(covered, profileId: "profile-covered");
            await InsertSessionAsync(uncovered); // NULL profile id
            var t0 = new DateTimeOffset(2026, 7, 10, 4, 0, 0, TimeSpan.Zero);
            // 'covered': two Ha lights at 300s/g100 with a matching flat + dark in the shared library.
            await InsertFrameAsync(covered, "light", "Ha", 300, 100, "M42", capturedUtc: t0);
            await InsertFrameAsync(covered, "light", "Ha", 300, 100, "M42", capturedUtc: t0.AddMinutes(5));
            await InsertFrameAsync(covered, "flat", "Ha", 1, 100, "FLAT", capturedUtc: t0.AddMinutes(6));
            await InsertFrameAsync(covered, "dark", null, 300, 100, "DARK", capturedUtc: t0.AddMinutes(7));
            // 'uncovered': one OIII light at 600s/g200 — no OIII flat, no 600s/g200 dark anywhere.
            await InsertFrameAsync(uncovered, "light", "OIII", 600, 200, "M101", capturedUtc: t0.AddHours(1));

            var page = await _svc.ListSessionsAsync(10, null, CancellationToken.None);
            // The fixture's SetUp session has no lights, so exactly these two are listed, newest first.
            Assert.That(page.Items.Select(i => i.Id), Is.EqualTo(new[] { uncovered, covered }));

            var u = page.Items[0];
            Assert.That(u.TargetName, Is.EqualTo("M101"));
            Assert.That(u.LightFrameCount, Is.EqualTo(1));
            Assert.That(u.FiltersUsed.Single().FilterName, Is.EqualTo("OIII"));
            Assert.That(u.FiltersUsed.Single().MeanExposureSeconds, Is.EqualTo(600));
            Assert.That(u.MatchingFlatsAvailable, Is.False, "the neighbor's Ha flat must not cover an OIII light");
            Assert.That(u.MatchingDarksAvailable, Is.False, "the neighbor's 300s/g100 dark must not cover a 600s/g200 light");
            Assert.That(u.ProfileId, Is.Null);

            var c = page.Items[1];
            Assert.That(c.TargetName, Is.EqualTo("M42"));
            Assert.That(c.LightFrameCount, Is.EqualTo(2));
            Assert.That(c.FiltersUsed.Single().FilterName, Is.EqualTo("Ha"));
            Assert.That(c.FiltersUsed.Single().LightFrameCount, Is.EqualTo(2));
            Assert.That(c.MatchingFlatsAvailable, Is.True);
            Assert.That(c.MatchingDarksAvailable, Is.True);
            Assert.That(c.ProfileId, Is.EqualTo("profile-covered"));

            // The batched page must agree field-for-field with the singular GetSessionAsync path.
            var single = await _svc.GetSessionAsync(uncovered, CancellationToken.None);
            Assert.That(single, Is.EqualTo(u) | Is.EqualTo(u with { FiltersUsed = single!.FiltersUsed }),
                "batched and singular assembly agree (FiltersUsed compared by content above)");
        }

        [Test]
        public async Task GenerateMatchingFlats_uses_the_default_target_adu_when_not_overridden() {
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");
            var plan = await _svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: true), idempotencyKey: null, CancellationToken.None);
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
        public async Task Uncooled_null_temperature_matches_across_generations() {
            // Sentinel pass: new uncooled frames record NULL; legacy rows may hold 0.0.
            // COALESCE bucketing keeps both generations matching each other.
            await InsertNullTempFrameAsync(Session, "light", "Ha", 300, 100, "M42");
            await InsertFrameAsync(Session, "dark", null, 300, 100, "DARK", temperatureC: 0.0); // legacy sentinel dark
            var matched = await _svc.GetSessionAsync(Session, CancellationToken.None);
            Assert.That(matched!.MatchingDarksAvailable, Is.True,
                "a NULL-temp light buckets with a legacy 0.0 dark (documented uncooled semantics)");
        }

        private async Task InsertNullTempFrameAsync(Guid sessionId, string frameType, string? filter, double exposureSeconds, int gain, string target) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                    exposure_seconds, gain, temperature_c, captured_utc, file_path,
                    file_size_bytes, width, height, bit_depth)
                VALUES ($id, $sid, $target, $type, $filter, $exp, $gain, NULL, $utc,
                    $path, 1000, 16, 16, 16);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$sid", sessionId.ToString());
            cmd.Parameters.AddWithValue("$target", target);
            cmd.Parameters.AddWithValue("$type", frameType);
            cmd.Parameters.AddWithValue("$filter", (object?)filter ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$exp", exposureSeconds);
            cmd.Parameters.AddWithValue("$gain", gain);
            cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$path", $"/tmp/{Guid.NewGuid():N}.fits");
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
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
                idempotencyKey: null, CancellationToken.None);

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
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: true), idempotencyKey: null, CancellationToken.None);
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
                idempotencyKey: null, CancellationToken.None);

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
            // §48.3: FlatPanelFlats captures its own FrameCount, so the block carries no loop.
            Assert.That(ha.GetConditionsSnapshot().OfType<LoopCondition>(), Is.Empty,
                "the auto-exposure flat set needs no LoopCondition wrapper");
            var haItems = ha.GetItemsSnapshot();
            var switchFilter = haItems.OfType<SwitchFilter>().Single();
            Assert.That(switchFilter.Filter?.Name, Is.EqualTo("Ha"));
            var focus = haItems.OfType<MoveFocuserAbsolute>().Single();
            Assert.That(focus.Position, Is.EqualTo(5230), "per-filter focus from the session's lights is replayed");
            var flats = haItems.OfType<FlatPanelFlats>().Single();
            Assert.That(flats.FrameCount, Is.EqualTo(5));
            Assert.That(flats.Gain, Is.EqualTo(100), "flats replay the lights' gain");
            Assert.That(flats.Offset, Is.EqualTo(10), "flats replay the lights' offset");

            // The OIII lights carried no offset/focuser → no focuser step; defaults untouched.
            var oiiiItems = blocks[1].GetItemsSnapshot();
            Assert.That(oiiiItems.OfType<MoveFocuserAbsolute>(), Is.Empty);
            Assert.That(oiiiItems.OfType<FlatPanelFlats>().Single().Offset, Is.EqualTo(-1),
                "no recorded offset → FlatPanelFlats keeps its camera-default sentinel");
        }

        [Test]
        public async Task GenerateMatchingFlats_reads_the_flat_panel_policy_and_appends_the_park() {
            var store = new FileSequenceService(_dir);
            var profile = new InMemoryProfileStore();
            profile.PutSafetyPolicies(profile.GetSafetyPolicies() with {
                FlatTargetAdu = 25000,
                FlatTargetAduTolerancePct = 3,
                FlatFramesPerFilter = 12,
                PostFlatParkMount = true,
            });
            var svc = new SqliteCalibrationService(_db, store, profile);
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");

            var plan = await svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: false),
                idempotencyKey: null, CancellationToken.None);

            Assert.That(plan.Steps.Single().FrameCount, Is.EqualTo(12));
            Assert.That(plan.Steps.Single().TargetAdu, Is.EqualTo(25000));

            var stored = await store.GetAsync(plan.GeneratedSequenceId!.Value, CancellationToken.None);
            var profileSvc = new HeadlessProfileService();
            profileSvc.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Add(new OpenAstroAra.Core.Model.Equipment.FilterInfo("Ha", 0, 0));
            var factory = HeadlessSequencerFactory.WithDefaults(profileService: profileSvc);
            var root = new SequenceJsonConverter(factory).Deserialize(stored!.Body.GetRawText());
            var block = root.GetItemsSnapshot().OfType<SequentialContainer>().Single();
            var flats = block.GetItemsSnapshot().OfType<FlatPanelFlats>().Single();
            Assert.That(flats.TargetAdu, Is.EqualTo(25000), "the §48.7 target ADU is enforced, not advisory");
            Assert.That(flats.TargetAduTolerancePct, Is.EqualTo(3));
            Assert.That(flats.FrameCount, Is.EqualTo(12));
            Assert.That(root.GetItemsSnapshot().OfType<ParkScope>().Count(), Is.EqualTo(1),
                "post_flat_park_mount appends the park as the final root step");
        }

        [Test]
        public async Task GenerateSkyFlats_wraps_the_set_in_a_twilight_gate_and_a_slew() {
            var store = new FileSequenceService(_dir);
            var profile = new InMemoryProfileStore();
            profile.PutSafetyPolicies(profile.GetSafetyPolicies() with {
                SkyFlatTargetAdu = 22000,
                SkyFlatFramesPerFilter = 18,
                SkyFlatTargetAzimuth = 100,
                SkyFlatTargetAltitude = 70,
                SkyFlatStopAtMaxAdu = 48000,
                SkyFlatStopAtMinAdu = 6000,
                SkyFlatSunAltitude = -8,
            });
            var svc = new SqliteCalibrationService(_db, store, profile);
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42", offset: 12, focuserPosition: 4100);

            var plan = await svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: false, Flavor: "sky"),
                idempotencyKey: null, CancellationToken.None);

            Assert.That(plan.GeneratedSequenceName, Is.EqualTo("Sky flats — M42"));
            Assert.That(plan.Steps.Single().FrameCount, Is.EqualTo(18));
            Assert.That(plan.Steps.Single().TargetAdu, Is.EqualTo(22000));

            var stored = await store.GetAsync(plan.GeneratedSequenceId!.Value, CancellationToken.None);
            Assert.That(stored!.Name, Is.EqualTo("Sky flats — M42"));
            var (valid, reason) = SequenceSchemaValidator.Validate(stored.Body);
            Assert.That(valid, Is.True, $"generated sky body must pass §38.5 validation: {reason}");

            // The twilight gate is asserted on the raw body: WaitForSunAltitude.Clone() eagerly
            // computes the sun altitude through the NOVAS native ephemeris, which isn't present in
            // a headless unit-test host — the generator (which only serializes) must not depend on
            // it, and neither should this test. The gate's shape is verified in the JSON directly.
            using var doc = System.Text.Json.JsonDocument.Parse(stored.Body.GetRawText());
            var bodyItems = doc.RootElement.GetProperty("Items").EnumerateArray().ToList();
            var waitNode = bodyItems.Single(i => i.GetProperty("$type").GetString()!.Contains("WaitForSunAltitude", StringComparison.Ordinal));
            var waitData = waitNode.GetProperty("Data");
            Assert.That(waitData.GetProperty("Offset").GetDouble(), Is.EqualTo(-8), "the twilight sun altitude gate is enforced");
            Assert.That(waitData.GetProperty("Comparator").GetInt32(), Is.EqualTo((int)ComparisonOperator.LessThan),
                "LessThan waits until the sun rises through the offset (morning twilight)");

            // The rest of the envelope [SlewScopeToAltAz → per-filter SkyFlats block] is
            // NOVAS-independent, so the decisive proof — deserialization through the real factory
            // into typed, executable instructions — still runs.
            var profileSvc = new HeadlessProfileService();
            profileSvc.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Add(new OpenAstroAra.Core.Model.Equipment.FilterInfo("Ha", 0, 0));
            var factory = HeadlessSequencerFactory.WithDefaults(profileService: profileSvc);
            var root = new SequenceJsonConverter(factory).Deserialize(stored.Body.GetRawText());
            var rootItems = root.GetItemsSnapshot();

            var slew = rootItems.OfType<SlewScopeToAltAz>().Single();
            Assert.That(slew.Coordinates.AzDegrees, Is.EqualTo(100));
            Assert.That(slew.Coordinates.AltDegrees, Is.EqualTo(70));

            var block = rootItems.OfType<SequentialContainer>().Single();
            var flats = block.GetItemsSnapshot().OfType<SkyFlats>().Single();
            Assert.That(flats.TargetAdu, Is.EqualTo(22000));
            Assert.That(flats.FrameCount, Is.EqualTo(18));
            Assert.That(flats.StopAtMaxAdu, Is.EqualTo(48000));
            Assert.That(flats.StopAtMinAdu, Is.EqualTo(6000));
            Assert.That(flats.Gain, Is.EqualTo(100), "sky flats replay the lights' gain");
            Assert.That(flats.Offset, Is.EqualTo(12), "sky flats replay the lights' offset");
            Assert.That(block.GetItemsSnapshot().OfType<MoveFocuserAbsolute>().Single().Position, Is.EqualTo(4100));
        }

        [Test]
        public async Task GenerateMatchingFlats_without_a_profile_store_omits_the_park() {
            var store = new FileSequenceService(_dir);
            var svc = new SqliteCalibrationService(_db, store);
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");

            var plan = await svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: false), idempotencyKey: null, CancellationToken.None);

            var stored = await store.GetAsync(plan.GeneratedSequenceId!.Value, CancellationToken.None);
            var profileSvc = new HeadlessProfileService();
            profileSvc.ActiveProfile.FilterWheelSettings.FilterWheelFilters.Add(new OpenAstroAra.Core.Model.Equipment.FilterInfo("Ha", 0, 0));
            var factory = HeadlessSequencerFactory.WithDefaults(profileService: profileSvc);
            var root = new SequenceJsonConverter(factory).Deserialize(stored!.Body.GetRawText());
            Assert.That(root.GetItemsSnapshot().OfType<ParkScope>(), Is.Empty);
        }

        [Test]
        public async Task GenerateOnly_returns_the_plan_without_persisting() {
            var store = new FileSequenceService(_dir);
            var svc = new SqliteCalibrationService(_db, store);
            await InsertFrameAsync(Session, "light", "Ha", 300, 100, "M42");

            var plan = await svc.GenerateMatchingFlatsAsync(
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: true), idempotencyKey: null, CancellationToken.None);

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
                Session, new MatchingFlatsRequestDto(null, null, GenerateOnly: false), idempotencyKey: null, CancellationToken.None);

            var stored = await store.GetAsync(plan.GeneratedSequenceId!.Value, CancellationToken.None);
            var factory = HeadlessSequencerFactory.WithDefaults();
            var root = new SequenceJsonConverter(factory).Deserialize(stored!.Body.GetRawText());
            var block = root.GetItemsSnapshot().OfType<SequentialContainer>().Single();
            Assert.That(block.GetItemsSnapshot().OfType<SwitchFilter>(), Is.Empty,
                "no filter wheel in the session → no SwitchFilter step");
            Assert.That(block.GetItemsSnapshot().OfType<FlatPanelFlats>().Count(), Is.EqualTo(1));
        }

        // ── helpers ────────────────────────────────────────────────────────────

        private async Task InsertSessionAsync(Guid id, string? profileId = null) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (id, profile_id, sequence_json, started_at, ended_at,
                    recovery_needed, last_completed_instruction_id, current_target_id, frame_count)
                VALUES ($id, $pid, NULL, $t, $t, 0, NULL, NULL, 0);
                """;
            cmd.Parameters.AddWithValue("$pid", (object?)profileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        private async Task InsertFrameAsync(Guid sessionId, string frameType, string? filter, int exposureSeconds, int gain, string target, double temperatureC = -10.0, int? offset = null, int? focuserPosition = null, DateTimeOffset? capturedUtc = null) {
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
            cmd.Parameters.AddWithValue("$utc", (capturedUtc ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$path", $"/tmp/{Guid.NewGuid():N}.fits");
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
