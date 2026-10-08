#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Net;
using System.Net.Sockets;

namespace OpenAstroAra.TestHarness.Net;

/// <summary>
/// Binds an <see cref="HttpListener"/> to an ephemeral loopback port. Used by the
/// bench's loopback HTTP servers (the Alpaca fault proxy, test stubs).
/// </summary>
/// <remarks>
/// <see cref="HttpListener"/> cannot bind port 0, so the port must be chosen first
/// (probe-bind a <see cref="TcpListener"/> on :0, read the OS-assigned port, release
/// it). That leaves an inherent TOCTOU window — under parallel <c>dotnet test</c>
/// another worker can steal the freed port before <see cref="HttpListener.Start"/>
/// grabs it. <see cref="Bind"/> closes that gap with a bounded retry: on a bind
/// collision it picks a fresh port and tries again.
/// </remarks>
public static class LoopbackListener {
    private const int MaxAttempts = 8;

    /// <summary>
    /// Returns a started <see cref="HttpListener"/> bound to <c>http://127.0.0.1:{port}/</c>
    /// and the chosen port, retrying on a port-collision bind failure.
    /// </summary>
    public static (HttpListener Listener, int Port) Bind() {
        for (var attempt = 1; ; attempt++) {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try {
                listener.Start();
                return (listener, port);
            } catch (HttpListenerException) when (attempt < MaxAttempts) {
                // Port stolen between the probe release and Start() — try a fresh one.
                listener.Close();
            }
        }
    }

    /// <summary>
    /// Closes a listener at teardown. On macOS and Linux (the managed <see cref="HttpListener"/>)
    /// <see cref="HttpListener.Stop"/> already released the endpoint, and <see cref="HttpListener.Close"/>
    /// releases it again by looking it up, which re-binds the port: if a parallel fixture took the
    /// freed port in between, Close throws "Address already in use". The listener is finished either
    /// way, so that throw is not the test's failure.
    /// </summary>
    public static void Close(HttpListener listener) {
        ArgumentNullException.ThrowIfNull(listener);
        try {
            listener.Close();
        } catch (HttpListenerException) {
            // The port was reused by another fixture; nothing of ours is left bound.
        }
    }

    private static int FreePort() {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
