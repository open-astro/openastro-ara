import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/stats_overview.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/services/stats_overview_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/stats_overview_state.dart';
import 'package:openastroara/widgets/stats/overview_section.dart';

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

class _FakeStatsOverviewClient implements StatsOverviewClient {
  _FakeStatsOverviewClient(this.value);
  StatsOverview value;
  bool throwOnFetch = false;

  @override
  Future<StatsOverview> fetch() async {
    if (throwOnFetch) throw StateError('boom');
    return value;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

Widget _app(List<AraServer> servers, StatsOverviewClient api) {
  return ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
      statsOverviewApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(
      home: Scaffold(
        body: SingleChildScrollView(child: OverviewSection()),
      ),
    ),
  );
}

void main() {
  testWidgets('renders the totals tiles when data is present', (tester) async {
    final api = _FakeStatsOverviewClient(
        const StatsOverview(totalSessions: 3, totalFrames: 120));
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    expect(find.text('Sessions'), findsOneWidget);
    expect(find.text('Frames'), findsOneWidget);
    expect(find.textContaining('Couldn’t refresh'), findsNothing);
  });

  testWidgets('a failed manual refresh shows the stale banner over the tiles',
      (tester) async {
    final api = _FakeStatsOverviewClient(const StatsOverview(totalFrames: 5));
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    expect(find.text('Frames'), findsOneWidget);
    expect(find.textContaining('Couldn’t refresh'), findsNothing);

    // Next fetch fails; tap refresh. The banner appears and the tiles stay.
    api.throwOnFetch = true;
    await tester.tap(find.byIcon(Icons.refresh));
    await tester.pumpAndSettle();

    expect(find.textContaining('Couldn’t refresh'), findsOneWidget);
    expect(find.text('Frames'), findsOneWidget);
  });
}
