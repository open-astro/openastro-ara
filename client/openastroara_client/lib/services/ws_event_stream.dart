import 'dart:async';
import 'dart:convert';

import 'package:web_socket_channel/io.dart';

import '../models/server.dart';
import '../models/ws_event.dart';

/// Minimal socket seam the [WsEventStream] drives — just the incoming [stream],
/// a [send] for outgoing text, and [close]. Production wraps an
/// `IOWebSocketChannel`; tests supply a `StreamController`-backed fake without
/// implementing the full `WebSocketChannel` interface.
class WsSocket {
  final Stream<dynamic> stream;
  final void Function(String message) send;
  final Future<void> Function() close;

  WsSocket({required this.stream, required this.send, required this.close});
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
  );
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
  static const List<Duration> defaultBackoff = [
    Duration(seconds: 1),
    Duration(seconds: 2),
    Duration(seconds: 5),
    Duration(seconds: 10),
    Duration(seconds: 30),
  ];

  final Uri _url;
  final WsConnector _connect;
  final List<Duration> _backoff;
  final StreamController<WsEvent> _events = StreamController<WsEvent>.broadcast();

  WsSocket? _socket;
  StreamSubscription<dynamic>? _sub;
  Timer? _reconnectTimer;
  int _reconnectAttempt = 0;
  int? _lastSeq;
  bool _disposed = false;

  WsEventStream(
    AraServer server, {
    WsConnector? connect,
    List<Duration>? backoff,
  })  : assert(backoff == null || backoff.isNotEmpty,
            'backoff must be non-empty — _onClosed indexes into it on every reconnect'),
        // TODO(wss): plaintext ws:// is fine for the trusted-LAN default, but the
        // §67.4 / web-support follow-up must switch to wss:// for untrusted routes
        // (the resume handshake + payloads would otherwise be in the clear).
        _url = Uri.parse('ws://${server.hostname}:${server.port}/api/v1/ws'),
        _connect = connect ?? _defaultConnect,
        _backoff = backoff ?? defaultBackoff;

  /// Broadcast stream of parsed events. Late subscribers get only events from
  /// the point they subscribe.
  Stream<WsEvent> get events => _events.stream;

  /// The highest seq seen so far (null before the first event) — exposed for
  /// tests and diagnostics; the resume handshake uses it internally.
  int? get lastSeq => _lastSeq;

  /// Open the connection. Idempotent: a second call while already open — or
  /// while a reconnect is pending in the backoff window — is a no-op (so it
  /// can't race the reconnect timer into opening a second, leaked socket).
  void connect() {
    if (_disposed || _socket != null || _reconnectTimer != null) return;
    _open();
  }

  void _open() {
    if (_disposed) return;
    // Defensive: a direct caller shouldn't be able to double-open alongside a
    // pending reconnect (the connect() guard already blocks that, but keep the
    // single-socket invariant local to _open too).
    _reconnectTimer?.cancel();
    _reconnectTimer = null;
    final socket = _connect(_url, const {'X-Ara-WS-Version': wsVersion});
    _socket = socket;
    // On a reconnect (we have a last-seen seq) ask the server to replay what we
    // missed — it answers with a resume-response frame, then replays the gap.
    if (_lastSeq != null) {
      socket.send(jsonEncode({'resume_token': _lastSeq.toString()}));
    }
    // cancelOnError auto-cancels the subscription before onError runs, so the
    // sub.cancel() inside _onClosed is a harmless no-op on the error path (and
    // the real cancel on the onDone path); either way the link is finished.
    _sub = socket.stream.listen(
      _onFrame,
      onDone: _onClosed,
      onError: (Object _) => _onClosed(),
      cancelOnError: true,
    );
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
    // The resume-response control frame (`{resumed, ...}`) is not an event.
    if (decoded.containsKey('resumed') && !decoded.containsKey('type')) {
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
    final sub = _sub;
    _sub = null;
    if (sub != null) unawaited(sub.cancel());
    _socket = null;
    if (_disposed) return;
    final i = _reconnectAttempt < _backoff.length ? _reconnectAttempt : _backoff.length - 1;
    final delay = _backoff[i];
    if (_reconnectAttempt < _backoff.length) _reconnectAttempt++;
    _reconnectTimer = Timer(delay, _open);
  }

  /// Close the socket, stop reconnecting, and close the [events] stream.
  Future<void> dispose() async {
    _disposed = true;
    _reconnectTimer?.cancel();
    _reconnectTimer = null;
    _reconnectAttempt = 0; // harmless on this terminal op, but keeps state clean for any future reset()/reuse
    final sub = _sub;
    final socket = _socket;
    _sub = null; // drop references so the cancelled sub / closed socket can be GC'd promptly
    _socket = null;
    await sub?.cancel();
    await socket?.close();
    await _events.close();
  }
}
