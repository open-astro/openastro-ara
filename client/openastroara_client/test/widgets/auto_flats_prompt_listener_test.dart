import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/auto_flats_prompt_listener.dart';

/// Records the §48 decision call; every other member is unused by the dialog.
class _RecordingClient implements SequenceClient {
  String? lastId;
  String? lastChoice;
  bool? lastRemember;
  int calls = 0;

  @override
  Future<String> decideAutoFlats(String id,
      {required String choice, required bool remember}) async {
    calls++;
    lastId = id;
    lastChoice = choice;
    lastRemember = remember;
    return 'accepted';
  }

  @override
  dynamic noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('${invocation.memberName}');
}

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

  Future<(_FakeConnector, _RecordingClient)> pumpListener(
    WidgetTester tester,
  ) async {
    final conn = _FakeConnector();
    final client = _RecordingClient();
    final stream = WsEventStream(server, connect: conn.connect);
    addTearDown(() => stream.dispose());
    await tester.pumpWidget(
      ProviderScope(
        overrides: [
          wsEventStreamProvider.overrideWith((ref) {
            stream.connect();
            return stream;
          }),
          sequenceApiProvider.overrideWith((ref) => client),
        ],
        child: const MaterialApp(
          home: Scaffold(
            body: AutoFlatsPromptListener(child: SizedBox.shrink()),
          ),
        ),
      ),
    );
    await tester.pump();
    return (conn, client);
  }

  void emitPrompt(_FakeConnector conn, {String sequenceId = 'seq-1', int seq = 1}) {
    conn.legs.first.incoming.add(jsonEncode({
      'type': 'sequence.auto_flats_prompt',
      'ts': '2026-01-01T00:00:00Z',
      'seq': seq,
      'payload': {'sequence_id': sequenceId, 'run_id': 'run-1'},
    }));
  }

  group('AutoFlatsPromptListener (§48)', () {
    testWidgets(
        'the prompt event shows the dialog; a remembered panel choice posts the decision',
        (tester) async {
      final (conn, client) = await pumpListener(tester);

      emitPrompt(conn);
      await tester.pump();
      await tester.pump();

      expect(find.text('Capture calibration frames tonight?'), findsOneWidget);

      await tester.tap(find.text('Remember my choice (stop asking)'));
      await tester.pump();
      await tester.tap(find.text('Panel flats at end'));
      await tester.pump();

      expect(find.text('Capture calibration frames tonight?'), findsNothing);
      expect(client.lastId, 'seq-1');
      expect(client.lastChoice, 'panel_at_end');
      expect(client.lastRemember, isTrue);
    });

    testWidgets('"Not tonight" posts later without remember', (tester) async {
      final (conn, client) = await pumpListener(tester);

      emitPrompt(conn);
      await tester.pump();
      await tester.pump();

      await tester.tap(find.text('Not tonight'));
      await tester.pump();

      expect(client.lastChoice, 'later');
      expect(client.lastRemember, isFalse);
    });

    testWidgets('a newer run\'s prompt replaces a stale unanswered dialog',
        (tester) async {
      final (conn, client) = await pumpListener(tester);

      emitPrompt(conn, sequenceId: 'seq-old', seq: 1);
      await tester.pump();
      await tester.pump();
      emitPrompt(conn, sequenceId: 'seq-new', seq: 2);
      await tester.pump();
      await tester.pump();

      // One dialog only — the stale one closed unanswered.
      expect(find.text('Capture calibration frames tonight?'), findsOneWidget);

      await tester.tap(find.text('Sky flats at twilight'));
      await tester.pump();

      expect(client.calls, 1);
      expect(client.lastId, 'seq-new',
          reason: 'the answer must bind to the run that is actually asking');
      expect(client.lastChoice, 'sky_at_twilight');
    });
  });
}
