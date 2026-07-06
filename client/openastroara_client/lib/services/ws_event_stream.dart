import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:web_socket_channel/io.dart';

import '../models/server.dart';
import '../models/ws_event.dart';

/// Minimal socket seam the [WsEventStream] drives — the incoming [stream],
/// a [send] for outgoing text, [close], and [closeCode] (readable once the
/// stream is done; null before then or when the transport doesn't know).
/// Production wraps an `IOWebSocketChannel`; tests supply a
/// `StreamController`-backed fake without implementing the full
/// `WebSocketChannel` interface.
class WsSocket {
  final Stream<dynamic> stream;
  final void Function(String message) send;
  final Future<void> Function() close;
  final int? Function() closeCode;

  WsSocket({
    required this.stream,
    required this.send,
    required this.close,
    int? Function()? closeCode,
  }) : closeCode = closeCode ?? (() => null);
}

/// Opens the socket for a URL + headers. Injectable for tests.
typedef WsConnector = WsSocket Function(Uri url, Map<String, String> headers);

WsSocket _defaultConnect(Uri url, Map<String, String> headers) {
  // IOWebSocketChannel (dart:io) so the X-Ara-WS-Version header can be set —
  // browser WebSocket can't set request headers, so web support is a follow-up
  // (the server would add a query-param version fallback).
  final channel = IOWebSocketChannel.connect(url, headers: headers);
  return WsSocket(
    stream: channel.stream,
    send: (m) => channel.sink.add(m),
    close: () => channel.sink.close(),
    // Populated by the channel once the close handshake finishes — read in
    // _onClosed, where the §27 takeover code (4004) must stop the reconnect.
    closeCode: () => channel.closeCode,
  );
}

/// §27 takeover-dance frame from the daemon: [from] wants to connect and the
/// current holder (this client) must allow or reject via
/// [WsEventStream.sendConnectionResponse] with the matching [requestId].
class WsConnectionRequest {
  final String from;
  final String requestId;
  const WsConnectionRequest({required this.from, required this.requestId});
}

/// Link state of a [WsEventStream], for consumers that need a connected /
/// disconnected indicator (a silent broadcast stream can't tell "no events" from
/// "link down"). `connecting` is the first attempt; `connected` is set once a
/// frame arrives or the connect-grace window elapses on a still-open socket;
/// `reconnecting` is a drop being retried with backoff; `disconnected` is the
/// pre-connect and post-dispose terminal state. `takenOver` is the §27 close
/// code 4004 — another WILMA took the control slot — and is terminal until the
/// user explicitly reconnects (auto-reconnecting would fight the new holder).
enum WsConnectionState {
  connecting,
  connected,
  reconnecting,
  disconnected,
  takenOver,
}

/// §60.9 WebSocket event-stream client. Connects to `ws://host:port/api/v1/ws`,
/// parses `{type, ts, seq, payload}` envelopes, and exposes them as a broadcast
/// [events] stream. On a dropped connection it reconnects with backoff and
/// resumes from the last-seen seq (sending `{"resume_token": "<seq>"}` as the
/// first frame), so a brief blip doesn't lose events.
///
/// Transport only — routing events by [WsEvent.type] to feature state (e.g.
/// diagnostics) is layered on top by consumers of [events].
class WsEventStream {
  static const String wsVersion = '1';
  // Capped at 10s (not the classic 30s): the dominant real-world outage is a
  // daemon restart or a Wi-Fi blip measured in seconds, and a 30s ceiling made
  // the comeback lag up to half a minute after the server was already back.
  // 10s keeps the idle-retry cost trivial (one dial per 10s against a dead
  // host) while roughly tripling reconnect snappiness after longer outages.
  static const List<Duration> defaultBackoff = [
    Duration(seconds: 1),
    Duration(seconds: 2),
    Duration(seconds: 5),
    Duration(seconds: 10),
  ];

  /// §60.9 close code for the §27 single-client takeover.
  static const int takenOverCloseCode = 4004;

  final Uri _url;
  final WsConnector _connect;
  final List<Duration> _backoff;
  final Future<String?> Function()? _claimSession;
  final StreamController<WsEvent> _events =
      StreamController<WsEvent>.broadcast();
  final StreamController<WsConnectionState> _connStates =
      StreamController<WsConnectionState>.broadcast();
  final StreamController<WsConnectionRequest> _connectionRequests =
      StreamController<WsConnectionRequest>.broadcast();
  WsConnectionState _connState = WsConnectionState.disconnected;

  /// Default for [_connectGrace]: how long an open-but-silent socket waits for its
  /// first frame before we treat it as connected anyway. Normally the server
  /// answers the resume frame at once (well under this), but an *idle* server with
  /// no live events — or an older daemon that doesn't reply to an empty resume
  /// token — would otherwise never deliver a frame, leaving the link wedged on
  /// `connecting` forever (which pins serverLinkUpProvider down so equipment reads
  /// "Server disconnected"). The socket being open is itself proof the link is up.
  static const Duration defaultConnectGrace = Duration(seconds: 3);

  final Duration _connectGrace;

  WsSocket? _socket;
  StreamSubscription<dynamic>? _sub;
  Timer? _reconnectTimer;
  Timer? _connectGraceTimer;
  int _reconnectAttempt = 0;
  int? _lastSeq;
  bool _disposed = false;
  bool _opening = false;

  WsEventStream(
    AraServer server, {
    WsConnector? connect,
    List<Duration>? backoff,
    Duration? connectGrace,
    // §27 — invoked before every dial (first connect AND each reconnect) to
    // claim/re-claim the control-session slot via POST /server/connect. Returns
    // the session id to bind this socket with (X-Ara-Session header), or null
    // to connect unbound (slot denied / older daemon / claim errored) — the
    // event stream itself works either way. Private named param: callers still
    // pass it as `claimSession:`.
    this._claimSession,
  }) : assert(
         backoff == null || backoff.isNotEmpty,
         'backoff must be non-empty — _onClosed indexes into it on every reconnect',
       ),
       _connectGrace = connectGrace ?? defaultConnectGrace,
       // TODO(wss): plaintext ws:// is fine for the trusted-LAN default, but the
       // §67.4 / web-support follow-up must switch to wss:// for untrusted routes
       // (the resume handshake + payloads would otherwise be in the clear).
       _url = Uri.parse('ws://${server.hostname}:${server.port}/api/v1/ws'),
       _connect = connect ?? _defaultConnect,
       _backoff = backoff ?? defaultBackoff;

  /// Broadcast stream of parsed events. Late subscribers get only events from
  /// the point they subscribe.
  Stream<WsEvent> get events => _events.stream;

  /// §27 — takeover requests from the daemon (this client is the current
  /// holder; another WILMA asked to connect). The consumer shows the modal and
  /// answers via [sendConnectionResponse].
  Stream<WsConnectionRequest> get connectionRequests =>
      _connectionRequests.stream;

  /// §27 — answer a [WsConnectionRequest]. No-op if the socket dropped since.
  void sendConnectionResponse(String requestId, {required bool allow}) {
    _socket?.send(
      jsonEncode({
        'type': 'connection.response',
        'request_id': requestId,
        'action': allow ? 'allow' : 'reject',
      }),
    );
  }

  /// The highest seq seen so far (null before the first event) — exposed for
  /// tests and diagnostics; the resume handshake uses it internally.
  int? get lastSeq => _lastSeq;

  /// Broadcast stream of link-state transitions (deduplicated). The current
  /// value is [connectionState].
  Stream<WsConnectionState> get connectionStates => _connStates.stream;

  /// Current link state (see [WsConnectionState]). `disconnected` until
  /// [connect] is called and `disconnected` again after [dispose].
  WsConnectionState get connectionState => _connState;

  void _setConnState(WsConnectionState s) {
    if (s == _connState) return;
    _connState = s;
    if (!_connStates.isClosed) _connStates.add(s);
  }

  /// Open the connection. Idempotent: a second call while already open — or
  /// while a reconnect is pending in the backoff window — is a no-op (so it
  /// can't race the reconnect timer into opening a second, leaked socket).
  void connect() {
    if (_disposed || _socket != null || _reconnectTimer != null || _opening) {
      return;
    }
    _setConnState(WsConnectionState.connecting);
    _open();
  }

  void _open() {
    // Single-socket invariant, local to _open: never open over a live socket
    // (would leak it), and never start a second dial while the §27 claim of a
    // first one is still awaiting. Unreachable in normal flow — _onClosed
    // clears _socket before scheduling the reconnect timer, and cancelOnError
    // stops a second _onClosed — but cheap insurance against any future
    // double-teardown path.
    if (_disposed || _socket != null || _opening) return;
    _reconnectTimer?.cancel();
    _reconnectTimer = null;
    _opening = true;
    unawaited(_openAsync().whenComplete(() => _opening = false));
  }

  Future<void> _openAsync() async {
    // §27 — claim (or re-claim) the control session BEFORE dialing, so the
    // upgrade can carry the session id and this socket's frames count as holder
    // liveness. A failed/denied claim degrades to an unbound socket: the event
    // stream is read-only state, not control, so it must keep working even when
    // another WILMA holds the slot (or the daemon predates §27).
    String? sessionId;
    final claim = _claimSession;
    if (claim != null) {
      try {
        sessionId = await claim();
      } catch (e) {
        debugPrint('[ws] §27 session claim failed (connecting unbound): $e');
      }
      // The await above is a suspension point: dispose() or a concurrent path
      // may have run. Re-check before touching the socket slot.
      if (_disposed || _socket != null) return;
    }
    final socket = _connect(_url, {
      'X-Ara-WS-Version': wsVersion,
      'X-Ara-Session': ?sessionId,
    });
    _socket = socket;
    // ALWAYS send a resume frame as the very first message so the server answers
    // with a resume-response control frame, which flips the link to `connected`
    // without waiting for the first live event on an idle server.
    //   - Fresh connect (no _lastSeq): send an EMPTY token. The server reads that
    //     as "fresh subscription" and replies Resumed:false WITHOUT replaying its
    //     historical event backlog — a brand-new client should start live, not get
    //     1..N queued events dumped on it. (Sending "0" would mean "I've seen up to
    //     seq 0 → replay everything after", i.e. the whole backlog.)
    //   - Reconnect (_lastSeq set): send the real last-seen seq so the server
    //     replays only the gap.
    // The send buffers in the sink until the HTTP upgrade completes; if the TCP
    // connect fails first the buffered token is dropped (cold connect) but _lastSeq
    // is retained for the next successful reconnect.
    socket.send(jsonEncode({'resume_token': _lastSeq?.toString() ?? ''}));
    // cancelOnError auto-cancels the subscription before onError runs, so the
    // sub.cancel() inside _onClosed is a harmless no-op on the error path (and
    // the real cancel on the onDone path); either way the link is finished.
    _sub = socket.stream.listen(
      _onFrame,
      onDone: _onClosed,
      onError: (Object _) => _onClosed(),
      cancelOnError: true,
    );
    // Fallback: if no frame arrives within the grace window but the socket is
    // still open (no onDone/onError fired → _socket still set), consider the link
    // connected. Covers an idle server and an old daemon that ignores an empty
    // resume token. Cancelled the moment a frame arrives (_onFrame) or the socket
    // tears down (_onClosed / dispose).
    _connectGraceTimer?.cancel();
    _connectGraceTimer = Timer(_connectGrace, () {
      if (!_disposed && _socket != null) {
        // A stable open socket counts as a successful connect → reset backoff so
        // the next drop retries from the first slot (matters against an old/idle
        // server that never sends the resume-response that would otherwise reset it).
        _reconnectAttempt = 0;
        _setConnState(WsConnectionState.connected);
      }
    });
  }

  void _onFrame(dynamic frame) {
    if (frame is! String) return;
    final Object? decoded;
    try {
      decoded = jsonDecode(frame);
    } on FormatException {
      return; // ignore non-JSON frames (don't reset backoff on garbage)
    }
    if (decoded is! Map<String, dynamic>) return;
    // Any decoded frame (event OR the resume-response control frame) proves the
    // link is live → connected; the grace fallback is no longer needed.
    _connectGraceTimer?.cancel();
    _connectGraceTimer = null;
    _setConnState(WsConnectionState.connected);
    // §27/§60.9 control frames (they carry `type` but no `seq`, so they must be
    // routed before the envelope parse). Heartbeat: the daemon pings session-
    // bound sockets every 30s and marks the holder dead after 60s of silence —
    // answering here, at the transport layer, keeps liveness meaningful ("the
    // app's event loop is alive"), which is exactly what the daemon uses it for.
    if (decoded['type'] == 'ping') {
      _socket?.send(jsonEncode({'type': 'pong'}));
      return;
    }
    // Takeover dance: surface to the modal layer; the answer comes back through
    // sendConnectionResponse.
    if (decoded['type'] == 'connection.request') {
      final requestId = decoded['request_id'];
      if (requestId is String &&
          requestId.isNotEmpty &&
          !_connectionRequests.isClosed) {
        _connectionRequests.add(
          WsConnectionRequest(
            from: decoded['from'] is String
                ? decoded['from'] as String
                : 'another client',
            requestId: requestId,
          ),
        );
      }
      return;
    }
    // The resume-response control frame (`{resumed, ...}`) is not an event.
    if (decoded.containsKey('resumed') && !decoded.containsKey('type')) {
      // A resume-response is a trusted server control frame (not arbitrary
      // garbage), so resetting backoff on it is safe — and necessary: against an
      // idle server that emits no events, this is the only signal that a reconnect
      // succeeded, so without it the backoff counter would ratchet to its 10s
      // ceiling across drops even though the server is healthy.
      _reconnectAttempt = 0;
      // The server rejected our resume token (expired / invalid — `resumed:false`
      // with a code). Drop _lastSeq so the next reconnect sends an empty token and
      // falls back to a fresh subscription; otherwise we'd replay the same rejected
      // token forever and the server would keep rejecting it (infinite loop, no events).
      if (decoded['resumed'] == false && decoded['code'] != null) {
        _lastSeq = null;
      }
      return;
    }
    final WsEvent event;
    try {
      event = WsEvent.fromJson(decoded);
    } on FormatException {
      return; // malformed envelope — skip it rather than kill the stream
    }
    // Reset backoff only on a genuinely-delivered event (not on malformed or
    // control frames), so a server that spews garbage right before dropping
    // can't keep us pinned to the first backoff slot.
    _reconnectAttempt = 0;
    // seq is per-connection monotonic and gap events replay in ascending order,
    // so only ever advance _lastSeq — never let an out-of-order or
    // counter-reset frame walk the resume token backwards.
    if (_lastSeq == null || event.seq > _lastSeq!) {
      _lastSeq = event.seq;
    }
    if (!_events.isClosed) _events.add(event);
  }

  void _onClosed() {
    // Fire-and-forget cancel: this is a sync onDone/onError callback (can't
    // await), and the stream that triggered it has already closed — so there's
    // nothing left to deliver. dispose() does NOT await this one (it short-
    // circuits on the null _sub below); it only awaits a cancel of a live sub.
    _connectGraceTimer?.cancel();
    _connectGraceTimer = null;
    final sub = _sub;
    final socket = _socket;
    _sub = null;
    _socket = null;
    // Surface (don't silently drop) a teardown failure: a faulted drain can make
    // cancel()/close() reject, and a bare unawaited would swallow it.
    if (sub != null) {
      unawaited(
        sub.cancel().catchError(
          (Object e) =>
              debugPrint('[ws] subscription cancel during teardown failed: $e'),
        ),
      );
    }
    // Finalize the old socket too — on the onError path (protocol/TLS fault, a
    // frame that kills the channel before a clean close) the sink is otherwise
    // never closed, leaking a half-open TCP connection across every reconnect.
    // On the onDone path close() is a harmless no-op on the already-closed sink.
    if (socket != null) {
      unawaited(
        socket.close().catchError(
          (Object e) =>
              debugPrint('[ws] socket close during teardown failed: $e'),
        ),
      );
    }
    if (_disposed) return;
    // §27 — close code 4004 means another WILMA took the control slot. This is
    // deliberate and terminal: auto-reconnecting would re-claim against the new
    // holder (popping takeover modals at them in a loop). The user re-enters via
    // an explicit action (connect() works again from this state).
    if (socket?.closeCode() == takenOverCloseCode) {
      _setConnState(WsConnectionState.takenOver);
      return;
    }
    _setConnState(WsConnectionState.reconnecting);
    final i = _reconnectAttempt < _backoff.length
        ? _reconnectAttempt
        : _backoff.length - 1;
    final delay = _backoff[i];
    if (_reconnectAttempt < _backoff.length) _reconnectAttempt++;
    _reconnectTimer = Timer(delay, _open);
  }

  /// Close the socket, stop reconnecting, and close the [events] stream.
  Future<void> dispose() async {
    if (_disposed) {
      return; // idempotent — a second dispose() must not re-close the controller
    }
    _disposed = true;
    _reconnectTimer?.cancel();
    _reconnectTimer = null;
    _connectGraceTimer?.cancel();
    _connectGraceTimer = null;
    final sub = _sub;
    final socket = _socket;
    _sub =
        null; // drop references so the cancelled sub / closed socket can be GC'd promptly
    _socket = null;
    await sub?.cancel();
    await socket?.close();
    _setConnState(WsConnectionState.disconnected);
    if (!_events.isClosed) await _events.close();
    if (!_connStates.isClosed) await _connStates.close();
    if (!_connectionRequests.isClosed) await _connectionRequests.close();
  }
}
