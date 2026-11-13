import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/stats/achievements.dart';
import 'package:openastroara/services/achievements_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/stats/achievements_state.dart';
import 'package:openastroara/widgets/stats/achievements_section.dart';
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

class _FakeAchievementsClient implements AchievementsClient {
  _FakeAchievementsClient(this.value);
  StatsAchievements value;
  bool throwOnFetch = false;

  @override
  Future<StatsAchievements> fetch() async {
    if (throwOnFetch) throw StateError('boom');
    return value;
  }

  @override
  void close() {}
}

const _server = AraServer(hostname: 'h', port: 5555);

Widget _app(List<AraServer> servers, AchievementsClient api) {
  return ProviderScope(
    overrides: [
      savedServerServiceProvider.overrideWithValue(_FakeSavedServerService(servers)),
      achievementsApiFactoryProvider.overrideWithValue((_) => api),
    ],
    child: const MaterialApp(
      home: Scaffold(
        body: SingleChildScrollView(child: AchievementsSection()),
      ),
    ),
  );
}

void main() {
  testWidgets('renders record tiles + a milestone badge when data is present',
      (tester) async {
    final api = _FakeAchievementsClient(const StatsAchievements(
      totalLightFrames: 1340,
      totalNightsImaged: 12,
      currentStreakNights: 2,
      uniqueTargetsImaged: 7,
      milestones: [
        StatsMilestone(
            id: 'hours_10',
            title: '10 Hours',
            description: 'Image for 10 hours',
            achieved: true,
            threshold: 10,
            current: 42),
      ],
    ));
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    expect(find.text('Achievements'), findsOneWidget);
    expect(find.text('Nights imaged'), findsOneWidget);
    expect(find.text('10 Hours'), findsOneWidget);
    expect(find.byType(CircularProgressIndicator), findsNothing);
  });

  testWidgets('an earned badge is as tall as one still in progress', (tester) async {
    tester.view.physicalSize = const Size(1600, 1200);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final api = _FakeAchievementsClient(const StatsAchievements(
      totalLightFrames: 906,
      milestones: [
        StatsMilestone(id: 'hours_10', title: 'Getting started', description: '10 hours of integration',
            achieved: false, threshold: 10, current: 7.2),
        StatsMilestone(id: 'targets_10', title: 'Explorer', description: '10 unique targets imaged',
            achieved: true, threshold: 10, current: 10),
      ],
    ));
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();
    Size badge(String title) => tester.getSize(
        find.ancestor(of: find.text(title), matching: find.byType(Container)).first);
    expect(badge('Explorer').height, badge('Getting started').height);
  });

  testWidgets('shows the connect hint when no server is saved', (tester) async {
    final api = _FakeAchievementsClient(const StatsAchievements());
    await tester.pumpWidget(_app(const [], api));
    await tester.pumpAndSettle();

    expect(find.textContaining('Connect to a server'), findsOneWidget);
  });

  testWidgets('shows the empty state when the catalog has no light frames',
      (tester) async {
    final api = _FakeAchievementsClient(const StatsAchievements());
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    expect(find.textContaining('No light frames yet'), findsOneWidget);
  });

  testWidgets('a failed manual refresh shows the stale banner over the records',
      (tester) async {
    final api = _FakeAchievementsClient(const StatsAchievements(totalLightFrames: 5));
    await tester.pumpWidget(_app(const [_server], api));
    await tester.pumpAndSettle();

    // Records visible, no banner yet.
    expect(find.text('Light frames'), findsOneWidget);
    expect(find.textContaining('Couldn’t refresh'), findsNothing);

    // Next fetch fails; pull down to refresh.
    api.throwOnFetch = true;
    pullRefresh(tester);
    await tester.pumpAndSettle();

    // Banner shown AND records still on screen (not blanked).
    expect(find.textContaining('Couldn’t refresh'), findsOneWidget);
    expect(find.text('Light frames'), findsOneWidget);
  });
}
