#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Services;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>
/// Serializes every send-side operation on one WebSocket behind a gate.
/// A WebSocket allows ONE in-flight send; before §27 the drain loop was the
/// only sender, but now the resume replay, the 30 s heartbeat ping, the §27
/// <c>connection.request</c> control frame (arriving from another request's
/// thread via <see cref="ClientSessionService"/>), and the close all share
/// the socket — so they all go through this wrapper.
/// </summary>
internal sealed class WsClientConnection : IWsClientConnection, IDisposable {

    // RFC 6455 §5.5: a close frame's payload is capped at 125 bytes — 2 for
    // the status code, leaving 123 for the UTF-8 reason.
    private const int MaxCloseReasonBytes = 123;

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public WsClientConnection(WebSocket socket) {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    }

    public async Task SendTextAsync(byte[] utf8Json, CancellationToken ct) {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_socket.State != WebSocketState.Open) {
                throw new WebSocketException(WebSocketError.InvalidState);
            }
            await _socket.SendAsync(utf8Json, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        } finally {
            _sendGate.Release();
        }
    }

    /// <summary>§27 takeover kick: sends the close frame (e.g. 4004) WITHOUT waiting
    /// for the peer's reply — the displaced client may be a zombie, and the socket's
    /// own receive loop finishes the handshake when/if the reply arrives.</summary>
    public async Task CloseAsync(int closeCode, string reason, CancellationToken ct) {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived) {
                await _socket.CloseOutputAsync((WebSocketCloseStatus)closeCode, Truncate(reason), ct)
                    .ConfigureAwait(false);
            }
        } finally {
            _sendGate.Release();
        }
    }

    /// <summary>Normal teardown: the full RFC 6455 close handshake, gated so it can't
    /// interleave with a heartbeat ping still in flight.</summary>
    public async Task CloseHandshakeAsync(WebSocketCloseStatus status, string reason, CancellationToken ct) {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived) {
                await _socket.CloseAsync(status, Truncate(reason), ct).ConfigureAwait(false);
            }
        } finally {
            _sendGate.Release();
        }
    }

    /// <summary>Caps the close reason at the RFC 6455 123-byte payload limit without
    /// splitting a multi-byte UTF-8 sequence (the hostname in the §27 takeover reason
    /// is client-supplied and may be non-ASCII).</summary>
    internal static string Truncate(string reason) {
        if (Encoding.UTF8.GetByteCount(reason) <= MaxCloseReasonBytes) {
            return reason;
        }
        var length = reason.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(reason.AsSpan(0, length)) > MaxCloseReasonBytes) {
            length--;
            // Never end on a high surrogate — that would re-encode as U+FFFD (3 bytes)
            // and could bounce back above the cap.
            if (length > 0 && char.IsHighSurrogate(reason[length - 1])) {
                length--;
            }
        }
        return reason[..length];
    }

    public void Dispose() => _sendGate.Dispose();
}
