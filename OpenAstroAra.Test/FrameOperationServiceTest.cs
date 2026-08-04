#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Moq;
using NUnit.Framework;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class FrameOperationServiceTest {
    [Test]
    public async Task Unknown_frame_returns_null_without_enqueuing() {
        var frames = new Mock<IFrameRepository>();
        frames.Setup(x => x.GetMetadataAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FrameMetadataResult?)null);
        var jobs = new InMemoryBatchJobService(logger: null);
        var service = new FrameOperationService(frames.Object, jobs);

        var accepted = await service.ReanalyzeAsync(Guid.NewGuid(), new(), null,
            CancellationToken.None).ConfigureAwait(false);

        Assert.That(accepted, Is.Null);
        frames.Verify(x => x.ReanalyzeAsync(It.IsAny<Guid>(),
            It.IsAny<FrameReanalysisRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Unknown_frame_result_is_not_cached_as_an_accepted_replay() {
        var frameId = Guid.NewGuid();
        var frames = new Mock<IFrameRepository>();
        frames.SetupSequence(x => x.GetMetadataAsync(frameId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((FrameMetadataResult?)null)
            .ReturnsAsync(Metadata(frameId, sourceExists: true));
        frames.Setup(x => x.RebuildPreviewAsync(frameId,
                It.IsAny<FramePreviewRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PreviewResult(frameId));
        var service = new FrameOperationService(frames.Object,
            new InMemoryBatchJobService(logger: null));

        var missing = await service.RebuildPreviewAsync(frameId, Preview(), "late-frame",
            CancellationToken.None).ConfigureAwait(false);
        var accepted = await service.RebuildPreviewAsync(frameId, Preview(), "late-frame",
            CancellationToken.None).ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(missing, Is.Null);
            Assert.That(accepted, Is.Not.Null);
        });
        frames.Verify(x => x.GetMetadataAsync(frameId, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Test]
    public void Missing_source_is_conflict_before_enqueuing() {
        var frameId = Guid.NewGuid();
        var frames = FramesWithMetadata(frameId, sourceExists: false);
        var service = new FrameOperationService(frames.Object,
            new InMemoryBatchJobService(logger: null));

        Assert.ThrowsAsync<FrameSourceUnavailableException>(() =>
            service.RebuildPreviewAsync(frameId, Preview(), null, CancellationToken.None));
        frames.Verify(x => x.RebuildPreviewAsync(It.IsAny<Guid>(),
            It.IsAny<FramePreviewRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Same_key_and_request_replays_job_but_different_request_conflicts() {
        var frameId = Guid.NewGuid();
        var frames = FramesWithMetadata(frameId, sourceExists: true);
        frames.Setup(x => x.RebuildPreviewAsync(frameId,
                It.IsAny<FramePreviewRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PreviewResult(frameId));
        var jobs = new InMemoryBatchJobService(logger: null);
        var service = new FrameOperationService(frames.Object, jobs);

        var first = await service.RebuildPreviewAsync(frameId, Preview(), "request-1",
            CancellationToken.None).ConfigureAwait(false);
        var replay = await service.RebuildPreviewAsync(frameId, Preview(), "request-1",
            CancellationToken.None).ConfigureAwait(false);

        Assert.That(replay!.OperationId, Is.EqualTo(first!.OperationId));
        Assert.ThrowsAsync<IdempotencyKeyConflictException>(() =>
            service.RebuildPreviewAsync(frameId, Preview() with { Invert = true },
                "request-1", CancellationToken.None));
    }

    [Test]
    public async Task Different_active_request_without_key_is_rejected() {
        var frameId = Guid.NewGuid();
        var frames = FramesWithMetadata(frameId, sourceExists: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        frames.Setup(x => x.RebuildPreviewAsync(frameId,
                It.IsAny<FramePreviewRequestDto>(), It.IsAny<CancellationToken>()))
            .Returns(async () => {
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return PreviewResult(frameId);
            });
        var jobs = new InMemoryBatchJobService(logger: null);
        var service = new FrameOperationService(frames.Object, jobs);

        var first = await service.RebuildPreviewAsync(frameId, Preview(), null,
            CancellationToken.None).ConfigureAwait(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Assert.ThrowsAsync<FrameOperationInProgressException>(() =>
            service.RebuildPreviewAsync(frameId, Preview() with { Invert = true },
                null, CancellationToken.None));
        release.TrySetResult();
        var terminal = await WaitForTerminalAsync(jobs, first!.OperationId).ConfigureAwait(false);
        Assert.That(terminal.State, Is.EqualTo("complete"));
    }

    [Test]
    public async Task Same_key_replays_job_after_catalog_state_becomes_active() {
        var frameId = Guid.NewGuid();
        var active = false;
        var frames = new Mock<IFrameRepository>();
        frames.Setup(x => x.GetMetadataAsync(frameId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Metadata(frameId, sourceExists: true,
                previewState: active ? "rendering" : null));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        frames.Setup(x => x.RebuildPreviewAsync(frameId,
                It.IsAny<FramePreviewRequestDto>(), It.IsAny<CancellationToken>()))
            .Returns(async () => {
                active = true;
                entered.TrySetResult();
                await release.Task.ConfigureAwait(false);
                return PreviewResult(frameId);
            });
        var jobs = new InMemoryBatchJobService(logger: null);
        var service = new FrameOperationService(frames.Object, jobs);

        var first = await service.RebuildPreviewAsync(frameId, Preview(), "retry-1",
            CancellationToken.None).ConfigureAwait(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var replay = await service.RebuildPreviewAsync(frameId, Preview(), "retry-1",
            CancellationToken.None).ConfigureAwait(false);

        Assert.That(replay!.OperationId, Is.EqualTo(first!.OperationId));
        release.TrySetResult();
        var terminal = await WaitForTerminalAsync(jobs, first.OperationId).ConfigureAwait(false);
        Assert.That(terminal.State, Is.EqualTo("complete"));
    }

    [Test]
    public async Task Accepted_key_replays_without_rechecking_mutable_source_state() {
        var frameId = Guid.NewGuid();
        var frames = new Mock<IFrameRepository>();
        frames.SetupSequence(x => x.GetMetadataAsync(frameId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Metadata(frameId, sourceExists: true))
            .ReturnsAsync(Metadata(frameId, sourceExists: false));
        frames.Setup(x => x.RebuildPreviewAsync(frameId,
                It.IsAny<FramePreviewRequestDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PreviewResult(frameId));
        var jobs = new InMemoryBatchJobService(logger: null);
        var service = new FrameOperationService(frames.Object, jobs);

        var first = await service.RebuildPreviewAsync(frameId, Preview(), "durable-retry",
            CancellationToken.None).ConfigureAwait(false);
        var replay = await service.RebuildPreviewAsync(frameId, Preview(), "durable-retry",
            CancellationToken.None).ConfigureAwait(false);

        Assert.That(replay!.OperationId, Is.EqualTo(first!.OperationId));
        frames.Verify(x => x.GetMetadataAsync(frameId, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task Repository_failure_exposes_safe_job_message() {
        var frameId = Guid.NewGuid();
        var frames = FramesWithMetadata(frameId, sourceExists: true);
        frames.Setup(x => x.RebuildPreviewAsync(frameId,
                It.IsAny<FramePreviewRequestDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("/private/source/path could not be decoded"));
        var jobs = new InMemoryBatchJobService(logger: null);
        var service = new FrameOperationService(frames.Object, jobs);

        var accepted = await service.RebuildPreviewAsync(frameId, Preview(), null,
            CancellationToken.None).ConfigureAwait(false);
        var terminal = await WaitForTerminalAsync(jobs, accepted!.OperationId)
            .ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(terminal.State, Is.EqualTo("failed"));
            Assert.That(terminal.ErrorMessage, Is.EqualTo("Frame preview rebuild failed."));
            Assert.That(terminal.ErrorMessage, Does.Not.Contain("/private"));
        });
    }

    [Test]
    public async Task Cancelling_job_cancels_repository_work_and_job_state() {
        var frameId = Guid.NewGuid();
        var frames = FramesWithMetadata(frameId, sourceExists: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        frames.Setup(x => x.ReanalyzeAsync(frameId,
                It.IsAny<FrameReanalysisRequestDto>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, FrameReanalysisRequestDto, CancellationToken>(async (_, _, ct) => {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
                return null;
            });
        var jobs = new InMemoryBatchJobService(logger: null);
        var service = new FrameOperationService(frames.Object, jobs);

        var accepted = await service.ReanalyzeAsync(frameId, new(), "cancel-me",
            CancellationToken.None).ConfigureAwait(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        Assert.That(jobs.TryCancel(accepted!.OperationId), Is.True);
        var terminal = await WaitForTerminalAsync(jobs, accepted.OperationId).ConfigureAwait(false);
        Assert.That(terminal.State, Is.EqualTo("cancelled"));
    }

    [Test]
    public void Invalid_request_is_rejected_before_catalog_access() {
        var frames = new Mock<IFrameRepository>();
        var service = new FrameOperationService(frames.Object,
            new InMemoryBatchJobService(logger: null));

        Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.ReanalyzeAsync(Guid.NewGuid(),
                new FrameReanalysisRequestDto(StarSensitivity: double.NaN),
                null, CancellationToken.None));
        frames.Verify(x => x.GetMetadataAsync(It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase("preview")]
    [TestCase("analysis")]
    public void Persisted_active_state_is_rejected_before_enqueuing(string operation) {
        var frameId = Guid.NewGuid();
        var frames = FramesWithMetadata(frameId, sourceExists: true,
            previewState: operation == "preview" ? "rendering" : null,
            analysisState: operation == "analysis" ? "analyzing" : null);
        var service = new FrameOperationService(frames.Object,
            new InMemoryBatchJobService(logger: null));

        if (operation == "preview") {
            Assert.ThrowsAsync<FrameOperationInProgressException>(() =>
                service.RebuildPreviewAsync(frameId, Preview(), null,
                    CancellationToken.None));
            frames.Verify(x => x.RebuildPreviewAsync(It.IsAny<Guid>(),
                It.IsAny<FramePreviewRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        } else {
            Assert.ThrowsAsync<FrameOperationInProgressException>(() =>
                service.ReanalyzeAsync(frameId, new(), null, CancellationToken.None));
            frames.Verify(x => x.ReanalyzeAsync(It.IsAny<Guid>(),
                It.IsAny<FrameReanalysisRequestDto>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }

    private static Mock<IFrameRepository> FramesWithMetadata(Guid frameId,
            bool sourceExists, string? previewState = null, string? analysisState = null) {
        var frames = new Mock<IFrameRepository>();
        frames.Setup(x => x.GetMetadataAsync(frameId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Metadata(frameId, sourceExists, previewState, analysisState));
        return frames;
    }

    private static FrameMetadataResult Metadata(Guid id, bool sourceExists,
            string? previewState = null, string? analysisState = null) => new(
        Frame: new FrameDto(id, Guid.NewGuid(), "M31", FrameType.Light, "L", 60,
            100, 20, -10, DateTimeOffset.UtcNow, "/tmp/frame.fits", 100,
            10, 10, 16, null, null, null, null, null, null, 0, []),
        Storage: null,
        SourceExists: sourceExists,
        SourceChecksumSha256: null,
        ImageFormat: "fits",
        CfaPattern: null,
        AnalysisState: analysisState,
        AnalysisFailureCode: null,
        AnalysisFailureMessage: null,
        PreviewState: previewState,
        PreviewFailureCode: null,
        PreviewFailureMessage: null,
        PreviewChecksum: null,
        DebayerMethod: null,
        PreviewVersion: null);

    private static FramePreviewRequestDto Preview() => new(
        "auto_stf", null, null, null, 512, ApplyDebayer: true);

    private static FramePreviewResult PreviewResult(Guid id) => new(
        Bytes: [1, 2, 3],
        ContentType: "image/jpeg",
        Metadata: new PreviewCacheMetadata(2, id, new string('a', 64), "cache-key",
            1, 1, "auto_stf", new OpenAstroAra.Stretch.StretchParams(), "none",
            "luminance", false, 1, DateTimeOffset.UtcNow),
        CacheHit: false);

    private static async Task<BatchJobDto> WaitForTerminalAsync(
            InMemoryBatchJobService jobs, Guid jobId) {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline) {
            var job = jobs.GetJob(jobId)!;
            if (job.State is "complete" or "failed" or "cancelled") return job;
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert.Fail("Job did not reach a terminal state.");
        return null!;
    }
}