#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>#1580 — the setup wizard adds devices to a fresh AlpacaBridge through the daemon: read its device
/// catalog, list the cameras plugged in, and register a device. The daemon forwards; AlpacaBridge keeps the device
/// (Ara stores no copy). Host and port are the AlpacaBridge the Connect screen found, validated like every other
/// management call (<see cref="AlpacaManagementClient.ManagementUri"/>).</summary>
public static partial class AlpacaBridgeSetupEndpoints {
    /// <summary>This file's group under the equipment group (<c>/api/v1/equipment</c>).</summary>
    private const string Group = "/alpacabridge";

    /// <summary>The add route under <see cref="Group"/>.</summary>
    private const string AddRoute = "/devices";

    /// <summary>The add route's full path, for the stale-session guard test (the guard is middleware over every mutating
    /// /api/v1 route). Built from the constants the route is mapped with, so the test checks the path the route serves.</summary>
    internal const string AddPath = "/api/v1/equipment" + Group + AddRoute;

    public static void MapAlpacaBridgeSetup(this RouteGroupBuilder equipment) {
        ArgumentNullException.ThrowIfNull(equipment);
        var bridge = equipment.MapGroup(Group);
        bridge.MapGet("/catalog", async (string? host, int? port, IAlpacaManagementClient alpaca, CancellationToken ct) =>
                await GetCatalogAsync(host, port, alpaca, ct))
            .Produces<AlpacaBridgeCatalogResponseDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("GetAlpacaBridgeCatalog")
            .WithSummary("The built-in drivers of an AlpacaBridge, by device type, with their field schemas as AlpacaBridge sends them.");
        bridge.MapGet("/cameras", async (string? host, int? port, string? vendor, IAlpacaManagementClient alpaca, CancellationToken ct) =>
                await GetCamerasAsync(host, port, vendor, alpaca, ct))
            .Produces<AlpacaBridgeCamerasResponseDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithName("GetAlpacaBridgeCameras")
            .WithSummary("The cameras plugged in for one vendor; empty for a vendor with no lister.");
        bridge.MapPost(AddRoute, async (string? host, int? port, JsonElement body, IAlpacaManagementClient alpaca, CancellationToken ct) =>
                await AddDeviceAsync(host, port, body, alpaca, ct))
            .Produces<AlpacaBridgeAddDeviceResponseDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithName("AddAlpacaBridgeDevice")
            .WithSummary("Registers a device on AlpacaBridge: the body goes to configuredevice unchanged.");
    }

    private static IResult? BadTarget(string? host, int? port) {
        try {
            AlpacaManagementClient.ManagementUri(host ?? string.Empty, port ?? 0);
            return null;
        } catch (ArgumentException ex) {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    // A vendor key is a short token ("zwo"); it goes into a query string, so nothing else is let through.
    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex VendorToken();

    public static async Task<IResult> GetCatalogAsync(string? host, int? port, IAlpacaManagementClient alpaca, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(alpaca);
        if (BadTarget(host, port) is { } bad) {
            return bad;
        }
        var drivers = await alpaca.GetDeviceCatalogAsync(host!, port!.Value, ct).ConfigureAwait(false);
        var groups = new List<AlpacaBridgeDeviceTypeDto>();
        foreach (var byType in drivers.GroupBy(d =>
                     d.TryGetProperty("deviceType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty)
                     .Where(g => g.Key.Length > 0)) {
            groups.Add(new AlpacaBridgeDeviceTypeDto(byType.Key, byType.ToList()));
        }
        return Results.Ok(new AlpacaBridgeCatalogResponseDto(groups));
    }

    public static async Task<IResult> GetCamerasAsync(string? host, int? port, string? vendor, IAlpacaManagementClient alpaca, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(alpaca);
        if (BadTarget(host, port) is { } bad) {
            return bad;
        }
        if (vendor is null || !VendorToken().IsMatch(vendor)) {
            return Results.Problem("vendor is required (letters, digits, '-' and '_')", statusCode: StatusCodes.Status400BadRequest);
        }
        var cameras = await alpaca.GetAttachedCamerasAsync(host!, port!.Value, vendor, ct).ConfigureAwait(false);
        return Results.Ok(new AlpacaBridgeCamerasResponseDto(
            cameras.Select(c => new AlpacaBridgeCameraDto(c.Index, c.Name, c.Serial)).ToList()));
    }

    public static async Task<IResult> AddDeviceAsync(string? host, int? port, JsonElement body, IAlpacaManagementClient alpaca, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(alpaca);
        if (BadTarget(host, port) is { } bad) {
            return bad;
        }
        if (body.ValueKind != JsonValueKind.Object
                || !HasText(body, "vendor") || !HasText(body, "deviceType")) {
            return Results.Problem("the body must be an object with a vendor and a deviceType",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var result = await alpaca.AddDeviceAsync(host!, port!.Value, body, ct).ConfigureAwait(false);
        return result.Outcome switch {
            AlpacaAddOutcome.Added => Results.Ok(new AlpacaBridgeAddDeviceResponseDto(result.Message, result.Warning)),
            AlpacaAddOutcome.Refused => Results.Problem(
                type: "https://openastro.net/errors/alpacabridge-refused",
                title: "AlpacaBridge refused the device",
                detail: result.Message,
                statusCode: StatusCodes.Status422UnprocessableEntity),
            _ => Results.Problem(
                type: "https://openastro.net/errors/alpacabridge-unconfirmed",
                title: "AlpacaBridge did not confirm",
                detail: "AlpacaBridge did not answer in time. The device may or may not have been added: check its device list before trying again.",
                statusCode: StatusCodes.Status502BadGateway),
        };
    }

    private static bool HasText(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());
}