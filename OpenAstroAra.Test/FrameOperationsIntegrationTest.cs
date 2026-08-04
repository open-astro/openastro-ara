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

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class FrameOperationsIntegrationTest {
    private static readonly string[] ExpectedStorageProgressStates = ["exposing", "downloading"];
    private string _root = null!;
    private SqliteAraDatabase _db = null!;
    private InMemoryProfileStore _profile = null!;
    private RecordingWs _ws = null!;
    private SqliteFrameRepository _repo = null!;
    private Guid _sessionId;

    [SetUp]
    public async Task SetUp() {
        _root = Path.Combine(Path.GetTempPath(), $"oara-frame-ops-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new SqliteAraDatabase(_root, logger: null);
        await _db.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        _profile = new InMemoryProfileStore();
        _ws = new RecordingWs();
        _repo = new SqliteFrameRepository(_db, _profile, _ws);
        _sessionId = Guid.NewGuid();
        await InsertSessionAsync(_sessionId).ConfigureAwait(false);
    }

    [TearDown]
    public void TearDown() {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Test]
    public async Task Preview_persists_ready_metadata_and_emits_complete_schema() {
        var (frameId, _) = await InsertFitsFrameAsync().ConfigureAwait(false);
        _ws.Events.Clear();

        var result = await _repo.GetPreviewAsync(frameId, PreviewRequest(),
            CancellationToken.None).ConfigureAwait(false);
        var metadata = await _repo.GetMetadataAsync(frameId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.Bytes, Is.Not.Empty);
            Assert.That(metadata!.PreviewState, Is.EqualTo("ready"));
            Assert.That(metadata.PreviewChecksum, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(metadata.PreviewFailureCode, Is.Null);
            Assert.That(metadata.DebayerMethod, Is.Not.Null.And.Not.Empty);
            Assert.That(metadata.PreviewVersion, Is.EqualTo("schema-3"));
        });
        AssertEvent(WsEventCatalog.FramePreviewStarted, frameId, _sessionId);
        var ready = AssertEvent(WsEventCatalog.FramePreviewReady, frameId, _sessionId);
        Assert.Multiple(() => {
            Assert.That(ready.GetProperty("preview_checksum").GetString(),
                Is.EqualTo(metadata!.PreviewChecksum));
            Assert.That(ready.GetProperty("width").GetInt32(), Is.GreaterThan(0));
            Assert.That(ready.GetProperty("height").GetInt32(), Is.GreaterThan(0));
        });
        AssertEvent(WsEventCatalog.FramePreviewReadyLegacy, frameId, _sessionId);

        _ws.Events.Clear();
        var cached = await _repo.GetPreviewAsync(frameId, PreviewRequest(),
            CancellationToken.None).ConfigureAwait(false);
        Assert.Multiple(() => {
            Assert.That(cached!.Value.CacheHit, Is.True);
            Assert.That(_ws.Events, Is.Empty,
                "a cache-hit refresh must not trigger another client invalidation");
        });
    }

    [Test]
    public async Task Storage_lifecycle_emits_started_progress_and_safe_failure() {
        var frameId = Guid.NewGuid();
        var path = Path.Combine(_root, $"{frameId:D}.fits");
        await _repo.BeginStorageAsync(new FrameStorageAttempt(frameId, _sessionId,
            DateTimeOffset.UtcNow, path + ".tmp", path, "fits"),
            CancellationToken.None).ConfigureAwait(false);
        await _repo.AdvanceStorageAsync(frameId, FrameStorageState.Exposing,
            CancellationToken.None).ConfigureAwait(false);
        await _repo.AdvanceStorageAsync(frameId, FrameStorageState.Downloading,
            CancellationToken.None).ConfigureAwait(false);
        await _repo.FailStorageAsync(frameId,
            new FrameStorageFailure("camera_timeout", "/private/path driver dump",
                DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);

        var started = AssertEvent(WsEventCatalog.FramePersistStarted, frameId, _sessionId);
        var progress = _ws.Events.Where(x => x.Type == WsEventCatalog.FramePersistProgress)
            .Select(x => x.Payload).ToList();
        var failed = AssertEvent(WsEventCatalog.FrameFailed, frameId, _sessionId);
        Assert.Multiple(() => {
            Assert.That(started.GetProperty("state").GetString(), Is.EqualTo("accepted"));
            Assert.That(progress.Select(x => x.GetProperty("state").GetString()),
                Is.EqualTo(ExpectedStorageProgressStates));
            Assert.That(progress[1].GetProperty("progress").GetDouble(),
                Is.GreaterThan(progress[0].GetProperty("progress").GetDouble()));
            Assert.That(failed.GetProperty("code").GetString(), Is.EqualTo("camera_timeout"));
            Assert.That(failed.GetProperty("message").GetString(),
                Is.EqualTo("Frame storage failed."));
            Assert.That(failed.GetProperty("message").GetString(),
                Does.Not.Contain("/private/path"));
        });
    }

    [Test]
    public async Task Missing_source_returns_compatible_placeholder_but_never_ready_state() {
        var frameId = Guid.NewGuid();
        await _repo.InsertAsync(Frame(frameId, Path.Combine(_root, "missing.fits"), 123),
            CancellationToken.None).ConfigureAwait(false);
        _ws.Events.Clear();

        var result = await _repo.GetPreviewAsync(frameId, PreviewRequest(),
            CancellationToken.None).ConfigureAwait(false);
        var metadata = await _repo.GetMetadataAsync(frameId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Value.Metadata.CacheKey, Is.EqualTo("missing-source"));
            Assert.That(metadata!.SourceExists, Is.False);
            Assert.That(metadata.PreviewState, Is.EqualTo("missing"));
            Assert.That(metadata.PreviewFailureCode, Is.EqualTo("source_unavailable"));
            Assert.That(metadata.PreviewChecksum, Is.Null);
            Assert.That(_ws.Events.Any(x => x.Type == WsEventCatalog.FramePreviewReady), Is.False);
        });
        var failed = AssertEvent(WsEventCatalog.FrameFailed, frameId, _sessionId);
        Assert.Multiple(() => {
            Assert.That(failed.GetProperty("stage").GetString(), Is.EqualTo("preview"));
            Assert.That(failed.GetProperty("code").GetString(), Is.EqualTo("source_unavailable"));
            Assert.That(failed.GetProperty("message").GetString(),
                Is.EqualTo("Source image is unavailable."));
        });
    }

    [Test]
    public async Task Invalid_preview_request_does_not_start_or_change_lifecycle_state() {
        var (frameId, _) = await InsertFitsFrameAsync().ConfigureAwait(false);
        _ws.Events.Clear();

        Assert.ThrowsAsync<ArgumentException>(() => _repo.GetPreviewAsync(frameId,
            PreviewRequest() with { StretchPalette = "invalid" }, CancellationToken.None));
        var metadata = await _repo.GetMetadataAsync(frameId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(metadata!.PreviewState, Is.Null);
            Assert.That(metadata.PreviewFailureCode, Is.Null);
            Assert.That(_ws.Events, Is.Empty);
        });
    }

    [Test]
    public async Task Blank_source_reanalysis_records_explicit_skipped_state() {
        var (frameId, _) = await InsertFitsFrameAsync(blank: true).ConfigureAwait(false);
        _ws.Events.Clear();

        var result = await _repo.ReanalyzeAsync(frameId, new(), CancellationToken.None)
            .ConfigureAwait(false);
        var metadata = await _repo.GetMetadataAsync(frameId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Persisted, Is.False);
            Assert.That(metadata!.AnalysisState, Is.EqualTo("skipped"));
            Assert.That(metadata.AnalysisFailureCode, Is.EqualTo("insufficient_stars"));
            Assert.That(metadata.Frame.Hfr, Is.Null);
            Assert.That(metadata.Frame.StarCount, Is.EqualTo(0));
        });
        AssertEvent(WsEventCatalog.FrameAnalysisStarted, frameId, _sessionId);
        var analyzed = AssertEvent(WsEventCatalog.FrameAnalyzed, frameId, _sessionId);
        Assert.Multiple(() => {
            Assert.That(analyzed.GetProperty("state").GetString(), Is.EqualTo("skipped"));
            Assert.That(analyzed.GetProperty("persisted").GetBoolean(), Is.False);
        });
    }

    [Test]
    public async Task Quarantine_is_reversible_idempotent_and_never_changes_source() {
        var (frameId, path) = await InsertFitsFrameAsync().ConfigureAwait(false);
        var before = await HashFileAsync(path).ConfigureAwait(false);
        _ws.Events.Clear();

        var request = new BulkQuarantineRequestDto([frameId], true, "cloud streak");
        var first = await _repo.BulkQuarantineAsync(request, "q-1", CancellationToken.None)
            .ConfigureAwait(false);
        var replay = await _repo.BulkQuarantineAsync(request, "q-1", CancellationToken.None)
            .ConfigureAwait(false);
        var quarantined = await _repo.GetAsync(frameId, CancellationToken.None).ConfigureAwait(false);
        await _repo.BulkQuarantineAsync(request, "q-duplicate", CancellationToken.None)
            .ConfigureAwait(false);
        var unchanged = await _repo.GetAsync(frameId, CancellationToken.None).ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(replay.OperationId, Is.EqualTo(first.OperationId));
            Assert.That(quarantined!.QuarantinedUtc, Is.Not.Null);
            Assert.That(quarantined.QuarantineReason, Is.EqualTo("cloud streak"));
            Assert.That(unchanged!.QuarantinedUtc, Is.EqualTo(quarantined.QuarantinedUtc));
            Assert.That(File.Exists(path), Is.True);
            Assert.That(_ws.Events.Count(x => x.Type == WsEventCatalog.FrameQuarantined),
                Is.EqualTo(1));
        });
        Assert.That(await HashFileAsync(path).ConfigureAwait(false), Is.EqualTo(before));
        Assert.ThrowsAsync<IdempotencyKeyConflictException>(() =>
            _repo.BulkQuarantineAsync(request with { Reason = "different" }, "q-1",
                CancellationToken.None));

        await _repo.BulkQuarantineAsync(new([frameId], Quarantined: false), "q-2",
            CancellationToken.None).ConfigureAwait(false);
        var restored = await _repo.GetAsync(frameId, CancellationToken.None).ConfigureAwait(false);
        Assert.Multiple(() => {
            Assert.That(restored!.QuarantinedUtc, Is.Null);
            Assert.That(restored.QuarantineReason, Is.Null);
            Assert.That(File.Exists(path), Is.True);
        });
    }

    [Test]
    public async Task Preview_cancellation_persists_interrupted_and_safe_failure() {
        var (frameId, _) = await InsertFitsFrameAsync().ConfigureAwait(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderer = new Mock<IPreviewImageService>();
        renderer.Setup(x => x.RenderAsync(It.IsAny<PreviewRenderRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns<PreviewRenderRequest, CancellationToken>(async (_, ct) => {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return default;
            });
        var repo = new SqliteFrameRepository(_db, _profile, _ws,
            previewImages: renderer.Object);
        using var cts = new CancellationTokenSource();

        var render = repo.GetPreviewAsync(frameId, PreviewRequest(), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        Assert.CatchAsync<OperationCanceledException>(async () => await render.ConfigureAwait(false));
        var metadata = await repo.GetMetadataAsync(frameId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(metadata!.PreviewState, Is.EqualTo("interrupted"));
            Assert.That(metadata.PreviewFailureCode, Is.EqualTo("preview_cancelled"));
            Assert.That(metadata.PreviewChecksum, Is.Null);
        });
    }

    [Test]
    public async Task Restart_marks_inflight_derived_work_interrupted_and_is_idempotent() {
        var (frameId, _) = await InsertFitsFrameAsync().ConfigureAwait(false);
        await using (var conn = _db.OpenConnection()) {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE frames
                SET preview_state = 'rendering', analysis_state = 'analyzing'
                WHERE id = $id;
                UPDATE schema_version SET version = 6;
                """;
            cmd.Parameters.AddWithValue("$id", frameId.ToString("D"));
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await _db.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await _db.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var metadata = await _repo.GetMetadataAsync(frameId, CancellationToken.None)
            .ConfigureAwait(false);
        await using var check = _db.OpenConnection();
        await using var version = check.CreateCommand();
        version.CommandText = "SELECT version FROM schema_version;";

        Assert.Multiple(() => {
            Assert.That(metadata!.PreviewState, Is.EqualTo("interrupted"));
            Assert.That(metadata.AnalysisState, Is.EqualTo("interrupted"));
            Assert.That(metadata.PreviewFailureCode, Is.EqualTo("daemon_restarted"));
            Assert.That(metadata.AnalysisFailureCode, Is.EqualTo("daemon_restarted"));
            Assert.That(Convert.ToInt64(version.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(7));
        });
    }

    [Test]
    public async Task V6_schema_missing_operation_columns_migrates_to_v7() {
        await using (var conn = _db.OpenConnection()) {
            await using var downgrade = conn.CreateCommand();
            downgrade.CommandText = """
                DROP INDEX idx_frames_quarantined_utc;
                ALTER TABLE frames DROP COLUMN analysis_state;
                ALTER TABLE frames DROP COLUMN analysis_failure_code;
                ALTER TABLE frames DROP COLUMN analysis_failure_message;
                ALTER TABLE frames DROP COLUMN preview_state;
                ALTER TABLE frames DROP COLUMN preview_failure_code;
                ALTER TABLE frames DROP COLUMN preview_failure_message;
                ALTER TABLE frames DROP COLUMN preview_checksum;
                ALTER TABLE frames DROP COLUMN debayer_method;
                ALTER TABLE frames DROP COLUMN preview_version;
                ALTER TABLE frames DROP COLUMN quarantined_utc;
                ALTER TABLE frames DROP COLUMN quarantine_reason;
                UPDATE schema_version SET version = 6;
                """;
            await downgrade.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await _db.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        await using var check = _db.OpenConnection();
        await using var columns = check.CreateCommand();
        columns.CommandText = """
            SELECT COUNT(*) FROM pragma_table_info('frames')
            WHERE name IN ('analysis_state', 'analysis_failure_code',
                           'analysis_failure_message', 'preview_state',
                           'preview_failure_code', 'preview_failure_message',
                           'preview_checksum', 'debayer_method', 'preview_version',
                           'quarantined_utc', 'quarantine_reason');
            """;
        Assert.That(Convert.ToInt64(await columns.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture), Is.EqualTo(11));
    }

    private async Task<(Guid FrameId, string Path)> InsertFitsFrameAsync(bool blank = false) {
        var frameId = Guid.NewGuid();
        var path = Path.Combine(_root, $"{frameId:D}.fits");
        const int width = 32;
        const int height = 24;
        var pixels = blank
            ? Enumerable.Repeat((ushort)1000, width * height).ToArray()
            : Enumerable.Range(0, width * height).Select(x => (ushort)(x * 31)).ToArray();
        using (var fits = FitsImage.Create(path, width, height, FitsBitDepth.UnsignedShort)) {
            fits.WriteImageData(pixels);
            fits.SetHeader("IMAGETYP", "LIGHT");
            fits.SetHeader("EXPTIME", 60.0);
            fits.Complete();
        }
        await _repo.InsertAsync(Frame(frameId, path, new FileInfo(path).Length, width, height),
            CancellationToken.None).ConfigureAwait(false);
        return (frameId, path);
    }

    private FrameDto Frame(Guid id, string path, long length, int width = 32,
            int height = 24) => new(id, _sessionId, "M31", FrameType.Light, "L", 60,
        100, 20, -10, DateTimeOffset.UtcNow, path, length, width, height, 16,
        null, null, null, null, null, null, 0, []);

    private static FramePreviewRequestDto PreviewRequest() => new(
        "linear", null, null, null, 512, ApplyDebayer: true);

    private async Task InsertSessionAsync(Guid id) {
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sessions (id, started_at) VALUES ($id, $started);";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        cmd.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private JsonElement AssertEvent(string eventType, Guid frameId, Guid sessionId) {
        var found = _ws.Events.FirstOrDefault(x => x.Type == eventType);
        Assert.That(found, Is.Not.Null, $"missing {eventType}");
        Assert.Multiple(() => {
            Assert.That(found!.Payload.GetProperty("frame_id").GetString(),
                Is.EqualTo(frameId.ToString("D")));
            Assert.That(found.Payload.GetProperty("session_id").GetString(),
                Is.EqualTo(sessionId.ToString("D")));
        });
        return found!.Payload;
    }

    private static async Task<string> HashFileAsync(string path) {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private sealed record RecordedEvent(string Type, JsonElement Payload);

    private sealed class RecordingWs : IWsBroadcaster {
        internal List<RecordedEvent> Events { get; } = [];
        public long CurrentSequence => Events.Count;

        public Task PublishAsync(string eventType, JsonElement payload, CancellationToken ct) {
            Events.Add(new RecordedEvent(eventType, payload.Clone()));
            return Task.CompletedTask;
        }
    }
}