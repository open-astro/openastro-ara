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
import 'package:openastroara/widgets/stats/stat_tile.dart';
import 'pull_refresh.dart';

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

    // Next fetch fails; pull down. The banner appears and the tiles stay.
    api.throwOnFetch = true;
    pullRefresh(tester);
    await tester.pumpAndSettle();

    expect(find.textContaining('Couldn’t refresh'), findsOneWidget);
    expect(find.text('Frames'), findsOneWidget);
  });

  Future<void> dashboard(WidgetTester tester, double width, {required bool sideBySide}) async {
      await tester.binding.setSurfaceSize(Size(width, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      final api = _FakeStatsOverviewClient(const StatsOverview(
          totalSessions: 3, totalFrames: 120, totalLightFrames: 100, totalIntegrationHours: 12.5));
      await tester.pumpWidget(_app(const [_server], api));
      await tester.pumpAndSettle();

      final hero = find.byKey(const Key('stats-hero'));
      expect(find.descendant(of: hero, matching: find.text('Total integration')), findsOneWidget);
      expect(find.descendant(of: hero, matching: find.text('12h 30m')), findsOneWidget);
      expect(find.byType(StatKpi), findsNWidgets(5));
      for (final label in ['Sessions', 'Frames', 'Light frames', 'Unique targets', 'Last imaged']) {
        expect(find.widgetWithText(StatKpi, label), findsOneWidget, reason: label);
      }
      final h = tester.getRect(hero);
      final k = tester.getRect(find.byType(StatKpiGrid));
      if (sideBySide) {
        expect(k.left, greaterThan(h.right), reason: 'the list beside the headline');
        expect(k.top, h.top);
      } else {
        expect(k.top, greaterThan(h.bottom), reason: 'the list under the headline');
      }
  }

  // Falsified by: overview_section.dart back to the old tile grid (no stats-hero, no StatKpi).
  testWidgets('the dashboard: the total integration headline with the totals list beside it on a wide window',
      (tester) => dashboard(tester, 1000, sideBySide: true));

  // Falsified by: overview_section.dart showing the list beside the headline at every width.
  testWidgets('the dashboard: on a narrow window the totals list goes under the headline',
      (tester) => dashboard(tester, 500, sideBySide: false));
}
