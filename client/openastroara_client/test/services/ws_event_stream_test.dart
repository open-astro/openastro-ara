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

  /// Close code the transport reports after the stream ends (see
  /// [WsSocket.closeCode]); null mimics an abrupt drop with no close frame.
  int? closeCode;

  late final WsSocket socket = WsSocket(
    stream: incoming.stream,
    send: sent.add,
    close: () async {
      closed = true;
      if (!incoming.isClosed) await incoming.close();
    },
    closeCode: () => closeCode,
  );

  /// Simulate the server closing the connection.
  Future<void> drop() => incoming.close();

  /// Simulate the server closing with an explicit close code (e.g. the §27
  /// takeover code 4004).
  Future<void> dropWithCode(int code) {
    closeCode = code;
    return incoming.close();
  }
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

String _envelope(
  String type,
  int seq, [
  Map<String, dynamic> payload = const {},
]) => jsonEncode({
  'type': type,
  'ts': '2026-06-12T12:00:00.000Z',
  'seq': seq,
  'payload': payload,
});

void main() {
  const server = AraServer(hostname: 'host-a', port: 5555);

  group('WsEventStream transport', () {
    test('connects to the ws URL with the version header AND query fallback', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      ws.connect();
      // ws_version rides in the query string too: browser WebSockets can't set
      // request headers, and the server accepts either (header wins).
      expect(conn.lastUrl, Uri.parse('ws://host-a:5555/api/v1/ws?ws_version=1'));
      expect(conn.lastHeaders, {'X-Ara-WS-Version': '1'});
      await ws.dispose();
    });

    test(
      'parses envelope frames, forwards events, and tracks lastSeq',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(server, connect: conn.connect);
        final received = <String>[];
        ws.events.listen((e) => received.add('${e.type}#${e.seq}'));
        ws.connect();

        conn.legs.first.incoming.add(
          _envelope('guider.dark_library.complete', 7, {'frame_count': 5}),
        );
        conn.legs.first.incoming.add(
          _envelope('diagnostics.health_changed', 8),
        );
        await pumpEventQueue();

        expect(received, [
          'guider.dark_library.complete#7',
          'diagnostics.health_changed#8',
        ]);
        expect(ws.lastSeq, 8);
        await ws.dispose();
      },
    );

    test(
      'skips the resume-response control frame and malformed frames',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(server, connect: conn.connect);
        final received = <int>[];
        ws.events.listen((e) => received.add(e.seq));
        ws.connect();

        conn.legs.first.incoming
          ..add(
            jsonEncode({
              'resumed': true,
              'missed_events': 0,
              'last_event_id': '0',
            }),
          )
          ..add('not json at all')
          ..add(
            jsonEncode({'type': 'x'}),
          ) // missing seq → FormatException, skipped
          ..add(_envelope('real.event', 3));
        await pumpEventQueue();

        expect(received, [
          3,
        ], reason: 'only the well-formed event envelope is forwarded');
        await ws.dispose();
      },
    );

    test(
      'an expired resume token clears _lastSeq so the next reconnect is fresh',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(
          server,
          connect: conn.connect,
          backoff: const [Duration.zero],
        );
        ws.connect();

        // See an event (so _lastSeq is set), then drop + reconnect resuming from it.
        conn.legs.first.incoming.add(_envelope('e', 99));
        await pumpEventQueue();
        expect(ws.lastSeq, 99);
        await conn.legs.first.drop();
        await pumpEventQueue();
        expect(conn.legs[1].sent, [
          jsonEncode({'resume_token': '99'}),
        ]);

        // Server rejects the token as expired; the client must drop _lastSeq.
        conn.legs[1].incoming.add(
          jsonEncode({
            'resumed': false,
            'code': 'resume_token_expired',
            'reason': 'too old',
          }),
        );
        await pumpEventQueue();
        expect(ws.lastSeq, isNull, reason: 'expired token cleared');

        // Next drop → reconnect must send an EMPTY token (fresh subscription), not '99'.
        await conn.legs[1].drop();
        await pumpEventQueue();
        expect(conn.legs[2].sent, [
          jsonEncode({'resume_token': ''}),
        ], reason: 'no longer replays the rejected token');
        await ws.dispose();
      },
    );

    test('reconnects on drop and resumes from the last-seen seq', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(
        server,
        connect: conn.connect,
        backoff: const [Duration.zero],
      );
      ws.connect();

      conn.legs.first.incoming.add(_envelope('e', 42));
      await pumpEventQueue();
      expect(ws.lastSeq, 42);

      await conn.legs.first.drop(); // server dropped the link
      await pumpEventQueue(); // let the zero-delay reconnect timer fire

      expect(conn.legs, hasLength(2), reason: 'reconnected with a fresh leg');
      expect(
        conn.legs[1].sent,
        [
          jsonEncode({'resume_token': '42'}),
        ],
        reason: 'first frame on reconnect resumes from the last-seen seq',
      );
      expect(
        conn.legs.first.closed,
        isTrue,
        reason:
            'the dropped socket is finalized on reconnect (no half-open leak)',
      );
      await ws.dispose();
    });

    test(
      'connect() during the backoff window does not leak a second socket',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(
          server,
          connect: conn.connect,
          backoff: const [Duration(seconds: 30)],
        );
        ws.connect();
        await conn.legs.first.drop(); // drop → a reconnect is now pending (30s)
        await pumpEventQueue();

        ws.connect(); // a stray connect during backoff must be a no-op...
        ws.connect();
        expect(
          conn.legs,
          hasLength(1),
          reason: 'no extra socket opened while a reconnect is pending',
        );
        await ws.dispose();
      },
    );

    test(
      'a malformed timestamp still delivers the event (epoch fallback)',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(server, connect: conn.connect);
        final received = <({int seq, DateTime ts})>[];
        ws.events.listen((e) => received.add((seq: e.seq, ts: e.ts)));
        ws.connect();
        conn.legs.first.incoming.add(
          jsonEncode({
            'type': 'e',
            'ts': 'not-a-date',
            'seq': 9,
            'payload': {},
          }),
        );
        await pumpEventQueue();
        expect(
          received,
          hasLength(1),
          reason: 'a bad ts does not drop an otherwise-valid event',
        );
        expect(received.first.seq, 9);
        expect(
          received.first.ts,
          DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
        );
        await ws.dispose();
      },
    );

    test(
      'lastSeq only advances — an out-of-order frame does not walk it back',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(server, connect: conn.connect);
        ws.events.listen((_) {});
        ws.connect();
        conn.legs.first.incoming
          ..add(_envelope('e', 10))
          ..add(_envelope('e', 5)); // out of order / counter reset
        await pumpEventQueue();
        expect(
          ws.lastSeq,
          10,
          reason: 'resume token must not regress on a lower seq',
        );
        await ws.dispose();
      },
    );

    test('rejects an empty backoff list (would RangeError on reconnect)', () {
      final conn = _FakeConnector();
      expect(
        () => WsEventStream(server, connect: conn.connect, backoff: const []),
        throwsA(isA<AssertionError>()),
      );
    });

    test(
      'reconnects on a stream error (onError path), not just a clean close',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(
          server,
          connect: conn.connect,
          backoff: const [Duration.zero],
        );
        ws.events.listen((_) {});
        ws.connect();
        conn.legs.first.incoming.add(_envelope('e', 11));
        await pumpEventQueue();
        conn.legs.first.incoming.addError(Exception('half-open channel error'));
        await pumpEventQueue();
        expect(
          conn.legs,
          hasLength(2),
          reason: 'an onError teardown also triggers reconnect',
        );
        expect(conn.legs[1].sent, [
          jsonEncode({'resume_token': '11'}),
        ]);
        await ws.dispose();
      },
    );

    test(
      'backoff caps at the last slot — repeated drops never index out of bounds',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(
          server,
          connect: conn.connect,
          backoff: const [Duration.zero, Duration.zero],
        );
        ws.events.listen((_) {});
        ws.connect();
        // Drop more times than the backoff list length; the saturated counter must
        // reuse the last slot, not walk past the end.
        for (var i = 0; i < 4; i++) {
          await conn.legs.last.drop();
          await pumpEventQueue();
        }
        expect(
          conn.legs.length,
          greaterThanOrEqualTo(5),
          reason: 'kept reconnecting through the cap without error',
        );
        await ws.dispose();
      },
    );

    test('dispose is idempotent — a second call does not throw', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      ws.connect();
      await ws.dispose();
      await ws
          .dispose(); // must be a no-op, not StateError on the closed controller
    });

    test(
      'resume token survives a failed reconnect (no frame before the next drop)',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(
          server,
          connect: conn.connect,
          backoff: const [Duration.zero],
        );
        ws.events.listen((_) {});
        ws.connect();
        conn.legs.first.incoming.add(_envelope('e', 42));
        await pumpEventQueue();

        await conn.legs[0]
            .drop(); // leg 0 → reconnect (leg 1 carries the token)
        await pumpEventQueue();
        await conn.legs[1]
            .drop(); // leg 1 drops before delivering any frame → reconnect again
        await pumpEventQueue();

        expect(conn.legs, hasLength(3));
        expect(
          conn.legs[2].sent,
          [
            jsonEncode({'resume_token': '42'}),
          ],
          reason: '_lastSeq is retained across a failed-handshake reconnect',
        );
        await ws.dispose();
      },
    );

    // ── connection-state signal (slice 2a) ──

    test(
      'starts disconnected and goes connecting → connected on the first frame',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(server, connect: conn.connect);
        expect(ws.connectionState, WsConnectionState.disconnected);
        final states = <WsConnectionState>[];
        ws.connectionStates.listen(states.add);

        ws.connect();
        expect(ws.connectionState, WsConnectionState.connecting);
        conn.legs.first.incoming.add(_envelope('e', 1));
        await pumpEventQueue();

        expect(ws.connectionState, WsConnectionState.connected);
        expect(states, [
          WsConnectionState.connecting,
          WsConnectionState.connected,
        ]);
        await ws.dispose();
      },
    );

    test(
      'a drop transitions to reconnecting, then back to connected on a frame',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(
          server,
          connect: conn.connect,
          backoff: const [Duration.zero],
        );
        final states = <WsConnectionState>[];
        ws.connectionStates.listen(states.add);
        ws.connect();
        conn.legs.first.incoming.add(_envelope('e', 1));
        await pumpEventQueue();

        await conn.legs.first.drop();
        await pumpEventQueue();
        expect(ws.connectionState, WsConnectionState.reconnecting);
        conn.legs[1].incoming.add(_envelope('e', 2));
        await pumpEventQueue();

        expect(states, [
          WsConnectionState.connecting,
          WsConnectionState.connected,
          WsConnectionState.reconnecting,
          WsConnectionState.connected,
        ]);
        await ws.dispose();
      },
    );

    test('dispose transitions to disconnected', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      ws.connect();
      conn.legs.first.incoming.add(_envelope('e', 1));
      await pumpEventQueue();
      await ws.dispose();
      expect(ws.connectionState, WsConnectionState.disconnected);
    });

    test(
      'first connect sends an empty resume_token (fresh subscription, no '
      'historical replay) so the server answers without dumping its backlog',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(server, connect: conn.connect);
        ws.connect();
        await pumpEventQueue();
        expect(conn.legs.first.sent, [
          jsonEncode({'resume_token': ''}),
        ]);
        await ws.dispose();
      },
    );

    test('an open socket that never sends a frame flips to connected after the '
        'connect-grace window (idle/old server safety net)', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(
        server,
        connect: conn.connect,
        connectGrace: const Duration(milliseconds: 20),
      );
      ws.connect();
      // No frame delivered. Before the grace window: still connecting.
      expect(ws.connectionState, WsConnectionState.connecting);
      await Future<void>.delayed(const Duration(milliseconds: 40));
      // Socket stayed open with no frame → grace fallback marks it connected.
      expect(ws.connectionState, WsConnectionState.connected);
      await ws.dispose();
    });

    test(
      'a socket dropped within the grace window does NOT get marked connected',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(
          server,
          connect: conn.connect,
          connectGrace: const Duration(milliseconds: 50),
          backoff: const [Duration(seconds: 30)],
        );
        ws.connect();
        await conn.legs.first.drop(); // teardown before the grace timer fires
        await pumpEventQueue();
        await Future<void>.delayed(const Duration(milliseconds: 70));
        // Grace timer was cancelled on teardown → reconnecting, not connected.
        expect(ws.connectionState, WsConnectionState.reconnecting);
        await ws.dispose();
      },
    );

    test('dispose stops reconnecting and closes the events stream', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(
        server,
        connect: conn.connect,
        backoff: const [Duration.zero],
      );
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

  group('§27 single-client policy', () {
    test(
      'claimSession runs before the dial and binds via X-Ara-Session',
      () async {
        final conn = _FakeConnector();
        var claims = 0;
        final ws = WsEventStream(
          server,
          connect: conn.connect,
          claimSession: () async {
            claims++;
            expect(
              conn.legs,
              isEmpty,
              reason: 'claim must resolve before the socket dials',
            );
            return 'session-123';
          },
        );
        ws.connect();
        await pumpEventQueue();

        expect(claims, 1);
        expect(conn.lastHeaders, {
          'X-Ara-WS-Version': '1',
          'X-Ara-Session': 'session-123',
        });
        await ws.dispose();
      },
    );

    test('a null claim (denied / older daemon) connects unbound', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(
        server,
        connect: conn.connect,
        claimSession: () async => null,
      );
      ws.connect();
      await pumpEventQueue();

      expect(
        conn.legs,
        hasLength(1),
        reason: 'the event stream must still connect',
      );
      expect(conn.lastHeaders, {'X-Ara-WS-Version': '1'});
      await ws.dispose();
    });

    test('a throwing claim still connects unbound', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(
        server,
        connect: conn.connect,
        claimSession: () async => throw StateError('daemon unreachable'),
      );
      ws.connect();
      await pumpEventQueue();

      expect(conn.legs, hasLength(1));
      expect(conn.lastHeaders, {'X-Ara-WS-Version': '1'});
      await ws.dispose();
    });

    test(
      'every reconnect re-claims (so a blip re-binds the same session)',
      () async {
        final conn = _FakeConnector();
        var claims = 0;
        final ws = WsEventStream(
          server,
          connect: conn.connect,
          backoff: const [Duration.zero],
          claimSession: () async {
            claims++;
            return 'session-123';
          },
        );
        ws.connect();
        await pumpEventQueue();
        await conn.legs.first.drop();
        await pumpEventQueue();

        expect(conn.legs, hasLength(2));
        expect(claims, 2);
        await ws.dispose();
      },
    );

    test('answers a ping control frame with a pong', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      ws.connect();
      conn.legs.first.incoming.add(jsonEncode({'type': 'ping'}));
      await pumpEventQueue();

      expect(
        conn.legs.first.sent.map(jsonDecode),
        anyElement(equals({'type': 'pong'})),
      );
      await ws.dispose();
    });

    test(
      'surfaces connection.request and sends the modal answer back',
      () async {
        final conn = _FakeConnector();
        final ws = WsEventStream(server, connect: conn.connect);
        final requests = <WsConnectionRequest>[];
        ws.connectionRequests.listen(requests.add);
        ws.connect();
        conn.legs.first.incoming.add(
          jsonEncode({
            'type': 'connection.request',
            'from': 'ipad.local',
            'request_id': 'req-1',
          }),
        );
        await pumpEventQueue();

        expect(requests, hasLength(1));
        expect(requests.single.from, 'ipad.local');
        expect(requests.single.requestId, 'req-1');

        ws.sendConnectionResponse('req-1', allow: false);
        expect(
          conn.legs.first.sent.map(jsonDecode),
          anyElement(
            equals({
              'type': 'connection.response',
              'request_id': 'req-1',
              'action': 'reject',
            }),
          ),
        );
        await ws.dispose();
      },
    );

    test('a connection.request without a request_id is ignored', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(server, connect: conn.connect);
      final requests = <WsConnectionRequest>[];
      ws.connectionRequests.listen(requests.add);
      ws.connect();
      conn.legs.first.incoming.add(jsonEncode({'type': 'connection.request'}));
      await pumpEventQueue();

      expect(requests, isEmpty);
      await ws.dispose();
    });

    test('close code 4004 is terminal: takenOver, no auto-reconnect', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(
        server,
        connect: conn.connect,
        backoff: const [Duration.zero],
      );
      ws.connect();
      await conn.legs.first.dropWithCode(4004);
      await pumpEventQueue();

      expect(ws.connectionState, WsConnectionState.takenOver);
      expect(
        conn.legs,
        hasLength(1),
        reason: 'auto-reconnecting would fight the new holder for the slot',
      );
      await ws.dispose();
    });

    test('an explicit connect() after a 4004 takeover dials again', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(
        server,
        connect: conn.connect,
        backoff: const [Duration.zero],
      );
      ws.connect();
      await conn.legs.first.dropWithCode(4004);
      await pumpEventQueue();

      ws.connect(); // the user chose Reconnect in the transferred modal
      await pumpEventQueue();
      expect(conn.legs, hasLength(2));
      await ws.dispose();
    });

    test('a non-4004 close code still reconnects normally', () async {
      final conn = _FakeConnector();
      final ws = WsEventStream(
        server,
        connect: conn.connect,
        backoff: const [Duration.zero],
      );
      ws.connect();
      await conn.legs.first.dropWithCode(1012); // restart imminent
      await pumpEventQueue();

      expect(conn.legs, hasLength(2));
      await ws.dispose();
    });
  });
}
