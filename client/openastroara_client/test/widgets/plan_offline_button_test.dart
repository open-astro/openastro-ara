import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_cache_service.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/state/profile_cache_state.dart';
import 'package:openastroara/widgets/plan_offline_button.dart';

/// In-memory cache double (real file IO doesn't advance under the test clock).
class _MemCache extends ProfileCacheService {
  Map<String, dynamic> store = {};
  @override
  Future<Map<String, dynamic>> load() async => store;
}

void main() {
  Future<ProviderContainer> pump(WidgetTester tester, _MemCache cache) async {
    final container = ProviderContainer(overrides: [
      profileCacheServiceProvider.overrideWithValue(cache),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: PlanOfflineButton())),
    ));
    await tester.pumpAndSettle();
    return container;
  }

  testWidgets('disabled with no cached profile — offline mode stays off',
      (tester) async {
    final container = await pump(tester, _MemCache());
    final button = tester.widget<TextButton>(find.byType(TextButton));
    expect(button.onPressed, isNull);
    expect(find.byType(Tooltip), findsOneWidget); // the "why" is discoverable
    await tester.tap(find.text('Plan offline'));
    await tester.pumpAndSettle();
    expect(container.read(offlineModeProvider), isFalse);
  });

  testWidgets('enabled once a profile is cached — tap enters offline mode',
      (tester) async {
    final cache = _MemCache()
      ..store = {
        'active_id': 'p1',
        'profiles': [
          {'id': 'p1', 'name': 'RedCat 91'},
        ],
      };
    final container = await pump(tester, cache);
    await tester.tap(find.text('Plan offline'));
    await tester.pumpAndSettle();
    expect(container.read(offlineModeProvider), isTrue);
  });
}
