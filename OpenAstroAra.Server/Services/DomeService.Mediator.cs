// The device-event members satisfy the equipment mediator interface but are never raised server-side
// (the Flutter client drives state over REST/WS), so CS0067 "event is never used" is expected here
// and intentionally suppressed for the whole file — same as the other *Service.Mediator.cs partials.
#pragma warning disable CS0067

#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using ASCOM.Alpaca.Clients;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Equipment.Equipment.MyDome;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using AscomShutter = ASCOM.Common.DeviceInterfaces.ShutterState;
using NinaShutter = OpenAstroAra.Equipment.Interfaces.ShutterState;

namespace OpenAstroAra.Server.Services;

/// <summary>Read-once dome capability flags cached for the <see cref="IDomeMediator"/> snapshot.</summary>
internal readonly record struct DomeCaps(
    bool CanSetShutter, bool CanSetAzimuth, bool CanSyncAzimuth, bool CanPark, bool CanFindHome,
    // §25.5.5 — gates the "set park position" control (distinct from CanPark: a dome can be
    // parkable at a factory position without accepting a new one).
    bool CanSetPark);

/// <summary>
/// §14e — the real <see cref="DomeService"/> also serves the Sequencer's <see cref="IDomeMediator"/>
/// (playbook §8.1: one singleton backs both the REST service and the mediator), so the dome
/// instructions (<c>OpenDomeShutter</c>, <c>CloseDomeShutter</c>, <c>ParkDome</c>, <c>FindHomeDome</c>,
/// <c>SlewDomeAzimuth</c>) drive the live Alpaca dome instead of the <c>HeadlessDomeMediator</c> stub.
///
/// The control ops drive the device and block on a bounded wait for their terminal condition (so the
/// instruction's post-op <c>GetInfo().ShutterStatus</c> / success check is accurate), mirroring the
/// hardened focuser/rotator-mediator move path. The dome-following surface (EnableFollowing/
/// SyncToScopeCoordinates/IsFollowingScope) stays a no-op stub: software dome-sync is the separate
/// <c>IDomeFollower</c> subsystem (§38k-21), not yet wired into the headless daemon.
/// </summary>
public sealed partial class DomeService : IDomeMediator {

    private const int DomeOpSettleMaxPolls = 1800; // ~180s at the poll interval — domes are slow
    private static readonly TimeSpan DomeOpPollInterval = TimeSpan.FromMilliseconds(100);
    // Wall-clock ceiling for a single blocking dome call (OpenShutter/Park/...): a silent device must
    // not park a sequence thread until the OS TCP timeout. The terminal-condition wait adds its own
    // bound (DomeOpSettleMaxPolls × DomeOpPollInterval ≈ 3min) on top, so the worst-case total before
    // RunDomeOpAsync returns is ~8min — generous for a physical dome; cancellation (ct) cuts it short.
    private static readonly TimeSpan DomeOpHardTimeout = TimeSpan.FromMinutes(5);
    // Sanity bound on the post-slew azimuth, paired with the authoritative !Slewing signal. Generous
    // (5°) on purpose: physical domes settle within their own 1–5° positioning accuracy, so a tighter
    // window would let a *completed* slew (Slewing==false) get vetoed and time out. It still guards
    // against a premature exit where Slewing hasn't yet asserted and the dome is at its old heading.
    private const double AzimuthToleranceDeg = 5.0;

    /// <summary>
    /// Synchronous live snapshot for the Sequencer, served from the §32.4 cache (no blocking HTTP on
    /// the sequence thread). Never throws after Dispose. Maps the cached raw ASCOM shutter status to
    /// NINA's <see cref="NinaShutter"/> and surfaces the read-once capability flags the dome
    /// instructions validate against (CanFindHome / CanSetAzimuth / ...).
    /// </summary>
    public DomeInfo GetInfo() {
        lock (_gate) {
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            var runtime = _runtime;
            var caps = _domeCaps ?? default;
            return new DomeInfo {
                Connected = connected,
                Name = _device?.Name ?? string.Empty,
                DeviceId = _device?.UniqueId ?? string.Empty,
                ShutterStatus = connected ? MapShutter(_shutterStatusRaw) : NinaShutter.ShutterNone,
                Azimuth = connected ? runtime.AzimuthDeg ?? double.NaN : double.NaN,
                AtHome = connected && runtime.AtHome,
                AtPark = connected && runtime.Parked,
                Slewing = connected && runtime.State == "slewing",
                CanSetShutter = connected && caps.CanSetShutter,
                CanSetAzimuth = connected && caps.CanSetAzimuth,
                CanSyncAzimuth = connected && caps.CanSyncAzimuth,
                CanPark = connected && caps.CanPark,
                CanFindHome = connected && caps.CanFindHome,
            };
        }
    }

    private static NinaShutter MapShutter(AscomShutter s) => s switch {
        AscomShutter.Open => NinaShutter.ShutterOpen,
        AscomShutter.Closed => NinaShutter.ShutterClosed,
        AscomShutter.Opening => NinaShutter.ShutterOpening,
        AscomShutter.Closing => NinaShutter.ShutterClosing,
        AscomShutter.Error => NinaShutter.ShutterError,
        _ => NinaShutter.ShutterNone,
    };

    public Task<bool> OpenShutter(CancellationToken cancellationToken) =>
        RunDomeOpAsync("dome.shutter.open", c => c.OpenShutter(),
            c => ReadShutter(c) == AscomShutter.Open, cancellationToken);

    public Task<bool> CloseShutter(CancellationToken cancellationToken) =>
        RunDomeOpAsync("dome.shutter.close", c => c.CloseShutter(),
            c => ReadShutter(c) == AscomShutter.Closed, cancellationToken);

    public Task<bool> Park(CancellationToken cancellationToken) =>
        RunDomeOpAsync("dome.park", c => c.Park(),
            c => ReadAtPark(c) && !ReadSlewing(c), cancellationToken);

    public Task<bool> FindHome(CancellationToken cancellationToken) =>
        RunDomeOpAsync("dome.findhome", c => c.FindHome(),
            c => ReadAtHome(c) && !ReadSlewing(c), cancellationToken);

    public Task<bool> SlewToAzimuth(double degrees, CancellationToken cancellationToken) {
        var target = NormalizeAzimuth(degrees);
        return RunDomeOpAsync("dome.slew", c => c.SlewToAzimuth(target),
            c => !ReadSlewing(c) && AzimuthReached(c, target), cancellationToken);
    }

    // The dome-following surface is the separate IDomeFollower subsystem (§38k-21), not wired into the
    // headless daemon yet — report "not following" / "didn't enable" like the headless stub.
    public bool IsFollowingScope => false;
    public Task WaitForDomeSynchronization(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<bool> SyncToScopeCoordinates(Coordinates coordinates, PierSide sideOfPier, CancellationToken cancellationToken) =>
        Task.FromResult(false);
    public Task<bool> EnableFollowing(CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<bool> DisableFollowing(CancellationToken cancellationToken) => Task.FromResult(false);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Sequencer dome-op boundary: the blocking ASCOM call can throw arbitrary driver/HTTP exceptions and a concurrent Disconnect/Dispose can dispose the captured client mid-op; genuine sequencer cancellation is rethrown but any other escape (including a device/HTTP-timeout OCE) is logged and reported as a failed op (false) rather than faulting the autonomous run. CA1031's log-and-recover boundary applies.")]
    private async Task<bool> RunDomeOpAsync(string op, Action<AlpacaDome> action, Func<AlpacaDome, bool> isDone, CancellationToken ct) {
        AlpacaDome? client;
        lock (_gate) {
            if (_disposed) {
                return false;
            }
            client = _state == EquipmentConnectionState.Connected ? _client : null;
        }
        if (client is null) {
            return false; // not connected: the instruction's Validate has already blocked this
        }
        try {
            // Race the blocking ASCOM call against both the sequencer token and a wall-clock bound so
            // a hung HTTP call can't pin the sequence thread. The abandoned op is observed.
            var opTask = Task.Run(() => action(client), CancellationToken.None);
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                linked.CancelAfter(DomeOpHardTimeout);
                var completed = await Task.WhenAny(opTask, Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);
                if (completed != opTask) {
                    ObserveQuietly(opTask);
                    ct.ThrowIfCancellationRequested();      // sequencer cancel → propagate
                    throw new TimeoutException($"dome op {op} did not complete within {DomeOpHardTimeout.TotalSeconds:0}s");
                }
                await linked.CancelAsync().ConfigureAwait(false);
            }
            await opTask.ConfigureAwait(false); // observe the op's result / surface its exception
            return await WaitForDomeConditionAsync(client, isDone, ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw; // genuine sequencer cancellation — propagate so the run aborts
        } catch (Exception ex) {
            LogDomeOpFailed(ex, op);
            return false;
        }
    }

    // Polls the device directly until the terminal condition holds (refreshing the §32.4 cache each
    // tick so GetInfo stays current), or returns false on timeout / a dropped-or-superseded connection.
    private async Task<bool> WaitForDomeConditionAsync(AlpacaDome client, Func<AlpacaDome, bool> isDone, CancellationToken ct) {
        var loggedReadFailure = false;
        for (var i = 0; i < DomeOpSettleMaxPolls; i++) {
            bool stillOurClient;
            lock (_gate) {
                stillOurClient = !_disposed && _state == EquipmentConnectionState.Connected
                    && ReferenceEquals(_client, client);
            }
            if (!stillOurClient) {
                return false; // disconnected / superseded — the op can't be confirmed
            }
            var done = ReadCondition(client, isDone);
            if (done is null && !loggedReadFailure) {
                // A consistently-throwing condition read (e.g. an unsupported property) would
                // otherwise time out silently after the full bound — log it once so the timeout is
                // diagnosable, then keep polling (treat as not-yet-met).
                LogConditionReadFailed();
                loggedReadFailure = true;
            }
            if (done == true) {
                RefreshCacheOnce();
                return true;
            }
            RefreshCacheOnce();
            await Task.Delay(DomeOpPollInterval, ct).ConfigureAwait(false);
        }
        return false; // timed out without reaching the terminal state
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-poll predicate boundary: a transient/unsupported device read throws; report null ('unknown') so the wait keeps polling (and logs once) rather than faulting. CA1031's log-and-recover boundary applies.")]
    private static bool? ReadCondition(AlpacaDome client, Func<AlpacaDome, bool> isDone) {
        try {
            return isDone(client);
        } catch (Exception) {
            return null;
        }
    }

    // Observes an abandoned (cancelled/timed-out) op task so a later fault can't surface as an
    // UnobservedTaskException, and logs it at Debug for post-mortem.
    private void ObserveQuietly(Task task) {
        _ = task.ContinueWith(
            t => LogAbandonedOpFaulted(t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Guarded per-field reads used by the terminal-condition predicates (each may throw on an
    // unsupported/transient property; the predicate treats a throw as not-yet-done via ReadCondition).
    private static AscomShutter ReadShutter(AlpacaDome c) => c.ShutterStatus;
    private static bool ReadAtPark(AlpacaDome c) => c.AtPark;
    private static bool ReadAtHome(AlpacaDome c) => c.AtHome;
    private static bool ReadSlewing(AlpacaDome c) => c.Slewing;
    private static bool AzimuthReached(AlpacaDome c, double target) => Math.Abs(c.Azimuth - target) < AzimuthToleranceDeg;

    private static double NormalizeAzimuth(double deg) {
        var a = deg % 360.0;
        return a < 0 ? a + 360.0 : a;
    }

    // Connection lifecycle is REST-driven; these mirror the headless stub. The instructions never
    // call them.
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());

    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IDomeConsumer consumer) { }
    public void RemoveConsumer(IDomeConsumer consumer) { }
    public void Broadcast(DomeInfo deviceInfo) { }

    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }

    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "DomeService does not expose a raw ASCOM IDevice; the Sequencer uses GetInfo() and the dome ops, and connection is driven through the REST surface.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
    public event EventHandler<EventArgs>? Synced;
    public event Func<object, EventArgs, Task>? Opened;
    public event Func<object, EventArgs, Task>? Closed;
    public event Func<object, EventArgs, Task>? Parked;
    public event Func<object, EventArgs, Task>? Homed;
    public event Func<object, DomeEventArgs, Task>? Slewed;

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Warning, Message = "Dome mediator op {Op} failed")]
    private partial void LogDomeOpFailed(Exception ex, string op);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "abandoned dome op (cancelled/timed-out) later faulted")]
    private partial void LogAbandonedOpFaulted(Exception ex);

    [LoggerMessage(Level = Microsoft.Extensions.Logging.LogLevel.Debug, Message = "dome terminal-condition read failed during settle-wait (will keep polling)")]
    private partial void LogConditionReadFailed();
}
