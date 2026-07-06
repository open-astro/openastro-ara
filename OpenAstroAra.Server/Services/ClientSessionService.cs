#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAstroAra.Server.Contracts;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>Server→client half of a bound WebSocket connection, as the §27 session
/// service sees it: a way to deliver a control frame to the holder and to close the
/// holder's socket with an ARA close code. Implementations MUST serialize sends
/// internally (WebSocket allows one in-flight send) — the session service calls this
/// concurrently with the event drain loop.</summary>
public interface IWsClientConnection {
    Task SendTextAsync(byte[] utf8Json, CancellationToken ct);
    Task CloseAsync(int closeCode, string reason, CancellationToken ct);
}

/// <summary>How a §27.1 connect attempt resolved.</summary>
public enum ConnectOutcomeKind {
    /// <summary>Slot was free (or the holder was dead, or the holder allowed the
    /// takeover) — the caller now controls the daemon.</summary>
    Granted,

    /// <summary>The holder answered "reject" — the caller gets 409 "in use by X".</summary>
    Rejected,

    /// <summary>The holder could not be asked (no bound WS) or did not answer the
    /// takeover modal within the timeout — 409 "try again in 60 s" per §27.2.</summary>
    Unresponsive,

    /// <summary>Another connect attempt already has a takeover request in flight —
    /// one modal at a time on the holder's screen.</summary>
    Busy,
}

/// <summary>Result of <see cref="ClientSessionService.ConnectAsync"/>. <c>SessionId</c>
/// and <c>ConnectedAt</c> are meaningful only for <see cref="ConnectOutcomeKind.Granted"/>;
/// <c>CurrentHostname</c> only for Rejected/Unresponsive (the 409 detail).</summary>
public sealed record ConnectOutcome(
    ConnectOutcomeKind Kind,
    Guid SessionId,
    DateTimeOffset ConnectedAt,
    string? CurrentHostname);

/// <summary>
/// §27 single-client policy: ARA serves ONE controlling client at a time, and a new
/// client takes over via a hand-off mediated by the current holder (WS
/// <c>connection.request</c> → modal → <c>connection.response</c>). This service owns
/// the slot: the current session, its liveness (last-seen updated by WS frames from
/// the session-bound socket), and the single in-flight takeover request.
///
/// <para><b>Back-compat</b>: the policy engages only between clients that call
/// <c>POST /server/connect</c>. A pre-§27 WILMA that never connects/binds is invisible
/// to the slot — REST and the WS event stream keep working for it unchanged.</para>
///
/// <para><b>Liveness</b> (§27.2): a session is dead when the daemon has heard nothing
/// for <see cref="DeadAfter"/> (60 s) — no WS frame from its bound socket, or the
/// socket unbound that long ago and never re-bound (the §60.9 resume protocol makes
/// short WS drops survivable, so an unbind alone must not kill the session). A dead
/// holder's slot is taken over immediately, and its zombie socket (if any) is closed
/// with 4004.</para>
/// </summary>
public sealed partial class ClientSessionService {

    /// <summary>§60.9 close code: single-client policy, another WILMA took over.</summary>
    public const int TakeoverCloseCode = 4004;

    /// <summary>§27.2 — how long the holder gets to answer the takeover modal.
    /// Internal-settable so tests shrink it to milliseconds.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>§27.2 — silence threshold after which the holder is dead.</summary>
    internal TimeSpan DeadAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>§60.9 heartbeat cadence — the WS endpoint pings session-bound sockets
    /// this often; the client answers each ping with <c>{"type":"pong"}</c>.</summary>
    internal TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Injectable clock for the liveness tests.</summary>
    internal Func<DateTimeOffset> UtcNow { get; set; } = () => DateTimeOffset.UtcNow;

    private readonly object _lock = new();
    private readonly ILogger<ClientSessionService> _logger;
    private Session? _current;
    private PendingTakeover? _pending;

    public ClientSessionService(ILogger<ClientSessionService>? logger = null) {
        _logger = logger ?? NullLogger<ClientSessionService>.Instance;
    }

    private sealed class Session {
        public required Guid Id { get; init; }
        public required string Hostname { get; init; }
        public required DateTimeOffset ConnectedUtc { get; init; }
        public DateTimeOffset LastSeenUtc { get; set; }
        public IWsClientConnection? Socket { get; set; }
    }

    private sealed record PendingTakeover(string RequestId, TaskCompletionSource<string> Tcs);

    /// <summary>§27.1 connect flow. Free (or dead-holder) slot → Granted immediately.
    /// Live holder → sends <c>connection.request</c> over the holder's bound WS and
    /// awaits the holder's answer up to <see cref="RequestTimeout"/>. Cancellation of
    /// <paramref name="ct"/> (the new client gave up / request aborted) cleans up the
    /// pending request and rethrows.</summary>
    public async Task<ConnectOutcome> ConnectAsync(string hostname, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostname);
        var requestId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        IWsClientConnection? holderSocket;
        IWsClientConnection? zombieSocket = null;
        string holderHostname;

        lock (_lock) {
            var now = UtcNow();
            if (_pending is not null) {
                return new ConnectOutcome(ConnectOutcomeKind.Busy, Guid.Empty, now, _current?.Hostname);
            }
            if (_current is null || IsDeadLocked(now)) {
                var hadDeadHolder = _current is not null;
                zombieSocket = _current?.Socket;
                var granted = TakeSlotLocked(hostname, now);
                CloseTakenOverSocketInBackground(zombieSocket, hostname);
                LogSlotGranted(hostname, hadDeadHolder);
                return granted;
            }
            if (_current.Socket is null) {
                // Holder is offline (WS dropped) but within the DeadAfter grace — the
                // modal can't be delivered, so per §27.2 the answer is "try again".
                return new ConnectOutcome(ConnectOutcomeKind.Unresponsive, Guid.Empty, now, _current.Hostname);
            }
            holderSocket = _current.Socket;
            holderHostname = _current.Hostname;
            _pending = new PendingTakeover(requestId, tcs);
        }

        // Deliver + await OUTSIDE the lock: the send and the modal answer both take
        // arbitrarily long, and RecordActivity/TryCompleteTakeover must stay callable.
        try {
            var frame = JsonSerializer.SerializeToUtf8Bytes(
                new WsConnectionRequestDto("connection.request", hostname, requestId),
                AraJsonSerializerContext.Default.WsConnectionRequestDto);
            try {
                await holderSocket.SendTextAsync(frame, ct).ConfigureAwait(false);
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // The holder's socket died mid-send. Don't grant the slot here — the
                // 60 s liveness rule owns that call; the connector just retries.
                LogRequestDeliveryFailed(ex, holderHostname);
                return new ConnectOutcome(ConnectOutcomeKind.Unresponsive, Guid.Empty, UtcNow(), holderHostname);
            }

            string action;
            try {
                action = await tcs.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
            } catch (TimeoutException) {
                LogRequestTimedOut(holderHostname);
                return new ConnectOutcome(ConnectOutcomeKind.Unresponsive, Guid.Empty, UtcNow(), holderHostname);
            }

            if (!string.Equals(action, "allow", StringComparison.Ordinal)) {
                return new ConnectOutcome(ConnectOutcomeKind.Rejected, Guid.Empty, UtcNow(), holderHostname);
            }

            IWsClientConnection? oldSocket;
            ConnectOutcome granted;
            lock (_lock) {
                oldSocket = _current?.Socket;
                granted = TakeSlotLocked(hostname, UtcNow());
            }
            CloseTakenOverSocketInBackground(oldSocket, hostname);
            LogTakeoverAllowed(holderHostname, hostname);
            return granted;
        } finally {
            lock (_lock) {
                if (_pending is not null && string.Equals(_pending.RequestId, requestId, StringComparison.Ordinal)) {
                    _pending = null;
                }
            }
        }
    }

    /// <summary>Graceful release per §27.3. True when <paramref name="sessionId"/>
    /// owned the slot (now free). A takeover request pending against the released
    /// holder resolves as "allow" — the slot the connector asked for just opened up.</summary>
    public bool Disconnect(Guid sessionId) {
        PendingTakeover? pending;
        lock (_lock) {
            if (_current is null || _current.Id != sessionId) {
                return false;
            }
            _current = null;
            pending = _pending;
        }
        pending?.Tcs.TrySetResult("allow");
        LogSlotReleased(sessionId);
        return true;
    }

    /// <summary>§27.3 <c>GET /server/session</c> snapshot. A dead holder reads as
    /// not-connected (the next connect will sweep it).</summary>
    public ClientSessionInfoDto GetSession() {
        lock (_lock) {
            var now = UtcNow();
            if (_current is null || IsDeadLocked(now)) {
                return new ClientSessionInfoDto(false, null, null, null);
            }
            return new ClientSessionInfoDto(
                true,
                _current.Hostname,
                _current.ConnectedUtc,
                Math.Max(0, (now - _current.LastSeenUtc).TotalSeconds));
        }
    }

    /// <summary>Binds a WS connection (upgrade carried <c>X-Ara-Session</c>) to the
    /// session so frames from it refresh liveness and the takeover modal can reach it.
    /// False when the id doesn't match the current session — the caller proceeds as a
    /// plain unbound event-stream subscriber.</summary>
    public bool BindSocket(Guid sessionId, IWsClientConnection connection) {
        ArgumentNullException.ThrowIfNull(connection);
        lock (_lock) {
            if (_current is null || _current.Id != sessionId) {
                return false;
            }
            _current.Socket = connection;
            _current.LastSeenUtc = UtcNow();
            return true;
        }
    }

    /// <summary>Detaches a closing WS connection. Only the currently-bound connection
    /// unbinds — a stale socket's late teardown must not detach its replacement.</summary>
    public void UnbindSocket(Guid sessionId, IWsClientConnection connection) {
        lock (_lock) {
            if (_current is not null && _current.Id == sessionId
                    && ReferenceEquals(_current.Socket, connection)) {
                _current.Socket = null;
                _current.LastSeenUtc = UtcNow();
            }
        }
    }

    /// <summary>Any complete frame from the session-bound socket counts as liveness
    /// (the contract frame is <c>{"type":"pong"}</c> answering the 30 s pings).</summary>
    public void RecordActivity(Guid sessionId) {
        lock (_lock) {
            if (_current is not null && _current.Id == sessionId) {
                _current.LastSeenUtc = UtcNow();
            }
        }
    }

    /// <summary>Resolves the in-flight takeover with the holder's answer. False when
    /// no request with that id is pending (stale/duplicate response — ignored).</summary>
    public bool TryCompleteTakeover(string requestId, string action) {
        PendingTakeover? pending;
        lock (_lock) {
            if (_pending is null || !string.Equals(_pending.RequestId, requestId, StringComparison.Ordinal)) {
                return false;
            }
            pending = _pending;
        }
        return pending.Tcs.TrySetResult(action);
    }

    private ConnectOutcome TakeSlotLocked(string hostname, DateTimeOffset now) {
        var session = new Session {
            Id = Guid.NewGuid(),
            Hostname = hostname,
            ConnectedUtc = now,
            LastSeenUtc = now,
        };
        _current = session;
        return new ConnectOutcome(ConnectOutcomeKind.Granted, session.Id, session.ConnectedUtc, null);
    }

    private bool IsDeadLocked(DateTimeOffset now) =>
        _current is not null && now - _current.LastSeenUtc > DeadAfter;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Closing the displaced holder's socket is best-effort cleanup off the connect path: the socket may already be aborted/disposed by its own teardown, and no failure here may leak back to the new client's 200. Log-and-recover boundary.")]
    private void CloseTakenOverSocketInBackground(IWsClientConnection? socket, string newHostname) {
        if (socket is null) {
            return;
        }
        _ = Task.Run(async () => {
            try {
                await socket.CloseAsync(
                    TakeoverCloseCode,
                    $"Single-client policy: {newHostname} took over",
                    CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                LogTakeoverCloseFailed(ex);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "§27 slot granted to {Hostname} (dead holder swept: {WasDeadHolder})")]
    private partial void LogSlotGranted(string hostname, bool wasDeadHolder);

    [LoggerMessage(Level = LogLevel.Information, Message = "§27 takeover: {OldHostname} allowed {NewHostname} to take over")]
    private partial void LogTakeoverAllowed(string oldHostname, string newHostname);

    [LoggerMessage(Level = LogLevel.Information, Message = "§27 slot released by session {SessionId}")]
    private partial void LogSlotReleased(Guid sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "§27 connection.request could not be delivered to holder {Hostname}")]
    private partial void LogRequestDeliveryFailed(Exception ex, string hostname);

    [LoggerMessage(Level = LogLevel.Information, Message = "§27 takeover request to {Hostname} timed out")]
    private partial void LogRequestTimedOut(string hostname);

    [LoggerMessage(Level = LogLevel.Debug, Message = "§27 close of the displaced holder's socket failed (already gone?)")]
    private partial void LogTakeoverCloseFailed(Exception ex);
}
