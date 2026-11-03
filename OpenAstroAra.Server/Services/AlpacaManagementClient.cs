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
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>§63.20 / #1067 — the daemon reads an Alpaca server's management API on the client's
/// behalf (<c>GET http://host:port/management/v1/configureddevices</c>) so the setup wizard can label
/// the guider daemon's generic choice strings ("Alpaca Camera [host:port/1]") with the real device
/// names ("ZWO ASI290MM Mini"). The Flutter client used to call the Alpaca host directly — the one
/// place it bypassed the daemon to reach equipment, and a lookup that fails whenever the client
/// cannot route to the rig's LAN.</summary>
public interface IAlpacaManagementClient {
    /// <summary>Device names keyed <c>"&lt;devicetype&gt;/&lt;devicenumber&gt;"</c> (type lowercased,
    /// e.g. <c>"camera/1"</c>). Best-effort: any transport/parse failure yields an empty map — the
    /// caller falls back to the generic labels.</summary>
    Task<IReadOnlyDictionary<string, string>> GetConfiguredDeviceNamesAsync(string host, int port, CancellationToken ct);

    /// <summary>#1308 — the configured devices with their <c>UniqueID</c>, same keys as the names map.
    /// The UniqueID is what survives an AlpacaBridge renumber (ZWO cameras are numbered in SDK
    /// enumeration order); it is empty when the server reports none. Best-effort like the names.</summary>
    Task<IReadOnlyDictionary<string, AlpacaConfiguredDevice>> GetConfiguredDevicesAsync(string host, int port, CancellationToken ct);

    /// <summary>The server's own name, maker and version (<c>/management/v1/description</c>), or null when
    /// it doesn't answer. Best-effort like the names.</summary>
    Task<AlpacaServerDescription?> GetDescriptionAsync(string host, int port, CancellationToken ct) =>
        Task.FromResult<AlpacaServerDescription?>(null);

    /// <summary>A device's <c>driverversion</c> (<c>/api/v1/{type}/{number}/driverversion</c>), or null
    /// when it doesn't answer. Best-effort like the names.</summary>
    Task<string?> GetDriverVersionAsync(string host, int port, string type, int number, CancellationToken ct) =>
        Task.FromResult<string?>(null);
}

/// <summary>An Alpaca server's <c>description</c>: <c>ServerName</c>, <c>Manufacturer</c>, <c>ManufacturerVersion</c>.</summary>
public sealed record AlpacaServerDescription(string? ServerName, string? Manufacturer, string? Version);

/// <summary>One <c>configureddevices</c> entry: the name and the server's <c>UniqueID</c> ("" when absent),
/// plus the vendor SDK version, firmware and serial number an OpenAstro AlpacaBridge adds (null when not
/// reported; the serial sits in the entry's <c>Config</c>).</summary>
public sealed record AlpacaConfiguredDevice(string Name, string UniqueId, string? SdkVersion = null, string? Firmware = null,
    string? SerialNumber = null);

public sealed partial class AlpacaManagementClient : IAlpacaManagementClient, IDisposable {
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private readonly HttpClient _http;
    private readonly ILogger<AlpacaManagementClient> _logger;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "The handler's ownership transfers to the HttpClient (disposeHandler: true), which Dispose() releases.")]
    public AlpacaManagementClient(ILogger<AlpacaManagementClient>? logger = null, HttpMessageHandler? handler = null) {
        _logger = logger ?? NullLogger<AlpacaManagementClient>.Instance;
        // Plain http against a trusted-LAN (§52/§67) surface; only the JSON body is ever parsed, and
        // only names come out of it. Same handler-injection seam as PushChannelService (tests).
        _http = new HttpClient(handler ?? LanHandler(), disposeHandler: true) {
            Timeout = RequestTimeout,
            // A configureddevices envelope is a few KB; never buffer whatever a broken or hostile
            // host at that address streams inside the 3 s window (default cap is 2 GB).
            MaxResponseContentBufferSize = 1 << 20,
        };
    }

    // No redirects (the asked host is the only host we dial — same rule as the sky-data and backup
    // clients in Program.cs) and a bounded pooled-connection lifetime so a daemon that runs for
    // weeks re-resolves `rig.local` instead of pinning the first DNS answer.
    private static SocketsHttpHandler LanHandler() => new() {
        AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lookup boundary: an unreachable/slow/malformed Alpaca host must yield an empty name map (the generic labels stay), never an error toward the wizard. CA1031's log-and-recover boundary applies.")]
    public async Task<IReadOnlyDictionary<string, string>> GetConfiguredDeviceNamesAsync(string host, int port, CancellationToken ct) {
        var devices = await GetConfiguredDevicesAsync(host, port, ct).ConfigureAwait(false);
        return NamesOf(devices);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lookup boundary: an unreachable/slow/malformed Alpaca host must yield an empty map (the generic labels stay, the pin check is skipped), never an error toward the caller. CA1031's log-and-recover boundary applies.")]
    public async Task<IReadOnlyDictionary<string, AlpacaConfiguredDevice>> GetConfiguredDevicesAsync(string host, int port, CancellationToken ct) {
        var uri = ManagementUri(host, port);
        try {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                LogLookupFailed(_logger, host, port, (int)response.StatusCode);
                return EmptyDevices;
            }
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseConfiguredDevices(json);
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            LogLookupThrew(_logger, host, port, ex);
            return EmptyDevices;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lookup boundary: an unreachable/slow/malformed Alpaca host just has no version to show.")]
    public async Task<AlpacaServerDescription?> GetDescriptionAsync(string host, int port, CancellationToken ct) {
        var uri = ManagementUri(host, port, "description");
        try {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                LogLookupFailed(_logger, host, port, (int)response.StatusCode);
                return null;
            }
            return ParseDescription(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            LogLookupThrew(_logger, host, port, ex);
            return null;
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lookup boundary: a device that doesn't answer just has no driver version to show.")]
    public async Task<string?> GetDriverVersionAsync(string host, int port, string type, int number, CancellationToken ct) {
        // The device API sits beside the management one on the same server.
        var mgmt = ManagementUri(host, port, "description");
        var uri = new Uri(mgmt, $"/api/v1/{Uri.EscapeDataString(type)}/{number.ToString(CultureInfo.InvariantCulture)}/driverversion");
        try {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                LogLookupFailed(_logger, host, port, (int)response.StatusCode);
                return null;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty("Value", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            LogLookupThrew(_logger, host, port, ex);
            return null;
        }
    }

    /// <summary>Parse the <c>description</c> envelope; null when it carries no object. Internal for testing.</summary>
    internal static AlpacaServerDescription? ParseDescription(string json) {
        try {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Value", out var v) || v.ValueKind != JsonValueKind.Object) {
                return null;
            }
            static string? Str(JsonElement o, string name) =>
                o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            return new AlpacaServerDescription(Str(v, "ServerName"), Str(v, "Manufacturer"), Str(v, "ManufacturerVersion"));
        } catch (JsonException) {
            return null;
        }
    }

    private static readonly IReadOnlyDictionary<string, AlpacaConfiguredDevice> EmptyDevices = new Dictionary<string, AlpacaConfiguredDevice>();

    /// <summary>The <c>"type/number" → name</c> view of a devices map.</summary>
    internal static IReadOnlyDictionary<string, string> NamesOf(IReadOnlyDictionary<string, AlpacaConfiguredDevice> devices) {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, device) in devices) {
            names[key] = device.Name;
        }
        return names;
    }

    /// <summary>The <c>"type/number" → UniqueID</c> view of a devices map (entries without one are left out).</summary>
    internal static IReadOnlyDictionary<string, string> UniqueIdsOf(IReadOnlyDictionary<string, AlpacaConfiguredDevice> devices) {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, device) in devices) {
            if (!string.IsNullOrEmpty(device.UniqueId)) {
                ids[key] = device.UniqueId;
            }
        }
        return ids;
    }

    /// <summary>The management URL for one Alpaca server. Throws <see cref="ArgumentException"/> (→ 400)
    /// for an empty host or a port outside 1..65535 — validated at the edge so a bad request never
    /// dials. Internal for direct unit testing.</summary>
    internal static Uri ManagementUri(string host, int port, string endpoint = "configureddevices") {
        if (string.IsNullOrWhiteSpace(host)) {
            throw new ArgumentException("host is required", nameof(host));
        }
        if (port is < 1 or > 65535) {
            throw new ArgumentOutOfRangeException(nameof(port), port, "port must be 1..65535");
        }
        // A host is a DNS name or an IP literal, nothing else: a value carrying '/', '?', '@' or a
        // port would survive Uri.TryCreate and retarget the GET (review of #1074).
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown) {
            throw new ArgumentException($"'{host}' is not a valid host name or IP address", nameof(host));
        }
        // Bracket a bare IPv6 literal; hostnames/IPv4 pass through.
        var authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
        if (!Uri.TryCreate($"http://{authority}:{port.ToString(CultureInfo.InvariantCulture)}/management/v1/{endpoint}",
                UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp) {
            throw new ArgumentException($"'{host}' is not a valid host", nameof(host));
        }
        return uri;
    }

    /// <summary>The <c>"type/number" → name</c> parse. Internal for direct unit testing.</summary>
    internal static IReadOnlyDictionary<string, string> ParseConfiguredDeviceNames(string json) => NamesOf(ParseConfiguredDevices(json));

    /// <summary>Parse the Alpaca <c>configureddevices</c> envelope (<c>{"Value":[{DeviceType,
    /// DeviceNumber, DeviceName, UniqueID}, …]}</c>) into the <c>"type/number" → (name, UniqueID)</c>
    /// map. Entries missing type/number/name, or with an empty name, are skipped; a missing UniqueID
    /// reads as ""; malformed JSON yields an empty map. Internal for direct unit testing.</summary>
    internal static IReadOnlyDictionary<string, AlpacaConfiguredDevice> ParseConfiguredDevices(string json) {
        var names = new Dictionary<string, AlpacaConfiguredDevice>(StringComparer.Ordinal);
        JsonDocument doc;
        try {
            doc = JsonDocument.Parse(json);
        } catch (JsonException) {
            return names;
        }
        using (doc) {
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("Value", out var value)
                    || value.ValueKind != JsonValueKind.Array) {
                return names;
            }
            foreach (var entry in value.EnumerateArray()) {
                if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("DeviceType", out var type) || type.ValueKind != JsonValueKind.String
                        || !entry.TryGetProperty("DeviceNumber", out var number) || number.ValueKind != JsonValueKind.Number
                        || !entry.TryGetProperty("DeviceName", out var name) || name.ValueKind != JsonValueKind.String
                        || !number.TryGetInt32(out var deviceNumber)) {
                    continue;
                }
                var deviceName = name.GetString();
                if (string.IsNullOrEmpty(deviceName)) {
                    continue;
                }
                var uniqueId = entry.TryGetProperty("UniqueID", out var uid) && uid.ValueKind == JsonValueKind.String
                    ? uid.GetString() ?? string.Empty : string.Empty;
                static string? Str(JsonElement o, string key) =>
                    o.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                names[$"{type.GetString()!.ToLowerInvariant()}/{deviceNumber.ToString(CultureInfo.InvariantCulture)}"] =
                    new AlpacaConfiguredDevice(deviceName, uniqueId, Str(entry, "SdkVersion"), Str(entry, "Firmware"),
                        entry.TryGetProperty("Config", out var config) && config.ValueKind == JsonValueKind.Object
                            ? Str(config, "serialNumber") : null);
            }
        }
        return names;
    }

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Alpaca management lookup at {Host}:{Port} answered HTTP {Status}; generic device labels stay.")]
    private static partial void LogLookupFailed(ILogger logger, string host, int port, int status);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Alpaca management lookup at {Host}:{Port} failed; generic device labels stay.")]
    private static partial void LogLookupThrew(ILogger logger, string host, int port, Exception ex);

    public void Dispose() => _http.Dispose();
}
