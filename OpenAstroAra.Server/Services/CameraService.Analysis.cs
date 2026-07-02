#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Clients;
using OpenAstroAra.Server.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// A throwaway in-memory measurement frame for image analysis (§59 autofocus HFR probes).
/// Deliberately NOT persisted: no §72 FITS write, no §28 catalog row — probe frames are
/// instrument readings, not data the user ever revisits.
/// </summary>
public sealed record AnalysisFrame(ReadOnlyMemory<ushort> Pixels, int Width, int Height, DateTimeOffset CapturedAt);

/// <summary>
/// §59 — the capture seam the autofocus sweep measures through. Implemented by
/// <see cref="CameraService"/> so probe exposures run the IDENTICAL device path (settings →
/// expose → ImageReady poll → download → pixel conversion) and the SAME in-flight gate as real
/// captures — an AF probe can never fight a manual snapshot or a sequence exposure for the camera.
/// </summary>
public interface IAnalysisFrameSource {

    /// <summary>
    /// Capture one analysis frame of <paramref name="exposureSec"/> at <paramref name="binning"/>.
    /// Waits (sequencer-style) for the shared capture gate; throws when the camera is not
    /// connected, disconnects mid-wait, or the exposure fails — a missing probe measurement must
    /// fail the sweep step loudly, never yield a silent gap. Cancellation aborts the exposure and
    /// propagates.
    /// </summary>
    Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct);
}

public sealed partial class CameraService : IAnalysisFrameSource {

    /// <inheritdoc/>
    public async Task<AnalysisFrame> CaptureForAnalysisAsync(double exposureSec, int binning, CancellationToken ct) {
        AlpacaCamera? client;
        CameraCapabilitiesDto? caps;
        lock (_gate) {
            client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
            caps = _capabilities;
        }
        // Same validation contract as StartExposureAsync (argument range before the connected
        // check; a zero caps max means the capability read failed and validation defers to the
        // device). The §59 sweep feeds COMPUTED values through this seam — the caller class most
        // likely to produce NaN from bad math or an absurd binning from a bug — and both would
        // otherwise fail SILENTLY: NaN sails past a bare `<= 0` (NaN comparisons are always
        // false), and an over-short binning wraps in ApplyExposureSettings' narrowing cast where
        // TrySet logs-and-skips. A silently mis-set probe corrupts the focus curve.
        if (exposureSec <= 0 || double.IsNaN(exposureSec) || double.IsInfinity(exposureSec)
            || (caps is not null && caps.MaxExposureSec > 0
                && (exposureSec < caps.MinExposureSec || exposureSec > caps.MaxExposureSec))) {
            throw new ArgumentOutOfRangeException(nameof(exposureSec), exposureSec,
                "analysis exposure must be a positive, finite number of seconds within the camera's supported range");
        }
        if (binning < 1 || binning > short.MaxValue
            || (caps is not null && caps.MaxBinX > 0 && caps.MaxBinY > 0
                && (binning > caps.MaxBinX || binning > caps.MaxBinY))) {
            throw new ArgumentOutOfRangeException(nameof(binning), binning,
                "analysis binning is outside the camera's supported range");
        }
        var bin = binning;
        if (client is null) {
            throw new InvalidOperationException("camera is not connected");
        }

        // Full-sensor frame at the requested binning; gain/offset stay at the camera's current
        // values (an AF probe measures relative HFR across positions — consistency between probes
        // matters, absolute calibration does not).
        var request = new ExposureRequestDto(
            ExposureSec: exposureSec,
            Gain: null,
            BinX: bin,
            BinY: bin,
            FilterName: null,
            CameraOffset: null);

        // Same gate discipline as the sequencer capture path (CaptureImage): WAIT for an in-flight
        // capture rather than fail — an AF sweep runs inside sequences and must queue behind a
        // manual snapshot, not abort the run. Same leak ceiling + message too.
        var gateDeadline = DateTimeOffset.UtcNow + CaptureGateMaxWait;
        while (Interlocked.CompareExchange(ref _captureInFlight, 1, 0) != 0) {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= gateDeadline) {
                throw new TimeoutException(
                    $"timed out after {CaptureGateMaxWait.TotalMinutes:0} min waiting for an in-progress capture to release the camera. Likely a manual capture running a very long exposure or a download stalled on a slow bridge; check the daemon log for an active capture. If none is running the in-flight gate is stuck and the daemon must be restarted to clear it — a reconnect does not reset the gate.");
            }
            await Task.Delay(CaptureGatePollInterval, ct).ConfigureAwait(false);
        }
        try {
            // Re-snapshot under the gate — a disconnect/reconnect during the queue wait can
            // supersede the client grabbed above (same rationale as CaptureImage).
            lock (_gate) {
                client = !_disposed && _state == EquipmentConnectionState.Connected ? _client : null;
            }
            if (client is null) {
                throw new InvalidOperationException("camera disconnected while the analysis capture was queued");
            }
            var frameId = Guid.NewGuid(); // log correlation only — nothing is persisted under it
            var exposed = await ExposeAndDownloadAsync(client, frameId, request, ct).ConfigureAwait(false);
            if (exposed is null) {
                throw new InvalidOperationException(
                    "analysis capture failed — see the daemon log for the cause (device timeout or disconnect)");
            }
            var (pixels, width, height, capturedAt) = exposed.Value;
            LogAnalysisCaptureComplete(frameId, width, height, exposureSec);
            return new AnalysisFrame(pixels, width, height, capturedAt);
        } finally {
            Interlocked.Exchange(ref _captureInFlight, 0);
            RefreshCacheOnce();
        }
    }

    [Microsoft.Extensions.Logging.LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Information,
        Message = "Analysis capture {FrameId} complete: {Width}x{Height} at {ExposureSec}s (not persisted)")]
    private partial void LogAnalysisCaptureComplete(Guid frameId, int width, int height, double exposureSec);
}
