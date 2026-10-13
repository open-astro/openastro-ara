import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/server_internal_state.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/server_internal_state_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/server_internal_state_state.dart';
import 'package:openastroara/widgets/stats/pi_performance_section.dart';

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

class _FakeClient implements ServerInternalStateClient {
  _FakeClient(this.value);
  ServerInternalState value;
  int fetches = 0;
  @override
  Future<ServerInternalState> fetch() async {
    fetches++;
    return value;
  }
  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

Widget _app(List<AraServer> servers, ServerInternalStateClient api) => ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
        serverInternalStateApiFactoryProvider.overrideWithValue((_) => api),
      ],
      child: const MaterialApp(home: Scaffold(body: SingleChildScrollView(child: PiPerformanceSection()))),
    );

void main() {
  test('§66.7 the wire shape parses, with the nullable CPU and load figures', () {
    final s = ServerInternalState.fromJson({
      'captured_utc': '2026-10-13T20:00:00Z',
      'uptime_seconds': 93784.5,
      'process': {
        'working_set_bytes': 268435456,
        'managed_heap_bytes': 50 * 1024 * 1024,
        'heap_committed_bytes': 60 * 1024 * 1024,
        'gen0_collections': 120,
        'gen1_collections': 30,
        'gen2_collections': 4,
        'gc_pause_total_ms': 1234.5,
        'cpu_percent': null,
        'thread_count': 42,
        'thread_pool_busy_workers': 2,
        'thread_pool_pending_work_items': 0,
      },
      'web_sockets': {'open_connections': 2, 'max_connections': 16},
      'host': {
        'processor_count': 4,
        'load_average1_min': 0.42,
        'os_description': 'Debian GNU/Linux 13',
        'architecture': 'Arm64',
        'runtime_version': '10.0.1',
      },
    });
    expect(s.uptimeSeconds, 93784.5);
    expect(s.cpuPercent, isNull);
    expect(s.loadAverage1Min, 0.42);
    expect(s.gen2Collections, 4);
    expect(s.wsMaxConnections, 16);
    expect(formatUptime(s.uptimeSeconds), '1d 2h');
    expect(formatUptime(4000), '1h 06m');
    expect(formatUptime(65), '1m 05s');
    expect(formatBytes(268435456), '256.0 MB');
    expect(formatBytes(3 * 1024 * 1024 * 1024), '3.00 GB');
    expect(ServerInternalState.fromJson(const {}).threadCount, 0, reason: 'a sparse body never throws');
  });

  testWidgets('renders the tiles, and the refresh button re-reads the server', (tester) async {
    final api = _FakeClient(const ServerInternalState(
      uptimeSeconds: 4000,
      workingSetBytes: 268435456,
      managedHeapBytes: 52428800,
      cpuPercent: 37.4,
      gen2Collections: 4,
      gcPauseTotalMs: 1500,
      threadCount: 42,
      threadPoolBusyWorkers: 2,
      wsOpenConnections: 2,
      wsMaxConnections: 16,
      processorCount: 4,
      loadAverage1Min: 5.1,
      osDescription: 'Debian 13',
      architecture: 'Arm64',
      runtimeVersion: '10.0.1',
    ));
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    expect(find.text('Pi performance'), findsOneWidget);
    expect(find.text('1h 06m'), findsOneWidget);
    expect(find.text('256.0 MB'), findsOneWidget);
    expect(find.text('37 %'), findsOneWidget);
    expect(find.text('2 / 16'), findsOneWidget);
    expect(find.text('Above the core count'), findsOneWidget, reason: 'load 5.1 on 4 cores');
    expect(find.textContaining('Debian 13'), findsOneWidget);

    api.value = const ServerInternalState(uptimeSeconds: 65, processorCount: 4);
    await tester.tap(find.byTooltip('Refresh'));
    await tester.pumpAndSettle();
    expect(api.fetches, 2);
    expect(find.text('1m 05s'), findsOneWidget);
    expect(find.text('—'), findsOneWidget, reason: 'no CPU figure yet');
    expect(find.text('Refresh for a reading'), findsOneWidget);
  });

  testWidgets('without a saved server it says to connect', (tester) async {
    await tester.pumpWidget(_app(const [], _FakeClient(const ServerInternalState())));
    await tester.pumpAndSettle();
    expect(find.textContaining('Connect to a server'), findsOneWidget);
  });
}
