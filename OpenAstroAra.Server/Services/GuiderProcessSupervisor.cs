#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// Service-level health of the sibling <c>openastro-guider</c> systemd unit, as reported by
/// <c>systemctl is-active</c>. <see cref="Unknown"/> means we couldn't ask systemd at all — the
/// daemon host isn't a systemd box (e.g. the macOS dev machine), so the guider can't be supervised.
/// </summary>
public enum GuiderProcessStatus {
    /// <summary>Unit is running (`active`).</summary>
    Active,
    /// <summary>systemd is (re)starting the unit (`activating`/`reloading`) — back off and re-poll.</summary>
    Activating,
    /// <summary>systemd gave up (`failed`) — won't auto-restart without a nudge.</summary>
    Failed,
    /// <summary>Unit is stopped (`inactive`/`deactivating`).</summary>
    Inactive,
    /// <summary>No systemd available (no `systemctl` on PATH) — cannot supervise.</summary>
    Unknown,
}

/// <summary>
/// §63.1/§63.3 process supervisor for the guider daemon. ARA does not own the
/// <c>openastro-guider</c> systemd unit (the <c>openastro-guider</c> .deb ships it), but it can read
/// its service-level health and request a restart — the seam the §63.3 crash-recovery decision tree
/// drives. This is the only place that shells out to <c>systemctl</c>.
/// </summary>
public interface IGuiderProcessSupervisor {
    /// <summary>Read the guider unit's current systemd state. Never throws; returns
    /// <see cref="GuiderProcessStatus.Unknown"/> when systemd isn't reachable.</summary>
    Task<GuiderProcessStatus> QueryStatusAsync(CancellationToken ct);

    /// <summary>Fire-and-forget <c>systemctl restart</c> of the guider unit. No-op (swallowed) when
    /// systemd isn't available.</summary>
    void RequestRestart();

    /// <summary>Fire-and-forget <c>systemctl start</c> of the guider unit (idempotent if already
    /// running). Used on connect to bring an inactive guider service up before connecting. No-op
    /// (swallowed) when systemd isn't available.</summary>
    void RequestStart();
}

/// <summary>
/// <c>systemctl</c>-backed <see cref="IGuiderProcessSupervisor"/>. Linux/Pi only in effect — on a
/// host without <c>systemctl</c> (dev/CI) every call degrades to a safe no-op
/// (<see cref="GuiderProcessStatus.Unknown"/> / swallowed restart), mirroring the §13 server
/// self-restart pattern (<c>PlaceholderServerStateService.TrySpawnSystemctl</c>).
/// </summary>
public sealed partial class SystemctlGuiderProcessSupervisor : IGuiderProcessSupervisor {

    // The guider daemon's systemd unit. The openastro-guider .deb ships debian/openastro-guider.service
    // (verified on a Pi: `systemctl list-units` shows openastro-guider.service and nothing named
    // openastro-phd2 — the old lineage name this constant carried made every is-active read Unknown
    // and every start/restart a silent no-op, #1093).
    internal const string Unit = "openastro-guider";

    private readonly ILogger<SystemctlGuiderProcessSupervisor> _logger;

    public SystemctlGuiderProcessSupervisor(ILogger<SystemctlGuiderProcessSupervisor> logger) {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<GuiderProcessStatus> QueryStatusAsync(CancellationToken ct) {
        // `systemctl is-active <unit>` prints the state word to stdout even when it exits non-zero
        // (e.g. exit 3 + "inactive"), so we classify on stdout, not the exit code.
        var state = await RunIsActiveAsync(ct).ConfigureAwait(false);
        if (state is null) {
            return GuiderProcessStatus.Unknown;
        }
        // Ordinal compares — never CultureInfo (the AOT container runs globalization-invariant, §27).
        return state.Trim() switch {
            "active" => GuiderProcessStatus.Active,
            "activating" or "reloading" => GuiderProcessStatus.Activating,
            "failed" => GuiderProcessStatus.Failed,
            "inactive" or "deactivating" => GuiderProcessStatus.Inactive,
            _ => GuiderProcessStatus.Unknown,
        };
    }

    public void RequestRestart() => RequestVerb("restart");

    public void RequestStart() => RequestVerb("start");

    private void RequestVerb(string verb) {
        // `sudo -n systemctl <verb> <unit>`, fire-and-forget. The openastroara user is not root and
        // the guider .deb ships no polkit rule, so a bare systemctl is refused by the bus; ARA's own
        // .deb ships the NOPASSWD sudoers line for exactly these two verbs on this one unit
        // (packaging/debian/etc/sudoers.d/openastroara). `-n` never prompts: a missing rule fails
        // fast and is logged rather than hanging a fire-and-forget process on a password read.
        try {
            using var _ = Process.Start(new ProcessStartInfo("sudo", $"-n systemctl {verb} {Unit}") {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            LogVerbRequested(verb);
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                         or PlatformNotSupportedException or IOException) {
            // No systemctl on PATH (non-Linux dev) or no permission — nothing useful to do.
            LogSystemctlUnavailable(ex);
        }
    }

    private async Task<string?> RunIsActiveAsync(CancellationToken ct) {
        try {
            var psi = new ProcessStartInfo("systemctl", $"is-active {Unit}") {
                RedirectStandardOutput = true,
                // Deliberately NOT redirecting stderr: we never read it, and a redirected-but-unread
                // stderr pipe would deadlock WaitForExit if systemctl writes enough to it (e.g. a
                // "Failed to connect to bus" error). Errors surface via the exception filter / a
                // non-"active" stdout instead; stderr just inherits the daemon's.
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null) {
                return null;
            }
            // Read stdout to completion before waiting so a full pipe can't deadlock the exit.
            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return stdout;
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                         or PlatformNotSupportedException or IOException) {
            LogSystemctlUnavailable(ex);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Requested systemctl {Verb} of the guider unit")]
    partial void LogVerbRequested(string verb);

    [LoggerMessage(Level = LogLevel.Debug, Message = "systemctl unavailable — guider process supervision is a no-op on this host")]
    partial void LogSystemctlUnavailable(Exception ex);
}
