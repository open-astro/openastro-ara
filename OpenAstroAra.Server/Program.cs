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
        app.MapGet("/healthz", () => Results.Ok(new {
            status = "ok",
            version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            api = "v1"
        }));

        // §60 meta endpoint — server identification + capabilities.
        app.MapGet("/api/v1/server/info", () => Results.Ok(new {
            name = "OpenAstroAra.Server",
            version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            api = "v1",
            tier = "scaffold"  // upgraded to "ready" once Phase 6-9 endpoints land
        }));

        app.Logger.LogInformation("OpenAstroAra.Server listening on :{Port}", port);
        app.Run();
    }

    private static int ResolvePort(Microsoft.Extensions.Configuration.IConfiguration config) {
        var envPort = System.Environment.GetEnvironmentVariable("OPENASTROARA_PORT");
        if (!string.IsNullOrWhiteSpace(envPort) && int.TryParse(envPort, out var p)) {
            return p;
        }
        var configured = config.GetValue<int?>("OpenAstroAra:Port");
        return configured ?? 5555;
    }
}
