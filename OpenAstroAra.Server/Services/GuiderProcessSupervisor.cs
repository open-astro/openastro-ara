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

    // Canonical unit shipped by the openastro-guider package.
    internal const string Unit = "openastro-guider";
    internal TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(10);
    internal Func<ProcessStartInfo, CancellationToken, Task<(int ExitCode, string Output, string Error)>> CommandRunner { get; set; } = RunProcessAsync;

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

    private void RequestVerb(string verb) => _ = RequestVerbAsync(verb);

    internal async Task RequestVerbAsync(string verb) {
        try {
            var result = await RunCommandAsync(verb, CancellationToken.None).ConfigureAwait(false);
            if (result.ExitCode != 0) {
                LogCommandFailed(verb, Unit, result.ExitCode, result.Error.Trim());
                return;
            }
            LogVerbCompleted(verb, Unit);
        } catch (OperationCanceledException) {
            LogCommandTimedOut(verb, Unit);
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                         or PlatformNotSupportedException or IOException) {
            LogSystemctlUnavailable(ex);
        }
    }

    private async Task<string?> RunIsActiveAsync(CancellationToken ct) {
        try {
            var result = await RunCommandAsync("is-active", ct).ConfigureAwait(false);
            // systemctl prints inactive/failed even with a nonzero exit code.
            return result.Output;
        } catch (OperationCanceledException) {
            // Preserve the never-throws status contract for both the command
            // deadline and caller cancellation. Caller cancellation is normal
            // during shutdown; only the internal deadline is a timeout signal.
            if (!ct.IsCancellationRequested) LogCommandTimedOut("is-active", Unit);
            return null;
        } catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                         or PlatformNotSupportedException or IOException) {
            LogSystemctlUnavailable(ex);
            return null;
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(string verb, CancellationToken ct) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(CommandTimeout);
        var start = new ProcessStartInfo("systemctl") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--no-ask-password");
        start.ArgumentList.Add(verb);
        start.ArgumentList.Add(Unit);
        return await CommandRunner(start, deadline.Token).ConfigureAwait(false);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(ProcessStartInfo start, CancellationToken ct) {
        using var process = Process.Start(start) ?? throw new IOException("Could not start systemctl");
        // Drain both pipes concurrently: neither may fill while waiting on the other.
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        try {
            await Task.WhenAll(output, error, process.WaitForExitAsync(ct)).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        } catch (OperationCanceledException) {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            catch (Win32Exception) { /* process exited or cannot be killed */ }
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "systemctl {Verb} completed for {Unit}")]
    partial void LogVerbCompleted(string verb, string unit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "systemctl {Verb} {Unit} failed with exit {ExitCode}: {Error}")]
    partial void LogCommandFailed(string verb, string unit, int exitCode, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "systemctl {Verb} {Unit} timed out")]
    partial void LogCommandTimedOut(string verb, string unit);

    [LoggerMessage(Level = LogLevel.Warning, Message = "systemctl unavailable — guider process supervision failed")]
    partial void LogSystemctlUnavailable(Exception ex);
}
