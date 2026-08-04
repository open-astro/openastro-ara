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
using OpenAstroAra.Fits;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System.Security.Cryptography;
using System.Text.Json;

namespace OpenAstroAra.Test {

    /// <summary>
    /// Rank 1 / PR 1: durable capture lifecycle, transactional catalog completion,
    /// eager source integrity, schema migration, and startup crash recovery.
    /// </summary>
    [TestFixture]
    public class FrameStorageLifecycleTest {
        private string _profileDir = null!;
        private string _captureRoot = null!;
        private SqliteAraDatabase _db = null!;
        private InMemoryProfileStore _profile = null!;
        private SqliteFrameRepository _repo = null!;
        private Guid _sessionId;

        [SetUp]
        public async Task SetUp() {
            _profileDir = Path.Combine(Path.GetTempPath(), $"oara-storage-{Guid.NewGuid():N}");
            _captureRoot = Path.Combine(_profileDir, "captures");
            Directory.CreateDirectory(_captureRoot);
            _profile = new InMemoryProfileStore();
            _profile.PutStorageSettings(_profile.GetStorageSettings() with { SaveDirectory = _captureRoot });
            _db = new SqliteAraDatabase(_profileDir, logger: null);
            await _db.InitializeAsync(CancellationToken.None);
            _sessionId = Guid.NewGuid();
            await InsertSessionAsync(_sessionId);
            _repo = new SqliteFrameRepository(_db, _profile);
        }

        [TearDown]
        public void TearDown() {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_profileDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        [Test]
        public async Task Fresh_schema_is_v7_and_has_lifecycle_indexes() {
            await using var conn = _db.OpenConnection();
            await using var version = conn.CreateCommand();
            version.CommandText = "SELECT version FROM schema_version;";
            Assert.That(Convert.ToInt64(await version.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(7));

            await using var columns = conn.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('frame_storage_lifecycle');";
            Assert.That(Convert.ToInt64(await columns.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(14));

            await using var indexes = conn.CreateCommand();
            indexes.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'index' AND tbl_name = 'frame_storage_lifecycle'
                  AND name IN ('idx_frame_storage_state_updated', 'idx_frame_storage_final_path');
                """;
            Assert.That(Convert.ToInt64(await indexes.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(2));
        }

        [Test]
        public async Task Lifecycle_advances_forward_idempotently_and_rejects_backward_motion() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            await BeginAsync(id, path);
            Assert.That((await _repo.GetStorageAsync(id, CancellationToken.None))!.State,
                Is.EqualTo(FrameStorageState.Accepted));

            await _repo.AdvanceStorageAsync(id, FrameStorageState.Exposing, CancellationToken.None);
            await _repo.AdvanceStorageAsync(id, FrameStorageState.Exposing, CancellationToken.None);
            await _repo.AdvanceStorageAsync(id, FrameStorageState.Downloading, CancellationToken.None);
            await _repo.AdvanceStorageAsync(id, FrameStorageState.Persisting, CancellationToken.None);
            Assert.That((await _repo.GetStorageAsync(id, CancellationToken.None))!.State,
                Is.EqualTo(FrameStorageState.Persisting));

            Assert.ThrowsAsync<InvalidOperationException>(() =>
                _repo.AdvanceStorageAsync(id, FrameStorageState.Exposing, CancellationToken.None));
            Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                _repo.AdvanceStorageAsync(id, FrameStorageState.Complete, CancellationToken.None));
        }

        [Test]
        public async Task Duplicate_begin_is_rejected_without_overwriting_original_identity() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            var accepted = DateTimeOffset.UtcNow.AddSeconds(-5);
            await BeginAsync(id, path, accepted);
            Assert.ThrowsAsync<SqliteException>(() => _repo.BeginStorageAsync(
                new FrameStorageAttempt(id, _sessionId, DateTimeOffset.UtcNow,
                    path + ".other.tmp", path + ".other", "fits"), CancellationToken.None));
            var stored = await _repo.GetStorageAsync(id, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(stored!.AcceptedUtc, Is.EqualTo(accepted).Within(TimeSpan.FromMilliseconds(1)));
                Assert.That(stored.FinalPath, Is.EqualTo(path));
                Assert.That(stored.TemporaryPath, Is.EqualTo(path + ".tmp"));
            });
        }

        [Test]
        public async Task Pre_cancelled_begin_writes_nothing() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Assert.ThatAsync(async () => await _repo.BeginStorageAsync(
                    new FrameStorageAttempt(id, _sessionId, DateTimeOffset.UtcNow,
                        path + ".tmp", path, "fits"), cts.Token),
                Throws.InstanceOf<OperationCanceledException>());
            Assert.That(await _repo.GetStorageAsync(id, CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task Complete_is_transactional_hashes_catalog_and_publishes_once() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            WriteFits(path, width: 12, height: 8, cfaPattern: "RGGB");
            var artifact = await CameraService.ValidateStoredFitsAsync(path, 12, 8, CancellationToken.None);
            await BeginAsync(id, path);
            await _repo.AdvanceStorageAsync(id, FrameStorageState.Persisting, CancellationToken.None);

            var ws = new Mock<IWsBroadcaster>();
            ws.Setup(x => x.PublishAsync(It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            var repo = new SqliteFrameRepository(_db, _profile, ws.Object);
            var frame = Frame(id, path, artifact.ByteCount, width: 12, height: 8);
            var completion = new FrameStorageCompletion(
                artifact.ByteCount, artifact.ChecksumSha256, DateTimeOffset.UtcNow, "fits", artifact.CfaPattern);

            await repo.CompleteStorageAsync(frame, completion, CancellationToken.None);
            await repo.CompleteStorageAsync(frame, completion, CancellationToken.None); // retry is idempotent

            var stored = await repo.GetStorageAsync(id, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(stored!.State, Is.EqualTo(FrameStorageState.Complete));
                Assert.That(stored.ChecksumSha256, Is.EqualTo(artifact.ChecksumSha256));
                Assert.That(stored.ByteCount, Is.EqualTo(artifact.ByteCount));
                Assert.That(stored.CfaPattern, Is.EqualTo("RGGB"));
                Assert.That(stored.TemporaryPath, Is.Null);
            });
            Assert.That(await repo.GetAsync(id, CancellationToken.None), Is.Not.Null);
            Assert.That(await ReadFrameChecksumAsync(id), Is.EqualTo(artifact.ChecksumSha256));
            ws.Verify(x => x.PublishAsync(WsEventCatalog.FrameComplete,
                It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task Completion_failure_rolls_back_frame_and_lifecycle_together() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            WriteFits(path, 4, 3);
            var artifact = await CameraService.ValidateStoredFitsAsync(path, 4, 3, CancellationToken.None);
            await BeginAsync(id, path);

            // Force the SECOND statement in CompleteStorageAsync to fail. The frame
            // INSERT happens first; transaction rollback must remove it again.
            await using (var conn = _db.OpenConnection()) {
                await using var trigger = conn.CreateCommand();
                trigger.CommandText = """
                    CREATE TRIGGER fail_storage_completion
                    BEFORE UPDATE OF state ON frame_storage_lifecycle
                    WHEN NEW.state = 'complete'
                    BEGIN
                        SELECT RAISE(ABORT, 'forced lifecycle completion failure');
                    END;
                    """;
                await trigger.ExecuteNonQueryAsync();
            }
            Assert.ThrowsAsync<SqliteException>(() => _repo.CompleteStorageAsync(
                Frame(id, path, artifact.ByteCount, 4, 3),
                new FrameStorageCompletion(artifact.ByteCount, artifact.ChecksumSha256,
                    DateTimeOffset.UtcNow, "fits", null),
                CancellationToken.None));

            Assert.That(await _repo.GetAsync(id, CancellationToken.None), Is.Null);
            Assert.That((await _repo.GetStorageAsync(id, CancellationToken.None))!.State,
                Is.EqualTo(FrameStorageState.Accepted));
        }

        [Test]
        public async Task Completion_without_lifecycle_never_inserts_a_false_complete_frame() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            WriteFits(path, 4, 3);
            var artifact = await CameraService.ValidateStoredFitsAsync(path, 4, 3, CancellationToken.None);
            Assert.ThrowsAsync<KeyNotFoundException>(() => _repo.CompleteStorageAsync(
                Frame(id, path, artifact.ByteCount, 4, 3),
                new FrameStorageCompletion(artifact.ByteCount, artifact.ChecksumSha256,
                    DateTimeOffset.UtcNow, "fits", null), CancellationToken.None));
            Assert.That(await _repo.GetAsync(id, CancellationToken.None), Is.Null);
        }

        [Test]
        public async Task Completion_rejects_session_or_path_identity_mismatch() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            await BeginAsync(id, path);
            var completion = new FrameStorageCompletion(
                10, new string('a', 64), DateTimeOffset.UtcNow, "fits", null);

            Assert.ThrowsAsync<InvalidOperationException>(() => _repo.CompleteStorageAsync(
                Frame(id, path, 10, 1, 1) with { SessionId = Guid.NewGuid() },
                completion, CancellationToken.None));
            Assert.ThrowsAsync<InvalidOperationException>(() => _repo.CompleteStorageAsync(
                Frame(id, Path.Combine(_captureRoot, "other.fits"), 10, 1, 1),
                completion, CancellationToken.None));
            Assert.That(await _repo.GetAsync(id, CancellationToken.None), Is.Null);
            Assert.That((await _repo.GetStorageAsync(id, CancellationToken.None))!.State,
                Is.EqualTo(FrameStorageState.Accepted));
        }

        [Test]
        public async Task Failure_is_durable_bounded_idempotent_and_terminal() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            await BeginAsync(id, path);
            var longMessage = new string('x', 700);
            await _repo.FailStorageAsync(id,
                new FrameStorageFailure("storage_io_failed", longMessage, DateTimeOffset.UtcNow),
                CancellationToken.None);
            await _repo.FailStorageAsync(id,
                new FrameStorageFailure("different_code", "ignored retry", DateTimeOffset.UtcNow),
                CancellationToken.None);

            var stored = await _repo.GetStorageAsync(id, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(stored!.State, Is.EqualTo(FrameStorageState.Failed));
                Assert.That(stored.FailureCode, Is.EqualTo("storage_io_failed"));
                Assert.That(stored.FailureMessage, Has.Length.EqualTo(512));
            });
            Assert.That(await _repo.GetAsync(id, CancellationToken.None), Is.Null);
            Assert.ThrowsAsync<InvalidOperationException>(() =>
                _repo.AdvanceStorageAsync(id, FrameStorageState.Persisting, CancellationToken.None));
        }

        [Test]
        public async Task Invalid_failure_code_and_checksum_do_not_mutate_state() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            await BeginAsync(id, path);
            Assert.ThrowsAsync<ArgumentException>(() => _repo.FailStorageAsync(id,
                new FrameStorageFailure("bad code!", "safe", DateTimeOffset.UtcNow), CancellationToken.None));
            Assert.ThrowsAsync<ArgumentException>(() => _repo.CompleteStorageAsync(
                Frame(id, path, 1, 1, 1),
                new FrameStorageCompletion(1, "not-a-sha", DateTimeOffset.UtcNow, "fits", null),
                CancellationToken.None));
            Assert.That((await _repo.GetStorageAsync(id, CancellationToken.None))!.State,
                Is.EqualTo(FrameStorageState.Accepted));
        }

        [Test]
        public async Task Ordinary_repository_insert_creates_complete_legacy_ledger_row() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            await _repo.InsertAsync(Frame(id, path, 123, 5, 4), CancellationToken.None);
            var stored = await _repo.GetStorageAsync(id, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(stored!.State, Is.EqualTo(FrameStorageState.Complete));
                Assert.That(stored.ByteCount, Is.EqualTo(123));
                Assert.That(stored.FinalPath, Is.EqualTo(path));
                Assert.That(stored.ChecksumSha256, Is.Null);
            });
        }

        [Test]
        public async Task Reinitialize_from_v4_backfills_existing_frames_and_is_idempotent() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            await _repo.InsertAsync(Frame(id, path, 123, 5, 4), CancellationToken.None);
            await using (var conn = _db.OpenConnection()) {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    DROP TABLE frame_storage_lifecycle;
                    UPDATE schema_version SET version = 4;
                    """;
                await cmd.ExecuteNonQueryAsync();
            }

            await _db.InitializeAsync(CancellationToken.None);
            await _db.InitializeAsync(CancellationToken.None);
            var stored = await _repo.GetStorageAsync(id, CancellationToken.None);
            Assert.That(stored!.State, Is.EqualTo(FrameStorageState.Complete));
            Assert.That(stored.FinalPath, Is.EqualTo(path));
            await using var check = _db.OpenConnection();
            await using var version = check.CreateCommand();
            version.CommandText = "SELECT version FROM schema_version;";
            Assert.That(Convert.ToInt64(await version.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(7));
        }

        [Test]
        public async Task Interrupted_v6_migration_repairs_index_and_backfill_on_restart() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            await _repo.InsertAsync(Frame(id, path, 123, 5, 4), CancellationToken.None);
            await using (var conn = _db.OpenConnection()) {
                await using var interrupt = conn.CreateCommand();
                interrupt.CommandText = """
                    DELETE FROM frame_storage_lifecycle WHERE frame_id = $id;
                    DROP INDEX idx_frame_storage_final_path;
                    UPDATE schema_version SET version = 4;
                    """;
                interrupt.Parameters.AddWithValue("$id", id.ToString("D"));
                await interrupt.ExecuteNonQueryAsync();
            }

            await _db.InitializeAsync(CancellationToken.None);

            var stored = await _repo.GetStorageAsync(id, CancellationToken.None);
            Assert.That(stored!.State, Is.EqualTo(FrameStorageState.Complete));
            await using var check = _db.OpenConnection();
            await using var index = check.CreateCommand();
            index.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'idx_frame_storage_final_path';";
            Assert.That(Convert.ToInt64(await index.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(1));
        }

        [Test]
        public async Task Fits_validation_returns_exact_hash_and_rejects_wrong_dimensions() {
            var path = Path.Combine(_captureRoot, "valid.fits");
            WriteFits(path, 7, 5, "BGGR");
            var artifact = await CameraService.ValidateStoredFitsAsync(path, 7, 5, CancellationToken.None);
            string expected;
            await using (var stream = File.OpenRead(path)) {
                expected = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            }
            Assert.Multiple(() => {
                Assert.That(artifact.ByteCount, Is.EqualTo(new FileInfo(path).Length));
                Assert.That(artifact.ChecksumSha256, Is.EqualTo(expected));
                Assert.That(artifact.CfaPattern, Is.EqualTo("BGGR"));
            });
            Assert.ThrowsAsync<InvalidDataException>(() =>
                CameraService.ValidateStoredFitsAsync(path, 8, 5, CancellationToken.None));
        }

        [Test]
        public void Fits_validation_rejects_corrupt_or_truncated_source() {
            var path = Path.Combine(_captureRoot, "corrupt.fits");
            File.WriteAllBytes(path, [0x01, 0x02, 0x03, 0x04]);
            Assert.ThrowsAsync<FitsException>(() =>
                CameraService.ValidateStoredFitsAsync(path, 1, 1, CancellationToken.None));
        }

        [Test]
        public void Fits_validation_rejects_block_aligned_truncated_pixel_payload() {
            var path = Path.Combine(_captureRoot, "truncated-payload.fits");
            WriteFits(path, 64, 64);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) {
                stream.SetLength(stream.Length - 2880);
            }
            Assert.ThrowsAsync<InvalidDataException>(() =>
                CameraService.ValidateStoredFitsAsync(path, 64, 64, CancellationToken.None));
        }

        [Test]
        public async Task Startup_recovery_preserves_interrupted_frame_and_session_identity() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            await BeginAsync(id, path, DateTimeOffset.UtcNow.AddMinutes(-10));
            WriteFits(path, 9, 6, "GRBG");

            await new CaptureScanService(_profile, _db, logger: null).RunAsync(CancellationToken.None);

            var frame = await _repo.GetAsync(id, CancellationToken.None);
            var stored = await _repo.GetStorageAsync(id, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(frame, Is.Not.Null);
                Assert.That(frame!.SessionId, Is.EqualTo(_sessionId));
                Assert.That(frame.FilePath, Is.EqualTo(path));
                Assert.That(stored!.State, Is.EqualTo(FrameStorageState.Complete));
                Assert.That(stored.FailureCode, Is.Null);
                Assert.That(stored.ChecksumSha256, Has.Length.EqualTo(64));
                Assert.That(stored.CfaPattern, Is.EqualTo("GRBG"));
            });
            Assert.That(await ReadFrameChecksumAsync(id), Is.EqualTo(stored!.ChecksumSha256));
        }

        [Test]
        public async Task Startup_preserves_tracked_stale_temp_for_review_and_marks_partial() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            var temp = path + ".tmp";
            await BeginAsync(id, path, DateTimeOffset.UtcNow.AddMinutes(-10));
            await File.WriteAllTextAsync(temp, "interrupted bytes");
            File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddMinutes(-10));

            await new CaptureScanService(_profile, _db, logger: null).RunAsync(CancellationToken.None);

            var stored = await _repo.GetStorageAsync(id, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(stored!.State, Is.EqualTo(FrameStorageState.Partial));
                Assert.That(stored.FailureCode, Is.EqualTo("write_interrupted"));
                Assert.That(stored.TemporaryPath, Is.EqualTo(temp));
                Assert.That(File.Exists(temp), Is.True, "reviewable temp bytes must not be silently deleted");
                Assert.That(File.Exists(path), Is.False);
            });
        }

        [Test]
        public async Task Startup_promotes_failed_write_with_surviving_temp_to_partial() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            var temp = path + ".tmp";
            await BeginAsync(id, path, DateTimeOffset.UtcNow.AddMinutes(-10));
            await _repo.FailStorageAsync(id,
                new FrameStorageFailure("storage_io_failed", "Write failed.", DateTimeOffset.UtcNow.AddMinutes(-10)),
                CancellationToken.None);
            await File.WriteAllTextAsync(temp, "surviving bytes");
            File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddMinutes(-10));

            await new CaptureScanService(_profile, _db, logger: null).RunAsync(CancellationToken.None);

            var stored = await _repo.GetStorageAsync(id, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(stored!.State, Is.EqualTo(FrameStorageState.Partial));
                Assert.That(stored.FailureCode, Is.EqualTo("write_interrupted"));
                Assert.That(File.Exists(temp), Is.True);
            });
        }

        [Test]
        public async Task Startup_preserves_failed_reason_when_no_bytes_survive() {
            var id = Guid.NewGuid();
            var path = Path.Combine(_captureRoot, $"{id:D}.fits");
            await BeginAsync(id, path, DateTimeOffset.UtcNow.AddMinutes(-10));
            await _repo.FailStorageAsync(id,
                new FrameStorageFailure("camera_disconnected", "Camera disconnected.", DateTimeOffset.UtcNow.AddMinutes(-10)),
                CancellationToken.None);

            await new CaptureScanService(_profile, _db, logger: null).RunAsync(CancellationToken.None);

            var stored = await _repo.GetStorageAsync(id, CancellationToken.None);
            Assert.Multiple(() => {
                Assert.That(stored!.State, Is.EqualTo(FrameStorageState.Failed));
                Assert.That(stored.FailureCode, Is.EqualTo("camera_disconnected"));
                Assert.That(stored.FailureMessage, Is.EqualTo("Camera disconnected."));
            });
        }

        [Test]
        public async Task Startup_still_sweeps_untracked_stale_temp() {
            var temp = Path.Combine(_captureRoot, "unknown.fits.tmp");
            await File.WriteAllTextAsync(temp, "untracked");
            File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddMinutes(-10));
            await new CaptureScanService(_profile, _db, logger: null).RunAsync(CancellationToken.None);
            Assert.That(File.Exists(temp), Is.False);
        }

        [Test]
        public async Task Startup_preserves_unrelated_stale_temp_files() {
            var temp = Path.Combine(_captureRoot, "observer-notes.tmp");
            await File.WriteAllTextAsync(temp, "keep");
            File.SetLastWriteTimeUtc(temp, DateTime.UtcNow.AddMinutes(-10));
            await new CaptureScanService(_profile, _db, logger: null).RunAsync(CancellationToken.None);
            Assert.That(File.Exists(temp), Is.True);
        }

        private async Task BeginAsync(Guid frameId, string finalPath, DateTimeOffset? acceptedUtc = null) {
            await _repo.BeginStorageAsync(new FrameStorageAttempt(
                frameId, _sessionId, acceptedUtc ?? DateTimeOffset.UtcNow,
                finalPath + ".tmp", finalPath, "fits"), CancellationToken.None);
        }

        private FrameDto Frame(Guid id, string path, long size, int width, int height) => new(
            Id: id,
            SessionId: _sessionId,
            TargetName: "M31",
            FrameType: FrameType.Light,
            FilterName: "L",
            ExposureSeconds: 60,
            Gain: 100,
            Offset: 10,
            TemperatureC: -10,
            CapturedUtc: DateTimeOffset.UtcNow,
            FilePath: path,
            FileSizeBytes: size,
            Width: width,
            Height: height,
            BitDepth: 16,
            Hfr: null,
            StarCount: null,
            Eccentricity: null,
            GuidingRmsArcsec: null,
            SnrEstimate: null,
            QualityScore: null,
            Rating: 0,
            Tags: []);

        private static void WriteFits(string path, int width, int height, string? cfaPattern = null) {
            var pixels = Enumerable.Range(0, width * height).Select(i => (ushort)(i * 17)).ToArray();
            using var fits = FitsImage.Create(path, width, height, FitsBitDepth.UnsignedShort);
            fits.WriteImageData(pixels);
            fits.SetHeader("IMAGETYP", "LIGHT");
            fits.SetHeader("EXPTIME", 60.0);
            fits.SetHeader("DATE-OBS", "2026-08-01T03:00:00.000");
            fits.SetHeader("OBJECT", "M31");
            if (cfaPattern is not null) fits.SetHeader("BAYERPAT", cfaPattern);
            fits.Complete();
        }

        private async Task InsertSessionAsync(Guid id) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $started);";
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            cmd.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task<string?> ReadFrameChecksumAsync(Guid id) {
            await using var conn = _db.OpenConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT sha256 FROM frames WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id.ToString("D"));
            return await cmd.ExecuteScalarAsync() as string;
        }
    }
}