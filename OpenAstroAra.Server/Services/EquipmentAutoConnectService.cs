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
/// <para>Best-effort and fully isolated per device: a single device failing (bridge
/// down, hardware unplugged, driver error) never blocks the others and never faults
/// startup. It only attempts devices that were actually connected before, so it
/// won't poke at hardware the user doesn't have.</para>
/// </summary>
public sealed partial class EquipmentAutoConnectService : BackgroundService {

    // Let the host finish wiring and Kestrel come up before reaching out to the
    // bridge — auto-connect is background work, not part of readiness.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(2);

    // Per-device deadline for the connect dispatch. The connect does network I/O
    // and can stall on a silent/unresponsive bridge; without a deadline the
    // sequential boot loop would block on that one device. Bounds each device so
    // a stuck one is skipped and the rest proceed.
    private static readonly TimeSpan PerDeviceTimeout = TimeSpan.FromSeconds(30);

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

        IReadOnlyList<DiscoveredDeviceDto> remembered;
        EquipmentConnectionDto conn;
        try {
            remembered = await store.GetAllAsync(stoppingToken).ConfigureAwait(false);
            conn = profile.GetEquipmentConnection();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
            return; // clean shutdown during the read — not a failure worth logging
        }
#pragma warning disable CA1031 // never let a read failure fault startup
        catch (Exception ex) {
            LogReadFailed(ex);
            return;
        }
#pragma warning restore CA1031

        // One entry per single-instance type plus each remembered switch — reconnect them all
        // (subject to the per-type connect-on-boot toggle).
        foreach (var device in remembered) {
            if (stoppingToken.IsCancellationRequested) {
                return;
            }
            if (!AutoConnectEnabled(conn, device.Type)) {
                continue; // user opted this type out of connect-on-boot
            }
            await TryConnectAsync(device.Type, device, stoppingToken).ConfigureAwait(false);
        }

        // The guider is not an Alpaca device (PHD2 over JSON-RPC), so it never appears
        // in the remembered-selection store — honor its connect-on-boot toggle here.
        // A null-field request means "the active profile's phd2 host/port" (which may
        // be a remote SBC), so this works for any deployment shape.
        if (conn.Guider && !stoppingToken.IsCancellationRequested) {
            try {
                LogReconnecting(DeviceType.Guider, "PHD2");
                await _services.GetRequiredService<IGuiderService>()
                    .ConnectAsync(new GuiderConnectRequestDto(), null, stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) {
                // clean shutdown mid-dispatch
            }
#pragma warning disable CA1031 // guider dispatch failure must not fault startup
            catch (Exception ex) {
                LogConnectFailed(ex, DeviceType.Guider, "PHD2");
            }
#pragma warning restore CA1031
        }
    }

    private async Task TryConnectAsync(DeviceType type, DiscoveredDeviceDto device, CancellationToken ct) {
        // Bound this device to PerDeviceTimeout, still honouring daemon shutdown (ct).
        // A stuck connect trips the deadline and we move on to the next device.
        using var deviceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deviceCts.CancelAfter(PerDeviceTimeout);
        var deviceCt = deviceCts.Token;
        try {
            // Alpaca is open by design — connect the remembered device directly (no version gate).
            // The resolver is shared with the manual /reconnect endpoints (IEquipmentReconnector).
            var connect = _services.GetRequiredService<IEquipmentReconnector>().ResolveConnect(type, device, deviceCt);
            if (connect is null) {
                return; // a type with no auto-connectable service (e.g. guider)
            }
            // Log intent before dispatching so the attempt is always recorded — even
            // if connect() faults synchronously (then LogConnectFailed follows it).
            LogReconnecting(type, device.Name);
            await connect().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // daemon shutting down — unwind cleanly, don't log it as a device failure
        }
        catch (OperationCanceledException) {
            // Our own per-device deadline fired (ct is not cancelled) — the bridge or
            // device is unresponsive. Skip it; the others still get their turn.
            LogConnectTimedOut(type, device.Name, (int)PerDeviceTimeout.TotalSeconds);
        }
#pragma warning disable CA1031 // one device's failure must not abort the others or startup
        catch (Exception ex) {
            LogConnectFailed(ex, type, device.Name);
        }
#pragma warning restore CA1031
    }

    /// <summary>The profile auto-connect bool for a device type. Guider is handled
    /// separately (PHD2 connects over JSON-RPC using the profile's host/port, not via
    /// the remembered-Alpaca-device loop) — see the tail of ExecuteAsync.</summary>
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
        DeviceType.Switch => conn.Switch,
        _ => false,
    };

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Auto-connecting {DeviceType} '{DeviceName}' on boot (§52.1).")]
    private partial void LogReconnecting(DeviceType deviceType, string deviceName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Auto-connect of {DeviceType} '{DeviceName}' failed; it can be connected manually.")]
    private partial void LogConnectFailed(Exception ex, DeviceType deviceType, string deviceName);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Auto-connect of {DeviceType} '{DeviceName}' timed out after {TimeoutSeconds}s (unresponsive bridge/device); skipping — it can be connected manually.")]
    private partial void LogConnectTimedOut(DeviceType deviceType, string deviceName, int timeoutSeconds);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Auto-connect-on-boot could not read its inputs; skipping (devices can be connected manually).")]
    private partial void LogReadFailed(Exception ex);
}
