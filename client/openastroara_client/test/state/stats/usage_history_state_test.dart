import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/usage_history.dart';
import 'package:openastroara/services/usage_history_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/usage_history_state.dart';

final _t0 = DateTime.utc(2026, 11, 13, 20);

UsageSample _s(int minute, {int free = 100}) => UsageSample(
  at: _t0.add(Duration(minutes: minute)),
  storageFreeBytes: free,
);

UsageHistory _h(List<UsageSample> s) =>
    UsageHistory(intervalSeconds: 15, processorCount: 4, samples: s);

class _Fake implements UsageHistoryClient {
  _Fake(this.answers);
  final List<Object?> answers; // UsageHistory, null (404) or an Exception
  final sinces = <DateTime?>[];
  Completer<void>? gate;
  var closed = false;

  @override
  Future<UsageHistory?> fetch({DateTime? since}) async {
    sinces.add(since);
    if (gate != null) await gate!.future;
    final a = answers.removeAt(0);
    if (a is Exception) throw a;
    return a as UsageHistory?;
  }

  @override
  void close() => closed = true;
}

ProviderContainer _container(_Fake fake) {
  final c = ProviderContainer(
    overrides: [
      activeServerProvider.overrideWithValue(
        const AraServer(hostname: 'test', port: 1),
      ),
      usageHistoryApiFactoryProvider.overrideWithValue((_) => fake),
    ],
  );
  addTearDown(c.dispose);
  return c;
}

void main() {
  test('loads the rig history when the panel opens', () async {
    final fake = _Fake([
      _h([_s(0), _s(1)]),
    ]);
    final c = _container(fake);
    final sub = c.listen(usageHistoryProvider, (_, _) {});
    final UsageHistory? h = await c.read(usageHistoryProvider.future);
    expect(h!.samples.length, 2);
    expect(fake.sinces, [null], reason: 'the first read asks for everything');
    sub.close();
  });

  test(
    'a refresh asks only for what is newer than the last sample and appends it',
    () async {
      final fake = _Fake([
        _h([_s(0), _s(1)]),
        _h([_s(1), _s(2), _s(3)]),
      ]);
      final c = _container(fake);
      final sub = c.listen(usageHistoryProvider, (_, _) {});
      await c.read(usageHistoryProvider.future);
      await c.read(usageHistoryProvider.notifier).refresh();
      expect(fake.sinces.last, _s(1).at);
      final h = c.read(usageHistoryProvider).value!;
      expect(
        [for (final s in h.samples) s.at.difference(_t0).inMinutes],
        [0, 1, 2, 3],
        reason: 'the repeated sample is not doubled',
      );
      sub.close();
    },
  );

  test(
    'an older daemon without the route (404) gives no history and no error',
    () async {
      final fake = _Fake([null, null]);
      final c = _container(fake);
      final sub = c.listen(usageHistoryProvider, (_, _) {});
      expect(await c.read(usageHistoryProvider.future), isNull);
      await c.read(usageHistoryProvider.notifier).refresh();
      expect(c.read(usageHistoryProvider).hasError, isFalse);
      expect(
        fake.sinces.length,
        1,
        reason: 'a rig without the route is not polled again',
      );
      sub.close();
    },
  );

  test('a missed poll leaves the graph as it was', () async {
    final fake = _Fake([
      _h([_s(0)]),
      DioException(requestOptions: RequestOptions()),
    ]);
    final c = _container(fake);
    final sub = c.listen(usageHistoryProvider, (_, _) {});
    await c.read(usageHistoryProvider.future);
    await c.read(usageHistoryProvider.notifier).refresh();
    expect(c.read(usageHistoryProvider).value!.samples.length, 1);
    expect(c.read(usageHistoryProvider).hasError, isFalse);
    sub.close();
  });

  test('a tick is skipped while the last read is still out', () async {
    final fake = _Fake([
      _h([_s(0)]),
      _h([_s(1)]),
    ]);
    final c = _container(fake);
    final sub = c.listen(usageHistoryProvider, (_, _) {});
    await c.read(usageHistoryProvider.future);
    fake.gate = Completer<void>();
    final first = c.read(usageHistoryProvider.notifier).refresh();
    await c.read(usageHistoryProvider.notifier).refresh();
    expect(
      fake.sinces.length,
      2,
      reason: 'the load and one refresh; the overlapping tick asked nothing',
    );
    fake.gate!.complete();
    await first;
    sub.close();
  });

  test('the client keeps nothing after the panel closes', () async {
    final fake = _Fake([
      _h([_s(0)]),
      _h([_s(0), _s(1)]),
    ]);
    final c = _container(fake);
    final sub = c.listen(usageHistoryProvider, (_, _) {});
    await c.read(usageHistoryProvider.future);
    sub.close();
    await Future<void>.delayed(Duration.zero);
    final again = c.listen(usageHistoryProvider, (_, _) {});
    final UsageHistory? h = await c.read(usageHistoryProvider.future);
    expect(fake.sinces, [
      null,
      null,
    ], reason: 'reopening reads the rig again, from the start');
    expect(h!.samples.length, 2);
    again.close();
  });

  test('the history is capped to what the rig keeps', () {
    final many = _h([for (var i = 0; i < UsageHistory.keepSamples; i++) _s(i)]);
    final grown = many.append(
      _h([_s(UsageHistory.keepSamples), _s(UsageHistory.keepSamples + 1)]),
    );
    expect(grown.samples.length, UsageHistory.keepSamples);
    expect(grown.samples.first.at, _s(2).at);
  });

  test('a null reading stays null, never 0', () {
    final h = UsageHistory.fromJson({
      'interval_seconds': 15,
      'processor_count': 4,
      'samples': [
        {
          'at_utc': '2026-11-13T20:00:00Z',
          'storage_free_bytes': null,
          'cpu_percent': null,
          'memory_bytes': 5,
        },
      ],
    });
    expect(h.samples.single.storageFreeBytes, isNull);
    expect(h.samples.single.cpuPercent, isNull);
    expect(h.samples.single.memoryBytes, 5);
  });

  test('the API maps a 404 to no history and sends since as UTC', () async {
    Uri? seen;
    final dio = Dio()
      ..interceptors.add(
        InterceptorsWrapper(
          onRequest: (o, h) {
            seen = o.uri;
            h.reject(
              DioException.badResponse(
                statusCode: 404,
                requestOptions: o,
                response: Response(requestOptions: o, statusCode: 404),
              ),
            );
          },
        ),
      );
    final api = UsageHistoryApi(
      const AraServer(hostname: 'test', port: 1),
      dio: dio,
    );
    expect(await api.fetch(since: _t0), isNull);
    expect(seen!.queryParameters['since'], '2026-11-13T20:00:00.000Z');
  });
}
