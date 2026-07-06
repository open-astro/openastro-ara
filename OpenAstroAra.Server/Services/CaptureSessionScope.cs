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
using System.Threading;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §40/§50 — the ambient catalog session a capture should attach its frames to.
/// A sequence run enters its own session id around execution; every frame the
/// run's instructions capture (TakeExposure → mediator → CameraService →
/// RegisterFrame) reads it here and lands grouped per-run in the library and
/// stats, instead of in the shared "manual capture" bucket that REST snapshots
/// use.
///
/// <para>AsyncLocal on purpose: the value flows through the run worker's whole
/// awaited instruction chain (including Task.Run hops, which capture the
/// ExecutionContext) with zero interface changes to the NINA-inherited
/// mediator surfaces — and two CONCURRENT runs each see only their own id.
/// Code outside any run (the REST capture path) reads null and falls back to
/// the manual session.</para>
/// </summary>
public static class CaptureSessionScope {

    private static readonly AsyncLocal<Guid?> _current = new();

    /// <summary>The ambient run session, or null outside any run.</summary>
    public static Guid? Current => _current.Value;

    /// <summary>Bind <paramref name="sessionId"/> to the current async flow (a
    /// run worker calls this before executing the sequence tree).</summary>
    public static void Enter(Guid sessionId) => _current.Value = sessionId;

    /// <summary>Clear the ambient session (the run worker's teardown). Only
    /// affects the current async flow — a concurrent run's scope is untouched.</summary>
    public static void Exit() => _current.Value = null;
}
