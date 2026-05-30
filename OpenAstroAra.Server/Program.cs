#region "copyright"

/*
    Copyright © 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server;

/// <summary>
/// Headless ASP.NET Core daemon entry point per PORT_PLAYBOOK.md §8 (Phase 4 — server scaffold).
/// Listens on Kestrel; default port <c>5555</c> (overridable via <c>OPENASTROARA_PORT</c> env var
/// or <c>appsettings.json</c>). Discovers itself on the LAN via mDNS service type
/// <c>_openastroara._tcp.local</c> (per §32.4).
///
/// DI registrations follow the §8.1 mapping from NINA's CompositionRoot. They land incrementally
/// alongside the endpoints they support — this scaffold has only health + meta endpoints; equipment
/// (Phase 6), sequencer (Phase 7), images (Phase 8), and WebSocket stream (Phase 9) bring up the
/// rest.
/// </summary>
public class Program {

    public static void Main(string[] args) {
        var builder = WebApplication.CreateSlimBuilder(args);

        // Kestrel port: env var > appsettings > default 5555 (per §2.1).
        var port = ResolvePort(builder.Configuration);
        builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(port));

        // CORS for WILMA clients (per §60.7.1). Trusted LAN; permissive by design.
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        builder.Services.AddOpenApi();

        // §60.6 — enums on the wire serialize as all-lowercase strings (no
        // separators) so the OpenAPI DeviceType token set (`filterwheel`,
        // `covercalibrator`) matches both the URL path parameter and the JSON
        // payload field, and other enums (FrameType etc.) follow the same
        // convention. Properties use snake_case (standard JSON convention).
        builder.Services.ConfigureHttpJsonOptions(opts => {
            opts.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(LowerCaseNamingPolicy.Instance));
            opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        });

        // §8.1 DI registrations.
        // Phase 6 (this block): equipment services — IEquipmentDiscoveryService
        // implemented; per-device services (ICameraService etc.) declared but
        // not yet registered (endpoints return 501 until impls land).
        // Phase 7 — sequence services (ISequenceService, ICaptureOrchestratorService)
        // Phase 8 — image services (IImageDataFactory, IFrameRepository)
        // Phase 9 — IWsBroadcaster + IWsEventChannel + dispatch worker
        builder.Services.AddSingleton<IEquipmentDiscoveryService, AlpacaEquipmentDiscoveryService>();

        // Phase 12h.6a: §37 profile store. In-memory for v0.0.1; file-based
        // persistence lands in Phase 13 (settings survive daemon restart).
        builder.Services.AddSingleton<IProfileStore, InMemoryProfileStore>();

        var app = builder.Build();

        app.UseCors();

        // §49 API docs: Scalar UI (AOT-friendly Swagger replacement per §71.3).
        app.MapOpenApi();
        app.MapScalarApiReference();

        // Health endpoint for the §13 systemd readiness check + §15 build gate.
        // Per playbook §60.4, /healthz returns plain text "ok" with Cache-Control: no-store
        // so the systemd watchdog + load balancers don't cache stale liveness signals.
        app.MapGet("/healthz", (HttpContext http) => {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Text("ok", contentType: "text/plain");
        });

        // Phase 6 equipment endpoints (501 stubs except discovery).
        app.MapEquipmentEndpoints();

        // Phase 7 endpoint groups (501 stubs until service implementations land).
        app.MapSequenceEndpoints();
        app.MapCalibrationEndpoints();
        app.MapMosaicEndpoints();

        // Phase 8 endpoint groups (501 stubs until service implementations land).
        app.MapImageEndpoints();
        app.MapDiagnosticsEndpoints();

        // Phase 9 endpoint groups (501 stubs except /api/v1/ws/catalog which is
        // functional today). /api/v1/server/info already lives directly in this file.
        app.MapServerStateEndpoints();
        app.MapNotificationEndpoints();
        app.MapStatsEndpoints();
        app.MapSystemEndpoints();
        app.MapWebSocketEndpoints();

        // Phase 12h.6a: §37 profile endpoints. imaging-defaults is real
        // (in-memory store); other sections follow as 12h.6b-N adds DTOs +
        // section-specific endpoint pairs on top of the same IProfileStore.
        app.MapProfileEndpoints();

        // §60 meta endpoint — server identification + capabilities.
        // Lightweight identity payload per the playbook contract: server_uuid (stable per
        // install), nickname (user-set in profile, defaults to hostname), version, api,
        // mDNS service-type so the WILMA client can verify discovery matches the daemon
        // it's connected to.
        app.MapGet("/api/v1/server/info", () => Results.Ok(new {
            server_uuid = ServerIdentity.Uuid,
            nickname = ServerIdentity.Nickname,
            version = ServerIdentity.Version,
            api = "v1",
            mdns_service = "_openastroara._tcp.local",
            tier = "scaffold"  // upgraded to "ready" once Phase 6-9 endpoints land
        }));

        app.Logger.LogInformation("OpenAstroAra.Server listening on :{Port}", port);
        app.Run();
    }

    /// <summary>
    /// Resolve listen port. Order of precedence (per playbook §2.1):
    ///   1. <c>OPENASTROARA_PORT</c> env var
    ///   2. <c>OpenAstroAra:Port</c> in appsettings.json
    ///   3. Default 5555
    /// Range-validates the result; invalid values fall back to the default so a
    /// misconfigured env var can't crash startup before Serilog is wired.
    /// </summary>
    /// <summary>
    /// All-lowercase JSON naming policy used for enum-to-string serialization
    /// (e.g. <c>DeviceType.FilterWheel</c> → <c>"filterwheel"</c>). No
    /// underscores/dashes so the JSON token matches the URL path segment.
    /// </summary>
    private sealed class LowerCaseNamingPolicy : JsonNamingPolicy {
        public static readonly LowerCaseNamingPolicy Instance = new();
        public override string ConvertName(string name) => name.ToLowerInvariant();
    }

    private static int ResolvePort(Microsoft.Extensions.Configuration.IConfiguration config) {
        const int defaultPort = 5555;
        const int minPort = 1;
        const int maxPort = 65535;

        var envPort = System.Environment.GetEnvironmentVariable("OPENASTROARA_PORT");
        if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out var p) && p >= minPort && p <= maxPort) {
            return p;
        }
        var configured = config.GetValue<int?>("OpenAstroAra:Port");
        if (configured is int c && c >= minPort && c <= maxPort) {
            return c;
        }
        return defaultPort;
    }
}
