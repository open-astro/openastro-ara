import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/calendar_stats.dart';
import 'package:openastroara/services/calendar_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/calendar_state.dart';
import 'package:openastroara/widgets/stats/charts/calendar_heatmap.dart';

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

class _FakeCalendarClient implements CalendarClient {
  _FakeCalendarClient(this.value);
  CalendarStats value;
  bool throwOnFetch = false;

  @override
  Future<CalendarStats> fetch({int days = 49}) async {
    if (throwOnFetch) throw StateError('boom');
    return value;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

final _stats = CalendarStats(days: [
  CalendarDay(date: DateTime(2026, 6, 10), frameCount: 8, integrationHours: 1.5),
]);

Widget _app(List<AraServer> servers, CalendarClient api) {
  return ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
      calendarApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(
      home: Scaffold(
        body: SingleChildScrollView(child: CalendarHeatmap()),
      ),
    ),
  );
}

void main() {
  testWidgets('a failed manual refresh shows the stale chip over the heatmap',
      (tester) async {
    final api = _FakeCalendarClient(_stats);
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    expect(find.text('Stale'), findsNothing);

    api.throwOnFetch = true;
    await tester.tap(find.byIcon(Icons.refresh));
    await tester.pumpAndSettle();

    expect(find.text('Stale'), findsOneWidget);
    expect(find.textContaining('Calendar Heatmap'), findsOneWidget);
  });
}
