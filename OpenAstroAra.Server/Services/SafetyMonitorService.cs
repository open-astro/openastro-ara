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
using OpenAstroAra.Server.Contracts;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// First real Alpaca-backed equipment service (§14e): connects to a discovered Alpaca
/// SafetyMonitor (the host/port/device-number carried by the
/// <see cref="DiscoveredDeviceDto"/> from <see cref="AlpacaEquipmentDiscoveryService"/>)
/// and exposes its live connection state + <c>IsSafe</c> over the §6 REST surface.
///
/// Connection lifecycle follows the §60.5 202-Accepted contract: <see cref="ConnectAsync"/>
/// transitions to <see cref="EquipmentConnectionState.Connecting"/> and does the actual
/// Alpaca connect on a background task, so the endpoint returns immediately; callers poll
/// <see cref="GetAsync"/> to observe <see cref="EquipmentConnectionState.Connected"/> /
/// <see cref="EquipmentConnectionState.Error"/>. That lifecycle is the shared
/// <see cref="DeviceLifecycle{TDevice}"/> (ADR 0011).
///
/// §32.4 cache: the lifecycle's refresh reads the (blocking HTTP) <c>IsSafe</c> every
/// refresh interval while Connected and this class caches it, so <see cref="GetAsync"/> and the
/// mediator's <c>GetInfo</c> serve the value synchronously without a per-poll HTTP read (no
/// thread-per-poll accumulation under aggressive polling).
///
/// ADR 0011 rule 3 — the failed-read threshold is one: the first failed <c>IsSafe</c> read or connection
/// probe trips the monitor to Error and reports it as not safe, and no safe verdict stands after either.
///
/// This class also serves the Sequencer's <c>ISafetyMonitorMediator</c> (so <c>WaitUntilSafe</c>
/// reads the live device) — that surface lives in the <c>SafetyMonitorService.Mediator.cs</c>
/// partial; one singleton is registered for both interfaces per playbook §8.1.
/// </summary>
public sealed partial class SafetyMonitorService : ISafetyMonitorService, IRetainedDeviceSource, IRecoverableDevice, IDisposable {

    private readonly ILogger<SafetyMonitorService> _logger;
    // ADR 0011 — connect, disconnect, forget, refresh, probe, read health and the desired state.
    private readonly DeviceLifecycle<ISafetyMonitorDevice> _lifecycle;
    private Func<DiscoveredDeviceDto, ISafetyMonitorDevice> _createDevice = AlpacaSafetyMonitorDevice.Create;
    // Last IsSafe read from the device; refreshed by the lifecycle's tick while Connected and served
    // (synchronously) by GetAsync/GetInfo. Only meaningful while Connected; guarded by the lifecycle's gate.
    private bool _cachedSafe;

    public SafetyMonitorService(ILogger<SafetyMonitorService>? logger = null, EquipmentEventPublisher? events = null,
            IEquipmentFaultSink? faults = null) {
        _logger = logger ?? NullLogger<SafetyMonitorService>.Instance;
        // Threshold one, and a failed read trips (rule 3): the monitor goes to Error on the first failed read or probe.
        _lifecycle = new DeviceLifecycle<ISafetyMonitorDevice>(this, DeviceType.SafetyMonitor, "safety monitor",
            d => _createDevice(d), ReadRuntime, ResetSessionLocked,
            failedReadThreshold: 1, _logger, events, faults, failedReadTrips: true);
    }

    private object _gate => _lifecycle.Gate;

    // #1265 — bench-test knob: re-arms the refresh timer at a shorter cadence. Init-only, never used by the daemon.
    internal TimeSpan RefreshPeriod { init => _lifecycle.RefreshPeriod = value; }

    // Test seam (ADR 0011 rule 4): the adapter the lifecycle builds for a selected device.
    internal Func<DiscoveredDeviceDto, ISafetyMonitorDevice> DeviceFactory { init => _createDevice = value; }

    // #1127 — the record the card names. The remembered-selection store and this can diverge
    // (a store-only DELETE /remembered keeps the device here), so the reconnector falls back to it.
    public DiscoveredDeviceDto? RetainedDevice => _lifecycle.RetainedDevice;

    public DesiredConnection? DesiredConnection => _lifecycle.Desired;

    // #1246 — true once a refresh tick since the current connect read the essential read cleanly.
    internal bool HasCleanReadSinceConnect => _lifecycle.HasCleanReadSinceConnect;

    public bool TryRecover(DiscoveredDeviceDto device) => _lifecycle.TryRecover(device);

    // ct is unused by design: the value is served from the cache the lifecycle maintains
    // (§32.4), so there is no per-call HTTP read to cancel. Kept to match the IEquipmentServices
    // contract. Returns a Task to satisfy the async interface without an await.
    public Task<SafetyMonitorDto?> GetAsync(CancellationToken ct) {
        lock (_gate) {
            // All public operations throw after Dispose() (uniform with Connect/Disconnect).
            ObjectDisposedException.ThrowIf(_lifecycle.IsDisposed, this);
            var device = _lifecycle.Device;
            if (device is null) {
                // No device has ever been selected — GET maps this to 404 at the endpoint.
                return Task.FromResult<SafetyMonitorDto?>(null);
            }
            // Single consistent snapshot: device, state, cached IsSafe, and timestamp all under
            // one lock. safe is only meaningful while Connected.
            var state = _lifecycle.State;
            var safe = state == EquipmentConnectionState.Connected && _cachedSafe;
            var dto = new SafetyMonitorDto(
                device.UniqueId, device.Name, state, safe,
                _lifecycle.LastTransition.ToString("O", CultureInfo.InvariantCulture));
            return Task.FromResult<SafetyMonitorDto?>(dto);
        }
    }

    // ct is unused by design: under the §60.5 202-Accepted contract this method returns
    // immediately and the work continues on a detached background task, so there is nothing for
    // the request token to cancel. Kept in the signature to match the IEquipmentServices contract.
    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Device); // guard a null Device from a malformed body
        return _lifecycle.ConnectAsync(request.Device, idempotencyKey);
    }

    // ct is unused by design (see ConnectAsync): the 202 contract makes teardown fire-and-forget.
    public Task<bool> ForgetAsync(CancellationToken ct) => _lifecycle.ForgetAsync();

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) =>
        _lifecycle.DisconnectAsync(idempotencyKey);

    // Caller holds the gate. Clear before the device reads Connected so we don't briefly serve a PRIOR
    // device's IsSafe under the NEW device's identity during the seed window (a stale true could
    // otherwise mislead WaitUntilSafe for one poll); a tripped or ended session is not safe either.
    private void ResetSessionLocked() => _cachedSafe = false;

    // The runtime read the lifecycle runs each tick. IsSafe is the essential read, and a failed one is
    // NOT safe: the cache is cleared in the same commit that trips the monitor to Error (rule 3).
    private DeviceRead ReadRuntime(ISafetyMonitorDevice client) {
        var safe = ReadIsSafe(client, out var readError);
        return new DeviceRead(readError, CommitLocked: () => _cachedSafe = safe);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Device-read boundary: an Alpaca IsSafe call can throw arbitrary driver/HTTP exceptions; any escape must read as not safe and be reported as the essential read's failure, not fault the refresh. CA1031's log-and-recover boundary applies.")]
    private bool ReadIsSafe(ISafetyMonitorDevice client, out string? readError) {
        readError = null;
        try {
            return client.IsSafe;
        } catch (Exception ex) {
            LogIsSafeReadFailed(ex);
            readError = ex.Message;
            return false;
        }
    }

    public void Dispose() {
        _lifecycle.Dispose();
        GC.SuppressFinalize(this);
    }

    // Logged whenever an IsSafe read throws; the lifecycle then trips the live monitor to Error.
    [LoggerMessage(Level = LogLevel.Warning, Message = "SafetyMonitor IsSafe read failed")]
    private partial void LogIsSafeReadFailed(Exception ex);
}
