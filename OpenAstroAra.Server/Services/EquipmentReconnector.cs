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

    /// <summary>Reconnect the remembered device(s) for <paramref name="type"/> — every remembered
    /// switch for <see cref="DeviceType.Switch"/>, otherwise the single remembered device. Returns
    /// how many devices were <see cref="ReconnectOutcome.Attempted"/> (remembered + connectable) and
    /// how many had a connect <see cref="ReconnectOutcome.Dispatched"/> without throwing, so the
    /// caller can tell "nothing remembered" (0 attempted) from "dispatching in background" (≥1
    /// dispatched) from "every dispatch failed synchronously" (attempted &gt; 0, 0 dispatched).</summary>
    Task<ReconnectOutcome> ReconnectAsync(DeviceType type, CancellationToken ct);

    /// <summary>The connection state of the device service for <paramref name="type"/>, so a
    /// caller that dispatched a background <see cref="ReconnectAsync"/> can confirm the outcome.
    /// Null when the type has no Alpaca service, or nothing is remembered/known for it. For the
    /// multi-instance Switch type: Error if any connection is Error, Connected only when every
    /// known switch is Connected, otherwise the first non-connected state.</summary>
    Task<EquipmentConnectionState?> GetConnectionStateAsync(DeviceType type, CancellationToken ct);
}

/// <summary>Outcome of a <see cref="IEquipmentReconnector.ReconnectAsync"/> call.
/// <paramref name="Attempted"/> is the count of remembered + connectable devices a connect was tried
/// for; <paramref name="Dispatched"/> is how many of those returned from <c>ConnectAsync</c> without
/// throwing (each then connects in the background). <c>Dispatched &lt; Attempted</c> means some
/// devices failed to even dispatch (e.g. their Alpaca server is down on a rig restart).</summary>
public readonly record struct ReconnectOutcome(int Attempted, int Dispatched);

public sealed partial class EquipmentReconnector : IEquipmentReconnector {

    private readonly IServiceProvider _services;
    private readonly IEquipmentSelectionStore _store;
    private readonly ILogger<EquipmentReconnector> _logger;

    public EquipmentReconnector(IServiceProvider services, IEquipmentSelectionStore store,
            ILogger<EquipmentReconnector> logger) {
        _services = services;
        _store = store;
        _logger = logger;
    }

    public Func<Task>? ResolveConnect(DeviceType type, DiscoveredDeviceDto device, CancellationToken ct) {
        var req = new ConnectRequestDto(device);
        return type switch {
            DeviceType.Camera => () => _services.GetRequiredService<ICameraService>().ConnectAsync(req, null, ct),
            DeviceType.Telescope => () => _services.GetRequiredService<ITelescopeService>().ConnectAsync(req, null, ct),
            DeviceType.Focuser => () => _services.GetRequiredService<IFocuserService>().ConnectAsync(req, null, ct),
            DeviceType.FilterWheel => () => _services.GetRequiredService<IFilterWheelService>().ConnectAsync(req, null, ct),
            DeviceType.Rotator => () => _services.GetRequiredService<IRotatorService>().ConnectAsync(req, null, ct),
            DeviceType.Dome => () => _services.GetRequiredService<IDomeService>().ConnectAsync(req, null, ct),
            DeviceType.SafetyMonitor => () => _services.GetRequiredService<ISafetyMonitorService>().ConnectAsync(req, null, ct),
            DeviceType.ObservingConditions => () => _services.GetRequiredService<IObservingConditionsService>().ConnectAsync(req, null, ct),
            DeviceType.FlatDevice or DeviceType.CoverCalibrator =>
                () => _services.GetRequiredService<IFlatDeviceService>().ConnectAsync(req, null, ct),
            DeviceType.Switch => () => _services.GetRequiredService<ISwitchService>().ConnectAsync(req, null, ct),
            _ => null,
        };
    }

    public async Task<ReconnectOutcome> ReconnectAsync(DeviceType type, CancellationToken ct) {
        var remembered = await _store.GetAllAsync(ct).ConfigureAwait(false);
        var attempted = 0;
        var dispatched = 0;
        // FlatDevice/CoverCalibrator are the same physical device under two tokens
        // (ASCOM type vs NINA concept), so a remembered "CoverCalibrator" still satisfies
        // a "FlatDevice" reconnect (and vice-versa).
        foreach (var device in remembered.Where(d => SameGroup(d.Type, type))) {
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

    public async Task<EquipmentConnectionState?> GetConnectionStateAsync(DeviceType type, CancellationToken ct) {
        switch (Normalize(type)) {
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

    private static bool SameGroup(DeviceType a, DeviceType b) => Normalize(a) == Normalize(b);

    private static DeviceType Normalize(DeviceType t) =>
        t == DeviceType.FlatDevice ? DeviceType.CoverCalibrator : t;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Reconnect dispatch for {DeviceType} '{DeviceName}' failed; the other remembered devices still get their turn.")]
    private partial void LogDispatchFailed(Exception ex, DeviceType deviceType, string deviceName);
}
