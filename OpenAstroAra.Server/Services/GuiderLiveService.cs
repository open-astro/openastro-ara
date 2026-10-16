#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>The live guide-camera view's control surface (#1364, epic item 1).</summary>
public interface IGuiderLiveService {
    /// <summary>What the guider sees and holds; <c>Connected</c> false with everything null when no
    /// guider is connected (200, never 404 — the panel shows its empty state).</summary>
    Task<GuiderLiveDto> GetAsync(CancellationToken ct);
    /// <summary>The guider's latest frame as its own stretched JPEG, or null when it has none yet
    /// (not looping) or no guider is connected.</summary>
    Task<byte[]?> GetFrameJpegAsync(CancellationToken ct);
    /// <summary>Start looping exposures so there is a picture and a star to pick.</summary>
    Task LoopAsync(CancellationToken ct);
    /// <summary>Stop looping or guiding.</summary>
    Task StopAsync(CancellationToken ct);
    /// <summary>Lock on the star nearest a tap, in guide pixels.</summary>
    Task SelectStarAsync(double x, double y, CancellationToken ct);
    Task DeselectStarAsync(CancellationToken ct);
}

/// <summary>
/// Setup / Live → Guiding → the guide camera's own picture, from Ara instead of the guider's web page:
/// the frame the guider is looping on, the star it locked, the search box, the SNR and mass of the
/// last guide frame, multi-star on or off, and the other stars it found; a tap on the picture picks a
/// star. Everything goes through the daemon's guider connection (the client never talks to the guider,
/// §63.20): the picture is the guider's live JPEG fetched daemon-side, the rest is a handful of RPCs
/// answered best-effort — one the guider refuses (no camera yet, no image) leaves its field null.
/// </summary>
public sealed partial class GuiderLiveService : IGuiderLiveService {

    /// <summary>How many of the guider's detected stars the view lists: enough to show a multi-star
    /// set, few enough to draw without clutter.</summary>
    internal const int MaxStars = 12;

    private readonly GuiderService _guider;
    private readonly IPolarAlignFrameFetcher _fetcher;
    private readonly ILogger<GuiderLiveService> _logger;

    public GuiderLiveService(GuiderService guider, IPolarAlignFrameFetcher fetcher, ILogger<GuiderLiveService>? logger = null) {
        _guider = guider ?? throw new ArgumentNullException(nameof(guider));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _logger = logger ?? NullLogger<GuiderLiveService>.Instance;
    }

    internal static readonly GuiderLiveDto Disconnected = new(
        Connected: false, AppState: null, FrameWidth: null, FrameHeight: null, LockX: null, LockY: null,
        SearchRegion: null, Snr: null, StarMass: null, MultiStarEnabled: null, Stars: Array.Empty<GuiderLiveStarDto>(), HasFrame: false);

    public async Task<GuiderLiveDto> GetAsync(CancellationToken ct) {
        var guider = _guider.ConnectedGuiderOrNull();
        if (guider is null) {
            return Disconnected;
        }
        // Each read is independent and best-effort: a guider without a camera answers the frame size
        // with an error, one that is not looping has no stars — neither is a reason to hide the rest.
        // They run together (each RPC is its own connection), so a slow guider costs one RPC's time,
        // not five, against the client's poll.
        var sizeTask = Quietly(() => guider.GetCameraFrameSizeAsync(ct), "frame size");
        var lockTask = Quietly<OpenAstroAra.Equipment.Equipment.MyGuider.LockPosition?>(async () => await guider.GetLockPosition().ConfigureAwait(false), "lock position");
        var regionTask = Quietly(() => guider.GetSearchRegionAsync(ct), "search region");
        var multiTask = Quietly(() => guider.GetMultiStarEnabledAsync(ct), "multi-star");
        var centroidsTask = Quietly<IReadOnlyList<Phd2StarCentroid>?>(async () => await guider.GetStarCentroidsAsync(null, MaxStars, ct).ConfigureAwait(false), "star centroids");
        await Task.WhenAll(sizeTask, lockTask, regionTask, multiTask, centroidsTask).ConfigureAwait(false);
        var size = await sizeTask.ConfigureAwait(false);
        var lockPos = await lockTask.ConfigureAwait(false);
        var region = await regionTask.ConfigureAwait(false);
        var multi = await multiTask.ConfigureAwait(false);
        var centroids = await centroidsTask.ConfigureAwait(false);
        var stars = new List<GuiderLiveStarDto>();
        if (centroids is not null) {
            foreach (var c in centroids) {
                if (c.X is double x && c.Y is double y) {
                    stars.Add(new GuiderLiveStarDto(Math.Round(x, 2), Math.Round(y, 2), c.Snr, c.Mass, c.Hfd));
                }
            }
        }
        var (snr, mass) = _guider.LatestStarStats;
        var state = guider.AppState?.State;
        return new GuiderLiveDto(
            Connected: true,
            AppState: state,
            FrameWidth: size?.Width,
            FrameHeight: size?.Height,
            LockX: lockPos is null ? null : Math.Round(lockPos.X, 2),
            LockY: lockPos is null ? null : Math.Round(lockPos.Y, 2),
            SearchRegion: region,
            Snr: snr,
            StarMass: mass,
            MultiStarEnabled: multi,
            Stars: stars,
            HasFrame: state is not null && state != "Stopped");
    }

    /// <summary>How long the frame fetch may take before the picture is skipped this poll — the same
    /// bound the polar-align preview uses (<see cref="PolarAlignService"/>).</summary>
    internal static readonly TimeSpan FrameFetchTimeout = TimeSpan.FromSeconds(5);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The picture is best-effort: any fetch failure (no frame yet, a wedged or slow guider HTTP side, a timeout) means no image this poll — 204, never a 500.")]
    public async Task<byte[]?> GetFrameJpegAsync(CancellationToken ct) {
        var guider = _guider.ConnectedGuiderOrNull();
        if (guider is null || string.IsNullOrEmpty(guider.ConnectedHost)) {
            return null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(FrameFetchTimeout);
        try {
            return await _fetcher.FetchLiveFrameJpegAsync(guider.ConnectedHost, guider.ConnectedRpcPort, timeout.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            // No frame yet (404 while not looping), the guider's HTTP side down or too slow (the fetcher's
            // own timeout surfaces as TaskCanceledException with the caller's token still live).
            LogFrameUnavailable(ex.Message);
            return null;
        }
    }

    public Task LoopAsync(CancellationToken ct) => _guider.RequireConnectedGuider().LoopAsync(ct);

    public Task StopAsync(CancellationToken ct) => _guider.RequireConnectedGuider().StopCaptureAsync(ct);

    /// <summary>Lock on the star nearest (x, y). Refused as out of range (400) when the position is
    /// negative or not finite, or — once the guider reports its frame size — outside the frame; with no
    /// frame size known, the guider itself judges the position.</summary>
    public async Task SelectStarAsync(double x, double y, CancellationToken ct) {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0) {
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is not a position in the frame");
        }
        var guider = _guider.RequireConnectedGuider();
        var size = await Quietly(() => guider.GetCameraFrameSizeAsync(ct), "frame size").ConfigureAwait(false);
        if (size is { } s && (x >= s.Width || y >= s.Height)) {
            throw new ArgumentOutOfRangeException(nameof(x), $"({x}, {y}) is outside the {s.Width}×{s.Height} guide frame");
        }
        await guider.SetLockPositionAsync(x, y, ct).ConfigureAwait(false);
    }

    public Task DeselectStarAsync(CancellationToken ct) => _guider.RequireConnectedGuider().DeselectStarAsync(ct);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort read of one field of the live view: a refused RPC leaves it null, the rest of the view still answers. Log-and-recover boundary.")]
    private async Task<T?> Quietly<T>(Func<Task<T?>> read, string what) {
        try {
            return await read().ConfigureAwait(false);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogFieldUnavailable(what, ex.Message);
            return default;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Guider live view: {What} not available ({Reason})")]
    partial void LogFieldUnavailable(string what, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Guider live view: no frame ({Reason})")]
    partial void LogFrameUnavailable(string reason);
}
