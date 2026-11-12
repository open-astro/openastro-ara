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
/// #1710 — a profile owns its devices: after a switch, the Safety Monitor goes first (a simulated one
/// reporting unsafe must never see the new mount), then every device the new profile does not remember,
/// then the new profile's remembered devices connect under their connect-on-boot toggles.
/// A device that fails is reported for that device and never fails the switch.
/// </summary>
public sealed partial class ProfileDeviceSwitch : IDisposable {

    private static readonly TimeSpan SettlePoll = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a switch waits for its connects (which run in the background) to settle.</summary>
    internal TimeSpan SettleTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly IEquipmentSelectionStore _store;
    private readonly IEquipmentReconnector _reconnector;
    private readonly IProfileStore _profiles;
    private readonly ILogger<ProfileDeviceSwitch> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1); // two switches never interleave their disconnects

    public ProfileDeviceSwitch(IServiceProvider services, IEquipmentSelectionStore store, IEquipmentReconnector reconnector,
            IProfileStore profiles, ILogger<ProfileDeviceSwitch> logger) {
        _services = services;
        _store = store;
        _reconnector = reconnector;
        _profiles = profiles;
        _logger = logger;
    }

    // A device a service holds, with how to release it.
    private sealed record Slot(DeviceType Type, DiscoveredDeviceDto Device, bool Live, Func<Task> Disconnect, Func<Task> Forget);

    /// <summary>Taken before the active profile changes and released once <see cref="ApplyAsync"/> is done.</summary>
    public IDisposable? HoldSiteFollow() => _services.GetService<ITelescopeService>()?.HoldSiteFollow();

    /// <summary>Bring the connected devices in line with the now-active profile; returns one message per device with a problem.</summary>
    public async Task<IReadOnlyList<ProfileDeviceMessageDto>> ApplyAsync(CancellationToken ct) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try {
            var messages = new List<ProfileDeviceMessageDto>();
            IReadOnlyList<DiscoveredDeviceDto> remembered;
            try {
                remembered = await _store.GetAllAsync(ct).ConfigureAwait(false);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Nothing from the old profile may stay on the rig because this file is unreadable.
                LogListUnreadable(ex);
                remembered = Array.Empty<DiscoveredDeviceDto>();
                messages.Add(new("Devices", "This profile's remembered devices could not be read, so none were connected."));
            }

            var conn = _profiles.GetEquipmentConnection();
            var known = remembered.Select(Key).ToHashSet();
            var slots = await SlotsAsync(ct).ConfigureAwait(false); // Safety Monitor first
            var stayLive = new HashSet<string>();
            foreach (var slot in slots) {
                var keep = known.Contains(Key(slot.Device));
                if (slot.Live && keep && slot.Type != DeviceType.SafetyMonitor) {
                    stayLive.Add(Key(slot.Device));
                    continue;
                }
                await DropAsync(slot, keep, messages).ConfigureAwait(false);
            }

            var connecting = remembered.Where(d => !stayLive.Contains(Key(d))).ToList();
            foreach (var monitor in connecting.Where(d => d.Type == DeviceType.SafetyMonitor && !EquipmentAutoConnectService.AutoConnectEnabled(conn, d.Type))) {
                if (slots.Any(s => s.Type == DeviceType.SafetyMonitor && s.Live && Key(s.Device) == Key(monitor))) {
                    messages.Add(new(Name(monitor), "Safety Monitor disconnected for the switch and left off: its connect-on-boot toggle is off in this profile."));
                }
            }
            await ConnectAsync(connecting.Where(d => EquipmentAutoConnectService.AutoConnectEnabled(conn, d.Type)).ToList(), messages, ct).ConfigureAwait(false);
            return messages;
        } finally {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static string Key(DiscoveredDeviceDto d) => $"{d.Type.Canonical()}|{DiscoveredDeviceEndpoint.KeyOf(d)}";

    private static string Name(DiscoveredDeviceDto d) => string.IsNullOrWhiteSpace(d.Name) ? d.Type.ToString() : d.Name;

    private async Task<List<Slot>> SlotsAsync(CancellationToken ct) {
        var slots = new List<Slot>();
        await AddAsync<ISafetyMonitorService>(DeviceType.SafetyMonitor, s => s.DisconnectAsync(null, ct), s => s.ForgetAsync(ct)).ConfigureAwait(false);
        await AddAsync<ICameraService>(DeviceType.Camera, s => s.DisconnectAsync(null, ct), s => s.ForgetAsync(ct)).ConfigureAwait(false);
        await AddAsync<ITelescopeService>(DeviceType.Telescope, s => s.DisconnectAsync(null, ct), s => s.ForgetAsync(ct)).ConfigureAwait(false);
        await AddAsync<IFocuserService>(DeviceType.Focuser, s => s.DisconnectAsync(null, ct), s => s.ForgetAsync(ct)).ConfigureAwait(false);
        await AddAsync<IFilterWheelService>(DeviceType.FilterWheel, s => s.DisconnectAsync(null, ct), s => s.ForgetAsync(ct)).ConfigureAwait(false);
        await AddAsync<IRotatorService>(DeviceType.Rotator, s => s.DisconnectAsync(null, ct), s => s.ForgetAsync(ct)).ConfigureAwait(false);
        await AddAsync<IDomeService>(DeviceType.Dome, s => s.DisconnectForProfileSwitchAsync(ct), s => s.ForgetAsync(ct)).ConfigureAwait(false);
        await AddAsync<IObservingConditionsService>(DeviceType.ObservingConditions, s => s.DisconnectAsync(null, ct), s => s.ForgetAsync(ct)).ConfigureAwait(false);
        await AddAsync<IFlatDeviceService>(DeviceType.CoverCalibrator, s => s.DisconnectAsync(null, ct), s => s.ForgetAsync(ct)).ConfigureAwait(false);

        var switches = _services.GetService<ISwitchService>();
        if (switches is not null) {
            foreach (var sw in await switches.GetAllAsync(ct).ConfigureAwait(false)) {
                if (switches.GetDevice(sw.DeviceId) is { } device) {
                    slots.Add(new Slot(DeviceType.Switch, device, sw.State != EquipmentConnectionState.Disconnected,
                        () => switches.DisconnectAsync(sw.DeviceId, null, ct), () => switches.RemoveAsync(sw.DeviceId, ct)));
                }
            }
        }
        return slots;

        async Task AddAsync<T>(DeviceType type, Func<T, Task> disconnect, Func<T, Task> forget) where T : class {
            var service = _services.GetService<T>();
            if ((service as IRetainedDeviceSource)?.RetainedDevice is not { } device) {
                return;
            }
            var state = await _reconnector.GetConnectionStateAsync(type, ct).ConfigureAwait(false);
            // Error counts as live: one reached from Connected still holds an open driver.
            slots.Add(new Slot(type, device, state is not (null or EquipmentConnectionState.Disconnected),
                () => disconnect(service!), () => forget(service!)));
        }
    }

    private async Task DropAsync(Slot slot, bool keepRecord, List<ProfileDeviceMessageDto> messages) {
        try {
            if (slot.Live) {
                await slot.Disconnect().ConfigureAwait(false);
            }
            if (!keepRecord) {
                // Or the card, and Reconnect's fallback to the retained device, bring the old profile's device back.
                await slot.Forget().ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // one device's failure must not stop the others leaving, least of all the mount after the monitor
        catch (Exception ex) {
            LogReleaseFailed(ex, slot.Type, slot.Device.Name);
            messages.Add(new(Name(slot.Device), "Could not be disconnected for the switch: " + ex.Message));
        }
#pragma warning restore CA1031
    }

    private async Task ConnectAsync(List<DiscoveredDeviceDto> devices, List<ProfileDeviceMessageDto> messages, CancellationToken ct) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SettleTimeout);
        var dispatched = new List<DiscoveredDeviceDto>();
        await Task.WhenAll(devices.Select(async device => {
            try {
                var connect = _reconnector.ResolveConnect(device.Type, device, deadline.Token);
                if (connect is null) {
                    return;
                }
                await connect().ConfigureAwait(false);
                lock (dispatched) { dispatched.Add(device); }
            }
#pragma warning disable CA1031 // one device failing must not fail the switch or the others
            catch (Exception ex) {
                LogConnectFailed(ex, device.Type, device.Name);
                lock (messages) { messages.Add(new(Name(device), "Could not connect: " + ex.Message)); }
            }
#pragma warning restore CA1031
        })).ConfigureAwait(false);

        // Connects run in the background; wait so a failure can be named in the response.
        var unsettled = dispatched;
        while (unsettled.Count > 0) {
            var still = new List<DiscoveredDeviceDto>();
            foreach (var device in unsettled) {
                switch (await StateAsync(device).ConfigureAwait(false)) {
                    case EquipmentConnectionState.Connected:
                        break;
                    case EquipmentConnectionState.Connecting or null:
                        still.Add(device);
                        break;
                    default:
                        messages.Add(new(Name(device), "Could not connect. Check that its Alpaca server is running, then connect it again."));
                        break;
                }
            }
            unsettled = still;
            if (unsettled.Count == 0) {
                return;
            }
            try {
                await Task.Delay(SettlePoll, deadline.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                messages.AddRange(unsettled.Select(d => new ProfileDeviceMessageDto(Name(d), "It is still connecting. Check its card in a moment.")));
                return;
            }
        }
    }

    // One switch among several is matched by endpoint; every other type has one device.
    private async Task<EquipmentConnectionState?> StateAsync(DiscoveredDeviceDto device) {
        if (device.Type != DeviceType.Switch || _services.GetService<ISwitchService>() is not { } switches) {
            return await _reconnector.GetConnectionStateAsync(device.Type, CancellationToken.None).ConfigureAwait(false);
        }
        foreach (var sw in await switches.GetAllAsync(CancellationToken.None).ConfigureAwait(false)) {
            if (switches.GetDevice(sw.DeviceId) is { } d && DiscoveredDeviceEndpoint.KeyOf(d) == DiscoveredDeviceEndpoint.KeyOf(device)) {
                return sw.State;
            }
        }
        return null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "This profile's remembered devices could not be read; none are connected for the switch.")]
    private partial void LogListUnreadable(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Profile switch could not release {DeviceType} '{DeviceName}'.")]
    private partial void LogReleaseFailed(Exception ex, DeviceType deviceType, string deviceName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Profile switch could not connect {DeviceType} '{DeviceName}'.")]
    private partial void LogConnectFailed(Exception ex, DeviceType deviceType, string deviceName);
}
