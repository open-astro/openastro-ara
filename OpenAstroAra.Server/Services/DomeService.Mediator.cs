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

using Microsoft.Extensions.Logging;
using OpenAstroAra.Astrometry;
using OpenAstroAra.Core.Enums;
using OpenAstroAra.Core.Model;
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
/// SyncToScopeCoordinates/IsFollowingScope) goes through the <see cref="DomeFollower"/> attached by the
/// composition root (#1689); with none attached it reports "not following".
/// </summary>
public sealed partial class DomeService : IDomeMediator {

    // ADR 0011 rule 5: the dome commands' timeout values and terminal condition. Hard timeout = wall-clock
    // ceiling for the blocking call only (the settle wait adds at most 1,800 x 100 ms = 180s, so ~8min
    // worst case for a physical dome). The motion flag is required: an unreadable read keeps polling.
    internal static readonly DeviceCommandPolicy DomePolicy = new(Contracts.DeviceType.Dome,
        HardTimeout: TimeSpan.FromMinutes(5), PollInterval: TimeSpan.FromMilliseconds(100), MaxPolls: 1800);

    // Test seam: millisecond timeouts. Production keeps DomePolicy.
    internal DeviceCommandPolicy CommandPolicy { get; init; } = DomePolicy;
    private DeviceCommand? _command;
    private DeviceCommand Command => _command ??= new DeviceCommand(CommandPolicy, _faults, _logger);

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
            var connected = _lifecycle.Live is not null;
            var runtime = _runtime;
            var caps = _domeCaps ?? default;
            var device = _lifecycle.Device;
            return new DomeInfo {
                Connected = connected,
                Name = device?.Name ?? string.Empty,
                DeviceId = device?.UniqueId ?? string.Empty,
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
        RunCommandAsync("dome.shutter.open", c => c.OpenShutter(),
            c => ReadShutter(c) == AscomShutter.Open, cancellationToken);

    public Task<bool> CloseShutter(CancellationToken cancellationToken) =>
        RunCommandAsync("dome.shutter.close", c => c.CloseShutter(),
            c => ReadShutter(c) == AscomShutter.Closed, cancellationToken);

    public Task<bool> Park(CancellationToken cancellationToken) {
        StopFollowing("the dome was parked");
        return RunCommandAsync("dome.park", c => c.Park(),
            c => ReadAtPark(c) && !ReadSlewing(c), cancellationToken);
    }

    public Task<bool> FindHome(CancellationToken cancellationToken) {
        StopFollowing("the dome was sent home");
        return RunCommandAsync("dome.findhome", c => c.FindHome(),
            c => ReadAtHome(c) && !ReadSlewing(c), cancellationToken);
    }

    public Task<bool> SlewToAzimuth(double degrees, CancellationToken cancellationToken) {
        StopFollowing("the dome was turned by hand");
        return FollowSlewAsync(degrees, cancellationToken);
    }

    // The slew itself, without the stop-following side effect: the follower's own moves and a one-off sync use it.
    internal Task<bool> FollowSlewAsync(double degrees, CancellationToken cancellationToken) {
        var target = NormalizeAzimuth(degrees);
        return RunCommandAsync("dome.slew", c => c.SlewToAzimuth(target),
            c => !ReadSlewing(c) && AzimuthReached(c, target), cancellationToken);
    }

    // #1689 — the dome-following surface goes through the DomeFollower once the composition root attaches it;
    // without one (unit tests of the dome alone) it reports "not following" / "didn't enable".
    public bool IsFollowingScope => _follower?.IsFollowing ?? false;

    public Task WaitForDomeSynchronization(CancellationToken cancellationToken) =>
        _follower?.WaitForDomeSynchronization(cancellationToken) ?? Task.CompletedTask;

    public Task<bool> SyncToScopeCoordinates(Coordinates coordinates, PierSide sideOfPier, CancellationToken cancellationToken) =>
        _follower?.SyncToScopeCoordinates(coordinates, sideOfPier, cancellationToken) ?? Task.FromResult(false);

    public async Task<bool> EnableFollowing(CancellationToken cancellationToken) {
        if (_follower is not { } follower) {
            return false;
        }
        try {
            await follower.Start().ConfigureAwait(false);
            return true;
        } catch (InvalidOperationException ex) {
            LogDomeOpFailed(ex, "dome.follow.enable");
            return false;
        }
    }

    public Task<bool> DisableFollowing(CancellationToken cancellationToken) {
        _follower?.TurnOff("turned off");
        return Task.FromResult(_follower is not null);
    }

    private async Task<bool> RunCommandAsync(string op, Action<IDomeDevice> action, Func<IDomeDevice, bool> isDone, CancellationToken ct) {
        if (_lifecycle.IsDisposed) {
            return false;
        }
        var client = _lifecycle.Live;
        if (client is null) {
            return false; // not connected: the instruction's Validate has already blocked this
        }
        await Command.RunAsync(new DeviceCommandRequest(op,
            Call: () => action(client),
            IsLive: () => _lifecycle.IsLive(client),
            CurrentDevice: () => _lifecycle.DeviceIfCurrent(client),
            Terminal: () => ReadCondition(client, isDone),
            Polled: _lifecycle.Refresh), ct).ConfigureAwait(false);
        return true;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Per-poll predicate boundary: a transient/unsupported device read throws; report null ('unknown') so the wait keeps polling rather than faulting. CA1031's log-and-recover boundary applies.")]
    private static bool? ReadCondition(IDomeDevice client, Func<IDomeDevice, bool> isDone) {
        try {
            return isDone(client);
        } catch (Exception) {
            return null;
        }
    }

    // Guarded per-field reads used by the terminal-condition predicates (each may throw on an
    // unsupported/transient property; the predicate treats a throw as not-yet-done via ReadCondition).
    private static AscomShutter ReadShutter(IDomeDevice c) => c.ShutterStatus;
    private static bool ReadAtPark(IDomeDevice c) => c.AtPark;
    private static bool ReadAtHome(IDomeDevice c) => c.AtHome;
    private static bool ReadSlewing(IDomeDevice c) => c.Slewing;
    private static bool AzimuthReached(IDomeDevice c, double target) => Math.Abs(c.Azimuth - target) < AzimuthToleranceDeg;

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
}
