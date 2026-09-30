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
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §63.3 guider-d — crash-recovery glue. When the live guider link drops unexpectedly (the daemon
/// crashed), kick off <see cref="GuiderRecoveryCoordinator"/> on a background task. Single-flight
/// (one recovery at a time) and cancellable: a new connect/disconnect or <see cref="Dispose"/>
/// cancels an in-flight pass via <see cref="CancelRecoveryLocked"/> so it can't emit a stale
/// "recovered"/"failed" notification for a crash the user already handled. User-initiated disconnects
/// never re-enter <c>OnConnectionLost</c> — <c>DisposeGuiderLocked</c> unsubscribes it first, so only
/// genuine socket deaths start a recovery.
/// </summary>
public sealed partial class GuiderService {

    private readonly GuiderRecoveryCoordinator _recovery;
    // The in-flight recovery pass's CTS, or null when idle. All access is under _gate.
    private CancellationTokenSource? _recoveryPassCts;
    private bool _recovering; // true while a pass is running; guarded by _gate (single-flight)
    // The most recent pass's task (#1123): the sequencer mediator's Connect() awaits it rather than
    // starting a second pass. Only meaningful while _recovering; guarded by _gate.
    private Task _recoveryPassTask = Task.CompletedTask;

    // Begin a recovery pass for an unexpected drop. Called from OnConnectionLost while holding _gate;
    // the work runs off-thread so we never block the lock or the listener.
    private void BeginRecoveryLocked() {
        if (_disposed || _recovering) {
            return;
        }
        _recovering = true;
        _recoveryPassCts = new CancellationTokenSource();
        var token = _recoveryPassCts.Token;
        // #1192: pin the pass to the connection that dropped. A profile switch during the pass
        // (POST /profiles/{id}/select swaps the live store without touching the guider) must not
        // turn "recover profile A's guider" into a connect to profile B's.
        var target = CaptureRecoveryTargetLocked();
        // Assigned under _gate before the pass's finally can take the gate, so a reader that sees
        // _recovering == true always gets this pass's task.
        _recoveryPassTask = Task.Run(() => RunRecoveryAsync(target, token), CancellationToken.None);
    }

    // The active profile's guider target + identity, read under _gate.
    private RecoveryTarget CaptureRecoveryTargetLocked() {
        var settings = _profileService.ActiveProfile.GuiderSettings;
        return new RecoveryTarget(settings.PHD2ServerHost ?? string.Empty, settings.PHD2ServerPort, _activeProfileIdResolver?.Invoke());
    }

    // Null while the pass's target is still the active profile's guider; else the reason to stop.
    private string? RecoveryAbandonReasonLocked(RecoveryTarget target) {
        var settings = _profileService.ActiveProfile.GuiderSettings;
        return RecoveryAbandonReason(target, settings.PHD2ServerHost ?? string.Empty, settings.PHD2ServerPort, _activeProfileIdResolver?.Invoke());
    }

    /// <summary>#1192 — the connection a recovery pass is recovering: the host:port that dropped and
    /// the ARA profile that was active when it dropped (null without a profile repository — benches).
    /// Captured at link-down, compared before every reconnect attempt.</summary>
    internal sealed record RecoveryTarget(string Host, int Port, Guid? ProfileId);

    /// <summary>Why a recovery pass must be abandoned, or null while its target is still current:
    /// the active profile changed (a profile switch never carries a recovery across), or the active
    /// profile's guider target no longer matches the connection that dropped.</summary>
    internal static string? RecoveryAbandonReason(RecoveryTarget target, string activeHost, int activePort, Guid? activeProfileId) {
        if (activeProfileId != target.ProfileId) {
            return $"the active profile changed from {target.ProfileId?.ToString() ?? "(none)"} to {activeProfileId?.ToString() ?? "(none)"}";
        }
        if (!string.Equals(activeHost?.Trim(), target.Host.Trim(), StringComparison.OrdinalIgnoreCase) || activePort != target.Port) {
            return $"the profile's guider target changed from {target.Host}:{target.Port} to {activeHost}:{activePort}";
        }
        return null;
    }

    // Cancel an in-flight recovery pass. Called under _gate when the guider state changes out from
    // under it (the user reconnected, disconnected, or disposed the service) so the pass unwinds
    // quietly instead of acting on a crash that's no longer current.
    private void CancelRecoveryLocked() {
        _recoveryPassCts?.Cancel();
    }

    // ─── §63.3 auto-reconnect test seams ───
    /// <summary>Cadence of the reconnect attempts inside the grace window.</summary>
    internal TimeSpan ReconnectAttemptInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Maps the profile's guider_retry_timeout_sec to the reconnect grace window; tests compress it.</summary>
    internal Func<int, TimeSpan> ReconnectGraceFromSeconds { get; set; } = s => TimeSpan.FromSeconds(Math.Max(10, s));

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background recovery boundary: the §63.3 tree shells out to systemctl and emits notifications; any escape must be contained so a recovery failure can't crash the daemon. Log-and-recover.")]
    private async Task RunRecoveryAsync(RecoveryTarget target, CancellationToken token) {
        try {
            // #1192: the coordinator supervises the LOCAL openastro-guider unit. A guider on another
            // machine has nothing here to poll or restart — its own host supervises it — so skip the
            // systemd tree and just retry the connection within the grace window.
            if (!await IsLocalGuiderHostDecision(target.Host, token).ConfigureAwait(false)) {
                token.ThrowIfCancellationRequested();
                LogRemoteHostNoLocalRecovery(target.Host, target.Port);
                // The coordinator would have posted its "Guider connection lost" here, and the §42.2
                // fault reaction stays quiet on a LinkDown with no sequence running because it
                // expects that — so a dropped remote guider must announce itself, or the user
                // hears nothing until the grace window expires.
                await NotifyFaultQuietlyAsync("Guider connection lost",
                    $"Lost the link to the guider at {target.Host}:{target.Port}. It runs on another machine, so ARA cannot restart it; "
                    + "retrying the connection within the guider retry window…").ConfigureAwait(false);
                await TryAutoReconnectAsync(target, localUnitRestarted: false, token).ConfigureAwait(false);
                return;
            }
            var outcome = await _recovery.RecoverAsync(token).ConfigureAwait(false);
            if (outcome == GuiderRecoveryOutcome.Recovered) {
                // §63.3 auto-reconnect: the systemd unit is active again, but ARA's
                // own client is still down (the daemon inside may still be opening
                // its listener). Re-establish within the profile's grace window so
                // an unattended night doesn't stay guider-less over a recovered
                // crash. Runs under the same pass token — a user connect/disconnect
                // cancels it (their action supersedes).
                await TryAutoReconnectAsync(target, localUnitRestarted: true, token).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // Cancelled by a new connect/disconnect or by Dispose — the user/host moved on.
        } catch (Exception ex) {
            LogRecoveryError(ex);
        } finally {
            // Dispose under _gate. Dispose() may have already disposed+nulled the CTS (both paths run
            // under the gate, so they're serialized and the null-check makes either order safe).
            lock (_gate) {
                _recoveryPassCts?.Dispose();
                _recoveryPassCts = null;
                _recovering = false;
            }
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort reconnect: any fault leaves the guider in Error for the user to reconnect manually (the pre-auto-reconnect behavior); logged, never rethrown into the recovery pass.")]
    private async Task TryAutoReconnectAsync(RecoveryTarget target, bool localUnitRestarted, CancellationToken token) {
        try {
            var (graceSeconds, grace) = ReconnectGraceWindow();
            var deadline = DateTimeOffset.UtcNow + grace;

            // #1192: reconnect to the connection that DROPPED (the pass's target), never to
            // whatever the active profile says now — and stop the moment they disagree.
            var (host, port) = (target.Host, target.Port);
            lock (_gate) {
                // Only auto-reconnect over the Error state this pass created; if the
                // user already reconnected (Connected/Connecting) or gave up
                // (Disconnected), their action wins.
                if (_disposed || _state != EquipmentConnectionState.Error) {
                    return;
                }
            }
            LogReconnectStarted(host, port, graceSeconds);

            while (DateTimeOffset.UtcNow < deadline && !token.IsCancellationRequested) {
                lock (_gate) {
                    if (_disposed || _state is EquipmentConnectionState.Connected or EquipmentConnectionState.Disconnected) {
                        return; // the user (or a prior attempt) settled it
                    }
                }
                // A profile switch (or a re-targeted profile) since the drop makes this pass's guider
                // no longer the one in use: BeginConnect refuses (false, logged) and we leave Error
                // for the new profile's own connect — the sequencer's ConnectEquipment /
                // ReconnectTrigger and the user's connect both go to the ACTIVE profile's target (a
                // clean restart, not a carry-over).
                if (!BeginConnect(new GuiderConnectRequestDto(host, port), supersedeRecovery: false, recoveringFor: target)) {
                    return;
                }

                // The connect runs 202-style in the background; poll its outcome
                // until it settles (Connected or back to Error) or the grace expires.
                while (DateTimeOffset.UtcNow < deadline && !token.IsCancellationRequested) {
                    EquipmentConnectionState state;
                    lock (_gate) { state = _state; }
                    if (state == EquipmentConnectionState.Connected) {
                        LogReconnectSucceeded();
                        await NotifyFaultQuietlyAsync("Guider reconnected automatically",
                            (localUnitRestarted
                                ? "The guider process was recovered and ARA reconnected to it. "
                                : $"The guider at {host}:{port} is reachable again and ARA reconnected to it. ")
                            + "Guiding settings were re-pushed on connect. "
                            + "If your sequence was paused by the guider loss, Resume it when ready.").ConfigureAwait(false);
                        return;
                    }
                    if (state is EquipmentConnectionState.Error or EquipmentConnectionState.Disconnected) {
                        break; // this attempt settled unsuccessfully — retry after the interval
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500, ReconnectAttemptInterval.TotalMilliseconds)), token).ConfigureAwait(false);
                }
                await Task.Delay(ReconnectAttemptInterval, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            LogReconnectGaveUp(graceSeconds);
            await NotifyFaultQuietlyAsync(localUnitRestarted ? "Guider recovered but not reconnected" : "Guider not reconnected",
                (localUnitRestarted
                    ? "The guider process was restarted, but ARA could not re-establish its connection within the retry window. "
                    : $"The guider at {host}:{port} runs on another machine (ARA cannot restart it) and did not come back within the retry window. ")
                + "Reconnect it manually (Equipment → Guider), then Resume any paused run.").ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // User action superseded the auto-reconnect — nothing to report.
        } catch (Exception ex) {
            LogReconnectFailed(ex);
        }
    }

    // The profile's guider_retry_timeout_sec and the grace window it maps to. Shared by the §63.3
    // auto-reconnect and the sequencer mediator's Connect() (#1123) so both honour the same bound.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A profile-store read failure must not break a reconnect; fall back to the 60 s default and log.")]
    private (int Seconds, TimeSpan Window) ReconnectGraceWindow() {
        int graceSeconds;
        try {
            graceSeconds = _profileStore?.GetSafetyPolicies().GuiderRetryTimeoutSec ?? 60;
        } catch (Exception ex) {
            LogReconnectPolicyReadFailed(ex);
            graceSeconds = 60;
        }
        return (graceSeconds, ReconnectGraceFromSeconds(graceSeconds));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Guider crash-recovery pass threw")]
    partial void LogRecoveryError(Exception ex);

    [LoggerMessage(EventId = 6334, Level = LogLevel.Information, Message = "§63.3 auto-reconnect started: {Host}:{Port}, grace {GraceSeconds}s")]
    private partial void LogReconnectStarted(string host, int port, int graceSeconds);

    [LoggerMessage(EventId = 6335, Level = LogLevel.Information, Message = "§63.3 auto-reconnect succeeded")]
    private partial void LogReconnectSucceeded();

    [LoggerMessage(EventId = 6336, Level = LogLevel.Warning, Message = "§63.3 auto-reconnect gave up after the {GraceSeconds}s grace window")]
    private partial void LogReconnectGaveUp(int graceSeconds);

    [LoggerMessage(EventId = 6337, Level = LogLevel.Warning, Message = "§63.3 profile read failed for the reconnect grace window; using 60s")]
    private partial void LogReconnectPolicyReadFailed(Exception exception);

    [LoggerMessage(EventId = 6338, Level = LogLevel.Error, Message = "§63.3 auto-reconnect failed; the guider stays in Error for a manual reconnect")]
    private partial void LogReconnectFailed(Exception exception);

    [LoggerMessage(EventId = 6366, Level = LogLevel.Warning, Message = "§63.3 auto-reconnect to {Host}:{Port} abandoned: {Reason}. The guider stays in Error; connect the active profile's guider")]
    private partial void LogReconnectAbandoned(string host, int port, string reason);
}
