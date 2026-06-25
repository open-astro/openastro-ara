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
    /// the number of devices a connect was dispatched for (0 = nothing remembered for this type).</summary>
    Task<int> ReconnectAsync(DeviceType type, CancellationToken ct);
}

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

    public async Task<int> ReconnectAsync(DeviceType type, CancellationToken ct) {
        var remembered = await _store.GetAllAsync(ct).ConfigureAwait(false);
        var attempted = 0;
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
        // Count every remembered+connectable device we attempted (not just clean dispatches),
        // so the endpoint returns 202 "reconnecting" rather than 404 when a device throws.
        return attempted;
    }

    private static bool SameGroup(DeviceType a, DeviceType b) => Normalize(a) == Normalize(b);

    private static DeviceType Normalize(DeviceType t) =>
        t == DeviceType.FlatDevice ? DeviceType.CoverCalibrator : t;

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Reconnect dispatch for {DeviceType} '{DeviceName}' failed; the other remembered devices still get their turn.")]
    private partial void LogDispatchFailed(Exception ex, DeviceType deviceType, string deviceName);
}
