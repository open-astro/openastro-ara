#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Common.DeviceInterfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — eighth real Alpaca-backed device service:
/// connects to a discovered Alpaca Dome, serves its live runtime (state + azimuth + shutter/home/park
/// flags) over the §6 REST surface, and drives it via four §60.5 202-Accepted background operations —
/// <see cref="SlewAsync"/>, <see cref="ParkAsync"/>, <see cref="OpenShutterAsync"/>,
/// <see cref="CloseShutterAsync"/>. Its lifecycle (generation supersede, §32.4 background-refresh
/// cache, AbortSlew-before-disconnect) is the shared <see cref="DeviceLifecycle{TDevice}"/> (ADR 0011). REST-only — the
/// <c>IDomeMediator</c> unification is the follow-up.
/// </summary>
public sealed partial class DomeService : IDomeService, IRetainedDeviceSource, IRecoverableDevice, IDisposable {

    private static readonly DomeStateDto IdleRuntime = new("idle", null, false, false, false);

    private readonly ILogger<DomeService> _logger;
    private readonly IEquipmentFaultSink? _faults;
    // ADR 0011 — connect, disconnect, forget, refresh, probe, read health and the desired state.
    private readonly DeviceLifecycle<IDomeDevice> _lifecycle;
    private Func<DiscoveredDeviceDto, IDomeDevice> _createDevice = AlpacaDomeDevice.Create;
    // Guarded by the lifecycle's gate, like the connection state.
    private DomeStateDto _runtime = IdleRuntime;
    // Read-once capabilities + the raw ASCOM shutter status, cached for the IDomeMediator GetInfo()
    // snapshot (the REST DomeDto doesn't expose them). Populated by the §32.4 refresh; reset on adopt.
    private DomeCaps? _domeCaps;
    // Error maps to NINA ShutterError ("not yet read") so GetInfo honestly signals "unknown" in the
    // ≤2s window between connect and the first refresh, rather than a possibly-false ShutterClosed.
    private ShutterState _shutterStatusRaw = ShutterState.Error;

    public DomeService(ILogger<DomeService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null) {
        _logger = logger ?? NullLogger<DomeService>.Instance;
        _faults = faults;
        _lifecycle = new DeviceLifecycle<IDomeDevice>(this, DeviceType.Dome, "dome",
            d => _createDevice(d), ReadRuntime, ResetSessionLocked,
            DeviceHealthTracker.DefaultThreshold, _logger, events, faults);
    }

    // #1689 — the follow-the-mount service; null until the composition root attaches it. A manual dome move
    // from any client turns following off through it.
    private DomeFollower? _follower;

    internal void AttachFollower(DomeFollower follower) => _follower = follower;

    private void StopFollowing(string reason) => _follower?.TurnOff(reason);

    public async Task<bool> SetFollowingAsync(bool enabled, CancellationToken ct) {
        if (_follower is not { } follower) {
            throw new InvalidOperationException("Following the mount is not available.");
        }
        if (enabled) {
            await follower.Start().ConfigureAwait(false);
        } else {
            follower.TurnOff("turned off");
        }
        return follower.IsFollowing;
    }

    private object _gate => _lifecycle.Gate;

    // #1265 — bench-test knob: re-arms the refresh timer at a shorter cadence. Init-only, never used by the daemon.
    internal TimeSpan RefreshPeriod { init => _lifecycle.RefreshPeriod = value; }

    // Test seam (ADR 0011 rule 4): the adapter the lifecycle builds for a selected device.
    internal Func<DiscoveredDeviceDto, IDomeDevice> DeviceFactory { init => _createDevice = value; }

    // #1127 — the record the card names. The remembered-selection store and this can diverge
    // (a store-only DELETE /remembered keeps the device here), so the reconnector falls back to it.
    public DiscoveredDeviceDto? RetainedDevice => _lifecycle.RetainedDevice;

    public DesiredConnection? DesiredConnection => _lifecycle.Desired;

    public bool TryRecover(DiscoveredDeviceDto device) => _lifecycle.TryRecover(device);

    public Task<DomeDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            // null ONLY when no device has ever been selected (-> 404). After a disconnect the
            // device is retained and the DTO is returned with Disconnected state + idle runtime,
            // consistent across all the device services.
            var device = _lifecycle.Device;
            if (device is null) {
                return Task.FromResult<DomeDto?>(null);
            }
            var state = _lifecycle.State;
            var runtime = state == EquipmentConnectionState.Connected ? _runtime : IdleRuntime;
            // Surface the read-once capabilities so the client can enable only the
            // controls the dome supports (shutter / azimuth-slew / park / home).
            var caps = state == EquipmentConnectionState.Connected && _domeCaps is { } c
                ? new DomeCapabilitiesDto(
                    c.CanSetShutter, c.CanSetAzimuth, c.CanSyncAzimuth, c.CanPark, c.CanFindHome, c.CanSetPark)
                : null;
            return Task.FromResult<DomeDto?>(
                new DomeDto(device.UniqueId, device.Name, state, caps,
                    state == EquipmentConnectionState.Connected && _follower is { IsFollowing: true } ? runtime with { Following = true } : runtime));
        }
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device);
        return _lifecycle.ConnectAsync(request.Device, idempotencyKey);
    }

    public Task<bool> ForgetAsync(CancellationToken ct) => _lifecycle.ForgetAsync();

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) {
        StopFollowing("the dome was disconnected");
        return _lifecycle.DisconnectAsync(idempotencyKey);
    }

    // Caller holds the gate: a fresh, ended or tripped session forgets the last one's reads.
    private void ResetSessionLocked() {
        _runtime = IdleRuntime;                  // don't serve a prior device's runtime
        _domeCaps = null;                        // re-read capabilities for the new device
        _shutterStatusRaw = ShutterState.Error; // "not yet read" until the first refresh, not a false Closed
    }

    public Task<OperationAcceptedDto> SlewAsync(DomeSlewRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        // Dispose check first (a disposed service can't be operated regardless of the argument),
        // then argument range, then connection state — aligned with the other device services.
        var client = _lifecycle.ConnectedDevice();
        if (IsAzimuthOutOfRange(request.TargetAzimuthDeg)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.TargetAzimuthDeg,
                "TargetAzimuthDeg must be in [0, 360).");
        }
        if (client is null) {
            throw new InvalidOperationException("dome is not connected");
        }
        var target = request.TargetAzimuthDeg;
        StopFollowing("the dome was turned by hand");
        RunControl("dome.slew", client, c => c.SlewToAzimuth(target));
        return Task.FromResult(Accepted("dome.slew", idempotencyKey));
    }

    public Task<OperationAcceptedDto> ParkAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        StopFollowing("the dome was parked");
        RunControl("dome.park", client, c => c.Park());
        return Task.FromResult(Accepted("dome.park", idempotencyKey));
    }

    public Task<OperationAcceptedDto> OpenShutterAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        RunControl("dome.shutter.open", client, c => c.OpenShutter());
        return Task.FromResult(Accepted("dome.shutter.open", idempotencyKey));
    }

    public Task<OperationAcceptedDto> CloseShutterAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        RunControl("dome.shutter.close", client, c => c.CloseShutter());
        return Task.FromResult(Accepted("dome.shutter.close", idempotencyKey));
    }

    // §25.5.5 — the remaining dome motions. FindHome is a long motion like Slew/Park, so it runs
    // through the same fire-and-forget RunControl (202 semantics, cache refresh on completion).
    public Task<OperationAcceptedDto> FindHomeAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        StopFollowing("the dome was sent home");
        RunControl("dome.findhome", client, c => c.FindHome());
        return Task.FromResult(Accepted("dome.findhome", idempotencyKey));
    }

    // §57 panic-stop shape (mirrors TelescopeService.AbortSlewAsync): issue AbortSlew NOW and await
    // it — not fire-and-forget — so the 202 means the stop was actually sent. CancellationToken.None
    // is critical: Task.Run(lambda, ct) returns a pre-cancelled task WITHOUT running the lambda if
    // ct is already cancelled, so an HTTP-timeout that cancelled the token would silently never send
    // the abort and the dome would keep rotating.
    public async Task<OperationAcceptedDto> AbortSlewAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        StopFollowing("the dome was stopped");
        await Task.Run(() => client.AbortSlew(), CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Refresh();
        return Accepted("dome.abort", idempotencyKey);
    }

    // SetPark is a prompt register write (no motion) — await it so a driver rejection surfaces as
    // the request's error instead of a silent background log line.
    public async Task<OperationAcceptedDto> SetParkAsync(string? idempotencyKey, CancellationToken ct) {
        var client = RequireConnectedClient();
        await Task.Run(() => client.SetPark(), CancellationToken.None).ConfigureAwait(false);
        return Accepted("dome.setpark", idempotencyKey);
    }

    // SyncToAzimuth re-labels the current position (no motion) — prompt write, same validation
    // range as Slew.
    public async Task<OperationAcceptedDto> SyncToAzimuthAsync(DomeSlewRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        if (IsAzimuthOutOfRange(request.TargetAzimuthDeg)) {
            throw new ArgumentOutOfRangeException(nameof(request), request.TargetAzimuthDeg,
                "TargetAzimuthDeg must be in [0, 360).");
        }
        var client = RequireConnectedClient();
        var target = request.TargetAzimuthDeg;
        await Task.Run(() => client.SyncToAzimuth(target), CancellationToken.None).ConfigureAwait(false);
        _lifecycle.Refresh();
        return Accepted("dome.sync", idempotencyKey);
    }

    private IDomeDevice RequireConnectedClient() {
        var client = _lifecycle.ConnectedDevice();
        return client ?? throw new InvalidOperationException("dome is not connected");
    }

    // Shared launcher for the four control ops: fire-and-forget the blocking ASCOM call on the
    // captured client, then refresh the cache so the read-back reflects the new motion immediately.
    private void RunControl(string op, IDomeDevice client, Action<IDomeDevice> action) {
        _ = Task.Run(() => RunControlInBackground(op, client, action), CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background control boundary: the blocking ASCOM SlewToAzimuth/Park/OpenShutter/CloseShutter can throw arbitrary driver/HTTP exceptions, and a concurrent Disconnect/Dispose can dispose the captured client mid-call; any escape must be contained and logged, never fault the fire-and-forget task or the daemon. CA1031's log-and-recover boundary applies.")]
    private void RunControlInBackground(string op, IDomeDevice client, Action<IDomeDevice> action) {
        try {
            action(client);
            _lifecycle.Refresh();
        } catch (Exception ex) {
            LogControlFailed(ex, op);
        }
    }

    // The runtime read the lifecycle runs each tick; Slewing is the essential read (§42.3 second
    // channel). Capabilities are read once per session, on the first tick that finds them missing.
    private DeviceRead ReadRuntime(IDomeDevice client) {
        bool needCaps;
        lock (_gate) {
            needCaps = _domeCaps is null;
        }
        var (runtime, rawShutter) = ReadRuntime(client, out var readError);
        var caps = needCaps ? ReadCaps(client) : (DomeCaps?)null;
        return new DeviceRead(readError, CommitLocked: () => {
            _runtime = runtime;
            _shutterStatusRaw = rawShutter;
            if (caps is not null) {
                _domeCaps = caps;
            }
        });
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported capability property throws; each flag falls back to false rather than failing the whole capability read. Capabilities are static, so this runs once per connection. CA1031's log-and-recover boundary applies.")]
    private static DomeCaps ReadCaps(IDomeDevice c) {
        bool canSetShutter;
        try { canSetShutter = c.CanSetShutter; } catch (Exception) { canSetShutter = false; }
        bool canSetAzimuth;
        try { canSetAzimuth = c.CanSetAzimuth; } catch (Exception) { canSetAzimuth = false; }
        bool canSyncAzimuth;
        try { canSyncAzimuth = c.CanSyncAzimuth; } catch (Exception) { canSyncAzimuth = false; }
        bool canPark;
        try { canPark = c.CanPark; } catch (Exception) { canPark = false; }
        bool canFindHome;
        try { canFindHome = c.CanFindHome; } catch (Exception) { canFindHome = false; }
        bool canSetPark;
        try { canSetPark = c.CanSetPark; } catch (Exception) { canSetPark = false; }
        return new DomeCaps(canSetShutter, canSetAzimuth, canSyncAzimuth, canPark, canFindHome, canSetPark);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-field read boundary: an unsupported/transiently-failing dome property throws; that field falls back to its default rather than failing the whole runtime read. CA1031's log-and-recover boundary applies.")]
    private static (DomeStateDto Runtime, ShutterState RawShutter) ReadRuntime(IDomeDevice c, out string? readError) {
        readError = null;
        bool slewing;
        // Slewing is the essential read (every ASCOM dome must implement it): a streak of failures
        // is a degraded fault (§42.3 second channel).
        try { slewing = c.Slewing; }
        catch (ASCOM.NotImplementedException) { slewing = false; } // not a fault: the driver has no Slewing
        catch (Exception ex) { slewing = false; readError = ex.Message; }
        double? azimuth;
        try { azimuth = c.Azimuth; } catch (Exception) { azimuth = null; }
        ShutterState shutter;
        try { shutter = c.ShutterStatus; } catch (Exception) { shutter = ShutterState.Error; }
        bool atHome;
        try { atHome = c.AtHome; } catch (Exception) { atHome = false; }
        bool parked;
        try { parked = c.AtPark; } catch (Exception) { parked = false; }

        var shutterOpen = shutter == ShutterState.Open;
        // State precedence: a shutter fault wins over everything so a caller polling for error
        // recovery sees it even mid-slew (the shutter and azimuth drive are independent mechanisms —
        // a jam can co-occur with rotation; azimuth still updates, so motion stays observable). Then
        // motion (slew / shutter transit), then the resting flags.
        var state = shutter == ShutterState.Error ? "error"
            : slewing ? "slewing"
            : (shutter is ShutterState.Opening or ShutterState.Closing) ? "shutter_moving"
            : parked ? "parked"
            : shutterOpen ? "shutter_open"
            : "idle";
        return (new DomeStateDto(state, azimuth, shutterOpen, atHome, parked), shutter);
    }

    private static OperationAcceptedDto Accepted(string operationType, string? idempotencyKey) =>
        new(OperationId: Guid.NewGuid(),
            OperationType: operationType,
            AcceptedUtc: DateTimeOffset.UtcNow,
            IdempotencyKey: idempotencyKey);

    // Extracted (internal) for direct unit testing. ASCOM dome azimuth is degrees in [0, 360).
    internal static bool IsAzimuthOutOfRange(double azimuthDeg) => azimuthDeg < 0 || azimuthDeg >= 360;

    public void Dispose() {
        _lifecycle.Dispose();
        GC.SuppressFinalize(this);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dome control op {Op} failed")]
    private partial void LogControlFailed(Exception ex, string op);
}
