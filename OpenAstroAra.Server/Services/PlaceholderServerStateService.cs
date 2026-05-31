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
using OpenAstroAra.Server.Contracts;

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
/// <see cref="RestartAsync"/> and <see cref="RestartOnIdleAsync"/>
/// throw — restarting the daemon from inside the daemon needs the
/// systemd watchdog path, which is the §13 hardening pass.
/// </summary>
public sealed class PlaceholderServerStateService : IServerStateService {
    private static readonly JsonDocument _empty = JsonDocument.Parse("{}");

    private readonly IWsBroadcaster? _broadcaster;

    public PlaceholderServerStateService(IWsBroadcaster? broadcaster = null) {
        _broadcaster = broadcaster;
    }

    public Task<ServerStateDto> GetSnapshotAsync(CancellationToken ct) {
        // §60.9.6 resume protocol: ws_resume_token is the client's
        // last-seen seq, in v0.0.1 just the broadcaster's current seq
        // stringified. WILMA stores this in its local state and presents
        // it back on reconnect via the §60.9.6 resume request.
        var seq = _broadcaster?.CurrentSequence ?? 0;
        var resumeToken = seq.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Task.FromResult(new ServerStateDto(
            ServerUuid: ServerIdentity.Uuid,
            Nickname: ServerIdentity.Nickname,
            Version: ServerIdentity.Version,
            ApiVersion: "v1",
            Tier: "scaffold",
            CurrentUtc: DateTimeOffset.UtcNow,
            CurrentProfileId: null,
            PendingRestart: null,
            WsResumeToken: resumeToken,
            WsEventCursor: seq,
            // Subsystem summary blobs are empty objects today; each
            // subsystem will fill its own shape when its placeholder /
            // real impl lands (§60.9.4).
            EquipmentStates: _empty.RootElement.Clone(),
            ActiveSequenceRun: _empty.RootElement.Clone(),
            DiagnosticsHealth: _empty.RootElement.Clone(),
            NotificationsSummary: _empty.RootElement.Clone()));
    }

    public Task<ApiVersionsDto> GetVersionsAsync(CancellationToken ct) =>
        // Version data answers WILMA's §54 "About" screen + the §63.1
        // compatibility check. Most values are static for v0.0.1; real
        // git sha + OS release land via build-time injection in Phase 14.
        Task.FromResult(new ApiVersionsDto(
            DaemonVersion: ServerIdentity.Version,
            DaemonGitSha: "placeholder",
            DotnetVersion: System.Environment.Version.ToString(),
            OsRelease: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            OsArch: System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            AlpacaSdkVersion: "2.1.0",        // matches the ASCOM.Alpaca.* package version
            Phd2ProtocolVersion: "0.4.6",      // PHD2 JSON-RPC dialect we target
            ApiSurfaces: new[] {
                new ApiSurfaceVersionDto("rest",      "1.0.0", IsDeprecated: false, SunsetUtc: null),
                new ApiSurfaceVersionDto("websocket", "1.0.0", IsDeprecated: false, SunsetUtc: null),
            }));

    public Task<ReleaseNotesDto?> GetReleaseNotesAsync(string? version, CancellationToken ct) =>
        // Single placeholder release-notes record for v0.0.1-ara.1 — the
        // §54 About-tab shows them by default; future versions append.
        Task.FromResult<ReleaseNotesDto?>(new ReleaseNotesDto(
            Version: ServerIdentity.Version,
            ReleasedUtc: new DateTimeOffset(2026, 5, 30, 0, 0, 0, TimeSpan.Zero),
            MarkdownBody:
                "## v0.0.1-ara.1 (pre-release placeholder)\n\n" +
                "First port milestone. §37 settings round-trip end-to-end, " +
                "§40 Library browseable (placeholder data), §51 diagnostics + " +
                "§50 stats render fixture views, §46 notifications functional " +
                "with sample data. Full FITS-to-JPEG pipeline + real frame " +
                "catalog DB land in the real-infra phase that follows §60.9.",
            BreakingChanges: Array.Empty<string>(),
            UpgradeGuideUrl: null));

    public Task<OperationAcceptedDto> RestartAsync(string reason, string? idempotencyKey, CancellationToken ct) {
        var accepted = PlaceholderEquipmentHelpers.Accepted("server.restart", idempotencyKey);
        // Fire-and-forget: spawn `systemctl restart openastroara-server`
        // 2 seconds from now so the 202 response reaches the client
        // before the daemon dies. Linux + systemd only — on macOS/Windows
        // dev runs the spawn fails silently (no systemctl in PATH) which
        // is the correct behavior: the WILMA-visible 202 still confirms
        // the request was accepted, just no actual restart fires.
        _ = Task.Run(async () => {
            await Task.Delay(TimeSpan.FromSeconds(2));
            TrySpawnSystemctl("restart", "openastroara-server");
        });
        return Task.FromResult(accepted);
    }

    public Task<OperationAcceptedDto> RestartOnIdleAsync(string reason, string? idempotencyKey, CancellationToken ct) =>
        // §34.7 restart-on-idle needs the §28 sequence-state check (don't
        // restart mid-capture). Placeholder until §38 orchestrator is
        // online + we can ask "is the daemon currently busy?".
        Task.FromResult(PlaceholderEquipmentHelpers.Accepted("server.restart-on-idle", idempotencyKey));

    private static void TrySpawnSystemctl(string verb, string unit) {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo("systemctl", $"{verb} {unit}") {
                UseShellExecute = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
        } catch {
            // No systemctl in PATH (non-Linux dev runs, or restricted
            // environment without polkit permission). Swallow — the 202
            // was already sent and there's nothing useful to log from a
            // process that's about to be killed anyway.
        }
    }
}
