#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// "Is this host THIS machine?" — shared by the guider's local-unit gating (#1192) and the Alpaca
/// device records. On 2026-10-16 the CM4's devices were remembered under three of its own
/// addresses (the hotspot's 172.24.1.1, the field router's 192.168.8.118, the home LAN's
/// 192.168.1.118), so every move between networks left half the rig "offline" at boot while the
/// bridge sat on the same box. A local Alpaca server is addressed as <c>localhost</c> instead
/// (<see cref="LocalizeDevice"/>): an address that never changes.
/// </summary>
public static class HostLocality {

    /// <summary>What a local Alpaca server is remembered and dialled as.</summary>
    public const string LocalHostName = "localhost";
    public const string LocalIpAddress = "127.0.0.1";

    /// <summary>
    /// The pure decision: blank, <c>localhost</c>, a loopback / unspecified address, this machine's
    /// host name (with or without a domain suffix), or an address bound to one of this machine's
    /// interfaces. Case-insensitive; an IPv6 literal may carry its brackets.
    /// </summary>
    public static bool IsLocalHost(string? host, string machineName, IEnumerable<IPAddress> localAddresses) {
        ArgumentNullException.ThrowIfNull(localAddresses);
        var name = host?.Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(name) || name.Equals(LocalHostName, StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
        if (IPAddress.TryParse(name, out var ip)) {
            if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) {
                return true;
            }
            foreach (var local in localAddresses) {
                if (SameAddress(local, ip)) {
                    return true;
                }
            }
            return false;
        }
        return SameMachineName(name, machineName);
    }

    /// <summary>The production decision for a literal host or a bare machine name: this machine's
    /// own name and interface addresses, no DNS. Never throws.</summary>
    public static bool IsThisMachine(string? host) => IsLocalHost(host, SafeMachineName(), SafeLocalAddresses());

    /// <summary><see cref="IsThisMachine"/> with the name and interface addresses read once, for a caller
    /// that asks about many records in a row (the selection store's read).</summary>
    public static Func<string?, bool> ThisMachineSnapshot() {
        var name = SafeMachineName();
        var addresses = SafeLocalAddresses();
        return host => IsLocalHost(host, name, addresses);
    }

    /// <summary>A device whose Alpaca server is this machine is remembered and dialled as
    /// <c>localhost</c>, whatever address discovery or the client saw it under; any other device is
    /// returned unchanged. <paramref name="isThisMachine"/> is injectable for tests.</summary>
    public static DiscoveredDeviceDto LocalizeDevice(DiscoveredDeviceDto device, Func<string?, bool>? isThisMachine = null) {
        ArgumentNullException.ThrowIfNull(device);
        var local = isThisMachine ?? IsThisMachine;
        var alreadyLocal = string.Equals(device.HostName, LocalHostName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(device.IpAddress, LocalIpAddress, StringComparison.Ordinal);
        if (alreadyLocal) {
            return device;
        }
        // An IP address decides: discovery's HostName is the reverse-DNS name of the answering server,
        // and a second Pi with the default name ("raspberrypi.lan" at 192.168.1.50) would otherwise read as
        // this box. The name counts only when no address was given (a hand-entered device).
        var isLocal = IPAddress.TryParse(device.IpAddress?.Trim().Trim('[', ']'), out _)
            ? local(device.IpAddress)
            : !string.IsNullOrWhiteSpace(device.HostName) && local(device.HostName);
        return isLocal
            ? device with { HostName = LocalHostName, IpAddress = LocalIpAddress }
            : device;
    }

    /// <summary>
    /// A device remembered under an address this machine no longer has: on 2026-10-22 the CM4 came home
    /// with every device still saved under the field router's 192.168.8.118, which <see cref="LocalizeDevice"/>
    /// cannot recognise because no interface carries it any more, and each connect waited out the 60 s
    /// Alpaca timeout. Asks the saved server and this machine's own Alpaca server (same port) for their
    /// configured devices: when the saved server does not list the device and the local one lists the same
    /// type and number under the same UniqueID, returns the record pointed at <c>localhost</c>. Null when the device is already local, the saved server still serves
    /// it, or the local server does not. Bounded by the management client's request timeout; never throws
    /// for an unreachable server.
    /// </summary>
    public static async Task<DiscoveredDeviceDto?> FindOnLocalServerAsync(DiscoveredDeviceDto device,
            IAlpacaManagementClient alpaca, CancellationToken ct, Func<string?, bool>? isThisMachine = null) {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(alpaca);
        if (!ReferenceEquals(LocalizeDevice(device, isThisMachine), device)
            || string.Equals(device.HostName, LocalHostName, StringComparison.OrdinalIgnoreCase)) {
            return null; // already this machine
        }
        var savedHost = string.IsNullOrWhiteSpace(device.IpAddress) ? device.HostName : device.IpAddress;
        // The management lookup is plain http: an https-only server would look gone, and a record with no
        // address has nothing to ask.
        if (device.UseHttps || string.IsNullOrWhiteSpace(savedHost)) {
            return null;
        }
        var key = EquipmentIdentityReconcile.DeviceKey(device.Type, device.AlpacaDeviceNumber);
        var savedTask = alpaca.GetConfiguredDevicesAsync(savedHost, device.IpPort, ct);
        var localTask = alpaca.GetConfiguredDevicesAsync(LocalIpAddress, device.IpPort, ct);
        await Task.WhenAll(savedTask, localTask).ConfigureAwait(false);
        var saved = await savedTask.ConfigureAwait(false);
        var local = await localTask.ConfigureAwait(false);
        if (saved.ContainsKey(key) || !local.TryGetValue(key, out var live) || !SameDevice(device, live)) {
            return null;
        }
        return device with { HostName = LocalHostName, IpAddress = LocalIpAddress };
    }

    // The number alone is not enough (two servers can both have a focuser 0), and neither is a driver name
    // ("ZWO EAF", "ZWO ASI120MM" on every rig that has one): the saved server may only be switched off. A
    // device whose UniqueID the bridge has since renamed is re-picked once by hand.
    private static bool SameDevice(DiscoveredDeviceDto device, AlpacaConfiguredDevice live) {
        var id = device.UniqueId?.Trim() ?? string.Empty;
        return id.Length > 0 && id.Equals(live.UniqueId?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // IPAddress.Equals includes the IPv6 scope id, so the NIC's fe80::1234%2 would never match a
    // host typed as fe80::1234. Compare the bytes only (family first).
    private static bool SameAddress(IPAddress a, IPAddress b) =>
        a.AddressFamily == b.AddressFamily && a.GetAddressBytes().AsSpan().SequenceEqual(b.GetAddressBytes());

    // Domain suffixes under which "<our label>.<suffix>" can only mean this machine: mDNS's own
    // .local and the usual router / RFC 8375 home suffixes. Any other domain is a real DNS name two
    // boxes with the same label may share (raspberrypi.site2, #1234).
    private static readonly string[] SelfSuffixes = ["local", "localdomain", "lan", "home", "home.arpa", "internal"];

    /// <summary>"raspberrypi" == "raspberrypi.local" == "RASPBERRYPI.lan": the first DNS label, plus a
    /// domain check when the host carries one — our own FQDN or one of the self suffixes.</summary>
    public static bool SameMachineName(string host, string machineName) {
        ArgumentNullException.ThrowIfNull(host);
        if (string.IsNullOrWhiteSpace(machineName)) {
            return false;
        }
        var hostParts = host.Split('.', 2);
        var machineParts = machineName.Trim().Split('.', 2);
        if (hostParts[0].Length == 0 || !hostParts[0].Equals(machineParts[0], StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        if (hostParts.Length == 1) {
            return true; // bare label
        }
        var hostDomain = hostParts[1].TrimEnd('.');
        if (machineParts.Length == 2 && hostDomain.Equals(machineParts[1].TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) {
            return true; // our own FQDN
        }
        foreach (var suffix in SelfSuffixes) {
            if (hostDomain.Equals(suffix, StringComparison.OrdinalIgnoreCase)) {
                return true;
            }
        }
        return false;
    }

    public static string SafeMachineName() {
        try {
            return Dns.GetHostName();
        } catch (SocketException) {
            return string.Empty;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "NetworkInterface enumeration throws platform-specific exceptions on some sandboxed hosts; an empty list just narrows 'local' to loopback + host name.")]
    public static IReadOnlyList<IPAddress> SafeLocalAddresses() {
        var addresses = new List<IPAddress>();
        try {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses) {
                    addresses.Add(unicast.Address);
                }
            }
        } catch (Exception) {
            // see justification
        }
        return addresses;
    }
}
