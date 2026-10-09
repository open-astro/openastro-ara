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
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Test {

    /// <summary>#1311 — the night's polar-alignment residuals, and the Polar Align result they are
    /// compared with.</summary>
    [TestFixture]
    [Category("IO")] // #1265 — real disk, loopback HTTP or a simulator: not part of the quick unit run
    public class SqlitePaResidualLogTest {

        private string profileDir = null!;
        private SqliteAraDatabase db = null!;

        [SetUp]
        public async Task SetUp() {
            profileDir = Path.Combine(Path.GetTempPath(), $"oara-pa-residual-{Guid.NewGuid():N}");
            Directory.CreateDirectory(profileDir);
            db = new SqliteAraDatabase(profileDir, logger: null);
            await db.InitializeAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown() {
            try {
                Directory.Delete(profileDir, recursive: true);
            } catch (IOException) {
            } catch (UnauthorizedAccessException) {
            }
        }

        private async Task<Guid> InsertSessionAsync() {
            var id = Guid.NewGuid();
            await using var conn = db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $now);";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
            return id;
        }

        private static PaResidualDto Done(DateTimeOffset completed, Guid? session, double error = 0.8) => new(
            Id: Guid.NewGuid(), Status: "done", StartedUtc: completed.AddMinutes(-6), CompletedUtc: completed,
            SampleSeconds: 300, TargetSeconds: 300, Frames: 118, DriftArcsecPerMin: -error / PaResidualEstimator.ArcminPerArcsecPerMin,
            PaErrorMinArcmin: error, UncertaintyArcmin: 0.2, Reliable: true, HourAngleHours: -1.25, DecDeg: 44.2,
            AlignErrorArcmin: 0.68, AlignEndedUtc: completed.AddMinutes(-40), SessionId: session);

        [Test]
        public async Task A_logged_residual_reads_back_newest_first_and_by_session() {
            var log = new SqlitePaResidualLog(db);
            var session = await InsertSessionAsync();
            var t0 = new DateTimeOffset(2026, 10, 8, 21, 0, 0, TimeSpan.Zero);
            var first = Done(t0, session, error: 0.8);
            var second = Done(t0.AddHours(3), session: null, error: 1.4) with {
                HourAngleHours = null, DecDeg = null, AlignErrorArcmin = null, AlignEndedUtc = null, Reliable = false,
            };
            await log.InsertAsync(first, CancellationToken.None);
            await log.InsertAsync(second, CancellationToken.None);

            var all = await log.ListAsync(sessionId: null, limit: 20, CancellationToken.None);
            Assert.That(all, Has.Count.EqualTo(2));
            Assert.That(all[0].Id, Is.EqualTo(second.Id), "newest first");
            Assert.That(all[0].Reliable, Is.False);
            Assert.That(all[0].AlignErrorArcmin, Is.Null);
            Assert.That(all[1], Is.EqualTo(first with { TargetSeconds = first.SampleSeconds }), "every field round-trips");

            var bySession = await log.ListAsync(session, limit: 20, CancellationToken.None);
            Assert.That(bySession, Has.Count.EqualTo(1));
            Assert.That(bySession[0].Id, Is.EqualTo(first.Id));
        }

        [Test]
        public async Task Only_a_finished_measurement_is_logged() {
            var log = new SqlitePaResidualLog(db);
            var measuring = Done(DateTimeOffset.UtcNow, null) with { Status = "measuring" };
            await Assert.ThatAsync(() => log.InsertAsync(measuring, CancellationToken.None), Throws.ArgumentException);
        }

        [Test]
        public async Task The_latest_measured_polar_alignment_is_tonights_last_result() {
            var log = new SqlitePolarAlignmentLog(db);
            var t0 = new DateTimeOffset(2026, 10, 8, 20, 0, 0, TimeSpan.Zero);
            await log.InsertAsync(new PolarAlignmentRecord(t0.AddDays(-1), t0.AddDays(-1).AddMinutes(5), 0.4, 0.3, 0.2, 5, "complete"), CancellationToken.None);
            await log.InsertAsync(new PolarAlignmentRecord(t0, t0.AddMinutes(4), 0.9, 0.5, 0.7, 6, "aborted"), CancellationToken.None);
            await log.InsertAsync(new PolarAlignmentRecord(t0.AddMinutes(10), t0.AddMinutes(11), null, null, null, 0, "failed"), CancellationToken.None);

            var latest = await log.GetLatestMeasuredAsync(t0.AddHours(-12), CancellationToken.None);
            Assert.That(latest?.FinalErrorArcmin, Is.EqualTo(0.9), "the stopped-while-adjusting run, not the failed seed after it");
            Assert.That(latest!.EndedAt, Is.EqualTo(t0.AddMinutes(4)));
            Assert.That(await log.GetLatestMeasuredAsync(t0.AddHours(1), CancellationToken.None), Is.Null, "nothing tonight after that");
        }
    }
}
