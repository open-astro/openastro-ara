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
using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Profile.Interfaces;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §63 guider-a — the first real guider service. Replaces <c>PlaceholderGuiderService</c> by driving
/// the re-ported <see cref="PHD2Guider"/> (PHD2's JSON-RPC client) over the §6 REST surface.
///
/// Unlike the Alpaca device services, PHD2 is its own protocol: there is no discovery handshake —
/// the client connects to a host/port (default <c>localhost:4400</c>) carried by the profile
/// (§63.5). <see cref="ConnectAsync"/> pushes the request's host/port into the active profile's
/// guider settings, then constructs a fresh <see cref="PHD2Guider"/> and connects on a background
/// task per the §60.5 202-Accepted contract; callers poll <see cref="GetAsync"/> for the outcome.
///
/// No §32.4 cache loop is needed: <see cref="PHD2Guider"/> maintains its <c>State</c> live from its
/// own background JSON-RPC listener, so <see cref="GetAsync"/> reads it directly.
///
/// RMS (<c>RmsTotal/Ra/Dec</c>) is left null for now — TODO(§63): accumulate it from the guider's
/// <c>GuideEvent</c> stream (guider-b). Mediator unification (<c>IGuiderMediator</c> so the sequencer
/// drives the live guider) is a separate follow-up that replaces <c>HeadlessGuiderMediator</c>.
/// </summary>
public sealed partial class GuiderService : IGuiderService, IDisposable {

    private readonly ILogger<GuiderService> _logger;
    private readonly IProfileService _profileService;
    private readonly object _gate = new();
    // A no-op progress sink: the headless daemon reports guider progress to clients over the §60.9 WS
    // stream, not through NINA's in-process IProgress reporter.
    private readonly IProgress<ApplicationStatus> _noProgress = new Progress<ApplicationStatus>();

    private PHD2Guider? _guider;
    private EquipmentConnectionState _state = EquipmentConnectionState.Disconnected;
    private long _connectGeneration; // this attempt's id; later attempts/disconnects bump it to supersede
    private bool _disposed;

    public GuiderService(IProfileService profileService, ILogger<GuiderService> logger) {
        _profileService = profileService ?? throw new ArgumentNullException(nameof(profileService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<GuiderDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_guider is null) {
                return Task.FromResult<GuiderDto?>(null);
            }
            var runtime = new GuiderStateDto(
                State: MapGuidingState(_guider.State),
                RmsTotal: null, // TODO(§63): accumulate from GuideEvent (guider-b)
                RmsRa: null,
                RmsDec: null,
                CurrentProfile: _guider.SelectedProfile?.Name);
            return Task.FromResult<GuiderDto?>(new GuiderDto("PHD2_Single", "PHD2", _state, runtime));
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(GuiderConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        long generation;
        PHD2Guider guider;
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Idempotent: connecting while already connecting/connected is a no-op accept (§60.5).
            if (_state is EquipmentConnectionState.Connecting or EquipmentConnectionState.Connected) {
                return Task.FromResult(Accepted("guider.connect", idempotencyKey));
            }
            // PHD2Guider reads host/port from the profile (§63.5), so honor the request by writing
            // them in before the connect.
            var settings = _profileService.ActiveProfile.GuiderSettings;
            settings.PHD2ServerHost = request.Host;
            settings.PHD2ServerPort = request.Port;
            DisposeGuiderLocked();
            guider = new PHD2Guider(_profileService);
            _guider = guider;
            generation = ++_connectGeneration;
            SetStateLocked(EquipmentConnectionState.Connecting);
        }
        // 202 contract: do the blocking connect off-thread; GetAsync reports the outcome.
        _ = Task.Run(() => ConnectInBackground(guider, generation), CancellationToken.None);
        return Task.FromResult(Accepted("guider.connect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ++_connectGeneration; // supersede any in-flight connect
            DisposeGuiderLocked();
            SetStateLocked(EquipmentConnectionState.Disconnected);
        }
        return Task.FromResult(Accepted("guider.disconnect", idempotencyKey));
    }

    public Task<OperationAcceptedDto> StartGuidingAsync(string? idempotencyKey, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        _ = Task.Run(() => guider.StartGuiding(forceCalibration: false, _noProgress, CancellationToken.None), CancellationToken.None);
        return Task.FromResult(Accepted("guider.start", idempotencyKey));
    }

    public Task<OperationAcceptedDto> StopGuidingAsync(string? idempotencyKey, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        _ = Task.Run(() => guider.StopGuiding(CancellationToken.None), CancellationToken.None);
        return Task.FromResult(Accepted("guider.stop", idempotencyKey));
    }

    public Task<OperationAcceptedDto> DitherAsync(double pixels, string? idempotencyKey, CancellationToken ct) {
        var guider = RequireConnectedGuider();
        // PHD2Guider.Dither reads the amplitude from the profile; honor the request's pixels.
        _profileService.ActiveProfile.GuiderSettings.DitherPixels = pixels;
        _ = Task.Run(() => guider.Dither(_noProgress, CancellationToken.None), CancellationToken.None);
        return Task.FromResult(Accepted("guider.dither", idempotencyKey));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background connect boundary: PHD2Guider.Connect can throw arbitrary socket/IO/protocol exceptions; any escape must surface as the Error state and be contained. Log-and-recover.")]
    private async Task ConnectInBackground(PHD2Guider guider, long generation) {
        try {
            var ok = await guider.Connect(CancellationToken.None).ConfigureAwait(false);
            lock (_gate) {
                if (generation != _connectGeneration) {
                    return; // superseded by a newer connect/disconnect
                }
                SetStateLocked(ok ? EquipmentConnectionState.Connected : EquipmentConnectionState.Error);
            }
        } catch (Exception ex) {
            LogConnectFailed(ex);
            lock (_gate) {
                if (generation == _connectGeneration) {
                    SetStateLocked(EquipmentConnectionState.Error);
                }
            }
        }
    }

    private PHD2Guider RequireConnectedGuider() {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_guider is null || _state != EquipmentConnectionState.Connected) {
                throw new InvalidOperationException("guider is not connected");
            }
            return _guider;
        }
    }

    // NINA PhdAppState → §63.2 GuiderStateDto.State token. Selected/Looping/unknown collapse to
    // "stopped"; "dithering" arrives via the GuideEvent settling path (not modeled until RMS lands).
    private static string MapGuidingState(string? phdState) => phdState switch {
        "Calibrating" => "calibrating",
        "Guiding" => "guiding",
        "Paused" => "paused",
        "LostLock" => "star_lost",
        _ => "stopped",
    };

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    private void SetStateLocked(EquipmentConnectionState state) => _state = state;

    private void DisposeGuiderLocked() {
        _guider?.Disconnect();
        _guider?.Dispose();
        _guider = null;
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
            _disposed = true;
            DisposeGuiderLocked();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "PHD2 guider connect failed")]
    partial void LogConnectFailed(Exception ex);
}
