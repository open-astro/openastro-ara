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
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// The #923 gate's missing half: builds published BEFORE the gate seeded
    /// the demo fixtures (M31 session, sample notifications, sample
    /// diagnostics) unconditionally into every real install's catalog — a
    /// fresh user's Stats reported a night they never had. Upgrading never
    /// removed the rows, so each service now scrubs its fixed sentinel ids on
    /// boot when sample seeding is disabled. These pin both directions: the
    /// fixtures go, and real rows (random v4 ids) are untouchable.
    /// </summary>
    [TestFixture]
    public class SampleDataScrubTest {

        private string _profileDir = string.Empty;
        private SqliteAraDatabase _db = null!;

        [SetUp]
        public async Task SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-scrub-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
            _db = new SqliteAraDatabase(_profileDir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown() {
            try { Directory.Delete(_profileDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task Frame_scrub_removes_the_demo_session_and_spares_real_data() {
            var repo = new SqliteFrameRepository(_db, new InMemoryProfileStore());
            await repo.EnsureSeededAsync(CancellationToken.None);
            // A real session + frame, as the orchestrator would write them.
            var realSession = Guid.NewGuid();
            var realFrame = Guid.NewGuid();
            await ExecAsync("""
                INSERT INTO sessions (id, profile_id, sequence_json, started_at, ended_at,
                    recovery_needed, last_completed_instruction_id, current_target_id, frame_count)
                VALUES ($a, NULL, NULL, '2026-08-01T03:00:00.0000000+00:00', NULL, 0, NULL, NULL, 1);
                """, ("$a", realSession.ToString()));
            await ExecAsync("""
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                    exposure_seconds, captured_utc, file_path, file_size_bytes, width, height, bit_depth)
                VALUES ($a, $b, 'NGC 6188', 'light', NULL, 300,
                    '2026-08-01T03:05:00.0000000+00:00', '/store/ngc6188-0001.fits', 1, 4, 4, 16);
                """, ("$a", realFrame.ToString()), ("$b", realSession.ToString()));

            await repo.ScrubSampleDataAsync(CancellationToken.None);

            Assert.That(await CountAsync("SELECT COUNT(*) FROM frames WHERE session_id = '11111111-1111-1111-1111-111111111111';"),
                Is.Zero, "the three demo M31/dark frames are gone");
            Assert.That(await CountAsync("SELECT COUNT(*) FROM sessions WHERE id = '11111111-1111-1111-1111-111111111111';"),
                Is.Zero, "the demo session row is gone");
            Assert.That(await CountAsync("SELECT COUNT(*) FROM frames;"), Is.EqualTo(1),
                "the user's real frame survives");
            Assert.That(await CountAsync("SELECT COUNT(*) FROM sessions;"), Is.EqualTo(1),
                "the user's real session survives");

            // Idempotent — a second boot-scrub is a clean no-op.
            await repo.ScrubSampleDataAsync(CancellationToken.None);
            Assert.That(await CountAsync("SELECT COUNT(*) FROM frames;"), Is.EqualTo(1));
        }

        [Test]
        public async Task Frame_scrub_leaves_the_demo_session_row_if_real_frames_were_moved_into_it() {
            var repo = new SqliteFrameRepository(_db, new InMemoryProfileStore());
            await repo.EnsureSeededAsync(CancellationToken.None);
            // A user could bulk-move their own frame into the demo session;
            // deleting the session would orphan it (FK), so the session must
            // survive while the fixture frames still go.
            await ExecAsync("""
                INSERT INTO frames (id, session_id, target_name, frame_type, filter_name,
                    exposure_seconds, captured_utc, file_path, file_size_bytes, width, height, bit_depth)
                VALUES ($a, '11111111-1111-1111-1111-111111111111', 'M31', 'light', NULL, 120,
                    '2026-08-01T04:00:00.0000000+00:00', '/store/m31-real.fits', 1, 4, 4, 16);
                """, ("$a", Guid.NewGuid().ToString()));

            await repo.ScrubSampleDataAsync(CancellationToken.None);

            Assert.That(await CountAsync("SELECT COUNT(*) FROM frames;"), Is.EqualTo(1),
                "fixture frames removed, the adopted real frame kept");
            Assert.That(await CountAsync("SELECT COUNT(*) FROM sessions WHERE id = '11111111-1111-1111-1111-111111111111';"),
                Is.EqualTo(1), "a session still owning real frames is not deleted");
        }

        [Test]
        public async Task Notification_scrub_removes_the_three_fixtures_and_spares_real_ones() {
            var svc = new SqliteNotificationService(_db, logger: null);
            await svc.EnsureSeededAsync(CancellationToken.None);
            await ExecAsync("""
                INSERT INTO notifications (id, posted_utc, severity, category, title, message,
                    read, dismissed, dismissed_utc, payload_json, related_entity_type, related_entity_id)
                VALUES ($a, '2026-07-05T02:00:00.0000000+00:00', 'Warning', 'Storage',
                    'Low disk space', 'Disk space low: 9.9 GB free.', 0, 0, NULL, NULL, NULL, NULL);
                """, ("$a", Guid.NewGuid().ToString()));

            await svc.ScrubSampleDataAsync(CancellationToken.None);

            Assert.That(await CountAsync("SELECT COUNT(*) FROM notifications WHERE id LIKE '33333333-%';"),
                Is.Zero, "the May-30 fixture notifications are gone");
            Assert.That(await CountAsync("SELECT COUNT(*) FROM notifications;"), Is.EqualTo(1),
                "the rig's real notification survives");
        }

        [Test]
        public async Task Diagnostics_scrub_removes_the_fixture_events_and_spares_real_ones() {
            var svc = new SqliteDiagnosticsService(_db, logger: null);
            await svc.EnsureSeededAsync(CancellationToken.None);
            await ExecAsync("""
                INSERT INTO diagnostic_events (id, event_type, severity, description,
                    detected_utc, cleared_utc, auto_action_taken, auto_action_description,
                    recommended_action, auto_correctible)
                VALUES ($a, 'guider.lost', 'Red', 'PHD2 lost the star.',
                    '2026-08-01T03:00:00.0000000+00:00', NULL, 0, NULL, NULL, NULL);
                """, ("$a", Guid.NewGuid().ToString()));

            await svc.ScrubSampleDataAsync(CancellationToken.None);

            Assert.That(await CountAsync("SELECT COUNT(*) FROM diagnostic_events WHERE id LIKE '44444444-%' OR id LIKE '55555555-%';"),
                Is.Zero, "the fixture issue + history events are gone");
            Assert.That(await CountAsync("SELECT COUNT(*) FROM diagnostic_events;"), Is.EqualTo(1),
                "the rig's real event survives");
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "Every caller passes a compile-time-constant query; values travel via parameters.")]
        private async Task ExecAsync(string sql, params (string Name, string Value)[] args) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) {
                cmd.Parameters.AddWithValue(name, value);
            }
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
            Justification = "Every caller passes a compile-time-constant query.")]
        private async Task<long> CountAsync(string sql) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return (long)(await cmd.ExecuteScalarAsync(CancellationToken.None) ?? 0L);
        }
    }
}
