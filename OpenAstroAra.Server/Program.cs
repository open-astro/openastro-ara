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
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using OpenAstroAra.Server.Services;
using Scalar.AspNetCore;
using System.Text.Json;
using System.Text.Json.Serialization;

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
// CA1052: Program is referenced as a generic type argument (ILogger<Program>,
// and by WebApplicationFactory<Program> in tests), so it cannot be static even
// though all its members are.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1052:Static holder types should be Static or NotInheritable", Justification = "Used as a generic type argument by the ASP.NET host and test harness.")]
public partial class Program {

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
        // Phase 14a: AraJsonSerializerContext is inserted at the head of the
        // resolver chain so every DTO uses pre-generated type metadata. Per-
        // enum generic JsonStringEnumConverter<TEnum> registrations replace
        // the non-generic factory — each generic instantiation is AOT-
        // traceable, so no IL3050 suppression is needed even when full AOT
        // publish is enabled in CI.
        builder.Services.ConfigureHttpJsonOptions(opts => {
            opts.SerializerOptions.TypeInfoResolverChain.Insert(0, AraJsonSerializerContext.Default);
            var policy = LowerCaseNamingPolicy.Instance;
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<DeviceType>(policy));
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<EquipmentConnectionState>(policy));
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<SequenceRunState>(policy));
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<FrameType>(policy));
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<NotificationSeverity>(policy));
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<NotificationCategory>(policy));
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<DiagnosticHealth>(policy));
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<DiagnosticsMode>(policy));
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

        // §28 SqliteFrameRepository — reads frames from the catalog with
        // sample data seeded on first init. Bulk ops still return placeholder
        // Accepted responses; next sub-PR makes them actually mutate. Preview
        // + thumbnail still serve the 1×1 JPEG placeholder until §65 lands;
        // OpenDownloadAsync returns null until §72 FITS storage lands.
        builder.Services.AddSingleton<IFrameRepository, SqliteFrameRepository>();
        // §28 SqliteSessionService — sessions from the catalog with derived
        // fields (target name, frame counts, filters) aggregated from the
        // frames table at read time per §28.1's schema. Composes on
        // IFrameRepository for GetFramesAsync + GetHfrAnalysisAsync so
        // /api/v1/sessions/{id}/frames stays consistent with
        // /api/v1/frames?sessionId=…. Mutating endpoints (resume-target,
        // restretch) keep the placeholder Accepted shape until §38 + §65 land.
        builder.Services.AddSingleton<ISessionService, SqliteSessionService>();
        // Phase 13.4 — placeholder INotificationService so WILMA's §46 inbox +
        // §46.4 preferences view has wire shapes to render. Three sample
        // notifications (Info/Warning/Critical across Sequence/Storage/Safety
        // categories); preferences default to "everything enabled" matching §46.4.
        // §46.5 SqliteNotificationService — persistent log table + JSON-blob
        // preferences in app_config. EnsureSeededAsync runs after IAraDatabase
        // init for first-time seed of the 3 fixture notifications.
        builder.Services.AddSingleton<INotificationService, SqliteNotificationService>();
        // Phase 13.5 — placeholder IDiagnosticsService. Static fixtures
        // (Yellow health + 1 open issue + 3-event history); SetMode stores
        // in-memory. The §51 *operating* mode reported here (Off/Observe/
        // Suggest/AutoCorrect) is conceptually distinct from the §51.5
        // *settings* reaction mode (notify_only/pause_on_critical/
        // abort_on_critical) which round-trips via the profile store —
        // the real-infra phase reconciles the two when §51 monitor lands.
        // §51 SqliteDiagnosticsService — diagnostic_events table holds
        // both open issues (cleared_utc IS NULL) + historical events.
        // Mode persists in app_config across restart. Monitor worker that
        // *writes* events wires up alongside the §38 sequence orchestrator.
        builder.Services.AddSingleton<IDiagnosticsService, SqliteDiagnosticsService>();
        // Phase 13.6 — placeholder IStatsService covering all 8 §50 chart
        // views with synthetic fixture data. Numbers are intentionally small
        // so the Stats tab renders something sensible without claiming the
        // system has acquired 50 nights of data.
        // §50 SqliteStatsService — aggregations over the §28 catalog. Views
        // that need data not yet captured (focuser position, separated RA/Dec
        // RMS) return empty payloads; they wire up when §38 sequence
        // orchestrator persists those columns.
        builder.Services.AddSingleton<IStatsService, SqliteStatsService>();
        // Phase 13.7 — placeholder IServerStateService for the §60.4 state
        // snapshot + §33.2.1 versions + §54 release notes. Restart endpoints
        // throw (§13 systemd-watchdog work needed).
        builder.Services.AddSingleton<IServerStateService, PlaceholderServerStateService>();
        // §65.5 batch-job tracker — backs /jobs/{id} status + the
        // session-restretch worker. In-memory by design: jobs are
        // ephemeral, state resets on daemon restart.
        builder.Services.AddSingleton<IBatchJobService, InMemoryBatchJobService>();
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
        // land per-device in the real-infra phase + Phase 14.
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
        // ISequenceService swapped to FileSequenceService below after
        // profileDir is resolved (filesystem-backed per §38.2). Runtime control
        // (ISequencerService) still placeholder until the real §38 orchestrator
        // wires up the Sequencer library engine.
        // §38j-5 — sequencer reads the saved body for real instruction count.
        // Func<> resolver breaks the FileSequenceService ↔ PlaceholderSequencerService
        // construction-time cycle (both reference each other now).
        // §38j-6 — also writes active/current.json checkpoint per §28.1.
        // Registered later (after profileDir is resolved) so the checkpoint
        // dep can be constructed.
        // Phase 13.14 — calibration + dark library + mosaic placeholders.
        builder.Services.AddSingleton<ICalibrationService, PlaceholderCalibrationService>();
        builder.Services.AddSingleton<IDarkLibraryService, PlaceholderDarkLibraryService>();
        builder.Services.AddSingleton<IMosaicService, PlaceholderMosaicService>();
        // Phase 13.15 — sequence templates + NINA import + auto-flats.
        // ISequenceTemplateService + ISequenceImportService are wired later
        // (after profileDir is resolved) so they can use the §38.2 sequences
        // subdirs.
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

        // Phase 38a — §38.2 filesystem-backed sequence library at
        // {profileDir}/sequences/library/. Replaces the in-memory placeholder
        // so saved sequences survive daemon restart. §38j-4 — also injected
        // the optional ISequencerService so ListAsync can surface the
        // current run state per item (running/paused/etc badge).
        builder.Services.AddSingleton<ISequenceService>(sp =>
            new FileSequenceService(
                profileDir,
                sp.GetService<ISequencerService>(),
                sp.GetService<ILogger<FileSequenceService>>()));

        // §38j-6 — active-sequence checkpoint at
        // {profileDir}/sequences/active/current.json per §28.1 / §38.2.
        builder.Services.AddSingleton(sp =>
            new ActiveSequenceCheckpoint(
                profileDir,
                sp.GetService<ILogger<ActiveSequenceCheckpoint>>()));

        // §38j-5 + §38j-6 — sequencer registered here (post profileDir +
        // ActiveSequenceCheckpoint). Func<> resolver breaks the
        // FileSequenceService ↔ PlaceholderSequencerService construction-time
        // cycle (both reference each other now).
        // §38 — real sequence execution. SequencerService deserializes the saved
        // body and drives it through NINA's inherited Sequencer (full container
        // semantics); equipment is still the headless-stub set, so no-equipment
        // instructions run for real and equipment-bound ones no-op cleanly.
        builder.Services.AddSingleton<ISequencerService>(sp =>
            new SequencerService(
                sp.GetRequiredService<SequenceBodyDeserializer>(),
                sp.GetService<IWsBroadcaster>(),
                () => sp.GetService<ISequenceService>(),
                sp.GetService<ActiveSequenceCheckpoint>(),
                sp.GetService<ILogger<SequencerService>>()));
        // Register the SAME singleton as a hosted service so its IHostedService.StopAsync
        // cancels any in-flight sequence runs on daemon shutdown.
        builder.Services.AddHostedService(sp =>
            (SequencerService)sp.GetRequiredService<ISequencerService>());

        // §38.7 — disk-shipped templates under {profileDir}/sequences/templates/
        // merged on top of the 3 hardcoded built-ins. .deb install can drop
        // additional templates without a code change.
        builder.Services.AddSingleton<ISequenceTemplateService>(sp =>
            new PlaceholderSequenceTemplateService(
                sp.GetRequiredService<ISequenceService>(),
                profileDir,
                sp.GetService<ILogger<PlaceholderSequenceTemplateService>>()));

        // §38.4 — NINA import service persists the raw upload under
        // {profileDir}/sequences/imported/from-nina-YYYY-MM-DD/ for audit
        // and backfills schemaVersion when the source omits it.
        builder.Services.AddSingleton<ISequenceImportService>(sp =>
            new PlaceholderSequenceImportService(
                sp.GetRequiredService<ISequenceService>(),
                profileDir,
                sp.GetService<ILogger<PlaceholderSequenceImportService>>()));

        // §28 SQLite catalog. Scaffold-only at this point: the connection
        // + schema land here so subsequent sub-PRs can flip the placeholder
        // frame/session repositories over one method at a time. Profile
        // and catalog live in the same dir so a single OPENASTROARA_PROFILE_DIR
        // (or systemd StateDirectory=) configures both.
        builder.Services.AddSingleton<IAraDatabase>(sp =>
            new SqliteAraDatabase(profileDir, sp.GetService<ILogger<SqliteAraDatabase>>()));

        // §28.2 startup reconciler — checks for an active/current.json left
        // by a previous run that didn't shut down cleanly. Registered as
        // singleton so future hosted services can reference it; the actual
        // reconciliation call runs once during startup below.
        builder.Services.AddSingleton<SequenceStartupReconciler>();

        // §38k engine wiring — HeadlessSequencerFactory + SequenceBodyDeserializer.
        // The factory ships with structural container prototypes (§38k-3),
        // utility instructions (§38k-4), no-equipment conditions (§38k-7),
        // and the first equipment-bound instruction (§38k-9: WaitUntilSafe
        // backed by HeadlessSafetyMonitorMediator). Real Alpaca-backed
        // mediators swap in at this DI registration point as Phase 14e
        // simulator pinning lands; SequenceBodyDeserializer is safe to
        // consume now — unknown $type values still gracefully degrade to
        // UnknownSequenceContainer for unregistered instruction types.
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.ISafetyMonitorMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessSafetyMonitorMediator>();
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessTelescopeMediator>();
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IGuiderMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessGuiderMediator>();
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IFocuserMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessFocuserMediator>();
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.ICameraMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessCameraMediator>();
        // §38k-15 — DI-registered to complete the mediator surface; its
        // SwitchFilter instruction (needs IProfileService) lands in a follow-up.
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IFilterWheelMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessFilterWheelMediator>();
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IRotatorMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessRotatorMediator>();
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.ISwitchMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessSwitchMediator>();
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IDomeMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessDomeMediator>();
        // §38k-19/20 — last two device-mediator stubs complete the device set.
        // No instruction prototype consumes them yet (there are no flat-device /
        // weather sequence items, and the Connect dir — Connect*/Disconnect*/
        // SwitchProfile — is deferred: Connect*/SwitchProfile need IProfileService,
        // and the Disconnect* classes are `internal` in the Sequencer; they land
        // together as the Connect capstone). DI-registered so consumers can resolve
        // the full IDeviceMediator surface.
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IFlatDeviceMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessFlatDeviceMediator>();
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IWeatherDataMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessWeatherDataMediator>();
        // §38k-21 — IDomeFollower stub (non-mediator dome dependency of SynchronizeDome).
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.IDomeFollower,
            OpenAstroAra.Server.Services.Equipment.HeadlessDomeFollower>();
        // §38k-22 — HeadlessProfileService stub: lets the profile-bound instruction
        // prototypes (Dither / SwitchFilter / Connect / Disconnect / SwitchProfile)
        // construct. Returns a default in-memory profile; the real user-edited
        // profile (IProfileStore / profile.json) wires in with the execution engine.
        builder.Services.AddSingleton<OpenAstroAra.Profile.Interfaces.IProfileService,
            OpenAstroAra.Server.Services.HeadlessProfileService>();
        builder.Services.AddSingleton<OpenAstroAra.Sequencer.ISequencerFactory>(sp =>
            HeadlessSequencerFactory.WithDefaults(
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.ISafetyMonitorMediator>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IGuiderMediator>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IFocuserMediator>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.ICameraMediator>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IRotatorMediator>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.ISwitchMediator>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IDomeMediator>(),
                domeFollower: sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.IDomeFollower>(),
                filterWheelMediator: sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IFilterWheelMediator>(),
                flatDeviceMediator: sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IFlatDeviceMediator>(),
                weatherDataMediator: sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IWeatherDataMediator>(),
                profileService: sp.GetRequiredService<OpenAstroAra.Profile.Interfaces.IProfileService>()));
        builder.Services.AddSingleton<SequenceBodyDeserializer>();

        var app = builder.Build();

        app.UseCors();

        // §60.9 WebSocket support — must be registered before MapWebSocketEndpoints
        // so the framework can negotiate the protocol upgrade.
        //   KeepAliveInterval = 30s — server-initiated RFC 6455 ping cadence
        //   KeepAliveTimeout  = 60s — close the socket if no pong/data arrives
        //                              within this window (matches openapi.yaml
        //                              line 680: "client must pong within 60s",
        //                              2 consecutive missed pongs → server closes).
        // .NET 10's KeepAliveTimeout enforces the unresponsive-client teardown
        // automatically; the close code emitted by the framework is 1011
        // ("internal error") which matches the spec's §60.9 line 711 mapping
        // of 1011 to "unresponsive client".
        app.UseWebSockets(new WebSocketOptions {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            KeepAliveTimeout = TimeSpan.FromSeconds(60),
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

        // §65.5 / §60.5 background-job status endpoints.
        app.MapJobsEndpoints();

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

        // §28 catalog init — applies PRAGMAs + creates schema before any
        // request can land. Synchronous-blocking here is fine: we're still
        // on the startup path and the work is single-digit ms on a fresh
        // DB. A failure here is a hard-fail-fast (we can't recover from a
        // broken catalog), so let it propagate.
        var araDb = app.Services.GetRequiredService<IAraDatabase>();
        araDb.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

        // Seed the frame catalog with fixture data on first run. Idempotent
        // (skips if frames table already has rows) — survives daemon
        // restart with persistence intact.
        var frameRepo = app.Services.GetRequiredService<IFrameRepository>();
        if (frameRepo is SqliteFrameRepository sqliteRepo) {
            sqliteRepo.EnsureSeededAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        // §46.5 notifications fixture seed — same idempotent pattern.
        var notificationSvc = app.Services.GetRequiredService<INotificationService>();
        if (notificationSvc is SqliteNotificationService sqliteNotif) {
            sqliteNotif.EnsureSeededAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        // §51 diagnostics fixture seed — three events (one open issue + two
        // historical) so the panel has data to render before the monitor
        // worker is online.
        var diagnosticsSvc = app.Services.GetRequiredService<IDiagnosticsService>();
        if (diagnosticsSvc is SqliteDiagnosticsService sqliteDiag) {
            sqliteDiag.EnsureSeededAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        // §28.2 — reconcile any interrupted-sequence checkpoint left by a
        // previous run. Per the §28.2 policy ("do not auto-resume") the
        // reconciler clears the checkpoint and returns the previous state
        // so we can surface a §46 notification + (on Corrupt only) a §51
        // diagnostic event. Sits after both the notification and the
        // diagnostics seeds — each seed is no-op once any row exists, so
        // emit-before-seed would silently skip those fixtures.
        var reconcilerResult = app.Services
            .GetRequiredService<SequenceStartupReconciler>()
            .Reconcile();
        if (reconcilerResult.Outcome != SequenceReconcileOutcome.Clean) {
            var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
            LogReconciliation(startupLogger, reconcilerResult.Outcome, reconcilerResult.PreviousState?.SequenceId);
            try {
                var notif = StartupNotificationFactory.ForReconcilerResult(reconcilerResult);
                notificationSvc.CreateAsync(notif, CancellationToken.None).GetAwaiter().GetResult();
            } catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException) {
                LogNotificationEmitFailed(startupLogger, ex);
            }
            if (reconcilerResult.Outcome == SequenceReconcileOutcome.Corrupt) {
                try {
                    var (evt, rec, autoCorr) =
                        StartupNotificationFactory.DiagnosticForCorruptResult(reconcilerResult);
                    diagnosticsSvc.CreateEventAsync(evt, rec, autoCorr, CancellationToken.None)
                        .GetAwaiter().GetResult();
                } catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException) {
                    LogDiagnosticEmitFailed(startupLogger, ex);
                }
            }
        }

        // §28.8 startup orphan scan — sweep stale .tmp files + recover
        // orphan FITS into the catalog. On fresh installs (no captures
        // dir) this is a sub-ms no-op; once real captures from the §38
        // sequence orchestrator land, it auto-heals across daemon crashes.
        var captureScan = new CaptureScanService(
            app.Services.GetRequiredService<IProfileStore>(),
            app.Services.GetRequiredService<IAraDatabase>(),
            app.Services.GetService<ILogger<CaptureScanService>>());
        captureScan.RunAsync(CancellationToken.None).GetAwaiter().GetResult();

        LogListening(app.Logger, port);
        app.Run();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Startup reconciliation: {Outcome} (previous sequence: {SeqId})")]
    private static partial void LogReconciliation(ILogger logger, SequenceReconcileOutcome outcome, Guid? seqId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to emit §46 reconciliation notification")]
    private static partial void LogNotificationEmitFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to emit §51 checkpoint-corrupt diagnostic")]
    private static partial void LogDiagnosticEmitFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenAstroAra.Server listening on :{Port}")]
    private static partial void LogListening(ILogger logger, int port);

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

        // XDG fallback. Treat empty + root ($HOME=/) as unset — the
        // chiseled arm64 Docker base ships USER 1000 with $HOME=/ since
        // there's no /etc/passwd entry, so the naive ?? chain would
        // resolve to /.local/share/openastroara which UID 1000 can't
        // create. Fall through to the OS temp dir in that case.
        var home = System.Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(home) || home == "/") {
            home = System.Environment.GetEnvironmentVariable("USERPROFILE");
        }
        if (string.IsNullOrWhiteSpace(home) || home == "/") {
            home = Path.GetTempPath();
        }
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