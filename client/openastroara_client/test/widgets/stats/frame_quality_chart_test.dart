import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/frame_quality.dart';
import 'package:openastroara/services/frame_quality_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/frame_quality_state.dart';
import 'package:openastroara/widgets/stats/charts/frame_quality_chart.dart';

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

class _FakeFrameQualityClient implements FrameQualityClient {
  _FakeFrameQualityClient(this.value);
  FrameQualityDistribution value;
  bool throwOnFetch = false;

  @override
  Future<FrameQualityDistribution> fetch({String? filter}) async {
    if (throwOnFetch) throw StateError('boom');
    return value;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

FrameQualityDistribution _dist(int count) => FrameQualityDistribution(
      buckets: [FrameQualityBucket(rangeLow: 0.9, rangeHigh: 1.0, count: count)],
      meanScore: 0.95,
    );

Widget _app(List<AraServer> servers, FrameQualityClient api) {
  return ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
      frameQualityApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(
      home: Scaffold(
        body: SingleChildScrollView(child: FrameQualityChart()),
      ),
    ),
  );
}

void main() {
  testWidgets('a failed manual refresh shows the stale chip over the chart',
      (tester) async {
    final api = _FakeFrameQualityClient(_dist(5));
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    // Histogram rendered, no stale chip yet.
    expect(find.text('Stale'), findsNothing);

    api.throwOnFetch = true;
    await tester.tap(find.byIcon(Icons.refresh));
    await tester.pumpAndSettle();

    // Stale chip shown; the chart (title) is still present.
    expect(find.text('Stale'), findsOneWidget);
    expect(find.textContaining('Frame Quality'), findsOneWidget);
  });
}
