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
    private const string SampleResumeToken = "ws-placeholder-00000000-0000-0000-0000-000000000000";

    public Task<ServerStateDto> GetSnapshotAsync(CancellationToken ct) =>
        Task.FromResult(new ServerStateDto(
            ServerUuid: ServerIdentity.Uuid,
            Nickname: ServerIdentity.Nickname,
            Version: ServerIdentity.Version,
            ApiVersion: "v1",
            Tier: "scaffold",
            CurrentUtc: DateTimeOffset.UtcNow,
            CurrentProfileId: null,
            PendingRestart: null,
            WsResumeToken: SampleResumeToken,
            WsEventCursor: 0,
            // Subsystem summary blobs are empty objects today; each
            // subsystem will fill its own shape when its placeholder /
            // real impl lands (§60.9.4).
            EquipmentStates: _empty.RootElement.Clone(),
            ActiveSequenceRun: _empty.RootElement.Clone(),
            DiagnosticsHealth: _empty.RootElement.Clone(),
            NotificationsSummary: _empty.RootElement.Clone()));

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
                "catalog DB land in Phase 13.x.",
            BreakingChanges: Array.Empty<string>(),
            UpgradeGuideUrl: null));

    public Task<OperationAcceptedDto> RestartAsync(string reason, string? idempotencyKey, CancellationToken ct) =>
        throw new NotImplementedException("Daemon restart needs the §13 systemd watchdog path; lands in Phase 14 hardening");

    public Task<OperationAcceptedDto> RestartOnIdleAsync(string reason, string? idempotencyKey, CancellationToken ct) =>
        throw new NotImplementedException("Restart-on-idle needs the §13 systemd watchdog path; lands in Phase 14 hardening");
}
