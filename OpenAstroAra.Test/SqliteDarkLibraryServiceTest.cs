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
using OpenAstroAra.Sequencer.SequenceItem.Camera;
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
    /// §39.8 — <see cref="SqliteDarkLibraryService"/> over a real temp SQLite catalog. The library
    /// is the catalog's DARK frames grouped by the §39 matching key; the build generates a
    /// runnable §38 dark-matrix sequence rather than running a parallel capture engine.
    /// </summary>
    [TestFixture]
    public class SqliteDarkLibraryServiceTest {

        private string _dir = string.Empty;
        private SqliteAraDatabase _db = null!;
        private static readonly Guid Session = Guid.Parse("44444444-4444-4444-4444-444444444441");
        private static readonly double[] BothExposures = [60.0, 300.0];
        private static readonly double[] OnlyLongExposure = [300.0];

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-darklib-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            await InsertSessionAsync(Session);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ── entries (catalog-backed reads) ─────────────────────────────────────

        [Test]
        public async Task Entries_group_darks_by_exposure_gain_and_temperature_bucket() {
            await InsertDarkAsync(300, 100, -10.1, sizeBytes: 100);
            await InsertDarkAsync(300, 100, -9.9, sizeBytes: 150); // same whole-degree bucket
            await InsertDarkAsync(300, 100, -5.0, sizeBytes: 100); // different bucket
            await InsertDarkAsync(60, 100, -10.0, sizeBytes: 100); // different exposure
            await InsertDarkAsync(300, null, -10.0, sizeBytes: 100); // no gain reported

            using var svc = new SqliteDarkLibraryService(_db);
            var entries = await svc.ListEntriesAsync(CancellationToken.None);

            Assert.That(entries.Count, Is.EqualTo(4));
            var main = entries.Single(e => e.ExposureSeconds == 300 && e.Gain == 100 && e.TemperatureC == -10);
            Assert.That(main.FrameCount, Is.EqualTo(2), "-10.1 and -9.9 share the whole-degree bucket");
            Assert.That(main.FileSizeBytes, Is.EqualTo(250), "group total, not a single frame");
            Assert.That(main.FilePath, Is.Not.Empty, "newest frame is the representative file");
            var noGain = entries.Single(e => e.Gain is null);
            Assert.That(noGain.ExposureSeconds, Is.EqualTo(300), "NULL-gain darks group honestly, no sentinel");
        }

        [Test]
        public async Task Entry_ids_are_stable_across_calls() {
            await InsertDarkAsync(300, 100, -10.0);
            using var svc = new SqliteDarkLibraryService(_db);
            var first = (await svc.ListEntriesAsync(CancellationToken.None)).Single().Id;
            var second = (await svc.ListEntriesAsync(CancellationToken.None)).Single().Id;
            Assert.That(second, Is.EqualTo(first), "same (exposure, gain, temp) group ⇒ same id");
        }

        // ── build (generated runnable sequence) ────────────────────────────────

        [Test]
        public async Task StartBuild_persists_a_runnable_dark_matrix_sequence() {
            var store = new FileSequenceService(_dir);
            using var svc = new SqliteDarkLibraryService(_db, store);

            await svc.StartBuildAsync(new DarkLibraryBuildRequestDto(
                ExposureSecondsList: [60, 300],
                GainList: [100],
                TargetTemperatureCList: [-10.0],
                FramesPerCombination: 30,
                ReuseExistingFrames: false), idempotencyKey: null, CancellationToken.None);

            var status = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(status.GeneratedSequenceId, Is.Not.Null);
            var stored = await store.GetAsync(status.GeneratedSequenceId!.Value, CancellationToken.None);
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.TemplateOrigin, Is.EqualTo("calibration:dark-library"));

            var (valid, reason) = SequenceSchemaValidator.Validate(stored.Body);
            Assert.That(valid, Is.True, $"generated body must pass §38.5 validation: {reason}");

            // Deserialize through the REAL sequencer factory: CoolCamera to the set-point,
            // then one looped TakeExposure(DARK) container per (exposure, gain) combination.
            var factory = HeadlessSequencerFactory.WithDefaults();
            var root = new SequenceJsonConverter(factory).Deserialize(stored.Body.GetRawText());
            var items = root.GetItemsSnapshot();
            var cool = items.OfType<CoolCamera>().Single();
            Assert.That(cool.Temperature, Is.EqualTo(-10.0));
            var blocks = items.OfType<SequentialContainer>().ToList();
            Assert.That(blocks.Count, Is.EqualTo(2), "one block per (exposure, gain) combination");
            foreach (var block in blocks) {
                Assert.That(block.GetConditionsSnapshot().OfType<LoopCondition>().Single().Iterations, Is.EqualTo(30));
                var take = block.GetItemsSnapshot().OfType<TakeExposure>().Single();
                Assert.That(take.ImageType, Is.EqualTo("DARK"));
                Assert.That(take.Gain, Is.EqualTo(100));
            }
            Assert.That(blocks.SelectMany(b => b.GetItemsSnapshot()).OfType<TakeExposure>().Select(t => t.ExposureTime),
                Is.EquivalentTo(BothExposures));
        }

        [Test]
        public async Task Empty_gain_and_temperature_lists_mean_camera_default_at_ambient() {
            var store = new FileSequenceService(_dir);
            using var svc = new SqliteDarkLibraryService(_db, store);

            await svc.StartBuildAsync(new DarkLibraryBuildRequestDto(
                ExposureSecondsList: [0.5],
                GainList: [],
                TargetTemperatureCList: [],
                FramesPerCombination: 50,
                ReuseExistingFrames: false), idempotencyKey: null, CancellationToken.None);

            var status = await svc.GetStatusAsync(CancellationToken.None);
            var stored = await store.GetAsync(status.GeneratedSequenceId!.Value, CancellationToken.None);
            var factory = HeadlessSequencerFactory.WithDefaults();
            var root = new SequenceJsonConverter(factory).Deserialize(stored!.Body.GetRawText());
            var items = root.GetItemsSnapshot();
            Assert.That(items.OfType<CoolCamera>(), Is.Empty, "no set-point → capture at ambient, no CoolCamera");
            var take = items.OfType<SequentialContainer>().Single().GetItemsSnapshot().OfType<TakeExposure>().Single();
            Assert.That(take.ExposureTime, Is.EqualTo(0.5), "§28: sub-second darks are expressible");
            Assert.That(take.Gain, Is.EqualTo(-1), "no gain requested → TakeExposure's camera-default sentinel");
        }

        [Test]
        public async Task ReuseExistingFrames_skips_combinations_the_catalog_already_covers() {
            var store = new FileSequenceService(_dir);
            using var svc = new SqliteDarkLibraryService(_db, store);
            // 60s g100 @-10°C already has 2 darks — enough for FramesPerCombination: 2.
            await InsertDarkAsync(60, 100, -10.0);
            await InsertDarkAsync(60, 100, -10.0);

            await svc.StartBuildAsync(new DarkLibraryBuildRequestDto(
                ExposureSecondsList: [60, 300],
                GainList: [100],
                TargetTemperatureCList: [-10.0],
                FramesPerCombination: 2,
                ReuseExistingFrames: true), idempotencyKey: null, CancellationToken.None);

            var status = await svc.GetStatusAsync(CancellationToken.None);
            var stored = await store.GetAsync(status.GeneratedSequenceId!.Value, CancellationToken.None);
            var factory = HeadlessSequencerFactory.WithDefaults();
            var root = new SequenceJsonConverter(factory).Deserialize(stored!.Body.GetRawText());
            var exposures = root.GetItemsSnapshot().OfType<SequentialContainer>()
                .SelectMany(b => b.GetItemsSnapshot()).OfType<TakeExposure>().Select(t => t.ExposureTime).ToList();
            Assert.That(exposures, Is.EqualTo(OnlyLongExposure), "the covered 60s combination is skipped");
            // Both combinations still count toward status (one already complete).
            Assert.That(status.TotalCombinations, Is.EqualTo(2));
            Assert.That(status.CompletedCombinations, Is.EqualTo(1));
        }

        [Test]
        public void An_empty_exposure_list_is_rejected() {
            using var svc = new SqliteDarkLibraryService(_db);
            Assert.ThrowsAsync<ArgumentException>(() => svc.StartBuildAsync(new DarkLibraryBuildRequestDto(
                ExposureSecondsList: [],
                GainList: [100],
                TargetTemperatureCList: [-10.0],
                FramesPerCombination: 30,
                ReuseExistingFrames: false), idempotencyKey: null, CancellationToken.None));
        }

        // ── status ──────────────────────────────────────────────────────────────

        [Test]
        public async Task Status_tracks_coverage_from_pending_to_complete() {
            var store = new FileSequenceService(_dir);
            using var svc = new SqliteDarkLibraryService(_db, store);

            var idle = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(idle.Status, Is.EqualTo("idle"));
            Assert.That(idle.GeneratedSequenceId, Is.Null);

            await svc.StartBuildAsync(new DarkLibraryBuildRequestDto(
                ExposureSecondsList: [60],
                GainList: [100],
                TargetTemperatureCList: [-10.0],
                FramesPerCombination: 2,
                ReuseExistingFrames: false), idempotencyKey: null, CancellationToken.None);

            var pending = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(pending.Status, Is.EqualTo("pending"));
            Assert.That(pending.TotalCombinations, Is.EqualTo(1));
            Assert.That(pending.CompletedCombinations, Is.EqualTo(0));
            Assert.That(pending.BuildStartedUtc, Is.Not.Null);
            Assert.That(pending.BuildCompletedUtc, Is.Null);

            // The generated sequence "runs" — darks land in the catalog via the normal pipeline.
            await InsertDarkAsync(60, 100, -10.0);
            await InsertDarkAsync(60, 100, -10.2); // same bucket

            var complete = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(complete.Status, Is.EqualTo("complete"));
            Assert.That(complete.CompletedCombinations, Is.EqualTo(1));
            Assert.That(complete.BuildCompletedUtc, Is.Not.Null, "completion is stamped when coverage is first observed");

            var again = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(again.BuildCompletedUtc, Is.EqualTo(complete.BuildCompletedUtc), "stamp is idempotent");
        }

        [Test]
        public async Task Ambient_builds_ignore_preexisting_darks_from_other_temperatures() {
            // The r3 failure scenario: 2 cooled darks at (60s, g100, -10°C) already catalogued.
            await InsertDarkAsync(60, 100, -10.0);
            await InsertDarkAsync(60, 100, -10.0);
            var store = new FileSequenceService(_dir);
            using var svc = new SqliteDarkLibraryService(_db, store);

            // An ambient build (no set-point, ReuseExistingFrames: false) for the same
            // exposure/gain must NOT be reported complete by those unrelated cooled frames.
            await svc.StartBuildAsync(new DarkLibraryBuildRequestDto(
                ExposureSecondsList: [60],
                GainList: [100],
                TargetTemperatureCList: [],
                FramesPerCombination: 2,
                ReuseExistingFrames: false), idempotencyKey: null, CancellationToken.None);

            var before = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(before.Status, Is.EqualTo("pending"),
                "pre-existing cooled darks must not complete an ambient build that never ran");
            Assert.That(before.CompletedCombinations, Is.EqualTo(0));

            // Darks captured AFTER the build was requested (the generated sequence ran) count.
            await InsertDarkAsync(60, 100, 12.5);
            await InsertDarkAsync(60, 100, 13.1);
            var after = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(after.Status, Is.EqualTo("complete"));
        }

        [Test]
        public async Task A_non_reuse_set_point_build_ignores_preexisting_coverage() {
            // r4: ReuseExistingFrames: false asks for FRESH frames — pre-existing matching
            // darks (same exposure/gain/temperature!) must not light the progress green
            // before the generated sequence runs.
            await InsertDarkAsync(300, 100, -10.0);
            await InsertDarkAsync(300, 100, -10.0);
            var store = new FileSequenceService(_dir);
            using var svc = new SqliteDarkLibraryService(_db, store);

            await svc.StartBuildAsync(new DarkLibraryBuildRequestDto(
                ExposureSecondsList: [300],
                GainList: [100],
                TargetTemperatureCList: [-10.0],
                FramesPerCombination: 2,
                ReuseExistingFrames: false), idempotencyKey: null, CancellationToken.None);

            var before = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(before.Status, Is.EqualTo("pending"),
                "the user asked for fresh darks; old coverage must not complete the build");

            await InsertDarkAsync(300, 100, -10.0);
            await InsertDarkAsync(300, 100, -10.4); // same bucket, captured after the request
            var after = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(after.Status, Is.EqualTo("complete"));
        }

        [Test]
        public async Task Set_points_colliding_after_whole_degree_rounding_merge_into_one_combination() {
            // r5: -10.4 and -9.6 both bucket to -10 — the library can't tell their darks
            // apart downstream, so generating two CoolCamera blocks would double-spend rig
            // time while either block's captures satisfied both coverage counts.
            var store = new FileSequenceService(_dir);
            using var svc = new SqliteDarkLibraryService(_db, store);

            await svc.StartBuildAsync(new DarkLibraryBuildRequestDto(
                ExposureSecondsList: [60],
                GainList: [100],
                TargetTemperatureCList: [-10.4, -9.6],
                FramesPerCombination: 2,
                ReuseExistingFrames: false), idempotencyKey: null, CancellationToken.None);

            var status = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(status.TotalCombinations, Is.EqualTo(1), "colliding set-points merge");

            var stored = await store.GetAsync(status.GeneratedSequenceId!.Value, CancellationToken.None);
            var factory = HeadlessSequencerFactory.WithDefaults();
            var root = new SequenceJsonConverter(factory).Deserialize(stored!.Body.GetRawText());
            var cools = root.GetItemsSnapshot().OfType<CoolCamera>().ToList();
            Assert.That(cools.Count, Is.EqualTo(1), "one CoolCamera block, not two");
            Assert.That(cools[0].Temperature, Is.EqualTo(-10.4), "first requested set-point per bucket wins");
        }

        [Test]
        public void A_temperature_list_of_only_NaN_is_rejected_not_silently_ambient() {
            using var svc = new SqliteDarkLibraryService(_db);
            Assert.ThrowsAsync<ArgumentException>(() => svc.StartBuildAsync(new DarkLibraryBuildRequestDto(
                ExposureSecondsList: [60],
                GainList: [100],
                TargetTemperatureCList: [double.NaN],
                FramesPerCombination: 2,
                ReuseExistingFrames: false), idempotencyKey: null, CancellationToken.None));
        }

        [Test]
        public async Task A_reuse_ambient_build_counts_preexisting_darks_consistently() {
            // Opposite intent: ReuseExistingFrames opted into any-temperature matching for
            // ambient combos, so the skip AND the status must both count the old frames —
            // otherwise a reuse-skipped combination would read "pending" forever.
            await InsertDarkAsync(60, 100, -10.0);
            await InsertDarkAsync(60, 100, -10.0);
            var store = new FileSequenceService(_dir);
            using var svc = new SqliteDarkLibraryService(_db, store);

            await svc.StartBuildAsync(new DarkLibraryBuildRequestDto(
                ExposureSecondsList: [60],
                GainList: [100],
                TargetTemperatureCList: [],
                FramesPerCombination: 2,
                ReuseExistingFrames: true), idempotencyKey: null, CancellationToken.None);

            var status = await svc.GetStatusAsync(CancellationToken.None);
            Assert.That(status.Status, Is.EqualTo("complete"), "everything was already covered");
            Assert.That(status.GeneratedSequenceId, Is.Null, "nothing to capture, nothing persisted");
            Assert.That(status.BuildCompletedUtc, Is.Not.Null);
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

        private async Task InsertDarkAsync(double exposureSeconds, int? gain, double temperatureC, long sizeBytes = 1000) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                    exposure_seconds, gain, temperature_c, captured_utc, file_path,
                    file_size_bytes, width, height, bit_depth)
                VALUES ($id, $sid, 'DARK', 'dark', NULL, $exp, $gain, $temp, $utc,
                    $path, $size, 16, 16, 16);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$sid", Session.ToString());
            cmd.Parameters.AddWithValue("$exp", exposureSeconds);
            cmd.Parameters.AddWithValue("$gain", gain is null ? DBNull.Value : gain.Value);
            cmd.Parameters.AddWithValue("$temp", temperatureC);
            cmd.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$path", $"/tmp/{Guid.NewGuid():N}.fits");
            cmd.Parameters.AddWithValue("$size", sizeBytes);
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
