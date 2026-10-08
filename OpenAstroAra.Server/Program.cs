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
using Serilog;
using Serilog.Formatting.Compact;
using System.Text.Json;
using System.Text.Json.Serialization;
// `using Serilog` pulls in Serilog.ILogger, which collides with the bare
// `ILogger` parameter on the LoggerMessage partials below. Alias the bare name
// back to the MS abstraction (the generic ILogger<T> usages are unaffected).
using ILogger = Microsoft.Extensions.Logging.ILogger;

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
        var app = BuildApp(args);
        try {
            app.Run();
        } finally {
            // Flush the §29.9 Serilog file sink even when app.Run() unwinds via an
            // exception — the daemon's own logs are the first thing reached for
            // after a crash, so the last lines must not be lost.
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// Everything <see cref="Main"/> does before <c>app.Run()</c>: the §8.1 service graph, the
    /// middleware and endpoint maps, the startup reconciliation and the boot probes. Split out
    /// (#1128) so a test can build the REAL composition root — the one the daemon boots with — and
    /// resolve from it without listening on a port: #1089 found <c>IGuiderMediator</c> had silently
    /// reverted to the headless stub because nothing ever built this graph outside <c>Main</c>.
    /// Kestrel binds only in <c>Run</c>, so building is side-effect-free on the network; the profile
    /// directory (<c>OPENASTROARA_PROFILE_DIR</c>) is created and the catalog opened, as at boot.
    /// </summary>
    internal static WebApplication BuildApp(string[] args) {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions {
            Args = args,
            // The OpenAPI generator tags untagged routes (/healthz, /server/info) with the
            // application name; pin it so the openapi.yaml snapshot reads the same from the
            // daemon and from any test host (#1131).
            ApplicationName = "OpenAstroAra.Server",
        });

        // Kestrel port: env var > appsettings > default 5555 (per §2.1).
        var port = ResolvePort(builder.Configuration);
        builder.WebHost.ConfigureKestrel(opts => opts.ListenAnyIP(port));

        // CORS for WILMA clients (per §60.7.1). Trusted LAN; permissive by design.
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        // §49 — the document is served at /openapi/v1.json and snapshotted into
        // openapi.yaml by OpenApiContractSnapshotTest (#1131); keep the info block
        // fixed so the snapshot does not churn with the host process or release.
        builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) => {
            doc.Info.Title = "OpenAstro Ara REST API";
            doc.Info.Version = "v1";
            return Task.CompletedTask;
        }));

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
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<FilterKind>(policy));
            opts.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        });

        // §8.1 DI registrations.
        // Phase 6 (this block): equipment services — IEquipmentDiscoveryService
        // implemented; per-device services (ICameraService etc.) declared but
        // not yet registered (endpoints return 501 until impls land).
        // Phase 7 — sequence services (ISequenceService, ICaptureOrchestratorService)
        // Phase 8 — image services (IImageDataFactory, IFrameRepository)
        // Phase 9 — IWsBroadcaster + IWsEventChannel + dispatch worker
        builder.Services.AddSingleton<AlpacaEquipmentDiscoveryService>();
        // #1298 — every filter-wheel listing also offers the driverless manual filter wheel.
        builder.Services.AddSingleton<IEquipmentDiscoveryService>(sp =>
            new ManualDeviceDiscoveryService(sp.GetRequiredService<AlpacaEquipmentDiscoveryService>()));

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
        // §54 push channels — forwards Warning+ notifications to Pushover/Telegram when the
        // profile carries both of a channel's values; inert otherwise.
        builder.Services.AddSingleton<PushChannelService>();
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
        // §59.9 — autofocus triggers defer while diagnostics carries an open sky-condition
        // issue (clouds_passing / aperture_blocked / dew_formation); the user is notified
        // once per episode. Fail-open: a broken diagnostics read never freezes focusing.
        builder.Services.AddSingleton<OpenAstroAra.Sequencer.Interfaces.IAutofocusConditionGate>(sp =>
            new DiagnosticsAutofocusGate(
                sp.GetRequiredService<IDiagnosticsService>(),
                sp.GetRequiredService<INotificationService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DiagnosticsAutofocusGate>>()));
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
        // §27 single-client policy — owns the controlling session slot, the takeover
        // dance (connection.request/response over WS), and holder liveness.
        builder.Services.AddSingleton<ClientSessionService>();
        // §28 — image plate-solving. The factory builds the configured solver backend (ASTAP / Platesolve);
        // PlateSolveService wraps the image-in → solution-out path. The §28 centering loop + §58.4 flip
        // recenter build on this.
        builder.Services.AddSingleton<OpenAstroAra.PlateSolving.Interfaces.IPlateSolverFactory, OpenAstroAra.PlateSolving.PlateSolverFactoryProxy>();
        builder.Services.AddSingleton<OpenAstroAra.Server.Services.IPlateSolveService, OpenAstroAra.Server.Services.PlateSolveService>();
        // §28 centering — slew → solve → sync → re-slew loop over live equipment (the §58.4 flip recenter uses it).
        builder.Services.AddSingleton<OpenAstroAra.Server.Services.ICenteringService, OpenAstroAra.Server.Services.CenteringService>();
        // §58.4 — the real meridian-flip orchestration (stop guiding → pass meridian → flip slew → recenter →
        // resume guiding), replacing the throwing placeholder. Handed into the sequencer factory below so the
        // MeridianFlipTrigger prototype runs it. Mount-gated to live-validate.
        builder.Services.AddSingleton<OpenAstroAra.Sequencer.Trigger.MeridianFlip.IMeridianFlipExecutor,
            OpenAstroAra.Server.Services.MeridianFlipExecutor>();
        // §65.5 batch-job tracker — backs /jobs/{id} status + the
        // session-restretch worker. In-memory by design: jobs are
        // ephemeral, state resets on daemon restart.
        builder.Services.AddSingleton<IBatchJobService, InMemoryBatchJobService>();
        // §29.9 ILogService and §54 IBugReportService are registered below, after
        // profileDir is resolved (both are disk-backed under {profileDir}/).
        // Phase 13.10 — three more system-service placeholders so the §36.2
        // Data Manager + §70 Profile Share + §44 Backup Stream surfaces are
        // testable end-to-end without the §28 catalog wired.
        // §36 IDataManagerService is registered below, after profileDir is resolved (the real
        // DataManagerService is disk-backed under {profileDir}/sky-data).
        // §70 export renders the real profile-share-v1 template (strips equipment /
        // calibration / secrets / paths); import preview+commit remain placeholder
        // until the import sub-PR. Depends on IProfileRepository (registered below).
        builder.Services.AddSingleton<IProfileShareService, ProfileShareService>();
        // §44 — real backup stream: single-target slot + pending queue + ack over the
        // frames catalog (sha256 lazily cached per frame on first queue serve).
        builder.Services.AddSingleton<IBackupStreamService, BackupStreamService>();
        // §43 IBackupService is registered below, after profileDir is resolved (the real BackupService is
        // disk-backed: snapshots live under {profileDir}/backups).
        // Phase 13.12 — placeholder equipment services for all 12 device
        // types (§52). All Gets return null → 404; Connects/Disconnects/
        // Actions return 202 OperationAccepted. Real ASCOM Alpaca drivers
        // land per-device in the real-infra phase + Phase 14.
        // §60.9 — the equipment.* connection events (state_changed + the
        // connected/disconnected/connection_failed aliases). Every device
        // service below takes this via its optional ctor param and publishes
        // from its SetState choke point.
        builder.Services.AddSingleton<EquipmentEventPublisher>();
        // §42.2 — the one place detected equipment faults converge (log + equipment.fault WS
        // broadcast). The §42.3 connection probes in the device services below publish into it via
        // their optional IEquipmentFaultSink ctor param — injected by constructor activation
        // exactly like EquipmentEventPublisher above (factory-lambda registrations pass it by hand,
        // the #711 lesson).
        // §42.5 — persisted fault history. The registry mirrors the sequencer's
        // CaptureSessionScope enter/exit so fault rows get session attribution from
        // watch/timer contexts; the hub persists every detection and the §42.3
        // reaction service stamps its outcome onto the same row. Constructor
        // activation injects both optional deps.
        builder.Services.AddSingleton<ActiveRunSessionRegistry>();
        // Singleton on purpose: the service sweeps stale request/result files from the storage
        // exchange in its constructor (#1135), which is only safe because exactly one instance is
        // built — resolved eagerly after Build() below, so the sweep really runs at boot and
        // before any request can be in flight. Do not make this scoped/transient.
        builder.Services.AddSingleton<IStorageDeviceService, StorageDeviceService>();
        // §33 client-pushed update (#1122): same root-helper escalation shape as storage.
        builder.Services.AddSingleton(new ServerListenPort(port));
        builder.Services.AddSingleton<IServerUpdateService, ServerUpdateService>();
        // Registered (not just constructed at startup) so POST /storage/rescan
        // can run the same scan on demand — see the endpoint for why that
        // matters once a user can change disks without restarting.
        builder.Services.AddSingleton<CaptureScanService>();
        builder.Services.AddSingleton<IFaultLogService, SqliteFaultLogService>();
        builder.Services.AddSingleton<EquipmentFaultHub>();
        builder.Services.AddSingleton<IEquipmentFaultSink>(sp => sp.GetRequiredService<EquipmentFaultHub>());
        // §14e — ninth real device service: live mount (RA/Dec + tracking/parked/home) + slew/sync,
        // park/unpark, set-tracking, abort-slew. One singleton backs BOTH the REST ITelescopeService
        // and the Sequencer's ITelescopeMediator (§8.1), so the telescope instructions drive the live
        // device (mediator wiring is below; this replaces the HeadlessTelescopeMediator stub).
        builder.Services.AddSingleton<TelescopeService>();
        builder.Services.AddSingleton<ITelescopeService>(sp => sp.GetRequiredService<TelescopeService>());
        // §14e — fourth real device service: live focuser (position/temp) + Move. One singleton
        // backs BOTH the REST IFocuserService and the Sequencer's IFocuserMediator (§8.1), so the
        // MoveFocuser* instructions drive the live device (the mediator wiring is below; this
        // replaces the HeadlessFocuserMediator stub).
        builder.Services.AddSingleton<FocuserService>();
        builder.Services.AddSingleton<IFocuserService>(sp => sp.GetRequiredService<FocuserService>());
        // §14e — sixth real device service: live filter wheel (slots + current position) + change
        // slot. One singleton backs BOTH the REST IFilterWheelService and the Sequencer's
        // IFilterWheelMediator (§8.1), so SwitchFilter drives the live device (mediator wiring is
        // below; this replaces the HeadlessFilterWheelMediator stub). On connect the wheel's filter
        // list imports into the active profile so SwitchFilter resolves filters by name/position.
        builder.Services.AddSingleton<FilterWheelService>();
        // #1298 — the router fronts the Alpaca wheel and the driverless manual wheel (registered
        // below, once the profile dir is known); it is THE IFilterWheelService and mediator.
        builder.Services.AddSingleton<FilterWheelRouter>();
        builder.Services.AddSingleton<IFilterWheelService>(sp => sp.GetRequiredService<FilterWheelRouter>());
        // §14e — fifth real device service: live rotator (mechanical/sky angle) + Move. REST-only;
        // One singleton backs BOTH the REST IRotatorService and the Sequencer's IRotatorMediator
        // (§8.1), so MoveRotatorMechanical drives the live device (mediator wiring is below; this
        // replaces the HeadlessRotatorMediator stub).
        builder.Services.AddSingleton<RotatorService>();
        builder.Services.AddSingleton<IRotatorService>(sp => sp.GetRequiredService<RotatorService>());
        // §14e — eighth real device service: live dome (azimuth + shutter/home/park) + slew, park,
        // open/close shutter. One singleton backs BOTH the REST IDomeService and the Sequencer's
        // IDomeMediator (§8.1), so the dome instructions drive the live device (mediator wiring is
        // below; this replaces the HeadlessDomeMediator stub).
        builder.Services.AddSingleton<DomeService>();
        builder.Services.AddSingleton<IDomeService>(sp => sp.GetRequiredService<DomeService>());
        // §14e — third real device service (first with a control action: SetValue). One singleton
        // backs BOTH the REST ISwitchService and the Sequencer's ISwitchMediator (§8.1), so the
        // SetSwitchValue instruction drives the live device (mediator wiring is below; this replaces
        // the HeadlessSwitchMediator stub).
        // #1065 — explicit factory: the fan-off interlock probes the camera's cooler state through
        // a Func<> (breaks the CameraService ↔ SwitchService construction cycle), which constructor
        // activation would not inject.
        builder.Services.AddSingleton<SwitchService>(sp =>
            new SwitchService(
                sp.GetRequiredService<ILogger<SwitchService>>(),
                sp.GetService<EquipmentEventPublisher>(),
                sp.GetService<IEquipmentFaultSink>(),
                sp.GetService<IProfileStore>(),
                sp.GetService<IWsBroadcaster>(),
                cameraProbe: () => sp.GetService<ICameraService>()));
        builder.Services.AddSingleton<ISwitchService>(sp => sp.GetRequiredService<SwitchService>());
        builder.Services.AddSingleton<ICoolingFanActuator>(sp => sp.GetRequiredService<SwitchService>());
        // §14e — second real device service: live weather sensors over REST (read-only, §32.4
        // cached). REST-only — no sequence instruction consumes the weather mediator's data, so
        // IWeatherDataMediator stays the headless stub.
        builder.Services.AddSingleton<IObservingConditionsService, ObservingConditionsService>();
        // §14e — first real Alpaca-backed device service (others remain placeholders
        // until each device's connect path lands). Connects to a discovered Alpaca
        // SafetyMonitor and reports live state + IsSafe; covered by the
        // alpaca-sim-integration CI job. One singleton backs BOTH the REST
        // ISafetyMonitorService and the Sequencer's ISafetyMonitorMediator (§8.1), so
        // WaitUntilSafe reads the live device (the mediator wiring is below; this
        // replaces the HeadlessSafetyMonitorMediator stub).
        builder.Services.AddSingleton<SafetyMonitorService>();
        builder.Services.AddSingleton<ISafetyMonitorService>(sp => sp.GetRequiredService<SafetyMonitorService>());
        // §14e — seventh real device service: live flat device / CoverCalibrator (cover + light)
        // + apply. REST-only; the mediator unification is a follow-up.
        builder.Services.AddSingleton<IFlatDeviceService, FlatDeviceService>();
        // §63.3 guider-d — crash detection + auto-restart of the sibling openastro-guider unit.
        builder.Services.AddSingleton<IGuiderProcessSupervisor, SystemctlGuiderProcessSupervisor>();
        builder.Services.AddSingleton<GuiderRecoveryCoordinator>();
        // §63 — one GuiderService singleton backs both the REST IGuiderService and the Sequencer's
        // IGuiderMediator (§8.1; the mediator alias is registered below, replacing HeadlessGuiderMediator).
        builder.Services.AddSingleton<GuiderService>(sp => {
            var guider = new GuiderService(
                sp.GetRequiredService<OpenAstroAra.Profile.Interfaces.IProfileService>(),
                sp.GetRequiredService<GuiderRecoveryCoordinator>(),
                sp.GetRequiredService<ILogger<GuiderService>>(),
                sp.GetRequiredService<IGuiderProcessSupervisor>(),
                sp.GetService<IWsBroadcaster>(),
                // §42.2 fault-flow deps — factory lambda bypasses constructor
                // activation, so optional params are passed by hand (#711 lesson);
                // the sequencer resolver breaks the construction cycle.
                sp.GetService<IProfileStore>(),
                () => sp.GetService<ISequencerService>(),
                sp.GetService<INotificationService>(),
                sp.GetService<IFaultLogService>(),
                // §30.7.4 — lets the calibration-state stamp detect a profile switch mid-build
                // (profile select swaps the live store without touching the guider).
                () => sp.GetService<IProfileRepository>()?.ActiveId,
                // §63.4 — the ARA profile identity (id + user-visible name) the guider twin is
                // named after. The Equipment layer's legacy store is always "Default"; the
                // repository holds the real name ("RC91 - Backyard").
                () => {
                    var repo = sp.GetService<IProfileRepository>();
                    var id = repo?.ActiveId;
                    if (repo is null || id is null) {
                        return null;
                    }
                    var name = repo.List().Profiles.FirstOrDefault(p => p.Id == id.Value)?.Name;
                    return (id.Value, name);
                },
                // #1234 — §29 diagnostics for a remote guider that never returns.
                sp.GetService<IDiagnosticsService>());
            // A sequence's Start Guiding takes the guide camera back from a running live-focus loop
            // (Setup → Smart Focus → Guide camera) — the loop holds the guider's polar-align lease
            // and the guider refuses to guide under it. Resolved lazily: the focus service needs
            // this very guider to construct.
            guider.ReleaseGuideCameraAsync = async () => {
                if (sp.GetService<IGuideFocusService>() is { IsActive: true } focus) {
                    await focus.StopAsync().ConfigureAwait(false);
                }
            };
            // ...but never the lease a running polar alignment holds (resolved lazily for the same reason).
            guider.PolarAlignActive = () => sp.GetService<IPolarAlignService>() is PolarAlignService { IsActive: true };
            return guider;
        });
        builder.Services.AddSingleton<IGuiderService>(sp => sp.GetRequiredService<GuiderService>());
        // §45 — the polar-align engine: the guider supplies capture + the PA-session lease, the frame
        // solver runs ASTAP with the guide optics, the telescope mediator drives the seed RA slew +
        // tracking hand-back, and the profile store supplies the site for the pole-error geometry.
        builder.Services.AddSingleton<IPolarAlignFrameSolver>(sp =>
            new PolarAlignFrameSolver(
                sp.GetRequiredService<OpenAstroAra.Profile.Interfaces.IProfileService>(),
                sp.GetRequiredService<IProfileStore>(),
                sp.GetRequiredService<OpenAstroAra.PlateSolving.Interfaces.IPlateSolverFactory>()));
        builder.Services.AddSingleton<IPolarAlignmentLog>(sp =>
            new SqlitePolarAlignmentLog(sp.GetRequiredService<IAraDatabase>()));
        // §45 capture-fetch — one shared HttpClient for the daemon's capture endpoint.
        builder.Services.AddSingleton<IPolarAlignFrameFetcher, HttpPolarAlignFrameFetcher>();
        builder.Services.AddSingleton<IPolarAlignService>(sp =>
            new PolarAlignService(
                sp.GetRequiredService<GuiderService>(),
                sp.GetRequiredService<ILogger<PolarAlignService>>(),
                sp.GetRequiredService<IPolarAlignFrameSolver>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>(),
                sp.GetRequiredService<IProfileStore>(),
                sp.GetService<IWsBroadcaster>(),
                sp.GetRequiredService<IPolarAlignmentLog>(),
                sp.GetRequiredService<IPolarAlignFrameFetcher>(),
                () => sp.GetService<IGuideFocusService>()));
        // Setup → Smart Focus: the guide-camera focus loop (frames through the guider, measured here).
        builder.Services.AddSingleton<IGuideFrameDecoder, CfitsioGuideFrameDecoder>();
        builder.Services.AddSingleton<IGuideFocusService>(sp =>
            new GuideFocusService(
                sp.GetRequiredService<GuiderService>(),
                sp.GetRequiredService<IPolarAlignFrameFetcher>(),
                sp.GetRequiredService<IGuideFrameDecoder>(),
                () => (sp.GetService<IPolarAlignService>() as PolarAlignService)?.IsActive ?? false,
                sp.GetRequiredService<ILogger<GuideFocusService>>(),
                syntheticFrames: sp.GetService<SyntheticGuideFrames>() is { } synthetic ? synthetic.Next : null,
                optics: () => GuideOpticsFor(sp.GetRequiredService<IProfileStore>())));
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
        // §39 — calibration sessions + matching-flats + the dark library all derive live from
        // the §28 SQLite catalog (replaced the Phase 13.14 fixture placeholders); builds
        // generate runnable §38 sequences. Mosaics stay a placeholder (§47). The flats
        // generator reads the §48.7 flat_panel policy for its FlatPanelFlats leaves.
        builder.Services.AddSingleton<ICalibrationService>(sp => new SqliteCalibrationService(
            sp.GetRequiredService<IAraDatabase>(),
            sp.GetRequiredService<ISequenceService>(),
            sp.GetRequiredService<IProfileStore>()));
        builder.Services.AddSingleton<IDarkLibraryService, SqliteDarkLibraryService>();
        builder.Services.AddSingleton<IMosaicService, PlaceholderMosaicService>();
        // Phase 13.15 — sequence templates + NINA import + auto-flats.
        // ISequenceTemplateService + ISequenceImportService are wired later
        // (after profileDir is resolved) so they can use the §38.2 sequences
        // subdirs.
        // §48 — the SequencerService singleton also serves the auto-flats decision
        // seam (§8.1 one-singleton pattern), replacing the Phase-13.15 placeholder.
        builder.Services.AddSingleton<IAutoFlatsService>(sp => sp.GetRequiredService<SequencerService>());
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
        // #1298 — the manual filter wheel remembers the installed filter under the profile dir.
        builder.Services.AddSingleton(sp => new ManualFilterWheelService(
            sp.GetService<ILogger<ManualFilterWheelService>>(),
            sp.GetService<IProfileStore>(),
            sp.GetService<OpenAstroAra.Profile.Interfaces.IProfileService>(),
            sp.GetService<EquipmentEventPublisher>(),
            sp.GetService<INotificationService>(),
            profileDir));

        // §29.9.2 — wire the rolling CLEF (Compact-JSON) file sink now that the
        // profile dir is known. The §29.9 log endpoints (LogService) tail +
        // download these files under {profileDir}/logs/. RenderedCompactJsonFormatter
        // writes the final message into `@m`, so the tail reads it straight back
        // with System.Text.Json (no reader dependency, AOT-clean). The sink rolls
        // daily and on a 50 MB size cap, retaining 14 files. The default sink opens
        // the file FileShare.Read, so LogService can read it while the daemon writes.
        var logsDir = Path.Combine(profileDir, "logs");
        Directory.CreateDirectory(logsDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                formatter: new RenderedCompactJsonFormatter(),
                path: Path.Combine(logsDir, "openastroara-.log"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 50L * 1024 * 1024,
                retainedFileCountLimit: 14)
            .CreateLogger();
        builder.Host.UseSerilog();
        // §29.9 real ILogService over the rolling CLEF files (replaces the
        // former Phase 13.8 placeholder) — needs logsDir.
        builder.Services.AddSingleton<ILogService>(sp =>
            new LogService(logsDir, sp.GetRequiredService<ILogger<LogService>>()));
        // §54 real IBugReportService — bundles logs + profile.json + system info into a
        // ZIP under {profileDir}/bug-reports/ (replaces the former placeholder).
        builder.Services.AddSingleton<IBugReportService>(sp =>
            new BugReportService(profileDir, sp.GetRequiredService<ILogger<BugReportService>>()));

        builder.Services.AddSingleton<IProfileStore>(sp =>
            new FileProfileStore(profileDir, sp.GetService<ILogger<FileProfileStore>>()));
        // §37 multi-profile repository — the known-profiles set (§30) layered over the
        // active-profile store. Seeds the legacy single profile.json as the initial
        // profile on first run and loads the active profile into the live store at boot.
        builder.Services.AddSingleton<IProfileRepository>(sp =>
            new FileProfileRepository(profileDir, sp.GetRequiredService<IProfileStore>(),
                sp.GetService<ILogger<FileProfileRepository>>()));

        // §36 Data Manager — real disk-backed inventory under {profileDir}/sky-data (replaces the
        // placeholder). Registered here (not at the §13.10 placeholder site above) since it needs the
        // resolved profileDir. The §36-2 download engine extends this same service.
        // No HttpClient.Timeout: with ResponseHeadersRead it would otherwise bound the wait for response headers
        // (default 100s), failing a download against a slow-to-start CDN. A download is bounded instead by its job's
        // CancellationToken (POST /cancel), which the worker observes through the whole fetch + extract.
        builder.Services.AddHttpClient(HttpSkyDataFetcher.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            // Don't auto-follow redirects: the HTTPS scheme guard only checks the initial URL, so a CDN
            // HTTPS→HTTP downgrade redirect would otherwise serve the body in cleartext. Our catalog URLs are
            // direct, so a redirect is unexpected — let it surface as a non-success status (→ failed download)
            // rather than silently following it. If redirects are ever needed, re-validate the Location scheme.
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<ISkyDataFetcher, HttpSkyDataFetcher>();
        // §63.20 / #1067 — the wizard's Alpaca device-name lookup, proxied through the daemon.
        builder.Services.AddSingleton<IAlpacaManagementClient>(sp =>
            new AlpacaManagementClient(sp.GetService<ILogger<AlpacaManagementClient>>()));
        var skyDataRoot = System.IO.Path.Combine(profileDir, "sky-data");
        // §36-2 startup polish: reclaim any .staging-*/.backup-* scratch dirs orphaned by a download worker
        // hard-killed mid-extract (a daemon crash) — a graceful drain can't catch that case. Best-effort + synchronous
        // (a handful of dirs); real package dirs are untouched (catalog ids never start with '.').
        SkyDataInstaller.SweepStaleScratch(skyDataRoot);
        builder.Services.AddSingleton<DataManagerService>(sp =>
            new DataManagerService(skyDataRoot,
                sp.GetRequiredService<ISkyDataFetcher>(),
                sp.GetRequiredService<IWsBroadcaster>(),
                sp.GetRequiredService<ILogger<DataManagerService>>()));
        builder.Services.AddSingleton<IDataManagerService>(sp => sp.GetRequiredService<DataManagerService>());
        // The same singleton as a hosted service so its StopAsync drains in-flight downloads on a
        // graceful daemon stop (§36-2b(b)) — cancel + await, so the workers' own finally paths
        // reclaim their staging dirs instead of abandoning them for the next boot's sweep.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<DataManagerService>());
        // §36 Catalogs — derives Messier/NGC/IC + type-filter overlays from the installed OpenNGC catalog.
        builder.Services.AddSingleton<ISkyCatalogService>(_ => new SkyCatalogService(skyDataRoot));

        // §43-1 backup — real disk-backed ZIP snapshots under {profileDir}/backups (replaces the placeholder).
        // Registered here (not at the §13.11 placeholder site) since it needs the resolved profileDir. The §43-2
        // restore worker + progress state machine extend this same service.
        // §43-2b(b) — the remote-source fetcher. Redirects stay off (mirrors sky-data: the scheme guard checks
        // only the initial URL); no client timeout — the download is bounded by the shutdown token + byte cap.
        builder.Services.AddHttpClient(HttpBackupSourceFetcher.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<IBackupSourceFetcher, HttpBackupSourceFetcher>();
        // §31 time-sync — the waterfall state machine; the clock setter needs CAP_SYS_TIME on the
        // binary (DEPLOY.md setcap step) and degrades to offset tracking without it. Location
        // pushes land in the profile site settings every lat/long consumer already reads.
        builder.Services.AddSingleton<TimeSyncService>(sp =>
            new TimeSyncService(
                sp.GetRequiredService<ILogger<TimeSyncService>>(),
                clockSetter: null,
                profiles: sp.GetService<IProfileStore>()));
        builder.Services.AddSingleton<ITimeSyncService>(sp => sp.GetRequiredService<TimeSyncService>());
        // §31.1 step 2 — the USB-GPS self-sync worker (probe /dev/ttyUSB*/ttyACM*, parse NMEA,
        // apply gps-internal/high syncs without WILMA involvement).
        builder.Services.AddHostedService(sp =>
            new UsbGpsTimeSyncWorker(
                sp.GetRequiredService<TimeSyncService>(),
                sp.GetRequiredService<ILogger<UsbGpsTimeSyncWorker>>()));
        // §37.4/§29 — the save-directory picker's server-side directory walk.
        builder.Services.AddSingleton<IStorageBrowseService, StorageBrowseService>();
        builder.Services.AddSingleton<IBackupService>(sp =>
            new BackupService(profileDir, sp.GetRequiredService<ILogger<BackupService>>(),
                // Explicit: this factory lambda bypasses constructor activation, so optional params are passed
                // by hand (the #711 CameraService lesson).
                restorer: null,
                remoteFetcher: sp.GetRequiredService<IBackupSourceFetcher>(),
                // §43-2b retention — read live per create (a settings change applies without a restart).
                profiles: sp.GetService<IProfileStore>(),
                // §43-2 async create — backup.create.* progress events.
                ws: sp.GetService<IWsBroadcaster>()));

        // §36/§25.5 Tonight's Sky — ranks the OpenNGC catalog by altitude (with visibility window,
        // transit, and integration hours) for the active profile's site; falls back to a starter list
        // when openngc-dso isn't installed.
        // §36 Planning horizon — projects the site's local horizon onto the equatorial sky for a client overlay.
        // §55.1 multi-device WILMA settings sync — opaque UI-preferences blob under {profileDir}/client-settings.json.
        // profileDir-scoped, so registered here alongside the other profile-dir-backed services.
        builder.Services.AddSingleton<IClientSettingsService>(sp =>
            new ClientSettingsService(profileDir, sp.GetRequiredService<ILogger<ClientSettingsService>>()));

        // §52.1 — remembers the last device connected per type under
        // {profileDir}/equipment-selection.json so EquipmentAutoConnectService can
        // re-establish it on boot. Written at the connect chokepoint (ConnectGatedAsync).
        builder.Services.AddSingleton<IEquipmentSelectionStore>(sp =>
            new EquipmentSelectionStore(profileDir, sp.GetRequiredService<ILogger<EquipmentSelectionStore>>()));

        // §52.1 — connects remembered devices without re-discovery; shared by auto-connect-on-boot
        // (EquipmentAutoConnectService) and the manual POST /equipment/{type}/reconnect endpoints.
        builder.Services.AddSingleton<IEquipmentReconnector, EquipmentReconnector>();

        // §14e — tenth real device service and the head of the capture path: live Alpaca camera
        // (caps + cooler/state runtime) whose StartExposure runs a REAL capture — exposure →
        // ImageReady → ImageArray download → §72 FITS write (atomic §28.7) → §28 catalog insert —
        // so the existing preview/thumbnail/download endpoints serve the new frame immediately.
        // Registered here (not with the other device services above) because it needs the
        // profileDir-scoped IFrameRepository/IProfileStore. One singleton backs the REST
        // ICameraService AND the Sequencer's ICameraMediator + IImagingMediator (§14e PRb), so the
        // TakeExposure instruction captures through the same pipeline as the REST endpoint.
        builder.Services.AddSingleton<CameraService>(sp =>
            new CameraService(
                sp.GetRequiredService<ILogger<CameraService>>(),
                sp.GetRequiredService<IFrameRepository>(),
                sp.GetRequiredService<IProfileStore>(),
                fallbackFramesDir: System.IO.Path.Combine(profileDir, "frames"),
                // §38: snapshot the connected focuser's position at capture so the
                // FITS FOCUSPOS header + catalog column feed the §50.4 view.
                focuser: sp.GetService<OpenAstroAra.Equipment.Interfaces.Mediator.IFocuserMediator>(),
                // §60.9 — explicit here because this factory lambda bypasses the
                // constructor activation that injects the optional param for the
                // other device services. Forgetting it silences camera events.
                events: sp.GetRequiredService<EquipmentEventPublisher>(),
                // §59.5 — post-capture star analysis feeds the session history the
                // HFR-drift autofocus trigger reads.
                imageHistory: sp.GetRequiredService<ImageHistoryService>(),
                // §42.2/§42.3 — explicit for the same reason as events above: this factory
                // lambda bypasses constructor activation, and forgetting it would silence
                // camera disconnect faults.
                faults: sp.GetRequiredService<IEquipmentFaultSink>(),
                // §29.2 — SQM/ambient into every frame's header when a
                // weather source is connected.
                weather: sp.GetService<IObservingConditionsService>(),
                // #1065 — the cooling fan follows the cooler (Func<>: construction-cycle breaker).
                fan: () => sp.GetService<ICoolingFanActuator>(),
                // §28 — the legacy profile the plate-solve capture's wrapped IImageData carries for
                // render paths the solve loop never takes; optional (Func<>: registered later in
                // this file).
                legacyProfile: () => sp.GetService<OpenAstroAra.Profile.Interfaces.IProfileService>(),
                // §29.2 — the mount's RA/Dec at readout for the OBJCTRA/OBJCTDEC/RA/DEC cards
                // (Func<>: the telescope mediator is registered later in this file).
                telescope: () => sp.GetService<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>()));
        builder.Services.AddSingleton<ICameraService>(sp => sp.GetRequiredService<CameraService>());
        // §59 — the autofocus sweep's probe-capture seam rides the same singleton (same device
        // path + same in-flight capture gate as real captures; probes are never persisted).
        var syntheticSky = builder.Environment.IsDevelopment()
            ? Environment.GetEnvironmentVariable(SyntheticSky.EnvVar)
            : null;
        if (syntheticSky is not null) {
            // Development only — a rendered star field in place of camera frames so the focus instruments
            // can be exercised without a sky (SyntheticSky). Logged loudly at startup below.
            var options = SyntheticSky.Parse(syntheticSky);
            builder.Services.AddSingleton<SyntheticSkySettings>(options);
            builder.Services.AddSingleton<IAnalysisFrameSource>(sp =>
                new SyntheticSkyFrameSource(
                    sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IFocuserMediator>(),
                    options,
                    sp.GetRequiredService<ILogger<SyntheticSkyFrameSource>>()));
            builder.Services.AddSingleton<SyntheticGuideFrames>();
        } else {
            builder.Services.AddSingleton<IAnalysisFrameSource>(sp => sp.GetRequiredService<CameraService>());
        }
        // §59.5 — session image/autofocus history: the sweep records completed runs, the
        // autofocus trigger family reads them (temperature delta + HFR trend are both measured
        // "since the last autofocus"). In-memory by design — triggers reason about the current
        // session; the §28 frames catalog owns the durable record.
        builder.Services.AddSingleton<ImageHistoryService>();
        builder.Services.AddSingleton<OpenAstroAra.Sequencer.Interfaces.IImageHistory>(sp =>
            sp.GetRequiredService<ImageHistoryService>());
        // §59.12 — the run record the Setup tab's Smart Focus pane reads (GET /api/v1/autofocus/state) and the
        // cancel seam (POST /api/v1/autofocus/cancel). One per daemon: one sweep runs at a time.
        builder.Services.AddSingleton<AutofocusRunTracker>();
        // §59 — the live autofocus V-curve sweep (probe → HFR → curve fit → move-to-best).
        builder.Services.AddSingleton<OpenAstroAra.Sequencer.SequenceItem.Autofocus.IAutofocusExecutor>(sp =>
            new AutofocusSweepService(
                sp.GetRequiredService<IProfileStore>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IFocuserMediator>(),
                sp.GetRequiredService<IAnalysisFrameSource>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AutofocusSweepService>>(),
                history: sp.GetRequiredService<ImageHistoryService>(),
                filterWheel: sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IFilterWheelMediator>(),
                ws: sp.GetRequiredService<IWsBroadcaster>(),
                notifications: sp.GetRequiredService<INotificationService>(),
                travelRange: ct => AutofocusSweepService.FocuserTravelAsync(sp.GetRequiredService<IFocuserService>(), ct),
                tracker: sp.GetRequiredService<AutofocusRunTracker>(),
                focuserStepUm: ct => AutofocusSweepService.FocuserStepUmAsync(sp.GetRequiredService<IFocuserService>(), ct)));
        // §48.3 — the auto-exposure flat set (panel light → probe-to-ADU → saved FLAT frames).
        builder.Services.AddSingleton<OpenAstroAra.Sequencer.SequenceItem.FlatDevice.IFlatCaptureExecutor>(sp =>
            new FlatCaptureService(
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FlatCaptureService>>(),
                sp.GetRequiredService<IAnalysisFrameSource>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IImagingMediator>(),
                sp.GetRequiredService<IFlatDeviceService>()));

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
        // One SequencerService instance, exposed under three registrations
        // (concrete + ISequencerService + IHostedService) so there's no concrete
        // cast — a future swap of the ISequencerService impl can't break the
        // hosted-service registration with an InvalidCastException at startup.
        // §58.12 — the unattended-shutdown countdown. Func<ISequencerService>
        // resolver breaks the construction cycle with SequencerService (which
        // notifies this service on awaiting-user entry; this service re-reads
        // run state before firing the ladder).
        builder.Services.AddSingleton<UnattendedShutdownService>(sp =>
            new UnattendedShutdownService(
                sp.GetService<IProfileStore>(),
                () => sp.GetService<ISequencerService>(),
                sp.GetService<OpenAstroAra.Equipment.Interfaces.Mediator.IGuiderMediator>(),
                sp.GetService<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>(),
                sp.GetService<ICameraService>(),
                sp.GetService<IFilterWheelService>(),
                sp.GetService<IFocuserService>(),
                sp.GetService<IRotatorService>(),
                sp.GetService<IFlatDeviceService>(),
                sp.GetService<INotificationService>(),
                sp.GetService<ILogger<UnattendedShutdownService>>()));
        // Hosted so a daemon shutdown cancels a pending countdown.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<UnattendedShutdownService>());

        builder.Services.AddSingleton<SequencerService>(sp =>
            new SequencerService(
                sp.GetRequiredService<SequenceBodyDeserializer>(),
                sp.GetService<IWsBroadcaster>(),
                () => sp.GetService<ISequenceService>(),
                sp.GetService<ActiveSequenceCheckpoint>(),
                sp.GetService<ILogger<SequencerService>>(),
                // §40 — explicit: this factory lambda bypasses constructor
                // activation, so the optional param must be passed by hand
                // (the #711 CameraService lesson).
                sp.GetService<IFrameRepository>(),
                // §58.12 — same lesson: pass the countdown service by hand.
                sp.GetService<UnattendedShutdownService>(),
                // §48 — auto-flats prompt flow deps (profile default, the §39.5
                // generator via resolver to avoid a construction cycle, notifications).
                sp.GetService<IProfileStore>(),
                () => sp.GetService<ICalibrationService>(),
                sp.GetService<INotificationService>(),
                // §42.5 — same lesson: the run-session registry by hand.
                sp.GetService<ActiveRunSessionRegistry>(),
                // §38.10 — resume-refinement deps (re-center + refocus before the
                // gate releases), resolver-shaped like the §35 centering seam.
                () => sp.GetService<ICenteringService>(),
                () => sp.GetService<OpenAstroAra.Sequencer.SequenceItem.Autofocus.IAutofocusExecutor>()));
        builder.Services.AddSingleton<ISequencerService>(sp => sp.GetRequiredService<SequencerService>());
        // The same singleton as a hosted service so its IHostedService.StopAsync
        // cancels any in-flight sequence runs on daemon shutdown.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SequencerService>());

        // §35.4 — safety-reaction engine: polls the connected SafetyMonitor and on a
        // safe→unsafe transition executes the profile's on_unsafe policy (pause/abort
        // + stop guiding + park), with the auto-resume-when-safe countdown. The
        // sequencer resolver breaks the construction cycle (same pattern as §58.12).
        builder.Services.AddSingleton<SafetyReactionService>(sp =>
            new SafetyReactionService(
                sp.GetService<ISafetyMonitorService>(),
                sp.GetService<IObservingConditionsService>(),
                sp.GetService<IProfileStore>(),
                () => sp.GetService<ISequencerService>(),
                sp.GetService<IGuiderService>(),
                sp.GetService<ITelescopeService>(),
                sp.GetService<INotificationService>(),
                sp.GetService<IWsBroadcaster>(),
                sp.GetService<ILogger<SafetyReactionService>>(),
                // §35 auto-resume pointing — resolver (not a direct dep) for the same
                // construction-cycle reason as the sequencer.
                () => sp.GetService<ICenteringService>()));
        // Hosted so the poll timer starts with the daemon and a shutdown cancels a
        // pending auto-resume countdown.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SafetyReactionService>());

        // §42.3 — equipment-fault reaction: subscribes to the EquipmentFaultHub and executes
        // the FaultPolicyMatrix plan per fault (pause-first for camera/mount, hot-reconnect
        // ladder via IEquipmentReconnector, resume on recovery, pause/abort+park on give-up).
        // Same optional-dep + sequencer-resolver pattern as the safety-reaction engine.
        builder.Services.AddSingleton<FaultReactionService>(sp =>
            new FaultReactionService(
                sp.GetService<EquipmentFaultHub>(),
                sp.GetService<IEquipmentReconnector>(),
                sp.GetService<IProfileStore>(),
                () => sp.GetService<ISequencerService>(),
                sp.GetService<ITelescopeService>(),
                sp.GetService<INotificationService>(),
                sp.GetService<IWsBroadcaster>(),
                sp.GetService<ILogger<FaultReactionService>>(),
                // §42.5 — the reaction outcome lands on the fault-log row.
                sp.GetService<IFaultLogService>()));
        // Hosted so the hub subscription is armed with the daemon and a shutdown cancels
        // any in-flight reconnect ladder.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<FaultReactionService>());

        // §35.3 — emergency stop (abort runs → abort exposure → stop guiding →
        // park → flat light off), behind POST /api/v1/server/emergency-stop.
        // Same optional-dep + sequencer-resolver pattern as the reaction engine.
        builder.Services.AddSingleton<EmergencyStopService>(sp =>
            new EmergencyStopService(
                sp.GetService<ICameraService>(),
                () => sp.GetService<ISequencerService>(),
                sp.GetService<IGuiderService>(),
                sp.GetService<ITelescopeService>(),
                sp.GetService<IFlatDeviceService>(),
                sp.GetService<INotificationService>(),
                sp.GetService<IWsBroadcaster>(),
                sp.GetService<ILogger<EmergencyStopService>>()));

        // §29 — background disk-space monitor: warns (diagnostic + OnDiskSpaceLow notification) when the image
        // save volume runs low so an unattended session doesn't silently die on a full disk. Warn-only.
        builder.Services.AddHostedService<DiskSpaceMonitor>();
        // §42.5 — fault-log retention sweep (#1145): drops fault rows older than storage.fault_log_retention_days
        // two minutes after start, then once a day. 0 keeps everything.
        builder.Services.AddHostedService<FaultLogRetentionService>();
        builder.Services.AddHostedService<StorageDeviceWatcher>();

        // §65.4 — background thumbnail warmer: renders any missing
        // .thumb.jpg sidecars after boot so a freshly imported archive
        // browses instantly instead of paying a full FITS decode per tile.
        builder.Services.AddHostedService<ThumbnailWarmerService>();

        // §32.4 — advertise the daemon over mDNS (_openastroara._tcp) on the bound
        // port so WILMA's first-run scan discovers it. Best-effort: the service
        // swallows responder failures so it can never block startup.
        builder.Services.AddHostedService(sp =>
            new MdnsAdvertiser(port, sp.GetRequiredService<ILogger<MdnsAdvertiser>>()));

        // §52.1 — on boot, re-establish each remembered device whose profile
        // auto-connect bool is set (through the §68 bridge gate). Best-effort: a
        // device failing never blocks the others or startup.
        builder.Services.AddHostedService<EquipmentAutoConnectService>();

        // §38.7 — disk-shipped templates under {profileDir}/sequences/templates/
        // merged on top of the 3 hardcoded built-ins. .deb install can drop
        // additional templates without a code change.
        builder.Services.AddSingleton<ISequenceTemplateService>(sp =>
            new PlaceholderSequenceTemplateService(
                sp.GetRequiredService<ISequenceService>(),
                profileDir,
                sp.GetService<ILogger<PlaceholderSequenceTemplateService>>()));

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
        // §14e — SafetyMonitor's mediator is the REAL service (registered above), so
        // WaitUntilSafe reads the live Alpaca device. The other devices remain headless
        // stubs until their real services land.
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.ISafetyMonitorMediator>(
            sp => sp.GetRequiredService<SafetyMonitorService>());
        // §14e — the real TelescopeService backs ITelescopeMediator too (replaces
        // HeadlessTelescopeMediator), so SetTracking, Park/UnparkScope, FindHome and
        // SlewScopeToRaDec drive the live Alpaca mount.
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>(
            sp => sp.GetRequiredService<TelescopeService>());
        // §63 guider-c — the real GuiderService backs IGuiderMediator too (replaces
        // HeadlessGuiderMediator), so StartGuiding / StopGuiding / Dither and the flip
        // executor's guide pause/resume drive the live PHD2 link instead of no-op stubs
        // that reported success while nothing guided.
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IGuiderMediator>(
            sp => sp.GetRequiredService<GuiderService>());
        // §14e — the real FocuserService backs IFocuserMediator too (replaces HeadlessFocuserMediator),
        // so the MoveFocuser* sequence instructions drive the live Alpaca focuser.
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IFocuserMediator>(
            sp => sp.GetRequiredService<FocuserService>());
        // §14e PRb — the real CameraService backs ICameraMediator + IImagingMediator too (replaces
        // HeadlessCameraMediator), so TakeExposure validates against the live camera and captures
        // through the §14e pipeline into the §28 catalog.
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.ICameraMediator>(
            sp => sp.GetRequiredService<CameraService>());
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IImagingMediator>(
            sp => sp.GetRequiredService<CameraService>());
        // §14e — the real FilterWheelService backs IFilterWheelMediator too (replaces
        // HeadlessFilterWheelMediator), so SwitchFilter drives the live Alpaca wheel. Its filter
        // list imports into IProfileService.ActiveProfile on connect (SwitchFilter resolves by
        // name/position against that list).
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IFilterWheelMediator>(
            sp => sp.GetRequiredService<FilterWheelRouter>());
        // §14e — the real RotatorService backs IRotatorMediator too (replaces HeadlessRotatorMediator),
        // so MoveRotatorMechanical drives the live Alpaca rotator.
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IRotatorMediator>(
            sp => sp.GetRequiredService<RotatorService>());
        // §14e — the real SwitchService backs ISwitchMediator too (replaces HeadlessSwitchMediator),
        // so SetSwitchValue drives the live Alpaca switch hub (writable ports surfaced as
        // IWritableSwitch wrappers with real min/max/step for Validate).
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.ISwitchMediator>(
            sp => sp.GetRequiredService<SwitchService>());
        // §14e — the real DomeService backs IDomeMediator too (replaces HeadlessDomeMediator), so the
        // Open/Close shutter, Park, FindHome and SlewAzimuth instructions drive the live Alpaca dome.
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IDomeMediator>(
            sp => sp.GetRequiredService<DomeService>());
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
        // §14e profile source-of-truth — StoreBackedProfileService (replaces the §38k-22
        // HeadlessProfileService stub): ActiveProfile hydrates from IProfileStore (profile.json,
        // the WILMA REST surface) at startup and on every settings PUT, so executing instructions
        // read the user's edited site/guider/focuser/image-file/plate-solve values. The headless
        // stub class is kept for factory/test defaults.
        builder.Services.AddSingleton<OpenAstroAra.Profile.Interfaces.IProfileService>(sp =>
            new StoreBackedProfileService(
                sp.GetRequiredService<IProfileStore>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<StoreBackedProfileService>>()));
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
                profileService: sp.GetRequiredService<OpenAstroAra.Profile.Interfaces.IProfileService>(),
                imagingMediator: sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IImagingMediator>(),
                meridianFlipExecutor: sp.GetRequiredService<OpenAstroAra.Sequencer.Trigger.MeridianFlip.IMeridianFlipExecutor>(),
                // §28/§38 — CenteringService doubles as the sequencer's centering seam, so an
                // imported CenterAndRotate drives the same solve→sync→re-slew loop as REST/flip.
                centeringExecutor: (OpenAstroAra.Server.Services.CenteringService)sp.GetRequiredService<OpenAstroAra.Server.Services.ICenteringService>(),
                // §59 — the live V-curve sweep, so RunAutofocus (and AutofocusAfterExposures)
                // execute for real instead of failing loudly.
                autofocusExecutor: sp.GetRequiredService<OpenAstroAra.Sequencer.SequenceItem.Autofocus.IAutofocusExecutor>(),
                // §59.5 — the session history the autofocus trigger family reads.
                imageHistory: sp.GetRequiredService<OpenAstroAra.Sequencer.Interfaces.IImageHistory>(),
                // §59.9 — autofocus defers while §51 diagnostics carries an open sky-condition issue.
                autofocusConditionGate: sp.GetRequiredService<OpenAstroAra.Sequencer.Interfaces.IAutofocusConditionGate>(),
                // §48.3 — the auto-exposure flat set, so FlatPanelFlats executes for real.
                flatCaptureExecutor: sp.GetRequiredService<OpenAstroAra.Sequencer.SequenceItem.FlatDevice.IFlatCaptureExecutor>()));
        // The by-hand rotation readout (the Plan screen's "Rotate camera" panel on a rig without a rotator):
        // the centering service's solver stack as the protractor, the profile's rotation tolerance as "done",
        // the profile's plate-solve exposure as the default exposure.
        builder.Services.AddSingleton<IRotationAssistService>(sp =>
            new RotationAssistService(
                // Development only (SyntheticSky): the angle comes from a file instead of a plate solve.
                sp.GetService<SyntheticSkySettings>() is not null
                    ? new SyntheticPositionAngleSolver(profileDir)
                    : new RotationFrameSolver(
                        sp.GetRequiredService<OpenAstroAra.Profile.Interfaces.IProfileService>(),
                        sp.GetRequiredService<IProfileStore>(),
                        sp.GetRequiredService<OpenAstroAra.PlateSolving.Interfaces.IPlateSolverFactory>(),
                        sp.GetRequiredService<IAnalysisFrameSource>(),
                        sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.ITelescopeMediator>()),
                () => sp.GetRequiredService<OpenAstroAra.Profile.Interfaces.IProfileService>().ActiveProfile?.PlateSolveSettings.RotationTolerance ?? 1.0,
                () => sp.GetRequiredService<OpenAstroAra.Profile.Interfaces.IProfileService>().ActiveProfile?.PlateSolveSettings.ExposureTime ?? 2.0,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RotationAssistService>>()));
        // #1299 — the Bahtinov mask focus readout (Setup → Smart Focus → Main telescope): the main camera through
        // the analysis seam, the profile's optics for the focus zone. It refuses to start while an autofocus run
        // or a sequence has the camera (and those refuse to start while it runs — see the run guards).
        builder.Services.AddSingleton<IBahtinovFocusService>(sp =>
            new BahtinovFocusService(
                // Development only (SyntheticSky): a mask star whose offset follows the simulator focuser.
                sp.GetService<SyntheticSkySettings>() is { } sky
                    ? new SyntheticBahtinovFrameSource(sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IFocuserMediator>(), sky)
                    : sp.GetRequiredService<IAnalysisFrameSource>(),
                () => BahtinovOpticsFor(sp.GetRequiredService<IProfileStore>()),
                () => sp.GetRequiredService<AutofocusRunTracker>().IsRunning ? "an autofocus run is in progress"
                    : sp.GetRequiredService<ActiveRunSessionRegistry>().HasAny ? "a sequence is running"
                    : null,
                sp.GetRequiredService<ILogger<BahtinovFocusService>>()));
        builder.Services.AddSingleton<SequenceBodyDeserializer>();

        var app = builder.Build();

        app.UseCors();

        // §60.9 WebSocket support — must be registered before MapWebSocketEndpoints
        // so the framework can negotiate the protocol upgrade.
        //   KeepAliveInterval = 30s — server-initiated RFC 6455 ping cadence
        //   KeepAliveTimeout  = 60s — close the socket if no pong/data arrives
        //                              within this window (matches API_CONTRACT.md
        //                              WebSocket section: "client must pong within 60 s",
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

        // §59.15 — Smart Focus calibration read + recalibrate (profile-state, not device ops).
        app.MapAutofocusEndpoints();
        app.MapRotationAssistEndpoints();
        app.MapBahtinovFocusEndpoints();

        // Phase 7 endpoint groups (501 stubs until service implementations land).
        app.MapSequenceEndpoints();
        app.MapCalibrationEndpoints();
        app.MapMosaicEndpoints();
        app.MapPlateSolveEndpoints(); // §18.I — solve a catalogued frame

        // Phase 8 endpoint groups (501 stubs until service implementations land).
        app.MapImageEndpoints();
        app.MapDiagnosticsEndpoints();
        app.MapFaultsEndpoints(); // §42.5 — persisted fault history

        // Phase 9 endpoint groups (501 stubs except /api/v1/ws/catalog which is
        // functional today). /api/v1/server/info already lives directly in this file.
        app.MapServerStateEndpoints();
        app.MapServerUpdateEndpoints();
        app.MapConnectionEndpoints(); // §27 — connect/disconnect/session (single-client policy)
        app.MapNotificationEndpoints();
        app.MapStatsEndpoints();
        app.MapClientSettingsEndpoints();
        app.MapSystemEndpoints();
        app.MapWebSocketEndpoints();

        // Phase 12h.6a: §37 profile endpoints. imaging-defaults is real
        // (in-memory store); other sections follow as 12h.6b-N adds DTOs +
        // section-specific endpoint pairs on top of the same IProfileStore.
        app.MapProfileEndpoints();

        // §37/§30 multi-profile management — CRUD over the known-profiles set.
        app.MapProfilesEndpoints();

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

        // Fixture seeds — an M31 session, sample notifications, sample
        // diagnostics events. These exist so CI's smoke gate and manual UI
        // work have something to render before a rig has ever imaged.
        //
        // They must NEVER run on a real install. On an astrophotographer's
        // machine they land in the same catalog as their own data and the
        // Stats dashboard then reports a night they did not have: a target
        // they never shot, integration hours they never earned, a "best
        // frame" score for an image that does not exist. Opt in explicitly.
        var notificationSvc = app.Services.GetRequiredService<INotificationService>();
        var diagnosticsSvc = app.Services.GetRequiredService<IDiagnosticsService>();
        var frameRepo = app.Services.GetRequiredService<IFrameRepository>();
        if (ShouldSeedSampleData()) {
            if (frameRepo is SqliteFrameRepository sqliteRepo) {
                sqliteRepo.EnsureSeededAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            if (notificationSvc is SqliteNotificationService sqliteNotif) {
                sqliteNotif.EnsureSeededAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            if (diagnosticsSvc is SqliteDiagnosticsService sqliteDiag) {
                sqliteDiag.EnsureSeededAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
        } else {
            // Self-heal (the #923 gate's missing half): builds published
            // BEFORE the gate seeded these fixtures unconditionally, so any
            // real install from an older .deb is still carrying the demo M31
            // session, sample notifications, and sample diagnostics in the
            // user's own catalog. Upgrading alone never removed them — scrub
            // the fixed sentinel ids here so affected installs heal on their
            // next boot. Exact-id matching means real data can never match.
            if (frameRepo is SqliteFrameRepository sqliteRepo) {
                sqliteRepo.ScrubSampleDataAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            if (notificationSvc is SqliteNotificationService sqliteNotif) {
                sqliteNotif.ScrubSampleDataAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            if (diagnosticsSvc is SqliteDiagnosticsService sqliteDiag) {
                sqliteDiag.ScrubSampleDataAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
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
        // The scan heals the catalog; it must never be the reason the rig is
        // down. On rc91 an unreadable lost+found at the store root turned this
        // line into a boot crash-loop — the walk is fixed, but the guard stays:
        // whatever a user's disk throws at us, the daemon comes up.
        try {
            app.Services.GetRequiredService<CaptureScanService>()
                .RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or Microsoft.Data.Sqlite.SqliteException) {
            var scanLogger = app.Services.GetRequiredService<ILogger<Program>>();
            LogCaptureScanFailed(scanLogger, ex);
        }

        // §43-2 startup polish: reclaim crash-only orphan archives under backups/ — a .tmp-*.zip from a create
        // hard-killed before its File.Move reveal, or a backup-*.zip whose .meta.json never got written (SIGKILL
        // between the reveal and the manifest write). ListSnapshots ignores both but never deletes them; a graceful
        // create reclaims its own temp, so these only linger after a hard kill. Runs before app.Run() (request
        // acceptance), so no concurrent create can race it. Logs a Warning when it actually reclaims something — a
        // non-empty sweep means the daemon died mid-backup. Mirrors §36-2c SweepStaleScratch.
        BackupService.SweepOrphans(profileDir, app.Services.GetService<ILogger<BackupService>>());

        // §55.1 settings-sync analogue: reclaim any client-settings.json.tmp-* orphaned by a settings write killed
        // between the temp write and its File.Move rename. Same boot-time, pre-request-acceptance, best-effort sweep.
        ClientSettingsService.SweepOrphans(profileDir, app.Services.GetService<ILogger<ClientSettingsService>>());

        // §54 bug-report analogue: reclaim any bug-reports/.tmp-*.zip orphaned by a prepare hard-killed before its
        // File.Move reveal. Startup-only (no in-flight prepare can race it), which also avoids the cross-platform
        // unlink()-of-an-open-file hazard a concurrent sweep would have. Best-effort.
        BugReportService.SweepStaleTempBundles(profileDir, app.Services.GetService<ILogger<BugReportService>>());

        // §37: eagerly construct the multi-profile repository at boot (not on first request) so it
        // migrates the legacy profile.json into the profiles/ set and loads the active profile into
        // the live store before any request is served.
        _ = app.Services.GetRequiredService<IProfileRepository>();

        // #1121 — say at boot when the configured plate-solver binary is missing (after the
        // normalizer's /usr/bin/astap → /usr/bin/astap_cli migration had its chance): otherwise
        // the first sign is a failed centering or polar alignment in the field.
        if (SolverPathMigration.BootWarning(
                app.Services.GetRequiredService<IProfileStore>().GetPlateSolveSettings(), File.Exists) is string solverWarning) {
            LogPlateSolverWarning(app.Logger, solverWarning);
        }

        // §14e — say up front whether the SOFA/NOVAS31 astrometry natives are next to the binary.
        // Without them the slew epoch transform degrades quietly, but altitude/sun/moon conditions,
        // the meridian-flip projection and polar-align solving fault on first use. A package built
        // without `scripts/build-astrometry-natives.sh` must be obvious in the first log lines.
        // Windows keeps the inherited External/x64 DllLoader+SetDllDirectory path, which this probe
        // does not mirror — a working Windows box would log a false "MISSING". Untested platform
        // (RUNNING.md points at WSL2); say so instead of crying wolf.
        if (OperatingSystem.IsWindows()) {
            LogAstrometryNativesNotProbed(app.Logger);
        } else {
            var (sofaOk, novasOk) = OpenAstroAra.Astrometry.AstrometryNatives.Probe();
            if (sofaOk && novasOk) {
                LogAstrometryNativesPresent(app.Logger);
            } else {
                var (sofaName, novasName) = OpenAstroAra.Astrometry.AstrometryNatives.ExpectedFileNames;
                LogAstrometryNativesMissing(app.Logger,
                    sofaOk ? "present" : $"MISSING ({sofaName})",
                    novasOk ? "present" : $"MISSING ({novasName})",
                    AppContext.BaseDirectory);
            }
        }

        // §72.3 / #1120 — same idea for CFITSIO: resolve it now so a missing or broken libcfitsio
        // shows in the boot log with an install hint instead of at the first exposure's FITS write.
        // Log-and-continue: the rest of the daemon (equipment, planning, the client UI) still works.
        LogCfitsioProbe(app.Logger, OpenAstroAra.Fits.FitsLibraryProbe.Probe(), CfitsioInstallHint());
        if (app.Services.GetService<SyntheticSkySettings>() is { } sky) {
            LogSyntheticSky(app.Logger, SyntheticSky.EnvVar, sky.BestPosition, sky.HfrAtFocus, sky.StepsPerPixel);
        }

        // #1135 — build the storage service now so its constructor sweep of the request/result
        // exchange happens at boot, not on the first /storage request (a lazily resolved singleton
        // would otherwise leave a cancelled request's leftover in tmpfs for the whole uptime).
        _ = app.Services.GetRequiredService<IStorageDeviceService>();

        LogListening(app.Logger, port);
        return app;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Startup reconciliation: {Outcome} (previous sequence: {SeqId})")]
    private static partial void LogReconciliation(ILogger logger, SequenceReconcileOutcome outcome, Guid? seqId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to emit §46 reconciliation notification")]
    private static partial void LogNotificationEmitFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to emit §51 checkpoint-corrupt diagnostic")]
    private static partial void LogDiagnosticEmitFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Startup capture scan failed — catalog not reconciled with disk; frames on the store may be missing from the library until a rescan succeeds")]
    private static partial void LogCaptureScanFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "OpenAstroAra.Server listening on :{Port}")]
    private static partial void LogListening(ILogger logger, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Warning}")]
    private static partial void LogPlateSolverWarning(ILogger logger, string warning);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "SYNTHETIC SKY ({EnvVar}): autofocus probes, guide-camera focus frames and Bahtinov readout frames are RENDERED, not captured — best focus at {Best}, HFR {Hfr} there, {Scale} focuser steps per pixel of defocus. Development only.")]
    private static partial void LogSyntheticSky(ILogger logger, string envVar, int best, double hfr, double scale);

    /// <summary>#1299 — the main telescope's working focal ratio (the reducer applied) and pixel size for the
    /// Bahtinov readout's focus zone, or null when the profile lacks the focal length, aperture or pixel size.</summary>
    internal static BahtinovOptics? BahtinovOpticsFor(IProfileStore store) {
        var optics = store.GetOpticsSettings();
        if (optics is null || !(optics.FocalLengthMm > 0) || !(optics.ApertureMm > 0) || !(optics.PixelSizeUm > 0)) {
            return null;
        }
        var focal = optics.FocalLengthMm * (optics.ReducerFactor > 0 ? optics.ReducerFactor : 1.0);
        return new BahtinovOptics(focal / optics.ApertureMm, optics.PixelSizeUm);
    }

    /// <summary>The guide camera's optics for the live-focus target: an off-axis guider sees the main
    /// telescope's focal length and aperture with the guide camera's pixels; a guide scope uses the
    /// §63.19 guide focal length (its aperture is not in the profile, so diffraction is left out).</summary>
    internal static (double FocalLengthMm, double PixelSizeUm, double ApertureMm)? GuideOpticsFor(IProfileStore store) {
        var phd2 = store.GetPhd2Settings();
        if (string.Equals(phd2.GuiderSetupType, "oag", StringComparison.OrdinalIgnoreCase)) {
            var optics = store.GetOpticsSettings();
            return (optics.FocalLengthMm, phd2.GuidePixelSize, optics.ApertureMm);
        }
        return (phd2.GuideFocalLength, phd2.GuidePixelSize, 0);
    }

    /// <summary>Logs the boot-time CFITSIO probe (#1120). Never throws.</summary>
    internal static void LogCfitsioProbe(ILogger logger, OpenAstroAra.Fits.FitsLibraryProbeResult result, string installHint) {
        var resolution = result.Resolution;
        var tried = resolution.Tried.Count == 0 ? "(resolver not reached)" : string.Join(", ", resolution.Tried);
        if (!result.Loaded) {
            LogCfitsioMissing(logger, installHint, tried, result.Error ?? "unknown error");
        } else if (resolution.ExplicitPath is not null && !resolution.ExplicitPathLoaded) {
            LogCfitsioExplicitPathIgnored(logger, resolution.ExplicitPath, resolution.LoadedFrom ?? "runtime default probing");
        } else {
            LogCfitsioLoaded(logger, resolution.LoadedFrom ?? "runtime default probing");
        }
    }

    /// <summary>Platform-specific "how to install CFITSIO" text for the boot log (§72.2).</summary>
    internal static string CfitsioInstallHint() =>
        OperatingSystem.IsLinux() ? "sudo apt install libcfitsio10"
        : OperatingSystem.IsMacOS() ? "brew install cfitsio, then rebuild so the dylib is staged next to the daemon (docs/RUNNING.md)"
        : "vcpkg install cfitsio:x64-windows, then set OPENASTROARA_CFITSIO_PATH to the full path of cfitsio.dll";

    [LoggerMessage(Level = LogLevel.Information, Message = "CFITSIO loaded from {Source}")]
    private static partial void LogCfitsioLoaded(ILogger logger, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OPENASTROARA_CFITSIO_PATH={Path} did not load; CFITSIO loaded from {Source} instead")]
    private static partial void LogCfitsioExplicitPathIgnored(ILogger logger, string path, string source);

    // The hint and candidates go first: the runtime's DllNotFoundException message lists every dlopen
    // path it probed and runs to dozens of lines.
    [LoggerMessage(Level = LogLevel.Error, Message = "Cannot load libcfitsio. Install via: {Hint}. Tried: {Tried}. Every capture will fail at the FITS write until it loads. Loader: {Error}")]
    private static partial void LogCfitsioMissing(ILogger logger, string hint, string tried, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Astrometry natives loaded (SOFA + NOVAS31)")]
    private static partial void LogAstrometryNativesPresent(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Astrometry natives not probed on Windows (inherited External/x64 SOFAlib.dll / NOVAS31lib.dll loader path; untested platform)")]
    private static partial void LogAstrometryNativesNotProbed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Astrometry natives incomplete: SOFA {Sofa}, NOVAS31 {Novas}. Expected next to the daemon in {BaseDir}; build them with scripts/build-astrometry-natives.sh. Cross-epoch slews fall back to untransformed coordinates; altitude/sun/moon conditions, the meridian-flip projection and polar-align solving will fail until they are installed.")]
    private static partial void LogAstrometryNativesMissing(ILogger logger, string sofa, string novas, string baseDir);

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
    /// <summary>
    /// Whether to plant the sample M31 session, sample notifications and
    /// sample diagnostics events into an empty catalog.
    ///
    /// Off unless <c>OPENASTROARA_SEED_SAMPLE_DATA</c> says otherwise — CI's
    /// smoke gate sets it, and so should anyone doing UI work against an
    /// empty database. A real rig must never get fixture rows: they are
    /// indistinguishable from the user's own data once written, and the Stats
    /// dashboard would credit them with a night they never had.
    /// </summary>
    private static bool ShouldSeedSampleData() {
        var raw = System.Environment.GetEnvironmentVariable("OPENASTROARA_SEED_SAMPLE_DATA");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        return raw.Equals("1", StringComparison.Ordinal)
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

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