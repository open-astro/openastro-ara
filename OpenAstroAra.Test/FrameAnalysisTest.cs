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
using OpenAstroAra.Server.Contracts.WsEvents;
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
        public async Task UpdateAnalysis_stamps_versioned_metrics_onto_the_row() {
            var id = Guid.NewGuid();
            await _repo.InsertAsync(Frame(id), CancellationToken.None);

            await _repo.UpdateAnalysisAsync(id,
                new FrameAnalysisMeasurement(2.34, 128, 0.42, 19.5, "detector-v7"),
                CancellationToken.None);

            var got = await _repo.GetAsync(id, CancellationToken.None);
            await using var conn = _db.OpenConnection();
            await using var version = conn.CreateCommand();
            version.CommandText = "SELECT analysis_version FROM frames WHERE id = $id";
            version.Parameters.AddWithValue("$id", id.ToString("D"));
            Assert.Multiple(() => {
                Assert.That(got!.Hfr, Is.EqualTo(2.34).Within(1e-9));
                Assert.That(got.StarCount, Is.EqualTo(128));
                Assert.That(got.Eccentricity, Is.EqualTo(0.42).Within(1e-9));
                Assert.That(got.SnrEstimate, Is.EqualTo(19.5).Within(1e-9));
                Assert.That(got.AnalysisVersion, Is.EqualTo("detector-v7"));
            });
            Assert.That(await version.ExecuteScalarAsync(), Is.EqualTo("detector-v7"));
        }

        [Test]
        public void UpdateAnalysis_on_a_deleted_frame_is_a_silent_noop() {
            Assert.DoesNotThrowAsync(() =>
                _repo.UpdateAnalysisAsync(Guid.NewGuid(),
                    new FrameAnalysisMeasurement(2.0, 50, null, null, "detector-v1"),
                    CancellationToken.None));
        }

        [Test]
        public async Task UpdateAnalysis_broadcasts_additive_versioned_metrics() {
            JsonElement payload = default;
            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(b => b.PublishAsync(WsEventCatalog.FrameAnalyzed,
                    It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Callback<string, JsonElement, CancellationToken>((_, value, _) => payload = value.Clone())
                .Returns(Task.CompletedTask);
            var repo = new SqliteFrameRepository(_db, new InMemoryProfileStore(), ws.Object);
            var id = Guid.NewGuid();
            await repo.InsertAsync(Frame(id), CancellationToken.None);

            await repo.UpdateAnalysisAsync(id,
                new FrameAnalysisMeasurement(2.4, 80, 0.51, 22.3, "managed-v2"),
                CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(payload.GetProperty("frame_id").GetString(), Is.EqualTo(id.ToString("D")));
                Assert.That(payload.GetProperty("hfr").GetDouble(), Is.EqualTo(2.4));
                Assert.That(payload.GetProperty("star_count").GetInt32(), Is.EqualTo(80));
                Assert.That(payload.GetProperty("eccentricity").GetDouble(), Is.EqualTo(0.51));
                Assert.That(payload.GetProperty("snr_estimate").GetDouble(), Is.EqualTo(22.3));
                Assert.That(payload.GetProperty("analysis_version").GetString(), Is.EqualTo("managed-v2"));
            });
        }

        [TestCase(double.NaN, 10, 0.5, 5.0, "v1")]
        [TestCase(2.0, -1, 0.5, 5.0, "v1")]
        [TestCase(2.0, 10, 1.5, 5.0, "v1")]
        [TestCase(2.0, 10, 0.5, -1.0, "v1")]
        [TestCase(2.0, 10, 0.5, 5.0, "")]
        public void UpdateAnalysis_rejects_invalid_measurements(double hfr, int stars,
                double eccentricity, double snr, string version) {
            Assert.CatchAsync<ArgumentException>(() => _repo.UpdateAnalysisAsync(Guid.NewGuid(),
                new FrameAnalysisMeasurement(hfr, stars, eccentricity, snr, version),
                CancellationToken.None));
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

            frames.Verify(f => f.UpdateAnalysisAsync(id,
                It.Is<FrameAnalysisMeasurement>(m => m.Hfr == 2.5 && m.StarCount == 42
                    && m.AnalysisVersion == "metric-override-v1"),
                It.IsAny<CancellationToken>()), Times.Once);
            var point = history.ImagePoints.Single();
            Assert.That(point.Type, Is.EqualTo("LIGHT"));
            Assert.That(point.Hfr, Is.EqualTo(2.5));
            Assert.That(point.Filter, Is.EqualTo("Ha"),
                "the HFR-drift trigger scopes by filter — the point must carry it");
        }

        [Test]
        public async Task Osc_analysis_uses_debayered_luminance_plane_without_mutating_source() {
            var dimensions = (Width: 0, Height: 0, Length: 0);
            var source = Enumerable.Range(0, 24).Select(static value => (ushort)(1000 + value)).ToArray();
            var original = source.ToArray();
            using var camera = Analyzer(out var frames, out _, (_, width, height) => {
                dimensions = (width, height, width * height);
                return (2.0, 30);
            });

            await camera.AnalyzeFrameAsync(Guid.NewGuid(), source, 6, 4, "L", "RGGB");

            Assert.Multiple(() => {
                Assert.That(dimensions, Is.EqualTo((3, 2, 6)));
                Assert.That(source, Is.EqualTo(original));
            });
            frames.Verify(f => f.UpdateAnalysisAsync(It.IsAny<Guid>(),
                It.IsAny<FrameAnalysisMeasurement>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Analysis_skips_a_star_starved_frame() {
            using var camera = Analyzer(out var frames, out var history,
                (_, _, _) => (1.8, CameraService.MinStarsForAnalysis - 1));

            await camera.AnalyzeFrameAsync(Guid.NewGuid(), new ushort[4], 2, 2, "Ha");

            frames.Verify(f => f.UpdateAnalysisAsync(It.IsAny<Guid>(),
                It.IsAny<FrameAnalysisMeasurement>(), It.IsAny<CancellationToken>()), Times.Never);
            Assert.That(history.ImagePoints, Is.Empty,
                "a 2-star HFR is noise that would swing the drift trigger's trend line");
        }

        [Test]
        public async Task Analysis_skips_a_nonsense_hfr() {
            using var camera = Analyzer(out var frames, out var history, (_, _, _) => (double.NaN, 100));

            await camera.AnalyzeFrameAsync(Guid.NewGuid(), new ushort[4], 2, 2, null);

            frames.Verify(f => f.UpdateAnalysisAsync(It.IsAny<Guid>(),
                It.IsAny<FrameAnalysisMeasurement>(), It.IsAny<CancellationToken>()), Times.Never);
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
        public async Task AnalysisQueue_preserves_capture_order_even_when_the_first_frame_is_slow() {
            var history = new ImageHistoryService();
            using var camera = new CameraService(frames: new Mock<IFrameRepository>().Object, imageHistory: history);
            var calls = 0;
            camera.AnalysisMetricOverride = (_, _, _) => {
                // The FIRST capture analyzes slowest — with per-frame Task.Run the second
                // point would jump the queue and shuffle the HFR-drift trigger's trend.
                if (Interlocked.Increment(ref calls) == 1) {
                    Thread.Sleep(150);
                    return (1.0, 50);
                }
                return (2.0, 50);
            };

            camera.QueueFrameAnalysis(Guid.NewGuid(), new ushort[4], 2, 2, "Ha");
            camera.QueueFrameAnalysis(Guid.NewGuid(), new ushort[4], 2, 2, "Ha");
            for (var i = 0; i < 400 && history.ImagePoints.Count < 2; i++) {
                await Task.Delay(10);
            }

            var points = history.ImagePoints;
            Assert.That(points, Has.Count.EqualTo(2));
            Assert.That(points[0].Hfr, Is.EqualTo(1.0),
                "history order must be capture order, not analysis-completion order");
            Assert.That(points[1].Hfr, Is.EqualTo(2.0));
        }

        [Test]
        public async Task AnalysisQueue_backlog_drops_the_newest_frame_honestly() {
            var history = new ImageHistoryService();
            using var camera = new CameraService(frames: new Mock<IFrameRepository>().Object, imageHistory: history);
            using var firstJobStarted = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var calls = 0;
            camera.AnalysisMetricOverride = (_, _, _) => {
                if (Interlocked.Increment(ref calls) == 1) {
                    firstJobStarted.Set();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
                return (2.0, 50);
            };

            Assert.That(camera.QueueFrameAnalysis(Guid.NewGuid(), new ushort[4], 2, 2, "Ha"), Is.True);
            Assert.That(firstJobStarted.Wait(TimeSpan.FromSeconds(10)), Is.True,
                "the worker must be mid-job so the buffer below fills deterministically");
            // The worker is stuck on job 1; the buffer holds AnalysisQueueCapacity more.
            for (var i = 0; i < CameraService.AnalysisQueueCapacity; i++) {
                Assert.That(camera.QueueFrameAnalysis(Guid.NewGuid(), new ushort[4], 2, 2, "Ha"), Is.True);
            }
            Assert.That(camera.QueueFrameAnalysis(Guid.NewGuid(), new ushort[4], 2, 2, "Ha"), Is.False,
                "the frame past the bound must report the skip — TryWrite on a Wait-mode "
                + "channel returns false; DropWrite would lie true and drop silently");
            release.Set();
            var expected = 1 + CameraService.AnalysisQueueCapacity;
            for (var i = 0; i < 400 && history.ImagePoints.Count < expected; i++) {
                await Task.Delay(10);
            }
            await Task.Delay(50); // settle: prove no extra point trickles in

            Assert.That(history.ImagePoints, Has.Count.EqualTo(expected),
                "the frame past the bound skips (its HFR stays unrecorded) instead of pinning buffers");
        }

        [Test]
        public async Task Analysis_write_back_fault_still_never_throws() {
            var frames = new Mock<IFrameRepository>();
            frames.Setup(f => f.UpdateAnalysisAsync(It.IsAny<Guid>(),
                    It.IsAny<FrameAnalysisMeasurement>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("db locked"));
            using var camera = new CameraService(frames: frames.Object, imageHistory: new ImageHistoryService()) {
                AnalysisMetricOverride = (_, _, _) => (2.0, 50),
            };

            await camera.AnalyzeFrameAsync(Guid.NewGuid(), new ushort[4], 2, 2, "Ha");
            Assert.Pass("fire-and-forget boundary held");
        }

        [Test]
        public void Managed_measurement_uses_median_shape_and_signal_metrics() {
            var result = new OpenAstroAra.Image.ImageAnalysis.StarDetectionResult {
                AverageHFR = 2.1,
                DetectedStars = 3,
                StarList = new[] {
                    new OpenAstroAra.Image.ImageAnalysis.DetectedStar { Roundness = 1.0, PeakToBackground = 4 },
                    new OpenAstroAra.Image.ImageAnalysis.DetectedStar { Roundness = 0.8, PeakToBackground = 10 },
                    new OpenAstroAra.Image.ImageAnalysis.DetectedStar { Roundness = 0.6, PeakToBackground = 100 },
                },
            };

            var measurement = CameraService.BuildAnalysisMeasurement(result);

            Assert.Multiple(() => {
                Assert.That(measurement.Hfr, Is.EqualTo(2.1));
                Assert.That(measurement.StarCount, Is.EqualTo(3));
                Assert.That(measurement.Eccentricity, Is.EqualTo(0.6).Within(1e-9));
                Assert.That(measurement.SnrEstimate, Is.EqualTo(10));
                Assert.That(measurement.AnalysisVersion, Is.EqualTo(CameraService.ManagedAnalysisVersion));
            });
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