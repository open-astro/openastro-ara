// The device-event members (Connected/Disconnected/IsSafeChanged) satisfy the equipment mediator
// interfaces but are never raised server-side (the Flutter client drives state over REST/WS, and
// WaitUntilSafe polls GetInfo() rather than subscribing), so CS0067 "event is never used" is
// expected here and intentionally suppressed for the whole file — same as HeadlessSafetyMonitorMediator.
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

using OpenAstroAra.Equipment.Equipment.MySafetyMonitor;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §14e — the real <see cref="SafetyMonitorService"/> also serves the Sequencer's
/// <see cref="ISafetyMonitorMediator"/> (playbook §8.1: one singleton backs both the REST service
/// and the mediator), so <c>WaitUntilSafe</c> reads the live Alpaca device instead of the
/// <c>HeadlessSafetyMonitorMediator</c> "not connected" stub.
///
/// <c>WaitUntilSafe</c> only ever calls <see cref="GetInfo"/>, so that is the single member with
/// real behaviour. Connection lifecycle is driven through the REST surface
/// (<see cref="ConnectAsync"/>/<see cref="DisconnectAsync"/>), so the parameterless mediator
/// <c>Connect()</c>/<c>Disconnect()</c> and the command/event members mirror the headless stub.
/// </summary>
public sealed partial class SafetyMonitorService : ISafetyMonitorMediator {

    /// <summary>
    /// Synchronous live snapshot for the Sequencer: reports the current connection state and,
    /// when connected, a fresh (bounded, ~3s ASCOM timeout) <c>IsSafe</c> read. Unlike the REST
    /// methods this never throws after Dispose — a running sequence may poll during shutdown, so
    /// a disposed/torn-down service simply reports "not connected".
    /// </summary>
    public SafetyMonitorInfo GetInfo() {
        lock (_gate) {
            // Single consistent snapshot, served from the cache the background loop maintains
            // (§32.4) — no blocking HTTP read on the sequence thread. Never throws after Dispose
            // (a running sequence may poll during shutdown): _disposed simply yields "not connected".
            var connected = !_disposed && _state == EquipmentConnectionState.Connected && _client is not null;
            return new SafetyMonitorInfo {
                Connected = connected,
                IsSafe = connected && _cachedSafe,
                Name = _device?.Name ?? string.Empty,
                DeviceId = _device?.UniqueId ?? string.Empty,
            };
        }
    }

    // Connection lifecycle is driven by the REST surface (ConnectAsync/DisconnectAsync), not the
    // parameterless mediator Connect/Disconnect; these mirror the headless stub. WaitUntilSafe
    // never calls them.
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());

    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(ISafetyMonitorConsumer consumer) { }
    public void RemoveConsumer(ISafetyMonitorConsumer consumer) { }
    public void Broadcast(SafetyMonitorInfo deviceInfo) { }

    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }

    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "SafetyMonitorService does not expose a raw ASCOM IDevice; the Sequencer uses GetInfo() and connection is driven through the REST surface.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
    public event EventHandler<IsSafeEventArgs>? IsSafeChanged;
}
