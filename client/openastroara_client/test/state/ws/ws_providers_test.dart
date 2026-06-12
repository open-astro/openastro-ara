import 'dart:async';
import 'dart:convert';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(this._stored);
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeSocket {
  final StreamController<dynamic> incoming = StreamController<dynamic>();
  late final WsSocket socket = WsSocket(
    stream: incoming.stream,
    send: (_) {},
    close: () async {
      if (!incoming.isClosed) await incoming.close();
    },
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

String _envelope(int seq) =>
    jsonEncode({'type': 'e', 'ts': '2026-06-12T12:00:00.000Z', 'seq': seq, 'payload': const {}});

void main() {
  group('ws_providers', () {
    test('no saved server → stream is null and state is disconnected', () async {
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(const [])),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);

      // Keep the autoDispose providers alive while we read them.
      final sub = container.listen(wsConnectionStateProvider, (_, _) {});
      addTearDown(sub.close);
      await pumpEventQueue();

      expect(container.read(wsEventStreamProvider), isNull);
      expect(container.read(wsConnectionStateProvider).asData?.value, WsConnectionState.disconnected);
    });

    test('active server → factory builds the stream, connects, and reaches connected on a frame', () async {
      final conn = _FakeConnector();
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider
            .overrideWithValue(_FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        wsEventStreamFactoryProvider.overrideWithValue((s) => WsEventStream(s, connect: conn.connect)),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);

      // Keep the autoDispose provider alive for the test.
      final sub = container.listen(wsEventStreamProvider, (_, _) {});
      addTearDown(sub.close);
      final stream = sub.read();
      expect(stream, isNotNull);
      expect(stream!.connectionState, WsConnectionState.connecting,
          reason: 'creating the provider connects the stream');

      conn.legs.first.incoming.add(_envelope(1));
      await pumpEventQueue();
      expect(stream.connectionState, WsConnectionState.connected);
    });
  });
}
