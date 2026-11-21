import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show ProviderListenable;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/profile_cache_service.dart';
import 'package:openastroara/state/profile_cache_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/settings/imaging_defaults_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/migrated_sections.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015 for the sections Simple Mode writes (slice 3 of #1807, PR 2c-i:
// optics, imaging defaults, autofocus). Most cases run the write a Simple Mode
// window makes (load the rig's copy, apply one change to it, save it with the
// loaded tag); the failed-load and cache cases edit and save the notifier
// itself. All assert only on what reached the rig (FakeSectionRig.puts).
// These cases guard the notifiers (strict readers, refusal, reset, cache);
// that each Simple Mode window loads before it writes is guarded by the widget
// cases in test/screens/simple/.

class _Case {
  const _Case({
    required this.name,
    required this.route,
    required this.rigBody,
    required this.editKey,
    required this.editValue,
    required this.cacheKey,
    required this.cacheBody,
    required this.setThenSave,
    required this.editThenSave,
    required this.load,
    required this.provider,
    required this.toJson,
  });

  final String name;
  final String route;

  /// The rig's copy: every field differs from the client default, so a Save
  /// that sends a client default shows.
  final Map<String, Object?> rigBody;

  /// The one field the Simple Mode write sets, and what it sets it to.
  final String editKey;
  final Object editValue;

  /// The offline cache's key for the section, or null when the cache does not
  /// seed it (safety and phd2), and the cache's copy of it.
  final String? cacheKey;
  final Map<String, Object?>? cacheBody;

  /// What the Simple Mode window does: one setter, then persist.
  final Future<void> Function(ProviderContainer c, ProfileApi api) setThenSave;

  /// The same change straight on the notifier: edit, then save. No load
  /// first, so the module's own refusal is what decides (ADR 0015 rule 1).
  final Future<void> Function(ProviderContainer c, ProfileApi api) editThenSave;
  final Future<void> Function(ProviderContainer c, ProfileApi api) load;

  /// The section's provider, and its wire form (to compare with the rig's).
  final ProviderListenable<Object?> provider;
  final Map<String, Object?> Function(Object? value) toJson;
}

final _cases = <_Case>[
  _Case(
    name: 'Optics',
    route: 'optics',
    rigBody: ProfileApi.opticsToJson(
      const OpticsSettings(
        focalLengthMm: 800,
        reducerFactor: 0.8,
        sensorWidthPx: 4144,
        sensorHeightPx: 2822,
        pixelSizeUm: 4.63,
        apertureMm: 100,
        telescopeName: 'Rig scope',
      ),
    ),
    editKey: 'focal_length_mm',
    editValue: 1200.0,
    cacheKey: 'optics',
    cacheBody: ProfileApi.opticsToJson(
      const OpticsSettings(focalLengthMm: 333, telescopeName: 'Cached scope'),
    ),
    setThenSave: (c, api) async {
      await _loadThenSave(
        c,
        opticsSettingsProvider,
        api,
        (o) => o.copyWith(focalLengthMm: 1200),
      );
    },
    editThenSave: (c, api) async {
      final n = c.read(opticsSettingsProvider.notifier)
        ..edit((o) => o.copyWith(focalLengthMm: 1200));
      await n.save(api);
    },
    load: (c, api) => c.read(opticsSettingsProvider.notifier).load(api),
    provider: opticsSettingsProvider,
    toJson: (v) => ProfileApi.opticsToJson(v as OpticsSettings),
  ),
  _Case(
    name: 'Imaging defaults',
    route: 'imaging-defaults',
    rigBody: ProfileApi.imagingDefaultsToJson(
      const ImagingDefaults(
        coolerTargetC: -20,
        defaultGain: 250,
        defaultOffset: 30,
      ),
    ),
    editKey: 'cooler_target_c',
    editValue: -5.0,
    cacheKey: 'imaging_defaults',
    cacheBody: ProfileApi.imagingDefaultsToJson(
      const ImagingDefaults(coolerTargetC: 10),
    ),
    setThenSave: (c, api) async {
      await _loadThenSave(
        c,
        imagingDefaultsProvider,
        api,
        (d) => d.copyWith(coolerTargetC: -5),
      );
    },
    editThenSave: (c, api) async {
      final n = c.read(imagingDefaultsProvider.notifier)
        ..edit((d) => d.copyWith(coolerTargetC: -5));
      await n.save(api);
    },
    load: (c, api) => c.read(imagingDefaultsProvider.notifier).load(api),
    provider: imagingDefaultsProvider,
    toJson: (v) => ProfileApi.imagingDefaultsToJson(v as ImagingDefaults),
  ),
  _Case(
    name: 'Autofocus',
    route: 'autofocus',
    rigBody: ProfileApi.autofocusSettingsToJson(
      const AutofocusSettings(exposureSeconds: 7, steps: 9, stepSize: 80),
    ),
    editKey: 'exposure_seconds',
    editValue: 10,
    cacheKey: 'autofocus',
    cacheBody: ProfileApi.autofocusSettingsToJson(
      const AutofocusSettings(exposureSeconds: 3),
    ),
    setThenSave: (c, api) async {
      await _loadThenSave(
        c,
        autofocusSettingsProvider,
        api,
        (a) => a.copyWith(exposureSeconds: 10),
      );
    },
    editThenSave: (c, api) async {
      final n = c.read(autofocusSettingsProvider.notifier)
        ..edit((a) => a.copyWith(exposureSeconds: 10));
      await n.save(api);
    },
    load: (c, api) => c.read(autofocusSettingsProvider.notifier).load(api),
    provider: autofocusSettingsProvider,
    toJson: (v) => ProfileApi.autofocusSettingsToJson(v as AutofocusSettings),
  ),
];

class _Servers extends SavedServersNotifier {
  @override
  Future<List<AraServer>> build() async => const [
    AraServer(hostname: 'a.local', port: 1),
  ];
}

class _Profiles extends ProfileManagementNotifier {
  @override
  Future<ProfileList> build() async =>
      const ProfileList(activeId: 'p1', profiles: []);
  void activate(String id) =>
      state = AsyncData(ProfileList(activeId: id, profiles: const []));
}

/// An offline cache holding profile p1's copy of each section the cache seeds.
class _Cache extends ProfileCacheService {
  @override
  Future<Map<String, dynamic>> load() async => {
    'sections': {
      'p1': {
        for (final c in _cases)
          if (c.cacheKey != null) c.cacheKey!: c.cacheBody,
      },
    },
  };
}

/// Whether a rig is connected: `profileApiProvider` is the test's rig then
/// (`a.local`, the saved server), so a section that loads itself reaches the
/// fake and never a real host.
class _Connected extends Notifier<bool> {
  @override
  bool build() => true;
  void set(bool on) => state = on;
}

final _connected = NotifierProvider<_Connected, bool>(_Connected.new);

/// The section's value, whatever the notifier's state type.
Object? _valueOf(Object? state) => state is LoadedSection ? state.value : state;

/// What Simple Mode's helper does: load the rig's copy, apply the change to
/// it, save it with the loaded tag. A failed load is a refusal.
Future<void> _loadThenSave<T>(
  ProviderContainer c,
  NotifierProvider<SectionNotifier<T>, LoadedSection<T>> provider,
  ProfileApi api,
  T Function(T) change,
) async {
  final n = c.read(provider.notifier);
  if (!await n.load(api)) throw StateError('not loaded');
  await n.replaceAndSave(change(c.read(provider).value as T));
}

/// A refusal is the right outcome here; the test asserts on the rig.
Future<void> _try(Future<void> Function() run) async {
  try {
    await run();
  } catch (_) {}
}

void main() {
  late ProviderContainer c;
  late FakeSectionRig rig;

  setUp(() {
    c = ProviderContainer(
      overrides: [
        profileApiProvider.overrideWith(
          (ref) => ref.watch(_connected) ? rig.api(hostname: 'a.local') : null,
        ),
        savedServersProvider.overrideWith(_Servers.new),
        profileManagementProvider.overrideWith(_Profiles.new),
        profileCacheServiceProvider.overrideWithValue(_Cache()),
      ],
    );
    addTearDown(c.dispose);
  });

  for (final k in _cases) {
    group(k.name, () {
      setUp(() => rig = FakeSectionRig({k.route: k.rigBody}));

      test('a set-then-save after a failed load writes nothing', () async {
        rig.failGet.add(k.route);
        await _try(() => k.editThenSave(c, rig.api()));
        expect(rig.puts, isEmpty);
      });

      test(
        "a set-then-save keeps the edit and the rig's other values",
        () async {
          // Not loaded at the write: a load the notifier fires on its own
          // fails first (ADR 0015: the write loads, never a default).
          rig.failGet.add(k.route);
          c.listen(k.provider, (_, _) {});
          await Future<void>.delayed(const Duration(milliseconds: 20));
          rig.failGet.remove(k.route);
          await _try(() => k.setThenSave(c, rig.api()));
          expect(rig.puts, hasLength(1));
          expect(rig.puts.single.body, {...k.rigBody, k.editKey: k.editValue});
        },
      );

      test('an active-profile change resets it, so Save never writes profile '
          "p1's copy to p2", () async {
        c.listen(migratedSectionsResetProvider, (_, _) {});
        c.listen(profileManagementProvider, (_, _) {});
        await c.read(savedServersProvider.future);
        await c.read(profileManagementProvider.future);
        final api = rig.api(hostname: 'a.local');
        await k.load(c, api);

        (c.read(profileManagementProvider.notifier) as _Profiles).activate(
          'p2',
        );
        await Future<void>.delayed(Duration.zero);
        rig.failGet.add(k.route);
        await _try(() => k.setThenSave(c, api));
        expect(rig.puts, isEmpty);
      });

      if (k.cacheKey != null) {
        test('seeded from the offline cache it is never saved', () async {
          c.read(_connected.notifier).set(false); // an offline session
          expect(await seedPlanningFromCache(c, 'p1'), isTrue);
          rig.failGet.add(k.route);
          await _try(() => k.editThenSave(c, rig.api()));
          expect(rig.puts, isEmpty);
        });

        test(
          'seeded offline, then a server appears: it loads from the rig',
          () async {
            c.read(_connected.notifier).set(false);
            c.listen(k.provider, (_, _) {});
            expect(await seedPlanningFromCache(c, 'p1'), isTrue);
            expect(k.toJson(_valueOf(c.read(k.provider))), k.cacheBody);

            c.read(_connected.notifier).set(true);
            await settle(() => rig.gets.contains(k.route));
            await Future<void>.delayed(const Duration(milliseconds: 20));
            expect(k.toJson(_valueOf(c.read(k.provider))), k.rigBody);
          },
        );
      }
    });
  }
}
