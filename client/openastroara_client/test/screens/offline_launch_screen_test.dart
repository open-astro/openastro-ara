import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/offline_launch_screen.dart';
import 'package:openastroara/services/profile_cache_service.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/state/profile_cache_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';

/// In-memory cache double — widget tests can't await real file IO (it doesn't
/// advance under the test binding's fake clock, so pumpAndSettle times out).
class _MemCache extends ProfileCacheService {
  Map<String, dynamic> store = {};

  @override
  Future<Map<String, dynamic>> load() async => store;

  @override
  Future<void> saveList(
      String? activeId, List<({String id, String name})> profiles) async {
    store['active_id'] = activeId;
    store['profiles'] = [
      for (final p in profiles) {'id': p.id, 'name': p.name},
    ];
  }

  @override
  Future<void> saveSections(
      String profileId, Map<String, dynamic> sections) async {
    (store['sections'] ??= <String, dynamic>{})[profileId] = sections;
  }
}

void main() {
  late _MemCache cache;

  setUp(() {
    cache = _MemCache();
  });

  Future<ProviderContainer> pump(WidgetTester tester) async {
    final container = ProviderContainer(overrides: [
      profileCacheServiceProvider.overrideWithValue(cache),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: OfflineLaunchScreen()),
    ));
    await tester.pumpAndSettle();
    return container;
  }

  testWidgets('empty cache: explains + Continue with defaults passes the gate',
      (tester) async {
    final container = await pump(tester);
    expect(find.textContaining('No profiles are cached'), findsOneWidget);
    await tester.tap(find.text('Continue with defaults'));
    await tester.pumpAndSettle();
    expect(container.read(profileGatePassedProvider), isTrue);
  });

  testWidgets(
      'cached profiles: pre-selects the active one; Plan seeds gear + passes the gate',
      (tester) async {
    await cache.saveList('p1', [
      (id: 'p1', name: 'RedCat 91'),
      (id: 'p2', name: 'Travel Rig'),
    ]);
    await cache.saveSections('p1', {
      'optics': {
        'focal_length_mm': 382,
        'aperture_mm': 51,
        'pixel_size_um': 3.76,
      },
    });
    final container = await pump(tester);
    expect(find.text('RedCat 91'), findsOneWidget); // pre-selected
    await tester.tap(find.text('Plan'));
    await tester.pumpAndSettle();
    expect(container.read(profileGatePassedProvider), isTrue);
    expect(container.read(opticsSettingsProvider).focalLengthMm, 382);
  });

  testWidgets('a profile without cached sections warns about defaults',
      (tester) async {
    await cache.saveList('p2', [
      (id: 'p2', name: 'Travel Rig'),
    ]);
    await pump(tester);
    expect(find.textContaining('haven\'t been cached yet'), findsOneWidget);
    expect(find.text('Plan'), findsOneWidget);
  });
}
