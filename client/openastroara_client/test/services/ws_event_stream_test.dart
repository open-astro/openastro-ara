import 'dart:async';
import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/ws_event_stream.dart';

/// One fake socket leg: the test pushes incoming frames via [incoming] and
/// inspects outgoing frames in [sent].
class _FakeSocket {
  final StreamController<dynamic> incoming = StreamController<dynamic>();
  final List<String> sent = <String>[];
  bool closed = false;

  late final WsSocket socket = WsSocket(
    stream: incoming.stream,
    send: sent.add,
    close: () async {
      closed = true;
      if (!incoming.isClosed) await incoming.close();
    },
  );

  /// Simulate the server closing the connection.
  Future<void> drop() => incoming.close();
}

/// Hands out a fresh fake socket per connect() call (so a reconnect gets its
/// own leg the test can drive independently).
class _FakeConnector {
  final List<_FakeSocket> legs = <_FakeSocket>[];
  Uri? lastUrl;
  Map<String, String>? lastHeaders;

  WsSocket connect(Uri url, Map<String, String> headers) {
    lastUrl = url;
    lastHeaders = headers;
    final leg = _FakeSocket();
    legs.add(leg);
    return leg.socket;
  }
}

String _envelope(String type, int seq, [Map<String, dynamic> payload = const {}]) =>
    jsonEncode({'type': type, 'ts': '2026-06-12T12:00:00.000Z', 'seq': seq, 'payload': payload});

void main() {
  const server = AraServer(hostname: 'host-a', port: 5555);

  group('WsEventStream transport', () {
    test('connects to the ws URL with the version header', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      ws.connect();
      expect(conn.lastUrl, Uri.parse('ws://host-a:5555/api/v1/ws'));
      expect(conn.lastHeaders, {'X-Ara-WS-Version': '1'});
      await ws.dispose();
    });

    test('parses envelope frames, forwards events, and tracks lastSeq', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      final received = <String>[];
      ws.events.listen((e) => received.add('${e.type}#${e.seq}'));
      ws.connect();

      conn.legs.first.incoming.add(_envelope('guider.dark_library.complete', 7, {'frame_count': 5}));
      conn.legs.first.incoming.add(_envelope('diagnostics.health_changed', 8));
      await pumpEventQueue();

      expect(received, ['guider.dark_library.complete#7', 'diagnostics.health_changed#8']);
      expect(ws.lastSeq, 8);
      await ws.dispose();
    });

    test('skips the resume-response control frame and malformed frames', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      final received = <int>[];
      ws.events.listen((e) => received.add(e.seq));
      ws.connect();

      conn.legs.first.incoming
        ..add(jsonEncode({'resumed': true, 'missed_events': 0, 'last_event_id': '0'}))
        ..add('not json at all')
        ..add(jsonEncode({'type': 'x'})) // missing seq → FormatException, skipped
        ..add(_envelope('real.event', 3));
      await pumpEventQueue();

      expect(received, [3], reason: 'only the well-formed event envelope is forwarded');
      await ws.dispose();
    });

    test('reconnects on drop and resumes from the last-seen seq', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect, backoff: const [Duration.zero]);
      ws.connect();

      conn.legs.first.incoming.add(_envelope('e', 42));
      await pumpEventQueue();
      expect(ws.lastSeq, 42);

      await conn.legs.first.drop(); // server dropped the link
      await pumpEventQueue(); // let the zero-delay reconnect timer fire

      expect(conn.legs, hasLength(2), reason: 'reconnected with a fresh leg');
      expect(conn.legs[1].sent, [jsonEncode({'resume_token': '42'})],
          reason: 'first frame on reconnect resumes from the last-seen seq');
      expect(conn.legs.first.closed, isTrue,
          reason: 'the dropped socket is finalized on reconnect (no half-open leak)');
      await ws.dispose();
    });

    test('connect() during the backoff window does not leak a second socket', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect, backoff: const [Duration(seconds: 30)]);
      ws.connect();
      await conn.legs.first.drop(); // drop → a reconnect is now pending (30s)
      await pumpEventQueue();

      ws.connect(); // a stray connect during backoff must be a no-op...
      ws.connect();
      expect(conn.legs, hasLength(1), reason: 'no extra socket opened while a reconnect is pending');
      await ws.dispose();
    });

    test('a malformed timestamp still delivers the event (epoch fallback)', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      final received = <({int seq, DateTime ts})>[];
      ws.events.listen((e) => received.add((seq: e.seq, ts: e.ts)));
      ws.connect();
      conn.legs.first.incoming
          .add(jsonEncode({'type': 'e', 'ts': 'not-a-date', 'seq': 9, 'payload': {}}));
      await pumpEventQueue();
      expect(received, hasLength(1), reason: 'a bad ts does not drop an otherwise-valid event');
      expect(received.first.seq, 9);
      expect(received.first.ts, DateTime.fromMillisecondsSinceEpoch(0, isUtc: true));
      await ws.dispose();
    });

    test('lastSeq only advances — an out-of-order frame does not walk it back', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      ws.events.listen((_) {});
      ws.connect();
      conn.legs.first.incoming
        ..add(_envelope('e', 10))
        ..add(_envelope('e', 5)); // out of order / counter reset
      await pumpEventQueue();
      expect(ws.lastSeq, 10, reason: 'resume token must not regress on a lower seq');
      await ws.dispose();
    });

    test('rejects an empty backoff list (would RangeError on reconnect)', () {
      final conn = _FakeConnector();
      expect(() => WsEventStream(server, connect: conn.connect, backoff: const []),
          throwsA(isA<AssertionError>()));
    });

    test('first connect sends no resume token', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      ws.connect();
      await pumpEventQueue();
      expect(conn.legs.first.sent, isEmpty);
      await ws.dispose();
    });

    test('dispose stops reconnecting and closes the events stream', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect, backoff: const [Duration.zero]);
      var done = false;
      ws.events.listen((_) {}, onDone: () => done = true);
      ws.connect();
      await ws.dispose();

      expect(conn.legs.first.closed, isTrue);
      expect(done, isTrue);

      await conn.legs.first.drop();
      await pumpEventQueue();
      expect(conn.legs, hasLength(1), reason: 'no reconnect after dispose');
    });
  });
}
