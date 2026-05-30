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
        //
        // Phase 14a: AraJsonSerializerContext is inserted at the head of
        // the resolver chain so every DTO uses pre-generated type metadata
        // instead of falling back to reflection. This unblocks `dotnet run`
        // in Development mode (OpenAPI introspection was tripping over the
        // missing source-gen). The non-generic JsonStringEnumConverter +
        // LowerCaseNamingPolicy is still required because the AOT-safe
        // generic JsonStringEnumConverter<TEnum> doesn't support custom
        // naming policies — IL3050 is suppressed since this only matters
        // for full AOT publish (not yet enabled in CI per §14.3 deferred).
        builder.Services.ConfigureHttpJsonOptions(opts => {
            opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
#pragma warning disable IL3050
            opts.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(LowerCaseNamingPolicy.Instance));
#pragma warning restore IL3050
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

        // Phase 13.1 — placeholder IFrameRepository so the §65 preview/thumbnail
        // endpoints return a real (tiny gray) JPEG instead of 501-stubbing. Real
        // OpenCvSharp4-backed implementation lands in Phase 13.2+ alongside the
        // §28 frame catalog DB.
        builder.Services.AddSingleton<IFrameRepository, PlaceholderFrameRepository>();
        // Phase 13.3 — placeholder ISessionService composing on the frame repo
        // so the §40 Library + session-drilldown UI has consistent fixture data.
        builder.Services.AddSingleton<ISessionService, PlaceholderSessionService>();
        // Phase 13.4 — placeholder INotificationService so WILMA's §46 inbox +
        // §46.4 preferences view has wire shapes to render. Three sample
        // notifications (Info/Warning/Critical across Sequence/Storage/Safety
        // categories); preferences default to "everything enabled" matching §46.4.
        builder.Services.AddSingleton<INotificationService, PlaceholderNotificationService>();
        // Phase 13.5 — placeholder IDiagnosticsService. Static fixtures
        // (Yellow health + 1 open issue + 3-event history); SetMode stores
        // in-memory. The §51 *operating* mode reported here (Off/Observe/
        // Suggest/AutoCorrect) is conceptually distinct from the §51.5
        // *settings* reaction mode (notify_only/pause_on_critical/
        // abort_on_critical) which round-trips via the profile store —
        // Phase 13.x reconciles.
        builder.Services.AddSingleton<IDiagnosticsService, PlaceholderDiagnosticsService>();
        // Phase 13.6 — placeholder IStatsService covering all 8 §50 chart
        // views with synthetic fixture data. Numbers are intentionally small
        // so the Stats tab renders something sensible without claiming the
        // system has acquired 50 nights of data.
        builder.Services.AddSingleton<IStatsService, PlaceholderStatsService>();
        // Phase 13.7 — placeholder IServerStateService for the §60.4 state
        // snapshot + §33.2.1 versions + §54 release notes. Restart endpoints
        // throw (§13 systemd-watchdog work needed).
        builder.Services.AddSingleton<IServerStateService, PlaceholderServerStateService>();
        // Phase 13.8 — placeholder ILogService. Tail returns 8 fixture
        // entries; rotate accepts; download is 404 (no Serilog file sinks
        // wired yet — Phase 14 §29.9.2).
        builder.Services.AddSingleton<ILogService, PlaceholderLogService>();
        // Phase 13.9 — placeholder IBugReportService for the §54 "Send me a
        // bug report" UI. Prepare returns a synthetic ready record; download
        // is 404 (real ZIP bundling lands in Phase 14 §54.3).
        builder.Services.AddSingleton<IBugReportService, PlaceholderBugReportService>();
        // Phase 13.10 — three more system-service placeholders so the §36.2
        // Data Manager + §70 Profile Share + §44 Backup Stream surfaces are
        // testable end-to-end without the §28 catalog wired.
        builder.Services.AddSingleton<IDataManagerService, PlaceholderDataManagerService>();
        builder.Services.AddSingleton<IProfileShareService, PlaceholderProfileShareService>();
        builder.Services.AddSingleton<IBackupStreamService, PlaceholderBackupStreamService>();
        // Phase 13.11 — placeholder IBackupService for §43 ZIP snapshots.
        builder.Services.AddSingleton<IBackupService, PlaceholderBackupService>();
        // Phase 13.12 — placeholder equipment services for all 12 device
        // types (§52). All Gets return null → 404; Connects/Disconnects/
        // Actions return 202 OperationAccepted. Real ASCOM Alpaca drivers
        // land per-device in Phase 13.x / 14.
        builder.Services.AddSingleton<ICameraService, PlaceholderCameraService>();
        builder.Services.AddSingleton<ITelescopeService, PlaceholderTelescopeService>();
        builder.Services.AddSingleton<IFocuserService, PlaceholderFocuserService>();
        builder.Services.AddSingleton<IFilterWheelService, PlaceholderFilterWheelService>();
        builder.Services.AddSingleton<IRotatorService, PlaceholderRotatorService>();
        builder.Services.AddSingleton<IDomeService, PlaceholderDomeService>();
        builder.Services.AddSingleton<ISwitchService, PlaceholderSwitchService>();
        builder.Services.AddSingleton<IObservingConditionsService, PlaceholderObservingConditionsService>();
        builder.Services.AddSingleton<ISafetyMonitorService, PlaceholderSafetyMonitorService>();
        builder.Services.AddSingleton<IFlatDeviceService, PlaceholderFlatDeviceService>();
        builder.Services.AddSingleton<IGuiderService, PlaceholderGuiderService>();
        builder.Services.AddSingleton<IPolarAlignService, PlaceholderPolarAlignService>();
        // Phase 13.13 — §38 sequence CRUD + runtime control.
        builder.Services.AddSingleton<ISequenceService, PlaceholderSequenceService>();
        builder.Services.AddSingleton<ISequencerService, PlaceholderSequencerService>();
        // Phase 13.14 — calibration + dark library + mosaic placeholders.
        builder.Services.AddSingleton<ICalibrationService, PlaceholderCalibrationService>();
        builder.Services.AddSingleton<IDarkLibraryService, PlaceholderDarkLibraryService>();
        builder.Services.AddSingleton<IMosaicService, PlaceholderMosaicService>();
        // Phase 13.15 — sequence templates + NINA import + auto-flats.
        builder.Services.AddSingleton<ISequenceTemplateService, PlaceholderSequenceTemplateService>();
        builder.Services.AddSingleton<ISequenceImportService, PlaceholderSequenceImportService>();
        builder.Services.AddSingleton<IAutoFlatsService, PlaceholderAutoFlatsService>();
        // Phase 13.17 — §60.9 WS broadcaster + event channel placeholders.
        // Single InMemoryWsServices instance backs both interfaces so the
        // publish + consume sides share state. The /api/v1/ws upgrade
        // handler stays 501 until a separate sub-PR wires the real
        // WebSocket lifecycle on top of these services.
        builder.Services.AddSingleton<InMemoryWsServices>();
        builder.Services.AddSingleton<IWsBroadcaster>(sp => sp.GetRequiredService<InMemoryWsServices>());
        builder.Services.AddSingleton<IWsEventChannel>(sp => sp.GetRequiredService<InMemoryWsServices>());

        // §37 profile store. Phase 12h.6a introduced the in-memory impl;
        // Phase 12h.7 upgraded to FileProfileStore (settings survive daemon
        // restart). Profile path resolution:
        //   1. OPENASTROARA_PROFILE_DIR env var (e.g. for tests + dev runs)
        //   2. /var/lib/openastroara (matches §13 systemd unit StateDirectory=)
        //   3. ~/.local/share/openastroara as a per-user fallback
        var profileDir = ResolveProfileDir();
        builder.Services.AddSingleton<IProfileStore>(sp =>
            new FileProfileStore(profileDir, sp.GetService<ILogger<FileProfileStore>>()));

        var app = builder.Build();

        app.UseCors();

        // §60.9 WebSocket support — must be registered before MapWebSocketEndpoints
        // so the framework can negotiate the protocol upgrade. KeepAliveInterval
        // is the framework-level ping cadence; the §60.9 application-level
        // heartbeat (30s ping / 60s pong) layers on top of this in the handler.
        app.UseWebSockets(new WebSocketOptions {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });

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
        // Use the typed ServerInfoDto so source-gen handles the wire shape
        // (Phase 14a). An anonymous type here would force a reflection
        // fallback that can't run under AOT and breaks Development mode.
        app.MapGet("/api/v1/server/info", () => Results.Ok(new OpenAstroAra.Server.Contracts.ServerInfoDto(
            ServerUuid: ServerIdentity.Uuid,
            Nickname: ServerIdentity.Nickname,
            Version: ServerIdentity.Version,
            Api: "v1",
            MdnsService: "_openastroara._tcp.local",
            Tier: "scaffold"  // upgraded to "ready" once Phase 6-9 endpoints land
        )));

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

    /// <summary>
    /// Resolve where the §37 profile file lives. Order:
    ///   1. <c>OPENASTROARA_PROFILE_DIR</c> env var — tests + dev runs use this
    ///   2. <c>/var/lib/openastroara</c> — matches the §13 systemd unit's
    ///      <c>StateDirectory=openastroara</c> so the profile is owned by the
    ///      daemon user with locked-down perms
    ///   3. <c>~/.local/share/openastroara</c> — XDG-style per-user fallback
    ///      for developers running `dotnet run` outside systemd
    /// </summary>
    private static string ResolveProfileDir() {
        var envDir = System.Environment.GetEnvironmentVariable("OPENASTROARA_PROFILE_DIR");
        if (!string.IsNullOrWhiteSpace(envDir)) return envDir;

        const string systemDir = "/var/lib/openastroara";
        if (Directory.Exists(systemDir)) return systemDir;

        var home = System.Environment.GetEnvironmentVariable("HOME")
            ?? System.Environment.GetEnvironmentVariable("USERPROFILE")
            ?? Path.GetTempPath();
        return Path.Combine(home, ".local", "share", "openastroara");
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
