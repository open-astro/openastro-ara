import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/sequence/sequence_summary.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/equipment_device_status.dart'
    show EquipmentConnectionState;
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/services/sequence_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/services/target_preview_service.dart';
import 'package:openastroara/state/album/album_art.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/imaging/last_frame_state.dart';
import 'package:openastroara/state/rig_setup_state.dart';
import 'package:openastroara/state/sequencer/sequence_list_state.dart';
import 'package:openastroara/state/server_state.dart'
    show discoveredServersProvider;
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/sky_atlas/dso_catalog_state.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/state/sky_atlas/target_preview_state.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';
import 'package:openastroara/widgets/sky_atlas/target_preview.dart'
    show surveyPicture;

/// #1958: a target without Album Art gets the DSS2 survey cutout on the Plan
/// target card and the Sky Map pick card; Art wins, and a survey that does
/// not come keeps today's look.

// A 1x1 PNG: something Image.memory decodes.
final _png = Uint8List.fromList(const [
  0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, //
  0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
  0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
  0x0D, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0xF8, 0xFF, 0xFF, 0x3F,
  0x00, 0x05, 0xFE, 0x02, 0xFE, 0xA7, 0x35, 0x81, 0x84, 0x00, 0x00, 0x00,
  0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
]);

class _Svc extends TargetPreviewService {
  _Svc(this.bytes);
  final Uint8List? bytes;
  final loaded = <String>[];
  @override
  Future<Uint8List?> load({
    required String id,
    required double raDeg,
    required double decDeg,
    required double fieldDeg,
  }) async {
    loaded.add(id);
    return bytes;
  }
}

class _Sel extends PlanetariumSelectionNotifier {
  _Sel(this.value);
  final PlanetariumSelection? value;
  @override
  PlanetariumSelection? build() => value;
}

class _Prefs extends SimpleModePrefsService {
  @override
  Future<bool> load() async => true;
  @override
  Future<void> save(bool enabled) async {}
}

class _Filters extends FilterSetNotifier {
  @override
  LoadedSection<FilterSetSettings> build() => const LoadedSection(
    value: FilterSetSettings(
      filters: [PlanningFilter(name: 'L', kind: FilterKind.l)],
    ),
    source: SectionSource.rig,
  );
}

class _Wheel extends FilterWheelNotifier {
  @override
  Future<FilterWheelStatus?> build() async => FilterWheelStatus(
    deviceId: 'w',
    name: 'Wheel',
    connectionState: EquipmentConnectionState.connected,
    runtimeState: 'idle',
    currentSlot: 0,
    slots: const [],
  );
}

class _StartOnLive extends SelectedTabIndexNotifier {
  @override
  int build() => kLiveTabIndex;
}

class _Ready extends PlanetariumReadyNotifier {
  @override
  bool build() => true;
}

class _Seq implements SequenceClient {
  @override
  Future<SequencePage> list({int? limit, String? cursor}) async =>
      const SequencePage(items: []);

  @override
  dynamic noSuchMethod(Invocation i) => super.noSuchMethod(i);
}

const _orion = PlanetariumSelection(
  name: 'Orion Nebula',
  kind: 'Nebula',
  raHours: 5.6,
  decDeg: -5.4,
  raJ2000Hours: 5.59,
  decJ2000Deg: -5.39,
  altDeg: 40,
  maxAltDeg: 60,
  designations: ['NAME Orion Nebula', 'M 42'],
);

// Not in the Messier or Caldwell catalog: no Art.
const _lonely = PlanetariumSelection(
  name: 'Lonely Cloud',
  kind: 'Nebula',
  raHours: 1.5,
  decDeg: 30,
  raJ2000Hours: 1.5,
  decJ2000Deg: 30,
  altDeg: 40,
  maxAltDeg: 60,
  designations: ['Sh2-1'],
);

// An object with no designation: the page names it by its type word.
PlanetariumSelection _star(double raHours, double decDeg) =>
    PlanetariumSelection(
      name: 'Star',
      kind: 'Star',
      raHours: raHours,
      decDeg: decDeg,
      raJ2000Hours: raHours,
      decJ2000Deg: decDeg,
      altDeg: 40,
      maxAltDeg: 60,
    );

final _catalog = [
  const PlanningDso(
    id: 'NGC1976',
    name: 'Orion Nebula',
    type: 'N',
    magnitude: 4,
    raDeg: 83.8,
    decDeg: -5.4,
    messierNum: 42,
    aliases: ['M 42'],
  ),
];

List<Override> _data(_Svc svc) => [
  targetPreviewServiceProvider.overrideWithValue(svc),
  bundledCatalogProvider.overrideWith((ref) async => _catalog),
  albumManifestProvider.overrideWith(
    (ref) async => {
      'M42': const AlbumArt(
        asset: 'assets/album/art/m042.webp',
        credit: 'NOIRLab',
        licence: 'CC0',
      ),
    },
  ),
];

Future<void> _pumpPlan(
  WidgetTester tester,
  PlanetariumSelection sel,
  _Svc svc,
) async {
  final c = ProviderContainer(
    overrides: [
      ..._data(svc),
      planetariumSelectionProvider.overrideWith(() => _Sel(sel)),
      filterSetProvider.overrideWith(_Filters.new),
      filterWheelProvider.overrideWith(_Wheel.new),
      sequenceApiProvider.overrideWith((ref) => _Seq()),
      tonightSkyProvider.overrideWith((ref) async => const []),
      tonightsBestProvider.overrideWith((ref) async => const []),
    ],
  );
  addTearDown(c.dispose);
  tester.view.physicalSize = const Size(1024, 1100);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: MaterialApp(
        home: Scaffold(
          body: Center(
            child: SimpleRunSheet(
              onShowSky: () {},
              onPickInSky: () {},
              onCreated: () {},
            ),
          ),
        ),
      ),
    ),
  );
  await tester.pumpAndSettle();
}

Future<void> _pumpSky(
  WidgetTester tester,
  PlanetariumSelection sel,
  _Svc svc,
) async {
  tester.view.physicalSize = const Size(1024, 768);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final c = ProviderContainer(
    overrides: [
      ..._data(svc),
      companionPlatformProvider.overrideWithValue(TargetPlatform.linux),
      simpleModePrefsProvider.overrideWithValue(_Prefs()),
      discoveredServersProvider.overrideWith(
        (ref) => const Stream<AraServer>.empty(),
      ),
      rigSetupHydratedProvider.overrideWith((ref) async => false),
      selectedTabIndexProvider.overrideWith(_StartOnLive.new),
      planetariumReadyProvider.overrideWith(_Ready.new),
    ],
  );
  addTearDown(c.dispose);
  await c.read(simpleModeProvider.future);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(
        home: SimpleShell(planetarium: SizedBox(key: Key('sky'))),
      ),
    ),
  );
  await tester.pump();
  c.read(lastCapturedFrameIdProvider.notifier).set('frame-1');
  await tester.pump();
  await tester.tap(find.byKey(const Key('simple-sky')));
  await tester.pump();
  c.read(planetariumSelectionProvider.notifier).set(sel);
  await tester.pump();
  await tester.pump();
  await tester.pump();
}

void main() {
  const planSurvey = Key('simple-run-target-survey');
  const skySurvey = Key('simple-sky-survey');

  group('Plan target card', () {
    // Falsified by: simple_plan_panel.dart _TargetCard, drop the surveyPicture branch (the gradient always shows).
    testWidgets('a target without Art shows the survey cutout in the card', (
      tester,
    ) async {
      final svc = _Svc(_png);
      await _pumpPlan(tester, _lonely, svc);
      expect(find.byKey(planSurvey), findsOneWidget);
      expect(find.byIcon(Icons.auto_awesome), findsNothing);
      expect(svc.loaded, ['pick-22.500_30.000']);
    });

    // Falsified by: target_preview.dart surveyPicture, drop the wait for the Album manifest.
    testWidgets('a target with Art keeps its Art and fetches no survey', (
      tester,
    ) async {
      final svc = _Svc(_png);
      await _pumpPlan(tester, _orion, svc);
      expect(find.byKey(const Key('simple-run-target-art')), findsOneWidget);
      expect(find.byKey(planSurvey), findsNothing);
      expect(svc.loaded, isEmpty);
    });

    // Falsified by: target_preview.dart surveyPicture, return empty bytes instead of the null failure.
    testWidgets('no survey (offline) keeps the drawn placeholder', (
      tester,
    ) async {
      await _pumpPlan(tester, _lonely, _Svc(null));
      expect(find.byKey(planSurvey), findsNothing);
      expect(find.byIcon(Icons.auto_awesome), findsOneWidget);
    });
  });

  group('Sky Map pick card', () {
    // Falsified by: simple_shell.dart _SkyPick.build, pass null instead of the surveyPicture bytes.
    testWidgets('a pick without Art shows the survey cutout', (tester) async {
      final svc = _Svc(_png);
      await _pumpSky(tester, _lonely, svc);
      expect(find.byKey(skySurvey), findsOneWidget);
      expect(find.byKey(const Key('simple-sky-art')), findsNothing);
      // The survey credit is behind an info mark, shown on a tap.
      expect(
        find.descendant(
          of: find.byKey(skySurvey),
          matching: find.byIcon(Icons.info_outline),
        ),
        findsOneWidget,
      );
      await tester.tap(find.byKey(skySurvey));
      await tester.pump();
      expect(find.text('DSS2 color survey (CDS hips2fits)'), findsOneWidget);
    });

    // Falsified by: target_preview.dart surveyPicture, drop the isSolarSystemName check.
    testWidgets('a planet pick (Jupiter) gets no survey', (tester) async {
      final svc = _Svc(_png);
      await _pumpSky(
        tester,
        const PlanetariumSelection(
          name: 'Jupiter',
          kind: '',
          raHours: 3,
          decDeg: 17,
          raJ2000Hours: 3,
          decJ2000Deg: 17,
          altDeg: 40,
          maxAltDeg: 60,
        ),
        svc,
      );
      expect(find.byKey(skySurvey), findsNothing);
      expect(svc.loaded, isEmpty);
    });

    // Falsified by: target_preview.dart surveyPicture, drop the solar-system kind check.
    testWidgets('a comet pick (by its kind) gets no survey', (tester) async {
      final svc = _Svc(_png);
      await _pumpSky(
        tester,
        const PlanetariumSelection(
          name: 'C/2023 A3',
          kind: 'Com',
          raHours: 14,
          decDeg: -5,
          raJ2000Hours: 14,
          decJ2000Deg: -5,
          altDeg: 40,
          maxAltDeg: 60,
        ),
        svc,
      );
      expect(find.byKey(skySurvey), findsNothing);
      expect(svc.loaded, isEmpty);
    });

    // Falsified by: target_preview.dart surveyPicture, drop the wait for the Album manifest.
    testWidgets('a pick with Art keeps its Art and fetches no survey', (
      tester,
    ) async {
      final svc = _Svc(_png);
      await _pumpSky(tester, _orion, svc);
      expect(find.byKey(const Key('simple-sky-art')), findsOneWidget);
      expect(find.byKey(skySurvey), findsNothing);
      expect(svc.loaded, isEmpty);
    });

    // Falsified by: simple_shell.dart _card, show a SurveyThumb whatever the bytes.
    testWidgets('no survey (offline) leaves the card as it was', (
      tester,
    ) async {
      await _pumpSky(tester, _lonely, _Svc(null));
      expect(find.byKey(skySurvey), findsNothing);
      expect(find.byKey(const Key('simple-sky-art')), findsNothing);
      expect(find.text('Lonely Cloud'), findsOneWidget);
    });
  });

  // Falsified by: target_preview.dart surveyPicture, key the cutout on the name only.
  testWidgets('two picks named Star at different positions load two ids', (
    tester,
  ) async {
    final svc = _Svc(_png);
    final c = ProviderContainer(overrides: _data(svc));
    addTearDown(c.dispose);
    await c.read(albumManifestProvider.future);
    await c.read(bundledCatalogProvider.future);
    for (final star in [_star(1, 10), _star(2, 20)]) {
      await tester.pumpWidget(
        UncontrolledProviderScope(
          container: c,
          child: Consumer(
            builder: (_, ref, _) {
              surveyPicture(ref, star);
              return const SizedBox();
            },
          ),
        ),
      );
      await tester.pumpAndSettle();
    }
    expect(svc.loaded, hasLength(2));
    expect(svc.loaded.toSet(), hasLength(2));
  });
}
