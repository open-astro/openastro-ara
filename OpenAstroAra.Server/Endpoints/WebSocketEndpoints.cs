#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Server.Contracts.WsEvents;
using OpenAstroAra.Server.Services;
using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;

namespace OpenAstroAra.Server.Endpoints;

/// <summary>Catalog payload returned by GET /api/v1/ws/catalog.</summary>
public sealed record WsCatalogResponse(IReadOnlyList<string> Events);

/// <summary>
/// §60.9 WebSocket endpoint. <c>GET /api/v1/ws</c> accepts the protocol
/// upgrade and drains <see cref="IWsEventChannel"/> to the client as
/// JSON text frames using the §60.9.3 envelope shape.
///
/// First sub-PR (accept + drain): no version validation, no heartbeat,
/// no resume — those layer on in subsequent sub-PRs. The handler is
/// already wired to the §13.17 broadcaster, so every placeholder
/// service that calls <c>broadcaster.PublishAsync(...)</c> reaches the
/// connected clients today.
///
/// <c>/api/v1/ws/catalog</c> stays the read-only event-catalog endpoint
/// per §60.9.4 — independent of the upgrade path.
/// </summary>
public static partial class WebSocketEndpoints {

    /// <summary>
    /// Current §60.9 WS protocol version. Bumped only on breaking changes
    /// to the on-wire envelope shape or framing (not for additive event
    /// types — those are negotiated via /api/v1/ws/catalog).
    /// </summary>
    private const string ProtocolVersion = "1";

    public static IEndpointRouteBuilder MapWebSocketEndpoints(this IEndpointRouteBuilder app) {
        var ws = app.MapGroup("/api/v1/ws").WithTags("WebSocket");

        // §60.9.4 — exposes the catalog without requiring a WS upgrade.
        // Lets the WILMA client validate "the daemon advertises this event type"
        // before subscribing.
        ws.MapGet("/catalog", () => Results.Ok(new WsCatalogResponse(WsEventCatalog.All)))
          .Produces<WsCatalogResponse>(StatusCodes.Status200OK)
          .WithName("GetWebSocketCatalog");

        // §60.9 WebSocket upgrade.
        //   - WS request + X-Ara-WS-Version: 1 → accept and run the send/receive loop.
        //   - WS request + missing/wrong X-Ara-WS-Version → 426 (handshake rejection).
        //   - Plain HTTP GET → 426 with Upgrade/Connection headers per RFC 7231 §6.5.15.
        ws.MapGet("",
            async (HttpContext http, IWsEventChannel channel, IWsBroadcaster broadcaster, ILoggerFactory loggerFactory) => {
                if (http.WebSockets.IsWebSocketRequest) {
                    // §60.9 requires X-Ara-WS-Version: 1. Per openapi.yaml line 674,
                    // a mismatched/missing version is rejected pre-upgrade with 426
                    // — close-code 4003 only applies if version negotiation fails
                    // *after* a successful upgrade, which can't happen with a
                    // pre-handshake header check.
                    var versionHeader = http.Request.Headers["X-Ara-WS-Version"].ToString();
                    if (!string.Equals(versionHeader, ProtocolVersion, StringComparison.Ordinal)) {
                        http.Response.Headers.Upgrade = "websocket";
                        http.Response.Headers.Connection = "Upgrade";
                        return Results.Problem(
                            type: "https://openastro.net/errors/ws-version-mismatch",
                            title: "Unsupported WebSocket protocol version",
                            statusCode: StatusCodes.Status426UpgradeRequired,
                            detail: $"X-Ara-WS-Version header is required and must equal \"{ProtocolVersion}\". Got: \"{versionHeader}\".");
                    }
                    var logger = loggerFactory.CreateLogger("OpenAstroAra.Server.Endpoints.WebSocket");
                    await HandleWebSocketAsync(http, channel, broadcaster, logger);
                    return Results.Empty;
                }
                // Per RFC 7231 §6.5.15, 426 responses must include hop-by-hop Upgrade
                // + Connection headers so the client knows which protocol to switch to.
                http.Response.Headers.Upgrade = "websocket";
                http.Response.Headers.Connection = "Upgrade";
                return Results.Problem(
                    type: "https://openastro.net/errors/upgrade-required",
                    title: "WebSocket upgrade required",
                    statusCode: StatusCodes.Status426UpgradeRequired,
                    detail: "This endpoint serves WebSocket connections only. See §60.9 for the connection protocol (X-Ara-WS-Version: 1, 30s ping / 60s pong, resume protocol with last-seen seq).");
            })
            .ProducesProblem(StatusCodes.Status426UpgradeRequired)
            .WithName("UpgradeToWebSocket");

        return app;
    }

    /// <summary>
    /// Window the server waits for an optional resume request as the
    /// client's first frame. After this, the connection is treated as a
    /// fresh subscription (no replay).
    /// </summary>
    private static readonly TimeSpan ResumeWindow = TimeSpan.FromSeconds(5);

    private static async Task HandleWebSocketAsync(
            HttpContext http,
            IWsEventChannel channel,
            IWsBroadcaster broadcaster,
            ILogger logger) {
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        // Linked CTS lets the passive receive loop cancel the send loop
        // when the client sends a Close frame, while the server-side
        // RequestAborted token still drives shutdown.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        var ct = cts.Token;

        // Register the per-client subscription BEFORE the resume phase
        // runs. The race Sonnet caught on PR #174: any event published
        // between the replay snapshot and the iterator's first MoveNext
        // landed nowhere — too late for replay, too early for the
        // subscription. Registering up front means new publishes buffer in
        // the per-sub channel during the resume window + replay send; the
        // drain loop later dedups against the highWaterMark so events that
        // appear in both the snapshot and the channel are sent exactly
        // once.
        var liveStream = channel.ReadAllAsync(ct);

        // §60.9 resume protocol — give the client a short window to send a
        // JSON resume request as its first frame. Anything else (timeout,
        // non-JSON, missing resume_token) → treat as fresh subscription.
        var (highWaterMark, pendingReceive) = await HandleResumePhaseAsync(socket, channel, broadcaster, logger, ct);

        // Passive receive loop — after the resume window the receive side
        // just watches for Close frames. Heartbeat (pong handling) and
        // any further client-→ server messages layer on in future sub-PRs.
        var receiveTask = Task.Run(async () => {
            var buffer = new byte[1024];
            try {
                // If the resume phase timed out, it handed us its still-pending
                // first receive (it can't start a second concurrent ReceiveAsync).
                // Await that first; if it's the client's Close, skip the loop and
                // fall through to teardown.
                var firstWasClose = false;
                if (pendingReceive is not null) {
                    var first = await pendingReceive;
                    firstWasClose = first.MessageType == WebSocketMessageType.Close;
                }
                while (!firstWasClose && !ct.IsCancellationRequested && socket.State == WebSocketState.Open) {
                    var result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close) {
                        break;
                    }
                }
            } catch (OperationCanceledException) {
                // Expected on shutdown.
            } catch (WebSocketException) {
                // Expected on abrupt client disconnect.
            } catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException) {
                LogReceiveLoopClosed(logger, ex);
            }
            // Whatever caused the receive loop to exit (close frame, disconnect,
            // shutdown), tear down the send loop too.
            try { await cts.CancelAsync(); } catch (ObjectDisposedException) { /* CTS already disposed */ }
        }, ct);

        try {
            await foreach (var envelope in liveStream) {
                if (socket.State != WebSocketState.Open) break;
                // Dedup against the replay snapshot's high-water mark.
                // Envelopes with seq <= highWaterMark were either already
                // replayed (during the resume phase) or already seen by
                // the client (per their resume_token). Anything new (seq
                // > highWaterMark) goes through normally. Fresh-subscription
                // case has highWaterMark = 0, so this is a no-op there.
                if (envelope.Seq <= highWaterMark) continue;
                var json = JsonSerializer.SerializeToUtf8Bytes(
                    envelope, AraJsonSerializerContext.Default.WsEventEnvelopeDto);
                await socket.SendAsync(
                    json, WebSocketMessageType.Text, endOfMessage: true, ct);
            }
        } catch (OperationCanceledException) {
            // Expected on shutdown or client close.
        } catch (WebSocketException ex) {
            LogSendLoopSocketError(logger, ex);
        } catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException or JsonException) {
            LogSendLoopFailed(logger, ex);
        }

        // Best-effort close — if the socket is already gone the framework
        // throws, but there's nothing useful to do at that point.
        if (socket.State == WebSocketState.Open) {
            try {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure, "server closing", CancellationToken.None);
            } catch (Exception ex) when (ex is WebSocketException or InvalidOperationException or ObjectDisposedException) {
                LogCloseFailed(logger, ex);
            }
        }

        try { await cts.CancelAsync(); } catch (ObjectDisposedException) { /* CTS already disposed */ }
        try { await receiveTask; } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException) { /* swallowed; loop already logs */ }
    }

    /// <summary>
    /// Runs the §60.9 resume protocol if the client sends a JSON resume
    /// request as its first frame. Returns the high-water-mark seq for
    /// drain-loop dedup — anything with seq &lt;= this was either already
    /// replayed or already seen by the client. Returns 0 for fresh
    /// subscriptions (no replay, no dedup needed).
    /// </summary>
    private static async Task<(long HighWaterMark, Task<WebSocketReceiveResult>? PendingReceive)>
            HandleResumePhaseAsync(
            WebSocket socket,
            IWsEventChannel channel,
            IWsBroadcaster broadcaster,
            ILogger logger,
            CancellationToken ct) {
        var buffer = new byte[4096];
        var ms = new MemoryStream();
        WebSocketReceiveResult result;

        // Bound the wait for the client's first frame WITHOUT cancelling the
        // receive: a cancelled WebSocket.ReceiveAsync ABORTS the socket (close
        // 1006), which would drop every client that stays silent past the window
        // (e.g. a fresh subscriber). Race the receive against a delay instead; on
        // timeout, hand the still-pending receive back to the caller's receive loop
        // so the socket lives on as a fresh subscription.
        var firstReceive = socket.ReceiveAsync(buffer, ct);
        try {
            var winner = await Task.WhenAny(firstReceive, Task.Delay(ResumeWindow, ct));
            if (winner != firstReceive) {
                // Window elapsed, client silent → fresh subscription. Do NOT cancel
                // firstReceive (that aborts the socket); pass it to the receive loop.
                return (0, firstReceive);
            }
            result = await firstReceive;
            if (result.MessageType == WebSocketMessageType.Close) {
                return (0, null);
            }
            await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);
            // Drain continuation frames of this in-flight message — they arrive
            // promptly and ct only fires on teardown, so this can't abort spuriously.
            while (!result.EndOfMessage) {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) {
                    return (0, null);
                }
                await ms.WriteAsync(buffer.AsMemory(0, result.Count), ct);
                if (ms.Length > 16 * 1024) {
                    // Resume request is small; anything larger isn't one.
                    return (0, null);
                }
            }
        } catch (OperationCanceledException) {
            // Connection teardown during the resume read — nothing to resume.
            return (0, null);
        } catch (WebSocketException ex) {
            LogResumeReceiveFailed(logger, ex);
            return (0, null);
        }

        if (result.MessageType != WebSocketMessageType.Text) {
            // Binary first frame can't be a resume request — discard.
            return (0, null);
        }

        WsResumeRequestDto? request;
        try {
            request = JsonSerializer.Deserialize(
                ms.ToArray(), AraJsonSerializerContext.Default.WsResumeRequestDto);
        } catch (JsonException) {
            // Malformed JSON → fresh subscription.
            return (0, null);
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ResumeToken)) {
            // First frame wasn't a resume request → fresh subscription.
            return (0, null);
        }

        // v0.0.1: resume_token is the base-10 stringified last-seen sequence
        // number. Real opaque-token + 1-hour validity ties in with REST
        // /api/v1/server/state.ws_resume_token in a follow-up sub-PR.
        if (!long.TryParse(request.ResumeToken, out var lastSeenSeq) || lastSeenSeq < 0) {
            await SendResumeResponseAsync(socket, new WsResumeResponseDto(
                Resumed: false,
                MissedEvents: null,
                LastEventId: null,
                Code: "resume_token_invalid",
                Reason: "Token must be a non-negative base-10 integer."), ct);
            return (0, null);
        }

        var currentSeq = broadcaster.CurrentSequence;
        // If the client's last-seen seq is too far behind, the replay
        // buffer doesn't cover the gap → token expired.
        const int replayWindow = 1000;
        if (lastSeenSeq < currentSeq - replayWindow) {
            await SendResumeResponseAsync(socket, new WsResumeResponseDto(
                Resumed: false,
                MissedEvents: null,
                LastEventId: null,
                Code: "resume_token_expired",
                Reason: $"Last seen seq {lastSeenSeq} is beyond the {replayWindow}-event replay window (current seq: {currentSeq})."), ct);
            return (0, null);
        }

        var missed = await channel.ResumeFromAsync(lastSeenSeq, ct);
        // High-water mark for drain-loop dedup. Use Max(Seq) rather than
        // missed[^1].Seq — under multi-publisher concurrency, _replay.Enqueue
        // can complete out of seq order if one thread pauses between
        // Interlocked.Increment and Enqueue, so the queue's last element
        // isn't guaranteed to be the max. O(N) on N ≤ 1000 envelopes.
        // Empty snapshot → fall back to lastSeenSeq so dedup-skip is a no-op.
        var highWaterMark = missed.Count > 0 ? missed.Max(e => e.Seq) : lastSeenSeq;
        await SendResumeResponseAsync(socket, new WsResumeResponseDto(
            Resumed: true,
            MissedEvents: missed.Count,
            LastEventId: highWaterMark.ToString(),
            Code: null,
            Reason: null), ct);

        // Replay every missed envelope in order before the normal drain
        // takes over. Events published during this loop also land in the
        // already-registered subscription channel — the drain loop dedups
        // them via the highWaterMark filter.
        foreach (var envelope in missed) {
            if (socket.State != WebSocketState.Open) break;
            var json = JsonSerializer.SerializeToUtf8Bytes(
                envelope, AraJsonSerializerContext.Default.WsEventEnvelopeDto);
            await socket.SendAsync(
                json, WebSocketMessageType.Text, endOfMessage: true, ct);
        }

        return (highWaterMark, null);
    }

    private static async Task SendResumeResponseAsync(
            WebSocket socket, WsResumeResponseDto response, CancellationToken ct) {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            response, AraJsonSerializerContext.Default.WsResumeResponseDto);
        await socket.SendAsync(json, WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    #region LoggerMessage delegates (CA1848)

    [LoggerMessage(Level = LogLevel.Debug, Message = "WS receive loop closed unexpectedly")]
    private static partial void LogReceiveLoopClosed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WS send loop ended on socket error")]
    private static partial void LogSendLoopSocketError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "WS send loop failed")]
    private static partial void LogSendLoopFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WS close failed")]
    private static partial void LogCloseFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WS resume receive failed")]
    private static partial void LogResumeReceiveFailed(ILogger logger, Exception ex);

    #endregion
}