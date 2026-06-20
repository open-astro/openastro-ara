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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §52.1 connection-lifecycle: on daemon boot, re-establish each device the user
/// had connected (remembered in <see cref="IEquipmentSelectionStore"/>) whose
/// profile auto-connect-on-boot bool is set. This is the daemon-side half of the
/// "Auto-connect on boot" setting — without it the bool is recorded but nothing
/// acts on it, so after a restart capture fails with "camera is not connected"
/// until the user reconnects by hand.
///
/// <para>Every connect routes through the same §68.1 AlpacaBridge version gate the
/// REST <c>/connect</c> endpoints use, so a boot-time auto-connect can't bypass the
/// handshake: a bridge below the minimum blocks the device (logged + skipped); a
/// warn-band bridge connects but emits the advisory event.</para>
///
/// <para>Best-effort and fully isolated per device: a single device failing (bridge
/// down, hardware unplugged, driver error) never blocks the others and never faults
/// startup. It only attempts devices that were actually connected before, so it
/// won't poke at hardware the user doesn't have.</para>
/// </summary>
public sealed partial class EquipmentAutoConnectService : BackgroundService {

    // Let the host finish wiring and Kestrel come up before reaching out to the
    // bridge — auto-connect is background work, not part of readiness.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _services;
    private readonly ILogger<EquipmentAutoConnectService> _logger;

    public EquipmentAutoConnectService(IServiceProvider services, ILogger<EquipmentAutoConnectService> logger) {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        try {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return; // daemon shutting down before we started — nothing to do
        }

        var store = _services.GetRequiredService<IEquipmentSelectionStore>();
        var profile = _services.GetRequiredService<IProfileStore>();
        var bridge = _services.GetRequiredService<IAlpacaBridgeHandshakeService>();
        var notifier = _services.GetRequiredService<IAlpacaBridgeGateNotifier>();

        IReadOnlyDictionary<DeviceType, DiscoveredDeviceDto> remembered;
        EquipmentConnectionDto conn;
        try {
            remembered = await store.GetAllAsync(stoppingToken).ConfigureAwait(false);
            conn = profile.GetEquipmentConnection();
        }
#pragma warning disable CA1031 // never let a read failure fault startup
        catch (Exception ex) {
            LogReadFailed(ex);
            return;
        }
#pragma warning restore CA1031

        foreach (var (type, device) in remembered) {
            if (stoppingToken.IsCancellationRequested) {
                return;
            }
            if (!AutoConnectEnabled(conn, type)) {
                continue; // user opted this type out of connect-on-boot
            }
            await TryConnectAsync(type, device, bridge, notifier, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TryConnectAsync(DeviceType type, DiscoveredDeviceDto device,
            IAlpacaBridgeHandshakeService bridge, IAlpacaBridgeGateNotifier notifier, CancellationToken ct) {
        try {
            // §68.1 gate — same decision the REST /connect path makes.
            var handshake = await bridge.HandshakeAsync(EquipmentEndpoints.BridgeUri(device), ct).ConfigureAwait(false);
            if (handshake.Status == AlpacaBridgeStatus.OutdatedBlock) {
                LogBridgeBlocked(type, handshake.Version);
                return;
            }
            if (handshake.Status == AlpacaBridgeStatus.OutdatedWarn) {
                await notifier.NotifyOutdatedWarnAsync(handshake.Version, ct).ConfigureAwait(false);
            }

            var connect = ResolveConnect(type, device, ct);
            if (connect is null) {
                return; // a type with no auto-connectable service (e.g. guider/switch)
            }
            await connect().ConfigureAwait(false);
            LogReconnecting(type, device.Name);
        }
#pragma warning disable CA1031 // one device's failure must not abort the others or startup
        catch (Exception ex) {
            LogConnectFailed(ex, type, device.Name);
        }
#pragma warning restore CA1031
    }

    /// <summary>Maps a device type to its service's connect call, or null when the type has no
    /// connect-on-boot path (Switch has no auto-connect setting; Guider connects via PHD2, not Alpaca).</summary>
    private Func<Task>? ResolveConnect(DeviceType type, DiscoveredDeviceDto device, CancellationToken ct) {
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
            _ => null,
        };
    }

    /// <summary>The profile auto-connect bool for a device type. Switch/Guider map to no bool here
    /// (Switch has none; Guider's connect is out of this Alpaca path), so they never auto-connect.</summary>
    private static bool AutoConnectEnabled(EquipmentConnectionDto conn, DeviceType type) => type switch {
        DeviceType.Camera => conn.Camera,
        DeviceType.Telescope => conn.Mount,
        DeviceType.Focuser => conn.Focuser,
        DeviceType.FilterWheel => conn.FilterWheel,
        DeviceType.Rotator => conn.Rotator,
        DeviceType.Dome => conn.Dome,
        DeviceType.SafetyMonitor => conn.SafetyMonitor,
        DeviceType.ObservingConditions => conn.Weather,
        DeviceType.FlatDevice or DeviceType.CoverCalibrator => conn.FlatPanel,
        _ => false,
    };

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Auto-connecting {DeviceType} '{DeviceName}' on boot (§52.1).")]
    private partial void LogReconnecting(DeviceType deviceType, string deviceName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Auto-connect skipped {DeviceType}: AlpacaBridge {BridgeVersion} is below the minimum supported version.")]
    private partial void LogBridgeBlocked(DeviceType deviceType, string? bridgeVersion);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Auto-connect of {DeviceType} '{DeviceName}' failed; it can be connected manually.")]
    private partial void LogConnectFailed(Exception ex, DeviceType deviceType, string deviceName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Auto-connect-on-boot could not read its inputs; skipping (devices can be connected manually).")]
    private partial void LogReadFailed(Exception ex);
}
