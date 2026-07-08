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
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<FramingFit>(policy));
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<OptimalSubBound>(policy));
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<FilterKind>(policy));
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter<FilterApproach>(policy));
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
        builder.Services.AddSingleton<IFilterWheelService>(sp => sp.GetRequiredService<FilterWheelService>());
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
        builder.Services.AddSingleton<SwitchService>();
        builder.Services.AddSingleton<ISwitchService>(sp => sp.GetRequiredService<SwitchService>());
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
        // §63.3 guider-d — crash detection + auto-restart of the sibling openastro-phd2 unit.
        builder.Services.AddSingleton<IGuiderProcessSupervisor, SystemctlGuiderProcessSupervisor>();
        builder.Services.AddSingleton<GuiderRecoveryCoordinator>();
        // §63 — one GuiderService singleton backs both the REST IGuiderService and the Sequencer's
        // IGuiderMediator (§8.1; the mediator alias is registered below, replacing HeadlessGuiderMediator).
        builder.Services.AddSingleton<GuiderService>(sp =>
            new GuiderService(
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
                sp.GetService<INotificationService>()));
        builder.Services.AddSingleton<IGuiderService>(sp => sp.GetRequiredService<GuiderService>());
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
        builder.Services.AddSingleton<IBackupService>(sp =>
            new BackupService(profileDir, sp.GetRequiredService<ILogger<BackupService>>(),
                // Explicit: this factory lambda bypasses constructor activation, so optional params are passed
                // by hand (the #711 CameraService lesson).
                restorer: null,
                remoteFetcher: sp.GetRequiredService<IBackupSourceFetcher>(),
                // §43-2b retention — read live per create (a settings change applies without a restart).
                profiles: sp.GetService<IProfileStore>()));

        // §36/§25.5 Tonight's Sky — ranks the OpenNGC catalog by altitude (with visibility window,
        // transit, and integration hours) for the active profile's site; falls back to a starter list
        // when openngc-dso isn't installed.
        builder.Services.AddSingleton<ITonightSkyService>(sp =>
            new TonightSkyService(
                sp.GetRequiredService<IProfileStore>(),
                sp.GetRequiredService<ISkyCatalogService>()));

        // §36 Planning horizon — projects the site's local horizon onto the equatorial sky for a client overlay.
        builder.Services.AddSingleton<IHorizonService>(sp =>
            new HorizonService(sp.GetRequiredService<IProfileStore>()));

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
                imageHistory: sp.GetRequiredService<ImageHistoryService>()));
        builder.Services.AddSingleton<ICameraService>(sp => sp.GetRequiredService<CameraService>());
        // §59 — the autofocus sweep's probe-capture seam rides the same singleton (same device
        // path + same in-flight capture gate as real captures; probes are never persisted).
        builder.Services.AddSingleton<IAnalysisFrameSource>(sp => sp.GetRequiredService<CameraService>());
        // §59.5 — session image/autofocus history: the sweep records completed runs, the
        // autofocus trigger family reads them (temperature delta + HFR trend are both measured
        // "since the last autofocus"). In-memory by design — triggers reason about the current
        // session; the §28 frames catalog owns the durable record.
        builder.Services.AddSingleton<ImageHistoryService>();
        builder.Services.AddSingleton<OpenAstroAra.Sequencer.Interfaces.IImageHistory>(sp =>
            sp.GetRequiredService<ImageHistoryService>());
        // §59 — the live autofocus V-curve sweep (probe → HFR → curve fit → move-to-best).
        builder.Services.AddSingleton<OpenAstroAra.Sequencer.SequenceItem.Autofocus.IAutofocusExecutor>(sp =>
            new AutofocusSweepService(
                sp.GetRequiredService<IProfileStore>(),
                sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IFocuserMediator>(),
                sp.GetRequiredService<IAnalysisFrameSource>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AutofocusSweepService>>(),
                history: sp.GetRequiredService<ImageHistoryService>(),
                filterWheel: sp.GetRequiredService<OpenAstroAra.Equipment.Interfaces.Mediator.IFilterWheelMediator>()));
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
                sp.GetService<INotificationService>()));
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
                sp.GetService<ILogger<SafetyReactionService>>()));
        // Hosted so the poll timer starts with the daemon and a shutdown cancels a
        // pending auto-resume countdown.
        builder.Services.AddHostedService(sp => sp.GetRequiredService<SafetyReactionService>());

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
        builder.Services.AddSingleton<OpenAstroAra.Equipment.Interfaces.Mediator.IGuiderMediator,
            OpenAstroAra.Server.Services.Equipment.HeadlessGuiderMediator>();
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
            sp => sp.GetRequiredService<FilterWheelService>());
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
        app.MapPlateSolveEndpoints(); // §18.I — solve a catalogued frame

        // Phase 8 endpoint groups (501 stubs until service implementations land).
        app.MapImageEndpoints();
        app.MapDiagnosticsEndpoints();

        // Phase 9 endpoint groups (501 stubs except /api/v1/ws/catalog which is
        // functional today). /api/v1/server/info already lives directly in this file.
        app.MapServerStateEndpoints();
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

        LogListening(app.Logger, port);
        try {
            app.Run();
        } finally {
            // Flush the §29.9 Serilog file sink even when app.Run() unwinds via an
            // exception — the daemon's own logs are the first thing reached for
            // after a crash, so the last lines must not be lost.
            Log.CloseAndFlush();
        }
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