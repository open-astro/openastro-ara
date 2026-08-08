#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §65.4 background thumbnail warmer. Thumbnails cache as
/// <c>&lt;stem&gt;.thumb.jpg</c> sidecars on first render, but a freshly
/// imported archive has none — browsing it pays ~1.5 s per tile on a Pi as
/// the grid warms the cache interactively. This service walks the catalog
/// once after boot and renders every missing sidecar through the repository's
/// gated render path, so the library is instant by the time the user opens
/// it. Paced with a small delay per render and capped by the repository's
/// render gate, interactive requests stay responsive throughout.
/// </summary>
public sealed partial class ThumbnailWarmerService : BackgroundService {
    // Give startup (device connects, first client requests) the box to
    // itself before burning CPU on cache warming.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PacingDelay = TimeSpan.FromMilliseconds(250);

    private readonly IAraDatabase _db;
    private readonly IFrameRepository _frames;
    private readonly ILogger<ThumbnailWarmerService> _logger;

    public ThumbnailWarmerService(IAraDatabase db, IFrameRepository frames,
            ILogger<ThumbnailWarmerService> logger) {
        _db = db;
        _frames = frames;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
            var pending = await ListFramesMissingThumbnailsAsync(stoppingToken).ConfigureAwait(false);
            if (pending.Count > 0) {
                LogWarmStart(pending.Count);
                var warmed = 0;
                foreach (var id in pending) {
                    stoppingToken.ThrowIfCancellationRequested();
                    try {
                        // Renders once and writes the sidecar; a frame the user
                        // already viewed serves from cache in microseconds.
                        _ = await _frames.GetThumbnailAsync(id, stoppingToken).ConfigureAwait(false);
                        warmed++;
                    } catch (Exception ex) when (ex is not OperationCanceledException) {
                        // One corrupt/missing FITS must not stop the sweep.
                        LogWarmFrameFailed(ex, id);
                    }
                    await Task.Delay(PacingDelay, stoppingToken).ConfigureAwait(false);
                }
                LogWarmComplete(warmed);
            }
            await WarmPreviewsAsync(stoppingToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Shutdown — nothing to unwind; the cache keeps whatever landed.
        }
    }

    // Phase 2 (after thumbnails): pre-render each frame's default-stretch
    // preview variant so opening the viewer is a cache read instead of a
    // multi-second FITS decode. auto_stf matches the client's default palette
    // for lights; calibration frames resolve to linear server-side, and both
    // land under the §65.4 cache key the viewer's first request computes.
    private async Task WarmPreviewsAsync(CancellationToken ct) {
        var pending = await ListFramesMissingPreviewsAsync(ct).ConfigureAwait(false);
        if (pending.Count == 0) return;
        LogPreviewWarmStart(pending.Count);
        var warmed = 0;
        var request = new FramePreviewRequestDto(
            StretchPalette: "auto_stf", BlackPoint: null, MidtonePoint: null,
            WhitePoint: null, MaxDimensionPx: null, ApplyDebayer: false);
        foreach (var id in pending) {
            ct.ThrowIfCancellationRequested();
            try {
                _ = await _frames.GetPreviewAsync(id, request, ct).ConfigureAwait(false);
                warmed++;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                LogWarmFrameFailed(ex, id);
            }
            await Task.Delay(PacingDelay, ct).ConfigureAwait(false);
        }
        LogPreviewWarmComplete(warmed);
    }

    private async Task<List<Guid>> ListFramesMissingPreviewsAsync(CancellationToken ct) {
        var pending = new List<Guid>();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, file_path FROM frames ORDER BY captured_utc DESC;";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
            var filePath = reader.GetString(1);
            if (!File.Exists(filePath)) continue;
            var dir = Path.GetDirectoryName(filePath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(filePath);
            // Any existing variant means the viewer already has an instant
            // first render for this frame — don't burn CPU adding another.
            bool hasVariant;
            try {
                using var variants = Directory.EnumerateFiles(dir, $"{stem}.preview.*.jpg").GetEnumerator();
                hasVariant = variants.MoveNext();
            } catch (DirectoryNotFoundException) {
                continue;
            }
            if (hasVariant) continue;
            if (Guid.TryParse(reader.GetString(0), out var id)) pending.Add(id);
        }
        return pending;
    }

    private async Task<List<Guid>> ListFramesMissingThumbnailsAsync(CancellationToken ct) {
        var pending = new List<Guid>();
        await using var conn = _db.OpenConnection();
        await using var cmd = conn.CreateCommand();
        // Oldest last: warm newest sessions first — that's what the library
        // shows at the top and what the user opens first.
        cmd.CommandText = "SELECT id, file_path FROM frames ORDER BY captured_utc DESC;";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) {
            var filePath = reader.GetString(1);
            if (!File.Exists(filePath)) continue;
            var dir = Path.GetDirectoryName(filePath) ?? "";
            var stem = Path.GetFileNameWithoutExtension(filePath);
            if (File.Exists(Path.Combine(dir, $"{stem}.thumb.jpg"))) continue;
            if (Guid.TryParse(reader.GetString(0), out var id)) pending.Add(id);
        }
        return pending;
    }

    #region LoggerMessage delegates (CA1848)

    [LoggerMessage(Level = LogLevel.Information, Message = "§65.4 thumbnail warmer: {Count} frame(s) missing a thumbnail sidecar — warming in background")]
    private partial void LogWarmStart(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "§65.4 thumbnail warmer complete — {Count} thumbnail(s) rendered")]
    private partial void LogWarmComplete(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "§65.4 thumbnail warmer: frame {FrameId} failed")]
    private partial void LogWarmFrameFailed(Exception ex, Guid frameId);

    [LoggerMessage(Level = LogLevel.Information, Message = "§65.4 preview warmer: {Count} frame(s) missing a default preview variant — warming in background")]
    private partial void LogPreviewWarmStart(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "§65.4 preview warmer complete — {Count} preview(s) rendered")]
    private partial void LogPreviewWarmComplete(int count);

    #endregion
}
