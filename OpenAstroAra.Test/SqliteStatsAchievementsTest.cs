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
using OpenAstroAra.Server.Services;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// §50.19 Stats Achievements: cumulative records + imaging-night streaks + milestone badges,
    /// aggregated over the light-frame catalog (dark/flat frames are excluded).
    /// </summary>
    [TestFixture]
    public class SqliteStatsAchievementsTest {

        private string _dir = null!;
        private SqliteAraDatabase _db = null!;
        private SqliteStatsService _svc = null!;
        private static readonly Guid Session = Guid.Parse("44444444-4444-4444-4444-444444444441");

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-stats-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _svc = new SqliteStatsService(_db);
            await InsertSessionAsync(Session);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task Empty_catalog_returns_zeroed_achievements() {
            var a = await _svc.GetAchievementsAsync(CancellationToken.None);
            Assert.That(a.TotalNightsImaged, Is.EqualTo(0));
            Assert.That(a.TotalLightFrames, Is.EqualTo(0));
            Assert.That(a.TotalIntegrationHours, Is.EqualTo(0));
            Assert.That(a.LongestStreakNights, Is.EqualTo(0));
            Assert.That(a.CurrentStreakNights, Is.EqualTo(0));
            Assert.That(a.FirstLightUtc, Is.Null);
            Assert.That(a.Milestones.Any(m => m.Achieved), Is.False, "nothing is unlocked on an empty catalog");
        }

        [Test]
        public async Task Aggregates_nights_hours_targets_and_unlocks_milestones() {
            // 3 consecutive nights ending tonight; a 10h night + two 1h nights = 12h over 2 targets. Dark is ignored.
            await InsertLightAsync("M31", 36000, NightUtc(2));
            await InsertLightAsync("M42", 3600, NightUtc(1));
            await InsertLightAsync("M31", 3600, NightUtc(0));
            await InsertFrameAsync("dark", "M31", 600, NightUtc(2));

            var a = await _svc.GetAchievementsAsync(CancellationToken.None);

            Assert.That(a.TotalNightsImaged, Is.EqualTo(3));
            Assert.That(a.TotalLightFrames, Is.EqualTo(3), "dark frame excluded");
            Assert.That(a.TotalIntegrationHours, Is.EqualTo(12).Within(1e-6));
            Assert.That(a.LongestNightHours, Is.EqualTo(10).Within(1e-6));
            Assert.That(a.UniqueTargetsImaged, Is.EqualTo(2));
            Assert.That(a.LongestStreakNights, Is.EqualTo(3));
            Assert.That(a.CurrentStreakNights, Is.EqualTo(3), "3 consecutive nights ending tonight");
            Assert.That(a.FirstLightUtc, Is.EqualTo(NightUtc(2)));

            var hours10 = a.Milestones.Single(m => m.Id == "hours_10");
            Assert.That(hours10.Achieved, Is.True, "12h ≥ 10h threshold");
            Assert.That(hours10.Current, Is.EqualTo(12).Within(1e-6));
            Assert.That(a.Milestones.Single(m => m.Id == "hours_50").Achieved, Is.False);
            Assert.That(a.Milestones.Single(m => m.Id == "targets_10").Achieved, Is.False, "only 2 targets");
        }

        [Test]
        public async Task Current_streak_resets_after_a_gap_but_longest_is_retained() {
            await InsertLightAsync("M31", 3600, NightUtc(4));
            await InsertLightAsync("M31", 3600, NightUtc(3));
            // gap on day-2 / day-1
            await InsertLightAsync("M31", 3600, NightUtc(0)); // tonight

            var a = await _svc.GetAchievementsAsync(CancellationToken.None);

            Assert.That(a.TotalNightsImaged, Is.EqualTo(3));
            Assert.That(a.LongestStreakNights, Is.EqualTo(2), "the two consecutive early nights");
            Assert.That(a.CurrentStreakNights, Is.EqualTo(1), "only tonight — the prior night had a gap");
        }

        [Test]
        public async Task Current_streak_is_zero_when_the_last_night_is_stale() {
            // A long streak entirely in the distant past: longest is retained, current is 0 (not imaged recently).
            await InsertLightAsync("M31", 3600, NightUtc(100));
            await InsertLightAsync("M31", 3600, NightUtc(99));
            await InsertLightAsync("M31", 3600, NightUtc(98));

            var a = await _svc.GetAchievementsAsync(CancellationToken.None);

            Assert.That(a.LongestStreakNights, Is.EqualTo(3));
            Assert.That(a.CurrentStreakNights, Is.EqualTo(0), "the last night was 98 days ago");
        }

        [Test]
        public async Task Current_streak_survives_when_the_last_night_was_yesterday() {
            // The 1-day grace branch: most-recent night is yesterday (an in-progress night before midnight UTC
            // would land here), so the streak is still "live".
            await InsertLightAsync("M31", 3600, NightUtc(2));
            await InsertLightAsync("M31", 3600, NightUtc(1));

            var a = await _svc.GetAchievementsAsync(CancellationToken.None);

            Assert.That(a.LongestStreakNights, Is.EqualTo(2));
            Assert.That(a.CurrentStreakNights, Is.EqualTo(2), "yesterday is within the 1-day grace");
        }

        [Test]
        public async Task Current_streak_is_zero_when_the_last_night_was_two_days_ago() {
            // The boundary just past the grace: most-recent night is the day before yesterday → stale.
            await InsertLightAsync("M31", 3600, NightUtc(3));
            await InsertLightAsync("M31", 3600, NightUtc(2));

            var a = await _svc.GetAchievementsAsync(CancellationToken.None);

            Assert.That(a.LongestStreakNights, Is.EqualTo(2));
            Assert.That(a.CurrentStreakNights, Is.EqualTo(0), "two days ago is past the 1-day grace");
        }

        [Test]
        public async Task Multiple_frames_on_one_night_count_as_a_single_night() {
            await InsertLightAsync("M31", 1800, NightUtc(0));
            await InsertFrameAsync("light", "M31", 1800, NightUtc(0).AddHours(1));

            var a = await _svc.GetAchievementsAsync(CancellationToken.None);
            Assert.That(a.TotalNightsImaged, Is.EqualTo(1));
            Assert.That(a.TotalLightFrames, Is.EqualTo(2));
            Assert.That(a.LongestNightHours, Is.EqualTo(1).Within(1e-6), "1800+1800s = 1h that night");
        }

        // ── helpers ────────────────────────────────────────────────────────────

        // A capture time on the night `daysAgo` days before today (22:00 UTC), so streak/staleness logic is
        // evaluated relative to the real current date.
        private static DateTimeOffset NightUtc(int daysAgo) =>
            new(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-daysAgo).ToDateTime(new TimeOnly(22, 0)), TimeSpan.Zero);

        private Task InsertLightAsync(string target, int exposureSeconds, DateTimeOffset capturedUtc) =>
            InsertFrameAsync("light", target, exposureSeconds, capturedUtc);

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

        private async Task InsertFrameAsync(string frameType, string target, int exposureSeconds, DateTimeOffset capturedUtc) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                    exposure_seconds, gain, temperature_c, captured_utc, file_path,
                    file_size_bytes, width, height, bit_depth)
                VALUES ($id, $sid, $target, $type, NULL, $exp, 100, -10.0, $utc,
                    $path, 1000, 16, 16, 16);
                """;
            cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            cmd.Parameters.AddWithValue("$sid", Session.ToString());
            cmd.Parameters.AddWithValue("$target", target);
            cmd.Parameters.AddWithValue("$type", frameType);
            cmd.Parameters.AddWithValue("$exp", exposureSeconds);
            cmd.Parameters.AddWithValue("$utc", capturedUtc.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$path", $"/tmp/{Guid.NewGuid():N}.fits");
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
