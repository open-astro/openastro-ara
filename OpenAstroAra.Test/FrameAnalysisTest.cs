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
using Moq;
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
    /// §59.5 live per-frame HFR analysis — the post-capture write-back
    /// (<see cref="IFrameRepository.UpdateAnalysisAsync"/>) and the CameraService analysis
    /// worker that feeds it plus the session <see cref="ImageHistoryService"/> the HFR-drift
    /// trigger reads.
    /// </summary>
    [TestFixture]
    public class FrameAnalysisTest {

        private static readonly Guid Session = Guid.Parse("59595959-5959-5959-5959-595959595959");

        // ── SqliteFrameRepository.UpdateAnalysisAsync ────────────────────────────────

        private string _dir = null!;
        private SqliteAraDatabase _db = null!;
        private SqliteFrameRepository _repo = null!;

        [SetUp]
        public async Task SetUp() {
            _dir = Path.Combine(Path.GetTempPath(), $"oara-hfr-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_dir);
            _db = new SqliteAraDatabase(_dir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _repo = new SqliteFrameRepository(_db, new InMemoryProfileStore());
            await InsertSessionAsync(Session);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task UpdateAnalysis_stamps_hfr_and_star_count_onto_the_row() {
            var id = Guid.NewGuid();
            await _repo.InsertAsync(Frame(id), CancellationToken.None);

            await _repo.UpdateAnalysisAsync(id, 2.34, 128, CancellationToken.None);

            var got = await _repo.GetAsync(id, CancellationToken.None);
            Assert.That(got!.Hfr, Is.EqualTo(2.34).Within(1e-9));
            Assert.That(got.StarCount, Is.EqualTo(128));
        }

        [Test]
        public void UpdateAnalysis_on_a_deleted_frame_is_a_silent_noop() {
            Assert.DoesNotThrowAsync(() =>
                _repo.UpdateAnalysisAsync(Guid.NewGuid(), 2.0, 50, CancellationToken.None));
        }

        // ── CameraService.AnalyzeFrameAsync (metric override seam) ──────────────────

        private static CameraService Analyzer(
                out Mock<IFrameRepository> frames,
                out ImageHistoryService history,
                Func<ReadOnlyMemory<ushort>, int, int, (double Hfr, int Stars)> metric) {
            frames = new Mock<IFrameRepository>();
            history = new ImageHistoryService();
            return new CameraService(frames: frames.Object, imageHistory: history) {
                AnalysisMetricOverride = metric,
            };
        }

        [Test]
        public async Task Analysis_writes_back_and_feeds_the_session_history() {
            using var camera = Analyzer(out var frames, out var history, (_, _, _) => (2.5, 42));
            var id = Guid.NewGuid();

            await camera.AnalyzeFrameAsync(id, new ushort[4], 2, 2, "Ha");

            frames.Verify(f => f.UpdateAnalysisAsync(id, 2.5, 42, It.IsAny<CancellationToken>()), Times.Once);
            var point = history.ImagePoints.Single();
            Assert.That(point.Type, Is.EqualTo("LIGHT"));
            Assert.That(point.Hfr, Is.EqualTo(2.5));
            Assert.That(point.Filter, Is.EqualTo("Ha"),
                "the HFR-drift trigger scopes by filter — the point must carry it");
        }

        [Test]
        public async Task Analysis_skips_a_star_starved_frame() {
            using var camera = Analyzer(out var frames, out var history,
                (_, _, _) => (1.8, CameraService.MinStarsForAnalysis - 1));

            await camera.AnalyzeFrameAsync(Guid.NewGuid(), new ushort[4], 2, 2, "Ha");

            frames.Verify(f => f.UpdateAnalysisAsync(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(history.ImagePoints, Is.Empty,
                "a 2-star HFR is noise that would swing the drift trigger's trend line");
        }

        [Test]
        public async Task Analysis_skips_a_nonsense_hfr() {
            using var camera = Analyzer(out var frames, out var history, (_, _, _) => (double.NaN, 100));

            await camera.AnalyzeFrameAsync(Guid.NewGuid(), new ushort[4], 2, 2, null);

            frames.Verify(f => f.UpdateAnalysisAsync(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(history.ImagePoints, Is.Empty);
        }

        [Test]
        public async Task Analysis_faults_degrade_to_a_logged_skip() {
            using var camera = Analyzer(out _, out var history,
                (_, _, _) => throw new InvalidOperationException("detector exploded"));

            await camera.AnalyzeFrameAsync(Guid.NewGuid(), new ushort[4], 2, 2, "Ha");

            Assert.That(history.ImagePoints, Is.Empty, "the frame is already safe; analysis is enrichment");
        }

        [Test]
        public async Task Analysis_write_back_fault_still_never_throws() {
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.UpdateAnalysisAsync(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("db locked"));
            using var camera = new CameraService(frames: frames.Object, imageHistory: new ImageHistoryService()) {
                AnalysisMetricOverride = (_, _, _) => (2.0, 50),
            };

            await camera.AnalyzeFrameAsync(Guid.NewGuid(), new ushort[4], 2, 2, "Ha");
            Assert.Pass("fire-and-forget boundary held");
        }

        private static FrameDto Frame(Guid id) => new(
            Id: id,
            SessionId: Session,
            TargetName: "M31",
            FrameType: FrameType.Light,
            FilterName: "Ha",
            ExposureSeconds: 300,
            Gain: 100,
            Offset: 10,
            TemperatureC: -10.0,
            CapturedUtc: DateTimeOffset.UtcNow,
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
