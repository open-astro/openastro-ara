import 'dart:async';
import 'dart:convert';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/cursor_page.dart';
import 'package:openastroara/models/fault_row.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/faults_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/faults/faults_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/status_indicator.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(List<AraServer> stored) : _stored = [...stored];
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async => _stored
    ..clear()
    ..addAll(servers);
  @override
  Future<void> add(AraServer server) async => _stored.add(server);
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

class _FakeFaultsClient implements FaultsClient {
  _FakeFaultsClient(this.rows);
  final List<FaultRow> rows;
  int listCalls = 0;
  bool? lastUnresolvedOnly;

  @override
  Future<CursorPage<FaultRow>> list({
    int limit = 200,
    String? cursor,
    String? equipmentType,
    String? faultType,
    String? sessionId,
    bool? unresolvedOnly,
  }) async {
    listCalls++;
    lastUnresolvedOnly = unresolvedOnly;
    return CursorPage(items: rows, nextCursor: null, hasMore: false);
  }

  @override
  Future<FaultRow?> getById(String id) async => null;

  @override
  void close() {}
}

// Live frames carry a current server timestamp — the clear-vs-seed ordering
// guard compares it against history rows' detected_utc, so a fixed past ts
// would misorder a clear against a now-relative seeded row.
String _frame(String type, Map<String, dynamic> payload, int seq) => jsonEncode({
      'type': type,
      'ts': DateTime.now().toUtc().toIso8601String(),
      'seq': seq,
      'payload': payload
    });

// Uses `pumpEventQueue()` like diagnostics_provider_test — the fake socket and
// fake client are microtask-only. The notifier's periodic prune Timer never
// fires inside a drained event queue.
void main() {
  group('activeFaultsProvider', () {
    test('seeds the standing set from unresolved fault history on build',
        () async {
      final conn = _FakeConnector();
      final fakeApi = _FakeFaultsClient([
        FaultRow(
          id: 'f1',
          sessionId: null,
          detectedUtc: DateTime.now().toUtc().subtract(const Duration(hours: 6)),
          equipmentType: 'telescope',
          equipmentId: 'dev-1',
          equipmentName: 'Overnight Mount',
          faultType: 'tracking_lost',
          details: 'tracking off for 3 ticks',
          actionTaken: 'gave_up:pause_sequence',
          resolvedUtc: null,
        ),
      ]);
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(
            _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        wsEventStreamFactoryProvider
            .overrideWithValue((s) => WsEventStream(s, connect: conn.connect)),
        faultsApiFactoryProvider.overrideWithValue((server) => fakeApi),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);

      final sub = container.listen(activeFaultsProvider, (_, _) {});
      addTearDown(sub.close);
      await pumpEventQueue();

      expect(fakeApi.listCalls, 1);
      expect(fakeApi.lastUnresolvedOnly, isTrue);
      final snap = container.read(activeFaultsProvider);
      expect(snap.byDeviceType['telescope']!.level, StatusLevel.error,
          reason: 'a fault that fired while WILMA was closed seeds the chip');
      expect(snap.worstFor(const {'telescope'}), StatusLevel.error);
    });

    test('a live recovered action clears a seeded fault', () async {
      final conn = _FakeConnector();
      final fakeApi = _FakeFaultsClient([
        FaultRow(
          id: 'f1',
          sessionId: null,
          detectedUtc: DateTime.now().toUtc().subtract(const Duration(hours: 1)),
          equipmentType: 'camera',
          equipmentId: 'dev-1',
          equipmentName: 'Cam',
          faultType: 'disconnected',
          details: null,
          actionTaken: 'reconnecting',
          resolvedUtc: null,
        ),
      ]);
      final container = ProviderContainer(overrides: [
        savedServerServiceProvider.overrideWithValue(
            _FakeSavedServerService(const [AraServer(hostname: 'h', port: 5555)])),
        wsEventStreamFactoryProvider
            .overrideWithValue((s) => WsEventStream(s, connect: conn.connect)),
        faultsApiFactoryProvider.overrideWithValue((server) => fakeApi),
      ]);
      addTearDown(container.dispose);
      await container.read(savedServersProvider.future);

      final sub = container.listen(activeFaultsProvider, (_, _) {});
      addTearDown(sub.close);
      await pumpEventQueue();
      expect(container.read(activeFaultsProvider).worstFor(const {'camera'}),
          StatusLevel.error);

      conn.legs.first.incoming.add(_frame('equipment.fault_action_taken',
          {'device_type': 'camera', 'kind': 'disconnected', 'action': 'recovered'}, 1));
      await pumpEventQueue();

      expect(container.read(activeFaultsProvider).worstFor(const {'camera'}),
          isNull, reason: 'the live episode outcome clears the seeded fault');
    });
  });
}
