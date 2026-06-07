#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model;
using OpenAstroAra.Equipment.Equipment.MySafetyMonitor;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-9 — headless no-op stub for <see cref="ISafetyMonitorMediator"/>.
/// Wraps the same surface as NINA's real <c>SafetyMonitorMediator</c> but
/// reports "no device connected" + no-op for every operation. Used by the
/// daemon's <see cref="HeadlessSequencerFactory"/> to construct equipment-
/// bound sequence-item prototypes (<see cref="OpenAstroAra.Sequencer.SequenceItem.SafetyMonitor.WaitUntilSafe"/>)
/// in §38k-9.
///
/// Real Alpaca-backed safety-monitor wiring lands when Phase 14e Alpaca
/// simulators get pinned (playbook §14.5.1). Until then this stub keeps
/// the DI graph valid; the JSON converter clones from prototypes whose
/// mediator references remain the stub even after Clone(). Once we have
/// a real impl, swap the DI registration and existing clones get the new
/// reference on next factory rebuild (singleton; rebuilt on daemon restart).
/// </summary>
public sealed class HeadlessSafetyMonitorMediator : ISafetyMonitorMediator {
    private static readonly SafetyMonitorInfo DisconnectedInfo = new() {
        Connected = false,
        IsSafe = false,
    };

    // IDeviceMediator surface — every method is a no-op or returns a
    // "not connected" sentinel. Mirrors the NINA real-mediator shape so
    // that swapping in the real impl later is a one-line DI change.

    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(ISafetyMonitorConsumer consumer) { }
    public void RemoveConsumer(ISafetyMonitorConsumer consumer) { }
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public void Broadcast(SafetyMonitorInfo deviceInfo) { }
    public SafetyMonitorInfo GetInfo() => DisconnectedInfo;
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }
    public IDevice GetDevice() => null!;

    // Events — IDeviceMediator's Connected/Disconnected + ISafetyMonitorMediator's
    // IsSafeChanged. Declared but never fired in the headless stub; subscribers
    // remain attached for free until a real-driver swap-in.
    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
    public event EventHandler<IsSafeEventArgs>? IsSafeChanged;
}