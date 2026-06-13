#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAstroAra.TestHarness.Alpaca;

/// <summary>
/// A single way an <see cref="AlpacaFaultProxy"/> can make a forwarded Alpaca
/// request misbehave. The five modes are the transport-level vocabulary of the
/// §42.2 fault matrix: a device that errors, a device that returns an HTTP
/// failure, a device whose comms drop, a device that goes unresponsive, and a
/// device that never reports a commanded motion as finished.
/// </summary>
public abstract record AlpacaFault {
    /// <summary>
    /// Respond 200 with a well-formed Alpaca envelope whose <c>ErrorNumber</c> is
    /// non-zero — how a real ASCOM driver surfaces an operation failure (the
    /// ASCOM.Alpaca client turns this into a thrown <c>DriverException</c>).
    /// Use for "the mount refused the slew", "the focuser reported a fault", etc.
    /// </summary>
    public static AlpacaFault AlpacaError(int errorNumber, string message) =>
        new AlpacaErrorFault(errorNumber, message);

    /// <summary>Respond with a raw HTTP status code (e.g. 500/503) and no Alpaca body — a gateway/server fault.</summary>
    public static AlpacaFault HttpStatus(int statusCode) => new HttpStatusFault(statusCode);

    /// <summary>Abort the connection without completing a response — the client sees a reset socket (a comms drop).</summary>
    public static AlpacaFault Drop() => DropFault.Instance;

    /// <summary>
    /// Wait <paramref name="duration"/> before acting. With no <paramref name="then"/> the
    /// request is then served normally (a slow-but-healthy device); pair it with the
    /// client's receive timeout to model an unresponsive device, or chain a terminal
    /// fault (e.g. <c>Delay(…, Drop())</c>) for "hung then dropped".
    /// </summary>
    public static AlpacaFault Delay(TimeSpan duration, AlpacaFault? then = null) =>
        new DelayFault(duration, then);

    /// <summary>
    /// Forward to the upstream device, then overwrite the <c>Value</c> field of the
    /// returned Alpaca envelope with <paramref name="jsonValueLiteral"/> (a raw JSON
    /// token, e.g. <c>"true"</c>, <c>"42.0"</c>, <c>"\"slewing\""</c>). This is the
    /// "stuck" fault: rewrite a <c>Slewing</c>/<c>IsMoving</c> poll to stay <c>true</c>
    /// so a motion never settles, or pin a reported position short of its target.
    /// </summary>
    public static AlpacaFault RewriteValue(string jsonValueLiteral) {
        ArgumentNullException.ThrowIfNull(jsonValueLiteral);
        // Validate eagerly: a malformed literal would otherwise fall through the
        // proxy's body-parse guard and silently become a pass-through, so the fault
        // would appear to do nothing. Fail fast at construction instead.
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(jsonValueLiteral);
        } catch (JsonException ex) {
            throw new ArgumentException(
                $"'{jsonValueLiteral}' is not a valid JSON value literal (use e.g. \"true\", \"42\", \"\\\"text\\\"\").",
                nameof(jsonValueLiteral), ex);
        }
        // A JSON null parses fine but would *erase* the envelope's Value field (the
        // ASCOM client then deserializes a silent default). That's almost certainly a
        // mistake, not an intended fault — reject it explicitly.
        if (parsed is null) {
            throw new ArgumentException(
                "'null' erases the Value field rather than rewriting it — use a typed JSON literal.",
                nameof(jsonValueLiteral));
        }
        return new RewriteValueFault(jsonValueLiteral);
    }
}

/// <summary>A non-zero Alpaca <c>ErrorNumber</c>/<c>ErrorMessage</c> response. See <see cref="AlpacaFault.AlpacaError"/>.</summary>
public sealed record AlpacaErrorFault(int ErrorNumber, string Message) : AlpacaFault;

/// <summary>A raw HTTP status response. See <see cref="AlpacaFault.HttpStatus"/>.</summary>
public sealed record HttpStatusFault(int StatusCode) : AlpacaFault;

/// <summary>An aborted connection. See <see cref="AlpacaFault.Drop"/>.</summary>
public sealed record DropFault : AlpacaFault {
    /// <summary>The single shared instance — <see cref="DropFault"/> carries no state.</summary>
    public static readonly DropFault Instance = new();
}

/// <summary>A pre-response delay, optionally followed by another fault. See <see cref="AlpacaFault.Delay"/>.</summary>
public sealed record DelayFault(TimeSpan Duration, AlpacaFault? Then) : AlpacaFault;

/// <summary>A forwarded response with its <c>Value</c> overwritten. See <see cref="AlpacaFault.RewriteValue"/>.</summary>
public sealed record RewriteValueFault(string JsonValueLiteral) : AlpacaFault;

/// <summary>
/// Arms an <see cref="AlpacaFault"/> for requests matching an Alpaca device/method
/// selector. A <c>null</c> selector field matches anything, so an empty rule
/// (only <see cref="Fault"/> set) affects every forwarded request.
/// </summary>
public sealed class AlpacaFaultRule {
    /// <summary>Alpaca device type segment to match (e.g. <c>"telescope"</c>), case-insensitive; <c>null</c> = any.</summary>
    public string? DeviceType { get; init; }

    /// <summary>Alpaca device number to match; <c>null</c> = any.</summary>
    public int? DeviceNumber { get; init; }

    /// <summary>Alpaca method segment to match (e.g. <c>"slewtocoordinatesasync"</c>), case-insensitive; <c>null</c> = any.</summary>
    public string? Method { get; init; }

    /// <summary>HTTP verb to match (<c>"GET"</c>/<c>"PUT"</c>), case-insensitive; <c>null</c> = any.</summary>
    public string? HttpVerb { get; init; }

    /// <summary>The fault to apply to matching requests.</summary>
    public required AlpacaFault Fault { get; init; }

    /// <summary>
    /// How many matching requests to affect before the rule expires; <c>null</c> =
    /// every matching request until <see cref="AlpacaFaultProxy.ClearFaults"/>.
    /// </summary>
    public int? MaxTriggers { get; init; }
}
