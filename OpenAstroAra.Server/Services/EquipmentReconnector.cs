#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §52.1 — connects the device(s) the user last had connected (remembered in
/// <see cref="IEquipmentSelectionStore"/>) without re-running discovery. The
/// single source of truth for "which service connects a remembered device of
/// type X", shared by <see cref="EquipmentAutoConnectService"/> (auto-connect on
/// boot) and the manual <c>POST /equipment/{type}/reconnect</c> endpoints.
/// </summary>
public interface IEquipmentReconnector {
    /// <summary>The service connect call for a remembered <paramref name="device"/> of
    /// <paramref name="type"/>, or <c>null</c> when the type has no Alpaca connect path
    /// (Guider connects via PHD2, not this Alpaca flow).</summary>
    Func<Task>? ResolveConnect(DeviceType type, DiscoveredDeviceDto device, CancellationToken ct);

    /// <summary>Reconnect the known device(s) for <paramref name="type"/> — every remembered
    /// switch for <see cref="DeviceType.Switch"/>, otherwise the single remembered device, or, when
    /// nothing is remembered for a single-instance type, the device its service still retains
    /// (#1127: the wizard's store-only forget keeps the retained record, and the card names that
    /// one). Returns how many devices were <see cref="ReconnectOutcome.Attempted"/> (remembered or
    /// retained, and connectable) and how many had a connect
    /// <see cref="ReconnectOutcome.Dispatched"/> without throwing, so the caller can tell "neither
    /// remembered nor retained" (0 attempted) from "dispatching in background" (≥1 dispatched) from
    /// "every dispatch failed synchronously" (attempted &gt; 0, 0 dispatched). The §42.3 fault
    /// reaction ladder uses the same call, so a device removed from auto-connect but still held by
    /// its service is hot-reconnected after a fault too.</summary>
    Task<ReconnectOutcome> ReconnectAsync(DeviceType type, CancellationToken ct);

    /// <summary>The connection state of the device service for <paramref name="type"/>, so a
    /// caller that dispatched a background <see cref="ReconnectAsync"/> can confirm the outcome.
    /// Null when the type has no Alpaca service, or nothing is remembered/known for it. For the
    /// multi-instance Switch type: Error if any connection is Error, Connected only when every
    /// known switch is Connected, otherwise the first non-connected state.</summary>
    Task<EquipmentConnectionState?> GetConnectionStateAsync(DeviceType type, CancellationToken ct);
}

/// <summary>Outcome of a <see cref="IEquipmentReconnector.ReconnectAsync"/> call.
/// <paramref name="Attempted"/> is the count of connectable devices a connect was tried for — the
/// remembered ones, or the service's retained device when nothing is remembered (#1127); <paramref name="Dispatched"/> is how many of those returned from <c>ConnectAsync</c> without
/// throwing (each then connects in the background). <c>Dispatched &lt; Attempted</c> means some
/// devices failed to even dispatch (e.g. their Alpaca server is down on a rig restart).</summary>
public readonly record struct ReconnectOutcome(int Attempted, int Dispatched);

public sealed partial class EquipmentReconnector : IEquipmentReconnector {

    private readonly IServiceProvider _services;
    private readonly IEquipmentSelectionStore _store;
    private readonly ILogger<EquipmentReconnector> _logger;
    // Finds a device saved under an address this box no longer has on this box's own Alpaca server.
    // Null (tests, no management client) dials the saved record as it is.
    private readonly IAlpacaManagementClient? _alpaca;
    private readonly Func<string?, bool>? _isThisMachine;

    public EquipmentReconnector(IServiceProvider services, IEquipmentSelectionStore store,
            ILogger<EquipmentReconnector> logger, IAlpacaManagementClient? alpaca = null,
            Func<string?, bool>? isThisMachine = null) {
        _services = services;
        _store = store;
        _logger = logger;
        _alpaca = alpaca;
        _isThisMachine = isThisMachine;
    }

    public Func<Task>? ResolveConnect(DeviceType type, DiscoveredDeviceDto device, CancellationToken ct) {
        Func<ConnectRequestDto, Task>? connect = type switch {
            DeviceType.Camera => req => _services.GetRequiredService<ICameraService>().ConnectAsync(req, null, ct),
            DeviceType.Telescope => req => _services.GetRequiredService<ITelescopeService>().ConnectAsync(req, null, ct),
            DeviceType.Focuser => req => _services.GetRequiredService<IFocuserService>().ConnectAsync(req, null, ct),
            DeviceType.FilterWheel => req => _services.GetRequiredService<IFilterWheelService>().ConnectAsync(req, null, ct),
            DeviceType.Rotator => req => _services.GetRequiredService<IRotatorService>().ConnectAsync(req, null, ct),
            DeviceType.Dome => req => _services.GetRequiredService<IDomeService>().ConnectAsync(req, null, ct),
            DeviceType.SafetyMonitor => req => _services.GetRequiredService<ISafetyMonitorService>().ConnectAsync(req, null, ct),
            DeviceType.ObservingConditions => req => _services.GetRequiredService<IObservingConditionsService>().ConnectAsync(req, null, ct),
            DeviceType.FlatDevice or DeviceType.CoverCalibrator =>
                req => _services.GetRequiredService<IFlatDeviceService>().ConnectAsync(req, null, ct),
            DeviceType.Switch => req => _services.GetRequiredService<ISwitchService>().ConnectAsync(req, null, ct),
            _ => null,
        };
        if (connect is null) {
            return null;
        }
        var localized = HostLocality.LocalizeDevice(device, _isThisMachine);
        return async () => {
            var target = await FindMovedAsync(localized, ct).ConfigureAwait(false) ?? localized;
            await connect(new ConnectRequestDto(target)).ConfigureAwait(false);
        };
    }

    // A record saved under an address this box no longer has (the field router's, back on the home LAN)
    // whose device this box's own Alpaca server serves: dial it as localhost and re-save it, so the next
    // boot goes straight there instead of waiting out the Alpaca timeout on a dead address.
    private async Task<DiscoveredDeviceDto?> FindMovedAsync(DiscoveredDeviceDto device, CancellationToken ct) {
        if (_alpaca is null) {
            return null;
        }
        var moved = await HostLocality.FindOnLocalServerAsync(device, _alpaca, ct, _isThisMachine).ConfigureAwait(false);
        if (moved is null) {
            return null;
        }
        LogMovedToLocalhost(device.Type, device.Name, device.UniqueId, device.IpAddress);
        // A switch is remembered per endpoint: the old address's entry goes (only that one; another
        // server's switch may share a serial-less UniqueId) or boot dials it too.
        await _store.RelocateAsync(device, moved, ct).ConfigureAwait(false);
        return moved;
    }

    public async Task<ReconnectOutcome> ReconnectAsync(DeviceType type, CancellationToken ct) {
        // The guider is not an Alpaca device (PHD2 over JSON-RPC) and never appears in
        // the remembered-selection store — dispatch its connect directly. A null-field
        // request means "the active profile's phd2 host/port", so this heals a dropped
        // guider wherever it lives (e.g. an SBC at :8080). Without this, a meridian
        // flip that needs guider re-calibration aborted whenever the guider was not
        // already connected (EnsureConnected saw 0 dispatched).
        if (type.Canonical() == DeviceType.Guider) {
            try {
                await _services.GetRequiredService<IGuiderService>()
                    .ConnectAsync(new GuiderConnectRequestDto(), null, ct).ConfigureAwait(false);
                return new ReconnectOutcome(Attempted: 1, Dispatched: 1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            }
#pragma warning disable CA1031 // a failed dispatch is an outcome, not a fault — same contract as the Alpaca loop below
            catch (Exception ex) {
                LogDispatchFailed(ex, DeviceType.Guider, "PHD2");
                return new ReconnectOutcome(Attempted: 1, Dispatched: 0);
            }
#pragma warning restore CA1031
        }
        var remembered = await _store.GetAllAsync(ct).ConfigureAwait(false);
        var attempted = 0;
        var dispatched = 0;
        // FlatDevice/CoverCalibrator are the same physical device under two tokens
        // (ASCOM type vs NINA concept), so a remembered "CoverCalibrator" still satisfies
        // a "FlatDevice" reconnect (and vice-versa).
        var candidates = remembered.Where(d => SameGroup(d.Type, type)).ToList();
        // #1127 — the card names the service's RETAINED device, and the store can have forgotten
        // it (the wizard's store-only DELETE …/remembered) while the service still holds it. With
        // nothing remembered, reconnect what the card shows rather than answer 404 to a device the
        // daemon plainly knows. Single-instance types only: the switch registry is addressed by id.
        if (candidates.Count == 0 && ResolveRetained(type) is { } retained) {
            candidates.Add(retained);
        }
        foreach (var device in candidates) {
            var connect = ResolveConnect(device.Type, device, ct);
            if (connect is null) {
                continue;
            }
            attempted++;
            try {
                // The service's ConnectAsync returns once accepted and connects in the
                // background, so this awaits only the dispatch.
                await connect().ConfigureAwait(false);
                dispatched++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw; // daemon shutting down — unwind cleanly
            }
#pragma warning disable CA1031 // one device failing the dispatch (e.g. its Alpaca server isn't up yet during a rig restart) must not abort the others
            catch (Exception ex) {
                LogDispatchFailed(ex, device.Type, device.Name);
            }
#pragma warning restore CA1031
        }
        // attempted vs dispatched lets the endpoint distinguish 404 (nothing remembered),
        // 202 (≥1 dispatched, connecting in the background), and "all dispatches failed
        // synchronously" (attempted > 0 but dispatched == 0) so the caller isn't told
        // "reconnecting…" when every device failed on the spot.
        return new ReconnectOutcome(attempted, dispatched);
    }

    // #1127 — the single-instance service's retained record for the type (null when the service
    // is a placeholder or holds nothing). Internal for direct unit testing.
    internal DiscoveredDeviceDto? ResolveRetained(DeviceType type) {
        object? service = type.Canonical() switch {
            DeviceType.Camera => _services.GetService<ICameraService>(),
            DeviceType.Telescope => _services.GetService<ITelescopeService>(),
            DeviceType.Focuser => _services.GetService<IFocuserService>(),
            DeviceType.FilterWheel => _services.GetService<IFilterWheelService>(),
            DeviceType.Rotator => _services.GetService<IRotatorService>(),
            DeviceType.Dome => _services.GetService<IDomeService>(),
            DeviceType.SafetyMonitor => _services.GetService<ISafetyMonitorService>(),
            DeviceType.ObservingConditions => _services.GetService<IObservingConditionsService>(),
            DeviceType.FlatDevice or DeviceType.CoverCalibrator => _services.GetService<IFlatDeviceService>(),
            _ => null,
        };
        return (service as IRetainedDeviceSource)?.RetainedDevice;
    }

    public async Task<EquipmentConnectionState?> GetConnectionStateAsync(DeviceType type, CancellationToken ct) {
        switch (type.Canonical()) {
            case DeviceType.Guider:
                return (await _services.GetRequiredService<IGuiderService>().GetAsync(ct).ConfigureAwait(false))?.State;
            case DeviceType.Camera:
                return (await _services.GetRequiredService<ICameraService>().GetAsync(ct).ConfigureAwait(false))?.State;
            case DeviceType.Telescope:
                return (await _services.GetRequiredService<ITelescopeService>().GetAsync(ct).ConfigureAwait(false))?.State;
            case DeviceType.Focuser:
                return (await _services.GetRequiredService<IFocuserService>().GetAsync(ct).ConfigureAwait(false))?.State;
            case DeviceType.FilterWheel:
                return (await _services.GetRequiredService<IFilterWheelService>().GetAsync(ct).ConfigureAwait(false))?.State;
            case DeviceType.Rotator:
                return (await _services.GetRequiredService<IRotatorService>().GetAsync(ct).ConfigureAwait(false))?.State;
            case DeviceType.Dome:
                return (await _services.GetRequiredService<IDomeService>().GetAsync(ct).ConfigureAwait(false))?.State;
            case DeviceType.SafetyMonitor:
                return (await _services.GetRequiredService<ISafetyMonitorService>().GetAsync(ct).ConfigureAwait(false))?.State;
            case DeviceType.ObservingConditions:
                return (await _services.GetRequiredService<IObservingConditionsService>().GetAsync(ct).ConfigureAwait(false))?.State;
            case DeviceType.CoverCalibrator:
                return (await _services.GetRequiredService<IFlatDeviceService>().GetAsync(ct).ConfigureAwait(false))?.State;
            case DeviceType.Switch: {
                var all = await _services.GetRequiredService<ISwitchService>().GetAllAsync(ct).ConfigureAwait(false);
                if (all.Count == 0) {
                    return null;
                }
                EquipmentConnectionState? firstNonConnected = null;
                foreach (var sw in all) {
                    if (sw.State == EquipmentConnectionState.Error) {
                        return EquipmentConnectionState.Error;
                    }
                    if (sw.State != EquipmentConnectionState.Connected) {
                        firstNonConnected ??= sw.State;
                    }
                }
                return firstNonConnected ?? EquipmentConnectionState.Connected;
            }
            default:
                return null;
        }
    }

    // FlatDevice/CoverCalibrator collapse lives on DeviceTypeExtensions.Canonical (shared).
    private static bool SameGroup(DeviceType a, DeviceType b) => a.Canonical() == b.Canonical();

    [LoggerMessage(Level = LogLevel.Information,
        Message = "{DeviceType} '{DeviceName}' ({UniqueId}) was saved under {OldAddress}, which no longer serves it; this box's own Alpaca server lists the same UniqueID, so it is dialled and saved as localhost (re-pick it in the client if it belongs to another rig)")]
    private partial void LogMovedToLocalhost(DeviceType deviceType, string deviceName, string uniqueId, string oldAddress);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Reconnect dispatch for {DeviceType} '{DeviceName}' failed; the other remembered devices still get their turn.")]
    private partial void LogDispatchFailed(Exception ex, DeviceType deviceType, string deviceName);
}
