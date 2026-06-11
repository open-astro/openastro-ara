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

    // Begin a recovery pass for an unexpected drop. Called from OnConnectionLost while holding _gate;
    // the work runs off-thread so we never block the lock or the listener.
    private void BeginRecoveryLocked() {
        if (_disposed || _recovering) {
            return;
        }
        _recovering = true;
        _recoveryPassCts = new CancellationTokenSource();
        var token = _recoveryPassCts.Token;
        _ = Task.Run(() => RunRecoveryAsync(token), CancellationToken.None);
    }

    // Cancel an in-flight recovery pass. Called under _gate when the guider state changes out from
    // under it (the user reconnected, disconnected, or disposed the service) so the pass unwinds
    // quietly instead of acting on a crash that's no longer current.
    private void CancelRecoveryLocked() {
        _recoveryPassCts?.Cancel();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background recovery boundary: the §63.3 tree shells out to systemctl and emits notifications; any escape must be contained so a recovery failure can't crash the daemon. Log-and-recover.")]
    private async Task RunRecoveryAsync(CancellationToken token) {
        try {
            await _recovery.RecoverAsync(token).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Guider crash-recovery pass threw")]
    partial void LogRecoveryError(Exception ex);
}
