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
/// (one recovery at a time) and bound to a service-lifetime CTS so <see cref="Dispose"/> cancels an
/// in-flight recovery. User-initiated disconnects don't reach here — <c>DisposeGuiderLocked</c>
/// unsubscribes <c>OnConnectionLost</c> first, so only genuine socket deaths trigger recovery.
/// </summary>
public sealed partial class GuiderService {

    private readonly GuiderRecoveryCoordinator _recovery;
    private readonly CancellationTokenSource _recoveryCts = new();
    private int _recovering; // 0 = idle, 1 = a recovery pass is running (Interlocked single-flight)

    // Begin a recovery pass for an unexpected drop. Called from OnConnectionLost while holding _gate;
    // the actual work runs off-thread, so we never block the lock or the listener.
    private void BeginRecoveryLocked() {
        if (_disposed) {
            return;
        }
        if (Interlocked.CompareExchange(ref _recovering, 1, 0) != 0) {
            return; // a recovery is already in flight — don't stack them
        }
        // Capture the token here, under _gate. Dispose() also takes _gate to set _disposed before it
        // disposes the CTS, and we returned above if _disposed — so the CTS is live at this read, and
        // the captured token struct stays valid (and observes cancellation) even after a later dispose.
        var token = _recoveryCts.Token;
        _ = Task.Run(() => RunRecoveryAsync(token), CancellationToken.None);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Background recovery boundary: the §63.3 tree shells out to systemctl and emits notifications; any escape must be contained so a recovery failure can't crash the daemon. Log-and-recover.")]
    private async Task RunRecoveryAsync(CancellationToken token) {
        try {
            await _recovery.RecoverAsync(token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // Service disposed mid-recovery — expected, nothing to do.
        } catch (Exception ex) {
            LogRecoveryError(ex);
        } finally {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Guider crash-recovery pass threw")]
    partial void LogRecoveryError(Exception ex);
}
