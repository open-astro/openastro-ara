import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/connection_policy_listener.dart';

class _FakeSocket {
  final StreamController<dynamic> incoming = StreamController<dynamic>();
  final List<String> sent = <String>[];
  int? closeCode;

  late final WsSocket socket = WsSocket(
    stream: incoming.stream,
    send: sent.add,
    close: () async {
      if (!incoming.isClosed) await incoming.close();
    },
    closeCode: () => closeCode,
  );
}

class _FakeConnector {
  final List<_FakeSocket> legs = <_FakeSocket>[];
  WsSocket connect(Uri url, Map<String, String> headers) {
    final leg = _FakeSocket();
    legs.add(leg);
    return leg.socket;
  }
}

void main() {
  const server = AraServer(hostname: 'host-a', port: 5555);

  /// Pumps the listener under a real MaterialApp with the WS stream provider
  /// overridden to a fake-socket-backed stream the test drives directly.
  Future<(_FakeConnector, WsEventStream)> pumpListener(
    WidgetTester tester, {
    bool simple = false,
    Locale? locale,
  }) async {
    final conn = _FakeConnector();
    final stream = WsEventStream(server, connect: conn.connect);
    addTearDown(() => stream.dispose());
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          if (simple) simpleModeActiveProvider.overrideWithValue(true),
          wsEventStreamProvider.overrideWith((ref) {
            stream.connect();
            // Mirror the real provider: tearing the scope down disposes the
            // stream. Without this the stream's connected-link timers outlive
            // the widget tree and the test fails on a pending timer.
            ref.onDispose(() => unawaited(stream.dispose()));
            return stream;
          }),
        ],
        child: MaterialApp(
          locale: locale,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const Scaffold(
            body: ConnectionPolicyListener(child: SizedBox.shrink()),
          ),
        ),
      ),
    );
    await tester.pump();
    return (conn, stream);
  }

  group('ConnectionPolicyListener (§27)', () {
    testWidgets(
      'wilma_handles_4004_single_client_takeover_by_showing_session_transferred_modal',
      (tester) async {
        final (conn, _) = await pumpListener(tester);

        // The daemon closes our socket with the §27 takeover code.
        conn.legs.first.closeCode = 4004;
        await conn.legs.first.incoming.close();
        await tester.pump();
        await tester.pump();

        expect(find.text('Session transferred'), findsOneWidget);
        expect(find.textContaining('took control'), findsOneWidget);
        // Terminal: no reconnect leg was dialed behind the modal.
        expect(conn.legs, hasLength(1));

        // OK just dismisses; the link stays down.
        await tester.tap(find.text('OK'));
        await tester.pump();
        expect(find.text('Session transferred'), findsNothing);
        expect(conn.legs, hasLength(1));
      },
    );

    testWidgets('Reconnect in the transferred modal dials a fresh connection', (
      tester,
    ) async {
      final (conn, _) = await pumpListener(tester);

      conn.legs.first.closeCode = 4004;
      await conn.legs.first.incoming.close();
      await tester.pump();
      await tester.pump();

      await tester.tap(find.text('Reconnect'));
      await tester.pump();

      expect(find.text('Session transferred'), findsNothing);
      expect(conn.legs, hasLength(2), reason: 'explicit user action re-dials');
      // Flush the fresh leg's connect-grace timer so no timer outlives the test.
      await tester.pump(WsEventStream.defaultConnectGrace);
    });

    testWidgets(
      'connection.request shows the takeover modal; Allow answers allow',
      (tester) async {
        final (conn, _) = await pumpListener(tester);

        conn.legs.first.incoming.add(
          jsonEncode({
            'type': 'connection.request',
            'from': 'ipad.local',
            'request_id': 'req-9',
          }),
        );
        await tester.pump();
        await tester.pump();

        expect(find.text('ipad.local wants to connect'), findsOneWidget);

        await tester.tap(find.text('Allow'));
        await tester.pump();

        expect(find.text('ipad.local wants to connect'), findsNothing);
        expect(
          conn.legs.first.sent.map(jsonDecode),
          anyElement(
            equals({
              'type': 'connection.response',
              'request_id': 'req-9',
              'action': 'allow',
            }),
          ),
        );
      },
    );

    testWidgets('Keep me connected answers reject', (tester) async {
      final (conn, _) = await pumpListener(tester);

      conn.legs.first.incoming.add(
        jsonEncode({
          'type': 'connection.request',
          'from': 'ipad.local',
          'request_id': 'req-9',
        }),
      );
      await tester.pump();
      await tester.pump();

      await tester.tap(find.text('Keep me connected'));
      await tester.pump();

      expect(
        conn.legs.first.sent.map(jsonDecode),
        anyElement(
          equals({
            'type': 'connection.response',
            'request_id': 'req-9',
            'action': 'reject',
          }),
        ),
      );
    });

    testWidgets('the takeover modal auto-dismisses before the daemon timeout', (
      tester,
    ) async {
      final (conn, _) = await pumpListener(tester);

      conn.legs.first.incoming.add(
        jsonEncode({
          'type': 'connection.request',
          'from': 'ipad.local',
          'request_id': 'req-9',
        }),
      );
      await tester.pump();
      await tester.pump();
      expect(find.text('ipad.local wants to connect'), findsOneWidget);

      await tester.pump(const Duration(seconds: 26));

      expect(find.text('ipad.local wants to connect'), findsNothing);
      // No answer was sent — the daemon's own 30 s timeout resolves the dance.
      expect(
        conn.legs.first.sent
            .map(jsonDecode)
            .where((f) => f is Map && f['type'] == 'connection.response'),
        isEmpty,
      );
    });

    testWidgets('a second request replaces a stale open modal', (tester) async {
      final (conn, _) = await pumpListener(tester);

      conn.legs.first.incoming.add(
        jsonEncode({
          'type': 'connection.request',
          'from': 'ipad.local',
          'request_id': 'req-1',
        }),
      );
      await tester.pump();
      await tester.pump();
      conn.legs.first.incoming.add(
        jsonEncode({
          'type': 'connection.request',
          'from': 'phone.local',
          'request_id': 'req-2',
        }),
      );
      await tester.pump();
      await tester.pump();

      expect(
        find.text('ipad.local wants to connect'),
        findsNothing,
        reason: 'the first request is stale once a second arrives',
      );
      expect(find.text('phone.local wants to connect'), findsOneWidget);

      await tester.tap(find.text('Allow'));
      await tester.pump();
      expect(
        conn.legs.first.sent.map(jsonDecode),
        anyElement(
          equals({
            'type': 'connection.response',
            'request_id': 'req-2',
            'action': 'allow',
          }),
        ),
      );
    });
  });

  group('Simple Mode', () {
    List<SimpleMessage> pills(WidgetTester tester) =>
        ProviderScope.containerOf(tester.element(find.byType(ConnectionPolicyListener))).read(simpleMessagesProvider);

    testWidgets('a takeover pill is in Brazilian Portuguese', (tester) async {
      final (conn, _) = await pumpListener(tester, simple: true, locale: const Locale('pt', 'BR'));
      conn.legs.first.incoming.add(jsonEncode({'type': 'connection.request', 'from': 'ipad.local', 'request_id': 'req-9'}));
      await tester.pump();
      await tester.pump();
      final pill = pills(tester).single;
      expect(pill.title, 'ipad.local quer se conectar');
      expect(pill.actions.map((a) => a.label), ['Manter conectado', 'Permitir']);
      await tester.pump(const Duration(seconds: 30)); // the request's timer
    });

    testWidgets('a takeover request is a question pill and Allow answers allow', (tester) async {
      final (conn, _) = await pumpListener(tester, simple: true);
      conn.legs.first.incoming.add(jsonEncode({'type': 'connection.request', 'from': 'ipad.local', 'request_id': 'req-9'}));
      await tester.pump();
      await tester.pump();
      expect(find.byType(AlertDialog), findsNothing);
      final pill = pills(tester).single;
      expect(pill.kind, SimpleMessageKind.question);
      expect(pill.title, 'ipad.local wants to connect');
      expect(pill.actions.map((a) => a.label), ['Keep me connected', 'Allow']);
      pill.actions.last.onPressed();
      await tester.pump();
      expect(conn.legs.first.sent.join(), contains('req-9'));
      expect(conn.legs.first.sent.join(), contains('"action":"allow"'));
      await tester.pump(const Duration(seconds: 30)); // the request's timer
    });

    testWidgets('an unanswered takeover pill goes when the rig stops asking', (tester) async {
      final (conn, _) = await pumpListener(tester, simple: true);
      conn.legs.first.incoming.add(jsonEncode({'type': 'connection.request', 'from': 'ipad.local', 'request_id': 'req-1'}));
      await tester.pump();
      await tester.pump();
      expect(pills(tester), hasLength(1));
      await tester.pump(const Duration(seconds: 26));
      expect(pills(tester), isEmpty);
    });

    testWidgets('a lost session is a question pill and Reconnect dials again', (tester) async {
      final (conn, _) = await pumpListener(tester, simple: true);
      conn.legs.first.closeCode = 4004;
      await conn.legs.first.incoming.close();
      await tester.pump();
      await tester.pump();
      expect(find.byType(AlertDialog), findsNothing);
      final pill = pills(tester).single;
      expect(pill.title, 'Session transferred');
      pill.actions.last.onPressed();
      await tester.pump();
      expect(conn.legs, hasLength(2));
      await tester.pump(WsEventStream.defaultConnectGrace);
    });
  });
}
