import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/stats_target.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/stats_export_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/stats_targets_state.dart';
import 'package:openastroara/widgets/stats/targets_section.dart';

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

class _FakeStatsExportClient implements StatsExportClient {
  _FakeStatsExportClient(this.targets);
  List<StatsTarget> targets;
  bool throwOnFetch = false;

  @override
  Future<List<StatsTarget>> fetchTargets() async {
    if (throwOnFetch) throw StateError('boom');
    return targets;
  }

  @override
  Future<String> fetchCsv(String scope) async => 'header\n';

  @override
  String astrobinExportUrl(String target) => 'http://h/x?target=$target';

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

Widget _app(List<AraServer> servers, StatsExportClient api) {
  return ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
      statsExportApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(
      home: Scaffold(
        body: SingleChildScrollView(child: TargetsSection()),
      ),
    ),
  );
}

void main() {
  testWidgets('renders the target tiles when data is present', (tester) async {
    final api = _FakeStatsExportClient(
        const [StatsTarget(targetName: 'M31', frameCount: 12)]);
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    expect(find.text('M31'), findsOneWidget);
    expect(find.textContaining('Couldn’t refresh'), findsNothing);
  });

  testWidgets('a failed manual refresh shows the stale banner over the list',
      (tester) async {
    final api = _FakeStatsExportClient(const [StatsTarget(targetName: 'M31')]);
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    expect(find.text('M31'), findsOneWidget);
    expect(find.textContaining('Couldn’t refresh'), findsNothing);

    api.throwOnFetch = true;
    await tester.tap(find.byIcon(Icons.refresh));
    await tester.pumpAndSettle();

    expect(find.textContaining('Couldn’t refresh'), findsOneWidget);
    expect(find.text('M31'), findsOneWidget);
  });
}
