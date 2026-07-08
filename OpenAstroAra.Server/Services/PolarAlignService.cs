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
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services {

    /// <summary>
    /// §45 — the real polar-alignment service (skeleton), replacing <c>PlaceholderPolarAlignService</c>.
    /// ARA owns the iPolar-style routine: the guider is only a capture + centroid + lease source, and ARA
    /// runs the plate solver (§18 <c>IPlateSolveService</c>), the <c>PolarAlignGeometry</c> two-point fit,
    /// the mount slews, and this state machine (see PORT_PLAYBOOK.md §45; guider POLAR_ALIGNMENT_DESIGN.md
    /// §4 "ARA owns the routine"). This slice is the lifecycle shell: <c>Start</c> acquires the single-client
    /// guide-camera PA-session lease so nothing else fights ARA for the camera, <c>Stop</c> releases it, and
    /// both publish the <c>polar_align.started</c>/<c>stopped</c> WS events. The capture→solve→slew loop that
    /// drives the <c>capturing</c>/<c>solving</c>/<c>solved</c>/<c>unsolved</c> states and populates the
    /// alt/az error is the next slice; here the active state is reported as <c>capturing</c> (the routine's
    /// first action) and no frames are captured yet.
    /// </summary>
    public sealed partial class PolarAlignService : IPolarAlignService, IDisposable {

        private readonly GuiderService _guider;
        private readonly ILogger<PolarAlignService> _logger;
        private readonly IWsBroadcaster? _ws;
        private readonly object _gate = new();

        // Serializes the whole Start/Stop lifecycle (including the lease RPC), so a Start and a concurrent
        // Stop can't race their set_pa_session calls on the wire and leave the daemon holding the lease while
        // the service reports "stopped". The endpoints call straight into this singleton with no request
        // serialization, so a double-click / retried-after-timeout Start+Stop is plausible. Only one lease
        // transition runs at a time; _gate still guards the short state reads/writes. (A plain lock can't span
        // the awaited RPC.)
        private readonly SemaphoreSlim _opLock = new(1, 1);

        // The lease is a courtesy hold on the single-client guide camera; the daemon default is 600 s and it
        // auto-expires so a crashed orchestrator can't wedge it. A renew watchdog for long sessions is a
        // later slice — the skeleton acquires once on Start.
        private const int LeaseTimeoutSeconds = 600;

        private bool _active;
        private string _state = "idle";
        private int _framesCaptured;
        private string? _lastFrameId;

        public PolarAlignService(GuiderService guider, ILogger<PolarAlignService> logger, IWsBroadcaster? ws = null) {
            _guider = guider;
            _logger = logger;
            _ws = ws;
        }

        public Task<PolarAlignStateDto> GetStatusAsync(CancellationToken ct) {
            lock (_gate) {
                return Task.FromResult(new PolarAlignStateDto(
                    State: _state,
                    CurrentErrorArcmin: null,
                    AzimuthAdjustmentArcmin: null,
                    AltitudeAdjustmentArcmin: null,
                    FramesCaptured: _framesCaptured,
                    LastFrameId: _lastFrameId));
            }
        }

        /// <summary>
        /// Begin the polar-alignment routine: acquire the guide-camera PA-session lease and enter the active
        /// state. Idempotent — a second Start on an already-running routine is a no-op accept (the lease
        /// renewal for long sessions is a later slice). Throws <see cref="InvalidOperationException"/> (via
        /// <see cref="GuiderService.RequireConnectedGuider"/>) when no guider is connected, mapped to the
        /// same client error the guide ops return. Serialized against <see cref="StopAsync"/> via
        /// <c>_opLock</c> so the two can't reorder their lease RPCs on the wire.
        /// </summary>
        public async Task<OperationAcceptedDto> StartAsync(string? idempotencyKey, CancellationToken ct) {
            await _opLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                lock (_gate) {
                    if (_active) {
                        return Accepted("polar-align.start", idempotencyKey);
                    }
                }
                // Preflight: the routine needs a connected guider to hold the camera + capture. Reuses the
                // guide ops' connected-or-throw contract, so a not-connected Start fails the same way.
                // Acquire the lease BEFORE recording active state, so a failed acquire (not-connected, daemon
                // rejection) leaves the state untouched — no reservation to roll back. Serialization by
                // _opLock means no concurrent Stop can interleave between the acquire and the state write.
                var guiderClient = _guider.RequireConnectedGuider();
                await guiderClient.SetPaSessionAsync(active: true, timeoutS: LeaseTimeoutSeconds, ct).ConfigureAwait(false);
                lock (_gate) {
                    _active = true;
                    _state = "capturing";
                    _framesCaptured = 0;
                    _lastFrameId = null;
                }
                LogStarted();
                await PublishStateEventAsync(WsEventCatalog.PolarAlignStarted, "capturing").ConfigureAwait(false);
                return Accepted("polar-align.start", idempotencyKey);
            } finally {
                _opLock.Release();
            }
        }

        /// <summary>
        /// Stop the routine: release the PA-session lease (best-effort — a disconnected guider has nothing to
        /// release, and the lease auto-expires regardless) and return to the stopped state. Idempotent, and
        /// serialized against <see cref="StartAsync"/> via <c>_opLock</c> — a Stop issued while a Start is
        /// still acquiring the lease waits for it, then releases, so the final lease state always matches the
        /// reported state.
        /// </summary>
        public async Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct) {
            await _opLock.WaitAsync(ct).ConfigureAwait(false);
            try {
                lock (_gate) {
                    _active = false;
                    _state = "stopped";
                }
                await ReleaseLeaseBestEffortAsync(ct).ConfigureAwait(false);
                LogStopped();
                await PublishStateEventAsync(WsEventCatalog.PolarAlignStopped, "stopped").ConfigureAwait(false);
                return Accepted("polar-align.stop", idempotencyKey);
            } finally {
                _opLock.Release();
            }
        }

        public void Dispose() => _opLock.Dispose();

        // Releasing the lease must not fail Stop: the guider may already be disconnected (nothing to release,
        // and the lease auto-expires), or the daemon may reject the call. Log-and-recover either way.
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Lease release is best-effort: Stop must always succeed and the lease auto-expires. Log-and-recover boundary.")]
        private async Task ReleaseLeaseBestEffortAsync(CancellationToken ct) {
            PHD2Guider guiderClient;
            try {
                guiderClient = _guider.RequireConnectedGuider();
            } catch (InvalidOperationException) {
                // No connected guider — nothing to release.
                return;
            }
            try {
                await guiderClient.SetPaSessionAsync(active: false, timeoutS: null, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                LogLeaseReleaseFailed(ex);
            }
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "WS publish is best-effort; a broadcaster fault must not abort the lifecycle transition. Log-and-recover boundary.")]
        private async Task PublishStateEventAsync(string eventType, string state) {
            if (_ws is null) {
                return;
            }
            try {
                var payload = new JsonObject { ["state"] = state };
                using var doc = JsonDocument.Parse(payload.ToJsonString());
                await _ws.PublishAsync(eventType, doc.RootElement.Clone(), CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                LogWsPublishFailed(ex);
            }
        }

        private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
            new(OperationId: Guid.NewGuid(),
                OperationType: operationType,
                AcceptedUtc: DateTimeOffset.UtcNow,
                IdempotencyKey: idempotencyKey);

        [LoggerMessage(EventId = 4510, Level = LogLevel.Information, Message = "§45 polar-align routine started (PA-session lease acquired)")]
        private partial void LogStarted();

        [LoggerMessage(EventId = 4511, Level = LogLevel.Information, Message = "§45 polar-align routine stopped (PA-session lease released)")]
        private partial void LogStopped();

        [LoggerMessage(EventId = 4512, Level = LogLevel.Warning, Message = "§45 PA-session lease release failed (best-effort; the lease auto-expires)")]
        private partial void LogLeaseReleaseFailed(Exception exception);

        [LoggerMessage(EventId = 4513, Level = LogLevel.Warning, Message = "§45 WS publish of the polar-align state failed (best-effort)")]
        private partial void LogWsPublishFailed(Exception exception);
    }
}
