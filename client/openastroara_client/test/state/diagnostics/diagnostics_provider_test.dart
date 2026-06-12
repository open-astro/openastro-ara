import 'dart:async';
import 'dart:convert';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/diagnostics/diagnostics_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/status_indicator.dart';

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

String _frame(String type, Map<String, dynamic> payload, int seq) =>
    jsonEncode({'type': type, 'ts': '2026-06-12T12:00:00.000Z', 'seq': seq, 'payload': payload});

void main() {
  group('diagnosticsStateProvider', () {
    test('no saved server → not connected', () async {
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(const [])),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);

      final sub = container.listen(diagnosticsStateProvider, (_, _) {});
      addTearDown(sub.close);
      await pumpEventQueue();

      final snap = container.read(diagnosticsStateProvider);
      expect(snap.level, StatusLevel.disconnected);
      expect(snap.label, 'Diagnostics: not connected');
    });

    test('routes a live diagnostics.issue_detected frame into the snapshot', () async {
      final conn = _FakeConnector();
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider
            .overrideWithValue(_FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        wsEventStreamFactoryProvider.overrideWithValue((s) => WsEventStream(s, connect: conn.connect)),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);

      final sub = container.listen(diagnosticsStateProvider, (_, _) {});
      addTearDown(sub.close);
      await pumpEventQueue();

      // Connected with no events yet → nominal.
      expect(container.read(diagnosticsStateProvider).label, 'Diagnostics: nominal');

      conn.legs.first.incoming.add(_frame('diagnostics.issue_detected',
          {'event_type': 'disk.low', 'severity': 'yellow', 'description': 'Low disk space'}, 1));
      await pumpEventQueue();

      final snap = container.read(diagnosticsStateProvider);
      expect(snap.level, StatusLevel.busy);
      expect(snap.label, 'Diagnostics: 1 issue — warning');
      expect(snap.events.single.source, 'disk.low');
    });

    test('a non-diagnostics frame leaves the snapshot at nominal', () async {
      final conn = _FakeConnector();
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider
            .overrideWithValue(_FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        wsEventStreamFactoryProvider.overrideWithValue((s) => WsEventStream(s, connect: conn.connect)),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);

      final sub = container.listen(diagnosticsStateProvider, (_, _) {});
      addTearDown(sub.close);
      await pumpEventQueue();

      conn.legs.first.incoming.add(_frame('guider.dark_library.complete', {'profile_id': 'p1'}, 1));
      await pumpEventQueue();

      final snap = container.read(diagnosticsStateProvider);
      expect(snap.label, 'Diagnostics: nominal');
      expect(snap.events, isEmpty);
    });
  });
}
