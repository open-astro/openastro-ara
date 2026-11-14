import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_cache_service.dart';
import 'package:openastroara/state/profile_cache_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/custom_horizon_state.dart';
import 'package:openastroara/state/settings/migrated_sections.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015 for a planning section the offline cache seeds: the custom horizon.
// Its old client default is an empty skyline, so a Save that was never loaded
// wipes the person's horizon on the rig. Every assertion is on what reached
// the rig (FakeSectionRig.puts), so the cases hold once the notifier moves
// onto the section module.

const _section = 'custom-horizon';

Map<String, Object?> _horizon(List<List<double>> points) => {
  'points': [
    for (final p in points) {'azimuth_deg': p[0], 'altitude_deg': p[1]},
  ],
};

/// The rig's skyline, which a wrong Save would replace.
final _rigHorizon = _horizon([
  [0, 12],
  [180, 20],
]);

class _Servers extends SavedServersNotifier {
  @override
  Future<List<AraServer>> build() async => const [
    AraServer(hostname: 'a.local', port: 1),
  ];
  void set(AraServer s) => state = AsyncData([s]);
}

class _Profiles extends ProfileManagementNotifier {
  @override
  Future<ProfileList> build() async =>
      const ProfileList(activeId: 'p1', profiles: []);
  void activate(String id) =>
      state = AsyncData(ProfileList(activeId: id, profiles: const []));
}

/// An offline cache holding profile p1's horizon, as `profile_cache.json` does.
class _Cache extends ProfileCacheService {
  @override
  Future<Map<String, dynamic>> load() async => {
    'sections': {
      'p1': {
        'custom_horizon': _horizon([
          [90, 30],
        ]),
      },
    },
  };
}

/// A load that fails must not throw out of the test: today's notifier
/// rethrows, the section module records the failure instead.
Future<void> _tryLoad(Future<void> Function() load) async {
  try {
    await load();
  } catch (_) {}
}

/// Save may refuse (it should, when not loaded); the test asserts on the rig.
Future<void> _trySave(Future<void> Function() save) async {
  try {
    await save();
  } catch (_) {}
}

void main() {
  late ProviderContainer c;
  late FakeSectionRig rig;
  CustomHorizonNotifier n() => c.read(customHorizonProvider.notifier);

  setUp(() {
    rig = FakeSectionRig({_section: _rigHorizon});
    c = ProviderContainer(
      overrides: [
        savedServersProvider.overrideWith(_Servers.new),
        profileManagementProvider.overrideWith(_Profiles.new),
        profileCacheServiceProvider.overrideWithValue(_Cache()),
      ],
    );
    addTearDown(c.dispose);
  });

  test(
    'Custom horizon: Save after a failed load writes nothing to the rig',
    () async {
      final api = rig.api();
      rig.failGet.add(_section);
      await _tryLoad(() => n().hydrateFromServer(api));
      n().addPoint(45, 15);
      await _trySave(() => n().persistToServer(api));
      expect(rig.puts, isEmpty);
    },
  );

  test('Custom horizon seeded from the offline cache is never saved', () async {
    expect(await seedPlanningFromCache(c, 'p1'), isTrue);
    final api = rig.api();
    rig.failGet.add(_section);
    n().addPoint(45, 15);
    await _trySave(() => n().persistToServer(api));
    expect(rig.puts, isEmpty);
  });

  test(
    'Custom horizon: Save over a copy another device replaced writes nothing',
    () async {
      final api = rig.api();
      await n().hydrateFromServer(api);
      rig.savedElsewhere(_section);
      n().addPoint(45, 15);
      await _trySave(() => n().persistToServer(api));
      expect(rig.ifMatches, ['"$_section-v1"']);
      expect(rig.puts, isEmpty);
    },
  );

  test('Custom horizon: a rig change resets it, so Save never writes rig A '
      'to rig B', () async {
    c.listen(migratedSectionsResetProvider, (_, _) {});
    c.listen(profileManagementProvider, (_, _) {});
    await c.read(savedServersProvider.future);
    await c.read(profileManagementProvider.future);
    await n().hydrateFromServer(rig.api(hostname: 'a.local'));

    final rigB = FakeSectionRig({_section: _horizon([])})
      ..failGet.add(_section);
    (c.read(savedServersProvider.notifier) as _Servers).set(
      const AraServer(hostname: 'b.local', port: 1),
    );
    await Future<void>.delayed(Duration.zero);
    await _trySave(() => n().persistToServer(rigB.api(hostname: 'b.local')));
    expect(rigB.puts, isEmpty);
  });

  test('Custom horizon: an active-profile change resets it, so Save never '
      "writes profile p1's skyline to p2", () async {
    c.listen(migratedSectionsResetProvider, (_, _) {});
    c.listen(profileManagementProvider, (_, _) {});
    await c.read(savedServersProvider.future);
    await c.read(profileManagementProvider.future);
    final api = rig.api(hostname: 'a.local');
    await n().hydrateFromServer(api);

    (c.read(profileManagementProvider.notifier) as _Profiles).activate('p2');
    await Future<void>.delayed(Duration.zero);
    rig.failGet.add(_section);
    await _trySave(() => n().persistToServer(api));
    expect(rig.puts, isEmpty);
  });
}
