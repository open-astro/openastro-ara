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
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>Who wants the Guide Camera. <see cref="Guiding"/> takes it from anyone; the other three are
/// setup activities (ADR 0016).</summary>
public enum CameraUse { Guiding, PolarAlign, GuideFocus, LiveView }

/// <summary>What the custody module needs from the Guider: its app state and the guider program's
/// single-client lease. <c>GuiderService</c> implements it; a holder or the Guider passes it in, so the
/// module needs no reference to either (no construction cycle) and tests need no socket.</summary>
public interface IGuideCameraPort {
    /// <summary>The PHD2 app-state token, or null when none is known.</summary>
    string? AppState { get; }
    /// <summary>Whether the guider reports a lease; null when it does not say.</summary>
    Task<bool?> LeaseActiveAsync(CancellationToken ct);
    Task SetLeaseAsync(bool active, int? timeoutS, CancellationToken ct);
}

/// <summary>
/// ADR 0016 — custody of the Guide Camera, in one place. Starting Guiding takes it from any holder (the
/// holder is stopped, not completed, then the lease released). The Polar Align Routine, guide focus and
/// the Guider live view are refused while the Guider is busy, and between them the newer request takes it
/// (the older one is stopped first). This is the only code that takes, renews or releases the guider
/// program's lease. Guiding itself is not stored as a holder: the Guider's own state says it.
/// A holder hands the module its stop callback when it takes custody, so the module never references the
/// holder services. Stop callbacks run without a module lock held; a stop must not take custody itself.
/// Two takes racing each other are not serialised: the last one to finish holds (a stop callback that
/// waits on the other service's lock would deadlock a serial gate).
/// </summary>
public sealed partial class GuideCameraCustody {

    /// <summary>The lease the guider program grants; renewed at less than half of it so one missed renew
    /// still leaves margin.</summary>
    public const int LeaseTimeoutSeconds = 600;

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Claim? _holder;

    public GuideCameraCustody(ILogger<GuideCameraCustody>? logger = null) {
        _logger = logger ?? NullLogger<GuideCameraCustody>.Instance;
    }

    /// <summary>How often a holder renews the lease. Settable so tests use a short interval.</summary>
    internal TimeSpan LeaseRenewInterval { get; set; } = TimeSpan.FromSeconds(240);

    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>The one definition of "the Guider is busy": guiding, calibrating, paused or star lost.
    /// (<c>GuiderService.IsGuidingState</c> is a different question: what a meridian flip must stop and
    /// bring back, which leaves Paused alone.)</summary>
    public static bool IsBusy(string? appState) => appState is "Guiding" or "Calibrating" or "Paused" or "LostLock";

    /// <summary>The setup activity that holds the camera now, or null.</summary>
    public CameraUse? Holder {
        get {
            lock (_gate) {
                return _holder?.Use;
            }
        }
    }

    /// <summary>Why the Guider's equipment must not be released because a setup activity holds the camera,
    /// or null. A live view counts only while the guider is looping or has a star selected.</summary>
    public string? HeldReason(string? appState) {
        return Holder switch {
            CameraUse.PolarAlign => "A polar alignment is in progress. It will be deleted once it ends.",
            CameraUse.GuideFocus => "The guide-camera focus loop is running. It will be deleted once it stops.",
            CameraUse.LiveView when appState is "Looping" or "Selected" =>
                "The guider live view is open. It will be deleted once it is stopped.",
            _ => null,
        };
    }

    /// <summary>
    /// A setup activity asks for the camera. Refused (<see cref="InvalidOperationException"/>, with the
    /// reason) while the Guider is busy; otherwise any older setup holder is stopped first, then the
    /// lease is taken (the live view holds custody only, no lease). A failure taking the lease leaves
    /// nobody holding.
    /// </summary>
    internal async Task<Claim> TakeAsync(CameraUse use, IGuideCameraPort port, Func<CameraUse, Task> stop, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(stop);
        if (use == CameraUse.Guiding) {
            throw new ArgumentOutOfRangeException(nameof(use), use, "guiding takes the camera through TakeForGuidingAsync");
        }
        var state = port.AppState;
        if (IsBusy(state)) {
            throw new InvalidOperationException($"the guider is {state!.ToLowerInvariant()} — stop guiding before {Purpose(use)}");
        }
        Claim? older;
        lock (_gate) {
            older = _holder;
        }
        // The same activity asking again replaces its own claim; it is not stopped.
        if (older is not null && older.Use != use) {
            await StopHolderAsync(older, use).ConfigureAwait(false);
        }
        var claim = new Claim(this, use, port, stop);
        if (use != CameraUse.LiveView) {
            await port.SetLeaseAsync(active: true, LeaseTimeoutSeconds, ct).ConfigureAwait(false);
        }
        lock (_gate) {
            _holder = claim;
        }
        return claim;
    }

    /// <summary>
    /// Starting Guiding takes the camera from any holder: the setup holder is stopped (not completed), then
    /// the lease is released; a lease the guider reports while nobody here holds it is stale and released
    /// too. Best-effort: a fault is logged and never stops the guide attempt (2026-10-03, 2026-10-16).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Best-effort hand-over before guiding: the guide attempt reports its own outcome.")]
    public async Task TakeForGuidingAsync(IGuideCameraPort port, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(port);
        try {
            Claim? holder;
            lock (_gate) {
                holder = _holder;
            }
            if (holder is not null) {
                await StopHolderAsync(holder, CameraUse.Guiding).ConfigureAwait(false);
                LogHolderStopped(holder.Use);
            }
            bool? reported = null;
            try {
                reported = await port.LeaseActiveAsync(ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                LogLeaseReadFailed(ex);
            }
            if (holder is not null || reported == true) {
                await port.SetLeaseAsync(active: false, timeoutS: null, ct).ConfigureAwait(false);
                LogLeaseReleased();
            }
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            LogHandOverFailed(ex);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The holder's stop is best-effort: a faulty holder never keeps the camera from its successor.")]
    private async Task StopHolderAsync(Claim holder, CameraUse by) {
        try {
            await holder.StopAsync(by).ConfigureAwait(false);
        } catch (Exception ex) {
            LogStopFailed(holder.Use, ex);
        }
        lock (_gate) {
            if (ReferenceEquals(_holder, holder)) {
                _holder = null;
            }
        }
        // The holder normally released its lease on the way out; a faulty or slow one may not have. Guiding
        // releases it itself, once, after it has looked at what the guider reports.
        if (by != CameraUse.Guiding && holder.Use != CameraUse.LiveView) {
            await holder.ReleaseLeaseAsync().ConfigureAwait(false);
        }
    }

    private static string Purpose(CameraUse use) => use switch {
        CameraUse.PolarAlign => "starting polar alignment",
        CameraUse.GuideFocus => "focusing the guide camera",
        _ => "opening the live view",
    };

    /// <summary>One setup activity's hold on the camera. Dropped by <see cref="ReleaseAsync"/>, or by the
    /// module when a newer request or Guiding takes the camera.</summary>
    internal sealed class Claim {
        private readonly GuideCameraCustody _owner;
        private readonly IGuideCameraPort _port;
        private readonly Func<CameraUse, Task> _stop;
        private DateTimeOffset _lastRenew;

        internal Claim(GuideCameraCustody owner, CameraUse use, IGuideCameraPort port, Func<CameraUse, Task> stop) {
            _owner = owner;
            Use = use;
            _port = port;
            _stop = stop;
            _lastRenew = owner.Clock();
        }

        public CameraUse Use { get; }

        internal Task StopAsync(CameraUse by) => _stop(by);

        private bool IsCurrent {
            get {
                lock (_owner._gate) {
                    return ReferenceEquals(_owner._holder, this);
                }
            }
        }

        /// <summary>Renews the lease when the interval has passed. Best-effort: a transient fault must not
        /// end a long session (the interval leaves margin inside the 600 s lease). No-op for a live view,
        /// or once this claim is no longer the holder.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Lease renewal is best-effort: an expired lease fails the next capture, which the holder already handles.")]
        public async Task RenewIfDueAsync(CancellationToken ct) {
            if (Use == CameraUse.LiveView || !IsCurrent) {
                return;
            }
            var now = _owner.Clock();
            if (now - _lastRenew <= _owner.LeaseRenewInterval) {
                return;
            }
            try {
                await _port.SetLeaseAsync(active: true, LeaseTimeoutSeconds, ct).ConfigureAwait(false);
                _lastRenew = now;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                _owner.LogLeaseRenewFailed(Use, ex);
            }
        }

        /// <summary>Gives the camera back and releases the lease (best-effort; it expires on its own). Does
        /// nothing for a claim another request has already replaced.</summary>
        public async Task ReleaseAsync() {
            lock (_owner._gate) {
                if (!ReferenceEquals(_owner._holder, this)) {
                    return;
                }
                _owner._holder = null;
            }
            if (Use != CameraUse.LiveView) {
                await ReleaseLeaseAsync().ConfigureAwait(false);
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types",
            Justification = "Lease release is best-effort: the guider may be gone, and the lease auto-expires.")]
        internal async Task ReleaseLeaseAsync() {
            try {
                await _port.SetLeaseAsync(active: false, timeoutS: null, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                _owner.LogLeaseReleaseFailed(Use, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Guide Camera: {Holder} was stopped because guiding takes the camera")]
    private partial void LogHolderStopped(CameraUse holder);

    [LoggerMessage(Level = LogLevel.Information, Message = "Released the guider's polar-align lease before guiding (guiding takes the guide camera from whoever held it)")]
    private partial void LogLeaseReleased();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the guider's polar-align lease before guiding — trying to guide anyway")]
    private partial void LogLeaseReadFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not hand the Guide Camera to guiding cleanly — trying to guide anyway")]
    private partial void LogHandOverFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide Camera: stopping {Holder} failed; the camera is taken regardless")]
    private partial void LogStopFailed(CameraUse holder, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide Camera: {Holder} lease renew failed (retrying next iteration — the lease has margin)")]
    private partial void LogLeaseRenewFailed(CameraUse holder, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Guide Camera: {Holder} lease release failed (it expires on its own)")]
    private partial void LogLeaseReleaseFailed(CameraUse holder, Exception ex);
}