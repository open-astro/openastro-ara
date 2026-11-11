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
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

// #1580 — first-time setup of an AlpacaBridge: its device catalog, the cameras plugged in, and registering a
// device. Reads are best-effort like the rest of this client; the add is not.
public sealed partial class AlpacaManagementClient {
    /// <summary>A body key that names a secret (a password, key or token). The daemon does not know the catalog's
    /// <c>secret</c> flags, so it treats these names as secret and keeps their values out of everything it writes.</summary>
    [GeneratedRegex("pass|secret|token|key|psk", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKeyName();

    private const string AddWarningLead = "Device registered successfully. Warning: ";

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lookup boundary: a server without a catalog (or one that is down) yields an empty list, which is how the wizard learns not to offer Add.")]
    public async Task<IReadOnlyList<JsonElement>> GetDeviceCatalogAsync(string host, int port, CancellationToken ct) {
        var uri = ManagementUri(host, port, "devicecatalog");
        try {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                LogLookupFailed(_logger, host, port, (int)response.StatusCode);
                return [];
            }
            return ParseCatalog(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            LogLookupThrew(_logger, host, port, ex);
            return [];
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort lookup boundary: no camera list is an empty list.")]
    public async Task<IReadOnlyList<AlpacaAttachedCamera>> GetAttachedCamerasAsync(string host, int port, string vendor, CancellationToken ct) {
        var uri = new Uri(ManagementUri(host, port, "attachedcameras").AbsoluteUri + "?vendor=" + Uri.EscapeDataString(vendor));
        try {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) {
                LogLookupFailed(_logger, host, port, (int)response.StatusCode);
                return [];
            }
            return ParseAttachedCameras(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            LogLookupThrew(_logger, host, port, ex);
            return [];
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The add boundary: any transport failure is reported as 'unconfirmed' (the device may or may not exist), never swallowed and never an unhandled 500.")]
    public async Task<AlpacaAddResult> AddDeviceAsync(string host, int port, JsonElement body, CancellationToken ct) {
        var uri = ManagementUri(host, port, "configuredevice");
        // The body goes out exactly as it came in. It is never logged: it may carry a secret.
        using var content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");
        try {
            using var response = await _addHttp.PostAsync(uri, content, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var result = ParseAddReply(text, body);
            LogAddAnswered(_logger, host, port, (int)response.StatusCode, result.Outcome);
            return result;
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            // Only the exception type: its message can carry request text.
            LogAddUnconfirmed(_logger, host, port, ex.GetType().Name);
            return new AlpacaAddResult(AlpacaAddOutcome.Unconfirmed, string.Empty, null);
        }
    }

    /// <summary>The drivers of a <c>devicecatalog</c> envelope whose <c>available</c> is true, each cloned as sent.
    /// Malformed JSON, or no <c>Value</c> array, is an empty list. Internal for direct unit testing.</summary>
    internal static IReadOnlyList<JsonElement> ParseCatalog(string json) {
        var drivers = new List<JsonElement>();
        try {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Array) {
                return drivers;
            }
            foreach (var driver in value.EnumerateArray()) {
                if (driver.ValueKind == JsonValueKind.Object
                        && driver.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.True) {
                    drivers.Add(driver.Clone());
                }
            }
        } catch (JsonException) {
            drivers.Clear();
        }
        return drivers;
    }

    /// <summary>The cameras of an <c>attachedcameras</c> envelope. A non-zero <c>ErrorNumber</c> (a vendor with no
    /// lister) or malformed JSON is an empty list. Internal for direct unit testing.</summary>
    internal static IReadOnlyList<AlpacaAttachedCamera> ParseAttachedCameras(string json) {
        var cameras = new List<AlpacaAttachedCamera>();
        try {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ErrorNumberOf(root) != 0
                    || !root.TryGetProperty("Value", out var value) || value.ValueKind != JsonValueKind.Array) {
                return cameras;
            }
            foreach (var entry in value.EnumerateArray()) {
                if (entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("Index", out var index) && index.TryGetInt32(out var i)) {
                    cameras.Add(new AlpacaAttachedCamera(i, Str(entry, "Name") ?? string.Empty, Str(entry, "Serial") ?? string.Empty));
                }
            }
        } catch (JsonException) {
            cameras.Clear();
        }
        return cameras;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int ErrorNumberOf(JsonElement root) =>
        root.TryGetProperty("ErrorNumber", out var e) && e.TryGetInt32(out var n) ? n : 0;

    /// <summary>A <c>configuredevice</c> answer: <c>ErrorNumber</c> 0 is Added (its <c>Value</c> text, and the part after
    /// "Warning: " as the warning); a non-zero one is Refused with <c>ErrorMessage</c>; a body that is not an Alpaca
    /// envelope is Unconfirmed. Secret values from <paramref name="request"/> are blanked out of the text. Internal for
    /// direct unit testing.</summary>
    internal static AlpacaAddResult ParseAddReply(string json, JsonElement request) {
        try {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ErrorNumber", out _)) {
                return new AlpacaAddResult(AlpacaAddOutcome.Unconfirmed, string.Empty, null);
            }
            if (ErrorNumberOf(root) != 0) {
                return new AlpacaAddResult(AlpacaAddOutcome.Refused, Redact(Str(root, "ErrorMessage") ?? string.Empty, request), null);
            }
            var message = Redact(Str(root, "Value") ?? string.Empty, request);
            var warning = message.StartsWith(AddWarningLead, StringComparison.Ordinal) ? message[AddWarningLead.Length..] : null;
            return new AlpacaAddResult(AlpacaAddOutcome.Added, message, warning);
        } catch (JsonException) {
            return new AlpacaAddResult(AlpacaAddOutcome.Unconfirmed, string.Empty, null);
        }
    }

    // shortcut: top-level string values under a secret-sounding key only; nested secrets (a record list) are not
    // searched. Upgrade when AlpacaBridge gets a driver whose secret sits in a nested record.
    private static string Redact(string text, JsonElement request) {
        if (request.ValueKind != JsonValueKind.Object) {
            return text;
        }
        foreach (var property in request.EnumerateObject()) {
            if (property.Value.ValueKind == JsonValueKind.String && SecretKeyName().IsMatch(property.Name)
                    && property.Value.GetString() is { Length: > 0 } secret) {
                text = text.Replace(secret, "***", StringComparison.Ordinal);
            }
        }
        return text;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "AlpacaBridge at {Host}:{Port} answered a device add with HTTP {Status}: {Outcome}.")]
    private static partial void LogAddAnswered(ILogger logger, string host, int port, int status, AlpacaAddOutcome outcome);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "AlpacaBridge at {Host}:{Port} gave no usable answer to a device add ({Reason}); the device may or may not have been added.")]
    private static partial void LogAddUnconfirmed(ILogger logger, string host, int port, string reason);
}
