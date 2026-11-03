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
/// ↑ara#1169 live-stack control surface, implemented by <see cref="CameraService"/> (new partial file) and reached
/// by the stack endpoints through a cast of <see cref="ICameraService"/>, so the shared service interface and its
/// placeholder stay untouched. Opt-in per Live View session: only <see cref="StartLiveStack"/> enables stacking.
/// </summary>
public interface ILiveStackControl {
    /// <summary>Enables stacking for the running Live View session. Throws <see cref="InvalidOperationException"/>
    /// (409) when Live View is not active, the session is OSC at 1x1, or its known frame size is over the cap.</summary>
    void StartLiveStack();

    /// <summary>Disables and empties the stack. Idempotent.</summary>
    void StopLiveStack();

    /// <summary>Empties the stack and keeps it enabled. Idempotent.</summary>
    void ResetLiveStack();

    LiveStackStatusDto GetLiveStackStatus();

    /// <summary>The stretched stack JPEG with its sequence, session and frame count; null when nothing is stacked.</summary>
    (ReadOnlyMemory<byte> Jpeg, long Seq, long SessionId, int Frames)? GetLiveStackFrame();
}
