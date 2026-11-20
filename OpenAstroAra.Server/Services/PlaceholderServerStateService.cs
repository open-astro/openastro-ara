#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Data.Sqlite;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Contracts.WsEvents;
using System;
using System.IO;
using System.Text.Json;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Phase 13.7 — placeholder <see cref="IServerStateService"/>. Produces
/// a <see cref="ServerStateDto"/> snapshot suitable for WILMA's top-bar
/// status indicator + the §60.9 WS resume-handshake state catalog.
///
/// The four nested <c>JsonElement</c> fields (equipment_states,
/// active_sequence_run, diagnostics_health, notifications_summary) are
/// thin summary blobs whose exact shape lives in the §60.9 spec —
/// keeping them as opaque JSON here means each subsystem can publish
/// its own summary shape without versioning churn on the outer DTO.
///
/// <see cref="RestartAsync"/> restarts through <see cref="IDaemonRestarter"/>
/// (a non-zero exit that <c>Restart=on-failure</c> turns into a restart);
/// <see cref="RestartOnIdleAsync"/> does the same at once when no run is active, else remembers the
/// request (in memory, one at a time), says <c>server.restart_pending</c> and restarts once no run is on. "A run is on"
/// means the catalog-session registry or the sequencer says so, checked again after the notice window
/// (an auto-flats run starts just after the last session exits).
/// </summary>
public sealed class PlaceholderServerStateService : IServerStateService {
    private static readonly JsonDocument _empty = JsonDocument.Parse("{}");

    private readonly IWsBroadcaster? _broadcaster;
    private readonly IDiagnosticsService? _diagnostics;
    private readonly INotificationService? _notifications;
    private readonly RigVersions _rig;
    private readonly IDaemonRestarter? _restarter;
    private readonly ActiveRunSessionRegistry? _runs;
    private readonly Func<ISequencerService?>? _sequencerResolver;
    private readonly object _idleGate = new();
    private bool _restartOnIdlePending;
    private bool _queued; // true from a queued request until its 2 s notice starts: the only time it can be cancelled
    private PendingRestartDto? _pending;
    private int _queuedGeneration; // bumped by each queued request and each cancel, so a cancelled waiter sees it is stale

    public PlaceholderServerStateService(
            IWsBroadcaster? broadcaster = null,
            IDiagnosticsService? diagnostics = null,
            INotificationService? notifications = null,
            IGuiderService? guider = null,
            IEquipmentSelectionStore? selections = null,
            IAlpacaManagementClient? alpaca = null,
            IProfileStore? profiles = null,
            IDaemonRestarter? restarter = null,
            ActiveRunSessionRegistry? runs = null,
            Func<ISequencerService?>? sequencerResolver = null) {
        _restarter = restarter;
        _runs = runs;
        _sequencerResolver = sequencerResolver;
        _broadcaster = broadcaster;
        _diagnostics = diagnostics;
        _notifications = notifications;
        _rig = new RigVersions(guider, selections, alpaca,
            astapPath: profiles is null ? null : () => RigVersions.AstapPathOf(profiles.GetPlateSolveSettings()));
    }

    public async Task<ServerStateDto> GetSnapshotAsync(CancellationToken ct) {
        // §60.9.6 resume protocol: ws_resume_token is the client's
        // last-seen seq, in v0.0.1 just the broadcaster's current seq
        // stringified.
        var seq = _broadcaster?.CurrentSequence ?? 0;
        var resumeToken = seq.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var diagSummary = await BuildDiagnosticsSummaryAsync(ct);
        var notifSummary = await BuildNotificationsSummaryAsync(ct);

        return new ServerStateDto(
            ServerUuid: ServerIdentity.Uuid,
            Nickname: ServerIdentity.Nickname,
            Version: ServerIdentity.Version,
            ApiVersion: "v1",
            Tier: "scaffold",
            CurrentUtc: DateTimeOffset.UtcNow,
            CurrentProfileId: null,
            PendingRestart: PendingRestartNow(),
            WsResumeToken: resumeToken,
            WsEventCursor: seq,
            // equipment_states + active_sequence_run still empty until
            // real Alpaca drivers + §38 orchestrator land.
            EquipmentStates: _empty.RootElement.Clone(),
            ActiveSequenceRun: _empty.RootElement.Clone(),
            DiagnosticsHealth: diagSummary,
            NotificationsSummary: notifSummary);
    }

    private async Task<JsonElement> BuildDiagnosticsSummaryAsync(CancellationToken ct) {
        if (_diagnostics is null) return _empty.RootElement.Clone();
        try {
            var state = await _diagnostics.GetStateAsync(ct);
            var health = state.Health.ToString().ToLowerInvariant();
            var json = $$"""
                {"health":"{{health}}","open_issue_count":{{state.OpenIssueCount}},"last_hour_issue_count":{{state.LastHourIssueCount}}}
                """;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException or SqliteException or IOException) {
            return _empty.RootElement.Clone();
        }
    }

    private async Task<JsonElement> BuildNotificationsSummaryAsync(CancellationToken ct) {
        if (_notifications is null) return _empty.RootElement.Clone();
        try {
            // Pull the first page of unread notifications to count + classify
            // by severity. 200 cap matches the cursor-page max so the count
            // accurately reflects "all unread" in any realistic single-night
            // session.
            var page = await _notifications.ListAsync(limit: 200, cursor: null, unreadOnly: true, ct);
            var critical = page.Items.Count(n =>
                n.Severity == Contracts.NotificationSeverity.Critical && !n.Dismissed);
            var warning = page.Items.Count(n =>
                n.Severity == Contracts.NotificationSeverity.Warning && !n.Dismissed);
            var json = $$"""
                {"unread_count":{{page.Items.Count}},"critical_count":{{critical}},"warning_count":{{warning}}}
                """;
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException or SqliteException or IOException) {
            return _empty.RootElement.Clone();
        }
    }

    public async Task<ApiVersionsDto> GetVersionsAsync(CancellationToken ct) {
        var rig = await _rig.CollectAsync(ct).ConfigureAwait(false);
        // Version data answers WILMA's §54 "About" screen + the §63.1
        // compatibility check. Daemon git sha pulled from
        // AssemblyInformationalVersionAttribute which .NET auto-populates
        // with `<version>+<sourceRevisionId>` when built from a git repo
        // (controlled by the IncludeSourceRevisionInInformationalVersion
        // MSBuild flag, default true). Falls back to "unknown" if absent
        // (running from a non-git checkout, e.g., the chiseled .deb).
        return new ApiVersionsDto(
            DaemonVersion: ServerIdentity.Version,
            DaemonGitSha: ExtractGitSha(),
            DotnetVersion: System.Environment.Version.ToString(),
            OsRelease: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            OsArch: System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            AlpacaSdkVersion: "2.1.0",        // matches the ASCOM.Alpaca.* package version
            Phd2ProtocolVersion: "0.4.6",      // PHD2 JSON-RPC dialect we target
            ApiSurfaces: new[] {
                new ApiSurfaceVersionDto("rest",      "1.0.0", IsDeprecated: false, SunsetUtc: null),
                new ApiSurfaceVersionDto("websocket", "1.0.0", IsDeprecated: false, SunsetUtc: null),
            },
            BoardModel: rig.BoardModel,
            GuiderVersion: rig.GuiderVersion,
            AlpacaServers: rig.AlpacaServers,
            AstapVersion: rig.AstapVersion);
    }

    public Task<ReleaseNotesDto?> GetReleaseNotesAsync(string? version, CancellationToken ct) =>
        // Placeholder release-notes record for the running version — the §54
        // About tab shows it by default. The real notes are written per release
        // in docs/releases/<version>.md and published with the source release (#2093).
        Task.FromResult<ReleaseNotesDto?>(new ReleaseNotesDto(
            Version: ServerIdentity.Version,
            ReleasedUtc: new DateTimeOffset(2026, 5, 30, 0, 0, 0, TimeSpan.Zero),
            MarkdownBody:
                $"## {ServerIdentity.Version}\n\n" +
                "Release notes for each version are published with its source " +
                "release on GitHub (open-astro/openastro-ara, Releases).",
            BreakingChanges: Array.Empty<string>(),
            UpgradeGuideUrl: null));

    public Task<OperationAcceptedDto> RestartAsync(string reason, string? idempotencyKey, CancellationToken ct) {
        var accepted = PlaceholderEquipmentHelpers.Accepted("server.restart", idempotencyKey);
        ScheduleRestart(reason);
        return Task.FromResult(accepted);
    }

    private void ScheduleRestart(string reason) {
        // §34.7 server.restart_imminent — give WILMA a heads-up before the
        // socket dies so the §60.9 reconnect-modal can show the right copy.
        _ = Task.Run(async () => {
            await EmitRestartImminentAsync(reason);
            // 2-second window matches the spec line 712 mapping; WILMA shows
            // the restart banner during this gap.
            await Task.Delay(TimeSpan.FromSeconds(2));
            _restarter?.Restart();
        }, CancellationToken.None);
    }

    private async Task EmitRestartImminentAsync(string reason) =>
        await PublishAsync(WsEventCatalog.ServerRestartImminent,
            new { reason, restart_at_utc = DateTimeOffset.UtcNow.AddSeconds(2).ToString("O") });

    private PendingRestartDto? PendingRestartNow() { lock (_idleGate) { return _pending; } }

    // Same rule as ProfilesEndpoints.RunIsOnAsync: a run keeps imaging with no catalog session when the
    // catalog fails, so the registry alone is not enough. Any run that is not terminal holds the restart:
    // paused and aborting included (#1951), unlike ActiveRunIdsAsync, which leaves an aborting run out.
    private async Task<bool> RunIsOnAsync() =>
        (_runs?.HasAny ?? false)
        || (_sequencerResolver?.Invoke() is { } sequencer
            && (await sequencer.UnfinishedRunIdsAsync(CancellationToken.None).ConfigureAwait(false)).Count > 0);

    public async Task<RestartOnIdleAcceptedDto> RestartOnIdleAsync(string reason, string? idempotencyKey, CancellationToken ct) {
        var accepted = PlaceholderEquipmentHelpers.Accepted("server.restart-on-idle", idempotencyKey);
        lock (_idleGate) {
            if (_restartOnIdlePending) return RestartOnIdleAcceptedDto.From(accepted, _queued); // one pending restart, idempotent
            _restartOnIdlePending = true;
        }
        reason ??= "";
        var announce = await RunIsOnAsync().ConfigureAwait(false);
        var generation = 0;
        if (announce) {
            lock (_idleGate) {
                generation = ++_queuedGeneration;
                _queued = true;
                _pending = new PendingRestartDto(reason, DateTimeOffset.UtcNow, PlannedAtUtc: null, RestartOnIdle: true);
            }
        }
        _ = Task.Run(() => RestartWhenIdleAsync(reason, announce, generation), CancellationToken.None);
        return RestartOnIdleAcceptedDto.From(accepted, announce);
    }

    private bool IsCancelled(int generation) { lock (_idleGate) { return generation != _queuedGeneration; } }

    public async Task CancelRestartOnIdleAsync(CancellationToken ct) {
        string reason;
        lock (_idleGate) {
            if (!_queued) return; // nothing queued, or the notice already started: no-op
            reason = _pending?.Reason ?? "";
            _queued = false;
            _pending = null;
            _restartOnIdlePending = false;
            _queuedGeneration++;
        }
        await PublishAsync(WsEventCatalog.ServerRestartCancelled,
            new { reason, cancelled_utc = DateTimeOffset.UtcNow.ToString("O") });
    }

    private async Task RestartWhenIdleAsync(string reason, bool announce, int generation) {
        if (announce) {
            await PublishAsync(WsEventCatalog.ServerRestartPending,
                new { reason, requested_utc = DateTimeOffset.UtcNow.ToString("O") });
        }
        while (true) {
            while (await RunIsOnAsync().ConfigureAwait(false)) {
                await Task.Delay(TimeSpan.FromSeconds(1));
                if (announce && IsCancelled(generation)) return;
            }
            lock (_idleGate) {
                if (announce && generation != _queuedGeneration) return; // cancelled while queued
                _queued = false; // the notice starts: too late to cancel
                _pending = null;
            }
            await EmitRestartImminentAsync(reason);
            await Task.Delay(TimeSpan.FromSeconds(2));
            // A run (auto flats) may start inside the notice window: keep waiting instead of killing it.
            if (!await RunIsOnAsync().ConfigureAwait(false)) break;
        }
        _restarter?.Restart();
    }

    private async Task PublishAsync(WsEvent eventType, object payload) {
        if (_broadcaster is null) return;
        try {
            await _broadcaster.PublishAsync(eventType, JsonSerializer.SerializeToElement(payload), CancellationToken.None);
        } catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or ObjectDisposedException) {
            // Broadcast best-effort — never let it abort the restart.
        }
    }

    private static string ExtractGitSha() {
        var attr = typeof(PlaceholderServerStateService).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute;
        if (attr is null) return "unknown";
        // Format: "<assemblyVersion>+<gitSha>". Split on '+' and return the
        // suffix; if there's no '+' the source-revision wasn't included
        // (clean builds without git, e.g., the chiseled .deb).
        var info = attr.InformationalVersion;
        var plus = info.IndexOf('+');
        return plus >= 0 && plus + 1 < info.Length ? info[(plus + 1)..] : "unknown";
    }
}