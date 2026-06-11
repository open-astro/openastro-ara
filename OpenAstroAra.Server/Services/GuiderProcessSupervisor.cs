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
/// Service-level health of the sibling <c>openastro-phd2</c> systemd unit, as reported by
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
/// <c>openastro-phd2</c> systemd unit (the <c>openastro-guider</c> .deb ships it), but it can read
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
}

/// <summary>
/// <c>systemctl</c>-backed <see cref="IGuiderProcessSupervisor"/>. Linux/Pi only in effect — on a
/// host without <c>systemctl</c> (dev/CI) every call degrades to a safe no-op
/// (<see cref="GuiderProcessStatus.Unknown"/> / swallowed restart), mirroring the §13 server
/// self-restart pattern (<c>PlaceholderServerStateService.TrySpawnSystemctl</c>).
/// </summary>
public sealed partial class SystemctlGuiderProcessSupervisor : IGuiderProcessSupervisor {

    // The guider daemon's systemd unit. The openastro-guider repo ships debian/openastro-phd2.service
    // (the unit name kept the openastro-phd2 lineage even though the project is now openastro-guider),
    // matching playbook §63.1.
    internal const string Unit = "openastro-phd2";

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

    public void RequestRestart() {
        // Mirror §13: bare `systemctl restart`, fire-and-forget. Privileged via the §63.1 NOPASSWD
        // sudoers / polkit drop-in the openastro-phd2 .deb installs for the openastroara user.
        try {
            using var _ = Process.Start(new ProcessStartInfo("systemctl", $"restart {Unit}") {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            LogRestartRequested();
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
                RedirectStandardError = true,
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Requested systemctl restart of the guider unit")]
    partial void LogRestartRequested();

    [LoggerMessage(Level = LogLevel.Debug, Message = "systemctl unavailable — guider process supervision is a no-op on this host")]
    partial void LogSystemctlUnavailable(Exception ex);
}
