#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// ↑ara#1787 Session Live Stack control surface, implemented by <see cref="CameraService"/> and reached by the
/// endpoints through a cast of <see cref="ICameraService"/>. A colour stack of the sequencer's saved lights, off at
/// daemon start. Separate from <see cref="ILiveStackControl"/>: the two stacks share no state.
/// </summary>
public interface ISessionLiveStackControl {
    /// <summary>Switches the stack on. Idempotent; a stack that already holds lights keeps them.</summary>
    void StartSessionLiveStack();

    /// <summary>Switches off and empties the stack. Idempotent.</summary>
    void StopSessionLiveStack();

    /// <summary>Empties the stack and keeps it on. Idempotent.</summary>
    void ResetSessionLiveStack();

    SessionLiveStackStatusDto GetSessionLiveStackStatus();

    /// <summary>The stretched colour stack JPEG with its sequence and light count; null when nothing is stacked.</summary>
    (ReadOnlyMemory<byte> Jpeg, long Seq, int Lights)? GetSessionLiveStackFrame();
}
