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
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #2169 — the guider link went down while guiding (rig night 2026-11-21: the guider was killed mid-frame).
/// The shared reaction's pause stops the light in progress (<see cref="IGuiderEpisodeListener.GuidedWhenLost"/>),
/// so no unguided frame is saved or counted. Once the §63.3 auto-reconnect (or any connect) brings the link
/// back, PHD2 is up with guiding stopped: guiding is started again and must settle before the runs resume
/// (<see cref="IGuiderEpisodeListener.RestoreGuidingAsync"/>), and the stopped light is taken again. One
/// attempt: a start that fails or never settles leaves the runs paused for the user.
/// </summary>
public sealed partial class GuiderService {

    /// <summary>How long a restarted guider may take to settle (after PHD2's own settle wait) before the
    /// runs are left paused. Internal so tests can shorten it.</summary>
    internal TimeSpan GuidingRestoreSettleTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>How long the restore waits for the new connection's guide-camera reconcile to start and
    /// finish before it starts guiding anyway.</summary>
    internal TimeSpan GuidingRestoreReconcileWait { get; set; } = TimeSpan.FromSeconds(30);

    private EquipmentFaultEvent? _guidedLinkFault; // the open link-down fault, when guiding was on at the loss (under _gate)
    private int _reconcileStartsAtLinkLoss;        // ReconcileStarts when the link went down (under _gate)

    // Caller holds _gate. A guide step or guiding_started since the last stop, or PHD2 still reading a
    // guiding state: either says the frame in progress was being guided.
    private bool GuidingOnLocked() => _guidingSeen || (_guider is not null && IsGuidingState(_guider.State));

    bool IGuiderEpisodeListener.GuidedWhenLost(EquipmentFaultEvent fault) {
        lock (_gate) {
            return _guidedLinkFault is not null && ReferenceEquals(fault, _guidedLinkFault);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A guiding start that throws (out of step, custody, RPC) is a failed restore: the runs stay paused. Logged.")]
    async Task<bool> IGuiderEpisodeListener.RestoreGuidingAsync(EquipmentFaultEvent fault, IReadOnlyList<Guid> runIds, CancellationToken ct) {
        int reconcileStartsAtLoss;
        lock (_gate) {
            if (_guidedLinkFault is null || !ReferenceEquals(fault, _guidedLinkFault)) {
                return true; // not a guided link loss: nothing to bring back
            }
            _guidedLinkFault = null; // one attempt per loss
            reconcileStartsAtLoss = _reconcileStartsAtLinkLoss;
        }
        var sequencer = _sequencerResolver?.Invoke();
        if (sequencer is null || runIds.Count == 0) {
            return true;
        }
        if ((await sequencer.DaemonPausedRunsAsync(runIds, ct).ConfigureAwait(false)).Count == 0) {
            return true; // the user resumed, paused or stopped the runs: guiding is theirs to start
        }
        if (SafetyHoldsRig?.Invoke() == true) {
            LogGuidingRestoreFailed("the safety engine holds the rig");
            return false;
        }
        await WaitForReconcileAsync(reconcileStartsAtLoss, ct).ConfigureAwait(false);
        LogGuidingRestoreStarted();
        bool started;
        try {
            started = RestartGuidingOverride is { } restart
                ? await restart(ct).ConfigureAwait(false)
                : await StartGuiding(forceCalibration: false, _noProgress, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            LogGuidingRestoreFailed(ex.Message);
            return false;
        }
        if (!started) {
            LogGuidingRestoreFailed("guiding did not start");
            return false;
        }
        if (!await WaitForGuidingSettledAsync(GuidingRestoreSettleTimeout, ct).ConfigureAwait(false)) {
            LogGuidingRestoreFailed("guiding did not settle");
            return false;
        }
        LogGuidingRestored();
        return true;
    }

    // The connect task starts the guide-camera reconcile right after Connected (and Recovered); guiding
    // starts after it, so it never races a camera re-pick.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The reconcile contains its own errors; a late or faulted one must not block the restart.")]
    private async Task WaitForReconcileAsync(int startsAtLoss, CancellationToken ct) {
        var deadline = DateTime.UtcNow + GuidingRestoreReconcileWait;
        while (ReconcileStarts == startsAtLoss && DateTime.UtcNow < deadline) {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        var left = deadline - DateTime.UtcNow;
        if (left <= TimeSpan.Zero) {
            return;
        }
        try {
            await ReconcileTask.WaitAsync(left, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception) {
            // timed out or faulted: start guiding anyway
        }
    }

    [LoggerMessage(EventId = 4261, Level = LogLevel.Information, Message = "#2169 guider link back after a loss while guiding — restarting guiding before the paused runs resume")]
    private partial void LogGuidingRestoreStarted();

    [LoggerMessage(EventId = 4262, Level = LogLevel.Information, Message = "#2169 guiding restarted and settled — the paused runs resume and the stopped frame is taken again")]
    private partial void LogGuidingRestored();

    [LoggerMessage(EventId = 4263, Level = LogLevel.Warning, Message = "#2169 guiding not restored after the link loss ({Why}) — the paused runs stay paused")]
    private partial void LogGuidingRestoreFailed(string why);
}
