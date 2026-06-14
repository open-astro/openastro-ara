import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/best_frame.dart';
import 'package:openastroara/services/best_frames_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/best_frames_state.dart';
import 'package:openastroara/widgets/stats/best_frames_section.dart';

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

class _FakeBestFramesClient implements BestFramesClient {
  _FakeBestFramesClient(this.frames);
  List<BestFrame> frames;
  bool throwOnFetch = false;

  @override
  Future<List<BestFrame>> fetch() async {
    if (throwOnFetch) throw StateError('boom');
    return frames;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

Widget _app(List<AraServer> servers, BestFramesClient api) {
  return ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
      bestFramesApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(
      home: Scaffold(
        body: SingleChildScrollView(child: BestFramesSection()),
      ),
    ),
  );
}

void main() {
  testWidgets('renders the best-frame tiles when data is present', (tester) async {
    final api = _FakeBestFramesClient(
        const [BestFrame(frameId: 'a', targetName: 'M31', compositeScore: 0.93)]);
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    expect(find.text('M31'), findsOneWidget);
    expect(find.textContaining('Couldn’t refresh'), findsNothing);
  });

  testWidgets('a failed manual refresh shows the stale banner over the list',
      (tester) async {
    final api = _FakeBestFramesClient(
        const [BestFrame(frameId: 'a', targetName: 'M31', compositeScore: 0.93)]);
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
