// Headless server stub: the device-event members in this file satisfy the
// equipment mediator interfaces but are never raised server-side (the Flutter
// client drives state over REST/WS), so CS0067 "event is never used" is
// expected here and intentionally suppressed for the whole file.
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

using OpenAstroAra.Equipment.Equipment.MyRotator;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;

namespace OpenAstroAra.Server.Services.Equipment;

/// <summary>
/// §38k-16 — headless no-op stub for <see cref="IRotatorMediator"/>.
/// Same pattern as the §38k-9 … §38k-13 stubs. Backs the
/// <c>MoveRotatorMechanical</c> instruction prototype.
/// </summary>
public sealed class HeadlessRotatorMediator : IRotatorMediator {

    private static readonly RotatorInfo DisconnectedInfo = new() {
        Connected = false,
    };

    // IDeviceMediator surface
    public void RegisterHandler(object handler) { }
    public void RegisterConsumer(IRotatorConsumer consumer) { }
    public void RemoveConsumer(IRotatorConsumer consumer) { }
    public Task<IList<string>> Rescan() => Task.FromResult<IList<string>>(new List<string>());
    public Task<bool> Connect() => Task.FromResult(false);
    public Task Disconnect() => Task.CompletedTask;
    public void Broadcast(RotatorInfo deviceInfo) { }
    public RotatorInfo GetInfo() => DisconnectedInfo;
    public string Action(string actionName, string actionParameters) => string.Empty;
    public string SendCommandString(string command, bool raw = true) => string.Empty;
    public bool SendCommandBool(string command, bool raw = true) => false;
    public void SendCommandBlind(string command, bool raw = true) { }
    public IDevice GetDevice() =>
        throw new NotSupportedException(
            "Headless rotator stub has no backing IDevice; real Alpaca-backed wiring swaps in at the DI registration point once §14e Alpaca simulator pinning lands.");

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;

    // IRotatorMediator additions — move ops report 0 (no driver attached);
    // real impls return the device's reported post-move angle. The pure
    // target-position helpers apply no offset, so they echo the input.
    public void Sync(float skyAngle) { }
    public Task<float> MoveMechanical(float position, CancellationToken ct) => Task.FromResult(0f);
    public Task<float> Move(float position, CancellationToken ct) => Task.FromResult(0f);
    public Task<float> MoveRelative(float position, CancellationToken ct) => Task.FromResult(0f);
    public float GetTargetPosition(float position) => position;
    public float GetTargetMechanicalPosition(float position) => position;

    public event EventHandler<RotatorEventArgs>? Synced;
    public event Func<object, RotatorEventArgs, Task>? Moved;
    public event Func<object, RotatorEventArgs, Task>? MovedMechanical;
}
