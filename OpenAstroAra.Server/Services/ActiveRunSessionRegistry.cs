#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.5 — which §40 catalog run sessions are currently executing, readable from ANY
/// async context. <see cref="CaptureSessionScope"/> deliberately rides AsyncLocal so
/// concurrent runs each see their own session — but a fault detected on a watch/refresh
/// timer flows on its own context where that ambient value is null. The sequencer
/// enters/exits here alongside the scope, and the fault log stamps
/// <see cref="Current"/> (the most recently started still-active run) at insert time.
/// </summary>
public sealed class ActiveRunSessionRegistry {
    private readonly object _gate = new();
    private readonly List<Guid> _active = [];

    public void Enter(Guid sessionId) {
        lock (_gate) {
            _active.Remove(sessionId);
            _active.Add(sessionId);
        }
    }

    public void Exit(Guid sessionId) {
        lock (_gate) {
            _active.Remove(sessionId);
        }
    }

    /// <summary>The most recently entered still-active run session, or null when no
    /// sequence run is in flight.</summary>
    public Guid? Current {
        get {
            lock (_gate) {
                return _active.Count > 0 ? _active[^1] : null;
            }
        }
    }
}
