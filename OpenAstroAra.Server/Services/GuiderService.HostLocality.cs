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
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1192 — is the profile's guider host THIS machine? The <c>openastro-guider</c> systemd unit the
/// supervisor starts/restarts is the local one, so every local-unit control (the connect-time
/// <c>systemctl start</c>, the §63.3 crash-recovery tree, the manual restart) is gated on this
/// answer: a profile that points at a guider on another box gets a plain reconnect and a log line,
/// never a start of a unit that is not the guider it uses.
/// </summary>
public sealed partial class GuiderService {

    /// <summary>Test seam: the local-host decision for a configured guider host. Defaults to
    /// <see cref="IsLocalGuiderHostAsync"/>; benches force "remote" without needing a second machine.</summary>
    internal Func<string, CancellationToken, Task<bool>> IsLocalGuiderHostDecision { get; set; } = IsLocalGuiderHostAsync;

    // Bound on the DNS lookup for a non-literal host: a dark site's hotspot has no upstream resolver,
    // and a name that does not resolve cannot be the local unit we would start anyway.
    private static readonly TimeSpan HostResolveTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// The pure decision: blank (the default guider), <c>localhost</c>, a loopback / unspecified
    /// address, this machine's host name (with or without a domain suffix), or an address bound to
    /// one of this machine's interfaces. Case-insensitive; an IPv6 literal may carry its brackets.
    /// </summary>
    internal static bool IsLocalGuiderHost(string? host, string machineName, IEnumerable<IPAddress> localAddresses) {
        var name = host?.Trim().Trim('[', ']');
        if (string.IsNullOrEmpty(name) || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)) {
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

    // IPAddress.Equals includes the IPv6 scope id, so the NIC's fe80::1234%2 would never match a
    // profile host typed as fe80::1234. Compare the bytes only (family first: the byte arrays of an
    // IPv4 and an IPv6 address can never collide in length, but be explicit).
    private static bool SameAddress(IPAddress a, IPAddress b) =>
        a.AddressFamily == b.AddressFamily && a.GetAddressBytes().AsSpan().SequenceEqual(b.GetAddressBytes());

    // "raspberrypi" == "raspberrypi.local" == "RASPBERRYPI.lan": compare the first DNS label.
    private static bool SameMachineName(string host, string machineName) {
        if (string.IsNullOrWhiteSpace(machineName)) {
            return false;
        }
        var hostLabel = host.Split('.', 2)[0];
        var machineLabel = machineName.Trim().Split('.', 2)[0];
        return hostLabel.Length > 0 && hostLabel.Equals(machineLabel, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The production decision: the pure check against this machine's name and interface
    /// addresses, then — for a DNS name that is not ours — a bounded resolve so a name that points at
    /// one of our own addresses still counts as local. Never throws.</summary>
    internal static Task<bool> IsLocalGuiderHostAsync(string host, CancellationToken ct) =>
        IsLocalGuiderHostAsync(host, SafeMachineName(), SafeLocalAddresses(), Dns.GetHostAddressesAsync, ct);

    /// <summary>The resolve step with its inputs injected, so the "a name that resolves to one of our
    /// own addresses is local" rule is testable without a second machine or a live resolver.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A DNS fault must degrade to 'not local' (no local unit is started for a host we cannot place), never break a connect.")]
    internal static async Task<bool> IsLocalGuiderHostAsync(string host, string machineName, IReadOnlyList<IPAddress> localAddresses,
            Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct) {
        var name = host?.Trim().Trim('[', ']') ?? string.Empty;
        if (IsLocalGuiderHost(name, machineName, localAddresses)) {
            return true;
        }
        if (name.Length == 0 || IPAddress.TryParse(name, out _)) {
            return false; // a literal address already got its verdict above
        }
        try {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(HostResolveTimeout);
            var resolved = await resolve(name, cts.Token).ConfigureAwait(false);
            foreach (var address in resolved) {
                if (IsLocalGuiderHost(address.ToString(), machineName, localAddresses)) {
                    return true;
                }
            }
        } catch (Exception) {
            // unresolvable / timed out / cancelled → not a host we can place on this machine
        }
        return false;
    }

    private static string SafeMachineName() {
        try {
            return Dns.GetHostName();
        } catch (SocketException) {
            return string.Empty;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "NetworkInterface enumeration throws platform-specific exceptions on some sandboxed hosts; an empty list just narrows 'local' to loopback + host name.")]
    private static List<IPAddress> SafeLocalAddresses() {
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

    [LoggerMessage(EventId = 6363, Level = LogLevel.Information, Message = "Guider host {Host}:{Port} is not this machine — not starting the local openastro-guider unit; connecting directly")]
    private partial void LogRemoteHostNoLocalStart(string host, int port);

    [LoggerMessage(EventId = 6364, Level = LogLevel.Information, Message = "Guider host {Host}:{Port} is not this machine — skipping the local systemd recovery tree; retrying the connection within the grace window")]
    private partial void LogRemoteHostNoLocalRecovery(string host, int port);

    [LoggerMessage(EventId = 6365, Level = LogLevel.Warning, Message = "Guider restart requested, but the profile's guider host {Host}:{Port} is not this machine — the local openastro-guider unit is not the guider in use; nothing restarted")]
    private partial void LogRemoteHostNoLocalRestart(string host, int port);
}
