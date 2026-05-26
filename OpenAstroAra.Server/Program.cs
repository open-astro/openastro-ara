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

        // §8.1 DI registrations (placeholder).
        // Each phase lands its own block here:
        //   Phase 6 — equipment services (ICameraService, ITelescopeService, ...)
        //   Phase 7 — sequence services (ISequenceService, ICaptureOrchestratorService)
        //   Phase 8 — image services (IImageDataFactory, IFrameRepository)
        //   Phase 9 — IWsBroadcaster + IWsEventChannel + dispatch worker
        // For now the scaffold deliberately registers nothing beyond framework defaults.

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
