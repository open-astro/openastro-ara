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
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Sequencer.Connections;

/// <summary>A device role the Connect family names. Mount is the ASCOM Telescope.</summary>
public enum EquipmentRole { Camera, FilterWheel, Focuser, Rotator, Mount, Guider, Switch, FlatPanel, Weather, Dome, SafetyMonitor }

/// <summary>
/// The Sequencer's one view of the rig's connections (#1545, ADR 0023). The device a role connects is the one
/// the rig remembers, or the one its service retains: no instruction reads a profile device id. The Server's
/// <c>EquipmentReconnector</c> implements it, so the Sequencer, auto-connect and the REST reconnect share one
/// answer. A connect or recover that returns has confirmed the device Connected, or it throws.
/// </summary>
public interface IEquipmentConnections {
    /// <summary>The service's own state for the role.</summary>
    bool IsConnected(EquipmentRole role);

    /// <summary>True when the user (or a Disconnect Equipment item) disconnected the role's device, so recovery leaves it alone.
    /// Always false for the guider, which records no desired state.</summary>
    bool IsDisconnectedByUser(EquipmentRole role);

    /// <summary>The user's connect: dispatches the role's known device, then waits until it is connected.
    /// A cancel ends only the wait: the device's own background connect keeps running.</summary>
    /// <exception cref="EquipmentRefusedException">Nothing is remembered or retained for the role.</exception>
    /// <exception cref="SequenceEntityFailedException">The device did not connect.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    Task ConnectAsync(EquipmentRole role, CancellationToken ct);

    /// <summary>The trigger's connect: like <see cref="ConnectAsync"/>, but a device the user disconnected is not dialled.</summary>
    /// <exception cref="EquipmentRefusedException">Nothing is known for the role, or the user disconnected it.</exception>
    Task RecoverAsync(EquipmentRole role, CancellationToken ct);
}

/// <summary>Why a command was refused before it touched a device.</summary>
public enum EquipmentRefusalCause { NotKnown, NotConnected, NotSupported, Busy, Parked, DisconnectedByUser }

/// <summary>A command the rig refused (nothing was sent to a device), as against one that ran and failed.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "A refusal is meaningless without its cause; the standard ctors would only produce cause-less instances.")]
public sealed class EquipmentRefusedException : SequenceEntityFailedException {
    public EquipmentRefusedException(EquipmentRefusalCause cause, string message) : base(message) {
        Cause = cause;
    }

    public EquipmentRefusalCause Cause { get; }
}