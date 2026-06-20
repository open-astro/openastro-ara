#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Makaretu.Dns;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §32.4 — advertises the daemon on the LAN over mDNS as
/// <c>_openastroara._tcp.local</c> so WILMA's first-run scan discovers it: the
/// SRV record carries the host + bound port the client connects to, and the TXT
/// record carries uuid/nickname/version so the client can match the discovered
/// server to <c>/api/v1/server/info</c>.
///
/// <para>Best-effort by design: a responder that can't bind (no usable
/// interface, a sandbox/permission restriction, or a :5353 conflict the library
/// can't share) must never take the daemon down — the REST endpoint and the
/// manual host:port connect path still work; only the network scan stays dark.
/// Failures are logged at Warning and swallowed.</para>
///
/// <para>The <c>Zeroconf</c> package used elsewhere in the daemon is browse-only
/// (Alpaca device probes) and cannot publish a service, so advertising comes from
/// Makaretu.Dns.Multicast, which co-exists with the OS mDNSResponder/Avahi on
/// :5353 via its socket-reuse handling.</para>
/// </summary>
public sealed partial class MdnsAdvertiser : IHostedService {
    // Must match ServerDiscoveryService.serviceType on the client and the
    // mdns_service string in /api/v1/server/info (the library appends ".local").
    private const string ServiceType = "_openastroara._tcp";

    private readonly int _port;
    private readonly string _instanceName;
    private readonly ILogger<MdnsAdvertiser> _logger;

    private MulticastService? _mdns;
    private ServiceDiscovery? _discovery;
    private ServiceProfile? _profile;

    public MdnsAdvertiser(int port, ILogger<MdnsAdvertiser> logger) {
        // Surface a bad port explicitly rather than silently truncating to ushort below.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, ushort.MaxValue);
        _port = port;
        _logger = logger;
        // A DNS-SD instance name is a single DNS label, so collapse the dots a
        // hostname may carry (e.g. "Mac.lan" → "Mac-lan") — otherwise the library
        // would split it into multiple labels and malform the advertised name.
        var host = ServerIdentity.Nickname;
        _instanceName = string.IsNullOrWhiteSpace(host)
            ? "OpenAstroAra"
            : host.Replace('.', '-');
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        try {
            var mdns = new MulticastService();
            var discovery = new ServiceDiscovery(mdns);
            // Null addresses → the profile fills A/AAAA from the live interfaces.
            var profile = new ServiceProfile(_instanceName, ServiceType, (ushort)_port, addresses: null);
            profile.AddProperty("uuid", ServerIdentity.Uuid);
            profile.AddProperty("nickname", ServerIdentity.Nickname);
            profile.AddProperty("version", ServerIdentity.Version);
            discovery.Advertise(profile);
            mdns.Start();
            // Proactively announce so a client already passively listening populates
            // without waiting for its next active query.
            discovery.Announce(profile);
            _mdns = mdns;
            _discovery = discovery;
            _profile = profile;
            LogAdvertising(_instanceName, ServiceType, _port);
        }
#pragma warning disable CA1031 // best-effort discovery: any failure must not fault daemon startup
        catch (Exception ex) {
            LogAdvertiseFailed(ex);
            Cleanup();
        }
#pragma warning restore CA1031
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        try {
            // Goodbye packet so clients drop us promptly instead of waiting out the TTL.
            if (_discovery is not null && _profile is not null) {
                _discovery.Unadvertise(_profile);
            }
            _mdns?.Stop();
        }
#pragma warning disable CA1031 // shutdown best-effort: a responder hiccup must not block daemon stop
        catch (Exception ex) {
            LogStopFailed(ex);
        }
#pragma warning restore CA1031
        finally {
            Cleanup();
        }
        return Task.CompletedTask;
    }

    private void Cleanup() {
        _discovery?.Dispose();
        _mdns?.Dispose();
        _discovery = null;
        _mdns = null;
        _profile = null;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "mDNS advertising '{Instance}' as {ServiceType}.local on port {Port}.")]
    private partial void LogAdvertising(string instance, string serviceType, int port);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "mDNS advertisement failed; the daemon runs but won't appear in WILMA's network scan (use manual connect).")]
    private partial void LogAdvertiseFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "mDNS advertiser shutdown hit an error.")]
    private partial void LogStopFailed(Exception ex);
}
