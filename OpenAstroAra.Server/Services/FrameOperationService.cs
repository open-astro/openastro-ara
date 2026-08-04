#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

public sealed class FrameSourceUnavailableException : InvalidOperationException {
    public FrameSourceUnavailableException()
        : base("The frame source image is unavailable.") { }

    public FrameSourceUnavailableException(string message) : base(message) { }

    public FrameSourceUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }

    public FrameSourceUnavailableException(Guid frameId)
        : base($"Frame {frameId:D} exists, but its source image is unavailable.") { }
}

public sealed class FrameOperationInProgressException : InvalidOperationException {
    public FrameOperationInProgressException()
        : base("A conflicting frame operation is already in progress.") { }

    public FrameOperationInProgressException(string message) : base(message) { }

    public FrameOperationInProgressException(string message, Exception innerException)
        : base(message, innerException) { }

    public FrameOperationInProgressException(Guid frameId, string operation)
        : base($"Frame {frameId:D} already has a different {operation} request in progress.") { }
}

/// <summary>
/// Admission and asynchronous execution for CPU/IO-heavy frame operations.
/// One job per frame and operation kind runs at once; accepted jobs remain
/// observable through the existing /api/v1/jobs surface.
/// </summary>
public sealed class FrameOperationService : IFrameOperationService {
    private sealed record ActiveOperation(Guid JobId, string Fingerprint);

    private sealed class FrameNotFoundDuringAdmissionException : Exception {
        public FrameNotFoundDuringAdmissionException() { }

        public FrameNotFoundDuringAdmissionException(string message) : base(message) { }

        public FrameNotFoundDuringAdmissionException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    private readonly IFrameRepository _frames;
    private readonly IBatchJobService _jobs;
    private readonly IdempotencyCache<OperationAcceptedDto> _previewRebuilds = new();
    private readonly IdempotencyCache<OperationAcceptedDto> _reanalyses = new();
    private readonly Dictionary<string, ActiveOperation> _active = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public FrameOperationService(IFrameRepository frames, IBatchJobService jobs) {
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
    }

    public async Task<OperationAcceptedDto?> RebuildPreviewAsync(Guid frameId,
            FramePreviewRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ValidateFrameId(frameId);
        ArgumentNullException.ThrowIfNull(request);
        ValidatePreviewRequest(request);
        var fingerprint = JsonSerializer.Serialize(request,
            AraJsonSerializerContext.Default.FramePreviewRequestDto);
        try {
            return await _previewRebuilds.GetOrRunAsync(idempotencyKey,
                $"{frameId:D}|{fingerprint}", async () => {
                    var metadata = await RequireAvailableFrameAsync(frameId, ct)
                        .ConfigureAwait(false);
                    if (metadata is null) throw new FrameNotFoundDuringAdmissionException();
                    return StartJob(frameId, "rebuild-preview", fingerprint, idempotencyKey,
                        persistedActive: string.Equals(metadata.PreviewState, "rendering",
                            StringComparison.Ordinal),
                        async jobCt => {
                            try {
                                var result = await _frames.RebuildPreviewAsync(frameId, request, jobCt)
                                    .ConfigureAwait(false);
                                if (result is null) {
                                    throw new KeyNotFoundException(
                                        $"Frame {frameId:D} disappeared during preview rebuild.");
                                }
                            } catch (OperationCanceledException) {
                                throw;
                            } catch (Exception ex) {
                                throw new InvalidOperationException(
                                    "Frame preview rebuild failed.", ex);
                            }
                        });
                }).ConfigureAwait(false);
        } catch (FrameNotFoundDuringAdmissionException) {
            return null;
        }
    }

    public async Task<OperationAcceptedDto?> ReanalyzeAsync(Guid frameId,
            FrameReanalysisRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ValidateFrameId(frameId);
        ArgumentNullException.ThrowIfNull(request);
        ValidateReanalysisRequest(request);
        var fingerprint = JsonSerializer.Serialize(request,
            AraJsonSerializerContext.Default.FrameReanalysisRequestDto);
        try {
            return await _reanalyses.GetOrRunAsync(idempotencyKey,
                $"{frameId:D}|{fingerprint}", async () => {
                    var metadata = await RequireAvailableFrameAsync(frameId, ct)
                        .ConfigureAwait(false);
                    if (metadata is null) throw new FrameNotFoundDuringAdmissionException();
                    return StartJob(frameId, "reanalyze", fingerprint, idempotencyKey,
                        persistedActive: string.Equals(metadata.AnalysisState, "analyzing",
                            StringComparison.Ordinal),
                        async jobCt => {
                            try {
                                var result = await _frames.ReanalyzeAsync(frameId, request, jobCt)
                                    .ConfigureAwait(false);
                                if (result is null) {
                                    throw new KeyNotFoundException(
                                        $"Frame {frameId:D} disappeared during reanalysis.");
                                }
                            } catch (OperationCanceledException) {
                                throw;
                            } catch (Exception ex) {
                                throw new InvalidOperationException(
                                    "Frame reanalysis failed.", ex);
                            }
                        });
                }).ConfigureAwait(false);
        } catch (FrameNotFoundDuringAdmissionException) {
            return null;
        }
    }

    private async Task<FrameMetadataResult?> RequireAvailableFrameAsync(
            Guid frameId, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var metadata = await _frames.GetMetadataAsync(frameId, ct).ConfigureAwait(false);
        if (metadata is { SourceExists: false }) {
            throw new FrameSourceUnavailableException(frameId);
        }
        return metadata;
    }

    private OperationAcceptedDto StartJob(Guid frameId, string operation,
            string fingerprint, string? idempotencyKey, bool persistedActive,
            Func<CancellationToken, Task> work) {
        var activeKey = $"{operation}:{frameId:D}";
        lock (_gate) {
            if (_active.TryGetValue(activeKey, out var active)) {
                var existing = _jobs.GetJob(active.JobId);
                if (existing?.State is "queued" or "running") {
                    if (!string.Equals(active.Fingerprint, fingerprint, StringComparison.Ordinal)) {
                        throw new FrameOperationInProgressException(frameId, operation);
                    }
                    return Accepted(existing, operation, idempotencyKey);
                }
                _active.Remove(activeKey);
            }
            if (persistedActive) {
                throw new FrameOperationInProgressException(frameId, operation);
            }

            var job = _jobs.Enqueue($"frames.{operation}:{frameId:D}", 1,
                async (report, jobCt) => {
                    try {
                        await work(jobCt).ConfigureAwait(false);
                        report(1);
                    } finally {
                        lock (_gate) {
                            if (_active.TryGetValue(activeKey, out var current)
                                && string.Equals(current.Fingerprint, fingerprint,
                                    StringComparison.Ordinal)) {
                                _active.Remove(activeKey);
                            }
                        }
                    }
                });
            _active[activeKey] = new ActiveOperation(job.JobId, fingerprint);
            return Accepted(job, operation, idempotencyKey);
        }
    }

    private static OperationAcceptedDto Accepted(BatchJobDto job, string operation,
            string? idempotencyKey) => new(
        OperationId: job.JobId,
        OperationType: $"frames.{operation}",
        AcceptedUtc: job.StartedUtc,
        IdempotencyKey: string.IsNullOrWhiteSpace(idempotencyKey)
            ? null
            : idempotencyKey.Trim());

    internal static void ValidatePreviewRequest(FramePreviewRequestDto request) {
        var palette = request.StretchPalette?.Trim().ToLowerInvariant();
        if (palette is not (null or "" or "auto_stf" or "stf" or "auto"
            or "linear" or "log" or "asinh" or "sqrt" or "equalized"
            or "histogram" or "manual")) {
            throw new ArgumentException("Unknown stretch palette.", nameof(request));
        }
        var channel = request.ChannelMode?.Trim().ToLowerInvariant();
        if (channel is not (null or "" or "rgb" or "color" or "luminance"
            or "gray" or "mono" or "red" or "green" or "blue")) {
            throw new ArgumentException("Unsupported preview channel mode.", nameof(request));
        }
        if (request.MaxDimensionPx is <= 0 or > 4096) {
            throw new ArgumentOutOfRangeException(nameof(request),
                "MaxDimensionPx must be between 1 and 4096.");
        }
        ValidateFiniteRange(request.Saturation, 0, 2, nameof(request.Saturation));
        ValidateFiniteRange(request.BlackPoint, 0, 1, nameof(request.BlackPoint));
        ValidateFiniteRange(request.MidtonePoint, 0, 1, nameof(request.MidtonePoint));
        ValidateFiniteRange(request.WhitePoint, 0, 1, nameof(request.WhitePoint));
        if (request.BlackPoint is { } black && request.WhitePoint is { } white
            && white <= black) {
            throw new ArgumentException("WhitePoint must exceed BlackPoint.", nameof(request));
        }
        ValidateFiniteRange(request.AsinhBeta, double.Epsilon, 1_000_000,
            nameof(request.AsinhBeta));
        ValidateFiniteRange(request.LinearClipLow, 0, 1, nameof(request.LinearClipLow));
        ValidateFiniteRange(request.LinearClipHigh, 0, 1, nameof(request.LinearClipHigh));
        if (request.LinearClipLow is { } low && request.LinearClipHigh is { } high
            && low >= high) {
            throw new ArgumentException(
                "LinearClipLow must be lower than LinearClipHigh.", nameof(request));
        }
        ValidateFiniteRange(request.StarSensitivity, 0.5, 50,
            nameof(request.StarSensitivity));
        if (request.StarNoiseReduction is < 0 or > 3) {
            throw new ArgumentOutOfRangeException(nameof(request),
                "StarNoiseReduction must be between 0 and 3.");
        }
        if (request.AnnotationColor is { Length: > 32 }) {
            throw new ArgumentException("AnnotationColor must not exceed 32 characters.",
                nameof(request));
        }
        if (request.AnnotationColor is { } color && !IsAnnotationColor(color)) {
            throw new ArgumentException(
                "Unknown annotation color. Use #RRGGBB, green, red, yellow, cyan, or white.",
                nameof(request));
        }
        if (request.AnnotationFontFamily is { Length: > 128 }) {
            throw new ArgumentException("AnnotationFontFamily must not exceed 128 characters.",
                nameof(request));
        }
        ValidateFiniteRange(request.AnnotationStrokeWidth, double.Epsilon, 32,
            nameof(request.AnnotationStrokeWidth));
        ValidateFiniteRange(request.AnnotationFontSize, double.Epsilon, 128,
            nameof(request.AnnotationFontSize));
        if (request.MaxAnnotatedStars is <= 0 or > 10_000) {
            throw new ArgumentOutOfRangeException(nameof(request),
                "MaxAnnotatedStars must be between 1 and 10000.");
        }
        var cropValues = new[] { request.CropX, request.CropY,
            request.CropWidth, request.CropHeight };
        var supplied = cropValues.Count(static value => value.HasValue);
        if (supplied is not (0 or 4)) {
            throw new ArgumentException("All crop values must be supplied together.", nameof(request));
        }
        if (supplied == 4 && (request.CropX < 0 || request.CropY < 0
            || request.CropWidth <= 0 || request.CropHeight <= 0)) {
            throw new ArgumentException(
                "Crop origin must be non-negative and dimensions must be positive.", nameof(request));
        }
    }

    internal static void ValidateReanalysisRequest(FrameReanalysisRequestDto request) {
        ValidateFiniteRange(request.StarSensitivity, 0.5, 50,
            nameof(request.StarSensitivity));
        if (request.StarNoiseReduction is < 0 or > 3) {
            throw new ArgumentOutOfRangeException(nameof(request),
                "StarNoiseReduction must be between 0 and 3.");
        }
    }

    private static void ValidateFiniteRange(double? value, double minimum,
            double maximum, string name) {
        if (value is { } actual
            && (!double.IsFinite(actual) || actual < minimum || actual > maximum)) {
            throw new ArgumentOutOfRangeException(name,
                $"{name} must be finite and between {minimum} and {maximum}.");
        }
    }

    private static bool IsAnnotationColor(string value) {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "" or "green" or "red" or "yellow" or "cyan" or "white") {
            return true;
        }
        if (normalized.Length != 7 || normalized[0] != '#') return false;
        foreach (var character in normalized.AsSpan(1)) {
            if (!Uri.IsHexDigit(character)) return false;
        }
        return true;
    }

    private static void ValidateFrameId(Guid frameId) {
        if (frameId == Guid.Empty) {
            throw new ArgumentException("Frame id must not be empty.", nameof(frameId));
        }
    }
}