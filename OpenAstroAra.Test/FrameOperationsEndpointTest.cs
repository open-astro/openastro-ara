#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Image.Interfaces;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test;

[TestFixture]
public sealed class FrameOperationsEndpointTest {
    [Test]
    public async Task Rebuild_returns_202_with_pollable_location() {
        var id = Guid.NewGuid();
        var accepted = AcceptedOperation("frames.rebuild-preview");
        var operations = new Mock<IFrameOperationService>();
        operations.Setup(x => x.RebuildPreviewAsync(id,
                It.IsAny<FramePreviewRequestDto>(), "key", It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted);

        var result = await ImageEndpoints.RebuildFramePreviewAsync(id, Preview(), "key",
            operations.Object, CancellationToken.None).ConfigureAwait(false);

        Assert.That(result, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
        var typed = (Accepted<OperationAcceptedDto>)result;
        Assert.Multiple(() => {
            Assert.That(typed.Value, Is.SameAs(accepted));
            Assert.That(typed.Location, Is.EqualTo($"/api/v1/jobs/{accepted.OperationId:D}"));
        });
    }

    [Test]
    public async Task Rebuild_maps_missing_conflict_and_validation_statuses() {
        var id = Guid.NewGuid();
        var operations = new Mock<IFrameOperationService>();
        operations.Setup(x => x.RebuildPreviewAsync(id,
                It.Is<FramePreviewRequestDto>(r => r.MaxDimensionPx == 1),
                null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((OperationAcceptedDto?)null);
        operations.Setup(x => x.RebuildPreviewAsync(id,
                It.Is<FramePreviewRequestDto>(r => r.MaxDimensionPx == 2),
                null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FrameSourceUnavailableException(id));
        operations.Setup(x => x.RebuildPreviewAsync(id,
                It.Is<FramePreviewRequestDto>(r => r.MaxDimensionPx == 3),
                null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("bad preview"));

        var missing = await ImageEndpoints.RebuildFramePreviewAsync(id,
            Preview() with { MaxDimensionPx = 1 }, null, operations.Object,
            CancellationToken.None).ConfigureAwait(false);
        var conflict = await ImageEndpoints.RebuildFramePreviewAsync(id,
            Preview() with { MaxDimensionPx = 2 }, null, operations.Object,
            CancellationToken.None).ConfigureAwait(false);
        var invalid = await ImageEndpoints.RebuildFramePreviewAsync(id,
            Preview() with { MaxDimensionPx = 3 }, null, operations.Object,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(Status(missing), Is.EqualTo(StatusCodes.Status404NotFound));
            Assert.That(Status(conflict), Is.EqualTo(StatusCodes.Status409Conflict));
            Assert.That(Status(invalid), Is.EqualTo(StatusCodes.Status400BadRequest));
        });
    }

    [Test]
    public async Task Reanalyze_returns_202_and_maps_idempotency_conflict() {
        var id = Guid.NewGuid();
        var accepted = AcceptedOperation("frames.reanalyze");
        var operations = new Mock<IFrameOperationService>();
        operations.Setup(x => x.ReanalyzeAsync(id,
                It.Is<FrameReanalysisRequestDto>(r => r.StarSensitivity == 8),
                "ok", It.IsAny<CancellationToken>()))
            .ReturnsAsync(accepted);
        operations.Setup(x => x.ReanalyzeAsync(id,
                It.IsAny<FrameReanalysisRequestDto>(), "conflict",
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IdempotencyKeyConflictException());

        var success = await ImageEndpoints.ReanalyzeFrameAsync(id,
            new(StarSensitivity: 8), "ok", operations.Object, CancellationToken.None)
            .ConfigureAwait(false);
        var conflict = await ImageEndpoints.ReanalyzeFrameAsync(id, new(), "conflict",
            operations.Object, CancellationToken.None).ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(success, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
            Assert.That(Status(conflict), Is.EqualTo(StatusCodes.Status409Conflict));
        });
    }

    [Test]
    public async Task Metadata_maps_found_and_missing() {
        var id = Guid.NewGuid();
        var repo = new Mock<IFrameRepository>();
        repo.SetupSequence(x => x.GetMetadataAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Metadata(id))
            .ReturnsAsync((FrameMetadataResult?)null);

        var found = await ImageEndpoints.GetFrameMetadataAsync(id, repo.Object,
            CancellationToken.None).ConfigureAwait(false);
        var missing = await ImageEndpoints.GetFrameMetadataAsync(id, repo.Object,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(found, Is.InstanceOf<Ok<FrameMetadataResult>>());
            Assert.That(Status(missing), Is.EqualTo(StatusCodes.Status404NotFound));
        });
    }

    [Test]
    public async Task Preview_maps_unsupported_source_to_422_and_invalid_request_to_400() {
        var repo = new Mock<IFrameRepository>();
        repo.Setup(x => x.GetPreviewAsync(It.IsAny<Guid>(),
                It.Is<FramePreviewRequestDto>(r => r.StretchPalette == "unsupported"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnsupportedSourceImageFormatException(
                "/srv/private/frame.fits could not be decoded"));
        repo.Setup(x => x.GetPreviewAsync(It.IsAny<Guid>(),
                It.Is<FramePreviewRequestDto>(r => r.StretchPalette == "bad-request"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("bad request"));
        repo.Setup(x => x.GetPreviewAsync(It.IsAny<Guid>(),
                It.Is<FramePreviewRequestDto>(r => r.StretchPalette == "raw-invalid"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RawImageDecodeException("safe native decode failure", -2));
        var http = new DefaultHttpContext();

        var unsupported = await ImageEndpoints.RenderFramePreviewAsync(Guid.NewGuid(),
            Preview() with { StretchPalette = "unsupported" }, http, repo.Object,
            CancellationToken.None).ConfigureAwait(false);
        var invalid = await ImageEndpoints.RenderFramePreviewAsync(Guid.NewGuid(),
            Preview() with { StretchPalette = "bad-request" }, http, repo.Object,
            CancellationToken.None).ConfigureAwait(false);
        var rawInvalid = await ImageEndpoints.RenderFramePreviewAsync(Guid.NewGuid(),
            Preview() with { StretchPalette = "raw-invalid" }, http, repo.Object,
            CancellationToken.None).ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(Status(unsupported), Is.EqualTo(StatusCodes.Status422UnprocessableEntity));
            Assert.That(((ProblemHttpResult)unsupported).ProblemDetails.Detail,
                Is.EqualTo("The frame source could not be decoded."));
            Assert.That(Status(invalid), Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(Status(rawInvalid), Is.EqualTo(StatusCodes.Status422UnprocessableEntity));
        });
    }

    [Test]
    public async Task Bulk_mutation_maps_success_invalid_and_key_conflict() {
        var accepted = AcceptedOperation("frames.bulk-rate");
        var success = await ImageEndpoints.BulkMutationAsync(() => Task.FromResult(accepted))
            .ConfigureAwait(false);
        var invalid = await ImageEndpoints.BulkMutationAsync(
            () => Task.FromException<OperationAcceptedDto>(new ArgumentException("bad")))
            .ConfigureAwait(false);
        var conflict = await ImageEndpoints.BulkMutationAsync(
            () => Task.FromException<OperationAcceptedDto>(new IdempotencyKeyConflictException()))
            .ConfigureAwait(false);

        Assert.Multiple(() => {
            Assert.That(success, Is.InstanceOf<Accepted<OperationAcceptedDto>>());
            Assert.That(Status(invalid), Is.EqualTo(StatusCodes.Status400BadRequest));
            Assert.That(Status(conflict), Is.EqualTo(StatusCodes.Status409Conflict));
        });
    }

    private static int? Status(IResult result) =>
        (result as ProblemHttpResult)?.StatusCode;

    private static OperationAcceptedDto AcceptedOperation(string type) =>
        new(Guid.NewGuid(), type, DateTimeOffset.UtcNow, null);

    private static FramePreviewRequestDto Preview() =>
        new("linear", null, null, null, 512, ApplyDebayer: true);

    private static FrameMetadataResult Metadata(Guid id) => new(
        Frame: new FrameDto(id, Guid.NewGuid(), "M31", FrameType.Light, "L", 60,
            100, 20, -10, DateTimeOffset.UtcNow, "/tmp/frame.fits", 100,
            10, 10, 16, null, null, null, null, null, null, 0, []),
        Storage: null,
        SourceExists: true,
        SourceChecksumSha256: null,
        ImageFormat: "fits",
        CfaPattern: null,
        AnalysisState: null,
        AnalysisFailureCode: null,
        AnalysisFailureMessage: null,
        PreviewState: null,
        PreviewFailureCode: null,
        PreviewFailureMessage: null,
        PreviewChecksum: null,
        DebayerMethod: null,
        PreviewVersion: null);
}