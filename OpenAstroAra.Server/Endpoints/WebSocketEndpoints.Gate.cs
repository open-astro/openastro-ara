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

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// #1194 — cap on concurrently open WebSocket subscribers. The daemon serves one controlling
/// client plus a handful of spectators/reconnect overlaps; every socket costs a per-subscriber
/// event channel and a send loop on a Pi. Without a cap a looping reconnect (or anything hostile
/// on the LAN) could open sockets until the daemon starved. The gate is counted at the endpoint,
/// before the upgrade, so a refused client gets an ordinary 503 problem rather than a dropped
/// handshake; Kestrel's <c>MaxConcurrentUpgradedConnections</c> sits above it as the hard belt.
/// </summary>
public static class WsConnectionGate {

    /// <summary>Open sockets allowed at once. Generous for one rig: WILMA on two devices, a
    /// reconnect overlap each, and room for a diagnostics tail.</summary>
    public const int MaxConcurrentConnections = 16;

    private static int _open;

    /// <summary>Sockets currently counted as open (diagnostics / tests).</summary>
    public static int Open => Volatile.Read(ref _open);

    /// <summary>Claim a slot. False when the cap is reached (nothing was claimed).</summary>
    public static bool TryEnter() {
        while (true) {
            var current = Volatile.Read(ref _open);
            if (current >= MaxConcurrentConnections) {
                return false;
            }
            if (Interlocked.CompareExchange(ref _open, current + 1, current) == current) {
                return true;
            }
        }
    }

    /// <summary>Release a slot claimed by <see cref="TryEnter"/>. Never goes below zero.</summary>
    public static void Exit() {
        while (true) {
            var current = Volatile.Read(ref _open);
            if (current <= 0) {
                return;
            }
            if (Interlocked.CompareExchange(ref _open, current - 1, current) == current) {
                return;
            }
        }
    }

    /// <summary>Test seam: forget every claimed slot.</summary>
    internal static void ResetForTests() => Volatile.Write(ref _open, 0);
}
