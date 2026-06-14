import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/calendar_stats.dart';
import 'package:openastroara/services/calendar_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/calendar_state.dart';

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
  int fetches = 0;
  bool throwOnFetch = false;

  @override
  Future<CalendarStats> fetch({int days = 49}) async {
    fetches++;
    if (throwOnFetch) throw StateError('boom');
    return value;
  }

  @override
  void close() {}
}

class _GatedCalendarClient implements CalendarClient {
  final List<Completer<CalendarStats>> calls = [];

  @override
  Future<CalendarStats> fetch({int days = 49}) {
    final c = Completer<CalendarStats>();
    calls.add(c);
    return c.future;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

CalendarStats _stats(int count) => CalendarStats(days: [
      CalendarDay(date: DateTime(2026, 6, 10), frameCount: count, integrationHours: 1.0),
    ]);

ProviderContainer _container(List<AraServer> servers, CalendarClient api) {
  final c = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
    calendarApiFactoryProvider.overrideWithValue((_) => api),
  ]);
  addTearDown(c.dispose);
  return c;
}

void main() {
  group('calendarProvider', () {
    test('no saved server → null data, no fetch', () async {
      final api = _FakeCalendarClient(const CalendarStats());
      final c = _container(const [], api);
      await c.read(savedServersProvider.future);
      expect(await c.read(calendarProvider.future), isNull);
      expect(api.fetches, 0);
    });

    test('fetches the active server calendar', () async {
      final api = _FakeCalendarClient(_stats(12));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final s = await c.read(calendarProvider.future);
      expect(s!.days.single.frameCount, 12);
      expect(api.fetches, 1);
    });

    test('refresh re-reads from the server', () async {
      final api = _FakeCalendarClient(_stats(1));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(calendarProvider.future);

      api.value = _stats(9);
      await c.read(calendarProvider.notifier).refresh();
      expect(c.read(calendarProvider).value!.days.single.frameCount, 9);
      expect(api.fetches, 2);
    });

    test('an initial-load failure lands in the provider error state', () async {
      final api = _FakeCalendarClient(const CalendarStats())..throwOnFetch = true;
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await expectLater(c.read(calendarProvider.future), throwsA(isA<StateError>()));
      expect(c.read(calendarProvider).hasError, isTrue);
    });

    test('a failed refresh keeps the prior calendar and rethrows (no blanking)', () async {
      final api = _FakeCalendarClient(_stats(7));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(calendarProvider.future);

      api.throwOnFetch = true;
      await expectLater(
          c.read(calendarProvider.notifier).refresh(), throwsA(isA<StateError>()));
      expect(c.read(calendarProvider).hasError, isFalse);
      expect(c.read(calendarProvider).value!.days.single.frameCount, 7);
    });

    test('a successful refresh swaps the new calendar in without a loading flash', () async {
      final api = _FakeCalendarClient(_stats(1));
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      await c.read(calendarProvider.future);

      api.value = _stats(9);
      final future = c.read(calendarProvider.notifier).refresh();
      expect(c.read(calendarProvider).isLoading, isFalse);
      expect(c.read(calendarProvider).value!.days.single.frameCount, 1);
      await future;
      expect(c.read(calendarProvider).value!.days.single.frameCount, 9);
    });

    test('a server switch mid-refresh discards the stale result (generation guard)', () async {
      final api = _GatedCalendarClient();
      final c = _container(const [_server], api);
      await c.read(savedServersProvider.future);
      final built = c.read(calendarProvider.future);
      api.calls[0].complete(_stats(1));
      await built;

      final notifier = c.read(calendarProvider.notifier);
      final refreshing = notifier.refresh(); // captures generation; calls[1] pending
      notifier.markBuild(); // stand in for a server-switch build() re-run
      api.calls[1].complete(_stats(99)); // now stale
      await refreshing;

      expect(c.read(calendarProvider).value!.days.single.frameCount, 1);
    });
  });
}
