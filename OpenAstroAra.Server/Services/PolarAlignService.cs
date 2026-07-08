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
    public sealed partial class PolarAlignService : IPolarAlignService {

        private readonly GuiderService _guider;
        private readonly ILogger<PolarAlignService> _logger;
        private readonly IWsBroadcaster? _ws;
        private readonly object _gate = new();

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
        /// same client error the guide ops return.
        /// </summary>
        [SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Rollback-and-rethrow: any failure after the state reservation (not-connected, lease rejection, cancellation, socket fault) must undo the reservation and propagate — nothing is swallowed.")]
        public async Task<OperationAcceptedDto> StartAsync(string? idempotencyKey, CancellationToken ct) {
            // Reserve the active state under a single lock BEFORE any await, so two near-simultaneous Starts
            // can't both observe _active==false and both acquire the lease + publish (the endpoint calls
            // straight into this singleton with no request serialization). The loser sees the reservation and
            // returns a no-op accept. This mirrors GuiderService.ConnectCoreAsync (check + transition in one
            // lock before the await). The preflight + lease acquire run AFTER the lock is released (so we
            // don't hold _gate across GuiderService's own lock); a failure there rolls the reservation back.
            lock (_gate) {
                if (_active) {
                    return Accepted("polar-align.start", idempotencyKey);
                }
                _active = true;
                _state = "capturing";
                _framesCaptured = 0;
                _lastFrameId = null;
            }
            try {
                // Preflight: the routine needs a connected guider to hold the camera + capture. Reuses the
                // guide ops' connected-or-throw contract, so a not-connected Start fails the same way.
                var guiderClient = _guider.RequireConnectedGuider();
                await guiderClient.SetPaSessionAsync(active: true, timeoutS: LeaseTimeoutSeconds, ct).ConfigureAwait(false);
            } catch {
                // Preflight/lease acquire failed — undo the reservation so a retry can start cleanly.
                lock (_gate) {
                    _active = false;
                    _state = "idle";
                }
                throw;
            }
            LogStarted();
            await PublishStateEventAsync(WsEventCatalog.PolarAlignStarted, "capturing").ConfigureAwait(false);
            return Accepted("polar-align.start", idempotencyKey);
        }

        /// <summary>
        /// Stop the routine: release the PA-session lease (best-effort — a disconnected guider has nothing to
        /// release, and the lease auto-expires regardless) and return to the stopped state. Idempotent.
        /// </summary>
        public async Task<OperationAcceptedDto> StopAsync(string? idempotencyKey, CancellationToken ct) {
            lock (_gate) {
                _active = false;
                _state = "stopped";
            }
            await ReleaseLeaseBestEffortAsync(ct).ConfigureAwait(false);
            LogStopped();
            await PublishStateEventAsync(WsEventCatalog.PolarAlignStopped, "stopped").ConfigureAwait(false);
            return Accepted("polar-align.stop", idempotencyKey);
        }

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
