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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Regression tests for the has_more pagination bug shared by the
    /// notifications / diagnostics / frames list queries: the loop condition
    /// <c>await reader.ReadAsync(ct) &amp;&amp; items.Count &lt; pageSize</c>
    /// consumed the LIMIT pageSize+1 sentinel row before the count check, so
    /// the follow-up has-more read always came up empty and every page
    /// reported <c>has_more=false</c> — a well-behaved client stopped
    /// paginating after page 1. Each test walks a 3-row catalog in pages of 2
    /// and asserts both pages arrive with no overlap.
    /// </summary>
    [TestFixture]
    public class SqlitePaginationHasMoreTest {

        private string _profileDir = string.Empty;
        private SqliteAraDatabase _db = null!;

        [SetUp]
        public async Task SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-hasmore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_profileDir);
            _db = new SqliteAraDatabase(_profileDir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown() {
            try {
                Directory.Delete(_profileDir, recursive: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        [Test]
        public async Task Notifications_second_page_is_reachable() {
            var svc = new SqliteNotificationService(_db, logger: null);
            var ids = new Guid[3];
            for (var i = 0; i < 3; i++) {
                ids[i] = Guid.NewGuid();
                await svc.CreateAsync(new NotificationDto(
                    Id: ids[i], PostedUtc: DateTimeOffset.UtcNow.AddMinutes(i),
                    Severity: NotificationSeverity.Info, Category: NotificationCategory.Sequence,
                    Title: $"n{i}", Message: $"m{i}", Read: false, Dismissed: false,
                    DismissedUtc: null, Payload: null, RelatedEntityType: null,
                    RelatedEntityId: null), CancellationToken.None);
            }

            var page1 = await svc.ListAsync(2, cursor: null, unreadOnly: null, CancellationToken.None);
            Assert.That(page1.Items, Has.Count.EqualTo(2));
            Assert.That(page1.HasMore, Is.True, "3 rows in pages of 2 must report a second page");
            var page2 = await svc.ListAsync(2, page1.NextCursor, unreadOnly: null, CancellationToken.None);
            Assert.That(page2.Items, Has.Count.EqualTo(1));
            Assert.That(page2.HasMore, Is.False);
            Assert.That(page1.Items.Concat(page2.Items).Select(n => n.Id), Is.EquivalentTo(ids));
        }

        [Test]
        public async Task Diagnostics_history_second_page_is_reachable() {
            var svc = new SqliteDiagnosticsService(_db, logger: null);
            var ids = new Guid[3];
            for (var i = 0; i < 3; i++) {
                ids[i] = Guid.NewGuid();
                await svc.CreateEventAsync(new DiagnosticEventDto(
                    Id: ids[i], EventType: $"test.event{i}", Severity: DiagnosticHealth.Green,
                    Description: $"d{i}", DetectedUtc: DateTimeOffset.UtcNow.AddMinutes(i),
                    ClearedUtc: null, AutoActionTaken: false, AutoActionDescription: null),
                    recommendedAction: null, autoCorrectible: null, CancellationToken.None);
            }

            var page1 = await svc.GetHistoryAsync(2, cursor: null, CancellationToken.None);
            Assert.That(page1.Items, Has.Count.EqualTo(2));
            Assert.That(page1.HasMore, Is.True, "3 rows in pages of 2 must report a second page");
            var page2 = await svc.GetHistoryAsync(2, page1.NextCursor, CancellationToken.None);
            Assert.That(page2.Items, Has.Count.EqualTo(1));
            Assert.That(page2.HasMore, Is.False);
            Assert.That(page1.Items.Concat(page2.Items).Select(e => e.Id), Is.EquivalentTo(ids));
        }

        [Test]
        public async Task Frames_second_page_is_reachable() {
            var repo = new SqliteFrameRepository(_db, new InMemoryProfileStore());
            var session = Guid.NewGuid();
            await InsertSessionAsync(session);
            var ids = new Guid[3];
            for (var i = 0; i < 3; i++) {
                ids[i] = Guid.NewGuid();
                await repo.InsertAsync(Frame(ids[i], session, minute: i), CancellationToken.None);
            }

            var page1 = await repo.ListAsync(2, cursor: null, sessionId: session, targetName: null, CancellationToken.None);
            Assert.That(page1.Items, Has.Count.EqualTo(2));
            Assert.That(page1.HasMore, Is.True, "3 rows in pages of 2 must report a second page");
            var page2 = await repo.ListAsync(2, page1.NextCursor, sessionId: session, targetName: null, CancellationToken.None);
            Assert.That(page2.Items, Has.Count.EqualTo(1));
            Assert.That(page2.HasMore, Is.False);
            Assert.That(page1.Items.Concat(page2.Items).Select(f => f.Id), Is.EquivalentTo(ids));
        }

        private static FrameDto Frame(Guid id, Guid session, int minute) => new(
            Id: id,
            SessionId: session,
            TargetName: "M31",
            FrameType: FrameType.Light,
            FilterName: "Ha",
            ExposureSeconds: 300,
            Gain: 100,
            Offset: 10,
            TemperatureC: -10.0,
            CapturedUtc: DateTimeOffset.UtcNow.AddMinutes(minute),
            FilePath: $"/tmp/{id:N}.fits",
            FileSizeBytes: 1000,
            Width: 100,
            Height: 100,
            BitDepth: 16,
            Hfr: null,
            StarCount: null,
            Eccentricity: null,
            GuidingRmsArcsec: null,
            SnrEstimate: null,
            QualityScore: null,
            Rating: 0,
            Tags: Array.Empty<string>(),
            FocuserPosition: null);

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
    }
}
