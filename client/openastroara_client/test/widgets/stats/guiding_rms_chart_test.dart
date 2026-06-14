import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/guiding_rms.dart';
import 'package:openastroara/services/guiding_rms_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/guiding_rms_state.dart';
import 'package:openastroara/widgets/stats/charts/guiding_rms_chart.dart';

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

class _FakeGuidingRmsClient implements GuidingRmsClient {
  _FakeGuidingRmsClient(this.value);
  GuidingRmsSeries value;
  bool throwOnFetch = false;

  @override
  Future<GuidingRmsSeries> fetch() async {
    if (throwOnFetch) throw StateError('boom');
    return value;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

final _series = GuidingRmsSeries(
  samples: [GuidingRmsPoint(timestamp: DateTime.utc(2026, 1, 1), rmsArcsec: 0.8)],
  meanRmsArcsec: 0.8,
);

Widget _app(List<AraServer> servers, GuidingRmsClient api) {
  return ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
      guidingRmsApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(
      home: Scaffold(
        body: SingleChildScrollView(child: GuidingRmsChart()),
      ),
    ),
  );
}

void main() {
  testWidgets('a failed manual refresh shows the stale chip over the chart',
      (tester) async {
    final api = _FakeGuidingRmsClient(_series);
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    expect(find.text('Stale'), findsNothing);

    api.throwOnFetch = true;
    await tester.tap(find.byIcon(Icons.refresh));
    await tester.pumpAndSettle();

    expect(find.text('Stale'), findsOneWidget);
    expect(find.textContaining('Guiding RMS'), findsOneWidget);
  });
}
